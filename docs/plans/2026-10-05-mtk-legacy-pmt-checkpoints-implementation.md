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
- PMT-01 CLI：`mtk-pmt disk` 与 `--mtk-pmt-layout disk` 可用；MtkStandardCliTests 16 通过。`dotnet build GeekFlashCore.slnx -c Release --no-restore` 0 警告/0 错误；`git diff --check` 通过。
- HOOK-01：调用点研究完成，准备补齐标准认证边界和上下文，具体漏洞实现保持空缺。

## 未决风险

无真实设备证据。未知介质、版本、第三方固件 Unsupported 生命周期和实际宿主策略需要另行硬件验证；框架测试不证明漏洞或安全绕过可用。
