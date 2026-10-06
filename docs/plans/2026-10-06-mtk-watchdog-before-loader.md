# MTK 交互 DA 选择前关闭看门狗

日期：2026-10-06。任务：WDT-CLI-01。起点：main / 72d55c0，工作区干净。

## 问题与设计

用户从 a803 工作树的 Release CLI 报告：热插拔后出现 DA 路径提示，还未输入设备就断开。其 21:01:35 日志只有输入等待和取消，没有 BROM 阶段日志；不能凭此确认设备断开的具体原因。代码确认 REC-02 把交互 DA 选择安排在 native USB Open / Probe 之前，导致已经存在的 FD 后标准 WDT 初始化无法在输入等待前执行。

目标：需要 DA 的 MTK 交互连接先获得 USB、创建协议、Probe（握手 → FD → 已知 WDT 写入并确认 → 安全/版本查询），显示目标与实际 WDT 状态，再异步等待 DA 路径。选择后沿用同一个 Probed 协议实例进入 ConnectAsync，缓存 Probe/WDT 不重复写入；DA 仍在核心请求资源时按真实目标解析。此顺序替代 REC 文档中“交互选择必须早于 USB Open”的旧约定。

非目标：不修改核心同步协议、芯片寄存器配置、认证/DA 线路、驱动安装、热插拔匹配或漏洞框架。未知 WDT 配置仍明确显示 ProfileUnavailable，不能猜测寄存器或宣称已经关闭。Qcom 与只诊断的 MTK 命令保持现有行为；非交互/无法提示时缺 DA 仍在获得 USB 前报错。

CLI 宿主负责 Probe 和异步选择编排，协议不会阻塞等待异步输入。选择仍使用有限 ResourceTimeout（默认 30000 ms），与后续 ConnectTimeout 分开。选择取消、留空、超时或 Probe 失败时释放协议和宿主持有的 transport，不重新握手或自动重试已开始的通信；USB 尚未启动协议的 NoDevice 重试边界保持。成功返回的协议和 transport 由现有连接生命周期持有。已配置 DA 的交互连接也遵循先 Probe 再验证文件，避免无效文件引起的修正提示提前阻塞。

## 步骤与文件

1. ignored CLI 测试先复现准备选项提前读输入，并定义模拟 USB 到输入回调的顺序、同一会话续接、WDT NAK、取消/超时与释放。
2. 修改 CliApplication 的协议创建/准备边界、MtkProtocolHostAdapter 的可等待准备入口；TransportResolver 更新准备回调注释。不新增公开 API、NuGet 或资源键。
3. CLI 与 MTK 全量回归、Release solution 构建、diff/资源/ignored 审查；同步本文与 MTK 实施记录。独立提交 `fix(cli): disable MTK watchdog before loader prompt`，测试不提交。

验证命令：CLI 和 MTK 对应 `.tests` 工程的 `dotnet test -c Release --no-restore`，`dotnet build GeekFlashCore.slnx -c Release --no-restore`，`git diff --check`、`git status --short --ignored`。

## 进度与风险

- WDT-CLI-01A：确认上述调用顺序，开始测试先行。用户运行的 a803 工作树为 700b35d，主工作区为 72d55c0；本次在主工作区修复，不覆盖其他工作树已有提交或文件。
- WDT-CLI-01B：新增 ignored MtkLoaderPreparationTests，测试夹具编译修正后、生产修复前 11 失败/1 通过。三个预准备案例在 DA 等待中取消，复现过早读取输入；其他顺序/失败案例定义新的可等待创建边界。修复后 12 项全部通过。USB 夹具证明 WDT 标准值 0x22000064 已 echo 并成功 status 后才进入输入；等待期保持打开且不上传 DA。提供最小 DA 容器后成功按目标解析，到 D7 边界按夹具停止，FD/WDT/Open 均仅一次。取消、超时、留空与 WDT NAK 释放借用 transport，NAK 不进入安全查询或输入。诊断命令不提前 Probe/提示，非交互缺文件在预验证失败，预取消没有写入。
- WDT-CLI-01C：CLI 全量 88、MTK 全量 389、Qcom 全量 294，合计 771 项通过，0 失败/跳过；Release solution 构建 0 警告/0 错误。完整差异/空白检查通过，生产层未添加日志或资源键。测试与 bin/obj 维持 ignored，不提交。
- 尚无修复后的真实硬件证据；普通 WDT 写入被设备 ACK 不保证所有厂商的重枚举/断连问题均已消除。驱动/UAC 之前的设备存活时间仍取决于系统与硬件。

## 交付与恢复

修复版已在主项目构建：`D:/Code/CSharp/GeekFlashCore/src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe`。a803 原目录没有合入本次修复；应运行主项目构建，或将本修复提交合入相应工作树后重新构建。独立提交标题见上，hash 通过 git log 查询；未推送/发布，没有执行任何真实设备 I/O。

恢复时先检查当前提交与本文；真机日志应在 DA 提示前出现 Handshaking/Probed、芯片快照与“看门狗已通过标准寄存器写入关闭”。若实际显示配置不可用或 WDT 状态失败，应按真实芯片/状态补证，不能把所有等待期间离线都视为同一个原因。DA 选择仍默认限时 30 秒，可显式设置正值 `--resource-timeout`。
