# Oplus 散包 Super 实施记录

日期：2026-10-07。设计见 [设计及用户性能修订](2026-10-07-oplus-loose-super-design.md)。使用见 [CLI/SDK](../oplus-loose-super.md)。

## 开始状态

- 基线 054a91c，工作区干净；已有 Sparse 组合、OFP/目录虚拟 Super 和 CLI 嵌套容器；没有独立逻辑分区按 LP extent 组成 Super 的流式入口。
- 真实 PGT110 包 266 个条目；两份 super_def 的 partitions 相同。原 LP blob 6736 字节，3 slots、34 partitions、17 linear extents、Virtual A/B；部分镜像条目是相对路径引用。只读查看元数据和头，无设备操作、无镜像提取。

## 进度

- LPSP-01（2026-10-07）：SparseImageComposer.ComposeLayout 接受 Raw/Sparse 源和有序位置窗，复用解析、checksum、区间扫线、编码头与有界 LRU。窗口裁剪、Fill/DontCare、不同分区尺寸、跨 chunk、重叠/越界/块对齐/预算均验证；头声明 chunk 数在原 parser 分配前计入预算。输出仍是虚拟 Sparse，没有镜像大小的数组。
- LPSP-02（2026-10-07）：LpSuperImageLayout 从 compact blob 借用读取或用 typed 配置创建；复用 LP checksum/模型验证和 sector allocator。小型 metadata 源按需生成 reserved/双 geometry/每槽主备，不分配完整 prefix，更不分配 Super。保留零尺寸 B 分区、Virtual A/B、groups 与 extent；创建默认参考 FsMgr LpMake 的 65536/2 slots/4096/1 MiB/readonly。重复 default group、超组容量、不支持 flags、multi-device、非 linear/不对齐布局明确拒绝；组统计和 JSON 对应校验用线性字典，避免二次方扫描。
- LPSP-03（2026-10-07）：FirmwareSuperImage 显式生成完整预检的 Sparse IDataSource；OplusSuperDefinition 有界解析 JSON、引用和 dynamic_partitions_info，校验 blob 与配置对应。CLI 普通 write 支持包内/嵌套镜像，父包一直保留到调用结束，失败释放。配置错误、缺图、循环和包外路径不回落本地文件。
- LPSP-04（2026-10-07）：真实 PGT110 完整索引路线验证 16181624832 logical bytes、11339902452 Sparse bytes、292 chunks，34 partitions、17 extents、3 slots/6 valid copies；17 镜像的 34 个独立头部窗口比对一致。该路线最初建图 44.63 s/18861000 累计分配 bytes，再读 48.13 s/224.71 MiB/s；用户指出启动等待过长，默认路线由 LPSP-05 替代。
- LPSP-05（2026-10-07，用户性能修订）：FirmwareSuperImagePlan 只校验配置、LP、路径、源长和固定头；TryReadHeader 读取 4/28 字节，不回退压缩源。Write 先 metadata，再逐源前向处理 Sparse Raw/Fill，DontCare 不写；跨 LP extent 拆窗口，CRC 随消费验证。临时 IDataSource 只打开一次，回调必须完全消费，回调返回后流失效。CLI super-info 和 super_def write 默认使用计划，Qcom 走显式 Raw Program、MTK 走原始 Write，避免协议再做 Sparse 全量预检。晚期资源失败关闭部分写入会话；取消/NAK 不重放。CLI 校验完整布局目标容量、解析后的 MTK 区域块大小/可写性，并将进度限制为 100 ms 或区域结束，按实际 wire bytes 计数。

## 性能与真实包证据

