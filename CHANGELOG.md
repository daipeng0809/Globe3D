# 更新记录

本文件格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [2.1.0] - 2026-11-17

### 新增

- **矢量图形**：`GlobeLayer.Vectors` 支持圆形（`GlobeCircleVector`）、方形（`GlobeSquareVector`）、
  线条/折线（`GlobePolylineVector`）与多边形（`GlobePolygonVector`）；`Fill = null` 即镂空，
  可自定义边框颜色/宽度/虚线、中心标签与单个图形显隐；与区域共用大圆展开 + 可见半球裁剪，
  平面模式按经度裁剪拆段，跨 ±180° 不横穿地图
- **图层显隐**：`GlobeLayer` 实现 `INotifyPropertyChanged`，修改 `IsVisible` 立即重绘，
  无需手动调用 `Refresh()`
- **测量工具**：`ActiveTool` 提供距离、多边形/圆形/方形面积测量；单击落点、双击/右键完成、
  Esc 取消；新增 `MeasurementCompleted` 事件、`Measurements` 结果列表、`AddMeasurement` 程序化添加、
  `ClearMeasurements` / `CancelMeasurement`
- **比例尺**：`ShowScaleBar` / `ScaleBarPlacement` 在视口角落绘制 1/2/5×10^n 的 m/km 刻度，
  按视口中心真实地表距离换算，三维倾斜与平面模式都随相机变化
- **测地线工具**：`Geodesy` 提供距离、目标点、圆/方形环、球面多边形面积、球冠面积与格式化
- **缩放级别**：`GetZoomLevel()` / `ZoomToLevel()` / `ZoomLevel`（赋值即缩放）按瓦片层级与视口物理像素
  自动换算相机高度；`MinZoomLevel` / `MaxZoomLevel` 约束缩放 API 与滚轮；`ShowZoomLevel` /
  `ZoomLevelPlacement` 在视口角落显示 `Z x.x`；`OrbitCamera.GetZoomLevel` / `GetAltitudeForZoomLevel`
  公开换算公式；示例新增 `--zoom-level-test` 与英文说明 [docs/zoom-level.md](docs/zoom-level.md)
- **示例**：第二行工具栏（比例尺、矢量图层显隐、四种测量、清除）与 `--vector-test` 回归参数；
  新增英文说明 [docs/vector-measure.md](docs/vector-measure.md)

### 修复

- 平面模式跨 ±180° 的折线/多边形：经度裁剪交点钉到 ±180° 边界，修复浮点越界被折回另一侧、
  导致线段横贯整张地图与填充异常的问题（区域与矢量共用该裁剪修正）
- `Geodesy.PolygonArea` 对包住极点的多边形会得到“全球面积 − 实际面积”：改为超过半球时取补集，
  极区测量不再显示接近全球的数值
- 圆形/方形测量在双击结束时残留一个无意义的新圆心草稿点：双击的第二次松手统一抑制
- `AddMeasurement` 不再清除当前鼠标交互草稿，且交互完成时先清空草稿再触发 `MeasurementCompleted`
- `Geodesy` 对非有限输入做防御，目标点推算在极点附近微幅内缩，圆/方形在极点仍能生成有效环；
  `GlobeCircleVector` / `GlobeSquareVector` 缓存经纬度环，避免相机拖动时每帧重算三角函数
- 比例尺在平面模式的地图边缘兜底估算按中心纬度换算，不再退化为与纬度无关的值
- `ActiveTool` 切换不再在属性回调里抢键盘焦点：改为点击地球时获取焦点，避免影响宿主输入控件
- 测量/比例尺数值增加千分位分隔（如 `1,069 km`、`787,941.65 km²`）

### 文档

- 中英文 README 的图标/预览图链接固定到 `v2.1.0`；新增 [docs/vector-measure.md](docs/vector-measure.md) 与
  [docs/zoom-level.md](docs/zoom-level.md) 说明矢量测量与缩放级别的公式、交互和限制

### 变更

- 版本号提升为 2.1.0

## [2.0.2] - 2026-11-17

### 修复

