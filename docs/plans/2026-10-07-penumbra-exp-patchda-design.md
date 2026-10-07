# Penumbra EXP 与 PatchDA 完整移植设计

日期：2026-10-07。任务：EXP-PORT-01。基线：main / `d331d0d`，启动时工作区干净。
参考：`D:/Code/Rust/penumbra-main`（无 Git 元数据，文件以 SHA256 标识，见实施记录）。

## 授权变更

用户于 2026-10-07 明确要求"全部完整的移植实现"，即在保留既有安全边界（有界分配、会话失效、取消、串行 gate、日志约束）的前提下，把此前限定为非执行占位类的四个 EXP 策略与 Penumbra 的 DA patch 算法完整移植为可执行 C# 实现。此项取代 2026-10-05/06 文档中"Execute 仅返回 NotApplicable""不实现具体补丁/特征/参数"的限制；占位类阶段框架、descriptor 路由、检查点位置、CLI 有序注入和"Completed 不等于认证成功"契约全部保留。原作者版权按用户要求在 `licenses/` 与 `NOTICE-MTK.md` 保留。

## 目标

1. 移植通用离线工具：`utils/patching.rs`（find_pattern/patch/patch_u32/patch_pattern_bytes/get_diff/get_diff_align）、`exploit/mod.rs` 的 DaEntryExt（V5/V6 哈希槽定位、哈希类型探测、DA 架构检测）与 `get_v6_payload` 容器解析。
2. 移植两套 DA patcher：`da/xflash/patch.rs` 与 `da/xml/patch.rs` 的 patch_da/patch_da1/patch_da2 全部规则（哈希重写、anti-rollback、DA SLA、security/SBC、boot_to/extloader 注入）。
3. 移植四个 EXP 的真实 Execute：
   - Unfused：三安全标志全关时打补丁并返回替换 DA（BeforeDa1）。
   - LineCode：BROM 下经 USB 控制传输与原始 sys_region_access 线路触发，复查 D8，打补丁并返回替换 DA。
   - Carbonara：DA1 就绪后经 BootTo 将补丁后 DA2 摘要写入运行中 DA1 的摘要槽，返回补丁后 DA2 替换（Da1Ready）。
   - HeapBait：XML 下 AIO1 雪橇+AIO2 溢出触发 hakujoudai，经 EXP-PATCH-MEM 对运行中 DA2 应用差异补丁（Da2Ready，不替换 DA）。
4. CLI 注入的四个策略携带嵌入 payload 仓库依赖。

## 非目标

- Penumbra DA 扩展加载（`boot_extensions`、ExtPointerTable、Penumbra2 扩展 ABI、SEJ/RPMB/peek/poke 扩展命令）不在本轮：该部分属 `2026-10-05-mtk-nonexploit-parity` 的扩展 ABI 计划，且当前项目已有独立的标准硬件加密/RPMB 服务。Da2Authenticated 检查点保持无内置策略。
- 独立 `PlProtocol::exploit`（bootpl 前的 LineCode 入口）不移植：Legacy 不增加参考自动路径的既有约定不变。
- dummy SLA 回退、`crash`、自动重连不移植（既有规则明确排除）。

## 关键映射与线路

