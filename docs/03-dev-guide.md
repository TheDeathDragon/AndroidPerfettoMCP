# 开发 / 测试 / 发布指南

## 常用命令

```bash
# 构建 + 测试（Windows 下测试会用 tools/win-x64 的 trace_processor 跑集成测试）
dotnet build PerfettoMcp.slnx
dotnet test  PerfettoMcp.slnx

# 发布（self-contained 单文件，trace_processor 放在 tools/ 下）
publish.bat win-x64          # -> publish/win-x64/perfetto-mcp.exe + tools/trace_processor_shell.exe
publish.bat linux-x64        # -> publish/linux-x64/perfetto-mcp     + tools/trace_processor_shell
./publish.sh linux-x64       # Linux/macOS 上同上

# 端到端冒烟测试（走真实 MCP stdio 协议，逐个调用工具并打印耗时）
python -I tests/e2e/smoke_test.py <perfetto-mcp 可执行文件绝对路径> <trace1> [<trace2>] [--calls calls.json] [--show 800]
```

`--calls` 文件格式：`[["tool_name", {参数}], ...]`，可在参数里用 `"trace_id": "t2"` 指定第二个 trace。
`lab/tests/` 存在时测试工程会一并编译其中的测试（私有 trace 上的回归测试，不在本仓库）。

安装、MCP 客户端配置、环境变量与命令行参数见 [README](../README.zh.md)。trace_processor 查找顺序：`$PERFETTO_TP_PATH` →
`<exe目录>/tools/` → `PATH`。

## CI 与发布

- `.github/workflows/ci.yml`：push / PR 到 main 时在 Windows 与 Ubuntu 上 `dotnet format --verify-no-changes`、
  `dotnet build -warnaserror`、`dotnet test`。
- `.github/workflows/release.yml`：推送 tag `v<版本>` 时在两个平台测试并打包 win-x64 zip / linux-x64 tar.gz，
  以 CHANGELOG 中 `## [<版本>]` 一节为说明创建 GitHub Release。
- 发版步骤：改 csproj `<Version>`，把 CHANGELOG 的 `[Unreleased]` 改为 `## [<版本>]`，提交后 `git tag v<版本> && git push origin v<版本>`。
- 安装脚本不带参数时从最新 Release 下载安装；带 zip / tar.gz / 目录参数时安装本地包（开发时的热更新即用此方式）。

## 更新已安装的服务（热更新）

```bat
publish.bat win-x64
powershell -File scripts\install.ps1 publish\win-x64
```

守护进程约 2 秒内切换到新版本并通知客户端刷新工具列表，无需 `/mcp` 重连；已加载的 trace_id 保留。
只有 `Supervisor/` 本身的改动需要用户 `/mcp` 重连（外壳 exe 在下次启动时才生效）。
验证整个机制：`python -I tests/e2e/reload_test.py <publish 目录> <trace>`。

## 升级 Perfetto 版本

1. 在装有 Docker 的 x86_64 Linux 构建机上：`bash scripts/build-tp.sh v<新版本> <工作目录> 16`（详见 [01](01-trace-processor-ubuntu1804.md)），检查输出 `MAX GLIBC ≤ 2.27`；
2. 替换 `tools/linux-x64/trace_processor_shell`，下载官方 `https://commondatastorage.googleapis.com/perfetto-luci-artifacts/v<版本>/windows-amd64/trace_processor_shell.exe` 替换 `tools/win-x64/`；
3. 重新生成 stdlib 索引（sparse checkout 只拉 stdlib 目录，用完删除）：
   ```bash
   T=$(mktemp -d) && git clone -q --depth 1 --branch v<版本> --filter=blob:none --sparse https://github.com/google/perfetto.git "$T/pf"
   git -C "$T/pf" sparse-checkout set src/trace_processor/perfetto_sql/stdlib
   python -I scripts/gen_stdlib_index.py "$T/pf/src/trace_processor/perfetto_sql/stdlib" > src/PerfettoMcp/Resources/stdlib_index.tsv
   rm -rf "$T"
   ```
4. 跑 `dotnet test` 和 `smoke_test.py`；领域工具里用到的 stdlib 表/列如有变更需同步修改（`Tools/AndroidTools.cs`、`SchedTools.cs`）。

