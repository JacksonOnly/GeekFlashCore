# GeekFlashCore 开发协作规范

## 项目基本信息

- 项目：`GeekFlashCore`
- 技术栈：C#、.NET 8、同步 `ITransport`、`Span<T>`/`Memory<T>`、`ArrayPool<T>`、Serilog。
- 当前目标：建设可独立复用的通用刷机开发框架，为 CLI、桌面工具、服务和其他宿主提供稳定的设备连接、Loader、协议、存储、镜像和厂商兼容能力。
- 当前重点：Qualcomm EDL 的 Sahara、Firehose、Android Sparse、普通 Digest、VIP、Oplus Digest、Xiaomi/OnePlus/Nothing 等厂商策略。
- 参考实现：`D:\Code\CSharp\GeekFlashTool`、`D:\Code\CSharp\QnQcLIB`、`D:\Code\CPlusPlus\qdl`。参考项目用于确认设备行为，不能直接继承其无界重试、异步串口循环、重复分配或吞异常逻辑。
- 用户沟通语言：中文。代码中的公共 API、类型名、协议字段和 Git 提交信息保持英文约定。

## 仓库结构

- `src/GeekFlashCore.Protocol.Qcom`：Qualcomm 协议实现、会话状态、Sahara、Firehose、厂商策略和存储操作。
- `src/GeekFlashCore.Protocol.Qcom.Abstractions`：稳定的公共契约、配置、模型、资源接口和异常类型。
- `src/GeekFlashCore.Protocol.Qcom.MessagePipe`：可选的 MessagePipe 资源适配层，不反向污染协议核心。
- `src/GeekFlashCore.Protocol.Mtk.Abstractions`：MTK 稳定契约、BROM 命令/会话、资源与安全能力。
- `src/GeekFlashCore.Protocol.Mtk`：同步 LibUsb BROM/Preloader、Legacy/XFlash/XML、DA/EMI 与流式存储。
- `src/GeekFlashCore.Protocol.Mtk.Extensions`：可选已加载 DA 扩展、SEJ、RPMB、seccfg；核心不反向依赖。
- `src/GeekFlashCore.Protocol.Sprd.Abstractions`：SPRD BSL 稳定契约、显式 FDL/profile、分区和资源所有权。
- `src/GeekFlashCore.Protocol.Sprd`：同步 BootROM/FDL1/FDL2、有限预算帧、命名分区流式操作和会话代数视图。
- `src/GeekFlashCore.Protocol.Abstractions`：跨协议公共抽象。
- `src/GeekFlashCore.Android.Sparse`：Android Sparse 解析、计划和流式区域访问。
- `src/GeekFlashCore.Transport.*`：SerialPort、LibUsb 和传输抽象。
- `src/GeekFlashCore.BlockDevice.*`、`src/GeekFlashCore.Gpt*`：块设备和 GPT 能力。
- `docs/plans`：设计方案、实施计划、进度和未决风险，是跨会话恢复工作的依据。
- `.tests`：本地测试工程和夹具目录，按项目约定被 Git 忽略，不得提交。

## 任务启动流程

### 小型修改

先阅读本文件、相关代码和现有测试，确认行为边界后直接修改。开始工作时在会话中说明目标、影响文件和验证方式。

### 大型功能或跨模块修改

必须先形成设计方案，再开始实现。方案至少包含：

1. 目标、非目标和兼容性要求。
2. 现有实现、参考项目行为和需要保留的线路顺序。
3. 公共契约、状态机、同步/异步边界和资源所有权。
4. 安全、性能、日志、取消、重连和失败恢复策略。
5. 文件范围、测试矩阵、验证命令、提交拆分和未决风险。

方案确认后，将任务拆成可以独立验证的小步骤。设计方案和实施进度分别记录在 `docs/plans`；需求、线路或风险变化时立即同步文档。

## 会话记录和汇报

每次会话都要维护可恢复的上下文：

