# Qualcomm XML 与 CLI 实施记录

日期：2026-10-06；基线：9159d62；启动工作区干净。

设计见 2026-10-06-qcom-xml-cli-design.md。范围与接口依据用户本轮需求确定，无额外审批要求。

最新输出恢复来源：`2026-10-06-cli-output-statistics-implementation.md`（OUTPUT-01）。已补充毫秒分区计时、XML/批次用时与平均速度、逐条跳过分区/地址和多行对齐输出；最终四套 Release 回归共 434 项通过。

## 进度

- XML-01：已读取 alioth 六组 XML、现有 Program/Storage/会话 gate 和 CLI 命令证据逻辑，确定采用专用领域转换与流式写入。
- XML-02/03：新增 IQcomProtocol 同步 ExecuteRawProgram/ExecutePatchFile、领域执行结果和有界 XML 解析。先预检一份 XML 的全部结构、几何、资源与 Sparse 计划，再持有同一 gate 按序执行专用适配命令；空 filename、不支持/设备排除命令及非 DISK patch 返回带序号的跳过原因。数据和 XML 共用既有 Program、OnePlus credential、Digest/VIP hook；CRC32 参数在 Host 检查，设备计算 CRC；命令失败不重放，失效会话清理并阻止旧块设备访问。预检失败不发送、不破坏会话。
- XML-01 样例补充：alioth rawprogram1.xml 和 rawprogram2.xml 在最后包含 `<xblgpt lun="1|2"/>`，已保留原序；这是一份真实 XML 样例证据，不等同于真机 xblgpt 执行证据。
- CLI-14：加入直接文件输入、rawprogram/patch 命令、qcom 前缀与 program XML 别名；通配匹配有上限、稳定排序、去重，路径含空格可用，镜像相对 XML 所在目录解析且限制在目录内；旧数字 patch 和 write/program 语法保持。
- CLI-15：默认帮助优先通用命令，连接/info/help 展示设备支持 => Host 支持交集，未知列表仅显示未知；细节由 help qcom、help 命令或 help all 展示。交互命令历史最多 200 条，仅内存；Up/Down 与草稿恢复、光标/删除、Tab/Shift+Tab 循环补全，资源输入不进入历史，重定向不调用补全。Windows 输入期间启用 VT 并在结束时恢复原模式。

### XML-PROGRESS-01（2026-10-06）：写入分区与文件名

- 启动：基线 bc77ab0，分支 codex/qcom-xml-cli-20261006，工作区干净。用户反馈写入时缺少分区名和文件名；原因是 ProgramProgress 丢弃请求中已有的 Label/FileName，使用固定“写入”文本。
- 行为：同步 Program、XML program 和通用 WriteAsync 共用带分区/LUN 的进度标签；XML/显式 Program 同时使用请求 FileName，无 label 时回退到 LUN 与起始扇区。普通 CLI write/program 在字节进度中追加本地镜像文件名；步骤进度保持原内容。名称在每条写入开始时确定并保留到最终 ACK 成功的完成记录，字节数、阶段和时序沿用既有回调，不改协议报文或公共契约。
- 范围：Qcom 三处 ProgramProgress 调用与标签构造、CLI StorageCommands 写入进度适配、对应中英文资源及使用文档；没有修改 Read/Patch 或其他协议线路。
- 测试先行：新增 4 项用模拟传输/宿主代理复现缺失名称，均先失败；覆盖中英文、连续三条 XML program（同文件不同分区与缺失 label）、开始/传输/完成名称和字节统计。修复后 Qcom 进度/XML 目标 48/48、完整 Qcom 292/292、CLI 67/67，共 359 项通过，无跳过。
- 验证命令与结果：dotnet test 两套工程 -c Release --no-restore（Qcom 最终全量使用 --no-build）；dotnet build GeekFlashCore.slnx -c Release --no-restore，0 警告/0 错误；git diff --check 通过，CLI 228 / Qcom 275 组中英文资源键一致。git ls-files .tests 为空，测试与 bin/obj 仍 ignored。
- 提交范围：独立提交 `fix(cli): show partition and filename during writes`，仅含本轮生产代码和文档；完成后工作区应干净，不合并或推送。
- 风险与继续：本轮没有设备通信或可见终端复测；请用本工作区更新后的 EXE 复核实际刷写时的名称、窄终端换行与完成行。Core 每条写入仅生成一次上下文标签，镜像资源和流式线路保持既有行为。

