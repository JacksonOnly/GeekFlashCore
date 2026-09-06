# GeekFlashCore Qualcomm 协议设计

日期：2026-09-04

状态：已批准，实施中

适用范围：`GeekFlashCore.Protocol.Qcom`、`GeekFlashCore.Protocol.Qcom.Abstractions` 及必要的通用基础抽象

## 1. 目标

在不改变 GeekFlashTool 已有设备行为和兼容逻辑的前提下，完成并重构 Qualcomm EDL 协议实现，使其能够作为通用刷机开发框架的稳定底层组件。

实现参考来源：

- `D:\Code\CSharp\GeekFlashTool` 中的 `GeekFlashTool.EdlClient`、其依赖项目及主项目集成逻辑；
- `D:\Code\CSharp\QnQcLIB` 中的 Qualcomm、Sparse 和 Oplus Digest 优化逻辑；
- `OplusDigestUtils` 提供的 Digest 解析能力；
- `QcomImageUtils` 提供的 Sahara 引导静态分析能力。

核心约束：

- 保持既有协议行为、厂商兼容顺序和回退语义；可以修复明确缺陷并优化结构、性能和安全性；
- Sahara、Firehose、Sparse 传输等底层数据路径使用同步 I/O；
- 上层兼容异步协议接口，但异步只用于资源获取、编排和取消边界；
- 尽量使用 `Span<T>`、`ReadOnlySpan<T>`、`stackalloc`、池化缓冲和流式处理，避免大对象和热路径分配；
- 新增测试只能位于被 Git 忽略的 `.tests` 中，不提交测试项目和测试文件；
- 功能按独立能力拆分为简短英文 Git 提交，不合并成一个巨型提交；
- 本文档是跨会话的设计与进度来源，需求变化时同步更新。

## 2. 总体架构

```text
宿主（GeekFlashTool / CLI / 服务）
  ├─ Loader、Digest、配置和认证材料
  ├─ 可选 MessagePipe Handler
  └─ 资源提供器或 MessagePipe 适配器
                    │
                    ▼
QcomProtocol（公共门面、异步编排与 IBlockDeviceProvider）
  ├─ 连接生命周期与取消边界
  ├─ Loader 静态分析和运行时证据合并
  ├─ 厂商策略选择
  └─ 对通用 IProtocol 的兼容
                    │
          ┌─────────┴─────────┐
          ▼                   ▼
SaharaSession（同步）   FirehoseSession（同步）
                              ├─ ConfigureNegotiator
                              ├─ FirehoseStorageService
                              ├─ SparseProgramPlanner
                              ├─ VendorStrategyResolver
                              └─ DigestPolicy
```

公共门面不把第三方 NuGet 类型暴露给调用方。外部模型由本项目定义，第三方结果在内部转换为稳定的领域模型。

## 3. 同步与异步边界

`SaharaSession` 和 `FirehoseSession` 的协议收发方法保持同步：

- 传输接口已经提供基于 `Span<T>` 的同步读写；
- 原始数据发送、XML ACK/NAK 处理、Sparse 展开和分段读写均不得引入 `Task`；
- 不允许在同步协议层使用 `.GetAwaiter().GetResult()` 等方式等待异步资源；
- 取消只在命令或数据块边界检查；若取消发生在无法安全恢复的原始数据阶段，会话标记为需要重新连接。

`QcomProtocol` 必须直接实现 `IBlockDeviceProvider`。连接并完成 Firehose 存储初始化后，
`GetBlockDevices` 和 `OpenBlockDevice` 提供可复用的块设备视图；打开的设备通过会话代数校验，
断开、重连或原始传输取消后旧设备必须失效。

`QcomProtocol` 可实现异步公共接口，但只负责：

- 获取 Loader、Digest、认证材料和运行时配置；
- 编排同步 Sahara/Firehose 步骤；
- 将同步结果转换为公共异步接口结果；
- 管理会话串行化、取消和生命周期。

参考 qdl 的 VIP 逻辑需要单独建模。VIP 不是 Oplus Digest 的别名：它传输
`DigestsToSign.mbn` 以及可选的 `ChainedTableOfDigests*.bin`，按已发送 Firehose
帧数推进表状态，并根据启动日志中的 `VIP is enabled, receiving the signed table`
判断设备是否要求 VIP。只有调用方提供对应 VIP 资源且设备宣布启用时才发送，不能把
普通 Digest 或 Oplus 分区索引直接当作 VIP 表。

