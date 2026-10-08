# MTK 上传 / XFlash 对照与诊断进度

日期：2026-10-08。方案：`2026-10-08-mtk-transfer-diagnostics-design.md`。

- 基线 `afbb3d7`，工作区干净；用户确认设计。已读 MTK 设计/进度、BROM 映射、框架边界、前次接入恢复记录；Qcom sender/receiver 的事件分层作为参考，不更改 Qcom。
- XFlash `protocol.rs` SHA256：`CB17E5926F4D0483DC7DABA44792AAD23BD73DA6C02A316783EE518E3AC84E23`；libusb backend：`8CEE4F8785B1181B7948F5F1F932193C42BCE068CE464B5D076939ECAE860692`。目录无 Git，不宣称 upstream revision。
- 当前确认 BROM UploadBytes 固定64-byte/无条件ZLP；XFlash send_data 参数组一次 ACK、read_data/ACK 上行与 checksum 下行是另外三条路径，不能将加和校验替换 BROM XOR。
- 当前 MESSAGE 已有限排空但无元数据日志；CLI 默认 sink 过滤所有未开 verbose 的 Serilog 事件，核心即使写 Information 也不会显示；需显式安全摘要允许列表，而不是开放全部 Information/设备全文。

## MTK-TXLOG-01：线路审查和测试先行

- 首批 ignored `TransferDiagnosticsTests` 6 项改前全部失败；CLI `MtkSummaryLogTests` 2 项改前默认摘要失败、verbose 通过。首批 BROM 写次数断言在实施前修正为“两块 + 一个奇数 pad”，不是通过放宽生产行为修复测试。
- 最终新增 MTK 26 项、CLI 10 项；包含非 seek/部分源读取、65-byte 奇数块边界、取消、checksum 失败、默认/兼容 ZLP、512-byte 协商包长拒绝与成功、上行逐块 ACK、下行 zero/checksum/data 与最终 ACK、EMI 分组 ACK、双语与隐私哨兵、无失败完成事件、非法 CLI 参数/跨协议拒绝及 64MiB BROM 有界分配。

| Penumbra 对照组 | 结论 / 处理 |
| --- | --- |
| BROM D7/E0/E2 UploadBytes | 原实现固定 64-byte host write 和强制 ZLP；不是 XFlash upload_data。改为默认 64KiB 池化块，非 seek 可流式，默认无 ZLP；原 BE header、奇数补零、跨块 LE16 XOR 不变。与 Rust 忽略 checksum 不同，本项目仍严格校验，失败不跳转、不重发 |
| libusb write_all | Rust 连续写剩余数据，由 backend 拆物理包，无额外 ZLP；本项目不修改公共 USB，提供显式 chunk/ZLP 兼容选择，不把 host chunk 当作 endpoint max packet |
| XFlash send_cmd/send_data | 保留 opcode FLOW → ACK、每参数独立 FLOW → 组末一个 ACK；pre-negotiation 写块按参考 0x8000 有界。重复 zero/checksum/command 小帧使用 stackalloc，不逐块分配 4-byte 数组 |
| BOOT_TO / EMI | BOOT_TO range + DA2 为一个流式参数组，status + sync 两次响应保留。XFlash EMI 改为池化流式一帧，无 1MiB EMI 临时数组，仍只消费组末 ACK；EMI 必要/完成/不需要有摘要 |
| upload_data / READ_DATA / Upload | device→host，每块写目的流后 zero ACK + status；raw/named 共用 ReceiveStream。数据 FLOW 必须同时适配协商读包、主机帧限制和剩余长度，超长在 payload/目的流写入前拒绝 |
| WriteData / Download | host→device 的 zero + additive checksum LE32 + payload → chunk status → final status 保留。raw/named 共用 SendStream；named START_DL_INFO / END_DL_INFO 保留，未知写结果不重试 |
| Format / FormatPartition / eFuse | 现有不同参数 ACK、进度与 final 状态顺序不改；只替换等价小帧并补元数据日志。标准扩展 / exp 算法、资源和策略次序不改 |
| MESSAGE / frame / status | 不照搬参考无界日志 drain、原始 header/body trace 和宽松状态/长度；仍有限预算、有限 MESSAGE/大小、精确 status frame。MESSAGE 仅长度/序号日志，无文本 |
| Legacy / XML | 补 typed command、ACK/checksum boolean、允许列表 XML 命令和生命周期/包长/进度诊断。保留 Legacy EMI/IoT 线路与 XML START/END、响应允许列表、完整 END 后 optional Unsupported 回退。checksum 不再冒充异常 Status 进入文件日志 |

## MTK-TXLOG-02～03：实施事实

