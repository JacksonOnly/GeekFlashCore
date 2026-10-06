# MTK 嵌入 payload 资源实施记录

日期：2026-10-06。任务：PAYLOAD-01。方案：[设计](2026-10-06-mtk-embedded-payload-resources-design.md)。

## 启动

工作区 D:/Code/CSharp/GeekFlashCore，main / 6d1e630，启动时干净。用户明确授权复制七个已有 payload，仅用于嵌入资源。此前未分发二进制的依赖记录按历史保留，最新范围以本记录为准。基线五工程 MTK/CLI/Qcom/Core/LP 为 464/96/457/9/55，共 1081 项通过。

## 进度

- PAYLOAD-01A：已核查源目录七个 .bin，总长 181520 字节。全局目录原有五项，新增两项 DA 扩展资源；四策略依赖映射不变。设计保持惰性显式仓库及默认 Empty，不改 Execute 或设备线路。
- PAYLOAD-01B：先添加 ignored EmbeddedPayloadResourceTests 并修订旧“不含 .bin”断言；首次执行因新增入口、模型与资源枚举缺失出现 CS0117/CS0246。实现两个新增枚举值、不可变 ResourceInfo、版本2目录、显式 FromEmbeddedResources 和内部惰性源；七文件通过 Copy-Item 原样复制。目标资源/依赖 75 项通过，包含 23 项新增资源测试。
- PAYLOAD-01C：逐文件对照源目录和复制目录的长度/SHA-256 全部一致；测试另核验程序集中的七个流及稳定 LogicalName。目录内 .gitattributes 声明 .bin 为 binary，禁止 Git 换行转换；暂存 blob 与原始字节的 Git hash-object 另作对照。显式仓库只读取 JSON，资源流打开时检查准确长度/流式 SHA-256 并归零，失败释放自有流；返回只读窗口，可独立重开/定位。保留 Empty、借用源所有权、旧五枚举值、旧目录入口和策略映射；四 Execute、CLI 和协议没有生产改动。
- PAYLOAD-01D：同步 AGENTS/README/NOTICE/框架和旧依赖记录的最新范围，保留历史事实；复制上游完整 AGPLv3，补充原作者与组件许可声明。全量五工程 487/96/457/9/55（共1104）通过，Release 构建 0 警告/0 错误；空白、忽略与跟踪审查通过，测试/构建产物不提交。

## 验证与风险

| 验证命令 / 检查 | 结果 |
| --- | --- |
| MTK Release/no-restore，资源及依赖定向 | 首次因缺入口/类型编译失败；实现后75通过 |
| `dotnet test .tests/GeekFlashCore.Protocol.Mtk.Tests/GeekFlashCore.Protocol.Mtk.Tests.csproj -c Release --no-restore --verbosity quiet` | 487通过，0失败/跳过 |
| CLI / Qcom / Core / Android.Lp，相同Release/no-restore参数 | 96 / 457 / 9 / 55通过，0失败/跳过；五工程总计1104 |
| `dotnet build GeekFlashCore.slnx -c Release --no-restore --verbosity quiet` | 0警告/0错误 |
| 七文件源/目标长度和SHA-256、程序集资源元数据及字节 | 全部一致，总长181520字节；稳定逻辑名，既有五枚举值不变 |
| 独立流、只读/定位/尾部/越界/重开、取消、缺失/错误长度/错误摘要、默认空、占位策略 | 新增23项均通过；既有依赖测试继续验证借用资源与失败/取消释放，不调用服务或设备context |
| `git diff --check`、完整差异审查、`git ls-files` / `git check-ignore` | 通过；没有新增日志、用户文本资源键或设备线路；.tests/bin/obj保持ignored，七个生产.bin可跟踪 |

生产修改仅限资源枚举/目录/仓库、两个新增元数据/源文件、JSON、MTK csproj、七个二进制及文档/许可。独立提交使用 `feat(mtk): embed Penumbra payload resources`，提交号通过 git log 查询；本地测试不提交。没有推送、发布、修改参考目录或连接设备。

任务已完成。源文件名、长度和摘要见 `src/GeekFlashCore.Protocol.Mtk/Exploits/Resources/dependencies.json`；调用示例见 README。元数据/工厂没有打开二进制，宿主显式打开时才校验并返回字节，默认依赖不加载资源。四 Execute 仍不读取这些资源或执行具体利用逻辑，NA 后的策略顺序和标准认证要求保持。

没有硬件执行或资源与真实 DA 兼容性证据，本轮仅验证资源包装。源快照无 Git 信息，不能声称对应本地 mtk-payloads HEAD 的准确构建；版权来源在 NOTICE 和 licenses 保留。恢复时先读本记录/设计及工作区状态，再按用户新需求推进，不能把嵌入资源作为利用或认证成功的证据。
