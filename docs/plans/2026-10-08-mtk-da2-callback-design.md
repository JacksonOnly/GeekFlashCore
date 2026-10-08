# MTK 已修改 DA1 / DA2 BootTo 回调路由修复

日期2026-10-08，任务 DA2-CB-01。起点 `831c79c`。用户确认允许修复回调衔接，不新增策略或载荷，进一步明确：BROM 接入且 DA1 已补丁后无需再执行 Carbonara，应直接标准 BootTo 并发送 DA2。本版替代先前拟修改回调 BootTo ACK 的方案；本任务不修改该 helper、漏洞算法、地址定位、补丁规则、资源或默认注册。

## 证据与边界

用户225138日志及抓包378～389：EMI448成功后，DA1Ready回调发送一次BootTo/16字节参数/32字节材料，只读取一次零状态就返回Completed；随后标准DA2只发送BootTo命令，初始ACK超时。实际DA2地址/载荷尚未发送，不能称作DA2块传输失败。

补充日志证据：BeforeDa1 策略 2/4（LineCode）已 Completed，标准 DA1 上传校验成功，随后 Da1Ready 策略 3/4（Carbonara）仍执行。Penumbra `core/src/macros.rs` 的 `exploit!` 在协议的 `patched` 为 true 时不再调用后续策略；XFlash `protocol.rs` 在 DA1 上传前调用 Unfused/Linecode，初始化后才调用 Carbonara。当前 C# Completed 只停止同一检查点，没有跨检查点的已修改 DA1 状态。

目标：在 Completed 的 BeforeDa1 替换资源被接受时，以固定池化窗口比较原 DA1 与新 DA1 的非签名字节，记录是否确实发生修改；在 DA1 上传校验/跳转与 DA1 初始化均成功后的检查点暴露该状态。Carbonara 仅在初始 BROM 且已上传修改 DA1 时，在资源读取或设备通道访问之前返回 NotApplicable。标准 DA2 顺序维持：BootTo 命令 ACK → 16 字节地址/长度 → DA2 FLOW 流 → 参数组零状态 → 执行状态 0/SYNC。不调用多余的 32 字节 BootTo，不用 sleep、重试或延长超时。

状态只证明被接受的宿主替换改变了 DA1 非签名字节且已完成标准上传流程，不证明认证成功、补丁语义正确或任意硬件状态。比较要求 DA1 执行布局不变（允许源文件偏移变化）；相同 DA1、仅 DA2/签名/元数据变化、未完成替换和普通 Completed 均不得跳过；Preloader 保留原路由。用户直接提供的预修改 DA 不凭文件名/模式猜测。无需新增结果构造参数或修改现有策略算法。

## 最小实现与兼容

- 抽象上下文增加默认 false 的虚属性 Da1ModifiedBeforeUpload，旧宿主上下文可继续编译。结果构造、策略注册与列表顺序不变；只在 Carbonara 应用新的跳过条件，不全局跳过其他策略。
- 核心在 BeforeDa1 比较 DA1，固定双窗口池化缓冲，检查总预算和取消；源仍借用且要求稳定可重开。BeforeDa1 上下文不暴露未上传状态。Fault、断开、重启、Dispose 和新资源准备清除状态；回调到期后新属性同样不可访问。
- 核心回调catch将实际异常类型传入既有安全Fault日志，不记录异常私密消息。
- 标准XFlash DA2摘要区分请求BootTo/初始ACK通过/载荷已发送等待确认/执行已确认，避免把命令ACK等待误称已上传；地址和有界长度允许日志，载荷与摘要材料禁止日志。
- 公共API、构造、认证顺序、会话gate/代数、资源所有权和所有失败不重试保持。

## 测试与提交

ignored 测试先复现 BROM 的已修改 DA1 仍进入 Carbonara，再验证实际核心上传状态、标准 DA2 恰好一次 BootTo、无摘要 BootTo、无变化/仅 DA2 变化/签名变化、Preloader 保持原行为、DA1 校验失败不进入回调、状态重连清除、回调过期、取消/源失败、DA2 参数和末 ACK 错误传播。仅合成 DA 和模拟 USB，无物理设备操作，不生成生产载荷。

目标测试、MTK可运行全回归/原15失败基线对照、CLI全量、Release solution/CLI Debug构建、资源中英对应、完整diff/git diff --check与ignored。一个独立英文 fix 提交，结果与风险记录对应 implementation。EMI448 初始化成功已由用户新日志证实；新的 DA2 路由需用户重新测试。未修改的 Carbonara BootTo helper 单 ACK 行为是其他未修改路由的独立待审风险，不归入本次已修复范围。
