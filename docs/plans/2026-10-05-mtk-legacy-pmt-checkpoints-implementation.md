# Legacy PMT 与宿主策略检查点实施记录

日期：2026-10-05。设计见 [设计方案](2026-10-05-mtk-legacy-pmt-checkpoints-design.md)。

## 启动证据

- 工作区：C:/Users/a1375/.codex/worktrees/8d7a/GeekFlashCore，分支 codex/mtk-protocol，基线 534302b。
- 已有本地测试基线 692 通过（MTK 243、CLI 134、Qcom 251、Core 9、LP 55），Release 构建通过。
- 原有两个未跟踪 kamakiri 方案文件不在本任务范围。
- 当前 Legacy 仅通过 READ_PMT 显式布局读取；XML 缺 DA1 SLA，Da2Ready 在第二次硬件初始化前；XFlash DA2 包长在 SLA 前查询。均为代码事实，无硬件验证。

## 进度

- PMT-01：新增 DiskV1，与原 READ_PMT 三布局分开；默认 Legacy eMMC USER/512 无 GPT 使用磁盘表。主/镜像分别验证标记、1.0 版本和尾标记，40 个条目校验名称/范围/重叠。只有元数据标记无效允许一次镜像回退；I/O 与条目失败不回退。直接诊断保留 flags、序号和副本属性；缓存的通用分区接口继续保留范围与物理区域。
- PMT-01 验证：新增 16 个测试先失败；实现后 LegacyDiskPmtTests、DaDiagnosticsTests、PartitionRecoveryTests 共 31 通过（2026-10-05，Release/no-restore）。覆盖主表偏移、分片、镜像、坏版本/尾、校验和失败、非法条目、默认发现及缓存、NOR 前置拒绝。无真实硬件证据。
- PMT-01 CLI：`mtk-pmt disk` 与 `--mtk-pmt-layout disk` 可用；MtkStandardCliTests 16 通过。`dotnet build GeekFlashCore.slnx -c Release --no-restore` 0 警告/0 错误；`git diff --check` 通过。提交 59f81e7。
- HOOK-01：XML DA1 初始化与门面签名编排分开，新增 Da1Sla；首次 HostInfo/Notify 顺序与参考一致，Da1Ready 在 DA1 SLA 后，Da2Ready 在第二次 HOST/NOTIFY/END 后。XFlash DA2Ready 在 BOOT-TO 确认后，重新查包长在 SLA 后且在最终宿主检查点前。
- HOOK-01：新增认证证据与已上传区域数量（原阶段值保持），明确 Unsupported 完整 END 才可继续；失败不吞掉。IoT Da1Ready 比较两段预上传内容，允许修改待发送 DA3；上下文仍受线程、代数、预算和过期保护，Completed 不改变标准认证要求。
- HOOK-01 测试：两项旧线路断言先失败；新增认证/IoT 测试最初 7 失败/2 通过；实现后相关 63 通过。补充认证证据、晚到签名、超时/取消、非法 DA1 属性和 IoT 正常替换后，MTK 全量 274 通过（2026-10-05）。空/超限签名两项测试先失败，增加发送前校验后通过；新增认证后非法包长阻止扩展检查点，最终 MTK 277 通过。

## 最终验收（2026-10-05）

| 命令 / 检查 | 结果 |
| --- | --- |
| `dotnet test .tests/GeekFlashCore.Protocol.Mtk.Tests/GeekFlashCore.Protocol.Mtk.Tests.csproj -c Release --no-restore` | 277 通过，0 失败/跳过，含原流式内存边界测试 |
| CLI / Qcom / Core / Android LP 同配置完整测试 | 135 / 251 / 9 / 55 通过，0 失败/跳过 |
| 五工程合计 | 727 通过，比本轮基线新增 35 项 |
| `dotnet build GeekFlashCore.slnx -c Release --no-restore` | 0 警告/0 错误 |
| MTK / CLI 中英文资源键及格式占位符 | 12 / 212 个键成对且占位符匹配 |
| `git diff --check` 与完整代码/文档差异审查 | 通过；只有行尾转换提示，无空白错误 |
| `git ls-files .tests` 与已跟踪 bin/obj/temp 检查 | 0；测试与构建产物保持 ignored |
| 生产具体策略 / 默认注册 / 漏洞载荷 | 无；只保留宿主注入接口、上下文和调用边界 |

生产文件范围：DiskV1 抽象、LegacyPmt/Diagnostics/Storage/Metadata、CLI 参数与帮助；IMtkExploitStrategy/MtkModels、XmlSession/XFlashSession、MtkProtocol/DaAuthentication/Exploits；README、NOTICE 和方案/进度文档。本地测试不提交。工作区原有两个未跟踪 Kamakiri 文档保持原样；没有 push、PR 或发布。

## 恢复入口与兼容性