## 4. 运行时资源请求

Core 必须能够在运行时向宿主请求非 Program/Read 命令参数的资源，例如 Sahara Loader、Oplus Digest、厂商签名材料和配置快照。

协议核心依赖强类型领域接口，不直接依赖 MessagePipe：

- `ISaharaImageProvider`；
- `IOplusDigestProvider`；
- `IVendorAuthenticationProvider`；
- `IFirehoseConfigurationProvider`。

消息与资源模型位于 `GeekFlashCore.Protocol.Qcom.Abstractions`，保持纯 POCO/record。MessagePipe 只作为可选适配方式存在于宿主或单独的集成层。通用的 `GeekFlashCore.Protocol.Abstractions` 不承担 MessagePipe 依赖。

使用 MessagePipe 时采用 `IAsyncRequestHandler<TRequest, TResponse>` 请求—响应语义，不使用无确定响应方的普通发布订阅。当前 `SaharaImageEntryRequest/Response` 保留并增强。

资源获取规则：

- Program、Read、Erase 等每次操作的参数由正常 API 直接传入，不通过消息总线；
- 一次连接或操作开始前获取不可变配置快照，传输过程中不反复查询；
- 只有依赖设备 Challenge 的动态签名允许在握手中途请求；
- Loader 请求必须有取消和超时，宿主应优先使用预索引或缓存，避免设备等待期间无限交互；
- 大型资源通过 `IDataSource` 流式提供，不返回完整 `byte[]`；
- 敏感认证数据具有明确所有权和生命周期，使用后清零且不得写入日志；
- Core 验证资源条目 ID、长度、可重开能力和必要元数据。

建议的 Sahara 连接流程：

1. 同步探测设备并取得 `SaharaTargetInfo`；
2. 上层异步请求候选 `SaharaImageEntry`；
3. Core 使用 QcomImageUtils 统一分析 Loader；
4. 同步上传 Loader；
5. 合并静态分析与运行时证据；
6. 同步进入 Firehose 会话。

同时保留显式底层 API，允许不使用资源提供器的调用方自行探测并上传 Loader。

## 5. Sahara 与 Loader 分析

QcomImageUtils 用于提取：

- OEM 与 SoC 类型；
- 支持命令的静态提示；
- `MaxPayloadSizeToTargetInBytesSupported`；
- Root CA Hash、硬件和软件标识；
- Loader 是否为有效 programmer。

这些信息是初始提示而不是运行时保证。优先级为：

1. 调用方显式配置；
2. 设备运行时响应和认证结果；
3. Loader 静态分析结果；
4. 安全默认值。

`SecureBootState` 使用三态模型：

- `Enabled`：设备 PKHASH 与已接受 Loader 的 CA Hash 匹配；
- `Disabled`：两者明确不匹配，但设备仍成功接受并运行该 Loader；
- `Unknown`：任一证据缺失或 Loader 是否真正运行无法确认。

不能只根据 Hash 不匹配推断 Secure Boot 已关闭。

## 6. Firehose Configure

Configure 使用有界状态机实现，保留 GeekFlashTool 的顺序和兼容行为：

- 显式 MemoryName 优先；自动模式从兼容默认值开始；
- 发送 MemoryName、Verbose、AlwaysValidate、MaxDigestTable、MaxPayload、ZLPAwareHost、SkipStorageInit；
- ZTE 等厂商附加 OEM 属性；
- 根据 NAK 内容执行厂商认证、存储类型回退、Payload/Digest 大小修正和扇区大小修正；
- Configure 成功后解析 LUN 0 存储信息，并按其 `num_physical` 在同一初始化阶段各读取其余 LUN 一次，再执行 Nothing、OnePlus 等后续认证和缓冲等级设置。分区表和范围校验只使用这份会话缓存；只有显式 `GetStorageInfo` 才主动刷新。

每次重试必须使配置状态发生明确变化；状态重复或超过上限立即返回结构化错误，避免无限重试。最终 Payload 为调用方上限、设备运行时能力、Loader 静态提示和安全上限的最小值。

## 7. 厂商策略

厂商逻辑通过 `IVendorFirehoseStrategy` 和解析器隔离，不散布在 Configure、Program、Read 方法中。首批范围包含 GeekFlashTool 已有完整流程：

