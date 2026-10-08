# MTK 上传兼容和诊断

当前修改仅涉及 MTK 标准协议和 CLI 的 MTK 接入/日志；不修改 exp 或 Qualcomm/SPRD 协议。

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

实现、参考指纹、测试基线与无实机风险见 [实施记录](plans/2026-10-08-mtk-transfer-diagnostics-implementation.md)。
