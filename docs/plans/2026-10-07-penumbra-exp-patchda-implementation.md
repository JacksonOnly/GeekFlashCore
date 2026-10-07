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

- 无真机验证：LineCode 的 BE/LE 字节解释按参考逐字节复刻，但与 mtkclient BE 惯例冲突，需硬件
  证据裁决；Carbonara XFlash boot_to 后不读中间状态（参考如此），与本项目标准 DA2 上传线路不同。
- HeapBait 的雪橇在参考中为约 50MiB 连续分配；本实现按 3MiB 有界窗口流式发送，设备端行为是否
  等价未验证。失败后的容忍性读可能造成 XML 流失步。
- 保护特征/补丁规则只证明静态移植，不构成漏洞适用性证明；Completed 不等于设备接受补丁。
- acon 数据来源为 main 快照（无版本号），后续更新需重新核对。

