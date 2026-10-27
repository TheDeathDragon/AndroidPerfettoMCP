# Trace 数据陷阱与分析口径

在真实 trace 上遇到的、会直接导致**结论错误**的数据问题，以及工具里的处理方式。

## 1. 线程 / 进程归属

**影子进程**：trace_processor 不知道线程 tgid 时，会建一个 pid == tid、没有名字的"影子进程"，线程的大部分 sched 数据挂在影子上：

| utid | tid | upid | 所属进程 | sched CPU |
|---|---|---|---|---|
| 4 | 10343 | 3 | com.example.thermal | 7 ms |
| 129 | 10343 | 47 | **pid=10343、无名进程** | 21355 ms |

按进程名统计时最关键的线程几乎"消失"。带 `linux.perf` 的 trace 尤其常见。处理（`Tools/Selectors.cs`）：
- `ProcessSelector.Predicate`：按进程筛选时自动纳入"pid 等于本进程某线程 tid、且无 name/cmdline"的进程；
- `OwnerUpidExpr` / `NameExpr`：影子映射回真实进程；线程类输出按 `tid` 合并（`utids` 列）；
- 按 utid 选线程时把同 tid 的影子一并取上，旧 utid 的最后一个状态会延伸到 trace 结束，截到新 utid 开始处，否则 >100%。

**占位进程**：不知道 tgid 的线程还可能被放进无名进程 pid 4294901760（0xFFFF0000），里面混着多个真实进程的线程；
要按线程（同 tid 的有名进程）归属，不能按进程映射。

**主线程**：stdlib 的 `is_main_thread` 对影子进程同样为真（影子 pid = 线程 tid），会把被拆出去的工作线程当主线程；主线程只认有名进程里的。

**同名多实例**：按进程名会匹配到重启前后的多个 upid，混在一起会出现 oom_adj 时间超过窗口等错误；已退出实例最后一条 Z/X 状态
dur = -1，会"覆盖"之后的任何窗口。统计前排除 Z/X/x 或只取窗口内在运行的实例（`process_memory` 列出全部实例，默认取窗口内最活跃的一个）。

**线程名**：来自 comm。没调用 `prctl(PR_SET_NAME)` / `Thread.setName` 的 native 线程沿用**创建者线程的名字**，"同名线程很多"
可能是反复 `pthread_create` 的匿名线程而非线程池；用 `task_rename` 区分（`cpu_top_threads` 的 `thread_churn.unnamed`）。
`thread.start_ts` 只对 trace 中看到 `task_newtask` 的线程非空，可据此判断"窗口内新建"。跨 trace 按名字对齐时，binder 线程名
`binder:<pid>_<十六进制序号>` 要整体归一化为 `binder:N_N`。

## 2. CPU

- **频率必须按时间加权**：cpufreq 计数器在每次变频时采样，短暂降频产生大量采样点，直接 `AVG(freq)` 严重低估：

  | 簇 / 窗口 | 采样点平均 | 时间加权 |
  |---|---|---|
  | 小核 平时 / 冻结期 | 1492 / 1833 MHz | **1998 / 1999 MHz** |
  | 大核 平时 / 冻结期 | 2382 / 2489 MHz | **2781 / 2787 MHz** |

  `cpu_frequency` 只输出时间加权结果。
- `cpu` 表的 `cluster_id` 在部分 MTK 设备上全为 0，按各 CPU 观测到的最高频率划分大小核簇。
- `sched.priority` 为内核优先级：100–139 为 CFS（nice = prio − 120），< 100 为实时。
- Linux load average 计入 D 状态：常驻 `TASK_UNINTERRUPTIBLE` 的内核线程会让空闲设备 loadavg 很高；判断是否空闲看 CPU idle / Running 时间。

## 3. perf 采样

- 内核态采样通常没有用户栈（`callsite_id` 为空，可占大多数）；`cpu_profile` 单独计数。
- 未符号化帧 `name` 为空串，用 `mapping + rel_pc` 定位（`lib.so+0x<rel_pc>`，可直接用于 IDA）。
- `perf_cpu_lost_records` 等 data_loss 意味着采样计数只能作相对指标。
- 累计热点里线程入口、ART invoke stub 等根帧接近 100%，无信息量，已隐藏（`common_root_frames_hidden`）。

## 4. 内存

- **抓取工具自身的开销**：小内存设备上 `traced_perf` 可常驻 500 MB+，`traced` 随 ring buffer 写满再涨百余 MB，足以引发 kswapd 与 LMK；
  `traced_perf` 的栈回溯线程还会读大量 ELF、与被测 App 争 IO。**对比启动耗时时另抓一份不带 perf、buffer 较小的 trace**。
