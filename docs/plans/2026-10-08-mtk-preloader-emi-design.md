# MTK Preloader 握手与 XFlash EMI 修复设计

日期：2026-10-08。任务：PL-HS-01、EMI-XF-01。起点 `23cb56f`，工作区干净。用户要求修复 Preloader 握手，随后提供失败日志、实际 preloader 和正确 EMI 抓包。仅 MTK 标准线路；不修改 exp、Qcom/SPRD 或增加设备执行策略。

## 证据与目标

- `222204` 日志第一候选 A0 OUT 成功、第一次 IN 返回 Pipe；日志没有候选 VID/PID/接口，不能认定设备阶段或 Pipe 的驱动原因。既有一次首次零字节 IN ClearHalt 已启用，不通过重复清除或重发写命令掩盖错误。第二候选完整 FD/FE 已证明 BROM，DA1 成功，随后缺 EMI，与第一候选握手不同。
- `222420` 日志 BROM/DA1 成功，InitExtRam 发送 336 字节后状态 `C0070005`。用户 Bus Hound `837.1` 包记录完整 448 字节，从 `MTK_BLOADER_INFO_v51` 开始，InitExtRam 的长度帧是 `0x1C0`，随后一次零状态。
- 本地 `penumbra-main/core/src/preloader/protocol.rs` 对 Preloader 先发一次 A0 唤醒，再执行四字节序列；libusb 后端使用接收缓存，不将 READY 多字节 USB 包直接装入一字节缓冲。当前实现没有唤醒，且前缀最多5字节。
- 本地 mtkclient `daconfig.py:m_extract_emi` 明确区分 XFLASH 完整 BLOADER 窗口与 Legacy 的 MTK_BIN+12 窗口。当前 parser 无条件裁剪，丢失112字节头部。Penumbra XFlash 发送长度帧、EMI FLOW、一次组状态；当前帧/ACK 次序正确，修复数据选择而非猜测校验或状态豁免。

## 契约、线路与兼容

1. EMI：保留 `MtkEmiImage(Source, Version)` 构造、Source 的 Legacy 语义；增添可选借用 `BloaderInfoSource`，解析器输出两个同一借用源的有界窗口。XFlash 优先完整窗口，宿主仅提供原 Source 的显式材料保持原样，不猜测/重新拼头。核心按实际 DA 方言验证选用窗口的长度、流可读性和稳定性，再发送 DA。Legacy 与 XML 旧线路不变。
2. Preloader：仅已支持的 `0E8D:2000/6000` 候选启用参考唤醒，不以 PID 当作最终阶段证明；BROM/未知/已加载 DA 候选不增加 OUT。首握手至 FD 用固定1024字节栈缓存完整接收包，按字节解释，有界前缀、一次唤醒 ACK/echo 去重，完整非零 FD 才允许 WDT/security。BROM 保持单字节首读及既有初始 IN 停滞恢复。未知响应/前缀超限、错误、取消、超时均关闭，CLI 完整 FD 前重新枚举；不原句柄重放握手。
3. 单个握手预算与操作预算共同约束，缓存读取同样检查取消/截止；缓存不可静默丢弃未消费内容。不无界 drain，不 sleep/多轮握手，不重发认证、DA 或存储命令。串行 gate 与同步 I/O 不变。首应答前保留有界启动前缀兼容；后续步骤非预期响应立即拒绝。Preloader包缓存不扩展共享USB原有的单字节首读ClearHalt恢复资格，BROM保留原资格，Preloader最终读取错误沿释放候选路径。
4. 诊断：Information UI 标明已选 USB 候选与 Preloader 唤醒；Debug 包长、接口/CDC、握手步骤/前缀计数/去重，最终阶段依 FE。仅命令/已知握手标记和元数据，不记录序列号、设备路径、原始 EMI/DA/认证材料。

## 步骤、文件与验证

- 先在 ignored MTK 测试复现 EMI 裁剪和正确448字节、长度/ACK顺序、Legacy窗口、宿主兼容、无效完整源提前拒绝。实际 preloader 只离线读取，对抓包448字节逐字节核对。
- 第一提交 EMI parser/model、XFlash/资源验证、资源文本和记录；第二提交 Preloader 握手/固定缓存、USB 元数据日志及相应记录。范围以实际最小实现为准。
- 握手测试使用真实包边界模拟：READY 与5F同包、重复/分片READY、唤醒无应答/双应答/已握手echo、错误/取消/预算、FD身份/失败不重试和BROM原线路；新行为先失败再修复。
- 目标测试、完整可运行 MTK/CLI 回归、完整 MTK 基线对照、Release solution build、资源键/占位符、`git diff --check`、完整 diff/ignored 检查。既有14项 exp Linecode 与缺 oppo DA夹具失败单独记录，不改断言/exp。

## 风险与恢复

抓包证明448字节线路，不证明当前硬件运行修复版成功；不访问 USB 或执行 DA/exp 作验证。首次 Pipe 缺端点/身份/抓包证据，新诊断供下一次实机定位，不能声称所有驱动停滞已根治。Preloader 唤醒的应答形态以参考和模拟为证，需实机补证；CDC接口来自当前 descriptor/显式配置，不照抄参考硬编码1或吞控制错误。native 阻塞取消限制保留。进度和命令结果另记对应 implementation 文档。
