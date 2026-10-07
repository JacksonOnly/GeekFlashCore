# OFP 分片 Super 直读与刷写

PGT110 等含 `super_def`、LP metadata 和独立逻辑分区镜像的散包使用 [metadata 优先写入路线](oplus-loose-super.md)，不在默认写入前建立整包 Sparse 索引。本文描述同几何 `super.N.*.img` 分片叠加。

`super.0.*.img`、`super.1.*.img` 等文件可能各自声明完整 Super 的逻辑大小。它们的 DontCare 区表示这一分片没有携带该区域，Raw/Fill 区可以重叠。框架按明确的分片顺序叠加：后片数据覆盖前片，DontCare 保留前片数据，所有输入都未覆盖的位置继续保持 DontCare。

输出是虚拟 Sparse 1.0 编码。框架只保存 chunk 区间、来源偏移和 Fill 值，按需生成头并直接读取原分片 payload，没有合并文件、Raw 镜像或按分区尺寸增长的缓存。Qcom 沿用现有 Sparse 计划，只发送实际数据区。

目录打开时只确认分片名称、NV 和范围，完整 Sparse 映射延迟到虚拟条目的 `GetLength(ct)` / `Length` 或 `OpenStream(ct)`，成功后复用。`firmware list` 使用 `KnownLength`，虚拟 `super.img` 的未知长度显示“按需解析”；列目录成功不代表镜像 chunk/CRC 已通过预检。

## 已解包目录

`FirmwareUnpacker.Open(directory)` 返回目录 catalog，自动为同目录连续的 `super.N.hash.img` 添加虚拟 `super.img`。有物理 `super.img` 时优先使用物理文件；缺片或重复索引会失败，子目录的组分别映射。目录枚举跳过 reparse point。

用户给出的目录可直接用于已连接的 CLI 会话：

```text
rawprogram "D:\ROM\PEHM00domestic_11_A.17_2022031122400000\rawprogram0.xml"
```

本地 XML 引用的普通文件照常读取，缺失的 `super.img` 通过同目录 Firmware catalog 解析。没有输出文件产生。也可以显式使用目录包语法：

```text
firmware list "D:\ROM\PEHM00domestic_11_A.17_2022031122400000"
rawprogram "D:\ROM\PEHM00domestic_11_A.17_2022031122400000::rawprogram0.xml"
```

开发者沿用同一 IDataSource 接口：

```csharp
using var firmware = FirmwareUnpacker.Open(unpackedDirectory,
    cancellationToken: cancellationToken);
var script = firmware.GetEntry("rawprogram0.xml");
qcom.ExecuteRawProgram(script,
    name => firmware.ResolveEntry(script.Name, name),
    progress, cancellationToken);
```

## 原始 OFP 与 ZIP 内 OFP

OFP 使用 NVList 的 super0/super1/... 选择有序分片。多个 NV 配置引用相同序列时自动映射；不同序列时开发者必须设置 `FirmwareOpenOptions.OfpSuperNvId`，框架不会猜测。没有 NVList 时仅接受 Super 中可确定的连续索引序列。

OFP 中已有 rawprogram0.xml 等物理文件保持原样。若内部 ProgramList 的 Super 行 filename 为空，生成的 rawprogram.xml 会引用虚拟 `super.img`，目标起点、扇区数与大小不改。

CLI 支持最多八层容器，包内路径保留大小写，父包会一直保持到批次结束：

```text
firmware list "D:\ROM\PEHM00domestic_11_A.17_2022031122400000.zip::PEHM00domestic_11_A.17_2022031122400000/PEHM00domestic_11_A.17_2022031122400000.ofp"
rawprogram "D:\ROM\PEHM00domestic_11_A.17_2022031122400000.zip::PEHM00domestic_11_A.17_2022031122400000/PEHM00domestic_11_A.17_2022031122400000.ofp::rawprogram0.xml"
```

框架开发者显式打开嵌套包并保持父包有效：

```csharp
using var zip = FirmwareUnpacker.Open(zipPath, cancellationToken: cancellationToken);
using var ofp = FirmwareUnpacker.Open(zip.GetEntry(ofpEntryName),
    new FirmwareOpenOptions { OfpSuperNvId = selectedNvId }, cancellationToken);
var script = ofp.GetEntry("rawprogram0.xml");
qcom.ExecuteRawProgram(script,
    name => ofp.ResolveEntry(script.Name, name), progress, cancellationToken);
```

ZIP Deflate 内的 OFP 定位需要从头重放解压，打开目录、预检与跨分片定位有时间成本。Stored/Deflate 使用 .NET 解码器，BZip2 使用内部适配器；CFB 按固定 64 KiB 窗口解密，避免小段读取不断回退外层解压器。没有通过落盘或大型缓存规避这些成本。

ZIP 条目另有固定 128 KiB 回读窗口，避免自动识别、footer 与近尾 metadata 的小范围回读再次解压。本机 PEHM00 的实际 CLI `firmware list ZIP::OFP` 约 29.56 秒，已有解包目录约 0.30 秒；第一次访问压缩 OFP 尾部仍需完整顺序解压一次。该窗口随流释放时清零归还池，不保留整个 OFP，也不跨独立命令缓存 catalog。

## 通用 Sparse API 与边界

`GeekFlashCore.Android.Sparse.SparseImageComposer.Compose` 接受有序的 `Func<CancellationToken, Stream>` 列表，并返回不可变的 `SparseImageComposition`。结果提供 `EncodedLength`、`ExpandedLength`、`BlockSize`、`TotalBlocks`、`ChunkCount` 和 `OpenStream(ct)`，可以在其他宿主中使用：

```csharp
SparseImageComposition image = SparseImageComposer.Compose(
    new Func<CancellationToken, Stream>[]
    {
        ct => first.OpenStream(ct),
        ct => second.OpenStream(ct),
        ct => third.OpenStream(ct)
    }, cancellationToken: cancellationToken);
using Stream sparse = image.OpenStream(cancellationToken);
```

输入几何必须一致。Compose 借用工厂、释放自己打开的解析流；输出流由调用方释放，各流游标和源缓存独立。源需稳定可重开，重开后校验长度。已有源 checksum 会验证；组合后的头不继承各片 checksum，解析成功不作为设备认证证明。

默认最多 16 个来源、262144 个输入/输出 chunk、32 MiB 映射预算，预算按输入 chunk 预留 512 字节，在原 Sparse parser 分配前校验；各限额取先到者。每个输出流最多缓存四个源流，Raw chunk 保持 uint32 编码上限。取消传入解析、checksum、扫线、打开来源、定位与读取。

真实目录只读验证和 ZIP/OFP 证据见 [实施记录](plans/2026-10-07-ofp-sparse-super-implementation.md)。没有执行真实设备刷写；Oplus Digest/VIP 对组合后发送包序的要求需单独验证。
