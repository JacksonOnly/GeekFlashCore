# Oplus 启动修复进度

2026-10-03 OB-01：分支 codex/qcom-legacy-audit，起点 2266fa0，工作区干净。已对照用户抓包确认首个 Firehose 输出是 76 字节 NOP，而设备正等待 8192 字节签名表；设计见同日期 oplus-bootstrap-design。旧 66 项本地回归作为基线。正在补充启动顺序测试。

未决风险：目前仅有失败硬件抓包，修复后握手及实际读写需用户复测。

2026-10-03 OB-02：先新增启动顺序和移除参数测试，8 项全部失败，实际线路为 NOP → Configure → getstorageinfo，与用户抓包一致。已实现 VIP 启动标记识别、Oplus 完整 1500 ms 被动探测预算及禁止初始化前主动探测；统一同步/异步 Digest → verify → 4096 字节 Sign → sha256init → Configure 顺序，Oplus 不再误入普通 VIP Provider。Legacy Sign 必须显式提供，Pt 使用参考芯片模板或手动 Sign；完整拒绝最多请求一次替换，NAK 后单独到达的 VIP 提示在 1000 ms 有界窗口内读取，必要时重发原 Digest。超时、半帧、Raw 错误不能进入 Configure。Verify ACK 支持省略/rawmode=false/rawmode=true，状态明确进入 RawTransfer，Sign 完整响应后回到 XML。

初始 Digest 确认后计数归零，随后 verify、Sign、sha256init、Configure、getstorageinfo 共 5 包；配置/存储回退增加 2 包，复用已认证资源，不重新获取 Digest。原 Rector 运行期换表的边界、NOP、重发、截断及 Flush 线路保持。Sign 缓冲最终清零、打开的流用 using 释放；调用方数据源不释放。新增公共请求标志 RequireSign/PreviousSignRejected 及响应 Sign 属性；旧宿主开启 Legacy 时必须补充此响应属性。

2026-10-03 OB-03：CLI 新增 --oplus-sign 和 --log-file；移除全部六项 Legacy 调整参数，固定 53/0/256/1000/4096/内置 NOP，Core 默认分段同为 256。Pt 及 Legacy 支持交互选文件；无效文件提示一次替换，非交互 Legacy 缺少资源在设备发现前失败。默认程序目录 logs 自动生成唯一文件日志，Debug 不依赖 --verbose，按事件刷新、16 MiB 分段、追加保护已有日志。记录命令名、字节数、状态、rawmode、预算/耗时、异常堆栈；精确允许已知签名错误码，附加材料仍隐藏；启动未完成时已经接收的诊断也保留。

2026-10-03 OB-04：最终检查追加已认证会话重配置回归，先复现重复发送启动 Digest 的失败，再增加会话认证标志。ConfigureFirehose 重配置只执行配置/查询，不向 XML 状态发送签名表；失效/断开清理标志，重新连接需要新认证。

2026-10-03 OB-05：验证成功日志也可能晚于完整 ACK；新增失败回归后修复关联窗口，在已有 ACK 的基础上合并完整后续日志，再判断 verify passed。单独日志不会代替 ACK，NAK 加成功日志仍不接受。

验证证据：完整 Release 回归 98/98 通过（此前基线 66，新增 32）；目标测试覆盖 Legacy/Pt、同步/异步、自动/显式/缺失 Sign、拒绝一次/重复拒绝、延后的 VIP/verify-passed 标记、长度/取消/超时/半帧、RawMode Verify、资源回退/重配置、文件日志实时刷新/异常堆栈及敏感材料隐藏。Release 解决方案构建 0 警告/0 错误；中英文资源键一致，git diff --check 通过，.tests/temp/bin/obj 均 ignored。离线 EXE --help 退出 0，确认 Sign 参数存在、Legacy 参数全部消失；temp/oplus-help-smoke.log 实际包含无 --verbose 的 DBG 会话记录。

交付：构建程序 src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe；当前分支 codex/qcom-legacy-audit。协议提交 35ba4d9、重配置 0af95d8、延后日志关联 64a445c；CLI/日志及恢复文档单独提交，最终提交号见 git log。下一步从用户硬件 info 握手日志复测开始，再验证实际读取/写入；本轮未操作设备，不把模拟 ACK 当作硬件成功。
