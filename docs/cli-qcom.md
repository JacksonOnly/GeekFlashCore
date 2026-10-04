# Qualcomm CLI 使用说明

CLI 可执行文件为 `geekflash`，构建目标为 .NET 10；协议库仍为 .NET 8。先运行 `geekflash --help` 查看命令，`geekflash devices` 只枚举设备。

## 连接与资源

```powershell
geekflash --port COM7 --loader programmer.elf info
geekflash --usb 05c6:9008 --loader programmer.elf --read-timeout 5000 --write-timeout 5000 info
geekflash --protocol qcom --device-wait-timeout 30000 --loader programmer.elf interactive
```

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

## Oplus 模式

两种 Oplus 模式均在 Configure 和存储查询前执行：启动日志 → Digest → verify XML → Sign（零填充至 4096 字节）→ verify passed → sha256init → Configure。Digest 失败、Sign 超时或半帧均立即中止连接，不发送其他命令。

Legacy 初始化按用户提供的正常抓包发送 `<verify EnableVip="0"/>`，不带 `value="ping"`；Verify、sha256init 和 Configure 的声明为 `<?xml version="1.0" encoding="UTF-8" chimerais="power" ?>`，保留结尾空格。Pt 保持其原有参考线路的 `verify value="ping" EnableVip="1"` 和分区映射。

Legacy 在 Sign 已确认后发送 sha256init，最多等待 1500 ms（若 read timeout 更短则使用更短预算）。抓包中该命令只有完整 `ERROR: Failed to run the last command -1` 日志、没有 ACK，随后继续 Configure；此特例有意保留并记录 Warning。纯静默、其他错误、NAK、RAW、半帧和设备重新等表均中止。此例外不适用于 Digest、Verify、Sign 或 Pt 的 sha256init。

Legacy 自动配置从 UFS 开始，默认 Configure 依次发送 ZlpAwareHost=1、SkipWrite=0、SkipStorageInit=0、MaxPayloadSizeToTargetInBytes=1048576、MemoryName=ufs；协商上限和非默认显式配置仍有效，UFS 被完整 NAK 拒绝时仍可有界回退。普通线路/Pt 的原有默认配置保持。Digest 使用文件精确长度，不按启动提示中的 8192 强行填充；Sign 零填充到 4096 字节。

