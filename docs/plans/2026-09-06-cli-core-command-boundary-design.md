# CLI-15：CLI 与 Qualcomm Core 命令边界设计

日期：2026-09-06。依据用户审查意见：CLI 只处理输入、文件和输出，设备通信及协议参数校验统一由 Qualcomm Core 负责。

## 目标

- 将所有已建模的 Firehose 命令通过 `IQcomProtocol` 的类型化入口执行。
- 保留 Core 的会话串行化、取消、存储缓存、范围校验、Raw 收发和响应解析。
- CLI 只负责命令行语法、路径和数值文本解析、输出格式以及能力帮助展示。
- 保持现有 CLI 命令名称、参数形式和设备线路顺序不变。

## 非目标

- 不改变 Firehose XML 协议字段、厂商策略、Configure/Digest/VIP 顺序或存储缓存策略。
- 不移除受控的 `ExecuteFirehoseXml`；它作为明确的诊断/扩展入口，由 Core 验证 XML。
- 不让 Core 依赖 CLI 的本地化、文件系统或控制台类型。

## 现状与问题

`FirehoseStorageService` 已实现 `SetBootableStorageDrive`、`Patch`、`Benchmark`、`FixGpt`、`Power`、`GetSha256Digest` 等协议操作，但 `QcomProtocol` 未全部暴露，CLI 因而构造 `BaseCommand` 后调用通用 `ExecuteFirehoseCommand`。这使 CLI 重复维护扇区大小、LUN、范围、数值宽度和 SHA256 响应解析，并允许其他宿主绕过类型化 API。

`StorageCommands` 对 Qcom 的 `TargetInfo` 做范围预校验也是重复逻辑；Core 的 `ReadAsync`、`WriteAsync`、`EraseAsync` 和命名分区解析已经拥有会话状态、存储映射和 checked 范围校验。CLI 只需在创建输出文件前完成必要的输入准备，协议校验由 Core 统一执行。

## 目标边界

Core 在 `IQcomProtocol`/`QcomProtocol` 增加类型化命令入口，内部调用 `FirehoseStorageService`，统一执行：

- NOP、SetBootableStorageDrive、XblGpt、FixGpt；
- Patch、Benchmark、GetSha256Digest；
- 已有的 Peek、Poke、FirmwareWrite、GetStorageInfo、Configure 和 Reboot。

公共参数使用稳定领域类型；Benchmark 模式使用 Core 定义的枚举或参数模型，不暴露 CLI 字符串。SHA256 返回 Core 已解析的 `byte[]`，CLI 只负责安全格式化显示。

CLI 继续负责：命令名和参数数量、十进制/十六进制文本转换、路径规范化、文件流生命周期、用户输出、命令帮助和基于已知能力的提示过滤。CLI 不再创建 Firehose packet，不再调用通用 `ExecuteFirehoseCommand` 执行已建模命令，不再直接读取 `TargetInfo.StorageInfos` 做协议范围判断。

## 兼容性与安全

- `ExecuteFirehoseCommand(BaseCommand)` 保留为底层扩展/测试入口，但禁止 Configure/Program/Read；CLI 不使用它执行已建模命令。
- Core 类型化入口继续使用 `EnterConnected`，由单一层负责会话互斥、取消传播和 NAK 结果。
- 现有 `FirehoseStorageService` 实现作为唯一协议逻辑来源，删除 QcomProtocol.Commands 中与 StorageService 重复的 Peek/FirmwareWrite 线路，或改为薄的门面适配。
- 所有范围和宽度校验集中在 Core；CLI 的文本转换只拒绝无法表示的输入，不复制设备容量判断。

## 测试矩阵

- 类型化 Core 命令的 XML、参数、ACK/NAK、范围和返回数据测试。
- CLI 命令只验证参数转换、调用对应 Core 方法和输出，不验证 Firehose packet 构造。
- 缺失存储缓存时 CLI 不主动调用 `GetStorageInfo`。
- `ExecuteFirehoseXml` 继续覆盖允许列表和 XML 注入边界。
- 全量 Qcom/CLI 测试、Release 构建、`git diff --check` 和 ignored 测试目录检查。

## 未决风险

- 真实设备对 XBL GPT、Benchmark 和特定 Firehose 方言的响应仍需硬件复测；本次只调整调用边界，不改变已有 XML 生成。
- `IQcomProtocol` 新增接口方法时需保留默认实现，以免破坏本地测试代理和其他宿主实现。
