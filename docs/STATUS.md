# photone-ipc status (2026-09-17)

Phase 1 (double-mapped cross-process ring buffer) is implemented, reviewed, fixed and verified on the dev box
(Windows 11 26200, x64, .NET SDK 10.0.112). Phase 2 (fastest cross-process signaling) has its baseline measurements in
`docs/SIGNALING-PROBE.md` and a swappable backend seam in the code. `RingBufferPool` (2026-09-17) reuses the shared memory of released
buffers on both sides, with an idle timeout (DESIGN §15); the layout is version 2.

## What exists

```
Photone.Ipc.slnx, global.json, Directory.Build.props, Directory.Packages.props, .editorconfig, .gitignore, README.md
docs/
  DESIGN.md              final design (12 sections + post-review section 13 + adaptive waiting section 14 + buffer pool section 15); the pseudo-code matches the code
  RESEARCH.md            the four research reports (Win32 mapping, sync protocol, .NET specifics, vmcircbuffer)
  DEVIATIONS.md          35 numbered deviations from DESIGN.md with reasons (stage A/B/C + post-review)
  REVIEW-NOTES.md        31 review findings: 22 fixed, 9 refuted/deferred, each with the reason
  SIGNALING-PROBE.md     measured cross-process wake costs (spin / Event / NtEvent / SignalObjectAndWait / NtAlert), phase-2 status
  STATUS.md              this file
src/Photone.Ipc/         the library (zero package dependencies, AOT-compatible, InternalsVisibleTo tests/bench)
  RingBuffer.cs          Create / Open(name) / Open(handle) / DuplicateSectionHandleTo / properties / Dispose / finalizer
  RingBuffer.Writer.cs   GetBucket / TryGetBucket / EndWrite / SignalReaders / ScanMin / WaitForSpace / laggards / CloseWriter
  RingBuffer.Readers.cs  CreateReader (slot claim protocol) / SweepDeadSlots / Evict
  RingReader.cs          TryRead / Advance / WaitSync / SpinUntil / BlockUntil / Status (liveness probe) / Dispose / finalizer
  RingReader.Async.cs    Wait(...) => ValueTask<bool> (IValueTaskSource, per-reader waiter thread with idle retirement)
  RingBufferPool.cs      RingBufferPool / RingBufferPoolOptions: reuse check (handle count + InitState handshake), parking, expiry timer, bounds
  Bucket.cs, Chunk.cs    ref structs (Span / Commit / Dispose; ReadOnlySpan)
  Options.cs, ReaderStatus.cs, Exceptions.cs, SafeSectionHandle.cs
  Signaling/SignalBackend.cs, NamedEventBackend.cs, WaitOutcome.cs   the swappable signaling layer (v1: 33 named auto-reset events)
  Internal/Kernel.cs     20 [LibraryImport]s (kernelbase: VirtualAlloc2, MapViewOfFile3; kernel32; ntdll: NtQuerySystemInformation, NtQueryObject)
  Internal/MirroredSection.cs   placeholder + 3 views (header, data, mirror); create / open at the creator's address / teardown; detachable section handle
  Internal/Layout.cs     4096-byte ControlBlock, 32 x 64-byte ReaderSlot, AliasBlock (pooled buffer names), static layout asserts
  Internal/SpinPolicy.cs  adaptive spin budget (DESIGN §14)
  Internal/PooledMapping.cs     one mapping + its events as the pool hands it out and takes it back (DESIGN §15)
  Internal/Capacity.cs, AddressHint.cs, ProcessLiveness.cs, SpinClock.cs, Counters.cs, TestHooks.cs
tests/Photone.Ipc.Tests/       330 xunit.v3 tests (unit, stress, and 25 cross-process tests via TestChild)
tests/Photone.Ipc.TestChild/   child-process verbs: reader, spin-reader, step-reader, writer [--crash], echo, crash-reader,
                               claim-and-die, slow-init, hold-name, join-storm, map-region, pool-reader-loop
bench/Photone.Ipc.Benchmarks/  BenchmarkDotNet hot path, `--quick`, `--compare` (named pipe / TCP), `--latency` (paced delivery latency + CPU),
                               `--lifecycle` (create / open / release costs without and with a pool)
samples/Photone.Ipc.Samples.Writer, .Reader   the user's API sketch verbatim, cross-process, sine wave
```

