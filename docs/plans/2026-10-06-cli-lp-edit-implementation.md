# CLI LP 编辑实施进度

2026-10-06 LPCLI-00：基线 944bc21，工作区干净。已阅读 AGENTS、Qcom 恢复文档、浏览器设计/进度和 LP 编辑契约。用户确认配置指 LP 元数据。方案见同日 cli-lp-edit-design；当前工作树没有 .tests，复用其他工作树已有 ignored 测试建立本轮验证。尚未访问设备。

2026-10-06 LPCLI-01：新增主命令 `ls <partition/path> [lun] [lp-slot]`，对 LP/Ext/EROFS 一次列表即返回；Raw 节点拒绝。LP 子项列举不探测每个文件系统，保留显式 LUN，歧义在打开设备前失败。browse 也能直接进入嵌套路径。主 read/write 支持 `super/system_a` 和显式 LP slot；挂载内 read 分区路径导出完整 Raw，文件路径仍导出文件，未知/损坏子文件系统不影响完整 Raw 导出。原子输出、源镜像保护与现有搜索/文本行为保留。

2026-10-06 LPCLI-02：CLI 复用 LpEditor/Draft/PartitionImageSource/Committer。write 支持 Raw/Sparse，保持分区容量、短镜像补零、超限零写；优先分配空闲 extent，需要时允许原位覆盖。新增 lp info/rename/resize/attributes/move/add/remove 及组新增/改名/容量/flags/删除；既有组可按有效或原始名称选择，新名字使用原始名称。元数据操作默认禁止原位数据覆盖，保留其他槽位占用与默认组约束。每次编辑前释放只读本地文件/浏览卷，提交成功或失败后清除全部浏览缓存，成功回到重新加载的 LP 容器，失败回到根目录。旧节点不可继续打开文件。

2026-10-06 LPCLI-03：测试发现 LP 数据计划可能把五字节 Raw 和后续零填充拆成不对齐区间，不能直接给要求整扇区写的 FirehoseBlockDevice。新增 CLI 内部可写切片，先校验 GPT 范围、扇区对齐、可写能力和实际 source/descriptor 一致性；不对齐头尾以一个有界池化扇区读改写，完整区间直接写。同步 ReadAt/WriteAt 继续经 Core 完整 ACK/Raw 顺序，分区切片外字节保留，缓冲清零归还。公共协议和格式契约未修改。

2026-10-06 LPCLI-04：进入挂载只输出一行提示和列表，help 按导航/读写/LP 分组，help read/write/find/lp 给出具体说明。参数错误只输出该命令语法。主命令列表/Tab 补全加入 ls/lp，help lp 与 lp help 脱离设备可用。所有新增固定文本中英资源化，资源键集合一致。使用与边界见 docs/cli-qcom.md。

2026-10-06 LPCLI-05 验证：最初 5 项新测试在旧代码全部失败，实施后逐步扩展到目标 29/29。覆盖挂载/未挂载、完整镜像/文件、Ext/EROFS、Raw/Sparse、512/4096 扇区、LUN 歧义、slot 1、其他槽/分区保留、全部元数据操作、默认组保护/非空删除、零写校验、缓存与资源释放、短帮助、取消和 NAK。真实 FirehoseBlockDevice 加模拟 transport 验证非对齐短镜像、主备 metadata 和最终 ACK 均消耗，随后 NOP 可继续。完整非 Raw program NAK 沿用 Core 可恢复状态且仅发送一次；元数据临界区取消完成主/备两次写后停止回读，清空浏览状态，没有重放。

完整本地回归：Qcom（含浏览器）449/449、CLI 76/76、Android LP 55/55、Core 9/9、MTK 337/337，总计 926 项通过。恢复的历史 BrowserPrintTests 中，旧完整帮助文案断言随新帮助更新，ProtocolRegistry 单注册项假设改成明确选择 Qualcomm；这是 ignored 测试适配，不改变生产协议。Release 解决方案构建 0 警告/0 错误；git diff --check、资源键检查和 git ls-files .tests 通过。测试、夹具、日志、bin/obj 未跟踪。

分配检查：预热后 1 MiB 与 64 MiB 本地镜像写入均约 74,520 字节托管分配，8 MiB 增量护栏通过；本地缓存文件测试约 9/58 ms，不能代表设备吞吐。数据路径使用现有 LP 256 KiB 缓冲，不按镜像总大小展开。测量输出位于 ignored 测试产物 lp-allocation.txt。

实际 EXE 冒烟：新版标准 Release 产物通过 --non-interactive/stdin 执行 help write、read system_a、write system_a、group-add/move/attributes/rename/resize/info、改名后文件导出及 help lp；退出 0，完整导出 32,768 字节，回读文本精确为 changed。lp help 独立调用退出 0、无需连接。夹具与日志留在 ignored .tests/smoke；未使用真实设备。

