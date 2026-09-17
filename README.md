# photone-ipc

Ultra-fast, zero-copy, broadcast IPC for .NET on Windows: a **double-mapped (virtual-memory mirrored) ring buffer** that lives in
shared memory, with one writer and up to 32 independent readers in any number of processes.

* `Span<T>` in, `ReadOnlyMemory<T>` out, straight on the shared pages: no serialization, no copying, no allocations on the hot path.
* A span never has to wrap: the data region is mapped twice back-to-back, so the bytes past the end of the ring are the bytes at its start.
* Same code path in-process and cross-process. Readers in other processes map the *same* section (best effort at the *same* virtual address).
* Wake-ups are cheap by construction: spin first, kernel wait only when a peer is provably asleep; **no syscall at all when nobody is waiting**.
* Robust to crashes: a dead reader is detected (PID + process start time, plus its process handle in the writer's wait set) and evicted; a dead
  or closed writer makes every reader's `Wait` return `false` with a `Status`.
* The signaling mechanism (how a blocked side is woken) is an isolated, swappable layer; the shared-memory layout reserves the words that
  alternative mechanisms need, so switching it never changes the layout.
* Stream tags: the writer attaches metadata to elements; every reader gets the tags of each chunk it reads, and the last value of every persistent
  tag, even when it joins long after the tag was written. In process the readers get the tag objects themselves; across processes tags are serialized
  (JSON by default). No capacity to size: tag memory grows and shrinks with what readers have not read yet. Opt-in per buffer.

Requirements: Windows 10 1803+ (`VirtualAlloc2` / `MapViewOfFile3`), x64, .NET 10 (C# 14). Zero runtime dependencies: the only package the library
references is CsWin32, a build-time source generator for the Win32 declarations, which ships nothing.

## The API

```csharp
using Photone.Ipc;

// writer process
using var buffer = RingBuffer<float>.Create(1 << 20, "demo");   // T : unmanaged; section "Local\photone.demo"
using (var bucket = buffer.GetBucket(1024))     // blocks (spin, then kernel) if there is no space for 1024 elements
{
    bucket.Span.Fill(0);                        // Span<float> of length 1024, contiguous thanks to the double mapping
    bucket.Commit(1000);                        // publish the first 1000 elements (may commit less than requested)
}

// reader (same process, or any other process: RingBuffer<float>.Open("demo"))
var reader = buffer.CreateReader();             // independent cursor per reader (broadcast); starts at the current head
await reader.Wait(100);                         // async wait until >= 100 elements are readable (WaitSync is the blocking twin)
reader.TryRead(100, out var chunk);             // chunk.Data : ReadOnlyMemory<float>, a window into the ring itself
Use(chunk.Data.Span);                           // Data.Span to read it, Data.Pin() for an address; it may also be stored or awaited on
reader.Advance(90);                             // consume 90 (may advance less than read)
```

Semantics in one table:

| Call | Rule |
|---|---|
| `Create(minCapacity, name?, options?)` | this process is *the* writer; capacity rounds up to a power of two so that `capacity * sizeof(T)` is a multiple of 64 KiB; `name == null` gives a random `Local\photone.{guid}`; `Local\…`/`Global\…` are honoured verbatim |
| `Open(name)` / `Open(SafeSectionHandle)` | reader role in another (or the same) process; waits for the creator to finish initialising; validates magic, version, element size and type |
| `GetBucket(n)` | exactly `n` elements (`1 <= n <= Capacity`); blocks while the slowest reader has not freed space; never blocks with zero readers; one outstanding bucket |
| `TryGetBucket(n, out b)` | non-blocking form |
| `Bucket.Commit(k)` | `0 <= k <= n`, exactly once; the next bucket starts at `W + k`; `Dispose` without `Commit` is `Commit(0)` |
| `CreateReader(options?)` | up to 32 readers per buffer (dead slots are reclaimed first); sees only commits made after it joined |
| `Wait(n[, timeout][, ct])` / `WaitSync(n[, timeout])` | `true` when `>= n` are readable; `false` on timeout or when the writer closed/died and fewer than `n` will ever arrive (check `Status`, drain with `Available`/`TryRead`) |
| `TryRead(n, out chunk)` | exactly `n` or `false`; never blocks; the chunk is valid until `Advance` moves past it |
| `Advance(k)` | `k <= what TryRead/Available observed`; frees space for the writer; may be less than the last chunk |
| `RingBuffer.Dispose()` (writer) | commits `0` for an outstanding bucket, publishes *Closed*, wakes every reader; readers keep draining |
| `RingReader.Dispose()` | releases the slot, wakes a blocked writer, completes a pending `Wait` with `ObjectDisposedException` |
| `RingBufferOptions.Pool` | `Create`/`Open` take the shared memory from a `RingBufferPool` and give it back on release (see below) |
| `Bucket.AddTag(tag, index = 0)` | attaches a tag to element `index` of the bucket (sets `tag.Offset`); published by `Commit` with the elements (see "Stream tags") |
| `RingBuffer.AddTag(tag)` | attaches a tag to the next element the writer publishes, without a bucket |
| `Chunk.Tags` | the tags of the chunk's elements, in offset order |
| `RingReader.ReadLastTagValues()` | `ReadOnlySpan<ITag>`: the last persistent tag of every key before `ReadCursor` |

Options: `RingBufferOptions { SpinTime = 20 µs, MaxSpinTime = null, LivenessCheckInterval = 10 ms, InitializationTimeout = 5 s, PreferredBaseAddress, PreFault = true, Pool = null, Tags = TagMode.None, TagSerializer = null, MaxUnreadTags = 0, MaxUnreadTagBytes = 0 }`,
`ReaderOptions { SpinTime = 20 µs, MaxSpinTime = null, AsyncSpinTime = 5 µs, AllowSynchronousContinuations = true }`,
`RingBufferPoolOptions { IdleTimeout = 30 s, MaxIdleBytes, ClearOnReuse = false }`. The spin budget adapts between
zero and `MaxSpinTime` (default: `SpinTime`) from the gaps actually observed; `Timeout.InfiniteTimeSpan` never touches the kernel (one busy core
per waiting side); `TimeSpan.Zero` blocks immediately. See "Waiting: latency versus CPU".

### Stream tags

Metadata that belongs to particular elements (a burst start, a timestamp, a change of sample rate) travels with the data. The writer attaches tags to
the elements of a bucket; a reader gets, with every chunk, the tags of exactly that chunk's elements. A tag type that is *persistent* describes state:
the reader can ask for the last one of every key at any time, including right after it joined, long after the tag was written.

```csharp
public sealed class SampleRate : ITag                 // any type; across processes it must round-trip through the serializer
{
    public static bool IsPersistent => true;          // state: ReadLastTagValues keeps the last one per key
    public ulong Offset { get; set; }                 // the absolute element index: set by AddTag (and from the record in other processes)
    public string Key => "sample_rate";
    public double Hz { get; set; }
}

public sealed class BurstStart : ITag { public ulong Offset { get; set; } public string Key => "burst"; }   // an event: not persistent

// writer process: tags are opt-in per buffer, with no capacity to choose
var writerOptions = new RingBufferOptions { Tags = TagMode.CrossProcess, TagSerializer = new JsonTagSerializer() };
using var buffer = RingBuffer<float>.Create(1 << 20, "sdr", writerOptions);
buffer.AddTag(new SampleRate { Hz = 48_000 });        // no bucket needed: attaches to the next element written
using (var bucket = buffer.GetBucket(1024))
{
    Fill(bucket.Span);
    bucket.AddTag(new BurstStart(), 100);             // element 100 of this bucket (index 0, the default, is its first element)
    bucket.Commit(1024);                              // both tags are published with the elements
}

// reader process: register the tag types this process wants back as objects
var tags = new JsonTagSerializer().Register<SampleRate>().Register<BurstStart>();
using var opened = RingBuffer<float>.Open("sdr", new RingBufferOptions { TagSerializer = tags });
using var reader = opened.CreateReader();
foreach (ITag tag in reader.ReadLastTagValues()) { }  // the state where the reader starts: one tag per key, no allocation
reader.TryRead(100, out var chunk);
foreach (ITag tag in chunk.Tags.Span) { }             // StartOffset <= tag.Offset < StartOffset + Length, in offset order
reader.Advance(100);                                  // passing a persistent tag makes it the key's last value
```

* **Two modes, no capacity.** `TagMode.InProcess` keeps tags as objects: readers created by the writer's own `RingBuffer` get the very instances that were
  added, nothing is serialized, and a tag is garbage once every reader has read past it. `TagMode.CrossProcess` also serializes each tag, once, into the
  buffer's shared memory for readers in other processes (or of the buffer opened by name); the writer's own readers still get the instances. `None`, the
  default, means no tags, and the buffer is laid out and behaves exactly as before. Treat a tag as immutable once added.
* **Memory as needed.** Shared tag memory is a large reserved range of the buffer's own section (it costs nothing until used) that the writer commits as
  tags arrive. Tags go into a ring that is reused once every reader has read past them; when the tags readers still need outgrow it, the log moves on to a
  ring twice as large, and after a burst back to a small one. A commit never waits for tags. What tags occupy is bounded by the tags of the elements the
  slowest reader has not read yet, and the data ring's back-pressure bounds those elements. Committed pages stay with the section until it is destroyed
  (Windows cannot decommit pages of a shared section), so a buffer keeps its high-water mark. Tag memory lives in the section, so a reader can drain every
  tag after the writer's process has exited.
* **What it costs, at any moment.** `buffer.TagMemory` reports that high-water mark (`CommittedBytes`, split into the ring generations and the persistent-tag
  table), what still holds it (`UnreadTags` / `UnreadBytes`, the tags the slowest reader has not passed), the ring the log is in and how often it has moved,
  and `PersistentKeys` — the one number that only grows, one table slot per distinct key for the buffer's life. A process that opened the buffer sees the
  committed sizes and the keys; the writer's own bookkeeping is writer-side only. `pool.IdleTagBytes` reports what the pool's idle mappings still hold.
* **Optional limit.** `MaxUnreadTags` (and, for `CrossProcess`, `MaxUnreadTagBytes`) bound what the writer keeps for the slowest reader: a commit that
  would exceed the limit waits for readers, exactly as `GetBucket` waits for space, and `Dispose` ends that wait. Both default to 0, no limit. Nothing
  breaks a deadlock: if a reader waits for more elements than are published while the writer waits for that reader to read past tags, both stop, so size
  the limit above the tags of the largest chunk a reader waits for. A limit that is not reached costs nothing measurable (145.7 ns against 145.9 ns per
  tagged commit, interleaved runs), and off it is one comparison.
* **Where a tag goes.** `bucket.AddTag(tag, index)` puts it on element `index` of the bucket. `buffer.AddTag(tag)` puts it on the next element the writer
  publishes: it waits through commits that publish nothing and is dropped only if the writer closes before publishing another element. Both set
  `tag.Offset`; readers in other processes set it again from the record, so a tag type does not even need to serialize it.
* **Exactly once, never early.** `chunk.Tags` holds the tags with `StartOffset <= Offset < StartOffset + Length`; `ReadLastTagValues()` holds, without
  allocating, one persistent tag per key *before* `ReadCursor`. A tag is either ahead of the reader (it arrives in a chunk) or behind it (it is state),
  never both. `Commit(k)` publishes the tags of the first `k` elements and drops the rest with the dropped elements; tags may be added in any order.
* **Serialization.** `JsonTagSerializer` writes each tag's JSON with its type name (default: the full type name) next to the key, the offset and the
  persistent flag. A reader turns a tag back into an object when a type is registered under that name in its own process, and otherwise delivers an
  `UnknownTag` with the raw JSON (also when deserialization throws, with the exception). The parameterless constructor uses reflection; for trimming or
  native AOT, pass `JsonSerializerOptions` with a source-generated resolver. `ITagSerializer` is the extension point for other formats.

What tags cost (BenchmarkDotNet, one thread, buckets of 256 floats, payload untouched; `TagBenchmarks`, two runs on the same laptop):

| round (GetBucket + Commit + TryRead + Advance) | mean | allocated |
|---|---:|---:|
| buffer without tags | 17.1-17.5 ns | 0 |
| tags enabled, none published: the writer's own reader / a reader of the buffer opened by name | 18.3-19.0 ns / 18.7-18.8 ns | 0 |
| one tag per bucket, `InProcess` (the reader gets the instance) | 133-134 ns | 64 B |
| one tag per bucket, `CrossProcess`, the writer's own reader (serialized, not deserialized) | 522-530 ns | 96 B |
| one tag per bucket, `CrossProcess`, a reader of the buffer opened by name (serialized and deserialized) | 905-925 ns | 160 B |
| one persistent tag per bucket: `InProcess` / `CrossProcess`, opened by name | 160-162 ns / 1,195-1,222 ns | 65 B / 129 B |
| for reference: `JsonTagSerializer` serialize + deserialize of that tag alone | 547-556 ns | 128 B |

In process, the allocation is the tag the benchmark creates plus the object log's chunks (256 entries each), amortized. Across processes JSON is most of
a tag's cost; a binary `ITagSerializer` would remove most of it. A buffer with tags that carries none pays one compare per round and a load of the
published end per new write cursor; against the commit before tags existed, a buffer without tags pays about 1 ns per 19-20 ns round (measured with
base and new runs interleaved).

### Many buffers: `RingBufferPool`

Every new buffer creates a section, maps it and pre-faults every page, and every release unmaps and frees it again; the process that opens the
buffer does the same on its side. That is about 0.4 ms per side for a 64 KiB ring and 60 ms for 64 MiB. A process that creates or opens buffers
one after another can keep those mappings instead:

```csharp
// writer process
var options = new RingBufferOptions { Pool = RingBufferPool.Shared };   // or new RingBufferPool(new RingBufferPoolOptions { IdleTimeout = ... })
foreach (string channel in channels)
{
    using var buffer = RingBuffer<float>.Create(1 << 20, channel, options);   // from the second one on: reuses a released section of this size
    Stream(buffer);
}                                                                               // released: the section goes back to the pool

// reader process: a pool on Open too, so that the next buffer in a reused section adopts the mapping this process already has
using var opened = RingBuffer<float>.Open(channel, new RingBufferOptions { Pool = RingBufferPool.Shared });
using var reader = opened.CreateReader();
```

* A section is reused only once no other process holds it: readers elsewhere finish draining the old buffer undisturbed, and new buffers get
  other sections meanwhile. The check is the kernel's system-wide handle count of the section, paired with a handshake in `Open` so that a late
  opener and the pool can never miss each other (DESIGN §15.3).
* Reuse needs the same data size (`Capacity * sizeof(T)`, any `T`) and namespace. Reuse keeps the address range, the views, the resident pages and
  the 33 events. The buffer's name lives in a small alias object, so names behave as before.
* Mappings unused for `IdleTimeout` (default 30 s) are released by a timer that runs only while something is idle; `MaxIdleBytes`, `Trim()`
  and `Dispose()` release them earlier. `ClearOnReuse` zeroes a reused data region (otherwise the previous buffer's bytes are still in the
  shared pages, although no reader can see them through the API).
* A pool is for buffers that serve the same trust domain: a process that mapped an earlier buffer can keep a view of the section (a parked
  mapping does), and would see the next buffer in it. Don't share a pool between parties that must not see each other's data.
* Only disposed buffers return their mapping. A buffer released by its finalizer, or a writer disposed while a bucket is still outstanding,
  releases it instead.

| data size | creator `Create` | creator `Dispose` | peer `Open` + `CreateReader` | peer release |
|---|---|---|---|---|
| 64 KiB | 308 µs → **35 µs** | 113 µs → **31 µs** | 241 µs → **44 µs** | 68 µs → **8 µs** |
| 1 MiB | 1.0 ms → **41 µs** | 293 µs → **31 µs** | 864 µs → **54 µs** | 143 µs → **10 µs** |
| 16 MiB | 12.1 ms → **96 µs** | 2.9 ms → **32 µs** | 11.0 ms → **137 µs** | 1.1 ms → **10 µs** |
| 64 MiB | 49.3 ms → **265 µs** | 12.0 ms → **33 µs** | 43.8 ms → **321 µs** | 5.6 ms → **11 µs** |

(no pool → pool; `Photone.Ipc.Benchmarks.exe --lifecycle`, p50, pre-fault on, the peer is a second process that opens each buffer by name, joins
as a reader, reads one element and releases it. What remains with a pool is the alias object and one pass over the already-resident pages.)

## How cross-process zero-copy works

One pagefile-backed section of `64 KiB + D` bytes holds a 4 KiB control block (cursors, reader slots) followed by the data (a buffer with
cross-process tags continues after the data with a large reserved tag region, which is mapped separately and committed as tags need it). Every process
reserves **one placeholder** of `64 KiB + 2·D` with `VirtualAlloc2(MEM_RESERVE | MEM_RESERVE_PLACEHOLDER)`, splits it twice with
`VirtualFree(MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER)`, and replaces the three pieces with three `MapViewOfFile3(MEM_REPLACE_PLACEHOLDER)`
views of the same section. The data range is mapped *twice*, back to back:

```
 section (kernel object, shared)          virtual address space of EVERY participating process
 +---------------------+                  base           +------------------+
 | control block  4 K  |  ---- view 0 --> |   header        |  section [0, 64K)
 | (reserved)          |                  +------------------+  base + 64K
 +---------------------+  ---- view 1 --> |   data      D    |  section [64K, 64K + D)
 | data          D     |                  +------------------+  base + 64K + D
 |                     |  ---- view 2 --> |   mirror    D    |  section [64K, 64K + D)   <-- the SAME pages again
 +---------------------+                  +------------------+  base + 64K + 2D

 element index i lives at  data + (i mod C) * sizeof(T)
 a bucket of n elements starting near the end of the ring simply runs on into the mirror:

        data                                          data + D            mirror                    data + 2D
        |..................................[==== bucket ====|=== continues ===]...............................|
                                          ^ same physical pages as the start of `data`
```

Because the mirror aliases the data pages, `new Span<T>(data + offset, n)` is always contiguous even when `offset + n·sizeof(T) > D`;
a write through the mirror lands at the start of the ring. The writer publishes its cursor with one `Interlocked.Exchange`; readers publish
theirs the same way; no data is ever copied and no lock is ever taken.

A second process opens the section by name (`OpenFileMappingW`) or from a handle passed by `DuplicateSectionHandleTo(pid)`, peeks at the
control block, and maps the same three views. The creator picks its base address from a hashed hint in a quiet region of the 64-bit address
space (`0x4000_0000_0000…`), and openers first try the creator's base: if that range is free, the buffer sits at **the same virtual address in
both processes** (`IsMappedAtCreatorAddress`); otherwise it maps anywhere. Correctness never depends on it (shared memory holds only cursors
and offsets), it just makes pointers comparable across processes.

Teardown is three `UnmapViewOfFile` calls plus `CloseHandle(section)`; the section dies with its last handle or view anywhere, so a crashed
process can never leak it.

## Synchronization and the signaling layer

All coordination is lock-free through the control block: the writer's `WriteCursor`, each reader's `ReadCursor` in its own 64-byte slot, a
`WaitersMask` (readers that went to sleep, with their wanted cursor), and a `WriterWaiting` flag (the writer went to sleep for space).

* **Data path** (`Commit`, `TryRead`, `Advance`): one cache-line store/load each; no kernel object is touched.
* **Waiting**: a reader that finds too little data spins polling `WriteCursor` for an adaptive budget, then publishes its slot bit in
  `WaitersMask` (Dekker handshake with the writer's commit), re-checks, and only then enters a kernel wait. The writer does the symmetric thing
  for space, with the process handles of the readers it waits on in its wait set (a reader that dies wakes the writer immediately).
  The budget is learned from the gaps each waiting party actually sees (see "Waiting: latency versus CPU" below): by default a reader spins
  up to `SpinTime` (20 µs) while most gaps are shorter than that and not at all while they are longer; `MaxSpinTime` lets it spin through
  longer gaps for sub-microsecond latency.
* **Async**: `await reader.Wait(n)` hands the wait to a thread owned by the reader, which spins, then blocks, and completes the task.
  By default (`AllowSynchronousContinuations`) the code after the `await` runs right there, and every further `Wait` made on that thread runs
  synchronously, so an `await` loop over a reader costs the same as a `WaitSync` loop: no thread-pool hop, no extra system call.
* **Signaling**: `Commit` looks at `WaitersMask` and issues a wake only for readers whose wanted cursor is now satisfied; `Advance` wakes the
  writer only when it is the `Advance` that crosses the writer's target. So the fast path never makes a syscall, and a blocked side costs one
  `SetEvent` per sleep, not per commit (measured: 1 M commits against a spinning reader in another process = 0 kernel calls on both sides).

The kernel primitive behind "now block" / "now wake" is isolated in `Photone.Ipc.Signaling.SignalBackend` (internal). The protocol never
calls it except at those two points. v1 ships `NamedEventBackend`: 32 auto-reset events (one per reader slot) plus one for the writer, named
from the buffer's random `InstanceId`, waited on with `WaitForMultipleObjects` together with process handles. The header records the backend
id; an opener instantiates whatever the header names and refuses unknown ids, so peers never silently mismatch. Reserved layout words
(`WaiterThreadId` / `BackendWord` per slot, `WriterWaiterThreadId` / `WriterBackendWord`, a 128-byte backend-private area) exist so that the
alternatives planned for the next phase need no layout change:

| Candidate | Status / measured (see `docs/SIGNALING-PROBE.md`) |
|---|---|
| Named events (`SetEvent` / `WaitForMultipleObjects`) | **v1**. ~22 µs round trip when both sides block; ~9 µs when the target core is kept out of deep idle. |
| Raw `NtSetEvent` / `NtWaitForMultipleObjects` | Same objects, ~1 µs less per round trip; a drop-in change inside the same backend. |
| `NtAlertThreadByThreadId` / `NtWaitForAlertByThreadId` | Measured **process-local only** (`STATUS_ACCESS_DENIED` cross-process); in-process candidate. |
| `WaitOnAddress` / `WakeByAddressSingle` | Process-local; in-process candidate (the reserved `BackendWord` is the address). |
| Keyed events (`NtCreateKeyedEvent`) | One kernel object instead of 33; same wake path. To measure. |
| Pure spin with core pinning, `UMWAIT`/`TPAUSE` | 0.1 µs round trip (measured); costs a core per waiting side. Available via `SpinTime = Timeout.InfiniteTimeSpan`, or adaptively via `MaxSpinTime`. |

The probe's main finding: the kernel wake itself is cheap; the ~15 µs is the *idle core* waking up (and ~50-60 µs once the core has idled for a
millisecond and dropped into a deep C-state). That is why the lever is how long a waiter spins, not which kernel primitive wakes it.

## Waiting: latency versus CPU

`Photone.Ipc.Benchmarks.exe --latency` measures what a wait policy actually trades. A writer process publishes a timestamp every *gap*
(busy-waiting to a fixed schedule, so its own core never sleeps); a reader in another process measures delivery latency (`now - stamp`,
QueryPerformanceCounter is machine-wide) and its whole process's CPU, from CPU cycle counts (`QueryProcessCycleTime`, calibrated on a busy
core; the tick-sampled process times misattribute short wake-ups). The reader process may use any core except the writer's physical core and
runs in the High priority class. Same laptop, one session; cells are p50 latency and reader CPU (100% = one core busy):

| reader | gap 10 µs | gap 50 µs | gap 200 µs | gap 1 ms | gap 5 ms |
|---|---:|---:|---:|---:|---:|
| `WaitSync`, before (fixed 20 µs spin) | 0.2 µs, 96% | 17 µs, 55% | 21 µs, 16% | 28 µs, 4% | 37 µs, 1% |
| `WaitSync`, **default now** (adaptive, up to 20 µs) | 0.2 µs, 96% | 17 µs, **17%** | 20 µs, **5%** | 44 µs, 1% | 51 µs, 1% |
| `WaitSync`, `MaxSpinTime = 1 ms` | 0.2 µs, 100% | **0.3 µs**, 95% | **0.3 µs**, 97% | **0.6 µs**, 98% | 35 µs, 1% |
| `WaitSync`, `MaxSpinTime = 5 ms` | 0.2 µs, 93% | 0.3 µs, 91% | 0.4 µs, 97% | 0.5 µs, 100% | **1.0 µs**, 100% |
| `WaitSync`, block immediately (`SpinTime = 0`) | 11 µs, 35% | 18 µs, 19% | 21 µs, 5% | 27 µs, 2% | 34 µs, 1% |
| `await Wait`, before (thread-pool continuation) | 12 µs, 219% | 20 µs, 131% | 26 µs, 86% | 62 µs, 31% | 102 µs, 15% |
| `await Wait`, **default now** (inline continuation) | **0.2 µs**, 100% | 18 µs, **18%** | 19 µs, **5%** | **27 µs**, **2%** | **35 µs**, **2%** |
| `await Wait`, default now + `MaxSpinTime = 1 ms` | 0.3 µs, 95% | **0.3 µs**, 98% | **0.4 µs**, 97% | **0.9 µs**, 96% | 35 µs, 2% |
| `await Wait`, `AllowSynchronousContinuations = false` | 12 µs, 200% | 22 µs, 159% | 42 µs, 64% | 69 µs, 13% | 76 µs, 4% |

Reading it:

- **Before**, a fixed 20 µs spin ahead of every kernel wait burned 55% of a core at 50 µs gaps for no latency gain: the spin never caught the data.
  The adaptive default keeps the same latency and stops spinning once most gaps are longer than the budget (17% left is the cost of the kernel
  wait and wake-up per message, the same as blocking immediately).
- **Sub-microsecond delivery through millisecond gaps** costs one core while traffic is that dense and nothing when it is not: with
  `MaxSpinTime = 1 ms` the reader spins through 50 µs-1 ms gaps (0.3-0.9 µs) and goes back to blocking (1-2% CPU) when gaps grow to 5 ms.
  Without spinning, a gap of a millisecond or more costs 30-60 µs, because the core has dropped into a deep idle state.
- **`await` now costs what `WaitSync` costs.** Before, every suspended wait went through an event, the waiter thread and a thread-pool
  continuation: 12-100 µs and up to two cores of CPU. With the continuation inline on the reader's thread, an await loop never suspends while it
  keeps up. Thread-pool continuations stay available (`AllowSynchronousContinuations = false`) and remain slow; with a large spin budget they
  also starve the pool (p99 in the tens of milliseconds), so combine `MaxSpinTime` with the default inline continuations.
- The p99 columns (in the benchmark output) are dominated by the laptop's scheduler noise in this run (30-60 µs for every mode that blocks).

Choosing options:

```csharp
// default: sub-microsecond while traffic is denser than ~20 µs per message, otherwise block (a few % of a core per 10 k messages/s)
var reader = buffer.CreateReader();

// latency first: spin through gaps up to 1 ms (up to one core while traffic is that dense, ~0 when idle)
var fast = buffer.CreateReader(new ReaderOptions { MaxSpinTime = TimeSpan.FromMilliseconds(1) });

// never spin (many readers, oversubscribed machine)
var frugal = buffer.CreateReader(new ReaderOptions { SpinTime = TimeSpan.Zero });

// the code after `await reader.Wait(n)` must run on the thread pool
var pooled = buffer.CreateReader(new ReaderOptions { AllowSynchronousContinuations = false });
```

The writer's wait for space follows the same policy (`RingBufferOptions.SpinTime` / `MaxSpinTime`).

## Measured on this dev box

Intel Core i7-8550U (4 cores / 8 threads, laptop, "Balanced" power plan), 16 GB, Windows 11 26200, .NET 10.0.12. All numbers from
`Photone.Ipc.Benchmarks.exe --quick` (writer/side A pinned to logical core 2, reader/peer to core 4; different physical cores). RTT = a
full round trip (A writes 8 bytes, B echoes, A reads); one-way latency is about half. The wake mode is the *reader's* `SpinTime`
(`spin` = never block, `default` = 20 µs spin then kernel wait, `block` = kernel wait immediately, `async` = `await reader.Wait(1)` with the
library defaults, i.e. the continuation runs on the reader's waiter thread and later waits there run synchronously, `async-pool` =
`await reader.Wait(1)` with no spin and thread-pool continuations, the v1 async path).

Methodology: warm-up is time-based (500 ms, past the JIT tiering delay). The spin/default rows time batches of 100 round trips and report
per-round percentiles of the batch means, because `QueryPerformanceCounter` ticks every 100 ns and costs ~20 ns per call, so a single
150 ns round trip cannot be timed on its own; the kernel-wake rows are timed per round.

| scenario | mode | rounds / buckets | result | kernel activity |
|---|---|---:|---|---|
| in-process ping-pong RTT | spin | 200,000 | p50 **0.14** / p99 0.18 / p99.9 0.48 / max 0.67 µs | 0 kernel waits, 0 signals |
| in-process ping-pong RTT | default | 100,000 | p50 **0.15** / p99 0.19 / p99.9 0.27 / max 0.52 µs | 33 kernel waits in 100,000 rounds |
| in-process ping-pong RTT | block | 10,000 | p50 **20.5** / p99 32.6 / p99.9 89 / max 2346 µs | 2 kernel waits + 2 `SetEvent` per round |
| in-process ping-pong RTT | async | 100,000 | p50 **0.28** / p99 0.55 / p99.9 1.8 / max 2.6 µs | 79 kernel waits in 100,000 rounds; no thread hop |
| in-process ping-pong RTT | async-pool | 10,000 | p50 **19.7** / p99 146 / p99.9 3490 / max 6767 µs | waiter thread + thread-pool continuation per wait |
| cross-process ping-pong RTT | spin | 100,000 | p50 **0.14** / p99 0.19 / p99.9 0.32 / max 0.60 µs | 0 kernel waits, 0 signals, same VA in both processes |
| cross-process ping-pong RTT | default | 50,000 | p50 **0.14** / p99 0.20 / p99.9 0.66 / max 0.66 µs | 43 kernel waits in 50,000 rounds |
| cross-process ping-pong RTT | block | 10,000 | p50 **20.6** / p99 34.8 / p99.9 116 / max 741 µs | 2 kernel waits + 2 `SetEvent` per round |
| cross-process ping-pong RTT | async | 100,000 | p50 **0.27** / p99 0.36 / p99.9 0.47 / max 0.71 µs | 116 kernel waits in 100,000 rounds; no thread hop |
| cross-process ping-pong RTT | async-pool | 10,000 | p50 **23.5** / p99 77.8 / p99.9 103 / max 475 µs | waiter thread + thread-pool continuation per wait |
| in-process throughput | 3.9 KiB buckets, fill+sum | 536,870 | **7.9 GB/s**, 1.97 M commits/s | 51 kernel waits total |
| cross-process throughput | 3.9 KiB buckets, fill+sum | 536,870 | **7.5 GB/s**, 1.87 M commits/s | 63 kernel waits total |
| in-process throughput | 62.5 KiB buckets, fill+sum | 33,554 | **9.9 GB/s**, 0.15 M commits/s | 76 kernel waits total |
| cross-process throughput | 62.5 KiB buckets, fill+sum | 33,554 | **7.1 GB/s**, 0.11 M commits/s | 370 kernel waits total |

Throughput runs push 2 GiB of `float` through a 64 MiB ring; the writer fills every bucket (`Span.Fill`), the reader sums every element with
`Vector<float>` and validates the first and last element of every chunk, so both sides' memory traffic is included. The bucket sizes (1000 and
16000 floats) do not divide the ring, so buckets regularly wrap through the mirror. The `max` column in the ping-pong rows is scheduler
noise; the p99.9 column is the honest tail. The whole harness takes ~7 s.

