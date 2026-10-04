# Legacy 读取与会话表边界修复

日期：2026-10-03；分支 codex/qcom-legacy-audit，起点 6ffd138，工作区干净。

用户新日志证明 GPT 多 LUN 分区读取成功，随后 read persist 失败。Bus Hound 完整 XML 为：Hash of new table doesn't match the expected hash 183、Packets received 53、Tables received 0、NAK rawmode=false，随后 Failed to read XML command -1、VIP 等待签名表。附件仅包含尾部响应，不能据此确认最后 OUT 的全部内容。cust 在这次展示的分区列表中不存在，其名称查找错误独立于表耗尽。

现状：Legacy 计数覆盖所有 XML 与完整写入 payload，但 CheckDigest 仅由 ProgramCommand 调用；读/GPT/一般 XML 会耗尽表。旧测试 ReadingNeverAutomaticallyRefreshesDigest 明确固化该限制。

参考边界：Rector firehose.cpp::check_digest 在剩余 <=2 时补 NOP 到 max_count+1，再送 Digest；独立 ACK 后计数归零并确认 NOP，无独立 ACK 时必须关联 NOP handler+ACK 后计数为 1。底层 send_xml 总有该检查，但 Session::read 创建 Firehose 时 enable_digest 默认 false。不能宣称参考公开读入口自动换表。本任务以用户实机表耗尽证据修复本项目 Legacy 会话的已认证读取，将参考算法应用到认证完成后的会话 XML；保留参考算法、计数、确认、Program 单次签名重放，不扩展普通/Pt/VIP。

实现：StorageService 安装 Legacy policy 时立即关联会话计数和内部前置检查；FirehoseSession 在 Execute/ExecuteXml 的同一串行操作租约内调用前置检查，由内部 executor 执行补位/换表，避免嵌套获取会话租约或递归检查。初始化 Digest/Verify/Sign/sha 的专用入口不经过该检查；首次 Configure 仍保持原启动顺序。后续普通 XML、NOP、storageinfo、Configure、GPT 和读取分段均在表边界前检查。没有改公共 API 或增加异步 I/O。

失败恢复：前置确认失败、NAK、半帧、超时、资源失效或取消不得恢复为 Started/Configured；会话保持 Faulted，不能发送主命令。一般读取 NAK 不盲目重放；只有原 Program 签名 NAK 有一次已有补表重放。文件保持流式、调用方资源所有权不变；取消与固定接收窗口保留。新增 Debug 包计数/边界日志，只含计数及容量；新表 Hash mismatch 输出固定脱敏状态，不输出 Hash/Digest。

测试先行：读取从 51/52/53/54 起点的 NOP/Digest 顺序与计数、跨调用/跨分段读取、通用 XML 和存储查询、取消/确认失败禁止主命令且失效、触发 NAK/日志确认及普通读取 NAK 不重试、Program 原回归。测试保持 ignored .tests。运行目标与完整 Release 回归、解决方案 Release 构建、双语资源/help/diff/ignored 检查并独立提交。

风险：本轮不连接硬件。Digest 复用与更长连续读写在此 Loader 上仍需用户复测。同步 I/O 被阻塞期间的取消延迟仍服从传输读超时。
