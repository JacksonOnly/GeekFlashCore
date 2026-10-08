# SPRD / Unisoc BSL

`GeekFlashCore.Protocol.Sprd.Abstractions` 提供 .NET 8 公共契约，`GeekFlashCore.Protocol.Sprd` 实现同步 `ITransport` 上的 BSL。核心可复用于 CLI、桌面和服务；不依赖参考项目的串口发现、WMI 或平台 DLL。`IProtocol` 异步门面与同步 API 共用串行 gate；只有 Loader Provider 等宿主资源边界等待异步结果。

当前实现 BootROM→FDL1→FDL2、显式已加载 FDL 接入、原生/GPT 分区枚举、流式 Raw/Sparse 写入（framed 或显式 Raw v1/v2）、按范围读取、Chip UID、整分区擦除、正常重启和关机。线路由本地参考源、[YC-nw/SPRDClientCore](https://github.com/YC-nw/SPRDClientCore) 和 [TomKing062/spreadtrum_flash](https://github.com/TomKing062/spreadtrum_flash) 的固定版本及模拟传输交叉验证，尚无 SPRD 实机证据。

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
geekflash --protocol sprd --port COM7 --sprd-entry fdl2 --sprd-partition-source gpt --sprd-sector-size CONFIRMED_SECTOR_BYTES partitions all
geekflash --protocol sprd --port COM7 --sprd-entry fdl2 sprd-chip-uid
```

`FDL1_ADDRESS` / `FDL2_ADDRESS` 和 `UNIT_BYTES` 必须换成设备已确认的数值，支持 `0x` 地址。不要照搬其他平台的 Loader 或地址。后续示例假定设备仍在 FDL2；未加载时继续使用完整 Loader 参数。交互模式缺失文件/地址会询问；非交互模式缺失必要参数在创建传输前失败。Provider 等待默认 30 秒；连接总预算默认 120 秒。`--resource-timeout` 与 `--connect-timeout` 可显式调整且必须为正数。

原生 `READ_PARTITION` 响应不提供可验证的容量单位，因此 CLI 存储命令要求 `--sprd-partition-unit BYTES`，例如只有确定表中值以 MiB 计数时才填 `1048576`。核心还支持宿主提供已验证的 `KnownPartitions`，不再请求原生表。不发送反复读失败请求来试探容量，也不合成缺失的 `splloader` 容量。

默认长度布局为 `--sprd-length 32`。只有确认 FDL 支持后才选 `64`（72 字节名称 + LE64 长度，共 80 字节）或 `64-reserved`（另加八字节零保留区，共 88 字节）。64 位布局的 READ_MIDST 使用 LE32 count + LE64 offset。不能表示的范围在写入/READ_START 前拒绝，不静默截断或换线路。

`--sprd-disable-transcode` 显式发送 DISABLE_TRANSCODE，ACK 后才切换编解码。若 FDL2 EXEC 明确报告不支持，则连接失败。`--sprd-entry-transcode-disabled` 只适用于入口已在 FDL2 且当前已经禁转义的设备，不会再次猜测或协商。两项默认关闭，实际时机需设备验证。

`--sprd-pad-odd` 选择 C++ 工具的偶数字节 profile。为保证声明和实际写入范围一致，该模式拒绝奇数长度 Loader/Raw，而不是补写分区末尾之外的字节；分片大小必须为偶数。默认 profile 保留 C# 参考的原始长度。

## 宿主 API

### 容量来源

默认 `PartitionTableSource = Native`，仍需显式 `PartitionTableSizeUnitBytes`。宿主提供的 `KnownPartitions` 优先于两种查询来源，核心快照清单，不能把未知容量填为整盘尺寸。

选用 `UserPartitionGpt` 时需提供确认的 `GptSectorSize = 512` 或 `4096`（CLI `--sprd-partition-source gpt --sprd-sector-size BYTES`）；读取 `user_partition` 的确认前缀窗口，`GptReadBytes` / `--sprd-gpt-bytes` 默认 32768，最大 4 MiB，必须整扇区且容纳完整主头和声明的条目数组。GPT 容量按精确 LBA 数换算为字节，无需原生单位，也不把结果取整到 MiB。主头必须在 LBA1、CRC 与条目 CRC 正确、可用区间不覆盖主/备份元数据，条目不得越界/重叠/重名。查询结果仅保留命名容量并缓存；不返回整盘块设备，也不推断 NAND/eMMC/UFS 类型。

窗口之外的条目、未知 `user_partition`、NAK 或无效 GPT 立即失效，不自动切到原生表，不增加窗口试读。`splloader` 等不在 GPT 中的特殊分区不自动加入。超过 4 GiB 的容量仍可精确枚举，读写前须显式选择适配设备的 64 位 selector，默认 32 位配置仍拒绝超范围操作。

### Raw 下载 profile

默认 `RawDataMode = Disabled` 使用 framed MIDST。确认设备支持后，宿主可显式选择 `Version1` 或 `Version2`，必须指定 `RawDataFlushSizeBytes`（1 字节～4 MiB）；有 FDL2 EXEC 信息时，模式与 `FlushSizeKiB * 1024` 必须精确匹配。直接 FDL2 入口没有 EXEC 信息，由宿主确认 profile。USB 另需确认的 `RawDataUsbPacketSize`（8～1024 的二次幂）；为包大小整倍数的窗口发送一个 ZLP，普通空 Write 不充当 ZLP。

CLI 对应 `--sprd-raw-mode off|v1|v2 --sprd-raw-flush BYTES` 和 USB 下的 `--sprd-raw-usb-packet BYTES`。这些数值无通用推荐值，需与实际 FDL/endpoint 一致。连接在 FDL2/禁转义后发送 ENABLE_RAW_DATA；Loader 保持 framed。v1 每窗发送 LE64 offset + LE32 length 的 0x31 并等待 ACK；v2 发送一次 0x33 ACK；随后窗口原样发送、不转义不补零，并逐窗等 ACK，全部完成后才 END。池化窗口上限固定，不按镜像大小分配。

Raw/Sparse 仍使用同一容量和源稳定性校验，Sparse 仍预检后展开，DONT_CARE 写零。Raw 模式收到 NAK/超时/取消或源/ZLP 失败后不降级为 framed，不重发、不发送收尾命令。`PadOddPayloads` 的既有奇数 Loader/Raw 拒绝规则保持；禁转义只控制 BSL 帧，原始窗口始终原样发送。

### Chip UID

`ReadChipUid(ct)` 显式发送 0x1a，要求 0xab 和 1～256 字节，返回独立数组，无字符编码、身份推断、缓存或自动日志。CLI 只在 `sprd-chip-uid` 命令中输出十六进制；普通 connect/info 不触发查询。接口有默认不支持实现，保留原有宿主实现的兼容性。

### 连接与存储

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

NV（名称含 nv）写入与擦除、NV 格式修复、镜像签名处理、诊断模式切换、重分区及直接偏移写未实现。正常复位仅支持 `ProtocolRebootMode.System` / `PowerOff`。FDL 签名、DRAM 和板级初始化由合法匹配 Loader 完成，本模块不提供绕过。硬件兼容、GPT 前缀、Raw flush/USB ZLP、USB 重枚举、特殊首包与实际吞吐待设备验证。

初版见 [设计](plans/2026-10-07-sprd-protocol-design.md) 与 [实施记录](plans/2026-10-07-sprd-protocol-implementation.md)；最新能力与恢复来源见 [上游补全设计](plans/2026-10-08-sprd-upstream-completion-design.md) 和 [实施记录](plans/2026-10-08-sprd-upstream-completion-implementation.md)。