- 隔离 PGT110 快速计划测量：catalog 0.093 s、plan 0.051 s、首次 metadata callback 0.0015 s；254 个区域，11339939840 material wire bytes，payload 单次前向解压 47.94 s、225.57 MiB/s，读取累计分配 7024216 bytes，测试进程峰值工作集 81641472 bytes。没有输出镜像。
- 优化固定头探测后的完整回归测量：catalog 0.043 s、plan 0.004 s/6956360 累计分配 bytes、首次 metadata callback 0.0003 s；相同 254 区域/字节数，42.14 s、256.63 MiB/s，读取累计分配 5974208 bytes。全套测试进程峰值 130355200 bytes，包含其他格式夹具，不能当作单计划 live heap。
- CLI 实际离线运行 `firmware super-info`，分别显式选择 10010111/00000000，均显示 3 slots、65536 metadata、4096 block、flags=1、34 个 readonly A/B 分区和原 groups；命令启动/显示约 0.9–1.2 s，无设备连接或镜像输出，日志保持在 ignored bin 路径。
- 模拟 Qcom：metadata 写到 super 起点，分区 payload 写到 LP extent，gap/DontCare 无数据；NAK 停在 metadata 后并使会话失效，未打开后续分区。模拟 MTK XFlash：相同原始区域位置、数据与最终 status，NAK 后 Faulted。CLI 模拟验证命名目标实际 LUN/区域、包释放、跨 extent、晚期 source 故障断开；没有真机证据。

## 验证命令与结果

- 测试均先定义行为再实现，补充检查包括 corrupted geometry/tables、重复 default group/JSON 字段、错误设备数、头几何/大小变化、前向流无 seek、Fill/洞/中间 CRC/最终 CRC、部分消费、截断、取消、借用流释放及 LP extent 交叉。
- 六工程 Release：Core 37、Firmware 103、CLI 105、Qcom 457、LP 63、MTK 515，共 1280 项。Firmware 完整回归先通过 102 项（约 4.93 min，含真实 PAC、目录分片、ZIP/OFP、两条 PGT 路线）；追加 MTK 命名区域回归 1 项通过，随后全部 98 项合成测试重跑通过。真实 ZIP/OFP 旧路线 166.1 s，原始脚本和首 RAW 窗口仍与提取包一致。
- 命令：`dotnet test .tests/<project>/<project>.csproj -c Release --no-restore`；Firmware 完整 `--no-build --logger console;verbosity=detailed`，最后新增区域检查后 `--filter FullyQualifiedName!~Real`；`dotnet build GeekFlashCore.slnx -c Release --no-restore`；`git diff --check`。
- Release 构建 0 warnings/0 errors；Sparse/LP/Firmware/CLI 双语资源键、非空值、格式参数对应；公共 API 不暴露 System.IO.Hashing 或解码器类型。System.IO.Hashing 8.0.0 只在 Sparse 内部计算流式 IEEE CRC。
- `.tests`、临时日志、bin/obj 均 ignored，未纳入解决方案或提交；没有改动参考 FsMgr、原 ROM ZIP 或解包目录。提交按 Sparse、LP、Firmware/CLI 拆分：Sparse `b497132`、LP `9a0dafb`，固件与 CLI 最终哈希见当前任务交付。

## 风险

- 默认计划不全量预检：尾部结构/CRC 错误可能在前面已成功写入后才发现；CLI 停止并断开，SDK 回调宿主需要执行同样的会话清理，不能承诺回滚。
- 完整 Sparse 索引入口与压缩源随机访问仍需重放；需要直接顺序写入时使用 FirmwareSuperImagePlan，不调用 CreateSparseImage。
- 目前仅指定 PGT110 散包和模拟传输证据；真实设备的 Qcom/MTK、Digest/VIP/Oplus 认证以及每 chunk 命令边界兼容性仍待验证。离线速度不是设备吞吐保证。
- 当前只映射单设备、同块大小、linear/对齐 extents；多设备/slot-suffix 和其他定义变体拒绝处理，没有静默转换。

## 恢复入口

本任务完成后，从 FirmwareSuperImagePlan、SparseSequentialReader 和 CLI FirmwareSuperImageWriter 开始；需要可定位 Sparse 或 rawprogram resolver 时看 FirmwareSuperImage / LpSuperImageLayout.CreateSparseImage。后续硬件验证和新变体按本文风险补充独立夹具，不恢复默认全包预扫描。
