# MTK BROM / Preloader 方法对应

日期：2026-10-05。参考：本地 mtkclient 基线 `e9fcf97` 的 `mtkclient/Library/mtk_preloader.py`；参考工作树未修改。

最初实现遗漏了 DA 连接以外的方法。根据用户反馈补齐标准方法、独立命令目录和受会话约束的公开入口，并修正 GET_HW_CODE 的字段解释。下面记录最终对应，不能将“命令有定义”当成“设备支持该命令”。

## 入口与生命周期

`MtkProtocol.Probe()` 完成握手与硬件查询；`IMtkProtocol.UseBromSession(...)` 在同一个 gate 内提供 `IMtkBromSession`。返回后、发生线上失败或执行跳转后通道失效；不允许在 DA2/storage 阶段继续发送 BROM 命令。手工跳转只表示设备接受了跳转命令，不能表示 DA2 已连接；后续需显式 Disconnect 后建立新会话。

`MtkBromCommand` 保留参考 Cmd 的全部 **57 个命令值**。本地逐值比对通过。Rsp 的四个单字节值对应 `MtkBromResponse`；NONE 为无字节响应，不能虚构成一个 0x00 ACK。Cap 对应 `MtkPreloaderCapability`，两项原始 capability word 均保留。

## 方法对应

| mtkclient 方法 | C# 对应 | 行为与差异 |
| --- | --- | --- |
| `__init__` | `MtkProtocol` 构造、`MtkWire` | 类型化 USB、选项和资源，不保留 Python GUI/全局配置 |
| `init` | `Probe`、`Connect` / `ConnectAsync` | 一次有界握手；查询顺序 FD → D8 → FE → FF → FC；watchdog/材料属于连接准备；身份读取显式执行 |
| `read_a2` | `ReadA2` | A2/address/count=1/一个 BE u16；参考实现本身只读取一个 word |
| `read`、`read16`、`read32` | `Read16`、`Read32` | BE address/count/data，初始和最终 status；按类型分入口 |
| `write`、`write16`、`write32` | `Write16`、`Write32` | BE 参数/逐值 echo；初始 status ≤3、最终 status ≤0xFF |
| `writemem` | `WriteMemory` | 内存字节按 LE word 转换、线路按 BE 发送；末 word 补零需显式同意 |
| `reset_to_brom` | `ConfigureBromReset` | 同样的 MISC_LOCK、RST_CON、download flag 次序；显式基址；只配置下次复位，不触发复位 |
| `run_ext_cmd` | `RunCacheDeinitialize` | 只提供参考默认 C8/B1，返回一个 response byte 与 u16 原始值；不提供任意子命令入口或漏洞线路 |
| `jump_bl` | `JumpBootLoader` | 两次 status；执行后 BROM 通道失效 |
| `jump_to_partition` | `JumpToPartition` | UTF-8 名称 ≤64 bytes、零填充、status；拒绝静默截断和嵌入 NUL |
| `send_partition_data` | `SendPartitionData` | 64-byte 名称、BE 长度、status、512-byte 流式块、BE checksum；参考没有最终确认，API 文档不承诺确认/持久化 |
| `setreg_disablewatchdogtimer` | `DisableWatchdog`、`MtkChipProfile` | 显式地址/值/16或32位宽；不自动套用未知芯片地址，特殊 remap/附加寄存器可通过标准读写显式执行 |
| `get_bromver` | `GetBromVersion` | FF 单字节版本；不做命令 echo 假设 |
| `get_blver` | `GetBootLoaderVersion` | FE 单字节版本；FE 响应表示 BROM |
| `get_target_config` | `GetTargetConfiguration` | D8/BE u32/status，完整 Raw 和已定义安全位；查询失败不返回“全部未启用” |
| `jump_da` | `JumpDownloadAgent` | D5/address echo/零 status；允许显式 Thumb entry bit |
| `jump_da64` | `JumpDownloadAgent64` | DE/32-bit wire address/01 echo/零 status |
| `uart1_log_enable` | `EnableUart1Log` | DB/零 status |
| `uart1_set_baud` | `SetUart1BaudRate` | DC/BE baud/零 status；拒绝零 baud |
| `send_root_cert` | `SendCertificate` | E0/长度 echo/status/64-byte 上传/显式 bulk ZLP/checksum/status |
| `send_auth` | `SendAuthentication` | E2/长度 echo/status/64-byte 上传/ZLP/checksum/status；1D0C 表示无需 auth |
| `handle_sla` | `Authenticate`、`IMtkAuthenticationProvider` | E3/challenge/LE response length/BE echo/status/response/BE final status；7017 表示已认证；只调用宿主签名器一次 |
| `get_brom_log`、`get_brom_log_new` | `GetBromLog(false/true)` | DD/DF、BE 外部长度、受限读取；DF 额外零 status；返回 owned sensitive buffer，不自动输出 |
| `get_hwcode` | `GetHardwareCode` | FD/两个 BE u16；第二个是硬件版本，**不是 status** |
| `brom_register_access` | `ReadRegisters`、`WriteRegisters` | DA/mode/address/byte length echo，两次 LE u16 零 status；有界、对齐、不提供 check_status=false |
| `get_plcap` | `GetPreloaderCapabilities` | FB/两个 BE u32 |
| `get_hw_sw_ver` | `GetHardwareSoftwareVersion` | FC/三个 BE u16/version + status |
| `get_meid`、`get_socid` | `GetMeId`、`GetSocId` | FE version gate、E1/E7 echo、BE length、data、LE status；早期 Preloader 返回 null；owned buffer 用后清零 |
| `prepare_data`、`upload_data` | `SendResource` / `UploadBytes` 内部步骤 | 不物化整份 DA；保留 odd-byte 零填充、LE u16 XOR、64-byte 包与 ZLP；严格核验 checksum |
| `send_da` | `SendDownloadAgent`、连接编排 | D7/address/补齐长度/signature length/status/上传/checksum/status；1D0D 中途 SLA 可同步或异步签名，成功后继续原上传，不重发 D7 |

