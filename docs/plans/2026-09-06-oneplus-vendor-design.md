# OnePlus/Nothing Core 厂商线路设计

日期：2026-09-06

状态：已实施，待真实设备复测

## 目标

- 将 GeekFlashTool 的 OnePlus 项目配置、代际 Token 算法和认证顺序迁移到 Core。
- OnePlus 未指定 `ProjId` 时先从已缓存 GPT 的 `param` 分区读取偏移 24 的 5 字节项目 ID。
- `param` 不存在、读取失败、内容无效或 ID 不在内置配置表时，在同一 Firehose 会话内按固定候选表有界尝试项目配置，成功即停止。
- Core 不因 OnePlus/Nothing 项目 ID 缺失而调用方认证 Provider；Provider 仍保留给其他需要外部材料的认证类型。
- 审查 Nothing：默认按已知设备项目 ID 顺序尝试本地 Token；Core 连接流程不请求 Provider。

## 非目标与兼容性

- 不改变 Sahara、Configure、Storage、Raw/Sparse 或 Oplus Digest 线路。
- 不在候选失败后重新 Configure、重连或无限重放；候选数量固定并受取消令牌约束。
- 不删除 `IVendorAuthenticationProvider` 或现有显式 Token 解码 API。
- 真实设备对不同 Loader 的响应文本和项目 ID 仍需硬件复测。

## 线路与算法

1. Configure 成功并完成存储缓存后，OnePlus 读取 `param`：使用已有分区快照定位分区，按其 LUN、起始扇区和当前扇区大小读取一个扇区；只接受 ASCII 数字/字母组成且存在于配置表的 5 字节 ID。
2. 候选顺序为调用方显式属性中的逗号分隔 ID（若存在）、有效 `param` ID，或内置配置表的声明顺序。重复候选去重。
3. 版本 1 使用原项目 ID；版本 2 使用配置中的 `Cm` 作为加密项目值；版本 3 使用 `Cm` 和其十六进制转十进制的设备 ID。生产密钥、随机后缀、固件版本、SHA-256 和 AES-CBC 常量与参考实现一致。
4. 支持 `demacia` 时先发送 Demacia，再按参考顺序发送 `SetNetType` 或 `setprojmodel`；支持 `setswprojmodel` 时先 `setprocstart`，读取设备时间戳，再发送软件项目 Token。只有支持 `setprojmodel`/`setswprojmodel` 的会话才给后续 program/patch 追加 Token。每个候选失败只记录脱敏诊断并继续。
5. Nothing 先发送 `checkntfeature`，再使用内置候选 `22111`、`20111` 生成 `ntprojectverify`；候选失败继续，成功结束。保留显式 `Verify` API 供已有宿主直接传入项目 ID，Core 连接流程使用内置候选。

## 契约、状态和资源

- 新增内部 `OnePlusDeviceProfile` 和 `OnePlusTokenGenerator`，不暴露第三方类型或完整 Token 日志。
- OnePlus/Nothing 认证在现有 `FirehoseSession` 的 `Configured` 状态内串行执行；任何取消、传输异常或全部候选失败都会使外层连接失败并由现有 `Cleanup` 失效会话。
- `param` 读取使用固定单扇区缓冲，不按分区大小分配；读取失败视为无法探测并进入候选枚举。
- 不保存认证 payload；随机公钥只在当前认证流程使用，日志只记录项目 ID、代际和结果。

## 文件范围与测试

- Core：OnePlus 配置/算法/验证器、Nothing 验证器、`QcomProtocol` 同步/异步认证工作流及必要本地化文本。
- 忽略测试：OnePlus Token 长度、候选顺序、Nothing 无 Provider 回退、program/patch 认证字段；参数读取异常回退由连接流程覆盖。
- 命令：目标 Qcom 测试、`dotnet build GeekFlashCore.slnx -c Release --no-restore`、`git diff --check`。

## 风险

- 参考项目 `demacia` 的明文构造存在非十六进制前缀，需以现有设备样本确认其 Loader 方言；实现保持参考调用顺序并限制输入长度。
- `param` 可能位于非零 LUN 或 GPT 不可解析，当前仅依赖会话已有分区快照，失败时自动走候选枚举。
- 候选表来自 GeekFlashTool 当前源码，未来新机型需追加配置并重新验证。
