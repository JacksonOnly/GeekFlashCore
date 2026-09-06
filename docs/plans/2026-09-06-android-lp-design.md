# Android.Lp 设计方案

日期：2026-09-06

状态：已完成（真实设备验证待补）

## 目标

将参考项目 `D:\Code\Project\GeekFlashCore\GeekFlashCore` 中的 Android Logical Partition（LP）能力移植到当前 .NET 8 仓库，提供可独立复用的元数据读取、双副本校验、逻辑分区映射、Sparse/RAW 镜像输入、编辑计划和提交验证能力。

## 兼容性与边界

- 保留 Android LP metadata v1.0-v1.2 的 geometry、partition、extent、group、block-device 编码布局。
- 复用当前仓库 `GeekFlashCore.BlockDevice.Abstractions`、`GeekFlashCore.BlockDevice` 和 `GeekFlashCore.Android.Sparse`，不引入参考项目不存在于本仓库的第三方公共类型。
- 公共契约放入 `GeekFlashCore.Android.Lp.Abstractions`；实现放入 `GeekFlashCore.Android.Lp`。
- 输入源和物理块设备遵循 `DeviceOwnership`；解析、计划和提交均使用有界长度及 checked 算术。
- 不在本任务中改变 Qualcomm、GPT 或 Sparse 的既有行为。

## 主要线路

1. `LpMetadataSet.Open` 读取并校验 primary/backup geometry，按 metadata slot 读取两个副本并选择有效副本。
2. `LpMetadataDocument` 解码表项、验证范围并按 extent 建立逻辑分区视图。
3. `LpPartitionImageSource` 对 RAW 或 Android Sparse 输入提供可重放或顺序流式展开的读取接口。
4. `LpEditor`/`LpEditSession`/`LpDraft` 维护单线程编辑状态，`LpLayoutPlanner` 生成 copy-on-write 数据和 metadata 计划。
5. `LpCommitter` 按数据、flush、metadata primary/backup、验证和清理阶段提交，失败返回结构化状态。

## 适配决策

- 参考项目的 `Storage` 类型映射到当前 `BlockDevice` 类型；缺失的映射块设备逻辑在 LP 实现内部提供，避免扩大通用模块范围。
- 参考项目的 `ImageFormats.Abstractions` 限制和诊断模型随 LP 公共契约提供最小等价实现，保持错误上下文和资源上限。
- 日志使用当前项目可用的 `Microsoft.Extensions.Logging.Abstractions`，敏感数据不进入日志。

## 验证矩阵

- 最小 v1.0/v1.1/v1.2 fixture：geometry、metadata、slot suffix、partition size。
- primary 损坏时回退 backup；geometry/表项/范围/校验和边界失败。
- 单块设备和多块设备 resolver；逻辑分区 linear/zero extent 读取。
- RAW、Sparse、Fill、重放与资源释放。
- 编辑计划的名称、容量、重叠、槽位和 metadata 限制。
- 提交阶段的取消、flush、metadata 验证和失败状态。