BenchmarkDotNet hot path (`Photone.Ipc.Benchmarks.exe --filter '*'`, one thread writes and reads a bucket of `float`, `MemoryDiagnoser`;
the drain thread of `Write_SpinningReaderThread` exists only while that benchmark runs):

| Method | BucketElements | Mean | Allocated | Note |
|---|---:|---:|---:|---|
| `WriteRead_Protocol` | 256 | 21.3 ns | 0 B | GetBucket + Commit + TryRead + Advance, payload untouched |
| `WriteRead_FillAndSum` | 256 | 86.1 ns | 0 B | + `Span.Fill` by the writer and a vectorised sum by the reader (1 KiB) |
| `WriteRead_FillAndSum_OddBucket` | 256 | 91.8 ns | 0 B | 257 elements: buckets regularly wrap through the mirror (+7 %) |
| `Write_SpinningReaderThread` | 256 | 10.9 ns | 0 B | writer only; a second thread drains (cross-core cursor traffic) |
| `WriteRead_Protocol` | 4096 | 19.9 ns | 0 B | |
| `WriteRead_FillAndSum` | 4096 | 1.30 µs | 0 B | 16 KiB filled + summed = ~25 GB/s through L1/L2 |
| `WriteRead_FillAndSum_OddBucket` | 4096 | 1.41 µs | 0 B | 4097 elements, wrapping (+8 %) |
| `Write_SpinningReaderThread` | 4096 | 13.3 ns | 0 B | |
| `WriteRead_Protocol` | 65536 | 23.3 ns | 0 B | the protocol cost does not depend on the bucket size |
| `WriteRead_FillAndSum` | 65536 | 17.9 µs | 0 B | 256 KiB filled + summed = ~29 GB/s |
| `WriteRead_FillAndSum_OddBucket` | 65536 | 18.3 µs | 0 B | 65537 elements, wrapping (+2 %) |
| `Write_SpinningReaderThread` | 65536 | 17.0 ns | 0 B | |

