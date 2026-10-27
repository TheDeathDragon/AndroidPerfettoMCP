# Pitfalls that lead to wrong conclusions

## Thread and process ownership
- trace_processor puts threads whose tgid it does not know into a nameless "shadow" process (pid == tid) or a
  placeholder process (pid 0xFFFF0000). The tools fold them back into the owner; raw SQL by `process.name` misses
  them, so the busiest thread of an app can look like a nameless process.
- Main threads exist only in named processes; `is_main_thread` is also true for shadows.
- Several instances with the same name (restarts): an exited instance's last state is open-ended (dur = -1) and
  covers any later window. Pick the instance alive in the window.
- Native threads that never set a name carry their creator's name: "many threads with the same name" can be one
  thread creating short-lived threads, not a pool (`cpu_top_threads` `thread_churn.unnamed`).

## CPU
- CPU frequency must be time-weighted; `AVG()` over counter samples strongly underestimates (short dips create
  many samples). `cpu_frequency` is time-weighted.
- `sched` priority: 100–139 CFS (nice = prio − 120), < 100 real-time.
- Load average counts D-state; idle devices with sleeping kernel threads in D show a high loadavg.

## Frames
- FrameTimeline "Buffer Stuffing" frames are presented on time: not user-visible jank. Count visible jank as
  late present or dropped frames (`visible_jank`).
- Attribution order for a slow frame: pipeline / shader compilation, main thread D or Runnable, main thread waiting
  for RenderThread, the main thread's own work.
- Screen-on: hundreds of ms in `waitForBufferRelease` while SurfaceFlinger is in `setPowerMode(ON)` is display
  power-up, not the app.
- Apps rendering on their own GL thread have no FrameTimeline: use `frame_intervals`.

## Memory
- Use MemAvailable, not MemFree (page cache is reclaimable).
- vmstat counters are cumulative: use deltas over the window.
- The capture itself costs memory (traced, traced_perf); on small devices it can cause the pressure you measure.
- RSS 0 at the window end means the process exited (check LMK kills).

## Binder, locks, ANR
- `android_binder_txns` contains replied transactions only; a call stuck at ANR time is in `android_anrs`
  `stuck_binder`.
- A lock holder's method is where the holder was at that moment: `Unsafe.park` / `Object.wait` means it waits for
  something else while holding the lock; `BinderProxy.transactNative` means IPC under a lock.

## Waker chain vs critical path
- The waker is recorded on the first runnable row after a sleep, not on the sleep row.
- The waker chain (who woke me) is not the critical path (whose execution decided when I could continue): a driver
  thread that finished work and was handed off by another thread only shows on the critical path.
- The critical path suits short windows (a frame, a launch, the seconds before an ANR). In long, busy windows (boot)
  it dilutes; look at how long the blocking threads were Runnable instead.
- SurfaceFlinger vsync threads on a launch's path mean the launch spans several frames, not a blocker.

## Launch
- TTFD is clamped to TTID: `reportFullyDrawn()` before the first frame hides the real TTFD.
- `OpenDexFilesFromOat` with `filter=verify` means no AOT code (interpreter + JIT); `<uses-library>` jars opened by
  the app are not app code.
- installd CPU during a launch is not always dexopt (e.g. storage size queries); check its AIDL slice names.

## Heap dumps
- No primitive field values (cannot read flags like `mDestroyed`); judge leaks by reachable instance counts across
  dumps.
- Use reachable counts, not total objects (garbage not yet collected inflates totals).
- Take at least three dumps (baseline, N, 2N rounds): the first round often just fills caches.

## Trace health
- Import errors differ in impact (`health_issues.affects`): perf / symbolization errors affect only `cpu_profile`;
  a few `systrace_parse_failure` lose a few slices; ftrace data loss on a CPU and `traced_flushes_failed` (missing
  end) make scheduling numbers on those CPUs / that span unreliable.
- Main-thread Sleeping time has no good or bad direction on its own.
- The first INCLUDE of a heavy stdlib module materializes the whole table (seconds on large traces); later queries
  are fast.
