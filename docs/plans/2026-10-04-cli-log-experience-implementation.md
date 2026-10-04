# LOG-01 实施记录

日期：2026-10-04；分支：codex/qcom-legacy-audit。

## 范围与结论

- 用户提供 bug1.txt，后续明确最后一次终止是人为取消，Legacy 的 Hash 诊断与 ACK 后继续发送必须保留。初步提出的停止处理已撤销，未修改任何正在使用的协议校验、计数、超时或线路顺序。
- 默认 CLI 输出由宿主控制：简短阶段、中文设备信息、资源输入、进度、失败提示和日志路径；完整异常只写文件。Verbose 仍保留额外诊断，但设备文本和异常堆栈不进控制台，StorageCommands 在执行期间抑制日志并在 finally 恢复。
- Sahara 身份继续保留完整 PkHash、64 位序列号、HEX 原始 ID、厂商和芯片；模式、安全启动及标签本地化。识别阶段与 Firehose 联机完成分开显示。已确认完成的 Loader 进度显示 100% 和实际发送数量。
- Loader/Digest/Sign/VIP 复用文件选择循环；错误路径直接重选，留空取消必需资源，非交互缺失立即失败。Legacy 必须手动或参数提供 Sign，Pt 的自动 Sign 与失败后替换流程保留；输入无额外时限。
- 文件增加结构化设备来源、原始级别与文本长度，存储操作记录目标、扇区范围和文件。Debug 有界批量刷新，Information/Error 立即刷新，16 MiB 滚动和 Dispose 刷新保留。静默时最多剩余一批 Debug 未刷；没有后台线程或异步串口。

## 清理审查

- 删除确认零调用的内部 FirehoseCmdSender.SendCommand、FirehoseSession.ExecuteLegacyNop、CLI FirehoseCommands.Handles；公共契约不删除。
- 删除每包头/包体重复 Sahara 接收日志 19 处，以及 Firehose XML 的重复发送日志。保留包头事件、命令名、长度和响应预算。
- 删除未使用的 Serilog.Sinks.Console 包引用及 4 对已无引用资源键；复用字节单位格式化，缓存控制台消息格式化器，收敛设备日志级别分发。
- 未发现需要在本轮删除的协议兼容注释；保留取消边界、资源所有权、NOP/Digest 顺序、稀疏流式窗口、原子输出和敏感数据清零说明。没有按风格猜测删除公共类型或厂商状态分支。

## 验证证据

- 先新增日志/输入/进度/信息展示测试，4 项确认失败后修复通过；再补充 verbose 写入静默、错误输出一次与文件堆栈、Legacy Hash 诊断加 ACK 继续分段测试。
- 完整当前工作区 Qcom/CLI 本地测试：204/204 通过，16 秒；包括新增 7 项及现有 bootstrap、Pt、Legacy 换表、输入取消、资源释放、USB 等测试。旧身份展示断言随文案更新，HEX 与完整 Hash 值断言保留。
- Release 解决方案构建：0 警告、0 错误。中英文 CLI/Qcom 资源键一致、无重复；git diff --check 通过；.tests、日志、bin/obj、temp ignored 且未被跟踪。
- 生产差异确认只有展示/日志、文件选择和无调用内部方法清理；真实设备 I/O 未执行。用户已授权提交并合并本地主分支，不推送远端。
- 合并后主工作区 Release 构建（含依赖还原）：0 警告、0 错误；CLI help 冒烟通过。主工作区 Qualcomm 251/251、CLI 55/55、Android Lp 55/55、Core 9/9 全部通过，未跳过测试。
- 主工作区历史测试首次运行时，CLI 40/55 失败、Qualcomm 11/250 失败；核实为早期反射构造方式、显示断言、超时异常类型、Legacy 计数及预读夹具失配。仅在 ignored .tests 迁移夹具与断言：保留 HEX/Hash、迟到资源释放、短预算失效、输出不截断、NOP 顺序、XML 重放一次、RAW NAK 后失效等验证；补充 API 零分段参数有效的断言。生产代码没有为旧测试改变线路；Legacy Hash 诊断加 ACK 继续发送仍由当前工作区回归覆盖。

## 合并与风险

- 生产提交：0312508（refactor(cli): streamline logs and oplus resource selection）。本地 main 位于 D:/Code/CSharp/GeekFlashCore，已从 37ee307 快进合并到该提交；功能分支 codex/qcom-legacy-audit 保留。两个工作区跟踪文件均干净，未推送远端；本记录补记后再快进同步文档。
- 主工作区及 C:/Users/a1375/.codex/worktrees/93f8/GeekFlashCore 的 src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe 均已更新；日志默认位于各自程序目录 logs 下，可用 --log-file 指定路径。
- 真实 Loader 延迟、正确资源组合及终端显示需要用户复测；bug1.txt 中 Legacy Hash 诊断不作为缺陷处理。其他 UI 宿主可按 DeviceDiagnostic 和级别筛选，当前仓库没有单独桌面 UI 工程。
