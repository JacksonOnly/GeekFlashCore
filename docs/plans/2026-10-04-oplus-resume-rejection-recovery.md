# 显式 Oplus 续接首次拒绝恢复

日期：2026-10-04；读取表修复提交 140b2e2 后继续，当前工作区干净。

bug.txt 的第一条 --oplus-resume 命令发送 Digest 后出现 data Hash mismatch、Packets received 10、Tables received 0、Failed to read XML -1、VIP 等待 signed table 8192、signature failed 3、完整 NAK。第二条相同命令正常 Digest/Verify/Sign/sha/Configure。证据说明显式续接时设备首先处于 XML 会话，拒绝后才切到签名表接收；不能声称最初已经等待第一张 Digest。

边界：只在显式 ResumeAwaitingDigest 且尚未认证的初始化中，第一张 Digest 的一次完整输出（长度不超过 8192 的单包）收到完整 NAK rawmode=false、data Hash mismatch 日志，并在拒绝响应/最多 1000 ms 的完整 XML 尾部确认 VIP 等待 signed table 时，允许重新打开同一 Digest 再发送一次。第二次仍必须完整 ACK，才能执行原 Verify/Sign/sha/Configure。没有 NOP/reset 探测、不使用异步 I/O、不重置普通/已认证会话、不换资源或盲目重放 RAW。多包中途失败、无明确接收状态、普通 Digest/其他模式入口、未知 NAK、无 value/半帧、RAW、静默和取消仍使会话 Faulted 并停止。

范围：Oplus 启动门面、FirehoseSession 专用初始化发送、双语日志、CLI 说明和本记录。初始 Digest ACK 后计数仍归零；独立的 Sign 手动替换最多一次保持。资源打开/关闭仍由门面所有，重试前验证 Digest 长度未变化，Sign 最终清零。

测试先行：用户日志结构的完整/分离尾部、同步异步和 Legacy/Pt、未显式 resume、无 marker/无 Hash 状态、RAW/半帧/第二次 NAK、不重复资源请求、不重复 Verify/Sign；本地全部测试与 Release 构建、资源/diff/ignored 检查。没有本轮硬件连接，真实复测入口为原参数 --oplus-resume。

进度 RR-01：新增 11 项测试，5 RED/6 GREEN，复现显式 resume 在完整拒绝/新等待状态后仍直接失败，以及第二次拒绝必须停止。实现后发现旧启动 VIP 提示被误当成拒绝后的状态证据，补充顺序检查：只接受最后一次 Hash mismatch 之后的等待提示，11/11 GREEN。Legacy/Pt 同步异步成功顺序均为 Digest → Digest → Verify → Sign → sha → Configure → storageinfo，没有重复资源请求。

进度 RR-02：补充 Digest 单包长度上限、完整 log-only 尾部、RAW 尾部和取消测试，记录打开/释放次数；单包复用时 Digest 重开两次并全部关闭，多包中途拒绝不再发送后续部分或重试。恢复权限只给第一次初始化资源尝试，不扩大到手动替换 Sign 后的重发。目标 Bootstrap 62 项通过；最终完整 197/197 Release 本地回归通过（16 秒），Release 解决方案构建 0 警告/0 错误，CLI/Core/Abstractions 双语资源键、diff/ignored/跟踪检查通过。原程序目录可以重新构建，测试及临时产物未跟踪。

交付验证：dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore；dotnet build GeekFlashCore.slnx -c Release --no-restore；git diff --check；git status --short --ignored .tests temp；git ls-files .tests temp。程序为 src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe，详细日志仍在程序旁 logs。本轮代理未连接硬件，用户 bug.txt 只证明原版第二次 resume 成功，修复后的首次恢复需复测。
