# GeekFlashCore 全仓质量治理设计

日期：2026-09-06

状态：待确认

适用范围：`src` 下全部正式项目，以及仅用于本地验证、被 Git 忽略的 `.tests`

## 1. 背景与基线

本轮目标不是机械追求更少的代码或零分析器告警，而是在保持设备可观察行为的前提下，修复能够由测试证明的逻辑缺陷，收敛重复实现，强化公共契约、资源所有权和取消边界，并优化具有明确证据的热路径。

2026-09-06 的只读基线如下：

- `dotnet build GeekFlashCore.slnx -c Release --no-restore` 成功，0 警告、0 错误；
- Qcom 本地测试 229/229、CLI 本地测试 48/48、Android LP 本地测试 55/55 通过；
- 受跟踪工作区干净，`.tests`、`bin/obj`、IDE 文件和 `temp` 保持 ignored；
- `AnalysisLevel=latest-all` 的重新构建产生 320 条唯一代码分析诊断，其中包含真实候选问题，也包含协议字段布局、所有权转移、固定厂商算法和同步协议边界造成的误报；
- GPT、Android Sparse、BlockDevice、Ext/Erofs 和 Transport 尚无各自独立的本地测试工程，现有测试全绿不能覆盖这些模块的全部公共契约。

已确认的结构性事实：

- 仓库存在三套 `Stream` 到 `IReadableBlockDevice` 的内部适配器，分别位于 Android Sparse、Android LP 和 Qcom Sparse 计划路径；
- Ext/Erofs 存在四套等价的 `Stream.ReadExactly` 循环；
- Qcom Sparse 写入计划先使用严格解析器验证，再使用旧模型解析器生成区域，对同一输入进行两次完整元数据遍历；
- CLI 多个用户可见消息仍以中文字符串直接写在生产代码中，没有使用中英文资源；
- `GeekFlashCore.Protocol.Abstractions` 引用了未被源码使用的 Serilog 包；
- Firehose 策略返回的映射范围没有统一的不变量验证，各读写入口依赖具体策略自行保证范围完整；
- Firehose 多个调用点已持有取消令牌，但没有一致传到会话命令边界；会话目前把命令期间的所有非 NAK 异常都视为 Faulted，需要先定义取消发生在写入前、命令后和 Raw 阶段时的不同语义。

## 2. 目标

1. 修复能够用失败测试复现的逻辑、边界、取消和资源生命周期缺陷。
2. 把通用的 Stream 块设备适配能力下沉到 BlockDevice 层，移除模块内重复实现。
3. 让 Firehose 策略扩展点具有明确且集中验证的不变量，错误在首次线路写入前暴露。
4. 保持 Sahara、Firehose、普通 Digest、VIP、Oplus Digest 和厂商认证的既有线路顺序。
5. 消除有证据的重复解析、热路径分配和无意义依赖，不引入按镜像总大小增长的新物化。
6. 完成用户可见文本资源化，删除无价值或过时注释，保留解释协议原因、所有权和兼容语义的注释。
7. 建立可重复的严格分析基线，将真实问题和有理由保留的诊断区分记录。

## 3. 非目标

- 不依据静态分析结果批量改写全部公共字段、枚举底层类型或异常构造函数。
- 不改变只有真实设备才能确认的厂商命令、认证顺序、重试次数或响应宽容度。
- 不将同步 Sahara/Firehose 数据路径改为异步 I/O，也不在同步协议层等待异步资源。
- 不为尚不存在的协议或厂商扩展点建立抽象层。
- 不把 `.tests` 加入 Git、正式解决方案发布产物或 NuGet 包。
- 不在没有测试或测量证据时引入对象池、自定义序列化器、Unsafe 或复杂缓存。

## 4. 兼容性策略

本轮允许公共 API 发生破坏性变化，但必须满足至少一个条件：

- 当前 API 无法表达资源所有权或有效状态；
- 当前 API 允许策略返回无法安全执行的范围；
- 多个正式模块为同一能力维护不同实现；
- 调整能消除已证明的逻辑错误，且兼容重载会继续保留错误或歧义。

