# MTK Preloader / EMI 修复实施记录

日期：2026-10-08。任务 PL-HS-01 / EMI-XF-01；设计见 [设计](2026-10-08-mtk-preloader-emi-design.md)。起点 `23cb56f`，开始工作区干净。用户新增的标准 EMI 抓包只在 ignored 测试中提取，不提交私有 ROM、日志、抓包或测试工程。

## EMI-XF-01：完整 XFlash BLOADER 窗口

- 原解析器无条件选择 MTK_BIN+12，Ares EMI v51 丢失112字节头，用户 `222420` 日志发送336字节后返回 C0070005。抓包 InitExtRam 长度0x1C0、FLOW448字节，从 MTK_BLOADER_INFO_v51 开始，最后组状态0；未要求额外校验帧或逐帧ACK。
- 增添可选借用 `MtkEmiImage.BloaderInfoSource`，保留旧构造/Source/Version/Legacy窗口。Parser 生成两个有界 MtkDataWindow，同一原源借用，不物化整个 preloader；MTK_BIN 搜索限制在已确认 INFO/version 后。XFlash 选完整窗口，原宿主只提供 Source 时原样使用。核心按实际DA方言在DA上传前验证选用资源，流仅释放自己打开的实例。
- EMI Debug 只记录格式/版本/字节长度，UI既有摘要显示448。Legacy/XML、exp、通用USB/Qcom/SPRD未改。错误状态仍立即失效，不豁免 C0070005、不重发 InitExtRam。
- 实际 preloader：452044字节，SHA256 `7C09C51C6FFC7FB7386E7B08E2DA91FC3D543501055E1D73473A92EB9BB028F4`。离线解析得到完整448/Legacy336/v51；完整448字节与用户837.1抓包逐字节相等，抓包提取窗口SHA256 `3242FECE81061260153A9FABD04A4327C03C3C60F59C8AFEFE5788DFBCD3074C`。这证明源窗口与正确抓包一致，不是修复版实机成功证据。

### 测试与验证

ignored `EmiWindowTests` 六项先5失败/1通过，修复后6全部通过。覆盖MMM签名排除、0x800零尾、完整/Legacy窗口、448长度与FLOW/组ACK次序、实际文件与抓包相等、原宿主材料兼容、选用完整源无效时DA上传前拒绝。

| 命令/检查 | EMI阶段结果 |
| --- | --- |
| EMI + parser旧用例 + TransferDiagnostics 目标测试 | 33通过 |
| MTK 排除既有14项Linecode与缺oppo DA夹具 | 603/603通过（实施Preloader前） |
| CLI全量Release | 168/168通过 |
| solution Release --no-restore | 0警告/0错误 |
| MTK中英资源 | 76键对应；新增模板参数对应 |
| git diff --check / 完整diff | 通过 |

测试编译唯一警告为既有 ignored exp 夹具CS0649；未改该文件。所有测试与原始材料 ignored，不进入提交。

## PL-HS-01：待实施

已定位缺少参考Preloader唤醒、单字节首包接收及只容纳一次READY；已写12项包边界模拟测试，原实现11失败/1通过。首次真实Pipe的候选VID/PID/CDC/端点原因仍未知，下一步补齐标准握手和候选诊断，不修改漏洞策略、不运行真实USB/DA。最终完整回归与风险将在此处追加。