## Public API (namespace `Photone.Ipc`)

```csharp
var buffer = RingBuffer<float>.Create(1 << 20, "demo");     // this process is the writer; "Local\photone.demo"
using (var bucket = buffer.GetBucket(1024))                   // blocks (spin, then kernel) until 1024 elements are free
{
    bucket.Span.Fill(0);                                      // contiguous through the mirror
    bucket.Commit(1000);                                      // publish a prefix; the rest is dropped
}

var other  = RingBuffer<float>.Open("demo");                  // any process; same virtual address when possible
var reader = other.CreateReader();                            // one of 32 broadcast readers, starts at the head
if (await reader.Wait(100))                                   // ValueTask<bool>; false = writer closed/terminated and < 100 will ever arrive
{
    reader.TryRead(100, out var chunk);                       // exactly 100 or false; chunk.Span is ReadOnlySpan<float>
    Use(chunk.Span);
    reader.Advance(90);                                       // may be less than read
}
```

Also: `TryGetBucket`, `WaitSync(count[, timeout])`, `Wait(count, timeout, ct)`, `Available`, `ReadCursor`, `Status`, `IsCompleted`,
`RingBuffer.Open(SafeSectionHandle)`, `DuplicateSectionHandleTo(pid)`, `RingBufferOptions { SpinTime, MaxSpinTime, LivenessCheckInterval,
InitializationTimeout, PreferredBaseAddress, PreFault, Pool }`, `ReaderOptions { SpinTime, MaxSpinTime, AsyncSpinTime, AllowSynchronousContinuations }`,
`RingBufferPool(RingBufferPoolOptions { IdleTimeout = 30 s, MaxIdleBytes, ClearOnReuse })`, `RingBufferPool.Shared`, `IdleCount`, `IdleBytes`,
`Trim()`, `Dispose()`.

## Verification

- `dotnet build Photone.Ipc.slnx -c Release`: 0 warnings, 0 errors (`TreatWarningsAsErrors`, `AnalysisLevel=latest`).
- `dotnet test --project tests/Photone.Ipc.Tests/Photone.Ipc.Tests.csproj -c Release`: 330/330, three consecutive runs, ~30 s each.
  Process-counter tests (handle count, virtual size) run in the non-parallel collection; 300 pooled create/open/reuse/revive cycles leave 0 handles behind.
