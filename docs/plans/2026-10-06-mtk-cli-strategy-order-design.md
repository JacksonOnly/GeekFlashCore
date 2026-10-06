# MTK CLI 占位策略接入与有序阶段调用设计

日期：2026-10-06。任务：HOST-ORDER-01。基线：main / `2764589`，启动工作区干净。

## 授权、目标与兼容

用户指出 BeforeDa1 只有方法调用、没有策略执行，要求先调用策略，NotApplicable 继续。用户随后明确选择“只继续下一策略，之后仍校验 DAA 材料”。本次授权覆盖 CLI 显式配置现有四个非执行占位类和通用阶段编排；取代此前禁止 CLI 注册占位类的范围，不授权具体 EXP、设备算法、补丁、资源下载或认证绕过。

核心未注入策略时仍走原标准线路；原单策略入口保持源码兼容和行为，保留原构造与 CreateUsb 签名的转发重载以兼容已编译宿主。新增可选有序策略集合参数，最多64项，构造时复制并校验每个 descriptor；单策略和集合不能同时传入。集合为空等同未注入，核心不实例化默认策略，不管理宿主策略/依赖的生命周期。

## 线路、结果与状态

CLI 顺序为 Unfused、LineCode、Carbonara、HeapBait。各项依其已有 descriptor 筛选阶段、初始 BROM/Preloader 与 DA 方言：XFlash/BROM 的 BeforeDa1 依次调用前两项，XML 只调用 Unfused；Da1Ready 调用 Carbonara，XML Da2Ready 调用 HeapBait，Legacy 不匹配。不读取四类的 Dependencies，四个 Execute 保持原实现。

每个匹配项有独立作用域 context，返回后立即失效；NotApplicable 继续下一匹配项，Completed 应用并校验原有结果后结束本阶段策略尝试，后续阶段仍独立筛选。Failed/ReconnectRequired、非法/null 结果、异常、取消或超时终止并失效，不能当成 NotApplicable 降级。标准 gate、同步 I/O、代数、有限连接预算和替换 DA/安全复查条件不变。

PrepareBootResources 仍按“DA 元数据及内容边界校验 → 所有适用 BeforeDa1 策略尝试 → 更新 DA 引用 → DAA/certificate 校验”执行。NotApplicable/Completed 都不代表认证成功，缺材料仍终止且不发送 DA/auth 命令。标准认证检查不被移除。

## 日志、资源与性能

沿用阶段到达/结果日志，新增 Debug 调用序号/集合大小/阶段；不打印任意宿主 descriptor Id、策略私有文本或敏感材料。中英资源成对。集合仅在构造时有界复制，逐阶段最多64项，不按 DA 大小分配或复制；替换资源仍借用并流式校验。CLI 每个 protocol 独立构造四项，无全局可变策略状态。

## 文件、测试与提交

范围：MtkProtocol 构造和 CreateUsb、MtkProtocol.Exploits 阶段调度、MTK 中英日志资源、CLI MtkProtocolHostAdapter、AGENTS/README/框架及设计/实施记录。不修改四个占位类、DA 命令、标准认证或存储。

先在 ignored `.tests` 定义有序调用、sync/async/cached Probe、NotApplicable 后缺认证、Completed/终止结果、逐回调失效、descriptor 快照/有限集合、三 DA/启动模式路由与标准 USB 写入等价；CLI 用实际 factory 及日志证明调用发生在材料异常前。缺少集合 API/CLI 注入应先失败。

验证：目标测试、五工程全量 Release tests、solution Release build、git diff --check、资源键/占位符、完整差异及 ignored 状态。独立提交 `fix(mtk): invoke CLI placeholder strategies before authentication`。测试/夹具/日志/bin/obj 不提交。

参考仅核对调用边界：本地 penumbra-main `core/src/da/xflash/protocol.rs:377`、`core/src/da/xml/protocol.rs:607` 与 `core/src/macros.rs`；本轮不执行参考 EXP。它们吞错继续的行为不移植。无真实设备证据；本轮代码成功调用占位策略不保证能够完成 DAA 或 DA 上传。
