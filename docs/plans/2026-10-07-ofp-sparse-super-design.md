# OFP 分片 Super 的虚拟 Sparse 映射

日期：2026-10-07。任务 FWSP-01–04。基线 a5d5211，工作区干净。用户要求将分片 Sparse Super 映射为可直接供 rawprogram 消费的 Sparse，不生成合并文件、不展开 Raw、不按镜像大小缓存。

## 证据与线路

- 用户目录 PEHM00domestic_11_A.17_2022031122400000 的三个 super.N.hash.img 都是 Sparse 1.0、4096 字节块、2732032 总块；各声明同一逻辑分区，并存在重叠 Raw/Fill 区，不能拼接物理文件。
- 用户 ZIP 内是 9899237376 字节 OFP（Deflate），另有 super_map.csv；两个 NV 配置指向相同三个分片。只流式读取 OFP 后缀确认内部 NVList 的 super0/1/2 与 Super 目录一致。
- 内部 ProgramList 的 super 行 filename 为空，Config 保存的真实 rawprogram0.xml 引用 super.img。OFP 元数据长度还包含一个 XML 根节点外的尾字节，参考 OfpQcUnpacker 固定丢弃末字节；本轮仅在前缀完整结束于 ProFile 根节点时兼容这一字节，不任意截取/吞 XML 错误。
- 映射语义与 AOSP libsparse/simg2img 的多输入顺序一致：后面的 Raw/Fill 覆盖之前的数据，DontCare 不覆盖数据。来源：https://android.googlesource.com/platform/system/core/+/master/libsparse/simg2img.cpp 。这里只借用语义，输出仍是 Sparse。
- Qcom 现有 SparseProgramPlanner 解析 SparseDocument/CreateDataRegions，仅向设备写 Raw/Fill 数据区；沿用其扇区范围、checksum、ACK/NAK、取消和会话失效顺序，不改协议核心。

## 架构与契约

- Android.Sparse 新增 SparseImageComposer、SparseImageComposition、SparseImageCompositionOptions。输入为有序、借用的 `Func<CancellationToken, Stream>`；compose 使用现有 SparseImageParser 验证全部头/块，不读取或展开 Raw payload。结果保存有限区间映射与源工厂，提供 EncodedLength/ExpandedLength 和独立可定位只读 OpenStream。
- 按 chunk 的起止位置扫线，最新输入优先，复杂度 O(N log N)，没有每块/每扇区索引。输出规范 Sparse 1.0 头，Raw payload 直接读取原分片窗口，Fill 仅保存四字节值，DontCare 保留逻辑空洞。原 checksum 如存在则先验证，组合后的 checksum 不继承单片 checksum。
- 同几何、总块数一致；来源流可读、可定位、稳定可重开；每次重开校验长度。默认 16 个来源、262144 个输入/输出 chunk、32 MiB 映射预算、最多 4 个同时打开的来源流；输出 Raw chunk 大小保持 uint32 可编码上限。
- Firmware 依赖 Sparse 模块，OFP 在目录发布前添加虚拟 super.img。NVList 只有一个唯一分片序列时自动选取，多个不同序列必须指定 FirmwareOpenOptions.OfpSuperNvId，禁止隐式猜 NV。分片序号连续、无重复，均来自已验证目录。
- 保留现有物理 super.img；仅修复内部 ProgramList 的空 super filename，已有真实 rawprogram XML 不改。虚拟 super.img 的 Length 为编码长度，逻辑长度来自 Sparse 头，Qcom 自行按 Sparse 计划读写。
- Firmware.Open(path) 同时支持已解包目录：安全枚举、忽略 reparse point，以严格 super.N.hash.img 的连续索引形成虚拟同目录 super.img；有多个同索引候选/缺片即失败。目录只读、物理文件优先，调用方显式选择目录。
- CLI 包内脚本支持有限深度的 `outer.zip::inner.ofp::rawprogram0.xml`，同时保持所有父包在执行期间有效。原本地 XML 缺少 super.img 时借助同目录 Firmware catalog 解析虚拟条目；普通本地文件路径仍优先。

## 生命周期、取消与失败

- compose 释放自己打开的所有解析流；composition 不拥有源工厂。每个输出流拥有独立有限源缓存，调用方释放。Firmware 包失效后虚拟条目和其流同样失效，父包不得提前释放。
- 同步 I/O，token 传递到解析、扫线、checksum、源工厂、定位、读取和缓存替换。预检失败不执行设备命令；传输失败继续由 Qcom 判定会话失效，无重试。
- checked 长度/偏移、输入/输出目录及事件数组分配前预算校验；所有诊断资源化，不输出密钥、完整 XML 或 hash。缓存源流关闭异常仍释放其余源。
- ZIP 中巨型 Deflate OFP 的定位需要顺序重放，这是格式的时间成本；不以落盘或大缓存换取随机访问。保留限额并记录实际只读耗时。
- 真实包复测发现小 CFB 读取回退和托管 Deflate 重放的时间成本。CFB 改为两个固定 64 KiB 池化缓冲，顺序窗口沿用最后密文块；非连续窗口才读取前一密文块。ZIP Stored/Deflate 使用 .NET ZipArchive，BZip2 保持内部 SharpCompress 适配；所有路径先做同一中央目录限额/方法/密码预检，源包装继续传递取消。

## 实施、验证与提交

1. FWSP-01：先测试 Sparse 顺序覆盖、DontCare、Fill、几何/预算/CRC/截断、随机定位、双流、取消和 >4 GiB 虚拟编码，再实现通用 composer；独立 feat(sparse) 提交。
2. FWSP-02：先测试 OFP NV 选择、虚拟条目/空 super 行、单尾字节/安全 XML、目录分片和源失效；实现 Firmware 接入。
3. FWSP-03：CLI 本地 rawprogram 和嵌套包生命周期；模拟 Qcom 验证目标扇区、覆盖后的确切字节、DontCare 不发送、NAK/取消/预检。
4. FWSP-04：真实分片只读逐区间/窗口比较；真实 ZIP/OFP 元数据、脚本和虚拟 Sparse 检查；目标及完整六套 Release 测试、解决方案构建、diff、资源/ignored 审查，更新文档并提交。

测试仅在 ignored .tests。命令沿用 firmware-streaming-implementation 的六套 test 与 `dotnet build GeekFlashCore.slnx -c Release --no-restore`、`git diff --check`、`git ls-files .tests`。

## 风险与非目标

- 真实证据只读，不通信设备；Oplus 特定 Digest 是否与组合后的写入包序匹配仍需硬件/脱敏抓包确认，不能把普通 Sparse 模拟通过写成厂商认证成功。
- 不实现 Raw 导出/合并文件、Packer、分区内容重排、LP 元数据修改或变体自动猜测。虚拟 Sparse 的 checksum 不作为认证证明。
