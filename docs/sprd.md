# SPRD / Unisoc BSL

`GeekFlashCore.Protocol.Sprd.Abstractions` 提供 .NET 8 公共契约，`GeekFlashCore.Protocol.Sprd` 实现同步 `ITransport` 上的 BSL。核心可复用于 CLI、桌面和服务；不依赖参考项目的串口发现、WMI 或平台 DLL。`IProtocol` 异步门面与同步 API 共用串行 gate；只有 Loader Provider 等宿主资源边界等待异步结果。

当前实现 BootROM→FDL1→FDL2、显式已加载 FDL 接入、分区枚举、流式 Raw/Sparse 写入、按范围读取、整分区擦除、正常重启和关机。线路由两份本地参考源码及模拟传输交叉验证，尚无 SPRD 实机证据。

## CLI

协议名为 `sprd`、`unisoc` 或 `spreadtrum`。必须显式指定下载模式的 `--port COMx` 或 `--usb VID:PID`；不根据未经确认的 VID/PID 清单自动识别。SerialPort 与 LibUsb 沿用现有传输实现和读写超时。

```text
geekflash help sprd
geekflash --protocol sprd --port COM7 --loader FDL1.bin --sprd-fdl1-address FDL1_ADDRESS --sprd-fdl2 FDL2.bin --sprd-fdl2-address FDL2_ADDRESS connect
geekflash --protocol sprd --port COM7 --sprd-entry fdl2 --sprd-partition-unit UNIT_BYTES partitions all
geekflash --protocol sprd --port COM7 --sprd-entry fdl2 --sprd-partition-unit UNIT_BYTES read boot boot.img
geekflash --protocol sprd --port COM7 --sprd-entry fdl2 --sprd-partition-unit UNIT_BYTES write boot boot.img
geekflash --protocol sprd --port COM7 --sprd-entry fdl2 --sprd-partition-unit UNIT_BYTES erase cache
geekflash --protocol sprd --port COM7 --sprd-entry fdl2 reboot system
```

`FDL1_ADDRESS` / `FDL2_ADDRESS` 和 `UNIT_BYTES` 必须换成设备已确认的数值，支持 `0x` 地址。不要照搬其他平台的 Loader 或地址。后续示例假定设备仍在 FDL2；未加载时继续使用完整 Loader 参数。交互模式缺失文件/地址会询问；非交互模式缺失必要参数在创建传输前失败。Provider 等待默认 30 秒；连接总预算默认 120 秒。`--resource-timeout` 与 `--connect-timeout` 可显式调整且必须为正数。

原生 `READ_PARTITION` 响应不提供可验证的容量单位，因此 CLI 存储命令要求 `--sprd-partition-unit BYTES`，例如只有确定表中值以 MiB 计数时才填 `1048576`。核心还支持宿主提供已验证的 `KnownPartitions`，不再请求原生表。不发送反复读失败请求来试探容量，也不合成缺失的 `splloader` 容量。

默认长度布局为 `--sprd-length 32`。只有确认 FDL 支持后才选 `64`（72 字节名称 + LE64 长度，共 80 字节）或 `64-reserved`（另加八字节零保留区，共 88 字节）。64 位布局的 READ_MIDST 使用 LE32 count + LE64 offset。不能表示的范围在写入/READ_START 前拒绝，不静默截断或换线路。

`--sprd-disable-transcode` 显式发送 DISABLE_TRANSCODE，ACK 后才切换编解码。若 FDL2 EXEC 明确报告不支持，则连接失败。`--sprd-entry-transcode-disabled` 只适用于入口已在 FDL2 且当前已经禁转义的设备，不会再次猜测或协商。两项默认关闭，实际时机需设备验证。

`--sprd-pad-odd` 选择 C++ 工具的偶数字节 profile。为保证声明和实际写入范围一致，该模式拒绝奇数长度 Loader/Raw，而不是补写分区末尾之外的字节；分片大小必须为偶数。默认 profile 保留 C# 参考的原始长度。

## 宿主 API