## 验证证据（2026-10-06）

- 恢复 D:/Code/CSharp/GeekFlashCore 的四套 ignored 历史测试源，不复制 bin/obj。初始协议/CLI 目标测试分别出现 5/7 项预期失败，确认入口缺失和未知列表误显示 xblgpt；其后负例收紧到领域异常，防止缺失入口被宽泛异常断言误判通过。
- 目标 XML 38/38、CLI 新增交互/脚本 11/11 通过。包括 12 份原始 alioth XML 配合合成镜像/几何、非 program 顺序、Raw/Sparse/Fill/Don't Care、offset、count=0、DISK/CRC/溢出、缺文件、DTD/嵌套/namespace、设备排除、NAK、取消/旧块设备失效、预检后继续 NOP、power 必须最后、流关闭、帮助过滤、路径含空格、通配去重/缺失、目录逃逸、草稿编辑/补全、重定向。
- XML 流式分配检查：8 MiB 与 512 MiB 合成 Raw 镜像分配差小于 256 KiB，512 MiB 执行分配低于 1 MiB，来源读取和 RAW 写入均不超过 1 MiB；预检及执行打开的两个流均释放。既有 512 MiB Raw 内存回归也通过。
- 完整当前本地回归：Qcom 289/289、CLI 66/66、Android LP 55/55、Core 9/9，共 419 项，无跳过。运行 dotnet test 四套工程 -c Release（Qcom/CLI 最终全量使用已生成 DLL 的 --no-build --no-restore）；dotnet build GeekFlashCore.slnx -c Release --no-restore，0 警告/0 错误。
- EXE 离线 help、help patch 和 rawprogram 缺参用法输出符合预期；无设备连接。CLI 227 / Qcom 272 组双语资源键一致，git diff --check 通过；git ls-files .tests 为空，测试/夹具/日志/bin/obj 保持 ignored。
- 最终范围复审：XML 不透传自定义命令和敏感字段；所有新增用户文本资源化；源和流所有权明确；分配不随 Raw 总长度增长；原数字 patch/write 路径回归通过。生产提交按 Core 与 CLI 拆分。
- 提交：Core 为 `3dbd801 feat(qcom): execute validated rawprogram and disk patches`；CLI 独立提交 `feat(cli): add XML flashing and interactive command editing`。分支为 `codex/qcom-xml-cli-20261006`，仅本地提交，不合并或推送；构建输出位于本工作区 src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe。

## 未决风险

- 没有本轮硬件证据；设备侧 CRC32、实际刷写与终端效果待复测。
- 本工作区只恢复上述四套历史测试，不声称覆盖其他工作树新增而未迁入的测试（例如 PBL 412 项矩阵或 MTK ignored 测试）。
- 批次非事务，多文件逐份预检；资源 resolver 必须提供稳定的可重开资源，实际执行仍再次校验计划。Sparse 预检和执行会读取/验证计划两次，真机吞吐待测。
- 没有 XML 文件适配的 read/peek/poke、配置/认证命令跳过；readbackverify=true 拒绝。表达式仅支持记录的数字/NUM_DISK_SECTORS 加减与 CRC32，不支持任意表达式或执行脚本。
- 下一步：用户使用本工作区新 EXE，在同一联机会话按 rawprogram、patch 顺序复测；核对实际设备的 CRC、xblgpt 支持证据和交互终端效果。
