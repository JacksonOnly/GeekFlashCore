# 厂商证据与只读资源浏览器设计

日期：2026-10-04；依据本轮用户要求实施。

## 目标与范围

- VB-01：优化 DetectRuntimeVendor，显式 VendorOverride 优先，Firehose 运行时证据其次，已接受 Loader 厂商、Sahara OEM 依次辅助；均未知时在 Configure/认证前通过可选宿主 Provider 选择。CLI 必须请求选择，非交互要求 --vendor；未接 Provider 的现有 Core 宿主保留 Generic 兼容默认。
- VB-02：CLI 只读挂载 LP、EROFS、Ext，支持设备 GPT 分区与本地 raw 镜像；Super → LP 逻辑分区 → 文件系统形成统一路径。保留已有 read/write 命令含义，新增 browse/browse-image 入口。
- VB-03：浏览 shell 列表、编号/路径进入、cd、up、pwd、read 导出、find 文件通配搜索并可批量导出到指定目录。目录不跟随符号链接，文件名与路径按文件系统大小写精确匹配。
- 非目标：可写挂载、操作系统挂载盘符、修改镜像、自动跟随符号链接、浏览 Sparse 容器、从文件中再挂载镜像。

## 已有实现和线路

VendorStrategyResolver 已识别启动日志并回退 Loader；QcomLoaderInspector 已有 OEM 映射，Sahara OEM 仅用于 CLI 展示。同步 ConfigureFirehose 与异步 ConnectAsync 共用 StartFirehose；选择放在门面资源边界，底层同步线路与 Oplus/VIP 初始化顺序不变，不增加探测 XML。

仓库已有 LpMetadataSet/Document、LP extent 块设备、IFileSystemDriver/Volume、EROFS/Ext 驱动、SliceBlockDevice 与 Qcom 会话代数块设备。直接组合这些实现，不复制格式解析。参考项目只用于检查既有 OEM 展示/厂商行为；本轮没有真实设备证据。

## 契约、状态与资源

新增 IVendorSelectionProvider 和请求/响应领域模型，不暴露第三方类型。QcomResourceResolver 管理取消/超时及迟到请求观察；选择结果必须是有效非 Auto 枚举。同步门面复用已有有界资源等待；同步 Sahara/Firehose 不等待宿主。

浏览会话为根、挂载容器、目录、文件节点。设备分区使用 LUN 块设备切片；LP 默认 slot 0，可显式指定 slot。LP 子分区按需打开并探测 EROFS/Ext；未知类型仍能以 raw 导出。多设备 LP 通过 GPT 分区名解析，名称歧义拒绝。路径根是虚拟 /，可跨挂载层返回，退出按子到父释放卷/块设备/LP 文档。会话断开后旧设备由 Core 代数校验拒绝。

## 安全、性能和失败

仅只读；未识别/损坏格式明确失败。元数据沿用现有预算和 checked 范围校验。文件导出池化流式复制，原子替换，失败清理临时文件；搜索流式 DFS，有节点/深度上限和取消边界，不跟随 symlink，防止目录循环。批量导出保留相对目录，校验每个名称和绝对目的地，不允许越出输出目录或覆盖挂载源镜像。目录列表分页，避免无界收集。命令错误允许返回浏览提示；会话失效则退出并要求重新连接。

## 验证和交付

先在 ignored .tests 写厂商优先级与空证据测试、浏览路径/父目录/嵌套/搜索/导出/释放测试，再实现。补真实格式合成夹具覆盖 LP→Ext/EROFS。运行目标/完整本地测试、Release 解决方案构建、git diff --check、资源双语一致性和 ignored 审查。提交按厂商识别、浏览器、交付文档拆分。

风险：本工作树没有历史 .tests，需重建本轮护栏；无硬件，EDL 小随机读取吞吐、设备取消延迟和实际镜像方言待验证。LP slot 需用户正确选择，默认不推测活动槽。
