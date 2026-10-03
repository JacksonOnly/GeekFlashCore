# Qualcomm Legacy 兼容、联机读写审查和 CLI 设计

日期：2026-10-03

状态：用户已于 2026-10-03 确认；实施中。

## 1. 范围与已确认需求

- 用户要求审查、重构和优化高通项目，复刻指定参考中的 Digest 行为，检查联机、实际读写、超时及长等待，并完善 CLI。
- 用户已明确选择：保留 `OplusDigestPt` 分区映射，将 `D:\Code\CSharp\QnQcLIB\QnQcLIB_Rector` 的 Digest/NOP 行为完善到 `OplusDigestLegacy`。
- 指定参考实际为 C++ 项目。以本次读取的 `src/protocol/firehose.cpp`、`firehose_program.cpp`、`firehose_read.cpp`、`src/session.cpp`、`src/exports.cpp` 和 `tests/protocol_tests.cpp` 为线路依据。README 描述与代码不一致时，以代码和对应测试为准，记录差异。
- 参考中的可疑协议兼容行为按用户要求保留；当前项目自己的错误、资源泄漏和等待问题单独修复。每项标记为“复刻兼容”或“本项目修复”，避免把参考行为当作缺陷改掉。
- 不扩大到无关文件系统模块；协议收发继续同步。CLI 保持当前 net10.0，协议库保持 net8.0。
- 现有 2026-09-04 总体设计中 Legacy 的“XML 计数、两个固定 NOP、共享读写刷新”描述由本次明确的参考线路覆盖；Pt 分区索引和授权规则保留。

## 2. 审查证据

开始工作时 `git status --short` 无输出，HEAD 为 `37ee307`。此工作树没有历史 `.tests`；旧文档的 246/51 项测试记录属于历史证据，不能作为本次已运行结果。

已建立被忽略的 `.tests/GeekFlashCore.Protocol.Qcom.Tests`，首轮 5 项测试中 1 项正常路径通过，4 项缺陷复现失败：

| 编号 | 结论 | 证据与影响 |
| --- | --- | --- |
| QA-01 | 没有 XML ACK 的非 XML 数据被当作 Raw ACK | `FirehoseWireReader.ReadResponse` 为非 XML 前缀构造 Ack/RawMode=true；模拟 read 输入 6 个任意字节，命令被接受而非使会话失败。可能将 Sahara/残留数据当作刷机应答。 |
| QA-02 | 写包超过实际协商 Payload | `FirehoseStorageService.GetTransferBufferSize` 优先选 Supported；协商 512、支持 4096 时，1024 字节被一次写出。模拟测试期望两包各 512，实际一包 1024。Oplus 初始 Digest 也使用同样选择逻辑。 |
| QA-03 | VIP 与 Oplus 缺少互斥 | `QcomProtocolOptions.Validate` 仅检查普通 Digest 与两者冲突；VIP+Pt 在模拟测试中通过校验，会混用签名表与 Oplus 会话。 |
| QA-04 | CLI Legacy 模式不可用 | CLI 只填 Mode，FixedSectorCount/MaxCommandsBeforeDigest 为 0；实际调用 CLI Parse/CreateOptions 后 Validate 抛 FixedSectorCount 异常。 |
| QA-05 | USB Open 初始化顺序错误 | 源码证据：Open 首先调用 CloseCore，后者释放 context；GetEndpointId 在 `_opened` 和读写端点尚未就绪时调用 EnsureOpen，并将 out 参数设 null，只修改字段；Open 最后又检查这些 null 输出。尚未做真实 USB 验证。 |
| QA-06 | LibUsb 精确读取重置每段超时 | `ReadExact` 每次短读重复完整 timeout，与串口和 QcomSessionTransport 的总预算不一致；直接消费者慢分片可能远超指定超时。待可注入 USB 后端的回归测试。 |
| QA-07 | CLI 资源输入绕过资源超时 | Console Providers 在返回 ValueTask 前同步 Ask/ReadLine；Resolver 的 WaitAsync 无法覆盖尚未返回 Task 的调用。持有 ConsoleUi 锁等待输入也阻塞日志。源码证据，待受控输入测试。 |
| QA-08 | 设备等待无时限且可能选错 COM | TransportResolver 热插拔仅依赖用户取消；显式协议分支谓词只要求 COM，可能接收无关设备。USB 分支未传 CLI read/write timeout，沿用工厂 1000 ms。源码证据。 |

