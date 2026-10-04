# FH-READ：Firehose 续接与文件系统字节读取修复

日期：2026-10-04。基线：c1b6821，工作区干净。

## 依据与修复边界

- 用户 23:28 日志：已运行 Firehose 对 Sahara 唤醒包返回完整 XML 解析错误日志。检测识别为 XML 后仍等待启动函数列表结束标记，十秒后超时。该样本没有 ACK/NOP 成功证据，不能直接宣告联机。
- 用户 23:29 日志：browse super 在 LP 魔数的 4 字节读取处被 FirehoseBlockDevice 的扇区对齐校验拒绝；合成 MemoryDevice 允许字节读取，此前浏览器测试未覆盖真实 FirehoseBlockDevice。
- FH-01：仅对普通模式、未上传 Loader、确实发送 Sahara 唤醒包后识别为 XML 的分支，允许将完整 XML 解析错误作为探测拒绝响应结束读取，再用有界 NOP ACK 确认运行状态。保留日志证据；不将该错误当作 Configure 成功。不完整 XML、一般启动日志、新上传 Loader、VIP 和 Oplus 等待首表仍不得发送恢复 NOP。
- FH-02：在 FirehoseBlockDevice 将任意字节读取转换为对齐扇区读取。头尾共用一个池化扇区，中间完整扇区直接写入调用方 Span；返回精确字节并遵循 EOF 短读语义。写入仍要求对齐。会话代数、只读句柄和协议同步 I/O 保留，未新增缓存或镜像物化。

## 验证与提交计划

1. 在 ignored .tests 先复现解析错误续接超时、4 字节和跨扇区读取失败；覆盖同步/异步、NOP 未确认、半帧、VIP、禁止恢复。
2. 验证 512/4096 字节扇区下的头/中/尾、EOF、无效句柄、写入对齐，以及真实 FirehoseBlockDevice 的 Super→Ext/EROFS 导航、搜索和小文件导出。
3. 每项修复单独英文提交；完整当前测试、Release 解决方案构建、资源键检查和 git diff --check。测试与夹具不提交。

## 进度与风险

- FH-00：已阅读用户附件、两份运行日志和既有 BOOT-04 保护边界，开始本轮回归。日志为用户设备证据；新增验证为模拟传输，不代表本轮硬件复测。
- FH-01：首次新增 14 项测试中 9 项失败，确认启动超时与字节对齐两个原始触发点；随后增加 Oplus、拒绝后的半帧/RAW 防护，续接相关 14/14 通过，原有测试加续接测试 67/67 通过（字节读取 4 项暂不运行，留给 FH-02）。完整解析错误之后按 ConnectTimeout（默认 1500 ms）收集后续完整日志/可选 NAK，再以相同预算确认 NOP ACK；VIP/函数列表结束标记优先识别。新上传 Loader 的公共 Start 行为及 Oplus 路线未启用此恢复入口。Release 解决方案构建 0 警告/0 错误，git diff --check 通过。没有操作真实设备。
- FH-01 提交：`4632cb6 fix(qcom): confirm firehose after rejected sahara probe`。
- FH-02：4 字节、非对齐起点、跨扇区、EOF 和 512/4096 字节扇区的 Super→Ext/EROFS 已通过真实 FirehoseBlockDevice 加模拟传输验证，导出内容均精确为 content，返回与搜索路径正确。对齐读取仍只发一个原始范围；非对齐读取最多头/中/尾三个范围，新增缓冲最多一个 65536 字节扇区，归还时清零。末尾不完整扇区及接近 Int64.MaxValue 的范围在 I/O 前拒绝；大于 65536 字节扇区的非对齐读取明确拒绝。写入对齐、只读保护和断开后句柄失效测试通过。测试中的只读句柄写入原本误期望对齐异常，已改为分别验证只读异常与可写句柄对齐异常，未改变生产写入路径。
- FH-03 最终验证：当前完整本地测试 74/74（原有 53 项加本轮 21 项，Release，约 0.5 秒），历史主工作区测试未迁移，不能称为历史全集回归。Release 解决方案构建 0 警告/0 错误；中英资源键一致，git diff --check 通过，git ls-files .tests 无输出。实际新 EXE 以 --non-interactive/stdin 浏览合成 super.img，完成 Ext 导出→返回 Super→EROFS 搜索导出，退出 0，两个输出内容校验通过。EXE、测试、夹具、日志和输出均 ignored。未连接或操作硬件。

验证命令：

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore -v quiet
dotnet build GeekFlashCore.slnx -c Release --no-restore -v quiet
git diff --check
git status --short --ignored .tests src/GeekFlashCore.CLI/bin src/GeekFlashCore.CLI/obj
git ls-files .tests
```

交付位置：本 worktree 的 src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe 及同目录依赖已重建。恢复步骤：退出旧 CLI 后运行该目录新版，真机先验证已运行 Firehose 的连接，再 browse super，进入 system_a 等分区并导出一个小文件核对；失败继续保留对应新日志分析。

- 待确认：恢复 NOP 的设备实际延迟及真实文件系统方言/压缩支持；同步传输取消延迟沿用现状。对已运行设备的未知/不完整响应仍明确失败，不无界重试或自动重新上传 Loader。
