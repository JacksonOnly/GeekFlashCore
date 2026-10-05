# GeekFlashCore MTK 协议设计

日期：2026-10-05

任务：MTK-00～MTK-13

状态：2026-10-05 MTK-01～12 已实施；用户后续授权 MTK-13 阶段接口框架，仍禁止漏洞实现。

## 2026-10-05 实施范围修订（优先于原草案）

- 用户指定在 worktree `8d7a/GeekFlashCore` 实施，最初要求“利用漏洞的部分只预留一个接口”；后续明确要求参考 mtkclient 与 penumbra 设置接口并在合适阶段调用，只搭框架，不进行漏洞利用。
- MTK-08 最初只预留接口；MTK-13 修订为 `IMtkExploitStrategy`、阶段元数据、作用域 context 和明确结果，由宿主显式注入后在连接阶段调用。无策略实现、默认注册、漏洞算法、DA 安全补丁生成、攻击载荷或架构扫描。最新契约以 [框架修订](2026-10-05-mtk-exploit-framework.md) 为准。
- 下文原草案中的漏洞族、patch 与内置攻击 payload 实施要求被本修订替代；不添加 exploit CLI 选项。
- MTK-09 保留已经合法加载且具有对应 ABI 的 DA 扩展通信、ACK/context、内存与 crypto；不通过补丁使标准 DA 获得扩展。宿主提供扩展上下文，能力以真实 ACK 与 context 状态为依据。
- RPMB、seccfg 仍属范围；缺少扩展、密钥或硬件算法时明确拒绝操作，不采用 dummy signature 或绕过线路。
- 所有非漏洞任务继续按原顺序实施，模拟证据与硬件风险分别记录。
- BROM 必须保留 mtkclient 的完整命令目录和标准方法；对应、来源差异与门禁见 `2026-10-05-mtk-brom-method-mapping.md`。GET_HW_CODE 的第二个 halfword 是 hardware version，不能当作 status。

## 1. 目标、范围与兼容性

在 GeekFlashCore 中建设可独立复用的 MediaTek 刷机协议，供 CLI、桌面工具与服务宿主使用。MTK 的生产传输统一使用现有 LibUsb 后端，协议收发使用同步 `ITransport`、`Span<T>` 与有界池化缓冲。异步仅用于宿主编排、资源请求、设备等待及取消边界。

用户于 2026-10-05 明确要求结合以下三个参考项目，而非直接移植某一个项目；当前授权范围为标准协议、RPMB、seccfg 及宿主阶段接口框架，禁止漏洞实现：

| 来源 | 本次读取的本地 HEAD | 主要用途 |
| --- | --- | --- |
| `D:\Code\CSharp\GeekFlashTool\GeekFlashTool.MtkClient` | GeekFlashTool `67ced05` | 原 C# 应用的芯片配置、厂商兼容、认证、Legacy/XFlash/XML、漏洞与 seccfg 行为 |
| `D:\Code\Rust\penumbra` | `ce13391` | USB 连接、XFlash/XML 分层、Kamakiri2、Carbonara、HeapBait、扩展上下文与 RPMB |
| `D:\Code\Rust\mtk-payloads` | `e34d980` | 上述 Rust 项目的设备端载荷源码、架构布局、命令编号及上下文协议 |
| `D:\Code\Python\mtkclient` | `e9fcf97` | BROM/Preloader、Legacy、DA 表格式、EMI、芯片配置与安全算法的交叉核对 |

这些版本用于定位已读取的参考源码，不构成真实硬件验证。外部状态检查发现：GeekFlashTool 的 MtkClient csproj 有本地修改；Python 的 daconfig、Legacy/XFlash extension、hwcrypto_sej、brom_config 及另外的 ebr/mtk_main 有本地修改。daconfig 的当前差异仅位于 `__main__` 演示入口；其余相关差异在对应实施任务前单独核对。Rust core 和 mtk-payloads 的本次 status 检查没有受跟踪改动。参考源码以已读取工作树为准，移植时同时记录 HEAD 和具体文件内容，绝不覆盖参考项目的本地改动。

交付范围：

