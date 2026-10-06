# Qualcomm CLI 使用说明

CLI 可执行文件为 `geekflash`，构建目标为 .NET 10；协议库仍为 .NET 8。`geekflash --help` 优先显示通用命令，`geekflash help all` 查看全部连接选项及 Host 语法，`geekflash devices` 只枚举设备。

交互会话中 `reboot system|download|poweroff` 成功后直接退出 CLI，并正常释放会话和传输；`power reset|reset_to_edl|off` 及 `qcom power` 同样退出。失败或参数错误不会触发成功退出。

## 连接与资源

```powershell
geekflash --port COM7 --loader programmer.elf info
geekflash --usb 05c6:9008 --loader programmer.elf --read-timeout 5000 --write-timeout 5000 info
geekflash --protocol qcom --device-wait-timeout 30000 --loader programmer.elf interactive
```

## rawprogram 与 patch XML

联机后输入文件路径或通配符；也可以显式使用命令。以下示例假设当前目录是刷机包的 images：

```text
rawprogram rawprogram*.xml
patch patch*.xml
```

直接输入 `rawprogram0.xml`、`rawprogram*.xml`、`patch0.xml` 或 `patch*.xml` 同样有效；`qcom rawprogram ...`、`qcom patch ...` 与 `program rawprogram*.xml` 也支持。含空格的路径用引号包住，例如 `rawprogram "D:\ROM\factory images\rawprogram*.xml"`。非交互命令可用 `geekflash --port COM7 --loader programmer.elf --non-interactive rawprogram "D:\ROM\images\rawprogram*.xml"`，完成后再执行相同连接选项的 `patch`，或在同一交互会话依次运行两者。

每个通配符只匹配当前层目录，结果按文件名排序；多参数保持显式顺序，重复文件只执行一次。镜像相对各自 XML 所在目录解析，允许子目录，拒绝跳出该目录。先预检一份 XML 的结构、参数、镜像和 Sparse 计划，再按该文档顺序执行；多份 XML 逐份预检执行，不提供事务或回滚。成功执行的前序写入在后续失败后仍保留。

rawprogram 的适配命令包括 `program`、`patch`、`erase`、`nop`、`setbootablestoragedrive`、`fixgpt`、`xblgpt`、`getstorageinfo`、`getsha256digest`、`benchmark`、`power`、`firmwarewrite`。其他命令（包括没有文件执行适配的 read/peek/poke、配置和认证命令）跳过并汇总原因，不透传任意 XML。设备明确上报命令列表时，还会跳过未声明支持的命令；没有列表时允许执行这些适配命令，但不宣称设备支持。power 必须为最后一条执行命令，成功后结束会话。

空 filename 的 program 不写入。Raw 按 `file_sector_offset * SECTOR_SIZE_IN_BYTES` 定位，声明范围限制来源窗口，只补齐实际数据的最后一扇区，不把整个分区填零；扇区数为 0 时以设备剩余容量为上限。Sparse 按实际文件头判断（XML 的 sparse 为提示），复用流式 RAW/Fill/Don't Care 路径。需要 readbackverify 的 program 暂时拒绝，避免忽略校验要求。

写入进度在开始、传输和最终 ACK 成功后均显示当前分区、LUN 与镜像文件，例如 `写入分区 boot_a (LUN 0) | 文件 boot.img`；各条 program 切换时更新名称。XML 未提供 label 时显示 LUN 与起始扇区，filename 保留 XML 中的相对路径。普通 `write/program <partition> <file>` 的进度同样显示分区及文件名；sector 形式显示实际写入位置。

patch 文件仅发送 filename=DISK 的 patch，不修改本地 GPT 镜像；普通数字 patch 语法继续有效。支持数字、十六进制、尾随小数点、`NUM_DISK_SECTORS` 的加减表达式及 `CRC32(start,length)`，CRC 范围在发送前检查并由设备计算。所有 LUN、扇区大小、容量、offset 和整数运算都检查边界；首个命令失败或 RAW 中取消后停止并要求重连，不重放写入。

