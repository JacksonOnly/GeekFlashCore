# 固件流式解包实施进度

日期：2026-10-07。设计：`2026-10-07-firmware-streaming-design.md`。

## 开始状态

- 基线 d331d0d，工作区干净；当前 worktree 缺少 ignored 本地测试，需恢复测试源到本工作区再验证。
- Qcom 现有 `ExecuteRawProgram`、`ExecutePatchFile` 已接受 IDataSource 与 imageResolver，无需改变协议线路。
- 参考项目提供 10 类格式（含 OFP 两个平台与 OZIP 两种布局），部分边界需重写；不迁移 NativeAOT 导出、Sdk ABI 与下载封装。
- 用户已授权自主选择架构；按独立 Firmware 项目实施设计。

## 进度

- FW-01（2026-10-07）：完成独立 net8.0 `GeekFlashCore.Firmware`，`FirmwareUnpacker/OpenOptions/Package/Entry` 公共契约。条目实现现有 IDataSource，独立只读可定位流；输入源借用、打开的流归调用方、包失效检查、双取消 token、固定源长度快照、密钥/池化缓冲清零。原始加密流按块直接定位，压缩流重开并有界跳过，没有临时解包目录或镜像缓存。
- FW-02（2026-10-07）：完成 ZIP、OZIP 两布局、OFP Qualcomm/MTK、OPS 三 MBox、PAC、KDZ/DZ、UPDATE.APP、完整 Android payload v2。OFP/OPS 生成只含标准属性的虚拟 rawprogram/patch；payload 覆盖全部目标 extent，按 operation 顺序映射、不物化 operation、拒绝增量/旧镜像操作与不完整/重叠布局。
- FW-02 边界证据：先写测试，再实现格式解析；OPS 最后不足 16 字节的参考反馈按 4 字节物理补齐修复。最终审查新增失败测试复现重复 OFP/OPS 节点丢命令、ZIP 小目录预算误计尾部扫描缓冲和危险目录路径，修复后通过；密码标志在第三方 codec 打开前拒绝。包级/条目级取消传递到 ZIP 内层源流。
- FW-02 依赖证据：参考 SharpCompress 0.42.1 有已公告漏洞；0.48.0 与 CLI 已有 EROFS 的 0.50.4 存在运行期版本冲突，最终统一 0.50.4，并直接固定 ZstdSharp.Port 0.8.8。XZ 在 decoder 分配前校验 LZMA2 字典属性，Zstandard 限制最大窗口；ZIP 只启用 Stored/Deflate/BZip2。
- FW-03（2026-10-07）：CLI 新增离线 list/extract（显式新文件、不覆盖），rawprogram/patch 支持 `package::entry.xml` 及包内通配。脚本目录和歧义预检在批次发送前完成，镜像通过包内相对路径解析；核心 Qcom ExecuteRawProgram/ExecutePatchFile、同步 Raw/Sparse、ACK/NAK 与会话失效线路未改。保留原帮助语法片段和单参数内部展开入口以通过现有 CLI 回归。
- FW-03 模拟传输证据：合成 OFP 明文 512 字节直接进入 Qcom program，再执行 patch；NAK 后会话失效且不执行 patch；缺图预检不发送设备命令。未连接真实设备。
- FW-04（2026-10-07）：目标测试、完整回归、Release 构建、离线命令、资源对应与忽略状态审查完成；使用文档 `docs/firmware.md` 和 README 已同步。框架提交 `9b97279 feat(firmware): add bounded streaming unpackers`；CLI 与交付文档使用独立提交 `feat(cli): stream package scripts into qcom`，最终编号见本分支 Git 历史。

## 验证证据

各测试项目均位于 ignored `.tests`，先从主工作目录恢复已有测试源，再在本 worktree 重新 restore/build；没有提交测试项目或夹具。

