# SPRD 分区容量来源自动选择设计

日期：2026-10-08；任务 SPRD-11；基线 d60b96e，工作区干净。用户要求自动检测 GPT 或其他格式。

## 范围、兼容与证据

核心/CLI PartitionTableSource 默认 Auto，枚举末尾追加，保留 Native/UserPartitionGpt 数值与手动线路。识别范围为可验证的 GPT 容量来源和 FDL 原生命名分区清单；后者不是 MBR/NAND/PMT 物理格式证明。本次不增加 MBR/PMT 解析、常见名称遍历、容量试读、重分区、未知写重发或单位启发式。KnownPartitions 优先且不查询设备。

参考固定源：YC-nw/SPRDClientCore fb20583c770c141602cf28c6db9c74eb2ef8bf92 的 GetPartitionsAndStorageInfo/CheckPartitionExist，spreadtrum_flash 64fe3e379f23c9e1ba1623964b47a326c66b5081 的 partition_list/check_partition，均先尝试 user_partition GPT，再原生 READ_PARTITION；拒绝的 READ_START 后发送 READ_END。独立实现有界线路，不沿用任意异常回退、猜容量单位、补 splloader 或全局状态。没有实机证据。

## 自动线路与校验

一次 user_partition READ_START 声明固定前缀（默认 32 KiB，最大 4 MiB）。ACK 后复用同步分片读取与 READ_END ACK；本地按既有 512/4096（或手动 sector）完整 CRC/几何校验，唯一有效接受。任一标准头位置含 EFI PART 或 MBR 55aa + protective 0xee，但无有效布局时视为损坏/窗口不足/歧义 GPT，停止且失效，不降级。完整读取没有这些 GPT 标志时，仅查询一次 READ_PARTITION。

仅初始 READ_START 的有效空 OPERATION_FAILED(0x84)/UNSUPPORTED_COMMAND(0xfe) 响应可作为该查询不可用，必须 READ_END ACK 成功后才能查询原生表。其他 NAK、携带数据的拒绝、部分帧、CRC/校验失败、READ_MIDST/READ_END 失败、取消或超时均失效，不回退或扩大窗口。该受控只读查询允许列表替代历史自动模式一律拒绝 NAK 回退的约束；手动 GPT 的任何拒绝仍失效。

原生响应须通过既有 76 字节记录、UTF16 名称/尾部、计数、重复及非零 size 校验；容量仍是 checked(size * 确认单位)。缺少单位不猜数值，不暴露虚假容量。确认原生清单后缓存内部名称/units 快照、记录 Source=Native，再抛专用内部配置异常；单 gate 内这一明确的只读配置错误保持 StorageReady，以便读取 info/UID。只有完整 READ_END 和原生表验证后才能产生该异常；写入/擦除未开始。重复调用不再探测，单位不可变，需以确认单位重新创建连接。其他异常沿用 Fault/Generation/关闭传输。

## 契约、资源与编排

TargetInfo.PartitionTableSource 在设备查询确认后为 Native/UserPartitionGpt，查询前或 KnownPartitions 为 null；GptSectorSize 仅 GPT 有值。原生快照和目标元数据在断开/失效时清除，不能跨会话。公共接口不新增异步资源等待；Core 查询均同步，共享原 gate/操作预算，池化前缀固定上限，无按镜像容量分配。日志仅来源/数值，用户可见错误/帮助中英文资源化。

自动窗口按 GPT sector 自动/手动规则预检；Native 不要求 GPT window 对齐。CLI 可省略来源或 --sprd-partition-source auto；仅显式 native 在连接前要求单位，Auto 缺单位允许查询 GPT，在确认原生时报告配置错误。显式 gpt 不降级；显式 native 不读取 user_partition。info 显示实际容量来源或未查询标记。

## 实施、测试与提交

先定义 ignored 测试：默认/显式 Auto、两种 GPT、无 GPT 标志到原生顺序及缓存、允许的启动拒绝/cleanup、手动无回退、坏 CRC/保护 MBR/歧义/窗口、NAK/超时/取消/分片、原生坏结构/重复/零容量、单位缺失保留会话与快照、KnownPartitions、视图代数及 CLI 验证。再修改 abstractions/options/metadata、Core GPT/storage/session、CLI/parser/resx，更新 README/docs/sprd/AGENTS 和实施证据。

运行完整可用 SPRD/CLI 测试、Release 解决方案构建、help、资源键、diff 和 ignored 审查。提交 feat(sprd): select validated partition sources automatically。实机待验证初始拒绝后的 READ_END ACK、前缀能力及原生单位；不把模拟当作硬件证据。
