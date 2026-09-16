# photone-ipc

Ultra-fast, zero-copy, broadcast IPC for .NET on Windows: a **double-mapped (virtual-memory mirrored) ring buffer** that lives in
shared memory, with one writer and up to 32 independent readers in any number of processes.

* `Span<T>` in, `ReadOnlySpan<T>` out, straight on the shared pages: no serialization, no copying, no allocations on the hot path.
* A span never has to wrap: the data region is mapped twice back-to-back, so the bytes past the end of the ring are the bytes at its start.
* Same code path in-process and cross-process. Readers in other processes map the *same* section (best effort at the *same* virtual address).
* Wake-ups are cheap by construction: spin first, kernel wait only when a peer is provably asleep; **no syscall at all when nobody is waiting**.
* Robust to crashes: a dead reader is detected (PID + process start time, plus its process handle in the writer's wait set) and evicted; a dead
  or closed writer makes every reader's `Wait` return `false` with a `Status`.
* The signaling mechanism (how a blocked side is woken) is an isolated, swappable layer; the shared-memory layout reserves the words that
  alternative mechanisms need, so switching it never changes the layout.

Requirements: Windows 10 1803+ (`VirtualAlloc2` / `MapViewOfFile3`), x64, .NET 10 (C# 14). Zero package dependencies in the library.

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
{
    reader.TryRead(100, out var chunk);         // chunk.Span : ReadOnlySpan<float>
    Use(chunk.Span);
    reader.Advance(90);                         // consume 90 (may advance less than read)
}
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

Options: `RingBufferOptions { SpinTime = 20 µs, LivenessCheckInterval = 10 ms, InitializationTimeout = 5 s, PreferredBaseAddress, PreFault = true }`,
`ReaderOptions { SpinTime = 20 µs, AsyncSpinTime = 5 µs }`. A spin budget of `Timeout.InfiniteTimeSpan` never touches the kernel (one busy core
per waiting side); `TimeSpan.Zero` blocks immediately.

## How cross-process zero-copy works

One pagefile-backed section of `64 KiB + D` bytes holds a 4 KiB control block (cursors, reader slots) followed by the data. Every process
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
* **Waiting**: a reader that finds too little data spins for `SpinTime` (default 20 µs) polling `WriteCursor`, then publishes its slot bit in
  `WaitersMask` (Dekker handshake with the writer's commit), re-checks, and only then enters a kernel wait. The writer does the symmetric thing
  for space, with the process handles of the readers it waits on in its wait set (a reader that dies wakes the writer immediately).
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
| Pure spin with core pinning, `UMWAIT`/`TPAUSE` | 0.1 µs round trip (measured); costs a core per waiting side. Already available via `SpinTime = Timeout.InfiniteTimeSpan`. |

The probe's main finding: the kernel wake itself is cheap; the ~15 µs is the *idle core* waking up. That is why every wait spins first.

## Measured on this dev box

Intel Core i7-8550U (4 cores / 8 threads, laptop, "Balanced" power plan), 16 GB, Windows 11 26200, .NET 10.0.12. All numbers from
`Photone.Ipc.Benchmarks.exe --quick` (writer/side A pinned to logical core 2, reader/peer to core 4; different physical cores). RTT = a
full round trip (A writes 8 bytes, B echoes, A reads); one-way latency is about half. The wake mode is the *reader's* `SpinTime`
(`spin` = never block, `default` = 20 µs spin then kernel wait, `block` = kernel wait immediately, `async` = `await reader.Wait(1)` with a
zero spin budget, i.e. every wait suspends through the waiter thread and a thread-pool continuation).

Methodology: warm-up is time-based (500 ms, past the JIT tiering delay). The spin/default rows time batches of 100 round trips and report
per-round percentiles of the batch means, because `QueryPerformanceCounter` ticks every 100 ns and costs ~20 ns per call, so a single
150 ns round trip cannot be timed on its own; the kernel-wake rows are timed per round.

| scenario | mode | rounds / buckets | result | kernel activity |
|---|---|---:|---|---|
| in-process ping-pong RTT | spin | 200,000 | p50 **0.14** / p99 0.18 / p99.9 0.48 / max 0.67 µs | 0 kernel waits, 0 signals |
| in-process ping-pong RTT | default | 100,000 | p50 **0.15** / p99 0.19 / p99.9 0.27 / max 0.52 µs | 33 kernel waits in 100,000 rounds |
| in-process ping-pong RTT | block | 10,000 | p50 **20.5** / p99 32.6 / p99.9 89 / max 2346 µs | 2 kernel waits + 2 `SetEvent` per round |
| in-process ping-pong RTT | async | 10,000 | p50 **25.6** / p99 40.2 / p99.9 76 / max 1483 µs | waiter thread + thread-pool continuation per wait |
| cross-process ping-pong RTT | spin | 100,000 | p50 **0.14** / p99 0.19 / p99.9 0.32 / max 0.60 µs | 0 kernel waits, 0 signals, same VA in both processes |
| cross-process ping-pong RTT | default | 50,000 | p50 **0.14** / p99 0.20 / p99.9 0.66 / max 0.66 µs | 43 kernel waits in 50,000 rounds |
| cross-process ping-pong RTT | block | 10,000 | p50 **20.6** / p99 34.8 / p99.9 116 / max 741 µs | 2 kernel waits + 2 `SetEvent` per round |
| cross-process ping-pong RTT | async | 10,000 | p50 **22.4** / p99 69.1 / p99.9 83 / max 377 µs | waiter thread + thread-pool continuation per wait |
| in-process throughput | 3.9 KiB buckets, fill+sum | 536,870 | **7.9 GB/s**, 1.97 M commits/s | 51 kernel waits total |
| cross-process throughput | 3.9 KiB buckets, fill+sum | 536,870 | **7.5 GB/s**, 1.87 M commits/s | 63 kernel waits total |
| in-process throughput | 62.5 KiB buckets, fill+sum | 33,554 | **9.9 GB/s**, 0.15 M commits/s | 76 kernel waits total |
| cross-process throughput | 62.5 KiB buckets, fill+sum | 33,554 | **7.1 GB/s**, 0.11 M commits/s | 370 kernel waits total |

Throughput runs push 2 GiB of `float` through a 64 MiB ring; the writer fills every bucket (`Span.Fill`), the reader sums every element with
`Vector<float>` and validates the first and last element of every chunk, so both sides' memory traffic is included. The bucket sizes (1000 and
16000 floats) do not divide the ring, so buckets regularly wrap through the mirror. The `max` column in the ping-pong rows is scheduler
noise; the p99.9 column is the honest tail. The whole harness takes ~7 s.

BenchmarkDotNet hot path (`Photone.Ipc.Benchmarks.exe --filter '*'`, one thread writes and reads a bucket of `float`, `MemoryDiagnoser`):

| Method | BucketElements | Mean | Allocated | Note |
|---|---:|---:|---:|---|
| `WriteRead_Protocol` | 256 | 21.7 ns | 0 B | GetBucket + Commit + TryRead + Advance, payload untouched |
| `WriteRead_FillAndSum` | 256 | 72.2 ns | 0 B | + `Span.Fill` by the writer and a vectorised sum by the reader (1 KiB) |
| `Write_SpinningReaderThread` | 256 | 17.4 ns | 0 B | writer only; a second thread drains (cross-core cursor traffic) |
| `WriteRead_Protocol` | 4096 | 21.7 ns | 0 B | |
| `WriteRead_FillAndSum` | 4096 | 1.12 µs | 0 B | 16 KiB filled + summed = ~29 GB/s through L1/L2 |
| `Write_SpinningReaderThread` | 4096 | 18.5 ns | 0 B | |
| `WriteRead_Protocol` | 65536 | 22.4 ns | 0 B | the protocol cost does not depend on the bucket size |
| `WriteRead_FillAndSum` | 65536 | 18.5 µs | 0 B | 256 KiB filled + summed = ~28 GB/s |
| `Write_SpinningReaderThread` | 65536 | 19.2 ns | 0 B | |

(BenchmarkDotNet v0.15.8, .NET 10.0.12, RyuJIT x86-64-v3, 4 min run. `MemoryDiagnoser` reports `-` = 0 B for all nine.)

Zero bytes are allocated per operation in every benchmark; the test suite additionally asserts 0 B across 1 M `GetBucket/Commit/Wait/TryRead/Advance`
and across suspended-then-completed async waits.

## Building, testing, benchmarking

```
dotnet build E:\GitHub\photone-ipc\Photone.Ipc.slnx -c Release
dotnet test  --project E:\GitHub\photone-ipc\tests\Photone.Ipc.Tests\Photone.Ipc.Tests.csproj -c Release     # 265 tests, ~15 s

# quick Stopwatch harness (latency + throughput, in-process and cross-process; < 60 s, typically ~3 s)
E:\GitHub\photone-ipc\bench\Photone.Ipc.Benchmarks\bin\Release\net10.0\Photone.Ipc.Benchmarks.exe --quick [--cores 2,4]

# BenchmarkDotNet microbenchmarks (a few minutes)
E:\GitHub\photone-ipc\bench\Photone.Ipc.Benchmarks\bin\Release\net10.0\Photone.Ipc.Benchmarks.exe --filter '*'

# samples: a writer streaming a 440 Hz sine of floats, and any number of readers printing stats (separate consoles)
dotnet run --project E:\GitHub\photone-ipc\samples\Photone.Ipc.Samples.Writer -c Release -- --rate 48000     # --rate 0 = unpaced
dotnet run --project E:\GitHub\photone-ipc\samples\Photone.Ipc.Samples.Reader -c Release
```

The tests spawn `Photone.Ipc.TestChild.exe` for the cross-process cases (reader/writer crash, same-address mapping, join storms, torn
initialisation, echo latency, "no syscall when nobody waits"). The quick harness spawns *itself* as the peer process.

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