Core 宿主可调用同步 `IQcomProtocol.ExecuteRawProgram(IDataSource, Func<string,IDataSource>, progress, cancellationToken)` 与 `ExecutePatchFile(IDataSource, progress, cancellationToken)`。方法持有整个文件执行的会话 gate；只释放自己打开的流，调用方拥有 IDataSource，镜像 resolver 必须返回内容稳定、可重开的资源。返回结果含执行数、实际写入字节数与带序号/原因的跳过条目。

## 命令显示与交互编辑

联机、info 和 help 将通用命令置前，Firehose 摘要按“设备支持的命令 => Host 支持的命令”显示已实现交集，例如 `program => write / rawprogram`。设备未上报列表时只显示未知提示，不展开包含 xblgpt 等厂商命令的 Host 候选列表。`help qcom` 查看设备已报告的映射语法，`help patch` 等查看单个命令；`help all` 的全部列表是 Host 能力说明。

交互命令行支持 ↑/↓ 浏览本会话最近 200 条命令，↓ 越过最新记录恢复当前草稿；Tab/Shift+Tab 循环命令补全，←/→、Home/End、Backspace/Delete 编辑，Esc 清空。命令历史仅存在内存，不保存到磁盘，Loader、认证等资源提示不进入历史。输入或输出重定向时保留逐行输入，不运行补全；取消仍使用 Ctrl+C。

2026-10-06：alioth 的 12 份 XML 已使用模拟传输及合成镜像验证（rawprogram1/2 的末尾 xblgpt 亦保持顺序）；没有本轮真实设备刷写证据，CRC 方言、厂商响应和实际终端显示需真机复核。

自动发现仅接受已识别的 Qualcomm EDL COM 设备；自定义 VID/PID 使用显式 `--usb`。串口与 USB 互斥，USB 同样使用传入的读写超时。

超时单位为毫秒。同步 I/O 和设备发现必须为正数；资源请求另外支持 `-1`，表示等待至调用方取消：

| 参数 | 默认 | 用途 |
| --- | ---: | --- |
| `--device-wait-timeout` | 30000 | 等待热插拔设备的总预算 |
| `--connect-timeout` | 1500 | 初始协议探测配置；普通线路单次最多 250 ms、最多 4 次；Oplus 被动探测和启动读取使用完整预算，禁止初始化前主动 NOP；并非整个连接的总时限 |
| `--resource-timeout` | 交互不限时；非交互 15000 | 每次 Loader、Digest、Sign 或认证资源请求；显式 `-1` 不限时，正数启用预算 |
| `--read-timeout` / `--write-timeout` | 10000 | 单次同步 I/O；XML 完整响应共用一次读取预算 |

Configure 有有限次数回退，多 LUN 查询逐个执行，因此连接累计耗时可能大于探测超时。大镜像持续传输不设统一总时限。Ctrl+C 在数据块和响应包边界生效；已阻塞的同步 I/O 最多需等当前单次超时结束。

资源提示仅在交互终端使用。脚本请显式提供资源并传入 `--non-interactive`，缺少资源立即失败，不会等输入；stdin 重定向同样不弹出资源提示。常见退出码：成功 0、执行失败 1、参数错误 2、取消 130。

手动输入默认没有 15 秒限制，Ctrl+C 可以取消。显式配置的资源超时会报告具体预算并返回执行失败，区别于用户取消。Core API 默认资源预算仍为 15000 ms；宿主可设置 `ResourceRequestTimeoutMilliseconds = -1`，生命周期取消和迟到结果释放仍有效。设备自身可能有独立等待时限。

Sahara 探测成功后、Loader 上传前会输出完整身份信息；`info` 和 `probe-sahara` 同样显示厂商名称、芯片名称和完整 `PkHash`。PkHash 是 Sahara `ReadOemPkHash` 返回的原始字节（现有模型字段为 CaHash），按原始顺序显示大写 HEX，不重新计算 Hash。序列号、SBL、MSM ID、OEM ID、Model ID、AntiRollback 和 SoC HW Version 使用 `0x` 前缀的大写 HEX，64 位序列号完整保留；Firehose 信息中的序列号也使用 HEX。