1. BROM / Preloader USB 识别、握手、芯片信息、受控内存访问与 watchdog 配置。
2. DA 容器解析、按设备身份选择条目、DA1/DA2 加载、EMI 提取与初始化。
3. Legacy、XFlash v5、XML v6 的独立同步会话与流式读、写、擦除、重启。
4. eMMC user/boot/GP 与 UFS LU 的类型化几何模型；GPT、Raw、Android Sparse 与通用块设备接入。
5. BROM SLA / DAA / cert、DA SLA 的类型化宿主资源和认证流程。
6. 单一宿主策略接口与阶段/context/result 框架，无策略实现；对已合法加载扩展的 ACK/context、内存、寄存器与 SEJ 操作。
7. RPMB 上下文初始化、读写与认证；seccfg v3/v4 的解析、算法匹配及锁定/解锁。
8. CLI 的 MTK 注册、纯 USB 自动发现、文件资源 Provider、能力与风险状态展示。

兼容性要求：现有 Qualcomm 线路、默认选项、公共接口与串口选择行为不改变；通用 USB 能力只以新增接口/重载扩展。MTK 不引入 COM 或 SerialPort 回退，不改变 `ProtocolType.Mtk` 的已有枚举值。

非目标：迁移参考项目 GUI、云端账户/服务、加密资源包、私钥、厂商商业 API DLL；不把 Android META、ADB 或 Fastboot 实现混入 MTK DA 协议；不承诺所有 MTK 芯片都能绕过认证或解锁；不以空实现、始终返回成功或无测试的策略登记宣告完成。

## 2. 当前项目与需要调整的边界

- `IProtocol` 已有 `ProtocolType.Mtk`，提供连接、读写、擦除、分区和重启异步宿主入口。
- 通用 `IDataSource` 提供长度和可重新打开的流；`ReadDestination` 单独定义输出流所有权。
- `IBlockDeviceProvider` 可供 GPT、LP、文件系统与 CLI 浏览器复用；已有块设备 Stream 适配设施应直接复用。
- `GeekFlashCore.Transport.LibUsb` 已实现同步 bulk 收发与 `IControlTransferTransport`，并在所认领接口中选择 bulk IN/OUT。
- 当前 LibUsb 工厂只提供 VID/PID/可选 class GUID，缺少明确设备身份、重枚举匹配和 MTK CDC 控制接口描述；不能用同 VID/PID 的任意设备替代原设备。
- 当前 CLI `TransportResolver` 的自动枚举、匹配和热插拔路径均要求 COM 名并创建 SerialPort。新增 MTK 必须使注册项决定传输选择，不能在通用循环散布 `if (Mtk)`。
- 当前 CLI 未指定协议且显式提供传输时，依赖只有一个注册项的默认行为。新增注册后仍保留原 `--port COMx` 的 Qualcomm 默认；`--usb VID:PID` 优先按注册标识推断，未知 USB 身份要求 `--protocol`。
- 本工作树初始 `HEAD=173d1bb`，处于 detached HEAD，启动时工作区干净。当前工作树不存在 `.tests`；测试是 ignored 的本地工程，不从历史测试数量推断本工作树已通过测试。

## 3. 参考事实、差异与保留顺序

### 3.1 BROM / Preloader

保留基本握手 `A0 → 5F, 0A → F5, 50 → AF, 05 → FA`。参考 Rust 的 LibUsb 读取最多 5 字节后检查末字节，C# 的串口版本读取单字节；实现需有首包缓冲和有界前缀识别，覆盖 Preloader 多余首字节、分片与已握手状态。单个 `A0` echo 只能作为候选证据，必须后续只读身份命令确认；不能直接判定完整握手成功。

`GET_HW_SW_VER (FC)`：已读取的 C# 和 Python 都解析大端 4 个 u16；Rust `Connection::get_hw_sw_ver` 却将前三个字段按小端读取。初始实现以 C#/Python 一致的线路定义为基础，模拟非对称字节值验证；保留硬件证据未确认风险，禁止因为值异常就盲目切换字节序。

`GET_HW_CODE (FD)`、`GET_TARGET_CONFIG (D8)`、`SEND_DA (D7)`、`JUMP_DA (D5)` 分别建模，不将零值当“未知但继续成功”。安全位 SBC/SLA/DAA 保留原始位和已观测状态，认证状态另行记录。

DA1 上传顺序：`D7 echo → address/length/signature length echo → initial status → bounded data chunks → XOR checksum → final status → D5/address echo → status → C0`。校验和必须计算并比对；参考 Rust 只读取 checksum 的行为不继承。watchdog 写入只在已识别芯片配置和访问能力明确时执行。

2026-10-05 BROM 初始化修订：Connect/ConnectAsync 在 FD 后使用已知普通 WDT（或显式 profile），再查询 D8/FE/FF/FC，随后请求资源和认证；CLI 的独立 Probe 同样启用该初始化，库独立 Probe 默认只查询。未知芯片及未支持特殊线路不写寄存器；一次会话不重复准备。芯片名称、DA alias、FD 初始版本与 WDT 结果单独保留；DA parser 与核心校验使用同一 alias，subcode/最低版本仍严格校验。详见 [BROM 标准初始化](2026-10-05-mtk-brom-initialization.md)。

