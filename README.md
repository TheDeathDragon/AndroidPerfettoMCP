# AndroidPerfettoMCP

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)

[English](README.md) | [简体中文](README.zh.md)

MCP server for Android performance analysis with [Perfetto](https://perfetto.dev) traces. C# .NET 10, runs on
Windows and Linux (Ubuntu 18.04+), ships its own `trace_processor_shell` v58.2.

- Dedicated tools for startup, jank, ANR, binder, locks, memory, IO, GC, CPU / frequency, wakeup chains and critical
  path, boot (bootchart + boot trace) and Java heap dumps; `execute_sql` with the PerfettoSQL stdlib for the rest.
- Large traces stream in with progress. A query that times out restarts trace_processor and the trace reloads on
  next use, so one slow query never blocks the following ones.
- Several traces at once (`trace_id`), base vs fix diff (`compare_traces`), non-ASCII paths, proxy environments.
- Records traces on an adb device (`record_trace`, `record_boot`, `profile_threads`) and loads them directly.
- Hot update: under `--supervise` a new version is picked up without reconnecting the client.

## Install

Get a release archive (`perfetto-mcp-<version>-win-x64.zip` / `perfetto-mcp-<version>-linux-x64.tar.gz`) from the
GitHub releases, or build one (see [Build from source](#build-from-source)). Then:

Windows (PowerShell):

```powershell
.\scripts\install.ps1 .\perfetto-mcp-<version>-win-x64.zip -AutoConfig
```

Linux (x86_64 with AVX2, glibc 2.23+):

```bash
sh scripts/install.sh perfetto-mcp-<version>-linux-x64.tar.gz --auto-config
```

The installers copy the server to `%LOCALAPPDATA%\Programs\perfetto-mcp\` / `~/.local/share/perfetto-mcp/` (each
version in `versions/<id>/`), verify both binaries and print the client config. `-AutoConfig` / `--auto-config` runs
`claude mcp add perfetto --scope user` for Claude Code. Re-running an installer while the server is in use is safe:
the running server switches to the new version within seconds.

The build is self-contained (no .NET runtime needed). `record_*` and `profile_threads` need `adb` on `PATH`.

## Claude Code config

`-AutoConfig` / `--auto-config` does this for you. Manually:

```bash
claude mcp add perfetto --scope user -- "C:/Users/<you>/AppData/Local/Programs/perfetto-mcp/perfetto-mcp.exe" --supervise
claude mcp add perfetto --scope user -- ~/.local/share/perfetto-mcp/perfetto-mcp --supervise      # Linux
```

or JSON:

```json
{
  "mcpServers": {
    "perfetto": {
      "type": "stdio",
      "command": "/home/<you>/.local/share/perfetto-mcp/perfetto-mcp",
      "args": ["--supervise"]
    }
  }
}
```

Loading a very large trace can take longer than the client's tool timeout; raise it (Claude Code: `MCP_TOOL_TIMEOUT`).

## Tools

| Name | Purpose |
|---|---|
| `load_trace` | load a trace (optionally merged with a logcat file); returns `trace_id` and an overview with health issues and hints |
| `list_traces` / `unload_trace` | loaded traces; free one |
| `execute_sql` | PerfettoSQL with row / time limits; column statistics instead of rows with `summarize` |
| `list_tables` / `list_table_structure` / `list_stdlib_modules` | schema and stdlib module lookup |
| `list_processes` / `list_threads_in_process` | processes and threads by CPU time |
| `android_startups` | app launches (cold / warm / hot, TTID / TTFD); per launch: breakdown, main thread, slow-launch rules, critical path |
| `android_jank_frames` | FrameTimeline jank per process, visible vs buffer stuffing, causes of the worst frames |
| `frame_intervals` | frame cadence and freezes of any render thread (incl. GL / SurfaceView apps) |
| `thread_state_breakdown` | Running / Runnable / Sleeping / D-state of a thread or process in a window |
| `cpu_top_threads` | CPU per thread / process / thread name in a window; thread churn |
| `cpu_frequency` | time-weighted CPU frequency per cluster (throttling check) |
| `cpu_profile` | perf callstack hotspots; unsymbolized frames as `lib.so+0x<rel_pc>` |
| `slice_hotspots` | heaviest slices by total or max duration |
| `android_binder_txns` | binder by method, slowest calls with server-side reasons, binder storms |
| `lock_contention` | Java monitor contention by lock holder, linked to binder clients |
| `main_thread_blocking_calls` | curated blocking calls on main threads (binder, locks, inflate, draw ...) |
| `wakeup_chain` | who a thread waited for: waker chains and the critical path of a window |
| `event_latency` | trigger (logcat line or slice) to response latency per operation |
| `android_anrs` | ANRs and what the main thread was stuck in before each |
| `android_gc` | ART GC count and cost per process |
| `android_logs` | logcat search aligned with the timeline |
| `memory_summary` / `process_memory` | memory pressure (MemAvailable, swap, LMK, direct reclaim) / one process over time |
| `io_summary` | D-state blocking and its kernel functions, block device latency, page-cache reads |
| `java_heap` | heap dump: retained classes, UI objects, bitmaps, growth vs a baseline, path to GC root |
| `compare_traces` | base vs fix diff of startup, CPU, main thread, binder, GC, memory, slices, critical path |
| `analyze_bootchart` | init bootchart (dir or .tgz, no trace needed): boot phases and who slowed them |
| `boot_timeline` | boot trace: boot_progress milestones, system_server phases, slow services and init commands |
| `record_trace` | record on an adb device (built-in `scenario` / `heap` / `boot` / `idle` configs or your own), pull and load |
| `record_boot` | **reboots** the device and captures bootchart + boot trace, then analyzes both (userdebug) |
| `profile_threads` | simpleperf sampling of chosen threads on the device (userdebug) |
| `server_stats` | this server's tool durations and slow queries |

Tools take an optional `trace_id` and default to the most recently loaded trace. A typical drill-down: `frame_intervals`
finds a freeze, then `thread_state_breakdown`, `cpu_profile`, `cpu_frequency` and `wakeup_chain` over the same window
explain it. The server sends a condensed workflow to MCP clients in `initialize.result.instructions`.

## Configuration

| Environment variable | Flag | Default | Meaning |
|---|---|---|---|
| `PERFETTO_QUERY_TIMEOUT_S` | `--query-timeout` | 120 | per-query timeout; a timeout restarts trace_processor |
| `PERFETTO_LOAD_TIMEOUT_S` | `--load-timeout` | 900 | trace load timeout |
| `PERFETTO_MAX_INSTANCES` | `--max-instances` | 4 | resident trace_processor processes (memory bound) |
| `PERFETTO_MAX_RESULT_MB` | | 256 | raw result size cap per query |
| `PERFETTO_MCP_PREWARM` | | on | `0` disables background INCLUDE of heavy stdlib modules after load |
| `PERFETTO_MCP_ADB` | | `adb` | adb used by the device tools |
| `PERFETTO_TP_PATH` | | | use another `trace_processor_shell` |
| | `--debug` / `--quiet` | info | stderr log level |

Run `perfetto-mcp` directly for a stdio server without hot update; `--version` prints the version.

## Build from source

Requires the .NET 10 SDK.

```bash
git clone https://github.com/TheDeathDragon/AndroidPerfettoMCP.git
cd AndroidPerfettoMCP
dotnet build PerfettoMcp.slnx
dotnet test  PerfettoMcp.slnx
publish.bat win-x64            # Windows -> publish/win-x64/ and publish/perfetto-mcp-<version>-win-x64.zip
./publish.sh linux-x64         # Linux   -> publish/linux-x64/ and publish/perfetto-mcp-<version>-linux-x64.tar.gz
python -I tests/e2e/smoke_test.py <absolute path to perfetto-mcp> <trace>     # end-to-end over stdio
```

The bundled `trace_processor_shell` binaries and how the Linux one is built: [tools/README.md](tools/README.md).
Design and development notes (Chinese): [docs/README.md](docs/README.md).

## License

[Apache 2.0](LICENSE). The bundled `trace_processor_shell` is part of Perfetto (Apache 2.0).
