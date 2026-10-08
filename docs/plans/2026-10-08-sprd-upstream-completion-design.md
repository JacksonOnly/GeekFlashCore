# SPRD 上游常规能力补全设计

日期：2026-10-08；任务：SPRD-05～08；基线 c3267b8，初始工作区干净。

## 范围与兼容

按用户提供两个 GitHub 上游继续完善的授权，补齐显式 GPT 容量来源、Raw v1/v2 下载和 Chip UID 查询及 CLI 接入。沿用同步 ITransport、单 gate、有限预算、Generation 和资源所有权。默认仍为原生分区表及 framed MIDST；既有 API 和默认线路不变。

不引入自动容量试探、NAK 后回退、强制分区重写、NV 变换、重分区、DIAG 重枚举或绕过认证。上游偏移写依赖整分区备份重写，0x49 仅有枚举，没有直接写参数证据；本轮不推断实现。特殊分区 splloader 不凭经验添加容量。

## 参考与顺序

- YC-nw/SPRDClientCore，fb20583c770c141602cf28c6db9c74eb2ef8bf92：Utils/SprdFlashUtils.cs GetPartitionsAndStorageInfo 读取 user_partition 的 32 KiB GPT，EfiTableUtils.cs 换算 LBA；新版没有直接偏移写，使用整分区备份。独立实现，复用本仓 GPT 校验能力，不沿用容量猜测。
- TomKing062/spreadtrum_flash，64fe3e379f23c9e1ba1623964b47a326c66b5081：common.c partition_list/gpt_info 同一 GPT 线路；load_partition 的 START → v1 每窗 0x31(LE64 offset + LE32 length) ACK，或 v2 一次 0x33 ACK → 原始字节/每窗 ACK → END ACK。spd_dump.c 在 FDL2 EXEC/禁转义之后用 0x28 ACK 启用。LibUsb 原始窗口为包大小整倍数时发送 ZLP。chip_uid 使用 0x1a→0xab。
- 不复制上游源码、资源和二进制；克隆保留 ignored temp，记录以上 SHA，不把参考 AGENTS 当作本项目指令。

## 公共契约与边界

SprdPartitionTableSource 默认 Native，新增 UserPartitionGpt；宿主显式提供 GPT sector size 512 或 4096 和读取窗口（默认 32 KiB，上限 4 MiB，整扇区）。只读取确认窗口，要求主头位于 LBA1、条目数组在窗口中；复用 GptParser Strict CRC/完整几何校验，禁止重叠、重复/无效名称，按精确 LBA 数换算字节。仍只返回命名容量，不暴露整盘视图；KnownPartitions 优先且不发查询。无自动回退或容量试探。

SprdRawDataMode 默认 Disabled，显式 Version1/Version2；必须提供确认的 flush 字节数（1 字节～4 MiB）。有 EXEC 能力信息时，版本及 flush 必须一致；直接 FDL2 入口由宿主 profile 确认。USB 传输必须额外提供确认的 bulk 包大小，复用 IUsbTransport.WriteZeroLengthPacket；不从 VID/PID 推断。0x28 只在连接时按显式选项发送；所有 Loader 始终 framed。Raw/Sparse 存储共用固定窗口池化流，Sparse 仍先校验再展开。不完整源、NAK、超时、取消、ZLP/输出失败立即失效，不发 END、不重试、不降级模式。

ReadChipUid 返回独立 byte[]，限制为 1～256 字节，无文本编码或身份推断。门面不缓存、记录或自动查询该标识；CLI 只在用户显式 sprd-chip-uid 命令时显示。新增接口方法提供默认不支持实现，保留原有宿主实现兼容性。

同步/异步入口共用现有门面，原始窗口的写超时由传输保证，操作/命令预算包括写与 ZLP；协议释放打开流及清零池，借用源/传输生命周期保持。

## 实施与验证

1. SPRD-05：先定义 ignored 测试，GPT 512/4096 精确容量、CRC/几何/重复/窗口边界及缓存；核对参考 SHA。
2. SPRD-06：Raw v1/v2 参数/顺序、原始 7e/7d 不转义、末窗、非定位前缀、Sparse、USB ZLP、模式不匹配、NAK/取消/源失效/无重试；UID 长度和会话代数。
3. SPRD-07：CLI profile/help/补全、资源键及 docs/sprd.md/进度来源同步。
4. SPRD-08：全部可用 .tests、Release solution build、git diff --check、资源键和 ignored 审查；按独立能力英文提交生产代码与文档，不提交 .tests/temp。

内存验证使用至少 64 MiB 虚拟 Raw 源和固定窗口，证明分配不随镜像大小增长；模拟不是 USB 吞吐证据。没有实机，FDL Raw 窗口确认、ZLP、GPT user_partition 支持和大容量布局仍待设备验证。
