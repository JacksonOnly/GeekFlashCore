# FIND-01：首个结果停止与安全取消

日期：2026-10-04；基线：214a39d，工作区干净。

## 用户证据与行为

用户 23:43 附件确认 Firehose 续接和 Super→Ext 挂载已可使用；find build.prop 打印 /super/system_a/system/build.prop 后继续遍历，后续 RAW 读取超时导致会话失效。本轮不能从日志断定后续扇区超时的设备原因，不放宽协议失败后的会话失效保护。

- find <glob> [path] [输出目录] 默认在第一个匹配文件（及其可选导出）后停止；find --all <glob> [path] [输出目录] 保留全量搜索。底层 Find 可枚举全部结果，CLI 控制停止范围。
- Ctrl+C 在 find 期间只取消本次搜索/搜索导出，当前同步 ReadAt 的 RAW 数据及最终 ACK 完成后再响应取消。搜索令牌只用于宿主遍历、文件系统字节读取边界及本地输出，不传给 Firehose RAW 传输，不 Flush、Reset、Close 或重连。
- 搜索结束、取消和失败均移除 Ctrl+C 搜索范围并恢复 BrowserSession 的操作令牌。取消后留在当前浏览器目录，可继续 ls/cd/read/find；搜索外的 Ctrl+C 保持原程序取消语义。实际 RAW 超时/NAK 仍报错并失效，不伪装成正常取消。
- 已完成导出保留；正在导出的文件取消时沿用原子输出逻辑，保留原目标并删除临时文件。路径、符号链接、循环、深度和命名冲突检查不变。

## 文件与验证

修改范围：CLI BrowserCommands、BrowserSession、ConsoleUi、Program、中英资源、使用文档。同步协议核心不修改；不增加并行设备读取或竞争 stdin 的后台读者。

ignored .tests 覆盖默认首个后不访问故障目录、--all 全量导出、首个导出、512/4096 字节扇区分片 RAW 内 Ctrl+C 请求后读完 ACK、继续浏览/搜索/NOP、搜索导出取消保留目标，以及搜索外的程序取消。先运行 RED，再最小实现并 GREEN；完整当前测试、Release 构建、资源键、diff 和忽略文件检查。

## 进度与风险

- FIND-00：已阅读附件与设备日志，确认超时发生在首个结果后的目录继续读取。开始新行为回归；真实日志与模拟传输验证分别记录。
- FIND-01：新增目标测试 10 项，首次运行 9 失败/1 通过，复现首个后继续访问故障目录、默认导出多个结果及全局取消行为。CLI 默认首个匹配后停止，--all 明确启用全量；搜索命令在参数数量校验后使用独立、关联程序令牌的取消范围，Ctrl+C 只发给该范围。BrowserReadDevice 在完整底层 ReadAt 返回后观察取消；原协议 RAW 令牌未修改，未 Flush/Reset/Close。退出范围通过锁与 Ctrl+C 同步，所有结束路径恢复浏览器操作令牌。
- FIND-02：目标回归扩展至 15/15；512/4096 字节扇区、默认与 --all 均在 RAW 分片期间请求取消，并确认剩余载荷与最终 ACK 全部读完，随后 ls/find/NOP 成功，程序令牌未取消、Firehose 保持联机。搜索导出取消保留原目标且无临时文件；空结果释放取消范围；真正 RAW 超时仍向上抛出并使会话失效；外部程序取消也不吞掉。当前完整本地测试 89/89（原有 74 + 本轮 15），Release 构建 0 警告/0 错误。测试为模拟传输/合成文件系统，本轮未操作设备。
- FIND-03：中英资源键集合一致，git diff --check 通过，git ls-files .tests 无输出；测试、夹具、EXE 与日志保持 ignored。实际新 EXE 配合 --non-interactive/stdin 对合成 Ext 镜像依次执行 find * 导出与 find --all * 导出，前者只生成 settings.conf，后者生成 settings.conf/hello.txt 两个文件，内容校验通过，随后 pwd 成功，退出 0。CLI 使用说明和既有浏览器进度已同步。源码按单个搜索修复能力提交，CLI 本目录依赖均已重建。
- Ctrl+C 需要等当前同步读取结束；若设备自身超时或断连，取消不能修复其通信。真实控制台按键与设备再次复测待用户完成。

验证命令：

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~BrowserFindStopTests -v quiet
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore -v quiet
dotnet build GeekFlashCore.slnx -c Release --no-restore -v quiet
git diff --check
git ls-files .tests
```

恢复位置：src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe。用户退出旧程序后重开新版，在 /super/system_a 执行 find build.prop，结果出现后应立即回到当前浏览器；如需全量，使用 find --all build.prop。搜索中 Ctrl+C 取消后继续 ls/read 核对设备通信。对当前已经因 RAW 超时失效的旧会话，取消修复不能使旧句柄重新有效。
