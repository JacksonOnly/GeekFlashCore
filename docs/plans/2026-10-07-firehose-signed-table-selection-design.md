# Firehose 等待签名表时的交互恢复设计

## 目标与兼容性

- 用户 2026-10-07 日志中，已运行的 Loader 拒绝 Sahara HELLO，随后 NOP 引发 Hash mismatch 并输出 `VIP is enabled, receiving the signed table of size 8192`，没有普通 ACK。当前连接在品牌选择前退出。
- 对完整、结构合法的 log-only XML 中明确的签名表接收标记，保留独立的等待状态，允许宿主先选择品牌，再选择 OplusDigestPt / OplusDigestLegacy，之后请求 Digest、Sign 并沿既有认证线路继续。
- 等待状态不代表 ACK、认证成功或连接成功。普通命令、RAW、半帧、普通 NAK、无响应、过时 VIP 字样和未知诊断不作为恢复证据。不自动把通用 VIP 判为 Oplus。
- 已显式配置品牌/模式时保留选择；显式通用 VIP 继续现有线路。新 Loader 的 Sahara + Loader 双证据自动选择保持兼容。不改变显式 `ResumeAwaitingDigest` 的一次性探测跳过与 Digest 有界重发规则。

## 现有实现与顺序

- `DetectProtocol` / `StartFirehose` 使用同步有限探测，Sahara 二进制唤醒被拒后用 NOP ACK 确认；目前 log-only 等待标记被普通响应读取器一直等到超时。
- 保留首轮探测与诊断日志；一旦观察到等待标记，不再追加 NOP、Sahara reset 或 Flush。交互等待期间不做设备 I/O。
- Oplus 后续顺序保持：品牌/模式选择 → 请求 Digest/Sign → 原始 Digest + ACK → verify → 4096 字节 Sign + verify passed/ACK → SHA256 初始化 → Configure → storage info。

## 契约、状态与所有权

- 为 `VendorSelectionRequest` 添加兼容 init 属性，区分是否需要品牌选择、是否必须选择 Oplus 模式；为 `VendorSelectionResponse` 添加可选 Oplus 模式。保留原构造与解构契约。
- 只有启用 `AllowOplusModeSelection`、存在 Digest provider、没有通用 Digest/VIP 冲突时才允许动态 Oplus 模式。宿主必须明确返回 Oplus/OnePlus 和有效模式；已知品牌不重复询问。
- NOP 专用响应读取返回内部等待标志；不扩展公共 ACK/NAK 枚举，不放宽普通 Execute。门面在请求认证材料前验证选择，认证完成后清除等待状态；失败与重连清理状态。
- 资源提供器继续异步、有超时/取消与迟到结果处理；同步门面沿用现有 resolver。Wire 层保持同步，借用 IDataSource 不被释放，自己打开的 Stream 及时释放，Sign 清零。

## 性能、安全与日志

- 只检查现有有界 XML 帧中的标记，明确等待时立即结束探测，不再消耗整个 NOP 超时。结构校验仅用于候选等待包，DTD/外部实体禁止，内存受 XML 上限约束。
- 不改变大文件流式读写，不缓存镜像；不输出 Hash、签名、XML 原文。新增提示使用中英文资源。
- 无效选择、取消、材料错误、设备拒绝均关闭并清理会话，不自动降级 Generic 或绕过认证；真实设备响应时间和 Legacy/Pt 线路仍需用户复测。

## 文件与验证

- 文件范围：Qcom Abstractions 的厂商选择契约/配置注释，Qcom NOP 专用接收、门面启动/选择与 Legacy 激活，CLI 厂商/模式提示，中英文资源与计划。
- ignored `.tests` 先复现失败，再覆盖同步/异步、NOP 后等待/静默探测/初始等待、选择顺序、显式品牌与模式、Pt/Legacy、普通 NAK/RAW/半帧/非法 XML/取消/资源超时、认证失败与资源释放。
- 验证：目标回归，完整 Qcom 和 CLI 测试，Release 解决方案构建，资源键/占位检查，`git diff --check` 和 ignored 跟踪检查。提交生产代码与文档，不提交测试或材料。
- 可独立提交核心恢复及 CLI 交互；最终提交按实际能力边界确定。没有设备写入授权，本轮仅模拟传输，不连接硬件。
