# 开源项目调研与吸收记录（2026-10-24）

调研对象（均读了源码，而非 README）：

| 项目 | 语言 / 许可 | 价值 |
|---|---|---|
| [Gracker/SmartPerfetto](https://github.com/Gracker/SmartPerfetto)（`backend/skills`，245 个 YAML skill） | TS / **AGPL-3.0** | 规则最丰富（ANR、锁、binder、启动慢原因、对比显著性）。**只借鉴思路与阈值，不复制 SQL** |
| [antarikshc/perfetto-mcp](https://github.com/antarikshc/perfetto-mcp) | Python / Apache-2.0 | ANR 根因阈值、binder overhead、锁竞争严重度 |
| [google/perfetto `ai/skills`](https://github.com/google/perfetto/tree/main/ai/skills/perfetto)、UI 插件 `com.google.PerfettoMcp` | Apache-2.0 | 内存泄漏工作流（dominator path）、"理论与实测分离"原则 |
| [tooluse-labs/perfetto-mcp-rs](https://github.com/tooluse-labs/perfetto-mcp-rs) | Rust / MIT+Apache | 错误分类提示（已在 v0.1 吸收） |
| [akhilpaulnp/tracely](https://github.com/akhilpaulnp/tracely) | — | 结果句柄分页、content + structuredContent 双输出 |

## 已吸收

| 来源 | 内容 | 落地位置 |
|---|---|---|
| SmartPerfetto `anr_matched.sql` | ANR 窗口 = `[anr_ts − timeout, anr_ts]`；超时取 trace 记录 → stdlib 默认 → 按类型回退（input 5 s、broadcast 10 s、service 20 s、FGS 30 s、bindApplication 15 s…），并输出 `timeout_source` | `android_anrs` |
| SmartPerfetto `anr_detail` / antarikshc `anr_root_cause` | 主线程时间分类为 binder / lock / IO / D / CPU busy / runnable 饥饿 / idle，带阈值与置信度（binder ≥500 ms high；lock ≥300 ms；IO ≥500 ms；Running ≥70%；Runnable ≥15/30%） | `android_anrs.likely_blockers` |
| SmartPerfetto 规则"10 s 内多个 ANR → 系统级问题" | `other_anrs_within_10s` | `android_anrs` |
| 自研（受 binder 阻塞链启发） | **未返回的 binder 调用**：`android_binder_txns` 只含已回复事务，卡住的调用要从 `binder transaction` 切片参数 `destination process` 取目标，再统计目标进程 binder 线程池忙/总数与忙线程卡在何处 | `android_anrs.stuck_binder` |
| SmartPerfetto `binder_root_cause` | `android.binder_breakdown` 的 `android_binder_client_server_breakdown` 按 reason 拆解最慢同步调用 | `android_binder_txns.slowest_breakdown` |
| antarikshc `binder_transaction_profiler` | `overhead_ms = client_dur − server_dur`（排队 / 线程池饥饿） | `android_binder_txns.slowest` |
| SmartPerfetto `lock_contention_analysis` | LOCK_MAIN_THREAD（>5 次且 >100 ms）、LOCK_HOTSPOT（>20 次或 >5 个等待者）、LOCK_IN_BINDER_REPLY（>3 次且 >50 ms）、LOCK_CHAIN | `lock_contention.findings` |
| SmartPerfetto `comparisonSignificance` | 变化需同时过绝对下限（5 ms / 1 MB / 1 次）与 5% 相对阈值（百分比只看 1 个百分点）；按指标方向判定 improved / regressed / same | `compare_traces.verdict` |
| SmartPerfetto `memory_growth_detector` | 增长 ≥100 MB 或 ≥50%（且 ≥5 MB）critical；≥50 MB 或 ≥20% warning | `memory_summary.growth_rating` |
| Perfetto Trace Doctor | `__intrinsic_trace_diagnostics`（抓取配置问题及修正建议） | `load_trace.capture_diagnostics` |

验证：官方 ANR 样例（`ref_proj/testdata/android_anr.pftrace.gz`）一次调用得出完整链路——videos 的 BIND_APPLICATION ANR ← 主线程同步 binder 调用 gms.persistent 挂起 14.9 s ← gms.persistent 16/16 binder 线程全忙 ← 全部卡在 `auth_account_base-ChimeraGetToken-IAuthManagerService`。已固化为 RealTraceTests。

## 待做（按价值排序）

1. ~~唤醒链~~（迭代 10 已完成 `wakeup_chain`；迭代 34 接入关键路径 `_thread_executing_span_critical_path` 作为 `critical_path` 段）
   - 坑：`waker_utid` 记录在睡眠**之后**的第一个 R/R+ 行上，不在 S/D 行上；需 `JOIN thread_state n ON n.utid=s.utid AND n.ts=s.ts+s.dur AND n.state IN ('R','R+')`。
   - 多跳：waker 自身上一次被唤醒，最多 6 跳；或 stdlib `_critical_path_stack`（内部 API，v58.2 有，可能变动）。
   - 锁竞争要从**持锁方**侧关联（`blocking_utid = 链上线程`）。
2. ~~启动慢原因规则集~~（迭代 11 已完成，见 `StartupRules.cs`）（SmartPerfetto `startup_slow_reasons` SR 规则）：JIT 编译数、ContentProvider 数、类校验、dlopen、WebView、inflate、fsync、同期其他进程 fork、峰值频率等阈值。
3. ~~系统冻结判定~~（迭代 12 已完成，`android_anrs.system_check`）（ANR）：system_server 与多个 App 主线程同时 stalled（R/R+/D ≥50% 存活时间）→ system_freeze / system_server_freeze。
4. ~~`android.critical_blocking_calls`~~（迭代 14 已完成 `main_thread_blocking_calls`；注意该表含所有线程）：精选主线程阻塞切片清单，可并入启动 / ANR 主线程拆解。
5. ~~binder 风暴~~（迭代 15 已完成，`android_binder_txns.storms`）：每客户端 100 ms 桶内 >50 次事务。
6. ~~SQL 错误时返回真实列名与最近似名~~（迭代 13 已完成）；~~结果摘要化~~（迭代 17 已完成）—（`__intrinsic_stdlib_objects`）；结果摘要化（列统计 + 样本行）进一步省 token。
7. ~~IO 阻塞函数族~~（迭代 13 已完成）用 GLOB 分类（`*erofs* *f2fs* *folio_wait* *blk_* *ufshcd* *dm_*`），避免 `LIKE '%dm_%'` 误匹配 `dma_fence_wait`。
8. ~~堆转储泄漏~~（迭代 28 `java_heap`：跨 dump 类增长、UI 对象实例数、位图、协程积压；迭代 39 `class_name` 给出到 GC 根的最短引用路径——支配树对多根可达对象只给出虚拟根，故用 root_distance 逐跳回溯；Perfetto 堆转储无基本类型字段值，无法读 `mDestroyed`）
9. ~~证据边界~~（迭代 16 已完成）输出中为每个候选原因附 `root_cause_boundary` / `next_evidence_needed`，防止 LLM 过度断言。
10. **解析/查询性能优化**（迭代 29 起：`server_stats` 记录工具耗时与慢查询（执行/等待分开）、概览 `timing`；`android_binder_txns` 20.3 s → 2.9 s）。
    158 MB 开机 trace 实测（串行、热 TP）：加载 5.2 s；概览 2.35 s（首查询 trace_bounds 1.1 s、首次触碰 heap_graph 0.7 s，均为一次性）；
    迭代 30：memory_summary 每进程部分改为直接读 mem.* 计数器、一次查询（5.3 s → 1.86 s）。
    剩余：main_thread_blocking_calls（stdlib critical_blocking_calls 精选清单，首次 ~3 s；不重写以免与 stdlib 规则脱节）、android.binder 0.75 s、
    迭代 31：process_memory / compare_traces 也改为直接读计数器（共用 `MemCountersCte`），代码中已不再使用 linux.memory.process；process_memory 3.5 s → 1.3 s。
    迭代 32：加载后**空闲时后台预热** critical_blocking_calls / binder / monitor_contention（INCLUDE 时物化，之后同一 TP 缓存）：main_thread_blocking_calls 首次 4.3 s → 1.3 s。
    剩余：by_call 的正则分组 1.3 s、binder breakdown 1.1 s 属查询本身；加载 5 s/158 MB 为 trace_processor 解析。
11. ~~真机采集工具~~（迭代 25 `record_trace`、迭代 26 `record_boot` 已完成）：`record_trace`（adb push config → perfetto → pull，自动处理 MSYS 路径/超时）、
    `collect_bootchart`、`record_boot_trace`；场景通用配置 `configs/scenario.pbtxt`。
12. ~~`boot_timeline`~~（迭代 24 已完成）：从开机 trace 提取 `boot_progress_*` 事件、SystemServerTiming 切片、`sf_stop_bootanim` 等，
    与 bootchart 结论互补（trace 起点晚于 zygote 时提示改用 bootchart）。

## 0.6.0 之后

- 0.7.0（2026-10-24，迭代 34–50）：健康检查按影响分级（`affects`/`idx`/`impact`）、`missing_services`、`cpu_top_threads` 的 `group_by=thread_name` 与 `thread_churn`、影子/占位进程按线程归属、`compare_traces` 关键路径对比、`frame_intervals` 的 `freeze_summary`、相机操作采集脚本；Windows 与 Ubuntu 18.04 回归通过。

- 待做清单 1–12 已完成（8 无 dominator path，10 剩余为 trace_processor 解析本身）。后续方向：按新的分析报告继续加规则。