- 开始：说明当前目标、已知进度、工作区状态、准备处理的任务和验证计划。
- 进行中：在读取上下文、发现关键事实、改变方案、遇到阻塞或完成阶段时，用简短中文汇报结论和下一步；长时间操作前说明正在运行的命令。
- 结束：说明已完成的代码和文档、测试与构建结果、提交号、工作区状态、未决风险和下一次应从哪里继续。
- 不把猜测写成事实。参考代码、模拟传输和真实设备行为必须区分记录；没有硬件证据时明确标为待验证风险。
- 进度记录应包含日期、任务编号、行为结论、验证证据和风险，不只记录“已完成”。

## Qualcomm 架构约束

- `QcomProtocol` 负责公共门面、异步资源获取、连接编排、取消和会话生命周期。
- Sahara、Firehose、Raw、XML ACK/NAK、Sparse 展开、分段读写和厂商线路保持同步 I/O；同步协议层不得阻塞等待异步资源。
- 异步只用于资源提供器、宿主编排和取消边界。资源请求必须有取消、超时和迟到结果的释放策略。
- 公共抽象不得暴露第三方 NuGet 类型；第三方结果在内部适配为稳定领域模型。
- 会话必须串行化。原始传输取消、协议错误、资源失效或不可恢复的 NAK 后，会话进入明确失效状态并要求重新连接。
- 旧块设备、租约和句柄通过会话代数校验；断开、重连或会话失效后不得继续操作旧视图。
- 普通 Digest、VIP、Oplus Digest 等线路必须保持互斥和明确的启动顺序。厂商策略不得通过隐式全局状态互相污染。

## 安全和性能要求

MTK 使用 LibUsb；BROM、三种 DA 与扩展共用串行化 gate、有限预算和会话代数。BROM 访问只在 Probe 后、DA 执行前开放，回调或跳转后通道失效。标准认证由宿主提供合法材料和签名。按用户最新授权，漏洞部分提供 `IMtkExploitStrategy` 及阶段/context/result 框架，另允许 LineCode、Carbonara、HeapBait、Unfused 的非执行接口占位类；占位类仅声明 descriptor、校验参数/取消并返回 NotApplicable，不读取设备上下文或执行 I/O。允许显式注入的宿主回调在连接阶段调用，不得添加具体漏洞策略逻辑、核心默认注册、漏洞算法、补丁生成或攻击载荷。用户进一步要求 CLI 显式注入现有四项，并让 NotApplicable 继续下一匹配策略、全部尝试后仍校验 DAA 材料；核心支持有界有序集合，每次回调独立失效，Completed 结束本阶段尝试，终止结果不得降级继续。核心未注入的连接仍不执行策略，完成结果不等于认证成功，终止结果要求重连。具体契约与阶段见 `docs/plans/2026-10-05-mtk-exploit-framework.md`，占位类范围见 `docs/plans/2026-10-06-mtk-exploit-placeholders.md`，CLI 接入最新范围与证据见 `docs/plans/2026-10-06-mtk-cli-strategy-order-design.md` 及对应 implementation 记录。扩展须验证 ACK/context；RPMB 不作为普通块设备，seccfg 写入必须备份、最小对齐写和回读，未知写结果不重试。

用户进一步授权补齐占位类依赖，范围是通用离线 DA 窗口、流式摘要/字节搜索、验证后的宿主元数据、资源清单/借用源读取及分析/变换接口；四个 Execute 不解析或调用这些依赖。最新授权允许原样复制 `penumbra-main/core/payloads` 七个 .bin 并嵌入程序集，通过显式 `MtkExploitResourceStore.FromEmbeddedResources()` 惰性读取与长度/摘要校验；这替代此前不分发参考二进制的范围。资源层不得添加设备执行、漏洞特征/地址表、参数填充、定位算法或补丁实现。宿主资源不自动下载/加载，默认仓库仍为空，真实无参构造保持。通用依赖见 `docs/plans/2026-10-06-mtk-exploit-dependencies-design.md`；最新嵌入范围及验证见 `docs/plans/2026-10-06-mtk-embedded-payload-resources-design.md` 与对应 implementation 记录。

