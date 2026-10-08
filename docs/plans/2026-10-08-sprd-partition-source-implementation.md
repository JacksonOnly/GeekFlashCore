# SPRD 分区容量来源自动选择实施记录

日期：2026-10-08；任务 SPRD-11；设计见同日 partition-source-design。

- 开始：d60b96e，codex/sprd-support，工作区干净；已读 SPRD 恢复记录及代码。按用户最新授权设计默认 Auto，保留手动来源和单位要求，不识别未实现的物理分区格式。
- 证据：固定上游先 GPT 后原生及失败选择的 READ_END 顺序；仅参考/模拟，无实机。
- 测试先行：新增 Auto 来源与 CLI 测试，因枚举/元数据缺失先失败；随后实现。Auto 追加为枚举末项，默认 GPT 前缀只读一次，复用分片读取和严格 GPT 校验；手动 Native/GPT 顺序及既有枚举值保持。原显式模式测试改为显式 source，保留原行为断言。
- 只有完整无 GPT 标志的前缀，或初始有效空 0x84/0xfe + READ_END ACK 才进入一次原生查询。任一标准 GPT signature/保护 MBR 都要求严格 GPT；坏 CRC、歧义、坏帧、其他 NAK、读中失败、cleanup 失败、部分帧、取消与超时不降级。保护 MBR 四个条目位置均覆盖；普通 MBR 前缀可选择 Native 来源但不解析/声明 MBR 几何。
- 原生解析与倍率换算分离：名称/单位原值快照固定计数上限；没有单位时仅完整有效记录可产生内部配置异常，保留 StorageReady/Generation/Source；没有容量输出或写入。该异常单独通过 gate，不将未知 I/O 错误降级。重复查询使用快照，无额外设备探测；断开/失效清除快照和目标来源。
- TargetInfo.PartitionTableSource 在设备查询确认后返回 Native/UserPartitionGpt；宿主已验证清单与查询前为 null，优先级不变。CLI 省略或 auto 均可，仅显式 native 预检单位；info 和数值/来源日志同步，所有错误与帮助中英文资源化。

## 2026-10-08 验证

全部为模拟传输/CPU 证据，无实机；当前仅 SPRD/CLI 两个 ignored 测试工程。

| 命令/检查 | 结果 |
| --- | --- |
| `dotnet test .tests/GeekFlashCore.Protocol.Sprd.Tests/GeekFlashCore.Protocol.Sprd.Tests.csproj -c Release --no-restore -v quiet` | 155/155，新增 34 项，含两种 GPT、来源/缓存/断开代数、允许拒绝及 cleanup、保护 MBR/CRC/歧义、超时/取消/分片、原生坏表与缺单位的会话及无写入、手动线路与宿主清单；原流式 64 MiB 回归保持 |
| `dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore -v quiet` | 26/26，新增 4 项，默认/显式 Auto、手动 Native 单位和协议隔离 |
| `dotnet build GeekFlashCore.slnx -c Release --no-restore -v quiet` | 成功，0 warning / 0 error |
| `dotnet src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.dll help sprd` | exit 0，来源默认 Auto 和手动覆盖、单位约束、损坏 GPT 不降级说明正确 |
| 资源/差异/忽略审查 | 三个相关项目中英文键一致；git diff --check 通过，.tests/temp/bin/obj 未被跟踪 |

生产代码、CLI、README、docs/sprd.md、设计/实施和 AGENTS 恢复入口同步，提交 `feat(sprd): select validated partition sources automatically`；确切提交号由包含本记录的提交恢复，.tests 不提交。

## 风险与恢复

下一次先读本设计/实施和 docs/sprd.md、检查工作区，再用合法匹配 FDL 做只读 GPT 与原生查询。没有实机证据；初始拒绝后的 READ_END ACK、不同 FDL 的 user_partition 前缀支持和原生单位须验证。不支持 cleanup 的设备自动选择失败后需重新连接并手动 native；不得通过清理失败继续猜测。GPT 标志缺失只说明前缀没有可识别 GPT 证据，Native 表不是 MBR/NAND/PMT 的物理格式证明；单位仍由宿主明确。未知格式、名称遍历、容量试探、重分区及 NV 专用写仍不属于此次范围。前缀池最多 4 MiB、原生快照最多 862 条，无镜像大小物化；模拟大源测试不构成设备吞吐证据。
