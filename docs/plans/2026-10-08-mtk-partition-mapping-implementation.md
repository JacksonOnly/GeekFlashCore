# MTK 特殊分区映射实施记录

日期：2026-10-08。任务：PART-MAP-01。设计见 [特殊分区映射](2026-10-08-mtk-partition-mapping-design.md)。

## 基线和行为

工作区 `C:/Users/a1375/.codex/worktrees/8930/GeekFlashCore`，起始 HEAD `f15697f`，进入时 tracked/untracked 工作区干净，没有 `.tests`。本轮从 `D:/Code/CSharp/GeekFlashCore/.tests` 复制 MTK/CLI 的测试源和 csproj 到当前 ignored `.tests`，没有修改主项目测试或参考仓库。

分区快照新增 PrimaryGPT、BackupGPT、Preloader、Preloader Backup。PGPT/PrimaryGpt、SGPT/BackupGPT、preloader/Preloader、preloader_backup/Preloader Backup 大小写不敏感，统一用于 PartitionTarget、Legacy 命名读/擦除、XFlash/XML 原生命名读写擦除和 scoped native write。普通分区名称保持原样，歧义匹配仍拒绝操作。

主/备 GPT 的普通范围按已验证表头的 FirstUsableLba / LastUsableLba 保留区边界计算，覆盖对应 padding；备份恢复仍提供两项。保留既有 header/entries CRC、位置、容量、重叠/身份验证和损坏后失败逻辑。没有 GPT 时不生成猜测范围；PMT/XML 表中的已知名称经过相同显示映射。eMMC BOOT1/BOOT2、UFS LU0/LU1 分别使用 wire region 1/2 的已报告全容量，缺失区域不生成条目。

分区元数据新增 `NativePartitionName`，保留 `PhysicalPartitionNumber`。CLI read 预检和浏览挂载仅对 MTK 使用原生名和大小写不敏感比较；原生 XML 文件名也使用转换后的 wire name。别名转换后仍执行原名称字符验证，不扩大任意空格/注入字符的允许范围。

同步范围和 native DA 的行为仍各自沿用现有传输顺序、资源和 Sparse/BROM header 语义。没有新增 Legacy native download 或设备执行步骤。gate、取消、代数、写许可、流所有权和缓存失效沿用现有实现；新增结构只保存范围，无按区域容量增长的物化。

## 文件范围

| 文件 | 变更 |
| --- | --- |
| MTK `MtkPartitionNames.cs` | 集中显示名、原生名和别名比较 |
| MTK `MtkProtocol.Storage.cs` | 分区枚举、范围解析和 NativePartitionName |
| MTK `MtkProtocol.Partitions.cs` | 从已验证 GPT 保存两份保留区范围 |
| MTK `MtkProtocol.NamedPartitions.cs`、`MtkProtocol.cs` | native/Legacy/scoped 操作转换名称 |
| Abstractions `IMtkNamedPartitionAccess.cs` | 公共契约说明 |
| CLI `StorageCommands.cs`、`BrowserCommands.cs` | read 预检和挂载识别快照别名 |
| 本设计/记录及原 MTK 实施进度 | 恢复入口、证据和风险 |

没有新增用户可见异常或日志模板，没有改 resx；现有中英文资源路径继续用于错误。没有新增敏感日志或第三方公共类型。

## 测试证据

先写 MTK 新行为测试并修正模拟应答帧后，28 项中 **20 失败 / 8 通过**，失败来自缺失枚举、host 别名范围及 native 名称转换；实现后 28 项全通过。CLI read 预检 8 项先 **5 失败 / 3 通过**，加入元数据和匹配后全通过。

最终新增 MTK **42 项**：eMMC/UFS、512/4096 block、primary/backup 恢复和缓存、八组名称的 host Read/Write/Erase（显式/默认 region）、XFlash/XML native 顺序及传入字节、scoped write、缺区域/无 GPT、歧义/非法名字、预取消/无额外 I/O、NAK 失效/不重试、写后重新发现、Legacy PMT 命名读/擦除和输出流保留。CLI **12 项**：八组名称的实际 read 命令预检及文件输出，四组别名挂载/解析路径。

原 MTK 7 项断言假设枚举结果只有一个普通分区；当前 ignored 副本改为筛选 `boot`，保留原 GPT/PMT 的 CRC、物理位置、回退和缓存断言。其余现有测试没有改动。

| 最终验证 | 结果 |
| --- | --- |
| `dotnet test .tests/GeekFlashCore.Protocol.Mtk.Tests/GeekFlashCore.Protocol.Mtk.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~PartitionMappingTests -v quiet` | 42 通过，0 失败/跳过 |
| MTK 同工程 `--no-build --no-restore -v quiet` 全量 | 601 通过，0 失败/跳过 |
| `dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore -v quiet` 全量 | 118 通过，0 失败/跳过（含 12 项映射） |
| 合计 | **719 通过** |
| `dotnet build GeekFlashCore.slnx -c Release --no-restore -v quiet` | 0 警告/0 错误 |
| `git diff --check`、完整差异/ignored 状态 | 通过；生产改动限上述范围 |
| `git ls-files .tests` | 空；测试和构建产物不受跟踪 |

第一次重新编译 MTK 测试时复制来的 `PenumbraCarbonaraStrategyTests.RecordingChannel.Digest` 有既有 CS0649 警告，本轮未改该无关测试；生产 Release slnx 构建无警告。

## 提交与恢复

独立提交 `feat(mtk): map GPT and preloader partition aliases`，提交号由本轮最终 `git log` 查询，避免文档自引用。提交仅包含生产代码和计划，`.tests`、结果文件、bin/obj 保持 ignored。没有 push、发布或真实设备 I/O。

后续从具体设备的标准 DA、GPT/启动区域几何和脱敏正常抓包验证名称/长度开始。模拟传输确认 host 选择的区域、偏移、长度、native wire name 和状态，不证明每种 DA 都支持这些 native 名称、BROM header 转换或相同保留区语义。两份 GPT 都缺失或损坏时，普通 host 路径仍拒绝推算 GPT 范围；native 命名能力依赖 DA 自己的支持。任何线上未知写结果仍要求重连后检查，不自动重试。
