# MTK 启动区域与 User GPT 发现

2026-10-08 / BOOT-GPT-01，起点 `6186ea8`。用户要求核对XFlash ReadData区域与Penumbra `core/src/da/xflash/flash.rs`。

## 核对与兼容边界

FlashParams的storage_type/partition_type/addr/size顺序与参考FlashOpParams相同，region.WireId没有+1/-1错误。参考UfsPartition Lu0=1/Lu1=2/Lu2=3，get_user_part为Lu2；eMMC Boot1=1/Boot2=2/User=8。当前解析正确，不能把UFS User改为0或8。

实际差异是LoadPartitionsCore给BOOT1/2添加Preloader后，仍调用ReadGpt，先读启动区头尾；Penumbra `da/storage.rs` 的get_aux_gpt_parts仅根据容量构造Preloader/backup，get_gpt_parts使用user_section。用户抓包中的区域1正由此产生，不是默认User目标被错误编码。此前DA-ZLP-01修复仍必要：USB成功零包也能发生在User正常读取时，不能当EOF。

## 最小修复

- 已报告eMMC/UFS的wire1/2仅生成全容量Preloader/backup并continue，不探测GPT，不新增设备命令。
- UFS User3/eMMC User8仍按已有严格范围读读取GPT。eMMC GP等其他非启动区域保留既有独立GPT能力，不扩大或改变RPMB/未知区域策略。
- 缺启动区域不生成条目；启动区明确raw/命名/别名读写擦除仍路由原区域，不能把所有操作强制改到User。GPT CRC、主备恢复、损坏失败、Legacy PMT/XML、缓存/gate/取消/代数保持。参考native PGPT/SGPT被严格range读替代是现有安全差异，不复制参考吞错误回退。
- 摘要记录发现开始（介质/User ID）和完成条目数；Debug记录启动区仅元数据及实际GPT探测区ID/是否User，不记录数据/私密ID。

文件限MtkProtocol.Storage/Partitions、两语言resx、诊断说明/实施记录；XFlashSession FlashParams无改动。公共API、其他协议和exp保持。同步/异步/host/scoped枚举共用此方法；资源只保存范围，无按容量物化，无I/O重试或写入。

测试先以只提供User GPT应答、Boot存在的eMMC/UFS 512/4096夹具复现旧Boot探测；覆盖线缆区域ID、缺Boot、缓存、boot/user明确raw及别名读写擦除、主备/坏CRC/取消/NAK失效。旧ignored夹具只移除专供Boot探测的应答，保留User严格断言；无关Linecode/缺DA基线不改。目标/可运行MTK/完整基线对照、CLI、Release/Debug、资源/diff/ignored验证后独立提交。

源码指纹：flash.rs SHA256 `186B35BDAFD5963B8E4150DBDAF53C400913943A6CA20E388CE2FD441F26D5D8`，da/storage.rs `C097A145259BAFF0D8546425E5607FCD510BD2B47ABF2AFC4A2CE83A0AF198FD`；参考非Git快照。真实User GPT/完整分区枚举仍待用户新日志确认。
