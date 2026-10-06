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

MTK 使用 LibUsb；BROM、三种 DA 与扩展共用串行化 gate、有限预算和会话代数。BROM 访问只在 Probe 后、DA 执行前开放，回调或跳转后通道失效。标准认证由宿主提供合法材料和签名。按用户最新授权，漏洞部分提供 `IMtkExploitStrategy` 及阶段/context/result 框架，另允许 LineCode、Carbonara、HeapBait、Unfused 的非执行接口占位类；占位类仅声明 descriptor、校验参数/取消并返回 NotApplicable，不读取设备上下文或执行 I/O。允许显式注入的宿主回调在连接阶段调用，不得添加具体漏洞策略逻辑、默认注册、漏洞算法、补丁生成或攻击载荷。默认连接不执行策略，完成结果不等于认证成功，终止结果要求重连。具体契约与阶段见 `docs/plans/2026-10-05-mtk-exploit-framework.md`，占位类范围见 `docs/plans/2026-10-06-mtk-exploit-placeholders.md`。扩展须验证 ACK/context；RPMB 不作为普通块设备，seccfg 写入必须备份、最小对齐写和回读，未知写结果不重试。

用户进一步授权补齐占位类依赖，范围是通用离线 DA 窗口、流式摘要/字节搜索、验证后的宿主元数据、资源清单/借用源读取及分析/变换接口；四个 Execute 不解析或调用这些依赖。不得把参考二进制、漏洞特征/地址表、参数填充、定位算法或补丁实现混入依赖层。宿主资源不自动下载/加载，默认仓库为空，真实无参构造保持。最新范围及验证见 `docs/plans/2026-10-06-mtk-exploit-dependencies-design.md` 与对应 implementation 记录。

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

开始 Qualcomm 相关工作前，依次阅读：

1. 本文件。
2. `docs/plans/2026-09-04-qcom-protocol-design.md`。
3. `docs/plans/2026-09-04-qcom-protocol-implementation.md` 的“进度”和“未决风险”。
4. 当前分支的 `git status --short`、最近提交和相关测试。

完成一个任务后，把事实、命令结果和风险写回实施计划，再进入下一项工作。

开始 MTK 工作前，读取本文件、`docs/plans/2026-10-05-mtk-protocol-design.md`、`docs/plans/2026-10-05-mtk-protocol-implementation.md`、`docs/plans/2026-10-05-mtk-brom-method-mapping.md` 和 `docs/plans/2026-10-05-mtk-exploit-framework.md`，再检查当前工作区与最近提交。MTK 测试位于 ignored `.tests/GeekFlashCore.Protocol.Mtk.Tests`。

MTK 标准功能补全的最新范围、证据和未决风险另见 `docs/plans/2026-10-05-mtk-standard-completion-design.md` 与 `docs/plans/2026-10-05-mtk-standard-completion-implementation.md`。此范围禁止新增漏洞利用；已有未跟踪漏洞计划和 ignored 测试不得混入标准功能提交。

更新至 `D:\Code\Rust\penumbra-main` 后的 Legacy NAND/IoT、独立硬件加密、Scatter、eFuse、命名分区与 Penumbra2 扩展 ABI 进度，以 `docs/plans/2026-10-05-mtk-nonexploit-parity-design.md` 和 `docs/plans/2026-10-05-mtk-nonexploit-parity-implementation.md` 为最新恢复来源；历史文档的缺口列表按日期保留。
