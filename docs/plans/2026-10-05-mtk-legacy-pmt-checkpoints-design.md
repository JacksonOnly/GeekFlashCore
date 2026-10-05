# Legacy 磁盘 PMT 与宿主策略检查点设计

日期：2026-10-05。任务：PMT-01、HOOK-01。用户已授权实现；具体漏洞、载荷、补丁与默认策略注册均不在范围内。

## 目标与兼容性

1. 保留 READ_PMT 的 Word32/Word64/Legacy96 数值和命令线路，新增 DiskV1（eMMC USER 512 字节扇区）。无 GPT 且未指定运行时布局的 Legacy eMMC 自动读取磁盘 PMT。
2. 参照 MtkPt README 的事实布局：USER 尾部减 1 MiB 为 PT，下一 4 KiB 为 MPT；4096 字节块、头/尾标记、`1.0\0` 版本、40 个 88 字节条目、低 8 位序号。不能把 ROM 文件截断偏移用于设备地址。
3. 参照新版 penumbra 的调用位置，把现有四阶段宿主回调调整到正确边界。同步与异步 Connect 共享同步协议过程；XML DA1 签名资源只能由外层编排等待。

## 线路与公共契约

- DiskV1 先通过现有同步 Legacy READ 读取主表。仅当完整读成功且标记/版本无效时读取镜像；I/O、校验和失败或条目非法直接失败。验证名称、对齐、容量、重复/重叠以及所有整数转换。保留 MaskFlags、低 8 位 Sequence 和表副本属性。默认分区缓存仍存储已验证范围。
- XFlash：BeforeDa1（参考 Unfused/Linecode 调用边界）→ DA1 初始化及包长查询 → Da1Ready（Carbonara 边界）→ BOOT-TO 确认 → Da2Ready → 标准 DA2 SLA → 重新查询包长 → Da2Authenticated（扩展入口）。
- XML：BeforeDa1（Unfused 边界）→ Runtime/HostCommands/HostInfo/NotifyInitHw → 标准 DA1 SLA → Da1Ready（Carbonara 边界）→ BOOT-TO/END → 第二次 HostCommands/NotifyInitHw/END → Da2Ready（HeapBait 边界）→ 标准 DA2 SLA → Da2Authenticated（扩展入口）。
- XML 第一阶段初始化单独返回到门面，以支持异步签名而不在同步协议层等待。新增 Da1Sla 认证种类，保留既有枚举值。明确不支持命令的响应必须消费并确认完整 END 后才可继续；其他失败不能吞掉。
- 上下文补充 DA1/DA2 认证观察状态及已上传区域数量，仍遵守线程、代数、超时和回调生命周期。Completed 不作为认证证明，不替代标准认证。IoT 在 Da1Ready 已预上传两段，仅允许替换尚未发送的 DA3，校验前两段内容。

## 生命周期、安全与恢复

- 宿主拥有 DA/EMI source；协议释放自己打开的流。4 KiB 元数据缓冲在 finally 清零。签名、Challenge 和迟到响应维持现有有限预算、取消、释放/清零策略。
- 分区访问及所有回调都持同一会话门。未知写入状态、I/O 失败、策略异常和不可恢复响应使会话失效；无无限重试，无自动实例化具体策略。
- 仅记录表副本回退与回调阶段/结果，使用成对中英资源；不记录策略 ID、签名、Challenge、完整 XML 或载荷。
- 每表固定 4096 字节、最多 40 条；镜像一次回退；内容比较继续使用池化窗口，不按镜像大小物化。

## 实施与验证

PMT-01：生产范围为 MTK Diagnostics/Storage/新增磁盘表解析、抽象布局、CLI 参数/帮助、本地测试与来源说明。先写原始磁盘夹具，验证主/镜像、坏版本/尾标记、条目错误、无 I/O 回退、容量/介质边界和默认发现缓存。

HOOK-01：生产范围为 MTK 门面、XML/XFlash 初始化、策略上下文、认证模型与框架文档。先调整模拟线路和断言，验证 XML DA1 同步/异步签名与释放、明确 Unsupported 完整 END、DA2 回调顺序、XFlash SLA 后包长查询、IoT 已上传内容不可替换、旧生命周期/路由/故障矩阵。

每步运行目标测试、Release 构建、git diff --check；最终运行 MTK、CLI、Qcom、Core 与 LP 本地完整测试并审查资源键与敏感日志。分为两个功能提交，测试工程保持 ignored。原有未跟踪 kamakiri 文档保持不动。

## 来源与待验证风险

- MtkPt：https://github.com/JacksonOnly/MtkPt/blob/main/README.md，仅参考公开磁盘格式事实；不复制代码。
- penumbra-main 非 Git 本地快照：XML protocol.rs SHA256 B9ED36BD0F956A26DB249EA19587A66457C77F3B98D50CE28FC0D1F1CE9F5408；XFlash protocol.rs CB17E5926F4D0483DC7DABA44792AAD23BD73DA6C02A316783EE518E3AC84E23；macros.rs 1100C6949EC249F60DE6EEF3CF4FAFE59969928BED5005DC40A721AF0A718522。
- 未连接真实硬件；MtkPt 的磁盘布局只确认 eMMC USER/512，不能推广至 NAND/NOR/SD/不同版本。未知布局继续要求宿主显式提供。
- XML 固件 Unsupported 的 ACK/END 组合需模拟边界验证和后续硬件确认；不通过宽松字符串匹配猜测。