名称优先从 Sahara OEM/SoC HW/MSM 标识映射，无法识别时使用已有 Loader 名称或显示“未知”，原始 ID 仍保留。共享的 OEM ID 只显示对应厂商组，例如 OPPO / OnePlus / realme；“厂商协议策略”与硬件厂商名称分别显示。通用 Sahara 诊断日志中的身份 ID 改为 HEX，Hash 仍仅记录长度。

通信策略按 `--vendor` → Firehose 启动证据 → 已接受 Loader 厂商 → Sahara OEM 顺序判断。全部未知时，在 Configure 和认证之前列出厂商，请输入编号或名称；通用设备可以选 `Generic`，留空或 Ctrl+C 取消。非交互或 stdin 重定向不能回答资源提示，未知厂商必须显式指定 `--vendor Generic` 或实际厂商。Sahara 的 OPPO/OnePlus/realme 共享 OEM 回退为 Oplus，Firehose 的 OnePlus/Nothing 特征仍优先。选择保留至断开/失效，重配置不重复提问。

Core 的 `IVendorSelectionProvider` 是可选宿主接口，使用既有资源超时、取消和迟到结果观察。原构造函数及厂商解析入口保留，未配置此 Provider 的旧宿主仍回退 Generic；新增完整构造函数末尾可传 `vendorSelectionProvider`，返回有效的非 Auto 枚举。

## Patch PBL

连接期间，CLI 根据 Sahara 芯片标识匹配所有品牌的 SDM845、SDM710 和 SM6125（Snapdragon 665）。选定并校验 Loader 后显示“正在进行 Patch PBL”，随后自动执行，无需再次确认。其他芯片或芯片身份未知时继续原有连接流程。

710 / 845 使用内置片段完成固定 175 次交互，关闭传输、等待 1000 ms、重新打开后上传 Loader。665 最多进行 100 次触发交互，再发送抓包提取的六块补丁资源；设备再次 Hello 后保留首个 READ_DATA64 继续用户 Loader 上传，不关闭或重开。Patch 后的 Loader 请求限制在文件范围内，单次不超过 4 MiB。取消、短读、超时、非法报文或重开失败会中止连接；只有 Loader、Firehose 配置与存储初始化完成后才报告联机成功。

Core 宿主通过 `QcomProtocolOptions.EnablePblPatch = true` 启用，默认值为 false；同步 `ProbeSahara → UploadSaharaImages → ConfigureFirehose` 和异步 `ConnectAsync` 共用 Patch 线路。Firehose 已运行会话与 Digest 续接保持其原有入口。665 的资源、重复计数与提取命令见 [抓包分析](plans/2026-10-05-sm6125-capture-analysis.md)。当前证据来自用户抓包、参考源码和模拟传输，本实现的跨品牌效果、Command→ImageTxPending、Patch 后重开与 USB 重枚举仍需真机验证。

## Oplus 模式

两种 Oplus 模式均在 Configure 和存储查询前执行：启动日志 → Digest → verify XML → Sign（零填充至 4096 字节）→ verify passed → sha256init → Configure。Digest 失败、Sign 超时或半帧均立即中止连接，不发送其他命令。

Legacy 初始化按用户提供的正常抓包发送 `<verify EnableVip="0"/>`，不带 `value="ping"`；Verify、sha256init 和 Configure 的声明为 `<?xml version="1.0" encoding="UTF-8" chimerais="power" ?>`，保留结尾空格。Pt 保持其原有参考线路的 `verify value="ping" EnableVip="1"` 和分区映射。

Legacy 在 Sign 已确认后发送 sha256init，最多等待 1500 ms（若 read timeout 更短则使用更短预算）。抓包中该命令只有完整 `ERROR: Failed to run the last command -1` 日志、没有 ACK，随后继续 Configure；此特例有意保留并记录 Warning。纯静默、其他错误、NAK、RAW、半帧和设备重新等表均中止。此例外不适用于 Digest、Verify、Sign 或 Pt 的 sha256init。