### 3.2 DA 容器、选择与 EMI

- 新格式 DA header 为 `0x6C`，count 位于 `0x68`，条目通常为 `0xDC`；旧格式条目为 `0xD8`。
- Python 与 C# 的旧格式会省略新格式的 `sw_version/reserved1`，因此旧格式 region count/table 的偏移必须独立解析，不能复用 Rust 的固定 `0x12/0x14` 假设。
- 新格式容器与 `MTK_DA_v6` 标记用于初步分类；`AND_SECRO_v` 和目标芯片/Preloader 能力用于辨认兼容 Legacy 条目。分类证据存入模型，禁止“解析出字段即判定该设备支持”。
- 选择同时校验 hw_code、已知 dacode 别名、hw_sub_code、hw_version、sw_version 与 entry region index。未知 dacode 不套用相近商业芯片；多个候选不能凭文件名或列表第一项随意选择。
- DA 描述仅保存 `IDataSource` 与 region 范围，读取有界元数据；不物化整个容器或全部芯片的 DA。
- EMI 提取区分完整 Preloader、BLOADER_INFO 与 MTK_BIN，校验 MMM 长度、签名、尾部 DRAM 区、可选 `0x800` 尾块与版本文本。输入损坏与“该 DA 不需要 EMI”不能混为一类。

### 3.3 三种 DA 方言

| 方言 | 必须独立保留的行为 |
| --- | --- |
| Legacy | DA1 启动后的 NAND/eMMC ID、ACK 和 stage2 config；按 stage/address/length 上传 DA2；存储信息、分区切换、逐块 checksum/ACK；查询 USB 速度；首版保持当前速度，不主动切换或重置设备 |
| XFlash v5 | little-endian 12 字节帧、FLOW/MESSAGE 区分；sync signal、environment、hardware init、EMI、DA2 boot；DEVCTRL 的 command/parameter/data/status 顺序；read ACK 和 write 的 zero-status/checksum/data/final-status |
| XML v6 | 同类 binary framing 内承载 XML/ACK；CMD:START、OK、命令应答、中间文件/进度交互、CMD:END 与最终 ACK；runtime/host/hardware/security 初始化；DA2 和已加载扩展的 ACK/context 分别确认 |

FLOW/MESSAGE 包的长度、类型和总数量有界。MESSAGE 不作为状态包，不打印敏感原文。XML 启动或结束确认超时不是成功；参考 Rust `check_lifetime` 吞超时的行为不继承。

XFlash chunk checksum 是字节加和后取低 16 位；写入一个 chunk 后和整个操作末尾的 status 分开检查。参考 Rust 允许 Stream 短读后自动补零的通用循环不继承：必须填满所需 chunk，提前 EOF 失败；仅对明确的镜像尾部扇区 padding 允许补零。

XML device file-system 请求仅在当前命令预先声明的虚拟资源中回答 exists/size 等问题，绝不将设备提供的路径映射为宿主文件路径。

### 3.4 扩展与安全能力

漏洞族和安全补丁不属于当前实施范围。`IMtkExploitStrategy` 仅由宿主显式注入；核心按 descriptor 筛选 BeforeDa1、Da1Ready、Da2Ready、Da2Authenticated 四阶段并提供串行化、有界、过期后不可用的 USB/BROM/DA context。没有策略实现、默认注册、exploit CLI、攻击二进制、反汇编器或自动绕过流程。完成结果不解除认证；终止或未知结果使会话失效。返回 DA 资源经重新验证，已执行阶段不可替换，详见框架修订。

扩展模块通信限定于宿主已经合法加载、具有 penumbra/mtk-payloads 对应 ABI 的 DA。必须在当前代数内完成 ACK → CTX → operation；CTX 的 hardware/DA2 范围与实际加载元数据匹配。访问寄存器/内存需显式允许窗口，SEJ 与派生密钥需明确硬件基址。任何线上失败使会话失效，不继续试其他扩展。

RPMB INIT 使用 Authenticate 语义，不提供烧录 key。没有认证材料时停止；不生成 dummy SLA 签名。seccfg 修改通过独立计划、备份、预写比对、最小对齐写与回读进行。

## 4. 模块与公共契约

