# SPRD 入口阶段自动识别实施记录

日期：2026-10-08；任务 SPRD-10；设计见同日 entry-detection-design。

- 开始：18c2a12，codex/sprd-support，工作区干净；已读原 SPRD 设计/实施、GPT/Raw 补充和 docs/sprd.md。按用户最新授权形成自动入口设计；原有不猜阶段约束由此设计替代。
- 证据：固定上游的首帧校验和 FDL2 unsupported/disable-transcode 顺序，不复制其重试、全局状态或吞异常；无实机。
- 测试先行：增加入口/Provider/CLI 测试，初次因 Auto 与 EntryStage 元数据缺失编译失败。随后实现同步首帧双校验、具体阶段选择与异步材料请求；默认 Auto，枚举末尾追加，三个显式入口顺序保持。
- 自动 BootROM 为 CHECK_BAUD(VERSION/CRC) → CONNECT ACK → FDL1 → 原 FDL1/FDL2 顺序；自动 FDL1 为 CHECK_BAUD(VERSION/FDL checksum) → CONNECT ACK → 仅 FDL2；自动已加载 FDL2 为特征响应 → DISABLE_TRANSCODE ACK，随后无转义，不请求 Provider、不上传。Raw/容量等仍由显式 profile 决定。日志和 CLI info 显示初始入口，TargetInfo.EntryStage 成功后可读，断开/失败清除。
- 仅初始零响应超时允许一次不同 CONNECT 查询；写超时、部分帧、日志后超时、取消和总预算耗尽禁止。补充先失败的 CONNECT 查询收到 VERSION 测试，复现多发 CONNECT 的边界；修正为该查询仅接受 FDL2 特征，ACK/VERSION 拒绝，不重发或上传。
- 双校验碰撞使用固定完整 VERSION body 00810002fd08（两种校验均为 0274）验证拒绝；识别后 ACK 改用另一校验也拒绝。碎片/混合校验日志不决定阶段；FDL2 协商 NAK 不进入 StorageReady。Provider 仅收到具体阶段，成功/迟到的拥有源释放；实际材料缺失、重入或超时失效关闭。并发 gate 跨探测及资源等待，第二个连接不重复探测。

## 2026-10-08 验证

均为本地模拟/CPU 证据，本工作树仅有 SPRD 与 CLI 两个 ignored 测试工程。

| 命令或检查 | 结果 |
| --- | --- |
| `dotnet test .tests/GeekFlashCore.Protocol.Sprd.Tests/GeekFlashCore.Protocol.Sprd.Tests.csproj -c Release --no-restore -v quiet` | 121/121（新增 27 项入口测试；包含原流式/大源及 GPT/Raw 回归） |
| `dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore -v quiet` | 22/22（新增 4 项，默认/显式 auto、手动 BootROM 预检及协议隔离） |
| `dotnet build GeekFlashCore.slnx -c Release --no-restore -v quiet` | 0 warning / 0 error |
| `dotnet src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.dll help sprd` | exit 0；默认 Auto、实际所需 Loader 及 FDL2 关闭转义说明正确 |
| 资源键与差异/忽略审查 | SPRD core/abstractions/CLI 中英文键一致；git diff --check 通过，.tests/temp/bin/obj 未被跟踪 |

生产代码、帮助、README、docs/sprd.md、设计/实施与 AGENTS 恢复入口同步，提交 `feat(sprd): detect initial BSL loader stage`，确切提交号由包含本记录的提交恢复；.tests 不提交。

## 未决风险与下次入口

没有真实设备。需用合法匹配 FDL 验证三种起始阶段、首包无响应的 FDL2 重连和 DISABLE_TRANSCODE；识别后等待 Provider 时的设备 watchdog/断线仍待验证。任意自定义 Loader、SPRD4/autod 中间模式及只返回 ACK 的入口不能视为已验证支持，使用显式入口。单字节 CHECK_BAUD 在部分 FDL 的行为与兼容性同样待实机确认。每个探测帧最多两个有限校验计算，帧数沿用日志预算，没有按镜像大小增加物化；原 64 MiB 流式回归通过，不作为设备吞吐证据。
