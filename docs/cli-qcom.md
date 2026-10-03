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
| `--connect-timeout` | 1500 | 初始协议探测配置；内部单次探测最多 250 ms，最多 4 次；并非整个连接的总时限 |
| `--resource-timeout` | 15000 | 每次 Loader、Digest 或认证资源请求，包括交互输入 |
| `--read-timeout` / `--write-timeout` | 10000 | 单次同步 I/O；XML 完整响应共用一次读取预算 |

Configure 有有限次数回退，多 LUN 查询逐个执行，因此连接累计耗时可能大于探测超时。大镜像持续传输不设统一总时限。Ctrl+C 在数据块和响应包边界生效；已阻塞的同步 I/O 最多需等当前单次超时结束。

资源提示仅在交互终端使用。脚本请显式提供资源并传入 `--non-interactive`，缺少资源立即失败，不会等输入；stdin 重定向同样不弹出资源提示。常见退出码：成功 0、执行失败 1、参数错误 2、取消 130。

## Oplus 模式

Pt 保持按分区 Digest 索引映射和权限校验。只指定 `--oplus-digest` 时，默认选择 Pt：

```powershell
geekflash --port COM7 --loader programmer.elf --oplus-digest digest.bin --non-interactive info
```

Legacy 使用 Rector 的包计数/NOP/Digest 线路，Digest 文件作为不透明签名数据，允许没有 Pt 分区索引：

```powershell
geekflash --port COM7 --loader programmer.elf --oplus-mode OplusDigestLegacy --oplus-digest signed.bin --non-interactive write boot boot.img
```

| Legacy 参数 | 默认 | 说明 |
| --- | ---: | --- |
| `--legacy-max-packets` | 53 | 表容量，最小 4；原 API `MaxCommandsBeforeDigest` 现按包解释 |
| `--legacy-initial-packets` | 0 | 安装 Legacy 策略时的设备表计数快照；宿主自行衔接此前 Configure/探测计数 |
| `--legacy-fixed-sectors` | 0 | 每个 program 的最大扇区数，0 表示不强制分段 |
| `--legacy-digest-timeout` | 1000 | Digest 独立回复窗口，半帧不能延长窗口 |
| `--legacy-xml-limit` | 4096 | 参考线路的 XML 字节截断上限 |
| `--legacy-nop` | 内置兼容 NOP | 一个安全的 `<data><nop .../></data>`；原文本按字节发送 |

安装策略后，每条 XML 和每次完整输出数据传输各计 1；串口/USB 分块不重复计数，Sparse 空洞不计数据包。自动换表只由 Legacy program 写入触发，读、NOP、patch 和通用 XML 不自动刷新。容量 53 时，计数 51/52/53/54 分别先发 3/2/1/0 个 NOP，再发第 55 包 Digest。独立 ACK 后确认 NOP 计为 1；没有独立 ACK 则保留队列，需 handler 日志和完整成功响应关联确认。

参考源码中的完整日志错误后继续确认、声明特殊属性、XML 截断和 Flush 顺序均有意保留；未知 NAK、半帧和 RAW 错误会让会话失效。签名失败只重发当前 XML 一次，不重放 RAW。普通 Digest、VIP、Oplus 三种线路互斥。

## 文件读取

```powershell
geekflash --port COM7 --loader programmer.elf --non-interactive read boot boot-backup.img
geekflash --port COM7 --loader programmer.elf --non-interactive read sector 0 0 34 gpt.bin
```

读取先写入目标同目录的唯一临时文件，协议成功且未取消后才替换目标。失败或取消会删除临时文件，保留已有输出；进程被强制终止时可能遗留 `.tmp` 文件。未执行真实硬件读写验证。
