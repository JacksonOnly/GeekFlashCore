# SPRD 入口阶段自动识别设计

日期：2026-10-08；任务 SPRD-10；基线 18c2a12，工作区干净。用户授权自动判断 BootROM、FDL1、FDL2。

## 范围与参考证据

核心及 CLI 默认入口改为 Auto，保留显式 BootRom/Fdl1/Fdl2 的原线路和枚举数值。新增枚举项放在末尾；成功连接的 TargetInfo 保留入口阶段，最终 Stage 仍为 Fdl2。未知响应、损坏/歧义校验不能触发 Loader 上传；不通过 VID/PID、版本文字或资源是否存在推断。

参考固定源：spreadtrum_flash 64fe3e379f23c9e1ba1623964b47a326c66b5081 的 common.c recv_check_crc、spd_dump.c 初始握手，以及 SPRDClientCore fb20583c770c141602cf28c6db9c74eb2ef8bf92 的 ConnectToDevice。CHECK_BAUD 单字节 7e，VERSION 的 CRC16/XMODEM 对应 BootROM、FDL checksum 对应 FDL1；FDL checksum 的 UNSUPPORTED_COMMAND(0xfe) 对应已加载 FDL2，随后 DISABLE_TRANSCODE ACK 确认线路。SPRD4/autod 文本不等于三阶段证明。以上是参考行为，没有实机证据。

## 线路与失败边界

自动模式只在初始识别期间对同一个完整帧计算两种校验，必须恰好一个匹配；后续命令锁定该校验，日志帧不能决定阶段。VERSION 后按该校验发送一次 CONNECT 并要求 ACK；FDL2 特征响应后发送一次 DISABLE_TRANSCODE 并要求 ACK，再切换无转义。不会自动启用 Raw 或推断容量/64 位布局。

CHECK_BAUD 完全没有收到任何字节且单命令超时时，允许剩余总预算内一次 FDL checksum CONNECT 查询（对应参考项目的已加载 FDL2 重连）。这不是命令重发；部分帧、日志后超时、坏校验、取消及总预算耗尽都禁止这一步。此 CONNECT 仅接受 FDL2 特征，ACK 无法区分 FDL1/FDL2，VERSION 也不触发另一次 CONNECT 或 Loader 上传。无第三次探测、清缓存或重连循环。未知/歧义报本地化异常，要求显式入口并断开重连。

## 编排与所有权

同步 Connect 借用调用方资源；Auto 在打开前检查已提供资源的有效性，识别后验证实际需要的 Loader。显式入口仍先验证必需资源再打开。异步 Auto 先在串行 gate 内同步识别，再异步请求实际 BootROM/FDL1 资源；FDL2 不调用 Provider。资源等待包含在 Connect 总预算内，迟到结果沿用释放逻辑。Provider 不在同步 wire 内等待，不收到 Auto 阶段。自动识别后资源缺失/超时/取消均 Faulted、关闭传输、推进 Generation；正常借用资源不由核心释放。

EntryTranscodeDisabled 仍限显式 FDL2；无法自动区分任意已有转义配置。已加载 FDL2 的自动入口沿用参考握手协商关闭转义；不支持该命令的自定义 Loader 使用显式 fdl2。新日志仅阶段/数值，不记录版本原文。

CLI 省略入口或 --sprd-entry auto 均自动；自动入口非交互模式在识别后校验所需文件/地址，已加载 FDL2 不要求 Loader。显式入口的预检保留。公共默认改变须记录在帮助、README、docs/sprd.md 和 AGENTS 恢复入口。

## 实施与验证

先在 ignored .tests 定义三阶段同步/异步顺序、实际 Provider 阶段、FDL2 零请求、碎片与日志、坏/双匹配校验、未知 ACK、首包零字节超时与部分帧超时、有限回退、取消/资源迟到释放、重入/gate、显式线路兼容和 CLI 默认/auto/非交互预检；再实现最小修改。范围为 SPRD abstractions/core/wire、CLI adapter/parser/resx、说明文档，通用传输不改。

运行全部可用 SPRD/CLI 测试、Release 解决方案构建、资源键、help 和 git diff --check；审查 ignored 与完整差异后提交 feat(sprd): detect initial BSL loader stage。没有实机；下一步以合法匹配 FDL 验证三种初始模式、FDL2 无响应重连及关闭转义行为，不能把模拟测试作为硬件证据。
