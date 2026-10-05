# PRINT-01：浏览器帮助与文件文本显示

日期：2026-10-05。范围为 CLI 小型功能，底层协议和公共契约保持既有行为。

## 行为与实施

- 浏览器 `help` 改为逐条中文/英文说明，覆盖 ls/cd/up/pwd/read/find/exit、路径与编号输入，并给出真实 Super 层级中的 build.prop 示例。`browse help` 和 `browse-image help` 可以直接查看帮助，无需打开传输或镜像。
- 新增 `print <path>`，打印普通文件的完整文本，当前默认上限为 24 KiB（24576 字节，含边界），超限在打开文件内容前拒绝并提示 read 导出；不提供扩大上限参数。
- 无 BOM 文本按严格 UTF-8 解码，BOM 支持 UTF-8/UTF-16/UTF-32。其他编码或无效文本提示使用 read；保留换行与制表符，其余控制字符显示为转义，防止文本改变终端状态。
- 长度校验后使用有界池化缓冲；全部读取与解码成功才输出，短读或取消不输出部分文本。流由浏览器关闭，缓冲清零归还。同步文件系统读取及 Firehose RAW/ACK 完整返回后检查取消，沿用搜索以外的程序级取消语义。
- 影响文件：BrowserCommands、BrowserSession、CliApplication、中英文 Strings、CLI 使用文档与实施记录；本地测试位于 ignored `.tests`。

## 验证进度

- 测试先行：最初 23 项覆盖 Ext/LP→EROFS、0/24576/24577/异常长度、BOM、分片/短读、节点类型、文本控制字符、取消与用法；旧实现目标测试 22 失败、1 通过。再补 512/4096 FirehoseBlockDevice 加模拟传输的普通/嵌套 print、RAW 内取消、脱离设备帮助；审查发现已断连交互入口先检查连接，新增两项先 RED，再把 help 分流提前。
- 最终目标测试 33/33，当前工作树完整本地回归 122/122（原 89 + 本轮 33），Release 解决方案构建 0 警告、0 错误。中英资源键集合一致、git diff --check 通过，git ls-files .tests 无输出。
- 首次标准构建曾被用户正在运行的 geekflash 锁住；采用 ignored `.tests/print-build` 独立验证，未终止用户程序。锁释放后标准构建和测试均通过，原 `src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe` 已更新。
- 实际新版 EXE 在合成 Super 镜像中打印 Ext 与 EROFS 文本、返回目录及 pwd，通过非交互 browse help；24577 字节文件被拒绝并返回退出码 1、提示 read。夹具、日志、测试与构建产物均 ignored，无设备操作。

验证命令：

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~BrowserPrintTests -v quiet
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore -v quiet
dotnet build GeekFlashCore.slnx -c Release --no-restore -v quiet
git diff --check
git ls-files .tests
```

## 风险与恢复

- 本轮真实设备 print 和终端按键尚未验证；已有真机 Super→Ext 浏览证据不能视为本轮验证。
- 下一步从新版 CLI 进入 browse super，执行 `print /super/system_a/system/build.prop` 进行真机小文件复测。真实 RAW 超时仍按原规则使会话失效，不把错误当作取消。
