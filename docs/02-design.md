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
skills/perfetto-analysis/       Claude Code skill：分场景分析流程、抓取指南、数据陷阱（随发布包分发）
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

## 3.1 热更新（守护进程模式）

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

验证：`tests/e2e/reload_test.py`（安装 → 加载 → 重装触发切换 → 句柄恢复并惰性重载 → 坏版本（启动即退出）不泄漏、只报一次、旧进程照常服务 → 杀工作进程触发自愈 → 干净退出）。

注意：一次 `JsonValue<T>` 类型初始化失败会在整个进程内永久生效（此后每次 `JsonValue.Create` 都抛 `TypeInitializationException`），
所以守护进程的 JSON-RPC 消息一律用字符串拼接（`JsonEncodedText` 转义）。运行中进程的 CLR 异常可用 ETW 抓，无需重启：
`logman start clrex -p Microsoft-Windows-DotNETRuntime 0x8000 4 -ets -o x.etl` → 复现 → `logman stop clrex -ets` → `tracerpt x.etl -of XML`。

## 4. 工具约定

工具列表见 [README](../README.zh.md#工具列表)。所有工具遵循：
- 参数 schema 封闭（`additionalProperties:false` + 服务端校验），幻觉参数直接报错并列出可用参数；
- 数值参数接受字符串形式（LLM 常把数字写成字符串）；
- 窗口参数 `ts_start/ts_end`（绝对 ns）或 `startup_id`；进程参数 `upid/pid/process_name`（`*` 为 GLOB）；
- 输出为列式 JSON（columns + rows 数组），CJK 不转义；超过 2^53 的整数输出为字符串（JS 客户端精度）；
- SQL 错误分类提示；`no such table` 时根据 stdlib 索引直接给出要 INCLUDE 的模块。

## 5. 容量参考

950 MB trace：`load_trace` 37.9 s（解析 20.4 s），trace_processor 峰值内存 2.8 GB（约为文件大小的 3 倍），其余工具 ≤ 3.1 s。
stdlib 重模块首次 INCLUDE 的耗时随数据量超线性增长（同一模块 166 MB 1.6 s → 950 MB 15 s）。

## 6. 后续可做

- 超大 trace（数 GB）：预热单个模块超过 60 s 会杀掉 trace_processor，重载后又会再次预热（应记住超时、不再预热）；
  trace_processor 内存约为文件大小的 3 倍，常驻上限按个数而不是按内存计算
- HTTP/SSE 传输（AndroidMCP 有 `HttpSseTransport`，按需移植）
- 用 skill 实测时（各场景交给独立代理分析真实 trace）发现的工具问题：
  - 所有工具输出都带上实际使用的 `trace_id`：多个代理共用一个服务时，默认 trace 会被别人的 `load_trace` 切走；
  - `load_trace` 概览：trace 边界远大于数据实际覆盖的时间（零星的早 / 晚时间戳）时给出提示；
  - `compare_traces`：名字里带每次运行细节的嵌套切片（`resynced to N`）产生误导性差值；`system.kswapd_cpu_ms` 无调度时应为 0
    而不是 null；`app.sync_binder_ms` 拆分主线程与后台线程；
  - 启动规则 `DEX2OAT` 在 installd 并非编译（如 `getUserSize`）时仍用该名字报 critical；
  - ftrace 丢事件的健康提示只给 CPU 号，应说明是否落在被分析的窗口内；
  - `android_binder_txns` 的 `slowest_breakdown` 取全进程最慢的调用，主线程的调用常被后台线程挤掉。
