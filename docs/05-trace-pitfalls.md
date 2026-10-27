# Trace 数据陷阱与分析口径

迭代中在真实 trace 上踩到的、会直接导致**结论错误**的数据问题，以及工具里的处理方式。

## 1. 影子进程：同一线程被拆成两个 utid

**现象**（某热成像 App 的 trace）：`Thread-4`（tid 10343）、`Thread-6`（tid 10387）各有两条 thread 记录：

| utid | tid | upid | 所属进程 | sched CPU |
|---|---|---|---|---|
| 4 | 10343 | 3 | com.example.thermal | 7 ms |
| 129 | 10343 | 47 | **pid=10343、无名进程** | 21355 ms |

trace_processor 在不知道线程 tgid 时会建一个 pid == tid、没有名字的"影子进程"，线程的大部分 sched 数据挂在影子上。
**按进程名统计时这两个最关键的线程几乎"消失"**（rs 版 MCP 同样受影响）。共发现 11 个影子进程。

**处理**（`Tools/Selectors.cs`）：
- `ProcessSelector.Predicate`：按进程筛选时，自动纳入"pid 等于本进程某线程 tid、且无 name/cmdline"的进程；
- `OwnerUpidExpr` / `NameExpr`：影子进程映射回真实进程（用于分组与显示）；
- 线程类输出按 `tid` 合并（`utids` 列列出合并的 utid）；主线程判断只看真实进程；
- `thread_state_breakdown`：旧 utid 的最后一个状态会一直延伸到 trace 结束，与新 utid 叠加导致 >100%；按 utid 出现时间分段，旧 utid 截止到新 utid 第一个状态的时刻。

## 2. CPU 频率：必须按时间加权

cpufreq 计数器在**每次变频时**产生一个采样点，短暂降频会产生大量采样。对采样点直接 `AVG(freq)` 会严重低估：

| 簇 / 窗口 | 采样点平均 | 时间加权 |
|---|---|---|
| 小核 平时 / 冻结期 | 1492 / 1833 MHz | **1998 / 1999 MHz** |
| 大核 平时 / 冻结期 | 2382 / 2489 MHz | **2781 / 2787 MHz** |

一份人工分析报告中"小核 1.92 GHz vs 平时 1.60 GHz"即为采样点平均口径；
按时间加权 CPU 几乎全程满频——"冻结期未降频、属纯算力打满"的结论依然成立且更强，但该组数字应修订。
`cpu_frequency` 工具只输出时间加权结果，并在工具描述中提示模型。

## 3. perf 采样

- kernel 态采样通常没有用户栈（`callsite_id` 为空），该 trace 中 42543 个采样有 32924 个无栈；`cpu_profile` 单独计数；
- 未符号化帧 `name` 为空串，需用 `mapping + rel_pc` 定位（`lib.so+0x<rel_pc>`，可直接用于 IDA）；
- `perf_cpu_lost_records` 等 data_loss 告警意味着采样计数只能作相对指标（`load_trace` 的 `health_issues` 会列出，按 CPU 折叠并给出丢失占比；某 MTK trace 为 1.1%，可忽略）；
- 累计（inclusive）热点里线程入口、ART invoke stub 等根帧接近 100%，无信息量，已隐藏（`common_root_frames_hidden`）。

## 4. 内存

- **抓取工具自身的内存开销**：一份 4 GB 内存机型的启动 trace（UMS9230E，MemAvailable 仅 1.4–1.6 GB）中 `traced_perf` 常驻 529 MB（期间 +127 MB）、
  `traced` +161 MB（256 MB ring buffer 逐渐写满），合计约 690 MB；同期 kswapd0 占 8.8 s CPU、换出 1140 页/s、3 次 LMK。
  在小内存设备上同时开 `linux.perf` 调用栈采样和大 buffer 会显著放大内存压力，**对比启动耗时时建议另抓一份不带 perf、buffer 较小的 trace**。
- **同名多进程实例**：按进程名会匹配到重启前后的多个 upid（例：Settings 旧实例 upid 709 在冷启动前 1.2 s 结束、新实例 upid 991）。
  混在一起统计会出现 oom_adj 时间超过窗口等错误；`process_memory` 列出全部实例并默认分析窗口内内存采样最多的一个。
- vmstat 计数器（pgmajfault、pswpin/out、pgscan/pgsteal_direct）是累计值，必须取窗口起止差值；meminfo 是瞬时值。
- 进程被杀后 RSS 计数器归零，`process_memory` 的 `end` 为 0 表示进程已退出（对照 `lmk_kills`）。

## 5. IO

