# Oplus 散包 LP Super 的流式映射与写入

日期：2026-10-07。任务 LPSP-01–04，基线 054a91c，工作区干净。用户授权参考 D:/Code/CSharp/FsMgr、当前 LP 和 LibLP CLI，并要求低内存、不落盘、高性能。

最新范围以文末 LPSP-05 用户性能修订为准：默认先生成 metadata，逐分区前向写入；完整 Sparse 索引仅作为显式入口保留。本文前半部分记录初版方案及其验证依据。

## 目标与证据

- PGT110 ZIP 有 266 个条目，含 META/super_def.00000000.json、super_def.10010111.json；两份分区列表相同。不是上一轮同几何 Sparse 分片叠加，而是 17 份独立逻辑分区镜像、34 个 A/B 定义、三组和单 Super 设备。
- super_meta.10010111.raw 是紧凑 LP blob：4096 字节 geometry + 10.2/256 字节 header + 2384 字节 tables，34 partitions、17 extents、3 groups、1 device。Geometry 声明 65536 字节 metadata、3 slots、4096 logical block；header flags=1（Virtual A/B）。设备长 16181624832，1 MiB alignment，首数据扇区 2048。
- 一些小 .img/.raw 是 UTF-8 相对路径引用。只解析有限、安全、存在于同包的引用；不能将其作为分区 payload，也不能读取包外文件。
- FsMgr LpMake 从 JSON 读取 metadata size、block device 大小/块长/对齐、groups 与 partitions；默认 readonly，缺省 metadata 为 65536/2 slots。原包有效 blob 必须优先，保留 3 slots、空 B 分区、flags 和 extents；不复制参考实现跳过零尺寸分区、吞解析错误、二进制转 UTF-8 或磁盘合并线路。

## 公共契约与范围

- Sparse 新增有界位置布局 API：输入 RAW/Sparse 工厂与逻辑窗口 placement，生成同一 SparseImageComposition。复用现有解析、checksum、扫线、编码头和有限源缓存；不展开 Sparse。placement 必须有序、互不重叠并在输出范围内。
- LP 新增 LpSuperImageLayout：读取紧凑 blob 时借用流、只复制有限元数据，通过现有 LpMetadataSet/Document 验证 checksum、几何和范围；输出保留模型。另提供单设备、typed 配置创建入口，使用现有编码/验证与对齐规则，默认沿用 LibLP CLI，保留显式零尺寸分区。
- LP 将重复 geometry/每槽主备 metadata 作为小虚拟 RAW 源，分区 Sparse 的逻辑窗口映射到 LP linear extents。来源大小必须等于分区逻辑长度；多设备、slot suffixing 和不能表示的非块对齐布局明确拒绝，不静默忽略。
- Firmware 新增显式 FirmwareSuperImage.Open(package, definitionName)：解析 bounded JSON、校验配置与原 LP blob 对应、解析引用并提供 IDataSource Sparse。借用父包，条目和已开流随父包失效。没有隐式选择 NV、下载、提取或包打开时的全镜像扫描。
- CLI `write super "package.zip::META/super_def.10010111.json" [lun]` 显式选择定义并映射；一般包内镜像也能作为 write 输入。`firmware super-info` 显示选定配置的映射与 LP 信息。SDK 可将该 IDataSource 送入 Qcom/MTK 现有 WriteAsync，或作为 rawprogram 的 super.img resolver。

## 安全、性能与恢复

- 所有路径限定同包；拒绝 DTD、重复 JSON 字段/名称、超长配置、缺图、循环引用、未知设备/flags、配置/元数据不一致、超范围和重叠。预检完成后才进入原协议写入。
- 同步离线解析/读取，取消传到头扫描、解压跳过、checksum、placement 和 payload。异步仅宿主现有 WriteAsync 编排；不新增同步协议资源等待、重试或会话状态。
- 不创建临时镜像或目录。内存按 metadata/chunk/extent 增长并有预算，源流 LRU 限额；大文件 payload 直接读取。ZIP Deflate 扫描 chunk 头需要一次顺序解压，顺序写再读取一次；生成 Super 的 seek 只移动映射游标，不重复打开整个目录或逐块回退压缩源。
- 写入复用协议 Sparse 与会话失效逻辑，普通写不保证数据回滚；原始 blob、metadata flags 和配置不代表认证/AVB 信任。

## 实施、验证与提交

1. LPSP-01：先测试有序窗口、RAW/Sparse 混合、跨 chunk 切分、Fill/DontCare、范围/预算/取消，再实现 Sparse 布局 API。
2. LPSP-02：先测试紧凑 blob、重复主备/所有槽、配置创建、A/B 空分区、checksum/对齐/多设备失败，再实现 LP 映射。
3. LPSP-03：先测试 JSON 与 blob 对应、引用/缺图/变体、流失效、CLI 输入、模拟写入字节/NAK/预检，再实现 Firmware/CLI。
4. LPSP-04：真实 ZIP 只读核对 metadata、逻辑分区窗口及吞吐/分配；六套 Release 回归、解决方案构建、diff/resource/ignored 检查，更新使用与恢复文档。真实设备不操作。

独立提交 Sparse、LP、Firmware/CLI 与必要文档。测试位于 ignored .tests，沿用六工程 Release --no-restore 测试、dotnet build GeekFlashCore.slnx -c Release --no-restore、git diff --check。巨型 PEHM00 ZIP 已在上一轮验证，未改相关路径时不重复耗时样本。

## 未决风险

- 当前只有指定 PGT110 包和模拟传输证据。完整压缩包刷写吞吐/峰值及设备厂商认证、MTK/Qcom 硬件仍需独立验证。
- Deflate 不具备廉价随机访问，源布局多次交错/随机读取会重放；设计提供有界流式顺序路径，不通过磁盘或整镜像缓存规避格式成本。

## 2026-10-07 用户性能修订（LPSP-05）

用户指出 44.63 秒建图不可接受，要求先生成 metadata，实际写入某分区时再读取。默认 CLI/SDK 改用 FirmwareSuperImagePlan：打开时只验证 JSON、LP blob、引用、源长度及各 Sparse 固定头；不建立全包 chunk 索引。保留 FirmwareSuperImage.Open 作为显式完整预检的 seekable Sparse 流入口，不作为默认写入线路。

计划执行先写小型 LP metadata，再逐源前向解析 Sparse chunk，Raw/Fill 直接交给宿主原始写入回调，DontCare 不写。LP extent 只做逻辑到物理位置换算，单个输入流只前向消费一次；不产生 Raw 文件、整分区数组或压缩源回退。CLI 使用 Qcom 的显式 Raw Program 与 MTK 的原始 Write，不触发协议的 Sparse 全量预检，不修改其 ACK、NAK、取消或厂商策略。第三方宿主可直接消费同一同步回调。

全部 chunk 头、payload 长度、逻辑累计范围和 CRC 在消费时检查；尾部损坏可能在此前成功写入后才发现，这是取消全量预检的明确边界。回调必须完整消费临时借用流，不能保留；失败立即停止，已提交的数据不自动重试或回滚。所有数据仍是对原 Sparse 的有界流式访问，不转换成 Raw 镜像。新增测试覆盖首次写入前不读 payload、只前向读取、跨 extent、Fill/DontCare/CRC、取消、截断、回调释放和模拟 Qcom/MTK。