Legacy 自动配置从 UFS 开始，默认 Configure 依次发送 ZlpAwareHost=1、SkipWrite=0、SkipStorageInit=0、MaxPayloadSizeToTargetInBytes=1048576、MemoryName=ufs；协商上限和非默认显式配置仍有效，UFS 被完整 NAK 拒绝时仍可有界回退。普通线路/Pt 的原有默认配置保持。Digest 使用文件精确长度，不按启动提示中的 8192 强行填充；Sign 零填充到 4096 字节。

Pt 保持按分区 Digest 索引映射和权限校验。只指定 `--oplus-digest` 时，默认选择 Pt；Pt 和 Legacy 均要求提供二进制 Sign，内置 Oplus Sign 查找已停用。使用 `--oplus-sign` 指定文件，或在缺失、文件无效、设备拒绝时交互选择；无法交互且缺少文件会失败：

```powershell
geekflash --port COM7 --loader programmer.elf --oplus-digest Digest.bin --oplus-sign Sign.bin --non-interactive info
```

Legacy 使用 Rector 的包计数/NOP/Digest 线路，Digest 文件作为不透明签名数据，允许没有 Pt 分区索引。Legacy 必须提供 Sign，可用参数指定或交互选择：

```powershell
geekflash --port COM7 --loader programmer.elf --oplus-mode OplusDigestLegacy --oplus-digest Digest.bin --oplus-sign Sign.bin --non-interactive info
```

CLI 不再暴露 Legacy 调整选项，固定使用表容量 53、初始计数 0、每段 256 扇区、运行期 Digest 独立回复窗口 1000 ms、XML 字节截断上限 4096、内置兼容 NOP。

### 续接等待第一张 Digest 的 Loader

Loader 上传并输出 `VIP is enabled, receiving the signed table` 后，设备已离开 Sahara。CLI 如果在输入 Sign 时退出，重新打开串口可能收不到任何启动日志；延长热插拔等待时间不会让 Loader 重播日志。

确认设备仍在等待第一张 Digest、且上一轮尚未发送 Digest 时，可显式续接：

```powershell
geekflash --port COM77 --vendor Oplus --oplus-mode OplusDigestLegacy --oplus-digest "D:\刷机\方案\奇美拉\480\Digest.bin" --oplus-resume
```

Sign 可通过提示手动输入，也可加 `--oplus-sign FILE`。续接跳过 Sahara 和启动日志，不重新上传 Loader，首包是 Digest，之后 Verify → Sign → sha256init → Configure。Pt 同样支持；缺少芯片信息时要求手动 Sign。Core 对应 `OplusDigestConfiguration.ResumeAwaitingDigest`，每个协议实例仅允许一次，认证/RAW 失败后再次 `connect` 不会盲目续接。

显式续接的首个 Digest 若被原 XML 会话完整 NAK 拒绝、报告 data Hash mismatch，且随后完整 XML 明确表示设备已转入签名表接收，新版会重新打开同一 Digest 并发送一次；这对应 `bug.txt` 中第一次拒绝、第二次相同命令成功的情况。只有不超过 8192 字节的完整单包初始 Digest 可以使用此恢复；旧的启动提示不算新的接收状态证据。第二次仍失败、无接收状态提示、RAW、半帧、静默或取消均停止，不发送 Verify/Sign，不发送额外 NOP/reset。

RAW 中断或未知状态仍应重新进入 EDL，再使用普通带 Loader 的连接命令。普通 Oplus 静默探测只输出状态说明；上述恢复仅用于显式续接的首次初始化，不用于已认证会话的重配置或一般读写。

### Legacy 包计数

