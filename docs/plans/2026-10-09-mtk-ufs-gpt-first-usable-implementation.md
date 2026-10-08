# UFS 4K GPT 边界兼容实施记录

2026-10-09 / UFS-GPT-01，起点 `04c2739`。[设计](2026-10-09-mtk-ufs-gpt-first-usable-design.md)。用户000226日志和a33ef517抓包：Boot已跳过、User3四次ReadData均状态0，进入主备GPT解析失败。

## 事实与修复

离线解析完整438/455/472/489四个IN载荷，没有字节缺口；独立Python zlib确认两Header与两16384字节条目数组CRC有效。主备Current/Alternate对应1/31240191，User127959826432字节/4096块；FirstUsable=34、LastUsable=31240183，主EntriesLba2/备31240184，128×128条目。首条目8～1031在物理数组末端6之后，却小于声明34。没有其他越界条目或非法UTF16证据。失败来自通用GptParser的严格FirstUsable检查，不是USB、Boot/User选择、坏CRC或设备NAK。

源码 `penumbra-main/core/src/storage/gpt.rs` from_bytes验证CRC/数组范围，不执行同一FirstUsable条目检查；SHA256 `5EB27FBA5E32684FFB40C2A9FD29CECA01028D277235F5F986B68380559C2B53`。34与512字节数组布局起点相同是源码/数值推断，不推断设备固件为何保留该值。

- 只在UFS User、4096块、128项/128字节、声明34、主数组LBA2或已验证备份数组紧随LastUsable的已观察布局，宿主紧凑解析副本使用物理元数据末端6作下界。
- 原始Header/数组CRC、位置、容量、数组范围必须先通过；原Header CRC字段恢复后才复制，紧凑副本清零/重算其CRC。通用GptParser仍Strict、无AllowUnpatched，元数据覆盖/尾越界/反向/重叠/空或重复ID/坏名称仍失败。eMMC及非匹配布局不放宽。没有设备GPT改写、命令重试、exp或共享GPT修改。
- 完全验证通过后才采用实际最早分区作为PGPT上界（抓包8×4096=32768），避免旧34×4096覆盖首分区。原正常表仍使用原FirstUsable；备份保留范围维持真实LastUsable边界。
- Debug新增主备几何/CRC布尔值及有界枚举失败阶段，不输出CRC数值、GUID、载荷或原始异常message。Warning摘要说明兼容与无设备改写；CLI结构化命令当前会压制诊断输出，日志文件有完整证据，警告可见性另行处理。

生产文件仅MTK MtkProtocol.Partitions和两语言资源，本设计/记录/诊断说明。未改XFlash参数、公共API、其他协议、策略/载荷。

## 测试与验证

先写3项复现（合成主/备、真实抓包），均失败；初版主表/抓包夹具只供两次应答，主表被拒后后续备份读取超时；备份合成用例直接复现GPT copies。随后补齐主备应答并验证真实原表交给未修改共享解析器仍严格拒绝。最终24项全部通过，真实主表及损坏主Header后的真实备份回放均成功，PGPT32KiB不覆盖分区。

包括原始Header/Entry CRC坏、元数据/尾/反向/重叠/身份/名称坏、不匹配FirstUsable/介质/数组布局拒绝、正常布局保持、共享解析器Strict、NAK/取消不变、主备恢复与无敏感日志。捕获数据和Python解析器仅ignored .tests/tmp，无材料提交。

| 检查 | 结果 |
| --- | --- |
| UFS目标 / 联合旧50ms夹具隔离检查 | 24/24；25/25 |
| 最终完整MTK TRX ufs-gpt-first-usable-final.trx | 807项，792通过/15旧失败，名称与Boot基线相同 |
| 可运行MTK最终复跑 | 792/792 |
| CLI Release | 168/168 |
| solution Release | 0警告/0错误 |
| CLI Debug | 首3目标版本0警告/0错误；最终默认输出被用户运行geekflash进程7028锁定，独立ignored OutDir构建0警告/0错误 |
| 中英资源 / diff / ignored | 102键/格式参数对应，diff通过，测试/材料/日志/产物未跟踪 |

第一次20目标时完整803项出现既有50ms Thread.Sleep握手调度失败（787通过/16失败），该项隔离通过、最终807全量通过。随后可运行筛选又出现同一旧50ms调度失败（791/1），不修改该夹具或BROM生产路径。14个旧Linecode与缺oppo DA仍是15项固定基线；旧ignored Carbonara字段CS0649未改，生产构建零警告。

## 交付与风险

兼容修复首3目标版本Debug CLI已更新，最终版复制遇用户进程锁定，不杀进程或中断设备操作；待用户操作结束exit后原dotnet run重建。离线抓包回放不等于新版本实机完整枚举/读写成功，需重连运行partitions all，核对PGPT32KiB与普通分区边界；未知布局保持失败，不能强制兼容或关闭CRC。会话失效后旧视图不可用，不自动重放设备操作。

工作过程中新增用户侧未跟踪src/GeekFlashCore.CLI/persist.img，来源未判断，保持原样，不读取/提交/删除。其他tracked修改仅本任务。提交查询：git log --oneline --grep='handle legacy UFS GPT lower bounds'。
