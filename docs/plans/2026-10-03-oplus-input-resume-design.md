# Oplus 手动输入与等待 Digest 续接

日期：2026-10-03。依据：用户要求取消手动输入时限并修复 Loader 上传后重新启动 CLI 无法连接。

## 目标与兼容性

- CLI 可交互资源请求默认无限等待，Ctrl+C 和协议生命周期取消仍生效；脚本默认维持 15000 ms，显式资源预算仍可指定。同步读写超时保持有界，Core 默认不变。
- 新增显式 `--oplus-resume`，调用方声明 Loader 已运行且正在等待第一张 Digest。跳过 Sahara 和启动日志，直接走原有 Digest → Verify → Sign → sha256init → Configure；Pt/Legacy、同步/异步保持一致。
- 普通 Oplus 静默探测不得发送 NOP、Sahara reset 或猜测设备状态。续接不适用于 RAW 中断、已认证会话或未知状态；这些状态需要重新进入 EDL。

## 证据和状态

第一份本机日志在 VIP 等待表提示之后结束，未发送 Digest；代码确认 Console Provider 受默认 15 秒资源预算控制，InteractiveAsync 漏记取消。第二份日志 1500 ms 收到 0 字节。符合 Loader 不重播启动日志的状态，但无新的抓包证明设备实际仍在等待表。

资源仍由 QcomProtocol 获取；同步协议层不等待输入。允许资源预算 -1，并保留迟到响应观察/释放。显式资源到期改为 QcomResourceException，调用方/生命周期取消保留 OperationCanceledException。显式续接创建新的 Firehose Started 会话，每个协议实例只消费一次声明，后续失败重连不得再次盲发 Digest；旧句柄不能复用，不保留已认证状态。Sign 在 Digest 前校验和获取，敏感数据不写日志。

## 范围与验证

- CLI options/parser/adapter/取消日志/双语帮助；Core options/resolver/连接编排；不修改 Rector 换表线路。
- ignored 测试先行：交互/脚本默认、非法超时、无限等待取消和迟到资源、明确资源超时、静默续接同步/异步/Pt/Legacy、首包 Digest、正常静默连接不写任何数据、续接配置冲突、失败不 Configure。
- 完整本地 Release 测试、解决方案 Release 构建、资源键一致、help 冒烟、diff/ignored 检查；英文小步提交。
- 未决风险：显式续接依赖调用方准确知道设备正在等待 Digest；没有硬件验证，不能保证任意中断恢复。无限等待期间设备自身可能超时。