用户另明确要求加入 Penumbra 的通用 utils/analysis；允许 `GeekFlashCore.Protocol.Mtk.Analysis` 的 Arch、IArchAnalyzer/ArchAnalyzer、Analyzer 和 ARM/AArch64/Thumb2 分析器实现通用离线指令解码、地址换算、调用方字符串引用、函数前导与有限寄存器回溯。该授权扩展此前仅提供宿主分析契约的范围；不得内置 MTK 专用定位特征、堆/DPC算法、漏洞参数或补丁，且不接入四个 Execute 或默认依赖。源保持借用、可定位、稳定可重开，使用有界缓存和取消；启发式结果不是执行地址或认证证明。最新范围、方法映射和验证见 `docs/plans/2026-10-06-mtk-architecture-analysis-design.md` 与对应 implementation 记录。

- 所有外部长度、扇区范围、Payload、XML 大小、计数器和整数转换在分配或读写前校验，并使用 `checked` 或有界逻辑。
- 大文件和大型资源使用 `IDataSource`、流式处理、窗口化和池化缓冲；不得按镜像总大小展开 Raw、Sparse 或 Digest 映射。
- 明确 Stream 所有权：协议只释放自己打开的流；调用方提供的资源、敏感载荷和迟到响应必须按接口约定释放或清零。
- XML 使用结构化解析和允许列表，禁止字符串拼接绕过协议验证、DTD、外部实体和多命令注入。
- Token、签名、Challenge、完整 Digest、私密 Hash、认证响应和完整自定义 XML 不得写入日志。
- 设备原始文本只在必要级别记录，按来源结构化并限制长度；相同事件由一个拥有上下文的层记录，避免重复日志。
- 包头、原始长度、逐包进度和协议帧只允许 Debug/Verbose；连接、配置、认证、刷新和读写阶段使用 Information；可恢复回退使用 Warning；最终失败和会话失效使用 Error。
- 用户可见异常和日志模板使用对应项目的 `Localization/Strings.resx` 与 `Strings.en.resx`，参数通过资源格式化方法传入。

## 测试约束

