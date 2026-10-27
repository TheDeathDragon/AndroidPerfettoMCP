# Changelog

## [Unreleased]

- Review fixes (docs/04, fourth round): `record_trace` / `record_boot` / `unload_trace` also hold back default-trace calls;
  the supervisor retries a crash restart whose new worker died at startup; the logcat bundle is reused only for the
  same files and built only when loaded; the host logcat stream never throws in cleanup and leaves the adb server
  alone; `process_cpu_pct` resolves owners once per thread; `process_churn` / `important_kills` pre-filter
  `android_logs` once; `supervisor.log` rotates while running; the user-build logcat hint names the right format.
- Build: `AnalysisLevel=latest-recommended` and `EnforceCodeStyleInBuild` (0 warnings). Code comments trimmed.

- Fix: a tool called without `trace_id` in the same batch as a `load_trace` could resolve the default trace before the
  load registered it and silently query the previous trace (requests are dispatched concurrently). The transport now
  notes arrival order; such calls wait for every earlier `load_trace` to finish.
- `record_trace`: on user builds (android.log gets nothing: SELinux denies traced_probes the logdr socket) `adb logcat`
  runs on the host during the recording and is merged into `<trace>+logcat.zip`, which is loaded (`logcat`
  auto / on / off). `config_text` takes an inline TraceConfig, so the caller can compose the data sources.
- `load_trace` `logcat`: merge a logcat text file (`-v threadtime -v year`) into a trace's timeline.
- New tool `profile_threads`: simpleperf call-stack sampling of chosen threads on the device (process / comm prefix /
  kernel thread / tid), self and inclusive hot functions and samples per thread; sets and restores kptr_restrict.

- `load_trace` overview: a hint when the config asked for `android.log` but no logcat line arrived (user builds deliver
  nothing; log-derived sections are then empty). `dex_reverify` also lists a single runtime verify >= 200 ms of a platform
  dex (an APEX app still run-from-apk before its first background dexopt).

