# Oplus 手动输入与等待 Digest 续接进度

日期：2026-10-03。设计见同目录 `2026-10-03-oplus-input-resume-design.md`。

- IR-01：已核对两份用户本机文件日志和资源/连接代码，工作区干净，起始提交 55a8e16。确认 CLI 资源默认 15 秒，交互取消漏记；静默连接没有协议状态证据。
- IR-02：测试先行，首轮目标 13 项中 9 失败、4 通过，复现交互 15 秒预算、无限资源参数被拒和续接缺失。CLI 交互资源默认 -1，脚本默认 15000 ms，显式预算优先；Core 默认不变。两种资源入口支持无限等待与取消，有限预算到期报告 QcomResourceException 和具体预算，迟到资源仍观察/释放。控制台和文件补充手动等待开始/完成/取消耗时，取消只记录一次且返回 130。
- IR-03：新增 --oplus-resume 和 ResumeAwaitingDigest，仅在声明设备等待第一张 Digest 时跳过 Sahara/启动日志；声明每实例仅使用一次。Pt/Legacy 的同步/异步模拟线路首包均为 Digest；普通静默连接不发送数据。Digest NAK 后不 Verify/Configure，再次连接不能盲发 Digest。未改 Rector 运行期换表线路。
- IR-04：完整 122/122 Release 本地测试两次通过，包括实际等待 16 秒仍停留在 Sign 提示、未发任何认证包，提交路径后正常认证；最终 Release 解决方案构建 0 警告/错误。--help 包含无限资源预算与续接说明，未启用 Oplus 的续接参数在发现设备前返回 2。CLI/Core/Abstractions 双语资源键一致、完整差异与 git diff --check 通过；.tests 和构建/日志产物保持 ignored、未跟踪。新增设计、实施记录和 CLI 使用说明已同步，修改均为本任务范围。

验证命令：

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore -v quiet
dotnet build GeekFlashCore.slnx -c Release --no-restore -v quiet
git diff --check
git status --short --ignored .tests
git ls-files .tests
```

分支：codex/qcom-legacy-audit。交付程序：src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe。没有合并或推送。

风险：未执行设备连接或读写，续接设备状态须由调用方确认；无期限手动等待不改变设备端超时。此续接不适用于已认证/RAW 中断/未知状态，需重进 EDL 后普通连接。下一步由用户使用已构建 CLI 复测，依据新文件日志核对实际 ACK 和 Verify。