初始 Digest 被确认后计数归零，每条 XML 和每次完整输出数据传输各计 1（包括 verify、Sign、sha256init）；策略安装和存储回退复用同一计数器及已认证资源。串口/USB 分块不重复计数，Sparse 空洞与设备输入 RAW 不计输出数据包。Legacy 完成初始化后，读取/GPT、program、NOP、patch、storageinfo 和通用 XML 共用表边界检查，避免只读操作耗尽签名表。容量 53 时，计数 51/52/53/54 分别先发 3/2/1/0 个 NOP，再发第 55 包 Digest。独立 ACK 后确认 NOP 计为 1；没有独立 ACK 则保留队列，需 handler 日志和完整成功响应关联确认。Rector 公开 read 默认未开启 Digest；此处按用户实机 53 包耗尽证据将其底层算法应用到本项目已认证 Legacy 会话。

完整且可恢复的 Sign 验证失败最多允许一次手动替换；读取 NAK 后续提示的预算为 1000 ms。如果设备再次进入签名表接收状态，先重发原 Digest，再发 verify 和新的 Sign。替换后仍失败需要重新进入 EDL。

参考源码中的完整日志错误后继续确认、声明特殊属性、XML 截断和 Flush 顺序均有意保留；未知 NAK、半帧和 RAW 错误会让会话失效。签名失败只重发当前 XML 一次，不重放 RAW。普通 Digest、VIP、Oplus 三种线路互斥。

## 调试文件日志

直接运行 `geekflash` 时不再询问模式编号。上传 Loader 并读取启动日志后，只有 VIP 已启用、Sahara OEM 属于 Oplus、解析的 Loader 也属于 Oplus/OnePlus，才请求 Digest 和 Sign 文件。Core 在发送首包前解析 Digest：能建立非空且有效的分区索引时自动使用 OplusDigestPt，否则使用 OplusDigestLegacy。读取失败、短读或非法长度直接中止，不回退 Legacy。文件路径留空或 Ctrl+C 取消；文件参数有效时直接使用。普通设备无需选择模式。显式 `--oplus-mode`、Oplus Digest、普通 Digest 或 VIP 参数优先；仅指定 `--oplus-digest` 的原有 Pt 默认保持不变。非交互、重定向不增加资源提问。身份未知或已运行的 Loader 无法自动满足这些条件，需显式指定模式。

启动日志明确以 `ufs:` 报告存储时，自动配置从 UFS 开始，避免先配置 eMMC、存储查询失败后再切换的多余命令；显式存储类型不覆盖，无证据仍沿用原有协商与回退。详细文件日志记录每次小米内置认证的通过状态和耗时，不记录签名内容；控制台仍只显示必要的联机阶段。

Loader 启动日志使用 `--read-timeout` 的总预算（默认 10000 ms），不再用 250 ms 探测窗口代替。刚上传 Loader，或已收到任何启动字节（含半帧）时，等待失败也不发送 NOP；读到 VIP 等待签名表却没有所需模式/资源时，直接说明资源问题。只有未上传 Loader、完全静默的普通会话保留短 NOP 探测回退。

设备已进入 Loader 后通常不会重新发送 Sahara HELLO，关闭 CLI 再运行 `--oplus-mode` 不能使其回到 Sahara。若上次已经出现 signature/authentication 失败，应重新进入 EDL 再连接；仅确认 Loader 仍在等待首张 Digest 时，才使用既有 `--oplus-resume`。CLI 不自动判断或重放表。

默认在可执行程序所在目录的 `logs` 中生成每次运行唯一的日志文件，并在启动时打印完整路径。不需要 `--verbose`，文件也包含 Debug 级别的命令名、发送字节数、响应状态/rawmode、读取预算、耗时、阶段顺序和完整异常堆栈；`--verbose` 只控制控制台详情。

```powershell
geekflash --loader programmer.elf --oplus-mode OplusDigestLegacy --oplus-digest Digest.bin --oplus-sign Sign.bin --log-file .\logs\oplus-debug.log info
```

指定的文件以追加方式写入，阶段与错误立即刷新；Debug 累积 64 条或下一条事件到来时距上次刷新超过 250 ms 则刷新，退出时刷新剩余内容。单文件达到 16 MiB 后继续写同目录带运行标识的分段文件。日志不包含完整 Sign、Digest、认证载荷或自定义 XML，但保留已知签名失败状态和错误码。读取在线日志的工具需要允许共享写入。日志目录/文件保持 Git ignored；若路径不可写，CLI 会在连接设备之前报错。

