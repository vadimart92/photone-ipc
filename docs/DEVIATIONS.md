# Deviations from DESIGN.md

Append-only log. Each entry: what the design says, what was done instead, and why.

## Stage A (scaffolding, native layer, double-mapped region)

1. **`ControlBlock.Slot(int)` instance accessor is a static pointer helper.**
   DESIGN §4 shows `ref ReaderSlot SlotRef(int i) => ref Unsafe.AsRef<ReaderSlot>(_hdr + 512 + 64 * i)` on `RingBuffer`; the partial
   `Layout.cs` had additionally put `public ref ReaderSlot Slot(int i)` on the `ControlBlock` struct itself. C# 14 rejects a struct
   member that returns `this`'s fields by reference (CS9084). Replaced by `ControlBlock.SlotRef(ControlBlock* hdr, int i)` (static,
   pointer-based, same arithmetic). `RingBuffer`/`RingReader` will use it exactly as §4 prescribes.

2. **Test child gained a Stage-A-only verb `map-region`.**
   DESIGN §9 lists the TestChild verbs `reader | echo | writer | crash-reader | claim-and-die | slow-init | hold-name | spin-reader`;
   they all need `RingBuffer<T>`, which does not exist yet. Stage A adds `map-region <name> <dataBytes> <creatorBaseHex> [--occupy]`
   (open the section, map with the creator's base as the first candidate, print the placement, verify a marker written by the parent
   across the data/mirror boundary, write one back, wait for stdin). `--occupy` pre-reserves the creator's range so the fallback path
   is exercised from another process (DESIGN test 23). The §9 verbs are added in the later stages; `map-region` stays as a region-layer test.

