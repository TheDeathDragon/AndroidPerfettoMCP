---
name: perfetto-analysis
description: Playbooks for analyzing Android performance with the perfetto MCP server (Perfetto traces, .pftrace / .perfetto-trace, bootchart). Use when investigating slow app launch, jank / stutter / freezes / low fps, ANR, high CPU or heat, memory pressure or Java leaks, IO stalls, slow boot, standby power, operation latency, or a base-vs-fix comparison, and when capturing a trace from an adb device. Also for 启动慢, 卡顿, 掉帧, 冻屏, ANR, 耗电, 发热, 内存泄漏, 开机慢, 抓 trace, 对比优化效果.
---

# Perfetto analysis playbooks

Tools come from the `perfetto` MCP server. These playbooks say which tools to call in which order and how to read
them. Call tools on one trace **sequentially**: they share one trace processor, parallel calls only queue.

## Ground rules

1. **Start with `load_trace`** and read the overview before anything else: `health_issues` (only ftrace loss /
   `trace_end` make scheduling conclusions unreliable), counts (`sched_slices = 0` means CPU and thread-state tools
   will be empty), launches, top CPU, `hints`. **Pass the returned `trace_id` to every later call**: the default
   is the server's most recent load, which another agent sharing the same server can change under you.
2. **Find the window, then drill into it.** Every symptom gives a time range (a launch's `startup_id`, a freeze's
   `gap_start_ts`/`gap_end_ts`, an ANR's window, a slice's ts/dur). Pass it as `startup_id` or `ts_start`/`ts_end`
   (absolute ns) to the per-window tools. Whole-trace numbers hide short stalls.
3. **Answer "what was the thread doing" before "why":** `thread_state_breakdown` (Running / Runnable / D / Sleeping)
   decides the next tool: Running → `cpu_profile` / `slice_hotspots`; Runnable → `cpu_top_threads` +
   `cpu_frequency` (CPU contention or low frequency); D → `io_summary` (blocked_function); Sleeping →
   `wakeup_chain` (who it waited for, critical path), `android_binder_txns`, `lock_contention`.
4. **Dedicated tools before SQL.** Use `execute_sql` for what they do not cover, with
   `INCLUDE PERFETTO MODULE ...` (find modules with `list_stdlib_modules`), aggregate in SQL, and narrow by window /
   upid / utid. On a timeout the trace reloads; narrow the query instead of raising `timeout_s` first.
5. **Separate measurement from inference.** Tool `findings`, `likely_blockers` and rules are starting points with
   thresholds, not verdicts; each lists `next_evidence`. Confirm a cause with the tool it names before reporting it.
6. Read `references/pitfalls.md` before concluding on jank, CPU frequency, memory, thread ownership, critical path
   or heap growth; those are the usual ways to be confidently wrong.

## Playbooks

### Slow app launch
1. `android_startups` → pick the launch (cold / warm / hot, TTID / TTFD).
2. `android_startups startup_id=N`: `findings` (rule hits with values and thresholds), main-thread state split,
   heaviest main-thread slices, slow binder calls, `critical_path` (which threads the main thread waited on).
3. Drill per finding with `startup_id=N`: `main_thread_blocking_calls`, `android_binder_txns` (server side and its
   reason), `lock_contention`, `io_summary`, `cpu_top_threads` (competing processes), `cpu_frequency`, `android_gc`.
4. Dex / compile state: findings on OpenDexFilesFromOat / odex filter; `filter=verify` means interpreted + JIT.
   A prebuilt odex that is opened and then unloaded (falling back to the vdex) means it was rejected.
5. TTFD equal to TTID: check `REPORT_FULLY_DRAWN_EARLY`. Camera-like apps: TTID is not "usable"; measure the real
   first frame with `event_latency`.

### Jank, stutter, freezes, low fps
1. App with FrameTimeline: `android_jank_frames process_name=...`. Use `visible_jank` (late / dropped), not `janky`
   (Buffer Stuffing frames are on time). `worst_visible_causes` attributes the worst frames.
2. Empty result (GL / SurfaceView / games / camera previews have no FrameTimeline): find the render thread with
   `list_threads_in_process` (busiest threads), then `frame_intervals process_name=... thread_name=...` → fps,
   interval percentiles, freezes with `gap_start_ts` / `gap_end_ts`, and `freeze_summary` (Running / Runnable share
   of the render thread during freezes, its nice value, freeze period).
3. For the worst frame or a freeze window: Running → `cpu_profile` with that window (compare with a normal window
   of the same length); Runnable → `cpu_top_threads` + `cpu_frequency`; Sleeping → `wakeup_chain ...
   critical_path=true` names the real blocker (e.g. a GPU driver compiler thread that the waker chain does not show).
