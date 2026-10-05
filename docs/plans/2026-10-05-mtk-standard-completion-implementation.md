# MTK 标准功能补全进度

日期：2026-10-05。设计：`2026-10-05-mtk-standard-completion-design.md`。基线 `76ee4bf`，分支 `codex/mtk-protocol`。

## 初始证据

- 工作树生产代码干净，已有两份未跟踪 kamakiri 计划保持原样。
- 原 MTK Release test 命令失败：ignored `KamakiriTests.cs` 引用不存在的 `MtkKamakiri2Strategy`。仅在本地测试 csproj 排除该文件，文件与漏洞接口不修改。
- 正常线路缺口：固定 LBA2 的 primary GPT、XML PowerOff 实际 reboot、普通 DA channel 无线程约束；旧支持矩阵中的 NAND/NOR/SDMMC、PMT、DA 查询与 A/B 需要逐项参考核对。
- 未连接真实设备，参考树只读。Codex UI 挂接报告工作树属于另一 chat；用户已明确授权使用此路径，文件操作仍严格使用指定路径，不移动或重新登记它。

## 任务与验证

| ID / 日期 | 本轮实现与行为结论 | 验证与限制 |
| --- | --- | --- |
| STD-01 / 2026-10-05 | DA1/DA2 按 `m_start_addr` 执行，`m_start_offset` 保留长度/签名边界语义；>=3 region 的 raw index 0 归一为实际 DA1 index 1；DA channel 固定回调线程和 generation；回调返回时核查预算；DisconnectAsync 等待 gate 可取消 | 先复现 XML PowerOff、错用 EntryOffset、raw index、跨线程、超时缺陷；模拟同步/异步线路；本地 V5/V6/Oppo/MT6893 资源解析通过，未执行这些 loader |
| STD-02 / 2026-10-05 | GPT 按物理 header 与实际 entry LBA 有界读取，独立校验两项 CRC、布局、条目唯一 ID/重叠；primary 损坏/缺失才尝试 last-LBA backup；Legacy PMT 32/64/96 明确布局；XML 原生分区表 | 非 LBA2、CRC、两副本损坏、4096 block backup、恶意 count、PMT 三种 stride、XML 重名/重叠/version；metadata <=1 MiB，表长与分配先检查 |
| STD-03 / 2026-10-05 | 独立可选 `IMtkDaDiagnostics`，XFlash 0x40001～0x40016 只读 allowlist；XML HW/FW/version/property/native table；Legacy USB speed、Legacy/XML aligned register | 生命周期 START/ACK/inline or file/END；查询 owned buffer 清零，包括操作返回后超期；非法枚举/key/address 无发包；没有任意 DEVCTRL/XML 入口 |
| STD-04 / 2026-10-05 | XFlash SDMMC/NOR/NAND 几何与标准 read/write/erase；NAND ECC data pages 排除 OOB；默认只读，显式启用写/擦除；NOR erase profile；Legacy NOR read/write、SDMMC write；XML NAND read/native table | 各 storage ID、页/擦除对齐、checksum/final status、默认拒绝无发包；XML NAND usable/BMT 未确认且只读；XFlash Download 对应 Fastboot，XML PowerOff 无发包拒绝 |
| STD-05 / 2026-10-05 | A/B version/CRC/slot count、当前 boot suffix 与最高优先级槽位分开；备份最小 containing window，保留其余字段和扇区字节，写后全窗口读回；RPMB erase 复用认证 Write 和零流；Read/Raw/Sparse/Erase progress；CLI 五种标准命令和三种介质选项 | 512/4096 sector 邻接保留、备份失败无写、unknown write invalidation/no retry、Raw padding 不多计进度、最终 NAK 不报告 Completed、XML 50%/END、借用 stream 不关闭、CLI valid/invalid/Qcom conflict |
| STD-06 / 2026-10-05 | 本轮完整回归、资源/日志、性能、来源和支持矩阵更新，提交按标准核心、BootControl 服务、CLI/文档拆分 | 下列真实命令证据；本地 ignored 测试不提交；漏洞 source/接口/checkpoint 文件未新增或修改 |

## 验证证据

以下均在指定工作区执行，0 失败/0 跳过。本轮 MTK 从排除缺失漏洞策略测试后的 131 项增加到 184 项；新增测试/调整夹具全部留在 ignored `.tests`。

