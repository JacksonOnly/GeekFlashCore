# 固件目录与流式解包

`GeekFlashCore.Firmware` 是独立的 .NET 8 模块。解包返回只读目录，每个 `FirmwareEntry` 本身实现 `IDataSource`；CLI、桌面宿主和框架开发者都可以直接消费包内的明文流。未来 Packer 可在此领域增加独立契约，目前仅提供解包。

## CLI

离线命令不需要设备：

```text
geekflash firmware list "D:\ROM\firmware.ofp"
geekflash firmware extract "D:\ROM\firmware.ofp" "boot.img" "D:\Output\boot.img"
```

`extract` 只导出指定条目到显式指定的新文件，已有目标文件不会被覆盖。目录保留大小写与完整相对路径；同名条目可按 `Entries[index]` 读取，按名称获取则拒绝歧义。

对已经连接的 Qcom 会话，交互式 CLI 可以运行：

```text
rawprogram "D:\ROM\firmware.ofp::rawprogram.xml"
patch "D:\ROM\firmware.ofp::patch.xml"
rawprogram "D:\ROM\firmware.zip::images/rawprogram*.xml"
```

非交互调用沿用原 Loader、连接和认证配置：

```text
geekflash --port COM73 --loader programmer_firehose.mbn rawprogram "D:\ROM\firmware.ofp::rawprogram.xml"
geekflash --port COM73 --loader programmer_firehose.mbn patch "D:\ROM\firmware.ofp::patch.xml"
```

先用 `firmware list` 确认脚本名称。OFP Qcom 的 ProgramList/PatchList 可生成 `rawprogram.xml`、`patch.xml`，Super 可生成 `rawprogram_super.xml`；OPS 的 ProgramN/PatchN 生成 `rawprogramN.xml`、`patchN.xml`。包内已有对应文件时优先使用文件。虚拟脚本只保留标准 Firehose 属性，去掉容器定位元数据。框架不会自动执行整个包或自动执行后续 patch。

脚本中的镜像路径基于该脚本在包内的目录解析。例如 `images/rawprogram0.xml` 引用 `boot.img`，会获取 `images/boot.img`。禁止绝对路径、驱动器、控制字符与 `.`/`..`；缺少镜像立即失败，不回落读取本地同名文件。显式参数顺序保留，同一参数内的通配结果按条目路径排序，最多 1024 个脚本。

## 开发者调用

```csharp
using GeekFlashCore.Firmware;
using GeekFlashCore.Protocol.Qcom.Abstractions;

// qcom is an already configured/connected IQcomProtocol owned by the host.
using FirmwarePackage package = FirmwareUnpacker.Open("firmware.ofp",
    cancellationToken: cancellationToken);

FirmwareEntry rawprogram = package.GetEntry("rawprogram.xml");
FirehoseScriptResult written = qcom.ExecuteRawProgram(rawprogram,
    name => package.ResolveEntry(rawprogram.Name, name),
    progress, cancellationToken);

FirehoseScriptResult patched = qcom.ExecutePatchFile(
    package.GetEntry("patch.xml"), progress, cancellationToken);
```

也可使用 `FirmwareUnpacker.Open(IDataSource, options, ct)` 接入宿主的稳定、可定位、可重复打开的数据源。没有 HTTP 下载、异步 Provider 等待或设备连接隐藏在 Firmware 模块中。

```csharp
using Stream image = package.GetEntry("boot.img").OpenStream(cancellationToken);
image.Seek(1024, SeekOrigin.Begin);
image.ReadExactly(buffer);

// Reopenable independent streams; export only when the host wants a file.
using var output = File.Create("boot.img");
package.GetEntry("boot.img").CopyTo(output, cancellationToken: cancellationToken);
```

容器嵌套是显式的：

