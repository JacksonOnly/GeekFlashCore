# Snapdragon 665 抓包资源与使用方式

日期：2026-10-05；任务：PBL-01。来源为用户提供的 `D:/Downloads/665.txt`，Bus Hound 7.04 文本，6298521 字节。本文只分析 665；710/845 保持参考项目原线路。

## 可复现提取

原始日志 SHA256：`43ac1610b5882ceec2f7054f9d586ca41f83229b46f112ffd1a8c93b9184da6b`。

提取工具按文本中的 `Cmd.Phase.Ofs(rep)`、IN/OUT、字节偏移校验，而不是搜索 ELF 魔数后拼接全部 OUT。只取第一份补丁 ELF 的六个请求/响应，后续 Loader 和 Firehose 数据不混入补丁。

```powershell
python tools/extract_sm6125_pbl.py 'D:/Downloads/665.txt' .tests/pbl-extracted
Get-FileHash .tests/pbl-extracted/sm6125_pbl_bootstrap.bin -Algorithm SHA256
```

产出 `.bin` 与包含覆盖范围、逐块校验值和重复计数的 `.json`。生产资源为 `src/GeekFlashCore.Protocol.Qcom/Internals/PblResources/sm6125_pbl_bootstrap.bin`，20480 字节，SHA256：`ec2f75c34a1f89a207a528c83aef5ac42873d94231cb5bf49b70c0b238ed1a55`。提取工具使用标准库，校验固定抓包布局、方向、报文、重复计数与最终资源字节；不同数据失败，不覆盖不同的已有输出。

## 重复记录

用户说明括号 `(xx)` 表示重复次数，未标记时计 1。下列每行是一组压缩显示，并非一次传输：

| OUT 命令 | 重复次数 |
| --- | ---: |
| 87 | 3 |
| 93 | 5 |
| 103 | 12 |
| 127 | 24 |
| 175 | 3 |
| 181 | 4 |
| 189 | 5 |
| 199 | 11 |
| 221 | 5 |
| 231 | 10 |
| 251 | 10 |
| 271 | 3 |

合计 **95 次**发送 `13 9A 9A 9A`，不是 12 次。运行代码以设备响应决定何时退出，最多 100 次，不把这份设备的 95 次固化成所有设备的次数。压缩记录保留组计数，不提供组内每次 IN/OUT 的独立时序；测试验证计数展开、载荷与阶段顺序，不宣称可以从压缩组恢复精确时间。

## 补丁阶段的六个资源区间

命令 276 是 `READ_DATA64`（命令 18、长度 32、image_id 13、offset 0、length 64），先于命令 277 的 HelloResponse。随后请求如下：

| 请求 IN / 数据 OUT | ELF 偏移 | 字节数 |
| --- | ---: | ---: |
| 276 / 278 | 0 | 64 |
| 279 / 280 | 64 | 896 |
| 281 / 282 | 4096 | 4096 |
| 284 / 285 | 8192 | 4096 |
| 287 / 288 | 12288 | 4096 |
| 290 / 291 | 16384 | 4096 |

实际写入合计 **17344 字节**。0..63 为 ELF64 头；64..959 为 16 个 56 字节 program headers；随后是四个 4 KiB 数据块。960..4095 没有请求和捕获，资源窗口中填零仅用于偏移定位，代码拒绝读取这些未捕获字节。

这不是完整原始 Loader：ELF 的其他声明范围超过本窗口，最后一个 program header 含异常大的内存长度。本实现把捕获的数据作为固定补丁材料，不对这些声明分配内存，也不将其作为正常 Loader 校验/上传。抓包能证明传输字节与阶段，无法单凭它证明每条机器指令的作用或所有品牌的实际 PBL 修补结果。

## 接入方式与后续 Loader

1. Sahara 身份已确认，用户 Loader 已获取并校验；仅 SoC 为 SDM845、SDM710、SM6125 时提示并自动执行，OEM/品牌不限。
2. 665 消费初始 Hello，发送四字节触发包并读取响应。Hello 则继续；合法首 READ_DATA64 则进入补丁阶段，发送抓包中的 HelloResponse。
3. 按上述六个精确请求发送补丁资源。身份、偏移、长度、顺序任一偏离都立即关闭；不使用用户 Loader 回答这些请求。
4. 最后一块之后，命令 293 是新的 Hello；294 是下一份 Loader 的首 READ_DATA64（0/64）；295 为 HelloResponse。代码消费 Hello 和首请求，发送 HelloResponse，再把首请求交还现有 Sahara 上传器。
5. 命令 296 已出现第二个 Loader 请求（64/952），随后 297/298 才发送 Loader 头和 program headers；已有输入缓冲保留这个请求，不能 Flush 或再次探测。补丁与用户 Loader 之间没有 Done、关闭或 1000 ms 重开。
6. 用户 Loader 后续读取、Done、Firehose 配置与存储初始化仍按既有流程，Patch 后请求完整位于镜像内，单次至多 4 MiB。

CLI 默认启用；Core 宿主通过 `QcomProtocolOptions.EnablePblPatch = true` 启用（默认 false）。同步与异步入口共用同步协议执行器。仅识别到目标芯片才自动提示；Patch 失败、取消、短读、超时或异常请求关闭当前会话，不盲目重试或发送普通 reset。

当前证据：用户抓包、参考源码、提取后字节比对和模拟传输。本实现尚未真机验证，尤其是身份探测后的 Command→ImageTxPending 适配，以及不同品牌/版本 PBL 的行为；支持条件扩展不等同所有设备已实测。
