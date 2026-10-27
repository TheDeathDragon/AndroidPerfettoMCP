# 代码 Review 记录（2026-10-23）

方式：独立 reviewer（不带实现者结论，只给代码和关注点）通读 `src/PerfettoMcp/`，实现者同步自查，逐条核实后修复。
修复后：30 个测试全部通过（新增 7 个回归测试，见 `tests/PerfettoMcp.Tests/ReviewRegressionTests.cs`），
Windows / Ubuntu 18.04 冒烟测试通过，`dotnet format --verify-no-changes` 通过。

## 已修复

| # | 级别 | 问题 | 场景 | 修复 |
|---|---|---|---|---|
| 1 | 高 | `slice_hotspots` 的 `max_ts`/`max_slice_id` 可能取错 | SQLite 的"裸列取 MAX 所在行"仅在只有一个 MIN/MAX 聚合时成立，查询里同时有 `MAX(dur)` 和 `MIN(depth)` | 用 `ROW_NUMBER() OVER (PARTITION BY name ORDER BY dur DESC)` 取最长实例；列改名 `max_process`/`max_thread`；真实 trace 上与直接查 `slice` 表对比一致 |
| 2 | 高 | `TraceSession.Dispose` 拿锁不释放、无 disposed 标记 | `unload_trace` 与加载并发 → 加载完成后子进程泄漏；已解析到会话的调用可能复活子进程；Dispose 后 `EnsureLoadedAsync` 永久挂起 | 增加 `disposed` 标记 + `lifetime` CTS（Dispose 时取消在途加载/查询）；Dispose 释放锁；加载完成后再检查 disposed；已卸载会话返回 "was unloaded" |
| 3 | 中 | 退出时每个会话额外等 5 s | `using` + `ProcessExit` 双重 Dispose，第一次 Dispose 未释放锁 | `TraceManager.Dispose` 幂等；会话 Dispose 释放锁、等待缩短为 2 s |
| 4 | 中 | stdin 关闭后仍等待在途请求 | 客户端断开时正在加载（最长 900 s）或查询，进程和子进程滞留 | 传输层 EOF 时调用 `McpServer.CancelAll()` 取消所有在途请求 |
| 5 | 中 | 解析错误响应缺少 `"id": null` | `WhenWritingNull` 把 id 字段整个省略，严格客户端拒收 | `Id` 属性 `JsonIgnoreCondition.Never` |
| 6 | 中 | > 2^53 的整数在 JS 客户端精度丢失 | realtime clock 时间戳（~1.7e18）被四舍五入，回传 `ts_start` 后裁剪漂移 | 超出安全整数范围的值输出为字符串（参数本就接受数字字符串） |
| 7 | 中 | `/query` 响应整体缓冲 | `SELECT * FROM slice` 等超大结果在超时前就把 MCP 进程内存打爆 | 流式读取，超过 `PERFETTO_MAX_RESULT_MB`（默认 256）即中止并重启 trace_processor，提示改用聚合 |
| 8 | 中 | 端口竞争可能连到别人的 trace_processor | 探测空闲端口→释放→子进程绑定之间被占用：子进程绑定失败退出，但 `/status` 由外部进程应答 | `/status` 成功后等待 150 ms 再确认子进程仍存活，否则换端口重试 |
| 9 | 中 | `android_startups` 主线程状态未处理 `dur=-1` | trace 结束时仍未结束的状态被丢弃或算出负时长 | 裁剪与重叠判断加 `IIF(x.dur < 0, 窗口结束, ...)` |
| 10 | 低 | 加载阶段启动超时抛原始 `TaskCanceledException` | `StartAsync` 在 try 外 | 移入 try，统一给出 "Loading ... exceeded" |
| 11 | 低 | 加载失败留下"幽灵"会话 | 坏文件加载失败后仍出现在 `list_traces` | 新注册的会话加载失败时移除 |
| 13 | 低 | 取消竞态 / 重复 id | `Cancel()` 打到已 Dispose 的 CTS；重复请求 id 覆盖/误删他人的 inFlight 项 | `TryCancel` 吞 `ObjectDisposedException`；`TryAdd` 不覆盖 |
| 15 | 低 | `list_processes` 的 `name_filter` 未转义 `%`/`_` | 过滤含下划线的名字时匹配过宽 | `SqlText.EscapeLike` + `ESCAPE '\'` |
| 16 | 低 | `min_dur_ms` 溢出 | NaN / 极大值强转 long | 校验范围 0–1e9 |

## 后续迭代修复（迭代 1）

