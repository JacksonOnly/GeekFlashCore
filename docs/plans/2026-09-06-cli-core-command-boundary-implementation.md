# CLI-15 实施记录

日期：2026-09-06。设计：`2026-09-06-cli-core-command-boundary-design.md`。

## 进度

- [x] 先为 Core 类型化命令入口和 CLI 调用边界增加失败测试。
- [x] 在 `IQcomProtocol/QcomProtocol` 暴露并实现类型化 Firehose 命令，复用 `FirehoseStorageService`。
- [x] 删除 CLI 对 Firehose packet 的构造、重复范围校验和重复 SHA256 解析。
- [x] 更新 CLI 设计/实施文档与主 Qualcomm 实施计划。
- [x] 运行 Qcom/CLI 测试、Release 构建、差异检查并完成审查。

## 验证证据

- `GeekFlashCore.Protocol.Qcom.Tests`: 224/224 通过。
- `GeekFlashCore.CLI.Tests`: 48/48 通过。
- CLI 类型化分发测试确认 Patch、Benchmark、FixGpt、XblGpt、SetBootableStorageDrive、GetSha256Digest 和 Nop 均调用 `IQcomProtocol` 对应入口，且不触发 `ExecuteFirehoseCommand`。
- Qcom 配置缓存测试确认分区表重复读取不发送 `getstorageinfo`；显式 `GetStorageInfo` 仍为主动刷新入口。
- Qcom/CLI Release 构建通过，`git diff --check` 通过。

## 未决风险

- 真实设备对 XBL GPT、Benchmark、Peek 方言及特定 Firehose 响应仍需硬件复测；本次仅调整调用边界和复用已有协议实现。
