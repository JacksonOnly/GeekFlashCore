# SPRD / Unisoc BSL

`GeekFlashCore.Protocol.Sprd.Abstractions` 提供 .NET 8 公共契约，`GeekFlashCore.Protocol.Sprd` 实现同步 `ITransport` 上的 BSL。核心可复用于 CLI、桌面和服务；不依赖参考项目的串口发现、WMI 或平台 DLL。`IProtocol` 异步门面与同步 API 共用串行 gate；只有 Loader Provider 等宿主资源边界等待异步结果。

当前实现 BootROM→FDL1→FDL2、自动/显式已加载 FDL 接入、原生/GPT 分区枚举、流式 Raw/Sparse 写入（framed 或显式 Raw v1/v2）、按范围读取、Chip UID、整分区擦除、正常重启和关机。线路由本地参考源、[YC-nw/SPRDClientCore](https://github.com/YC-nw/SPRDClientCore) 和 [TomKing062/spreadtrum_flash](https://github.com/TomKing062/spreadtrum_flash) 的固定版本及模拟传输交叉验证。2026-10-10 已在 iPlay40/ums512 实机验证自动 COM 发现、BootROM CRC 修正握手、两级签名 FDL 上传执行及 StorageReady，也验证了已加载 FDL2 自动重连；存储读写、容量与其他型号仍不能据此视为实机验证。

## CLI

协议名为 `sprd`、`unisoc` 或 `spreadtrum`。Windows 下，无参数 CLI 会从已注册候选自动发现 `SPRD U2S Diag` 下载串口；`--protocol sprd` 将自动扫描/等待限定为 SPRD。当前只确认并注册 `VID_1782&PID_4D00`（`1782:4D00`），不把其他 `1782` 产品或仅名称相似的设备自动归为 BSL。发现依赖实际 COM 端口元数据；VID/PID 仅选协议候选，BootROM/FDL1/FDL2 仍由校验后的握手判断，不自动发送 DIAG 切换包。

可用 `--protocol sprd --port COMx` 手动指定官方驱动串口；显式 `--port` 没有协议时仍沿用 Qualcomm 默认。`--usb 1782:4D00` 可推断为 SPRD 并使用 LibUsb（需对应驱动），其他 USB ID 需显式协议；无 COM 元数据的设备不会被串口自动接管。不会自动安装或替换 SPRD 驱动。显式 SPRD 的设备等待默认 30 秒，可用 `--device-wait-timeout MS` 覆盖，Ctrl+C 取消；无协议的通用自动发现仍沿用持续等待。SerialPort 与 LibUsb 沿用现有传输实现和读写超时；多设备时请用显式端口区分。

```text
geekflash help sprd
geekflash --protocol sprd
geekflash --protocol sprd --device-wait-timeout 60000 connect
geekflash --protocol sprd --port COM7 --loader FDL1.bin --sprd-fdl1-address FDL1_ADDRESS --sprd-fdl2 FDL2.bin --sprd-fdl2-address FDL2_ADDRESS connect
geekflash --protocol sprd --port COM7 partitions all
geekflash --protocol sprd --port COM7 --sprd-entry fdl2 --sprd-partition-source native --sprd-partition-unit UNIT_BYTES read boot boot.img
geekflash --protocol sprd --port COM7 --sprd-entry fdl2 --sprd-partition-source native --sprd-partition-unit UNIT_BYTES write boot boot.img
geekflash --protocol sprd --port COM7 --sprd-entry fdl2 --sprd-partition-source native --sprd-partition-unit UNIT_BYTES erase cache
geekflash --protocol sprd --port COM7 --sprd-entry fdl2 reboot system
geekflash --protocol sprd --port COM7 --sprd-partition-source gpt partitions all
geekflash --protocol sprd --port COM7 --sprd-entry fdl2 sprd-chip-uid
```

`FDL1_ADDRESS` / `FDL2_ADDRESS` 和 `UNIT_BYTES` 必须换成设备已确认的数值，支持 `0x` 地址。不要照搬其他平台的 Loader 或地址。省略入口或 `--sprd-entry auto` 默认自动识别；显式 `brom/fdl1/fdl2` 保持原线路。识别为 BootROM 才需要两个 Loader，FDL1 只需要 FDL2，FDL2 不上传，也不调用 Loader Provider。交互模式识别后会询问缺失文件/地址；非交互自动入口在识别后拒绝缺失的必要资源，显式入口仍在打开传输前预检。Provider 等待默认 30 秒；连接总预算默认 120 秒，包含自动识别和资源等待。`--resource-timeout` 与 `--connect-timeout` 可显式调整且必须为正数。

自动识别先发送单字节 CHECK_BAUD：完整 VERSION 帧必须唯一匹配 CRC16/XMODEM（BootROM）或 FDL checksum（FDL1），随后 CONNECT ACK 确认。FDL checksum 的空 UNSUPPORTED_COMMAND(0xfe) 响应指向已加载 FDL2，随后 DISABLE_TRANSCODE ACK 确认并切换无转义。若首个查询完全无响应且单命令超时，剩余预算内允许一次 FDL checksum CONNECT 查询，仅接受上述 FDL2 特征；未知 ACK/VERSION 不继续猜测或重发。部分帧、坏/歧义校验、日志后超时、取消或总预算耗尽均停止。首包等待受单命令超时限制（核心默认 10 秒，CLI --read-timeout）；特殊 Loader 无明确响应时用手动入口。版本文字和 VID/PID 不决定阶段。

容量来源省略或 `--sprd-partition-source auto` 默认自动选择：优先严格验证一次 GPT 前缀，确认不可用且读会话已结束后才请求原生清单。可显式选择 `gpt` 或 `native`；选择 native 会跳过 GPT 查询。自动选择的是容量来源，不把原生清单声明为 MBR、NAND 或 PMT。

原生 `READ_PARTITION` 的每条记录为名称 + size 数值，没有单位标签；`--sprd-partition-unit BYTES` 是每个 size 单位对应的字节数。`UNIT_BYTES` 是示例占位符，不能原样输入。计算为 `capacityBytes = size * unitBytes`，例如 size=65536、unitBytes=1024 得到 64 MiB，size=64、unitBytes=1048576 也得到 64 MiB。原生模式须按设备确认单位；有效 GPT 无需此参数，直接从验证后的几何计算容量。自动识别到完整原生清单但未配置单位时，报告配置错误、不返回容量，保留会话与已确认来源以供 info/UID；重复调用不重新探测。单位配置不可变，须按已确认单位重新建立配置或重新运行 CLI。显式 native 缺单位仍在查询前拒绝。核心还支持宿主提供已验证的 `KnownPartitions`，不再请求表。不发送反复读失败请求来试探容量，也不合成缺失的 `splloader` 容量。

默认长度布局为 `--sprd-length 32`。只有确认 FDL 支持后才选 `64`（72 字节名称 + LE64 长度，共 80 字节）或 `64-reserved`（另加八字节零保留区，共 88 字节）。64 位布局的 READ_MIDST 使用 LE32 count + LE64 offset。不能表示的范围在写入/READ_START 前拒绝，不静默截断或换线路。

`--sprd-disable-transcode` 显式发送 DISABLE_TRANSCODE，ACK 后才切换编解码。若 FDL2 EXEC 明确报告不支持，则连接失败。自动识别已加载 FDL2 的握手本身协商关闭转义；从 BootROM/FDL1 上传到 FDL2 不隐式关闭，仍由选项决定。`--sprd-entry-transcode-disabled` 只适用于显式 `--sprd-entry fdl2` 且当前已经禁转义的设备，不会再次猜测或协商。选项默认关闭，实际时机需设备验证。

`--sprd-pad-odd` 选择 C++ 工具的偶数字节 profile。为保证声明和实际写入范围一致，该模式拒绝奇数长度 Loader/Raw，而不是补写分区末尾之外的字节；分片大小必须为偶数。默认 profile 保留 C# 参考的原始长度。

## 宿主 API

### 设备发现

`SprdDeviceIdentify` 实现 `IDeviceIdentify`，只匹配完整 `1782:4D00` 元数据，不执行 I/O，也不接受仅名称匹配或整数截断后的 ID。Windows 的公共 UsbWatcher 清单仅返回 `Present = TRUE` 设备，避免已断开的历史 COM 名称；热插拔同时监视实例创建/删除与已有实例 Present 变化。CLI 先订阅再启动监控并复查库存，取消后不返回传输。手动端口打开或随后握手失败仍直接报错，不自动重发设备命令。

`SprdProtocolOptions.EntryStage` 默认 `SprdBootStage.Auto`，既有 BootRom/Fdl1/Fdl2 枚举数值保持。同步 `Connect(resources)` 借用提供的资源，自动识别前验证已提供的 Loader，之后验证实际需要的 Loader；异步 `ConnectAsync` 在识别后将具体阶段交给 Provider。识别后资源失效、超时或取消会关闭传输，必须断开再连接。`TargetInfo.EntryStage` 保存初始阶段，`TargetInfo.Stage` 在成功连接后仍为 Fdl2；断开/失败清空元数据。

### 容量来源

默认 `PartitionTableSource = Auto`，也可显式指定 `Native` / `UserPartitionGpt`，既有枚举数值保持。宿主提供的 `KnownPartitions` 优先于查询来源，核心快照清单，不能把未知容量填为整盘尺寸。`TargetInfo.PartitionTableSource` 在确认查询结果后为 Native 或 UserPartitionGpt，查询前或使用宿主清单时为 null；CLI info 以 `-` 表示未查询。识别成功后缓存清单，断开/失效清除来源及内部原生快照。

Auto 仅在两种情形查询原生表：完整固定前缀经 READ_END ACK 结束且没有标准 GPT 标志；或初始 READ_START 收到有效空 OPERATION_FAILED(0x84)/UNSUPPORTED_COMMAND(0xfe)，随后 READ_END ACK 成功。清理失败、其他拒绝、携带数据的拒绝、坏帧、读取中失败、超时或取消都使会话失效。前缀任一 512/4096 标准位置有 EFI PART，或 MBR 55aa + 0xee 保护分区时，必须通过完整 GPT 校验，损坏/窗口不足/歧义不会降级。没有 GPT 标志只能选择已验证原生清单，不证明某种物理格式。

`Auto` / `UserPartitionGpt` 默认 `GptSectorSize = null`，CLI 省略 `--sprd-sector-size` 或使用 `--sprd-sector-size auto`。只读取一次 `user_partition` 前缀，在内存中校验 512/4096 两种布局；每个候选必须通过完整主头/条目 CRC、几何与名称校验，恰好一个有效才接受，两个均有效或带 GPT 标志但均无效都拒绝。识别成功后 `TargetInfo.GptSectorSize` 返回实际值，并记录数值日志。不通过分别向设备读不同扇区地址来尝试。

手动 `GptSectorSize = 512` 或 `4096`（CLI `--sprd-sector-size 512|4096`）严格覆盖自动识别，校验失败不换另一个值。`GptReadBytes` / `--sprd-gpt-bytes` 默认 32768、最大 4 MiB，必须容纳完整主头和声明的条目数组；自动模式要求窗口为 4096 的整倍数且至少 12288，手动模式为其 sector 的整倍数且至少三个 sector。GPT 容量按精确 LBA 数换算为字节，无需原生单位，也不把结果取整到 MiB。主头必须在 LBA1、CRC 与条目 CRC 正确、可用区间不覆盖主/备份元数据，条目不得越界/重叠/重名。查询结果仅保留命名容量并缓存；不返回整盘块设备，也不推断 NAND/eMMC/UFS 类型。

显式 `UserPartitionGpt` 的窗口之外条目、未知 `user_partition`、NAK 或无效 GPT 立即失效，不切到原生表。自动模式也不扩大窗口试读，仅允许上述明确的只读查询选择。`splloader` 等不在 GPT 中的特殊分区不自动加入。超过 4 GiB 的容量仍可精确枚举，读写前须显式选择适配设备的 64 位 selector，默认 32 位配置仍拒绝超范围操作。

### Raw 下载 profile

默认 `RawDataMode = Disabled` 使用 framed MIDST。确认设备支持后，宿主可显式选择 `Version1` 或 `Version2`，必须指定 `RawDataFlushSizeBytes`（1 字节～4 MiB）；有 FDL2 EXEC 信息时，模式与 `FlushSizeKiB * 1024` 必须精确匹配。直接 FDL2 入口没有 EXEC 信息，由宿主确认 profile。USB 另需确认的 `RawDataUsbPacketSize`（8～1024 的二次幂）；为包大小整倍数的窗口发送一个 ZLP，普通空 Write 不充当 ZLP。

CLI 对应 `--sprd-raw-mode off|v1|v2 --sprd-raw-flush BYTES` 和 USB 下的 `--sprd-raw-usb-packet BYTES`。这些数值无通用推荐值，需与实际 FDL/endpoint 一致。连接在 FDL2/禁转义后发送 ENABLE_RAW_DATA；Loader 保持 framed。v1 每窗发送 LE64 offset + LE32 length 的 0x31 并等待 ACK；v2 发送一次 0x33 ACK；随后窗口原样发送、不转义不补零，并逐窗等 ACK，全部完成后才 END。池化窗口上限固定，不按镜像大小分配。

Raw/Sparse 仍使用同一容量和源稳定性校验，Sparse 仍预检后展开，DONT_CARE 写零。Raw 模式收到 NAK/超时/取消或源/ZLP 失败后不降级为 framed，不重发、不发送收尾命令。`PadOddPayloads` 的既有奇数 Loader/Raw 拒绝规则保持；禁转义只控制 BSL 帧，原始窗口始终原样发送。

### Chip UID

`ReadChipUid(ct)` 显式发送 0x1a，要求 0xab 和 1～256 字节，返回独立数组，无字符编码、身份推断、缓存或自动日志。CLI 只在 `sprd-chip-uid` 命令中输出十六进制；普通 connect/info 不触发查询。接口有默认不支持实现，保留原有宿主实现的兼容性。

### 连接与存储

自动入口的首次 CHECK_BAUD 默认仅等待 500 ms，`EntryProbeTimeoutMilliseconds` 可由宿主覆盖，且仍受单命令和连接总预算封顶；后续命令保留原超时。仅零字节超时允许一次 FDL CONNECT 查询。完整空 VERIFY_ERROR(0x008b) 唯一匹配 CRC16 时，发送一次 CRC16 CONNECT，匹配空 ACK 后才接受 BootROM；不接受带数据的拒绝、FDL 校验拒绝或坏/歧义帧，不重发上传/存储写命令。日志只报告查询/响应号、长度和校验算法，拒绝信息不会输出原始设备载荷。

BootROM EXEC ACK 后默认等待 `Fdl1StartDelayMilliseconds = 500` 再执行一次 FDL1 握手；等待计入连接总预算且可取消，0 可禁用，最大10000。FDL2 上传使用独立 `Fdl2BlockSize = 528`，不再使用存储的 `TransferBlockSize = 4096`；确认设备支持后宿主可分别覆盖，旧宿主仅修改 TransferBlockSize 将只影响存储。FDL2 元数据支持原有 v1/v2 与 TLV，以及实机确认的 legacy v4 **精确256字节**布局；未知版本/截断/无效标志仍拒绝。元数据能力不自动启用 Raw 或禁转义。

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

同步 Connect 借用资源容器。默认容器借用 IDataSource，核心只释放自己打开的流。`ownsSources: true` 明确转移可释放源的所有权；共享同一源只释放一次。`ISprdLoaderProvider.GetLoadersAsync` 必须迅速返回 ValueTask，遵守取消，并返回由核心拥有的容器。成功、失败、取消以及超时后的迟到结果均按容器所有权释放；核心不会等待不合作的 Provider 无限返回。显式入口在打开前获取并验证实际需要的 Loader；Auto 先打开并识别阶段，再请求对应资源，FDL2 不调用 Provider。Auto 在握手后缺资源或等待失败会关闭传输并失效，必须断开重连。输出流始终借用；`ReadDestination.OwnsStream` 由调用方 Dispose 处理。

Raw 支持可定位及不可定位的可读源，前四字节识别后仍完整保留。Sparse 要求稳定可定位源，使用现有 Sparse parser（最多 262144 chunks）预检结构和 CRC，再通过 expanded stream 分片；DONT_CARE 显式写零，不能理解为保留设备旧数据。Sparse 元数据内存受 chunk 上限约束，数据缓存不随镜像大小增长。预检、校验及每个分片均检查取消和总预算。

## 失败与限制

NAK、坏帧、校验错误、响应长度错误、传输超时或传输开始后的取消/源/输出失败使会话进入 Faulted，并关闭传输；未知写入结果不重试。先 Disconnect，再用正确入口重新连接。参数预检失败且未发生 I/O 时保留会话。所有状态、读写阶段和错误使用资源化日志；原始设备版本、设备日志帧、Loader 和完整包均不自动记录。

正常 `leaveTransportOpen: true` 断开/释放保留传输供宿主管理；wire 失败仍关闭。自定义 ITransport 必须提供有界同步 Write，因通用接口没有取消 Write，取消最多需要等当前写超时。只读块设备契约没有取消参数，使用有限 OperationTimeout；需要逐次取消时使用 ReadPartition。

NV（名称含 nv）写入与擦除、NV 格式修复、镜像签名处理、诊断模式切换、重分区及直接偏移写未实现。正常复位仅支持 `ProtocolRebootMode.System` / `PowerOff`。FDL 签名、DRAM 和板级初始化由合法匹配 Loader 完成，本模块不提供绕过。硬件兼容、GPT 前缀、Raw flush/USB ZLP、USB 重枚举、特殊首包与实际吞吐待设备验证。

初版见 [设计](plans/2026-10-07-sprd-protocol-design.md) 与 [实施记录](plans/2026-10-07-sprd-protocol-implementation.md)；最新能力与恢复来源见 [上游补全设计](plans/2026-10-08-sprd-upstream-completion-design.md) 和 [实施记录](plans/2026-10-08-sprd-upstream-completion-implementation.md)。
