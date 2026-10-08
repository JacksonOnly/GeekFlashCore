# MTK 特殊分区映射

日期：2026-10-08。任务：PART-MAP-01。

用户要求分区获取、Read/Write/Erase 加入 PGPT、SGPT 与 PrimaryGPT、BackupGPT 的对应，以及 Preloader、Preloader Backup 的映射。本轮范围包括 MTK 分区快照、PartitionTarget、Legacy 命名读/擦除、XFlash/XML 原生命名读写擦除及 scoped native write。CLI read 的预检和浏览挂载同步识别快照中的别名。

| 列表名称 | DA 原生名称 / 接受的别名 | 普通范围 |
| --- | --- | --- |
| PrimaryGPT | PGPT、PrimaryGpt（大小写不敏感） | user region 的 `[0, FirstUsableLba × BlockSize)` |
| BackupGPT | SGPT、BackupGPT（大小写不敏感） | user region 的 `[(LastUsableLba + 1) × BlockSize, region.Length)` |
| Preloader | preloader（大小写不敏感） | eMMC BOOT1 / UFS LU0，即 wire region 1 的全范围 |
| Preloader Backup | preloader_backup（大小写不敏感） | eMMC BOOT2 / UFS LU1，即 wire region 2 的全范围 |

GPT 两项只在通过现有 header/entries CRC、位置、容量和重叠检查后生成，采用表头的保留区边界，包含其中的 padding；不硬编码参考项目的 32 KiB。主表恢复到备份表后仍可计算两项。两份都损坏时仍使会话失败；没有 GPT 时不猜测 GPT 范围。启动区域按已报告容量生成，零容量或缺失区域不生成条目。PMT/XML 表中的已知名称也统一显示；普通名称保持原样。存在同名或别名冲突时继续要求唯一匹配，不静默选择一个范围。

参考本地 `D:/Code/Rust/penumbra-main/core/src/da/storage.rs` 的辅助分区、`storage/emmc.rs` 与 `storage/ufs.rs` 的启动区域归属，仅作为源码证据。现有 host 读写仍使用同步范围 I/O；native 操作只转换名称，保持 DA 的 download info、XML lifetime、Sparse/BROM header 处理和能力拒绝。不会在分区枚举期间执行写入或擦除，也不新增 Legacy native download 线路。

统一映射集中于内部 `MtkPartitionNames`，不增加公共方法或更改类型签名。`PartitionInfo.Metadata` 增加 `NativePartitionName`，与现有 `PhysicalPartitionNumber` 一起供宿主匹配；CLI 仅对 MTK 使用此元数据和大小写不敏感比较。名称先映射再通过既有允许字符校验，只允许已知的 `Preloader Backup` 含空格；其他名称、XML 注入字符仍拒绝。

会话 gate、取消/预算、代数、资源所有权、写许可、流式处理、失败失效及写后缓存失效沿用现有机制。新增几何仅保存范围，无按区域容量分配。生产改动限于 MTK Storage/Partitions/NamedPartitions、scoped write、一份内部映射、命名契约说明、CLI StorageCommands/BrowserCommands 和计划记录。没有新异常/日志模板或资源键。

验证矩阵：eMMC/UFS、512/4096 block、primary/backup GPT、无 GPT/缺启动区域、别名与普通名称/冲突/显式 region、host Read/Write/Erase、XFlash/XML native 操作、Legacy 与 scoped write、预取消/NAK/缓存失效/输出所有权，以及 CLI read 和挂载。测试先复现，再修改；测试位于 ignored `.tests`。运行 MTK/CLI 目标和全量测试、Release slnx 构建、`git diff --check` 与 ignored 状态审查。单独提交 `feat(mtk): map GPT and preloader partition aliases`。

没有真实设备 I/O。不同 DA 的原生命名支持、BROM header 转换以及保留区域语义仍需真机确认；模拟证据不得标为硬件支持。
