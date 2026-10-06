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