| Penumbra | C# 承载 | 线路差异说明 |
| --- | --- | --- |
| `exploit!` 宏（Err 吞掉后继续） | 框架有序调度：前置缺失（缺资源/缺芯片表/无控制传输）在接触设备前返回 NotApplicable 继续下一项；已产生设备 I/O 后的失败返回 Failed 并失效会话 | 明确偏离参考的"失败继续"；符合本项目失败契约 |
| Unfused `protocol.patch_da` | 策略内对 DA 副本打补丁，`MtkExploitResult` 携带替换 DA | 使用框架既有的 BeforeDa1 替换校验 |
| LineCode `PlProtocol` 原始字节 | 策略经 `context.Transport`（ScopedUsb）逐字节复刻：echo 命令、BE 参数、BE 状态、控制传输 0x21/0x20、0x80/0x06、0xA1/0x21 | 不复用 `IMtkBromSession.Read32`（其 BE 字解释与参考 LE 解释不同，见风险）；字节序列与参考完全一致 |
| Carbonara XFlash `boot_to(addr,data)` | channel.SendData×3（0x10008 LE、16 字节参数、摘要）+ ReceiveData 校验 {0,0x434E5953} | 参考在命令后不读中间状态；与本项目标准 DA2 上传线路（读中间状态）不同，策略内单独复刻参考线路 |
| Carbonara XML `boot_to` | channel.BeginXmlCommand("BOOT-TO")/SendXmlFile/EndXmlCommand | 等价 |
| HeapBait 原始 ACK 序列 | channel.SendData/ReceiveData 手工复刻（OK@值 ACK、GETBAITED、容忍性读） | 容忍性读失败继续与参考一致，记录为失步风险 |
| EXP-PATCH-MEM / EXP-CALL-FUNC | `MtkXmlCodec` 允许列表新增两项；SECURITY-SET-ALLINONE-SIGNATURE 的 source_file 上限单独放宽到 8192（5000 字节堆塑形必需） | 其余命令上限保持 4096 |
| `chip().uart0()` | `MtkChipCatalog` 新增 Uart0 元数据（来源 mtkclient brom_config，GPLv3，既有归属） | 芯片缺失 → NotApplicable |

补丁目标为区域字节副本（DA2 ≤16MB、DA1 有界），不物化整个容器；替换 DA 使用新增 `MtkDaOverlaySource`（借用原 source + 覆盖列表），已上传区域校验按既有窗口比较。所有摘要复用 `MtkExploitBinaryTools.ComputeDigest`；模式/字符串搜索复用 Analysis 分析器与 `MtkExploitBinaryTools.FindPattern`。

## 公共契约变化

- `MtkExploitDependencies`/`IMtkExploitStrategy` 签名不变；四个策略类的 Execute 从占位变为真实实现，缺依赖（Empty 仓库）时保持 NotApplicable——既有无依赖注入行为不变。
- `MtkXmlCodec` 允许列表扩展为内部协议细节，不改变公共 API。
- `MtkChipDescriptor` 增加 `uint? Uart0`（可空，不破坏构造）。
- 无新增公共异常类型；日志不输出载荷、地址表、摘要或认证材料，策略到达阶段沿用既有 Debug/Information 键。

## 文件范围

- 新增 `Exploits/Penumbra/`：MtkDaPatching、MtkDaPayloadFormat、MtkDaMetadata、MtkDaOverlaySource、XFlashDaPatcher、XmlDaPatcher、LinecodeTrigger、HeapBaitRunner。
- 修改：四个策略类、MtkChipCatalog（Uart0）、MtkXmlCodec（允许列表）、CLI 注入、AGENTS、NOTICE-MTK、licenses、README（如需）、本计划。

## 测试与验证

ignored `.tests/GeekFlashCore.Protocol.Mtk.Tests` 测试先行：

1. 补丁工具：pattern/patch/越界/diff 对齐。
2. 载荷格式：v6 容器解析（合法/魔数错/越界）、Linecode 表（合成表 + 内嵌 brom_defuse.bin 34 条目与边界）。
3. 元数据：V5/V6 哈希槽、哈希类型（16/20/32/未知）、架构检测向量。
4. patcher：合成 DA 数据（手排 BL + 标记字符串）验证每条规则命中/未命中路径。
5. 策略线路：ScriptedUsb 全线路——Unfused 替换字节、LineCode 控制传输与原始序列逐字节断言、Carbonara XFlash/XML BootTo 位置与摘要、HeapBait 三阶段与 EXP-PATCH-MEM 差异、前置缺失 NotApplicable、设备 I/O 后失败失效会话、标准无注入线路回归。

命令：MTK 全量、五工程回归、`dotnet build GeekFlashCore.slnx -c Release --no-restore`、`git diff --check`、ignored 检查。分批提交（离线工具/patcher；策略+CLI；文档与许可）。

## 风险

- 无真机验证：LineCode 的 LE 字解释与参考一致但与 mtkclient BE 惯例冲突，需硬件证据裁决；Carbonara XFlash BootTo 无中间状态读与本项目已验证标准线路不同，均按参考逐字节复刻并记录。
- HeapBait 雪橇按参考常量构造约 50MiB 单次分配（有界但显著）；失败后的容忍性读可能造成流失步。
- 保护特征/补丁规则只证明静态移植，不构成漏洞适用性证明。
