# 固件分支主分支合并记录

- 日期：2026-10-07；任务：MERGE-FW-01；授权：用户明确要求“合并”。
- 目标：将 `codex/firmware-streaming-20261007` 的固件流式读取、OFP 分片 Sparse Super、散包 Super metadata 优先计划、ZIP/OFP 目录性能优化以及等待签名表的交互恢复并入本地 `main`。
- 合并前：main=`bd401b7`，功能分支=`7396a06`，共同基线=`d331d0d`；两工作区干净。main 有 3 项后续提交，功能分支有 12 项提交，因此使用保留历史的合并提交，不强制重置任何分支。
- `git merge --no-ff --no-commit codex/firmware-streaming-20261007` 在 `D:\Code\CSharp\GeekFlashCore` 自动合并成功，没有冲突；main 原有改动保留。
- 验证策略：在主工作区还原依赖并运行 Release 解决方案构建；补入当前工作树仅主工作区缺少的 25 个 ignored 测试文件，保留主工作区所有已有测试。六套测试覆盖 Qcom、CLI、Core/Sparse、LP、Firmware 和 MTK，真实固件测试仅做离线读取，不连接设备或输出镜像。
- 门禁：资源键/占位符、`git diff --check`、测试与产物 ignored 状态，确认 main 合并提交包含 `7396a06` 后交付；本轮不推送远端、不删除分支或工作树。
- 已验证：新鲜 `dotnet restore GeekFlashCore.slnx -v quiet` 通过；主工作区 Release 解决方案构建 0 警告 / 0 错误。Qcom 506、CLI 106、Core 38、LP 63、MTK 552、Firmware 111，共 1376 项通过。
- 六套主工作区测试使用 `dotnet test .tests/<project>/<project>.csproj -c Release --no-build --no-restore -v quiet`；测试程序集事先顺序 Release 构建，避免长时间真实包测试与构建复制相互争用。固件完整回归用时 5 分 36 秒，覆盖真实 PAC、PEHM00 目录与 ZIP/OFP、PGT110 的 Sparse/LP metadata 和单次前向读取；首次 metadata 回调与流式读取分配护栏通过，没有设备 I/O 或镜像输出。
- 资源验证：CLI/Qcom/Firmware/Sparse/LP 的 6 对资源文件键、非空值与格式占位符一致；工作区与暂存区差异检查通过，测试和日志保持 ignored。主目录 Release CLI 实际离线列出 PEHM00 目录 101 个条目，包括按需解析的虚拟 super.img，没有镜像输出。
- 构建说明：主工作区已有的 ignored MTK 测试文件报告一项未赋值字段 CS0649；生产解决方案构建无警告，没有改动该测试或相应生产代码。合并结果相对功能分支的 Firmware/Sparse/LP/Qcom 模块无差异，也没有覆盖 main 原有 MTK 修改。
- 合并交付：以上门禁通过后创建合并提交，父提交保留 main=`bd401b7` 与功能分支=`7396a06`，最终提交号以 Git 历史为准。主目录 `src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe` 已重建；主工作区与原功能工作树最终应干净。没有推送远端或删除工作树。
- 内存证据：测试运行到约 252 秒时进程工作集 127.5 MiB，包含测试框架，不能作为库的峰值分配或设备吞吐指标。下一步从主目录 CLI 开始用户真机复测；本轮没有硬件认证或刷写证据。
- 风险沿用功能记录：压缩 OFP 首次尾部读取仍需顺序解压；顺序 Super 写入不承诺全量预检/回滚；设备认证与实际刷写兼容性仍须用户真机验证。恢复入口为本记录及 `2026-10-07-firehose-signed-table-selection-implementation.md`、`2026-10-07-ofp-catalog-performance-implementation.md`、`2026-10-07-oplus-loose-super-implementation.md`。
