# NAV-01 / REVIEW-01：返回父目录与浏览器提交合并

日期：2026-10-05。用户授权审查并合并本轮提交。

## 行为与范围

- 原有路径解析已支持 `cd ..`；本轮将精确的两参数 `cd ..` 显式映射到 `up` 的同一命令处理入口。参数数量错误仍报用法，普通 cd 路径与多层相对路径行为保持。
- 新增本地导航回归先确认既有行为，再验证映射：文件夹→文件系统分区→Super→虚拟根目录；根目录返回仍停留 `/`。`up`、`cd ..`、大写命令和带引号的 `..` 输出与路径完全一致。中英文 cd 帮助补充父目录说明。
- 合并范围为 `main` 基线 cbafc5b 后全部提交：厂商证据/宿主选择、LP→Ext/EROFS 只读浏览、Firehose 续接与字节对齐、搜索首个停止/安全取消、print/帮助及本轮导航别名。主工作区为 `D:/Code/CSharp/GeekFlashCore`，源工作区为 b6cf；开始时两处 Git 工作区均干净。

## 审查结论

- 未发现阻止合并的问题。重点核对厂商优先级与选择失效清理、同步/异步门面一致性、特定 Sahara 唤醒后的 XML 拒绝/NOP ACK 续接条件、VIP/新 Loader/Oplus 保护、非对齐头中尾读范围与池化生命周期。
- 浏览器审查包括按需 LP 挂载、借用/转移/退出释放、文件系统小范围同步读取、首个匹配后停止、局部取消令牌恢复、真实 RAW 错误失效、路径边界和链接/特殊文件保护、原子导出保留原文件、print 24 KiB 与解码/终端控制字符边界。
- 公共旧构造函数及既有入口保留；用户文本资源化且中英键一致。无新增敏感日志，无按镜像总大小物化，测试/日志/构建产物不提交。
- 既有风险继续保留：真实随机读与终端取消延迟待设备复测，Ext/EROFS 方言未穷举，输出目录链接的并发替换竞态没有新保证。审查和模拟测试不替代真机证据。

## 验证与合并进度

- 本轮目标导航测试 4/4（每项分别执行四种命令写法），当前工作树完整回归 126/126，Release 解决方案构建零警告零错误。
- 用户运行中的 b6cf CLI 锁定标准产物，使用 ignored `.tests/review-build/bin` 构建验证，未终止用户程序或访问设备。
- 已提交 `2c36c84 fix(cli): map cd parent to browser up`，主工作区 main 从 cbafc5b 快进合并到该代码提交，无冲突，无未知修改；未推送。
- 主工作区旧 Qcom 测试有一项仍要求字节读取必须对齐，与 FH-02 已明确修改的契约冲突。将该 ignored 本地测试更新为真实跨扇区字节结果校验，继续要求写入严格对齐；未修改生产代码或提交测试。更新前 Qcom 250 通过/1 失败，目标更新后 1/1、完整 251/251。
- 合并后主工作区 Qcom 251/251、CLI 55/55、Android LP 55/55、Core 9/9，共 370 项通过；完整 Release 构建零警告零错误，中英 CLI/Qcom 资源键和 diff 检查通过。
- 主工作区新 EXE 合成 Super 冒烟确认连续 cd .. 返回文件系统分区、Super、虚拟根目录，根目录不越界，随后嵌套 EROFS print/find 可继续使用，退出码 0。主工作区标准 CLI 已重建，b6cf 运行中的旧实例未中断；改用主工作区 `src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe` 可使用合并版。
- 两处 Git 工作区干净，测试、合成夹具、日志、独立构建目录及标准 bin/obj 均 ignored，无受跟踪测试。文档收尾同步 main；真机复测仍从只读 browse/print/find 小文件开始。

验证命令：

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore -p:BaseOutputPath=C:/Users/a1375/.codex/worktrees/b6cf/GeekFlashCore/.tests/review-build/bin/ --filter FullyQualifiedName~BrowserNavigationTests -v quiet
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore -p:BaseOutputPath=C:/Users/a1375/.codex/worktrees/b6cf/GeekFlashCore/.tests/review-build/bin/ -v quiet
dotnet build GeekFlashCore.slnx -c Release --no-restore -p:BaseOutputPath=C:/Users/a1375/.codex/worktrees/b6cf/GeekFlashCore/.tests/review-build/bin/ -v quiet
git diff --check
```

主工作区追加验证（工作目录 `D:/Code/CSharp/GeekFlashCore`）：

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore -v quiet
dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore -v quiet
dotnet test .tests/GeekFlashCore.Android.Lp.Tests/GeekFlashCore.Android.Lp.Tests.csproj -c Release --no-restore -v quiet
dotnet test .tests/GeekFlashCore.Core.Tests/GeekFlashCore.Core.Tests.csproj -c Release --no-restore -v quiet
dotnet build GeekFlashCore.slnx -c Release --no-restore -v quiet
git diff --check
git ls-files .tests
```