默认控制台显示中文阶段、设备信息、资源选择、进度和简短结果；异常只提示一次并附日志路径，堆栈留在文件。`--verbose` 显示额外宿主/协议诊断，但不显示原始设备文本或堆栈，分区查询和读写期间也不会打断进度。文件中的设备诊断标记为 `DeviceDiagnostic`，附原始级别和文本长度，便于 UI 宿主单独筛选。文件还记录读写目标、文件路径、命令名、包计数和响应预算。连接后不自动刷出全部命令用法，需要时输入 `help`。

Sahara 阶段完成显示“设备识别完成”，与 Firehose 连接成功区分。信息使用“序列号”“MSM ID”等标签，HEX 和完整 PkHash 保留。Loader 由设备分段请求，确认完成时进度为 100%，数量显示实际发送字节，不因文件中未被请求的尾部显示 99.4%。

交互选择 Loader、Digest 或 Sign 时，无效文件路径可直接重新输入；必需文件留空取消，Ctrl+C 可随时取消。Pt 和 Legacy 均提示需要 Digest 和 Sign，未指定文件时要求手动选择。输入预算及脚本行为沿用原配置。

手动输入另外记录等待开始、耗时、完成或取消，不记录输入内容。资源预算超时和用户取消分别记录；显式续接和静默探测失败也会说明当前状态与恢复条件。

设备完整日志逐帧落盘，等待 ACK 超时不会丢掉先前错误；明确的 Hash mismatch 状态可见，连续和带空格的 Hash 字节串均隐藏。Verify 后收到设备重新等待签名表的提示时立即报认证失败，不发送 Sign；sha256init 的已知 log-only 兼容路径明确注明未收到 ACK。

按用户确认，Legacy 在读写中出现 Hash 诊断但收到 ACK 后继续发送，是应保留的参考兼容行为。本次日志整理不改变 ACK/NAK、换表、重试和 RAW 发送顺序，不把这类诊断升级为中断条件。

## 命令列表与执行

Loader 报告非空 SupportedFunctions 时，CLI 展示已实现命令与设备报告列表的交集，并检查命令是否在报告列表内。VIP/Oplus Loader 可能在启动时只报告等待签名表，未输出命令列表；此时能力状态为“未确认”，CLI 展示自己的实现列表并允许显式执行 `partitions`、`read`、`write` 等命令，结果以设备 ACK/NAK 和实际读写为准。缺失列表不再导致“设备未声明支持 read”的本地误拦截，也不会额外发送探测包或给设备信息填充虚构能力。断开或失效会话仍须重连。

## 文件读取

```powershell
geekflash --port COM7 --loader programmer.elf --non-interactive read boot boot-backup.img
geekflash --port COM7 --loader programmer.elf --non-interactive read sector 0 0 34 gpt.bin
```

读取先写入目标同目录的唯一临时文件，协议成功且未取消后才替换目标。失败或取消会删除临时文件，保留已有输出；进程被强制终止时可能遗留 `.tmp` 文件。未执行真实硬件读写验证。

## 只读挂载与资源浏览器

连接后的交互提示符中执行 `browse <partition> [lun] [lp-slot]`，例如 `browse super`、`browse super 0 1` 或 `browse system_a`。`browse help` 显示浏览器命令的含义和示例，进入浏览器后输入 `help` 可再次查看。也可在命令行连接后直接进入：

```powershell
geekflash --port COM7 --loader programmer.elf browse super
geekflash browse-image "D:\images\super.img"
geekflash browse-image "D:\images\system.img"
geekflash browse help
```

`browse-image <raw-image> [lp-slot]` 无需设备，支持 raw LP/EROFS/Ext 镜像；不直接处理 Android Sparse 容器。设备入口使用 GPT 字节范围切片，LP 按 extent 映射读取，均不会先转储完整 Super。LP slot 默认 0，最后一个参数可选择 1 等槽位，不自动猜测活动槽。设备分区同名时需要显式 LUN，LP 外部块设备名存在歧义时拒绝挂载；本地多设备 LP 需要额外源，当前单镜像入口会明确拒绝。

