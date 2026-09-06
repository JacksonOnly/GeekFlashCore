# CLI-12 实施记录

开始日期：2026-09-05；完成验证：2026-09-06。设计：2026-09-05-cli-device-regressions-design.md。

- [x] 阅读实机日志、历史与相关代码，先写设计与验证方案。
- [x] 回归测试复现超时配置、GPT 空槽和断线误报。
- [x] 修复代码并验证失败路径，保持现有兼容性。
- [x] 全量测试、Release 构建、资源/diff 审查。
- [x] 按独立修复拆分提交并检查仅包含任务文件，用户 cust.img 保留未跟踪。

初始证据：基线 7c5e396；CLI 默认 1 秒而 Core 默认 10 秒；Qcom 严格空类型检查导致整 LUN 被跳过；Require 未先判断 IsConnected。原始实机附件不提交，尚未执行本轮硬件操作。

## 2026-09-06 / CLI-12 验证结果

- 测试先行：5 项 CLI 超时/断线回归、2 项 GPT 残留空槽回归在修复前失败，修复后通过。审查扩展到 sector/info/xml 前置路径，新增 3 项失败复现后由适配器统一连接检查修复。
- 只读 ROM 验证：alioth 工厂镜像 gpt_main1–5.bin 各有 1 个带残留的空类型槽；严格解析后有效分区数依次为 2、2、4、66、10。输入字节未改动。
- GPT 兼容性：残留非法 UTF-16 名称在空槽被忽略；默认镜像编辑仍保留空类型条目；损坏数组 CRC 和有效分区越界仍拒绝。
- 超时验证：模拟 1150 ms 的首包响应，CLI 默认 10 秒可成功；显式 1 秒仍抛超时并使会话失效，不引入重试或无限等待。
- `dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore -v quiet`：213/213 通过。
- `dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore -v quiet`：35/35 通过。
- `dotnet build GeekFlashCore.slnx -c Release --no-restore -v quiet`：0 警告、0 错误。
- 完整生产 diff 已审查：修改局限于超时默认值、连接判断和 GPT 空槽；未更改认证/传输顺序或弱化 CRC；新增 Guid 为值类型，不新增镜像分配。新增文本中英文键一致；git diff --check 通过；测试文件保持 ignored。

## 风险与继续验证

提交拆分：GPT 与设计为 bb2da21（fix(gpt): skip residual unused slots in device tables）；CLI 超时/连接提示及实施记录为 fix(cli): align timeouts and report disconnected sessions。

未连接真实设备。默认 1 秒覆盖是已修复的配置缺陷，不能仅凭模拟测试断言所有硬件超时已消失；若仍超时，需要带 --verbose 的定时日志确认 ACK 与数据到达。下一步在设备上执行 connect、partitions all、read boot_a boot_a.img，检验完整 LUN 枚举与读取；不自动重试任何设备写入。

过程中工作区出现未跟踪的 cust.img，不属于本次修复，不读取、不删除、不纳入提交。保留用户镜像；测试和 ROM 文件不提交。
