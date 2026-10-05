# Qualcomm PBL Patch 实施进度

日期：2026-10-05；任务：PBL-01。

- 起点：codex/oplus-digest-auto-20261005，HEAD f9ff352，受跟踪工作区干净；上一任务 Qcom 351、CLI 55 项通过。
- 已读参考：OPPOLoaderTest/Program.cs；710/845 分别 175 次固定交互和 1000 ms 重开，665 最多 100 次且复用首个 READ_DATA64；11 个固定资源存在。
- 设计：见 `2026-10-05-pbl-patch-design.md`，按用户需求形成设计后分步实施；协议同步 I/O 与资源所有权不变。最新需求为所有品牌的三个芯片，710/845 线路和资源不变，只修正 665 并通用化；已同步设计后实施。
- 交互已确认：用户选择“显示提示后自动执行”。无二次确认；Core EnablePblPatch 默认关闭，CLI 默认开启，仅根据精确的芯片映射判断，OEM 不作为门槛。
- 已实现：Internals 下独立同步执行器、12 个嵌入片段。710/845 原 11 个片段逐字节保持；门面层执行可取消的 1000 ms 关闭/重开窗口。身份探测后的 SwitchMode 过渡由 Sahara 提供内部入口，新的 Hello 交给 Patch 执行器。
- 665 抓包：用户原临时路径已失效，随后提供 D:/Downloads/665.txt 并说明括号重复计数；12 组触发 OUT 展开为 95 次。提取六块共 17344 字节，重建 20480 字节窗口；未观察的 3136 字节仅零填占位且禁止请求。资源 SHA256 ec2f75c34a1f89a207a528c83aef5ac42873d94231cb5bf49b70c0b238ed1a55。补丁六个请求严格校验身份/范围/顺序，随后 Hello、Loader 首 READ_DATA64、HelloResponse，回放首包给既有上传器；不使用用户 Loader 回答补丁请求，不 Done/重开。提取工具与完整证据见抓包分析文档。
- 已实现：Loader 在 Patch 前验证，Patch 后请求严格限制文件范围及单次 4 MiB；Patch/后续 Loader 失败或取消直接关闭，不执行普通 reset。旧 Core 配置默认为关闭，原上传填充与 Firehose/Digest 续接保持既有行为。仅新路径增加固定片段和 48 字节栈缓冲，不新增 Loader 大小物化；引用的 Loader 校验器原有内存行为本轮未改。
- 测试先行：初版执行器 17 项、连接 14 项、范围 3 项分别确认失败后最小实现；最新需求先修改 OEM/665 六块流程的测试，48 项中 20 项失败，移除 OEM 门槛并补齐资源阶段后通过。最终 61 项目标测试通过，覆盖品牌不限、参考字节与175次循环、665第1/3/95/100次及上限、抓包重复计数/六块字节、3字节分片、非法命令/长度/身份/未捕获范围/重复请求、异常重入顺序、已排队的第二个 Loader 请求、取消/重开失败、资源先校验、零 Loader 写入和清理行为、同步/异步完整连接。
- 验证命令与结果：`dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore --verbosity quiet` 初次 410/410，通过后补充两项队列/未知身份测试，目标 61/61；最终追加 `--no-build` 完整回归 412/412 通过。CLI 55/55 通过，共 467 项 C# 测试，无跳过。`python .tests/test_sm6125_extractor.py`，4/4 通过（原始字节、重复计数篡改、缺失偏移、资源字节篡改）；提取工具成功复现相同资源。`dotnet build GeekFlashCore.slnx -c Release --no-restore --verbosity quiet`，0 警告/0 错误。中英资源键一致；`git diff --check` 通过，`.tests` ignored 且 git ls-files .tests 无输出。
- 资源/性能证据：原 11 个资源总计 19608 字节，与参考文件 SHA256 对照一致；新增 665 固定窗口 20480 字节，与提取输出一致，12 个资源共 40088 字节。线路测试另逐包比对参考/抓包字节。纯内存无分配传输下，通用化后完整 175 次交互稳态新增分配：710 为 12456 字节、845 为 14328 字节（均小于 64 KiB）；该值仅说明 Core 执行器分配，不代表真实传输吞吐。665 仅增加固定 20 KiB 窗口和栈包缓冲，不按 Loader 或异常 ELF 声明大小增长。
- 工作区：仅生产代码、固定片段和必要文档提交；测试/夹具与 bin/obj 保持 ignored。实现阶段保留分支，未合并或推送；后续用户授权合并见下方记录。
- 风险和继续位置：本轮未操作硬件，Command 模式恢复、665 探测后的状态、1000 ms 重开窗口、串口与 LibUsb 重新枚举待验证。下一步从用户真机新 EDL 连接开始，核对自动提示、参考片段响应和 Loader/Firehose 初始化；不能根据片段成功发送就宣称实际 Patch 成功。

## 合并复核（2026-10-05，MERGE-PBL-01）

- 用户明确要求合并；主工作区 D:/Code/CSharp/GeekFlashCore 的 main 与当前功能工作区均无受跟踪修改。main 从 c300957 无冲突快进至 9c42699，纳入 f9ff352（Digest 内容自动识别）和 9c42699（三芯片通用 PBL Patch）。未推送、未删除分支或工作区。
- 合并后主工作区执行 `dotnet build GeekFlashCore.slnx -c Release --no-restore --verbosity quiet`，0 警告/0 错误。功能工作区与 main 源码一致，在该工作区完整 Release Qcom 412/412、CLI 55/55 通过，Python 提取校验 4/4 通过；主工作区原有 ignored 测试文件保持原样。
- 首次并行测试构建遇到共享 obj 写锁（CS2012）；CLI 完成后串行重跑 Qcom 通过，无需修改生产代码。git diff --check、源码一致性、两个工作区状态与 .tests 未跟踪/ignored 检查通过。仅追加本合并记录；设备验证风险与下一步保持上述结论。
