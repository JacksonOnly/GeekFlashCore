# GeekFlashCore MTK 实施进度

日期：2026-10-05。设计：`2026-10-05-mtk-protocol-design.md`。BROM 对应：`2026-10-05-mtk-brom-method-mapping.md`。宿主阶段框架：`2026-10-05-mtk-exploit-framework.md`。

工作区：`C:\Users\a1375\.codex\worktrees\8d7a\GeekFlashCore`。分支：`codex/mtk-protocol`。初始 HEAD：`173d1bb`。

## 授权范围与基线

用户最初授权实施 MTK-01～12，并明确漏洞部分只预留一个接口；后续明确要求参考 mtkclient 与 penumbra 设置接口并在合适阶段调用，新增 MTK-13 只搭建宿主框架，仍禁止任何漏洞实现或利用。本要求覆盖原草案中的漏洞/patch/payload 实施内容。生产 MTK 使用 LibUsb；标准认证、已合法加载的 DA 扩展通信、RPMB、seccfg 保留。未连接或写入真实设备。

本次进入指定工作区时有两份未跟踪 MTK 计划，没有其他生产修改。由 detached HEAD 建立上述分支。原来没有本地测试工程；本轮将已有 Qcom/CLI/Core/LP 测试源复制到 ignored `.tests`，并创建 MTK 模拟测试。参考仓库只读；已有参考修改保留，revision 不等同于所有工作树文件的指纹。

## 任务进度

| ID / 日期 | 结果与行为结论 | 验证证据 / 限制 |
| --- | --- | --- |
| MTK-00 / 2026-10-05 | 设计、范围修订、来源和支持矩阵同步 | 当前文档、BROM 方法对应、NOTICE；没有硬件证据 |
| MTK-01 / 2026-10-05 | 新增稳定 contracts/options/models/providers/errors；有限配置、敏感 owned buffer、借用源；公共类型不暴露第三方包 | 契约/边界/清零、同步/异步资源测试 |
| MTK-02 / 2026-10-05 | USB 物理身份、唯一匹配、同配置/接口/alt 的 bulk pair、分离 CDC control、ZLP、有界重枚举工厂；旧 factory 签名保留 | 模拟后端覆盖歧义释放、CDC/alt、错误 endpoint 组合、释放异常；实际驱动/重枚举待验证 |
| MTK-03 / 2026-10-05 | 完整 BROM 命令目录和标准方法、BE query/word、LE register status、64-byte DA/auth 上传、SLA、显式 watchdog、串行失效 | 非零 FD hardware version、57个值比对、1D0D/7017、MEID/SOCID/日志、UART、jump、partition/reset/16-bit watchdog |
| MTK-04 / 2026-10-05 | D8/DC 新旧/v6 DA metadata、版本/subcode/alias 选择、bounded Legacy marker、EMI 和可重开窗口 | 真实参考 v5/v6/Oppo DA 容器本地解析；截断、count/region/签名、版本、EMI、窗口夹具 |
| MTK-05 / 2026-10-05 | 独立 XFlash/XML DA1/DA2 初始化、认证、存储、read/write/erase/reboot；严格 frame/status 和 XML lifetime | 完整模拟线路、MESSAGE 上限、DA.SLA item、DTD/重复项、END timeout、文件路径/大小拒绝；XML EMI 见矩阵 |
| MTK-06 / 2026-10-05 | 独立 Legacy eMMC 初始化/EMI/DA2、分区选择、校验和读写、进度擦除、系统重启 | 完整 Legacy 模拟线路、checksum/short nonseek EOF 失败；其他 Legacy 介质明确拒绝 |
| MTK-07 / 2026-10-05 | eMMC/UFS 几何、primary GPT、目标解析、Raw/Sparse 流式写、generation block view、任意字节对齐读 | 512/4096 block、小 buffer、GPT CRC、Raw/Sparse 大源与 DONT_CARE、最终 NAK/旧视图失效 |
| MTK-08 / 2026-10-05 | 初次授权只预留 `IMtkExploitStrategy`；后由 MTK-13 扩充框架 | 初次交付无核心调用；最新契约和调用范围见 MTK-13 |
| MTK-09 / 2026-10-05 | 可选 Extensions 依赖 Abstractions 与 Shared CRC 工具，不依赖协议核心；已加载 DA ACK/context、允许范围内的内存/寄存器、SEJ、显式 RPMB key derivation | XFlash/XML ABI、已加载 DA2 上下文、边界、过期通道、硬件 cipher 同 gate 不重入；硬件算法待验证 |
| MTK-10 / 2026-10-05 | 已存在 32-byte key 的 RPMB Authenticate、256-byte data block、区域/容量/chunk/final status、失败不重试 | XFlash/XML read/write transcript、UFS 显式容量、final NAK 失效；不提供 ProgramKey |
| MTK-11 / 2026-10-05 | seccfg v3/v4 SW/SEJ cipher、原文验证、generation plan、预写快照、备份、最小对齐写、完整回读 | v3/v4、尾部保留、原 hash、硬件 cipher mock、最小 sector 写；写后失败 MayHaveWritten 且失效 |
| MTK-12 / 2026-10-05 | CLI 注册、纯 USB discovery/devices、文件资源、MTK 参数/命令与帮助；Qcom 默认保持；README/AGENTS/来源文本 | CLI 参数冲突、枚举歧义/物理选择、旧 Qcom 回归；完整验收见下一节 |
| MTK-13 / 2026-10-05 | descriptor、四个连接 checkpoint、作用域 USB/BROM/DA context、明确 outcome、重连终止、校验后 DA 替换与标准安全复查；同步/异步共用 | 38 个无漏洞/无设备 I/O 的观察器测试通过；MTK120、全量499通过；Release0警告/错误；三种 DA 顺序和默认写入字节等价；没有策略实现或真实设备操作 |

