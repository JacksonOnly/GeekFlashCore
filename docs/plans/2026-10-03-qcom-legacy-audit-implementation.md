# Qualcomm Legacy 审查实施进度

日期：2026-10-03

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task-by-task. 本次直接在已隔离 worktree 内执行，最终独立审查。

**Goal:** 保留 Pt 映射，复刻 Rector Legacy 并修复联机读写/CLI 问题。

**Architecture:** 同步 Firehose 共用收发器；Legacy 策略隔离包计数和可选回复。Core 管理会话，CLI 管理输入、文件与发现。

**Tech Stack:** C#、net8.0 协议库、net10.0 CLI、同步 ITransport、xUnit 本地模拟。

**Spec:** `docs/plans/2026-10-03-qcom-legacy-audit-design.md`（已确认）。

## Global Constraints

- Pt 保留；Legacy 按指定参考，包括完整日志错误后继续确认、NOP 截断和 Flush。
- `.tests` 不提交；敏感日志资源化；仅释放自行打开的 Stream；不执行真机破坏性操作。

## Review Focus

- XML ACK 与 Raw 同次抵达：保留剩余字节，不能伪造 ACK。
- 1000 ms 可选回复结束恰有半帧：失败，不能丢弃或延长窗口。
- 输出路径已有文件、协议失败：原文件保留。
- 取消资源输入后再次输入：旧任务不能抢读。
- USB 打开中途失败：context 留到 Dispose，端点和接口清理。

## Task 1：QA-A 协议护栏

Files：`Internals/FirehoseWireReader.cs`、`Firehose/FirehosePayloadLimits.cs`、`Firehose/Storage/FirehoseStorageService.cs`、`QcomProtocol.cs`、Abstractions options/resx。
Interfaces：共用 `FirehosePayloadLimits.GetTransferBufferSize(FirehoseConfigureResponse) -> int`。

- [x] RED：已有 ReadCommandRequiresExplicitXmlAckBeforeRawTransfer、ProgramPacketsRespectNegotiatedPayloadInsteadOfSupportedMaximum、VipAndOplusDigestAreRejectedBeforeConnecting 失败。
- [ ] GREEN：非 XML 失败；实际 negotiated/supported 最小 Payload；VIP/Oplus 校验在连接前拒绝。
- [ ] 验证：运行上述 Filter 测试，Release 构建、diff check。
- [ ] 提交：`fix(qcom): enforce firehose response and payload limits`。

## Task 2：QA-B USB

Files：`Transport.LibUsb/Internals/LibUsbTransport.cs`、必要的独立 I/O 预算实现、`.tests` USB 测试。
Interfaces：保留 ITransport；设备初始化与 I/O 就绪检查分开。

- [ ] RED：受控 USB 设备首次打开/再打开/失败清理和短读预算测试。
- [ ] GREEN：保留 context，正确发现接口和端点，精确读取总预算。
- [ ] 验证：目标测试、Release 构建、diff check。
- [ ] 提交：`fix(usb): repair transport lifecycle and read deadlines`。

## Task 3：QA-C Legacy

Files：OplusDigestLegacyPolicy/CommandCounter、Oplus configuration、Firehose Session/Executor/Reader/Sender、Storage/ProgramExecutor、Qcom 初始化、resx。
Interfaces：内部 Legacy 发送选项和完整 payload 回调；可选回复返回完整日志/ACK/NAK，无独立 ACK 用同一接收队列确认。

- [ ] RED：51/52/53/54 -> 3/2/1/0 NOP、Digest55、确认计1；日志错误继续；迟到纯 ACK 拒绝；半帧有界失败；签名只重放一次；Raw/FILL 每传输计1、读不自动换表。
- [ ] GREEN：按 spec 3 的全部 11 条行为；Legacy 不强制解析 Pt 表，分段 0 表示不分段。
- [ ] 验证：Legacy 全部模拟测试、Pt 授权回归、Release 构建、diff check。
- [ ] 提交：`feat(qcom): replicate rector legacy digest flow`。

## Task 4：QA-D 连接/读写

Files：Firehose receiver/executor、Qcom session/storage、Sahara 相关收发路径。
Interfaces：现有同步/异步门面共用相同 state/策略。

- [ ] RED：取消最后 Raw 块不继续等待 ACK、提前 NAK 停写、短读/超时旧租约失效、重连清缓存、部分 XML/启动/多 LUN 等待有界。
- [ ] GREEN：仅修复已复现的本项目缺陷，并记录累计等待与同步 I/O 取消限度。
- [ ] 验证：目标测试和全套本地测试、Release 构建、diff check。
- [ ] 提交：`fix(qcom): bound transfer cancellation and recovery`。

## Task 5：QA-E CLI

Files：CliOptions/CommandLine/QcomProtocolHostAdapter、ConsoleUi/ConsoleProviders/CliApplication、TransportResolver、StorageCommands、CLI resx。
Interfaces：可取消单输入通道；新增 connect/resource/device-wait/Legacy 配置和 non-interactive 参数。

