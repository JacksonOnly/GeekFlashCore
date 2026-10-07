# Penumbra EXP 与 PatchDA 移植实施记录

日期：2026-10-07。任务：EXP-PORT-01。设计见 [设计文档](2026-10-07-penumbra-exp-patchda-design.md)。

## 参考指纹（分析时读取的 penumbra-main 文件 SHA256）

| 文件 | SHA256 |
| --- | --- |
| core/src/exploit/mod.rs | 93D446586F5B16538FBD8B54200796536419F1F64486AE28C0A3372AE65A7F00 |
| core/src/exploit/linecode.rs | D7D1C8E03A2C4B571BCAE0F90F957AE5F93AB924E249A07D2F8EED796052F575 |
| core/src/exploit/carbonara.rs | AD43347F54887F83E00EDEAF607620EBAEA5AEE9377D5AC28098D3E8794D0B00 |
| core/src/exploit/heapbait.rs | 1CDD6D76688241BFD49F2A3A88506ECCE2A4B9446A14DDD422FB017CE1701C7B |
| core/src/da/xflash/patch.rs | DAD3D43BD6665364D4B3E235EEB8B0BB7AA5C9B10FA010EA8AD486C9F08D272C |
| core/src/da/xml/patch.rs | 213929A356DEA309191F4568F6EF2E817A41EA6DD6C502731355694BF3E82227 |
| core/src/utils/patching.rs | F8279F5C43E454DF90CDC63FBFEF733A65509A0665AC482043AED4B797355F62 |
| core/src/utils/hash.rs | A045EF9BC4EA66B655818689A21CD8C874F58F3EEB451195BFB3FFE62A7B9A6F |
| core/src/preloader/protocol.rs | E9B6D0744458AEC96B84E1320AAE1820BBD272C3B894A72D27ED7C562DFFF5D9 |
| core/src/preloader/cmd.rs | 81A93F3E39B01ABF807EE6382AAEF76A306E09111C640C84A11359CEEB8BE0DF |
| core/src/preloader/macros.rs | CE92A9939F06F98F86E9F58122CE5C7EF1C58916A91D9DBD682F861E8E991486 |
| core/src/da/xflash/cmd.rs | 117DE0CDB72310761D062567ACE3A88E2480029D27A0500938F48B159AFC4F8B |
| core/src/da/xflash/protocol.rs | CB17E5926F4D0483DC7DABA44792AAD23BD73DA6C02A316783EE518E3AC84E23 |
| core/src/da/xml/protocol.rs | B9ED36BD0F956A26DB249EA19587A66457C77F3B98D50CE28FC0D1F1CE9F5408 |

## 进度

### EXP-PORT-01A（2026-10-07）：通用离线工具与 Unfused

- 新增 `Exploits/Penumbra/`：PenumbraPatching（find_pattern/patch/patch_u32/get_diff/get_diff_align）、
  PenumbraPayloadFormat（PENUMBRAV6P 容器）、PenumbraDaMetadata（V5/V6 哈希槽、摘要类型、架构检测）、
  PenumbraLinecodeTable（PENUMBRALC 表）、PenumbraStrategySupport（区域副本/overlay 组装）。
- `MtkDaOverlaySource`：借用基源 + 非重叠覆盖窗口的流式 IDataSource，替换 DA 不物化整个容器。
- `MtkChipCatalog` 重写为 acon 风格分组表：Models（74 条目，含 acon 新增的 MT6835/6858/6878/6879/
  6899/6989/6989/6991/6993/6995/8188）+ Watchdogs 分组（mtkclient 数据逐芯片核对一致）+ Uarts 分组
  （acon uart0 数据）。`MtkChipDescriptor` 新增可空 `Uart0`。
