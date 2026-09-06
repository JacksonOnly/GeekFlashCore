# CLI-14：存储信息查询时机

日期：2026-09-06。基线：1296102。用户要求仅在 configure 初始化时自动读取存储状态，分区表和 LUN 操作不再隐式调用 getstorageinfo。

## 设计

- 原因：Core ReadPartitions 对未缓存 LUN 调用 QueryStorageInfo；CLI 通用 sector 范围与 Firehose Patch/SHA256 范围校验也有查询回退。目前 configure 仅缓存 LUN 0。
- 保留 configure → LUN 0 存储探测及 eMMC/UFS 有界回退、厂商验证顺序；成功后在同一串行初始化操作内查询 num_physical 所报告的其余 LUN，各一次，再发布连接成功。LUN 数量按现有上限校验，异步入口每次查询前检查取消。
- 每个 LUN 使用自己报告的容量，不把 LUN 0 容量复制到其他 LUN。分区表读取和范围校验只消费缓存；缺失容量时继续使用现有日志/错误，不临时访问设备补查。
- 显式 GetStorageInfo/CLI getstorageinfo 继续表示主动刷新。重新 configure 创建新的 StorageService/StorageInfos；重连清理旧会话，不复用旧 LUN 描述或租约。
- 不新增公共 API、异步 I/O、资源或日志文本。其余 LUN 查询放在存储类型回退捕获范围外，避免把其 NAK 误判为 LUN 0 存储类型错误。

## 实施与验证

- [x] 先复现 configure 未缓存全部 LUN，定义后续分区/sector 命令不查询的线路测试。
- [x] 调整 Core configure 收集时机并删除 Core/CLI 隐式查询回退。
- [x] 验证同步/异步初始化、重新配置缓存重建、重复 GPT 读取、显式刷新、非法数量与失败路径。
- [x] 完整 Qcom/CLI 测试、Release 构建、diff 审查后提交。

## 进度

- 2026-09-06：测试先行复现 Configure 只缓存 LUN 0；同步和异步 Configure 现在在发布连接成功前读取 `num_physical` 报告的其余 LUN，并以每个响应的容量注册块设备。
- 2026-09-06：移除 `QcomProtocol.ReadPartitions`、CLI sector 范围校验和 SHA256/patch 路径的隐式 `GetStorageInfo` 回退；显式刷新仍通过 `GetStorageInfo` 保留。
- 2026-09-06：Qcom 224/224、CLI 47/47 通过；Release 构建 0 警告/0 错误，`git diff --check` 通过。`.tests` 保持 ignored，未连接真实设备。

风险：模拟传输与真实设备证据区分；初始化时会集中查询已报告的 LUN，设备 NAK/超时仍传播并阻止发布完整连接。实际设备的 LUN 信息和初始化耗时待实机复测。
