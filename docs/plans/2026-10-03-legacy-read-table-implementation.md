# Legacy 读取表边界实施记录

开始 2026-10-03，完成 2026-10-04；设计见同目录 legacy-read-table-design，分支 codex/qcom-legacy-audit。

- LR-01：新 12 项测试全部 RED，复现跨读调用、读取分段及一般 XML 不检查表边界、换表失败不传播的问题。旧写入线路和新边界组合共 28 项 GREEN；随后新增取消、设备新表 NAK、普通读 NAK、不在触发位置的签名 NAK及脱敏测试，1 RED/7 GREEN，修复前置失败误进入 Program 重放后完整 182/182 Release 测试通过（16 秒）。模拟传输确认 51/52/53/54 时 3/2/1/0 个补位 NOP → Digest → 确认 NOP → read，连续 60 个 read 在同一表计数中换表，输入 RAW 不累计为输出包。
- LR-02：Legacy Attach 安装内部 session 检查，Execute/ExecuteXml 在原操作租约中调用，executor 内补位/确认不递归获取租约；预检查取消/NAK失效不恢复旧状态，Program 已失效时不重放。Debug 表计数/边界日志不包含材料；新表 Hash mismatch 固定脱敏状态可见。生产范围为 FirehoseSession、Legacy policy/counter、设备文本和双语资源，StorageService 已有 Attach 无须修改。
- LR-03：最初原输出目录被用户 geekflash 进程 18268 锁住，构建无法复制文件；未终止设备会话。改用 ignored temp/legacy-read-fix 完成测试及 Release 解决方案构建（0 警告/0 错误）。用户进程结束后，常规 Release 构建也通过（0 警告/0 错误），原程序路径可用。测试/临时产物保持 ignored，未提交。

验证命令：dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --artifacts-path temp/legacy-read-fix；dotnet build GeekFlashCore.slnx -c Release --artifacts-path temp/legacy-read-fix；dotnet build GeekFlashCore.slnx -c Release --no-restore；双语资源键、help、git diff --check 和 ignored/跟踪检查。

风险：未进行本轮硬件读写；用户原日志证明 GPT 读取及 53 包处新表拒绝，修复后的连续读取/换表需复测。Rector Session::read 默认关闭 Digest 的限制已明确记录，不能将本项目修复说成参考公开入口原行为。后续 bug.txt 的显式 resume 首次失败/第二次成功是独立启动恢复任务。