- Unfused 策略真实化：三安全标志全关 → 双区补丁 → overlay 替换；缺资源/安全开启 → NotApplicable。
- 测试 16（工具）+20（表/格式/元数据）+3（Unfused）全部通过；嵌入 brom_defuse.bin 实表 34 条目验证。
- 验证：MTK 533 通过（旧表数据一致性校验通过测试回归实现）、Release 构建 0 警告。
- 提交 `610c234 feat(mtk): port Penumbra offline patch tools and unfused strategy`。

### EXP-PORT-01B（2026-10-07）：LineCode / Carbonara / HeapBait 真实化

- `PenumbraLinecodeTrigger`：PlProtocol sys_region_access（0xDA echo、BE 参数、BE 状态）与
  USB 控制传输序列（0x21/0x20、0x80/0x06、0xA1/0x21）逐字节复刻；payload 写入 cert_addr 后
  把地址推入 ptr_usbdl 发送缓冲。LineCode 策略：BROM 阶段 + 表匹配 + 控制传输探测，前置缺失
  NotApplicable；设备 I/O 后失败沿框架失效会话。
- `PenumbraDaChannelSupport`：Carbonara 摘要投递——XFlash 原始 boot_to 线路（0x010008 + 16 字节
  参数 + 摘要 + SyncSignal 状态）与 XML BOOT-TO 生命周期。
- `PenumbraHeapBaitRunner`：AIO1 雪橇 + hakujoudai（流式分块，单块 3MiB 有界）、AIO2 5000 字节
  堆塑形、0x1400 溢出帧、EXP-PATCH-MEM 差异补丁。`MtkXmlCodec` 允许列表新增 EXP-PATCH-MEM /
  EXP-CALL-FUNC；SECURITY-SET-ALLINONE-SIGNATURE 的 source_file 上限放宽到 8192（5000 字节必需）。
- `IMtkDaChannel.AcknowledgeXml(long)` 新增带值 ACK；XmlSession/Channel 实现。
- Carbonara 策略：保护特征（mtkclient 四组）→ DA2 补丁 → 摘要槽刷新 → 补丁 DA2 替换。
- HeapBait 策略：DA2 特征 → 芯片 Uart0 → hakujoudai 流程 → 差异补丁到运行中 DA2，不替换 DA。
- 测试：Linecode 7 + Carbonara 3 + HeapBait 4 + 旧占位契约测试按新语义更新（secured target、
  安全标志前置、未知芯片、缺资源、缺通道路径）。MTK 550、CLI 96、Qcom 457、Core 9、Android.Lp 55
  全部通过；Release 构建 0 警告 0 错误；git diff --check 通过。
- 提交 `1f1480b feat(mtk): port Linecode, Carbonara and HeapBait strategies`。

### EXP-PORT-01C（2026-10-07）：CLI 接线与文档

- CLI `MtkProtocolHostAdapter` 四个策略携带嵌入 payload 仓库依赖（`MtkExploitDependencies` +
  `FromEmbeddedResources()`）。
- NOTICE-MTK.md、licenses/MTK-PAYLOAD-NOTICES.txt 补充 EXP/PatchDA 移植的作者归属（Shomy、
  R0rt1z2、Chimera、kamakiri/chaosmaster/xyzz、mtkclient 字符串来源）与偏离说明。
- 全量回归后提交。

### EXP-PORT-01D（2026-10-07）：真机裁决与 Kamakiri2 变体切换

- 真机（MT6893 Dimensity 1200，hw 0x0950，SBC/SLA/DAA 全开）：Penumbra linecode 变体
  （0xA1/0x21 7 字节、SET 12 字节、GET_DESCRIPTOR 0x0200）在第一次 sys_region_access（读
  ptr_usbdl，offset 0xE75C）收到 BROM 状态 0x1D1A（BE 读法；mtkclient LE 0x1A1D，即
  "Kamakiri2 执行漏洞失败, 缓存问题"）——缓存指针 corrupt 未生效。echo/Read32/控制传输全部完成
  （第二次运行全程 16ms），排除传输层与移植偏差。
