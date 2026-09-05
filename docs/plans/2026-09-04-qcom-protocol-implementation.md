# Qualcomm Protocol Implementation Plan

> **执行要求：** 后续会话应先阅读本计划和同目录设计文档，再从“进度”中第一个未完成任务继续。每项功能使用测试先行，小步提交。

**目标：** 完成可独立复用的 Qualcomm Sahara/Firehose 协议栈，兼容 GeekFlashTool 的厂商逻辑，并吸收 QnQcLIB 的安全、高性能 Sparse 与 Oplus Digest 行为。

**架构：** 公共 `QcomProtocol` 负责异步资源获取和连接编排；同步 `SaharaSession`、`FirehoseSession` 负责确定性的底层 I/O；Configure、存储、厂商认证和 Digest 通过内部策略组合。MessagePipe 仅存在于可选适配项目，不进入协议核心。

**技术栈：** .NET 8、同步 `ITransport`、Span/Memory、ArrayPool、GeekFlashCore Android.Sparse、MessagePipe 1.8.2、QcomImageUtils、OplusDigestUtils、Serilog。

**参考实现：**

- `D:\Code\CSharp\GeekFlashTool\GeekFlashTool.EdlClient\Sahara.cs`
- `D:\Code\CSharp\GeekFlashTool\GeekFlashTool.EdlClient\Firehose.cs`
- `D:\Code\CSharp\GeekFlashTool\GeekFlashTool.EdlClient\FirehoseClient.cs`
- `D:\Code\CSharp\GeekFlashTool\GeekFlashTool.EdlClient\Module\Xiaomi.cs`
- `D:\Code\CSharp\GeekFlashTool\GeekFlashTool.EdlClient\Module\Oppo.cs`
- `D:\Code\CSharp\GeekFlashTool\GeekFlashTool.EdlClient\Module\Oneplus.cs`
- `D:\Code\CSharp\GeekFlashTool\GeekFlashTool.EdlClient\Module\OneplusParam.cs`
- `D:\Code\CSharp\GeekFlashTool\GeekFlashTool.EdlClient\Module\Nothing.cs`
- `D:\Code\CSharp\GeekFlashTool\GeekFlashTool\Services\FirehoseClientService.cs`
- `D:\Code\CSharp\QnQcLIB\QnQcLIB\Qcom\Firehose.cs`

## 执行规则

- 测试工程固定放在 `D:\Code\CSharp\GeekFlashCore\.tests`，不得加入 Git；每次提交前执行 `git status --short --ignored .tests` 确认其为 ignored。
- 先写会失败的测试并确认失败原因，再写最小实现；测试命令使用 `dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release`。
- 每个任务结束执行目标测试、`dotnet build GeekFlashCore.slnx -c Release --no-restore` 和 `git diff --check`。
- 不复制参考项目中的异步串口循环、无界重试、重复分配或吞异常代码，只保持设备可观察行为。
- 未经证据不得把 Loader 静态提示当作设备运行时能力。
- Git 提交只包含当前任务，提交信息使用简短英文。

## Task 1：建立本地测试护栏并修复现有会话缺陷

**文件：**

- 修改：`.gitignore`
- 修改：`src/GeekFlashCore.Protocol.Qcom/Internals/SaharaProtocol.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom/Internals/FirehoseProtocol.cs`
- 测试（忽略）：`.tests/GeekFlashCore.Protocol.Qcom.Tests/Sahara/SaharaUploadTests.cs`
- 测试（忽略）：`.tests/GeekFlashCore.Protocol.Qcom.Tests/Firehose/FirehoseLifetimeTests.cs`

**步骤：**

1. 在 `.gitignore` 增加根目录 `/.tests/`，创建本地 xUnit 工程并引用 Qcom、Qcom.Abstractions 和 Transport.Abstractions。
2. 写内存传输测试，覆盖 Sahara 多 Image ID 切换、重复 ID、请求越界、短流和 Stream 释放；先确认当前多镜像共用同一 Stream 的测试失败。
3. 写 Firehose Dispose/未连接状态测试，确认对象名、未初始化 `_targetInfo` 和状态行为问题。
4. 修改 Sahara 上传：按 Image ID 管理/切换并释放 Stream，验证偏移和长度，拒绝重复 ID，所有 checked 转换先于分配和读写。
5. 修改 Firehose 生命周期：构造时初始化目标信息，正确抛出 `ObjectDisposedException(nameof(FirehoseProtocol))`，修正日志名称并保证 Dispose 幂等。
6. 运行测试、Release 构建和差异检查。
7. 提交：`🐞 fix(qcom): harden session lifetimes`

## Task 2：定义稳定的 Qualcomm 公共契约

**文件：**

