# XFlash 可选 SLA 查询兼容实施

2026-10-08 / SLA-COMPAT-01，起点 `c05b8bd`。[设计](2026-10-08-mtk-xflash-sla-compatibility-design.md)。仅标准查询兼容，无策略/载荷/签名回退。

## 实施事实

`XFlashSession.GetAuthenticationChallenge` 拆开可选SlaEnabledStatus查询，只允许子命令初始状态的完整UnsupportedCtrlCode（内部命名枚举0xC0010004），该拒绝不消费数据或尾ACK。父DeviceCtrl、结果帧、尾ACK、挑战命令、其他状态与IO/取消/超时仍严格失败。成功结果仅允许4字节0/1，零状态为NotRequired；启用仍须宿主签名。门面使用XFlash内部证据属性传播Unsupported，不再将null挑战一律当NotRequired。UI本地化Warning、常规认证证据摘要和Debug状态均不记录敏感材料。

参考Penumbra `handle_sla` 接受所有query错误，本实现仅接受明确不支持子命令的完整拒绝，保留安全性差异。参考protocol SHA256 `CB17E5926F4D0483DC7DABA44792AAD23BD73DA6C02A316783EE518E3AC84E23`、error.rs `6EEF6D4F0FEB3C31F53B2DD734DFA0C7B28A48E2BA9B4E88644324D6BB555104`；无Git revision。

## 验证

ignored `XFlashSlaCompatibilityTests` 首次15项3失败/12通过，失败均为本次应兼容的拒绝与日志证据；最终25/25。覆盖同步异步、无额外ACK/挑战/签名、父/子/尾/结果位置错误与安全错误不降级、非法长度/0/1/2、启用后标准签名与清零、取消/IO/超时、重连清证据、后续坏包长仍失败、日志脱敏。共用模拟FeedConnect新增可选SLA响应，默认旧夹具线路不变；ignored测试不提交。

| 检查 | 结果 |
| --- | --- |
| 目标 | 25/25 |
| MTK可运行回归 | 681/681（复跑） |
| MTK完整TRX `xflash-sla-compatibility-full.trx` | 696项，681通过/15既有失败，失败名称与c05b8bd前阶段完全相同 |
| CLI Release / solution Release / CLI Debug | 168/168、两构建0警告/0错误 |
| 中英资源键及参数 / diff / ignored | 91对应、通过；测试/抓包/日志不跟踪 |

第一次可运行回归中既有50ms计时 `FragmentsShareOneReadBudgetAndStopWithoutFurtherCommands` 因调度偶发失败；单独复跑及全量/可运行全量均通过，未改夹具/生产握手路径。既有14 Linecode失败及缺oppo DA继续保留，不归入本次修复。旧ignored Carbonara字段CS0649警告保持，生产构建无警告。

## 用户实机更新与恢复

用户232837日志及新抓包确认SlaEnabledStatus完整UnsupportedCtrlCode后进入GetPacketLength，未多读ACK、未请求挑战；这是可选查询兼容的实机证据，不是认证成功。随后包长查询全部ACK零且结果8字节，抓包369.1.0为写/读各0x00200000（2MiB）；现有1MiB包长校验拒绝，应单独修复能力上限与宿主限制的混淆，不是SLA拒绝或USB超时。

后续从包长兼容任务继续；仍须验证存储及大型读取线路，不自动重发任何操作。提交查询 `git log --oneline --grep='unsupported XFlash SLA'`。