- 块设备请求只以原始 ftrace 事件存在（`ftrace_event`：block_rq_issue/complete，参数 dev/sector/nr_sector/rwbs/comm），
  需按 (dev, sector) 用 `LEAD` 配对得到延迟；dev 用 `linux.block_io` 的 `linux_device_major_id/minor_id` 解码（8:0 = 主 UFS，7:x = loop/APEX）。
- 内核线程在 trace 中各自是一个"进程"（父进程 kthreadd，pid 2），`msleep`/`kthread` 等 D 状态是常驻休眠，会淹没真实 IO 等待，默认需排除。
- `traced_perf` 的 stack-unwinding 线程会读取大量 ELF 文件（同一 trace 启动窗口内 D-IO 502 ms、页缓存 7.8 MB），与被测 App 争抢 IO —— 与第 4 节内存开销同理，测启动耗时时应对照无 perf 的 trace。

## 6. binder / ANR

- 客户端 `waitForService` 等一个永远不会注册的服务时，libbinder 每秒打一行 `Waited one second for <name>` 并重试；servicemanager 每次都新建线程去 `ctl.interface_start`（lazy 服务机制），init 拒绝。表现为 servicemanager 的匿名短命线程（`thread_churn`）与 init/servicemanager 常驻 CPU；`load_trace` 的 `missing_services` 会列出。
- `android_binder_txns` 只包含**已回复**的事务；ANR 时卡住的调用（slice dur = -1）不在其中。目标要从 `binder transaction` 切片参数
  `destination process` / `destination thread` 取（键名含空格，`EXTRACT_ARG(arg_set_id, 'destination process')`）。
- 目标进程 binder 线程池是否耗尽：统计其 `binder*` 线程在调用时刻是否有未结束的深度 0 `binder reply` 切片（线程名大小写不一，用 `LOWER(name) GLOB 'binder*'`）。
- 唤醒者（waker_utid）记录在睡眠之后的第一个 R/R+ 行，不在 S/D 行（详见 06 待做 1）。

## 7. 启动

- `android_startup_time_to_display` 的 TTFD 不会小于 TTID：App 在首帧前调用 `reportFullyDrawn()` 时 TTFD 被钳为 TTID。看到 TTFD == TTID 先查 `reportFullyDrawn*` slice 的时刻（`android_startups` 规则 `REPORT_FULLY_DRAWN_EARLY`）。
- ART 打开 dex 时输出 `location=<odex|vdex> status=<..> filter=<..> reason=<..>` 切片：`filter=verify`（只有 vdex）意味着启动路径解释执行 + JIT。
  实测某机型的 **Settings（/system_ext/priv-app）为 verify**，是系统集成方可直接改进的点（`dex_preopt { compiler_filter: "speed-profile" }` + profile）；
  `location=error status=io-error-no-oat filter=run-from-apk` 表示某个 APK/jar 没有 oat。
- 类加载切片以类描述符命名（`Lxxx/yyy;`），用 `GLOB 'L*;'` 计数；不要用 `*oat*` 之类宽泛模式（会匹配 `ExtractFloatResult`）。
- "同期启动的进程"要区分 zygote 子进程（真正的 App 竞争）与 shell 子进程（测试脚本，如 sh 循环执行 `date` 90 次）。

- `android.critical_blocking_calls` 的 `_android_critical_blocking_calls` **包含进程所有线程**（Settings 启动期 274 条分布在 19 个线程，合计 1328 ms），
  不是只含主线程；按主线程统计需 `utid IN (tid = pid 的线程)`（主线程 binder 235 ms）。

- ART 的 `location=... status=... filter=...` 切片在**没有 oat 时 location 写 `error`**（`status=io-error-no-oat filter=run-from-apk`），真实 dex 路径只在父切片 `OpenDexFilesFromOat(<path>)` 里；App 进程还会打开 `<uses-library>` 共享库（即使已在 BOOTCLASSPATH），不能把这些行都当成 App 自身代码。
- 启动窗口内 installd CPU 不一定是 dex2oat/dexopt：设置首页存储摘要 → `StorageStatsManager.getFreeBytes` → installd `getUserSize`（逐 App 统计，即使已开 quota 也要 500 ms 级）。看 installd 的 `AIDL::*::server` 切片名区分。

- **`am start -W` / TTID 不等于相机可用**：相机预览首帧在 HAL `Cam3Stream::previewFirstFrameTag` / cameraserver `Stream 0: first full buffer`，ums9230 上比 TTID 晚约 550 ms；相机场景要用这两个切片计时。
- `android_monitor_contention` 的持锁方法是持锁线程**当时**所在的方法：`Unsafe.park` / `Object.wait` 说明它在同步块里等别的东西（`Object.wait` 只释放被 wait 的那个监视器）；`BinderProxy.transactNative` 说明持锁做 IPC。

