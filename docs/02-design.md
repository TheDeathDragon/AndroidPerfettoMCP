# perfetto-mcp (.NET) 设计说明

> 目标：Ubuntu 18.04 + Windows 都能跑的 Perfetto MCP Server，解决 perfetto-mcp-rs 经常超时、18.04 跑不了的问题。
> 参考：[AndroidMCP](https://github.com/TheDeathDragon/AndroidMCP)（MCP 协议层/工程结构）、perfetto-mcp-rs（工具面/错误分类）。

## 1. 技术选型

| 项 | 选择 | 理由 |
|---|---|---|
| 运行时 | .NET 10（LTS，至 2028-11） | .NET 10 linux-x64 最低 glibc 2.27 = Ubuntu 18.04；.NET 8 于 2026-11 EOL；.NET 11 起不再支持 18.04 |
| 发布 | self-contained 单文件 + `InvariantGlobalization` | 目标机无需装 .NET，不依赖 libicu |
| trace_processor | 随包附带：Linux 自编 v58.2（glibc 2.23，见 [01](01-trace-processor-ubuntu1804.md)），Windows 官方 v58.2 | 不在运行时下载（内网/代理环境） |
| protobuf | 手写最小编解码（`Perfetto/ProtoWire.cs`、`TpMessages.cs`） | 只用到 4 个消息，免 protoc/codegen 依赖 |
| MCP 协议 | 复用 AndroidMCP 的 JSON-RPC/stdio 实现并扩展 | 增加 progress 通知、`notifications/cancelled` |

## 2. 目录结构

```
PerfettoMcp.slnx
src/PerfettoMcp/
├── Program.cs                 入口、参数、工具注册
├── Mcp/                       JSON-RPC 协议、stdio 传输、RequestContext(进度/取消)、PerfLog
├── Supervisor/                --supervise 守护进程（热更新，见 3.1）
├── Perfetto/
│   ├── TpBinary.cs            定位 trace_processor_shell
│   ├── TraceProcessor.cs      子进程 + HTTP RPC 客户端 + 孤儿进程回收
│   ├── TraceSession.cs        单个 trace：串行队列、加载、超时杀进程、自动重载、预热
│   ├── TraceManager.cs        多 trace、trace_id、LRU 常驻上限
│   ├── TraceBundle.cs         trace + 主机 logcat 合并 zip
│   ├── TpMessages.cs/ProtoWire.cs  protobuf 编解码
│   ├── QueryErrors.cs         SQL 错误分类 + 提示
│   ├── StdlibIndex.cs         stdlib 模块索引（嵌入资源）
│   └── Settings.cs            超时/实例数配置
├── Device/                    adb 封装、设备端多步操作（record_*、profile_threads）
├── Boot/                      init bootchart 解析
├── Tools/                     MCP 工具
└── Resources/stdlib_index.tsv 由 scripts/gen_stdlib_index.py 生成
configs/                       内置采集配置（scenario / boot / heap / idle，嵌入可执行文件）
tests/PerfettoMcp.Tests/       xunit（含真实 trace_processor 集成测试）
tests/e2e/                     stdio 端到端：smoke_test.py、reload_test.py、调用清单
tools/{win-x64,linux-x64}/     随包 trace_processor_shell（来源见 01）
scripts/                       install.ps1 / install.sh、build-tp.sh、gen_stdlib_index.py
docs/                          本文档目录
```

## 3. 针对 perfetto-mcp-rs 超时问题的设计

rs 版超时的主要原因（读代码分析）：
1. `load_trace` 启动超时 20 s 硬失败，大 trace 解析超过即失败；
2. 查询超时 30 s 后 **不杀/不重启** trace_processor，慢查询在服务端继续跑，trace_processor 串行执行，后续查询全部排队超时（连锁超时）；
3. Windows 中文路径：trace_processor 以 ANSI 码页解析 argv，加载失败。

本实现：

| 机制 | 说明 |
|---|---|
| 流式加载 | `trace_processor_shell -D --http-port N --http-ip-address 127.0.0.1` 空启动，.NET 读文件按 16 MB 分块 `POST /parse`，最后 `POST /notify_eof`。HTTP body 上限 64 MB（`kMaxPayloadSize`）。 |
| 加载进度 | 每块发 `notifications/progress`（客户端带 progressToken 时），0–90% 解析、90–100% 排序收尾 |
| 独立加载超时 | 默认 900 s（`PERFETTO_LOAD_TIMEOUT_S` / `--load-timeout`），与查询超时分开 |
| 每 trace 串行门 | `SemaphoreSlim(1)`；排队等待也计入超时，超时返回 "busy" 而不是堆积 |
| 查询超时 = 杀进程 | 默认 120 s（`PERFETTO_QUERY_TIMEOUT_S` / `--query-timeout` / 工具参数 `timeout_s`）；超时立即 kill 子进程，下次调用透明重载 trace，并在错误信息里提示如何收窄查询 |
| 客户端取消 | `notifications/cancelled` → 取消对应请求 → 同样 kill 子进程 |
| 结果大小上限 | `/query` 流式读取，超过 256 MB（`PERFETTO_MAX_RESULT_MB`）立即中止并重启 trace_processor，防止 MCP 进程 OOM |
| 客户端断开 | stdin EOF 时取消所有在途请求（加载/查询），子进程随之被杀，进程尽快退出 |
| 卸载/退出 | 会话 Dispose 取消在途加载、释放锁、杀子进程；`TraceManager.Dispose` 幂等（`using` 与 ProcessExit 都会调用） |
| 端口竞争 | `/status` 应答后再确认子进程仍存活，避免连到占用同端口的其他 trace_processor |
| 崩溃检测 | RPC 失败且子进程已退出 → 报"可能 OOM"并附 stderr 尾部 40 行；下次自动重载 |
| 文件变化检测 | size + mtime 变化自动重载 |
| 常驻上限 | 默认 4 个 trace_processor（`PERFETTO_MAX_INSTANCES`），超出时 LRU 驱逐空闲的；trace_id 仍有效，用时重载 |
| 中文路径 | 文件由 .NET 读取，不经过 trace_processor 的 argv |
| 不走代理 | `SocketsHttpHandler.UseProxy=false`（用户环境全局 export 了 http_proxy） |
| 管道排空 | stdout/stderr 持续异步读取，防止子进程写满管道阻塞 |
| 父死子亡 | Windows：子进程加入 kill-on-close 作业对象（`ChildProcessGuard`），MCP 被强杀时由系统一并结束 |
| 孤儿回收 | 子进程 pid 记录在 `%TEMP%/perfetto-mcp/<pid>.pid`（内容为父 pid）；启动时回收父进程已死的 trace_processor（主要针对 Linux，托管代码无法设置 PDEATHSIG） |

## 3.1 热更新（守护进程模式，0.3.0）

Claude Code 对 stdio MCP：进程退出后交互会话只能用户在 `/mcp` 里手动 Reconnect；但支持 `list_changed` 通知（收到后重新拉取工具列表，无需重连）。
因此用"稳定外壳 + 可替换工作进程"实现免重连更新：

```
<prefix>/perfetto-mcp.exe --supervise     ← Claude Code 启动的守护进程（Supervisor/Supervisor.cs，很少改动）
<prefix>/current.txt                      ← 当前版本 id
<prefix>/versions/<id>/perfetto-mcp.exe   ← 工作进程（真正的 MCP server）+ tools/
<prefix>/state/<守护进程pid>.json          ← 已加载 trace 列表（每个会话一份）
```

| 机制 | 说明 |
|---|---|
| 转发 | 守护进程逐行转发 JSON-RPC；记录请求 id → 所属工作进程，响应/取消按归属路由 |
| 更新检测 | 每 1.5 s 读 `current.txt`；安装脚本先写新目录再原子替换 `current.txt` |
| 切换 | 启动新工作进程 → 用客户端原始 `initialize` 参数握手（内部 id）→ 新请求发给新进程 → 旧进程处理完在途请求后关闭 stdin 退出（最长 20 分钟后强杀）→ 向客户端发 `notifications/tools/list_changed` |
| trace 句柄保留 | 工作进程把已加载 trace（id + 路径 + 当前活动 id）写入 state 文件；新进程按原 id 注册，首次使用时惰性加载 |
| 崩溃自愈 | 工作进程意外退出：在途请求返回错误提示重试，自动拉起新进程并通知客户端；1 分钟内超过 5 次则停止重启 |
| 文件锁 | Windows 运行中的 exe 不能覆盖但可改名：每次安装进新目录，外壳程序用"改名 + 复制"更新；旧版本目录（非当前/上一版）安装时清理 |
| 生命周期 | 工作进程加入作业对象（随守护进程退出）；stdin 关闭时守护进程关闭所有工作进程后退出 |
| 失败处理 | 新版本握手失败（超时 30 s、握手前退出、返回 error）：杀掉新进程、旧进程继续服务，该版本 1 分钟内不再重试；只有当前活动进程退出才算崩溃（启动中的新进程退出不触发自愈）；工作进程 stdout 的非 JSON 行丢弃不转发 |
| 日志 | 守护进程日志（含转发的工作进程 stderr）同时追加到 `state/supervisor.log`（>1 MB 轮转为 `.1`）：Claude Code 只保留服务器启动时的 stderr，长时间运行后的切换失败只能在这里看到 |

验证：`tests/e2e/reload_test.py`（安装 → 加载 → 重装触发切换 → 句柄恢复并惰性重载 → 坏版本（启动即退出）不泄漏、只报一次、旧进程照常服务 → 杀工作进程触发自愈 → 干净退出），Windows 16 项全部通过。

0.7.0 事故（2026-10-24）：一个运行了约 30 分钟的守护进程里 `System.Text.Json.Nodes.JsonValue<T>` 的类型初始化器失败过一次（具体原因已无从得知；类型初始化失败在进程内永久生效），此后 `InitializeAsync` 里每次 `JsonValue.Create(id)` 都立即抛 `TypeInitializationException`；旧代码在 catch 里不杀新进程，每 1.5 s 轮询泄漏一个工作进程（184 个），自愈重启同样失败。定位方法：ETW 抓 CLR 异常事件（`logman start clrex -p Microsoft-Windows-DotNETRuntime 0x8000 4 -ets -o x.etl` → 触发 → `logman stop clrex -ets` → `tracerpt x.etl -of XML`），无需重启进程。修复：守护进程的 JSON-RPC 消息改为字符串拼接（`JsonEncodedText` 转义），不再经过 `JsonValue<T>`。

## 4. 工具列表

| 工具 | 用途 |
|---|---|
| `load_trace` | 加载一个/多个 trace，返回 trace_id + 概览（时长、设备元数据、数据量、健康问题、启动列表、CPU Top 进程、提示） |
| `list_traces` / `unload_trace` | 管理已加载 trace |
| `execute_sql` | 任意 PerfettoSQL；`max_rows`(默认200/上限5000)、`timeout_s`、`max_string_len` |
| `list_tables` / `list_table_structure` | 表/视图及列（stdlib 视图自动 INCLUDE） |
| `list_stdlib_modules` | 按名称/对象搜索 stdlib 模块（嵌入索引） |
| `list_processes` / `list_threads_in_process` | 进程/线程，按 CPU 时间排序 |
| `android_startups` | 启动列表（含 TTID/TTFD）；带 `startup_id` 输出归因、主线程状态、主线程切片、主线程 binder |
| `android_jank_frames` | FrameTimeline 掉帧：按进程统计（可见掉帧 vs Buffer Stuffing）、jank 类型、最差帧（含期望时长）、最差可见帧归因（主线程状态、RenderThread、管线编译、最长切片、likely） |
| `android_binder_txns` | binder 按方法聚合 + 最慢调用 |
| `thread_state_breakdown` | 线程在窗口内 Running/Runnable/Sleeping/D 状态 + blocked_function |
| `cpu_top_threads` | 窗口内 CPU 占用（线程/进程/同名线程池 `group_by=thread_name`）；`thread_churn`：窗口内反复新建的线程 |
| `slice_hotspots` | 切片热点（总/最大/次数），含最长实例 ts/id |
| `frame_intervals` | 任意渲染线程的帧节奏（自动识别 eglSwapBuffers / doFrame / DrawFrame / queueBuffer）：fps、间隔分位数、卡顿与冻结区间 |
| `cpu_frequency` | 窗口内各 CPU / 大小核簇的时间加权频率（降频排查） |
| `android_logs` | logcat 检索（级别/tag/关键字/进程/窗口）+ 按 tag 计数 |
| `android_gc` | ART GC：按进程统计次数/耗时/回收量/堆范围，最长 GC 的状态拆分 |
| `memory_summary` | 系统内存压力：meminfo 起止/最值、vmstat 增量与速率（major fault、换入换出、直接回收）、LMK、直接回收卡顿线程、kswapd CPU、峰值 RSS 与增长最多的进程 |
| `process_memory` | 单进程内存：RSS 分量起止/峰值、24 点时间线、各 OOM-adj 档位时长、LMK、直接回收；同名多实例时列出并选窗口内最活跃的一个 |
| `io_summary` | IO：线程 D 状态（IO/非 IO）与内核阻塞函数；块设备请求延迟（issue/complete 按 设备+扇区 配对）、最慢请求；各进程页缓存读入量 |
| `compare_traces` | 两个 trace 对比（A/B/差值/百分比）：系统内存、启动、CPU、主线程状态、binder、GC、内存、帧；线程 CPU 与主线程切片按名字（数字归一化）对齐并按变化排序；`align=startup` 以各自第 N 次启动为窗口 |
| `android_anrs` | ANR 列表（超时来源）+ 每个 ANR 的 likely_blockers（分类+置信度）、stuck_binder（未返回调用的目标进程、binder 线程池忙/总数、忙线程卡在何处）、主线程状态/未结束切片/切片/binder/锁 |
| `lock_contention` | Java 锁竞争：按持锁方法聚合、规则结论（主线程/热点/binder reply 中/链）、最严重的竞争及背后的 binder 客户端 |
| `wakeup_chain` | 线程在等谁：按唤醒者汇总睡眠时间；最长几次睡眠沿唤醒链逐跳追溯（唤醒者 → 唤醒者的唤醒者…），直到"一直在干活"的源头线程；`critical_path`：窗口内真正卡住进度的线程（含其运行/排队/D 时间与执行的切片） |
| `event_latency` | 触发→响应耗时：每个锚点（logcat 文本 + 可选 tag，或 slice GLOB）之后各目标 slice 的首次出现（ms、时长、进程/线程）与汇总（hits/min/avg/max）；锚点来源按 tag 列出，混入 adbd 等时提示 |
| `main_thread_blocking_calls` | stdlib 精选阻塞调用（binder、锁、inflate、traversal、draw、AIDL handler…）：按进程或按调用类型汇总，默认只取主线程 |
| `analyze_bootchart` | 开机 bootchart（目录或 tgz，无需 trace）：bootloader 各阶段、里程碑（init→launcher）、阶段间隔及其中 CPU top 进程、逐秒 CPU/iowait/运行队列、磁盘读写、规则结论 |
| `boot_timeline` | 开机 trace：boot_progress 里程碑与间隔、关键区间、system_server 阶段（SystemServerTiming）、最慢服务启动、其他线程长任务；trace 起点晚时提示用 bootchart |
| `record_trace` | adb 设备上录制（内置 scenario 配置或本地 pbtxt，可改时长）→ 拉回 → 清理设备文件 → 加载并返回概览；进度通知、可取消 |
| `record_boot` | 重启设备并采集 bootchart + Perfetto 开机 trace，拉回、清理、调用 analyze_bootchart 与 boot_timeline（需 userdebug） |
| `java_heap` | Java 堆转储：堆/可达/native 大小、按保留大小与实例数的类、UI 对象、位图、协程积压；`baseline_trace_id` 对比类增长 |
| `server_stats` | 本服务的工具调用耗时（按工具汇总、最慢调用）与慢查询（TP 内执行 ≥200 ms 或等待 ≥1 s，附 SQL） |
| `profile_threads` | 设备上用 simpleperf 对指定线程采样（自身/累计热点函数，含内核线程；需 userdebug/root） |
| `cpu_profile` | `linux.perf` 调用栈采样热点：自身/累计（隐藏公共根帧）、最常见完整栈；按线程/进程/窗口过滤；未符号化帧输出 `lib.so+0x<rel_pc>` 便于 IDA 反查；C++ 名简化 demangle |

通用约定：
- 参数 schema 封闭（`additionalProperties:false` + 服务端校验），幻觉参数直接报错并列出可用参数；
- 数值参数接受字符串形式（LLM 常把数字写成字符串）；
- 窗口参数 `ts_start/ts_end`（绝对 ns）或 `startup_id`；进程参数 `upid/pid/process_name`（`*` 为 GLOB）；
- 输出为列式 JSON（columns + rows 数组），CJK 不转义；超过 2^53 的整数输出为字符串（JS 客户端精度）；
- SQL 错误分类提示；`no such table` 时根据 stdlib 索引直接给出要 INCLUDE 的模块。

## 5. 已验证（2026-10-23）

Windows 10（publish/win-x64）：

| 场景 | 结果 |
|---|---|
| 85 MB 启动 trace | 加载 2.75 s，8 次进度通知；概览含 2 次启动、8 项 perf 丢数据告警 |
| 158 MB 启动 trace | 加载 5.6 s（含概览 7 s）；6 次启动；各分析工具 < 1 s |
| `android_startups startup_id=1` | 相机冷启动 512 ms：activity_start 158 ms、binder 81 ms、inflate 64 ms；主线程 Running 64% |
| 3 s 超时笛卡尔积查询 | 3.05 s 返回超时错误，trace 变为非常驻；下一次查询自动重载（2.8 s）并成功 |
| 中文路径 `中文路径测试/启动_base.pftrace` | 加载成功 |
| 30 个单元/集成测试（含 review 回归测试） | 全部通过；测试后无残留 trace_processor 进程 |
| 950 MB trace（2026-10-27） | `load_trace` 37.9 s（解析 20.4 s），trace_processor 峰值内存 2.8 GB；其余工具 ≤ 3.1 s |

Ubuntu 18.04.6（publish/linux-x64 + 自编 trace_processor）：

| 场景 | 结果 |
|---|---|
| 全部 15 个工具冒烟测试 | 全部正常；85 MB trace 加载 1.05 s |
| 设置 http_proxy/https_proxy/all_proxy 后运行 | 正常（loopback 不走代理） |
| 2 s 超时递归查询 | 2.04 s 超时返回，下一次调用自动重载 |
| `kill -9` MCP 进程 | trace_processor 被 init 收养；下次启动日志 `reaping orphaned trace_processor`，已回收 |

代码 review 及修复记录见 [04-code-review.md](04-code-review.md)。

## 6. 后续可做

- 超大 trace（数 GB）：预热单个模块超过 60 s 会杀掉 trace_processor，重载后又会再次预热（应记住超时、不再预热）；
  trace_processor 内存约为文件大小的 3 倍，常驻上限按个数而不是按内存计算
- GitHub Actions：CI（Windows + Ubuntu 构建/测试）与 tag 触发的 Release
- HTTP/SSE 传输（AndroidMCP 有 `HttpSseTransport`，按需移植）

## 7. 迭代记录

各版本变化见根目录 [CHANGELOG.md](../CHANGELOG.md)。