```csharp
using var ota = FirmwareUnpacker.Open("ota.zip");
using var payload = FirmwareUnpacker.Open(ota.GetEntry("payload.bin"));
IDataSource boot = payload.GetEntry("boot.img");

using var kdz = FirmwareUnpacker.Open("firmware.kdz");
using var dz = FirmwareUnpacker.Open(kdz.GetEntry("firmware.dz"));
```

原始 OZIP 返回 `decrypted.zip` 条目，可以再对该条目调用 `Open`。含加密条目的 ZIP 直接提供解密后的文件。ZIP 中的 payload 不会被隐式展开。

## 格式与限制

| 格式 | 已实现行为 | 明确边界 |
| --- | --- | --- |
| ZIP | Stored、Deflate、BZip2；独立重开与定位 | 不支持密码 ZIP、分卷或其他压缩方法 |
| OZIP | 原始间隔 ECB ZIP、ZIP 内带分块头的加密文件 | 只使用参考项目的固定候选密钥；未知密钥报错 |
| OFP Qcom | 512/4096 尾页、元数据 CFB128、加密前缀及明文尾、虚拟 XML | 厂商变体需真实固件验证 |
| OFP MTK | 尾部 shuffle 目录、各条目 CFB128 前缀 | 不涉及 MTK 设备认证或执行 |
| OPS | 三种 MBox、反馈解密、程序/补丁虚拟文件 | 根据参考格式保持 4 字节物理补齐 |
| PAC | UTF-16 目录、目录偏移与 64 位范围 | 不实施设备刷写线路 |
| KDZ/DZ | KDZ 子容器、DZ zlib chunk | DZ 输出独立 chunk，不自动合并分区 |
| UPDATE.APP | 98 字节记录、HeaderSize、4 字节对齐 | 参考布局从偏移 92 开始 |
| Android payload v2 | REPLACE、REPLACE_BZ、REPLACE_XZ、参考扩展 ZSTD、ZERO/DISCARD；全部目标 extents | 拒绝增量/依赖旧镜像操作、覆盖重叠和缺失区间；DISCARD 导出为零 |

公共契约不暴露第三方类型。默认元数据上限 32 MiB、目录 65536 项、操作/extent 或 OZIP 描述 262144 项、复制缓冲 64 KiB。payload XZ 字典/Zstandard 窗口默认不超过 64 MiB，可通过 `FirmwareOpenOptions` 有界调整。尾部补零仅允许 payload 最后不足一块的已解码数据；解码超长或提前截断均失败。

Raw、OFP/OPS 及间隔 ECB 流直接定位。压缩流通过重开与顺序解码提供 `Seek`，向后定位会花费重新解压的时间；ZIP 包内多次打开也需要重读目录。没有按整镜像或整 operation 分配的缓存，没有临时解包目录。第三方解码器仍有固定窗口与缓冲成本。

## 生命周期与验证证据

输入 `IDataSource` 属于宿主；模块只释放自己打开的流。每个条目流由调用方释放，同一条目可以同时打开独立流，单个流不支持并发使用。包释放后旧条目和已开流都不能再读，调用方仍应释放已开流；嵌套包也必须在外层包有效期间使用。内部加密密钥和池化解密缓冲在释放时清零。

`OpenStreamAsync` 是同步离线数据源的契约适配，不等待异步资源。解析、读取、解码跳过和复制检查取消，包打开时的 token 和各条目打开时的 token 都有效。Qcom 的预检、XML 验证、同步 Raw/Sparse 写入、ACK/NAK 和会话失效行为沿用原实现。

本轮覆盖合成格式夹具、参考 OPS 变换向量及模拟 Qcom 传输；另只读验证真实 4.5 GB PAC 的 40 个条目，以及超过 4 GB 的 `super.img` 在起点、2 GiB 以上和尾部的读取。没有执行真实设备刷写，其他厂商格式仍待真实包验证。解包成功也不代表 payload 签名、镜像 hash 或设备认证通过；本模块未执行这些信任判断。具体测试命令和结果见 [实施记录](plans/2026-10-07-firmware-streaming-implementation.md)。
