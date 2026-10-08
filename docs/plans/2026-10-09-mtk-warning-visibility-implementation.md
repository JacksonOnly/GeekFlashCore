# MTK 工具输出期间的警告可见性

2026-10-09 / MTK-UI-WARN-01，起点 `f944e3d`。用户已要求MTK UI/详细日志补齐；000226的GPT发现摘要存在于文件但未显示于终端，确认CLI StorageCommands/BrowserCommands统一SuppressDiagnosticLogs压制所致。

最小边界：ConsoleLogSink在工具输出压制期间，只允许MtkSummary=true且Warning/Error等级的无异常、非DeviceDiagnostic/非UserPresentation事件；Info/Debug仍压制，普通Qcom/SPRD等输出策略不变。连接阶段原策略保持，不新增日志材料/资源键，不改变命令、I/O、进度或错误恢复。用于展示已验证GPT兼容/备用表选择等一次性重要警告，不能放开高频raw读取摘要。

文件为CLI ConsoleLogSink、诊断/当前进度记录；测试先定义工具压制期间MTK警告保留/信息与其他协议仍隐藏、敏感标记及异常不显示，再最小修改。目标和CLI全量、MTK前项完整基线、Release/独立Debug、diff/ignored完成后独立提交。用户运行进程占用默认Debug DLL时不杀进程，使用ignored OutDir验证，用户exit后正常重建。

测试先2个新增失败/10旧项通过；最小过滤修改后全部CLI170/170（含目标12项）通过。Release slnx、独立ignored OutDir Debug均0警告/0错误，diff通过；MTK前项最终807项792通过/15固定旧失败，可运行792/792，协议核心此步未改。没有新增资源键，测试/捕获/产物未跟踪。工作区用户侧persist.img原样保留，不提交。

恢复：用户当前CLI进程保持运行，不强制替换其已加载DLL；操作结束exit后正常dotnet run --no-restore加载新警告过滤与GPT修复。Info摘要仍会被工具模式压制，只留文件完整诊断，Warning/Error摘要可显示，敏感/异常/重复标签始终拦截。真实设备新版本完整枚举/读写仍待用户证据。提交查询git log --oneline --grep='preserve MTK warnings during tool output'。