```text
CLI / Desktop / Service
  ├─ DA / EMI / AUTH / challenge response Provider
  └─ MtkProtocol : IMtkProtocol + IBlockDeviceProvider
       ├─ serial session gate / generation / resource resolver
       ├─ LibUsb connection / identity / bounded reenumeration
       ├─ BromSession / PreloaderSession
       ├─ LegacyDaSession / XFlashDaSession / XmlDaSession
       ├─ storage / GPT / raw / sparse streaming
       └─ optional Mtk.Extensions services
            ├─ existing extension ACK / validated context
            ├─ bounded memory / registers / SEJ
            └─ memory / crypto / RPMB / seccfg
```

### 4.1 新项目

- `GeekFlashCore.Protocol.Mtk.Abstractions`：net8.0，稳定 public API、不可变配置/模型、资源请求与领域异常；只引用本仓库通用抽象，不暴露 LibUsbDotNet、反汇编器或其他第三方类型。
- `GeekFlashCore.Protocol.Mtk`：net8.0，公共门面、BROM/Preloader/三种 DA、LibUsb 组合入口、DA/EMI 解析、存储与块设备。标准使用不要求启用漏洞组件。
- `GeekFlashCore.Protocol.Mtk.Extensions`：net8.0，已加载 DA 扩展通信、RPMB/seccfg/SEJ。通过 Abstractions 中的同步 scoped channel 访问门面；核心不能反向依赖扩展项目。CLI 可组合注册这些能力。

MessagePipe 不是首轮必需依赖；所有 Provider 均可由任意宿主直接实现。将来增加集成包不能污染协议核心。

### 4.2 类型化 API

以下名称表示本方案契约方向，实现前逐项写契约测试并检查现有项目命名：

- `IMtkProtocol : IProtocol`：`TargetInfo`、`SessionState`、`Capabilities`、`GetStorageInfo`、同步 `Probe/Connect/Read/Write/Erase` 与异步资源编排入口；同步连接使用已解析的显式资源，绝不阻塞等待异步 Provider。
- `IMtkBromSessionAccess` / `IMtkBromSession`：Probe 后、DA 执行前的同步 scoped 标准 BROM 方法；`MtkBromCommand` 为参考 57 个命令值的完整目录。
- `MtkTargetInfo`：真实 hw_code/subcode/version、BROM/Preloader 版本、连接阶段、安全配置快照及证据；MEID/SOCID 默认不写日志。
- `MtkDaKind`：`Legacy/XFlash/Xml`；`UsbTransportIdentity` 与 `MtkBootStage` 分别表示 USB 身份与实际运行阶段。
- `MtkDaImage` / `MtkDaEntry` / `MtkDaRegion`：可重开数据源、文件窗口、目标地址、签名长度、版本与匹配证据；不以数组表示全部 DA 文件。
- `MtkStorageKind`、`MtkStorageRegion`、`MtkStorageInfo`、`MtkFlashRange`：区分存储类型与 wire partition ID；eMMC user wire ID 8 不误映射成 UFS LU8。保留实际逻辑块大小。
- `MtkCapabilities`：`Supported/Unsupported/Unknown/RequiresExtension`，来自 profile、DA 静态证据、运行时命令及扩展 ACK 的组合；功能缺失不会显示为操作成功。
- `MtkProtocolException`：阶段、命令、数值 status 与 `RequiresReconnect`；`MtkResourceException` 与 `MtkCapabilityException` 分离。用户文本在 MTK 项目中英文 resx 配对。
- `IMtkDaProvider`、`IMtkEmiProvider`、`IMtkAuthenticationProvider`：强类型 request/response + `ValueTask` + `CancellationToken`。
- `IMtkExploitStrategy`：宿主显式注入的阶段接口，无策略实现；`MtkExploitContext` 提供回调期 USB/BROM/DA 通道；`IMtkSessionAccess` / `IMtkDaChannel` 提供同步、受限生命周期的扩展访问。上下文仅在当前 gate/代数/线程内有效，验证阶段与访问范围；不将原始 Transport 暴露给异步资源 Provider。
- `IMtkRpmbService`：读取、写入、认证/上下文状态与独立 region/256 字节数据块计数；不作为普通 writable block device 暴露。
- `IMtkSecurityConfigurationService`：读取/校验、生成变更计划、应用 lock/unlock 和回读验证；不得在构造或 Connect 中自动改 seccfg。

契约初稿不新增对现有 `IProtocol` 的破坏性成员。MTK 专用扩展通过独立能力接口获取。

## 5. 会话、取消、重枚举与资源所有权

### 5.1 状态机

