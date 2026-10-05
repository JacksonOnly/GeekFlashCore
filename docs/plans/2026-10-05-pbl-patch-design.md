# Qualcomm PBL Patch 设计

日期：2026-10-05；任务：PBL-01。

## 目标与兼容性

按用户最新要求，所有品牌均只按 Sahara SoC 标识匹配 SDM845、SDM710、SM6125（Snapdragon 665），显示 Patch PBL 提示并自动执行，再继续用户选择的 Loader 和 Firehose 初始化。710/845 保持 `D:/Code/CSharp/GeekFlashTool/OPPOLoaderTest/Program.cs` 与 11 个 Resources/*.bin 的原有线路和字节。665 改用用户提供的 `D:/Downloads/665.txt`（Bus Hound）抓包：重复标记必须展开，补全先传补丁 ELF 再传 Loader 的两个阶段。本轮不操作硬件，不增加其他芯片或持久分区写入。

Core 增加默认关闭的 `QcomProtocolOptions.EnablePblPatch`，CLI 启用；显式低层上传与异步 ConnectAsync 共用校验、Patch 与上传实现。Core 用户未启用时保持既有线路。用户已确认“显示提示后自动执行”，不增加二次确认。

## 状态与线路

先探测身份、获取并校验 Loader，随后启动 Patch；缺失芯片身份、非目标 SoC、Firehose 续接及 Oplus Digest 续接不执行。OEM 不参与限制。Patch 只发生于 Loader 上传前。

现有探测使 Sahara 处于 Command 模式，新增内部入口只发送 SwitchMode(ImageTxPending)，由 Patch 执行器消费并校验新的 Hello；这是身份探测后恢复参考项目起始状态的适配，需真机复核。内部入口不发送普通 HelloResponse，不提前消费首个 READ_DATA。

- 710：专用 Hello → 32 字节响应 → ELF header → 32 → program headers → 32 → segment1 → 32 → common zero → 16 → 175 次单字节 0x13 / 48 字节响应 → segment2 → segment3。
- 845：普通参考 Hello → 32 → ELF header → 32 → program headers → 32 → segment1 → 32 → segment2 → 16 → common zero → 16 → 175 次八字节 0x13 包 / 48 字节响应 → segment3。
- 710/845：关闭传输 → 可取消等待 1000 ms → 重新打开 → Flush → 普通参考 Hello；不再探测身份。重新创建 Sahara/wire，保留原身份，置 ImageTxPending，复用现有 Loader 读请求/Done 校验。
- 665：初始 Hello 后最多 100 次发送 13 9A 9A 9A；完整响应仅允许 Hello 或 32 字节 READ_DATA64。抓包 12 组 OUT 的重复次数合计 95，不能当作 12 次。首 READ_DATA64 后发送普通 HelloResponse，按 6 个精确请求发送内置补丁 ELF 数据；收到新的 Hello，再等待首个用户 Loader READ_DATA64、发送 HelloResponse，把首包放回会话缓冲。这个阶段不发送 Done、不关闭、不等待重开，也不能把用户 Loader 提前用于补丁请求。
- 665 内置资源按捕获的偏移重建为 20480 字节窗口：0/64、64/896、4096/4096、8192/4096、12288/4096、16384/4096。960..4095 没有捕获，零填仅用于定位，禁止提供该区域；这不是完整原始 Loader。补丁阶段只允许 image_id=13 和上述精确顺序/范围，任何偏差失败关闭。单请求最大 4096，总写入 17344，不按不可信 ELF 声明分配或上传其他段。

## 边界、所有权与失败恢复

协议 I/O 保持同步。1000 ms 重开等待在 QcomProtocol 编排层：异步 ConnectAsync 使用可取消 Task.Delay；同步上传使用有界取消等待。Patch 执行器无异步资源等待、无无界重试。

片段作为生产程序集嵌入资源，加载时验证固定长度、只打开自己拥有的流；710/845 单片段至多 4096 字节，665 固定窗口 20480 字节。固定响应使用 stackalloc 48 字节，665 变长包严格校验头和期望长度后读取，不按外部长度分配。每次发送/接收、重开和上传边界检查取消，失败/超时/取消立即清理当前会话并关闭传输；不盲目重放 Patch，不发送 Firehose，不自动宣告设备已被修补。

Patch 后的 Loader 读请求与参考项目一致，最大 4 MiB，且必须完整落在对应镜像范围内；现有非 Patch Sahara 上传的 0xFF 兼容填充规则保留。Patch 开始后的清理只关闭，不发送普通 reset；同步上传同样关联实例生命周期取消。资源阶段或未开始 Patch 的原有 Sahara 失败继续采用现有恢复规则。

阶段信息与最终失败资源化；不记录片段、Loader 内容、认证或 Digest。既有会话串行门禁与代数校验保持。Patch 后仍须 Loader 成功 Done、Firehose Configure 和存储初始化才能报告联机成功。

## 范围、测试与提交

文件：Qcom 配置、QcomProtocol 编排与独立 PBL partial、Sahara 内部状态入口、Internals/PblPatch 执行器与 12 个资源、csproj、CLI 选项组合、中英资源、使用说明、抓包提取工具和计划。

测试先行：三个芯片与其他/未知/OEM 无关组合；资源长度/字节一致；710/845 全部顺序与 175 次循环；665 95 次展开重试、6 块补丁、重入 Hello/首包、100 次上限、非法命令/长度/范围/身份、分片/短读/超时；预取消与循环/等待/重开/上传中取消；重开失败；同步/异步 Loader 后续线路与失败清理；旧宿主关闭配置不触发。测试位于 ignored `.tests`，模拟线路和抓包比对不等同本实现真机证据。

命令：目标与完整 Qcom/CLI Release 测试、Release 解决方案构建、资源键对照、git diff --check、测试/产物 ignored 与跟踪检查。每阶段小步验证，提交包含生产代码/片段和必要文档，使用英文提交；不合并或推送。

未决风险：Command→ImageTxPending 过渡、补丁后 1000 ms 时序、串口/LibUsb 重开和设备重枚举、665 的命令模式探测对原始状态的影响，均须硬件复核。参考固定响应内容未解码，仅按抓包长度消耗；不能将固定片段成功发送等同设备 Patch 成功。
