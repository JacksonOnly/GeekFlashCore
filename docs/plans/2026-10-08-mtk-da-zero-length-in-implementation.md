# DA 零字节 USB IN 实施记录

2026-10-08 / DA-ZLP-01，起点 `e7cc62f`。[设计](2026-10-08-mtk-da-zero-length-in-design.md)。用户233705日志确认UFS StorageReady，首区8192字节已经完成；抓包463.1.0为宿主数据ACK之后的成功零字节IN。此时尚未得到最终状态，不代表设备拒绝区域，也不能宣称分区枚举成功。

## 行为与来源

- 共享LibUsbTransport已区分native错误与成功transferLength=0。仅MTK MtkWire.Read在DA1/DA2、transport仍open时继续成功零包；每次逻辑读最多4个连续零包，正片段重置计数。原ReadTimeout和总操作预算不重置，native前后检查取消/超时。没有重发命令、参数或ACK。
- 第5个连续零包、关闭连接、负/超界计数、native异常仍失败并失效。BROM/Preloader接入和ReadStartupPacket的零读边界未变；协议FLOW长度0仍拒绝。扩展raw USB的行为未改变，没有新增策略/载荷。
- Debug新增中英资源DaZeroLengthIn，只含阶段/命令/计数/剩余预算；没有设备内容或认证材料。
- Penumbra `core/src/port/backend/libusb_backend.rs` 的read_exact遇成功n==0继续；本实现不复制其无界循环。SHA256 `8CEE4F8785B1181B7948F5F1F932193C42BCE068CE464B5D076939ECAE860692`。XFlash upload_data仍要求完整帧→宿主ACK→状态，不能通过删除状态等待掩盖零包。

## 测试证据

ignored DaZeroLengthInTests最初8192夹具误用4096协商读上限，在到达ZLP前已拒绝；修正夹具为8192后，暂时禁用新分支，确认2个零包读取用例失败/1个startup边界通过，恢复实现后通过。最终20项覆盖单/4零包、5个拒绝、DA1 setup状态、DA2数据/状态头体、正片段重置、后续查询对齐、取消、读/操作超时、native IO/关闭/非法计数、NAK及空FLOW拒绝。失败路径不重发ACK，不把USB0当协议成功。

| 检查 | 结果 |
| --- | --- |
| 目标 | 20/20 |
| 可运行MTK | 724/724 |
| 完整MTK TRX da-zero-length-in-full.trx | 739项，724通过/15既有失败，名称与包长基线相同 |
| CLI Release | 168/168 |
| solution Release / CLI Debug | 0警告/0错误 |
| 中英资源 / diff / ignored | 94键及占位参数对应；diff通过；测试/日志/产物未跟踪 |

旧ignored Carbonara夹具CS0649警告未改；15项为14个旧Linecode夹具及缺失oppo DA，不将其标为本次通过。没有本次实机分区完成证据。

## 用户追加区域审查

用户指出BOOT1/2不是User；源码核对确认FlashParams传入region.WireId无偏移，UFS User=3、eMMC User=8。LoadPartitionsCore先扫描启动区GPT的行为与Penumbra不同，属于另外的不必要探测。随后单独修正枚举：启动区从容量直接生成Preloader条目、不读GPT；不取消正常User GPT的校验或ACK。零包可能同样出现在User读取，因此本修复仍需要保留。

提交查询：git log --oneline --grep='bounded DA zero-length'。