## 实施中的纠正

最初 BROM 只实现 DA 连接子集。用户指出遗漏后，补齐完整命令目录与标准方法，增加 `IMtkProtocol.UseBromSession`，并写入逐方法对应。

GET_HW_CODE 的第二个 BE halfword 是硬件版本，已纠正原错误 status 解读。Probe 按 FD → D8 → FE → FF → FC 查询；MEID/SOCID/log 不自动读取或输出。BROM 的普通成功 status 范围按参考方法处理，jump/UART/新日志等保留零 status 要求，register access 的两次 status 按 LE 读取。

SEND_DA 的 1D0D 允许通过宿主同步/异步签名完成当前上传，不重发 D7；SLA 的 7017 表示已认证。奇数字节资源/DA 保留参考 padding，上传使用 64-byte 包与专门的 bulk ZLP。checksum 不匹配一律失败，不沿用参考中的 Warning 后继续。partition checksum 的短尾循环按实际剩余 bytes 修正。

重连 Probe 清除旧阶段/命令；USB 工厂先校验协议选项，构造失败释放新 transport；资源/认证的失败、超时和迟到结果覆盖清零。预取消/发包前范围错误保留 Ready 会话；发送后异常关闭 transport、清除资源和缓存、增加代数。seccfg 写后错误只失效一次，不掩盖取消、不自动重试。

CLI 原来的 1500 ms 是 Qualcomm 初始探测预算，不能复用于整个 MTK 连接。已用先失败的工厂测试修正：MTK 默认 60000 ms，显式 `--connect-timeout` 优先；Qcom 默认保持 1500 ms。MTK 每次资源请求默认 30000 ms，不接受无限时限。

## 支持矩阵

“已实现”指源代码与本地夹具/模拟证据，不表示真实设备验证。

