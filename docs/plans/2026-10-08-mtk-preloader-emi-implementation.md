# MTK Preloader / EMI 修复实施记录

日期：2026-10-08。任务 PL-HS-01 / EMI-XF-01；设计见 [设计](2026-10-08-mtk-preloader-emi-design.md)。起点 `23cb56f`，开始工作区干净。用户新增的标准 EMI 抓包只在 ignored 测试中提取，不提交私有 ROM、日志、抓包或测试工程。

## EMI-XF-01：完整 XFlash BLOADER 窗口

- 原解析器无条件选择 MTK_BIN+12，Ares EMI v51 丢失112字节头，用户 `222420` 日志发送336字节后返回 C0070005。抓包 InitExtRam 长度0x1C0、FLOW448字节，从 MTK_BLOADER_INFO_v51 开始，最后组状态0；未要求额外校验帧或逐帧ACK。
- 增添可选借用 `MtkEmiImage.BloaderInfoSource`，保留旧构造/Source/Version/Legacy窗口。Parser 生成两个有界 MtkDataWindow，同一原源借用，不物化整个 preloader；MTK_BIN 搜索限制在已确认 INFO/version 后。XFlash 选完整窗口，原宿主只提供 Source 时原样使用。核心按实际DA方言在DA上传前验证选用资源，流仅释放自己打开的实例。
- EMI Debug 只记录格式/版本/字节长度，UI既有摘要显示448。Legacy/XML、exp、通用USB/Qcom/SPRD未改。错误状态仍立即失效，不豁免 C0070005、不重发 InitExtRam。
- 实际 preloader：452044字节，SHA256 `7C09C51C6FFC7FB7386E7B08E2DA91FC3D543501055E1D73473A92EB9BB028F4`。离线解析得到完整448/Legacy336/v51；完整448字节与用户837.1抓包逐字节相等，抓包提取窗口SHA256 `3242FECE81061260153A9FABD04A4327C03C3C60F59C8AFEFE5788DFBCD3074C`。这证明源窗口与正确抓包一致，不是修复版实机成功证据。

### 测试与验证

ignored `EmiWindowTests` 六项先5失败/1通过，修复后6全部通过。覆盖MMM签名排除、0x800零尾、完整/Legacy窗口、448长度与FLOW/组ACK次序、实际文件与抓包相等、原宿主材料兼容、选用完整源无效时DA上传前拒绝。

| 命令/检查 | EMI阶段结果 |
| --- | --- |
| EMI + parser旧用例 + TransferDiagnostics 目标测试 | 33通过 |
| MTK 排除既有14项Linecode与缺oppo DA夹具 | 603/603通过（实施Preloader前） |
| CLI全量Release | 168/168通过 |
| solution Release --no-restore | 0警告/0错误 |
| MTK中英资源 | 76键对应；新增模板参数对应 |
| git diff --check / 完整diff | 通过 |

测试编译唯一警告为既有 ignored exp 夹具CS0649；未改该文件。所有测试与原始材料 ignored，不进入提交。

EMI阶段提交：`f7a1069 fix(mtk): retain complete XFlash EMI headers`。

## PL-HS-01：标准 Preloader 握手

