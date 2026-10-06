# MTK 首次 USB 读取停滞恢复与检查点诊断设计

日期：2026-10-06。任务：STALL-01、CP-LOG-01。起点 main / 08743d6，工作区干净。

## 证据与边界

用户 21:23:44 日志显示 Handshaking 后，BROM 读取应答在 libusb bulk Read 返回 Pipe；没有 FD/芯片证据，也没有握手步骤序号，无法从旧日志确认第几个字节。增加 Debug 步骤序号以便后续确认。21:24:07 日志确认 HW=0950、WDT Disabled，DA 选择后异常在 PrepareBootResources 第 27 行。源码第 25 行先调用 BeforeDa1，第 27 行才校验 DAA，现有 CompletedDoesNotSuppressDeviceDaaRequirement 也断言回调一次。所以第二项不是认证校验越过了检查点，而是 CLI 未注入策略，RunExploitCheckpoint 直接返回且没有日志。

目标一：为 MTK CLI 显式启用新 LibUsbConnectionOptions.RecoverInitialReadStall（默认 false）。仅一次 Open 后首次非空 bulk 读取、长度为 1、有限 timeout、Pipe 且 transferred=0 时允许一次 ClearHalt(IN)，在剩余同一读预算中续读一次；不重发 A0，不重新握手、reset、枚举或重试写入。其它读取入口/Flush 消耗首次资格；后续 Pipe、部分读取、超时、NoDevice、ClearHalt 失败都沿原失败线路。Qcom/旧 factory 默认不启用，不对 DA/认证/存储状态做恢复。CLI 对残余 Pipe 明确显示端点停滞与重连提示。

目标二：Debug 记录每次到达的宿主检查点、无策略/descriptor 不匹配的跳过理由，已有执行结果 Information 保留。同步/异步连接、缺 DAA/cert 和显式观察器验证同一顺序；维持默认无策略、四类不执行、缺材料继续拒绝。没有注册默认策略或改变认证条件。

## 契约、所有权与性能

只增加稳定 bool 选项，不暴露第三方类型到公共接口。LibUsb 后端持有串行 lock，ClearHalt 仅在上一次同步 transfer 已结束后执行。内部可测试恢复状态机接受 Span 读取委托，固定一次资格、最多两次 read、一次 halt；委托只在首次读取构造，不进入逐包热路径。空读取不消耗资格，Close/reopen 清除/重建。有限控制超时沿现有验证；native ClearHalt 为阻塞调用，返回后拒绝超期，不承诺强制中断底层调用。已开始会话发生最终异常照常关闭/失效，句柄所有权不改变。

不记录 DA 内容、auth/cert、challenge 或异常私有策略文本。新 CLI 与 MTK 文本资源中英成对；跳过理由仅固定标识 NoStrategy / DescriptorMismatch。

## 文件、步骤与验证

1. ignored MTK 恢复夹具定义成功恢复、默认禁用、资格耗尽、长度/部分传输、重复 Pipe、clear 失败/超时、预算递减；日志/观察器夹具先证明认证顺序，再复现缺失诊断。ignored CLI 夹具复现 Pipe 原因被隐藏。
2. Transport.LibUsb 选项/内部恢复/Read 路由，CLI MTK factory 开关与 ConsoleUi/中英文本。独立提交 `fix(usb): recover initial MTK read endpoint stalls`。
3. MTK 握手步骤、检查点诊断/中英文本及框架恢复记录。独立提交 `fix(mtk): clarify handshake and host checkpoint diagnostics`。同步更新 MTK 实施记录、设计对应实施记录与 README。
4. MTK/CLI/Qcom/Core/LP 全量 Release 测试，solution Release 构建，git diff --check、完整差异、资源 keys/参数、ignored 检查。所有测试/日志/bin/obj 不提交。

参考只用于标准 USB 行为：[libusb clear_halt](https://libusb.sourceforge.io/api-1.0/group__libusb__dev.html)、[同步 bulk transfer](https://libusb.sourceforge.io/api-1.0/group__libusb__syncio.html)、[LibUsbDotNet EndpointBase](https://github.com/LibUsbDotNet/LibUsbDotNet/blob/master/src/LibUsbDotNet/LibUsb/UsbEndpointBase.cs)；当前本地使用 LibUsbDotNet 3.0.224，ClearHalt 返回 Error 见其 XML 文档。

风险：Pipe 的具体驱动/端点起因未知，修复是条件恢复，不是已验证所有实机均可恢复。日志已证明前轮 WDT 修复生效；本轮尚无真机恢复或 DA 执行证据。缺认证材料不会因检查点诊断而变成认证成功。