- Xiaomi 签名认证；
- Oppo/Oplus 签名和 Digest；
- OnePlus Token 与 Project Verify；
- Nothing `ntprojectverify`；
- ZTE Configure OEM 属性；
- 参考项目中其他已存在且有明确设备行为的自定义命令。

策略选择同时使用 Loader 静态信息和 Firehose 运行时证据。运行时证据可纠正静态 OEM/SoC 推断。原始自定义 XML 只通过受控接口提供，默认进行长度、属性和状态检查。

## 8. Oplus Digest

公开模式名称固定为：

- `OplusDigestPt`：GeekFlashTool 原有的基于分区条目的 Digest 行为；
- `OplusDigestLegacy`：QnQcLIB 的固定扇区分段和周期性 NOP/Digest 行为。

### 8.1 OplusDigestPt

Digest 发送并通过设备 Verify 后，使用 OplusDigestUtils 解析分区表，构建不可变索引。每个条目至少包含：

- Label；
- FileName；
- StartSector；
- Sectors；
- AllowRead/AllowWrite；
- 可选 Hash。

Program/Read 时将调用方请求映射到条目：

- Label 为主要键，FileName 用于消除歧义；
- 允许条目内部的任意起始地址和长度；
- 发往设备的 XML 保留 Digest 条目要求的 Label/FileName，同时使用调用方实际的 StartSector 和 NumPartitionSectors；
- 请求跨越多个连续条目时拆分执行；
- 未映射、越界、重叠歧义或权限不允许时在发送前失败。

兼容保留 GeekFlashTool 的 BackupGPT/PrimaryGPT 探测和特殊 GPT 行为，但使用统一映射器表达，避免把 GPT 条件散布在读写循环中。

### 8.2 OplusDigestLegacy

在分区映射基础上加入：

- Program/Read 按固定扇区数分段；
- 共享统计成功的 Program/Read 命令次数；
- 达到阈值前执行两次 NOP、发送 Digest、再执行一次 NOP，然后重置计数；
- 遇到包含签名验证失败语义的 NAK 时执行同一恢复序列；
- 恢复后只重放当前 XML 命令一次，禁止无限重放；
- Raw、Sparse Raw、Fill 和其他实际产生写命令的路径共用同一计数器。

Digest 原始数据、ACK 和 NOP 的顺序必须与参考实现一致，但会修复无界循环、计数溢出、错误吞噬和资源泄漏。

## 9. Program、Read 与 Sparse

统一生成内部写入计划，再由同步执行器发送：

- 普通镜像映射为连续 Raw 区域；
- Sparse Raw 合并安全的连续区域；
- Fill 使用固定小缓冲重复生成，不展开为完整分区；
- Don't Care 根据调用方策略跳过或显式处理；
- CRC 和 Chunk 边界在计划阶段验证；
- 所有扇区、字节偏移和长度运算使用 checked 算术；
- 设备 NAK 尽早终止数据发送并保留完整错误上下文。

普通设备、OplusDigestPt 和 OplusDigestLegacy 必须复用同一套 Raw/Sparse 计划与执行器；
Digest 策略只负责范围映射、固定窗口和刷新计数，不得绕过 Sparse 的 Raw、Fill、Don't Care
或 CRC 校验路径。

普通设备的 Digest 由独立的 `FirehoseDigestConfiguration` 和
`IFirehoseDigestProvider` 提供，可在连接阶段按选项发送；该模式与 Oplus Digest 模式互斥。
VIP 已作为独立的签名表传输策略建模：首张签名表最多覆盖 54 个 Firehose XML 帧，
链式表每张最多覆盖 256 个帧；表在 XML 命令前发送并等待 ACK，不能将 VIP 表当作普通
Digest 或 Oplus 分区索引。VIP 资源缺失、表耗尽或设备未宣布支持时必须快速失败。

现有 `GeekFlashCore.Android.Sparse` 的健壮边界检查和块设备模型优先保留；只移植 QnQcLIB 中能证明改善设备行为的语义，不复制其高分配和不安全实现。