- penumbra 原版实测同样失败：antumbra 2.0.0（本地构建，nightly + VS2022 完整工具链）输出
  "Device is vulnerable to Linecode! Exploiting..." 后无 "Linecode done!"，`exploit!` 宏吞掉
  trigger 错误后继续 DA1 上传，最终报 SLA challenge not completed（0x1d0d）。证明 C# 移植忠实、
  失败源于变体本身而非移植 bug。
- 裁决：MT6893 需要 mtkclient kamakiri2 变体——取线编码 0xA1/0x25 8 字节 + 终止零（9 字节）、
  SET_LINE_CODING 13 字节（指针落在偏移 9，比 Penumbra 变体晚 1 字节）、GET_DESCRIPTOR 0x02FF、
  先做 brom_register_access(0,1) 缓存探针。用户自研 GeekFlashTool（基于 mtkclient）对本机
  ChipConfig[0x950] 明确使用 KAMAKIRI2，与裁决一致。
- 修改：`PenumbraLinecodeTrigger.ControlSequence` 改 13 字节 SET + 0x02FF；`Linecode()` 前置容错
  缓存探针（读 1 字节 @ 0，失败忽略且不吞取消）；策略取线编码改 0xA1/0x25 8 字节；异常消息携带
  状态码；trigger 条目/指针/访问 Debug 日志。
- 测试更新（linecode 8 个）：13 字节 SET、指针在 w[9..]、0x02FF 断言、探针 wire 字节。MTK 553
  全通过、CLI Debug 构建 0 警告 0 错误、git diff --check 通过。
- 真机复验（第二轮）：0xA1/0x25 取线编码请求被 MT6893 BROM 直接 STALL（LibUsbDotNet
  UsbException "Input/Output Error"），触发前即失败。对照 mtkclient kamakiri2.py：其 0x25
  首次尝试失败被 except 吞掉，`exploit()` 主路径回退 `ctrl_transfer(0xA1, 0x21, 0, 0, 7)` 取 7
  字节线编码 + 终止零（8 字节）。据此修正为单变量实验：取码回退 0xA1/0x21（8 字节）、SET 回退
  12 字节（指针在偏移 8）、GET_DESCRIPTOR 保持 0x02FF——即 Penumbra 变体唯一改 0x0200→0x02FF；
  缓存探针同步移除（mtkclient 中 try/except 说明非必需）。
- 待验证：真机重跑；若仍 0x1D1A 则逐一回退探针/变体差异（0x02FF 索引、13 字节长度）。

### EXP-PORT-01E（2026-10-07）：与 mtkclient 逐项对照后的线编码修复

- 现象：真机（MT6893，hw 0x0950，SBC/SLA/DAA 全开）在 `Run` 的第一次 `Linecode`
  访问（读 ptr_usbdl）失败，异常 `linecode status 0x1D1A`。堆栈定位：`Linecode` 内
  部 try 块中的一字节缓存 prime 已被拒绝（吞掉），控制序列后真正的区域读再被
  BROM 以同一状态拒绝，说明 usbdl 指针没有被修复。