| # | 问题 | 修复 |
|---|---|---|
| 12 | `EvictForRoom` 不计入"加载中"的会话 | `TraceSession.IsLoading`，驱逐时把加载中的会话计入常驻数 |
| 14（Windows 部分） | MCP 被强杀后子进程残留 | `ChildProcessGuard`：子进程加入 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` 作业对象；实测强杀 MCP 后 trace_processor 1 → 0 |

## 仍存在的已知限制

| 问题 | 缓解 |
|---|---|
| Linux 下 MCP 被 SIGKILL 时子进程由 init 收养 | 下次启动时 `OrphanRegistry.Sweep` 回收（已在 18.04 实测）；父 pid 恰好被复用、或 `PERFETTO_TP_PATH` 指向改名的二进制时可能漏回收 |
| 多个 `load_trace` 真正并发时常驻数仍可能短暂超限（检查与加载之间无全局锁） | 单客户端场景基本串行；下次加载/查询时回收 |

## 确认无问题的部分

protobuf 解码（packed/unpacked、`string_cells` NUL 分隔与空串、跨 batch 的行）、`SqlText.Literal` 引号转义与标识符白名单、
`TimeWindow` 对 `dur=-1` 的裁剪、查询串行门与各 CTS 生命周期、超时杀进程 + 惰性重载流程。

## 2026-10-24 第二轮（0.6 新增代码，独立审查 agent）

范围：后台预热（TraceSession）、Adb / DeviceSteps、record_trace / record_boot、关键路径与堆路径 SQL、PerfLog。预热的锁、SQL 拼接、record_trace 清理：未发现问题。

| # | 问题 | 场景 | 修复 |
|---|---|---|---|
| 1 | `WaitWhileAsync` 超时也返回"已完成" | 设备端循环到上限时同样打印 `waited=$i`，返回值不会是 -1；`record_boot` 的"未开机完成"分支永远走不到，设备卡在开机/循环重启时会继续往下拉空数据或报莫名其妙的超时 | 设备端在循环后再判断一次条件：仍成立打印 `still=`、不成立打印 `done=`；只有 `done=` 算完成，否则重试到截止时间返回 -1 |
| 2 | `record_boot` 先删后检 | bootchart 四个文件 pull 不检查结果就 `rm -rf /data/bootchart/*`（一次 adb 抖动就丢唯一副本）；等待开机 trace 结束的结果被忽略，perfetto 还在写就 pull + 删；`analyze_bootchart` 抛异常会跳过开机 trace 的拉取和清理 | 每个 pull `Check`，全部成功后才删；等待超时先 `kill -INT` perfetto 再等 20 s，仍未结束则报错并保留设备文件；bootchart 分析异常记为文本继续执行 |
| 3 | adb 超时抛 `TimeoutException` | `WaitWhileAsync` 中 `wait-for-device` 超时直接逃出（文档说返回 -1）；MCP 层显示为异常类型名 | `Adb.RunAsync` 超时改抛 `ToolException`；`WaitWhileAsync` 捕获后重试直到截止 |

验证：`DeviceStepsTests` 用假 adb（Windows `.cmd` / Linux sh）覆盖 `done=` / `still=`；设备端脚本在 ums9230 上实跑（已开机 → `done=0`，恒真条件 → `still=2`）。

## 2026-10-24 第三轮（迭代 45–55 新增代码，自查）

范围：`event_latency`、健康检查分级 SQL、`thread_churn` / `group_by=thread_name`、`missing_services`、`REPORT_FULLY_DRAWN_EARLY`。

| # | 问题 | 场景 | 修复 |
|---|---|---|---|
| 1 | `event_latency` 锚点编号在同 ts 时不稳定 | 三个查询各自 `ROW_NUMBER() OVER (ORDER BY ts)`，同一时刻的多条日志编号可能互换 → `anchor_kind` 归错；`LEAD(ts)` 等于本身 ts 时窗口长度为 0 | 全部按 `(ts, label)` 排序；窗口截止取"ts 更大的下一个锚点" |
| 2 | 未结束的 slice（dur = -1）算作瞬间完成 | `done_ms` 用 `MAX(dur, 0)`，trace 结尾仍在进行的目标被当成"已完成"，拉低 `done_min_ms` | `dur < 0` 时 `dur_ms` / `done_ms` 为 null，汇总跳过 |
| 3 | `target_process` 不认影子进程 | 按 `process.name` 过滤，被拆进无名影子进程的线程上的目标 slice 被滤掉（迭代 44 同类问题） | 先用 `ProcessSelector.NameOf` 解析进程名再过滤 |
| 4 | `thread_churn.unnamed` 漏算跨线程改名 | `task_rename` 事件记在执行改名的线程上；`pthread_setname_np` / `Thread.setName` 改别的线程时 utid 是改名者（某 trace：814 次改名中 6 次跨线程） | 改用事件参数 `pid`（被改名的线程） |

未改：`missing_services` 的服务名截取（固定前缀 "Waited one second for "）、健康检查 SQL、TTFD 规则的 30 s 搜索窗口（同进程 30 s 内再启动且首次无 reportFullyDrawn 时可能取到下一次的，罕见）。

## 2026-10-26 第四轮（v0.7.0 以来全部改动，独立审查 agent + 自查）

范围：`git diff v0.7.0..HEAD`（86 个提交、44 个源码 / 测试 / 脚本文件）：概览新增各节、`process_churn` / `important_kills` / `vsync_consumers`、守护进程修复、按到达顺序等待 `load_trace`、主机侧 logcat 合并、`profile_threads`。审查 agent 给出 10 条（未验证），逐条核实后修 9 条、驳回 1 条。

| # | 问题 | 场景 | 处理 |
|---|---|---|---|
| 1 | 改变默认 trace 的调用只登记了 `load_trace` | 同批先 `record_trace`（默认加载）/ `record_boot` / `unload_trace`，再发不带 trace_id 的调用，后者仍可能查到旧 trace | 登记四个工具；`record_*` 带 `load: false` 时不登记（测试覆盖） |
| 2 | 崩溃重启时新 worker 启动失败后不再重试 | `OnWorkerExitedAsync` 忽略非 active worker 的退出，`SwapAsync` 失败后 `active` 仍指向已退出的旧 worker，轮询因版本未变不处理 → 直到 `/mcp` 重连都不可用 | 轮询发现 active 已退出时，在退避（1 min）后按当前版本重启 |
| 3 | logcat 合并 zip 只按 trace 名 + 时间判断复用 | 同一 trace 换一份更旧的 logcat 合并，返回旧 zip；并发加载时共用 `.tmp` 文件 | 复用前核对 zip 内两个条目的文件名与大小；临时文件名带 GUID |
| 4 | 概览提示让用户抓 `-v epoch` 格式 | trace_processor 只认 `-v threadtime -v year`，按提示抓的日志合并后仍为 0 行 | 提示改为 `record_trace`（logcat=auto）或 `load_trace` `logcat` + 正确格式 |
| 5 | `compare_traces` 的 `process_cpu_pct` 按 sched 行解析进程名 | 每行跑两个相关子查询，长 trace 上 O(行数 × 线程数) | 先按 utid 汇总 CPU，再每个线程解析一次 |
| 6 | 概览的 `process_churn` / `important_kills` 反复全表 GLOB 扫 `android_logs` | 日志多（合并 logcat、相机 HAL 刷屏）时每个进程 / 每条死亡事件各扫一遍 | 先用 `MATERIALIZED` CTE 按 tag 预筛一次；`important_kills` 先取前 10 条死亡再查杀手 |
| 7 | 不加载时也生成合并 zip；失败时主机侧 logcat 文件残留 | `load=false` 白白复制一份 trace；perfetto 启动 / 拉取失败后 `.logcat.txt` 留在输出目录 | zip 只在加载时生成；未拉到 trace 时删除 logcat 文件 |
| 8 | `AdbStream.DisposeAsync` 可能抛异常、杀掉 adb server | 在 `finally` 里抛出会跳过设备端清理并掩盖原错误；`Kill(entireProcessTree: true)` 可能连带客户端拉起的 adb server（共用 adb server 的另一台机器） | 不再抛出（记 debug 日志），只杀 adb 客户端进程 |
| 9 | `event_latency` 按锚点种类汇总时丢掉 0 命中的种类 | — | **驳回**：说明里"hits < anchors"指同一种锚点覆盖不同操作（电源键亮 / 灭屏），已能体现；不同种类不显示 0 命中是有意的（相机测试断言只列两次切换） |
| 10 | `supervisor.log` 只在启动时按大小轮转 | 守护进程连续运行多天，日志无限增长 | 每次写入后检查大小，超过即轮转为 `.1` 并重开 |

同时：
- 打开 `AnalysisLevel=latest-recommended` 和 `EnforceCodeStyleInBuild`。共报出 9 条（CA1859、CA1854、CA1001），已修改或注明理由抑制，现在编译 0 警告。
- 测试工程关闭 CA1861：断言里的内联数组可读性更好。
- 精简代码注释（74 处多行注释）：去掉具体设备 / 案例的叙述，只保留一两行关键原因，案例留在文档里。
