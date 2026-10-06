# MTK 通用离线架构分析实施记录

日期：2026-10-06。任务：ANALYSIS-01。方案：[设计](2026-10-06-mtk-architecture-analysis-design.md)。

## 启动与来源

主工作区 main / c4471cd，启动时干净；基线五工程487/96/457/9/55，共1104通过。用户此次授权加入通用分析器，仍保持无具体EXP。

参考 `D:/Code/Rust/penumbra-main/core/src/utils/analysis`，无Git元数据。SHA-256：

| 文件 | SHA-256 |
| --- | --- |
| mod.rs | DA1C04E1B58D645673C24196FC74584C895CC22DF7B65BEE1EC485C54DD7C902 |
| arm.rs | 9171F705D7674E77CD22432B3007E72AAF276AA58840A2431237CE846860FBC1 |
| aarch64.rs | C848E9340082AD099106C8CB38CED3B626DCD871B5FA3BEEC6358BCDB1D0739B |
| thumb.rs | 717BA89647B861762926A0E4EE92655CDFB42D7DACC213E7FC1C1B15ED6A5872 |

## 方法映射

| Penumbra 类型 / 方法 | C# 对应 |
| --- | --- |
| Arch / is_arm64 | Arch / ArchExtensions.IsArm64 |
| ArchAnalyzer trait | IArchAnalyzer；内置类型共享 ArchAnalyzer 抽象基类 |
| Analyzer enum | Analyzer 显式选择/包装，Implementation 可查询；重复包装展平 |
| ArmAnalyzer / Aarch64Analyzer / Thumb2Analyzer | 同名类型，各自借用 IDataSource 与显式基址 |
| data / arch / len / is_empty | Data / Architecture / Length / IsEmpty |
| read_u32 / va_to_off / off_to_va | ReadUInt32 / VirtualAddressToOffset / OffsetToVirtualAddress |
| fn_from_str / fn_from_off / str_xref | FindFunctionFromString / FindFunctionFromOffset / FindStringReference |
| find_call_arg_from_string / reg_value | FindCallArgumentFromString / RegisterValue |
| bl_target / b_target / bl_target_off | BranchLinkTarget / BranchTarget / BranchLinkTargetOffset |
| next_bl_from_off / next_b_from_off | NextBranchLinkFromOffset / NextBranchFromOffset |
| decode_movw / decode_movt / decode_sub_reg / is_bx_lr | ARM/Thumb静态 DecodeMovw / DecodeMovt / DecodeSubRegister / IsReturn |
| decode_adrp / decode_add_imm / is_pointer_auth | AArch64静态 DecodeAdrp / DecodeAddImmediate / IsPointerAuthentication |

## 进度与风险

- ANALYSIS-01A：先新增 ignored ArchitectureAnalysisTests；因缺少 Analysis 命名空间/Arch/API，首次执行出现 CS0234/CS0246。之后实现公共类型、共享读取器和三种架构；首次完整合成矩阵13通过/2失败，两项均为测试向量问题（Thumb状态位清除后地址仍在范围内、MOVT立即数位置），核对后修正向量。
- ANALYSIS-01B：扩大范围至缓存跨页、区域窗口、截断/对齐、ARM减法/字面量、AArch64有符号ADRP/shift ADD、Thumb混合边界/字面量/负宽分支、未知写入/分支屏障、UTF-8/NUL优先、源失效/非可定位与释放。32MiB零源扫描分配小于1MiB、单次读取请求不超过65536字节；每次查询独立流，不物化整个源。
- ANALYSIS-01C：审查后先加入两项回归，均复现失败：包装对象未检查旧源长度；Thumb不应因为分支目标地址不可表示而跳过分支屏障。修复后目标26通过；包装保留捕获长度且展平，屏障根据编码识别。
- ANALYSIS-01D：本地 LLVM clang 汇编三组纯合成向量、llvm-objdump核验；ARM CALL 重定位通过离线链接消解后确认 EB000002 与地址0x1020，不执行ELF。核对MOVW/MOVT、SUB、ADRP/ADD、B/BL、Thumb宽分支及混合指令。0xD50303BF在本地LLVM显示MSR，不纳入PACIASP/PACIBSP识别；这不是设备认证判断。

边界差异：B/BL严格区分；支持偏移0的前导与最后完整指令；拒绝地址溢出、空/非法UTF-8、超限参数；统一RegisterValue语义为查询点之前的值；Thumb回溯只看过去、保持指令边界，支持16位B；ARM字面量也检查池中地址，SUB使用32位算术。公共源和资源仍由宿主持有，构造/地址换算不打开流。缓存源要求稳定内容，长度校验不能检测同长度的调用方内容突变。

## 最终验证与交付

| 命令 / 检查 | 结果 |
| --- | --- |
| MTK Release/no-restore，ArchitectureAnalysisTests定向 | 最终26通过；两个审查回归先2失败，修复后包含于通过结果 |
| `dotnet test .tests/GeekFlashCore.Protocol.Mtk.Tests/GeekFlashCore.Protocol.Mtk.Tests.csproj -c Release --no-restore --verbosity quiet` | 513通过，0失败/跳过 |
| CLI / Qcom / Core / Android.Lp，相同Release/no-restore | 96 / 457 / 9 / 55通过；五工程总计1130，0失败/跳过 |
| `dotnet build GeekFlashCore.slnx -c Release --no-restore --verbosity quiet` | 0警告/0错误 |
| clang三架构合成汇编、llvm-objdump、ARM离线链接 | 编码/分支位移核对完成；仅编译/反汇编，没有运行文件 |
| 32MiB零源扫描、跨65536边界读取和DA窗口 | 分配小于1MiB、读取请求最多65536字节、查询流释放；不是USB性能证据 |
| `git diff --check`、完整差异、资源/日志/ignored审查 | 通过；无新NuGet、无日志/用户资源键、无设备线路；.tests、汇编/对象/ELF和bin/obj保持ignored |

生产范围为 Analysis 九个源文件及 AGENTS/README/NOTICE/框架/设计/方法映射与进度文档。没有更改 csproj、已有资源目录、策略类或协议。独立提交使用 `feat(mtk): add bounded offline architecture analyzers`，提交号通过 git log 查询；本地测试和汇编夹具不提交，没有推送或发布。

不自动调用分析器，不改四个 Execute、默认依赖、已有资源映射或认证线路；没有设备执行或真实DA兼容性证据。结果是受支持编码的局部启发式，不等于完整反汇编、CFG、真实函数签名或执行目标验证；Thumb全局边界扫描从宿主给定的源起点开始。源所有权属于宿主，必须在查询期间保持内容稳定；同长度内容变化不可由长度校验发现。恢复时先读本记录、设计和工作区状态；新增具体目标或设备执行需要独立需求，不由通用分析结果推导授权。