进入后显示当前路径、类型、大小、编号和返回项。输入编号或目录路径进入；输入文件路径可选择输出文件。挂载层和普通目录使用同一条路径，例如 `/super/system_a/etc/settings.conf`，路径和搜索区分大小写：

```text
cd /super/system_a/etc
ls
up
cd ..
pwd
print /super/system_a/etc/settings.conf
read /super/system_a/etc/settings.conf "D:\backup\settings.conf"
find --all *.conf /super/system_a "D:\backup\system_a"
find build.prop /super
exit
```

`up` 和 `cd ..` 返回上层，能依次退出文件目录、文件系统分区、LP 容器，到虚拟 `/`，例如 `/super/system_a` 返回 `/super`。`pwd` 显示当前目录路径。`exit` 返回原 CLI 提示符；以独立 `browse`/`browse-image` 启动时退出程序。路径中的空格用引号包裹，路径分隔符使用 `/`。`ls [path] [page]` 每页 50 项，page 从 0 开始，编号为目录内序号；LP 子分区在首次进入时才打开文件系统。未知格式可按 raw 文件导出，已识别但损坏/不支持的文件系统会报错。

`print <path>` 直接在终端显示普通文件文本，默认只允许不超过 24 KiB（24576 字节，含）的文件，当前没有扩大上限参数；大文件使用 `read` 保存到电脑。支持 UTF-8，以及带 BOM 的 UTF-16/UTF-32；其他编码或二进制文件可用 read 导出。换行与制表符保留，其他控制字符显示为 `\uXXXX`，文件内容只显示在终端，不写入诊断日志。短读、无效文本或取消时不打印部分内容，读取仍使用同步文件系统与完整 Firehose RAW/ACK。`browse help` 和 `browse-image help` 均无需连接设备或打开镜像。

`read <path> <输出文件>` 导出一个普通文件或 raw 分区；父输出目录需要存在。`find <文件名通配符> [path] [输出目录]` 递归搜索 EROFS/Ext 的普通文件，支持 `*`、`?`，默认找到第一个匹配文件后停止；指定输出目录时导出该文件后停止。需要全部结果或批量导出时使用 `find --all <文件名通配符> [path] [输出目录]`。不指定输出目录时仅打印完整虚拟路径；指定时创建并保留相对搜索起点的目录结构，例如上述全量搜索输出 `D:\backup\system_a\etc\settings.conf`。搜索 LP 容器会按需访问各逻辑分区。

搜索期间按 Ctrl+C 只取消本次搜索（包含搜索导出），等当前设备读取及最终 ACK 完成后返回当前浏览器目录，可继续 `ls`、`cd`、`read` 或再次 `find`。取消不会中断 Firehose RAW 通信、关闭传输或要求重连；已完成导出保留，正在导出的文件取消时保留原目标并删除临时文件。若设备自身超时/断连，仍报告通信错误并要求重连。搜索以外的 Ctrl+C 保持原程序取消行为。

不跟随符号链接，不导出设备节点/Socket 等特殊文件；目录循环有检测，目录深度和节点数有上限。每个文件用池化缓冲流式复制并原子替换，取消/短读保留该文件原输出，成功时显示导出进度；批量导出中已成功的文件会保留，后续失败停止本次搜索。输出不能越出指定目录、通过链接目录写入、覆盖挂载源镜像，多个结果映射同一路径时停止。交互命令错误可以重试，设备会话失效则退出浏览器并要求重连。

可给 `browse-image` 的 stdin 提供命令脚本；配合 `--non-interactive` 时，命令错误立即返回失败退出码，不等待文件输出提示，脚本使用显式 `read`/`find` 输出路径。设备脚本还需提供连接资源；所有厂商证据未知时必须加 `--vendor`。用户已在真机完成 Firehose 续接、Super→Ext 浏览和 build.prop 定位；本轮首个停止与安全取消仍需真机复测，建议先搜索小目录、导出一个小文件。
