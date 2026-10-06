# MTK 嵌入 payload 资源设计

日期：2026-10-06。任务：PAYLOAD-01。用户明确要求把 Penumbra 的 payloads 复制到当前项目，仅作为资源嵌入并接通 MtkExploitResourceStore / MtkExploitDependencyCatalog，不实现具体 EXP。

## 范围与兼容

原样复制 `D:/Code/Rust/penumbra-main/core/payloads` 的七个 .bin 到 MTK 核心 `Exploits/Resources/Payloads`，通过固定 LogicalName 嵌入程序集。该授权取代依赖层早期“不分发参考二进制”的范围。源快照没有 Git 元数据，以文件长度和 SHA-256 标识，不使用旧 Penumbra 提交号。保留上游来源、作者及许可声明。

保留原五个资源枚举数值，末尾新增 XFlashExtension / XmlExtension。清单升级至内部版本 2，七个条目包含标识、文件名、清单资源名、长度、SHA-256；四项策略的既有依赖映射保持。新增不可变资源元数据查询，原 Get / GetReferenceFileName 保持兼容。

## 资源入口和所有权

新增显式 `MtkExploitResourceStore.FromEmbeddedResources()`，只读取有界 JSON 元数据、创建惰性 IDataSource，不打开二进制。只在宿主 OpenRead / source.OpenStream 时打开所选资源，验证可读/可定位/准确长度和流式 SHA-256，归零后交付只读窗口。每次打开独立流，调用方负责释放；任何校验失败释放已打开流。没有全量 byte[] 缓存。同步入口保持同步，不阻塞等待异步资源；取消检查沿用读取前后边界。

Empty、MtkExploitDependencies.Empty、真实无参策略构造保持原行为。四个 Execute 不读取资源、不调用分析器/变换器，仍返回 NotApplicable。CLI 的已授权有序占位调用、DAA 校验、四检查点、协议字节、串行 gate、预算和代数均不变。不新增下载、本机资源发现、设备传输、地址/参数填充、定位或补丁算法。

## 文件与步骤

1. ignored 测试先定义七种嵌入资源、元数据、字节完整性、显式仓库、窗口/重开/取消/缺失/错误摘要及默认空行为。
2. 修改资源枚举、目录、仓库及 csproj；复制七个二进制并记录来源和许可。
3. 同步 AGENTS、README、NOTICE、历史范围修订和实施记录，审查策略/协议没有变化。
4. 运行目标测试、五工程回归、Release 解决方案构建、git diff --check、跟踪与 ignored 检查；独立提交生产代码、资源和文档，测试不提交。

## 验证矩阵与风险

元数据查询不打开二进制；七文件长度/摘要与源快照一致；工厂注册完整；所有资源只读、可重开、定位边界有效；旧五个枚举值保持；未知标识、缺失、错误长度/摘要、取消和失败关闭流。既有四 Execute 拒绝访问依赖测试继续通过。

目标与全量命令使用 `.tests/GeekFlashCore.Protocol.Mtk.Tests`、CLI、Qcom、Core、Android.Lp，各自 Release/no-restore；再构建 `GeekFlashCore.slnx -c Release --no-restore`。不并行编译共享项目。没有硬件执行或资源与真实 DA 兼容性证据，本任务只验证资源包装；本地 mtk-payloads 源目录仅用于核查归属，不能证明这些二进制对应其 HEAD 的可复现构建。