```csharp
// transport、fdl1Source、fdl2Source、imageSource 与各地址/容量由宿主提供。
var options = new SprdProtocolOptions
{
    KnownPartitions = new[] { new SprdPartition("boot", confirmedBootBytes) }
};
using var protocol = new SprdProtocol(transport, options, leaveTransportOpen: true);
using var resources = new SprdConnectionResources(
    new SprdLoader(fdl1Source, confirmedFdl1Address),
    new SprdLoader(fdl2Source, confirmedFdl2Address));
protocol.Connect(resources, cancellationToken: cancellationToken);

long written = protocol.WritePartition("boot", imageSource, cancellationToken: cancellationToken);
protocol.ReadPartition("boot", 0, confirmedBootBytes, outputStream, cancellationToken: cancellationToken);

using var boot = protocol.OpenPartition("boot", cancellationToken);
byte[] bytes = new byte[512];
int count = boot.ReadAt(0, bytes);
```

名称为 UTF-16LE NUL 终止字段，最多 35 个 code unit，不接受控制字符、未配对代理项或重复名称。`SprdPartition.Length` 是字节容量，模型不声称已知整盘偏移。通用 `PartitionTarget` 仅接受默认区域（null 或 0）；`SectorTarget`/`OffsetTarget` 未实现。`GetPartitionsAsync` 返回的 Offset/Address 为 null。

`OpenPartition` 为只读 `IReadableBlockDevice`，支持字节范围和不对齐读取。它借用会话，不拥有协议/传输；Disconnect、重启、失效和新连接推进 Generation，旧视图在任何 I/O 前失败。它不是可写块设备，也不实现整盘 `IBlockDeviceProvider`，CLI browser/LP 尚未接入该命名模型。

同步 Connect 借用资源容器。默认容器借用 IDataSource，核心只释放自己打开的流。`ownsSources: true` 明确转移可释放源的所有权；共享同一源只释放一次。`ISprdLoaderProvider.GetLoadersAsync` 必须迅速返回 ValueTask，遵守取消，并返回由核心拥有的容器。成功、失败、取消以及超时后的迟到结果均按容器所有权释放；核心不会等待不合作的 Provider 无限返回。异步连接前先获取并验证两个 Loader，再打开传输、发送握手，缺少资源不会留下部分加载状态。输出流始终借用；`ReadDestination.OwnsStream` 由调用方 Dispose 处理。

Raw 支持可定位及不可定位的可读源，前四字节识别后仍完整保留。Sparse 要求稳定可定位源，使用现有 Sparse parser（最多 262144 chunks）预检结构和 CRC，再通过 expanded stream 分片；DONT_CARE 显式写零，不能理解为保留设备旧数据。Sparse 元数据内存受 chunk 上限约束，数据缓存不随镜像大小增长。预检、校验及每个分片均检查取消和总预算。

## 失败与限制

NAK、坏帧、校验错误、响应长度错误、传输超时或传输开始后的取消/源/输出失败使会话进入 Faulted，并关闭传输；未知写入结果不重试。先 Disconnect，再用正确入口重新连接。参数预检失败且未发生 I/O 时保留会话。所有状态、读写阶段和错误使用资源化日志；原始设备版本、设备日志帧、Loader 和完整包均不自动记录。

正常 `leaveTransportOpen: true` 断开/释放保留传输供宿主管理；wire 失败仍关闭。自定义 ITransport 必须提供有界同步 Write，因通用接口没有取消 Write，取消最多需要等当前写超时。只读块设备契约没有取消参数，使用有限 OperationTimeout；需要逐次取消时使用 ReadPartition。

NV（名称含 nv）写入与擦除、NV 格式修复、镜像签名处理、诊断模式切换、重分区和 raw 快速下载未实现。正常复位仅支持 `ProtocolRebootMode.System` / `PowerOff`。FDL 签名、DRAM 和板级初始化由合法匹配 Loader 完成，本模块不提供绕过。硬件兼容、USB 重枚举、特殊首包与实际吞吐待设备验证。

设计、参考方法映射和测试证据见 [设计](plans/2026-10-07-sprd-protocol-design.md) 与 [实施记录](plans/2026-10-07-sprd-protocol-implementation.md)。
