# 文档索引

安装、配置与工具列表见根目录 [README.zh.md](../README.zh.md)；版本变化见 [CHANGELOG.md](../CHANGELOG.md)。

| 文档 | 内容 |
|---|---|
| [01-trace-processor-ubuntu1804.md](01-trace-processor-ubuntu1804.md) | 官方 trace_processor 的 glibc 需求；针对 Ubuntu 18.04 自编译（v58.2, glibc 2.23）的方法、踩坑与验证；随包二进制来源与校验和 |
| [02-design.md](02-design.md) | 设计：技术选型、目录结构、超时与生命周期机制、热更新守护进程、工具约定、容量参考、后续可做 |
| [03-dev-guide.md](03-dev-guide.md) | 构建 / 测试 / 冒烟测试、CI 与发版、热更新、升级 Perfetto、开发约定与踩坑、Linux 验证、真机录制、性能排查 |
| [04-code-review.md](04-code-review.md) | 代码 review：已确认的不变量、仍存在的已知限制、发现并修复的问题 |
| [05-trace-pitfalls.md](05-trace-pitfalls.md) | Trace 数据陷阱与分析口径：线程/进程归属、CPU 频率、perf、内存、IO、binder/ANR/锁、关键路径、启动、帧、堆转储、trace 健康 |
| [06-oss-research.md](06-oss-research.md) | 开源项目调研（SmartPerfetto、antarikshc/perfetto-mcp、Perfetto ai/skills…）与已吸收的规则、阈值 |

新会话：先读根目录 `CLAUDE.md`，再按需看上表。
