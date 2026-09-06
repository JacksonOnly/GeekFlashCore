# CLI-11 实施计划

日期：2026-09-05。设计：`2026-09-05-cli-commands-design.md`。按本会话逐项执行，不委派其他 agent。

- [x] 1. 核心能力与 LUN：测试先行修复实际 SupportedFunctions、Chip 回填、按 LUN 查询和分区读取；文件涉及 QcomProtocol.Storage、FirehoseStorageInfoParser、IQcomProtocol。
- [x] 2. Firehose 方法：增加有界 Peek/Poke、流式 FirmwareWrite、类型化 Poke/XblGpt，测试 XML、字节、错误和释放，保持会话串行与取消。
- [x] 3. CLI 语法与映射：独立 CommandSyntax/StorageCommands/FirehoseCommands，修正命令分派，缺参直接输出用法，已声明能力过滤，连接显示映射列表，Bytes 显示。
- [x] 4. 完整验证与交付：完整 Qcom/CLI 测试，Release 构建，help/缺参进程退出码，资源中英文对应，git diff --check，忽略文件检查；同步主实施计划。

## 进度

- 2026-09-05：初始工作区干净（70f8d90）。实机反馈证明 GPT、UFS 产品和构建日期已可用；本轮确认 CLI 忽略 partitions 参数、能力解析有默认回填问题。已核对 EdlClient NOP、读写、擦除、patch、power、peek/poke、xblgpt 方法。

- 2026-09-05 / CLI-11：完成按 LUN/全 LUN 查询；无 num_physical 时只使用已有信息，非法或超过 8 的数量被拒绝。部分 GPT 无效记录 Warning，传输失败传播。SupportedFunctions 来自实际日志，取消默认 18 项；Chip 优先 Firehose，再使用 Sahara/SoC 证据。后续 CLI-14 将容量探测收敛到 Configure 阶段，分区和范围命令只读缓存。
- 2026-09-05 / CLI-11：通用帮助仅列通用命令；Firehose 的命令说明和语法通过协议注册项显示，按 Core 与设备支持交集过滤，大小写不敏感，未实现命令不展示。`qcom` 前缀兼容。交互 `connect` 实际调用重连并刷新映射。读写擦支持名称和 sector 形式，缺参/额外参数返回 2；范围校验和名称消歧发生在创建读取文件前。
- 2026-09-05 / CLI-11：Peek 按 256 字节分块解析，Poke 按最多 8 字节小端发送，FirmwareWrite 流式传输、打开者释放 Stream；NAK/取消有测试。按名称 Raw 写入只补齐末扇区，显式 sector 写入仍补齐目标范围；公开 Program 请求默认行为保持兼容。

## 最终审查

逐项审查完整生产代码差异、未跟踪新增文件、命令到 XML 的映射、通用/协议边界、整数范围、文件创建顺序、日志和 Stream 生命周期。发现并修正：刷新省略 num_physical 导致 LUN 消失；内存 NAK 异常带出数据；命名 Raw 写入补零整个分区；交互 connect 仅打印消息未重连。四项均有失败复现和修复后的回归证据。核心新代码不在异步资源等待中做同步阻塞，未新增整镜像物化或无界重试。

验证命令及结果：

- `dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore -v quiet`：203/203 通过。
- `dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore -v quiet`：25/25 通过。
- `dotnet build GeekFlashCore.slnx -c Release --no-restore -v minimal`：0 警告、0 错误。
- 64 MiB 生成数据源/计数传输测试：读写窗口均不超过 1 MiB，无需物化生成源。
- 编译后 CLI 离线进程：help 返回 0 且不包含 Firehose 命令列表；read/write/erase/partitions/qcom/peek/patch 缺参均显示对应用法并返回 2。
- 中英文资源键一致（CLI 33、Qcom 210）；`git diff --check` 通过；`.tests`、bin/obj/temp 保持 ignored，未跟踪任何测试文件。

## 未决风险与续接点

提交拆分：Core 与设计已提交为 `bc83c92`（`feat(qcom): add lun discovery and firehose command transfers`）；CLI 与本实施/审查记录单独提交为 `feat(cli): validate commands and map firehose capabilities`。测试文件保持本地 ignored，不包含在提交中。

本轮未连接真实设备或执行写入/擦除。下一步从用户设备的 `partitions all`、`partitions 1` 和小分区读取验证开始；检查实际多 LUN 容量、日志和映射结果。ufs/emmc provisioning 不在首批映射范围；厂商私有 Peek/Poke 参数方言、FirmwareWrite 硬件行为仍需脱敏响应或真实设备验证。缺少 Sahara 证据的直接 Firehose 重连仍允许 chip=unknown。严格主 GPT 校验和既有 Sparse 分段计划保留，未加入备份表恢复。
