# ZIP 内 OFP 目录性能实施记录

日期：2026-10-07。设计：2026-10-07-ofp-catalog-performance-design.md。基线 2459542，工作区干净。

- OFPCAT-00：已确认 catalog 提前 Compose，以及 ReplayStream 每次向后读取都会重开解码器。真实尾部证据 metadata 85378 字节、4096 页，距尾部约 90 KiB；原完整 ZIP/OFP 目录/索引/脚本/首窗口回归 155–166 秒，不能当作单独 list 基线。
- OFPCAT-01：初始护栏修正测试 helper 的递归读后，四项测试 3 失败/1 通过，分别复现目录扫描 payload、损坏 Sparse 阻止列目录和近尾回读重开解码器。ZIP ReplayStream 已添加固定 128 KiB 池化环形历史，只保留最近解码字节；窗口外重放，独立流各自持有，释放清零归还池。取消和声明长度/EOF 检查保留。
- OFPCAT-02：虚拟分片 Super 延迟解析；目录只验证名称、连续索引、NV、范围和歧义。FirmwareEntry.KnownLength 不触发 I/O，GetLength(ct) 与打开流在包 gate 下构建，成功复用，失败/取消可重新解析。Compose 保留原签名，新增解析与结果生命周期 token 分离重载，避免取消已完成的单次操作导致缓存不可用。CLI list 显示本地化“按需解析”，不建立索引；完整 Sparse/CRC 检查仍在原协议写入前完成。
- OFPCAT-03：真实 catalog 测试约 31 秒，103 个条目、6 个编号 rawprogram，Super 未初始化；实际 CLI 同一 ZIP::OFP 命令 29.556 秒，已有目录命令 0.302 秒，均正确列出虚拟 Super。没有创建 OFP、Super 或 Raw 文件；只写 ignored 测试日志，原 ROM 与参考项目只读。历史 155–166 秒包含完整索引和读窗口，不能作为 list 单独基线比较。

## 验证

- Core 38、CLI 106、Firmware 111（合成 105、真实 6）、Qcom 457、LP 63、MTK 515，共 1290 项通过。7 项 catalog 合成覆盖不扫描 payload、延后损坏报告、缓存/窗口外回退/环形边界/随机读取、取消重试/已完成 token 取消、并发一次构建、父包释放和长度错误；另有 1 项 CLI 与 1 项 Core 新增护栏。
- `dotnet build GeekFlashCore.slnx -c Release --no-restore --verbosity quiet`：0 warnings/0 errors。CLI 261 个中英文资源键、非空值与格式参数匹配；git diff --check 通过，git ls-files .tests/temp/bin/obj 无结果，日志与测试保持 ignored。
- 其余 5 项真实回归通过（约 4.87 分钟）：PEHM00 ZIP 原 rawprogram/首 Raw 窗口和 508 个 chunk 一致，160.5 秒包含完整 Super 索引；目录 1327 个独立窗口和 PAC 保持一致。PGT110 完整 Sparse 292 个 chunk、6 份有效 LP metadata、34 个来源窗口通过；快速计划仍先 metadata、254 个区域，约 0.005 秒建计划，完整读取约 235.96 MiB/s。整套真实回归进程峰值约 93.6 MiB，不是单 catalog live heap；本轮未输出镜像。
- 补跑合成时曾与真实测试进程争用同一个输出 DLL，MSB3027 停止于复制阶段；待该进程结束后已顺序重跑 Firmware 合成 105、CLI 106，并完成最终 Release 0 warnings/0 errors。没有终止用户进程。
- 测试命令：六工程 `dotnet test .tests/<project>/<project>.csproj -c Release --no-restore`；Firmware 通过 `--filter FullyQualifiedName!~Real`、`--filter FullyQualifiedName~CatalogPerformanceTests` 与 `--filter 'FullyQualifiedName~Real&FullyQualifiedName!~RealCatalogPerformanceTests' --no-build` 的互补范围覆盖全部 111 项。详细日志保持在 ignored .tests。
- 分模块提交：ZIP 回读 `9116779`、Sparse 生命周期 `9d0b32f`；延迟目录与 CLI 文档提交号见本分支最终交付。工作区仅本任务变更，测试/日志/构建产物未跟踪。

## 边界与恢复

- 只有单流的固定回读窗口，没有永久缓存或跨 CLI 命令 catalog 缓存。Deflate OFP 的首次尾部读取仍需完整顺序解压，超出 128 KiB 的回退也仍可能重放；物理 OFP/已有目录可直接定位。
- 列目录成功不代表 chunk 或 CRC 有效，SDK 应在写前主动调用 GetLength(ct)/打开流。普通 Qcom rawprogram 路径保留原预检，不把这次目录优化解释为厂商 Digest/VIP 的硬件兼容证据。
- 恢复入口：FirmwareEntry、OfpSuperMapper、ReplayStream 和 FirmwareCommands；真实证据与本地测试留在 ignored .tests，没有设备写入。