- FrameTimeline `jank_type = 'Buffer Stuffing'` 的帧 `present_type` 多为 On-time Present：App 提前排队、按时上屏，**不是可见掉帧**；可见掉帧看 `present_type IN ('Late Present','Dropped Frame')`。滑动场景里 Buffer Stuffing 常占 70%+，直接按 jank_type != 'None' 统计会严重夸大。
- 一帧"慢"的常见归因顺序：管线编译（`CreateGraphicsPipeline-CompileAfterCacheMiss`）→ 主线程 D/Runnable → 主线程等 RenderThread（sync draw）→ 主线程自身工作；`android_jank_frames.worst_visible_causes` 按这个顺序给出 likely。

- 亮屏时 App 帧的 `waitForBufferRelease` 几百 ms 不是 App 的问题：SF 在 `setPowerMode(ON)` 期间不释放缓冲区；FrameTimeline 可能标 `PowerModeChange in progress`，也可能只标 App Deadline Missed，要看与 `setPowerMode(*)` 的时间重叠。
- 亮屏耗时口径：WindowManager `Receive Power Key` → SF `setPowerMode(ON)` 结束 ≈ 屏幕点亮；`Unblocked screen on after N ms` 只是锁屏内容画好，不是屏亮。

- 开机 trace 默认没有 FrameTimeline（旧 boottrace 配置），`android_jank_frames` 为空时用 `frame_intervals` / doFrame 切片；新配置已加。
- 内存"低"要看 MemAvailable，不要看 MemFree：页缓存会被回收。厂商守护进程按 MemFree 判断会误报（如 ums9230 的 `ummd`）。

- Perfetto Java 堆转储（`android.java_hprof`）**没有基本类型字段值**（`heap_graph_primitive` 为空）：读不到 `Activity.mDestroyed`、Bitmap 宽高；泄漏判断靠实例数与跨 dump 增长。
- 堆增长要看**可达**对象（`reachable_obj_count`），总对象数含未回收垃圾，两次 dump 间可以差 60%。
- 判断泄漏至少要 3 个 dump：基线 → N 次 → 2N 次；第一轮的增长常是懒加载和缓存/日志环形缓冲填充。

- stdlib 模块（如 `android.binder_breakdown`、`linux.memory.process`、`android.critical_blocking_calls`）首次使用时物化全量，WHERE 过滤不会下推；大 trace 上首次调用可能十几秒，之后缓存。

- 唤醒链（谁唤醒了我）≠ 关键路径（谁的执行决定了我何时能继续）：GPU 驱动线程（mali-compiler）完成编译后由别的线程转交唤醒，唤醒链看不到它；`wakeup_chain` 的 `critical_path` 段能看到。驱动线程通常没有 trace 切片，只能看到运行时间。

- 关键路径（stdlib `_thread_executing_span_critical_path`）对"窗口结束时仍在睡、要很久以后才被唤醒"的线程会沿那次（无关的）唤醒回溯，给出误导性的阻塞线程。ANR 尤其如此：主线程被唤醒往往是 ANR 处理本身。工具里在这种睡眠开始处截断并写 `note`。

- 关键路径适合短窗口（一帧、一次启动、ANR 前的主线程）；在开机这类长且高负载的窗口里会在多个进程间跳转、被稀释，此时更有用的信号是路径上阻塞线程的 Runnable 时间（CPU 饥饿）。

- 启动关键路径上的 SurfaceFlinger `TimerDispatch`（vsync 分发）不是阻塞者：主线程在等下一帧，说明启动跨多帧（启动窗口/转场动画），应看帧而不是某个线程。
- C# 侧读 JSON 数值：`JsonValue.GetValue<T>()` 类型严格（int 节点读 long 会抛异常），用 TryGetValue 依次尝试 double/long/int。

- 找"谁持有对象"不要只看支配树：能从多个 GC 根到达的对象，直接支配者是虚拟根（`idom_id` 为空、depth 1）。用 `root_distance` 逐跳找距离小 1 的引用者，得到到根的最短路径（`java_heap class_name`）。

- 按进程名统计主线程状态时，同名的已退出实例最后一条 Z/X 状态 dur=-1，会"覆盖"之后的任何窗口；统计前排除 Z/X/x（或只取窗口内在运行的实例）。
- 主线程 Sleeping 时长没有好坏方向：可能是空闲，也可能在等 binder/锁；窗口长度不同（按启动对齐）时绝对值也不可比。

