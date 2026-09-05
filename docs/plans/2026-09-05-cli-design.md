# GeekFlashCore CLI 设计

日期：2026-09-05

状态：已完成（首批 Qualcomm EDL CLI）

## 目标

新增 `GeekFlashCore.CLI`，使用 .NET 10 将现有传输、USB 监视、协议公共接口和 Qualcomm 专用接口组合为可独立运行的命令行宿主。默认只要求串口或等待 USB 热插拔，协议自动识别；识别失败时允许 `--protocol` 手动指定。运行期间通过 Qcom Provider 向用户请求 loader、Digest、VIP、配置和认证资源。

## 非目标

- 不在 CLI 中复制 Sahara、Firehose 或厂商协议线路。
- 不修改核心协议契约以适配命令行 UI。
- 当前不声称支持尚未实现的 MTK、Fastboot 或 ADB 协议；注册表保留扩展位置。

## 现有实现与线路

- `IProtocol` 提供连接、断开、读写、擦除、分区、重启等通用能力。
- `IQcomProtocol` 额外提供 Sahara 探测、Firehose 配置、Program/Read、原始 Firehose 命令和块设备视图。
- `SerialPortTransportFactory` 和 `LibUsbTransportFactory` 创建同步 `ITransport`；`UsbWatcher` 提供枚举和热插拔事件。
- `QcomProtocol` 已负责同步协议 I/O、Provider 超时、会话失效和资源清理，CLI 只负责编排。

## CLI 架构

```text
Console entry point
  ├─ ArgumentParser / CommandDispatcher
  ├─ ConsoleUi (prompt, progress, table, cancellation)
  ├─ TransportResolver (serial, USB watcher, explicit protocol)
  ├─ ProtocolRegistry (IDeviceIdentify + protocol factory + extension commands)
  └─ QcomConsoleProviders (loader/digest/VIP/config/auth)
```

命令包括 `devices`、`connect`、`info`、`partitions`、`read`、`write`、`erase`、`reboot` 和由注册项提供的协议专用命令。一次性命令在完成后断开；没有命令时进入交互式会话，展示通用和协议专用能力菜单。

## 资源与安全

- 文件参数可选；未提供时 Provider 通过控制台提问路径。文件以 `FileDataSource` 流式打开，不将整个镜像载入内存。
- 认证响应使用 `SensitiveDataOwner`，CLI 不打印 payload 和 challenge。
- Vendor 识别与认证选择分离；Xiaomi 签名线路只有显式 `--auth xiaomi` 时启用，避免将 loader 静态 OEM 提示误当作运行时认证要求。
- 进度使用 `ProgressRecord`，默认单行更新；Serilog 阶段日志写控制台，包级 Debug 日志只有显式 `--verbose` 才启用。
- Ctrl+C 转换为取消令牌；退出前释放协议、传输、USB 监视器和文件流。

## 兼容性与风险

- CLI 目标为 `net10.0`，引用的核心项目仍为 `net8.0`，由 .NET 10 向后兼容加载。
- Windows USB 监视依赖现有 WMI 实现；在非 Windows 环境，串口和显式 USB 创建仍可用，USB 热插拔提示友好失败。
- 未发现需要修改 GeekFlashCore 核心的缺口；若新增协议注册或传输发现能力不足，优先在 CLI 内适配。

## 测试矩阵

- 参数解析、协议选择和无效参数的退出码。
- 文件 Provider 的取消、路径校验和流所有权。
- 进度渲染不改变协议字节；模拟协议连接、通用命令和 Qualcomm 命令分派。
- .NET 10 Release 构建、`git diff --check`；核心 Qcom 回归测试保持通过。

## CLI-06 修复记录（2026-09-05）

- Provider、XML 命令和资源文件参数统一执行 `Trim` 与外围单/双引号剥离，避免交互输入路径被当作实际文件名。
- ConsoleUi 使用共享锁清理进度行后输出日志和结果，日志级别显示为短名称；Serilog 字符串属性不再显示多余外围引号。
- Qcom 探测保留首轮 qdl HELLO 探测，并在未知或后续超时首包时执行有界的 Flush + Sahara `ResetStateMachine`；最多 4 次尝试，最终保留明确异常。
- Sahara 资源或连接失败清理前发送一次状态机复位，再关闭传输，使设备可以重新进入 Sahara 并被下一次连接识别。
- 本地模拟传输新增未知前缀恢复测试；真实设备重连与后端 Flush 行为仍是待验证风险。

## Xiaomi 认证补充（2026-09-05）

- 未指定 `AuthenticationKind` 时，Qcom 核心自动尝试内置兼容签名；签名全部失败后才调用宿主提供的 `IVendorAuthenticationProvider`，CLI 因而会在需要时询问用户。
- 显式 `--auth xiaomi` 作为 Provider 覆盖模式，适用于设备不接受内置签名或需要外部授权材料的场景。
- 内置签名只保存在协议程序集的私有静态表中，逐次解码后清零，不写入日志；真实设备兼容性和授权策略仍需现场验证。
