# CLI-11 命令与 Firehose 映射设计

日期：2026-09-05。根据用户明确的 CLI 语法与 EdlClient 参考要求实施，保留同步协议架构。

## 交互契约

- `partitions <all|lun>`；兼容用户拼写 `parittions`。无参数显示用法，不发 I/O。
- `read <partition> <file> [lun]`、`write <partition> <file> [lun]`、`erase <partition> [lun]`。
- `read sector <lun> <start> <count> <file>`、`write sector <lun> <start> <count> <file>`、`erase sector <lun> <start> <count>`。start/count 接受十进制或 0x；使用已协商的扇区大小。
- 缺参、非法参数和多余参数显示具体命令用法，返回非零状态；交互会话继续。参数校验发生在文件创建/连接/命令发送前。
- 分区结果带 LUN、起始扇区、offset/length 的原始 Bytes 与按 1024 换算的 KB/MB/GB/TB。

## 协议与资源边界

核心增加按 LUN 读取分区和查询存储的公共方法；Configure 阶段读取 LUN 0 并按设备报告的 `num_physical`（上限为现有 MaximumPhysicalPartitionCount）各读取其余 LUN 一次，建立本次会话的存储快照。通用 GetPartitionsAsync 只消费快照，保留局部无 GPT 的诊断，传输错误继续传播；同名分区要求明确 LUN。显式 `getstorageinfo` 仍可主动刷新，分区和范围命令不再隐式查询。

CLI 映射为“本地已实现处理器 ∩ 设备声明命令”。连接后显示映射成功、命令说明和具体用法，只展示交集，缺少能力证据时不虚构支持。`CommandSyntax` 和离线 help 仅包含通用命令；注册适配器通过 `IProtocolCommandSet` 提供 Firehose 专用解析、执行和动态帮助，主流程继续只依赖 `IProtocol`。普通 `read/write/erase` 与 `program` 别名使用同一存储入口；每次执行检查当前快照，重配后重新显示映射。保留 `qcom` 前缀入口。`qcom xml` 和 `qcom probe-sahara` 保留为显式诊断入口，不纳入设备命令交集列表。

按分区名写入 Raw 镜像仅补齐末扇区，分区长度作为容量上界；显式 `write sector` 保留指定扇区范围的补齐行为。公共 `FirehoseProgramRequest.PadToSectorCount` 默认 true，保留原 Program 调用契约，命名分区的 WriteAsync 设置为 false；Sparse 计划不受影响。保持流式计划，不额外重开或物化镜像。写入结束以实际写入字节报告完成。

首批：program/read/erase/nop/configure/patch/setbootablestoragedrive/power/getstorageinfo/benchmark/peek/poke/xblgpt/getsha256digest/firmwarewrite，另支持已声明 fixgpt。benchmark 必须明确 read/write/digest 模式；power 使用生命周期入口。ufs/emmc provisioning 暂不映射，不能仅以通用 XML 当作已实现功能。

peek 以 256 字节窗口解析设备十六进制日志，poke 以至多 8 字节小端 value64 写入；参考 EdlClient 的标准 size_in_bytes/address64 线路，大小写旧方言仍为实机风险。firmwarewrite 使用 IDataSource 和流式 Raw，不按文件大小物化。所有流由打开者释放，取消在命令/数据块边界检查。公共类型不暴露第三方库；新增接口方法提供默认 NotSupportedException 以兼容其他 IQcomProtocol 实现。

Firehose ChipId 缺失时来自 Sahara.MsmHwInfo.MsmId；ChipName 缺失时显示可用 SoC 名或 Sahara MSM ID，不将数值推断为芯片型号。SupportedFunctions 不再默认伪造 18 个命令，支持无标题但有结束标记的命令列表。刷新存储时保留此前已获得、而新响应省略的 num_physical。Peek/Poke/SHA256 响应不写设备文本日志，NAK 使用通用异常消息，结果内容仍由调用方取得。

## 验证和风险

测试放 ignored `.tests`：缺参/额外参数无副作用，文件参数顺序，512/4096 扇区和十六进制地址，指定 LUN/全 LUN/同名歧义；声明能力过滤和命令映射；peek/poke 字节顺序和 NAK，firmwarewrite 流与取消；能力列表及 Chip 回填。完整 Qcom/CLI 测试、Release 构建、diff 和资源键检查后按独立能力提交。

未操作真实设备。GPT 继续严格校验主表；LUN 数量缺失时只使用已知 LUN，不盲探。厂商 provisioning、认证、QFIL 批任务及私有 peek/poke 方言不在首批命令范围。