- 修改：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Interfaces/IQcomProtocol.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Models/SaharaTargetInfo.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Models/FirehoseTargetInfo.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Messages/SaharaMessages.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Configuration/QcomProtocolOptions.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Configuration/FirehoseConfiguration.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Types/QcomVendorKind.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Types/SecureBootState.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Types/OplusDigestMode.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Models/QcomTargetInfo.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Models/FirehoseProgramRequest.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Models/FirehoseReadRequest.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Models/FirehoseCommandResult.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Exceptions/*.cs`
- 测试（忽略）：`.tests/GeekFlashCore.Protocol.Qcom.Tests/Abstractions/QcomOptionsTests.cs`

**步骤：**

1. 写配置验证测试：超时、Payload、扇区大小、Legacy 分段和命令阈值必须为安全范围。
2. 定义不可变或 init-only 公共模型；原始 Hash 使用只读内存并在构造边界复制，避免外部数组修改会话证据。
3. 扩充 `IQcomProtocol` 的显式 Probe、上传、Configure、Program、Read、自定义命令能力，同时保留 `IProtocol` 兼容面。
4. 建立传输、Sahara、资源、Configure、Firehose NAK、Digest、认证和会话失效异常层次。
5. 确认 `OplusDigestMode` 只暴露 `None`、`OplusDigestPt`、`OplusDigestLegacy`。
6. 运行测试和构建。
7. 提交：`✨ feat(qcom): define protocol contracts`

## Task 3：实现运行时资源接口和可选 MessagePipe 适配层

**文件：**

- 修改：`src/GeekFlashCore.Protocol.Abstractions/GeekFlashCore.Protocol.Abstractions.csproj`
- 新建：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Resources/ISaharaImageProvider.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Resources/IOplusDigestProvider.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Resources/IVendorAuthenticationProvider.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Resources/IFirehoseConfigurationProvider.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Messages/OplusDigestMessages.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Messages/VendorAuthenticationMessages.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Messages/FirehoseConfigurationMessages.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom.MessagePipe/GeekFlashCore.Protocol.Qcom.MessagePipe.csproj`
- 新建：`src/GeekFlashCore.Protocol.Qcom.MessagePipe/*.cs`
- 修改：`GeekFlashCore.slnx`
- 测试（忽略）：`.tests/GeekFlashCore.Protocol.Qcom.Tests/Resources/MessagePipeResourceProviderTests.cs`

**步骤：**

1. 写资源适配测试：单一 Handler 返回、取消传播、Handler 异常包装、空资源拒绝和配置快照不可变。
2. 从通用 `Protocol.Abstractions` 移除 MessagePipe；消息记录不得实现 MessagePipe 接口。
3. 定义四个强类型 Provider，所有异步方法返回 `ValueTask<T>` 并接受 `CancellationToken`。
4. 新建可选集成项目，引用 MessagePipe 1.8.2 和 Qcom.Abstractions；用 `IAsyncRequestHandler<TRequest,TResponse>` 实现 Provider 适配器。
5. 为大型 Loader/Digest 保留 `IDataSource`，定义资源有效期；认证响应使用可清零的所有权对象。
6. 加入解决方案并验证不引用适配项目时 Qcom 核心仍可构建使用。
7. 提交：`✨ feat(qcom): add runtime resource providers`

## Task 4：集成 Loader 分析和 Secure Boot 证据模型

**文件：**

- 修改：`src/GeekFlashCore.Protocol.Qcom/GeekFlashCore.Protocol.Qcom.csproj`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Loaders/QcomLoaderInspector.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Loaders/QcomLoaderInfo.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Loaders/SecureBootEvaluator.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Loaders/QcomEvidenceMerger.cs`
- 测试（忽略）：`.tests/GeekFlashCore.Protocol.Qcom.Tests/Loaders/QcomLoaderInspectorTests.cs`
- 测试（忽略）：`.tests/GeekFlashCore.Protocol.Qcom.Tests/Loaders/SecureBootEvaluatorTests.cs`

**步骤：**

1. 使用小型 ELF/MBN fixture 写 QcomImageUtils 结果转换测试；测试第三方类型不会泄漏到公共 API。
2. 引用固定版本 QcomImageUtils，并封装 `TryParse(string|ReadOnlySpan<byte>)`。
3. 映射 OEM、SoC、SupportedCommands、最大 Payload、Root CA Hash 和 programmer 有效性。
4. 写三态 Secure Boot 测试：Hash 匹配为 Enabled；明确不匹配且 Loader 已运行才为 Disabled；其余为 Unknown。
5. 实现证据优先级：显式配置 > Firehose 运行时 > Loader 静态信息 > 安全默认值。
6. 对无法 Seek 的数据源使用受限池化探测，不把完整 Loader 常驻内存。
7. 提交：`✨ feat(qcom): inspect sahara programmers`

## Task 5：完成同步 Firehose 命令会话

**文件：**

- 重构：`src/GeekFlashCore.Protocol.Qcom/Internals/FirehoseProtocol.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom/Internals/FirehoseCmdSender.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom/Internals/FirehoseCmdReceiver.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom/Internals/FirehoseWireReader.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom/Internals/FirehoseCommandBuilder.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Firehose/FirehoseSession.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Firehose/FirehoseCommandExecutor.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Packets/FirehosePackets.cs`
- 测试（忽略）：`.tests/GeekFlashCore.Protocol.Qcom.Tests/Firehose/FirehoseCommandSessionTests.cs`

**步骤：**

1. 写内存线路测试：启动日志、ACK、NAK、多个 log、XML 与 Raw 粘包、Raw 后响应、超长 XML、短读和超时。
2. 建立严格同步 `FirehoseSession`，状态为 Created/Started/Configured/RawTransfer/Faulted/Disposed；并发调用必须失败。
3. 统一命令执行：发送 XML、接收响应、验证期望 RawMode，并保留结构化 NAK 与日志。
4. WireReader 使用有上限的池化 carry buffer，确保 XML 扫描不会因恶意长度无限增长。
5. CommandBuilder 继续缓存 schema，但消除每次值装箱中可避免部分；所有字符串属性进行 XML 转义，数值固定 invariant culture。
6. 原始 XML 入口限制最大长度和根标签，不允许 XML 声明外的 DTD/实体。
7. 提交：`♻️ refactor(qcom): build synchronous firehose session`

## Task 6：实现有界 Configure 协商状态机

**文件：**

- 新建：`src/GeekFlashCore.Protocol.Qcom/Firehose/Configuration/ConfigureNegotiator.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Firehose/Configuration/ConfigureState.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Firehose/Configuration/ConfigureEvidenceParser.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Models/FirehoseResponses.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom/Extensions/FirehoseResponseParser.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Packets/FirehosePackets.cs`
- 测试（忽略）：`.tests/GeekFlashCore.Protocol.Qcom.Tests/Firehose/ConfigureNegotiatorTests.cs`

**步骤：**

1. 为 GeekFlashTool 的成功、MemoryName 回退、Payload 修正、Digest 大小修正、扇区修正、Xiaomi 认证触发和重复 NAK 写逐帧测试。
2. 补齐 `SkipStorageInit`、ZTE OEM 等 Configure 属性，并保持字段大小写和发送顺序。
3. 实现不可变 ConfigureState；只有状态实际改变才允许重试，保存已见状态指纹并限制总次数。
4. 将设备建议值与调用方/Loader/安全上限合并，checked 转换后更新最终 Firehose 能力。
5. 不把字符串包含判断散布到会话中；由 EvidenceParser 解析兼容日志和响应属性。
6. 提交：`✨ feat(qcom): negotiate firehose configuration`

## Task 7：实现同步存储命令与块设备适配

**文件：**

- 新建：`src/GeekFlashCore.Protocol.Qcom/Firehose/Storage/FirehoseStorageService.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Firehose/Storage/FirehoseBlockDevice.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Firehose/Storage/FirehoseBlockDeviceLease.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Firehose/Storage/FirehoseRangeValidator.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Models/FirehoseTargetInfo.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom/Extensions/FirehoseResponseParser.cs`
- 测试（忽略）：`.tests/GeekFlashCore.Protocol.Qcom.Tests/Firehose/FirehoseStorageServiceTests.cs`

**步骤：**

1. 写 getstorageinfo、read、program、erase、setbootable、power、peek、digest、自定义命令的线路测试。
2. 解析 UFS/eMMC/NAND 存储信息和 BasicDevInfo，原始未知属性保留但不覆盖已验证字段。
3. 所有字节/扇区换算集中到 RangeValidator，拒绝未对齐、越界、零长度非法场景和乘法溢出。
4. Read 流式写入目标，Program 流式读取来源；wire length 对齐填充必须显式且可配置。
5. 实现 `IBlockDeviceProvider` 和 Lease，使 GPT、文件系统等上层模块可直接复用 Qualcomm 存储。
6. 提交：`✨ feat(qcom): add firehose storage operations`

## Task 8：实现高性能 Raw 与 Sparse 写入计划

**文件：**

- 新建：`src/GeekFlashCore.Protocol.Qcom/Firehose/Programming/FirehoseProgramPlanner.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Firehose/Programming/FirehoseProgramSegment.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Firehose/Programming/FirehoseProgramExecutor.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Firehose/Programming/SparseProgramPlanner.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom/Internals/FirehoseCmdSender.cs`
- 复用：`src/GeekFlashCore.Android.Sparse/SparseImageParser.cs`
- 复用：`src/GeekFlashCore.Android.Sparse/Models/SparseRegion.cs`
- 测试（忽略）：`.tests/GeekFlashCore.Protocol.Qcom.Tests/Programming/SparseProgramPlannerTests.cs`
- 测试（忽略）：`.tests/GeekFlashCore.Protocol.Qcom.Tests/Programming/FirehoseProgramExecutorTests.cs`

**步骤：**

1. 写 Raw、连续 Sparse Raw 合并、Fill、Don't Care、CRC、超大长度、截断输入和取消测试。
2. 以现有 Android.Sparse 解析结果生成统一 ProgramSegment，不复制 QnQcLIB 的 Sparse parser。
3. 合并物理连续且源也连续的 Raw 区域；超过设备 Payload/策略分段限制时无损拆分。
4. Fill 使用池化固定缓冲重复填充；Don't Care 只推进逻辑扇区，不发送数据。
5. Sender 使用一个租用缓冲循环完成读取、填充和写入；确保异常路径归还缓冲。
6. 添加可选分配基准，记录稳态单块发送分配并防止大对象堆分配。
7. 提交：`⚡ perf(qcom): stream sparse programs`

## Task 9：建立厂商识别与自定义命令策略

**文件：**

- 新建：`src/GeekFlashCore.Protocol.Qcom/Vendors/IVendorFirehoseStrategy.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Vendors/VendorStrategyResolver.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Vendors/GenericVendorStrategy.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Vendors/CustomCommandValidator.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Packets/FirehosePackets.cs`
- 测试（忽略）：`.tests/GeekFlashCore.Protocol.Qcom.Tests/Vendors/VendorStrategyResolverTests.cs`

**步骤：**

1. 写 OEM 静态提示、Firehose 日志纠正、显式覆盖和冲突证据测试。
2. 策略解析器返回 Generic、Xiaomi、Oplus、OnePlus、Nothing、ZTE 等策略，不允许 Configure 内部出现厂商大 switch。
3. 为 `setbootablestoragedrive`、`fixgpt`、`firmwarewrite`、`benchmark`、`peek`、`getsha256digest` 和受控原始 XML 建立统一命令结果。
4. 校验自定义命令根标签、最大 XML、属性和值；敏感属性进行日志遮蔽。
5. 提交：`✨ feat(qcom): add vendor command strategies`

## Task 10：实现 Xiaomi、ZTE、Nothing 和 OnePlus 流程

**文件：**

- 新建：`src/GeekFlashCore.Protocol.Qcom/Vendors/Xiaomi/XiaomiFirehoseStrategy.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Vendors/Xiaomi/XiaomiAuthentication.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Vendors/Zte/ZteFirehoseStrategy.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Vendors/Nothing/NothingFirehoseStrategy.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Vendors/OnePlus/OnePlusFirehoseStrategy.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Vendors/OnePlus/OnePlusTokenCodec.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Vendors/OnePlus/OnePlusProjectVerifier.cs`
- 测试（忽略）：`.tests/GeekFlashCore.Protocol.Qcom.Tests/Vendors/VendorAuthenticationTests.cs`

**步骤：**

1. 从参考实现提取逐字节黄金向量和 XML 交换序列，先写确定性测试。
2. Xiaomi 保留 Configure NAK 触发和签名请求顺序；签名材料来自 Provider，不记录内容。
3. ZTE 只在正确策略下注入 Configure OEM 属性。
4. Nothing 实现 `ntprojectverify` Token 生成/发送/ACK 验证。
5. OnePlus 实现 Token、项目配置判断和 Project Verify；保持不同代际 Loader 的分支顺序。
6. 密钥和临时认证缓冲使用后清零；固定比较使用 `CryptographicOperations.FixedTimeEquals`。
7. 分为可独立审查的提交：
   - `✨ feat(qcom): support xiaomi authentication`
   - `✨ feat(qcom): support nothing verification`
   - `✨ feat(qcom): support oneplus verification`

## Task 11：集成 OplusDigestUtils 和分区映射器

**文件：**

- 修改：`src/GeekFlashCore.Protocol.Qcom/GeekFlashCore.Protocol.Qcom.csproj`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Vendors/Oplus/OplusDigestParser.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Vendors/Oplus/OplusDigestEntry.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Vendors/Oplus/OplusDigestIndex.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Vendors/Oplus/OplusRangeMapper.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Vendors/Oplus/OplusFirehoseStrategy.cs`
- 测试（忽略）：`.tests/GeekFlashCore.Protocol.Qcom.Tests/Vendors/OplusDigestParserTests.cs`
- 测试（忽略）：`.tests/GeekFlashCore.Protocol.Qcom.Tests/Vendors/OplusRangeMapperTests.cs`

**步骤：**

1. 用脱敏 Digest fixture 写解析转换、重复 Label/FileName、权限、重叠、空条目和溢出测试。
2. 引用固定版本 OplusDigestUtils，通过内部适配器转换为不可变条目，第三方类型不进入公共层。
3. 构建按物理分区、Label、FileName 和范围索引；构造时拒绝非法范围和无法消歧的重叠。
4. Mapper 支持条目内部任意子范围以及跨连续条目拆分；未映射和权限失败必须在发命令前返回。
5. Digest 发送和 Verify 成功后才发布索引，失败时旧索引失效。
6. 提交：`✨ feat(qcom): map oplus digest partitions`

## Task 12：实现 OplusDigestPt

**文件：**

- 新建：`src/GeekFlashCore.Protocol.Qcom/Vendors/Oplus/OplusDigestPtPolicy.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Vendors/Oplus/OplusGptCompatibility.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom/Firehose/Storage/FirehoseStorageService.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom/Firehose/Programming/FirehoseProgramPlanner.cs`
- 测试（忽略）：`.tests/GeekFlashCore.Protocol.Qcom.Tests/Vendors/OplusDigestPtTests.cs`

**步骤：**

1. 写 BackupGPT 探测成功、PrimaryGPT sector 6、PrimaryGPT sector 34、普通映射和跨条目拆分测试。
2. Program/Read XML 使用 Digest 条目的 Label/FileName，但保留调用者映射后的实际 StartSector/NumPartitionSectors。
3. 将 GeekFlashTool 的 GPT 探测表示为兼容映射规则，不在读写循环中重复分支。
4. 确保普通任意分区和任意合法子范围均可读写，不局限 GPT 测试路径。
5. 提交：`✨ feat(qcom): support oplus digest pt`

## Task 13：实现 OplusDigestLegacy

**文件：**

- 新建：`src/GeekFlashCore.Protocol.Qcom/Vendors/Oplus/OplusDigestLegacyPolicy.cs`
- 新建：`src/GeekFlashCore.Protocol.Qcom/Vendors/Oplus/OplusDigestCommandCounter.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom/Firehose/Storage/FirehoseStorageService.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom/Firehose/Programming/FirehoseProgramExecutor.cs`
- 测试（忽略）：`.tests/GeekFlashCore.Protocol.Qcom.Tests/Vendors/OplusDigestLegacyTests.cs`

**步骤：**

1. 写命令阈值前后线路测试，精确断言 `NOP, NOP, Digest, NOP` 顺序和计数重置。
2. 写 `Verifying signature failed with` NAK 测试，断言恢复后只重放当前 XML 一次，第二次失败立即返回。
3. Program 和 Read 按固定扇区拆分；Raw、Sparse Raw、Fill 产生的每个实际命令使用同一成功计数器。
4. 计数使用 checked 或饱和逻辑，阈值小于安全最小值时配置验证失败。
5. NOP/Digest 的 ACK、NAK 和超时均不得被吞掉；恢复失败使会话进入明确状态。
6. 提交：`✨ feat(qcom): support oplus digest legacy`

## Task 14：完成 QcomProtocol 门面和通用框架集成

**文件：**

- 新建：`src/GeekFlashCore.Protocol.Qcom/QcomProtocol.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom/QcomDeviceIdentify.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom.Abstractions/Interfaces/IQcomProtocol.cs`
- 修改：`src/GeekFlashCore.Protocol.Qcom/Localization/Strings.resx`
- 修改：`src/GeekFlashCore.Protocol.Qcom/Localization/Strings.en.resx`
- 测试（忽略）：`.tests/GeekFlashCore.Protocol.Qcom.Tests/QcomProtocolWorkflowTests.cs`

**步骤：**

1. 写完整模拟流程：识别 Sahara、请求 Loader、分析、上传、Firehose Configure、厂商认证、存储操作、重启和释放。
2. 门面公开同步精确 API，并用最薄的 `Task`/`ValueTask` 编排实现通用 `IProtocol`；不使用 `Task.Run` 包裹底层 I/O。
3. 实现会话级互斥和状态检查；重复 Connect/Disconnect、失败重连、资源取消和中途 Raw 取消行为可预测。
4. 将 Firehose 存储 Lease 接入 GPT/文件系统上层所需的块设备抽象，并确保 `QcomProtocol` 直接实现 `IBlockDeviceProvider`。
5. 中英文资源补齐，不在异常中暴露敏感认证内容。
6. 提交：`✨ feat(qcom): complete protocol workflow`

## Task 15：兼容性、性能和安全审查

**文件：**

- 修改：实施中发现的 Qcom/Sparse/抽象文件
- 更新：`docs/plans/2026-09-04-qcom-protocol-design.md`
- 更新：`docs/plans/2026-09-04-qcom-protocol-implementation.md`
- 测试（忽略）：`.tests/GeekFlashCore.Protocol.Qcom.Tests/Regression/*.cs`

**步骤：**

1. 建立 GeekFlashTool 与 QnQcLIB 行为矩阵，逐项核对 Configure 顺序、命令 XML、厂商认证、GPT Digest 和 Legacy Digest。
2. 验证普通设备分区写入：Raw 请求必须支持完整范围、短源自动填充和分段；Android Sparse 请求必须支持 Raw、Fill、Don't Care、CRC 以及跨 Payload 分段。
3. 验证 OplusDigestPt 分区写入：Sparse 产生的每个实际 Raw/Fill 命令均经过 Digest 映射，保留 Label/FileName 并使用调用方实际扇区范围；跨连续 Digest 条目和 GPT sector 6/34 兼容规则必须覆盖读写测试。
4. 验证 OplusDigestLegacy 分区写入：Sparse Raw、Fill 和普通 Raw 的每个实际命令共用发送计数器，固定扇区拆分、阈值前 `NOP, NOP, Digest, NOP`、签名失败 `Digest, NOP` 后单次重放和恢复失败熔断均有线路测试；线路顺序与 XML 计数语义已和本地 QnQcLIB 源码核对。
5. 审查连接自动识别：连接时应同时支持 Sahara 和 Firehose；已处于 Firehose 且首包为启动日志时直接进入 Configure，不要求先执行 Sahara，也不得因 `_sahara` 为空抛出状态异常；覆盖 XML 声明、无声明 XML、分片首读和未知前缀边界。
6. 对比 `D:\Code\CPlusPlus\qdl` 的 VIP/连接逻辑并做兼容决策：VIP 是带签名表和链式表的逐命令传输状态机，不等同于 Oplus Digest；需决定是否新增通用 `IVipTransferPolicy`，以及是否在启动日志检测到 `VIP is enabled, receiving the signed table` 后才启用，缺少表时必须快速失败。
   - 2026-09-05 已新增通用 `IFirehoseDigestProvider`/`FirehoseDigestConfiguration`，普通设备可按连接选项发送 Digest；通用 Digest 与 Oplus Digest 互斥，避免两套线路同时生效。
   - 2026-09-05 已新增 `IFirehoseVipProvider`、VIP 签名表/链式表资源模型和统一命令前钩子；首张表按 54 个 Firehose XML 帧、链式表按 256 个帧轮换，启动日志未宣布 VIP 或资源缺失时在 Configure 前失败。
7. 对比 qdl 的连接容错：首次读取超时时对 QUD/Auto 后端发送 Sahara Hello 响应并重试，普通后端可将超时视为已在 Firehose；首包为 `<?xml` 时跳过 Sahara。需决定是否将当前严格的 8 字节前缀探测扩展为可回推的首包探测和后端能力标记。
8. 对关键类审查：整数溢出、负长度、数组/池化泄漏、无限重试、XML 注入、敏感日志、并发重入、Stream 所有权和取消后复用；删除同一逻辑的重复实现和不可达分支。
9. 审查日志等级和数量：协议帧、包头、原始长度和逐块进度只允许在 Debug/Verbose；连接、Configure、认证、Digest/VIP 刷新、读写完成等阶段状态使用 Information；可恢复的兼容回退、资源变更和一次性重试使用 Warning；会话失效、协议错误、数据损坏和最终失败使用 Error。相同事件只能由一个拥有该上下文的层记录，底层不得与门面重复记录同一异常。
10. 审查日志安全性和可观测性：禁止输出 Token、签名原文、完整 Digest、私密 Hash、认证 Challenge/响应和完整自定义 XML；设备返回的 Firehose/Sahara 文本作为结构化数据记录，必要时截断并标记来源，不把同一原始日志重复拼接到异常和日志。
11. 审查异常和日志资源化：`GeekFlashCore.Protocol.Qcom`、`GeekFlashCore.Protocol.Qcom.MessagePipe` 及 Task 15 涉及的存储/Sparse/Vendor 类中，所有用户可见异常消息和日志模板必须使用 `Localization/Strings.resx` 与 `Strings.en.resx` 的资源键；参数通过资源格式化方法传入。参数名、类型名、协议命令名和设备原始返回文本属于结构化数据，不单独伪造资源键。检查中发现的硬编码文本至少覆盖 `Oplus*Policy/Parser/Mapper`、`FirehoseVipTransferPolicy`、`FirehoseSession`、`FirehoseStorage*`、`SparseProgramPlanner`、`CustomCommandValidator`、`MessagePipe*Provider` 和 `QcomProtocolOptions`。
12. 使用模拟大镜像验证 Raw/Sparse/Oplus/VIP 稳态内存和吞吐；确认不按镜像大小展开数据、不产生大对象堆分配，carry buffer 有上限，热路径不重复解析或复制源数据。
13. 执行全部 `.tests`、Release 构建和必要的格式化检查；记录命令与结果。
14. 检查 `git status --short --ignored`，确保 `.tests`、fixture、日志、benchmark 输出均未跟踪。
15. 对全部提交进行最终 diff 审查并更新本文进度与未决风险。
16. 提交：`📝 docs: finalize qcom implementation`

## 进度

- [x] 完成参考项目首轮分析。
- [x] 完成并批准总体设计。
- [x] 固化设计文档。
- [x] Task 1：测试护栏与会话缺陷（2026-09-04；同时修复损坏长度触发的可变 stackalloc 栈溢出）。
- [x] Task 2：公共契约（2026-09-04；配置、读写请求、目标证据、异常与同步精确 API）。
- [x] Task 3：运行时资源和 MessagePipe 适配（2026-09-04；Core 保持无 MessagePipe 依赖）。
- [x] Task 4：Loader 分析与 Secure Boot（2026-09-04；固定 QcomImageUtils 版本并隔离第三方类型）。
- [x] Task 5：同步 Firehose 会话（2026-09-04；有界池化线路缓冲、强类型命令构建、Raw 状态与并发保护）。
- [x] Task 6：Configure 状态机（2026-09-04；保留厂商回退顺序，新增状态指纹、重试上限与结构化证据解析）。
- [x] Task 7：存储命令与块设备（2026-09-04；同步流式 I/O、集中范围校验、存储证据解析与读写权限适配）。
- [x] Task 8：Raw/Sparse 写入（2026-09-04；复用 Android.Sparse 合并区域，窗口化来源、空洞跳过、流式 Fill 与 Payload 分包）。
- [x] Task 9：厂商策略与自定义命令（2026-09-04；三层厂商证据、策略化 Configure、命令允许列表与敏感属性脱敏）。
- [x] Task 10：Xiaomi/ZTE/Nothing/OnePlus（2026-09-04；同步签名交换、Nothing 本地 Token、OnePlus 两代 Project Verify 原语与 ZTE Configure 策略）。
- [x] Task 11：Oplus Digest 解析与映射（2026-09-04；固定兼容包版本、内部类型适配、重叠/权限校验与连续范围拆分）。
- [x] Task 12：OplusDigestPt（2026-09-05；统一预先映射、跨条目读写及 GPT sector 6/34 分段；11 项线路测试通过）。
- [x] Task 13：OplusDigestLegacy（2026-09-05；固定扇区窗口、共享成功计数、NOP/Digest 刷新、签名 NAK 单次重放与恢复失败熔断；11 项线路测试通过）。
- [x] Task 14：QcomProtocol 工作流（2026-09-05；连接门面、资源超时与清理、Digest provider 接入、块设备租约失效、失败重连和厂商认证工作流；163 项测试通过）。
- [x] Task 15：最终审查（2026-09-05；本地代码、回归测试、Release 构建和差异检查完成，保留真实设备验证风险）。
- [x] Task CLI-01：GeekFlashCore.CLI 首批宿主（2026-09-05；.NET 10、自动 Qualcomm EDL 识别、串口/USB 热插拔、通用与 Qcom 命令、交互式 Provider、Serilog 和进度输出）。
  - 2026-09-05 已完成第一批异常和阶段日志资源化：Qcom 核心、Qcom.Abstractions、MessagePipe 的固定用户可见文本已迁移到中英文 `Strings` 资源；修复 SessionBlockDevice 捕获设备导致的生命周期警告。
  - 2026-09-05 同步 `ConfigureFirehose()` 已补齐配置 Provider、普通 Digest、VIP、Oplus Digest、Xiaomi 身份验证和 OnePlus/Nothing 校验顺序；新增无 XML 声明 Firehose 首包兼容和普通 Digest 通用 raw 长度上限。
- 2026-09-05 `FirehoseProgramExecutor` 改为两阶段映射校验，执行前仍拒绝未映射范围，但不再保留整份 Sparse Digest 映射数组；新增 4 MiB 以上普通 Digest、同步资源/VIP 和无声明首包线路测试。
- 2026-09-05 日志审查确认 Sahara/Firehose 包级收发日志均为 Debug，连接、配置、认证、转储和吞吐阶段为 Information/Warning/Error；Sahara 包级详细模板已统一资源化，默认生产日志级别不会输出包级细节。
- 2026-09-05 已根据本地 QnQcLIB 源码确认 Legacy 线路：阈值刷新为 `NOP, NOP, Digest, NOP`，签名 NAK 恢复为 `Digest, NOP` 后仅重放当前 XML 一次；读写计数在 XML 发送后立即增加，普通 NAK 也计数，恢复失败使会话进入 Faulted。
- 2026-09-05 已将 Sahara Sender/Receiver 的包级 Debug 模板统一迁移到中英文资源键；新增首次读取超时后的 qdl 兼容 HELLO_RESP 探测，可通过 `ProbeFirehoseOnSaharaTimeout` 关闭，并以回归测试覆盖。
- 2026-09-05 最终验证：Qcom 测试 163 项通过，`GeekFlashCore.slnx -c Release --no-restore` 构建通过且无警告/错误，`git diff --check` 通过；工作区无未提交非忽略文件。
- 2026-09-05 深审修复 `ProbeSahara()` 的超时探测副作用；补齐 Sahara 内存区域和 Firehose 响应属性日志资源键，回归测试增至 164 项并通过。
- 2026-09-05 CLI-05 修复真实设备联机问题：Xiaomi 认证改为由 `QcomProtocolOptions.AuthenticationKind` 显式启用，且按参考项目先发送 `sig` 命令再发送 256 字节签名；未指定认证时不发送 `sig req`，直接返回需要显式认证的 Configure 错误。
- 2026-09-05 CLI-05 修复 CLI 进度和目标信息输出：进度行使用 ANSI 清行并与 Serilog 通过共享控制台锁协调，日志输出到 stderr；Protocol/Vendor 分开显示，Sahara CA Hash 只显示长度，目标硬件字段结构化展示。
- 2026-09-05 CLI-06 修复 Sahara 失败后的恢复和 CLI 输出：文件 Provider 与 `qcom xml` 统一剥离用户输入的外围引号；ConsoleUi 将 Serilog 级别映射为 `INF/WRN/ERR/DBG/VRB/FTL` 并去除结构化消息属性的外围引号，所有运行期输出通过共享控制台锁清理进度行；Qcom 协议探测在启用 qdl 兼容探测时最多重试 4 次，未知/超时首包执行有限的 Flush + `ResetStateMachine`，Sahara 清理前主动复位状态机以支持资源失败后的再次联机。
- 2026-09-05 Xiaomi 内置认证：将 GeekFlashTool 的 5 组兼容签名迁移到 `XiaomiAuthentication`，默认在未指定认证方式时按顺序执行 `sig` XML + 256 字节 Raw，并仅在设备返回 `authenticated` 时成功；全部候选失败后回退到 `IVendorAuthenticationProvider`，显式 `XiaomiSignature` 继续直接使用 Provider。
- 2026-09-05 CLI-07 修复 Firehose 存储初始化：Configure 成功后 `getstorageinfo` 遇到 `Failed to open the SDCC Device` 时，有界切换到 UFS/4096 并重新 Configure；同步 `ConfigureFirehose()` 与异步 `ConnectAsync()` 均覆盖，新增模拟线路顺序回归测试。

## 未决风险

### CLI-01（2026-09-05）

- 行为结论：CLI 作为宿主层组合现有 `IProtocol`/`IQcomProtocol`、Transport 和 UsbWatcher；不复制核心协议线路。当前只注册已实现的 Qualcomm EDL，其他 `ProtocolType` 保留为不可用提示。
- 验证证据：已检查 `QcomProtocol` 构造函数、Provider 契约、`IProtocol` 通用方法、串口/USB 工厂和 USB 监视器接口。
- 风险：USB 热插拔为 Windows WMI 实现；真实设备上的交互式 Provider 和终端宽度尚需现场验证。

### CLI-02（2026-09-05）

- 行为结论：已新增 .NET 10 `GeekFlashCore.CLI`，提供 `devices`、通用 IProtocol 操作、`qcom probe-sahara/configure/xml` 和无命令交互会话。显式文件选项优先，缺失资源由控制台 Provider 提问；未对核心协议做改动。
- 验证证据：CLI Release 构建通过；`--help`、未知协议退出路径和 `devices` 已运行；本机 WMI 枚举发现 `05C6:9008` Qualcomm EDL（COM73）。
- 风险：尚未对真实设备执行连接、认证、读写或重启；CLI 的 Provider 输入与终端进度显示仍需脱敏设备验证。

### CLI-04（2026-09-05）

- 行为结论：修复串口 Transport 工厂和内部构造函数将 `StopBits.None` 作为默认值的问题；`System.IO.Ports.SerialPort` 默认改为合法的 `StopBits.One`（8N1）。
- 验证证据：新增本地串口默认构造回归测试，Qcom 测试 165 项通过；完整 Release 构建和 CLI `--help` 冒烟通过。
- 风险：真实设备串口参数仍可能需要调用方显式指定；当前公共工厂尚未暴露串口参数配置入口。

### CLI-03（2026-09-05）

- 行为结论：移除 CLI 主流程对 QualcommProtocol 的固定依赖。协议注册项现在统一提供设备识别、传输后的协议工厂、协议专用命令和信息展示；主流程仅依赖 `IProtocol`。
- 验证证据：CLI 主流程和 TransportResolver 不再引用 Qcom 类型；Qcom 逻辑集中到 `QcomProtocolHostAdapter`，Release 构建通过。
- 风险：当前仓库仍只有 Qualcomm 实现，新增协议需要提供对应注册项和适配器；真实设备验证风险不变。

### CLI-05（2026-09-05）

- 行为结论：Vendor 识别只决定 Firehose 策略，不再隐式开启 Xiaomi 认证；`--auth xiaomi` 映射为 `AuthenticationKind.XiaomiSignature`，认证 Provider 提供签名材料后执行参考项目的直接 `sig`/Raw 顺序。CLI 进度更新清理整行并和日志输出串行化，目标信息不再把 Vendor 当作 Protocol，也不记录完整 CA Hash。
- 验证证据：Qcom 回归测试 166 项通过；CLI Release 构建通过；新增 Xiaomi 未显式认证时不发送 `sig` 请求、显式认证发送 `sig` 而不发送 `req` 的本地线路测试。
- 风险：尚未在本地再次执行真实刷写；设备实际接受的 Xiaomi 签名材料仍需使用 `dotnet run -- --auth xiaomi` 现场验证。终端需支持 ANSI 控制序列，否则进度清行效果取决于宿主终端实现。

- OplusDigestLegacy 的阈值刷新与签名 NAK 恢复顺序、以及 XML 发送后计数（包括普通 NAK）已由本地 QnQcLIB 源码确认；真实设备仍可能存在 Loader 变体，需用硬件或脱敏抓包复核兼容性。
- `FirehoseProgramExecutor` 已采用两阶段映射校验和执行时映射；Sparse segment 计划本身仍按输入结构建立列表，超大镜像需要在目标环境继续观察计划列表和第三方 Sparse 解析器的稳态分配。
- 当前 Qcom 核心仍有两类审查债务：Sahara/Firehose 包级收发日志数量较多，需要确认默认日志级别不会造成生产日志噪声；阶段日志与底层 NAK 日志存在重复风险，需要按事件归属层去重。
- Sahara 收发包模板已资源化；仍需在宿主集成环境确认包级日志数量和阶段日志归属不会造成重复噪声。
- CLI-06 的路径规范化、日志格式和探测恢复已用本地模拟传输验证；真实串口设备在 Loader 资源失败后是否始终接受 `ResetStateMachine`，以及不同 USB 后端的 Flush 语义，仍需硬件复核。

- 不同 Loader 对同名厂商命令的响应文本和大小写可能不同，解析应基于捕获样本保持宽容，但状态机必须有界。
- Sahara 获取 TargetInfo 后设备可等待时间没有统一保证；资源提供器应缓存候选 Loader，超时策略需由宿主配置。
- NuGet 包 API 在更新时可能变化；实现使用固定版本并通过内部适配器隔离。
- 某些厂商认证依赖外部服务或用户凭据；Core 只定义资源契约和线路行为，不保存账号凭据。
