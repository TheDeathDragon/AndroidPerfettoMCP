# perfetto-mcp (.NET)

MCP server (stdio) for Perfetto trace analysis, Android-focused. C# .NET 10, runs on Windows and
Ubuntu 18.04+. Wraps a bundled `trace_processor_shell` (HTTP-RPC mode). Docs in `docs/` (Chinese):
start with `docs/README.md`.

## Build / test

```
dotnet build PerfettoMcp.slnx
dotnet test  PerfettoMcp.slnx                      # real trace_processor integration tests (+ lab/tests if present)
publish.bat win-x64 | publish.bat linux-x64        # self-contained single file + tools/
python -I tests/e2e/smoke_test.py <abs exe path> <trace> [...]   # end-to-end over stdio
```

In Git Bash set `MSYS_NO_PATHCONV=1` before `adb push/pull /data/...`.
Device capture is built in: `record_trace` (config `scenario` / `heap` / `boot` / `idle` or a .pbtxt), `record_boot`
(REBOOTS the phone).

Layout: `src/` server, `tests/` xunit + `tests/e2e/` stdio scripts, `configs/` embedded capture configs, `skills/`
Claude Code skill (playbooks; shipped in the release, installed by `-AutoConfig` / `-Skill`), `scripts/`
install / build-tp / stdlib index, `tools/` bundled trace_processor_shell (provenance: docs/01).
This repository is public: no device serials, hosts, customer / product names or private trace names in it.
Private material (device research, lab hosts, regression tests on private traces) lives in a separate repository
cloned to the git-ignored `lab/`; its notes: @lab/CLAUDE.md

## Updating the live server (no reconnect needed)

The user's Claude Code runs `%LOCALAPPDATA%\Programs\perfetto-mcp\perfetto-mcp.exe --supervise`. After changing code:

```
publish.bat win-x64
powershell -File scripts\install.ps1 publish\win-x64      # new versions/<id>/, switches current.txt
```

The supervisor swaps workers within ~2 s and sends `tools/list_changed`; loaded `trace_id`s survive (lazy reload).
Only changes to the supervisor itself (`Supervisor/`) need a `/mcp` reconnect by the user.
Supervisor log (incl. worker stderr) is mirrored to `%LOCALAPPDATA%\Programs\perfetto-mcp\state\supervisor.log` — Claude Code
keeps only a server's startup stderr. Live CLR exceptions of a running process: ETW (`logman ... Microsoft-Windows-DotNETRuntime
0x8000`, docs/02 3.1). Supervisor messages are built as text, not `JsonValue<T>` (a failed type initializer is permanent).
`python -I tests/e2e/reload_test.py <publish dir> <trace>` checks the whole mechanism.

## Key facts

- `tools/linux-x64/trace_processor_shell` is self-built v58.2 against an Ubuntu 18.04 sysroot
  (max GLIBC 2.23); official Linux prebuilts >= v48 need glibc 2.34. Rebuild via `scripts/build-tp.sh`
  on an x86_64 Linux host with Docker (see docs/01). Windows uses the official v58.2 prebuilt.
- Trace is streamed through `POST /parse` + `/notify_eof` (not argv) — progress + non-ASCII paths.
- Calls without `trace_id` wait for earlier-arrived `load_trace` / `record_*` / `unload_trace` (McpServer.NoteArrival,
  called by the transport in read order); requests otherwise run concurrently.
- User builds: perfetto gets no logcat (android.log is userdebug-only); `record_trace` merges a host `adb logcat` into
  `<trace>+logcat.zip` (TraceBundle). No root there: `profile_threads` / `record_boot` need userdebug.
- A query timeout kills the trace_processor child and the session reloads lazily; never let a
  timed-out query keep running (that caused cascading timeouts in perfetto-mcp-rs).
- `Resources/stdlib_index.tsv` must match the bundled Perfetto version (`scripts/gen_stdlib_index.py`).
- Session lifecycle invariants (docs/04): every access to the child goes through the session gate;
  Dispose cancels `lifetime`, releases the gate and is idempotent; `/query` responses are byte-capped.
- SQLite bare columns are only defined with a single MIN/MAX aggregate; use ROW_NUMBER for "row of max".
- stdlib modules materialize whole tables on first INCLUDE regardless of WHERE (binder_breakdown: ~20 s on 158 MB):
  prefer direct per-utid / per-window queries; sessions prewarm the heavy modules in the background when idle.
- Measure performance serially (`server_stats` separates exec vs wait): parallel calls on one trace queue on its gate.
- Critical path (`TraceToolBase.CriticalPathAsync`) is meaningful for short windows; it stops at sleeps without a
  causal waker (ANRs) and picks the instance running most when a process restarted.
- Thread / process names: resolve owners with `ProcessSelector.OwnerUpidOf` / `NameOf` (shadow pid==tid processes and
  the 0xFFFF0000 placeholder); main threads exist only in named processes; unrenamed native threads carry their
  creator's comm (task_rename `pid` arg = renamed thread).
- `stats` errors differ in impact: overview `health_issues.affects` grades them; only ftrace loss / trace_end are serious.
- Linux regression without touching a phone: `tests/e2e/smoke_calls_v07.json` on Ubuntu 18.04 (docs/03). In Git Bash,
  `tar` needs `--force-local` for `C:\` paths; publish / install from PowerShell (execution policy).

## Conventions

- stdout is JSON-RPC only; log via `Logging/Log` (stderr, UTF-8).
- No Chinese in code or commits; docs may be Chinese. Comments: one or two lines of "why" at key places only;
  device anecdotes and numbers go to docs/, not code.
- Build must stay at 0 warnings (`AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild`, `.editorconfig`);
  run `dotnet format PerfettoMcp.slnx` before committing.
- New tool: subclass `TraceToolBase`, declare params with `SchemaBuilder` (closed schema), register in
  `Program.cs`. Expected failures throw `ToolException`; interpolate strings via `SqlText.Literal`.
- Clean up temp files/dirs immediately after use.
- Commit messages: a single title line, no body, no trailers.
- Release: bump `<Version>` in `src/PerfettoMcp/PerfettoMcp.csproj`, add a `## [x.y.z]` CHANGELOG section, push tag
  `v<x.y.z>`; `.github/workflows/release.yml` tests both RIDs and publishes the GitHub release (CI: `ci.yml`).