- 仅已支持的0E8D:2000/6000候选先发一次A0唤醒，再发送正常四字节序列。前缀默认64、显式范围0～1024；READY重复、分片或与5F同包都按字节解释。首应答A0 echo在前缀后也被识别为候选；必须完整非零FD确认。
- Preloader 使用固定1024字节栈接收缓存，从首握手至FD共享ReadTimeout与操作预算，所有线上/缓存读取和下一次写入之前检查截止和取消。最多消费一次额外唤醒5F（第二步前）或A0 echo（FD前），不重发后续命令、不重新握手、不无界drain。FD后仍有未消费数据立即失效，保留已识别标志，阻止CLI自动重试已经完整识别的设备。
- BROM/未知/DA候选不额外唤醒、不使用包缓存；首读仍1字节、FD半字仍2字节，保留既有一次初始单字节零传输IN停滞恢复。新增接收函数只用于启动，DA各方言线路不变；未改公共USB及恢复资格。Preloader 1024字节读不满足旧单字节ClearHalt资格，失败沿已授权的释放候选/重新枚举，而非扩展共享后端恢复范围。
- UI记录候选VID/PID/data/CDC接口和唤醒；Debug记录CDC设置、启动IN实际长度/预算、前缀计数、握手应答、去重和FD证据。不记录serial/topology/path、原始包或材料。PID不决定最终阶段，仍由FE确认。
- 核心串行gate、流所有权、FD后WDT/security/认证次序及失败不重试保持。使用1024固定栈空间，不增加按镜像大小增长的物化。

### 测试与最终回归

ignored `PreloaderHandshakeTests` 最初12项原实现11失败/1通过，最终27项全部通过。包含唤醒无应答/双应答/已握手、READY同包/分片/重复/超限、FD整包/部分/零编号/剩余字节、取消/IO错误/迟到返回、PID与FE不一致、BROM原1/2字节native请求形状、候选限制、选项边界、CDC选择及中英脱敏日志。

| 命令/检查 | 最终结果 |
| --- | --- |
| Preloader目标测试 | 27通过 |
| MTK全量Release --no-build --no-restore，TRX仅ignored | 645项：630通过/15既有失败/0跳过 |
| MTK排除既有Linecode14项与缺oppo DA夹具 | 630/630通过 |
| CLI全量Release | 168/168通过 |
| solution Release / CLI Debug --no-restore | 均0警告/0错误 |
| MTK中英资源键与完整格式参数 | 84对应 |
| 完整diff、git diff --check、ignored检查 | 通过；测试/材料/日志/bin/obj均不跟踪 |

原15项失败名称与上一阶段/23cb56f基线一致，未修改exp或原失败断言。可运行回归含既有5项固定缓存/64MiB内存路径。此前50ms计时夹具的调度风险保持，本次通过。参考快照SHA256：Penumbra preloader/protocol `E9B6D0744458AEC96B84E1320AAE1820BBD272C3B894A72D27ED7C562DFFF5D9`、libusb_backend `8CEE4F8785B1181B7948F5F1F932193C42BCE068CE464B5D076939ECAE860692`、mtkclient daconfig `4BFA6ACDA16466FFDD92E8CBE9B1E703D9B6DAF1075D0DA332952C5287EFF73D`。前两者无Git revision可证，指纹只识别本地已读取源码。

## 用户后续实机证据与边界

用户`225138`新日志：候选0E8D:0003，data1/CDC0；BROM完整识别、WDT/DA1成功；448字节EMI已被设备接受，外部RAM初始化完成。这是EMI修复的实机证据，不是Preloader2000/6000线路证据。

随后DA1Ready宿主回调发送BootTo及32字节材料，返回Completed；标准DA2的BootTo初始ACK超时，尚未发送实际DA2。用户新抓包378～389确认同一顺序。目前只定位回调之后通道不再回应，不能将其称为DA2流式载荷/吞吐失败。该路径落在原先排除的exp，已向用户询问是否授权审查回调衔接；本提交不修改它。等待范围确认时保留现有错误/会话失效，不增加睡眠、重发或状态豁免。

## 未决风险与恢复

Preloader唤醒、双应答形态及固定缓存有模拟/参考证据，仍需2000/6000实机补证。第一份Pipe日志缺候选元数据，驱动/CDC/端点原因未知，不声称根治全部Pipe。完整FD后失败不重连重试，native控制/传输阻塞仍不能立即强制取消。下一步针对用户最新DA1Ready之后的BootTo超时，先确认范围，再以标准线路/回调证据分开验证；不直接用延长超时解释或跳过ACK。
