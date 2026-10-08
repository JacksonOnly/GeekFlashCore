# SPRD 上游补全实施记录

日期：2026-10-08；任务：SPRD-05～08；设计见同日 upstream-completion-design。

## 进度

- SPRD-05：开始时基线 c3267b8，codex/sprd-support，工作区干净；读取已有设计、实施与 docs/sprd.md。上游已读并固定 SHA：YC fb20583、spreadtrum_flash 64fe3e3；独立实现常规 GPT/Raw/UID 线路，设计已依据当前用户授权确认范围。
- SPRD-05：先写 GPT/Raw/UID 测试，因新增类型/依赖缺失而失败；随后独立实现。GPT 复用本仓 GptParser Strict CRC、禁止未修补几何与空类型条目，读取确认前缀后校验原始主头、数组边界、可用范围及主/备元数据空间，返回不可变精确容量。KnownPartitions 优先，无查询回退和特殊分区猜测。
- SPRD-05：补充两项先失败的几何测试，复现通用 parser 允许 firstUsable 与元数据重叠、lastUsable 包含备份头；在 SPRD 适配层增加主/备份数组区间约束后通过，不修改通用 GPT 语义。
- SPRD-06：Raw v1/v2 使用显式 profile；有 EXEC 信息则精确校验 mode/flush，USB 包大小在连接 I/O 前校验。0x28 在 FDL2/禁转义后，v1 逐窗 0x31 LE64/LE32 或 v2 一次 0x33，原样字节与可选 ZLP 后逐窗 ACK；Loader 仍 framed，无失败降级/重试/END。非定位源前缀、Sparse 校验展开、源流释放、窗口清零与既有 gate/Generation 保持。
- SPRD-06：Chip UID 0x1a→0xab，长度 1～256 字节，独立复制，无日志/缓存；接口默认不支持实现保持宿主兼容。坏响应复用失效状态。
- SPRD-07：CLI 新增 GPT source/sector/window 和 Raw mode/flush/USB packet 配置、显式 sprd-chip-uid；新选项仍要求 --protocol sprd，缺少 Raw/GPT profile 在连接前拒绝。既有默认 Qcom、原生表与 framed 线路保持；帮助、补全、中英文资源和 docs/sprd.md/README/AGENTS 恢复入口已更新。
- SPRD-08：完整可用本地测试与 Release 构建通过；本工作树仍只有 SPRD/CLI 两个 ignored 测试工程，无历史 Qcom/MTK 测试源。提交只包含本轮生产代码与文档，确切提交号由包含本记录的提交恢复。
- 提交拆分：核心与设计已提交 `bf847e3 feat(sprd): add confirmed GPT and raw FDL2 profiles`；CLI/使用文档/恢复记录使用 `feat(cli): expose confirmed SPRD storage profiles`。

## 验证证据

2026-10-08，全部为参考源/模拟/CPU 证据，无实机：

| 命令/检查 | 结果 |
| --- | --- |
| `dotnet test .tests/GeekFlashCore.Protocol.Sprd.Tests/GeekFlashCore.Protocol.Sprd.Tests.csproj -c Release --no-restore` | 86/86；含初版 43 项，新测试覆盖 512/4096 GPT、超过 4 GiB、CRC/数组窗口/主备范围/重复/重叠、缓存、Raw 两种顺序、NAK/ACK timeout/取消/截断/多余源数据、Sparse、前缀与所有权、ZLP 和失败、能力不匹配、UID 拷贝/空/长/错误响应 |
| `dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore` | 15/15；含初版 8 项，新 profile/前置校验与 UID 命令参数 |
| 64 MiB Raw v2（虚拟源，模拟 ACK，池已预热） | 最大 Write 固定 1048576 字节；当前线程累计分配 3512 字节，elapsed 1 ms；只测主机流与模拟写计数，未执行 USB，不是吞吐保证或进程峰值内存 |
| 既有 64 MiB framed Raw | 最大帧 8200 字节，累计分配 525720 字节，elapsed 333 ms；同样仅模拟证据 |
| `dotnet build GeekFlashCore.slnx -c Release --no-restore` | 成功，0 warning / 0 error |
| `geekflash help sprd` / `help sprd-chip-uid` | 返回 0，正确列出 GPT/Raw profile、显式 UID 命令和现有限制 |
| 资源键、`git diff --check` 和 ignored | 三个相关项目中英文资源键一致，差异检查通过；.tests/temp/bin/obj 未被 Git 跟踪 |

## 风险与恢复

无实机证据；默认线路保持。恢复先读本设计/实施、原 SPRD 文档和工作区状态，再运行 ignored .tests。GPT 窗口、Raw flush/USB packet 必须按设备确认，不能自动试探或重发。Raw 直接 FDL2 无 EXEC 元数据，宿主须保证此前 FDL 支持对应线路；有数据传输后出错需断开重连。签名验证/最后窗口延迟应在有限 CommandTimeout 中按设备配置，不无限等待。下一步先用合法匹配 FDL 做只读 GPT/UID，再在可恢复的测试分区验证 Raw v1/v2、ZLP、取消/重连。DIAG、NV 专用格式、重分区、PAC 清单适配及整盘浏览仍为后续独立任务。
