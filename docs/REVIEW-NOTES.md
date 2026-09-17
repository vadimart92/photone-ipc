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

# Review notes: stream tags (2026-09-17)

One read-only adversarial reviewer checked DESIGN §16 against the code (reuse rule, joiner snapshot, reader loading, exception safety, pool, layout,
hot paths). It confirmed the reuse rule and the snapshot argument on TSO and found the issues below; each was verified by hand, and all are fixed.

| # | Finding | Fix |
|---|---|---|
| T1 | Major. `Dispose` on another thread just as a commit with tags stopped waiting for tag space: the commit had released its local reference, so both threads ran `EndWrite` (the tags could be lost, the write cursor go back, the snapshot be torn, the mapping be released under the copy). | `EndWriteWithTags` and `CloseWriter` form a Dekker pair (`_committing` / `_disposed`): the commit either sees the disposal and touches nothing, or `CloseWriter` waits (waking the writer every millisecond) until the commit completed or gave up. Test: `Dispose_JustAsACommitWithTagsStopsWaiting_LetsTheCommitComplete` (fails with the handshake disabled). |
| T2 | Major. A corrupt `TagEnd` together with a huge record length made `TagReader.Load` copy far beyond the 4 KiB log (access violation instead of `RingBufferLayoutException`); the constructor also re-read the tag sizes from shared memory after `Validate`. | `TagEnd - position > L` and records longer than the log are reported; the wrap-aware copies refuse lengths above the log; openers take the sizes once (`ReadTagArea`) and only if `64 KiB + S + L` equals the mapped header view. Test: `CorruptTagEnd_OrRecordLength_IsReportedInsteadOfFollowed`. |
| T3 | With `SpinTime` negative (spin forever) the writer never reached the kernel phase, so the tag deadlock was never detected (endless spin). | `SpinForMin` runs the deadlock check every millisecond during a tag wait. Test: `TagLog_Deadlock_IsDetectedAlsoWhenTheWriterSpinsForever`. |
| T4 | A join that kept losing to tagged commits held the writer back through its cursor; its bump to the snapshot's cursor did not wake the writer (a liveness slice of stall). | The joiner publishes its start cursor with a full fence and wakes a writer whose target it reached, as `Advance` does. This also re-opens the earlier refuted finding "join step (d) does not wake a blocked writer": a writer's scan can see the provisional cursor of step (a) below a target (a tag wait targets up to `W`, and a space target can exceed a stale provisional cursor), so every join now does this check, with or without tags. |
| T5 | A joiner whose writer died inside `PublishSnapshot` started at its own join cursor, after the tags of elements it could still read. | It starts at the final write cursor, loaded after `TagEnd`. Test: `JoiningReader_WhenTheWriterDiedMidSnapshot_StartsAtTheFinalCursorWithoutState`. |
| T6 | `RingBufferPool.MaxIdleBytes` / `IdleBytes` ignored the tag area (up to ~1 GiB per idle entry). | They count the data region and the tag area. Test: `Pool_CountsTheTagAreaAgainstMaxIdleBytes`. |
| T7 | Suspected: the seqlock's odd version was a plain store, and a large table copy may use non-temporal stores that are not ordered with it. | The odd store is an `Interlocked.Exchange` (full fence). |

Found while fixing: `TagReader.Consume` cleared consumed queue entries, so a chunk the reader had advanced into only partly showed `null` for the tags it
was advanced past; entries are no longer cleared (test `PartiallyAdvancedChunk_KeepsTheTagsItStillCovers`). Benchmarks showed 5 ns per round for a tag area
that carries no tags (two out-of-line calls per new write cursor), and about 1 ns per round for buffers without tags against the base commit (four
extra compares and a 16-byte tag view in every chunk): `TryRead` and `Advance` now test one threshold, and a chunk carries a single reference from
which `Chunk.Tags` is computed on access.

Deliberately not changed: the deadlock detector treats a reader parked with a timeout or a cancellation longer than 1 s as stuck (documented). Out of scope
and reported separately: the same class of race as T1 exists, much narrower, between a blocked `GetBucket` that stops waiting and `Dispose` (pre-existing).
