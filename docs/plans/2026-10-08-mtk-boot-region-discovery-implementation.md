# MTK Boot 区域发现实施记录

BOOT-GPT-01：2026-10-08启动，2026-10-09完成，起点 `6186ea8`。[设计](2026-10-08-mtk-boot-region-discovery-design.md)。用户追加要求核对XFlash ReadData是否错误选择BOOT1/2而非User。

## 源码与实机事实

1. XFlashSession.FlashParams依次发送Kind、WireId、offset、length，与参考FlashOpParams/flash.rs相同。UFS LU0/LU1/LU2分别wire1/2/3，User为3；eMMC BOOT1/2/User为1/2/8。解析与默认Offset/Sector/Block目标正确，不应修改FlashParams或将UFS User改成8。
2. 当前LoadPartitionsCore原来遍历所有普通区域，添加Preloader之后仍探测Boot头尾GPT。用户233705抓包中实际读区域1，属于这条不必要的枚举路径。Penumbra da/storage.rs直接用已报告容量生成辅助Preloader条目，再获取User GPT，不读Boot头尾。
3. 这不否定DA-ZLP-01：抓包已完整收到8192字节，宿主ACK后成功0字节IN被旧代码判EOF。User数据读取同样可能有ZLP，零包与区域发现分别修复，不能删除正常ACK状态等待或将USB0当操作成功。

## 实现

- MtkProtocol.Storage：eMMC/UFS wire1/2仅生成全容量Preloader/backup并continue，无额外I/O。缺失区域不猜测。其他非Boot区域保留独立GPT能力，eMMC GP未被误删；User的PrimaryGPT/BackupGPT和普通分区继续严格解析。
- 显式Boot raw/命名/别名读写擦除保持原区域，不把所有操作强制路由User。缓存、Session gate/代数、主备恢复、CRC/坏表失败、Legacy PMT、XML/NAND与流所有权不改，不增加命令重发。
- Storage摘要包含发现开始的介质/User ID和完成条目数；Partitions在实际GPT读取前Debug记录介质/region/User标志，Boot Debug说明只使用容量不读GPT。四个中英资源，无原始数据/认证材料/私密ID。
- XFlashSession、CLI、公共API、exp及其他协议未改。枚举入口包括CLI partitions all、host PartitionTarget及scoped GetPartitionRanges，共用这次修正。

## 测试与验证

ignored BootRegionDiscoveryTests初始6项：仅提供User GPT应答的4个eMMC/UFS 512/4096用例失败，缺Boot的2项通过。最小修复后6/6；最终44项，联合旧PartitionRecovery/ExtendedStorage/LegacyDiskPmt共71项通过。

新测试核对所有56字节flash参数的介质/区域/偏移/长度，User GPT首/数组两次读取、Boot仅元数据、无/单侧Boot、缓存、默认Offset目标、明确Boot/User raw读写擦除、四组Preloader别名、scoped入口、eMMC GP保留、User备份恢复/坏CRC/NAK失效及预取消。日志测试确认真实User3、两Boot跳过日志、两Information摘要且不输出原始分区数据。

旧ignored夹具仅移除专供Boot头尾GPT探测的模拟应答：PartitionRecoveryTests的NoGptBoot、ExtendedStorageTests一处调用、LegacyDiskPmtTests一处Boot头尾数据；原User CRC/物理位置/恢复/失败断言保留。没有修改旧Linecode策略或缺失DA测试，所有测试/结果继续ignored不提交。

| 检查 | 结果 |
| --- | --- |
| 新区域目标 / 联合分区回归 | 44/44；71/71 |
| 完整MTK TRX boot-region-discovery-full.trx | 783项，768通过/15旧失败，名称与零包基线相同 |
| 可运行MTK | 768/768 |
| CLI Release | 168/168 |
| solution Release / CLI Debug | 0警告/0错误 |
| 中英资源 / diff / ignored | 98键和占位参数对应，diff通过；测试/材料/日志/产物未跟踪 |

旧ignored Carbonara字段CS0649仅测试警告；14旧Linecode及缺oppo DA的15项基线不能标为通过。源码指纹见设计，参考目录只读。

## 实机风险与恢复

Debug CLI已更新。用户000226详细日志已确认：两个Boot仅生成元数据、不读GPT，所有四次读取均为User region3；成功零IN之后全部ACK状态0。读取User主表/数组及备份表/数组后出现GPT copies，进入独立的GPT解析校验问题，不能宣称完整列表成功。当前错误被主备包装隐藏原因，下一步核对紧凑元数据转换和解析器、加入有限且不含原始材料的失败上下文。没有直接操作真实设备，不自动重放读写或认证。

提交查询：git log --oneline --grep='skip GPT probes in boot regions'。
