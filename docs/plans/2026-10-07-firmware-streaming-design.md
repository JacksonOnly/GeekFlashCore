# 固件解包与流式刷写设计

日期：2026-10-07。任务 FW-01–FW-04。用户授权迁移 FlasherCore 固件解包，自主决定项目边界，并支持不落盘的 OFP → Qcom rawprogram/patch。

## 目标与边界

- 独立 `GeekFlashCore.Firmware`（net8.0），公共 API 不暴露压缩库、Protobuf、NativeAOT 或参考项目类型。未来 Packer 可在同一领域扩展，本轮不实现打包。
- 迁移 ZIP、OZIP 两种布局、OFP Qcom/MTK、OPS、PAC、KDZ、DZ、UPDATE.APP、Android payload v2 的目录及流式读取能力。
- payload 支持独立完整镜像的 REPLACE/BZ/XZ/ZSTD/ZERO/DISCARD，遍历全部目标 extent，拒绝依赖旧镜像的增量操作，绝不静默填零。DISCARD 导出为确定性零。
- Firmware 不依赖 Qcom 实现、不连接设备、不下载资源。CLI/开发者显式调用现有 Qcom ExecuteRawProgram/ExecutePatchFile，保留其预检、同步发送、ACK/NAK、会话失效和取消顺序。

## 参考与兼容性

- 参考：`D:\Code\Project\FlasherCore\Lib\FlasherCore\FlasherCore.Firmware` 的 Unpackers、Models/EntryStream 与 update_metadata.proto。
- 保留 OFP 的尾页检测、密钥派生、CFB128 前缀和明文尾；MTK shuffle、OPS 自定义反馈与虚拟 XML；PAC 高低位长度；KDZ 容器和 DZ 独立 chunk；OZIP 间隔 ECB 布局。
- 修正参考实现的无界长度、整段解密分配、共享流游标、ushort 索引截断、短读成功、宽泛吞异常、ZIP/payload 内存缓存和只使用首 extent。
- 格式识别按签名和有限尾部候选进行；已识别格式的损坏不回退成其他格式。OFP/OPS 密钥探测有固定候选预算。

## 公共契约与所有权

- `FirmwareUnpacker.Open(IDataSource, FirmwareOpenOptions?, CancellationToken)` 返回只读 `FirmwarePackage`。另有路径入口；源由宿主借用，包只释放自己打开的流和内部密钥。
- `FirmwareEntry : IDataSource`：稳定规范化名称、明文 Length、可独立重复打开的只读 seekable Stream。条目索引 int；重名保留目录，按名称解析时拒绝歧义。
- 包 Dispose 后旧条目/已开流失效；每个流拥有自己的源流与解码状态；同一流不支持并发使用，不共享文件游标。
- 所有操作同步；异步 IDataSource 入口只按契约返回流并检查取消，不阻塞异步资源提供器。源需稳定、可重开、可定位且长度一致。
- 嵌套容器显式 `Open(package.GetEntry(...))`，避免自动展开或隐式择选 firmware。

## 安全、性能与失败

- checked 范围/长度、目录项、metadata、operation/extent 数量上限；安全 XML（禁 DTD/外部实体）与有界 Protobuf 解析。
- 加密按有限块解码；压缩通过重开/顺序跳过提供 Seek，只有固定缓冲，不写临时文件。向后 Seek 有重解压时间成本。
- ZIP 在第三方目录构建前校验 EOCD/Zip64、目录实际记录数、目录字节预算、分卷与密码标志；仅启用 Stored/Deflate/BZip2，避免其他解码方法的未受控字典。固定 65557 字节尾部签名搜索不占用可配置的序列化目录预算。
- SharpCompress 固定 0.50.4（与现有 EROFS 依赖一致），ZstdSharp.Port 固定 0.8.8；payload XZ 在分配前读取 index/block filter 字典配置，Zstandard 设置 windowLogMax，默认每解码器窗口 64 MiB。
- OFP 唯一 section 与 OPS 唯一输出脚本名校验在目录发布前完成，重复定义失败；已有物理脚本仍优先于对应虚拟脚本。
- 路径禁止绝对、驱动器、空段、`.`/`..` 和控制字符，解析图片基于 XML 所在包目录，找不到时失败，绝不任意回落本地目录。
- CLI 提供 `firmware list <package>`、`firmware extract <package> <entry> <output>`；rawprogram/patch 接受 `<package>::<entry.xml>`。不自动刷完整包，按用户指定脚本及顺序执行。
- 所有用户可见框架诊断使用中英文 resx；不记录密钥/完整 XML，不新增重复底层日志。取消在解析循环、读、定位和复制边界检查；解析失败释放已取得资源，不重试设备写入。

## 实施及验证

1. FW-01：公共 API、资源生命周期、流式基础设施与边界测试。
2. FW-02：各格式 parser/decoder 与合成夹具；OF​​P 明文输出、短读、>2 GiB seek、错误长度与取消。
3. FW-03：CLI 离线命令、包内 XML/镜像解析，模拟 Qcom 写入字节、顺序与失败测试，开发者示例。
4. FW-04：全测试、Release 构建、diff/resource/ignored 审查、文档收尾与独立英文提交。

测试仅留 ignored `.tests`。命令：`dotnet test .tests/GeekFlashCore.Firmware.Tests/GeekFlashCore.Firmware.Tests.csproj -c Release`；现有 Qcom/CLI/Core/LP/MTK 全测试；`dotnet build GeekFlashCore.slnx -c Release --no-restore`；`git diff --check`；`git ls-files .tests`。

## 未决风险

- 合成格式/模拟传输证据不等于真实厂商固件或硬件验证。本轮另有真实 PAC 的目录和大偏移只读证据；OFP/OZIP/OPS 等真实包和 Qcom 真机线路尚未验证，未知密钥与厂商布局应明确报错。
- 压缩流随机定位的时间成本与第三方解码器窗口内存需实际固件测量；不承诺 O(1) 的压缩 Seek。
- 不实现 payload 增量补丁、DZ 自动合并分区、签名信任判断或镜像真实性认证；容器解包成功不是刷写授权或认证结果。