- 逐项对照三个参考实现（mtkclient `Library/Exploit/kamakiri2.py`、
  GeekFlashTool `Exploit/Kamakiri2*.cs`、penumbra `core/src/exploit/linecode.rs`）：
  - 三者都把**设备线编码读回值**当作 SET_LINE_CODING 的前缀。GeekFlashTool 用
    `Serial.ControlTransfer(0xA1, 0x25, …)` 的 1 ms 读回结果（其 `UsbDevice.ControlTransfer`
    经 `PinnedHandle` 把控制传输的 IN 数据写回同一 byte[]），mtkclient 用
    `0xA1/0x21` 的 7 字节 + 终止零，penumbra 用 `ctrl_in(0xA1, 0x21, 0, 0, 7)` 后
    `push(0)`。**上一版 C# 丢弃读回值、改用 8 个零字节，与三者都不一致。**
  - BROM 的 SET_LINE_CODING 处理器用该缓存记账，第二个字节是缓冲区内索引；索引被
    置零后改写的是无关字，usbdl 指针保持损坏 → 后续 `sys_region_access` 返回
    0x1A1D（mtkclient 的 “Kamakiri2 failed, cache issue”）。
  - 载荷长度：mtkclient 是 13 字节（7 字节线编码 + 终止零 + 4 字节地址，指针落在
    偏移 9），GeekFlashTool 是 12 字节（8 字节缓存 + 指针落在偏移 8）。两者都在
    0x950 上可用，说明 BROM 使用的是 8 字节缓存，指针应落在偏移 8；本实现保持 12
    字节，只把前缀换成真实线编码。
  - 状态字：`brom_register_access` 与 GeekFlashTool 都以本机序（小端）解析 2 字节
    状态；上一版按大端解析，0x1A1D 被显示成 0x1D1A。已改为小端，异常消息与参考一致。
  - 多余的 `0xA1/0x25`：mtkclient 主路径不发该请求（MT6893 直接 STALL，第二轮真机
    日志已证实），已从 `ControlSequence` 移除，只保留 0xA1/0x21 取码。
- 修改：`PenumbraLinecodeTrigger.ControlSequence` 去掉 0xA1/0x25、状态字改小端；
  `LineCodeExploitStrategy` 把 `0xA1/0x21` 读到的 7 字节 + 终止零交给触发序列。
- 测试更新（linecode 17 个）：新增 `ControlSequenceCarriesDeviceLineCodingBytes`
  （断言设备线编码原样上线、指针在偏移 8、载荷 12 字节）、`DoesNotContain(0xA1/0x25)`、
  探针测试改为小端状态。MTK 556 全通过，CLI 106 / Qcom 506 / Core 38 / Android.Lp 63 /
  Firmware 111 全通过，Release 构建 0 警告 0 错误，`git diff --check` 通过。
- 真机复验脚本（ignored，不提交）：`.tests/tmp/mtk_linecode_ab.py`
  （`zeros` = 修复前行为，`device` = mtkclient 行为），每次运行需要重新插拔；脚本逐步
  打印控制传输字节、两种字节序的状态字与失败点。

### EXP-PORT-01F（2026-10-07）：取线编码顺序对齐 mtkclient（0xA1/0x25 优先）

- 真机复验（EXP-PORT-01E 之后，MT6893）：异常消息已是参考一致的小端 `0x1A1D`，失败点
  仍在 `Run` 的第一次 `Linecode` 区域读；日志新增的逐笔记录显示 prime/probe 已通过
  （`Linecode access: address=0x0000E79C` 之后直接失败，18 次控制传输等待 ≈750 ms），
  说明线编码前缀生效但 usbdl 指针仍未修复到可读状态。
- 用真实设备读到线编码：`ctrl_in(0xA1, 0x21, 0, 0, 7) -> 00 C2 01 00 00 00 08`（921600
  8N1），即 mtkclient/penumbra 形式的前缀为 `00 C2 01 00 00 00 08 00`。
- 重新逐行核对 mtkclient `Kamakiri2`：
  - `kamakiri2()`（内层，每轮都调用）**首选 `ctrl_transfer(0xA1, 0x25, 0, 0, 8)` 读 8 字节**
    再补终止零（共 9 字节），随后 `linecode + pack("<I", addr)` = **13 字节**
    （指针落在偏移 9），最后发 `0x80/0x06 0x02FF`。
  - `exploit()` 里的 `ctrl_transfer(0xA1, 0x21, 0, 0, 7) + [0]` 只是 0x25 失败时的回退。
  - `da_read_write` 在控制序列**之前**先 `brom_register_access(0,1)` + `read32(wdt+0x50)`，
    两者共用一个 try（本实现一致）。
  - 与移植相关的其余事实：`chipconfig[0x950].brom_register_access=(0xEBA4, 0xEC5C)`，
    `da_read_write` 取 `[0][1] = 0xEC5C`（与 penumbra CSV `0x950,,0x10007000,0xe79c,0xec5c,0x100A00`
    一致，本实现表值正确）；mtkclient 控制传输之间**没有任何主机侧延时**，本实现原先的
    50 ms 间隔是自行添加的（已改默认 0）。
