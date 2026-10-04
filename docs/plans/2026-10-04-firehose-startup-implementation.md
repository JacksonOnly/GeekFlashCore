# BOOT-04 实施记录

日期：2026-10-04；分支：codex/qcom-legacy-audit；基线：cd371d7，两个工作区跟踪文件干净。

- 已读取用户两个运行日志。首次为 250 ms 启动等待后错误发送 NOP，并收到 VIP marker、认证失败和 NAK；第二次被动检测为 0 字节超时。
- 设计见 2026-10-04-firehose-startup-design.md。先补模拟启动和模式选择回归，再修改门面启动判定和 CLI 参数准备。
- 保持 Legacy Hash 诊断加 ACK 后继续发送的参考兼容；不改变程序/RAW/计数/换表/验证线路，不把静默设备自动当作等待 Digest。
- RED：StartupSelectionTests 共 22 项，21 失败、1 项既有普通静默回退通过。模拟 300 ms 后的 VIP/函数结束 marker，短 ConnectTimeout 和 1000 ms ReadTimeout；另覆盖完整日志/半帧后超时、注入已上传阶段且完全静默、模式选择与显式/脚本/取消。
- GREEN：22/22 通过。启动总预算改用 ReadTimeout；Reader 保留启动字节证据，仅普通且未上传、完全静默会话允许 NOP 回退。新上传阶段测试使用门面阶段注入，非真实 Sahara 上传或硬件证据。
- CLI 在协议工厂创建前通过可选注册回调选普通/Pt/Legacy；默认普通，错误输入重选，显式 None/模式/资源参数、脚本和不可交互输入跳过选择。明确 Oplus 重连和 --oplus-resume 的前提，选择事件仅进入文件。
- 完整当前本地测试 226/226 通过（18 秒，无跳过），含 Legacy Hash 诊断加 ACK 继续发送和既有 bootstrap/换表回归；Release 解决方案构建 0 警告、0 错误。CLI 137 对资源键、Qcom 252 对资源键一致且无重复；git diff --check 通过。
- 验证命令：dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore；dotnet build GeekFlashCore.slnx -c Release --no-restore。CLI 使用说明已同步，生产差异未触及 Legacy Policy、计数、Program/RAW 或 Sign 线路；未进行真实设备 I/O。
- 提交与主工作区合并后验证待补记。当前运行已显示认证失败，无法证明可直接续接；请重新进入 EDL 后使用正确模式/资源复测，禁止自动推断静默状态。
