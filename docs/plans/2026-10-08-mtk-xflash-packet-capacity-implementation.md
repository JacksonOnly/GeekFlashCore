# XFlash 2MiB 能力和数据接收实施

2026-10-08 / PKT-CAP-01，起点 `5e3be28`。[设计](2026-10-08-mtk-xflash-packet-capacity-design.md)。用户232837日志/抓包确认有效8字节写读能力均为2MiB，旧1MiB判断误拒绝。

## 实现与证据

- Abstractions命名硬限MaximumXFlashPacketLength=2MiB及独立MaximumXFlashDataFrameSize默认2MiB、有效512～2MiB。XFlash能力校验接受已观察范围；BufferSize仍64KiB默认/1MiB硬限，小帧/消息/认证/EMI/scoped MaximumFrameSize仍1MiB，不扩大XML/Legacy/BROM默认限。
- 写块min(设备能力,BufferSize)，数据接收限min(设备读能力,独立数据帧限)。不是发送包长协商请求，显式较小数据限只拒绝较大响应，不重试。
- MtkWire公共内部FLOW头/MESSAGE解析保持旧小帧验证，新ReadStreamFrame按BufferSize池化窗口接收；校验设备/宿主/剩余请求长度后才写sink。单个payload各窗口共享ReadTimeout与总预算，取消/IO/输出错误不ACK部分帧。完整数据帧仍只发一次ACK并等一次状态。
- scoped ReceiveData与ReadPacketLength保持MaximumFrameSize上限，不向扩展ABI声称支持无法物化的大帧。包长响应长度/设备值/是否合法及有效宿主限制先后Debug记录，无载荷；保留非法能力的既有异常类型，status0为本地校验失败，不是设备NAK。

生产文件：MtkProtocolOptions、IMtkProtocol、XFlashSession、MtkWire、门面channel以及两语言资源。来源Penumbra xflash get_packet_length与read_data/send_data顺序；protocol指纹沿SLA记录。没有改策略/载荷/其他协议。

## 测试与验证

ignored XFlashPacketCapacityTests初始3/3失败；最终23/23。包含同步异步/Unsupported SLA之后2MiB能力连接、raw/named完整2MiB数据帧小窗口且一次ACK、读路径线程分配低于512KiB、能力/选项上下界与uint.MaxValue、设备/宿主/剩余请求上限拒绝、取消/输出/USB/迟到窗口无ACK、scoped1MiB限保持。

| 检查 | 结果 |
| --- | --- |
| 目标 | 23/23 |
| 可运行MTK全回归（最终复跑） | 704/704 |
| 完整TRX xflash-packet-capacity-full.trx | 719项，704通过/15既有失败，名称与上一SLA基线完全相同 |
| CLI Release | 168/168 |
| solution Release / CLI Debug | 0警告/0错误 |
| 中英资源 / diff / ignored | 93对应，diff通过；测试/材料/日志未跟踪 |

首次可运行回归仍出现既有50ms握手夹具调度失败；最终完整及可运行回归均通过，未改原夹具/握手生产代码。14Linecode及缺oppo DA基线保留，旧ignored字段CS0649仅测试警告。大型合成源只模拟线路；不等于实机大帧吞吐证明。

## 用户实机更新与后续

用户233705日志：包长刷新成功、UFS3区/4096块/用户容量127959826432、StorageReady成功，是2MiB能力/存储接入实机证据。partitions all已收到首区8192字节，ACK之后native返回0。新抓包463.1.0显示零字节IN，需单独审查USB ZLP不是EOF；本次不取消标准ACK状态等待。恢复从新DA零长度IN任务继续。大型2MiB读取/完整分区枚举仍需实机证据；源与native取消边界保持。

提交查询：git log --oneline --grep='XFlash packet ceilings'。
