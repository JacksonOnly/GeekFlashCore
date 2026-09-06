# CLI-12 实机日志回归修复设计

开始日期：2026-09-05，实施与验证续接至 2026-09-06。基线：7c5e396，开始时工作区干净。按用户要求先形成本文档，再修改代码。

## 目标与证据

用户粘贴的 22:49–22:52 实机日志显示：首次认证后 getstorageinfo 超时；重开 CLI 后可连接；partitions all/read boot_a 遍历 LUN 时再次发生 getstorageinfo 超时；LUN 1/2/3/4 因空类型 GUID 被整体跳过；超时之后的 read/partitions 被误报成设备不支持 read。

1. 已确认 CLI 的 ReadTimeout/WriteTimeout 默认值为 1000 ms，覆盖 QcomProtocolOptions 的 10000 ms。FirehoseWireReader 将整个 XML 响应（含多个日志包）限制在此期限，实机失败位置与该期限相符。修复默认配置差异，不把未知硬件延迟或丢包断言为已证明的原因。
2. 已确认 Qcom 将 AllowEmptyPartitionTypeId 设置为 false，GptParser 仅同时为空的类型 GUID/唯一 GUID 才跳过。空类型 GUID 带有其他残留字段时会使整个 LUN 的有效分区丢失。为设备读取增加明确的空类型槽跳过选项，在字段解码/几何检查前忽略未使用槽；既有镜像编辑允许空类型的行为保持兼容。
3. 已确认故障会话会清理 TargetInfo，而 CLI Require 将缺失快照当作不支持命令。先校验连接状态，给出执行 connect 的恢复提示；禁止在失效会话继续设备 I/O。

2026-09-06 补充只读证据：日志中所用 alioth 工厂 ROM 的 gpt_main1–5.bin 均存在一个带残留内容的空类型槽，位置与日志中整表跳过现象相符。使用新设备解析选项可在严格 CRC/几何校验下分别取得 2、2、4、66、10 个有效分区；这些是 ROM 文件证据，并非新采集的设备 GPT。

## 范围和兼容性

涉及 CLI 超时默认值、Firehose 命令可用性判断与资源、GPT 解析选项/条目读取、Qcom GPT 解析选项，以及 ignored .tests。通用帮助与设备/Core 命令交集保持不变。Sahara→Firehose 的认证、NOP/configure/getstorageinfo 顺序和同步 I/O 不变。无需新资源提供器或异步边界。

新增 GptParseOptions.SkipEmptyPartitionTypeId，默认 false 保留镜像编辑兼容性；Qcom 设备读取显式开启。该选项优先于 AllowEmptyPartitionTypeId，空类型槽在名称解码之前跳过。CLI 协议适配器在命令分派前统一检查连接状态，覆盖 sector/info/xml 等不经过 Require 的前置路径；connect/configure/probe-sahara/help 等恢复与诊断入口仍可使用。

仅修复上述有代码证据的问题，不盲目重试已超时命令、不返回不完整分区列表作为成功、不削弱有效分区的 CRC/范围校验、不修改设备 GPT、不自动重新认证/刷写。重开进程后只有 Firehose 证据时 chip=unknown 无法靠之前进程的 Sahara 日志回填，不虚构芯片信息。

## 失败、安全和性能

默认超时统一为 Core 常量，显式 --read-timeout/--write-timeout 保持优先，仍有确定期限。超时后的核心清理与旧句柄失效继续保留，CLI 提示 connect。GPT 空槽仅跳过解析，不更改输入字节及 CRC；头部和条目数组的分配上限继续保留。新增用户文本使用中英文 resx，无额外原始数据日志。

## 验证和提交

先编写失败测试：默认与显式超时；超过 1 秒但小于默认期限的模拟响应；空类型/非空唯一 GUID/残留名称槽旁的有效分区可读，损坏 CRC 和有效条目越界仍拒绝；断线后的 read/partitions/专用命令提示连接且无 I/O，联机但真正未声明能力仍提示不支持。再作最小修复。

完整运行 Qcom 与 CLI 测试、Release 解决方案构建、git diff --check、资源键和 ignored 状态检查，审查完整 diff。按 GPT 和 CLI 两个独立修复提交；进度记录于相邻 implementation 文档。真实设备的延迟/厂商 GPT 变体仍需用户复测，模拟测试不替代硬件证据。
