# 文档索引

| 文档 | 内容 |
|---|---|
| [01-trace-processor-ubuntu1804.md](01-trace-processor-ubuntu1804.md) | 为什么 perfetto-mcp-rs 在 18.04 跑不了；官方包 glibc 实测表；自编译 trace_processor（v58.2, glibc 2.23）的方法、踩坑、验证命令；随包二进制的来源与校验和 |
| [02-design.md](02-design.md) | 设计：技术选型、目录结构、超时问题的解决机制、热更新守护进程、工具列表、验证结果、后续可做 |
| [03-dev-guide.md](03-dev-guide.md) | 构建/测试/发布/冒烟测试命令、MCP 客户端配置、配置项（含预热、adb）、升级 Perfetto 版本步骤、开发约定与踩坑、Linux 验证流程、真机录制注意事项、性能排查方法 |
| [04-code-review.md](04-code-review.md) | 代码 review 记录：问题、场景、修复方式、暂不修的已知限制 |
| [05-trace-pitfalls.md](05-trace-pitfalls.md) | Trace 数据陷阱与分析口径：影子进程、CPU 频率时间加权、perf 采样、内存/IO/binder/ANR/启动口径、Buffer Stuffing 不算可见掉帧、关键路径的适用边界、堆转储找持有者 |
| [06-oss-research.md](06-oss-research.md) | 开源项目调研（SmartPerfetto、antarikshc/perfetto-mcp、Perfetto ai/skills…）：已吸收的逻辑与阈值、待做清单 |

## 新会话快速上手

1. 先读根目录 `CLAUDE.md`（构建命令、关键事实、约定）；
2. 改 trace_processor 相关看 01，改架构/超时机制看 02，日常开发看 03，历史问题看 04，分析口径/数据坑看 05，开源借鉴看 06。
