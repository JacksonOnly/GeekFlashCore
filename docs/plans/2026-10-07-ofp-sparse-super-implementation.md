# OFP 分片 Super 实施记录

日期：2026-10-07。设计见 2026-10-07-ofp-sparse-super-design.md。

## 开始状态与证据

- 基线 a5d5211，工作区干净；已有 Firmware 58、Qcom 457、CLI 102 项，Sparse 缺少多输入 Sparse 输出映射 API。
- 真实三个分片头分别 310/176/84 个 chunk，均为 4096 字节块、2732032 总块；存在重叠数据区，必须有序覆盖，不能简单拼接。
- ZIP 5943402070 字节，包含 Deflate OFP 9899237376 字节。流式扫描耗时 23.8 秒，仅保留最后不足 8 MiB 到 ignored 测试证据目录，未解出 OFP 或 Raw 镜像。
- 内部 NVList 两个配置指向同序列；Super 保存三个真实条目范围，ProgramList 的 super filename 为空，而 Config 的 rawprogram0.xml 引用 super.img。元数据包含完整 ProFile 后的单个非 XML 字节，参考读取明确减一；兼容范围按设计限定。

## 进度

- FWSP-01（2026-10-07）：测试先行后完成通用 SparseImageComposer/Composition/Options。按 chunk 边界扫线叠加，Raw/Fill 后片优先、DontCare 透明，输出保持 Sparse；生成头与窗口直接引用原 payload。输入/输出 count、512 字节/输入 chunk 的预算、uint32 Raw chunk 上限、稳定源长度、checksum 和同几何均在发布前验证。每个输出流最多四个源，源缓存关闭异常仍清理全部已打开资源。
- FWSP-02（2026-10-07）：Firmware 新增 Directory catalog，OFP/目录为连续分片添加虚拟 super.img。OFP NVList 相同序列自动选取，不同序列必须 OfpSuperNvId；缺片、重复索引、未知条目失败。物理 super.img 优先，Config 的原始 XML 不改；仅内部 ProgramList 的空 Super filename 修复为虚拟名称。OFP 仅兼容完整 ProFile 根节点后的单个尾字节，多个尾字节、附加 XML 和 DTD 仍拒绝。
- FWSP-02 性能修复：真实嵌套包前两次复测在五分钟预算触发取消，先定位到 CFB 小读反复回退外层压缩源，再定位到托管 Deflate 重放成本。先建立 256 次单字节读不反复回退的失败测试，再将 CFB 改为固定 64 KiB 窗口；ZIP Stored/Deflate 改用 .NET codec，BZip2 保持已测试适配。未增加镜像缓存、临时文件或无界等待，最终真实只读测试 155.2 秒通过。
- FWSP-03（2026-10-07）：CLI 支持本地 rawprogram 的缺失 super.img 解析，以及最多八层 `ZIP::OFP::entry.xml`。包内大小写区分、父包反序释放；同一批次展开和执行共用 catalog，避免巨型包重复解析。真实目录离线 firmware list 展示 101 个条目及虚拟 super.img。模拟传输验证叠加后的三组 512 字节分别写目标 10/11/12 扇区；DontCare 场景只写 10/12，共 1024 字节；NAK 后只发送首区间即失效，不重放后续区间。
- FWSP-04（2026-10-07）：完成真实目录、嵌套 ZIP/OFP 只读验证、六套回归、Release 构建和使用文档。实现范围仅 Sparse/Firmware/CLI，Qcom 生产协议代码未改。通用映射提交 `7a61dd8 feat(sparse): compose ordered sparse fragments`；Firmware 提交 `8750862 feat(firmware): map OFP split Super as sparse`，CLI 与文档独立提交 `feat(cli): resolve virtual Super and nested packages`，最终编号见本分支 Git 历史与会话交付。

## 验证证据

