# Review notes (post-implementation review, 2026-09-16)

Five read-only reviewers (memory ordering, native interop, cross-process robustness, API semantics, performance) produced 31 findings.
Each was verified by hand against the code before anything was changed. Verdicts and what was done:

## Confirmed and fixed

| # | Finding | Fix |
|---|---|---|
| 1 | `RingBuffer.Dispose()` from another thread while the writer is blocked in `GetBucket` unmapped the control block under the wait loop (access violation), and did not wake the writer. | `SlowGetBucket` holds a local ref (`TryAddLocalRef`) for its whole duration, so the mapping outlives a concurrent Dispose; `CloseWriter` now calls `WakeWriter()`; the blocked call ends with `ObjectDisposedException`. Test: `Writer_DisposedFromAnotherThread_WhileBlocked_ThrowsObjectDisposed`. |
| 2 | `WriteCursor` / `ActiveReaderCount` / `EvictedReaders` / `IsWriterClosed` / `Available` dereferenced the control block after Dispose. | They throw `ObjectDisposedException`; `IsWriterClosed` on the writer answers from local state. Test: `BufferProperties_AfterDispose_Throw`. |
| 3 | A fresh `Claimed` slot could be evicted by a sweeper in another process: `ProcessStartTime`/`ClaimTick` still held the previous owner's values for a few instructions after the claim CAS, and a start-time mismatch reads as "PID reused = dead". | Identity fields are zeroed before a slot is freed (Dispose and Evict); a `Claimed` slot with `ProcessStartTime == 0` is evicted only when the PID does not exist at all. Tests: `ReaderDispose_ZeroesSlotIdentity`, `Evict_ZeroesSlotIdentity`, `SweepDeadSlots_NeverReclaimsAClaimOfALiveProcess`. |
| 4 | Reclaiming a "stuck" claim (> 10 s) of a live process let the claimant's remaining stores (`ResetEvent`, provisional cursor) land in a slot that already had a new owner (lost wake, wrong cursor). | Live claims are never reclaimed; only claims whose process is provably dead. The 10 s rule is gone. |
| 5 | Async `Wait` racing `Dispose` could start a waiter thread that crashed on the disposed `AutoResetEvent` (unhandled exception on a background thread). | `Dispose` publishes `_disposing` with a full fence; `BeginWait` re-checks it after publishing `_waitOutstanding` (Dekker); `WaiterLoop` tolerates a disposed event. Test: `ReaderDispose_RacingWait_NeverCrashes`. |
| 6 | A polling reader (TryRead/Available/Status only) never noticed a crashed writer. | `Status` probes the writer once per `LivenessCheckInterval` (one `WaitForSingleObject(h, 0)`, or a PID check in poll mode). Test: `CrossProcess_WriterKilled_PollingReaderNoticesViaStatus`. |
| 7 | A `RingReader` dropped without Dispose stalled the writer forever; `DangerousAddRef` pins also leaked the kernel handles. | Finalizers on `RingReader` (frees the slot, wakes the writer, drops the local ref) and `RingBuffer` (publishes Closed, drops its ref); the idle waiter thread retires after 5 s so an abandoned reader becomes collectable. Test: `AbandonedReader_FinalizerFreesTheSlot`. |
| 8 | `CreatorPid`/`CreatorStartTime` were published after pre-faulting, so an opener could not detect a creator dying during a long pre-fault. | Published first. |
| 9 | Pre-faulting touched only the creator's data view; the mirror view and every opener still paid first-touch soft faults on the hot path. | Creator writes the data view and reads the mirror; openers read both views when `PreFault` is set. |
| 10 | `ERROR_ACCESS_DENIED` counted as alive without any start-time check: a dead peer whose PID was reused by an inaccessible process became immortal. | On access denied the creation time is fetched through a `PROCESS_QUERY_LIMITED_INFORMATION`-only handle, then through a `SystemProcessInformation` snapshot; a mismatch or absence means dead. |
| 11 | `Local\` event names silently split when a section handle is shared across logon sessions: readers never wake. | An opener that had to *create* any event while the writer is alive fails with `RingBufferInitializationException` (use a `Global\` name). |
| 12 | `RingReader.Dispose` cleared shared mask bits before proving slot ownership. | `ReleaseSlot` checks the slot word first and touches nothing it does not own. |
| 13 | `WaitSync`/`Wait` reloaded `WriteCursor` even when the cached value already satisfied the request. | Cached value consulted first (it only ever rises). Test: `Wait_UsesCachedWriteCursor_WhenItAlreadySatisfiesTheRequest`. |
| 14 | `Wait(count, TimeSpan.Zero)` spun for `AsyncSpinTime` before returning false; `WaitSync` returned at once. | Poll semantics aligned. Test: `AsyncWait_ZeroTimeout_DoesNotSpin`. |
| 15 | `ReserveEnd` was stored twice per bucket on the line that holds `WriterState`, which `Status`/`IsCompleted` read. | The informational store is gone from the hot path (the field stays in the layout, always 0). |
| 16 | `WaitSync`'s spin-satisfied path paid three atomics (`Exchange` + `ManualResetEventSlim.Reset/Set`). | The event is gone; Dispose polls `_waitOutstanding` after waking the waiter. |
| 17 | Benchmark: sub-microsecond ping-pong percentiles were quantised to the 100 ns QPC tick and included two QPC calls per sample. | Spin/default rows time batches of 100 rounds and report per-round percentiles of batch means. |
| 18 | Benchmark: 2000 warm-up rounds are below the JIT tiering delay. | Warm-up is time-based (>= 500 ms) plus a round minimum. |
| 19 | Benchmark: the spinning drain thread ran during the single-threaded baseline benchmarks. | Per-benchmark `GlobalSetup` targets. |
| 20 | Benchmark: throughput rows measured `Span.Fill` only; the reader never read the payload. | The drain side sums every bucket (vectorised); rows are labelled fill+sum. |
| 21 | Benchmark: `await reader.Wait` was never measured. | New `async` wake mode (in-process and cross-process). |
| 22 | Benchmark: no bucket ever wrapped into the mirror. | Throughput rows use bucket sizes that do not divide the ring (1000 / 16000 floats); BDN gained an odd-sized bucket benchmark. |

## Refuted or deliberately not changed

| Finding | Why |
|---|---|
| Join step (d) raises `ReadCursor` without waking a blocked writer. | A joiner's cursor is `W >= target` for any writer episode (`target = W + count - C < W`), so a joiner is never a blocker; no wake is needed. |
| `GetBucket(n)` returns exactly `n`, the sketch says `>= n`. | Deliberate (DESIGN §2 semantics table): exact reservations are simpler to reason about and the sketch (`GetBucket(1024)`, `Commit(1000)`) works verbatim. |
| Writer cannot restart under the same name while a reader holds the old section. | v1 has no writer takeover (DESIGN §11); the `AlreadyExists` exception message names the cause. |
| `Evict` never wakes an evictee. | v1 evicts only dead processes; there is nobody to wake. |
| 64-byte reader slots pair up under the 128-byte adjacent-line prefetcher. | Real but second-order (only multi-reader configurations, only cursor lines). Kept for v1; a 128-byte slot layout is a candidate for layout version 2. |
| `Status` reads the writer-state line that the writer used to store `ReserveEnd` on. | Resolved by #15 above (no more hot-path stores on that line). |
