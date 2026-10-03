# Oplus 抓包对齐进度

日期：2026-10-03；设计见 `2026-10-03-oplus-capture-alignment-design.md`。

- CA-01：完成三个附件比对；成功路径 verify EnableVip=0，无 ping；当前失败发 EnableVip=1，设备完整 Hash mismatch 日志后重新等表，没有 ACK。成功路径 sha256init log-only 错误后继续，当前严格 ACK 会在下一步再次阻塞。
- CA-02：先写黄金线路、sha 边界、Verify 表重启及 spaced HEX 脱敏测试，首轮 12 项中 6 失败/6 通过，复现错误字段、严格 sha ACK 阻塞、错误诊断丢失及 spaced HEX 未隐藏。随后收窄到有硬件证据的 Legacy 路径；Pt 原有参考 ping/EnableVip=1 与严格 sha ACK 保留，并新增同步/异步回归。认证材料采用模拟内容，真实素材不提交。
- CA-03：Legacy Verify 改 EnableVip=0，无 ping；Verify/sha/Configure 声明空白和字段/顺序逐字节对齐，仅使用 chimerais 名称。黄金传输长度 8144/93/4096/95/190 与参考片段一致。sha log-only -1 特例在最多 1500 ms（服从更短 read budget）后记录 Warning 并 Configure；静默/未知错误/NAK/RAW/半帧/重新等表失败，Sign 的 ACK 不能省略。初始 UFS 配置仍可自动回退，显式存储/参数/Payload 上限保留；运行期 Legacy 声明/NOP/换表未改。
- CA-04：普通和可选响应按帧即时发布已完成日志；queued response 只发布一次，敏感命令仍可抑制文本。Hash mismatch 提供固定脱敏状态，spaced HEX 全部隐藏。首轮完整 134 项通过；补充大 Digest 精确长度、接收预算、配置回退/显式值、认证严格及日志测试后通过 141 项。随后新增畸形/空 response 和未知错误伴随 ACK 的失败测试，分别 RED→GREEN；最终完整 144/144 Release 本地回归通过（16 秒），全部是模拟传输/纯内存证据。Release 解决方案构建 0 警告/0 错误；help、中英文资源键、diff 和 ignored 检查通过，测试及产物没有被跟踪。

验证命令：

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore -v quiet
dotnet build GeekFlashCore.slnx -c Release --no-restore -v quiet
src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe --help
git diff --check
git status --short --ignored .tests
git ls-files .tests
```

交付：分支 `codex/qcom-legacy-audit`，现成程序位于 `src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe`，详细文件日志仍写到程序旁 `logs`。本轮修改集中于 Legacy bootstrap、Configure 编排、接收诊断/脱敏、双语资源和说明文档，未修改 CLI 参数或资源等待实现。之前无限手动等待、显式 Sign、Sahara HEX/PkHash 功能保留。

未决风险：本轮无硬件连接；最终重命名声明后的设备接受情况需用户复测。下一步请重新进入 EDL 后执行原 Legacy 命令，可加 --oplus-sign 指定 Sign 文件，并对比新日志中的 Verify/Sign/sha/Configure。此次失败已经发送 Digest 和 Verify，不能作为仅等待第一张 Digest 的 --oplus-resume 场景。入口为同目录设计与 CLI 使用说明。
