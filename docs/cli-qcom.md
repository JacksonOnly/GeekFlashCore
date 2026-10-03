# Qualcomm CLI 使用说明

CLI 可执行文件为 `geekflash`，构建目标为 .NET 10；协议库仍为 .NET 8。先运行 `geekflash --help` 查看命令，`geekflash devices` 只枚举设备。

## 连接与资源

```powershell
geekflash --port COM7 --loader programmer.elf info
geekflash --usb 05c6:9008 --loader programmer.elf --read-timeout 5000 --write-timeout 5000 info
geekflash --protocol qcom --device-wait-timeout 30000 --loader programmer.elf interactive
```

自动发现仅接受已识别的 Qualcomm EDL COM 设备；自定义 VID/PID 使用显式 `--usb`。串口与 USB 互斥，USB 同样使用传入的读写超时。

所有超时单位为正毫秒数：

| 参数 | 默认 | 用途 |
| --- | ---: | --- |
| `--device-wait-timeout` | 30000 | 等待热插拔设备的总预算 |
| `--connect-timeout` | 1500 | 初始协议探测配置；普通线路单次最多 250 ms、最多 4 次；Oplus 被动探测和启动读取使用完整预算，禁止初始化前主动 NOP；并非整个连接的总时限 |
| `--resource-timeout` | 15000 | 每次 Loader、Digest、Sign 或认证资源请求，包括交互输入 |
| `--read-timeout` / `--write-timeout` | 10000 | 单次同步 I/O；XML 完整响应共用一次读取预算 |

Configure 有有限次数回退，多 LUN 查询逐个执行，因此连接累计耗时可能大于探测超时。大镜像持续传输不设统一总时限。Ctrl+C 在数据块和响应包边界生效；已阻塞的同步 I/O 最多需等当前单次超时结束。

资源提示仅在交互终端使用。脚本请显式提供资源并传入 `--non-interactive`，缺少资源立即失败，不会等输入；stdin 重定向同样不弹出资源提示。常见退出码：成功 0、执行失败 1、参数错误 2、取消 130。

Sahara 探测成功后、Loader 上传前会输出完整身份信息；`info` 和 `probe-sahara` 同样显示厂商名称、芯片名称和完整 `PkHash`。PkHash 是 Sahara `ReadOemPkHash` 返回的原始字节（现有模型字段为 CaHash），按原始顺序显示大写 HEX，不重新计算 Hash。序列号、SBL、MSM ID、OEM ID、Model ID、AntiRollback 和 SoC HW Version 使用 `0x` 前缀的大写 HEX，64 位序列号完整保留；Firehose 信息中的序列号也使用 HEX。

名称优先从 Sahara OEM/SoC HW/MSM 标识映射，无法识别时使用已有 Loader 名称或显示“未知”，原始 ID 仍保留。共享的 OEM ID 只显示对应厂商组，例如 OPPO / OnePlus / realme；“厂商协议策略”与硬件厂商名称分别显示。通用 Sahara 诊断日志中的身份 ID 改为 HEX，Hash 仍仅记录长度。

## Oplus 模式

两种 Oplus 模式均在 Configure 和存储查询前执行：启动日志 → Digest → verify XML → Sign（零填充至 4096 字节）→ verify passed → sha256init → Configure。Digest 失败、Sign 超时或半帧均立即中止连接，不发送其他命令。

Pt 保持按分区 Digest 索引映射和权限校验。只指定 `--oplus-digest` 时，默认选择 Pt；`--oplus-sign` 可选，未指定时根据 Loader/启动日志中的 SM 芯片名匹配参考内置 Sign。匹配不到、文件无效或设备拒绝时需要交互选择二进制 Sign 文件。脚本建议显式指定 Sign，无法交互时会失败：

```powershell
geekflash --port COM7 --loader programmer.elf --oplus-digest Digest.bin --oplus-sign Sign.bin --non-interactive info
```

Legacy 使用 Rector 的包计数/NOP/Digest 线路，Digest 文件作为不透明签名数据，允许没有 Pt 分区索引。Legacy 必须提供 Sign，可用参数指定或交互选择：

```powershell
geekflash --port COM7 --loader programmer.elf --oplus-mode OplusDigestLegacy --oplus-digest Digest.bin --oplus-sign Sign.bin --non-interactive info
```

CLI 不再暴露 Legacy 调整选项，固定使用表容量 53、初始计数 0、每段 256 扇区、运行期 Digest 独立回复窗口 1000 ms、XML 字节截断上限 4096、内置兼容 NOP。

初始 Digest 被确认后计数归零，每条 XML 和每次完整输出数据传输各计 1（包括 verify、Sign、sha256init）；策略安装和存储回退复用同一计数器及已认证资源。串口/USB 分块不重复计数，Sparse 空洞不计数据包。自动换表只由 Legacy program 写入触发，读、NOP、patch 和通用 XML 不自动刷新。容量 53 时，计数 51/52/53/54 分别先发 3/2/1/0 个 NOP，再发第 55 包 Digest。独立 ACK 后确认 NOP 计为 1；没有独立 ACK 则保留队列，需 handler 日志和完整成功响应关联确认。

完整且可恢复的 Sign 验证失败最多允许一次手动替换；读取 NAK 后续提示的预算为 1000 ms。如果设备再次进入签名表接收状态，先重发原 Digest，再发 verify 和新的 Sign。替换后仍失败需要重新进入 EDL。自动芯片表来自参考项目，不能保证所有 Loader 都接受。

参考源码中的完整日志错误后继续确认、声明特殊属性、XML 截断和 Flush 顺序均有意保留；未知 NAK、半帧和 RAW 错误会让会话失效。签名失败只重发当前 XML 一次，不重放 RAW。普通 Digest、VIP、Oplus 三种线路互斥。

## 调试文件日志

默认在可执行程序所在目录的 `logs` 中生成每次运行唯一的日志文件，并在启动时打印完整路径。不需要 `--verbose`，文件也包含 Debug 级别的命令名、发送字节数、响应状态/rawmode、读取预算、耗时、阶段顺序和完整异常堆栈；`--verbose` 只控制控制台详情。

```powershell
geekflash --loader programmer.elf --oplus-mode OplusDigestLegacy --oplus-digest Digest.bin --oplus-sign Sign.bin --log-file .\logs\oplus-debug.log info
```

指定的文件以追加方式写入，每个事件立即刷新，单文件达到 16 MiB 后继续写同目录带运行标识的分段文件。日志不包含完整 Sign、Digest、认证载荷或自定义 XML，但保留已知签名失败状态和错误码。读取在线日志的工具需要允许共享写入。日志目录/文件保持 Git ignored；若路径不可写，CLI 会在连接设备之前报错。

## 文件读取

```powershell
geekflash --port COM7 --loader programmer.elf --non-interactive read boot boot-backup.img
geekflash --port COM7 --loader programmer.elf --non-interactive read sector 0 0 34 gpt.bin
```

读取先写入目标同目录的唯一临时文件，协议成功且未取消后才替换目标。失败或取消会删除临时文件，保留已有输出；进程被强制终止时可能遗留 `.tmp` 文件。未执行真实硬件读写验证。