## 开发约定（沿用 AndroidMCP）

- stdout 只用于 JSON-RPC，日志一律走 `Logging/Log`（stderr，UTF-8）；
- 代码与提交不写中文，文档可以中文；注释只写"为什么"；
- `private static readonly` / `const` 用 PascalCase，实例字段 camelCase（`.editorconfig`）；
- 新工具：继承 `TraceToolBase`，用 `SchemaBuilder` 声明参数（自动封闭 schema），在 `Program.cs` 注册；
  预期内的失败抛 `ToolException`（原样返回给 LLM），SQL 错误由 `TableFormat.ThrowIfError` 抛 `SqlQueryException`；
- 拼 SQL 时字符串一律 `SqlText.Literal`，标识符/GLOB 用 `SqlText.Identifier` 校验；
- 代码风格与静态检查：`.editorconfig`（格式、命名、`var`、using 位置等）+ 两个工程的 `AnalysisLevel=latest-recommended`、
  `EnforceCodeStyleInBuild=true`，要求编译 0 警告；提交前跑 `dotnet format PerfettoMcp.slnx`（或 `--verify-no-changes` 检查）；
- 注释从简：只在关键处写一两行"为什么"，具体设备上的案例、数据写进 docs/，不写进代码。

## 开发过程踩坑

| 问题 | 解决 |
|---|---|
| C# 内插原始字符串 `$"""` 里写 `{{...}}` 报 CS9006 | 改写文案避免花括号，或用 `$$"""` |
| 测试里 `new JsonObject{["upid"]=7}` 构造的值 `TryGetValue<long>` 失败（JsonValue 只按原类型转换） | 数值统一取 JSON 文本再 `long.TryParse`（`Args.NumericText`） |
| 测试工程引用 internal 枚举作 Theory 参数报 CS0051 | 参数改为 string |
| stderr 日志在 Windows 默认是控制台码页（GBK），客户端按 UTF-8 读出错 | `Log` 改用 UTF-8 StreamWriter 包装 stderr |
| PowerShell 里 `cmd /c publish.bat` 找不到文件 | 用 `& .\publish.bat` |
| Python `subprocess.Popen` 用 Git Bash 风格相对路径找不到 exe | 传 `cygpath -w` 后的绝对路径 |
| Ubuntu 18.04 自带 Python 3.6：`subprocess.Popen(text=True)` / `run(capture_output=True)` 报错 | 改用 `universal_newlines=True`、`stdout=PIPE, stderr=PIPE` |
| Windows PowerShell 5.1 + `$ErrorActionPreference='Stop'` 把原生程序写 stderr 当异常 | `--version` 改为输出到 stdout |
| reload_test 在 Linux 把 `versions/<id>/tools/trace_processor_shell` 也算成工作进程 | 按可执行文件名过滤 |

## Linux 验证流程（Ubuntu 18.04）

```bash
# 构建机
publish.bat linux-x64            # 或 ./publish.sh linux-x64
# 把 publish/perfetto-mcp-<版本>-linux-x64.tar.gz、tests/e2e/ 下的脚本与清单、测试 trace 拷到 18.04 机器，然后：
tar -xzf perfetto-mcp-*-linux-x64.tar.gz && chmod +x linux-x64/perfetto-mcp linux-x64/tools/trace_processor_shell
python3 -I smoke_test.py $PWD/linux-x64/perfetto-mcp $PWD/<trace>
# 回归清单（多 trace，按加载顺序 t1/t2/t3 引用；v07 不含 record_*）：
python3 -I smoke_test.py --calls smoke_calls_v07.json $PWD/linux-x64/perfetto-mcp <idle trace> <camera ops trace> <cold start trace>
```

- Git Bash 的 `tar -czf "$TEMP/x.tar.gz"`（`C:\...` 路径）报 `Cannot connect to C: resolve failed`：GNU tar 把 `C:` 当远程主机，加 `--force-local`（或在 PowerShell 里用 Windows 自带 tar）。
- Windows 打的 tar.gz **不带可执行位**：解压后必须 `chmod +x linux-x64/perfetto-mcp linux-x64/tools/trace_processor_shell`（install.sh 已处理），否则 `Permission denied`。
- 验证机若与 Windows 共用同一个 adb server，在上面跑含 `record_trace` / `record_boot` 的清单会真的录制 / **重启设备**。