仅为命名偏好、分析器风格或理论性能收益，不破坏公共 API。若确需破坏，实施计划必须列出旧签名、新签名、迁移方式和受影响调用点。

## 5. 架构设计

### 5.1 通用 Stream 块设备适配器

在 `GeekFlashCore.BlockDevice` 增加一个正式的 seekable Stream 适配器，负责：

- 从构造时的 origin 暴露固定长度的 `IReadableBlockDevice`；
- 校验可读、可定位、长度、逻辑块大小和 `DeviceOwnership`；
- `ReadAt` 使用实例锁串行化 Position 修改，并在读取后恢复调用方 Stream 的原位置；
- `Borrow` 不释放来源 Stream，`Transfer` 在适配器释放时释放来源 Stream；
- 空读取、尾部读取、越界、短读、并发读取和重复释放具有明确行为。

Android Sparse、Android LP 和 Qcom 通过该类型组合能力，不再维护各自的 Stream 块设备类。适配器属于基础设施 Adapter；它不理解 Sparse、LP 或 Qcom 领域语义。

### 5.2 Firehose 策略范围契约

保留 `IFirehoseStoragePolicy` Strategy 扩展点，但在执行层增加统一范围规范化与验证。对完整请求，映射结果必须：

- 非 null、非空；
- 每段 `SectorCount > 0`；
- Start/Count 的加法不溢出；
- 按原请求方向连续覆盖，不重叠、不留洞、不超出请求；
- 所有段的 SectorCount 总和严格等于原请求；
- 在任何 Firehose XML 或 Raw 写入前完成全量验证。

Read、Span Program 和流式/Sparse Program 复用同一个验证器。策略仍可改变 Label/FileName，并可把连续范围拆成固定窗口或 GPT 兼容单扇区，但不能改变请求覆盖的物理范围。

### 5.3 取消和会话状态

取消按阶段建模：

1. 进入命令前已取消：不写线路，不改变会话可用状态；
2. `_beforeCommand` 完成后、主命令写入前取消：若前置策略已完成且线路重新处于 XML 命令状态，则不发送主命令并恢复原状态；
3. XML 已发送并等待响应时：同步 Transport 没有可中断读能力，只能在读超时或响应完成后的安全边界观察取消；不得伪装成即时取消；
4. Raw 传输期间取消：立即使会话 Faulted，要求重新连接；
5. 资源 Provider 超时或调用方取消：迟到的敏感响应必须被观察和释放，链接 CTS 的生命周期不得早于仍使用其 Token 的 Provider 任务。

所有持有取消令牌的调用点显式传到会话边界。`FirehoseSession` 区分“线路写入前取消”和“线路可能已改变后的取消”，避免把安全的预取消误标为协议损坏。

### 5.4 Qcom 门面职责收敛

`QcomProtocol` 继续承担公共门面、会话串行化和连接编排，但把资源请求的以下职责抽到内部协作者：

- 异步请求与同步宿主桥接；
- 调用方取消、会话生命周期和资源超时的链接；
- Provider 异常到 `QcomResourceException` 的转换；
- 迟到认证载荷的释放；
- Provider 任务与 CancellationTokenSource 的安全生命周期。

该协作者只处理资源生命周期，不发送 Sahara/Firehose 数据，也不拥有 Transport。Sahara、Firehose、存储和厂商策略继续保持现有领域边界。

### 5.5 Sparse 单次解析

以 `SparseDocument` 的严格解析结果作为唯一结构事实来源。新增内部区域投影能力，把已经验证的 Raw、Fill、Don't Care 和 CRC chunk 转换为 Qcom 写入所需的流式区域，不再调用旧 `SparseImageParser.Parse(Stream)` 做第二次完整解析。

区域投影必须：

- 合并安全的相邻 Raw chunk；
- 保留 Fill 的小缓冲生成方式；
- 跳过 Don't Care，但保持后续 StartSector 正确；
- 不为展开后的镜像分配等长数组；
- 使用源窗口相对偏移，支持非零 `SourceOffset`；
- 在计划生成前完成 CRC、长度、块数和目标范围验证。

