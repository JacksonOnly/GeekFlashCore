# MTK DA1 修改状态 / DA2 回调实施记录

日期：2026-10-08。任务 DA2-CB-01，起点 `831c79c`。最新授权和方案见 [设计](2026-10-08-mtk-da2-callback-design.md)。仅修复既有回调路由，不新增策略、算法或载荷。

## 结论与实现

参考 Penumbra `core/src/macros.rs` 的跨阶段 `patched` 门控。现有 LineCode 的 BeforeDa1 Completed 被当前框架当作“只结束该检查点”，随后误调用 Carbonara。其额外 32 字节 BootTo 后，用户 225138 日志/抓包显示标准 DA2 初始 ACK 超时，实际 DA2 尚未发送。不能归因于 DA2 大块上传。

- `MtkExploitContext.Da1ModifiedBeforeUpload` 默认 false，不改旧构造和结果契约。核心在接受 BeforeDa1 replacement 时，用两个固定池化窗口比较 DA1 非签名字节；只在相同执行布局的实际字节修改时记录。原 source 和 replacement 都保持借用，读流全部释放，检查取消和连接预算。
- 状态在 DA1 上传校验/跳转及初始化完成后的检查点才开放；BeforeDa1 总为 false。Fault/Disconnect/Reboot/Dispose/新资源准备清除；过期上下文新属性也失效。不改变 DAA/SLA 安全快照或认证证据。
- Carbonara 在 Da1Ready + 初始 BROM + 已修改并上传 DA1 时，先于资源/通道访问返回 NotApplicable，并输出摘要。Preloader、未修改 DA1、仅 DA2/签名/元数据变化、普通 Completed 和宿主直接提供的预修改文件不猜测跳过；其他策略注册/顺序不变。
- 标准 XFlash DA2 保留命令 ACK → 参数/载荷 → 参数组 ACK → 执行状态 0/SYNC。新增请求/命令接受/载荷已发/执行确认诊断，地址/长度仅元数据，不记录源字节或私密摘要。回调异常把实际类型传给原 Fault 日志，不记录私有消息。

生产文件：MTK Abstractions `IMtkExploitStrategy.cs`，MTK `MtkProtocol.cs` / `.Exploits.cs`、`CarbonaraExploitStrategy.cs`、`XFlashSession.cs` 和中英资源。算法/补丁 helper/资源/CLI 注册/其他协议均未修改。

## 测试及验证

ignored `Da1PatchRoutingTests` 初始 13 项先失败，包含已修改 DA1 仍进入 Carbonara 的完整模拟线路和新状态契约。最终 26 项通过：实际既有 Unfused 离线夹具修改后标准 DA2 仅一次 BootTo、正确 DA2 内容/地址/长度、修改与非修改分类、同步/异步、Preloader 保持原路由、普通 Completed 不免除 DAA、上传校验失败、双 ACK 错误、重连清除/上下文过期、取消/IO/迟到源/流释放、512-byte 窗口跨 64KiB 比较、日志脱敏。未操作真实设备；先前用于其他 ACK 方案的自建测试已撤销，未改用户既有失败测试。

| 检查 | 结果 |
| --- | --- |
| 目标测试 | 26/26 |
| 可运行 MTK 全回归 | 656/656 |
| 原始 MTK 全量（ignored TRX `da1-patch-routing-full.trx`） | 671项，656通过、15既有失败、0跳过；与 `preloader-emi-final.trx` 失败名称完全相同 |
| CLI Release | 168/168 |
| solution Release / CLI Debug | 均0警告/0错误 |
| 中英键及完整模板参数 | 90对应 |
| git diff --check / ignored | 通过，测试/ROM/抓包/日志/bin/obj不跟踪 |

既有15失败为14项 Linecode 夹具及缺失 oppo DA 文件，与本任务无关；既有 ignored Carbonara 夹具有 CS0649 未赋值字段警告，生产构建无警告。参考 macros SHA256 `1100C6949EC249F60DE6EEF3CF4FAFE59969928BED5005DC40A721AF0A718522`；参考目录无 Git revision，按本地源指纹识别。

## 用户新增实机证据 / 后续问题

用户 231901 日志已确认：448-byte EMI 成功、跳过 Carbonara、标准 DA2 地址0x40000000/431132字节传输、参数组0和执行SYNC均通过。此为本次路由及标准 DA2 的实机证据，不是认证或存储证据。

随后新失败为 DA2 `SlaEnabledStatus` 0x40016 的命令拒绝0xC0010004；此标准查询兼容问题单独审查/提交，不能再称 DA2 上传失败。未修改 Carbonara helper 的单ACK行为仍是其他路由的独立待审风险。Preloader2000/6000仍缺实机证据。借用源须稳定，native取消仍受驱动约束。恢复入口为本记录及新 SLA 查询兼容记录；提交可由 `git log --oneline --grep='skip redundant Carbonara'` 查询。
