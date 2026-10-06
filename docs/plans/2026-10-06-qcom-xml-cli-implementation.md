# Qualcomm XML 与 CLI 实施记录

日期：2026-10-06；基线：9159d62；启动工作区干净。

设计见 2026-10-06-qcom-xml-cli-design.md。范围与接口依据用户本轮需求确定，无额外审批要求。

## 进度

- XML-01：已读取 alioth 六组 XML、现有 Program/Storage/会话 gate 和 CLI 命令证据逻辑，确定采用专用领域转换与流式写入。
- XML-02/03：新增 IQcomProtocol 同步 ExecuteRawProgram/ExecutePatchFile、领域执行结果和有界 XML 解析。先预检一份 XML 的全部结构、几何、资源与 Sparse 计划，再持有同一 gate 按序执行专用适配命令；空 filename、不支持/设备排除命令及非 DISK patch 返回带序号的跳过原因。数据和 XML 共用既有 Program、OnePlus credential、Digest/VIP hook；CRC32 参数在 Host 检查，设备计算 CRC；命令失败不重放，失效会话清理并阻止旧块设备访问。预检失败不发送、不破坏会话。
- XML-01 样例补充：alioth rawprogram1.xml 和 rawprogram2.xml 在最后包含 `<xblgpt lun="1|2"/>`，已保留原序；这是一份真实 XML 样例证据，不等同于真机 xblgpt 执行证据。
- CLI-14：加入直接文件输入、rawprogram/patch 命令、qcom 前缀与 program XML 别名；通配匹配有上限、稳定排序、去重，路径含空格可用，镜像相对 XML 所在目录解析且限制在目录内；旧数字 patch 和 write/program 语法保持。
- CLI-15：默认帮助优先通用命令，连接/info/help 展示设备支持 => Host 支持交集，未知列表仅显示未知；细节由 help qcom、help 命令或 help all 展示。交互命令历史最多 200 条，仅内存；Up/Down 与草稿恢复、光标/删除、Tab/Shift+Tab 循环补全，资源输入不进入历史，重定向不调用补全。Windows 输入期间启用 VT 并在结束时恢复原模式。

## 验证证据（2026-10-06）

- 恢复 D:/Code/CSharp/GeekFlashCore 的四套 ignored 历史测试源，不复制 bin/obj。初始协议/CLI 目标测试分别出现 5/7 项预期失败，确认入口缺失和未知列表误显示 xblgpt；其后负例收紧到领域异常，防止缺失入口被宽泛异常断言误判通过。
- 目标 XML 38/38、CLI 新增交互/脚本 11/11 通过。包括 12 份原始 alioth XML 配合合成镜像/几何、非 program 顺序、Raw/Sparse/Fill/Don't Care、offset、count=0、DISK/CRC/溢出、缺文件、DTD/嵌套/namespace、设备排除、NAK、取消/旧块设备失效、预检后继续 NOP、power 必须最后、流关闭、帮助过滤、路径含空格、通配去重/缺失、目录逃逸、草稿编辑/补全、重定向。
- XML 流式分配检查：8 MiB 与 512 MiB 合成 Raw 镜像分配差小于 256 KiB，512 MiB 执行分配低于 1 MiB，来源读取和 RAW 写入均不超过 1 MiB；预检及执行打开的两个流均释放。既有 512 MiB Raw 内存回归也通过。
- 完整当前本地回归：Qcom 289/289、CLI 66/66、Android LP 55/55、Core 9/9，共 419 项，无跳过。运行 dotnet test 四套工程 -c Release（Qcom/CLI 最终全量使用已生成 DLL 的 --no-build --no-restore）；dotnet build GeekFlashCore.slnx -c Release --no-restore，0 警告/0 错误。
- EXE 离线 help、help patch 和 rawprogram 缺参用法输出符合预期；无设备连接。CLI 227 / Qcom 272 组双语资源键一致，git diff --check 通过；git ls-files .tests 为空，测试/夹具/日志/bin/obj 保持 ignored。
- 最终范围复审：XML 不透传自定义命令和敏感字段；所有新增用户文本资源化；源和流所有权明确；分配不随 Raw 总长度增长；原数字 patch/write 路径回归通过。生产提交按 Core 与 CLI 拆分。

## 未决风险

- 没有本轮硬件证据；设备侧 CRC32、实际刷写与终端效果待复测。
- 本工作区只恢复上述四套历史测试，不声称覆盖其他工作树新增而未迁入的测试（例如 PBL 412 项矩阵或 MTK ignored 测试）。
- 批次非事务，多文件逐份预检；资源 resolver 必须提供稳定的可重开资源，实际执行仍再次校验计划。Sparse 预检和执行会读取/验证计划两次，真机吞吐待测。
- 没有 XML 文件适配的 read/peek/poke、配置/认证命令跳过；readbackverify=true 拒绝。表达式仅支持记录的数字/NUM_DISK_SECTORS 加减与 CRC32，不支持任意表达式或执行脚本。
- 下一步：用户使用本工作区新 EXE，在同一联机会话按 rawprogram、patch 顺序复测；核对实际设备的 CRC、xblgpt 支持证据和交互终端效果。