其他审查点：同步阻塞读期间的取消上限；Raw 最后一块与 ACK 等待之间的取消检查；Configure 与多 LUN 初始化的累计等待；部分 XML、日志洪流、迟到 ACK、Raw 期间提前 NAK；断开重连的会话代数；CLI 输出文件创建/失败恢复和枚举参数校验。尚未完成的点不能标为已修复。

## 3. Legacy 线路与必须保留的兼容行为

1. Digest 是可重开、流式发送的签名表。Legacy 不以 Pt 分区表解析成功作为前置条件；Pt 继续解析、映射和校验权限。Legacy 读写按请求的真实扇区范围分段，0 表示参考中的不分段。
2. 每个已发送 XML 计 1 次，每个完整普通/RAW/FILL 数据发送计 1 次；串口分块不重复计数。XML NAK 仍计数，数据传输在发送完成、最终 ACK 前计数。空洞没有数据发送。
   计数器归 Firehose 会话持有，创建会话时注入 InitialPacketCount，早于探测/Configure；资源或策略重建、存储回退不得重新注入。InitialPacketCount 只表示 Core 开始前的设备状态，Core 不能让宿主估算自己的动态重试次数（QA-F 独立审查修正）。
3. 会话保留计数，自动换表/签名补发仅在启用 Legacy 的写入操作内生效；读取、NOP、通用 XML、patch 不因历史配置触发自动 Digest。操作结束即撤销写入开关，防止污染后续调用。
4. 以参考 `check_digest` 当前代码为准：剩余位置 <=2 时，用调用方 NOP 补到 `max_count+1` 后发 Digest。max_count=53、当前 51/52/53/54 对应 3/2/1/0 次边界 NOP；Digest 计在第 55 次。不要改成固定两次 NOP。
5. 触发位置完整 NAK 若日志包含参考 `digest_requested` 识别的换表 Hash/签名请求，继续发 Digest；其他 NAK、半帧、RawMode 错误立即失败。
6. Digest 独立回复窗口为 1000 ms。完整 ACK/NAK 立即结束；无回复或只有完整日志可继续 NOP；半帧在该窗口失败，不能升级到普通 10 秒等待。
7. 独立 ACK 后计数清零，再发送同一 NOP 并计为 1。无独立 ACK 时保留队列；只有 `Calling handler for nop` 日志及完整成功响应才能确认并置为 1，纯迟到 ACK 不够。
8. 完整日志只有 `ERROR: Failed to parse xml / Failed to run the last command`、没有明确 NAK 时，保留参考继续 NOP 确认行为，不自行改为失败。
9. 完整签名失败 NAK 时发送 Digest、确认 NOP、仅重放当前 XML 一次；再次失败停止。不重放已发送的 Raw 数据。
10. Legacy 接受参考允许的 ACK 缺省 rawmode；明确 false 不进入 Raw，最终 true 不算完成。无 XML 回复不伪造 ACK。
11. 配置的 NOP XML 内容、参考 XML 发送前 Flush、声明方言及长度处理列入逐字节对照；相关特殊行为限定在 Legacy，并以结构化预校验保护公共接口。调用方指定的 NOP 文本优先，发送时不再自动追加或覆盖声明；默认 NOP 使用 `value="Start Send Digest"` 和现有 `chimerais="power"` 声明。其他 Legacy 命令继续现有声明方言。特殊声明属性仅接受已识别的 `chimerais`/`Bylaowang` 与固定值 `power`，预校验正文仍使用安全 XML 解析。Legacy 线上 XML 长度处理保持参考截断语义，以配置上限 4096 字节为默认，正文先经结构化校验；该可疑兼容行为在测试中单独记录。

## 4. 契约、状态与资源

- 推荐直接完善现有 Legacy，实现参考规定的线路；不新增第三个模式，Pt 继续独立运行。
- 备选是保留旧 Legacy 并加一个 Rector 模式，API 与宿主复杂度更高，且与用户已选择的模式对应关系不符，因此不采用。
- `OplusDigestConfiguration` 增补/明确可选分段、包计数阈值、初始计数、确认 NOP、XML 发送长度上限与可选回复窗口。兼容保留原公共成员，XML 与输入长度必须有界。CLI 默认采用 max_count=53、不分段、初始计数 0、默认确认 NOP，提供显式参数覆盖。原 `MaxCommandsBeforeDigest` 保留成员名称，但在 Legacy 按参考包计数定义解释，并在 XML 文档及迁移说明中明确语义变化。
- 为完整数据发送计数设置单独内部回调；不复用 VIP 的逐传输块回调。必要的接口扩展优先采用内部策略或默认接口成员，避免破坏现有 IFirehoseStoragePolicy 实现。
- Firehose 接收器增加明确的可选 XML 回复结果：没有回复、完整日志、ACK、NAK、半帧分别表达；共享同一 carry buffer，不清掉关联确认需要的迟到响应。
- 保留会话串行化。命令未上线路的预取消不使会话失效；Raw 取消、超时、读写异常和无法确认的新表使会话 Faulted，旧租约通过代数校验失效，要求重连。
- 调用方保留 IDataSource 所有权，Core 只释放自行 OpenStream 的流；Digest 流长度变化立即失败。敏感认证载荷按契约清零，迟到资源继续观察和释放。
- LibUsb context 在 Transport Dispose 时释放；Open/Close 只管理端点、接口和设备会话。端点选择先验证设备初始化状态，再验证完整 I/O 就绪状态。