验证命令：

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~LpCliTests -v quiet
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore -v quiet
dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore -v quiet
dotnet test .tests/GeekFlashCore.Android.Lp.Tests/GeekFlashCore.Android.Lp.Tests.csproj -c Release -v quiet
dotnet test .tests/GeekFlashCore.Core.Tests/GeekFlashCore.Core.Tests.csproj -c Release -v quiet
dotnet test .tests/GeekFlashCore.Protocol.Mtk.Tests/GeekFlashCore.Protocol.Mtk.Tests.csproj -c Release -v quiet
dotnet build GeekFlashCore.slnx -c Release --no-restore -v quiet
git diff --check
git status --short --ignored
git ls-files .tests
```

交付范围：仅 CLI 生产代码、双语资源、docs/cli-qcom.md 与本任务两份方案/进度；测试不提交。代码与本记录一起提交，提交号以本轮 git log 为准；未合并或推送。

2026-10-06 LPCLI-06：用户补充实机 `read super/system_a system_a.img` 日志，每个 128 KiB 块都显示 100% 和零用时。确认 CLI 两处导出适配把 ProgressRecord(Total, Current) 参数传反，且未声明 Bytes，导致步骤进度每块自动结束。将进度发布集中到 BrowserSession，挂载 read/find 导出与未挂载路径 read 共用固定总大小、Bytes 单位和 Started/Running/Completed；最后一块不提前发 100%，AtomicReadOutput 完成 Flush 与目标文件替换后才发布 Completed。空文件同样有开始/完成；失败和取消保留原输出、清除临时文件，不发布完成。只改 CLI 三个文件，沿用已有双语资源、128 KiB 池化缓冲和同步设备读取，未更改公共协议、设备识别或线路。

LPCLI-06 验证：新行为首轮 4/4 在修复前失败；扩展后的目标 8/8 通过，覆盖未挂载 LP 分区读取、多块 Raw/Ext 文件导出、固定总量与单调进度、空文件、读失败、取消和最终文件替换失败，Completed 回调确认目标文件已保存。完整 Qcom（含浏览器）457/457、CLI 76/76，共 533 项通过；Release 解决方案构建 0 警告/0 错误。新版 EXE 的 1 MiB Raw 与 32 KiB LP 分区离线导出均退出 0，每次仅一个 100% 与最终统计，显示可读大小、平均速度和毫秒总用时。git diff --check 与 ignored/跟踪检查通过；测试、夹具和日志未提交。

LPCLI-06 设备证据与风险：用户已撤回“Qualcomm 被识别成 MTK”的判断，随后日志显示 Qualcomm 连接成功；本轮只读 WMI 与 CLI devices 也识别为 05C6:9008 / COM77，没有对识别流程做猜测性修改。本轮未连接或读写真实设备；进度修复的真机终端效果待用户复测。恢复入口仍为当前工作树 Release EXE，继续运行原 `read super/system_a system_a.img` 命令即可验证。

LPCLI-06 验证命令：

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~BrowserExportProgressTests|FullyQualifiedName~UnmountedLpReadReportsOneByteOperation' -v quiet
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore --no-build -v quiet
dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore -v quiet
dotnet build GeekFlashCore.slnx -c Release --no-restore -v quiet
git diff --check
git status --short --ignored
git ls-files .tests
```

## 主分支合并记录

2026-10-06 MERGE-LP-01：按用户授权，将功能分支的 `700b35d`（LP 读写/元数据/ls/帮助）与 `9e32ace`（连续字节导出进度）合入本地 main。合并前 main 为 `ebfe456`，工作区干净；主分支已有的 `72d55c0` 与 `ebfe456` 保留，CliApplication 自动合并无冲突，MTK Probe/WDT 完成后再提示 Loader 的创建顺序保持。合并提交标题为 `merge: integrate LP editing and export progress fixes`，最终 hash 以 git log 为准；未推送。

主工作区验证：LP/导出目标 37/37；完整 Qcom（含浏览器）457/457、CLI 88/88、MTK 389/389、LP 55/55、Core 9/9，合计 998 项通过，无失败/跳过。Release 解决方案构建 0 警告/0 错误，合并差异、资源中英键集合与 ignored/跟踪检查通过。仅将功能工作树缺失的 12 份 ignored 测试源码补充到主工作区，保留已有测试；本地 Qcom 测试工程改为 net10 并引用 CLI 以验证宿主集成，测试项目和产物不提交。

主工作区 EXE 离线冒烟：LP help 无设备退出 0；合成 Super 上完成分区导出、镜像写入、rename、attributes、info 和改名后导出，退出 0。导出字节与源/写入镜像精确一致，两次导出分别只有一次 100% 完成统计。未通信真实设备。恢复入口为 `D:/Code/CSharp/GeekFlashCore/src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe`；a803 功能工作树保持原提交与干净状态，后续真机 LP 读写风险见下。

合并复核命令使用上述 Qcom/CLI 测试，另运行主工作区 MTK 测试（`-c Release --no-restore`）、LP/Core 测试（`-c Release --no-restore --no-build`）、Release 解决方案构建、`git diff --cached --check`、资源键检查与 `git ls-files .tests`。

## 未决风险

真实 LP 写入/元数据提交、多源 LP 与设备取消尚未验证；普通分区写允许原位覆盖时不保证数据回滚，缓存文件分配检查不能代替 EDL 吞吐。LP resize 只修改元数据映射，不自动调整文件系统或 AVB。单文件浏览入口不解析 Sparse Super，也不提供外部 LP 数据源。恢复点为新版 `src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe`；下一步在有备份的硬件环境先执行 `ls super`、`lp info super`、`read super/system_a`，核对 LUN/slot 后再验证小范围写入。