## 保留的安全与架构边界

- 原参考 Cmd 中没有对应方法的 I2C、旧 SLA、PWR、ZEROIZATION、未知命令保留定义，不推测线路或自动执行。
- 原 Python 的内置 SLA key 遍历、GUI/文件配置、1000次重试、吞异常、失败默认值、忽略 checksum 和关闭 status 检查不移植。
- `calc_xflash_checksum` 的非四字节尾部按剩余 byte 数正确求和；原循环在某些短尾长度上越界，已有 1/2/3/5-byte 夹具。
- DA 与资源保持流式/池化；所有外部长度、地址加法与 count 在写入/分配前校验。读取失败清理局部敏感数据。
- `IMtkExploitStrategy` 是唯一宿主策略接口，无策略实现或默认注册。按用户最新要求，核心仅提供显式注入后的四阶段调用、作用域通道和结果处理，详见 [框架修订](2026-10-05-mtk-exploit-framework.md)。标准寄存器访问不接受畸形长度、不跳过认证/状态、不自动修改安全控制。

## 证据与风险

模拟测试覆盖非零 FD hardware version、BE word 读写、LE register status、身份 version gate/清零、capability/A2、UART、7017、odd auth/DA padding、ZLP、Thumb/64-bit jump、独立 DA 上传、1D0D 同步/异步续传、partition 尾部 checksum、MISC_LOCK 次序、16-bit watchdog、超大日志及通道失效。

这些是本地线路证据；尚无真实设备/抓包。C8/B1 原始返回语义、芯片特殊 watchdog/remap、Preloader-as-DA partition 最终完成状态、实际 ZLP/USB 控制传输与各设备命令支持需要硬件验证。
