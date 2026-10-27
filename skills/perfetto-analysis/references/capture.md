# Capturing traces on a device

## record_trace

`record_trace config=<name> [duration_s=N] [serial=...]` records on an adb device, pulls the file, removes the
device copy and loads it. The device keeps running while it records: start the call, then drive the scenario
(launch, scroll, unlock ...) by hand or with an Android-automation MCP.

| config | Use for | Notes |
|---|---|---|
| `scenario` | launches, jank, ANR, CPU, binder, IO, memory | sched / freq / block / atrace / FrameTimeline / process stats / logcat; no callstack sampling |
| `idle` | standby, background power, wakeups | low own overhead; process list once; screen off and leave the device alone |
| `heap` | Java heap dump of `process=<name>` | one dump per call; for `java_heap` |
| `boot` | used by `record_boot` | |
| a `.pbtxt` path or `config_text` | anything else | e.g. a built-in config plus `ftrace_events: "block/block_rq_issue"`, or `linux.perf` callstack sampling |

- Callstack sampling (`linux.perf`) is not in `scenario` on purpose: on small-memory devices traced_perf costs
  hundreds of MB and competes for IO, which distorts launch timings. Record a separate trace with perf when you
  need stacks, or use `profile_threads` on the live device (userdebug).
- Several devices attached: pass `serial`.
- Keep scenario traces short (10–30 s): long traces load slower and dilute per-window numbers.

## User vs userdebug builds

| | userdebug | user |
|---|---|---|
| logcat in the trace (`android.log`) | yes | no (SELinux); `record_trace` merges a host `adb logcat` into `<trace>+logcat.zip` automatically (`logcat=auto`) |
| `profile_threads`, `record_boot` | yes | no (need root) |
| absolute numbers | often pessimistic (debug kernel options, verbose logging) | representative |

Measure gains on the build the user ships; userdebug is fine for finding causes.

## record_boot

Reboots the device, captures the init bootchart and a Perfetto boot trace, then runs `analyze_bootchart` and
`boot_timeline`. Always confirm with the user first. Needs userdebug.

## Loading existing files

- `load_trace path=...` accepts .pftrace / .perfetto-trace / .pb / .gz and zip bundles; `logcat=<file>` merges a
  `logcat -v threadtime -v year` text file into the timeline.
- Large traces (hundreds of MB) load in tens of seconds with progress; the client's tool timeout must be longer.
- `analyze_bootchart` reads a bootchart directory or .tgz directly, no trace needed.

## Measuring well

- Repeat each scenario several times and compare distributions; one run is anecdote.
- Cold launch: force-stop and drop caches the same way for every run; keep the test script's own work (shell
  loops, `am start -W`) out of the measured window.
- Mark operations from the test script (`log -t <tag> '<op>'`) and measure with `event_latency anchor_tag=<tag>`;
  adbd also logs the command text, so filter by tag.
