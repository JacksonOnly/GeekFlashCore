# MTK BROM 标准初始化与 DA 芯片映射

日期：2026-10-05。任务 BROM-01～03；用户授权完善标准 BROM、看门狗及交互引导。漏洞仅保留既有宿主接口，不注册或实现策略。

## 证据与边界

14:06:35 日志显示握手和 Probe 成功，失败来自 `matching DA`；旧 CLI 没有配置 ChipProfile，未关闭看门狗。该日志没有实际硬件编号，不能认定设备一定返回 0x0950。

用户提供的 792544-byte DA 含一个条目：DA code 0x6893、subcode 0x8A00、最低 HW 0xCA00、SW 0。参考 `D:/Code/Python/mtkclient` 的 mtk_preloader.py、mtk_config.py、brom_config.py 和 daconfig.py（基准 e9fcf97）：芯片 0x0950 名为 MT6893，DA code 为 0x6893，WDT 0x10007000 / 0x22000064。本地 brom_config 仅追加 print，与所需标准字段无差异；不执行该模块，不修改参考树。

## 设计

1. 建立仅含 CPU 名称、说明、DA code、普通 WDT 寄存器的已知芯片目录。未知芯片没有默认寄存器。0x6261 特殊 remap 与 0x6592 附加写保持未支持，不自动操作。目录不含漏洞地址或载荷。
2. 保持库 Probe 默认只查询；新增显式初始化选项，CLI 启用。在 FD 得到硬件编号与初始版本后关闭已知 WDT，再执行 D8 / FE / FF / FC，符合参考顺序。显式 profile 优先；WDT ACK 错误终止并失效，不能假装成功。库 Connect / ConnectAsync 同样在 FD 后立即准备 WDT，先 Probe 再 Connect 时在请求资源前补准备。一次会话只写一次，重连重新初始化。兼容性变化：已知芯片 Connect 不再因缺少显式 profile 跳过 WDT；独立库 Probe 默认线路保留。
3. 快照新增 FD 初始版本、CPU 描述、DA code 和 WDT 状态，兼容既有构造函数。不自动读取、记录 MEID/SOCID；既有显式敏感缓冲接口保留。原始安全位与版本不改写。
4. DA parser 和核心校验共用 DA alias，显式覆盖优先；保留 subcode、最低版本校验和匹配歧义拒绝，比参考忽略 subcode 更严格。只读取容器元数据和有界 DA2 marker，不修改 DA。
5. CLI 资源回调在选择 DA 前显示芯片和安全配置，因此材料失败仍保留诊断信息。交互选择文件在打开 USB 前完成，沿用 REC-01 的有限等待、取消和错误展示。默认交互缺少 DA 时提示路径，明确 --loader 时直接使用。

同步线路、资源所有权、连接总预算、会话串行化及失效规则沿用现有实现。日志/控制台文本中英资源化，记录非敏感硬件快照；所有认证、签名、完整身份继续禁止日志。

## 范围与验证

涉及 Mtk.Abstractions 模型/选项、Mtk 芯片目录/BROM/DA 校验、CLI host/资源，以及对应文档。测试固定 ignored `.tests`：先复现已知芯片 alias、FD→WDT→D8 顺序、无未知地址、显式 profile、ACK 失败、去重、CLI 提前展示。用用户 DA 作只读选择测试，模拟目标不代表真机返回值。

运行 MTK、CLI、Qcom 及其余既有测试，Release solution build、资源键/占位符检查、git diff --check；一个独立 fix 提交。真实 WDT 写入、设备版本/subcode、DA 执行和认证/EMI 尚待用户真机验证，不执行任何漏洞利用。

## 进度

- BROM-01：新增 67 个芯片名称/DA alias；61 个有明确普通 WDT 配置，其余返回 ProfileUnavailable。未知芯片不套用参考通用默认地址。显式 16-bit profile 优先、ACK 失败即失效、会话去重与重连复位通过模拟测试。
- BROM-02：parser 和核心校验使用同一 alias；先写两个失败测试，均复现 matching DA，修改后通过。用户 DA 只读选择测试在模拟 0x0950 / 0x8A00 / HW 0xCA00 / SW 0 条件下通过；没有上传或执行 DA，不能视为用户设备匹配证据。
- BROM-03：控制台在选择 DA 前显示 CPU、硬件/DA 编号、subcode、FD/FC HW、SW、BROM/FE 应答、启动阶段、安全位和 WDT 状态；库保留非敏感日志快照。CLI 缺少资源时仍先输出芯片，模拟连接失败后会话失效。沿用并回归交互文件选择/取消/预算。
- 全部本地测试：MTK 131、CLI 104、Qcom 251、Core 9、Android Lp 55，合计 550 项通过。Release solution 构建 0 警告 / 0 错误；差异与中英资源校验通过。ignored 测试与产物不提交。

## 未决风险与恢复

真实 FD/FC 编号、WDT 应答、安全配置、DA/EMI 初始化需要用户重新运行最新 CLI 获取证据。只有标准认证材料/合法 signer 可满足 SLA/DAA/证书；没有绕过机制。特殊 WDT 及未知芯片必须提供经过确认的显式配置，不能把 ProfileUnavailable 当成功。读取本文、REC 文档和 implementation 的进度后，从新日志中的硬件快照与首个失败阶段继续。
