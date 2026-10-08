# XFlash 旧版 DA SLA 状态查询兼容

日期2026-10-08，任务 SLA-COMPAT-01，起点 `c05b8bd`。用户231901日志已确认DA2双ACK成功，随后0x40016返回0xC0010004。仅MTK标准查询/证据/日志，不新增策略、载荷、签名回退或认证绕过。

## 参考与边界

Penumbra `core/src/da/xflash/protocol.rs` 的 `handle_sla` 在 `devctrl(SlaEnabledStatus)` 返回错误时直接结束SLA阶段，再查询包长。`core/src/error.rs` 明确0xC0010004为UnsupportedCtrlCode。用户实际响应是DeviceCtrl父命令零ACK之后、SlaEnabledStatus子命令的完整4字节拒绝状态。该拒绝没有结果数据帧或尾ACK，必须立即进入后续包长查询，不能多读导致错位。

保留比参考更严格的安全差异：只允许SlaEnabledStatus子命令初始ACK的完整UnsupportedCtrlCode；父DeviceCtrl不支持、其他错误、结果帧/尾ACK报错、坏帧/短帧/IO/超时/取消不降级。查询成功仍完整消费结果与零尾ACK，状态仅0/1；启用后挑战/签名错误终止，不吞认证失败。

## 最小改动

- 内部命名XFlash状态枚举定义Success及UnsupportedCtrlCode，不散落状态常量。GetAuthenticationChallenge只为此可选查询显式接受该命令拒绝，不改变通用Command/Control/ReadStatus的严格行为。
- XFlash内部AuthenticationState区分NotQueried/NotRequired/Unsupported，门面沿现有证据日志/上下文传播；Unsupported不等同Authenticated或SLA禁用。未知/错误状态不得更新证据为NotRequired。
- 可恢复不支持用本地化Warning摘要输出UI，Debug继续帧/状态/预算；不记录挑战或签名。之后保持GetPacketLength → 已有后续检查点 → GetStorage顺序。
- 同步/异步共用同一查询实现，资源/会话gate/代数/取消边界保持，不延长超时、不重试、无全局状态。

## 测试与交付

先写ignored模拟USB测试复现本次拒绝；验证同步/异步正常继续、Unsupported证据与包長/存储顺序、不读额外ACK、不请求挑战/签名、不把其他位置/状态误判不支持、0/1/非法结果、坏帧/超时/取消、已有启用SLA线路及敏感日志。目标/可运行MTK/15基线失败对照、CLI、Release solution/Debug CLI、资源中英一致、diff/ignored检查；独立fix提交和对应implementation记录。仅用户下一次实机日志能证明此兼容DA接下来包长/存储阶段行为。