(BenchmarkDotNet v0.15.8, .NET 10.0.12, RyuJIT x86-64-v3. `MemoryDiagnoser` reports `-` = 0 B for all twelve. The wrap-around cost
is the double mapping's own overhead: a span that crosses the data/mirror boundary touches two TLB entries and two page sets, nothing else.)

Zero bytes are allocated per operation in every benchmark; the test suite additionally asserts 0 B across 1 M `GetBucket/Commit/Wait/TryRead/Advance`
and across suspended-then-completed async waits.

## Compared with named pipes and TCP loopback

`Photone.Ipc.Benchmarks.exe --compare` runs the cross-process rows above next to the two fastest plain-.NET transports between Windows
processes that do not share memory: a byte-mode synchronous named pipe (`NamedPipeServerStream`/`NamedPipeClientStream`) and a TCP loopback
socket (`NoDelay`, synchronous `Send`/`Receive`). Same cores, same peer-process discipline, same message shapes: an 8-byte ping-pong round
trip, and 2 GiB streamed in fixed buckets that the producer fills and the consumer sums. The pipe and socket use 1 MiB buffers; the
stream consumer reads up to 1 MiB per call and parses buckets out of its receive buffer (one read per bucket would measure the wake-up cost
per bucket instead of the transport). Representative run; a second run agreed within ~15 %:

| scenario | ring buffer (spin) | ring buffer (block) | ring buffer (`await Wait`) | named pipe | TCP loopback |
|---|---:|---:|---:|---:|---:|
| ping-pong RTT p50 | **0.14 µs** | 21.4 µs | 20.9 µs | 24.7 µs | 59.1 µs |
| ping-pong RTT p99 | 0.18 µs | 36.9 µs | 66.4 µs | 39.2 µs | 94.4 µs |
| ping-pong RTT p99.9 | 0.30 µs | 118 µs | 139 µs | 61.1 µs | 179 µs |
| throughput, 3.9 KiB buckets | **7.8 GB/s** | | | 1.38 GB/s | 0.19 GB/s |
| throughput, 62.5 KiB buckets | **7.1 GB/s** | | | 4.02 GB/s | 1.16 GB/s |

The transport itself, with the workload taken out. `int` buckets; the producer touches one element per bucket (pipe and socket write the same
prepared buffer every time), the consumer makes one vectorised pass counting the elements equal to 1; and, for the ring buffer only, a
"protocol only" row where nobody touches the payload at all (commit + advance across the two processes). Best of 3 per row:

| bucket | ring buffer, touch1 + count1 | ring buffer, protocol only | named pipe, touch1 + count1 | TCP loopback, touch1 + count1 |
|---|---:|---:|---:|---:|
| 3.9 KiB (1000 ints) | **12.6 GB/s** | 31 M commits/s = 32 ns/commit | 1.13 GB/s | 0.17 GB/s |
| 62.5 KiB (16000 ints) | **8.6 GB/s** | 43 M commits/s = 23 ns/commit | 3.66 GB/s | 1.48 GB/s |
| 1000 KiB (256000 ints) | **10.9 GB/s** | 34 M commits/s = 30 ns/commit | 3.63 GB/s | 2.34 GB/s |

With the producer's fill removed the ring buffer runs at 9-13 GB/s, which is the single-core memory read bandwidth of this laptop (the
consumer streams 2 GiB out of a 64 MiB ring that does not fit in cache): the library itself costs 23-32 ns per bucket regardless of the
bucket size, i.e. two cache-line hand-offs (write cursor one way, read cursor the other) and nothing else. The pipe and the socket copy
every byte twice (user to kernel, kernel to user), which caps them at 1-4 GB/s no matter how little the endpoints do with the data.

