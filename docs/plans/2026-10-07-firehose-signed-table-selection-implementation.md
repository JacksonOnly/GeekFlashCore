# Firehose 等待签名表恢复实施记录

- 日期：2026-10-07；任务：FHST-01。
- 范围与线路：[设计](2026-10-07-firehose-signed-table-selection-design.md)。用户明确要求等待签名表时手动选品牌及 DigestPt/Legacy，再请求 Digest/Sign。
- 初始工作区：`codex/firmware-streaming-20261007`，基线 `a9012f9`，工作区干净。
- 已确认根因：NOP 的完整 log-only 等待标记经过普通响应循环后超时，`StartFirehose` 在厂商 provider 之前抛异常。尚无本轮硬件验证。

## FHST-01 行为与实现

- 先补 ignored `SignedTableSelectionTests`：新增契约后，原实现的 6 项同步/异步、拒绝 HELLO / 静默探测 / 初始等待场景全部复现失败。实现后新增 49 项通过。
- `FirehoseWireReader` 仅为启动日志和 NOP 探测识别完整 log-only 签名表接收标记，候选帧额外用禁用 DTD/实体的结构解析验证。内部 `FirehoseProbeResult` 将等待标志与 ACK 分开；普通 NOP / Execute 仍要求真实 ACK。
- `QcomProtocol` 在等待状态中请求品牌/模式，已知品牌只选模式，显式模式不重复询问。新增 `VendorSelectionRequest.RequiresVendorSelection` / `RequiresOplusModeSelection` 和 `VendorSelectionResponse.OplusMode`，原构造与解构兼容。动态选择仍需 `AllowOplusModeSelection` 和 Digest provider，并拒绝通用 Digest/VIP 冲突。
- CLI 默认适配器已通过完整模拟输入：`Oplus → 1/DigestPt 或 2/Legacy → Digest 文件 → Sign 文件`。也接受 Pt、OplusDigestPt、OplusDigestLegacy 等名称。空输入取消，非法输入重选；非交互错误提示使用实际支持的 `--oplus-mode`、`--oplus-digest`、`--oplus-sign`、`--vip-signed` 参数。
- 选择后的首次设备发送是原始 Digest；随后保留 verify → Sign → verify passed/ACK → sha256init → Configure → storage info。动态 Legacy 正确配置 `chimerais="power"` 和既有计数线路。没有增加探测/重置/Flush 到等待与首张 Digest 之间。
- Digest NAK 立即停止；错误 Sign 最多请求一次替换，随后失败关闭。材料流释放、同步/异步取消、选择超时与迟到结果、失败后重新选 Pt、不沿用 Legacy 的恢复均有模拟证据。显式 Generic VIP 仍先发签名表再 Configure，未被隐式改成 Oplus。

## 验证证据

- `dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore -v quiet`：506/506 通过，其中本轮 49 项；包含 1 字节分片、非法 XML、半帧、普通 NAK、RAW、无标记/无响应、模式错误、资源超时/取消、认证顺序与重连。
- `dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore -v quiet`：106/106 通过。
- `dotnet build GeekFlashCore.slnx -c Release --no-restore -v quiet`：0 警告 / 0 错误，当前 Release CLI 已重建。
- 中英文资源：CLI 267 个键、Qcom 278 个键，唯一性、键集合与格式占位符一致；`git diff --check` 通过。`.tests`、临时合成 Digest/Sign、bin/obj 保持 ignored，`git ls-files .tests` 为空。
- 性能范围：新增解析仅发生在受既有 1 MiB 帧/响应预算限制的候选 XML 上；没有镜像、固件或 Digest 的额外整体缓存。等待标记出现后直接进入资源选择，省去等待剩余 NOP 超时；没有对真实设备延迟作测量。

## 提交与恢复入口

- 作为一个连接缺陷提交核心、兼容契约、CLI 与计划；最终提交号以 Git 历史为准，测试不提交。
- 工作区仅本轮生产代码与计划，提交后应为干净；下一步使用当前 Release CLI，在用户设备复测上述交互顺序及认证结果。
- 风险：本轮全为模拟传输与合成离线材料，没有连接或刷写硬件。设备可能因材料不匹配、厂商非标准 XML 或其他状态拒绝认证；等待标记不是认证证明。普通 Digest/VIP/Oplus 互斥、显式 resume 的有界重发及会话失效边界没有放宽。