Pt 保持按分区 Digest 索引映射和权限校验。只指定 `--oplus-digest` 时，默认选择 Pt；`--oplus-sign` 可选，未指定时根据 Loader/启动日志中的 SM 芯片名匹配参考内置 Sign。匹配不到、文件无效或设备拒绝时需要交互选择二进制 Sign 文件。脚本建议显式指定 Sign，无法交互时会失败：

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
geekflash --port COM77 --oplus-mode OplusDigestLegacy --oplus-digest "D:\刷机\方案\奇美拉\480\Digest.bin" --oplus-resume
```

Sign 可通过提示手动输入，也可加 `--oplus-sign FILE`。续接跳过 Sahara 和启动日志，不重新上传 Loader，首包是 Digest，之后 Verify → Sign → sha256init → Configure。Pt 同样支持；缺少芯片信息时要求手动 Sign。Core 对应 `OplusDigestConfiguration.ResumeAwaitingDigest`，每个协议实例仅允许一次，认证/RAW 失败后再次 `connect` 不会盲目续接。

显式续接的首个 Digest 若被原 XML 会话完整 NAK 拒绝、报告 data Hash mismatch，且随后完整 XML 明确表示设备已转入签名表接收，新版会重新打开同一 Digest 并发送一次；这对应 `bug.txt` 中第一次拒绝、第二次相同命令成功的情况。只有不超过 8192 字节的完整单包初始 Digest 可以使用此恢复；旧的启动提示不算新的接收状态证据。第二次仍失败、无接收状态提示、RAW、半帧、静默或取消均停止，不发送 Verify/Sign，不发送额外 NOP/reset。

RAW 中断或未知状态仍应重新进入 EDL，再使用普通带 Loader 的连接命令。普通 Oplus 静默探测只输出状态说明；上述恢复仅用于显式续接的首次初始化，不用于已认证会话的重配置或一般读写。

### Legacy 包计数

初始 Digest 被确认后计数归零，每条 XML 和每次完整输出数据传输各计 1（包括 verify、Sign、sha256init）；策略安装和存储回退复用同一计数器及已认证资源。串口/USB 分块不重复计数，Sparse 空洞与设备输入 RAW 不计输出数据包。Legacy 完成初始化后，读取/GPT、program、NOP、patch、storageinfo 和通用 XML 共用表边界检查，避免只读操作耗尽签名表。容量 53 时，计数 51/52/53/54 分别先发 3/2/1/0 个 NOP，再发第 55 包 Digest。独立 ACK 后确认 NOP 计为 1；没有独立 ACK 则保留队列，需 handler 日志和完整成功响应关联确认。Rector 公开 read 默认未开启 Digest；此处按用户实机 53 包耗尽证据将其底层算法应用到本项目已认证 Legacy 会话。

完整且可恢复的 Sign 验证失败最多允许一次手动替换；读取 NAK 后续提示的预算为 1000 ms。如果设备再次进入签名表接收状态，先重发原 Digest，再发 verify 和新的 Sign。替换后仍失败需要重新进入 EDL。自动芯片表来自参考项目，不能保证所有 Loader 都接受。

参考源码中的完整日志错误后继续确认、声明特殊属性、XML 截断和 Flush 顺序均有意保留；未知 NAK、半帧和 RAW 错误会让会话失效。签名失败只重发当前 XML 一次，不重放 RAW。普通 Digest、VIP、Oplus 三种线路互斥。

## 调试文件日志

默认在可执行程序所在目录的 `logs` 中生成每次运行唯一的日志文件，并在启动时打印完整路径。不需要 `--verbose`，文件也包含 Debug 级别的命令名、发送字节数、响应状态/rawmode、读取预算、耗时、阶段顺序和完整异常堆栈；`--verbose` 只控制控制台详情。

```powershell
geekflash --loader programmer.elf --oplus-mode OplusDigestLegacy --oplus-digest Digest.bin --oplus-sign Sign.bin --log-file .\logs\oplus-debug.log info
```

指定的文件以追加方式写入，每个事件立即刷新，单文件达到 16 MiB 后继续写同目录带运行标识的分段文件。日志不包含完整 Sign、Digest、认证载荷或自定义 XML，但保留已知签名失败状态和错误码。读取在线日志的工具需要允许共享写入。日志目录/文件保持 Git ignored；若路径不可写，CLI 会在连接设备之前报错。

手动输入另外记录等待开始、耗时、完成或取消，不记录输入内容。资源预算超时和用户取消分别记录；显式续接和静默探测失败也会说明当前状态与恢复条件。

设备完整日志逐帧落盘，等待 ACK 超时不会丢掉先前错误；明确的 Hash mismatch 状态可见，连续和带空格的 Hash 字节串均隐藏。Verify 后收到设备重新等待签名表的提示时立即报认证失败，不发送 Sign；sha256init 的已知 log-only 兼容路径明确注明未收到 ACK。

## 命令列表与执行

Loader 报告非空 SupportedFunctions 时，CLI 展示已实现命令与设备报告列表的交集，并检查命令是否在报告列表内。VIP/Oplus Loader 可能在启动时只报告等待签名表，未输出命令列表；此时能力状态为“未确认”，CLI 展示自己的实现列表并允许显式执行 `partitions`、`read`、`write` 等命令，结果以设备 ACK/NAK 和实际读写为准。缺失列表不再导致“设备未声明支持 read”的本地误拦截，也不会额外发送探测包或给设备信息填充虚构能力。断开或失效会话仍须重连。

## 文件读取

```powershell
geekflash --port COM7 --loader programmer.elf --non-interactive read boot boot-backup.img
geekflash --port COM7 --loader programmer.elf --non-interactive read sector 0 0 34 gpt.bin
```

读取先写入目标同目录的唯一临时文件，协议成功且未取消后才替换目标。失败或取消会删除临时文件，保留已有输出；进程被强制终止时可能遗留 `.tmp` 文件。未执行真实硬件读写验证。
