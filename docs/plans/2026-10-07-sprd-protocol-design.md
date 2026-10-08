# SPRD BSL 协议设计

日期：2026-10-07；任务：SPRD-01；分支：codex/sprd-support。

## 目标、范围与兼容

新增 .NET 8 `GeekFlashCore.Protocol.Sprd.Abstractions` 和 `GeekFlashCore.Protocol.Sprd`，实现通用 `IProtocol`。CLI 为 .NET 10 宿主，使用 registry 接入。正常刷机线路包括 BootROM/FDL1/FDL2、宿主指定 Loader、命名分区枚举/读取/Raw 与 Sparse 写入/擦除、正常重启与关机。允许显式选择已经运行的 FDL1/FDL2；不通过校验失败或 NAK 猜测阶段。

不包含 DIAG 切换、签名绕过、AVB/TrustOS 变换、NV 特殊变换、重分区、原始快速下载及自动容量试探。NV 仍可原样读取；NV 写入和擦除拒绝，避免忽略其专用格式。不假定 NAND 与 eMMC 的物理几何，不把命名分区伪装成整盘。保留既有 ProtocolType 数值，在末尾新增 Sprd。

## 参考证据与线路

只读取用户指定的本地源，按公共线路独立实现，不复制参考源、二进制或平台 DLL。

- `D:/Code/CSharp/SPRDClientCore-Xia/Protocol/Encoders/HdlcEncoder.cs`、`Protocol/CheckSums/CheckSums.cs`：0x7e 帧、0x7d XOR 0x20 转义、BE16 command/length/checksum；BootROM CRC16/XMODEM（初值零），FDL one's-complement 校验。
- `Utils/SprdFlashUtils.cs` 的 SendFile、ExecuteDataAndConnect：START_DATA(BE32 address/length)→分片 MIDST→END→EXEC；FDL1 后 CHECK_BAUD→CONNECT→KEEP_CHARGE；FDL2 EXEC 返回 DA info，随后 DISABLE_TRANSCODE。
- C++ `Bootmode/BMPlatform/BMPackage.cpp`：CHECK_BAUD 是单独的 0x7e；有奇数字节补零的实现，与 C# 不补零不同，提供明确选项；无转义模式必须按解码长度读取，载荷内允许 0x7e。
- C++ `BootModeOpr.cpp` 的响应处理：0x96 是 FDL2 DA info，不能当成普通 ACK；只在 FDL2 EXEC 位置接受且验证信息结构。
- C# partition selector：UTF-16LE name[36] + LE32/LE64 length；C++ 64 位 START 使用 name[36]+LE64 length+reserved[8]。两种布局通过选项选择，不静默切换。
- READ_START→READ_MIDST(LE32 count + LE32/LE64 offset)→READ_END；响应 READ_FLASH 必须与请求长度完全一致。ERASE 使用 name[36]+LE32 zero。
- READ_PARTITION(0x2d)/0xba 为 76 字节记录，72 字节名称+LE32 size。C# 用最小分区猜单位，不可作为容量证据；必须显式指定单位或提供已验证分区清单。

## 公共契约与所有权

`ISprdProtocol` 扩展 IProtocol：同步 Connect/ReadPartition/WritePartition/ErasePartition/GetSprdPartitions/Reboot、状态/目标信息/Generation，以及 `OpenPartition` 只读块设备。`SprdPartition` 是不可变命名容量模型，无猜测物理偏移。通用 PartitionTarget 只支持默认区域；OffsetTarget/SectorTarget 在 I/O 前拒绝。

`SprdConnectionResources` 包含 FDL1/FDL2 的借用 IDataSource、宿主地址，可显式拥有可释放源。同步 Connect 借用容器；异步 Provider 结果把容器所有权交给核心，加载结束释放；取消/超时的迟到结果也释放。核心打开的流均由核心释放，ReadDestination 的输出仍由调用方管理。

所有 wire I/O 同步，Span/ArrayPool 固定上限，公共异步方法只用于 gate/资源获取/宿主编排，不用异步串口循环。Provider 必须迅速返回 ValueTask，后台资源解析由宿主负责。gate 覆盖完整连接（含资源等待）及每次操作，AsyncLocal 拒绝回调重入。会话为 Disconnected→Connecting→BootRom/Fdl1→StorageReady；失败为 Faulted，必须 Disconnect 后重连。Disconnect、失败、重启均推进代数，旧分区视图不能跨会话访问。

## 安全、性能与恢复

连接、单命令、完整操作和资源请求均有有限预算；每次读使用剩余预算，分片间检查取消。传输本身负责同步 Write 的超时；ITransport 无可取消 Write 契约，因此取消延迟受一次写超时约束。没有包重发、自动改校验或清缓存恢复。发出任何字节后的失败（含输出流或进度回调失败）使会话失效并关闭传输；未开始 I/O 的参数错误不失效。借用传输正常断开按所有权处理，wire 失败仍关闭。

帧最大 payload 65535；转义最坏情况 2 倍预分配；增量解码处理碎片与粘包，日志帧数量有限，不记录原始文本。校验、长度、末尾标记、TLV/legacy DA info、分区名称、重复项、容量与 checked 运算均验证。Loader 上限与地址范围在打开传输前验证；写入先确认容量并检测 Sparse，Sparse 复用现有 bounded parser/expanded stream，DONT_CARE 显式写零，不分配镜像大小缓存。FDL1 默认 528 字节，FDL2 默认 4096 字节。

用户可见文本使用中英文 resx；只记录状态和命令/数值，不记录版本原文、完整包、Loader 内容或认证内容。可选禁转义在确认 ACK 后切换；默认关闭，设备/profile 需验证。FDL2 原生能力信息不等于真实硬件验证。

## 文件、步骤、测试与提交

1. SPRD-01：设计、ignored 测试工程；定义固定黄金帧、阶段线路与失败状态。
2. SPRD-02：抽象、帧/预算、同步门面、资源编排和存储；测试取消、超时、碎片/粘包、NAK、不重发、长度/容量、迟到释放、会话代数、Sparse。
3. SPRD-03：registry、CLI 参数/Loader Provider、帮助及使用文档；验证参数冲突与命令解析。
4. SPRD-04：全部本地测试、Release solution build、diff/check/资源键/ignored 审查，按独立能力提交英文 commit。

验证：`dotnet test .tests/GeekFlashCore.Protocol.Sprd.Tests/GeekFlashCore.Protocol.Sprd.Tests.csproj -c Release`；全部可用 `.tests` 工程；`dotnet build GeekFlashCore.slnx -c Release --no-restore`；`git diff --check`；`git status --short --ignored`。

## 未决风险

无 SPRD 实机；所有线路证据为参考源与模拟。FDL 签名/DRAM/板级初始化依赖适配设备的合法 Loader，不能猜地址。奇数补零、FDL2 EXEC ACK/0x96、禁转义时机、64 位 selector 和容量单位需要按设备确认。USB VID/PID 在两个参考源码中没有可靠清单，首版使用显式 --protocol sprd 和 --port/--usb，不按 VID 猜自动识别。CLI browser/LP 的整盘模型暂不适配命名分区；独立 OpenPartition 支持其他宿主复用。
