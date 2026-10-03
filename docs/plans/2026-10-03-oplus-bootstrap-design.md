# Oplus 启动与 CLI 调试修复

日期：2026-10-03。范围由用户的 CLI 日志、Bus Hound 抓包及本轮明确要求确定。

## 目标与证据

抓包显示 Sahara DONE 后，Loader 报告 `VIP is enabled, receiving the signed table of size 8192`；旧实现等待 supported-functions 结束标记超时后发送 NOP，导致签名表验证失败。Oplus 必须识别此启动标记，禁止初始化前主动 NOP，连接顺序改为启动日志 → Digest → verify XML → 4096 字节零填充 Sign → 验证成功 → sha256init → Configure → 存储查询。

GeekFlashTool 的 Firehose 初始化和 Module/Oppo.cs 提供上述顺序及芯片 Sign 表；Rector 继续作为 Legacy 运行期换表参考。保留已有 Legacy 换表、包计数、特殊 XML 声明及 Pt 分区映射，不调整参考兼容细节。没有成功硬件握手证据，需用户复测。

## 契约与状态

Oplus 资源响应增加可选 Sign 数据源，请求增加必须提供 Sign、前次 Sign 被拒绝标志。Legacy 必须提供 Sign；Pt 优先显式 Sign，否则用参考芯片表，未知或拒绝时请求手动 Sign。异步资源获取只在 QcomProtocol，所有线路同步；资源请求有超时/取消，调用方拥有数据源，Core 释放自己打开的流并清零 Sign 缓冲。资源在首包 Digest 前校验，认证不成功不能配置或查询存储。

完整 XML NAK 可用于一次手动恢复；超时、半帧、Raw 异常失效会话。显式表接收状态只在重新发送 Digest 后恢复 verify，不发送探索命令。连接重试不重复获取并发送 Digest；存储几何回退复用同一已认证策略。

## CLI 与日志

增加 --oplus-sign FILE（交互选择二进制 Sign 文件路径）；移除六个 --legacy-* 选项，CLI 固定 53/0/256/1000/4096/内置 NOP，Core 默认分段也为 256。每次执行生成独立文件日志，默认位于程序目录 logs，可用 --log-file 指定。文件包含 Debug 时间、命令名、字节数、超时、响应状态、耗时和完整异常堆栈；禁止完整 Digest、Sign、认证材料、用户自定义 XML。允许输出已知签名失败状态语句，防止过度脱敏掩盖错误。

## 实施与验证

1. 新增模拟启动日志与线路顺序测试，先复现 VIP 标记后误发 NOP。
2. 实现同步/异步同一 Oplus bootstrap、Sign 选择及有限恢复；资源验证、取消、失败不得越过认证。
3. 更新 CLI 参数、双语资源、自动文件日志与用户文档。
4. 运行完整 .tests/GeekFlashCore.Protocol.Qcom.Tests Release 测试、解决方案 Release 构建、git diff --check；审查资源键、敏感日志和 ignored 状态。

提交分为协议修复和 CLI/日志交付。Verify ACK 兼容省略/false/true 的 rawmode，并明确进入 Sign RawTransfer。风险：真实 Loader 的 Verify/Sign 回复、Sign 拒绝后的签名表重入、参考内置 Sign 的接受性需要硬件复测；阻塞同步 I/O 的取消仍受单次读取预算限制。
