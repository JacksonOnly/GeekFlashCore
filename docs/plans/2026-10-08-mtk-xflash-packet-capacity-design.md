# XFlash 2MiB 能力与窗口化接收

2026-10-08 / PKT-CAP-01，起点 `5e3be28`。用户232837日志和抓包369.1.0确认GetPacketLength返回8字节，写/读均0x00200000，全部ACK零。Penumbra xflash/protocol.rs get_packet_length按LE32写/读顺序存储，无1MiB拒绝。现项目误用旧1MiB上限拒绝有效能力；不是USB错误或设备状态0报错。

## 范围与边界

仅MTK XFlash能力/接收与诊断，不改exp算法、其他协议、标准ACK次序、取消/失效策略。不能只把校验放宽而保留无法接收2MiB的整帧路径，也不能将2MiB设备能力当作宿主写块/分配大小。

- 命名常量MaximumXFlashPacketLength=2MiB为本次有硬件证据的设备能力硬上限；写/读仍要求至少512，超过硬限或异常长度拒绝，不猜/重试。
- 新MaximumXFlashDataFrameSize默认2MiB，可显式限制512～2MiB；只影响XFlash存储数据FLOW。现有MaximumFrameSize继续用于小帧、控制结果、认证、消息和宿主scoped API，仍1MiB默认/硬限；XML/Legacy/BROM限制保持。写块继续min(设备能力,BufferSize)；数据接收限为min(设备读能力,新显式数据帧限)。改变的是XFlash数据FLOW的独立默认上限，不将报告解释为协商请求。
- MtkWire提取共同的有限FLOW头/MESSAGE读取；原ReadFrame小帧行为不变。新增窗口化数据帧接收：长度先校验设备限/剩余请求，池化BufferSize缓冲循环，不按完整帧或镜像分配；一帧全部写入sink后才发送一个ACK/消费一个状态。payload所有窗口共享一次ReadTimeout及总预算，取消/坏帧/EOF/输出失败立即失效，不ACK部分帧。
- 包长结果长度/设备写读值/有效宿主限在校验前后Debug可见；保留非法能力的既有MtkProtocolException类型（本地校验失败的status0不是设备NAK），Debug明确结果长度/能力是否有效。scoped ReceiveData及其公开ReadPacketLength仍受MaximumFrameSize约束。不记录载荷。

## 测试/交付

ignored先复现2MiB报告连接失败，再验证同步异步/含Unsupported SLA、DA1→DA2刷新、写块仍65536、完整2MiB数据帧跨窗口读取及一次ACK、named/raw共用、显式较小限/剩余请求/设备限/硬限、边界数值/坏帧/短数据/超时/取消/输出失败、MESSAGE有界和旧小帧1MiB限制。模拟2MiB/更大源验证池化读窗口不增长；原MTK/CLI回归、15既有失败对照、Release/Debug构建、资源/完整diff/ignored，独立提交及实施记录。用户下一次实机可证明存储查询，大型读取仍需额外证据。