4. Periodic freezes: look for the trigger just before each `gap_start_ts` with `android_logs` (the app's own tags)
   or `slice_hotspots` over a short window; compare several freezes. Check `android_gc` and `memory_summary` for
   the same windows.

### ANR
1. `android_anrs [process_name=...]`: per ANR `likely_blockers`, `stuck_binder` (unanswered call, destination
   binder pool busy/total), `system_check` (app_specific / system_freeze / system_server_freeze), `critical_path`.
2. Stuck binder: analyze the destination process in the same window (`thread_state_breakdown`,
   `lock_contention`, `android_binder_txns side=server`). Pool exhaustion means look at what its binder threads wait on.
3. System-wide (`system_freeze` or several ANRs within seconds): `memory_summary`, `io_summary`, `cpu_top_threads`
   for the window before the ANR.
4. No ANR in the trace: check the time span the data actually covers (sched / slice min and max ts) against when
   the ANR happened, and ask for the ANR time, package and `/data/anr` traces or a bugreport rather than guessing.

### High CPU, heat, background drain
1. `cpu_top_threads group_by=process` over the whole trace or the busy window, then `group_by=thread_name` (thread
   pools) and `thread_churn` (threads created again and again).
2. Busy thread → `cpu_profile` (needs `linux.perf` in the trace) or, on a live device, `profile_threads`.
3. `cpu_frequency` per cluster; `slice_hotspots process_name=...` for what the code was doing.
4. Overview hints: capture / log daemons' own CPU (`cpu_overhead`), `noisy_log_tags`, `vsync_consumers` (animating
   while nothing is drawn), `missing_services` (1 Hz retry loops).

### Standby / idle power
1. Capture with `record_trace config=idle` (screen off, device untouched) to keep the capture's own overhead low.
2. `cpu_top_threads group_by=process`, wakeups via `wakeup_chain` on the busiest threads, `thread_churn`.
3. Overview: `missing_services`, `vsync_consumers`, `noisy_log_tags`, `process_churn` (kill / restart loops).
4. Compare devices or settings with `compare_traces` (no `process_name`): `system.cpu_pct`, `process_cpu_pct`.

### Memory pressure, Java leaks
1. `memory_summary`: MemAvailable (not MemFree), swap, LMK kills, direct reclaim stalls, kswapd CPU, top RSS
   growth. `process_memory process_name=...` for one app over time and its oom_adj.
2. Leak check: `record_trace config=heap process=<name>` (baseline) → repeat the scenario N times → heap dump again
   (and again after 2N) → `java_heap baseline_trace_id=<first>` on the later dump → `java_heap class_name=<growing class>`
   for the reference path to the GC root (who keeps it alive).
3. Judge growth on reachable objects; the first round often only fills caches and log ring buffers.

### IO stalls
`io_summary` with the window: threads in D state with `blocked_function` (IO vs non-IO), block-device latency and
slowest requests, page-cache reads per process. Kernel threads are excluded by default
(`include_kernel_threads=true` to see them).

### Slow boot
1. Bootchart (no trace needed): `analyze_bootchart path=<dir or .tgz>` → bootloader stages, milestones, phase gaps
   and the CPU users inside each gap. `focus_process` for one process.
2. Boot trace: `boot_timeline` → boot_progress milestones, system_server phases, slowest services, other long
   system_server work, `slow_init_commands`. Drill into a phase with the per-window tools.
3. Capture both at once with `record_boot` — it **reboots the device**; ask the user before calling it.

### Operation latency (tap → result, key → screen on)
`event_latency anchor_log="<marker text>" anchor_tag=<tag> target_globs=["<slice glob>", ...]
[target_process=...]`, or `anchor_slice=<glob>`. Make targets specific (one anchor kind can cover different
operations) and read the per-`anchor_kind` summary. Write markers from the test script with `log -t <tag> '<op>'`.

### Base vs fix (A/B)
1. Load both traces. `compare_traces trace_a=... trace_b=... process_name=<app> align=startup` for launch A/B
   (adds critical-path deltas), `align=trace` for whole scenarios, no `process_name` for system-wide CPU.
2. The `improved` / `regressed` labels are per-pair thresholds, not statistics: trust a change only if it repeats
   over several runs per side; report per-run numbers.
3. Check that both sides ran under the same conditions before crediting the change: page-cache state (same APK
   madvise / dex reads taking very different time), background load (`cpu_top_threads group_by=process` on both
   windows), memory pressure (kswapd), frequency. A device-wide change (e.g. background dexopt) also changes the
   other apps in the trace, so they are no control group.
4. Read nested-slice and whole-process deltas with care: slices whose names encode per-run details (frame resync
   counts) and background-thread binder time can dominate the diff without being on the launch path; confirm with
   the critical-path section.

## Capturing on a device
See `references/capture.md` for configs, durations, user vs userdebug builds and how to drive the scenario while
`record_trace` runs.

## Reporting
For each conclusion give: the evidence (tool, window, numbers), the claim it supports, and what would confirm or
refute it next. Name the window (`startup_id` or ns range) so the user can reproduce it. When options are
compared, report the measured differences of each; leave the product choice to the user.