旧公开解析 API 是否保留，由调用点和兼容性测试决定；若仅供兼容，标记为旧模型并停止在新路径使用，不为删除而删除。

### 5.6 CLI 资源化与依赖清理

CLI 的帮助、参数错误、设备等待、资源询问、认证提示、取消和失败消息全部进入 `Localization/Strings.resx` 与 `Strings.en.resx`。协议名、命令名、VID/PID、路径和设备返回文本作为格式参数保留。

移除 `GeekFlashCore.Protocol.Abstractions` 中未使用的 Serilog PackageReference；若构建证明任何消费者依赖该传递包，则由消费者显式引用。删除“global using 指令”“Rider 生成很好”等无维护价值注释；把协议取值限制改为简短英文 XML 文档或代码验证，保留说明兼容原因和安全边界的注释。

### 5.7 性能优化准入

优先执行能够从结构上证明的优化：单次 Sparse 解析、消除重复适配器、避免不必要的完整数组复制。LibUsb ControlIn/ControlOut 的临时数组只有在受控测试证明池化不会泄露旧数据、不会改变第三方 API 行为且能减少稳态分配时才调整。

LP 日志继续保证日志 Provider 失败不改变事务结果。是否使用 `LoggerMessage` 或其他无装箱入口，必须同时满足本地化模板、EventId 和异常隔离要求；无法保持三者时记录为有理由保留的分析器诊断。

## 6. 错误、安全和资源所有权

- 外部长度、偏移、扇区计数和映射总和使用 checked 或减法式有界校验；
- 策略错误使用领域异常或参数异常，并在首次设备写入前失败；
- Stream 适配器只按显式 `DeviceOwnership` 释放来源；
- 迟到认证响应中的敏感载荷始终释放，异常和日志不包含 Payload、Token、签名、完整 Digest 或私密 Hash；
- 取消若发生在线路状态不确定的阶段，会话明确 Faulted，不尝试隐式复用；
- 清理路径可以聚合或保留首个释放异常，但不得用空 `catch` 隐藏正常操作中的错误；仅 best-effort 清理和日志隔离允许吞异常，并需要说明原因；
- XML 继续使用结构化构建、允许列表和禁用 DTD/外部实体的解析配置。

## 7. 文件范围

预计新增或修改以下区域，精确文件列表在实施计划中按任务锁定：

- `src/GeekFlashCore.BlockDevice`：通用 Stream 块设备适配器；
- `src/GeekFlashCore.Android.Sparse`：删除重复适配器、提供严格文档到流式区域的投影；
- `src/GeekFlashCore.Android.Lp`：复用通用适配器；
- `src/GeekFlashCore.Protocol.Qcom/Firehose`：策略范围验证、取消传播、Sparse 单次计划和资源请求协作者；
- `src/GeekFlashCore.Protocol.Qcom/QcomProtocol*.cs`：门面委托与会话生命周期；
- `src/GeekFlashCore.CLI`：用户可见文本资源化；
- `src/GeekFlashCore.Protocol.Abstractions/*.csproj`：移除未使用依赖；
- Ext/Erofs 相关文件：使用框架 `Stream.ReadExactly`，删除重复私有循环；
- `docs/plans`：实施进度、命令证据、未决风险；
- `.tests`：新增 BlockDevice、Sparse/文件系统契约测试，扩充 Qcom 和 CLI 回归测试，保持 ignored。

## 8. 测试矩阵

### 8.1 BlockDevice Adapter

- 非零 origin 和固定 length；
- 尾部短读、空读取和越界；
- 每次 ReadAt 后恢复来源 Position；
- Borrow/Transfer 所有权和 Dispose 幂等；
- 两个并发 ReadAt 不互相污染 Position；
- 不可读、不可 Seek、非法长度和非法块大小。

### 8.2 Firehose 映射与取消

- null/空、零长度、负长度、溢出、重叠、缺口、乱序、超范围和总数不符均在零线路写入时失败；
- OplusDigestPt、Legacy 固定窗口和 GPT 特殊单扇区仍保持原有 XML/Raw 顺序；
- 预取消不写线路且会话可继续；
- Raw 期间取消使旧会话和旧块设备租约失效；
- Provider 超时后迟到的认证材料被释放，Provider 观察 Token 时不遇到已释放 CTS 引起的异常。

