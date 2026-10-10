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
- 分区/读写/浏览的工具输出期间仍压制高频Info/Debug，但已批准MTK Warning/Error摘要继续显示；异常详情、设备正文、重复UserPresentation始终不显示。文件日志保持完整诊断，其他协议过滤不改。
- 文件日志：CLI 默认 Information 及以上，`--verbose` 才收集 Debug；可用 `--log-file` 指定位置，沿用原 16MiB 分卷。SDK 宿主应在创建 `MtkProtocol` **之前**配置 Serilog；用 `MtkSessionId` 关联同一会话。
- 不输出原始载荷、签名、Challenge、Token、checksum 数值、私密标识、XML 全文/参数或设备 MESSAGE 正文。标准分区名经过 ASCII/64-byte 校验后可出现在操作摘要。

“DA 上传已验证”只说明 checksum/status 接受，不代表认证成功或存储可用。`Unsupported`、`NotRequired` 和设备允许跳过材料交换，不应解读为本次宿主认证成功。Sparse 操作大小是逻辑镜像范围（洞保留原内容），Raw 写大小包含对齐补零；不是物理 USB 流量统计。

## 超时和断连

Preloader候选（0E8D:2000/6000）现在先发一次A0唤醒，再进行四步握手；固定1024字节首包缓存处理重复/分片READY和同包应答。BROM不增加唤醒，其首个1字节/FD半字2字节线路不变。`MaximumHandshakePrefix` 默认64、范围0～1024，整个握手及FD共享一次ReadTimeout，不无限排空或在原句柄重放。UI新增候选VID/PID/data/CDC接口，Debug补充唤醒应答/首包长度；候选PID不是阶段证明。

### XFlash EMI 窗口

`--mtk-preloader` 的 XFlash 路径保留完整 `MTK_BLOADER_INFO` 窗口（含头部），不能只发 Legacy 的 `MTK_BIN+12` 数据。Ares v51 的正确窗口为448字节，而不是336；长度帧与FLOW载荷必须一致，最后等待一次组状态。SDK 的 `MtkEmiImage.Source` 保留Legacy语义，Parser 另提供借用 `BloaderInfoSource` 给XFlash；只提供Source的宿主材料保持原样。Debug记录格式/版本/长度，不记录原始EMI。抓包/离线对照证据及实机风险见 [Preloader/EMI记录](plans/2026-10-08-mtk-preloader-emi-implementation.md)。

完整 FD 识别之前，瞬态 USB/初始握手失败释放候选并继续等；Ctrl+C 停止，显式等待超时仍有效。识别之后认证、DA、在途读写失败且无法确认完整命令边界时，旧会话失效，需要重新连接，不自动重试。标准 XML 命令完整结束后的主机检查错误按下述边界保留连接。超时只表示预算耗尽，不等于已确认拔出设备。

定位时核对：最后 `SessionState` / `BootStage` / `CommandName`、`Command`、`Status` / `ExceptionType`、读的 `ReceivedLength` / `ReadFragmentCount` / `ElapsedMilliseconds` / `TimeoutMilliseconds`、协商包长。Write 中断的预算是剩余操作预算，不是 native driver 的超时设置。同步 native call 的立即中断仍受 backend 限制。

DA1/DA2成功零字节USB IN可为ZLP，不直接解释成EOF；仍open的连接在同一次读预算中消费最多4个连续零包，不重发命令或ACK。Debug记录计数及剩余预算；实际native超时/断连异常、关闭句柄、超限零包仍终止操作并失效。协议FLOW长度0不是USB ZLP，仍非法；BROM/Preloader接入零读边界未变。见 [零包实施](plans/2026-10-08-mtk-da-zero-length-in-implementation.md)。

实现、参考指纹、测试基线与无实机风险见 [实施记录](plans/2026-10-08-mtk-transfer-diagnostics-implementation.md)。

## DA1 修改与 DA2 路由

在 BeforeDa1 回调返回的 replacement 确实修改同一执行布局的 DA1 非签名字节，且标准 DA1 上传/初始化成功后，BROM 路径跳过多余 Carbonara。仅 Completed 或 BROM 模式不足以跳过；Preloader 和宿主直接提供的预修改文件不猜测。SDK 新上下文属性 `Da1ModifiedBeforeUpload` 不代表认证成功，过期不可访问。

DA2 摘要区分请求标准 BootTo（尚待命令确认）与参数组/执行状态均确认。Debug 另记录命令接受与载荷已发送待确认。双状态通过后若 `SlaEnabledStatus` 查询失败，则是 DA2 后续认证查询，不是上传失败。用户231901日志已确认正常DA2启动，详情见 [回调记录](plans/2026-10-08-mtk-da2-callback-implementation.md)。

旧DA在SlaEnabledStatus子命令初始ACK完整返回0xC0010004时，仅以Debug记录认证证据Unsupported，继续包长/存储查询；不是认证成功或SLA禁用证明。父命令、结果帧/尾ACK、已启用后挑战/签名错误、未知状态、取消/超时不会降级。用户232837日志已确认此路径，见 [SLA兼容记录](plans/2026-10-08-mtk-xflash-sla-compatibility-implementation.md)。

