# photone-ipc status (2026-09-16)

Phase 1 (double-mapped cross-process ring buffer) is implemented, reviewed, fixed and verified on the dev box
(Windows 11 26200, x64, .NET SDK 10.0.112). Phase 2 (fastest cross-process signaling) has its baseline measurements in
`docs/SIGNALING-PROBE.md` and a swappable backend seam in the code.

## What exists

```
Photone.Ipc.slnx, global.json, Directory.Build.props, Directory.Packages.props, .editorconfig, .gitignore, README.md
docs/
  DESIGN.md              final design (12 sections + post-review section 13); the pseudo-code matches the code
  RESEARCH.md            the four research reports (Win32 mapping, sync protocol, .NET specifics, vmcircbuffer)
  DEVIATIONS.md          35 numbered deviations from DESIGN.md with reasons (stage A/B/C + post-review)
  REVIEW-NOTES.md        31 review findings: 22 fixed, 9 refuted/deferred, each with the reason
  SIGNALING-PROBE.md     measured cross-process wake costs (spin / Event / NtEvent / SignalObjectAndWait / NtAlert)
  STATUS.md              this file
src/Photone.Ipc/         the library (zero package dependencies, AOT-compatible, InternalsVisibleTo tests/bench)
  RingBuffer.cs          Create / Open(name) / Open(handle) / DuplicateSectionHandleTo / properties / Dispose / finalizer
  RingBuffer.Writer.cs   GetBucket / TryGetBucket / EndWrite / SignalReaders / ScanMin / WaitForSpace / laggards / CloseWriter
  RingBuffer.Readers.cs  CreateReader (slot claim protocol) / SweepDeadSlots / Evict
  RingReader.cs          TryRead / Advance / WaitSync / SpinUntil / BlockUntil / Status (liveness probe) / Dispose / finalizer
  RingReader.Async.cs    Wait(...) => ValueTask<bool> (IValueTaskSource, per-reader waiter thread with idle retirement)
  Bucket.cs, Chunk.cs    ref structs (Span / Commit / Dispose; ReadOnlySpan)
  Options.cs, ReaderStatus.cs, Exceptions.cs, SafeSectionHandle.cs
  Signaling/SignalBackend.cs, NamedEventBackend.cs, WaitOutcome.cs   the swappable signaling layer (v1: 33 named auto-reset events)
  Internal/Kernel.cs     19 [LibraryImport]s (kernelbase: VirtualAlloc2, MapViewOfFile3; kernel32; ntdll: NtQuerySystemInformation)
  Internal/MirroredSection.cs   placeholder + 3 views (header, data, mirror); create / open at the creator's address / teardown
  Internal/Layout.cs     4096-byte ControlBlock, 32 x 64-byte ReaderSlot, static layout asserts
  Internal/Capacity.cs, AddressHint.cs, ProcessLiveness.cs, SpinClock.cs, Counters.cs, TestHooks.cs
tests/Photone.Ipc.Tests/       275 xunit.v3 tests (unit, stress, and 20 cross-process tests via TestChild)
tests/Photone.Ipc.TestChild/   child-process verbs: reader, spin-reader, step-reader, writer [--crash], echo, crash-reader,
                               claim-and-die, slow-init, hold-name, join-storm, map-region
bench/Photone.Ipc.Benchmarks/  BenchmarkDotNet hot path + `--quick` Stopwatch harness (in-process and cross-process, `--peer` mode)
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
`RingBuffer.Open(SafeSectionHandle)`, `DuplicateSectionHandleTo(pid)`, `RingBufferOptions { SpinTime, LivenessCheckInterval,
InitializationTimeout, PreferredBaseAddress, PreFault }`, `ReaderOptions { SpinTime, AsyncSpinTime }`.

## Verification

- `dotnet build Photone.Ipc.slnx -c Release`: 0 warnings, 0 errors (`TreatWarningsAsErrors`, `AnalysisLevel=latest`).
- `dotnet test --project tests/Photone.Ipc.Tests/Photone.Ipc.Tests.csproj -c Release`: 275/275, three consecutive runs, ~15 s each.
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

## Known limitations (v1)

- Windows 10 1803+ / x64 only; one writer per buffer (the creator); no writer takeover; 32 readers.
- A live reader that never advances stalls the writer forever (the requested backpressure contract); only dead processes are evicted.
- `Local\` namespace by default: sharing a section handle into another logon session is detected and refused (use a `Global\` name).
- Same virtual address in every process is best effort.
- `Dispose` of the writer while a bucket is outstanding on another thread is unsupported; `Dispose` while blocked in `GetBucket` is supported.
- 64-byte reader slots pair up under the adjacent-line prefetcher (multi-reader configurations only); a 128-byte slot layout is a v2 candidate.

## Next steps (phase 2: signaling)

1. `docs/SIGNALING-PROBE.md` shows the lever is *where the waiter waits*, not which kernel primitive wakes it: an idle core costs ~12 us
   per wake (C-state exit), a busy core ~4 us, spinning 0.07 us. `NtAlertThreadByThreadId` is process-local (access denied cross-process).
2. Candidates to implement behind `SignalBackend` and benchmark cross-process: keyed events (one kernel object for all slots),
   direct `NtSetEvent`/`NtWaitForMultipleObjects` (about 1 us per round trip), a dedicated spinning waiter thread with an adaptive budget,
   `UMWAIT`/`TPAUSE` (WAITPKG) via a tiny native helper to spin at low power.
3. Expose a backend selector on `RingBufferOptions` once a second backend exists (the header already carries `SignalBackendId`).