| 命令（均为 `-c Release --no-restore`） | 结果 |
| --- | --- |
| `dotnet test .tests/GeekFlashCore.Firmware.Tests/GeekFlashCore.Firmware.Tests.csproj` | 58 通过，0 失败、0 跳过 |
| `dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj` | 457 通过 |
| `dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj` | 102 通过（含本轮 6 项） |
| `dotnet test .tests/GeekFlashCore.Core.Tests/GeekFlashCore.Core.Tests.csproj` | 9 通过 |
| `dotnet test .tests/GeekFlashCore.Android.Lp.Tests/GeekFlashCore.Android.Lp.Tests.csproj` | 55 通过 |
| `dotnet test .tests/GeekFlashCore.Protocol.Mtk.Tests/GeekFlashCore.Protocol.Mtk.Tests.csproj` | 513 通过 |
| `dotnet build GeekFlashCore.slnx` | 0 警告、0 错误 |

- 合计 1194 项。Firmware 覆盖短读、双流/重开/seek、源释放、取消、目录/元数据/窗口预算、路径/重复项、安全 XML、各格式字节、全 extent/操作、增量拒绝与溢出。5 GiB ZERO 镜像目录及窗口读取的线程分配量小于 1 MiB；没有按镜像尺寸增长的物化。
- 真实 PAC 只读：`D:\ROM\iPlay40(T1020S)_酷比魔方OS_20220424-固件及教程\线刷固件\T1020S-ALLDOCUBEOS-20220424.pac`，源 4524902486 字节，目录 40 项；`super.img` 4299161600 字节。独立读取原 PAC 目录中的物理 offset，并核对条目起点、`int.MaxValue + 17`、最后 64 字节三窗口，全部一致。目录及窗口打开/读取的线程分配量小于 2 MiB；未导出该真实固件。
- CLI 实际离线 `firmware list/extract` 对 ignored 合成 ZIP 成功，导出 28 字节与原输入一致；`help firmware` 输出具体语法。所有日志及 smoke 输出留 ignored 路径。
- 中英文 Firmware 13 键、CLI 256 键完全对应；不新增敏感日志。`git diff --check` 通过，`git ls-files .tests` 为空，构建 bin/obj 与测试目录继续 ignored。
- 首次 `--no-restore` 解决方案构建因新 worktree 缺少 MessagePipe assets 失败；完成 `dotnet restore GeekFlashCore.slnx` 后构建通过。此问题为本地恢复条件，不是生产源码缺陷。

## 未决风险

- 有真实 PAC 目录/大偏移读取证据；OFP/OZIP/OPS/KDZ/DZ/UPDATE.APP/payload 的兼容证据仍为合成夹具及参考算法，参考测试程序列出的这些真实包本机不存在。下一步优先用脱敏真实 OFP 验证虚拟脚本、密钥变体、sparse 镜像，再验证 Qcom 硬件线路。
- 压缩条目的后向定位会重解压；第三方 decoder 有受限窗口开销，ZIP 多次重开有目录重读成本。大型真实 payload 的吞吐与峰值内存仍需测量。
- 不实现增量 payload、DZ 自动分区合并、Packer、签名/镜像 hash 信任判断；这些是显式边界，解包成功不等于刷写认证通过。Packer 后续可在 Firmware 项目增加独立契约与实现，不改变本轮只读条目契约。

## 追加：FWSP 分片 Super（2026-10-07）

- 用户追加 PEHM00 真实目录与 ZIP。已补齐 Sparse 多输入叠加、OFP NVList 虚拟 super.img、已解包目录及 CLI 嵌套容器。原始 rawprogram0.xml 不改，虚拟输出保持 Sparse；不展开 Raw、不合并落盘。CFB 固定窗口与 ZIP 原生 Deflate 修复了巨型嵌套源的小读回退和重放开销。
- 真实目录 1327 个窗口独立比对通过，ZIP/OFP 的原始脚本和首 Raw 窗口只读通过；上述历史 OFP 缺少真实包证据的风险已由这一指定样本部分补充，其他厂商变体与设备线路仍待验证。
- 最新六套合计 1227 项通过，Release 构建 0 警告/0 错误。恢复工作改为读取 `2026-10-07-ofp-sparse-super-design.md`、`2026-10-07-ofp-sparse-super-implementation.md` 和 `docs/ofp-sparse-super.md`，历史数字按本记录保留。