## 5. 超时、取消、性能和 CLI

- XML 与精确读取使用单次总预算；Raw 大镜像用空闲超时，正常持续传输不受镜像总时长限制。短探测、启动等待、资源等待和设备发现分别配置。
- CLI 增加 connect/resource/device-wait timeout、Legacy 分段与计数/NOP 参数、非交互开关。非交互资源缺失立即报告；交互输入通过单一可取消输入路径处理，超时后不得遗留读任务抢占下一条命令。
- 修复设备识别谓词，使用相同的协议识别规则匹配枚举和热插拔；USB Transport 传入实际读写 timeout。参数在打开设备前校验，非法枚举、负数、互斥参数报告用法错误。
- 转储输出先写同目录临时文件，读及最终 ACK 成功后再替换目标；失败保留原输出，并在 CLI 明确报告部分数据处理方式。避免无效请求先截断现有文件。
- 所有生产日志和用户异常资源化，中英文键对应；不记录 Digest/签名/完整 XML。兼容确认只记录阶段、计数、耗时与安全的状态。
- Raw/Sparse/Digest 使用窗口和池化缓冲，发送包大小取实际协商上限；映射全量预校验保留，避免按镜像总长度物化数据。测试重复分段传输的分配趋势，吞吐提升只在测量后陈述。

## 6. 实施与验证拆分

1. QA-A：补齐模拟护栏，修复 ACK 判定、协商 Payload 和 Digest 互斥。验证不同扇区大小、正常/提前 ACK、NAK、半帧和失效租约。
2. QA-B：修复 LibUsb 生命周期、端点选择与精确读取预算；通过可控后端验证首次打开、关闭再打开、失败清理、短读及超时。
3. QA-C：逐项迁移 Rector Legacy 测试场景，先失败再实现。覆盖 51/52/53/54 位置、独立 ACK/无 ACK/日志错误、handler 关联、迟到 ACK、NAK、半帧、签名单次重放、Raw/FILL/Sparse/普通分段及写入范围开关。
4. QA-D：连接/读取/写入审查并修复已有项目缺陷；同步入口与异步入口共享策略，记录各阶段预算和硬件风险。
5. QA-E：CLI 参数、取消输入、非交互和设备等待/匹配、USB timeout、可靠输出文件；运行 help/非法参数/输入取消的无硬件测试。
6. QA-F：完整本地回归、Release 构建、资源键校验、敏感日志检查、内存趋势检查和最终 diff；分别提交 `fix(qcom)`、`fix(usb)`、`feat(qcom)`、`fix(cli)` 等独立能力，测试不提交。

每一步更新本次实施进度及原实施文档。测试固定放 `.tests`，不加入解决方案及包。

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore
dotnet build GeekFlashCore.slnx -c Release --no-restore
dotnet src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.dll --help
git diff --check
git status --short --ignored
git ls-files .tests
```

## 7. 已有验证与风险

- NuGet 还原成功；Release 解决方案构建成功，0 警告/0 错误。
- 新增本地测试 5 项：1 通过、4 失败，失败均为上述目标缺陷的复现，不是修复完成。
- 差异检查通过；测试和 bin/obj 均 ignored。
- 未连接真实设备，也未进行真实读取、擦除或写入。本次方案不默认授权在未知设备上执行破坏性验证。
- 同步 Transport 无法直接中断已阻塞的调用；取消延迟需受有效 I/O timeout 约束并实测。
- NOP handler 日志关联规则是指定参考的刻意要求；不输出该日志的 Loader 可能不兼容，记录风险而非放宽规则。
- 参考源码和 README 的近期换表文字有差异，以本次源码及 fixture 固化，后续更新参考需要重新对照。

执行顺序：QA-A、QA-B、QA-C、QA-D、QA-E、QA-F；具体进度见对应实施计划。
