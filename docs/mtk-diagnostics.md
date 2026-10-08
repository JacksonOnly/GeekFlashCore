# MTK 上传兼容和诊断

当前修改涉及 MTK 标准协议和 CLI 的 MTK 接入/日志；最新授权另包含既有 DA1→DA2 回调路由，不新增 exp 算法/策略/载荷，不修改 Qualcomm/SPRD 协议。

## 上传兼容

BROM 原始上传现在默认按 65536-byte 宿主块流式写入，不额外发送尾部 ZLP。USB backend 负责物理包；宿主块不是端点 max packet size。保留奇数补零、XOR 校验、最终状态与失败不重试。

```text
geekflash --protocol mtk --loader DA.bin --verbose --log-file D:\logs\mtk.log info
```

只有设备兼容性确有需要时，显式恢复以前的宿主写入形态：

```text
--mtk-brom-chunk 64 --mtk-brom-zlp
```

SDK 对应 `MtkProtocolOptions.BromUploadChunkSize`（0 使用 BufferSize，其他值 64～1048576）和 `BromUploadZeroLengthPacket`（默认 false）。这两个设置只影响 BROM DA/证书/认证数据上传，不改变 XFlash FLOW/checksum/ACK。XFlash raw/named 接收使用已协商读包长，超长帧拒绝，不自动重发命令。

## 日志分层

- 默认 UI：连接、DA、EMI、认证证据、存储几何和读写擦除开始/完成；可恢复回退 Warning。命名擦除不猜测容量。
- `--verbose`：追加 Debug 的命令、USB 写长度、帧、ACK/status、读片数/预算/耗时和失败上下文。
- 文件日志：CLI 默认已收集 Debug，可用 `--log-file` 指定位置，沿用原 16MiB 分卷。SDK 宿主应在创建 `MtkProtocol` **之前**配置 Serilog；用 `MtkSessionId` 关联同一会话。
- 不输出原始载荷、签名、Challenge、Token、checksum 数值、私密标识、XML 全文/参数或设备 MESSAGE 正文。标准分区名经过 ASCII/64-byte 校验后可出现在操作摘要。

“DA 上传已验证”只说明 checksum/status 接受，不代表认证成功或存储可用。`Unsupported`、`NotRequired` 和设备允许跳过材料交换，不应解读为本次宿主认证成功。Sparse 操作大小是逻辑镜像范围（洞保留原内容），Raw 写大小包含对齐补零；不是物理 USB 流量统计。

## 超时和断连

Preloader候选（0E8D:2000/6000）现在先发一次A0唤醒，再进行四步握手；固定1024字节首包缓存处理重复/分片READY和同包应答。BROM不增加唤醒，其首个1字节/FD半字2字节线路不变。`MaximumHandshakePrefix` 默认64、范围0～1024，整个握手及FD共享一次ReadTimeout，不无限排空或在原句柄重放。UI新增候选VID/PID/data/CDC接口，Debug补充唤醒应答/首包长度；候选PID不是阶段证明。

### XFlash EMI 窗口

`--mtk-preloader` 的 XFlash 路径保留完整 `MTK_BLOADER_INFO` 窗口（含头部），不能只发 Legacy 的 `MTK_BIN+12` 数据。Ares v51 的正确窗口为448字节，而不是336；长度帧与FLOW载荷必须一致，最后等待一次组状态。SDK 的 `MtkEmiImage.Source` 保留Legacy语义，Parser 另提供借用 `BloaderInfoSource` 给XFlash；只提供Source的宿主材料保持原样。Debug记录格式/版本/长度，不记录原始EMI。抓包/离线对照证据及实机风险见 [Preloader/EMI记录](plans/2026-10-08-mtk-preloader-emi-implementation.md)。

完整 FD 识别之前，瞬态 USB/初始握手失败释放候选并继续等；Ctrl+C 停止，显式等待超时仍有效。识别之后认证、DA、读写失败立即使旧会话失效，需要重新连接，不自动重试。超时只表示预算耗尽，不等于已确认拔出设备。

定位时核对：最后 `SessionState` / `BootStage` / `CommandName`、`Command`、`Status` / `ExceptionType`、读的 `ReceivedLength` / `ReadFragmentCount` / `ElapsedMilliseconds` / `TimeoutMilliseconds`、协商包长。Write 中断的预算是剩余操作预算，不是 native driver 的超时设置。同步 native call 的立即中断仍受 backend 限制。

DA1/DA2成功零字节USB IN可为ZLP，不直接解释成EOF；仍open的连接在同一次读预算中消费最多4个连续零包，不重发命令或ACK。Debug记录计数及剩余预算；实际native超时/断连异常、关闭句柄、超限零包仍终止操作并失效。协议FLOW长度0不是USB ZLP，仍非法；BROM/Preloader接入零读边界未变。见 [零包实施](plans/2026-10-08-mtk-da-zero-length-in-implementation.md)。

实现、参考指纹、测试基线与无实机风险见 [实施记录](plans/2026-10-08-mtk-transfer-diagnostics-implementation.md)。

## DA1 修改与 DA2 路由

在 BeforeDa1 回调返回的 replacement 确实修改同一执行布局的 DA1 非签名字节，且标准 DA1 上传/初始化成功后，BROM 路径跳过多余 Carbonara。仅 Completed 或 BROM 模式不足以跳过；Preloader 和宿主直接提供的预修改文件不猜测。SDK 新上下文属性 `Da1ModifiedBeforeUpload` 不代表认证成功，过期不可访问。

DA2 摘要区分请求标准 BootTo（尚待命令确认）与参数组/执行状态均确认。Debug 另记录命令接受与载荷已发送待确认。双状态通过后若 `SlaEnabledStatus` 查询失败，则是 DA2 后续认证查询，不是上传失败。用户231901日志已确认正常DA2启动，详情见 [回调记录](plans/2026-10-08-mtk-da2-callback-implementation.md)。

旧DA在SlaEnabledStatus子命令初始ACK完整返回0xC0010004时，输出Warning并记录认证证据Unsupported，继续包长/存储查询；不是认证成功或SLA禁用证明。父命令、结果帧/尾ACK、已启用后挑战/签名错误、未知状态、取消/超时不会降级。用户232837日志已确认此路径，见 [SLA兼容记录](plans/2026-10-08-mtk-xflash-sla-compatibility-implementation.md)。

XFlash设备包上限可为2MiB，不能按旧1MiB宿主小帧限制拒绝能力查询。宿主写块仍默认64KiB；大存储FLOW用小池化窗口接收，完整帧才ACK。SDK `MaximumXFlashDataFrameSize` 默认2MiB，可限制512～2MiB；小帧/消息/认证/scoped ReceiveData仍由 `MaximumFrameSize` 控制，默认1MiB。每个payload窗口共享读预算。Debug在拒绝前记录响应长度和设备包长，status0本地校验失败不等于设备返回NAK。用户233705已联机到UFS，见 [包长实施](plans/2026-10-08-mtk-xflash-packet-capacity-implementation.md)。
