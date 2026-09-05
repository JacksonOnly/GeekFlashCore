# CLI-10：联机信息、重连与 GPT 修复

日期：2026-09-05。范围为用户本次明确要求的缺陷修复，沿用已批准 Qualcomm 架构。

## 目标和证据

- `partitions` 不再静默：没有 GPT、无效 GPT 和空列表有可见诊断；传输异常继续向 CLI 传播。
- `GptParser.Parse(..., HeaderOnly=true)` 的布局检测仍要求完整条目容量，两扇区输入必然被拒绝。Qcom 改为先校验头字段、CRC、条目长度和设备范围，再补读元数据并调用完整解析器。
- NOP 探测只返回 bool 丢失日志；基本信息解析未设置 BuildDate。保留 NOP 响应并解析 `Binary build date: ... @ ...`，configure 时间仍可回填。
- `getstorageinfo` 已解析 `prod_name`，但未写入 FirehoseTargetInfo.UfsName；将其作为 UFS 产品名运行时证据。
- 重连现有编排已包含 configure/getstorageinfo，增加同步与异步线路顺序、失败清理测试，确保不会只执行 NOP 就宣告联机。
- 参考项目 `D:\Code\Project\GeekFlashCore\GeekFlashCore` 的 Qcom 分区读取和 FirehoseResponseExtensions 仅作为源码行为证据，未进行硬件验证。

## 实施边界

不新增公共 API，不改变同步 I/O、资源 Provider、Digest/VIP/厂商认证顺序。NOP 响应仅在当前连接中保留，断开仍清理。GPT 元数据读取上限保持 16 MiB，所有外部数值先校验再分配；取消传入存储读取，损坏 GPT 可跳过但传输失败不可吞掉。新增诊断使用中英文资源，不输出认证材料。

文件范围：QcomProtocol、QcomProtocol.Storage、FirehoseSession、ConfigureEvidenceParser、FirehoseStorageInfoParser、CLI 输出及对应资源；不改通用 GPT 镜像解析契约。

## 验证与交付

本地 `.tests` 测试先行：512/4096 扇区 GPT 头后补读、错误头/CRC/越界、NOP 重连完整顺序和元数据保留、启动/配置日期、UFS 产品名、CLI 空结果及错误日志。运行目标测试、完整 Qcom 回归、Release 解决方案构建、`git diff --check`，确认测试与产物忽略。按联机信息、GPT/CLI 诊断两项独立能力提交。

## 进度和风险

- 2026-09-05：已完成代码、CLI 文档与历史提交核对，工作区初始干净；开始回归测试。
- 2026-09-05：联机信息修复已实现。两条 NOP 探测入口保留响应，基本信息解析构建日期，UFS 产品名和配置 TargetName 回填，修复函数列表结束标记误匹配。同步/异步重连及失败清理测试通过；完整 Qcom 回归 186 项通过，Release 构建 0 警告/0 错误。
- 设备未报告的 Build Date 只能显示 unknown；不能从主机时间推断设备构建时间。
- 当前存储初始化只查询 LUN 0；本次先修复已发现块设备的 GPT 读取，多 LUN 自动发现不混入本次修复。
- 真实设备重连、厂商 GPT 和元数据日志格式需现场复测，本地模拟传输只能证明已覆盖线路。
