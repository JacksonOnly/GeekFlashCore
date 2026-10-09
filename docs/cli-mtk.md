# MediaTek CLI

## 连接与重连

```text
geekflash --protocol mtk --loader DA.bin --mtk-preloader preloader.bin
```

不指定命令进入交互模式，成功命令保持会话；操作失败时保留提示符，但未知线上结果仍使旧会话失效，不能继续使用旧块设备或扩展。`exit` 退出。显式单次命令仍在完成后退出进程。

```text
reconnect
reconnect brom
reconnect da1 xflash
reconnect da2 xml
```

重连重新打开 USB，优先使用原物理拓扑/序列号、原 DA 和存储快照。Auto 被动检查一次可用启动字节并保留给后续协议；C0 由 DA1 初始化线路继续验证。已有干净命令边界且设备没有新启动帧时可沿原 DA2 继续。XML 启动帧不能单独区分 DA1/DA2；USB PID 也不是阶段证明，信息不足时要求确认，不依次发送不同方言试探。

DA1 继续初始化/EMI 和 DA2 上传，不重发 BROM 上传；DA2 只验证对应协议、认证状态和存储，不再次上传 DA。Legacy DA2 必须有原会话已观察的存储几何，并验证 USB speed ACK；没有几何或 NAND 时请复位回 BROM。首次接入已有 DA，完整硬件身份无法查询时会询问硬件编号、subcode、硬件/软件版本、BROM 版本与 security raw，DA 文件必须匹配。

中途读写失败后，不自动重发命令。重新接管 DA2 前必须确认已经复位/重新进入空闲命令边界；不能向仍等待镜像数据的设备发送查询帧。取消确认不发送恢复命令。非交互模式无法确认缺失信息时直接报错。

## 传输与日志

- XFlash 写包遵循设备协商，上限默认 2 MiB，不再被 BROM 的 64 KiB 缓冲截断；读取默认使用 64 KiB 池化 USB 窗口，完整接收设备数据帧才 ACK/状态确认。原生短包直接消费，不为凑满宿主窗口额外发起小读取。SDK 可用 `MaximumXFlashWritePacketLength` 限制写包。
- 进度节流约 100 ms，保留开始、首个进度和完成事件；不并行发送协议命令。
- 默认控制台和文件仅 Information 及以上；`--verbose` 才记录逐包 Debug。旧 DA 不支持 SLA 查询和已验证的旧 UFS GPT 边界仅为 Debug，验证逻辑不放宽。
- 分区偏移显示原始字节数，起始扇区使用该区域真实逻辑块单位；`read/write sector` 的 MTK 区域编号为 DA wire ID（UFS USER=3、eMMC USER=8），不是零基 LUN。

用户 MT6893/UFS 实机日志确认扩展加载、分区发现和 persist 读取成功；开启逐包 Debug 日志时读取 69,697,536 字节用时约 1.78 秒（37.28 MiB/s）。后续短包优化的吞吐仍需同模式复测，不能据模拟传输承诺倍率。界面沿用 MB 标签，数值按 1024 进制计算。

## Preloader 与扩展

```text
read Preloader preloader.bin
read PreloaderBackup preloader-backup.bin
write Preloader preloader.bin
write PreloaderBackup preloader.bin
```

XFlash/XML 使用 DA 原生命名接口处理启动头；读取以 BOOT 区容量为上限，实际输出为 DA 返回的镜像长度，不导出整个 BOOT1/2。写入不将 bin 原样写入原始 BOOT 区。Legacy 不猜测头布局，拒绝此命名入口；命名擦除也拒绝，原始 sector 操作仍是显式危险入口。

连接后离线确认 DA2 确实包含扩展加载器，再解析 Penumbra 函数地址、填充嵌入扩展指针表，通过 BOOT-TO 上传并验证 ACK/context。只有这些步骤都成功才输出“DA 扩展已加载”。缺少加载器、UART 或函数地址时不上传，保持标准存储能力；线上失败使会话失效。XFlash DA2 的 Thumb2 定位独立于 DA1 的 ARM 架构。

默认内置扩展 ABI 为 Penumbra2。另行接管宿主加载的旧扩展可显式指定 `--mtk-extension-abi legacy`。`IMtkProtocol.Capabilities` 的 RequiresExtension 表示核心需要可选扩展服务，不是加载结果；`mtk-capabilities` 使用已验证服务的 `MtkDaExtension.Capabilities`，仅当前代数有效。DA2 重连沿用此前已加载的扩展时重新验证 ACK/context，不再上传代码。加密基址及 UFS RPMB 容量仍需已确认的配置，不猜测容量；Supported 不表示 RPMB 已认证或写入无需密钥。

## Scatter

```text
mtk-scatter plan MT6893_scatter.txt
mtk-scatter flash MT6893_scatter.txt image-directory backup-directory
```

eMMC/UFS USER 布局按名称、偏移和长度与设备比较。不一致时先备份当前主/备 GPT，再显示差异，必须输入 `yes` 才更新 GPT 并刷写；其他输入取消，不写设备。随后沿用镜像预检、备份、备 GPT 先写、回读校验和保护数据恢复。备份文件不覆盖，重试需另选备份目录。相同布局不重建 GPT，保留其 GUID/属性；NAND 不套用 GPT，保留既有原生 Scatter 线路。

离线转换无需 USB/Loader：

```text
mtk-scatter to-gpt scatter.txt output-prefix ufs 4096 USER-CAPACITY-BYTES
mtk-scatter from-gpt pgpt.bin scatter.txt ufs 4096 USER-CAPACITY-BYTES MT6893
```

存储可选 `emmc`/`ufs`，逻辑块为 512/4096，容量必须是已确认的 USER 字节数。to-gpt 输出紧凑 `.pgpt.bin` 和 `.sgpt.bin`，不物化整盘。from-gpt 支持严格校验的紧凑主/备 GPT，校验 CRC、容量、边界、名称和重叠；不静默修复损坏表。对已观察的 UFS 4K/128×128/FirstUsable=34 特定布局，先验证原始 CRC 与物理数组，再仅在宿主副本中以元数据末端 LBA 6 验证，不更改输入文件或设备。

GPT 只有 USER 分区几何，不包含 BOOT 区、平台、镜像文件名或 Scatter 特有下载策略。导出使用 `file_name: NONE`、`is_download: false`，使用前须按实际镜像补充，不能把转换结果当作可直接刷写的完整工厂包。