qdl 还允许连接层在首读超时或首包为 Firehose XML 时跳过 Sahara，并对 QUD/Auto 后端
发送一次 Hello 响应后重试。当前实现已支持带 XML 声明和无声明的 `<data>` 首包；后端能力
标记和超时后的 Sahara Hello 回推仍需真实 QUD/Auto 线路样本。同步 `ConfigureFirehose()`
与异步连接共享配置资源、普通 Digest、VIP、厂商认证和存储初始化顺序；同步资源 Provider
通过有界超时等待完成。

## 10. 公共模型与错误

公共 API 使用本项目稳定类型，例如：

- `QcomProtocolOptions`；
- `QcomTargetInfo`；
- `FirehoseProgramRequest`；
- `FirehoseReadRequest`；
- `FirehoseCommandResult`；
- `QcomVendorKind`；
- `SecureBootState`；
- `OplusDigestMode`。

异常或结果至少区分：

- 传输超时、断开和短读写；
- Sahara 协议错误；
- Loader 资源缺失或无效；
- Firehose NAK；
- Configure 协商失败；
- Digest 解析、映射和权限失败；
- 厂商认证失败；
- Sparse 格式或范围错误；
- 会话因中途取消而失效。

日志不得包含 Token、签名原文、完整 Digest、私密 Hash 材料或认证 Challenge 响应。

日志分层约定：协议帧、包头、原始长度和逐块进度属于 Debug/Verbose；连接、Configure、认证、Digest/VIP 刷新和读写完成属于 Information；兼容回退、资源变更和一次性重试属于 Warning；会话失效、协议错误、数据损坏和最终失败属于 Error。拥有完整上下文的上层负责记录阶段失败，底层只记录其独有的线路细节，避免同一事件重复输出。

用户可见的异常消息和日志模板必须来自模块 Localization 资源（`Strings.resx`/`Strings.en.resx`），参数通过资源格式化方法传入。协议命令名、类型名、参数名以及设备返回的原始文本是结构化数据，不要求为每个动态值创建资源键；原始设备文本必须限制长度并按敏感字段规则脱敏。

## 11. 性能与可靠性

- 固定尺寸协议头优先 `stackalloc`；
- 大缓冲使用 `ArrayPool<byte>` 并在 `finally` 中归还；
- 敏感池化缓冲归还前清零；
- XML 构建和解析避免反射热路径与重复字符串转换；
- 大文件和 Sparse 数据始终流式读取；
- 会话对象单线程使用，通过显式锁或状态机拒绝并发命令；
- Dispose 幂等，并确保当前 Stream、租用缓冲和传输资源正确释放；
- 所有协议循环具有长度、次数或状态上限。

## 12. 验证策略

实施遵循测试先行。临时测试工程和数据放在仓库根目录 `.tests`，并加入 `.gitignore`，不得提交。

验证包括：

- 基于内存传输的 Sahara/Firehose 数据包测试；
- Configure 状态机和 NAK 回退路径；
- OplusDigestPt 的条目映射、跨条目拆分、权限和越界；
- OplusDigestLegacy 的阈值、NOP/Digest 顺序、计数和单次重放；
- Raw/Sparse/Fill 的写入计划与边界；
- Loader 分析证据和 SecureBootState 推断；
- MessagePipe 适配器取消、超时和资源生命周期；
- Release 构建、格式检查和手工安全审查。

测试中发现的缺陷必须在对应能力提交前修复。最终完成前执行独立代码审查，重点检查整数溢出、无限循环、缓冲生命周期、状态竞争、敏感日志和错误恢复。

## 13. 分阶段交付

1. 修复现有 Sahara/Firehose 基础缺陷，确定公共领域模型和同步会话边界；
2. 实现运行时资源接口及可选 MessagePipe 适配方式；
3. 集成 QcomImageUtils 和 Loader/Secure Boot 证据模型；
4. 完成 Configure 状态机和基础 Firehose 存储操作；
5. 完成高性能 Raw/Sparse/Read 路径；
6. 实现通用厂商策略和认证流程；
7. 集成 OplusDigestUtils 与 `OplusDigestPt`；
8. 实现 `OplusDigestLegacy`；
9. 完成兼容性审查、性能检查、完整构建和提交整理。

## 14. 进度记录

- 2026-09-04：完成 GeekFlashCore、GeekFlashTool、EdlClient 和 QnQcLIB 的首轮结构与行为分析；总体设计经用户批准。
- 2026-09-04：新增运行时资源请求要求；决定使用强类型资源接口隔离协议核心，并将 MessagePipe 限定为可选宿主适配层。