| 能力 | 当前支持 | 条件 / 明确限制 |
| --- | --- | --- |
| BROM / Preloader | 标准查询、word/register、UART、auth/SLA、DA/jump、partition、reset flag | 需要真实命令支持；未知目录命令没有猜测实现；watchdog 地址/值/位宽显式 |
| DA 容器 / EMI | D8/DC、旧/新/v6、Legacy marker；Preloader EMI 窗口 | 不扫描参考目录；宿主 source；过大 marker scan 要求显式 DA mode |
| Legacy | eMMC、NOR 普通读写、SDMMC 标准写入、明确布局 PMT、标准寄存器、系统重启 | SDMMC read/erase、NOR erase、NAND/OOB/BMT/IoT 无确认线路而拒绝；其他重启模式拒绝 |
| XFlash | eMMC/UFS/SDMMC/NOR、逻辑 NAND/ECC、DA1/EMI/DA2、标准只读查询、读写擦除/重启、合法 SLA | NAND 写/擦除显式启用；NOR 擦除几何显式提供；无物理页/OOB；厂商环境/返回码待设备验证 |
| XML | eMMC/UFS、只读 NAND、原生分区表、标准寄存器/系统属性/FW/HW 查询、严格 lifetime/file/progress、合法 SLA | NAND usable/BMT 未确认，仅只读；PowerOff 拒绝；使用 DA runtime 的 DRAM 初始化；无确认的外部 EMI upload |
| 存储/GPT/镜像 | eMMC 1/2/4～8；UFS 1～3；有界 primary/backup GPT、实际 entry LBA、CRC；Raw/Sparse 进度 | RPMB 排除普通 region；Flush 仅保证协议应答；不可寻址 Sparse 在写前拒绝，Raw 保留首部并流式 |
| 重枚举 | 独立有界 LibUsb 工厂，同 serial 或 bus/port 与允许 PID | 不发送 DA 提速/USB reset、不自动续接 DA；旧视图必须失效 |
| DA extension | XFlash/XML 已加载兼容 ABI 的 ACK/CTX、内存/register/SEJ/key derivation | 不 patch/load extension，不内置二进制；地址与实际 DA2/profile 匹配 |
| Hardware crypto | 扩展 SEJ 和 backend key derivation | 无独立主机 GCPU/DXCC/TZCC 驱动；基址由宿主提供；算法/硬件待验证 |
| RPMB | eMMC/UFS 独立 Authenticate/read/write/erase，256-byte data blocks | erase 为已认证的有界零写；UFS 每 region 容量显式；不烧录 key；简化 ABI 不提供宿主 nonce/MAC/counter 的完整帧校验 |
| seccfg | v3/v4、软件与已提供的 SEJ cipher、lock/unlock plan/apply | 不推断 Android UI/AVB 最终状态；写失败可能已改变设备；调用方承担备份持久化，CLI 使用 durable FileStream |
| A/B boot control | version/slot/CRC 校验、misc/para 最小扇区备份/写/完整回读 | 显式分区 range；未知写结果失效且不重试；CLI FileStream 在写前 durable flush；普通 Stream 持久化由宿主负责 |
| CLI | mtk-probe/capabilities/memory/rpmb/seccfg/query/property/register/pmt/slot，通用存储命令、MTK devices | 标准材料显式文件；无厂商账户/签名服务；BROM 与派生 key 走类型化 API；查询结果仅写文件 |
| 宿主阶段框架 | 策略契约、context/result 与四阶段调用 | 显式注入才调用；无策略实现、默认注册、认证绕过、攻击载荷、patch 生成或 CLI 开关 |

## 验收命令与证据

目标测试先定义行为或复现缺陷，再修改实现；例如 fresh Probe/非法 options 两项先失败再通过。BROM 方法新增时先记录缺少 UseBromSession 的编译失败，再实现并完成线路测试。测试工程与所有夹具留在 ignored `.tests`，不加入 solution、NuGet 或 Git。