Reading it:

- With a spinning reader the ring buffer answers in 140 ns, about 170x faster than a named pipe and 400x faster than TCP loopback. That is the
  zero-copy, zero-syscall path: the message *is* the cache line.
- Even when every wait is a kernel wait (`block`), the ring buffer is a little faster than a named pipe (one `SetEvent` + one wait per hop
  versus `WriteFile` + `ReadFile` with a copy on each side) and about 3x faster than TCP.
- Throughput with small buckets is where copies and syscalls hurt the most: the pipe manages 1.4 GB/s at 3.9 KiB per write, TCP loopback
  0.2 GB/s (Windows delivers a loopback segment synchronously inside the `Send` call, about 20 µs per segment on this laptop), the ring
  buffer 7.8 GB/s regardless of the bucket size because nothing is copied and the consumer never enters the kernel while data keeps coming.
- The ring buffer's remaining cost at large buckets is memory bandwidth (the writer fills, the reader sums: two passes over 2 GiB); the
  pipe at 62.5 KiB is within 2x of it because its per-write overhead is amortised, while it still copies every byte twice.

## Building, testing, benchmarking

```
dotnet build E:\GitHub\photone-ipc\Photone.Ipc.slnx -c Release
dotnet test  --project E:\GitHub\photone-ipc\tests\Photone.Ipc.Tests\Photone.Ipc.Tests.csproj -c Release     # 419 tests, ~35 s

# quick Stopwatch harness (latency + throughput, in-process and cross-process; ~7 s)
E:\GitHub\photone-ipc\bench\Photone.Ipc.Benchmarks\bin\Release\net10.0\Photone.Ipc.Benchmarks.exe --quick [--cores 2,4]

# delivery latency and reader CPU under paced traffic, per wait policy (~1.5 min; --modes / --intervals to narrow it down)
E:\GitHub\photone-ipc\bench\Photone.Ipc.Benchmarks\bin\Release\net10.0\Photone.Ipc.Benchmarks.exe --latency

# the same cross-process measurements next to a named pipe and a TCP loopback socket, plus light-workload and protocol-only rows (~2.5 min)
E:\GitHub\photone-ipc\bench\Photone.Ipc.Benchmarks\bin\Release\net10.0\Photone.Ipc.Benchmarks.exe --compare [--cores 2,4]

# create / open / release costs without and with a RingBufferPool, in-process and with a peer process (~10 s)
E:\GitHub\photone-ipc\bench\Photone.Ipc.Benchmarks\bin\Release\net10.0\Photone.Ipc.Benchmarks.exe --lifecycle

# BenchmarkDotNet microbenchmarks (a few minutes)
E:\GitHub\photone-ipc\bench\Photone.Ipc.Benchmarks\bin\Release\net10.0\Photone.Ipc.Benchmarks.exe --filter '*'

# samples: a writer streaming a 440 Hz sine of floats, and any number of readers printing stats (separate consoles)
dotnet run --project E:\GitHub\photone-ipc\samples\Photone.Ipc.Samples.Writer -c Release -- --rate 48000     # --rate 0 = unpaced
dotnet run --project E:\GitHub\photone-ipc\samples\Photone.Ipc.Samples.Reader -c Release
```