### 8.3 Sparse

- Raw 合并、Fill、Don't Care、CRC 和混合 chunk；
- 非零 SourceOffset；
- 大 Sparse 计划的内存与区域数量按 chunk 数增长，不按展开长度增长；
- 损坏 header/chunk/CRC 在任何 Firehose 写入前失败；
- 单次解析测试通过受控计数块设备确认元数据不被完整扫描两次。

### 8.4 CLI 与公共契约

- `zh-CN` 与 `en-US` 的帮助、错误、取消和 Provider 提示；
- 中英文资源键集合一致；
- 公共 null/范围参数抛出正确异常类型与 ParamName；
- 移除 Serilog 传递依赖后所有正式项目仍可构建。

## 9. 验证命令

每个任务执行目标测试、Release 构建和差异检查。阶段结束执行：

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore
dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore
dotnet test .tests/GeekFlashCore.Android.Lp.Tests/GeekFlashCore.Android.Lp.Tests.csproj -c Release --no-restore
dotnet build GeekFlashCore.slnx -c Release --no-restore
dotnet build GeekFlashCore.slnx -t:Rebuild -c Release --no-restore -p:AnalysisLevel=latest-all -p:EnableNETAnalyzers=true -p:TreatWarningsAsErrors=false
git diff --check
git status --short --ignored
```

新增测试工程使用同样的 Release、`--no-restore` 验证方式。严格分析结果按规则编号和处置原因记录，不以无差别抑制达到零告警。

## 10. 提交拆分

设计批准后，实施按可独立验证的能力拆分，预计顺序为：

1. `test(core): add quality review guardrails`
2. `fix(qcom): validate mapped firehose ranges`
3. `fix(qcom): align cancellation lifetimes`
4. `refactor(block): share seekable stream adapter`
5. `perf(qcom): parse sparse programs once`
6. `fix(cli): localize remaining user messages`
7. `refactor(core): remove redundant dependencies and comments`
8. `refactor(qcom): isolate resource resolution`
9. `docs: record quality review results`

若某项测试证明不存在预期缺陷，取消对应生产修改并在进度中记录证据，不为维持提交列表而改代码。

## 11. 风险与停止条件

- Firehose 取消和 Dispose 涉及会话状态竞争；若无法用模拟传输证明线路状态，停止在更窄的命令前边界，不推断真实设备行为。
- Sparse 单次解析可能影响旧 `SparseImage` 区域合并语义；必须逐字节比较既有 Raw/Fill/Don't Care 线路。
- 通用 Stream 适配器引入锁和 Position 恢复；对单线程热路径的开销需与正确性、复用收益一起评估。
- CLI 资源化可能改变测试快照或脚本文本；退出码、命令名和结构化值保持不变。
- 公开 API 破坏会影响仓库外消费者；每项破坏必须在最终汇报列出迁移方式。
- 真实 Qualcomm、Ext/Erofs 和多 LUN 设备行为本轮仍可能无法验证；模拟传输和镜像夹具证据必须明确标注，不能写成硬件结论。
- 任一阶段出现无法解释的已有测试回归、资源泄漏或协议线路变化时，停止该阶段并重新审查设计，不扩大修复范围掩盖失败。

## 12. 完成标准

- 所有新增行为均经历可解释的红—绿测试；
- 重复 Stream 适配器和自写 ReadExactly 循环被统一，且所有权行为有测试；
- Firehose 映射错误在零线路写入时失败，取消状态有明确测试；
- Qcom Sparse 正式写入路径只执行一次结构解析；
- CLI 用户可见文本完成中英文资源化；
- 未使用依赖和无维护价值注释被删除；
- 正式 Release 构建、全部本地测试、资源键、差异检查通过；
- 严格分析器剩余诊断具有分类记录，没有新增未经解释的高风险诊断；
- 实施计划记录日期、任务编号、行为结论、验证证据、提交号、工作区状态和真实设备风险。
