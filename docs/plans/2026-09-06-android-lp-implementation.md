# Android.Lp 实施进度

## 进度

- 2026-09-06：确认当前仓库已有空的 `GeekFlashCore.Android.Lp` 项目，参考项目提供完整 LP 实现；确定按现有 BlockDevice/Sparse API 做适配移植。
- 2026-09-06：完成目录、公共契约、依赖差异和参考测试的首轮分析；开始迁移实现。
- 2026-09-06：完成 `Android.Lp.Abstractions`、`Android.Lp` 全量源码和中英文资源迁移；新增 `ImageFormats.Abstractions` 最小公共契约、`BlockDeviceExtent`、`MappedBlockDevice` 与 flush durability 接口，解决当前仓库与参考项目的命名及依赖差异。
- 2026-09-06：参考 LP fixture/公共契约测试 53 项通过；新增 RAW/非 Seekable 输入和单文件 metadata 提交验证测试通过。
- 2026-09-06：补齐 Seekable/Non-Seekable `Transfer` 输入在探测、前缀读取失败路径的释放逻辑，新增 2 项所有权测试；Android.Lp 目标测试共 55 项通过。
- 2026-09-06：`dotnet build GeekFlashCore.slnx -c Release --no-restore` 通过（0 警告、0 错误），`git diff --check` 通过；尚未执行真实设备验证。

## 未决风险

- 当前仓库没有参考项目的 `ImageFormats` 和 `Storage.MappedBlockDevice` 类型，已通过独立 `ImageFormats.Abstractions` 与公共 `MappedBlockDevice` 提供等价适配；真实设备上的多物理 block-device 提交仍需硬件验证。
- 本地测试工程按规范位于被忽略的 `.tests`，不会提交到 Git。
- 非 Seekable RAW 输入按参考语义只允许一次顺序消费（`CanReplay == false`）；调用方需要在规划阶段保证数据写入顺序，当前已有测试覆盖输入所有权和基本声明长度行为。
