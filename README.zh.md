# AndroidPerfettoMCP

[![CI](https://github.com/TheDeathDragon/AndroidPerfettoMCP/actions/workflows/ci.yml/badge.svg)](https://github.com/TheDeathDragon/AndroidPerfettoMCP/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/TheDeathDragon/AndroidPerfettoMCP)](https://github.com/TheDeathDragon/AndroidPerfettoMCP/releases)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)

[English](README.md) | [简体中文](README.zh.md)

基于 [Perfetto](https://perfetto.dev) trace 的 Android 性能分析 MCP 服务。C# / .NET 10 实现，Windows 与 Linux
（Ubuntu 18.04+）均可运行，自带 `trace_processor_shell` v58.2。

- 启动、掉帧、ANR、binder、锁、内存、IO、GC、CPU / 频率、唤醒链与关键路径、开机（bootchart + 开机 trace）、Java 堆转储
  都有专门工具；其余用 `execute_sql` + PerfettoSQL stdlib。
- 大 trace 流式加载并上报进度；查询超时会重启 trace_processor，下次使用时自动重载，一条慢查询不会拖垮后续查询。
- 同时加载多个 trace（`trace_id`）、base / fix 对比（`compare_traces`）、中文路径、代理环境均可用。
- 直接在 adb 设备上录制并加载（`record_trace`、`record_boot`、`profile_threads`）。
- 热更新：`--supervise` 模式下安装新版本后无需客户端重连。

## 安装

Windows（PowerShell）：

```powershell
iwr https://raw.githubusercontent.com/TheDeathDragon/AndroidPerfettoMCP/main/scripts/install.ps1 | iex
```

同时注册到 Claude Code：

```powershell
& ([scriptblock]::Create((iwr https://raw.githubusercontent.com/TheDeathDragon/AndroidPerfettoMCP/main/scripts/install.ps1))) -AutoConfig
```

Linux（x86_64，需 AVX2，glibc 2.23+）：

```bash
curl -fsSL https://raw.githubusercontent.com/TheDeathDragon/AndroidPerfettoMCP/main/scripts/install.sh | sh
```

同时注册到 Claude Code：

```bash
curl -fsSL https://raw.githubusercontent.com/TheDeathDragon/AndroidPerfettoMCP/main/scripts/install.sh | sh -s -- --auto-config
```

安装脚本下载最新 [release](https://github.com/TheDeathDragon/AndroidPerfettoMCP/releases)，装到
`%LOCALAPPDATA%\Programs\perfetto-mcp\` / `~/.local/share/perfetto-mcp/`（每个版本在 `versions/<id>/`），校验两个二进制并打印
客户端配置。`-AutoConfig` / `--auto-config` 会执行 `claude mcp add perfetto --scope user` 并安装 [skill](#claude-code-skill)；
`-Skill` / `--skill` 只安装 skill。其他参数：`-Version` /
`--version <x.y.z>`；第一个参数给本地发布包或 publish 目录则安装它。服务运行中重新安装是安全的：几秒内自动切换到新版本。

自包含发布，无需安装 .NET。`record_*` 与 `profile_threads` 需要 `adb` 在 `PATH` 中。

## Claude Code 配置

`-AutoConfig` / `--auto-config` 会自动完成。手动：

```bash
claude mcp add perfetto --scope user -- "C:/Users/<你>/AppData/Local/Programs/perfetto-mcp/perfetto-mcp.exe" --supervise
claude mcp add perfetto --scope user -- ~/.local/share/perfetto-mcp/perfetto-mcp --supervise      # Linux
```

或 JSON：

```json
{
  "mcpServers": {
    "perfetto": {
      "type": "stdio",
      "command": "/home/<你>/.local/share/perfetto-mcp/perfetto-mcp",
      "args": ["--supervise"]
    }
  }
}
```

加载超大 trace 可能超过客户端的工具调用超时，需要调大（Claude Code：`MCP_TOOL_TIMEOUT`）。

## 工具列表

| 工具 | 用途 |
|---|---|
| `load_trace` | 加载 trace（可合并 logcat 文件），返回 `trace_id` 与概览（健康问题、提示） |
| `list_traces` / `unload_trace` | 已加载的 trace；释放一个 |
| `execute_sql` | PerfettoSQL，限制行数 / 时间；`summarize` 只返回列统计 |
| `list_tables` / `list_table_structure` / `list_stdlib_modules` | 表结构与 stdlib 模块查询 |
| `list_processes` / `list_threads_in_process` | 按 CPU 时间排序的进程 / 线程 |
| `android_startups` | 应用启动（冷 / 温 / 热，TTID / TTFD）；单次启动的拆解、主线程、慢启动规则、关键路径 |
| `android_jank_frames` | FrameTimeline 掉帧：按进程统计，可见掉帧与 Buffer Stuffing 分开，最差帧归因 |
| `frame_intervals` | 任意渲染线程的帧节奏与冻结（含 GL / SurfaceView 应用） |
| `thread_state_breakdown` | 线程 / 进程在窗口内的 Running / Runnable / Sleeping / D 状态 |
| `cpu_top_threads` | 窗口内按线程 / 进程 / 线程名的 CPU 占用；反复新建的线程 |
| `cpu_frequency` | 各簇时间加权 CPU 频率（降频排查） |
| `cpu_profile` | perf 调用栈热点；未符号化帧输出 `lib.so+0x<rel_pc>` |
| `slice_hotspots` | 按总耗时或单次最长排序的切片热点 |
| `android_binder_txns` | binder 按方法汇总、最慢调用及服务端原因、binder 风暴 |
| `lock_contention` | Java 锁竞争（按持锁者），关联 binder 客户端 |
| `main_thread_blocking_calls` | 主线程上的精选阻塞调用（binder、锁、inflate、draw…） |
| `wakeup_chain` | 线程在等谁：唤醒链与窗口内的关键路径 |
| `event_latency` | 操作触发（logcat 行或切片）到响应的耗时 |
| `android_anrs` | ANR 及每次 ANR 前主线程卡在哪里 |
| `android_gc` | 各进程 ART GC 次数与开销 |
| `android_logs` | 与时间线对齐的 logcat 检索 |
| `memory_summary` / `process_memory` | 系统内存压力（MemAvailable、swap、LMK、直接回收）/ 单进程内存随时间变化 |
| `io_summary` | D 状态阻塞及内核函数、块设备延迟、页缓存读入 |
| `java_heap` | 堆转储：保留大小、UI 对象、位图、与基线的增长、到 GC 根的引用路径 |
| `compare_traces` | base / fix 对比：启动、CPU、主线程、binder、GC、内存、切片、关键路径 |
| `analyze_bootchart` | init bootchart（目录或 .tgz，无需 trace）：开机阶段及拖慢者 |
| `boot_timeline` | 开机 trace：boot_progress 里程碑、system_server 阶段、慢服务与慢 init 命令 |
| `record_trace` | 在 adb 设备上录制（内置 `scenario` / `heap` / `boot` / `idle` 配置或自定义），拉回并加载 |
| `record_boot` | **重启**设备并采集 bootchart + 开机 trace，然后分析（需 userdebug） |
| `profile_threads` | 设备上用 simpleperf 对指定线程采样（需 userdebug） |
| `server_stats` | 本服务的工具耗时与慢查询 |

工具的 `trace_id` 可省略，默认最近加载的 trace。典型下钻：`frame_intervals` 找到冻结 → 在同一窗口用
`thread_state_breakdown`、`cpu_profile`、`cpu_frequency`、`wakeup_chain` 找原因。服务通过
`initialize.result.instructions` 向 MCP 客户端提供精简的使用流程。

## Claude Code skill

[`skills/perfetto-analysis`](skills/perfetto-analysis/SKILL.md) 是给 Claude Code 的分场景分析流程：启动慢、掉帧与冻屏、ANR、
CPU 高、待机功耗、内存与泄漏、IO、开机、操作耗时、A/B 对比，以及抓取指南和容易得出错误结论的数据陷阱。只在任务需要时加载。
安装脚本加 `-AutoConfig` / `--auto-config` 或 `-Skill` / `--skill` 时装到 `~/.claude/skills/perfetto-analysis/`，也可以手动复制该目录。

## 配置项

| 环境变量 | 命令行 | 默认 | 说明 |
|---|---|---|---|
| `PERFETTO_QUERY_TIMEOUT_S` | `--query-timeout` | 120 | 单次查询超时；超时会重启 trace_processor |
| `PERFETTO_LOAD_TIMEOUT_S` | `--load-timeout` | 900 | 加载超时 |
| `PERFETTO_MAX_INSTANCES` | `--max-instances` | 4 | 常驻 trace_processor 上限（内存约束） |
| `PERFETTO_MAX_RESULT_MB` | | 256 | 单次查询原始结果上限 |
| `PERFETTO_MCP_PREWARM` | | 开 | 设为 `0` 关闭加载后的 stdlib 重模块后台预热 |
| `PERFETTO_MCP_ADB` | | `adb` | 设备工具使用的 adb |
| `PERFETTO_TP_PATH` | | | 指定其他 `trace_processor_shell` |
| | `--debug` / `--quiet` | info | stderr 日志级别 |

直接运行 `perfetto-mcp` 为不带热更新的 stdio 服务；`--version` 打印版本。

## 从源码构建

需要 .NET 10 SDK。

```bash
git clone https://github.com/TheDeathDragon/AndroidPerfettoMCP.git
cd AndroidPerfettoMCP
dotnet build PerfettoMcp.slnx
dotnet test  PerfettoMcp.slnx
publish.bat win-x64            # Windows -> publish/win-x64/ 与 publish/perfetto-mcp-<版本>-win-x64.zip
./publish.sh linux-x64         # Linux   -> publish/linux-x64/ 与 publish/perfetto-mcp-<版本>-linux-x64.tar.gz
python -I tests/e2e/smoke_test.py <perfetto-mcp 绝对路径> <trace>     # 走 stdio 的端到端测试
```

随包 `trace_processor_shell` 的来源与 Linux 版构建方法见 [tools/README.md](tools/README.md)；
设计与开发文档见 [docs/README.md](docs/README.md)。

## 许可证

[Apache 2.0](LICENSE)。随包的 `trace_processor_shell` 属于 Perfetto（Apache 2.0）。