3. **Section creation/opening helpers live on `MirroredSection`.**
   DESIGN §3.3/§3.4 describe `CreateFileMappingW` / `OpenFileMappingW` / the header peek inline in `RingBuffer<T>.Create/Open`.
   They are implemented as static helpers `MirroredSection.CreateSection`, `OpenSection`, `MapHeaderPeek`, `UnmapPeek` and
   `NormalizeName`, so the region layer is testable without `RingBuffer<T>` and `MirroredSection` remains "the ONLY mapping code".
   Error mapping is exactly as specified (183 → `RingBufferAlreadyExistsException`, 2 → `RingBufferNotFoundException`,
   5 on `Global\` → message mentions `SeCreateGlobalPrivilege`, 5 on the peek / data view → `RingBufferLayoutException`).
   `MirroredSection.Create/Open` take ownership of the section handle and dispose it on failure; the §3.3 unwind may dispose it again
   (a second `SafeHandle.Dispose` is a no-op).

4. **Files not listed in DESIGN §1 that exist for build hygiene only.**
   `tests\Photone.Ipc.Tests\GlobalUsings.cs` (`global using Xunit;` — xunit.v3 does not inject it), `AssemblyInfo.cs` in both test
   projects (`[assembly: SupportedOSPlatform("windows")]`, otherwise CA1416 fails the build under `TreatWarningsAsErrors`),
   `tests\Photone.Ipc.Tests\TestKernel.cs` (test-only `VirtualQuery` / `VirtualAlloc` / `VirtualFree` imports — DESIGN §3.1 explicitly keeps
   `VirtualQuery` out of the library), `tests\Photone.Ipc.Tests\ChildProcess.cs` (the §9 child helper, plus the `"ipc"` collection definition).
   `Internal\Kernel.cs` carries a few non-import helpers next to the 18 imports (`AllocationGranularity`, `PageSize`, `EnsurePlatform`,
   `LastError`, `Fail`); no additional imports were added.

5. **`dotnet test` invocation.** With `global.json` selecting the Microsoft.Testing.Platform runner, this SDK rejects positional paths:
   use `dotnet test --project <csproj>` or `dotnet test --solution <slnx>` (both verified: 181/181 green).

## Stage B (ring buffer, readers, signaling layer)

6. **`SignalBackend.Create` takes an extra `bool globalNamespace`; the named-event backend keeps that flag in the backend area.**
   DESIGN §6.2 says `Global\` sections get `Global\` events, but the header carries no name (`NameChars` was removed in §0.1/14) and an
   opener by handle cannot know the section's namespace. The creator passes the flag; `NamedEventBackend` stores it in byte 0 of the
   backend-private area (2560), which openers read. No reserved word outside the backend area is touched.

7. **`ScanMin` returns `_w` (the published write cursor) instead of `_e` when no slot is Active.**
   At every call site `_e == _w` (no bucket is outstanding while scanning), so the value is identical, but `_w` is the safe choice:
   a joiner adopts `W`, never `E`, so returning `E` with an outstanding bucket would break invariant I2 (`_min <= R_new`).

8. **Laggard set = every Active slot with `ReadCursor < target` (superset of `ReadCursor == _min`), rescanned inside `RefreshLaggards`.**
   Every blocker's process handle is in the writer's wait set from the first iteration, so a reader that becomes the sole blocker later
   (judge 2's scenario, DESIGN test 27) is covered without waiting for a slice. Bounded by 32 handles either way.

9. **Cached laggard process handles are never closed inside `Evict`.** `Evict` can run on another thread of the writer process
   (`CreateReader` -> `SweepDeadSlots`) while the writer thread is inside `WaitForMultipleObjects` on that handle. Stale entries are
   closed by the next `RefreshLaggards` (before every kernel wait) and by `ReleaseNative`. Consequence: a dead reader's process handle
   may stay open in the writer until it blocks again or disposes.

10. **`EndWrite` ignores/rejects a bucket that outlives `RingBuffer.Dispose`.** `CloseWriter` drops the outstanding bucket (`Commit(0)`),
    but the `Bucket<T>` value on the user's stack still thinks it is open. `Bucket.Dispose()` afterwards is a no-op; `Commit(k > 0)`
    throws `InvalidOperationException`. Checked before any header access (the mapping may already be unmapped).

11. **`SpinUntil` performs cold checks while spinning** (every 4096 iterations: writer closed / slot evicted / disposing; every 65536
    iterations one `WaitForSingleObject(writerProcess, 0)`), so a reader configured with `SpinTime = Timeout.InfiniteTimeSpan` still
    terminates on writer close, writer death, eviction and `Dispose`. Finite budgets are unaffected (the checks are off the hot loop).

12. **`RingReader.Dispose` loops `while (_waitOutstanding != 0) { WakeReader; _waitExited.Wait(1 ms) }`** instead of a single
    wake + wait, closing the window between a waiter's `Exchange(_waitOutstanding, 1)` and its `_waitExited.Reset()`.

13. **`Status` also consults `WriterState`** (one extra header load) so a reader that never waited still reports `WriterClosed`;
    `IsCompleted` builds on it. `Counters` gained `WaiterAllocatedBytes` (bytes allocated on the async waiter thread; diagnostic for the
    zero-allocation tests).

14. **Writer `WaitForSpace` phase 2 re-checks free space right after `RefreshLaggards`** (it rescans and may have evicted) before
    publishing `WriterWaiting`; otherwise as §5.3. The Failed branch throws `ObjectDisposedException` when the buffer was disposed
    from another thread (its events were closed under the wait), otherwise `PhotoneIpcException`.

15. **Test child verbs beyond DESIGN §9:** `step-reader <name>` (stdin-driven `advance N` / `die` / `exit`), `join-storm <name> <n>`,
    `reader --stdin-handle <count>` (opens from a duplicated handle received on stdin), `writer ... [--crash] [--crash-after k] [--capacity c]`.

16. **Test plan adjustments (§9):** test 27 is `CrossProcess_DeadReaderAhead_LiveLaggardAdvances_WriterResumes` (with deviation 8 the
    "becomes laggard later" case is the same code path); test 36 (`CrossProcess_Fuzz_4Readers_30s`) is not included (runtime);
    test 18 (`Fuzz_InProcess`, 10 s) is the 10 M-element `Stress_1Writer_3Readers_10M`; test 20's async part measures allocations
    exactly per thread (caller arm path, waiter thread, writer thread — all 0 B) because the continuation hops through the thread pool
    whose dispatch allocates outside the library. Analyzer rules xUnit1031/xUnit1051 are disabled for the test project
    (`tests\Photone.Ipc.Tests\.editorconfig`): the tests block with timeouts on tasks they own.

17. **Benchmarks (§10) are not part of this stage**; `bench\Photone.Ipc.Benchmarks\Program.cs` remains the Stage A shell.

## Stage C (benchmarks, samples, README)

18. **Benchmark project name and location.** The Stage C brief says `bench/Photone.Ipc.Bench`; DESIGN §1 (and the library's
    `InternalsVisibleTo`) say `bench/Photone.Ipc.Benchmarks`. The DESIGN name is kept; the existing Stage A shell project was filled in.

19. **Harness switch `--quick` (the brief) is the primary name; DESIGN §10's `--cross` is accepted as an alias.** Both run the same
    Stopwatch harness (`QuickHarness.cs`). `--cores a,b` selects the two logical cores (default 2,4). Total run time ~2.5 s (brief: < 60 s).

20. **The cross-process peer is the benchmark executable itself (`--peer echo|drain ...`, `Peer.cs`), not the TestChild `echo` verb** as
    DESIGN §10 suggests. The brief asks to "spawn the same exe as the peer"; it also lets the peer pin its core and use the harness's wake
    modes, which the test child cannot. The `ProjectReference` to TestChild in the bench csproj is kept (DESIGN §1.1) but unused.

21. **§10 benchmark plan implemented partially.** Implemented: in-process write+read throughput for 256 / 4096 / 65536 floats
    (`HotPathBenchmarks`: `WriteRead_Protocol`, `WriteRead_FillAndSum`, `Write_SpinningReaderThread`, all with `MemoryDiagnoser`),
    ping-pong RTT in-process and cross-process for spin / default / block wake modes, throughput at 4 KiB and 64 KiB buckets in-process and
    cross-process (2 GiB through a 64 MiB ring, writer fills, reader validates chunk edges). Not implemented in this stage: the separate
    `Wait_*` micro-benchmarks, `ScanMin` benchmarks, `DisassemblyDiagnoser`, 1 MiB buckets and 2/4 reader processes, fault-injection timing,
    join storm under load, HdrHistogram (plain sorted-array percentiles are used). The DESIGN file names `WaitBenchmarks.cs`,
    `CrossProcessHarness.cs`, `Histogram.cs` are realised as `QuickHarness.cs`, `Peer.cs`, `Stats.cs`, `Affinity.cs`.

22. **"Event-wake vs spin-only" is realised through `SpinTime`, not a backend switch.** v1 has exactly one signal backend (§6.2), so there is no
    `--backend` option yet (DESIGN §6.3/4 defers it to the second backend). The harness compares `SpinTime = ∞` (never touches the kernel),
    the 20 µs default, and `SpinTime = 0` (every wait is a kernel wait): that isolates the named-event wake cost exactly.

23. **Thread pinning uses `SetThreadAffinityMask` (a bench-local `LibraryImport`), not `Process.ProcessorAffinity`** (DESIGN §10): per-thread
    pinning lets the in-process ping-pong pin its two threads to two different cores inside one process. `ThreadPriority.Highest` is used for
    the ping-pong threads only, as specified.

24. **Samples (`samples/Photone.Ipc.Samples.Writer`, `.Reader`) are not in DESIGN §1**; they come from the Stage C brief and were added to the
    solution under `/samples/`. Both are top-level-statement console apps with `[assembly: SupportedOSPlatform("windows")]` (CA1416 under
    `TreatWarningsAsErrors`). The writer emits the sketch's `GetBucket(1024) / Span.Fill(0) / Commit(1000)` verbatim as a silent prologue and
    then streams a paced 440 Hz sine (`--rate`, default 48 000 samples/s; 0 = unpaced); the reader runs the sketch's
    `await Wait(100) / TryRead(100) / Advance(90)` verbatim on its first chunk, then consumes 1024-sample blocks and prints rate / rms / peak / lag.

25. **README numbers are from this dev box** (i7-8550U laptop, Balanced power plan) and are labelled as such; the `max` column of the ping-pong
    rows is dominated by scheduler preemptions, so the README points at p99.9 as the meaningful tail.

## Post-review fixes (see docs/REVIEW-NOTES.md for the findings)

26. **Blocked writer vs `Dispose` from another thread.** DESIGN §5.3 assumed the blocked loop could keep touching the header; `SlowGetBucket`
    now holds a local reference (`TryAddLocalRef`, refused once the native resources are gone) for its whole duration, and `CloseWriter`
    wakes the space event so the blocked `GetBucket` returns with `ObjectDisposedException` immediately. Disposing while a *bucket* is
    outstanding on another thread remains unsupported (the user holds a span into the mapping).

27. **`ReserveEnd` is no longer stored on the hot path** (DESIGN §5.1/§5.2 stored it twice per bucket). The field stays in the layout at
    offset 256 and reads 0; `WriterState` on the same line is therefore never invalidated by the writer's fast path.

28. **No stuck-claim reclaim of live processes** (DESIGN §5.7 reclaimed a `Claimed` slot after 10 s). A claim is evicted only when its process
    is provably dead. `ClaimTick` is still written (diagnostics). Identity fields (`ProcessStartTime`, `ClaimTick`) are zeroed before a slot
    goes back to `Free`, both in `Dispose` and in `Evict`, so a sweeper can never read a previous owner's identity behind a fresh claim.

29. **`Status` probes writer liveness** at most once per `LivenessCheckInterval` (DESIGN §2 called it "cached + one volatile load"): a reader
    that never blocks still reports `WriterTerminated`.

30. **Finalizers** on `RingReader<T>` and `RingBuffer<T>` (DESIGN §1 listed none). They only run for abandoned objects and follow the local
    ref-count protocol, so the order in which the GC finalizes a buffer and its readers does not matter. The async waiter thread retires
    after 5 s idle (`WaiterIdleExitMs`) so that an abandoned reader is collectable at all.

31. **`_waitExited` (ManualResetEventSlim) removed** (DESIGN §5.5/§5.9/§7): `Dispose` wakes the waiter and polls `_waitOutstanding` with 1 ms
    sleeps. `Dispose` publishes `_disposing` with `Interlocked.Exchange`, and `BeginWait` re-checks it after publishing `_waitOutstanding`
    (a proper Dekker pair), so a wait can never be armed on a reader that is being disposed.

32. **Pre-faulting covers both views and openers** (DESIGN §3.3 step 4 touched only the creator's data view), and `CreatorStartTime`/
    `CreatorPid` are published before pre-faulting.

33. **`ProcessLiveness` on `ERROR_ACCESS_DENIED`** additionally compares the creation time obtained through a
    `PROCESS_QUERY_LIMITED_INFORMATION`-only handle or a `SystemProcessInformation` snapshot (`NtQuerySystemInformation`, the 19th import)
    before treating the PID as alive.

34. **Openers refuse a session-split** (DESIGN §6.2 tolerated `CreateEventW` creating the events): if an opener had to create any event while
    the writer is alive, `Open` throws `RingBufferInitializationException` (the section was shared into a different logon session;
    use a `Global\` name).

35. **Benchmark methodology** (DESIGN §10): spin/default ping-pong rows time batches of 100 rounds; warm-up is time-based (>= 500 ms);
    the drain thread exists only for `Write_SpinningReaderThread`; throughput rows read every element on the consumer side (fill+sum) and
    use bucket sizes that do not divide the ring (1000 / 16000 floats) so buckets wrap through the mirror; an `async` wake mode measures
    `await reader.Wait(1)`; `WriteRead_FillAndSum_OddBucket` measures wrapping buckets in BenchmarkDotNet.

## Phase 2, step 1: adaptive waiting (see DESIGN §14)

36. **Spin budgets adapt** (DESIGN §2/§5.3/§5.5 had fixed `SpinTime` budgets). `SpinPolicy` learns from observed wait durations; new options
    `ReaderOptions.MaxSpinTime` and `RingBufferOptions.MaxSpinTime` (default `null` = `SpinTime`). `SpinTime = 0` still never spins and a
    negative `SpinTime` still spins forever.

37. **The async waiter thread spins** (DESIGN §7: it blocked right away, after a 5 µs spin on the caller): for data (`_spin`) and, between requests,
    for the next request (`_idleSpin`). The caller-side spin is now at most the adaptive budget. The `_arm` event is only set when the waiter
    parked (Dekker pair `_request` / `_waiterParked`); `Counters.ArmSignals` counts those sets.

38. **`ReaderOptions.AllowSynchronousContinuations`, default true** (DESIGN §7: continuations always on the thread pool). With it, a `Wait` called on
    the waiter thread runs synchronously and returns a completed task. Thread-pool continuations measured 12-100 µs and up to two cores; with a
    large spin budget they also starved the pool (p99 in the tens of milliseconds).

39. **`Dispose` on the waiter thread** completes an armed-but-untaken request with `ObjectDisposedException` and does not join itself.

40. **Test isolation:** `MappingTests` (process-wide virtual size / handle counts) and `AdaptiveWaitTests` (timing, thread-pool injection)
    joined the non-parallel `ipc` collection; the async zero-allocation test measures the writer over the steady-state window only (the first
    `SetEvent` of a process allocates 48 bytes once, in the runtime's lazy P/Invoke binding).

41. **Benchmarks:** `--latency` (paced delivery latency and reader CPU from cycle counts); `TieredCompilationQuickJitForLoops=false` in the
    benchmark project, so measurement loops never hit an on-stack-replacement compile inside a measured window.

## Dispose racing the reservation after a wait (found while reviewing the stream-tags work)

42. **The reservation that follows `GetBucket`'s wait is paired with `CloseWriter` (Dekker) instead of being left to the local reference.**
    DESIGN §5.1/§5.3 (and deviation 26) had `SlowGetBucket` release its local reference in its `finally`, before `Reserve`. A `Dispose` on another
    thread that landed in between found no outstanding bucket (`_closedWithBucket` stayed false) and released the mapping, or returned it to
    `RingBufferOptions.Pool`, where the next `Create` could reuse it, while the writer thread went on to return a bucket over unmapped memory (access
    violation) or over the other buffer's shared memory (silent corruption, possibly visible to other processes). Now `SlowGetBucket` ends by storing
    `_outstanding`, passing `Interlocked.MemoryBarrier()` and loading `_closed`, the reverse of `CloseWriter`'s fenced `_closed` store and
    `_outstanding` load: `CloseWriter` finds the reservation (drops it, `_closedWithBucket`: released, not pooled), or the call throws
    `ObjectDisposedException` without handing out a span. The reference is still dropped before the reservation (which touches no shared memory), and
    `CloseWriter` does not wait for the call. Buffer-scoped test hooks `TestHooks.AfterSpaceWait` / `AfterReservationPublished`; tests
    `Create_DisposeBetweenTheSpaceWaitAndTheReservation_GetBucketThrows_TheNextBufferReusesTheSection` (before the fix: the bucket lies in the next
    buffer's data view) and `Create_DisposeAfterTheReservationWasPublished_ReleasesTheSectionInsteadOfPoolingIt_GetBucketThrows`.
    The fast path (`GetBucket` without a wait, `TryGetBucket`) is unchanged, and so is its race with a `Dispose` of a writer that is not blocked
    (DESIGN §5.3). BenchmarkDotNet `WriteRead_Protocol`, one series in the order base, a, b, c, c, b, a, base (two runs each, 256 / 4096 / 65536
    floats): unchanged code 18.2 / 18.3 / 20.4 ns; (a) (b) plus `SlowGetBucket` returning the bucket 20.0 / 20.2 / 21.9 ns; (b) the fast path
    storing `_outstanding` before loading `_closed` (without a fence) 18.9 / 19.1 / 21.6 ns; (c) this change 18.5 / 18.6 / 20.7 ns. With (c), the
    Tier1 code of `GetBucket` is identical to the unchanged code's (`DOTNET_JitDisasm`). A second series (base, c, c, base, base, c; three runs
    each) measured 18.6 / 18.5 / 20.2 ns for the unchanged code and 19.0 / 19.0 / 20.7 ns for (c), whose last run gave 18.2 / 18.7 / 20.3 ns: with
    the same machine code, the difference can only come from code placement (the larger `SlowGetBucket` moves later JIT allocations).

## Stream tags (see DESIGN §16)

43. **Layout version 4; a section with cross-process tags is reserved, not committed** (DESIGN §3.3: one `PAGE_READWRITE | SEC_COMMIT` section of
    64 KiB + D). With `TagMode.CrossProcess` the section continues after the data with a tag reserve of about 34 GiB and is created `SEC_RESERVE`; the
    creator commits the control view and the data right after mapping them, and the writer commits tag memory as it needs it. `DataOffset` stays 64 KiB and
    the three views are unchanged; tag views are mapped separately, on demand. Buffers without tags, or with in-process tags, keep the committed
    64 KiB + D section. (Version 3 was the first revision of the tags, never released: a fixed tag area between the control view and the data.)

44. **An opener's header peek commits its view** (DESIGN §3.4 maps the peek and reads `InitState`). A reserved section's control page may still be
    uncommitted when an opener finds the name, and a load would fault; `MapHeaderPeek` calls `VirtualAlloc(MEM_COMMIT)` on the 64 KiB view first
    (idempotent, a no-op on committed sections, the creator's stores are kept).

45. **Line 2 holds `TagEnd` next to `WriteCursor`** (DESIGN §4.1: nothing else on the write cursor's line). Stored by the writer immediately before the write
    cursor, only on commits that carry tags, and loaded by readers immediately after it; `LayoutTests` allows exactly this field.

46. **The requested tag API, adjusted** (DESIGN §16.1): `ITag.IsPersistent` is `static virtual` with a default of `false` (a `static abstract` member would make
    `ReadOnlyMemory<ITag>` illegal, CS8920); `Offset` has a setter and is set by `AddTag` (`bucket.AddTag(tag, index)`, `buffer.AddTag(tag)`);
    `ReadLastTagValues` returns `ReadOnlySpan<ITag>` (no allocation, the key is in each tag); a chunk's tags are the tags of its own elements
    (`StartOffset <= Offset < StartOffset + Length`, not every tag at or after the start); `StartOffset` is added to `Chunk` and `Bucket`; tags are enabled
    by `RingBufferOptions.Tags` (`TagMode`), and readers of the writer's own buffer receive the tag instances instead of copies.

47. **Join step (f): the start cursor is published with a full fence and a waiting writer is woken** (DESIGN §5.6 ended with the plain store of (d), and
    the earlier review refuted the need for a wake). A provisional cursor that a later commit left behind, or a slow tag join, can make a joiner the reader
    a blocked writer waits for; see REVIEW-NOTES T4. (Kept after tags stopped waiting for tag space: the space wait alone can need it.)

48. **Dispose waits for a commit with tags** (DESIGN §5.8 dropped an outstanding bucket right away). `CloseWriter` loads `_committing` after the fenced
    `_disposed` store and waits while a commit with tags runs, so the two never run `EndWrite` at once (REVIEW-NOTES T1). Such a commit maps and commits
    tag memory, and waits for readers when the buffer limits its tags (`MaxUnreadTags` / `MaxUnreadTagBytes`); `CloseWriter` wakes it, as it wakes a
    blocked `GetBucket`.

49. **A claim that fails after its slot became Active releases the slot like a disposed reader** (DESIGN §5.6 only freed the word and the mask). A
    malformed tag snapshot makes `CreateReader` throw after the claim; `ReleaseFailedClaim` zeroes the identity fields before freeing the slot (a sweeper
    must never read them behind the next claim), bumps the generation and wakes a writer blocked on the slot (REVIEW-NOTES R8).

50. **The writer also evicts dead readers before its shared tag memory grows** (DESIGN §5.7: eviction happens when the writer blocks for space, and in
    a claim that finds no free slot). A dead reader's cursor keeps every later tag record reserved, and dense tags can make the log grow long before the
    data ring fills; a sweep before moving to a larger ring, at most once per liveness interval, evicts it (REVIEW-NOTES R1).

## Stage E (generated Win32 bindings)

51. **The Win32 declarations are generated by CsWin32, not written by hand** (DESIGN §3.1 lists the imports as source). `Microsoft.Windows.CsWin32`
    (`NativeMethods.txt` + `NativeMethods.json` per project, `allowMarshaling: false`, `useSafeHandles: false`, `public: false`) generates the imports,
    the structs and the flag enums from the official Win32 metadata; `Internal\Kernel.cs` keeps its shape and becomes the thin typed layer over them
    (raw handles on the signal path, `uint` wait results, the flag names the design uses, `LastError` / `Fail`). Measured A/B against the hand-written
    `LibraryImport` version (`--quick`, interleaved runs): no difference beyond run-to-run noise - the generated import is the same blittable
    `DllImport` with `SetLastError`. The tests (`TestKernel`) and the benchmarks (`Affinity`, `PacedLatency`) generate their own.

52. **`VirtualAlloc2` / `MapViewOfFile3` now come from the `api-ms-win-core-memory-l1-1-6` API set, not from `kernelbase.dll`** (RESEARCH §1 verified
    the kernelbase export). That is the module the metadata names; the API set exists from Windows 10 1803, which the library requires anyway. Same
    exports, resolved through the loader's API-set map.

53. **`MEM_PRESERVE_PLACEHOLDER` is cast from `UNMAP_VIEW_OF_FILE_FLAGS`.** The metadata declares the flag only for `UnmapViewOfFileEx`, while
    `VirtualFree` takes the same bit to split or preserve a placeholder; `Kernel` casts it into `VIRTUAL_FREE_TYPE` with a note. Everything else the
    library passes is a metadata enum member, which also separates `MEM_REPLACE_PLACEHOLDER` from `MEM_DECOMMIT` (same value, different enums -
    RESEARCH note 24) at compile time.

54. **`[assembly: SupportedOSPlatform("windows")]` gained the version `10.0.17134`** in all four assemblies. The generated imports carry the
    `[SupportedOSPlatform]` of the API they wrap, so the unversioned attribute failed CA1416 under `TreatWarningsAsErrors`; the version is the one
    `Kernel.EnsurePlatform` has always enforced at run time. The test project also sets `<PlatformTarget>x64</PlatformTarget>`, because
    `MEMORY_BASIC_INFORMATION` has no AnyCPU shape - the library was x64-only already.

55. **The test and benchmark projects suppress CS0436** where they call their own generated `PInvoke`. Their generated types have the same names as the
    library's, which `InternalsVisibleTo` makes visible to them; the compiler prefers the ones from the project's own source, which is what these
    helpers want.