```text
Disconnected → Opening → Handshaking → Probed
 → [Authenticating]
 → UploadingDa1 → Da1Ready → [InitializingEmi]
 → UploadingDa2 → Da2Ready
 → [Authenticating / Extending] → StorageReady
任意已改变设备状态的阶段失败 → Faulted → Disconnect → fresh Connect
```

所有同步、异步、块设备与扩展入口共用同一个串行化 gate。内部同步步骤不重新获取 gate，进度或 Provider 回调不得重入线路操作。gate 之外只允许快照查询。

预取消和发包前参数/资源验证失败不破坏一个既有 Ready 会话。第一次线上命令之后的传输失败、NAK、短读、stream EOF、资源失效或取消若无法证明已完成协议边界，就 Faulted，关闭底层句柄并增加代数。禁止通过单纯清理主机字段宣告设备恢复。

阻塞的同步 USB 读写不能承诺即时取消；读写超时必须有限。每个块、命令和重枚举等待边界检查 token，同时限制整阶段 wall-clock deadline、握手次数、MESSAGE 次数、认证次数与进度事件数量。相同 status 不能无限重试。

### 5.2 LibUsb

新增通用、无第三方类型的 `IUsbTransport` 或等价稳定能力接口，表达 bulk/control 访问、USB identity 和选定接口。现有 `ITransport` 与 `LibUsbTransportFactory.Create` 签名保留；新增 options/typed factory 返回 USB 能力。生产 MTK 工厂只组合 LibUsb；模拟 transport 实现同一抽象。

USB identity 至少表达 VID/PID、可用的 serial/device path、bus/address/port path 与 configuration/interface。若仅 VID/PID 不能唯一匹配设备，显式选择或报歧义，不能选择任意一个。Windows path 与 LibUsb backend 可用身份之间的映射需通过本地 API 验证，不用 WMI 字符串猜测。

bulk IN/OUT 必须来自同 configuration/interface/alt-setting，CDC control interface 分开表示；`0E8D:6000`/LG profile 的 interface 2 提示是兼容候选而非硬编码全设备。CDC SET_LINE_CODING/SET_CONTROL_LINE_STATE 按 profile 发起，失败只对已知容许差异回退，不能吞全部异常。

首版不发送 DA 提速/重置命令。LibUsb 提供独立的有界重枚举等待工厂，按原 serial 或 bus/port path 与允许 PID 匹配；宿主关闭旧连接后使用新 transport。自动续接 DA 的协议目前不登记为支持。普通超时不自动 crash、reset USB 或跑 exploit。旧块设备和扩展上下文一律失效。

### 5.3 资源

Provider 对象及调用方 `IDataSource` 由调用方持有；Core 释放自己通过 `OpenStream` 打开的流。显式调用方 Stream 用独立 `leaveOpen/ownsStream` 语义；读目标在返回前不被协议释放。

DA/EMI 在各个首次不可逆线上阶段前获取并验证，保留可重开和窗口长度约束。只依赖 challenge 的签名在握手中途异步请求。连接/资源预算有限；超时后观察迟到 Task，并释放响应中已转移所有权的资源。资源 CTS 保留到迟到任务结束，不能提前 dispose 后遗漏 cleanup。

签名、key、challenge、seccfg digest、派生 key 用专门敏感资源所有者，使用后清零；池化内存归还前清零。响应异常、取消和迟到结果覆盖同一释放规则。

## 6. 存储、镜像、RPMB 与解锁

### 6.1 普通存储

连接只在 DA2、必要认证和存储几何初始化全部成功后进入 `StorageReady`。所有区域缓存不可变快照；显式 refresh 更新代数或清晰校验旧视图。`PhysicalPartitionNumber` 在通用接口层表示已登记的区域标识，由 MTK region 模型转换 wire ID；文档明确 eMMC 与 UFS 编号规则。

名称目标从缓存 GPT/区域中解析，重名且未指定区域时拒绝。sector/offset/range、uint64 → int64、乘法、末扇区补齐均 checked；范围验证在第一个存储命令前完成。读输出总量必须与目标相符，不能信任 DA 声明任意长度。

Raw 通过 `IDataSource` 的 window 流发送；Sparse 使用现有 `SparseDocument` 与区域流，保留 RAW/FILL/DONT_CARE 的语义，DONT_CARE 不默认擦除或补零整个分区。元数据无效在写入前拒绝；无按 expanded image size 分配。

