# MTK BROM 接入与断连审查进度

方案：`2026-10-08-mtk-brom-admission-design.md`。日期：2026-10-08。

## 启动

- 基线 `acb9daf`，分支 `codex/mtk-nonexploit-audit-20261008`，工作区干净。用户确认默认持续等待、显式预算保留、识别后不重放。
- 已读 MTK 设计/进度、BROM 映射与 exp 边界，以及 CLI recovery、initial stall、watchdog-before-loader 恢复文档。
- 确认旧流程仅恢复 USB Open NoDevice，Probe 失败直接退出；MtkWire Read 按每次分片重新取得 read timeout。旧计划的“仅 Open 前可恢复”由本设计的“完整 FD 之前可恢复”取代。
- 无实机证据；下文仅记录本地参考与模拟传输事实。

## BROM-REC-01：参考审查与测试先行

- Penumbra `core/src/preloader/protocol.rs` SHA256：`E9B6D0744458AEC96B84E1320AAE1820BBD272C3B894A72D27ED7C562DFFF5D9`。参考目录无 `.git`，该指纹不是 upstream revision。
- 首批 ignored BromAdmissionTests 6 项，改前 5 失败/1 通过；MtkAdmissionRecoveryTests 首批 13 项，改前全部失败。后续单独新增纯 ReceiveData 超时测试，改前失败：StorageReady/IsConnected 未被清除。均先复现再修复。

| BROM 审查组 | 结论与处理 |
| --- | --- |
| CDC、A0/0A/50/05、FD、启动前缀、已握手候选 | 默认 4-byte 前缀容不下 READY 的 5 bytes，修为 5，仍有最大 16 的选项上限；A0 echo 仅作候选，仍需 FD 完整非零标识；不复制参考的同句柄重试/无限排空 |
| FD/FC/D8/FE/FF、WDT、Probe/Connect 顺序 | 保留 FD 两个 u16 的硬件编号/初始版本解释，不照搬 Rust 把第二 word 当 status；历史识别标记在 WDT 前置位，WDT/安全查询失败不恢复；已知 WDT ACK、未知 profile 不写、同会话去重不变 |
| Read16/32/A2、Write16/32、RegisterAccess、reset、UART/cache | 维持有界地址/count/对齐/BE 参数、LE register status、echo 与最终状态校验；本轮修复公共读取预算与接收失效，不更改命令值/线路 |
| D7/DA、E0/E2、SLA、身份/日志、partition/jump | 保留流式上传、奇数补零/XOR/ZLP、状态特例、合法宿主签名、敏感缓冲清理、命名长度上限和跳转后通道失效；不照搬忽略 checksum/失败默认值的行为；接入重试不包围这些命令 |
| gate、代数、取消、异常、资源所有权 | 纯接收失败也 Fault/Close/递增代数；预取消不发命令；同步调用返回后检查取消/预算；候选失败释放协议与传输，清理异常不替换主故障；核心无自动重试 |

## BROM-REC-02～03：实现完成

- `MtkConnectionAdmission` 只恢复初始 Probe 显式标记，完整 FD 前 USB/超时/echo 失败可换新候选。FD 后即使 Probe 尚未返回也不重放 WDT。库异常类型不变；新增具体 MtkProtocol.HasIdentifiedTarget 不改变 IMtkProtocol 或既有 enum 值。
- MTK 默认持续发现，取消仍停止；解析器记录显式等待预算。首次发现及各候选共用总预算，完整 Probe 成功后解除接入预算，沿用原 DA 选择/连接预算。非交互与 mtk-probe 也经过接入门禁；mtk-capabilities 不发送 Probe，既有直接内部创建入口不变。
- 枚举/打开的 USB、I/O、设备状态错误继续等；无候选正常轮询不报错；等待/恢复提示去重。配置、歧义、依赖/驱动安装失败、DA 选择错误及取消不归类为闪现。
- MtkWire Read 分片共享一个截止时间；read/write/control/ZLP 返回后检查取消/预算。纯接收失败不再遗留 StorageReady；永久写/Scatter 未知写结果仍使用 HasWritten 判断，不添加写入重试。
- MTK Faulted 后清除终端进度和统计，结束交互循环并提示重新连接，不执行旧会话下一条命令。显式接入等待超时独立展示，不误称已连接设备响应超时；普通响应超时也不伪称已拔出。
- 范围：MTK Core/Abstractions、CLI MTK 接入编排/等待参数/展示、三项双语资源及文档。exp、payload、宿主策略注入与顺序、公共 USB 传输实现、Qcom/SPRD 协议生产代码未修改。未指定协议的自动等待默认也持续到取消，其他显式协议默认仍为 30000ms。