XFlash设备包上限可为2MiB，不能按旧1MiB宿主小帧限制拒绝能力查询。宿主写块按协商包长，上限默认2MiB，与BROM缓冲独立；大存储FLOW默认以64KiB USB请求接收，短包直接消费，以最多1MiB池化缓冲合并输出写入，完整帧落入输出流才ACK。SDK `MaximumXFlashDataFrameSize`/`MaximumXFlashWritePacketLength` 默认2MiB，可限制512～2MiB；小帧/消息/认证/scoped ReceiveData仍由 `MaximumFrameSize` 控制，默认1MiB。每个payload窗口共享读预算。Debug在拒绝前记录响应长度和设备包长，status0本地校验失败不等于设备返回NAK。用户20261009-103949日志证明1MiB原生请求在2MiB帧末尾超时，104320确认恢复64KiB后完整读取；104747确认短包优化后读取约39.8MiB/s。合并输出优化尚待实机复测。

## XML 存储进度与会话边界

2026-10-10 用户捕获的写入预擦除进度为 `OK!PROGRESS@100@\0`。尾部 `@` 是字段分隔符，不是百分比的一部分；现在同时接受有/无该尾分隔符的 0～100 十进制进度。仍必须收到 `OK!EOT`，100% 不等于写入完成。无效百分比、额外字段、坏帧、事件超限和在途取消/超时仍报错，不自动 ACK 后继续或重发写命令。

以前在一次操作中尝试过 I/O 后，任何异常都会关闭会话。现在标准 XML 入口只有在完整成功 `CMD:END` 已验证且最终 ACK 成功后，才记录可用命令边界。此时后续的主机参数、资源或分区表校验失败保留 `StorageReady`、transport 和会话代数，并输出保留连接 Warning；错误仍返回调用方，错误数据不可使用，也不自动重试。下一次 I/O 立即撤销该边界证据。连接期失败、未知/错误 END、ACK 发送失败、在途错误及明确要求重连的异常仍失效。scoped `UseSession`/`UseDaHardware` 与扩展 ACK/context、未知写结果的失效规则不变。

XML file-data 接收使用独立 `MtkProtocolOptions.MaximumXmlReadPacketLength`，**默认 64 KiB**；范围 512～2 MiB，只有已经验证的设备/USB 后端组合才显式增大。池化窗口不超过设备 packet、实际文件大小及宿主上限，完整 FLOW 长度仍在写入输出前严格校验。短 IN 在同一帧预算内续读，不增加 ACK、不重发命令，也不放宽 `MaximumXmlDataFrameSize`、控制帧、XFlash 或 scoped extension 限制。写入仍独立使用 `MaximumXmlWritePacketLength`（默认 2 MiB）。

2026-10-10 的 512 KiB 默认曾在无损模拟中将 64 MiB native payload read 调用数降至 128，但用户 12:29:53 日志和 bbb.txt 的首次 persist 读取证实回归：2 MiB FLOW 只有 2,070,528 字节返回，仍缺 26,624 字节，伴随被取消的 USB 子请求，最后超时。默认已恢复此前成功的 64 KiB 形态（64 MiB 无损模拟为 1024 次）；不 ACK 截断数据、不放宽超时、不在失败后自动降级重读。请求拆分/取消由实际驱动决定，Penumbra 的接收缓冲大小不能直接证明本后端的大请求安全。恢复后的实机读取及 MB/s 仍需新日志确认。

### DA 扩展准备与加载

CLI 仅从当前上传 DA 镜像准备扩展，不猜函数地址；准备失败的 Warning 带 `Reason` 代码（例如 `LoaderNotFound`、`FreeNotFound`）。SDK 保留原 `Prepare` 入口，并增加输出 `MtkExtensionPreparationFailure` 的重载；取消和资源错误仍抛出，不当作“未准备”。

bbb.txt 还原的 MT6895 ARM DA2 中已有完整加载器，但 `Bad %s` 引用的 MOVW/MOVT 之间存在条件 MOVT R3。此前寄存器回溯将任意条件指令都当作障碍，错误地阻止了 R0 中的 `free` 地址解析。现在仅跳过已解码且写其他寄存器的条件 MOVW/MOVT；条件写当前寄存器、跳转、其他未知编码仍停止解析。捕获 DA2 的离线准备和指针表检查通过，不代表已经在设备上启动扩展。实际加载仍要求 BOOT-TO 完成、EXT-ACK 状态 OK 和 EXT-DA-CTX 完成后才发布当前代数的扩展能力；断开或失效后不复用。

发生在预擦除的解析错误不代表分区未变化；设备可能已完成擦除但尚未接收镜像。使用修复版重新连接、完整重刷并回读核对，不续接旧的未知写入。

## GPT 与 Boot 区域

分区发现不读取eMMC BOOT1/2或UFS LU0/LU1的头尾GPT；列表的Preloader/backup容量仅是已报告的Boot区域上限。命名读写现走XFlash/XML原生接口处理启动头，读取实际镜像长度，不将整个Boot区当作Preloader。UFS User为wire3（LU2），eMMC User为wire8，不能使用通常的零基LUN号替换DA wire编号。原始sector入口仍显式访问1/2，eMMC GP独立GPT能力保留；User CRC/主备/坏表边界不降级。实机完整列表待确认。

已观察UFS 4K GPT可声明FirstUsable34，却有已通过原始CRC的分区从8开始。只对UFS User/128×128/明确物理数组布局，以元数据末端6严格验证；PGPT截止真实首分区（抓包32KiB），避免覆盖分区。其他布局不降级，CRC/重叠/越界/身份/名字校验继续。兼容说明移至Debug，不改写设备；主备几何、CRC布尔和失败阶段也仅Debug记录，不记录校验数值/GUID/载荷。

Preloader、扩展加载、Scatter转换/更新及安全重连用法见 [MediaTek CLI](cli-mtk.md)。普通帮助不再列出仅高通支持的rawprogram/patch，使用 `help qcom` 查看。