- NuGet README 顶部图标改用 Markdown 图片，避免 NuGet 把 `<img>` HTML 当作转义文本显示
- README 图标/预览图/平面模式对比图改用 jsDelivr CDN，提升 NuGet 与国内网络下的图片可访问性；
  README 顶部新增 128×128 图标 `assets/globe3d-icon-128.png`

### 变更

- 版本号提升为 2.0.2

## [2.0.1] - 2026-11-17

### 修复

- NuGet 包页 README 中的预览图与平面模式对比图改用 `raw.githubusercontent.com` 绝对地址，
  NuGet 页面不再出现破图；说明文档链接也改为 GitHub 绝对地址
- NuGet 发布工作流修复 snupkg 重复推送，并增加 `--skip-duplicate`，避免重跑时 409

### 新增

- 新增 512×512 包图标 `assets/globe3d-icon.png`（附 SVG 源文件），通过 `PackageIcon` 随包发布，
  同时显示在 README 顶部

### 变更

- 版本号提升为 2.0.1

## [2.0.0] - 2026-11-17

### 变更（破坏性）

- 移除 `OnlineMapProvider` 及 `OnlineTileSources` 的天地图、星图地球专用方法；`OnlineTileSources`
  只保留通用 `Create(urlTemplate, ...)`，服务地址、key 与多 key 轮换由调用方自行实现（示例自行拼装 URL）
- 版本号提升为 2.0.0：本次为公开 API 破坏性变更，在线源需从专用方法迁移到通用 URL 模板

### 新增

- **坐标系检测**：`TileSourceInfo.CoordinateSystem`；`HttpTileSource` 按 URL/名称检测常见服务商，
  `MbTilesTileSource` 读取 metadata 的 crs/srs，`CompositeTileSource` 混合时报告 `Unknown`
- **坐标转换**：`CoordinateConverter` 支持 WGS84、Web 墨卡托（EPSG:3857）、GCJ-02、BD-09
  之间的转换，境外不应用 GCJ/BD 偏移
- **示例**：`--coordinate-test` 输出转换往返误差与 URL 检测结果；新增英文说明
  [docs/coordinate-systems.md](docs/coordinate-systems.md)

### 文档

- 新增英文 `README.md` 作为 GitHub 与 NuGet 的默认展示文档，中文版本移至
  [README.zh-CN.md](README.zh-CN.md)
- 补充平面模式 Heading/Pitch 行为说明与 3D/平面同角度对比截图（[docs/flat-mode.md](docs/flat-mode.md)、README）
- 修正平面模式文档中“仅投影/输入变化”的描述：实际还会切换渲染通道（隐藏星空/大气/光照）
- 示例新增 `--orientation-test`，验证平面模式不应用 Heading/Pitch、切回三维后数值仍保留

## [1.1.1] - 2026-11-17

### 新增

- **平面模式**：`IsFlatMode` 开关在二维 Web 墨卡托平面与三维地球之间切换；平面模式下复用瓦片 LOD、
  标记/区域/标注与悬停命中，左键平移、滚轮缩放、双击飞近，右键倾斜与星空/大气自动关闭；
  瓦片用独立顶点着色器按屏幕 NDC 矩形铺开，跨 ±180° 区域拆到地图两侧；示例新增「平面模式」按钮、
  `--flat` 与 `--flat-toggle-test` 回归参数
- **文档**：新增英文说明 [docs/flat-mode.md](docs/flat-mode.md)

### 修复

- 平面模式切换/缩放/平移后，把被视口限制的中心经纬度同步回控件属性，状态栏与绑定不再显示旧值
- 平面模式比例尺改为只由高度与视场角决定：修复纵向平移时比例尺变化导致的拖拽漂移与瓦片 LOD 抖动
- 平面模式滚轮改为以光标为锚点缩放，并增加视口外标记/标注剔除
- 二维/三维切换时清除悬停状态；平面模式标记命中改用经纬度直接投影，修复 ±180° 端点与屏幕外标记误判

### 变更

- Avalonia 依赖由 12.1.6 降至 12.1.1（库与示例统一），包依赖声明同步为 12.1.1，
  Avalonia 12.1.1 及以上版本的项目可直接引用；已用 12.1.1 编译并验证三维/平面模式

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
