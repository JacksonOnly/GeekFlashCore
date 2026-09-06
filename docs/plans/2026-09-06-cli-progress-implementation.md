# CLI-13 实施记录

日期：2026-09-06。设计：2026-09-06-cli-progress-design.md。

- [x] 阅读进度链路并记录设计；现有进度只含 Total/Current/Label，写入使用分区容量上界。
- [x] Core 单位/阶段、准确总量与回归测试。
- [x] CLI 单调计时、容量/速度/时间显示和刷新优化。
- [x] 完整测试、构建和审查；按 Core/CLI 分别提交，保留未跟踪 cust.img。

## 行为结论

- ProgressRecord 保留三参数构造，默认 Steps/Running；读写与 Loader 上传明确发布 Bytes/Started/Completed。同步和异步读写门面共用适配器。
- Raw 写入总量来自已有计划，包含实际发送的尾扇区补齐；Sparse 汇总发送区域，不包括 DontCare。读取总量为请求长度。没有增加镜像物化、文件重读或异步协议操作。
- 读写完成事件在最终 ACK 后发布，NAK 或传输中取消没有完成事件。Started 回调取消发生在 Firehose I/O 前，不应使尚未触碰的会话失效；Raw 传输中取消仍使会话和旧租约失效。
- 显示当前量/总量、百分比、平均速度、已用时间、剩余时间，完成后保留总用时。平均速度采用本次传输累计字节/单调时钟耗时，包含 ACK 等待；时间超过 24 小时继续累计小时。
- 终端中间刷新间隔 100 ms，重定向间隔 1 s，开始/完成强制输出。节流后才格式化；窄终端省略或缩短条形部分，按字符显示宽度显式换行并清理全部进度行。换行使用栈缓冲，避免逐字符临时字符串。
- ConsoleUi 日志和进度共享锁，清行不重置计时；重定向输出无 ANSI。单位按 1024 换算，新增界面文本中英文资源键对应。

## 验证证据（2026-09-06）

- 测试先行：named Raw 总量断言复现旧的分区容量上界；ProgressDisplay 测试在实现前失败。新增固定时间源、Sparse 实际发送量、ACK/NAK 和取消边界测试。
- 原 RawCancellation 测试调整为 Current > 0 后取消，以继续触发真实 Raw 传输中的取消；另增读/写 Started 取消测试验证不发送数据且会话保持可用。
- `dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore -v quiet`：220/220 通过，包含已有 64 MiB 流式窗口回归。
- `dotnet test .tests/GeekFlashCore.CLI.Tests/GeekFlashCore.CLI.Tests.csproj -c Release --no-restore -v quiet`：46/46 通过，其中 11 项进度显示测试覆盖平均速度/耗时/ETA、开始重置、节流、最终统计、未知/零/超量、步骤单位、跨天耗时、重定向及中文窄行。
- `dotnet build GeekFlashCore.slnx -c Release --no-restore -v quiet`：0 警告、0 错误。
- `git diff --check`、CLI/Qcom 中英文资源键对比通过；完整生产代码 diff 已审查。测试目录、构建产物均未跟踪，cust.img 不纳入提交。

## 未决风险

- 本轮仅模拟传输与本地格式化验证，未连接设备或执行真实刷写；实际吞吐、终端缩放和特殊组合字符显示仍需实机复测。
- 速度为平均速度，ETA 为估算，末尾 ACK 等待可能延长总用时；不表示设备内部闪存实际落盘速度。
- 擦除只收到协议 ACK，不能得到逐字节擦除量，本轮不模拟擦除速度。Sahara 总量沿用已提供 Loader 长度，设备重复请求或只读取部分 Loader 时可能与实际累计发送量不同。