块设备读支持任意字节范围的有界对齐窗口，写要求逻辑块对齐；`Flush` 的保证仅为协议应答完成，不能虚构介质持久化。每个打开视图使用 generation 检查，Disconnect/Fault/Reconnect 后旧对象报错。

eMMC/UFS 是完整首要矩阵。Legacy NAND/NOR/SDMMC 的配置、坏块/BMT/OOB 语义不得冒充普通 eMMC；有完整可交叉验证线路的 profile 单独实现，其余以明确能力状态拒绝，不通过“默认 user”写入。

### 6.2 RPMB

RPMB 必须完成 extension ACK 与 DA context，再按 storage/region 执行 INIT/READ/WRITE。`mtk-payloads` 的 `RPMB INIT` 当前用于核验已存在的 key 与写计数器，不是烧录新 key；公共 API 使用 `Authenticate` 语义，不将其命名为 `ProgramKey`。

区分 512 字节 RPMB wire frame 与 256 字节数据块；输入地址、块数、容量、region、key 长度、响应数和 status 在传输前后验证。遵守对应 backend 的 chunk 上限，Rust XFlash 扩展的 32 KiB 写上限单独建模，不能应用普通 flash packet multiplier。

协议 timeout 或未知写结果后不自动重试 RPMB 写，防止 counter 及状态不一致。若载荷仅返回简化数据/status，没有提供 nonce/MAC/counter 到宿主，应在结果能力中明确设备端验证范围，不宣称宿主验证完整 wire frame。暂不提供一次性 RPMB key programming；未来需要独立 API 与真实协议证据。

### 6.3 seccfg / crypto

v3/v4 分开解析，校验 magic/version/declared size、头部/条目与算法要求。SW/SEJ HW/HWv3/HWv4 仅在 profile/扩展具有能力时按固定候选顺序验证原 digest；不能遇到不匹配就用零值生成新数据。

读取限定元数据窗口并保存原始修改窗口的快照，生成 lock/unlock 计划，保留所有未修改字段/尾部；应用前在同一 gate/代数下确认快照未变 → 最小对齐写入 → 回读和 hash/状态验证。原始备份由调用方接收；Core 不隐式落盘敏感数据或整分区 dump。

取消、写超时或回读失败：结果清楚记录“可能已写入，需重连检查”，不得声称锁状态未变。lock/unlock 仅修改该设备格式支持的 seccfg，不推断 Android bootloader UI、AVB 或数据分区的最终状态。

首版提供扩展 ABI 的 SEJ 与 RPMB key derivation，SEJ/TZCC/SSR 基址由宿主显式提供；不声明独立 GCPU/DXCC/TZCC 主机驱动支持。硬件循环由兼容扩展 backend 执行，宿主线路有有限预算。key derivation、内存 dump 与寄存器操作为类型化显式入口，禁止 Connect 自动导出敏感材料。

## 7. 性能、安全、日志与载荷来源

- 帧头和小参数使用 stackalloc/BinaryPrimitives；数据与受限 XML 缓冲使用 ArrayPool；不在逐包线路创建 Task 或 ToArray。
- 帧大小由 host cap、device packet length、profile 和安全硬上限取最小值；无效设备值拒绝，不直接乘 BufferLevel。推荐起始 bulk buffer 64 KiB，最大 flow/data buffer 1 MiB，DA 单 region 的静态分析预算另设上限；这些是待夹具/吞吐验证的初始配置，不能视作硬件事实。
- Loader Legacy marker 检索只扫描所选 DA2，最多 16 MiB，按窗口读取；没有漏洞 patch/架构静态分析。大文件、Sparse 与 RPMB dump 稳态内存只随窗口/元数据增长。
- XML 使用 XmlReader/XmlWriter，禁用 DTD/外部实体，限制字符、深度、元素/属性，命令与参数允许列表；不以字符串 contains 判断结果，不提供无校验 custom XML 旁路。
- Information 只记录阶段、策略名、存储和结果摘要；Warning 用于已证明可恢复回退；Error 由拥有会话上下文的层记录最终失败；帧头与进度仅 Debug/Verbose。
- 原始设备文本限长且默认不输出；禁止记录 auth、challenge、完整 MEID/SOCID/hash、RPMB key、认证响应及完整安全 XML。新增异常/日志/进度文本使用配对中英文 resx。
- GeekFlashCore 的 LICENSE 为 AGPL v3；参考 Rust/payload 的多个文件标注 AGPL/GPL/MIT 等来源。移植时逐文件保留版权与 SPDX，记录改动和来源，生成必要 NOTICE；不抹掉原作者信息。
- 不嵌入或构建任何漏洞/补丁/扩展二进制；标准 DA、EMI、auth/cert 由宿主提供。扩展通信依赖已合法加载且 ABI 兼容的 DA。
- 运行时不扫描参考仓库，不要求 Python/Rust、ARM GCC 或外部刷机程序。来源、许可与参考 revision 见 `NOTICE-MTK.md`。

