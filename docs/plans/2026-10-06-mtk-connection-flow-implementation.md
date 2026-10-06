# MTK 分阶段连接流程实施记录

日期：2026-10-06。任务：FLOW-01。方案：[连接流程补齐](2026-10-06-mtk-connection-flow-design.md)。

## 启动证据

主工作区 `D:/Code/CSharp/GeekFlashCore`，HEAD `9159d62`。开始时只有上一轮分析文档和实施记录两处已知变更，保留其内容。当前四个宿主检查点与 XML 分阶段认证/XFlash 认证后包长查询已经实现；发现 HostSupported Unsupported 兼容和宿主 OS 参数两处正常协议缺口。无具体 EXP 实现授权。

主工作区缺少 MTK 本地测试工程，原功能工作区 `C:/Users/a1375/.codex/worktrees/8d7a/GeekFlashCore` 保留既有正常测试。将 43 个源码/项目文件复制至当前 ignored `.tests`，排除 KamakiriTests，不复制 bin/obj，不覆盖其他现有测试工程；项目 restore 成功。

## 进度

- FLOW-01A（2026-10-06）：先增加 ConnectionFlowTests 的 40 个模拟案例，修复前 18 失败/22 通过/0 跳过，复现两个阶段 HostSupported Unsupported 不能回退、缺 END 未按完整生命周期等待、Windows 环境参数错误及回退线路无法对比的问题。夹具仅添加可注入标准响应和写入观察；测试中的 Observer 仅记录阶段并返回 NotApplicable，无漏洞操作。
- FLOW-01B（2026-10-06）：XmlSession 增加内部 BeginOptional，复用原 DA.SLA 的严格 Unsupported ACK/END 验证；两次 HostSupported 可回退，必选命令仍严格失败。回退前确认 END/ACK 并复查预算，仅记录固定命令和 BootStage 的资源化 Warning。XFlash environment 和 XML Runtime 按实际宿主选择 Windows/Linux 值。未新增公共 API、改变四阶段值或移动认证边界。
- FLOW-01B 验证：ConnectionFlowTests、ExploitFrameworkTests、XmlCheckpointAuthenticationTests 定向 96 通过；MTK 全量 317 通过（原正常测试 277 + 本轮 40），均 0 失败/跳过。覆盖同步/异步、3 字节分片、两种明确 Unsupported END、非法 ACK/END、缺 END 超时、命令执行中取消、必选命令拒绝、有无观察回调写入字节一致、OS/XML adv 参数；既有认证失败、IoT 区域保护和流式内存边界仍通过。
- FLOW-01C（2026-10-06）：主工作区其他四工程回归全部通过，Release 构建 0 警告/0 错误；13 个 MTK 中英资源键唯一、成对、占位符匹配；没有跟踪 .tests/bin/obj/temp 文件。代码审查确认没有策略实现、默认注册、安全补丁、载荷、dummy auth、自动 crash/重枚举或扩展注入。完整差异与空白检查通过，测试和产物保持 ignored。

## 最终验证

| 命令 / 检查 | 结果 |
| --- | --- |
| MTK 定向 Release/no-restore，过滤 ConnectionFlowTests、ExploitFrameworkTests、XmlCheckpointAuthenticationTests | 96 通过，0 失败/跳过 |
| `dotnet test .tests/GeekFlashCore.Protocol.Mtk.Tests/GeekFlashCore.Protocol.Mtk.Tests.csproj -c Release --no-restore --no-build` | 317 通过，0 失败/跳过 |
| CLI / Qcom / Core / Android LP，各自 `dotnet test ... -c Release --no-restore --verbosity quiet` | 55 / 251 / 9 / 55 通过，0 失败/跳过 |
| 当前主工作区五工程合计 | 687 通过；CLI 采用主工作区现有 ignored 工程，未以历史功能工作区的 135 项代替本次证据 |
| `dotnet build GeekFlashCore.slnx -c Release --no-restore --verbosity quiet` | 0 警告/0 错误 |
| 中英资源键、占位符、日志内容、跟踪产物及 `git diff --check` | 通过 |

提交范围：XmlSession、XFlashSession、MTK 两个 resx、本次设计/实施文档、上一轮参考阶段分析与其进度记录；本地测试不提交。功能提交使用 `fix(mtk): complete staged DA initialization`，具体提交号由 `git log` 查询，避免文档自引用。未修改参考目录，未推送、发布或连接真机。

## 风险与恢复

没有真机证据；参考 Unsupported 组合与平台初始化参数须后续合法设备抓包核实。本次在 Windows 上验证了实际发送值，其他系统分支只有源码证据。BeforeDa1 继续位于标准 BROM 认证之前，与 Rust 自动策略位于 init 可选认证之后的差异保持并明确记录。

本轮实现和本地验证已完成。恢复时先读本记录、设计和参考阶段分析；下一步是合法设备上的正常连接抓包核对，不实现策略、载荷、DA 安全补丁、dummy auth 或自动崩溃线路。