- 修改：`LineCodeExploitStrategy` 新增 `TryReadLineCoding`，按 mtkclient 顺序先请求
  `0xA1/0x25` 8 字节，STALL 时回退 `0xA1/0x21` 7 字节，两者都追加终止零；两者都失败或
  传输不支持控制传输时保持 NotApplicable，不触碰设备状态。`PenumbraLinecodeTrigger`
  的控制传输间隔默认改为 0（与 mtkclient/penumbra 一致，保留可配置旋钮用于单变量实验）。
  触发路径补充逐笔 Debug 日志（prime/probe 结果、每轮 SET_LINE_CODING 字节、0x02FF
  是否 STALL、控制序列后的区域偏移）。
- 测试：新增 3 个策略级用例（0x25 成功时线编码上线、0x25 STALL 回退 0x21、两者都 STALL
  时 NotApplicable 且无任何写入），`UnknownHardwareCodeKeepsNotApplicable` 改为按探测到的
  目标硬件编号路由（原用例用 DA 镜像编号，与策略实际取值不符）。MTK 559 全通过、Release
  0 警告 0 错误。
- 待验证（下一次插拔）：`.tests/tmp/mtk_linecode_sweep.py` 一次插拔依次跑
  `ref`（mtkclient 原样：0x25 + 13 字节 + 0x02FF）/`ref21`/`nodesc`/`zeros`，逐步打印
  每一笔控制传输与状态字；同时保留 `mtk_linecode_ab.py` 作为 `ptr_da` 单变量脚本。

## 验证汇总（EXP-PORT-01B 时点）

| 验证命令 | 结果 |
| --- | --- |
| MTK Release tests | 550 通过，0 失败 |
| CLI Release tests | 96 通过，0 失败 |
| Qcom / Core / Android.Lp | 457 / 9 / 55 通过 |
| `dotnet build GeekFlashCore.slnx -c Release` | 0 警告 0 错误 |
| `git diff --check` | 通过 |
| .tests 跟踪检查 | 无测试/产物提交 |

## 未决风险

- LineCode 已获真机裁决：Penumbra 变体在 MT6893 上被 BROM 以 0x1A1D（缓存问题）拒绝，
  penumbra 原版同样失败；本实现已切换为 mtkclient kamakiri2 变体（EXP-PORT-01D），并在
  EXP-PORT-01E 修复线编码前缀与状态字字节序、在 EXP-PORT-01F 对齐取码顺序（0xA1/0x25
  优先、无主机侧间隔）。**用户确认 mtkclient 在本机 BROM 能成功**，因此该漏洞对本机适用，
  剩余失败必然是移植偏差，不是漏洞不适用。修复后的序列仍未在真机复验：预期 prime 可能被
  拒绝（参考同样吞掉），但控制序列后的区域读必须成功；若仍为 0x1A1D，用
  `.tests/tmp/mtk_linecode_sweep.py` 一次插拔跑 `ref`/`ref21`/`nodesc`/`zeros` 定位是
  13/12 字节长度、`0x02FF` 请求还是取码请求号的差异。Carbonara XFlash boot_to 后不读中间
  状态（参考如此），与本项目标准 DA2 上传线路不同。
- HeapBait 的雪橇在参考中为约 50MiB 连续分配；本实现按 3MiB 有界窗口流式发送，设备端行为是否
  等价未验证。失败后的容忍性读可能造成 XML 流失步。
- 保护特征/补丁规则只证明静态移植，不构成漏洞适用性证明；Completed 不等于设备接受补丁。
- acon 数据来源为 main 快照（无版本号），后续更新需重新核对。