## 8. 实施顺序、文件范围与提交拆分

| 任务 | 内容与主要文件范围 | 独立门禁 / 建议英文提交 |
| --- | --- | --- |
| MTK-00 | 本设计、实施进度与参考差异矩阵 | 用户确认范围、架构和次序；`docs(mtk): define protocol implementation plan` |
| MTK-01 | `Mtk.Abstractions`、options/models/providers/errors、solution 登记 | 契约/参数/枚举/所有权测试；`feat(mtk): add stable protocol contracts` |
| MTK-02 | `Transport.Abstractions` USB 能力、`Transport.LibUsb` identity/options/CDC 与工厂 | mock USB 配置/interface/endpoint/歧义/释放；`feat(usb): support identified mtk connections` |
| MTK-03 | Mtk `Brom/Preloader/Configuration`、chip profiles、门面探测/认证/失效 | handshake/echo/endian/checksum/watchdog/auth/resources；`feat(mtk): implement brom and preloader sessions` |
| MTK-04 | `Loaders` DA 容器/版本选择/EMI/资源窗口 | 旧新/v6/无效/歧义/大容器窗口夹具；`feat(mtk): parse download agents and emi` |
| MTK-05 | `Da/XFlash`、`Da/Xml`、shared framed wire；DA1/DA2 初始化 | 按方言完整连接/认证/错误/重枚举 transcript；分两提交 |
| MTK-06 | `Da/Legacy` stage config/DA2/存储与 USB speed 查询 | Legacy eMMC 核心 transcript 与明确其他介质能力；`feat(mtk): support legacy download agents` |
| MTK-07 | `Storage`、GPT、Raw/Sparse、block devices/leases、sync/async facade | 边界/最终 ACK/资源/旧代数/流式大镜像；`feat(mtk): add streaming flash storage` |
| MTK-08 → MTK-13 | 宿主策略契约和四阶段编排 | 无策略实现、默认注册、payload 或 exploit CLI；显式注入才调用；独立框架修订与验证 |
| MTK-09 | `Extensions/Da/Memory/Crypto`、已加载扩展 ABI、上下文与 capability | ACK → CTX → operation、ABI、未知地址/禁重入；`feat(mtk): add validated da extensions` |
| MTK-10 | `Extensions/Rpmb` | 多 region/256vs512/chunk/status/认证/未知写结果；`feat(mtk): support authenticated rpmb operations` |
| MTK-11 | `Extensions/Security` seccfg v3/v4、crypto algorithms、变更计划和回读 | 已知加密向量/原文校验/保留尾部/最小写/失败恢复；`feat(mtk): support verified seccfg lock changes` |
| MTK-12 | CLI adapter/Provider/options/help/USB resolver 注册、README、AGENTS 进度来源 | qcom 默认兼容/纯 USB/命令能力/全部集成回归；`feat(cli): expose mtk usb and security commands` |

每一步先写 `.tests` 中能定义行为或复现差异的测试，再写最小实现；每次完成写回实施进度后进入下一项。新增公共 API 带英文 XML 文档。实现时必要的已发现缺陷修复单独提交，不借机重构无关 Qualcomm 代码。

CLI 保留 `--loader` 为选定协议 Loader；新增 `--mtk-preloader`、`--mtk-da-mode`、`--mtk-auth-file`、`--mtk-cert`、USB identity/interface 选择、`--mtk-ufs-rpmb-blocks` 与显式 SEJ/TZCC/SSR 基址。MTK 专用命令提供 probe、capabilities、memory、RPMB、seccfg；寄存器/BROM/派生 key 使用类型化 API；通用 read/write/erase/partitions/browser 复用 IProtocol/块设备。参数在 MTK adapter 中验证，不经过 Qcom vendor/auth 验证器。

## 9. 测试矩阵与验证命令

新建本地 ignored `.tests/GeekFlashCore.Protocol.Mtk.Tests`、LibUsb 与 CLI 回归测试（按已有约定命名）；不加入发布 solution、NuGet 或 Git。使用可分片、限次、记录完整写入顺序/控制传输/超时/关闭的模拟 USB transport。

