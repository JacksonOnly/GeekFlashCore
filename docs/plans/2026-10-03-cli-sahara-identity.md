# CLI Sahara 身份输出

日期：2026-10-03；起点 3133592，分支 codex/qcom-legacy-audit，工作区干净。

用户明确要求输出完整 PkHash，序列号、MSM/OEM/Model 等原始标识显示十六进制，显示厂商和芯片名称。此授权覆盖 CLI 显式显示完整 PkHash；通用协议日志继续仅记录 Hash 长度。

实现范围：ConsoleUi 的公共信息块、SaharaIdentityDisplay 格式化与名称映射、ConsoleSaharaImageProvider 的上传前信息、probe-sahara 命令、双语资源与文档；SaharaProtocol 诊断格式同步改用 HEX，但继续只记录 Hash 长度。CLI 复用现有 QcomImageUtils 映射识别 Sahara OEM、SoC HW/MSM 标识；无法识别时回退到已有 Loader 名称或“未知”，不推断设备型号。不改变协议线路、认证、超时、资源所有权或公共抽象。HEX 使用 0x 前缀与大写数字，Serial/MSM/SoC/SBL 最小 8 位，OEM/Model 最小 4 位，保留 64 位序列号；PkHash 按设备原始字节序转为完整大写十六进制。

验证：先补本地输出测试，覆盖 32/64 位序列号、48 字节 PkHash、名称映射、空字段和 Loader 名称回退；覆盖连接前及 probe-sahara 输出。运行目标测试、完整 Release 回归、Release 解决方案构建、资源键检查及 git diff --check；.tests 和产物保持 ignored。单个英文提交交付。硬件字段的真实性以设备响应为准，映射库未知的新芯片保留原始 HEX ID。

2026-10-03 SI-01：新增 4 项输出回归，全部先复现旧 decimal/caHashLength 输出；修改后通过。CLI 根据 Sahara 标识识别 OPPO / OnePlus / realme 和 SM4350 等名称，无 Loader 也可显示；未知 ID 保留 HEX，零值与空值分开处理。48 字节 PkHash 显示完整 96 个 HEX 字符，支持 32/64 位序列号。上传前及 probe-sahara 两项输出回归通过。

2026-10-03 SI-02：新增诊断日志回归先失败，再将原始身份字段统一 HEX；通用日志仍只记录 Hash 长度，不扩大完整 PkHash 的输出范围。CLI Firehose 序列号同为 HEX；厂商策略和硬件厂商分开命名，避免 Generic 被误读为厂商名。

验证证据：目标输出测试 7/7 通过；完整 Release 回归 105/105 通过；Release 解决方案构建 0 警告/0 错误；中英文资源键与 git diff --check 通过。.tests 和 bin/obj ignored，未提交测试或产物。新版程序已构建至 src/GeekFlashCore.CLI/bin/Release/net10.0/geekflash.exe。

交付：本任务单个英文提交，提交号见 git log；下一步可使用新版 CLI 的 info/probe-sahara 复测设备输出。本轮证据来自已知字段及模拟宿主输出，未操作真实设备；新的芯片/OEM 若映射库未收录，则名称保持未知并显示原始标识。
