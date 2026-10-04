# 实施进度

2026-10-04 VB-00：工作区干净，基线 cbafc5b；读取 Qualcomm 设计/进度及 CLI、LP、EROFS/Ext 契约，确认直接组合现有只读模块。设计见同日 vendor-browser-design。历史 .tests 未随 worktree 携带，准备本轮测试护栏。

2026-10-04 VB-01：DetectRuntimeVendor 在原 Firehose 特征之后使用已接受 Loader 和 Sahara OEM；共享 QcomLoaderInspector 的 OEM 映射。QcomProtocol 新增可选 IVendorSelectionProvider，在 Configure/Oplus/VIP 前经既有资源解析器调用；拒绝 Auto/无效枚举，重配置保留选择，断开/失效清除。旧构造函数与 DetectRuntimeVendor/Resolve 原方法签名保留，未接 Provider 的旧宿主继续 Generic。CLI 未知时输入厂商编号/名称，非交互要求 --vendor；无额外 XML 探测。目标护栏先复现旧 API 缺失，最终同步/异步先选后配、超时/迟到/取消/无效值零发送、显式覆盖和 Generic 重配置测试通过。

2026-10-04 VB-02：新增 BrowserSession/Node/Path/Commands，组合既有 GPT 字节切片、LP Metadata/extent、EROFS/Ext 驱动。设备 browse 和无需设备的 browse-image 支持 Super→system_a/vendor_a→目录；slot 默认 0、可显式选择。LP 元数据备份回退、slot 1、嵌套目录返回/绝对路径、LUN 同名显式选择和子分区保留原 LUN、按需挂载（列出 LP 不探测各文件系统）、退出子到父释放均有模拟/合成格式验证。没有改动格式驱动或设备线路。

2026-10-04 VB-03：shell 支持编号/路径、ls 分页、cd/up/pwd、read、find 通配文件名搜索与指定目录批量导出。禁止跟随符号链接/目录循环/特殊文件，搜索设 128 深度和百万节点上限，挂载节点设 128 上限。文件导出为同步来源读取、池化复制、异步本地输出及同目录原子替换；取消/短读不覆盖原输出，保护源镜像、输出相对目录、名称、链接路径和输出冲突。非交互脚本错误立即返回 1。所有新增固定文本中英资源化。实际 EXE 冒烟发现挂载分区大小误显示为根目录大小，新增测试先 RED 再修复为源镜像长度。

2026-10-04 VB-04 验证：本工作树本轮完整本地测试 53/53 通过（Release，约 0.5 秒），不是历史 197 项测试回归（历史 .tests 未随 worktree 携带）。Release 解决方案构建 0 警告/0 错误，git diff --check 通过，中英资源键集合一致，git ls-files .tests 无输出，夹具/测试/日志/bin/obj 均 ignored。实际 geekflash.exe 配合 --non-interactive/stdin 脚本完成 Super→Ext 文件读取→返回→EROFS 搜索导出，退出 0，两份输出均精确为 content；.tests/smoke 保存合成 raw 夹具供恢复验证。流式测量预热后 1 MiB/64 MiB Ext 稀疏文件导出托管分配约 138 KB，增量小于 8 MiB 护栏，64 MiB 约 30 ms（本地合成/磁盘缓存结果，不能代表 EDL 吞吐）。完整 diff 手工审查未发现敏感日志或按镜像大小物化。

验证命令：

```powershell
dotnet test .tests/GeekFlashCore.Protocol.Qcom.Tests/GeekFlashCore.Protocol.Qcom.Tests.csproj -c Release --no-restore -v quiet
dotnet build GeekFlashCore.slnx -c Release --no-restore -v quiet
git diff --check
git status --short --ignored
git ls-files .tests
```

代码提交：`67c0c9e feat(qcom): combine vendor hints and host selection`；`129ef0c feat(cli): browse nested LP filesystems and export searches`。收尾文档独立提交，工作区仅本轮文档待收尾；测试、合成夹具、测量结果和 EXE 日志均 ignored，无受跟踪测试。新版 EXE 位于 `src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe`。

未决风险与恢复位置：无本轮硬件证据，设备随机读性能、同步 I/O 取消延迟、实际 EROFS 压缩/Ext 方言与厂商回退均待验证；合成夹具覆盖 EROFS FlatPlain/Inline 和 Ext direct/holes，不能代表所有真实镜像。默认 LP slot 0，活动槽不自动推测。本地单文件入口不解析 Sparse、不接多设备 LP 外部源；均有明确使用范围。搜索一次性批量导出不是目录事务，已成功文件在后续失败时保留；输出目录链接的并发替换竞态无法由预检查完全排除。下一步在真实设备运行 browse super，进入目标槽位，先导出一个小文件/搜索小目录并核对数据和 EDL 延迟；入口见 docs/cli-qcom.md 与同日设计。

2026-10-04 FH-02 补充：用户 23:29 真机日志确认 FirehoseBlockDevice 拒绝 LP 魔数的 4 字节读取，原 MemoryDevice 夹具未覆盖该约束。已在核心块设备加入有界扇区适配；新增 512/4096 字节扇区真实 FirehoseBlockDevice 加模拟传输的嵌套导航/搜索/导出回归通过，当前完整本地测试 74/74，Release 构建与资源/diff 检查通过。新版本目录 CLI 已重建；修复后真机仍待复测。详细进度见 `2026-10-04-firehose-resume-byte-read-fixes.md`。
