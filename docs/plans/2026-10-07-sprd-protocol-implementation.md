# SPRD 实施记录

日期：2026-10-07；任务：SPRD-01～04。设计见同日 sprd-protocol-design。

## 进度

- SPRD-01：已读取仓库规范、通用协议/传输/存储和 CLI registry。初始工作区干净，HEAD=9bb6818；从该提交创建 codex/sprd-support。
- 参考事实：已交叉读取 C# BSL 和 C++ Bootmode/BMPlatform；确认 0x96 DA info、奇数补零差异及 80/88 字节 selector 差异；容量单位、设备地址和 VID/PID 不猜测。
- 设计范围已按用户“加入 SPRD 并遵循当前架构”的授权确定，初始 ignored 测试先因缺少模块失败，随后实现；未引入参考源码或二进制。
- SPRD-02：完成两个 .NET 8 程序集。公共模型、显式 profile、资源容器/Provider、同步帧及门面、完整 Loader 线路、命名存储操作和 Generation 只读视图均已实现。ProtocolType 末尾追加 Sprd，保留既有数值。
- SPRD-02：帧处理验证长度/校验/转义/末尾标记，保留碎片与粘包；ACK/NAK 明确，写入未知结果不重试。FDL2 0x96 只在 EXEC 接受并解析 legacy/TLV 信息；日志帧有上限且不记录原文。
- SPRD-02：Raw 非定位源保留四字节前缀；Sparse 在最多 262144 chunks 的元数据预算下预检 CRC，再展开流式写入，DONT_CARE 写零；校验 Fill/DONT_CARE 也检查操作预算。奇数补零 profile 拒绝奇数长度 Loader/Raw，避免改变写入范围。
- SPRD-02：资源在连接 I/O 前获取验证，所有打开流正确释放，异步迟到资源按容器所有权释放。传输开始后的源/输出/取消/回调/协议失败使会话失效。审查新增关闭失败复现测试后修正：关闭失败保留 Faulted，必须成功关闭才能再次连接。
- SPRD-03：CLI registry 注册 sprd/spreadtrum/unisoc；独立 Adapter/Loader Provider、显式 FDL 地址/入口/容量单位/64 位布局及兼容开关，不改变默认 Qualcomm 选择。缺少容量单位、跨协议资源、sector/browser/LP 和 download 重启等在非交互连接前拒绝。帮助/补全明确当前能力。
- SPRD-03：新增 `docs/sprd.md` 的 API/CLI/所有权/失败恢复/限制；README、AGENTS 和 solution 已同步。中英文资源键一致。
- SPRD-04：43 项协议测试、8 项 CLI 测试通过；最终整体 Release 构建、资源键、ignored 和 staged diff 审查通过。本工作树只有这两个本地测试工程，无历史 Qcom/MTK 测试源可运行。交付提交使用 `feat(sprd): add serialized BSL and streaming partition support`，确切提交号可由包含本记录的 Git 提交恢复。

## 验证证据

2026-10-07，本地模拟证据：

| 验证 | 结果与行为 |
| --- | --- |
| `dotnet test .tests/GeekFlashCore.Protocol.Sprd.Tests/GeekFlashCore.Protocol.Sprd.Tests.csproj -c Release --no-restore` | 43/43；Loader 顺序、黄金帧、碎片/粘包、NAK/坏帧/超时、不重发、取消、Provider 重入/迟到释放/并发 gate、关闭失败恢复、流所有权、64 位选择/偏移、分区容量与重复、DA info、禁转义、Sparse 与预算、视图代数及重启、NV 拒绝 |
| `dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore` | 8/8；profile/alias、默认协议不变、跨协议选项、非交互 Loader 前置校验、有限预算、容量单位与 sector 前置拒绝 |
| 64 MiB Raw（全部 0x7e，转义最坏载荷） | 64 MiB 流式成功，最大传输帧 8200 字节；一次采样当前线程累计分配 525616 字节（含模拟 ACK，池已预热），elapsed 324 ms；测试约束累计分配小于 4 MiB。仅模拟/CPU 结果，不代表 USB 吞吐或峰值进程内存 |
| 1 TiB 虚拟 Sparse Fill | 44 字节容器，在 5 ms 操作预算内触发超时且没有存储 I/O，保持会话可用；不按展开大小分配 |
| `dotnet restore GeekFlashCore.slnx` / Release `--no-restore` build | 最终构建成功，0 warning / 0 error |
| `dotnet .../geekflash.dll help sprd` | 返回 0，正确显示中文入口、资源、布局、容量单位和限制 |
| resx 键 / `git diff --check` / ignored | SPRD abstractions、core、CLI 中英文键一致；差异检查通过；`.tests`、bin/obj/logs 保持 ignored，测试工程未加入 solution 或 Git |

验证工程固定为 ignored `.tests`；此工作树初始不存在历史测试，不能把当前 51 项描述为历史全仓回归。没有实机证据。

## 未决风险与恢复

恢复入口：先读设计、此进度、docs/sprd.md、git status 和 SPRD 测试。下一步用合法匹配 FDL 和确认地址/容量单位的真实设备，先只读确认 BROM/FDL1 首包、FDL2 EXEC、禁转义切换和 64 位布局，再验证备份分区读写/取消/重连。参考行为与设备保证须区分。

真实板级 FDL、签名、禁转义/奇数补零兼容、USB 重枚举及吞吐待硬件验证；FDL1 KEEP_CHARGE 默认开启，不支持的 profile 由宿主显式关闭。ITransport 同步 Write 的取消延迟受底层写超时约束。无取消参数的只读块设备使用有限操作预算。DIAG、NV 专用变换、重分区、原始高速下载、整盘 sector 与 CLI browser/LP 不在本次能力内。用户需按设备确认 profile，不能通过反复 NAK 或容量试探获得。
