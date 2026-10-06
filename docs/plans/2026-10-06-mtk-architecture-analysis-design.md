# MTK 通用离线架构分析设计

日期：2026-10-06。任务：ANALYSIS-01。基线 main / c4471cd，工作区干净。用户要求加入 Penumbra `utils::analysis::{Aarch64Analyzer, Analyzer, Arch, ArchAnalyzer, ArmAnalyzer}`。

## 目标与边界

移植 `core/src/utils/analysis/{mod,arm,aarch64,thumb}.rs` 的通用离线能力，包括同模块 Thumb2：显式架构选择、源长度/空值、LE读取、VA/偏移换算、B/BL目标与扫描、调用方字符串引用、常见函数前导及有限寄存器回溯。保留原作者 Shomy（2025–2026；thumb为2026）、AGPL-3.0-or-later 和参考文件指纹。源码快照没有 Git 信息。

用户此次要求扩展早期“仅宿主分析接口”的范围，允许上述通用工具的真实实现；不加入 MTK 专用特征、已知设备符号、堆/DPC定位、漏洞参数、载荷填充、DA补丁或任何设备执行。已有四 Execute、连接顺序、默认依赖、DAA校验和资源仓库不变。通用启发式结果不能作为执行地址、安全状态或认证成功的证据。

## 契约、资源与实现

公共命名空间 `GeekFlashCore.Protocol.Mtk.Analysis`：Arch、IArchAnalyzer（Rust trait 的 C# 契约）、ArchAnalyzer（共享抽象基类）、ArmAnalyzer、Aarch64Analyzer、Thumb2Analyzer、Analyzer（统一包装/工厂）。各分析器借用显式 IDataSource 和基址；公开方法使用 PascalCase，返回 nullable 表达无匹配。不推断架构，不自动适配为 IMtkExploitDaAnalyzer。可直接分析 MtkExploitDaData 提供的区域窗口。

源须稳定、可定位、可重开，长度限制256MiB；不物化整个源。每次有I/O的查询独立打开流，使用固定池化缓存，finally释放流/清零并归还缓存；源所有权保持调用方。实例无共享流或可变扫描状态。同步查询支持取消，异步资源和协议gate不变。地址范围、加减法、指令对齐、尾部短指令、寄存器、UTF-8模式（最多4096字节）、回溯（最多4096条）和递归预算均有界。

不照搬参考的明显边界问题：B/BL区分、偏移0函数、截断指令、空字符串、地址溢出；Thumb回溯保持16/32位边界且不向查询点之后读取MOVT。寄存器回溯是局部启发式，支持源模块的常量构造/复制/字面量和ARM减法子集，遇到已识别的分支屏障或不支持的目标寄存器写入返回null，不作完整控制流或全指令模拟保证。

## 步骤、文件与验证

1. ignored `.tests/GeekFlashCore.Protocol.Mtk.Tests/ArchitectureAnalysisTests.cs` 先定义API与合成指令向量，记录首次失败。
2. 实现 Analysis 下通用类型/读取器及三架构，覆盖地址、正负分支、字符串引用、前导、复制/减法/字面量、截断/取消/资源失败、Thumb边界与有界内存；补充映射文档/README/NOTICE/AGENTS/进度。
3. 本机 LLVM clang 只汇编无设备/漏洞的合成向量，用 llvm-objdump 核对编码；它不是运行依赖。不引入第三方NuGet。
4. 目标与全量 MTK/CLI/Qcom/Core/Android.Lp Release/no-restore测试、Release解决方案构建、diff --check、日志/资源/ignored审查；独立英文提交，不提交.tests，不推送。

既有线路测试验证连接与占位类无变化。测试属于合成离线证据；没有真机、真实DA定位或漏洞成功验证。详细映射、边界差异和命令结果写入对应实施记录。
