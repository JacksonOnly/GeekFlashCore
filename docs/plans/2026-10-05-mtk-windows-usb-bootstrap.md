# MTK Windows USB 启动准备

## 设计与授权

2026-10-05，用户要求修复 CLI 缺少 libusb-1.0 DLL，并自动安装 libusb-win32；用户确认按 MTK 刷机硬件 ID 安装。仅覆盖核心识别的 VID 0E8D、PID 0003/2000/2001/6000，包含各实例、接口和设备类。禁止对共享 Ports/USB 类或全部 USB 设备安装，不包含普通 Android PID 2029。不实现或执行漏洞利用。

原生库与内核驱动是两项依赖。CLI 在 native 枚举/创建前加载与进程架构一致的 libusb-1.0；Windows MTK 连接前读取注册表有效驱动/设备与类过滤器，缺少可用绑定时调用官方 install-filter.exe 的 install --device= 硬件 ID 参数。整个 ID 的实例均已有 WinUSB/libusbK/libusb0 或有效 libusb0 过滤器时跳过安装；若同 ID 有不兼容实例，按用户选择的硬件 ID 范围覆盖该 ID 的全部实例/接口。枚举命令只加载原生库，不安装驱动。串口、help、离线镜像流程不依赖原生 USB。

只在 CLI 增加 internal 引导组件及可替换的注册表/进程后端，不改变协议公共契约、同步 I/O、会话代数和线路顺序。先准备 DLL，再检查桥接依赖，再安装过滤器并重新检查注册表，最后建立新的 libusb context。每轮 native 查找检查新出现的 MTK ID，准备时间不消耗设备发现预算；安装含等待 UAC 最多 60 秒，外部取消始终传递。迟到启动仅清理进程句柄并尝试终止，不参与连接。UAC 是 Windows 必需的管理员授权；非交互模式须预先以管理员运行，错误资源化，不隐藏不确定结果、不自动重试失败安装。

构建属性 LibUsbNativeRoot / LibUsbWin32Root 可覆盖；默认在用户给定目录存在时使用该目录，复制 x64/x86 runtime、安装器、配套 DLL/SYS 和许可证到 build/publish 目录；ARM64 仅可部署 libusb-1.0，自动 libusb-win32 安装明确不支持。第三方二进制不提交。libusb0.sys 访问还需要 libusbK.dll，允许使用系统现有桥接库；缺失时在修改驱动前报告依赖错误。官方来源：https://github.com/libusb/libusb/wiki/Windows#driver-installation 。1.2.7.3 本地 install.c / registry.c / install-filter-help.txt 确认硬件 ID 匹配及安装参数。Windows 官方 libusb 说明指出过滤器支持存在兼容性限制，实际设备仍需验证。

LibUsbDotNet 3.0.224 自带 assembly DLL resolver（NuGet 源码提交 a89bc81569327840e98a2c30207e5b74bbc97b3a）；CLI 不再注册第二个解析器，而是按完整路径预加载 DLL，由 Windows 已加载模块机制与原解析器共同工作。实际 build/publish native 枚举已验证兼容，初次第二 resolver 的失败已修复并添加回归测试。

install-filter.exe 本身不复制 SYS。CLI 使用隐藏的内部 helper 入口提权，在固定系统目录只补齐缺失的 libusb0.sys / libusb0.dll，不覆盖任何已有系统文件，再用 ArgumentList 调用配套安装器。x86 DLL 同时以 libusb0.dll 正确名称部署。helper 再次检查管理员、白名单及完整包，不接受文件路径/类 GUID 参数。参考 filter-bin-setup.iss.in 的文件部署顺序。工具内部会停启已有直接绑定 libusb0 的设备，因此其他已使用该服务的设备可能短暂重启；其绑定/过滤器不属于新增安装范围。Windows 签名策略或系统现有旧版本不兼容时不得关闭系统保护，需要设备补证。

## 步骤与验证

