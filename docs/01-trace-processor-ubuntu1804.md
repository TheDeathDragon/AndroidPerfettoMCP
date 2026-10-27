# trace_processor_shell 在 Ubuntu 18.04 上的兼容与自编译

## 1. 问题

`perfetto-mcp-rs` 在 Ubuntu 18.04 上无法使用。

根因 **不是 Rust**：perfetto-mcp-rs 的 Linux 发布物是 `x86_64-unknown-linux-musl` 静态二进制，18.04 上能跑。
跑不起来的是它下载的 `trace_processor_shell`（固定 v54.0）。

## 2. 官方预编译包的 glibc 需求（实测）

检测方法（Windows Git Bash 即可，不需要 objdump）：

```bash
curl -sSfL -o tp https://commondatastorage.googleapis.com/perfetto-luci-artifacts/v54.0/linux-amd64/trace_processor_shell
grep -aoE 'GLIBC_2\.[0-9]+' tp | sort -t. -k2,2n -u | tail -1
```

| 版本 | 最高 GLIBC | 18.04 (glibc 2.27) |
|---|---|---|
| v30.0 | 2.14 | ✅ |
| v35.0 ~ v47.0 | 2.23 | ✅ |
| v48.0 / v48.1 / v49 / v51 / v54 | 2.34 | ❌ |

原因：Perfetto 的 LUCI 发布构建（`infra/luci/recipes/perfetto.py`）直接用构建机系统 glibc 链接，不用 sysroot；
v48 起构建机升级，最低 glibc 变成 2.34。代码本身没有依赖新 glibc API。

相关上游讨论：
- https://github.com/google/perfetto/issues/937
- https://issues.chromium.org/issues/383999365 （Google 建议自行从源码编译）

## 3. 自编译方案（已验证）

思路：Perfetto 自带 hermetic clang + 自带 libc++（`use_custom_libcxx` / `custom_libcxx_is_static` 在 Linux hermetic 下默认 true，静态链接），
唯一外部依赖只剩 glibc；把 GN 的 `sysroot` 指向一份 **Ubuntu 18.04 rootfs**，产物就只依赖 18.04 的 glibc。

脚本：`scripts/build-tp.sh`

```bash
# 用法: build-tp.sh [perfetto_tag] [work_dir] [jobs]
bash scripts/build-tp.sh v58.2 /root/tpbuild 16
```

脚本步骤：
1. `git clone --depth 1 --branch <tag>`（带重试，`http.version=HTTP/1.1`）
2. `tools/install-build-deps --no-dev-tools`（带重试；只校验 clang + `buildtools/open_csd` 存在）
3. `docker create ubuntu:18.04` → `apt install libc6-dev linux-libc-dev libgcc-7-dev` → `docker export | tar -x --anchored --exclude=...` 得到 sysroot，再把绝对路径软链改成相对路径
4. `tools/gn gen out/linux-bionic --args='is_clang=true is_debug=false monolithic_binaries=true enable_perfetto_etm_importer=false skip_buildtools_check=true target_os="linux" target_cpu="x64" sysroot="<sysroot>"'`
5. `tools/ninja -C out/linux-bionic trace_processor_shell`
6. `objdump -T` 输出最高 GLIBC 版本

### 结果

| 项 | 值 |
|---|---|
| 版本 | v58.2（2026-08-24 发布，当时最新 GitHub Release；v58.3/v59.0 只有 tag 无 Release） |
| clang | Perfetto 自带 llvm 19 |
| 编译 | 2392 步，16 并发约 10 分钟（64 核机器） |
| 产物 | `out/linux-bionic/stripped/trace_processor_shell`，14.9 MB |
| 最高 GLIBC | **2.23** |
| sha256 | `aa75f660f039e9e7b242e51dff066dc98a03e713e7bfb92e47cea68dac4d3849` |

产物向后兼容更高版本的 glibc（已在 Debian 13 / glibc 2.41 上运行）。

### Windows 版

`tools/win-x64/trace_processor_shell.exe` 为官方预编译包，未修改：
`https://commondatastorage.googleapis.com/perfetto-luci-artifacts/v58.2/windows-amd64/trace_processor_shell.exe`，
sha256 `adfa6bad3d72be3ba9b83fa2b17b69fa13b3ab1cad0f42e52b86188bd5f0f997`。

## 4. 踩坑记录

| 现象 | 原因 | 解决 |
|---|---|---|
| `git clone` 报 `GnuTLS recv error (-9)` / `early EOF` | 代理不稳定 | clone 加重试，`git -c http.version=HTTP/1.1` |
| `install-build-deps` 在 pip 处失败 `SSLEOFError` | 默认会建 python venv 装 grpcio/pandas 等开发工具 | 加 `--no-dev-tools`（C++ 构建不需要） |
| `install-build-deps` 最后卡在下载 ~1.5 GB `test/data` 并失败 | v58 的脚本无条件同步测试数据 | 只要工具链齐全就继续；GN 加 `skip_buildtools_check=true` 跳过构建时依赖校验（`gn/perfetto_check_build_deps.gni`） |
| `fatal error: 'sys/cdefs.h' file not found` | `tar --exclude=sys` 未锚定，误删 `usr/include/x86_64-linux-gnu/sys/` | `tar --anchored` |
| 依赖下载源全是 Google 域名 | 部分网络需代理 | `export http_proxy=... https_proxy=... all_proxy=...` 后再跑脚本；Docker 也要配代理 |

### CPU 指令集要求

Perfetto 默认编译参数含 `-mavx2 -mbmi -mbmi2 -mpopcnt -mlzcnt -msse4.2`，**运行机器 CPU 必须支持 AVX2**。
检查：`grep -qw avx2 /proc/cpuinfo && echo ok`。若需支持老 CPU，需要在 GN 里关掉这些（未验证）。

## 5. 验证命令（在 18.04 上）

```bash
./trace_processor_shell --version
# Perfetto v58.2-add693d8b ... Trace Processor RPC API version: 14

# 直接查询 + stdlib
./trace_processor_shell -Q "INCLUDE PERFETTO MODULE android.startup.startups; \
  SELECT startup_id, package, startup_type, dur/1e6 AS dur_ms FROM android_startups ORDER BY dur DESC LIMIT 5;" trace.pftrace

# HTTP RPC 模式（MCP 使用的模式）
./trace_processor_shell -D --http-port 19101 trace.pb &
curl -s --noproxy '*' -o st.bin -w '%{http_code}\n' http://127.0.0.1:19101/status
printf '\x0a\x1aSELECT COUNT(*) FROM slice' > q.bin        # QueryArgs{sql_query}: 0x0a + 长度 + SQL
curl -s --noproxy '*' --data-binary @q.bin -o r.bin http://127.0.0.1:19101/query
```

实测（android_startup_real.perfetto_trace，45 MB）：加载 0.57 s，`android_startups` 查询 36 ms。

## 6. 体积参考

| 项 | 大小 |
|---|---|
| perfetto 仓库完整历史 | ~290 MB（GitHub API） |
| `--depth 1` 源码 + 依赖 + 工具链 | ~1.1 GB（buildtools 1.1 GB） |
| `out/`（只编 trace_processor_shell） | ~1.1 GB |
| `test/data`（不需要，已清理） | ~1.4 GB+ |
