# SPRD 主分支合并记录

日期：2026-10-08；任务 SPRD-12；用户授权将 SPRD 工作并入当前项目。

## 范围与工作区

- 目标：`D:\Code\CSharp\GeekFlashCore` 的 `main`，合并前为 `5930ec8`；来源为 `codex/sprd-support` 的 `633d2a6`，共同基线为 `9bb6818`。
- 使用 `git merge --no-ff --no-commit codex/sprd-support`，自动合并无冲突。引入六项 SPRD 提交及其公共契约、同步 BSL/流式存储、CLI、入口/扇区/容量来源自动选择与设计实施文档；本次不改变这些已经验证的行为。
- 保留 main 已提交的 MTK line coding 修复及 GPT/Preloader 别名映射。相关 MTK 源码不属于本次暂存范围。
- 主工作区原有 `MtkProtocolOptions.cs` 超时配置修改保留为未暂存状态；合并前后文件 SHA256 一致，不纳入合并提交。
- 从来源工作树复制 SPRD 测试工程和四个独立 CLI 测试文件到主工作区 ignored `.tests`；没有覆盖主工作区的既有测试工程或测试文件。测试、基线归档和构建产物均不提交。

## 验证证据

以下均为本地构建、CPU/模拟传输测试，没有实机证据；主工作区验证包含上述用户已有的未提交 MTK 超时配置。

| 命令/检查 | 结果 |
| --- | --- |
| `dotnet restore GeekFlashCore.slnx -v quiet` | 成功，新增 SPRD 项目依赖可恢复 |
| `dotnet build GeekFlashCore.slnx -c Release --no-restore -v quiet` | 成功，0 warning / 0 error |
| SPRD Release 测试 | 155/155 通过 |
| CLI Release 测试（恢复新增传递引用后） | 132/132 通过，包含 SPRD 的 26 项测试 |
| Qcom Release 测试 | 506/506 通过 |
| Core Release 测试 | 38/38 通过 |
| Android LP Release 测试 | 63/63 通过 |
| MTK Release 全量测试 | 551/559 通过，8 项失败，见下述合并前对照 |
| Firmware Release 测试 | 111/111 通过，包含真实固件的流式 Super/映射与分配检查，耗时约 4 分 20 秒，无输出镜像 |
| `dotnet .../geekflash.dll help sprd` | exit 0，默认自动入口/来源/扇区及手动覆盖说明可见 |
| 资源与合并审查 | SPRD、SPRD.Abstractions 和 CLI 中英文资源键一致；SPRD 生产代码及适配器与来源分支一致；git diff --check 与暂存差异检查通过，.tests/temp/bin/obj 未被跟踪 |

### 合并前 MTK 对照

将 `git archive --format=zip 5930ec8` 解压到 ignored `temp/sprd-merge-baseline-5930ec8`，复制主工作区同一批 MTK 测试源码及工程，在归档中的未修改源代码上运行 Release 全量测试。结果同为 551/559，通过数及八个失败用例一致；不包含 SPRD 合并或用户未提交的超时修改。

- 七项分区断言仍假定返回列表只有 boot，main 的 `5930ec8` 已增加 Preloader/PrimaryGPT/BackupGPT 别名；旧断言分别位于 `PartitionRecoveryTests`（五项）、`ExtendedStorageTests`（一项）和 `LegacyDiskPmtTests`（一项）。本次没有修改既有 MTK 测试或生产行为。
- 一项 `LoaderTests.ExistingReferenceContainersParseWithoutLoadingTheEntireImage` 缺少外部 `D:\Code\Python\mtkclient\mtkclient\Loader\oppo_2_MTK_AllInOne_DA.bin`。
- MTK 测试夹具的既有 `CS0649` 警告在合并前归档及合并后相同；正式解决方案 Release 构建无警告。

## 风险与恢复

本次没有新增设备验证，SPRD 的 FDL 地址、原生容量单位、64 位布局、禁转义、Raw flush/USB ZLP 等硬件风险继续以各能力实施记录为准。下一次先读取本记录、`2026-10-08-sprd-partition-source-implementation.md` 与 `docs/sprd.md`，再检查 main 工作区。现有 MTK 旧断言及外部夹具失败作为独立本地测试问题保留，不能把本次全量回归表述为全部通过。

合并提交号由包含本记录的 `merge(sprd): integrate BSL and automatic storage detection` 提交恢复；只合入本地 main。