- 带 `linux.perf` 的 trace 里，一个线程常被拆成"真实 utid + 影子进程里的 utid"。影子那份只有零星状态（甚至一条横跨 3 s 的 Runnable）；按 utid 选线程要把同 tid 的影子一并取上，并像 thread_state_breakdown 那样把旧 utid 截到新 utid 开始处，否则会重复计数。
- 自建 GL 线程 + SurfaceView 的 App（相机/热成像预览）没有 FrameTimeline，也拿不到 HWUI RenderThread 的优先级提升：用 `frame_intervals`（eglSwapBuffers）看帧，看 `freeze_summary.render_thread_nice`。

- 跨 trace 按线程名对齐时，binder 线程名是 `binder:<pid>_<十六进制序号>`（如 `_1E`、`_A`），只把数字替换成 N 会留下字母，同类线程对不上；按 `binder:N_N` 整体归一化。

- 除 pid==tid 的影子进程外，trace_processor 还会把不知道 tgid 的线程放进一个无名占位进程（pid 4294901760 = 0xFFFF0000），里面混着多个真实进程的线程；按进程映射会整体归错，要按线程（同 tid 的有名进程）归属。

- stdlib 的 `is_main_thread`（binder、thread_slice）对影子进程同样为真（影子 pid = 线程 tid）：按"进程 + 影子"汇总再取主线程时，会把被拆出去的工作线程当主线程。主线程只认有名进程里的。
- 抓取失败的 trace（如 ftrace 部分没生效，只剩 FrameTimeline）照样能加载；先看概览 counts，`sched_slices = 0` 时 CPU / 线程状态类工具全是空。

## 8. 其他

- 用 `event_latency` 量启动：`START u0`（ActivityTaskManager）同时匹配启动 App 与回桌面（HOME intent），按 `anchor_kind` 分组看；目标 `Choreographer#doFrame*` 必须配 `target_process`，否则先命中 system_server 的 `android.anim` 动画帧。`START u0` 日志比 `android_startups` 的起点（launch intent）晚约 20 ms。
- adbd 会把收到的 shell 命令原文写进 logcat（`adbd service requested 'shell,v2,...,raw:<命令>'`）：用脚本里 `log -t X "op ..."` 做标记时，按文本搜会多出一条更早（~58 ms）的 adbd 行；按 tag 过滤（`event_latency` 的 `anchor_tag`）。
- `stats` 里的 error / data_loss 影响面差别很大，`load_trace` 的 `health_issues.affects` 已分类：`perf_*`、`symbolization_*` 只影响 cpu_profile；`systrace_parse_failure` 只丢少量 slice；`android_log_*` 只影响日志；`config_write_into_file_no_flush` 是配置建议（未丢数据）；真正影响调度/线程状态的是 `ftrace_cpu_has_data_loss`（idx 为 CPU 号）与 `*overrun*`，以及 `traced_flushes_failed`（结尾缺数据）。实测一份启动 trace 的 CPU 6、7 有 ftrace 丢失，该 trace 上大核的结论要核对时间线。
- 线程名来自 comm：没调用 `prctl(PR_SET_NAME)` / `Thread.setName` 的 native 线程会一直沿用**创建者线程的名字**（`ps` 里也一样）。所以"同名线程很多"可能是同一个线程反复 `pthread_create` 匿名线程，不是线程池 worker；用 `task_rename` 事件区分（`cpu_top_threads` 的 `thread_churn.unnamed`）。`thread.start_ts` 只对 trace 中看到 `task_newtask` 的线程非空，可据此判断"窗口内新建"。
- MTK trace 普遍有上千条 `systrace_parse_failure`（stats 中 severity=error）：厂商自定义格式的 atrace 行解析不了，通常 < 1% 的 print 事件，只丢少量 slice/counter，调度数据不受影响；不要因此判定 trace 不可靠。真正要警惕的是 ftrace `*overrun*`（某些 CPU/时段有空洞）和 `traced_flushes_failed`（结尾缺数据）。
- 自建 GL/SurfaceView 渲染线程不在 FrameTimeline 覆盖范围内，用 `frame_intervals`（`eglSwapBuffers` 间隔）分析帧率与冻结；
- `cpu` 表的 `cluster_id` 在部分 MTK 设备上全为 0，按各 CPU 观测到的最高频率划分大小核簇；
- `sched.priority` 为内核优先级：100–139 为 CFS（nice = prio − 120），< 100 为实时。
- Linux load average 计入 D 状态：常驻 `TASK_UNINTERRUPTIBLE` 睡眠的内核线程（ums9230 上 15 个）会让空闲设备 loadavg≈15。判断设备是否空闲看 CPU idle 或 trace 中的 Running 时间，不要看 loadavg。
