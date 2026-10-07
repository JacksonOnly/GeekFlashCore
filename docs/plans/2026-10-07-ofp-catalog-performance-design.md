# ZIP 内 OFP 目录读取性能

日期：2026-10-07。任务 OFPCAT-01–03，基线 2459542，工作区干净。用户报告交互 `firmware list ZIP::OFP` 很慢，授权优化；继续保持不提取、低内存和同步离线读取。

## 结论与方案

- 代码证据：OfpSuperMapper.Add 在目录创建时调用 Compose，逐片扫描完整 Sparse；CLI list 又读取每个 Length。列目录不应该要求镜像 payload 通过检查。NV、路径、条目范围和歧义仍在打开 catalog 时校验，完整 Sparse/CRC 检查推迟到获取虚拟条目的精确长度或打开流，且仍早于原 Qcom 写入。
- 真实 ZIP 的 OFP 是约 9.9 GB 的 Deflate 条目；OFP footer 在尾部，metadata 85378 字节，距尾部约 90 KiB。自动识别、footer 和 metadata 的小范围回读会反复重放解压。ZIP 的 ReplayStream 增加固定 128 KiB 池化环形回读窗口，涵盖本包的尾部访问。窗口外回退仍重放，任何窗口都不随文件大小增长。
- Deflate 没有任意位置直接寻址能力；首次获取内部 OFP 尾部仍需要一遍顺序解压，不能承诺像 PGT110 的独立 JSON 那样毫秒返回。本轮不添加临时文件、整包缓存、永久索引或设备操作。

## 契约、所有权与失败

- FirmwareEntry 新增 KnownLength（nullable，不触发 I/O）和 GetLength(ct)（有取消的精确解析）；现有 IDataSource.Length 继续返回精确值，虚拟条目首次访问可能解析。CLI list 对尚未解析的长度显示本地化“按需解析”，不主动构建 Super。流仍独立、由调用方释放，父包借用。
- 延迟映射由包 gate 串行化，只缓存成功且未取消的结果；失败不永久缓存，包失效后不能继续解析或打开旧条目。原 sparse 来源/计数/映射预算和 checksum 不放宽。
- Sparse Compose 保留原三参数签名与取消语义，新增四参数重载分离本次解析 token 和缓存结果生命周期 token。Firmware 使用包 token 作为后者，避免一次 GetLength/OpenStream 的 token 在完成后取消，导致已缓存映射永久不可用。
- 回读窗口属于单个 ZIP 条目流，释放时清零并归还池；不共享游标。取消、EOF、声明长度、补零和窗口外重放保持原校验，缓存命中也检查取消。不改变 Qcom、MTK、Digest 或重连线路。

## 文件与验证

- FirmwareEntry、FirmwarePackage、FirmwareCatalog、OfpSuperMapper、ZIP/ReplayStream、CLI FirmwareCommands 和双语资源；说明文档与独立实施记录。
- ignored .tests 先复现列目录触及 payload、坏 Sparse 阻止列目录、近尾回读重开解码器；再验证按需长度缓存、取消后重试、并发/释放、跨窗口回退、环形边界和随机读一致。CLI 验证 list 不解析虚拟镜像。
- 运行 Firmware、CLI、Core、Qcom 回归，解决方案 Release、git diff --check、双语资源与 ignored 检查；真实 PEHM00 ZIP::OFP 测量 catalog 时间/内存并核脚本及虚拟镜像旧回归。PGT110 路线保持现状。
- 提交按离线回读优化与延迟目录能力拆分。风险：第一次读取压缩 OFP 的尾部仍需全条目解压；虚拟条目损坏晚于 list 才报告；大于回读窗口的 metadata 仍可能重放。测试与离线证据不代表设备兼容验证。
