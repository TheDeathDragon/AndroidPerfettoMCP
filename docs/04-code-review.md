# 代码 Review 记录

方式：独立 reviewer（只给代码和关注点，不带实现者结论）通读改动，实现者同步自查，逐条核实后修复并补回归测试
（`tests/PerfettoMcp.Tests/ReviewRegressionTests.cs` 等）。

## 已确认的不变量

- 会话生命周期：对子进程的每次访问都经过会话锁（gate）；`Dispose` 取消 `lifetime`、释放锁、幂等；加载完成后再检查 disposed，
  已卸载会话返回 "was unloaded"；查询超时 / 客户端取消即杀子进程，下次使用时惰性重载。
- `/query` 流式读取并按字节封顶（`PERFETTO_MAX_RESULT_MB`）。
- protobuf 解码（packed/unpacked、`string_cells` NUL 分隔与空串、跨 batch 的行）、`SqlText.Literal` 引号转义与标识符白名单、
  `TimeWindow` 对 `dur=-1` 的裁剪。

## 仍存在的已知限制

| 问题 | 缓解 |
|---|---|
| Linux 下 MCP 被 SIGKILL 时子进程由 init 收养 | 下次启动时 `OrphanRegistry.Sweep` 回收；父 pid 恰好被复用、或 `PERFETTO_TP_PATH` 指向改名的二进制时可能漏回收 |
| 多个 `load_trace` 真正并发时常驻数可能短暂超限（检查与加载之间无全局锁） | 单客户端基本串行；下次加载/查询时回收 |
| `missing_services` 按固定前缀 "Waited one second for " 截取服务名；TTFD 规则在 30 s 窗口内找 `reportFullyDrawn` | 同进程 30 s 内再次启动且首次没有 reportFullyDrawn 时可能取到下一次的（罕见） |

## 发现并修复的问题

| 问题 | 修复 |
|---|---|
| SQLite 裸列只在单个 MIN/MAX 聚合时有定义，`slice_hotspots` 的最长实例取错 | `ROW_NUMBER() OVER (PARTITION BY name ORDER BY dur DESC)` |
| `TraceSession.Dispose` 拿锁不放、无 disposed 标记：卸载与加载并发时子进程泄漏或复活 | `disposed` + `lifetime` CTS，Dispose 释放锁，加载完成后再检查 |
| 退出时 `using` + ProcessExit 双重 Dispose，每个会话多等 5 s | `TraceManager.Dispose` 幂等，等待缩短为 2 s |
| stdin 关闭后仍等待在途加载 / 查询 | 传输层 EOF 时 `McpServer.CancelAll()` |
| 解析错误响应省略了 `"id": null` | `Id` 用 `JsonIgnoreCondition.Never` |
| > 2^53 的整数在 JS 客户端丢精度 | 超出安全整数范围输出为字符串（参数也接受数字字符串） |
| `/query` 整体缓冲，`SELECT *` 打爆内存 | 流式读取，超上限中止并重启 trace_processor |
| 端口竞争时连到别的进程 | `/status` 成功后再确认子进程仍存活，否则换端口 |
| `dur=-1`（trace 结束仍未结束）的状态被丢弃或算出负时长 | 裁剪统一处理为"到窗口结束" |
| 加载失败留下幽灵会话；启动超时抛原始 `TaskCanceledException` | 新会话加载失败即移除；统一报 "Loading ... exceeded" |
| 取消竞态、重复请求 id 覆盖他人的在途项 | `TryCancel` 吞 `ObjectDisposedException`；`TryAdd` 不覆盖 |
| `name_filter` 未转义 `%`/`_`；`min_dur_ms` 溢出 | `SqlText.EscapeLike` + `ESCAPE '\'`；范围校验 |
| 驱逐时不计入加载中的会话 | `TraceSession.IsLoading` 计入常驻数 |
| Windows 上 MCP 被强杀后子进程残留 | `ChildProcessGuard`：kill-on-close 作业对象 |
| `WaitWhileAsync` 超时也返回"已完成"，`record_boot` 卡住的设备分支走不到 | 设备端循环后再判一次：`done=` / `still=`，只有 `done=` 算完成 |
| `record_boot` 先删后检：pull 失败也删设备文件；perfetto 还在写就拉取 | 全部 pull 成功才删；超时先 `kill -INT` 再等 20 s，否则保留并报错 |
| adb 超时抛 `TimeoutException` | 改抛 `ToolException`，等待循环捕获后重试到截止 |
| `event_latency` 同 ts 锚点编号不稳定；未结束的目标 slice 算作瞬间完成；`target_process` 不认影子进程 | 按 `(ts, label)` 排序；`dur < 0` 时 `done_ms` 为 null；先用 `ProcessSelector.NameOf` 解析进程名 |
| `thread_churn.unnamed` 漏算跨线程改名（`task_rename` 记在改名者上） | 用事件参数 `pid`（被改名的线程） |
| 只有 `load_trace` 会让后到的无 trace_id 调用等待 | `record_trace` / `record_boot` / `unload_trace` 也登记（`record_*` 带 `load: false` 除外） |
| 守护进程：崩溃重启时新 worker 启动失败后不再重试 | 轮询发现 active 已退出时，退避 1 min 后按当前版本重启 |
| 守护进程：`JsonValue<T>` 类型初始化失败一次后永久失败，每次轮询泄漏一个 worker | 消息改为字符串拼接；失败的新 worker 被杀，1 min 内不重试；非 JSON 输出丢弃 |
| logcat 合并 zip 只按名字和时间复用；并发加载共用 `.tmp` | 核对 zip 内两个条目的文件名与大小；临时文件名带 GUID |
| 概览提示的 logcat 格式（`-v epoch`）trace_processor 不认 | 改为 `-v threadtime -v year` 或 `record_trace` logcat=auto |
| `process_cpu_pct` 按 sched 行解析进程名，长 trace 上很慢 | 先按 utid 汇总再解析 |
| 概览反复全表 GLOB 扫 `android_logs` | `MATERIALIZED` CTE 按 tag 预筛一次 |
| `AdbStream.DisposeAsync` 在 finally 里抛异常、`Kill(entireProcessTree)` 连带杀掉共用的 adb server | 不再抛出；只杀 adb 客户端进程 |
| `supervisor.log` 只在启动时轮转 | 每次写入后检查大小并轮转 |