The tests spawn `Photone.Ipc.TestChild.exe` for the cross-process cases (reader/writer crash, same-address mapping, join storms, torn
initialisation, echo latency, "no syscall when nobody waits"). The quick harness spawns *itself* as the peer process.

The Win32 declarations, structs and flag enums are generated by [CsWin32](https://github.com/microsoft/CsWin32) from the official Win32 metadata:
each project lists what it needs in its own `NativeMethods.txt`, and `Internal/Kernel.cs` is the thin typed layer the library calls (raw handles on
the signal path, the flag names the design uses, the error helpers). It is an analyzer-only package: nothing is shipped with the library.

Repository layout: `src/Photone.Ipc` (library), `tests/` (xunit.v3 tests + test child), `bench/Photone.Ipc.Benchmarks`, `samples/`,
`docs/DESIGN.md` (the authoritative spec), `docs/RESEARCH.md` (Win32 mapping, sync protocol, .NET specifics, vmcircbuffer analysis),
`docs/SIGNALING-PROBE.md` (measured wake-up primitives), `docs/DEVIATIONS.md` (where the implementation departs from the design and why).

## Limitations (v1)

* Windows x64 only (`VirtualAlloc2` / `MapViewOfFile3`, Windows 10 1803+). Linux would need `memfd_create` + two `mmap`s; not started.
* One writer per buffer, at most 32 readers. A reader that is alive but never advances stalls the writer forever: that is the requested
  back-pressure contract (a `MaxReaderLag` escape hatch is reserved for later).
* `T : unmanaged` and `capacity <= 2^30` elements; capacity rounds up to a power of two with `capacity * sizeof(T)` a multiple of 64 KiB.
* The whole ring is committed at creation (`SEC_COMMIT`); multi-GB rings may hit the commit limit.
* Same virtual address in every process is best effort (`IsMappedAtCreatorAddress` tells you); never rely on it.
* Each reader that has ever suspended an async `Wait` owns one parked waiter thread (needed so every candidate backend fits behind the same
  `IValueTaskSource`); fine for a handful of readers.
* Spinning costs a core while it lasts; on oversubscribed hosts set `SpinTime = TimeSpan.Zero`.
* Cross-user / cross-session sharing (`Global\`) works but is untested beyond name handling; liveness falls back to polling when a peer's
  process handle cannot be opened.
* Named events are the only signaling backend today; `--backend` switching in the harness arrives with the second backend.
* Tags allocate (in process: the tag objects and the object log's chunks; across processes: serialization and one object per tag per reader); only
  the element path is allocation-free. Shared tag memory is committed as tags need it and never decommitted while the buffer's section exists (Windows
  cannot decommit shared pages); one ring holds at most 16 GiB of tags that readers have not read past, and the last persistent tag of every key
  takes just under 2 GiB at most. It is a commit charge against the system limit, not private bytes, and it goes away only with the section — watch it
  with `buffer.TagMemory` and `pool.IdleTagBytes`, bound it with `MaxUnreadTags` / `MaxUnreadTagBytes`, and keep the set of persistent keys bounded (a key
  per message fills the table). A reader that dies holds tag memory until a sweep evicts it (before the log grows, or when the writer blocks for
  space). Readers of the writer's own buffer share the tag instances: treat tags as immutable once added.