1. ignored CLI 测试先定义硬件 ID 白名单、不同类/接口、已有绑定、精确参数、空集合、安装失败/取消/复核。
2. 原生库按进程架构部署与加载，驱动安装器按 Windows 系统架构选择；接入显式 --usb、native 探测和 devices。
3. 验证 CLI 目标测试、全部本地测试、Release solution 构建、native 枚举及 publish 文件结构、资源对应和 git diff --check；审查并单独提交。

测试通过不等于驱动/UAC/设备连接验证。驱动安装可能触发 USB 重启，超时/取消无法回滚已发生的驱动变化；后续必须重新枚举。安装输出属于外部诊断，CLI 不直接转发无界日志。

## 进度

- USB-01：设计完成；初始工作区 clean，HEAD dfa0207。当前注册表存在 0003/2000 的 Ports 类实例且无过滤器；shell 非管理员，系统已有 x64/x86 libusbK.dll。仅为只读证据，未安装驱动。
- USB-02：实现 NativeUsbRuntime、NativeUsbAssets.props、WindowsMtkDriver 与固定内部提权 helper；接入显式 USB、MTK 原生探测、只读 devices。库解析器冲突已用实际 native 枚举复现并修复。安装器参数始终为非空的 install + --device= 白名单，不包含 class/all 开关；缺失 SYS/DLL 在提权后补齐，现有文件不覆盖。中英文新增 13 个资源键。
- USB-03：目标测试先因缺少接口编译失败，再实现并完成 CLI 85 项（新增 21 项）。全套 MTK/Qcom/CLI/Core/Android LP 为 120/251/85/9/55，共 **520 通过、0失败、0跳过**。后续 CLI 修改单独复验 85 项无警告；Release solution 构建 0警告/0错误；publish 与 `git diff --check` 通过。模拟后端覆盖白名单、接口后缀、去重、新设备、已有驱动、依赖失败、非零退出、复核失败/设备消失、安装前后取消；只读 native context 回归覆盖 resolver，实际无修改 PowerShell 等待进程覆盖超时/取消。
- USB-03 实机环境证据：build 与 publish 的 `--protocol mtk devices` 均 exit 0，未发现在线 MTK，不宣称设备可访问；发布的 x86/x64 DLL、SYS、安装器、x86 libusb0.dll 改名及许可证检查通过。`--protocol mtk --non-interactive mtk-probe` exit 1，正确发现两类历史 MTK ID、加载 libusb0/libusbK，按非管理员约束在 native 设备连接和安装之前报错；无注册表/系统文件写入。UAC 实际提权、内核签名加载、硬件握手均未执行。
- 测试、bin/obj、日志和 publish 产物保持 ignored，无受跟踪 `.tests` 或第三方二进制。作为独立 `fix(cli): prepare native USB and scoped MTK filters` 提交，提交号见 git log；未 push。恢复时先读本文和实施计划，再以管理员/交互 UAC 在真实 MTK 设备补证，无需新增漏洞实现。

## 使用与恢复

在本工作区重新构建后，运行原 CLI 交互命令即可按发现的 MTK 刷机 ID 自动检查和安装。Windows 可能弹出一次管理员授权；已是管理员则直接安装。devices 命令只读，不安装。非交互模式需要预先以管理员启动。默认使用用户给定的两个 D:\Code 目录，文件会进入输出和 publish 目录，运行时不依赖该源码目录。

其他构建机可指定：

```powershell
dotnet publish src/GeekFlashCore.CLI/GeekFlashCore.CLI.csproj -c Release -p:LibUsbNativeRoot="D:\Code\libusb-1.0.29" -p:LibUsbWin32Root="D:\Code\libusb1273\libusb-win32-bin-1.2.7.3"
```

还须保证目标机器有对应架构的 libusbK.dll（系统目录、CLI 根目录或对应 runtimes native 目录）。取消/超时/驱动绑定失败后停止本次连接，检查 Windows 驱动状态并重新连接。没有真实 USB 传输、UAC 提权安装或签名加载证据前，不宣称真机刷机可用。