后续先读本记录、设计及更新后的 mtk-exploit-framework.md；从真实 Legacy eMMC PT/MPT 读盘和新版 XML DA1/DA2 抓包核实开始。宿主签名器需显式支持新追加的 `Da1Sla`，原 `DaSla` 仍为 DA2、枚举数值不变。原四阶段数值和 Execute 签名不变，但 XML/XFlash 的阶段含义已按新版参考修正；不能沿用旧 XML Da2Ready 在 HOST/NOTIFY 前的假设。

## 未决风险

无真实设备证据。DiskV1 仅适用 eMMC USER/512/v1.0，格式无 CRC，头尾通过不能代替条目边界检查；未知介质、版本、第三方固件 Unsupported 生命周期和实际宿主策略需要另行硬件验证。仅精确 ERR!UNSUPPORTED ACK 加完整 END 可继续，其他响应失败。同步宿主回调仍依赖合作取消；Borrowed DA source 必须稳定可重开。框架测试不证明任何具体漏洞可用。

## 主分支集成（2026-10-06）

- 用户授权“并入主分支”。目标工作区 D:/Code/CSharp/GeekFlashCore，main 原提交 173d1bb，合并前已跟踪工作区干净。
- `git merge-base --is-ancestor main codex/mtk-protocol` 成功；`git merge --ff-only codex/mtk-protocol` 将全部 22 个功能/修订提交快进至 bd271cf，无冲突。包含标准 MTK 框架、Legacy/NAND/IoT、硬件加密、CLI、磁盘 PMT 与宿主检查点，不增加具体漏洞实现。
- 主工作区 `dotnet build GeekFlashCore.slnx -c Release --verbosity quiet` 成功，0 警告/0 错误。
- 原功能工作区保留 ignored 测试夹具，运行 MTK Release `--no-build --no-restore`：277 通过，0 失败/跳过。`git diff --exit-code main codex/mtk-protocol -- src GeekFlashCore.slnx` 无差异，确认测试代码版本与合并后的生产代码一致。
- `git diff --check` 通过。合并后仅追加本验收记录并独立提交；原功能工作区的两个未跟踪 Kamakiri 文档保留，不纳入 main。未执行远端推送；硬件兼容性风险保持上述记录。

## 参考 EXP 阶段分析（2026-10-06 / EXP-AUDIT-01）

用户要求分析 `D:/Code/Rust/penumbra-main` 全部 EXP 的位置、前后命令和条件。完整源码证据、调用时序、34 个 Linecode HW code 与关键文件 SHA256 记录于 [阶段分析](2026-10-06-penumbra-exploit-stage-analysis.md)。本轮只新增分析记录，未修改生产代码或参考目录。

静态结论：当前快照有 Linecode/Carbonara/HeapBait 三个漏洞策略和 Unfused 补丁入口；XML 默认上传没有 Linecode；Carbonara 在 DA1 初始化之后（XML 还在 DA1 SLA 之后），HeapBait 在 XML 第二次 HOST/NOTIFY/progress/END 后、DA2 SLA 前。后续扩展加载在 DA2 SLA 流程返回后，XFlash 还需再次查询包长。`patched` 导致候选策略互斥，错误吞掉和部分补丁未命中使它不能表示已认证或扩展已就绪。

验证：主工作区 Release/no-restore 构建 0 警告/0 错误；原功能工作区现有阶段/认证框架定向测试 56 通过，0 失败/跳过；main 与 codex/mtk-protocol 的 src/slnx 比对无差异。`git diff --check` 通过，新增文档 no-index 检查无空白错误；30 个源码链接有效，参考 8 个关键文件指纹复查一致。工作区只有分析与实施记录两处预期文档变更，本轮未提交。上述测试不执行 Rust EXP，没有新增硬件或漏洞成功证据。后续从分析文档及实际 DA 版本/正常协议抓包核对继续。

## 正常连接流程补齐（2026-10-06 / FLOW-01）

用户进一步授权在当前项目实现流程，明确不实现任何 EXP。保留既有四检查点、三 DA 和标准认证编排，补齐 XML 两次可选 HostSupported 的精确 Unsupported/完整 END 回退，并修正 XFlash/XML 的宿主 OS 参数。BeforeDa1 的公共边界保持在标准 BROM 认证前；没有增加策略、默认注册、载荷、补丁或自动扩展上传。

新增 40 项正常协议/观察回调测试，先记录 18 失败，再修复至定向 96、MTK 全量 317 通过。主工作区 CLI/Qcom/Core/LP 55/251/9/55 通过，五工程合计 687，Release 构建 0 警告/0 错误；资源键、产物忽略与差异检查通过。没有真机或漏洞运行证据，完整设计、变更和验证命令见 [实施记录](2026-10-06-mtk-connection-flow-implementation.md)。
