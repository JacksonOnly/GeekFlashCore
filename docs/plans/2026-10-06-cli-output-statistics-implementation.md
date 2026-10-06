# CLI 输出与统计实施记录

日期：2026-10-06；任务 OUTPUT-01；设计见 2026-10-06-cli-output-statistics-design.md。

## 进度

- 已读取 ce7eeba 的写入标签修复、用户设备输出、统计时钟、脚本跳过模型和 CLI 输出模板；工作区干净。
- 已确认分数秒被取整、没有 XML/批次独立统计，跳过汇总无法定位分区。用户确认分区和批次都要修正。
- Core 已为 FirehoseScriptSkippedEntry 增加可选 init 元数据，保留三参数构造/解构：label、filename、原始/可解析 LUN、起始扇区与扇区数。转换仅使用缓存几何；位置无法解析时保留原文，原本可跳过的非法位置继续跳过，零设备 I/O，会话保持可用。
- 分区进度时间精度改为毫秒，开始、运行、最终 ACK 完成共用单调时钟；完成速度包含 ACK 等待，重复完成记录不重新统计。多行进度按实际行数与宽字符宽度清理，控制字符替换为空格。CLI 给每份 XML 和整批文件分别计时、累加真实写入量，汇总同一 elapsed 快照的时间与平均速度；零字节批次不显示速度。
- 逐条显示所有跳过项的 XML 序号/命令/分区、位置、文件与原因，不再只给命令名分组汇总。未知名称用 LUN/扇区定位；原表达式可保留并附解析数值。
- 命令映射一项一行并对齐；默认帮助的多种用法、连接/存储/浏览信息按字段换行，分区列表按 Unicode 显示宽度对齐。联机/info 的通用命令放在映射之后、提示符之前，避免被列表挤出可见范围；help 默认仍先展示通用语法。共享 CLI 的 MTK 模板/能力展示也拆开字段，未修改 MTK 协议实现或线路。
- 新增测试先确认 Qcom 2 项、CLI 6 项失败；追加分区列对齐与 MTK 能力展示测试也先失败。新增共 11 项（含一个已通过的跳过展示集成测试）覆盖有界元数据、非法位置可跳过、最终 ACK 等待、125 ms/64 KB/s、重复完成、显式换行行数、列对齐、中英文模板、2/4/6 秒 XML/批次统计与零字节 patch、控制字符、MTK 能力逐项展示。

## 验证

- 目标：Qcom XML 41/41、CLI 输出/统计/进度 20/20 通过。完整本地四套 Release 回归：Qcom 294/294、CLI 76/76、Android LP 55/55、Core 9/9，共 434 项，无跳过；Qcom/CLI 最终全量使用已重新编译的测试 DLL（--no-build --no-restore），LP/Core 以 --no-restore 重新编译测试运行。
- dotnet build GeekFlashCore.slnx -c Release --no-restore 成功，0 警告/0 错误。CLI 236 / Qcom 275 组中英文资源键及格式参数一致；git diff --check 通过，git ls-files .tests 为空，测试与 bin/obj/日志 ignored。
- 已用 EXE 离线 help all 检查多种用法与选项分行；同步/异步普通写入、Raw/Sparse、NAK/取消/流关闭及既有 512 MiB 流式分配回归包含在上述 Qcom 全量测试内，不新增设备 I/O。生产提交按跳过元数据与 CLI 展示分开，当前只在 codex/qcom-xml-cli-20261006 本地分支提交，不合并或推送。
- 提交：`a210330 feat(qcom): describe skipped XML partitions and addresses`；CLI 独立提交 `fix(cli): align output and report XML batch statistics`。下一步使用本工作区 src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe 复核；不要根据旧实例的输出判断新布局。

## 风险

- 本轮不执行硬件通信；待用户以本工作区更新后的 EXE 复核终端布局及真实总用时/速度。
- 用户日志仍是旧版固定“写入”；本机查询没有返回正在运行的 geekflash.exe，不能据此认定其执行路径。

## 主分支集成（2026-10-06，MERGE-XML-01）

- 用户授权“并入主分支”。主工作区为 D:/Code/CSharp/GeekFlashCore；合并前 main 为 777d345，功能分支 codex/qcom-xml-cli-20261006 为 51212f9，两处已跟踪工作区均干净。
- 共同基线为 9159d62；main 新增两项 MTK 提交，功能分支新增五项 Qualcomm/CLI 提交（3dbd801、bc77ab0、ce7eeba、a210330、51212f9）。使用 git merge --no-ff --no-commit 合并，无冲突，保留双方历史；main 的 MTK 核心和 AGENTS.md 没有被本次合并修改。
- 在主工作区补入本任务新增的 16 个 ignored 测试/原始 XML 夹具文件；仅对四个既有测试文件/工程应用已审查的新增断言、帮助语义和夹具引用差异，保留其他本地测试。CLI 首次 --no-restore 编译因旧依赖缓存缺少 MTK 抽象引用失败；执行该测试工程 dotnet restore 后重新编译运行通过，没有因此修改生产代码。
- 主工作区五套工程均以 dotnet test <project> -c Release --no-restore -v quiet 重新编译并测试：Qcom 294/294、CLI 76/76、MTK 337/337、Android LP 55/55、Core 9/9，共 771 项，0 失败/跳过。包含 XML 顺序与流式内存、DISK patch、跳过位置、分区/XML/批次统计、命令映射、历史和补全，以及 main 当前 MTK 回归。
- 主工作区 dotnet build GeekFlashCore.slnx -c Release --no-restore -v quiet 通过，0 警告/0 错误；实际 Release EXE 离线 help all 退出 0，包含 rawprogram/patch XML 用法。git diff --cached --check 通过，git ls-files .tests 和已跟踪 bin/obj/temp 检查均为空；本地测试、夹具和输出保持 ignored。
- 合并与本记录仅在本地 main 提交，未推送；功能工作区及分支保留。主工作区 CLI 已更新至 src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe。没有执行设备通信；下一步使用此 EXE 复核 rawprogram/patch、终端布局及真实统计，设备侧 CRC32/厂商响应风险保持上述记录。