| 命令 | 结果 |
| --- | --- |
| `dotnet test .tests/GeekFlashCore.Protocol.Mtk.Tests/GeekFlashCore.Protocol.Mtk.Tests.csproj -c Release --no-restore` | 184 通过 |
| `dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore` | 119 通过 |
| `dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore` | 251 通过 |
| `dotnet test .tests/GeekFlashCore.Core.Tests/GeekFlashCore.Core.Tests.csproj -c Release --no-restore` | 9 通过 |
| `dotnet test .tests/GeekFlashCore.Android.Lp.Tests/GeekFlashCore.Android.Lp.Tests.csproj -c Release --no-restore` | 55 通过 |
| `dotnet build GeekFlashCore.slnx -c Release --no-restore` | 0 警告 / 0 错误 |
| `--filter FullyQualifiedName~StreamingTests --logger 'console;verbosity=detailed'` | Raw 67,108,864 bytes、分配 1,049,840 bytes、最大源读取 4096 bytes；Sparse 展开同样大小、源仅 44 bytes、分配 1,313,408 bytes；DONT_CARE 保持未写 |
| 中英文资源键/placeholder 核查 | MTK Abstractions 7、Core 11、Extensions 1、CLI 206 成对；无重复/不匹配 |
| `git diff --check` / `git ls-files .tests` / ignored 状态 | 差异检查通过；无跟踪测试；`.tests`、bin/obj、temp 保持 ignored |
| 日志审查 | 新增日志只有“采用 GPT backup”和固定 region name；诊断二进制不输出控制台/日志；认证/身份/key/XML 内容无新增日志；register 值仅为用户显式 read 的控制台结果 |

## 非漏洞功能对应与明确边界

| 参考功能组 | 可用入口与状态 |
| --- | --- |
| BROM / Preloader 标准命令、合法认证、watchdog、DA upload/jump | 既有 `UseBromSession` 与标准 chip catalog 保留；本轮修正真实 DA metadata；完整方法映射见原 BROM mapping |
| Loader/EMI/三种 DA | 既有容器、alias/version 选择、同步/异步资源和合法 SLA；本轮按正确地址启动；外部 XML EMI 无确认线路 |
| Flash read/write/format、Raw/Sparse、普通块设备 | 全部复用同步 transport 和 gate；标准介质条件见上表；进度按传输/展开 bytes，Started/Running 与最终 ACK/END 后 Completed 分离 |
| Partition/GPT/PMT | primary/backup CRC 与实际 LBA；Legacy 明确布局；XML 原生表；NAND 不伪装 GPT sector；原生表条目按 bytes 校验 |
| DA queries / reg / property / native partition metadata | 新可选诊断接口；XFlash 寄存器仍要求已加载扩展，Legacy/XML 标准寄存器不要求扩展 |
| A/B get/set | `MtkBootControlService` 与 `mtk-slot`；CRC/版本/数量、最小窗口备份/写/回读；最高 priority 不代表 Android 最终启动成功 |
| Memory / SEJ / RPMB / seccfg | 既有已合法加载的 ABI 扩展、显式地址/容量/密钥、seccfg SW/SEJ 保留；新增 RPMB erase；不注入/patch/load 扩展，不烧录 key |
| Host CLI / USB / profiles / recovery | 既有 discovery、物理选择、有限材料预算和 Windows bootstrap 保留；新增标准 command/profile；旧 views/channel 失效，unknown writes 无自动重试 |

没有将“所有非漏洞功能”写成对所有芯片/厂商/介质的兼容承诺。以下是本轮审查后仍缺少确认线路、能力或真实设备证据的部分：

- Legacy NAND/IoT 的 DA3/config/OOB/BMT、Legacy SDMMC read/erase、Legacy NOR erase；明确拒绝，不发送猜测命令。
- XML NAND 只报告 total/page/spare/erase，没有 usable/BMT，`LogicalCapacityConfirmed=false`，即使启用 XFlash NAND write 选项也不能写 XML NAND；XML NOR/SDMMC 没有本地确认的完整 GET-HW-INFO 契约。
- XML PowerOff 不发送 reboot；外部 XML EMI upload 不臆造。XFlash reboot 使用 penumbra 的 28-byte 参数；mtkclient 的 32-byte 变体需要设备/profile 证据。
- 无独立主机 GCPU/DXCC/TZCC 驱动、OTP/eFuse 编程、RPMB 一次性 key programming。合法材料/已加载扩展之外的硬件 crypto/密钥派生仍取决于明确 profile 和后端。
- RPMB 简化扩展 ABI 的认证/counter/MAC 由设备端承担，不声称提供完整主机 nonce/MAC/counter 协议验证。
- 不自动切换 USB speed/reset 后续接 DA；重枚举只有独立有限工厂。厂商在线账户/签名服务不包含在协议核心。
- 没有本轮真机连接/擦写；模拟和参考源码不等同于硬件证据。新的 NAND/NOR/SDMMC、28-byte reboot、A/B、RPMB、vendor SLA 与 USB 吞吐需要相应设备验证。

## 工作树与恢复入口

生产变更只涉及 MTK Core/Abstractions/Extensions、MTK CLI adapter/options/resources 和必要文档；Qualcomm、Transport、GPT parser、既有漏洞框架/策略文件不改。两份初始未跟踪 kamakiri 计划保留原样；ignored `KamakiriTests.cs` 保留，仅本地测试 csproj 排除其缺失策略引用。参考目录只读；mtkclient 基线 `e9fcf97`，当前 penumbra 拷贝无 Git 元数据，历史 `ce13391` 不能作为此次文件指纹。

恢复工作时先读本文件和支持矩阵，运行当前 Release 测试；要新增上述未确认介质/硬件能力，需要设备 profile 或正常协议抓包再补设计和边界测试，不能照搬参考中的无界重试或吞状态错误。