- Samples run cross-process at the same virtual address; killing the writer ends the reader with `WriterTerminated` after draining.
- Zero allocations on every hot path (asserted by tests and by BenchmarkDotNet's `MemoryDiagnoser`).

## Measured (quick harness, this laptop: i7-8550U, Balanced power plan)

| scenario | spin | default (20 us spin) | block (kernel) | async (`await Wait`) |
|---|---|---|---|---|
| in-process ping-pong RTT p50 | 0.14 us | 0.15 us | 20.5 us | 25.6 us |
| cross-process ping-pong RTT p50 | 0.14 us | 0.14 us | 20.6 us | 22.4 us |

| throughput (2 GiB of float through a 64 MiB ring, writer fills, reader sums) | in-process | cross-process |
|---|---|---|
| 3.9 KiB buckets | 7.9 GB/s | 7.5 GB/s |
| 62.5 KiB buckets | 9.9 GB/s | 7.1 GB/s |

BenchmarkDotNet (one thread): protocol cost per bucket (GetBucket + Commit + TryRead + Advance) 20-23 ns independent of the bucket size; fill + vectorised sum 86 ns / 1.3 us / 17.9 us for 256 / 4096 / 65536 floats; a bucket that wraps through the mirror costs 2-8 % more; writer-only commit with a spinning reader thread 11-17 ns; 0 B allocated in all twelve.

Compared with the alternatives (`--compare`, same session): ping-pong RTT p50 ring buffer 0.14 us spinning / 21 us blocking, named pipe 25 us,
TCP loopback 59 us; throughput at 3.9 KiB buckets ring buffer 7.8 GB/s, named pipe 1.4 GB/s, TCP loopback 0.2 GB/s; at 62.5 KiB buckets
7.1 / 4.0 / 1.2 GB/s. With the workload taken out (producer touches one int per bucket, consumer counts the ones in one vector pass) the ring
buffer reaches 12.6 / 8.6 / 10.9 GB/s at 3.9 KiB / 62.5 KiB / 1000 KiB buckets (the laptop's single-core memory read bandwidth) against
1.1 / 3.7 / 3.6 GB/s for the pipe and 0.17 / 1.5 / 2.3 GB/s for TCP; the protocol alone costs 23-32 ns per bucket cross-process.
Details and the reading of these numbers are in the README.

Buffer lifecycle (`--lifecycle`, p50, pre-fault on, no pool → `RingBufferPool` on both sides): creator `Create` 308 us → 35 us (64 KiB),
1.0 ms → 41 us (1 MiB), 12.1 ms → 96 us (16 MiB), 49.3 ms → 265 us (64 MiB); creator `Dispose` 113 us / 293 us / 2.9 ms / 12.0 ms → 31-33 us;
peer process `Open` + `CreateReader` 241 us / 864 us / 11.0 ms / 43.8 ms → 44 / 54 / 137 / 321 us; peer release 68 us / 143 us / 1.1 ms / 5.6 ms → 8-11 us.

## Known limitations (v1)

- Windows 10 1803+ / x64 only; one writer per buffer (the creator); no writer takeover; 32 readers.
- A live reader that never advances stalls the writer forever (the requested backpressure contract); only dead processes are evicted.
- `Local\` namespace by default: sharing a section handle into another logon session is detected and refused (use a `Global\` name).
- Same virtual address in every process is best effort.
- `Dispose` of the writer while a bucket is outstanding on another thread is unsupported; `Dispose` while blocked in `GetBucket` is supported.
- 64-byte reader slots pair up under the adjacent-line prefetcher (multi-reader configurations only); a 128-byte slot layout is a v2 candidate.
- `RingBufferPool` reuses a section only after every other process has closed it (readers that never dispose keep it busy until the idle
  timeout releases the pool's side); only disposed buffers return their mapping; reuse needs the same data size and namespace. A pool is for
  one trust domain: a process that mapped an earlier buffer can keep a view of the section and see the next buffer in it.

## Phase 2, step 1: adaptive waiting (done)

Spin budgets adapt to the gaps each waiting party observes (`MaxSpinTime`), the async waiter thread spins, and awaited continuations run
inline on the reader's thread (`AllowSynchronousContinuations`, default on). `bench --latency`, same session, p50 latency and reader CPU:

| reader | gap 10 µs | gap 50 µs | gap 200 µs | gap 1 ms | gap 5 ms |
|---|---:|---:|---:|---:|---:|
| `WaitSync` before | 0.2 µs, 96% | 17 µs, 55% | 21 µs, 16% | 28 µs, 4% | 37 µs, 1% |
| `WaitSync` default now | 0.2 µs, 96% | 17 µs, 17% | 20 µs, 5% | 44 µs, 1% | 51 µs, 1% |
| `WaitSync`, `MaxSpinTime = 1 ms` | 0.2 µs, 100% | 0.3 µs, 95% | 0.3 µs, 97% | 0.6 µs, 98% | 35 µs, 1% |
| `await` before | 12 µs, 219% | 20 µs, 131% | 26 µs, 86% | 62 µs, 31% | 102 µs, 15% |
| `await` default now | 0.2 µs, 100% | 18 µs, 18% | 19 µs, 5% | 27 µs, 2% | 35 µs, 2% |
| `await`, `MaxSpinTime = 1 ms` | 0.3 µs, 95% | 0.3 µs, 98% | 0.4 µs, 97% | 0.9 µs, 96% | 35 µs, 2% |

## Next steps

1. `UMWAIT`/`TPAUSE` (WAITPKG) to spin at low power while staying in C0: needs Alder Lake or newer and emitted machine code (no .NET intrinsic);
   not available on this laptop (Kaby Lake R).
2. Keyed events (one kernel object instead of 33 per buffer): faster `Open`, fewer handles, same wake path.
3. Layout v2 with 128-byte reader slots (adjacent-line prefetcher), multi-reader configurations only.
4. Direct `NtSetEvent` / `NtWaitForMultipleObjects`: tried, no measurable gain on this laptop (DESIGN/SIGNALING-PROBE); revisit on a quiet desktop.
