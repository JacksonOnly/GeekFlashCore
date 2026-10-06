# MTK 初始 USB 停滞与检查点诊断实施记录

日期：2026-10-06。任务：STALL-01、CP-LOG-01。起点：main / `08743d6`，工作区干净。范围见 [设计](2026-10-06-mtk-initial-usb-stall-design.md)。

## 事实与行为

- 用户 `geekflash-20261006-212344-833-daecaa1f5a0d4ea9a12ebb7cdedc07dd.log` 在 BROM Handshaking 的 ReadByte 返回 UsbException/Pipe，尚未查询 FD；旧日志未记录握手序号，不能确认是第一个字节，也不能确定停滞的驱动/设备原因。
- 用户 `geekflash-20261006-212407-715-3bee4f64dd7f4389b9c445eb68d4c728.log` 确认 MT6893、HW0950、DA6893、WDT Disabled、安全 E7；DA 输入后缺 DAA。异常位于 PrepareBootResources 第二次校验（原第 27 行），其前第 25 行已经调用 BeforeDa1。CLI 未注入策略，原检查点直接返回；没有认证抢在检查点前的缺陷。
- STALL-01：新增默认 false 的 `RecoverInitialReadStall`，仅 MTK CLI 显式开启。Open 后首次非空 bulk Read 为单字节、有限 timeout、Pipe 且 transferred=0，允许一次 IN ClearHalt 后按剩余原预算读一次。最多两次 Read、一次 ClearHalt；不重新发握手字节，不 reset/re-enumerate，也不重试写入。
- 空读取不消耗资格；其它大小/状态/部分传输、ReadExact/ReadAvailable/Flush 消耗资格。Close 清除资格，新的 Open 才重建，重复 Open 不重建；后续 Pipe 沿原会话失败路径。清除失败或预算耗尽不继续读取。Qcom 和未显式开启的通用宿主行为不变。
- CLI 对最终 Pipe 显示“USB 端点停滞”及重连提示，避免仅显示泛化失败。原有 NoDevice/Timeout 分支保留。
- CP-LOG-01：Debug 记录握手步骤序号、每个检查点到达及固定 NoStrategy/DescriptorMismatch 原因；已有实际回调结果保持 Information。仅补诊断，不重排调用、不改变认证要求、不增加默认策略或具体利用逻辑。

## 测试先行与验证证据

所有测试位于 ignored `.tests`，不加入 solution、Git 或发布产物。恢复测试联编生产 `InitialUsbReadRecovery.cs`，不是另写一份状态机；真实 LibUsb 后端生命周期由模拟 endpoint/context 夹具覆盖，未访问物理 USB。

1. 初始恢复 17 项夹具定义后，因生产恢复类型尚不存在而编译失败；实现后通过。覆盖预算递减、默认禁用、长度/部分传输、错误种类、重复 Pipe、ClearHalt 失败、预算耗尽、空读、异常、资格及 reopen。另新增 1 项实际后端 Open/Close/重复 Open 资格测试。
2. 最初 8 项检查点夹具中，4 项认证顺序观察器已通过、4 项缺少日志的断言失败，证明原认证顺序正确而诊断缺失。修复后通过；扩大到 fresh/cached Probe，最终 14 项覆盖同步/异步、缺 DAA/certificate、默认与 descriptor 过滤。
3. 3 项 CLI 测试先失败、修复后通过，覆盖中英 Pipe 展示、MTK 开启/通用默认禁用。既有 12 项 DA 提示前 Probe/WDT 准备测试继续通过。

观察器仅记录阶段/认证证据并返回 NotApplicable；缺材料前回调恰好一次，认证证据仍 NotQueried，没有发送 DA/auth/cert/SLA 命令。四占位类未改。

| 2026-10-06 验证命令 | 结果 |
| --- | --- |
| `dotnet test .tests/GeekFlashCore.Protocol.Mtk.Tests/GeekFlashCore.Protocol.Mtk.Tests.csproj -c Release --no-restore` | 421通过，0失败/0跳过；本轮新增32项 |
| `dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore` | 91通过，0失败/0跳过；本轮新增3项 |
| `dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore` | 457通过，0失败/0跳过；既有测试 |
| `dotnet test .tests/GeekFlashCore.Core.Tests/GeekFlashCore.Core.Tests.csproj -c Release --no-restore` | 9通过，0失败/0跳过 |
| `dotnet test .tests/GeekFlashCore.Android.Lp.Tests/GeekFlashCore.Android.Lp.Tests.csproj -c Release --no-restore` | 55通过，0失败/0跳过 |
| 五工程合计 | **1033通过** |
| `dotnet build GeekFlashCore.slnx -c Release --no-restore --verbosity quiet` | 0警告/0错误 |
| CLI / MTK 中英资源 | 253 / 16 keys 成对，格式占位符对应 |
| `git diff --check`、完整差异与 ignored 状态 | 通过；`.tests`、日志与构建产物不受跟踪 |
| 修复后真机 Probe / DA / 认证 | 未执行 |

## 提交与恢复

STALL-01 已提交为 `66b4f6d fix(usb): recover initial MTK read endpoint stalls`；CP-LOG-01 独立提交为 `fix(mtk): clarify handshake and host checkpoint diagnostics`，其提交号通过 `git log` 查询。仅生产代码和必要文档进入提交；本轮不 push、不创建 PR、不发布包。

主项目修复版：`D:/Code/CSharp/GeekFlashCore/src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe`。未更新用户早期 a803 worktree。下一次用此路径运行并检查新日志中的握手步骤、BeforeDa1 到达及 NoStrategy；仍需合法认证材料才能完成标准认证。

## 未决风险

- 初始读取的条件恢复已由模拟传输验证；第一份旧日志没有步骤序号，若真实 Pipe 出现在后续字节或端点持续停滞，当前条件不会恢复，需要新日志继续定位，不能宣称所有 Pipe 已解决。
- 标准 ClearHalt 只清 IN 端点状态，不保证设备仍保留待收应答。未收到响应或清除失败时继续终止会话，避免重复 OUT 命令。
- ClearHalt 是同步 native 调用，有限控制 timeout 沿现有配置检查；返回后拒绝超期结果，不能承诺强制中断 native call。同步 I/O、串行 gate 和 transport 所有权不变。
- 新日志不包含 DA 字节、认证材料、挑战或策略私有文本。缺 DAA/cert、NotApplicable 或 Completed 都不会被解释为认证成功。
