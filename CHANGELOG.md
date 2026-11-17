# 更新记录

本文件格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [1.1.0] - 2026-11-17

### 新增

- **Native AOT**：库开启 `IsAotCompatible`（AOT/裁剪分析器零告警，IL2026/IL2067/IL3050 提升为编译错误）；
  示例启用 `PublishAot`（自动启用分析器）与仅在 Windows 生效的 `BuiltInComInteropSupport`，
  win-x64 原生产物已验证 LOD/深缩放、MBTiles 与在线瓦片、PNG/JPG/GIF/SVG 图标、区域与标注、
  悬停/命中回归与性能面板；新增英文说明 [docs/native-aot.md](docs/native-aot.md)
- **性能面板**：`ShowPerformancePanel` 开关在控件左上角显示实时性能 HUD（0-100 流畅度评分、帧率、
  由较快一档帧间隔估算的刷新率、帧耗时、绘制瓦片/纹理数、LOD 层级与选瓦片耗时、加载队列，
  以及 MBTiles/磁盘缓存与网络的读取速度、单瓦片耗时）；面板关闭时不创建定时器也不绘制
- **流畅度评分**：按帧耗时（45%）、帧耗时抖动（20%）与达成帧率（35%，固定 60 FPS 满分线）加权，
  空闲时保留最近一次评分；面板按 ≥90 / ≥70 / 其余分档着色
- **性能快照 API**：`GetPerformanceSnapshot()` 返回 `GlobePerformanceSnapshot`（含 `SmoothnessScore`），
  速率按两次调用的时间差计算，可在不显示面板时自行打日志或绘制自定义 HUD
- **瓦片 IO 统计**：`MbTilesTileSource` / `HttpTileSource` 记录逻辑读取字节数、次数与耗时
  （`Interlocked` 计数），并新增 `TileLoader.QueuedCount` / `InFlightCount` 队列诊断
- **示例工程**：新增「性能面板」按钮与 `--stats-test` 参数（输出含 `score=` 的 `[PERF]` 快照到 demo.log）
- **文档**：新增英文说明 [docs/performance-panel.md](docs/performance-panel.md)

### 修复

- 性能面板空闲超过 1 秒后不再回放上一交互期的帧耗时/刷新率：`GetRenderStats` 将帧统计归零并在面板
  显示 `—`，与归零的 FPS 保持一致（评分保留最近一次测量值）
- 流畅度评分固定以 60 FPS 为满分线，不再用“由帧间隔估算的刷新率”做归一化分母，避免稳定低帧率被误判为满分

### 变更

- 版本号提升为 1.1.0；NuGet 包元数据补齐 `RepositoryUrl` / `PackageProjectUrl`，包页可直接跳转 GitHub 仓库
- **示例密钥改为本地配置**：天地图 tk / 星图地球 token 从 `samples/Globe3D.Demo/keys.local.json` 读取
  （已加入 `.gitignore`，不随仓库发布）；未配置时自动跳过在线底图，README 已补充配置步骤；
  仓库与提交历史中的硬编码密钥已清除

## [1.0.0] - 2026-11-16

首个可发布版本。

### 新增

- **影像瓦片**：Web 墨卡托 XYZ 金字塔，网格由墨卡托参数直接映射到球面；四叉树分层 LOD
  按「相机距离 + 掠射角 + 屏幕投影尺寸」逐瓦片选层级，同一帧近景/地平线更细、远处更粗
- **数据源**：MBTiles（离线 SQLite）、HTTP（在线 + 磁盘缓存）、天地图/星图地球工厂、多源组合；
  失败瓦片冷却重试，祖先纹理兜底不产生空洞
- **场景效果**：程序化星空、大气菲涅尔散射与外发光、地表方向光
- **区域多边形**：球面大圆插值 + 可见半球裁剪 + 近平面/视口裁剪，支持跨 ±180° 经线与极区
- **标记点**：形状图标、海拔引线、球体遮挡自动隐藏
- **图片图标**：png / jpg / jpeg / gif（首帧）/ bmp / webp / svg（内置矢量渲染，无额外依赖），
  支持 `ImageScale` 缩放与 `ImageOffset` 像素偏移；`ImagePath` 懒加载 + LRU 缓存
- **接近提示**：`ShowHoverTip` / `HoverTip` / `HoverRadius`，鼠标靠近时高亮并在鼠标旁弹出提示；
  新增 `MarkerHoverChanged` 事件与 `UpdateHoverMarker()` API
- **命中查询**：`FindNearestMarker`（屏幕坐标 / 经纬度两种）、`GlobePointerMoved` 事件（含最近标记与距离）、
  单击按最近图标命中并触发 `MarkerClicked`
- **示例工程**：`--basemap`、`--hover-test`、`--hit-test`、`--horizon-test`、`--deep-test`、
  `--street-test`、`--pole-test`、`--seam-test`、`--seam-close` 等回归参数
- **NuGet 打包**：包内附带 README / CHANGELOG / LICENSE，生成 `.snupkg` 符号包

### 修复

- 命中测试改为按「当前相机」重新投影并做地平线遮挡剔除：球体背面的标记不再被旧帧缓存误判为可悬停/可点击
- 惯性滑动只在松手瞬间鼠标仍在移动时触发；拖到位置停稳后再松手，地图不再继续滑动
- 悬停光标只接管自身设置的部分，鼠标离开后恢复调用方原有的光标

### 变更

- 图片缓存改为有上限的 LRU（`MarkerImage.MaxCachedImages`，默认 64），长时间运行不会无限增长；
  同一图标被多个标记点复用时只解码一次，加载失败也只尝试一次
- 位图改为按流解码，不再把整文件复制进内存再解码
