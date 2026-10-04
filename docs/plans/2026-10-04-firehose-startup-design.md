# BOOT-04：Loader 启动等待与 CLI 模式选择

日期：2026-10-04；依据：用户 20:28 和 20:31 运行日志。

## 已确认的问题与目标

- 20:29:03.515 Sahara Done 完成后，普通模式仅用 250 ms 等待启动文本；收到首条 build 日志即超时。随后发送 NOP，设备报告 VIP 签名表等待及 signature/authentication 失败、NAK。该探测破坏了首张表输入顺序。
- 20:31 的 Oplus Legacy 被动检测未收到任何 8 字节前缀。日志不能证明当前设备仍可续接；不能自动重放 Loader、NOP、Reset 或 Digest。需重新进入 EDL，或由用户明确确认等待首张 Digest 后使用已有 --oplus-resume。
- 裸启动 CLI 应提供普通、Oplus Pt、Oplus Legacy 选择；显式参数、非交互及普通 Digest/VIP 参数不额外提问。

## 边界、实现范围与兼容性

- StartFirehose 被动启动等待统一使用 ReadTimeout（默认 10000 ms），短 NOP 探测仍有 250 ms 上限。上传了 Sahara Loader 后，即使完全静默也不发送 NOP；已有完整日志或半帧时同样不探测。只有未上传且完全未收到启动数据的普通模式保留原有 NOP 回退。
- FirehoseWireReader 记录本次启动是否收到任何字节，经 Receiver/Executor/Session 只读内部属性供门面判断；不修改 ACK/NAK、RAW、Legacy 计数、Digest/NOP 换表、Sign 验证和 sha256init 兼容。
- 已读到 VIP marker 但未配置对应资源时保持现有资源错误，补充可操作文案。资源/输入等待仍无限可取消；同步启动 I/O 仍有总预算，取消最多等待当次 I/O。
- CLI 协议注册增加可选的异步宿主选项准备回调，在协议工厂创建、打开 transport 及连接前选择；回调失败释放已创建但未打开的 transport。Qualcomm 适配器拥有选择逻辑，通用主流程不引用 Qcom 类型。显式 --oplus-mode None 也保持用户选择。
- 文件范围：QcomProtocol、WireReader/Receiver/Executor/Session、CLI ProtocolRegistry/Application/HostAdapter/Ui/Options/CommandLine、中英文资源、CLI 使用说明和实施记录。公共 API 不新增或删除。

## 测试、提交与风险

2026-10-04 FH-01 补充：用户 23:28 日志提供了一个可区分的例外。普通模式在自己发送 Sahara 唤醒包后识别到 Firehose 的完整 XML 解析错误，该响应属于探测拒绝；可有界收集完整诊断/可选 NAK，再以 NOP ACK 确认续接。一般启动日志、半帧、新上传 Loader、RAW 与 VIP/Oplus 首表仍不启用该入口。实现与证据见 `2026-10-04-firehose-resume-byte-read-fixes.md`。

1. 先用模拟分片/延迟启动验证超过 250 ms 的普通/VIP/Oplus marker，sync/async 行为一致；完整日志、半帧和新上传静默均禁止 NOP，既有普通静默回退保留。
2. 测试模式选择、错误输入重选、默认普通、显式及非交互跳过、取消；保持 Legacy Hash 诊断加 ACK 的既有回归。
3. 当前完整测试、主工作区历史回归、Release 构建、资源键和 git diff --check。测试仅在 ignored .tests，真实设备日志与模拟证据分开。
4. 按既有用户授权提交修复并安全快进同步本地 main，不推送、不操作设备。CLI 二进制重新构建。

风险：本次不能从已失败设备的静默状态推断是否可恢复；新模式选择不自动识别 Pt/Legacy，也不自动续接。10 秒默认启动预算仍需不同 Loader 现场复核。