- 内存"低"看 MemAvailable 而不是 MemFree（页缓存可回收）；厂商守护进程按 MemFree 判断会误报（如 ums9230 的 `ummd`）。
- vmstat 计数器（pgmajfault、pswpin/out、pgscan/pgsteal_direct）是累计值，取窗口起止差值；meminfo 是瞬时值。
- 进程被杀后 RSS 计数器归零：`process_memory` 的 `end` 为 0 表示进程已退出（对照 `lmk_kills`）。

## 5. IO

- 块设备请求只以原始 ftrace 事件存在（`block_rq_issue/complete`，参数 dev/sector/nr_sector/rwbs/comm），按 (dev, sector) 用 `LEAD`
  配对得到延迟；dev 用 `linux.block_io` 的 `linux_device_major_id/minor_id` 解码（8:0 = 主存储，7:x = loop/APEX）。
- 内核线程各自是一个"进程"（父进程 kthreadd，pid 2），`msleep` / `kthread` 等 D 状态是常驻休眠，会淹没真实 IO 等待，默认排除。

## 6. binder / ANR / 锁

- `android_binder_txns` 只含**已回复**的事务；ANR 时卡住的调用（dur = -1）不在其中。目标从 `binder transaction` 切片参数
  `destination process` / `destination thread` 取（键名含空格：`EXTRACT_ARG(arg_set_id, 'destination process')`）。
- 目标进程 binder 线程池是否耗尽：统计其 `binder*` 线程（用 `LOWER(name) GLOB 'binder*'`）在调用时刻是否有未结束的深度 0 `binder reply`。
- 客户端 `waitForService` 一个永远不会注册的服务时，libbinder 每秒打一行 `Waited one second for <name>`，servicemanager 每次新建线程去
  `ctl.interface_start`；表现为 servicemanager 的匿名短命线程与常驻 CPU。`load_trace` 的 `missing_services` 会列出。
- `android_monitor_contention` 的持锁方法是持锁线程**当时**所在的方法：`Unsafe.park` / `Object.wait` 说明它在同步块里等别的东西
  （`Object.wait` 只释放被 wait 的那个监视器）；`BinderProxy.transactNative` 说明持锁做 IPC。

## 7. 唤醒链与关键路径

- 唤醒者（`waker_utid`）记录在睡眠**之后**的第一个 R/R+ 行，不在 S/D 行：`JOIN thread_state n ON n.utid = s.utid AND n.ts = s.ts + s.dur`。
- 唤醒链（谁唤醒了我）≠ 关键路径（谁的执行决定了我何时能继续）：GPU 驱动线程编译完成后由别的线程转交唤醒，唤醒链看不到它，
  关键路径能看到。驱动线程通常没有切片，只能看到运行时间。
- 关键路径（stdlib `_thread_executing_span_critical_path`）对"窗口结束时仍在睡、很久以后才被唤醒"的线程会沿那次无关的唤醒回溯；
  ANR 尤其如此（主线程被唤醒往往是 ANR 处理本身）。工具在这种睡眠开始处截断并写 `note`。
- 关键路径适合短窗口（一帧、一次启动、ANR 前）；开机这类长且高负载的窗口里会被稀释，更有用的是路径上阻塞线程的 Runnable 时间。
- 启动关键路径上的 SurfaceFlinger `TimerDispatch`（vsync 分发）不是阻塞者：说明启动跨多帧，应看帧。

## 8. 启动

- TTFD 不会小于 TTID：App 在首帧前调用 `reportFullyDrawn()` 时 TTFD 被钳为 TTID；看到 TTFD == TTID 先查 `reportFullyDrawn*`
  切片时刻（规则 `REPORT_FULLY_DRAWN_EARLY`）。
- ART 打开 dex 输出 `location=<odex|vdex> status=<..> filter=<..>` 切片：`filter=verify`（只有 vdex）意味着启动路径解释执行 + JIT；
  没有 oat 时 location 写 `error`（`status=io-error-no-oat filter=run-from-apk`），真实 dex 路径只在父切片 `OpenDexFilesFromOat(<path>)`。
  App 还会打开 `<uses-library>` 共享库（即使已在 BOOTCLASSPATH），不能都算成 App 自身代码。预置 App 为 verify 时可用
  `dex_preopt { compiler_filter: "speed-profile" }` + profile 改进。
