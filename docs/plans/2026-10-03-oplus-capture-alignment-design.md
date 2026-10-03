# Oplus 按文件 3 抓包对齐

日期：2026-10-03。用户指定附件文件 3（bus.txt 片段）为正常流程，要求声明名使用 chimerais。起始提交 1657b9d，工作区干净。

## 证据与目标

- 正常抓包：Sahara Done → 启动 VIP 等表提示 → 8144 字节 Digest → ACK false → `<verify EnableVip="0"/>` → handler/ACK true → 4096 字节 Sign → verify passed/ACK false → sha256init → 完整 `Failed to run the last command -1` 日志（无 ACK）→ 约 1.5 秒后 Configure UFS → ACK。
- 当前失败抓包：Digest 已 ACK，但发送了 verify value=ping EnableVip=1；设备 Hash 校验错误后重新宣布等待表，没有 ACK，CLI 最后显示 10 秒超时。原始 Hash/Sign/Digest 不复制到生产代码或文档。
- 本次用户失败日志明确使用 Legacy。Legacy bootstrap 使用正常抓包的字段与空白，唯一声明名称替换为 `chimerais="power" ?>`。同步/异步一致；保留 Legacy 运行期 Rector 换表行为和声明空白。
- Legacy 初始 Configure 默认 UFS，保持自动回退；发送抓包的五个属性顺序和默认值（含 SkipWrite=0、MemoryName=ufs、无多余默认属性）。显式非默认配置和协商 Payload 上限仍保留。普通 Configure 不变。
- Pt 保持既有 GeekFlashTool 参考实现的 verify value=ping EnableVip=1、分区映射及严格 sha256init ACK，文件 3 不能证明 Pt Loader 支持 Legacy 的例外，不将该线路更改扩大到 Pt。

## 状态、边界与兼容例外

- 资源仍由公共门面异步获取；线路保持同步、串行。Digest/Verify/Sign 仍要求完整 ACK，Sign 必须 verify passed，不放宽认证。
- Legacy sha256init 独立接收窗口最多 1500 ms（同时服从更短 read timeout）；只有完整 ACK false 或精确已知错误日志 `Failed to run the last command -1` 且没有其他错误/ACK/NAK/RAW/半帧时允许继续。纯静默、未知错误、NAK、RAW、部分 XML 和取消失败并失效。兼容路径记录 Warning，不称为 ACK 成功。
- Verify 后设备重新等待表的完整提示立即失败，不用 NOP/reset 探测，不自动重发 Digest；已有完整 NAK 的一次手动换 Sign 逻辑保留。
- 完整设备日志按帧即时发布，超时不丢已接收的诊断；spaced HEX 与原始 Hash 均脱敏，明确的 Hash mismatch 状态保留，日志不重复。
- Sign 4096 字节和 Digest 精确长度保持；串口抓包中的零长 USB 包属于驱动传输边界，不虚构新的协议包计数或发送镜像大小缓冲。

## 文件与验证

QcomProtocol.Oplus、Firehose executor/session/ConfigureState/negotiator、response parser、reader/receiver/文本脱敏、双语资源、CLI 文档和本设计/实施记录。测试留 ignored .tests。

测试先行：黄金 verify/sha/configure XML、Pt/Legacy 同步异步、sha 已知 log-only/未知/静默/NAK/RAW/半帧、Verify 表重启、认证严格、日志在超时前可见且字节串脱敏、显式配置保留、自动 UFS 回退与非 Oplus 不变；完整 Release 测试/构建/diff/资源/忽略检查，英文提交。

风险：抓包来源设备序列号不同，只证明该 Loader 路径；声明更名遵从用户要求，但 Digest 对字节及镜像组合的要求仍需用户实测。本轮不操作硬件，不修改用户 Digest/Sign 文件。