- 核心新增 `BromUploadChunkSize`（0=BufferSize，其他 64～1048576）和 `BromUploadZeroLengthPacket`（false）。CLI 提供 `--mtk-brom-chunk bytes` / `--mtk-brom-zlp`，MTK 专用；旧写形态为 `--mtk-brom-chunk 64 --mtk-brom-zlp`。默认 USB host write 数从每 64 bytes 一次变为每 65536 bytes 一次，不宣称实机吞吐提升比例。
- `MtkDiagnostics` / `MtkTransferLog` 统一非敏感事件；阶段文本中英资源化。UI 默认只接收显式 `MtkSummary=true` 的摘要，verbose 追加 Debug，已展示/设备诊断事件仍过滤，摘要不重复展示。其他协议默认 sink 策略保留，Qcom/SPRD/公共 USB 生产代码未改。
- 摘要涵盖打开/握手/识别、DA 资源和选择、DA1 上传验证/跳转、DA2 上传/就绪、EMI、宿主签名/认证证据、WDT、存储类型/区域几何、raw/async/named 读写擦除开始/完成，以及可恢复 GPT/PMT/XML optional 回退。命名操作名称经过原有校验；命名擦除不报告虚构字节数。Sparse 计数为逻辑镜像范围，Raw 写入计数包含实际 padding；不是逐扇区物理流量统计。
- 每个会话携带 `MtkSessionId`。Debug 收发包含阶段、命令名/码、帧 type/length、USB 写长度/耗时、读长度/片数/实际预算/耗时、ACK/status、协商写/读包长。Fault 一次记录失效前 state、stage、command、异常类型/真正 status，不附加原始异常或消息。完成事件在最后 ACK/lifetime 与取消预算检查之后。
- BROM 0x7017 / E2 0x1d0c 只记“设备允许跳过交换”，不冒充本次签名成功；Legacy DA SLA Unsupported 与 XML 不支持也不写成 Authenticated。认证材料、checksum 数值、私密标识、任意 XML 参数、MESSAGE 文本和载荷均不日志化。
- 完整 FD 前重等、识别后不恢复、绝对逻辑 read budget、会话代数/进度清理等前次接入修复保持，没有日志诱发的 USB 查询、保活线程或自动写重试。

## MTK-TXLOG-04：验证

- 最终 MTK 原始全量 **612 项：597 通过 / 15 失败**；仍是基线 14 项 `PenumbraLinecodeTriggerTests` 与缺少外部 `oppo_2_MTK_AllInOne_DA.bin` 的 1 项 LoaderTests，不称为全量通过。明确排除基线项后完整回归 **597/597**；新增目标测试 **26/26**。
- CLI 完整回归 **168/168**；新增目标 **10/10**。Release solution **0 warnings / 0 errors**，ignored MTK 工程仍有原 exp 夹具 CS0649，新增测试无警告。
- 3 个历史 Scatter/GPT 测试向协商 4096-byte 接收包发送 8KiB/64KiB 单帧；栈定位到新超长帧拒绝。将 ignored 公共 Read 夹具按原协商值正确切为 4096-byte + 每块 ACK，保留原业务断言，不放宽生产校验。
- BROM 64MiB generated 非 seek 源：断言 1024 次 payload write（总 1028 含四个 header）、最大源读 65536、分配 <2MiB、流释放；最初测量用 OnWrite 回调导致测试 USB 每块复制、分配断言失败，改为测试 backend 的纯计数器，生产代码未按测量器改变。Raw/Sparse/Fill 64MiB 与离线固定缓存原有 4 项继续通过，阈值分别 16MiB / 1MiB。不是硬件吞吐证据，也不把已启用 Debug 日志的分配等同于日志关闭测量。
- 一次 quiet MTK 回归中原有 `FragmentsShareOneReadBudgetAndStopWithoutFurtherCommands`（50ms 预算 + Thread.Sleep(30)）失败；单项和随后 normal 完整 597 项复跑通过，原始完整 612 项该项也通过。未改其断言或生产 timeout；保留本地 `temp/mtk-txlog-*` 日志，睡眠/调度依赖测试的稳定性仍需后续排查，不能推断硬件 timeout 有问题。
- 中英文资源：MTK 75 keys、CLI 284 keys 对应，非空/无重复，命名/数字格式占位符一致；`git diff --check` 通过。`.tests`、temp 日志和 bin/obj 均 ignored，`git ls-files .tests temp` 为空。

### 复验命令

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Mtk.Tests/GeekFlashCore.Protocol.Mtk.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~TransferDiagnosticsTests -v quiet
dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore -v quiet
dotnet test .tests/GeekFlashCore.Protocol.Mtk.Tests/GeekFlashCore.Protocol.Mtk.Tests.csproj -c Release --no-build --no-restore -v quiet
dotnet test .tests/GeekFlashCore.Protocol.Mtk.Tests/GeekFlashCore.Protocol.Mtk.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName!~PenumbraLinecodeTriggerTests&DisplayName!~oppo_2_MTK_AllInOne_DA.bin' -v normal
dotnet test .tests/GeekFlashCore.Protocol.Mtk.Tests/GeekFlashCore.Protocol.Mtk.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~64MiB|FullyQualifiedName~LargeSourcesUseAFixedCacheAndNoPerInstructionAllocation' -v normal
dotnet build GeekFlashCore.slnx -c Release --no-restore -v quiet
git diff --check
git status --short --ignored
```

## 未决风险 / 恢复点

- 无实机 USB/ZLP/速度/EMI 证据；默认改变 host bulk call 形状，遇到兼容问题显式恢复旧 chunk/ZLP，不用重放写命令试探。协商 read packet 是否会动态变化仍须真机核对，不能删除协商上限消除错误。
- 开启 Debug 帧/USB 诊断会增加日志量与 sink 成本；CLI 沿用已有 16MiB 分卷，不承诺与禁日志相同的吞吐。库宿主可调 Serilog 级别，并应在创建协议前配置 logger。
- 原生 USB call 的即时取消仍受 backend/驱动限制；Write 故障中的 TimeoutMilliseconds 是剩余操作预算，不是对原生写超时的推断。仅 Timeout 不能证明设备已拔出，不新开读线程窥探在线状态。
- 下一步按 `docs/mtk-diagnostics.md` 采集脱敏真机日志，核对 BROM 64KiB/no-ZLP、预协商 EMI、DA2/SLA 后包长与断连返回时长。非 exp 可运行回归先继续，15 项基线失败不混入本任务。
