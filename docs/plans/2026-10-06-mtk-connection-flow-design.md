# MTK 分阶段连接流程补齐

日期：2026-10-06。任务：FLOW-01。用户已授权在当前项目实现 [参考流程](2026-10-06-penumbra-exploit-stage-analysis.md)，明确不实现任何 EXP。工作区：`D:/Code/CSharp/GeekFlashCore`，基线 `9159d62`。

## 目标、范围与兼容

现有实现已经包含三 DA 的四个宿主检查点、XML DA1/DA2 分别认证、XFlash 认证后重新查询包长及作用域通道。本轮保留这些实现，补齐参考正常初始化中的可选命令兼容和宿主 OS 参数，并用模拟传输验证完整顺序与失败边界。

不增加具体策略、自动注册、DA 安全补丁、载荷、dummy 签名、自动 crash/重枚举或扩展上传。没有宿主策略时只执行标准协议；已有 `Completed` 语义和四个枚举数值保持不变，不引入参考的 patched 布尔值或吞错误后继续执行。

保留 `BeforeDa1` 的既有公共契约：Probe/资源校验后、标准 BROM 认证和 DA1 上传前。Rust 的自动策略在 Device.init 的可选 auth 之后，这是已记录的编排差异；本轮没有具体策略，因此不为复刻 Linecode 改变既有安全配置复查和资源替换契约。之后的三个检查点对齐参考的准确命令边界。

## 正常线路与实际缺口

- XFlash：Probe/WDT → BeforeDa1 → cert/auth/BROM SLA → D7/D5 → C0/SYNC/环境/硬件/EMI/校验配置/第一次包长 → Da1Ready → DA2 BootTo 确认 → Da2Ready → 标准 DA2 SLA → 第二次包长 → Da2Authenticated → 存储探测。
- XML：Probe/WDT → BeforeDa1 → cert/auth/BROM SLA → D7/D5 → Runtime/HostSupported/HostInfo/第一次 Notify/progress/END → 标准 DA1 SLA → Da1Ready → DA2 BOOT-TO/下载/END → 第二次 HostSupported/Notify/progress/END → Da2Ready → 标准 DA2 SLA → Da2Authenticated → 存储探测。
- Legacy/IoT 保留原独立线路及已上传区域保护；不复制 Rust 缺失的 Legacy 行为。

既有四个检查点的调用条件与可用通道如下。条件由门面筛选，芯片型号及其他宿主条件交给回调从 Target/InitialTarget 判断，不加入内置适配表。

| 检查点 | 已完成的命令/阶段 | 后续命令/阶段 | 调用条件与范围 |
| --- | --- | --- | --- |
| BeforeDa1 | Probe/WDT、DA 资源初次校验 | 标准 cert/auth/BROM SLA、D7/D5 | 显式注入且阶段、初始 BROM/Preloader 模式、DA 方言匹配；可用标准 BROM 通道，资源替换后重新校验；不省略正常认证 |
| Da1Ready | XFlash 首次包长；XML 首次 HOST/NOTIFY/END 和 DA1 SLA；Legacy/IoT 原初始化边界 | 各方言继续启动剩余 DA | 同一筛选；XFlash/XML 可用 DA 通道；资源替换必须保留已上传区域，IoT 已上传两段 |
| Da2Ready | XFlash BOOT-TO 确认；XML BOOT-TO/下载/END、第二次 HOST/NOTIFY/END；Legacy 原启动确认 | 标准 DA2 SLA 查询/认证 | 同一筛选；不接受 DA 替换；DA2 认证状态仍未查询，不把 Completed 视为认证成功 |
| Da2Authenticated | DA2 SLA 已认证或明确 NotRequired/Unsupported；XFlash 第二次包长已校验 | 存储探测、StorageReady | 同一筛选；最终检查点不自动上传扩展或登记扩展能力 |

所有检查点均共享连接 gate、代数、取消和时间预算，回调返回后上下文失效。未注入则不调用；Failed/ReconnectRequired、抛异常、断连或非法结果终止当前会话，需要重新连接。

缺口一：参考两次 HOST-SUPPORTED-COMMANDS 都是可选，当前 XML 把明确 Unsupported 视为致命错误。改为仅允许精确 `ERR!UNSUPPORTED` ACK，之后必须完整读取、验证并确认 END。END 允许 OK，或 ERR 且唯一 message 为 ERR!UNSUPPORTED；模糊 ACK、其他错误、缺少 END、I/O 失败、取消、超时均终止连接。复用既有 SLA Unsupported 验证逻辑，不改变必选命令的处理。

缺口二：XFlash environment 的 system_os 和 XML Runtime 的 system_os 固定为 Linux。按当前宿主 Windows/其他平台选择与参考一致的值；XML version=1.1、initialize_dram 在 adv 节点的现有结构保持。测试解析实际发送的帧/XML，而不是只检查函数返回。

## 契约、状态与资源

不增加公共 API。可选命令帮助函数是 XmlSession 内部实现，返回仅表示该命令是否受支持；SLA 的 Unsupported 仍由认证查询设置独立证据，不当作认证成功。

所有 USB 和回调继续同步并共用 gate、连接预算和代数。异步入口只在门面等待资源/签名，迟到响应仍释放和清零；有无策略及同步/异步的标准写入字节应一致。源流保持借用，只释放协议打开的流；本轮不增加按 DA/镜像总大小增长的物化。

可选命令回退由 XmlSession 记录一次 Warning，资源键中英文配对，只包含固定命令名。不记录设备原文、完整 XML、认证材料或策略 ID。其他失败沿现有 Fault 路径失效、关闭传输并要求重新连接，不自动重试。

## 文件、步骤与验证

生产范围：`Da/XmlSession.cs`、`Da/XFlashSession.cs`、MTK 中英 resx；必要时修订流程说明。测试位于 ignored `.tests/GeekFlashCore.Protocol.Mtk.Tests`，从原功能工作区复制已有正常测试夹具到当前主工作区，排除原 KamakiriTests，保留当前其他测试工程。

1. FLOW-01A：先增加模拟测试，复现 DA1/DA2 HostSupported 明确 Unsupported 导致连接失败，以及 Windows OS 参数错误。
2. FLOW-01B：实施有界可选命令/完整 END 处理和宿主参数；复查既有 SLA 失败矩阵。
3. FLOW-01C：定向/MTK 完整回归、其他工程回归、Release 构建、资源键/敏感日志/diff/ignored 审查与文档记录。

矩阵：两个 DA 阶段、同步/异步、支持/Unsupported ACK+OK END/Unsupported ACK+ERR END、模糊 ACK、异常 END、缺失 END、取消、分片、必选命令拒绝；标准认证失败不可到达下一检查点；没有策略与纯观察回调的标准线路一致；XFlash/XML OS 字段和 XML Runtime adv/version 正确。

命令：当前主工作区 MTK 定向及完整 `dotnet test ... -c Release --no-restore`；CLI/Qcom/Core/LP 完整回归；`dotnet build GeekFlashCore.slnx -c Release --no-restore`；`git diff --check` 和 ignored/跟踪文件审查。功能独立提交，必要的参考分析和设计文档随同保存；不包含 `.tests` 或产物。

## 未决风险

参考快照无 Git 元数据，关键 SHA256 见分析文档。可选命令 Unsupported 的 ACK/END 组合仅有源码和模拟证据；真实 DA 固件、USB 吞吐、不同平台初始化参数仍待硬件验证。框架检查点和测试不证明任何漏洞可用，也不代表宿主已经加载扩展。
