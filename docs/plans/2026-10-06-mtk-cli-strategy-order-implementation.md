# MTK CLI 策略接入与顺序实施记录

日期：2026-10-06。任务：HOST-ORDER-01。起点 main / `2764589`，启动工作区干净。设计见 [有序阶段调用设计](2026-10-06-mtk-cli-strategy-order-design.md)。

## 行为与范围

用户 21:51 日志明确显示 BeforeDa1 到达后 NoStrategy，随后 DAA 缺失。用户要求真实调用占位类，并明确选择“只继续下一策略，之后仍校验 DAA 材料”。本轮将 CLI 显式接入现有四项，并实现通用有序调度；没有实现 EXP、patch、地址/特征定位、攻击载荷、认证绕过或资源下载。

- CLI 每个会话创建 Unfused、LineCode、Carbonara、HeapBait 并按此顺序注入；不增加 CLI 参数或全局状态。Probe 和诊断命令不执行策略。
- 核心新增可选 `exploitStrategies`（最多64项），与原单项参数互斥；构造时复制集合、校验并捕获 descriptor 一次。空集合等同未注入。原构造和 CreateUsb 二进制签名通过转发重载保留；旧源码及单项行为不变。
- 每项按现有阶段、初始启动模式、DA 方言筛选。XFlash/BROM 的 BeforeDa1 调用 Unfused → LineCode，XFlash/Preloader 只调用 Unfused；XML BeforeDa1 调用 Unfused。Da1Ready 调用 Carbonara，XML Da2Ready 调用 HeapBait，Legacy 不匹配这些占位类。
- NotApplicable 继续下一匹配项；Completed 校验并应用原有结果，然后结束当前阶段的尝试，后续阶段仍独立筛选。Failed/ReconnectRequired、null/非法结果、异常、取消、超时不继续下一项；仍失效、关闭并要求重连。
- 每次回调拥有独立 context 和通道，在返回后立即失效。共用原 gate、会话代数和有限连接预算，原 DA 替换、已上传区域校验与标准 D8 安全复查规则不变。
- PrepareBootResources 保持元数据/内容边界校验 → 实际 BeforeDa1 策略序列 → 更新 DA 引用 → DAA/certificate 校验。全部 NotApplicable 后缺材料仍抛出资源异常，不发送 D7/E0/E2/E3，不把结果解释为认证成功。
- 日志增加 Debug 阶段/集合序号（1～4），现有 Information 结果每次实际回调记录一次；不输出任意宿主 Id、私有文本或敏感资源。四个 Execute 没有修改，也不调用 Dependencies。

## 测试先行与证据

1. 新增 ignored OrderedStrategyTests，初次因缺少 exploitStrategies 参数而编译失败 CS1739。初始32项实现后通过，扩大到43项覆盖取消/超时、null结果、0/64/65项边界、Preloader 路由与旧二进制入口。
2. CLI 夹具修正显式 `--protocol mtk` 后，用未注入的原 factory 复验：4项全部失败，BeforeDa1 实际调用数预期2、实际0。恢复新的显式注入后全部通过；覆盖交互/非交互、同步/异步。旧12项 WDT/DA 选择准备继续通过。
3. 使用用户提供的 `MTK_AllInOne_DA.bin` 进行离线资源选择，配合模拟 MT6893/HW0950、安全 E7 的响应，确认当前自动选择也调用两项 BeforeDa1 占位策略。这个证据来自真实 DA 文件与模拟 USB，不代表真机执行。
4. 回调观察器只记录顺序/返回预置结果，不做设备操作。逐项失效测试在后一项中验证前一项 context、USB、BROM/DA 通道已拒绝访问；三种 DA 的占位注入连接与默认连接的 USB 写入字节一致。Completed 仅停止本阶段后续项；终止结果无降级。
5. 旧构造/USB factory 精确签名的兼容测试先失败，补充转发重载后通过，旧构造实际单项调用一次。核心未注入的 NoStrategy 与单项认证顺序测试继续通过。

| 2026-10-06 验证命令 / 检查 | 结果 |
| --- | --- |
| MTK OrderedStrategyTests Release/no-restore | 43通过，0失败/0跳过 |
| CLI MtkLoaderPreparationTests Release/no-restore | 17通过（新增5，既有12） |
| `dotnet test .tests/GeekFlashCore.Protocol.Mtk.Tests/GeekFlashCore.Protocol.Mtk.Tests.csproj -c Release --no-restore --verbosity quiet` | 464通过，0失败/0跳过 |
| `dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore --verbosity quiet` | 96通过，0失败/0跳过 |
| Qcom / Core / Android Lp 对应 Release tests | 457 / 9 / 55通过，0失败/0跳过 |
| 五工程合计 | **1081通过** |
| `dotnet build GeekFlashCore.slnx -c Release --no-restore --verbosity quiet` | 0警告/0错误 |
| MTK / CLI 中英资源 | 17 / 253 keys 成对，格式占位符对应 |
| `git diff --check`、完整差异与 ignored 检查 | 通过；测试、用户 DA 文件、日志和产物未提交 |
| 真机策略调用 / DA 上传 / 认证 | 未执行 |

所有本地测试在 ignored `.tests`。首次并行构建两个测试工程曾遇到共享 obj 文件占用，改为串行构建/测试后通过；不是生产行为故障。最终兼容入口修订后重跑 MTK/CLI 全量和 Release；其它三个工程的结果来自同轮回归，未修改对应实现。

## 提交、恢复与风险

独立提交 `fix(mtk): invoke CLI placeholder strategies before authentication`，hash 通过 `git log` 查询。同步更新 AGENTS、README、框架、占位类历史记录与 MTK 总实施记录；本轮不 push/发布。

当前修复版位置：`D:/Code/CSharp/GeekFlashCore/src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe`。使用原命令复测时，XFlash/BROM 日志应显示 BeforeDa1 正在调用 1/4（Unfused）、NotApplicable、2/4（LineCode）、NotApplicable，然后才显示 DAA 缺失。它仍可能以相同资源异常结束，但现在已经实际调用了适用占位策略。

未决风险：没有修订后真实设备日志；四项只有接口占位实现，无法解除 DAA；同步宿主代码仍需合作取消，无法强制中断；实际宿主若返回 Completed，不自动跳过后续认证或认定安全状态变化。下一次从此记录和新设备日志确认调用顺序，不扩展到具体 EXP。