| 矩阵 | 必须覆盖 |
| --- | --- |
| USB / 生命周期 | 已打开/未打开、bulk pair 同接口、CDC 控制接口、重复匹配、重枚举失败、取消、释放异常、借用 transport |
| BROM / Preloader | 四字节握手、首包噪声边界、echo mismatch、BE 非对称字段、未知芯片/安全位、D7 checksum、SLA challenge、认证资源超时与迟到清零 |
| DA / EMI | D8/DC 条目、v6、entry index、alias/version/subcode、签名范围、极大 count/region/address、截断、Stream 位置/所有权与短读 |
| 方言 | 每种完整 DA1/EMI/DA2/storage transcript；FLOW/MESSAGE 交错；超大/零长/错误帧；command 与 final status；XML START/END/ERR/文件/进度 |
| Flash / GPT / Sparse | eMMC user/boot/GP、UFS LU、512/4096 block、名称歧义、越界/溢出零写入、部分读/尾部 padding、Sparse RAW/FILL/DONT_CARE/损坏、最终 ACK 才完成 |
| 会话 | sync/async 同线路、并发串行、重入拒绝、预取消不污染 Ready、Raw 取消 Faulted、旧 lease 失效、disposed/late provider 释放 |
| Host strategy framework | descriptor 筛选、三 DA/同步异步阶段、作用域/代数/线程、取消/失败/重连、资源替换与认证不隐式跳过；无策略实现、默认注册、攻击二进制或 CLI 选项 |
| RPMB | region/地址/计数/key 长度、256 数据与512 wire、chunk 上限、认证错误、部分 response、counter/未知写结果、不自动重试 |
| seccfg / crypto | v3/v4 与已知向量、magic/size/digest、SW/HW profile、保留未变字段、预写快照/回读、取消/超时可能已写结果 |
| CLI | MTK 强制 LibUsb、无 COM 热插拔、显式 USB 推断、默认串口 Qcom 不变、未注册协议/未知能力、非交互资源缺失、帮助与中英文 key 对齐 |

每个任务至少：

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Mtk.Tests/GeekFlashCore.Protocol.Mtk.Tests.csproj -c Release --no-restore
dotnet build GeekFlashCore.slnx -c Release --no-restore
git diff --check
git status --short --ignored
git ls-files .tests
```

新测试工程先 restore。大型收尾运行可用的全量本地测试（Qcom/CLI/LP/Core/LibUsb/MTK），资源键配对、敏感日志及完整 diff 审查。先记录旧测试在该工作树的可用性，不把其他工作树或历史记录当成本次执行证据。

内存/吞吐：模拟 64 MiB 以上 Raw、expanded Sparse 与多包 RPMB，限制源读取窗口，记录缓冲峰值/分配、最终总量和重复操作资源数；USB 实际吞吐、取消延迟与设备端兼容必须真机或脱敏抓包补证，模拟速度不能作为真机指标。

## 10. 未决风险与验收标准

1. 当前没有本次 MTK 真机/抓包证据。所有初期“通过”仅指源码交叉核对、夹具与模拟线路；交付必须保留该限制。
2. FC 字节序、旧 DA 布局、已握手 A0、XML lifetime 和各厂商返回码存在参考差异，以明确 transcript 和实机证据推进，不靠宽泛 fallback。
3. 多设备身份匹配、Windows 驱动/CDC 接口、DA 提速的重新枚举和 LibUsb 控制传输行为需要后端 API 与真实环境确认。
4. 标准 DA、EMI 与厂商 auth/cert 不保证本地材料齐全。缺少的资源须清楚列出及定义 Provider 输入，不能承诺所有厂商能执行。
5. 漏洞、补丁、ARM 工具链与内置载荷不在当前范围。后续扩展 ABI 变化需要独立证据与测试。
6. 未知芯片、新 DA、NAND/OOB/IoT、UFS 多 RPMB region 与硬件 crypto 差异需要 profile 级能力与风险记录，不能扩大支持声明。
7. seccfg/RPMB 写入失败可能已改变设备，备份/回读/失效语义必须通过失败路径测试；未执行任何真实设备写入。
8. 标准操作与扩展共享 gate/代数；任何线上失败后不能复用旧认证状态、扩展通道或块设备视图。

完成条件：MTK-01～12 已按确认的范围落地并具备有效测试；标准与扩展组件可分别由宿主复用；LibUsb 为生产 MTK 唯一后端；全量可用测试、Release 构建、diff/资源/日志/忽略状态门禁通过；设计/进度/支持矩阵/未决硬件风险与英文提交同步。不能以仅接入枚举、协议接口或一个方言替代此次完整范围。