- [ ] RED：CliLegacyModeCreatesUsableConfiguration、非法参数、取消输入、非交互缺资源、设备匹配和输出文件失败保留。
- [ ] GREEN：配置 defaults=53/0/0；USB timeout 传递；设备等待总预算；输出同目录临时文件成功后替换。
- [ ] 验证：所有本地 CLI 测试、help/非法参数冒烟、Release 构建、资源键检查。
- [ ] 提交：`feat(cli): add bounded legacy and resource workflows`。

## Task 6：QA-F 交付

- [ ] 全套本地测试、Release 构建、diff check、resx 键一致、无敏感日志/无被跟踪测试。
- [ ] 重复分段传输分配趋势检查，记录只能由真机验证的项目。
- [ ] 最终独立审查；重要发现先 RED 再 GREEN，文档同步和提交。

## 进度

- [x] QA-00：读取仓库规范、既有设计/进度、当前工作区、CLI/Core 和指定 Rector 源码。
- [x] 明确模式：用户选择保留 Pt 分区映射，指定参考行为完善到 Legacy。
- [x] NuGet 还原与 Release 基线构建：成功，0 警告/0 错误；初次 no-restore 构建因缺少 assets 失败，已通过还原排除环境原因。
- [x] 缺陷复现：`.tests/GeekFlashCore.Protocol.Qcom.Tests/AuditRegressionTests.cs` 共 5 项，正常 XML ACK 路径 1 项通过；非 XML 伪 ACK、Payload 上限、VIP/Oplus 冲突、CLI Legacy 零配置 4 项失败，建立修复前证据。
- [x] 静态审查：USB 初始化/生命周期、精确读取预算、资源输入阻塞、设备等待与匹配、USB CLI timeout 传递已记录；尚无硬件验证。
- [x] 设计草案与测试矩阵已保存，`git diff --check` 通过；生产代码未修改。
- [x] 用户确认设计（2026-10-03）。
- [x] QA-A：ACK、Payload、模式互斥（2026-10-03；3 项缺陷 RED→GREEN，正常 ACK 1 项通过；Release 构建 0 警告/错误，diff check 通过）。CLI Legacy 测试仍保留已知失败，等待 QA-E。
- [x] QA-B：USB 生命周期和预算（2026-10-03；首次端点发现、Open/Close/Open、失败释放并重试、慢分片总预算 4 项 RED→GREEN；使用可控 IUsbContext/IUsbDevice 和端点，未连接硬件）。
- [x] QA-C：Rector Legacy 线路（2026-10-03；首批 12 项测试 9 RED/3 既有失败路径通过，迁移后 15 项全部通过，累计 24 项本地测试通过）。已覆盖 51/52/53/54 边界、日志错误与 handler 关联、Digest NAK/半帧、签名单次重放、读取不自动刷新、无 Pt 索引、Sparse RAW/FILL/skip 和缺省 rawmode。Legacy 资源安装前的 Configure/探测计数由 InitialPacketCount 表达，安装后所有 XML/完整输出载荷共享计数；未主动发送初始签名表，按 Rector 只在边界或签名 NAK 发送。
- [x] QA-D（2026-10-03）：最终 RAW NAK 和末块读取取消 2 项 RED→GREEN；普通 XML 等待传入取消 token。复现 Configure 同步/异步在 storage-open-failed + payload 调整同时出现时选错存储，统一 UFS 回退，1 项 RED→GREEN。提前 NAK 仅写第一块且不可继续、资源超时后迟到结果释放回归通过。累计 29 项本地测试通过，Release 0 警告/错误、diff check 通过。
- 审查裁定：Sahara 按设备请求上传，分包/Configure/LUN 查询已有有界单次等待；不添加可能中断正常大镜像的统一读写总时限。同步阻塞 I/O 的取消最多等当前单次超时，持续有有效进度的操作由调用方 token 控制；CLI 明示各超时用途。Configure 是有限次数回退，多 LUN 有最大数量，累计连接耗时可能超过 ConnectTimeout（该字段用于探测）。没有硬件证据，不宣称真机验证或吞吐提升。
- [x] QA-D：连接/实际读写路径修复与回归。
- [x] QA-D 补充（2026-10-03）：模拟真实编排 ConnectAsync → 分片 RAW 取消 → Cleanup → ConnectAsync 复现重连读到旧 pending RAW 的失败，清理 QcomSessionTransport 前缀/溢出缓存后 GREEN。同一测试验证 TargetInfo 清空、旧块视图代数失效、新视图读回 512 字节；Pt 跨分区映射/未覆盖范围回归通过。使用模拟传输，非硬件测试。
- [ ] QA-E：CLI 完善。
- [ ] QA-F：完整验证、独立提交和风险收尾。

## 未决风险与恢复位置

- 工作树不含旧的本地测试工程；历史 246/51 测试结果不能算作本次完整回归结果，需在现有 `.tests` 逐项恢复必要覆盖。
- Legacy NOP 文本优先、默认声明和长度截断语义已在设计明确；QA-C 须以逐字节 fixture 验证，防止全局声明注入覆盖原文。原配置成员保留，计数语义变化需迁移说明。
- 尚未真实联机、读写或测量 USB 吞吐/取消延迟；静态发现与模拟证据分开记录。
- 本次尚无提交。恢复时先检查本设计确认状态、`git status --short`，运行上述 5 项测试确认 4 个已知失败，再进入 QA-A。