- 真实目录：三个源 310/176/84 chunk，有 60 处重叠数据区；映射输出 508 chunk，编码长度 8105220928，逻辑长度 11190403072。独立从最后覆盖源的物理 payload 读取各数据区起点/中部/尾部，共 1327 个 64 字节窗口，全部一致；DontCare 没有来源数据覆盖。构建、解析与窗口验证线程分配 967224 字节（GC.GetAllocatedBytesForCurrentThread），不是整进程峰值内存测量；没有生成合并或 Raw 文件。
- 真实 ZIP/OFP：保持父 ZIP 有效，直接 Open(zip.GetEntry(...ofp)) 得到 103 条目。虚拟 Sparse 大小及 chunk 与目录相同；rawprogram0.xml 与已解包目录全字节相同，ResolveEntry 解析到同一 super.img 条目，首 Raw 窗口与目录映射一致。155.2 秒完成，没有解出 OFP 或导出 Super。
- Core 覆盖顺序、跨 Raw 区间切分、Fill、DontCare、双流随机 seek、坏 CRC、几何/预算、取消、源长度变更、有限缓存/关闭异常及 32 个随机布局与独立块参考比较；5 GiB Fill 编码仅 44 字节，5 GiB Raw 通过虚拟源验证 uint32 chunk 切分和末端读取，线程分配小于 1 MiB。
- Firmware 覆盖 NV 多配置/明确选择、空 filename、单尾字节/多尾字节拒绝、缺片/重复索引、物理文件优先和 BZip2 回退；CLI 覆盖嵌套大小写、目录解析、批次后条目失效且未产生合并文件。已有六套全部用 Release 重新构建运行。

| 命令（测试均附 `-c Release --no-restore`） | 结果 |
| --- | --- |
| `dotnet test .tests/GeekFlashCore.Core.Tests/GeekFlashCore.Core.Tests.csproj` | 22 通过 |
| `dotnet test .tests/GeekFlashCore.Firmware.Tests/GeekFlashCore.Firmware.Tests.csproj --filter FullyQualifiedName!~RealZipOfpPublishesMappedSparseAndOriginalRawprogramWithoutExtraction` | 75 通过，含真实目录 |
| 同 Firmware 工程，`--filter FullyQualifiedName~RealZipOfpPublishesMappedSparseAndOriginalRawprogramWithoutExtraction` | 1 通过，155.2 秒；两次先前超时失败已修复 |
| `dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj` | 104 通过 |
| `dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj` | 457 通过 |
| `dotnet test .tests/GeekFlashCore.Android.Lp.Tests/GeekFlashCore.Android.Lp.Tests.csproj` | 55 通过 |
| `dotnet test .tests/GeekFlashCore.Protocol.Mtk.Tests/GeekFlashCore.Protocol.Mtk.Tests.csproj` | 513 通过 |
| `dotnet build GeekFlashCore.slnx -c Release --no-restore` | 0 警告、0 错误 |

共 1227 项（Firmware 分两条互补筛选命令覆盖 76 项，没有跳过）。所有测试/夹具/只读证据位于 ignored .tests，不提交。中英文 Sparse 56 键、Firmware 15 键、CLI 256 键完全对应且无重复；没有新增敏感日志。git diff --check 通过，git ls-files .tests 为空，.tests 与 bin/obj 继续 ignored；本轮生产改动和文档已按预期范围审查。

## 未决风险与下一步

- 尚无真实设备写入证据。普通 Sparse 写入/空洞/NAK 由模拟传输确认；Oplus Digest/VIP 是否与组合后 chunk 和包序匹配须独立硬件/脱敏抓包验证，不等同于厂商认证通过。
- 巨型 Deflate 嵌套源向后定位必须重放解压，155.2 秒仅是本机目录/脚本/首窗口只读证据，不能据此宣称完整刷写吞吐。缓存受限，整包刷写耗时和整进程峰值仍待实测。
- 其他 OFP 密钥/元数据/NV 变体继续按真实包样本扩展；源工厂必须稳定可重开，长度相同不能证明内容未改变。源 checksum 有则验证，输出不继承单片 checksum，不执行签名/完整镜像 hash 信任判断。
- 后续从本设计/实施记录恢复：优先验证实际 Loader 的普通 Sparse 与厂商 Digest 包序，然后再扩展 Packer、其他固件变体；本轮不执行 Raw 转换、磁盘合并、LP 内容修改或 NV 猜测。
