# MTK 标准功能补全设计

日期：2026-10-05。工作区：`C:\Users\a1375\.codex\worktrees\8d7a\GeekFlashCore`，分支 `codex/mtk-protocol`，基线 `76ee4bf`。

用户要求对照 `D:\Code\Rust\penumbra` 与 `D:\Code\Python\mtkclient` 完善非漏洞功能。本轮不增加、修改或执行漏洞策略、补丁、载荷及既有阶段框架；保留两份已有未跟踪 kamakiri 计划。仅参考正常命令线路，认证始终由合法宿主材料完成。

## 目标与兼容性

保留既有 BROM、三种 DA、认证、扩展 ABI、RPMB、seccfg、Raw/Sparse 和 USB 选择；补齐标准功能与异常处理。公共功能通过独立可选接口扩展，避免给现有第三方 IMtkProtocol 实现新增必需成员。同步 I/O、异步资源获取、单 gate 与 generation 保持。不得将未知设备类型或未知返回状态当作支持。

## 参考事实与任务

| 任务 | 行为与文件范围 | 验证 |
| --- | --- | --- |
| STD-01 | DA channel 固定回调线程/代数；连接、传输预算与资源预检；XFlash 连接 agent、帧大小、XML reboot 语义纠正 | 先复现跨线程、超期、错误响应与无发包拒绝 |
| STD-02 | GPT 有界读取分离 header/entry，复用 GptParser 做条目校验；CRC、非固定 entry LBA、backup fallback；Legacy PMT 的明确布局 | 主/备损坏、512/4096 sector、异常范围、无整盘物化 |
| STD-03 | 正常 DA 查询、寄存器、系统属性、分区表/设备信息入口；只读结果有界且敏感结果可清零 | XFlash DEVCTRL command/data/status；Legacy BE；XML START/ACK/file/END |
| STD-04 | 对有明确参考线路的 SDMMC/NOR/NAND 建模；NAND 的逻辑数据与 OOB/BMT 区分，未知布局拒绝；正常 reboot 各模式显式映射 | 类型/几何/partition ID、擦除对齐、read/write/status，不复制参考的无界循环 |
| STD-05 | A/B boot control 格式校验与有备份的最小扇区写/回读；通用存储进度与 CLI 查询接入 | CRC、slot 状态、邻接字节保留、generation、写后未知结果无重试 |
| STD-06 | 全量回归、资源/日志/大源检查、支持矩阵及逐功能对应 | MTK/Qcom/CLI/Core/LP tests、Release build、diff 与 ignored 检查 |

参考本地源码包括 penumbra `da/{xflash,xml}/{flash,storage,cmds,da_protocol}`、`core/{storage,bootctrl}`，mtkclient `Library/DA/{legacy,xflash,xml}`、`partition.py` 与 `gpt.py`。参考工作树只读，其修订号和差异不当作真机证据。XFlash read 的每包 ACK/status、write 的 checksum/chunk/final status、XML 的完整命令生命周期必须保留。

## 生命周期、资源和失败策略

所有标准 API 和扩展共用 gate/操作预算。通道不能跨线程、过期或跨代数使用；线上失败使整个会话失效；纯参数/资源验证错误不发包。对设备不支持的已完整消费状态可以类型化报告，不盲试其他协议。读取借用 Stream；Core 只释放自己打开的流。输出/敏感材料清理遵循既有资源约定。

外部 frame、table、count、range、转换与分配在操作前有界校验。大镜像流式；GPT/PMT/bootctrl 只读取有限元数据与最小写窗口。不得记录身份、认证材料、完整 XML/内存/密钥。用户文本中英文资源配对。

参考核对后的范围修订：XML 原生 `READ-PARTITION-TABLE` 使用严格结构和 version 属性允许列表；XML NAND 的 total size 与 page/spare/erase 元数据只支持普通读取，未报告 usable/BMT 时标记 `LogicalCapacityConfirmed=false` 并只读。Legacy SDMMC 仅有可确认的 0x62 写线路，read/erase 不按猜测的 storage 字节执行；Legacy NOR 擦除也拒绝。Extensions 增加 Shared CRC 工具依赖，仍不依赖协议核心。RPMB erase 复用已认证的 Write 和有界零流。不可寻址 Raw 用 4-byte prefix 保持首部；检测到 Sparse 则在写前要求 seekable source，不按总镜像物化。

## 交付与风险

生产变更按独立能力提交，`.tests` 永不提交。先在 ignored 本地工程添加行为测试再实现。本地 `KamakiriTests.cs` 引用缺失策略，保留文件、在本地 csproj 排除它；不修复或实现漏洞。无真实设备写入；硬件兼容、USB 吞吐、厂商认证、特殊 NAND 与硬件 crypto 需要真实 profile/抓包，不编造支持。已加载扩展的 ABI 只能保证其提供的验证范围，RPMB 不改为普通块设备。
