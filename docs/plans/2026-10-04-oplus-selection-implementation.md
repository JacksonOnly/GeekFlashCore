# OP-UX-02 实施进度

日期：2026-10-04。

- 起点：codex/qcom-legacy-audit 与本地 main 均为 83c620a，受跟踪工作区干净。
- 已确认：用户样本认证约 2 ms；前置模式与 Loader 输入占主要等待时间，启动 UFS 证据可以避免 eMMC 失败回退。
- 已实现：删除连接前菜单与注册回调。VIP + Sahara Oplus OEM + 已解析的 Oplus/OnePlus Programmer 同时满足时，通过资源 Provider 在启动后选择 Pt/Legacy；显式模式和其他 Digest/VIP 优先。活动配置会话隔离，断开/失败重置，资源不能覆盖显式模式。
- 已实现：Pt 和 Legacy 始终要求外部 Sign，内置 Oplus 查找调用已注释，Xiaomi 内置认证保持。非交互缺少任一资源在发现/上传前报参数错误；Sign 拒绝后仍只允许一次替换，不重复询问模式。Legacy Hash+ACK 继续传输线路未改。
- 已实现：自动存储从明确 `ufs:` 启动证据选择 UFS，保留显式存储和无证据回退。每次 Xiaomi 内置认证追加通过状态、尝试序号和耗时，含失败尝试，不记录签名。
- 追加要求：成功 reboot 的 system/download/poweroff 及同一线路 power 别名结束交互循环，由既有 Dispose 释放会话；失败和非法参数不会误退出。已用模拟 IProtocol 验证不读取成功重启后的下一条命令。
- 测试先行证据：停用内置 Sign 前，已知芯片 Pt 新期望有 2 项失败；追加脚本 Pt 必需资源/重启退出测试在实现前 6 项失败。修复后完整本地 Qcom/CLI 联合测试 259/259 通过（约 19 秒）。
- 验证：Release 解决方案构建 0 警告/0 错误；CLI help 显示 Pt/Legacy 均需 Sign；中英文资源键 CLI 137、Qcom 253 完全对应；git diff --check 通过，.tests 无被跟踪文件。
- 证据边界：晚选择测试在上传后的边界注入 Sahara/Loader 身份，验证同步与异步启动、线路顺序、签名替换、取消、无效模式、缺失身份、会话重置和 UFS 配置；真实 SM4350.elf 与 Xiaomi SM8250 Loader 仅只读解析，均成功识别 Programmer 和对应厂商，未与硬件通信。
- 待交付：整理提交、本地主分支快进与合并后测试/构建。风险：未知 OEM/无法解析的 Loader 需显式模式；晚选择及 UFS 首次配置仍需用户真机复测。
