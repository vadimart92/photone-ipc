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