- `load_trace` overview: `process_churn` (apps started >= 3 times, average life, kills by lmkd / am_kill / UniStab) with a
  kill / restart loop hint and restart sources (`am_proc_start` type "restart", UNIPNP `restart by unipnp`); `dex_reverify` (platform dex verified >= 3 times, i.e. opened through a private class loader);
  `app_compactions` (CachedAppOptimizer `am_compact` per process with the app's own GC time, hint for >= 2 s);
  `noisy_log_tags` (lines/s per tag and level, Unisoc `[Ttid@Ccpu]` kernel prefixes folded, hint for >= 50 lines/s).
  `vsync_consumers` (app main threads woken by SurfaceFlinger's vsync thread vs RenderThread runs; hint for >= 20/s
  with little drawing: an animation running in a stopped activity or with the screen off).
  `important_kills` (apps that died at oom_adj 100-899 with the killer: lmkd, UniStab or am_kill; hint up to the
  previous app, adj 700).
- `io_summary`: blocked-function family `workqueue_wait` (`__flush_work` / `flush_workqueue`, e.g. eswap read-back).
- Capture config `scenario` also records the kernel log (`LID_KERNEL`): driver log floods show up in `noisy_log_tags`.

- Fix (supervisor): a failed hot swap left the new worker running; one long-running supervisor leaked one worker per
  1.5 s poll (184 processes) because `JsonValue<T>`'s type initializer had failed once in that process and every
  `JsonValue.Create` threw `TypeInitializationException`. Supervisor JSON-RPC messages are now built as text; a failed
  worker is killed, the version is not retried for a minute, a worker dying during startup no longer triggers a crash
  restart, polling survives exceptions, and non-JSON worker stdout is dropped. The supervisor log is mirrored to
  `state/supervisor.log` (Claude Code keeps only a server's startup stderr). Needs a `/mcp` reconnect.
- `boot_timeline`: `slow_init_commands` (init `Command '...' took N ms` lines >= 500 ms, with trigger and rc source)
  and a hint for >= 2 s (e.g. `swapon_all` building a 12 GB swap file: 29.4 s). Matches the number, as long kernel lines are cut ("29449m").
- Fix: `sf_composition.gpu_pct` counts client composition by `drawLayers` on SurfaceFlinger's main thread; RenderEngine
  renders for region sampling (nav-bar luma) are reported separately as `region_sampling_renders` (a camera trace
  read 124 % before).
- `compare_traces`: `process_cpu_pct` verdicts use a 0.1 percentage-point floor (idle processes are far below 1 % of a CPU).
- Built-in capture config `idle` (`record_trace config=idle`) for standby / power: sched, wakeups, frequency, idle,
  suspend, wakeup sources and IRQs; process list once, memory counters every 5 s, logcat >= I in its own buffer.
  Capture CPU in a 30 s screen-off trace drops 60-77 % vs `scenario`. The overview's capture-overhead hint points to it.
- `compare_traces` without `process_name`: `system.cpu_pct` / `system.sched_slices_per_s` (normalized by the window) and
  `process_cpu_pct`, the processes behind the CPU difference (two devices or traces of different length compare).
- Fix: `record_trace` waited for perfetto with a device-side `sleep 1` loop that forked a process every second
  (~57 ms CPU each on ums9230, 5.7 % of a CPU, a third of a screen-off idle trace). It now waits on the host while
  recording and polls the device only for the final flush. `record_boot` waits on the host for the boot trace's
  planned end (perfetto start time + duration) and polls boot_completed every 3 s instead of every second.
- `load_trace` overview: `sf_composition` (frames composited, GPU / client-composed share, RenderEngine ms) with a hint
  when >= 30% of >= 100 frames are GPU-composed.
- `load_trace` overview: `nameless_processes` + hint when >= 20 processes that ran have no name (start-of-trace process
  scan lost, e.g. a log burst wrapped the ring buffer holding it); the missing_services hint no longer starts with an
  empty process name.
- Capture configs (`scenario`, `boot`): logcat gets its own 32 MB buffer so a log burst cannot overwrite the process
  scan. `capture_camera_ops.sh`: the Capture tab is at x=228 once Video is selected (360 left the app in Slow motion).
- Fix (review): `event_latency` numbers anchors by (ts, label) so ties are stable and windows never collapse to 0,
  leaves `done_ms` null for slices still open at trace end, and resolves shadow processes before `target_process`;
  `thread_churn.unnamed` uses task_rename's `pid` arg (the renamed thread, not the renaming one).
- `smoke_calls_v07.json` covers `event_latency` and the startup rule (third trace: settings_cold); passed on Ubuntu 18.04.
- `android_startups`: info finding `REPORT_FULLY_DRAWN_EARLY` when the app calls reportFullyDrawn() before its first frame
  (the stdlib then clamps TTFD to TTID, so TTFD == TTID hides that the metric measures nothing).
- `event_latency`: the summary is split by `anchor_kind` (anchor label with numbers normalized) when the anchors
  differ, e.g. app launches vs going home, shutter vs camera switch.
- `event_latency`: `done_ms` (when the target slice ended, e.g. panel on = end of `setPowerMode(ON)`) per hit and
  done min / avg / max in the summary.
- New tool `event_latency`: trigger -> response latency. Anchors are logcat lines containing `anchor_log` (optionally
  one `anchor_tag`) or slices matching `anchor_slice`; for each anchor the first slice matching every `target_globs`
  entry before the next anchor (max `window_ms`) is reported with process / thread, plus per-target hits and
  min / avg / max. `anchor_sources` shows which tags matched (adbd logs the shell commands it runs).
## [0.7.0] - 2026-10-24

- `tests/e2e/smoke_calls_v07.json`: regression list for the new overview / cpu_top_threads features (no device tools);
  passed on Ubuntu 18.04.
- `health_issues` rows carry `affects` (profile / slices / logs / config / ftrace / trace_end / unknown) and `idx`
  (e.g. the CPUs of `ftrace_cpu_has_data_loss`); impacts for perf skipped samples, symbolization and config advice;
  the hint names the affected data and stays mild when only the profile, a few slices or logs are incomplete.
- `load_trace` overview: `missing_services` aggregates libbinder "Waited one second for <service>" by service and waiting
  process, with a hint when a client retries >= 10 times (a service that never registers costs a 1 Hz wakeup chain
  through servicemanager and init). Small `android_log_*` losses are graded minor.
- `cpu_top_threads`: `group_by=thread_name` sums same-named threads (worker pools) per process; `thread_churn` lists
  thread names created >= 5 times in the window (from task_newtask) with exits, average lifetime, CPU and `unnamed`
  (never renamed, so the name is the creating thread's).
- `health_issues` impact shows "<0.01%" instead of "0.0%" for a handful of failures.
- `load_trace` `health_issues` explain their impact and are graded: per-CPU counters are folded into one row
  (`entries`), `systrace_parse_failure` shows its share of ftrace print events and lost perf records their share of
  samples; when everything is minor (< 5 %) the hint says scheduling / timing data are complete instead of
  "conclusions may be unreliable" (every MTK trace has ~0.5 % unparseable vendor atrace lines).
- Fix: worker threads split into shadow processes (pid == tid) counted as main threads in `compare_traces`
  (`main.sync_binder_ms`, `main_thread_slices_ms`), `android_binder_txns` (`from_main_thread`, storms),
  `lock_contention` (`via_binder_client`) and `main_thread_blocking_calls`: only named processes have main threads.
- `load_trace` overview warns when the trace has no scheduling data (a capture whose ftrace part failed) — except heap
  dumps — and when sched exists without thread states.
- Thread owner resolution is per thread (`ProcessSelector.OwnerUpidOf` / `NameOf`): threads trace_processor parked in a
  nameless placeholder process (pid 0xFFFF0000, mixing threads of several processes) are attributed to the named
  process that has a thread with the same tid. Applies to the overview, list_processes, cpu_top_threads,
  list_threads_in_process, io_summary, cpu_profile, slice_hotspots, wakeup_chain and critical paths.
- `compare_traces align=startup`: `critical_path_ms` — the main thread's critical path per thread on both sides (threads
  absent on one side count as 0), showing which waits the launch lost or gained. Binder pool thread names
  (`binder:<pid>_<hex>`) normalize to one key; critical-path process names resolve shadow processes.
- `frame_intervals`: `freeze_summary` when freezes exist — render-thread running / runnable / sleeping / D share during the
  freezes, freeze period, render-thread nice, and `likely` (computing on the render thread, starved at default priority,
  periodic recomputation).
- `load_trace` overview: top CPU processes fold linux.perf shadow processes into their owner (an app's busiest threads
  showed up as nameless processes and the app looked idle).
- Selecting a thread by `utid` also covers the utids the same thread was split into (shadow halves).
- `compare_traces`: main-thread states skip dead (Z / X) states, which an earlier instance of the process left open over
  the whole window (showed as `main.Exit (Zombie)_ms` = window); `main.Sleeping_ms` has no verdict (idle vs. waiting, and
  it scales with startup-aligned windows of different length).
- Server instructions: critical path, leak-check workflow (heap dump -> baseline diff -> class_name), sequential calls for
  performance. Ubuntu 18.04 regression list covers critical path / startup / ANR / heap paths (tests/e2e/smoke_calls_v06.json).
- Fix (review): device waits treated a device-side timeout as success, so `record_boot` never reported a device that
  did not finish booting; bootchart files were deleted from the device without checking the pulls; an unfinished boot
  trace was pulled and deleted while perfetto still wrote it; adb timeouts surfaced as raw `TimeoutException`.
- `java_heap class_name=...`: shortest reference path from a GC root to the largest reachable instances of a class
  (field per hop, root type, app classes on the path) — who keeps them alive; findings point at it.
- `android_startups` findings from the critical path: `CRITICAL_PATH_BLOCKER` (a non-main thread >= 15% / 30% of the
  launch, with its running / runnable / D split and main slice; starved blockers get a priority suggestion) and
  `VSYNC_WAIT_ON_PATH` (info: the main thread waited on SurfaceFlinger vsync dispatch — several frames, not a blocker).
- Critical path root: with several instances of the process (restarts) the one running most in the window is used
  (a dead instance's open-ended last state used to win); an empty path explains itself in `note`.
- `android_anrs` details: main-thread `critical_path` over the timeout window (last 10 s for long timeouts).
- Critical path stops where a sleep of the selected thread starts when that sleep outlasts the window without a causal
  waker (always for ANRs, otherwise when the wake comes > 1 s later or never) and says so in `note`: the stdlib walk
  would otherwise attribute the wait to unrelated threads (ANR handling, later wakeups).
- `android_startups` (with `startup_id`): the main thread's `critical_path` over the launch (`critical_path=false` to skip).
- `wakeup_chain`: `critical_path` section from stdlib `sched.thread_executing_span` (threads that held up the selected
  thread over the window, their running / runnable / D time while on the path, and the slices they executed); on by
  default for explicit windows up to 30 s. `android_jank_frames` points sync-draw frames at it.

## [0.6.0] - 2026-10-24

Device capture (adb), boot analysis, Java heap, scenario-driven rule fixes and performance work.

- Missing adb is reported as a normal tool error (no server warning / exception type in the message).
- Verified on Ubuntu 18.04 (glibc 2.27): all tools incl. record_trace / record_boot through adb.

- Add `analyze_bootchart`: parses Android init bootchart (`/data/bootchart` dir or `bootchart.tgz`) — bootloader stage
  times from the kernel cmdline, milestones (init → launcher), phase gaps with the top CPU consumers in each gap,
  per-second CPU/iowait/run-queue timeline, disk reads, `focus_process` growth; findings SLOW_PHASE, BOOTLOADER_SLOW,
  IO_BOUND, CPU_SATURATED, LOGGING_HEAVY with `next_evidence`.
- `android_jank_frames`: `visible_jank` / `visible_jank_pct` (Late Present or Dropped Frame) and `buffer_stuffing_only`
  (on-time frames FrameTimeline still marks as jank); `worst_visible_causes` for the 5 worst visible frames: main
  thread Running / Sleeping / Runnable / D, RenderThread running, GPU pipeline compile time, longest non-generic
  main / render slice and a `likely` cause. `compare_traces` adds `frames.visible_jank_pct`.
- Background prewarm after each (re)load: INCLUDE of android.critical_blocking_calls / android.binder /
  android.monitor_contention when the trace has such data, one module at a time and only while the trace is idle
  (never blocks a user query). `PERFETTO_MCP_PREWARM=0` disables it; `list_traces` shows `prewarmed_modules`.
  First main_thread_blocking_calls on a 158 MB boot trace: 4.3 s -> 1.3 s.
- `process_memory` and `compare_traces` read the mem.* counters directly too (shared CTE; the wide RSS table carries
  each field forward with a range join so ftrace rss_stat updates at different times still line up): process_memory
  3.5 s -> 1.3 s on a 158 MB boot trace with identical output. linux.memory.process is no longer used.
- `memory_summary`: per-process peak RSS / growth read directly from the mem.* counter tracks in one query instead
  of the stdlib memory_rss_and_swap_per_process view (5.3 s -> 1.9 s on a 158 MB boot trace, same numbers).
- Add `server_stats`: per-tool durations, slowest calls and slow trace_processor queries (exec vs wait time).
- `load_trace` overview: `timing` (load seconds, overview ms, slowest overview steps).
- `android_binder_txns.slowest_breakdown` computed directly for the slowest calls (server thread states with D-state
  function, monitor / lock contention, client overhead) instead of the stdlib `android.binder_breakdown` module, whose
  first use materializes every transaction: 20.3 s -> 2.9 s on a 158 MB boot trace.
- Add `java_heap`: Java heap dump analysis (dump sizes / OOM, classes by retained size and by instance count, live UI
  objects, bitmaps, suspended-coroutine backlog) and class growth against `baseline_trace_id`; per-class growth is
  downgraded to info when overall reachable objects grew < 2%.
- `record_trace`: built-in `heap` config with a `process` parameter; the overview hints java_heap for heap dumps.
- SQL error hint: a stdlib object missing although its module is already included is reported as a table function
  needing arguments instead of asking to include the module again.
- `configs/boottrace.pbtxt` adds FrameTimeline so jank tools work on interactions recorded in long boot traces.
- `boot_timeline` other-thread work is limited to the boot window (last milestone + 10 s).
- Add `record_boot`: reboots an adb device (userdebug) with bootchart and / or a Perfetto boot trace enabled, waits
  for boot_completed and the end of the trace, pulls and cleans up, returns `analyze_bootchart` + `boot_timeline`.
  Built-in `boot` config (configs/boottrace.pbtxt) is also usable from `record_trace`.
- `boot_timeline`: falls back to `wm_boot_animation_done` when `sf_stop_bootanim` is missing; other-thread work
  skips monitor-contention slices.
- Add `record_trace`: record on an adb device with the built-in `scenario` config (or a local .pbtxt), optional
  `duration_s`, pull to ~/perfetto-traces (or `out_dir`), clean the device, load and return the overview. Waits with a
  single device-side loop so the recording is not perturbed by per-second adb shells; cancellation stops perfetto.
- `cpu_overhead` counts child processes of log daemons / capture processes (ylog's sh + cat helpers).
- Add `boot_timeline`: boot trace milestones (boot_progress_*, boot animation end, user unlock, long-lived process
  starts) with gaps, key intervals, system_server SystemServerTiming phases, slowest service starts and long work on
  other system_server threads; hints when the trace starts after zygote.
- `load_trace` overview: `cpu_overhead` (total CPU, the capture's own traced / traced_probes / perfetto share and
  log daemons' share) with hints when they are >= 10% / >= 15% of all CPU.
- `worst_visible_causes`: `jank_type`, `render_wait_buffer_ms` (RenderThread in waitForBufferRelease) and
  `display_power_change_ms` (overlap with surfaceflinger `setPowerMode(*)`); `likely` names display power transitions
  and buffer starvation before the generic main-thread causes.
- `lock_contention` findings: `LOCK_HELD_WHILE_WAITING` (holder parked / waiting / sleeping inside the monitor),
  `LOCK_HELD_DURING_BINDER`, `LOCK_PING_PONG` (two threads block each other), `LOCK_MAIN_LONG` (one main-thread wait
  >= 32 ms; `LOCK_MAIN_THREAD` needs > 5 waits and > 100 ms).
- `android_startups`: `ODEX_FILTER` names the real dex (from `OpenDexFilesFromOat(<path>)`; ART logs `location=error`
  when no oat exists) and tells app code from shared libraries; `dex_files` gain `dex` / `kind`. `DEX2OAT` names the
  busiest installd call when installd is not compiling (e.g. `getUserSize` recomputing every app's size for Settings' storage summary).
- `configs/scenario.pbtxt`: scenario capture config.
- `configs/boottrace.pbtxt`: Perfetto boot-trace config (RING_BUFFER, sched/freq/block/atrace/logcat/process stats).
- Add `main_thread_blocking_calls`: the stdlib's curated blocking calls (binder, locks, inflate, traversal, draw,
  AIDL handlers ...) per process or per call type; main threads only unless `all_threads=true`.
- `android_binder_txns`: `storms` (> 50 calls per 100 ms from one client; peak, number of storm buckets to tell bursts
  from steady pipelines, top server / method and main-thread share in the peak).
- Conclusions carry evidence boundaries: ANR `likely_blockers` get `proves` / `next_evidence`, startup `findings` and
  `lock_contention.findings` get `next_evidence` (the tool call that confirms or refutes them).
- `execute_sql`: truncated results include `column_stats` (numeric min/max/avg/p50/p90/p99, text distinct + top values
  over up to 5000 rows); `summarize=true` returns stats plus `sample_rows` rows, optionally the largest by `sort_by`.

## [0.5.0] - 2026-10-24

- Add `wakeup_chain`: who a thread waited for — sleep time per waker, and the longest sleeps followed waker by
  waker (wakers are read from the row after the sleep) until a thread that was already active.
- `android_startups` with `startup_id` now returns `findings` from a 23-rule slow-launch rule set (main-thread binder /
  lock / IO / runnable, OpenDexFilesFromOat, AppImage, class loading, JIT, inflate, dlopen, WebView, storage, GC,
  bindApplication share, app forks, shell spawns, dex2oat/installd, CPU frequency, reclaim, kswapd, profiler overhead)
  plus ODEX_FILTER from ART's `location= status= filter=` slices (app code not AOT-compiled).
- `android_anrs`: `system_check` verdict (app_specific / system_freeze / system_server_freeze) from the main threads of
  system_server and all apps in the ANR window.
- `execute_sql`: on "no such column" the error lists the real columns of the referenced tables and the closest names.
- `io_summary`: blocked functions grouped into families (erofs, filesystem, page_cache_io, block_layer, storage_driver,
  device_mapper, swap, memory_reclaim, gpu_fence, mm_lock) with exact GLOBs.

## [0.4.0] - 2026-10-24

- Add `memory_summary`: meminfo start/end/min/max, vmstat deltas and rates, LMK kills, direct-reclaim stalls, kswapd CPU,
  top processes by peak RSS and by anon+swap growth.
- Add `process_memory`: RSS components, RSS timeline, OOM-adj bucket time, LMK and direct reclaim for one process;
  disambiguates several instances with the same name.
- Add `io_summary`: D-state time per thread (IO vs other) and kernel blocked functions, block-device request latency
  (issue/complete paired per device+sector), slowest requests, page-cache insertions per process.
- Add `compare_traces`: base vs fix diff (A / B / delta / %) of system, startup, CPU, main-thread states, binder, GC,
  memory and frame metrics; per-thread CPU and main-thread slices matched by digit-normalized names;
  `align=startup` uses each trace's Nth startup of the focus package as the window; rows carry an
  improved / regressed / same verdict (absolute floor + 5 % relative).
- Add `android_anrs`: per-type timeout fallback, likely blockers with confidence, stuck binder calls with the
  destination's binder thread-pool occupancy and what its threads were doing, open slices at ANR time.
- Add `lock_contention`: by lock holder, rule-based findings (main thread, hotspot, in binder reply, chains),
  worst contentions with the binder client behind system_server waits.
- `android_binder_txns`: overhead_ms (client - server) and android.binder_breakdown reasons for the slowest calls.
- `memory_summary`: growth_rating; `load_trace`: Perfetto trace diagnostics (capture_diagnostics).

## [0.3.0] - 2026-10-23

- Hot updates: `--supervise` runs a stable front process that hosts the server from `versions/<current.txt>/`; re-installing
  switches workers in place, keeps loaded trace handles (`trace_id`) and sends `tools/list_changed` — no client reconnect.
- A crashed worker is restarted automatically; the client gets an error only for the requests that were in flight.
- Install scripts use the versioned layout and register `perfetto-mcp --supervise`; `--version` prints to stdout.
- `tests/e2e/reload_test.py`: end-to-end hot-reload / crash-recovery test (verified on Windows and Ubuntu 18.04).

## [0.2.0] - 2026-10-23

- Add `cpu_profile`: perf callstack hotspots (self / inclusive / top stacks), unsymbolized frames as `lib.so+0x<rel_pc>`.
- Add `frame_intervals`: fps, interval percentiles, janky intervals and freezes of any render thread.
- Add `cpu_frequency` (time-weighted), `android_logs`, `android_gc`.
- `list_threads_in_process` reports scheduling priority and nice.
- Merge trace_processor "shadow" processes (pid == tid, no name) into their owner; split threads no longer vanish or double count.
- Windows: trace_processor children die with the server (kill-on-close job object).
- Release archives (`zip` / `tar.gz`) and `scripts/install.ps1` / `scripts/install.sh`.

## [0.1.0] - 2026-10-23

- Initial release: stdio MCP server, bundled trace_processor_shell v58.2 (Linux build targets glibc 2.27 / Ubuntu 18.04).
- Streamed trace loading with progress; query timeout kills and lazily reloads the processor.
- 15 tools: trace management, SQL / schema / stdlib discovery, startups, jank, binder, thread state, CPU, slice hotspots.
