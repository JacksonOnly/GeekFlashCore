# CLI LP 分区访问与元数据编辑

日期：2026-10-06。用户要求挂载/未挂载 LP 读写、配置编辑、ls 快捷列表和简洁帮助；已确认配置指 LP 元数据。

## 目标与兼容性

- LPCLI-01：主 shell 支持 `ls <partition/path> [lun] [lp-slot]`，仅 LP、Ext、EROFS 可列表；一次列表后返回，不进入交互浏览。
- LPCLI-02：主 shell `read/write super/system_a <file> [lun] [lp-slot]`，挂载 shell `read/write system_a <file>` 读写完整逻辑分区，即使该分区可浏览为文件系统。文件导出沿用原子输出。普通 write 保持原分区大小，Raw/Sparse 超出容量时拒绝。
- LPCLI-03：主 shell 和挂载 shell 共用 `lp <operation> <container-path> ...`；提供 info、rename、resize、attributes、move、add、remove、group-add、group-rename、group-resize、group-flags、group-remove。操作立即校验、生成计划并提交，info 不写；末尾支持位置参数 [lun] [lp-slot]，挂载默认沿用 slot。
- LPCLI-04：进入挂载只显示提示和列表；help 显示分组的简短命令，help read/write/find/lp 显示具体语法。
- 兼容既有 GPT read/write/erase、browse、browse-image 与文件 read/print/find；不增加文件系统文件编辑、操作系统挂载、Sparse Super 浏览或漏洞能力。

## 现有实现与线路

BrowserSession 组合 LpMetadataSet/Document 和 Ext/EROFS，只读节点把逻辑分区目录与镜像混为一种 read 目标。StorageCommands 只解析 GPT 名称。复用 LpEditor/LpDraft/LpPartitionImageSource/LpCommitter，不复制 LP 编码、校验、分配或 Firehose 线路；设备仍经 IBlockDeviceProvider 和同步 ReadAt/WriteAt。参考证据来自仓库代码和本地合成夹具，本轮暂无真机证据。

## 契约、状态和所有权

变更集中 CLI 内部类型，不修改公共协议契约。共用 GPT 分区解析器提供读租约与可写切片，保留 LUN 与稳定设备 ID；多设备 LP 按唯一 GPT 名称解析，歧义拒绝。读写 LP 命令串行执行；异步只在宿主编排/租约与本地输出。编辑前释放浏览资源，本地文件独占打开；编辑退出无论成功或失败均清除卷与元数据缓存，重新解析路径或回退根目录。设备 Core 会话代数继续限制旧句柄；未知写结果不重试。

## 安全、性能和恢复

长度、名称、属性、槽位、组容量与跨槽冲突交由已有 LP 校验。Raw/Sparse 使用流式源与池化数据缓冲，普通 write 不隐式改变容量，可在空间不足时使用现有分区原位写；这不保证数据回滚，失败明确传播。设备可写切片在不对齐区间执行有界扇区读改写，完整区间直接写；切片起点/长度必须扇区对齐，实际设备与 descriptor 的大小和块长必须一致。元数据操作默认保留原数据，不擦除释放区域。提交回读失败不能报告成功。输入镜像不得与挂载源相同，导出保护与链接检查保持。数据写入取消在完整同步设备交换之间观察；元数据进入主备提交临界区后沿用 LP 核心完成两份副本再观察取消，不重试。完整非 Raw NAK 沿用 Core 可恢复规则；传输/Raw 异常按 Core 规则要求重连。用户文本资源化，中英键一致，不记录镜像内容或私密材料。

## 文件、步骤与验证

范围：BrowserSession/Node/Commands、新 LP 命令与共用设备解析器、StorageCommands、CliApplication、CommandSyntax/Completion、中英资源、docs/cli-qcom.md。

先恢复 ignored 本地测试，在改生产代码前新增失败用例。依次实现分区原始读取与 ls、LP 写入与元数据命令、缓存失效与帮助。测试矩阵覆盖：挂载/未挂载、Raw/Ext/EROFS 子分区、主备/slot、LUN 歧义、多 extent/Sparse、改名/属性/resize/分组、失败零写、输出原子性、资源释放、写后刷新与简洁帮助。运行目标及全部本地测试、Release 解决方案构建、git diff --check、资源键和 ignored 检查。生产代码与文档按一个完整 CLI 能力提交；测试不提交。

风险：真实设备多 LUN/多源 LP、EDL 小读取性能、取消延迟、原位写入中断与 AVB 验证仍需硬件验证；本轮模拟不能代替真机证据。