MTK-01～12 的首次验收通过 **461 项**，0失败/0跳过。下表保留首次交付证据；MTK-13 的最终复验单独记录在框架修订文档。

| 命令 / 检查 | 最终结果 |
| --- | --- |
| `dotnet test .tests/GeekFlashCore.Protocol.Mtk.Tests/GeekFlashCore.Protocol.Mtk.Tests.csproj -c Release --no-restore` | 82通过/0失败/0跳过，含模拟 LibUsb 后端 |
| Qcom / CLI / Core / Android LP 对应 Release tests | 251 / 64 / 9 / 55通过，均0失败/0跳过 |
| `dotnet build GeekFlashCore.slnx -c Release --no-restore` | 通过，0警告/0错误 |
| 中英文资源 keys / format placeholders | 5 / 8 / 1 / 17 / 171 个 key 成对，共202，placeholder 对应；最终 CLI 默认预算文本复核通过 |
| `git diff --check` / diff / logs | 完整含新文件差异与各提交 staged 门禁通过；仅阶段/失效日志，无身份/auth/key/challenge/完整 XML |
| `git ls-files .tests` / ignored 状态 | 无受跟踪测试；`.tests`、bin/obj、临时格式化副本/日志均 ignored |
| CLI `--help` | 成功退出；包含 MTK mode、材料、USB topology/interface、RPMB/profile 与命令帮助 |
| 真机连接/认证/擦写/RPMB/seccfg | 未执行 |

最终单独运行 `Raw64MiB` / `Sparse64MiB` 夹具：Raw 64 MiB 的托管分配 1,053,728 bytes，源最大 read 4 KiB；expanded Sparse 64 MiB 使用 44-byte 原始 FILL 源，分配 1,319,136 bytes。模拟 transport 不记录大数据包，并预先准备应答；这是当前线程分配证据，不能视作 USB 吞吐或完整进程峰值。此前运行约 1 MiB，差异包含 JIT/池化状态；两者都未按镜像总大小分配。

## 来源与提交

来源/版权和具体参考文件见根目录 `NOTICE-MTK.md`，附 mtkclient GPLv3 文本 `licenses/MTK-GPL-3.0.txt`。不分发参考 DA/auth/cert/密钥文件，不修改外部参考树。

已按独立能力提交：

| 提交 | 能力 |
| --- | --- |
| `6f92110` | identified USB、bulk/control、ZLP |
| `6a6d3de` | MTK 稳定契约、57个 BROM 命令与来源说明 |
| `fb15d34` | BROM、Legacy/XFlash/XML、DA/EMI 与流式存储 |
| `b286cf3` | 已加载 DA 扩展、SEJ/key derivation、RPMB |
| `24e1bd7` | seccfg 原文校验、变更计划、备份/最小写/回读 |
| `8ac4fb6` | CLI 注册、USB 发现、参数/命令与首次交付文档 |

MTK-13 的框架修订与文档在独立 `feat(mtk)!: add scoped host extension checkpoints` 提交中；footer 标明原预留接口签名变化，提交号通过 `git log` 查询，避免在自身提交内容中写循环 hash。没有 push、PR、包发布或真实设备操作；验收后工作区仅有 ignored 本地测试/构建产物。

## 未决风险与恢复入口

2026-10-05 STD-01～06 标准功能补全：DA `m_start_offset` 语义和 raw index 修正、DA channel 线程/代数、主备 GPT、PMT/XML 原生表、DA 查询/寄存器、存储介质、A/B、RPMB erase、传输进度和 CLI 见 [最新补全证据](2026-10-05-mtk-standard-completion-implementation.md)。本轮测试共 618 项通过（MTK184/CLI119/Qcom251/Core9/LP55），无失败/跳过；不含缺失策略导致无法编译的既有 ignored KamakiriTests.cs，排除说明见补全文档；没有实现漏洞。上表为当前支持，旧验收表保留其历史结果。