## BROM-REC-04：验证证据

- 新增 ignored 测试 33 项：BROM 7 + CLI 26。覆盖实际 CLI Core 的失败句柄释放→新候选成功、FD 后只尝试一次且无 D7/E0/E2/E3、首次准备预算、累计预算、默认取消、两种语言清理/退出与其他显式协议默认预算；DA 选择取消/超时/WDT NAK 由原有测试回归。
- 原始完整 MTK：586 项，571 通过、15 失败。仍是基线 14 项 PenumbraLinecodeTriggerTests（exp、范围外）和缺少外部 `oppo_2_MTK_AllInOne_DA.bin` 的 1 项 LoaderTests，没有新增非 exp 失败。
- 排除上述明确基线项的 MTK 回归：571/571 通过；不能表述成原始全量通过。
- CLI 最终全量：158/158 通过。中途一次 quiet 全量出现 SectorReadDelegatesToCoreAndKnownUnsupportedReadDoesNotTruncateOutput 单项失败；该项单跑及随后完整回归通过，未改 Qcom 或该测试，偶发原因未确定，若再现需保留完整异常栈。
- 64MiB Raw/Sparse/Fill 与离线固定缓存共 4 项内存回归通过：前三项分配上限断言 16MiB，Raw 单次源读取不超过 4096 bytes；离线扫描固定缓存断言低于 1MiB。不是实机吞吐指标。
- Release solution 构建通过，0 警告/0 错误；MTK ignored 测试工程仍有基线 CS0649 exp 夹具警告。中英文资源键完全对应、无重复；`git diff --check` 通过。`git ls-files .tests temp` 为空，新增测试、诊断日志和构建产物均 ignored。

### 复验命令

```powershell
dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore -v quiet
dotnet test .tests/GeekFlashCore.Protocol.Mtk.Tests/GeekFlashCore.Protocol.Mtk.Tests.csproj -c Release --no-restore -v quiet
dotnet test .tests/GeekFlashCore.Protocol.Mtk.Tests/GeekFlashCore.Protocol.Mtk.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName!~PenumbraLinecodeTriggerTests&DisplayName!~oppo_2_MTK_AllInOne_DA.bin' -v quiet
dotnet test .tests/GeekFlashCore.Protocol.Mtk.Tests/GeekFlashCore.Protocol.Mtk.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~64MiB|FullyQualifiedName~LargeSourcesUseAFixedCacheAndNoPerInstructionAllocation' -v quiet
dotnet build GeekFlashCore.slnx -c Release --no-restore -v quiet
git diff --check
git status --short --ignored
```

## 未决风险与恢复点

- 无实机或 native USB 断连时序证据。同步 native call/驱动安装不能保证被强行即时中断；有限 timeout 与返回后检查阻止继续旧命令，不宣称拔线瞬间必定返回。
- Read timeout 现在按逻辑读取累计；依赖旧分片重置行为的慢设备应显式增大 read timeout，不能靠慢速碎片无限延长操作。
- HasIdentifiedTarget/SessionState 不是后台在线探测。空闲或等待 DA 输入期间未加保活、重发或独立读取线程；NoDevice 才能确认断连，只有超时则如实报告超时。
- 持续权限/设备打开问题会继续等并记 Debug，Ctrl+C 可停止查看日志；依赖/驱动安装/歧义和资源配置问题仍明确失败，不盲目吞所有程序错误。
- 下次优先采集真实闪现→重枚举→FD/WDT 的脱敏日志、拔线后 native 返回时长，核对设备身份过滤；不要扩大恢复到 WDT、认证、DA 或写入阶段。
