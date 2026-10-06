# MTK CLI 连接准备与错误展示修复

日期：2026-10-05。起点 ff14fa8，工作区 clean。用户两份真实日志分别证明：14:04:40 驱动安装与注册表复核成功，14:05:04 在 UsbDevice.Open 返回 NoDevice，尚未进行 BROM；第二次 14:05:10 已握手且 Probed，随后资源 factory 因未指定 DA --loader 失败，ConsoleUi 的异常允许列表未包括 MtkResourceException，隐藏了可操作原因。第一份日志不能证明所有断开均由过滤器安装引起，也不能证明 DA 线路正确。

2026-10-06 顺序修订：交互 DA 提示早于 USB/Probe 导致 WDT 初始化尚未执行，现改为先 Probe/关闭已知 WDT，再等待 DA 并复用同一会话。下文保留 REC 当时的实现证据，材料等待顺序以 [WDT-CLI-01 修复记录](2026-10-06-mtk-watchdog-before-loader.md) 为准；非交互预验证和协议创建前 NoDevice 重试边界不变。

## 设计与步骤

1. ignored CLI 测试先复现 MTK 资源错误被隐藏，再定义文件选择与 Open 重试边界。
2. CLI 识别为 MTK 后，在持有 native 设备/context 和开始 Probe 之前异步选择 DA 文件。已配置文件直接验证；交互模式提示路径，非交互/重定向未配置时显示 --loader 用法。mtk-probe / mtk-capabilities / devices 不要求 DA，Qcom 不改变。准备结果通过 internal TransportResolution 返回宿主，资源 factory 仍在 Probe 后按目标解析 DA。选择合作取消且沿用 MTK 有限资源预算；不阻塞同步协议资源回调，不新增协议公共契约。
3. MTK native 解析在发现预算内创建并立即 Open 传输；仅 NoDevice 在协议尚未创建时释放传输、重新枚举，其他错误停止。候选过期不复用 context/句柄，不重试握手、认证、DA 上传或存储操作。Open 只建立/配置 USB，BROM 帧由核心首次 Probe 发送。显式 --usb 也使用这一入口并保持 VID/PID 限制、物理身份和多设备歧义拒绝。
4. ConsoleUi 显示 MTK 资源异常的已有资源化消息；USB NoDevice 使用资源化重连提示，未知异常仍隐藏详情。日志保留完整堆栈且不输出敏感材料。

修改限于 CLI 的连接编排、MTK adapter、USB resolver、ConsoleUi 和中英文资源，以及本计划/实施进度。同步协议、漏洞框架和驱动安装行为不变。验证 CLI 目标测试、MTK/Qcom 回归、Release solution 构建、差异与 ignored 状态；提交独立修复。实机 DA 上传仍需要用户提供与设备匹配的合法 DA，禁止自动选用未知来源文件。

## 进度与风险

- REC-01：日志根因已确认，开始测试先行。长 native 枚举/Open 的耗时和物理断开仍须设备补证；本次有界重试不承诺中断第三方正在阻塞的 native 调用。材料选择可能使设备自行重新枚举，所以在选择完成后才取得全新 native 句柄。
- REC-02：实现宿主材料准备回调、PreparedOptions 转交、DA 文件选择/有限等待、MTK 异常展示、USB preflight Open/NoDevice 重新枚举。默认/显式 MTK、显式 VID/PID 选择共用入口，取消或超预算不交付已打开句柄。重试只发生于协议创建之前；握手后没有任何自动重试/策略实现。读取核心确认 Fault 始终关闭借用传输，CLI 持有并最终 Dispose preopened 传输，协议代数/资源所有权未改变。
- Windows monitor 的超时转换只包围 hotplug wait，发现设备后先停止 monitor，再进行材料选择/native 解析；用户留空取消 DA 选择不会被错误转换为设备发现超时。自动检测的这一边界为代码审查证据；与当前机器 hotplug 的联动仍待后续日志。
- REC-03：测试先因新增宿主接口与 preflight helper 缺失而编译失败，实现后 CLI **101 通过**（本次新增 16），MTK **120**、Qcom **251**，共 **472** 项目标/回归测试，均0失败/0跳过；Release solution 构建0警告/0错误，git diff --check 通过。覆盖资源原因显示、DA 选择重试/引号、非交互缺失、Probe/capabilities 跳过、Qcom 保持、选择超时/取消区分、自动 MTK 拒绝无限资源预算、显式 VID/PID 约束、NoDevice 候选释放、非瞬时错误不重试、Open 后取消释放及无协议读写。
- 本地实际命令 `--protocol mtk --non-interactive info` exit 1，立即显示 DA 文件与 --loader 操作提示，未创建 USB/开始 Probe；`--protocol mtk devices` exit 0，保持只读、不要求 DA；这些证据不能代替重连或 DA 上传的真机验证。测试、输出/日志均 ignored，无跟踪 .tests。以独立 `fix(cli): prepare MTK loaders and recover stale USB candidates` 提交，hash 见 git log；未 push。

重新运行本工作区 build 的 geekflash，可在识别 MTK 后输入合法 DA 文件路径；也可使用 `geekflash --protocol mtk --loader "<DA 文件完整路径>"`。仅诊断握手使用 `geekflash --protocol mtk mtk-probe`。若文件选择期间设备离线，等待其再次枚举；认证、EMI 与 DA 执行失败不自动重试。下一步是在匹配 DA/必要 preloader 材料下补充设备连接日志，本次未执行任何 DA/闪存操作。
