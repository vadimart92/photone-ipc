# Cross-process signaling probe (measured 2026-09-16)

Purpose: ground the phase-2 work ("most efficient way to notify the other side that data arrived / space was freed") in numbers from this dev box before designing anything.

Setup: two processes, one 4 KiB shared section, ping-pong round trips (parent writes `ping = i`, child answers `pong = i`). Parent pinned to logical core 2, child to core 4, both at `THREAD_PRIORITY_HIGHEST`, 8 logical cores, Windows 11 26200, .NET 10. 1000 warm-up iterations, then 50 000 (200 000 for spin). Source: `scratchpad/sigprobe` (probe only, not part of the library).

| mode | what it does | min us | median us | p99 us | max us |
|---|---|---|---|---|---|
| spin | both sides spin on the shared cache line (`Thread.SpinWait(1)`) | 0.00 | **0.10** | 0.20 | 106 |
| event | `SetEvent` / `WaitForSingleObject` on two named auto-reset events | 0.6 | **24.4** | 64.9 | 18 784 |
| ntevent | same objects, direct `NtSetEvent` / `NtWaitForSingleObject` | 0.6 | 22.7 | 43.5 | 8 986 |
| sow | `SignalObjectAndWait` (one syscall for signal + wait) | 6.4 | 25.0 | 49.7 | 5 979 |
| event-busy | `event`, but a lowest-priority thread keeps core 4 busy (no C-state exit) | 0.6 | **8.6** | 24.3 | 8 808 |
| sow-busy | `sow` with the busy core | 4.0 | 9.8 | 27.3 | 5 566 |
| alert | `NtAlertThreadByThreadId` / `NtWaitForAlertByThreadId` across processes | - | - | - | - |

All numbers are round trips (two wake-ups). One-way wake latency is roughly half.

## Findings

1. **`NtAlertThreadByThreadId` is process-local.** Calling it with a thread id from another process returns `STATUS_ACCESS_DENIED` (0xC0000022) in both directions. It cannot be a cross-process backend. (It stays a candidate for the in-process case only, where `WaitOnAddress` already exists.)
2. **The kernel wake itself is not the main cost; the idle core is.** With the target core kept busy the Event round trip drops from ~24 us to ~9 us. The missing ~15 us is the CPU leaving its idle state (plus the scheduler IPI). No user-mode primitive avoids it; only keeping the waiting core awake does.
3. **Direct `Nt*` syscalls save about 1 us per round trip** versus the kernel32 wrappers. Not worth a second backend on their own; worth taking for free inside the existing one.
4. **`SignalObjectAndWait` does not help** for this pattern (one fewer transition, but the same wake path). It has a noticeably worse minimum.
5. **Spinning on the mirrored buffer is 100+ times faster** than any kernel wake (0.1 us round trip). The ring buffer's protocol therefore must (a) spin first with a bounded budget, (b) publish the "I am blocking" flag only after the budget expires, so the writer pays a `SetEvent` only when a reader is really asleep, (c) treat every kernel wake as "re-check the condition" (spurious wakes are fine).

## Consequences for phase 2 (signal backends)

- Keep `NamedEventBackend` as the portable baseline; switch its calls to `NtSetEvent` / `NtWaitForMultipleObjects` only if the 1 us matters after everything else is done.
- The lever that actually moves latency is *where the waiter waits*: a dedicated waiter thread that spins (`pause`) for a configurable budget before sleeping turns the 12 us wake into 0.05 us for any traffic gap shorter than the budget. Cost: one busy core per waiting reader while spinning. Expose the budget per reader (`ReaderOptions.SpinTime` already exists).
- Candidates still worth measuring: (a) keyed events (`NtCreateKeyedEvent` / `NtReleaseKeyedEvent` / `NtWaitForKeyedEvent`) - one kernel object for all 32 slots instead of 33 events, same wake path; (b) `UMWAIT` / `TPAUSE` (WAITPKG, Alder Lake+) via a tiny native helper to spin at low power while staying in C0; (c) a "burst-aware" adaptive spin budget (spin longer when the last N wakes came within the budget).
- Not candidates: `NtAlertThreadByThreadId` (process-local), `WaitOnAddress` (process-local), ALPC / pipes (slower than events).

## Status after phase 1 (2026-09-16, evening)

What the ring buffer already does with these findings:

- Spin first, kernel later: readers poll `WriteCursor` for `SpinTime` (20 us default, `Timeout.InfiniteTimeSpan` = never block) and only then
  publish their slot bit and wait on their event; the writer spins on free space the same way before waiting on the space event.
- No syscall when nobody is blocked: `Commit` reads `WaitersMask` (a line that stays shared in the writer's cache) and calls `SetEvent`
  only for a reader whose `WaitFor` is now satisfied; `Advance` calls `SetEvent` only when the writer has published `WriterWaiting` and this
  advance is the one that crosses the writer's target. The `CrossProcess_NoSyscallWhenNobodyWaits` test asserts 0 signals and 0 kernel
  waits on both sides for 1 M commits with a spinning reader.
- Measured with the library (`--quick`, `--compare`): 0.14 us round trip spinning, 20-25 us round trip when every wait is a kernel wait,
  23-32 ns of protocol per bucket cross-process, 9-13 GB/s streaming with a light consumer (memory-bandwidth bound).

Tried and not adopted:

- Direct `NtSetEvent` / `NtWaitForMultipleObjects` instead of the kernel32 wrappers inside `NamedEventBackend`. All 275 tests pass with it,
  but the blocking round trip stayed at 20-26 us in four runs before and four after: the expected ~0.5 us per wake is far below this
  laptop's run-to-run noise (+-25 %). Not worth depending on ntdll exports for an unmeasurable gain; revisit on a quiet desktop with the
  same harness if the kernel path ever becomes the focus.

Done in phase 2, step 1 (DESIGN §14, numbers in README "Waiting: latency versus CPU"):

1. **Adaptive spin budget.** `SpinPolicy` decides from the recent share of waits within `MaxSpinTime` whether to spin, and spins for twice the
   recent short gaps. Default: same latency as before, CPU at 50 µs gaps 55% -> 17%. With `MaxSpinTime = 1 ms`: 0.3-0.9 µs delivery through
   50 µs-1 ms gaps, back to blocking beyond that. (A haltpoll-style grow/halve rule oscillated under wake-up jitter and was replaced.)
2. **The async waiter spins, and continuations run inline on it.** `await reader.Wait` went from 12-100 µs at up to two cores to the `WaitSync`
   numbers; re-arming costs no system call (`ArmSignals` stays 0 in an await loop).

Still open, ranked by expected effect:

1. `UMWAIT`/`TPAUSE` (WAITPKG) for low-power spinning: not available on this CPU (Kaby Lake R); needs Alder Lake or newer and dynamically
   emitted machine code (no .NET intrinsic).
2. Keyed events (one kernel object instead of 33 per buffer): faster `Open` (saves ~0.3 ms) and fewer handles, but the same wake path;
   only worth it if many buffers are opened per process.
3. Layout v2 with 128-byte reader slots (adjacent-line prefetcher): multi-reader configurations only.

Not candidates (measured): `NtAlertThreadByThreadId` (process-local), `WaitOnAddress` (process-local), `SignalObjectAndWait` (no gain),
named pipes and TCP loopback as a wake mechanism (25-60 us round trips, plus copies).