2026-10-05 新增 USB-01～03：CLI Windows native DLL 部署与按 MTK 刷机硬件 ID 自动安装 libusb-win32 的设计、实现、验证及恢复方法见 [Windows USB 启动准备](2026-10-05-mtk-windows-usb-bootstrap.md)。设备过滤器范围遵循用户明确确认，不对共享 Ports/USB 类安装；不涉及漏洞策略实现。此项补齐运行依赖，驱动/UAC 与真机连接仍待验证。

同日用户真实日志已证明过滤器安装/注册表复核成功，第二次 BROM Probe 成功；尚无 DA 执行证据。REC-01～03 修复默认交互缺少 DA 文件选择、MTK 资源异常被隐藏以及协议启动前 USB 候选过期的处理，见 [CLI 连接准备与错误展示](2026-10-05-mtk-cli-connection-recovery.md)。CLI 101 / MTK 120 / Qcom 251 项通过，Release 与差异检查通过；仅协议创建前 NoDevice 有界重新枚举，已开始的协议通信不重试。

BROM-01～03 补齐 67 芯片标准目录、61 个普通 WDT 配置、FD 后初始化和非敏感芯片快照，修复 0x0950→0x6893 等 DA alias 在 parser/核心校验的缺失；资源选择前显示信息，交互 DA 提示沿用 REC。用户 14:06 日志的 matching DA 故障已用其实际文件和模拟目标复现/修复，真实硬件编号尚需新日志确认。MTK 131 / CLI 104 / Qcom 251 / Core 9 / Android Lp 55，全部 550 项通过；Release 0 警告/错误、差异和资源检查通过。设计与恢复入口见 [BROM 标准初始化](2026-10-05-mtk-brom-initialization.md)。既有 57 命令/标准方法及漏洞宿主接口保留，没有漏洞策略或真机 DA 上传。

- 无真机/脱敏抓包。驱动绑定、CDC 控制接口、native ZLP、物理身份与重枚举、包长/取消延迟和吞吐仍需硬件验证。
- LibUsbDotNet control timeout 是进程静态配置；typed MTK 连接拒绝非有限值，不修改全局。不能承诺中断正在阻塞的 native call。
- FC/Preloader 字段、Legacy 初始化/EMI、XML DRAM、厂商状态、XFlash 28-byte reboot 与 Python 32-byte 形式存在参考差异；本地测试锁定当前明确线路，需设备补证。
- 认证 Provider 需真实合法响应。没有账户/在线签名服务或跨厂商认证保证；Resource/Connect 预算有限，迟到敏感结果清零。
- 普通 API 不提供 NAND/NOR/IoT 或全部芯片特殊 watchdog；C8/B1 只返回原始响应。Preloader partition 参考线路没有最终确认。
- 扩展依赖已加载兼容 ABI，硬件算法与 UFS RPMB region/capacity 由宿主确认。简化 RPMB API 不宣称主机端完整帧认证。
- seccfg/RPMB 写入未知结果不可重试/伪回滚；备份与回读失败需重连检查。只修改格式支持的配置，不保证 bootloader UI/AVB 状态。

恢复时依次读取 AGENTS、设计、本文、BROM 方法对应与宿主框架修订，检查 `git status --short` 与最近提交；先补充上述设备证据或具体 profile/夹具，再扩大支持矩阵。不得添加默认策略或漏洞实现。宿主同步回调必须合作取消；核心只能在回调返回后拒绝超期结果。

2026-10-05 后续以 `penumbra-main` 为参考的正常能力补齐，新增 Legacy NAND/IoT、独立 SEJ/GCPU/DXCC、Scatter/native partition/eFuse、扩展新 ABI 和逻辑 Fill。历史缺口按日期保留；当前支持与未决风险以 [非漏洞能力补齐进度](2026-10-05-mtk-nonexploit-parity-implementation.md) 为准。