## 真机录制（record_trace）

- **录制期间不在设备上轮询**：每次 `adb shell` / 设备端 `sleep` 都会 fork、唤醒 adbd，在静置 trace 里能占全系统 CPU 的三成。
  录制期间在主机端等待，到时长后才在设备上短轮询等文件落盘；`record_boot` 按 perfetto 启动时刻 + 时长在主机端等，
  等 `boot_completed` 的轮询为 3 s 一次。
- 需要 `adb` 在 PATH（或设置 `PERFETTO_MCP_ADB`）；多设备时传 `serial`。
- 配置推到 `/data/misc/perfetto-configs/`（perfetto 读不了 /data/local/tmp），输出在 `/data/misc/perfetto-traces/`，拉回后删除。
- 参数通过 `ProcessStartInfo.ArgumentList` 传给 adb，没有本机 shell，不受 Git Bash 路径改写影响。
- 内置配置来自仓库 `configs/scenario.pbtxt`（EmbeddedResource `config.scenario.pbtxt`），改配置后重新发布即可。
- `config_text` 可直接传一整份 TraceConfig 文本（由调用方按问题组合数据源 / ftrace 事件 / atrace 类别），与 `config` 二选一。
- **user 版没有 logcat**：Perfetto 的 android.log 只支持 userdebug（SELinux 拒绝 traced_probes 连 `/dev/socket/logdr`）。
  `logcat=auto`（默认）时，若设备是 user 版且配置要了 android.log，录制期间在主机侧跑
  `adb logcat -b all -v threadtime -v year -T 1`，结束后与 trace 打成 `<trace>+logcat.zip` 加载（trace_processor 会按 trace 的
  时钟快照把 logcat 时间换算到同一时间线）。手工抓的同格式日志可用 `load_trace` 的 `logcat` 参数合并。
- `profile_threads`：设备上用 simpleperf 采指定线程（进程 / 线程名前缀 / 内核线程 / tid）的调用栈；需要 root
  （userdebug），会临时把 `kptr_restrict` 设为 0 并在结束后恢复，数据文件用完即删。

## 性能排查

- `server_stats`：按工具汇总耗时、最慢调用、慢查询（`exec_ms` = trace_processor 内执行，`wait_ms` = 等会话锁 / 懒重载 / RPC）。
- **测量要串行**：同一 trace 只有一个 trace_processor，同一条消息里并行发的多个工具会在会话锁上排队，工具耗时里大部分是等待。
- 热更新后 worker 换新，trace 懒重载：先发一条 `SELECT 1` 预热（158 MB 约 5 s），再 `server_stats clear=true`，再测。
- stdlib 模块第一次 INCLUDE 时会物化整表（与过滤条件无关）：`android.binder_breakdown` 在 158 MB trace 上 19.5 s；之后同一 TP 内是缓存（1 ms）。工具里优先用按 utid/时间窗口的直接查询代替"物化全量再过滤"。
- `load_trace` 概览的 `timing` 给出加载秒数与概览各步耗时。
- SQL 写法：避免"每个点一个相关子查询去查 CTE"——SQLite 不物化 CTE，每次都重算；改用区间连接（每个采样带 `LEAD` 得到的结束时间）一次算完。
- 拼接 SQL 片段时注意 CTE 重名（`duplicate WITH table name`）：公共 CTE 取不易冲突的名字（`mc`、`fs`），并在真实 trace 测试里覆盖每个 section。
- 预热（TraceSession.PrewarmAsync）：加载完成后后台按顺序 INCLUDE 重模块；每个模块前要求 1.5 s 无用户查询、`gateWaiters == 0` 且 `gate.Wait(0)` 成功，所以用户查询最多被正在执行的那一个模块耽误；加载代数变化即放弃。环境变量 `PERFETTO_MCP_PREWARM=0` 关闭。
- 热更新后立即发的请求可能还由旧 worker 处理（切换约 2 s），验证新功能前先等几秒或发两次。
