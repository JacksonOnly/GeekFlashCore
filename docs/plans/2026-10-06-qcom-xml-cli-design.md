# Qualcomm XML 刷机与 CLI 交互设计

日期：2026-10-06；范围：XML-01～03 / CLI-14～15；用户本轮已授权实现。

## 目标与兼容性

- CLI 支持 `rawprogram <xml-or-pattern> [...]`、`patch <xml-or-pattern> [...]` 和直接输入 rawprogram*.xml / patch*.xml；镜像相对各自 XML 目录解析。保留现有数字 patch、program/write 与脚本用法。
- 协议公开同步 rawprogram / patch 文件执行入口，使用 IDataSource 与宿主镜像 resolver，不依赖 CLI 或第三方公共类型。
- XML 按文档顺序执行已实现且设备列表未明确排除的命令；未知命令跳过并报告；空 filename 的 program 跳过；patch 文件仅执行 filename=DISK 的 patch。
- 帮助优先显示通用命令，设备支持 => Host 映射使用简洁摘要；未上报列表显示未知，不展示全部本地候选为设备能力。完整 Host 语法仅按需查看。
- REPL 加入会话内历史 Up/Down、编辑和 Tab/Shift+Tab 命令循环补全；资源/认证输入不进入历史。重定向输入保留逐行行为。

## 证据与线路

- alioth images 中 rawprogram0～5.xml 有空 filename、raw/sparse、num_partition_sectors=0 与 NUM_DISK_SECTORS-5.；patch0～5.xml 混合 GPT 文件补丁与 DISK 补丁，包含 CRC32。
- 参考本地 qdl program.c：file_sector_offset 按 XML 扇区计算；sparse 属性是提示，以头部判定；按实际镜像长度传输。现有 Core 的 PadToSectorCount=false 与流式 Sparse 可复用。
- 保留 Program XML => RAW => 最终 ACK、OnePlus credential、Digest/VIP session hook 与会话失效规则；DISK patch 通过现有 storage.Patch 发送，CRC32 由设备计算，不在 Host 回读整个介质。

## 契约、资源与边界

- 每次执行持有同一个协议 gate，先对整个 XML 做有界解析与参数/资源预检，再按序发送；首个失败停止，写入结果未知时不重试。取消在预检及命令/RAW 边界检查。
- XML 最大 8 MiB 字符，命令/属性/值数量有界，禁用 DTD、实体、命名空间、嵌套命令；允许列表转换成领域请求，不透传自定义 XML。
- 扇区数、LUN、SECTOR_SIZE、文件窗口、NUM_DISK_SECTORS 算术、CRC 范围在 I/O 前校验；拒绝未知表达式和整数溢出。不执行未有专用适配的 RAW/认证/配置命令。
- 只释放协议打开的流；镜像 IDataSource 归宿主所有，resolver 需可重开且返回稳定资源。不展开镜像，只保存有上限的命令元数据与现有 Sparse segment 计划。
- 日志仅阶段/序号/命令名与跳过原因，异常和提示双语资源化，不记录完整 XML 或认证内容。

## 文件、验证、提交与风险

- 文件：Qcom.Abstractions 接口/脚本结果，Qcom 脚本解析/执行、CLI 命令接入/帮助/输入编辑器、双语资源、docs/cli-qcom.md 与计划。
- 测试固定 ignored .tests：真实包 XML 只读解析；Raw/Sparse/offset/空文件名/顺序；DISK/CRC/表达式；未知/设备排除命令；缺文件/错扇区/溢出/DTD/嵌套/NAK/取消/释放；帮助过滤；历史/编辑/补全/重定向。
- 验证：目标测试、完整本地 Qcom/CLI 测试、Release 解决方案构建、资源键对应、git diff --check、ignored/跟踪检查；分协议 XML 与 CLI 体验提交。
- 无硬件操作；真实设备 CRC 方言、ROM 适配、厂商策略接受、终端宽字符渲染仍需实机验证。批次执行非事务，成功的前序写入不可回滚。
