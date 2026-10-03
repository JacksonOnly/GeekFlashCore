# CLI 未报告命令列表时的执行修复

日期：2026-10-03；起点 b027229，分支 codex/qcom-legacy-audit，工作区干净。

用户日志证据：Legacy Sign verify passed，sha256init 已知 log-only 兼容后 Configure 成功，六个 LUN 存储查询完成。随后 CLI 显示映射 0 项，partitions all 在 FirehoseCommands.Require 内抛 NotSupportedException，没有向设备发送 read。这次问题发生在宿主能力判断，而非认证或读传输超时。

修复边界：缺失或空 SupportedFunctions 表示未知，允许用户显式执行 CLI 已实现的 Firehose 命令；非空列表保持原有大小写不敏感允许列表和 write/program、reboot/power 别名。断线仍要求重连，未知命令不进入设备执行。展示未知状态与 CLI 实现列表，不宣称设备支持；不填充 TargetInfo 的设备能力，不额外探测，不改 Digest/NOP 包计数或认证线路。完整 NAK、RAW、超时、取消和旧视图仍由 Core 原有边界处理。

范围：FirehoseCommands、CLI 双语资源、CLI 使用说明与本记录；模拟测试留在 ignored .tests。目标验证缺失信息、空列表、非空列表、别名、断线、partitions all/指定 LUN 的实际宿主调用以及底层失败传播，随后完整本地回归、Release 构建、资源键和 diff/ignored 检查。

进度 CE-01：先新增 18 项测试，13 项失败、5 项通过，复现未报告列表误拦截及 partitions 从未调用协议。修复 Require 的未知状态判断和 PrintMapping 展示后，新增本地候选列表/实际设备列表互不污染测试，目标 19/19 通过（46 ms）。覆盖 TargetInfo/Firehose/basic 缺失、空列表、显式读写等命令、大小写/别名、已知列表拒绝、断线拒绝、partitions all/指定 LUN 调用和实际读取超时传播。没有修改协议核心或资源等待策略。

进度 CE-02：完整 163/163 Release 本地回归通过（16 秒），Release 解决方案构建通过，0 警告/0 错误；中英文资源键、git diff --check、CLI --help 和 ignored/跟踪检查均通过。测试及产物未跟踪。现成程序仍为 src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe（2026-10-03 23:09:26 构建），详细日志记录未知命令列表 Warning。

验证命令：dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore（目标测试增加 --filter FullyQualifiedName~CliCommandEvidenceTests）；dotnet build GeekFlashCore.slnx -c Release --no-restore；git diff --check；git status --short --ignored .tests；git ls-files .tests。

风险与后续：用户这次日志证明 chimerais 声明的启动认证和六 LUN 存储查询成功；这属于用户提供的硬件证据，本轮代理未连接设备。尚无本轮 GPT/读写真机成功证据，不能将本地放行等同于 Loader 支持所有命令。下一步使用新 EXE 原参数重新连接后执行 partitions all；如设备拒绝或 RAW 失败，以新的详细日志继续分析。
