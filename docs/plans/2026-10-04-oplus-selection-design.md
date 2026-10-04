# Oplus 启动后选择与联机耗时

日期：2026-10-04；任务：OP-UX-02。依据用户最新要求实施，替代 BOOT-04 的连接前模式菜单。

## 行为与边界

普通设备不再询问连接模式。上传 Loader、被动读取启动日志后，仅在 VIP 启用、Sahara OEM 属于 Oplus、解析的 Loader 属于 Oplus/OnePlus 三项同时成立时，向宿主请求选择 Pt/Legacy。未知身份与已经运行的 Loader 不猜测厂商；显式模式、普通 Digest、VIP、非交互选项继续优先。Pt 和 Legacy 均要求外部 Digest 与 Sign，停用 Oplus 内置 Sign 调用；保留 Xiaomi 内置认证。

保持 `Digest → verify → Sign → sha256init → configure → getstorageinfo`，选择发生在第一条 Firehose 命令之前。Legacy 的 XML 声明、容量 53、256 扇区分段、换表以及 Hash 诊断伴随 ACK 后继续传输完全保留。启动超时不向新 Loader 发送试探 NOP。

## 契约、状态与资源

增加默认关闭的宿主选择选项，复用 IOplusDigestProvider；请求 Mode=None 表示启动证据已满足，可由响应的可选 SelectedMode 指定 Pt/Legacy。不修改构造函数与 Provider 方法签名，不暴露第三方类型。活动模式保存在会话内，连接失败/断开后重置为配置快照；显式模式不允许资源响应覆盖。

同步和异步编排共用证据判断、模式激活与校验；同步资源调用保持现有“不等待未完成异步请求”的限制。Sign 仍验证 1–4096 字节、发送时补齐并清零；Digest 流式读取，失败重试有界；交互等待由资源预算与取消控制。

## 小米日志证据与优化

样本 geekflash-20261004-205730 日志：前置菜单约 2772 ms，Loader 路径输入约 16547 ms，上传约 134 ms，启动日志约 487 ms；内置认证实际约 2 ms。启动已报告 `ufs:`，但先尝试 eMMC，getstorageinfo NAK 后再配置 UFS，带来两条多余命令。

仅在存储配置为自动且启动存在明确 `ufs:` 前缀证据时，使用既有 UFS 配置策略；保留显式 eMMC 与无证据回退。认证增加尝试结果和单调时钟耗时的文件诊断，不缩短设备 I/O 窗口，不记录签名、Challenge 或认证载荷。不宣称该样本授权算法缓慢。

## 范围、验证与交付

范围：Qcom 配置/资源模型、连接与 Oplus 编排、Xiaomi 认证阶段日志、CLI Provider/注册清理、中英文资源、CLI 说明与进度文档。

先写本地测试：证据组合和无提前提问，Pt/Legacy 选定后线路与声明，Sign 必填与重选、取消、会话重置、同步/异步一致，UFS 自动与显式配置，Legacy Hash+ACK 兼容。运行目标和完整 Qcom 测试、Release 解决方案构建、资源键与 diff 检查；提交代码后按此前授权本地快进 main，再构建现成 CLI。不提交 .tests、日志和构建输出。

风险：厂商身份来自 Sahara OEM 与固定版本 Loader 解析器，无法解析时需显式模式；本轮不执行真实设备写入，晚选择和 UFS 首次配置需硬件复测。日志仅证明样本耗时，不能代表其他小米 Loader。

## 追加要求：重启后退出

用户追加：reboot 成功后直接退出程序。交互命令循环在成功的 reboot（以及同一重启线路的 power 别名）后返回，随后由既有 using/await using 释放会话和传输；不调用 Environment.Exit。失败、取消、参数错误继续沿用现有处理，不把失败当成功退出。抽出命令读取循环以模拟 IProtocol 验证三种重启模式均不再读取后续命令，失败与非法参数仍可处理后续命令。Pt 非交互缺少 Digest/Sign 同时在设备发现前拒绝，以免上传后才发现资源不足。