- 测试项目固定放在 `.tests`，不得加入 Git、解决方案发布产物或正式 NuGet 包。
- 优先用模拟传输覆盖线路顺序、ACK/NAK、取消、超时、分片、首包识别、资源失效和会话状态；真实设备验证只能作为额外证据，不能替代边界测试。
- 新行为采用测试先行：先写能复现缺陷或定义行为的测试，再实现最小修复；测试应验证协议结果、写入字节、线路顺序、状态和资源释放。
- 每个任务至少运行目标测试、Release 构建和 `git diff --check`。大型改动还要运行完整测试和必要的内存/吞吐检查。
- 推荐命令：

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore
dotnet build GeekFlashCore.slnx -c Release --no-restore
git diff --check
git status --short --ignored
```

- 测试夹具、临时日志、Benchmark 输出、`bin/obj` 和 `temp` 必须保持 ignored；提交前确认没有被跟踪。

## 编辑和提交规则

- 先读后改，修改范围保持在当前任务涉及的模块；不要顺手重构无关代码。
- 使用补丁方式编辑文件，保留用户已有修改；不得使用破坏性 Git 操作覆盖未知来源的工作区内容。
- 每个独立能力使用简短英文提交，提交信息说明行为，例如 `fix(qcom): align legacy digest recovery`。
- 提交前审查完整 diff、资源键中英文对应关系、异常路径、敏感日志和忽略文件状态。
- 提交只包含当前任务的生产代码和必要文档；`.tests` 中的本地测试用于验证但不提交。

## 交付检查

结束一个大型任务前必须确认：

1. 设计文档、实施计划、进度和未决风险已同步。
2. 新旧线路、同步入口和异步入口的行为没有未经说明的差异。
3. 测试覆盖新增行为和失败路径，测试、Release 构建、差异检查均通过。
4. 日志不泄露敏感内容，用户可见文本已资源化，资源和 Stream 生命周期明确。
5. 性能路径没有按镜像大小增长的额外物化或大对象分配；无法用本地环境确认的部分记录为风险。
6. `git status --short` 只显示预期结果，`.tests`、临时文件和构建产物没有被跟踪。
7. 最终汇报包含修改文件、行为变化、验证结果、提交号、工作区状态和后续风险。

## 当前进度来源

开始 SPRD 工作前，读取 `docs/plans/2026-10-07-sprd-protocol-design.md`、`docs/plans/2026-10-07-sprd-protocol-implementation.md` 和 `docs/sprd.md`，再检查工作区与最近提交。参考源为 `D:\Code\CSharp\SPRDClientCore-Xia` 和 `D:\Code\CPlusPlus\SPD_Flash_Tool_Source_Code`；当前没有实机证据。Loader 地址、容量单位、64 位布局、禁转义及奇数补零必须显式确认，不自动重发写命令或试探容量。测试位于 ignored `.tests/GeekFlashCore.Protocol.Sprd.Tests`。用户追加 YC-nw/SPRDClientCore 与 TomKing062/spreadtrum_flash 后的严格 GPT 容量、显式 Raw v1/v2 和 UID 最新范围与恢复来源为 `docs/plans/2026-10-08-sprd-upstream-completion-design.md` 与对应 implementation；SPRD-09 允许 GPT 从一次固定前缀读取中自动识别唯一完全校验有效的 512/4096 布局，保留手动覆盖，原生单位/前缀窗口和 Raw flush/USB 包大小仍须确认，无设备试读回退、NV 变换或整盘推断。

SPRD-10 的最新默认入口为 Auto，基于一次首帧的唯一校验和明确响应判断 BootROM/FDL1/FDL2，再获取实际所需 Loader。仅首个 CHECK_BAUD 完全无响应允许一次不同 CONNECT 查询；部分帧/坏校验/取消不回退，不重发写命令。已加载 FDL2 自动入口按参考握手确认 DISABLE_TRANSCODE ACK；手动入口保持原线路。恢复另读 `docs/plans/2026-10-08-sprd-entry-detection-design.md` 与对应 implementation；旧文档的显式入口默认约束被本设计替代，无实机证据。

SPRD-11 最新容量来源默认 Auto，优先校验固定 GPT 前缀，只有完整非 GPT 前缀或允许的 READ_START 空拒绝且 READ_END ACK 后才查询原生清单；损坏/保护性 MBR/歧义 GPT 不降级。原生单位不猜，缺单位的完整有效清单仅报告配置错误并保留会话与来源缓存，不启动写入。Source 是容量获取方式，不是 MBR/NAND/PMT 证明。恢复另读 `docs/plans/2026-10-08-sprd-partition-source-design.md` 与对应 implementation；手动 Native/GPT 旧线路保持，无实机。

SPRD-12 并入本地 main 的范围、工作区保留、回归结果及合并前 MTK 失败对照见 `docs/plans/2026-10-08-sprd-merge-implementation.md`。

SPRD-13 补齐 CLI 自动串口发现：仅已确认 `1782:4D00` 标识为 SPRD 候选，`--protocol sprd` 可无手动端口扫描/等待，已有 `--port`/`--usb` 覆盖保留；阶段仍由 BSL 握手决定，不自动切 DIAG 或替换驱动。发现后按实际协议重新校验选项，交互会话保留 SPRD 协议；热插拔先订阅再启动并复查库存，取消后不返回传输。Windows 仅枚举 Present 设备，并监听既有实例 Present 变化；历史未在场的 COM 不能当作当前设备。恢复见 `docs/plans/2026-10-10-sprd-cli-discovery-design.md` 和对应 implementation。

SPRD-14 补齐实机连接：Auto 首次 CHECK_BAUD 默认500ms；完整空 CRC16 VERIFY_ERROR 后只允许一次 CRC16 CONNECT 并要求同校验空 ACK，FDL 拒绝/坏帧/部分帧不降级。BootROM EXEC 后默认有界可取消等待500ms；FDL2 上传独立528-byte profile，存储仍4096；legacy v4只接受精确256-byte元数据，不自动Raw/禁转义。iPlay40/ums512 实机已验证自动发现→BootROM→FDL1→FDL2→StorageReady以及已有FDL2接入；容量/存储/其他型号未验证。恢复见 `docs/plans/2026-10-10-sprd-live-connection-design.md` 和对应 implementation；历史无实机/严格不接受任何VERIFY_ERROR的说明被上述精确范围替代。

SPRD-15 支持 CLI `--pac`：复用 FirmwareUnpacker，FirmwareEntry.ResourceId 保留 PAC File-ID（其他格式null）；按唯一FDL/FDL1、FDL2角色及唯一BMAConfig File/ID/Block/Base读取Loader和uint地址，在设备发现/I/O前有界预检，直接流式读取包条目，不解压整包。XML≤16份且每份≤2MiB、禁DTD/外部解析，坏/歧义配置不猜；资源容器明确拥有包Source并清理部分失败/迟到结果。无协议时选sprd，不能混用手动Loader/地址；其他显式profile保持，不执行XML刷写/Erase/NV/Reset，不自动容量单位/Raw/禁转义/64位。实机已用T1020S PAC验证Auto BootROM→FDL1→FDL2→StorageReady；其他PAC/型号和存储待验证。恢复见 `docs/plans/2026-10-10-sprd-pac-cli-design.md` 和对应 implementation。

SPRD-16 接入 CLI 命名分区只读 resolver：支持 browse/ls、嵌套 read 和 lp info，不虚构整盘偏移/LUN，不提供 LP 编辑/嵌套 write，普通分区 write 保留。启动 --sprd-block-size 范围1..65534、默认4096，FDL仍独立528；不自动Raw/64位/失败回退或重发。接收热路径在补充缓冲前后、每256字节及帧完成检查命令/总预算和取消。SerialPort通知TryEnter避免stream/reader锁反转，库存及最多100ms轮询兜底，不添后台接收或改变原预算。iPlay40/ums512已实测GPT512容量、super内system/vendor/product及vendor文件系统浏览；固定32KiB+显式brom+禁转义完整boot35MiB为13.374秒约2.62MiB/s，与原备份校验一致，misc同会话32KiB/4KiB数据一致。写入只有模拟分片线路证据。读取速度、失败记录、二次握手不稳定及最新提交/部署结果见 `docs/plans/2026-10-10-sprd-browser-performance-design.md` 和对应 implementation；此前CLI未接入browser及所有存储未实测的描述由本记录精确替代。

开始 Qualcomm 相关工作前，依次阅读：

1. 本文件。
2. `docs/plans/2026-09-04-qcom-protocol-design.md`。
3. `docs/plans/2026-09-04-qcom-protocol-implementation.md` 的“进度”和“未决风险”。
4. 当前分支的 `git status --short`、最近提交和相关测试。

完成一个任务后，把事实、命令结果和风险写回实施计划，再进入下一项工作。

开始 MTK 工作前，读取本文件、`docs/plans/2026-10-05-mtk-protocol-design.md`、`docs/plans/2026-10-05-mtk-protocol-implementation.md`、`docs/plans/2026-10-05-mtk-brom-method-mapping.md` 和 `docs/plans/2026-10-05-mtk-exploit-framework.md`，再检查当前工作区与最近提交。MTK 测试位于 ignored `.tests/GeekFlashCore.Protocol.Mtk.Tests`。

MTK 标准功能补全的最新范围、证据和未决风险另见 `docs/plans/2026-10-05-mtk-standard-completion-design.md` 与 `docs/plans/2026-10-05-mtk-standard-completion-implementation.md`。此范围禁止新增漏洞利用；已有未跟踪漏洞计划和 ignored 测试不得混入标准功能提交。

更新至 `D:\Code\Rust\penumbra-main` 后的 Legacy NAND/IoT、独立硬件加密、Scatter、eFuse、命名分区与 Penumbra2 扩展 ABI 进度，以 `docs/plans/2026-10-05-mtk-nonexploit-parity-design.md` 和 `docs/plans/2026-10-05-mtk-nonexploit-parity-implementation.md` 为最新恢复来源；历史文档的缺口列表按日期保留。
