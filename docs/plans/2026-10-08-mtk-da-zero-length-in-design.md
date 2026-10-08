# DA 成功零字节 IN 与 EOF 区分

2026-10-08 / DA-ZLP-01，起点 `e7cc62f`。用户233705日志/抓包463.1.0：UFS连接成功，首区8192字节读取完成、ACK发出后native成功返回0。共享LibUsbTransport.Read已校验native Error，再返回transferLength；真实Timeout/Pipe/NoDevice是异常，Error.None/0可为USB ZLP，不能当作流EOF。

Penumbra libusb_backend read_exact对n==0继续；xflash upload_data的send调用send_data并消费状态，当前逐帧ACK→状态顺序正确，不能删除ACK状态等待。本修复只MTK DA同步读取，不改共享LibUsb或其他协议。

实现：MtkWire.Read仅在Stage Da1/Da2且transport仍open时，允许成功0字节native IN继续同一逻辑读取。每次Read最多4个连续零包，正字节重置连续计数；共用原ReadTimeout/操作deadline，前后检查取消，不重发任何命令/ACK。超过限、负数/超界计数、关闭句柄或IO异常按原失败失效；BROM/Preloader初次接入零读及ReadStartupPacket保持原立即失败/释放候选边界。Debug记录ZLP计数/阶段/命令/剩余读预算，无原始数据。FLOW header长度0仍非法，USB0不构成协议成功。

测试先复现完整ReadData之后单ZLP误EOF；模拟DA1状态/DA2数据头/体/ACK片段中ZLP、4个边界/5个拒绝、cancel/late/native IO/坏状态、空FLOW非法、BROM保持、后续命令对齐、只发送一次ACK、预算不重置与会话失效。目标/可运行MTK/15基线对照、CLI/Release/Debug、资源/diff/ignored，独立提交及实施记录。这里只证明有界接收修复；下一次实机枚举/完整GPT有效性仍需确认。
