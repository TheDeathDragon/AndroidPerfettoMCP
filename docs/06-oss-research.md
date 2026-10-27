# 开源项目调研与吸收

调研对象（均读了源码，而非 README）：

| 项目 | 语言 / 许可 | 价值 |
|---|---|---|
| [Gracker/SmartPerfetto](https://github.com/Gracker/SmartPerfetto)（`backend/skills`，245 个 YAML skill） | TS / **AGPL-3.0** | 规则最丰富（ANR、锁、binder、启动慢原因、对比显著性）。**只借鉴思路与阈值，不复制 SQL** |
| [antarikshc/perfetto-mcp](https://github.com/antarikshc/perfetto-mcp) | Python / Apache-2.0 | ANR 根因阈值、binder overhead、锁竞争严重度 |
| [google/perfetto `ai/skills`](https://github.com/google/perfetto/tree/main/ai/skills/perfetto)、UI 插件 `com.google.PerfettoMcp` | Apache-2.0 | 内存泄漏工作流（dominator path）、"理论与实测分离"原则 |
| [tooluse-labs/perfetto-mcp-rs](https://github.com/tooluse-labs/perfetto-mcp-rs) | Rust / MIT+Apache | 错误分类提示 |
| [akhilpaulnp/tracely](https://github.com/akhilpaulnp/tracely) | — | 结果句柄分页、content + structuredContent 双输出 |

## 已吸收

| 来源 | 内容 | 落地位置 |
|---|---|---|
| SmartPerfetto `anr_matched.sql` | ANR 窗口 = `[anr_ts − timeout, anr_ts]`；超时取 trace 记录 → stdlib 默认 → 按类型回退（input 5 s、broadcast 10 s、service 20 s、FGS 30 s、bindApplication 15 s…），输出 `timeout_source` | `android_anrs` |
| SmartPerfetto `anr_detail` / antarikshc `anr_root_cause` | 主线程时间分类为 binder / lock / IO / D / CPU busy / runnable 饥饿 / idle，带阈值与置信度（binder ≥500 ms high；lock ≥300 ms；IO ≥500 ms；Running ≥70%；Runnable ≥15/30%） | `android_anrs.likely_blockers` |
| SmartPerfetto"10 s 内多个 ANR → 系统级问题" | `other_anrs_within_10s`；system_server 与多个 App 主线程同时 stalled → `system_check` | `android_anrs` |
| 自研（受 binder 阻塞链启发） | 未返回的 binder 调用：从 `binder transaction` 切片参数 `destination process` 取目标，统计目标进程 binder 线程池忙/总数与忙线程卡在何处 | `android_anrs.stuck_binder` |
| SmartPerfetto `binder_root_cause` / antarikshc `binder_transaction_profiler` | 最慢同步调用按 reason 拆解；`overhead_ms = client_dur − server_dur`；binder 风暴（每客户端 100 ms 桶 >50 次） | `android_binder_txns` |
| SmartPerfetto `lock_contention_analysis` | LOCK_MAIN_THREAD（>5 次且 >100 ms）、LOCK_HOTSPOT（>20 次或 >5 个等待者）、LOCK_IN_BINDER_REPLY（>3 次且 >50 ms）、LOCK_CHAIN | `lock_contention.findings` |
| SmartPerfetto `startup_slow_reasons` | JIT、ContentProvider、类校验、dlopen、WebView、inflate、fsync、同期 fork、峰值频率等阈值 | `StartupRules.cs` |
| SmartPerfetto `comparisonSignificance` | 变化需同时过绝对下限（5 ms / 1 MB / 1 次）与 5% 相对阈值（百分比只看 1 个百分点）；按指标方向判定 improved / regressed / same | `compare_traces.verdict` |
| SmartPerfetto `memory_growth_detector` | 增长 ≥100 MB 或 ≥50%（且 ≥5 MB）critical；≥50 MB 或 ≥20% warning | `memory_summary.growth_rating` |
| Perfetto Trace Doctor | `__intrinsic_trace_diagnostics`（抓取配置问题及修正建议） | `load_trace.capture_diagnostics` |
| Perfetto ai/skills | 每个候选原因附 `proves` / `next_evidence`，防止过度断言；堆转储到 GC 根的引用路径（支配树对多根可达对象只给虚拟根，改用 root_distance 逐跳回溯） | `android_anrs`、启动 / 锁 findings、`java_heap` |

验证：Perfetto 官方 ANR 样例（`test/data/android_anr.pftrace.gz`）一次调用得出完整链路——BIND_APPLICATION ANR ← 主线程同步
binder 调用挂起 14.9 s ← 目标进程 16/16 binder 线程全忙 ← 全部卡在同一个 AIDL 方法。

实现要点：唤醒者记录在睡眠**之后**的第一个 R/R+ 行上（见 05）；锁竞争要从持锁方侧关联；IO 阻塞函数用 GLOB 分族
（`*erofs* *f2fs* *folio_wait* *blk_* *ufshcd* *dm_*`），避免 `LIKE '%dm_%'` 误匹配 `dma_fence_wait`。
