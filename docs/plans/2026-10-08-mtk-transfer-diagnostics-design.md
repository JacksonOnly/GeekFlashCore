# MTK 上传 / XFlash 对照与诊断设计

日期：2026-10-08。任务：MTK-TXLOG-01～04。用户确认：BROM 有界大块上传默认不附加 ZLP，保留显式兼容选项；补足默认 UI 摘要与详细诊断，禁止材料/载荷日志。

## 目标与非目标

- 对照 `penumbra-main/core/src/da/xflash/protocol.rs`、`flash.rs`、BROM `preloader/protocol.rs` 与 libusb backend。不要混淆方向：BROM UploadBytes 是 host→device，Rust XFlash upload_data 是 device→host。
- BROM 固定 64-byte 调用改为池化有界流式块，不物化 DA；默认无需额外 ZLP，显式 chunk/ZLP 可恢复旧模式。保留 BE header、奇数补零、LE word XOR、checksum/status 严格校验，失败不重发。
- XFlash 保留 FLOW header/参数组末 ACK、三帧 zero/checksum/data/块 ACK、最终 status、BOOT_TO 两次状态和 SLA 后 packet refresh 顺序；统一重复收发循环，减少每块临时数组，尊重已协商读包长并在读取 payload 前拒绝超长帧。
- exp、资源二进制、策略调用顺序与实现、Qcom/SPRD/公共 USB 不修改。Qcom 只作日志分层参考。未知硬件、忽略 checksum、无限 drain、重启回退不照搬。

## 日志契约与所有权

- 核心 Serilog 是事实事件来源；显式 `MtkSummary=true` 的非敏感 Information/Warning 事件由 CLI 默认显示，verbose 追加 Debug，事件只展示一次，其他协议默认 sink 策略不变。
- 摘要覆盖阶段、DA 选择/上传/跳转、EMI 是否需要与完成、SLA 所需/认证证据、存储几何与读写擦除开始/完成。命令名称/编号、frame type/length、ACK/status、包长、read budget/片数/耗时和失败 stage/state/command/异常类型在 Debug/文件日志。逐块细节不进入默认 UI。
- 不记录 Token、签名、Challenge、认证响应、MEID/SOCID/eFuse/RPMB 数据、摘要、checksum 值、XML 全文、任意参数值或原始设备文本。MESSAGE 只记录长度/数量并有界清理。自定义 XML 名称先通过现有允许列表，传入参数不日志化。
- 标准命名分区门面可记录已通过 ASCII/64-byte 校验的分区名称，除此之外不放开自定义参数；命名擦除不猜容量，也不将未知容量显示成“擦除 0 字节”。
- 不重复输出错误栈：wire 记录收发中断元数据，protocol Fault 一次提供阶段/命令/异常类型；CLI 负责最终用户错误和重连提示。发包前参数错误不失效、不虚构操作成功。完成摘要必须在最后 ACK/END 与取消预算确认后。
- gate/代数、同步 I/O、资源借用与迟到结果释放保持；日志不额外访问 USB，不新建后台读线程或默认开启 DA USB log channel。
- 宿主在创建协议前配置 Serilog；会话固定 logger context 和 MtkSessionId，故障后仍能关联最后阶段与命令。Read 记录逻辑读预算；Write 中断记录剩余操作预算，不冒充后端原生超时设置。

## 文件与步骤

1. MTK-TXLOG-01：ignored 测试先定义 BROM 64KiB/非seek/odd/checksum/兼容 ZLP 和 XFlash 帧/状态/包长边界，确认当前失败；记录本地参考指纹。
2. MTK-TXLOG-02：BROM chunk/ZLP 配置（核心及 MTK CLI），XFlash 流式循环/常量帧优化和读包限制；线序一致/差异明确。
3. MTK-TXLOG-03：MTK 核心/各 DA/wire 阶段与收发诊断、本地化资源、CLI 摘要 sink；用捕获日志验证双语、级别、上下文、无 secret、无失败完成事件。
4. MTK-TXLOG-04：完整 CLI、原始 MTK 与基线失败对照、可运行 MTK、64MiB 内存回归、Release build、资源键对照、diff check、ignored 门禁后英文独立提交。

## 风险

模拟线路不是实机 USB 包/吞吐证据。批量 bulk write 不等于 USB max packet size，backend 负责拆物理包；保守兼容参数可恢复 64-byte + ZLP。部分 DA 报告包长是否会变化需硬件确认；按连接/认证阶段显式 refresh，不为每次读写新增未确认设备命令。默认不泄露 DA 文本是安全差异，不照搬参考 trace 全文。
