# Oplus 散包 Super 的 metadata 优先写入

PGT110 这类包提供 `META/super_def.*.json`、紧凑 LP metadata blob 和多份逻辑分区镜像。它们通过 LP extent 组成 Super，与 `super.0.*.img` 的同几何叠加不同。

默认入口 `FirmwareSuperImagePlan.Open` 只读取配置、LP metadata、路径引用和各镜像固定头。它保留包内有效 blob 的 metadata 大小、槽位、Virtual A/B、groups、空 B 分区和 extent；没有 blob 时按 JSON 创建，默认 metadata 65536、2 slots、4096 字节块、1 MiB 对齐和 readonly 分区。多个 `super_def` 必须显式选择，不猜 NV。

写入先发送小型 geometry/各槽主备 metadata，再逐个打开分区镜像。Sparse 库前向读取 chunk，Raw/Fill 按 LP extent 换算目标位置并直接发送，DontCare 不写。分区流只消费一次，不创建完整 Super chunk 索引、合并文件、Raw 镜像、整分区数组或磁盘缓存。

## CLI

离线查看配置，不扫描 payload：

```text
geekflash firmware super-info "D:\BaiduNetdiskDownload\PGT110domestic_11_14.0.0.550CN01_2024090919340129.zip::META/super_def.10010111.json"
```

已连接的 Qcom 或 MTK CLI 会话直接写 Super：

```text
write super "D:\BaiduNetdiskDownload\PGT110domestic_11_14.0.0.550CN01_2024090919340129.zip::META/super_def.10010111.json"
```

末尾可显式加 `[lun]`，目标必须能唯一确定且足以容纳整个 LP 布局。目录包和嵌套容器也可使用相同 `::` 语法。普通包内镜像可用 `write boot_a "rom.zip::IMAGES/boot.img"`。

CLI 对计划使用 Qcom 显式 Raw Program、MTK 原始 Write，避免进入协议的 Sparse 全包预检。Raw 指发给设备的有界数据窗口，不是转换后的 Raw 镜像。进度按实际发送字节计数，消费前总量未知，完成时报告最终值；最多每 100 ms 或区域结束更新。

## SDK

宿主先连接、认证并校验目标 Super 起点、LUN、容量和块对齐，然后调用同步写入回调。Qcom 示例：

```csharp
using GeekFlashCore.Firmware;
using GeekFlashCore.Protocol.Qcom.Abstractions;

using var package = FirmwareUnpacker.Open(zipPath,
    cancellationToken: cancellationToken);
var plan = FirmwareSuperImagePlan.Open(package,
    "META/super_def.10010111.json", cancellationToken);

long sent = plan.Write((region, source) =>
{
    qcom.Program(new FirehoseProgramRequest
    {
        Source = source,
        Format = FirehoseProgramFormat.Raw,
        PhysicalPartitionNumber = lun,
        StartSector = superStartSector + region.OutputOffset / sectorSize,
        SectorCount = region.Length / sectorSize,
        SectorSizeInBytes = sectorSize,
        Label = "super"
    }, cancellationToken: cancellationToken);
}, cancellationToken);
```

MTK 回调使用相同计划：

```csharp
plan.Write((region, source) =>
{
    using Stream input = source.OpenStream();
    mtk.Write(new MtkFlashRange(regionId,
        superStartByte + region.OutputOffset, region.Length),
        input, cancellationToken);
}, cancellationToken);
```

回调输入是显式 Raw 的临时 `IDataSource`，允许打开一次、完整前向读取；回调返回后源和流失效。Qcom 会释放自己打开的流，MTK 示例由宿主释放；底层包由宿主保持有效。宿主应串行化整个写入，失败立即停止，不自动重试。

需要完整预检和可定位 Sparse 流时，显式使用 `plan.CreateSparseImage(ct)` 或 `FirmwareSuperImage.Open(package, definition, ct)`。这条路线扫描所有 chunk，ZIP Deflate 会完整解压一次，再在写入时读取一次，适合需要索引或 rawprogram resolver 的调用方：

```csharp
var image = plan.CreateSparseImage(cancellationToken);
qcom.ExecuteRawProgram(script,
    name => name == "super.img" ? image : package.ResolveEntry(script.Name, name),
    progress, cancellationToken);
```

LP 的通用入口为 `LpSuperImageLayout.ReadMetadataBlob` / `Create` / `OpenMetadataStream` / `CreateSparseImage`；Sparse 的位置映射入口为 `SparseImageComposer.ComposeLayout`，前向入口为 `SparseSequentialReader.TryReadHeader` / `ReadData`。公共 API 不暴露第三方解码器或 CRC 类型。

## 边界和证据

当前支持单设备、同 Sparse/LP 块大小、块对齐 linear extents。路径只能在包内，最多八次引用；重复字段、配置/blob 不一致、缺图、错误固定头和容量在写入前拒绝。chunk 范围、payload 长度及累计 CRC 随消费检查；损坏尾部可能在前面的数据已写入后才发现。CLI 会停止并关闭部分写入会话，SDK 宿主承担相同清理职责。

原始 LP blob 会按其 geometry 复制为每槽主备 metadata，保留零尺寸 B 分区。没有填满未分配空间或清零 DontCare 的行为。厂商 Digest/VIP/认证仍由原协议策略负责；新增每 chunk 的命令边界尚无真实设备证据。

真实 PGT110 包的计划建立从完整索引路线的 44.63 秒降为 0.004 秒，首 metadata 回调约 0.3 ms；单次前向读取 11339939840 字节约 42.14 秒、256.63 MiB/s，读取累计分配约 5.7 MiB，没有输出镜像。较早隔离测试进程峰值约 78 MiB，最新全套回归进程峰值约 124 MiB（含其他格式夹具），两者都不是单计划 live heap。这是本机离线数据，不能当作设备速度保证。完整测试、测量条件和硬件风险见 [实施记录](plans/2026-10-07-oplus-loose-super-implementation.md)。