- 类加载切片以类描述符命名（`Lxxx/yyy;`），用 `GLOB 'L*;'` 计数；不要用 `*oat*` 之类宽泛模式（会匹配 `ExtractFloatResult`）。
- "同期启动的进程"区分 zygote 子进程（真正的 App 竞争）与 shell 子进程（测试脚本）。
- `_android_critical_blocking_calls` **包含进程所有线程**，按主线程统计需限定 `tid = pid` 的线程。
- 启动窗口内 installd CPU 不一定是 dexopt：如设置首页存储摘要 → `getUserSize`（逐 App 统计，500 ms 级）；看 installd 的 `AIDL::*::server` 切片名。
- **相机**：`am start -W` / TTID 不等于相机可用，预览首帧在 HAL `previewFirstFrameTag` / cameraserver `Stream 0: first full buffer`，可晚数百 ms。
- 用 `event_latency` 量启动：`START u0` 同时匹配启动 App 与回桌面，按 `anchor_kind` 分组；目标 `Choreographer#doFrame*` 必须配
  `target_process`，否则先命中 system_server 的动画帧。`START u0` 比 launch intent 晚约 20 ms。

## 9. 帧与显示

- FrameTimeline `jank_type = 'Buffer Stuffing'` 的帧多为 On-time Present：**不是可见掉帧**；可见掉帧看
  `present_type IN ('Late Present','Dropped Frame')`。滑动场景 Buffer Stuffing 常占 70%+，按 `jank_type != 'None'` 统计会严重夸大。
- 一帧"慢"的归因顺序：管线编译（`CreateGraphicsPipeline-CompileAfterCacheMiss`）→ 主线程 D/Runnable → 主线程等 RenderThread →
  主线程自身工作（`android_jank_frames.worst_visible_causes`）。
- 亮屏时 App 帧 `waitForBufferRelease` 几百 ms 不是 App 的问题：SF 在 `setPowerMode(ON)` 期间不释放缓冲区，看与 `setPowerMode(*)` 的重叠。
  亮屏耗时口径：WindowManager `Receive Power Key` → SF `setPowerMode(ON)` 结束；`Unblocked screen on after N ms` 只是锁屏内容画好。
- 自建 GL 线程 + SurfaceView 的 App 没有 FrameTimeline，也拿不到 RenderThread 的优先级提升：用 `frame_intervals`（eglSwapBuffers），
  看 `freeze_summary.render_thread_nice`。
- 开机 trace 若没开 FrameTimeline，`android_jank_frames` 为空，用 `frame_intervals` / doFrame 切片。

## 10. Java 堆转储

- `android.java_hprof` **没有基本类型字段值**：读不到 `Activity.mDestroyed`、Bitmap 宽高；泄漏判断靠实例数与跨 dump 增长。
- 增长看**可达**对象（`reachable_obj_count`）；总对象数含未回收垃圾，两次 dump 间可差 60%。
- 至少 3 个 dump：基线 → N 次 → 2N 次；第一轮增长常是懒加载与缓存 / 日志环形缓冲填充。
- 找持有者不要只看支配树：多个 GC 根可达的对象直接支配者是虚拟根；用 `root_distance` 逐跳找距离小 1 的引用者（`java_heap class_name`）。

## 11. trace 健康与其他

- `stats` 里的 error / data_loss 影响面差别很大（`health_issues.affects` 已分类）：`perf_*`、`symbolization_*` 只影响 cpu_profile；
  `systrace_parse_failure` 只丢少量 slice（MTK 厂商 atrace 格式常有上千条，通常 < 1%）；`android_log_*` 只影响日志；
  `config_write_into_file_no_flush` 是配置建议。真正影响调度/线程状态的是 `ftrace_cpu_has_data_loss`（idx 为 CPU 号）、`*overrun*`
  与 `traced_flushes_failed`（结尾缺数据）。
- 主线程 Sleeping 时长没有好坏方向（可能空闲，也可能在等 binder / 锁），窗口长度不同时绝对值也不可比；`compare_traces` 不判方向。
- 抓取失败的 trace（如只剩 FrameTimeline）照样能加载；先看概览 counts，`sched_slices = 0` 时 CPU / 线程状态类工具全是空。
- stdlib 模块（`android.binder_breakdown`、`android.critical_blocking_calls` 等）首次使用时物化全量，WHERE 不会下推；大 trace 上首次十几秒，之后缓存。
- adbd 会把收到的 shell 命令原文写进 logcat：脚本里 `log -t X "op ..."` 打的标记按文本搜会多出一条更早的 adbd 行；按 tag 过滤
  （`event_latency` 的 `anchor_tag`）。
