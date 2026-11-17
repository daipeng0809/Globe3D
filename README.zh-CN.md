![Globe3D.Avalonia 图标](https://cdn.jsdelivr.net/gh/daipeng0809/Globe3D@v3.0.0/assets/globe3d-icon-128.png)

# Globe3D.Avalonia

简体中文 | [English](https://github.com/daipeng0809/Globe3D/blob/master/README.md)

[![NuGet](https://img.shields.io/nuget/v/Globe3D.Avalonia.svg)](https://www.nuget.org/packages/Globe3D.Avalonia)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Globe3D.Avalonia.svg)](https://www.nuget.org/packages/Globe3D.Avalonia)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-net8.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/Avalonia-12.1.1-8B4FBF.svg)](https://avaloniaui.net/)

面向 Avalonia 12 的实时三维地球控件。基于 OpenGL ES 3.0 渲染影像瓦片、程序化星空与大气辉光，
并在其上叠加三维标记点（支持 png/jpg/gif/svg 图标）、区域多边形与文字标注，
内置瓦片分层 LOD 与完整的鼠标交互。

![预览](https://cdn.jsdelivr.net/gh/daipeng0809/Globe3D@v3.0.0/docs/screenshot.png)

## 目录

- [特性](#特性)
- [环境与兼容性](#环境与兼容性)
- [安装](#安装)
- [快速开始](#快速开始)
  - [最简用法](#最简用法)
  - [添加标记、区域与标注](#添加标记区域与标注)
  - [矢量图形与测量工具](#矢量图形与测量工具)
  - [图片图标标记](#图片图标标记)
  - [相机控制](#相机控制)
  - [缩放级别](#缩放级别)
  - [事件与命中查询](#事件与命中查询)
  - [性能面板](#性能面板)
  - [平面模式](#平面模式)
- [数据源](#数据源)
- [交互](#交互)
- [主要属性](#主要属性)
- [技术说明](#技术说明)
- [已知限制](#已知限制)
- [示例工程](#示例工程)
- [Native AOT 与裁剪](#native-aot-与裁剪)
- [打包与发布](#打包与发布)
- [版本记录](#版本记录)
- [许可](#许可)

## 特性

| 能力 | 说明                                                                                                           |
| --- |----------------------------------------------------------------------------------------------------------------|
| 影像瓦片 | Web 墨卡托 XYZ 金字塔，网格直接由墨卡托参数映射到球面，无接缝                                                  |
| 分层 LOD | 四叉树按瓦片距离 + 掠射角 + 屏幕投影尺寸选择层级，同一帧内近景/地平线用高层级、远处用低层级                    |
| 数据源 | MBTiles（离线 SQLite）、HTTP 通用 URL 模板（在线 + 磁盘缓存 + 坐标系检测）、多源组合 |
| 稀疏回退 | 某层级瓦片缺失时自动沿祖先链回退，区域外不会出现黑洞                                                           |
| 星空 | 程序化生成（三层哈希星点 + 银河带），随相机旋转，零图片资源                                                    |
| 大气 | 菲涅尔边缘散射 + 外发光环，颜色与强度可调                                                                      |
| 三维标记 | 经纬度 + 海拔，自动处理球体遮挡，可绘制高度引线                                                                |
| 图片图标 | png / jpg / gif / bmp / webp / svg 直接作为图标，可单独设置缩放与像素偏移                                      |
| 接近提示 | 鼠标靠近标记点时高亮并在鼠标旁弹出提示，可按标记点关闭                                                         |
| 区域 | 经纬度顶点自动做球面细分，支持填充与描边                                                                       |
| 矢量图形 | 圆形、方形、线条/折线、多边形，支持镂空、边框颜色/宽度/虚线，图层可整体显隐                                     |
| 测量工具 | 比例尺；距离测量与多边形/圆形/方形面积测量，自动在图上标注长度与面积                                           |
| 文字标注 | 使用 Avalonia 矢量绘制，中文清晰，自动防重叠避让                                                               |
| 交互 | 左键旋转、右键倾斜、滚轮缩放、双击飞行、标记/地表拾取                                                          |
| 渲染调度 | 按需渲染，静止时 CPU/GPU 占用归零                                                                              |
| 平面模式 | `IsFlatMode` 在三维地球与二维 Web 墨卡托平面间切换，支持平移/缩放、瓦片 LOD、标记/区域/标注                    |
| 缩放级别 | `GetZoomLevel` / `ZoomToLevel` / `SetView` 按瓦片层级自动换算镜头高度，支持中心+级别原子定位、最小/最大级别限制与屏幕显示 |
| 性能面板 | 一键开启的性能 HUD：0-100 流畅度评分、帧率/刷新率/帧耗时、瓦片与加载队列、MBTiles/磁盘缓存与网络的实时读取速度 |

## 环境与兼容性

| 项目 | 说明 |
| --- | --- |
| 目标框架 | `net8.0` |
| 依赖 | Avalonia 12.1.1（最低依赖，兼容任意 Avalonia 12.1.1+ 项目）、Silk.NET.OpenGLES 2.22.0、Microsoft.Data.Sqlite 8.0.11 |
| 渲染后端 | OpenGL ES 3.0（`OpenGlControlBase`）。Windows 由 ANGLE 提供（底层 D3D11），无需额外运行时 |
| 平台 | Windows / Linux 桌面；Linux 与国产化平台需要系统加载器提供 GLES 3.0 驱动 |
| 线程模型 | 控件与叠加层绘制在 UI 线程，瓦片下载/解码在后台线程；图标解码可跨线程调用 |
| Native AOT | 库开启 AOT/裁剪分析（`IsAotCompatible`，零告警）；示例以 Native AOT 发布并验证通过，见 [Native AOT 与裁剪](#native-aot-与裁剪) |

> **3.0.0 为破坏性更新**：`Pitch` 改为中心点处的真实视线俯仰角（旧值是轨道偏移角），
> `IsFlatMode` 默认保持显示级别。需要旧的“保持 Altitude”行为时使用
> `SetFlatMode(true, preserveZoomLevel: false)`。

## 安装

```bash
dotnet add package Globe3D.Avalonia --version 3.0.0
```

或直接在工程中引用：

```xml
<PackageReference Include="Globe3D.Avalonia" Version="3.0.0" />
```

XAML 命名空间：

```xml
<Window xmlns:g="clr-namespace:Globe3D.Avalonia;assembly=Globe3D.Avalonia">
```

## 快速开始

### 最简用法

```xml
<g:GlobeView MbTilesPath="D:\maps\satellite.mbtiles" />
```

### 添加标记、区域与标注

```csharp
// 普通标记
var cityLayer = new GlobeLayer("城市");
cityLayer.Markers.Add(new GlobeMarker
{
    Position = new GeoPoint(39.9042, 116.4074),   // 纬度, 经度
    Label = "北京",
    Shape = MarkerShape.Pin,
    Fill = Brushes.OrangeRed
});

// 三维标记点：经度、纬度、高度（米）
cityLayer.Markers.Add(new GlobeMarker
{
    Position = new GeoPoint(35d, 118d, 400_000d),
    Label = "高空目标 400km",
    Shape = MarkerShape.Triangle,
    ShowAltitudeLine = true
});
globe.Layers.Add(cityLayer);

// 区域多边形
var region = new GlobeRegion
{
    Label = "示例区域",
    Fill = new SolidColorBrush(Color.Parse("#3348A0FF")),
    Stroke = Brushes.SkyBlue,
    StrokeThickness = 2
};
region.AddPoint(28, 100).AddPoint(34, 98).AddPoint(36, 110).AddPoint(31, 114);

var regionLayer = new GlobeLayer("区域");
regionLayer.Regions.Add(region);
globe.Layers.Add(regionLayer);

// 纯文字标注
var labelLayer = new GlobeLayer("标注");
labelLayer.Labels.Add(new GlobeLabel
{
    Position = new GeoPoint(42d, 103d),
    Text = "内蒙古高原",
    Foreground = Brushes.Khaki
});
globe.Layers.Add(labelLayer);
```

### 矢量图形与测量工具

`GlobeLayer.Vectors` 用于放矢量图形，`GlobeLayer.IsVisible` 控制整层显示/隐藏（修改后自动重绘）：

```csharp
var vectorLayer = new GlobeLayer("矢量图形");

// 圆形（圆心 + 半径），半透明填充
vectorLayer.Vectors.Add(new GlobeCircleVector
{
    Center = new GeoPoint(39.9042, 116.4074),
    RadiusMeters = 260_000,
    Fill = new SolidColorBrush(Color.Parse("#2E5AC8FF")),
    Stroke = Brushes.DeepSkyBlue,
    StrokeThickness = 2,
    Label = "圆形 · 260 km"
});

// 方形（中心 + 边长 + 旋转角）；Fill = null 即镂空，只画边框
vectorLayer.Vectors.Add(new GlobeSquareVector
{
    Center = new GeoPoint(31.2304, 121.4737),
    SideMeters = 300_000,
    Heading = 20,
    Fill = null,
    Stroke = Brushes.Orange,
    DashStyle = DashStyle.Dash,
    Label = "方形（镂空）"
});

// 多边形（闭合）；折线/线条用 GlobePolylineVector（Closed = false）
var polygon = new GlobePolygonVector
{
    Fill = new SolidColorBrush(Color.Parse("#3381C784")),
    Stroke = Brushes.MediumSeaGreen
};
polygon.AddPoint(28, 100).AddPoint(34, 98).AddPoint(36, 109);
vectorLayer.Vectors.Add(polygon);

var line = new GlobePolylineVector { Stroke = Brushes.Khaki, DashStyle = DashStyle.Dot };
line.AddPoint(23.13, 113.26).AddPoint(30.57, 104.07).AddPoint(34.34, 108.94);
vectorLayer.Vectors.Add(line);

globe.Layers.Add(vectorLayer);
vectorLayer.IsVisible = false;   // 整层隐藏，立即生效；改回 true 恢复
```

修改 `GlobeLayer.IsVisible` 会立即重绘，无需手动调用 `Refresh()`；但修改单个图形的颜色、半径、
顶点等属性后，需要调用一次 `globe.Refresh()`（控件按需渲染，静止时不会自动重画）。
也可以继承 `GlobeVector` 并重写 `BuildRing()` / `IsClosedRing` 实现自定义形状，
大圆插值、地平线裁剪与投影由控件统一处理。

| 矢量图形属性 | 说明 |
| --- | --- |
| `GlobeCircleVector` | `Center` + `RadiusMeters`，按球面大圆生成边界 |
| `GlobeSquareVector` | `Center` + `SideMeters` + `Heading`（相对正北的旋转角） |
| `GlobePolylineVector` / `GlobePolygonVector` | `Points` 顶点列表；`Closed=false` 为线条/折线，`true` 为多边形 |
| `Fill` | 填充画刷；`null` 表示镂空 |
| `Stroke` / `StrokeThickness` / `DashStyle` | 边框颜色、宽度与虚线样式 |
| `Label` / `LabelForeground` / `LabelFontSize` | 图形中心标签 |
| `IsVisible` | 单个图形的显隐 |

比例尺与测量工具：

```csharp
globe.ShowScaleBar = true;                                 // 视口角落显示比例尺
globe.ScaleBarPlacement = GlobeScaleBarPlacement.BottomRight;

globe.ActiveTool = GlobeTool.Distance;                     // 距离：单击落点，双击/右键完成
// GlobeTool.AreaPolygon / AreaCircle / AreaSquare 为多边形、圆形、方形面积
// 圆形/方形：第一次单击定中心，移动后第二次单击定半径/边界点

globe.MeasurementCompleted += (_, e) =>
    Console.WriteLine($"{e.Measurement.Tool}: {e.Measurement.Text}");

globe.ClearMeasurements();                                 // 清除全部测量结果
globe.CancelMeasurement();                                 // 取消当前草稿（Esc 同样生效，需控件持有键盘焦点）

// 也可以不经过鼠标交互，直接追加测量结果；不会清除当前交互草稿
var m = globe.AddMeasurement(GlobeTool.Distance,
    new GeoPoint(39.9042, 116.4074), new GeoPoint(31.2304, 121.4737));
```

`Geodesy` 提供同一套球面计算（`Distance` / `Destination` / `CircleRing` / `SquareRing` /
`PolygonArea` / `CircleArea` / `FormatDistance` / `FormatArea`），可用于自定义标注或核对结果。
面积按较小一侧报告：包住极点或超过半球的多边形会取补集（结果不大于 2πR²）。

### 图片图标标记

任何 Avalonia `IImage` 都能当作标记图标；也可以只给文件路径，控件会按需加载并缓存。
支持 png / jpg / jpeg / gif（取首帧）/ bmp / webp，以及 **svg**（内置矢量渲染，缩放不模糊）。

```csharp
var layer = new GlobeLayer("图片标记");

// 1) 直接给文件，缩放 0.5、向上偏移 26px（图钉尖端对准坐标点）
layer.Markers.Add(new GlobeMarker
{
    Position = new GeoPoint(39.9042, 116.4074),
    ImagePath = @"Assets\pin.png",
    ImageScale = 0.5,
    ImageOffset = new Vector(0, -26),
    HoverTip = "北京 · 机场"        // 可选：鼠标靠近时显示的内容
});

// 2) SVG 图标（矢量，任意缩放清晰）
layer.Markers.Add(new GlobeMarker
{
    Position = new GeoPoint(31.2304, 121.4737),
    ImagePath = @"Assets\radar.svg",
    ImageScale = 1.3,
    Tag = "上海雷达"
});

// 3) 自己解码好的 IImage（Bitmap / RenderTargetBitmap / 任意第三方 IImage）
layer.Markers.Add(new GlobeMarker
{
    Position = new GeoPoint(23.1291, 113.2644),
    Image = new Bitmap(@"Assets\target.jpg"),
    ImageScale = 0.46
});

globe.Layers.Add(layer);

// 也可以手动加载（同一文件被多个标记点复用时只会解码一次，缓存最多 64 个图标）
IImage icon = MarkerImage.Load(@"Assets\dot.gif");
IImage svg = MarkerImage.FromSvg("<svg viewBox='0 0 24 24'>...</svg>");
```

| 标记点属性 | 说明 |
| --- | --- |
| `Image` / `ImagePath` | 图标来源，同时设置时以 `Image` 为准；加载失败时 `ImageError` 给出原因，并回退到底色图形 |
| `ImageScale` | 相对图像自身像素尺寸的缩放倍数，默认 1 |
| `ImageOffset` | 相对锚点的像素偏移（正值向右/向下），常用来把图钉尖端对准坐标 |
| `ShowHoverTip` | 是否参与接近提示，默认 true；设为 false 后靠近时既不高亮也不弹提示 |
| `HoverTip` | 接近时显示的内容，为空时依次回退到 `Label`、`ToolTip` |
| `HoverRadius` | 该标记点单独的接近判定半径（像素），不大于 0 时用控件的 `MarkerHoverRadius` |

### 相机控制

```csharp
globe.JumpTo(31.23, 121.47, 1_200_000);            // 立即定位（按高度）
globe.FlyTo(31.23, 121.47, 260_000,
    TimeSpan.FromMilliseconds(1200));               // 平滑飞行（按高度）
globe.SetView(31.23, 121.47, 12);                   // 原子设置中心 + 缩放级别
globe.JumpToLevel(31.23, 121.47, 12);               // SetView 的别名

globe.CenterLatitude = 33;                          // 也可直接设属性
globe.CenterLongitude = 108;                        // 请求中心（平面模式可被视口夹取）
globe.Altitude = 9_500_000;                         // 离地高度（米）
globe.Pitch = 25;                                   // 视线俯仰角 0-88 度
double effectivePitch = globe.GetEffectivePitch();  // 实际视线俯仰角（平面模式为 0）
double actualLat = globe.EffectiveCenterLatitude;   // 实际绘制中心（平面边缘可能不同）
double actualLon = globe.EffectiveCenterLongitude;
```

### 缩放级别

```csharp
double level = globe.GetZoomLevel();                        // 当前缩放级别（约等于 256px 瓦片层级）
globe.ZoomToLevel(8);                                       // 按瓦片层级自动计算镜头高度
globe.ZoomToLevel(9, TimeSpan.FromMilliseconds(400));       // 平滑缩放
globe.ZoomLevel = 8;                                        // 等价写法

globe.MinZoomLevel = 3;                                     // 约束 ZoomToLevel / ZoomLevel / 滚轮
globe.MaxZoomLevel = 15;

globe.ShowZoomLevel = true;                                 // 视口角落显示 Z 8.4
globe.ZoomLevelPlacement = GlobeScaleBarPlacement.BottomRight;
```

- 三维模式按当前俯仰角、中心纬度与视口物理像素高度迭代求解镜头高度，使设定级别与实际显示级别一致；
  平面模式为解析换算。`Pitch` 是在中心点处量取的真实视线俯仰角：0 垂直向下、越大越接近水平，
  相机按当前高度反解轨道偏移，低空时中心点仍在地平线以内。
- `SetView(纬度, 经度, 级别)`（别名 `JumpToLevel`）原子设置中心与级别：按目标中心、当前俯仰角
  与物理像素视口一次性求解高度，避免“先设中心、后设级别”被中间状态夹取；布局前调用会一并暂存。
- `MinZoomLevel` / `MaxZoomLevel` 与 `MinAltitude` / `MaxAltitude` 同时生效，更严格者优先；
  直接设置 `Altitude` / `JumpTo` / `FlyTo` 仍只受高度限制。
- `ZoomLevel` 是实时计算值，不参与 Avalonia 属性变更通知；实时显示用 `ShowZoomLevel`，监听变化用 `CameraChanged`。
- 控件尚未完成布局时调用 `ZoomToLevel` 会暂存，首次渲染（GL 已测出物理像素尺寸）后自动应用。

详细公式与限制说明见 [docs/zoom-level.md](docs/zoom-level.md)（英文）。

### 事件与命中查询

```csharp
globe.GlobeClicked += (_, e) =>
{
    if (e.Location is { } p)
        Console.WriteLine($"纬度 {p.Latitude}, 经度 {p.Longitude}");
};

// 鼠标移动：携带屏幕坐标、地表经纬度、最近的标记点及其像素距离
globe.GlobePointerMoved += (_, e) =>
{
    if (e.NearestMarker is { } nearest && e.NearestDistance <= globe.MarkerHoverRadius)
        ShowPopupNearMouse(e.ScreenPosition, nearest.Tag);
};

// 接近状态变化（进入/离开标记点，离开时 Marker 为 null）
globe.MarkerHoverChanged += (_, e) => Console.WriteLine(e.Marker?.Tag ?? "离开标记点");

// 点击最近标记点：单击时按“离图标最近”命中，容忍点偏几个像素
globe.MarkerClicked += (_, e) => Console.WriteLine(e.Marker.Label);

// 主动查询：屏幕最近 / 经纬度最近
globe.FindNearestMarker(new Point(x, y), out var pixels);   // 不限距离，返回最近标记与距离
globe.FindNearestMarker(new Point(x, y), maxDistance: 24); // 限定命中范围
globe.FindNearestMarker(31.23, 121.47);                    // 按地表大圆距离

// 由外部输入转发或自动化测试驱动接近提示
globe.UpdateHoverMarker(new Point(x, y));
globe.UpdateHoverMarker(null);                              // 收起

globe.RenderingError += (_, message) => Console.WriteLine(message);
```

> 命中测试每次都用**当前相机**重新投影并做地平线遮挡剔除，球体背面的标记点不会被误判为可悬停/可点击，
> 也不依赖上一帧的屏幕坐标缓存。

### 性能面板

```csharp
globe.ShowPerformancePanel = true;   // 左上角显示评分 / 帧率 / 瓦片与 IO 速度

var stats = globe.GetPerformanceSnapshot();   // 也可以自己取数打日志或画 HUD
Console.WriteLine($"{stats.FramesPerSecond:0.0} fps, score {stats.SmoothnessScore}, " +
                  $"{stats.DiskReadBytesPerSecond:0} B/s");
```

面板包含 0-100 流畅度评分（帧耗时 45% + 抖动 20% + 达成帧率 35%，以 60 FPS 为满分线；
绿色 ≥90、黄色 ≥70、红色 <70）、帧率、刷新率、帧耗时、绘制瓦片/纹理数、LOD 层级与选瓦片耗时、
加载队列，以及 MBTiles / 磁盘缓存 / 网络的瓦片读取速度与单瓦片耗时。空闲超过 1 秒后帧耗时/刷新率
显示 `—`（不再回放旧样本），评分保留最近一次测量值。
详细指标含义、采样方式与局限见 [docs/performance-panel.md](docs/performance-panel.md)（英文）。

### 平面模式

```csharp
globe.IsFlatMode = true;   // 二维 Web 墨卡托平面（保持当前缩放级别）
globe.IsFlatMode = false;  // 恢复三维地球（保持当前缩放级别）
globe.SetFlatMode(true);   // 等价写法；传 preserveZoomLevel: false 则保持 Altitude 不变
```

平面模式复用同一套瓦片/LOD/图层能力：

- 左键拖动平移地图，滚轮缩放（复用 `Altitude`），双击飞近；右键拖拽倾斜不生效
- 不绘制星空、大气与地表光照，标记高度引线隐藏，标记按经纬度落在平面上
- 区域跨 ±180° 经线时会拆到地图左右两侧；地图在 ±180° 边缘停止，不做横向循环滚动
- 投影切换默认保持显示级别，并取消进行中的飞行/惯性动画；
  平面视口的中心夹取只影响实际投影，不回写 `CenterLatitude` / `CenterLongitude`（夹取结果仅在用户拖拽/滚轮缩放/自动旋转后回写）；
  缩出去再缩回来仍能回到调用方请求的中心，实际绘制中心用 `EffectiveCenterLatitude/Longitude` 读取；
  状态栏示例已改用实际绘制中心。自动旋转与惯性同样在平面边界停止
- `Pitch` / `Heading` 在平面模式下不参与投影：地图保持正北向上、不做旋转/倾斜；两者数值保留，切回三维后生效
- 英文说明与 3D/平面同角度对比截图见 [docs/flat-mode.md](docs/flat-mode.md)

![3D/平面模式 Heading/Pitch 对比](https://cdn.jsdelivr.net/gh/daipeng0809/Globe3D@v3.0.0/docs/flat-mode-heading-pitch.png)

## 数据源

### MBTiles（离线）

```csharp
globe.MbTilesPath = @"D:\DMap\mapabc\data\satellite.mbtiles";
```

要求：标准 MBTiles，`tiles` 表为 TMS 行序（自南向北），`tile_data` 为 PNG/JPEG。
若数据实际使用 XYZ 行序，可显式构造：

```csharp
globe.TileSource = new MbTilesTileSource(path, flipY: false);
```

### 在线瓦片（通用 URL 模板）

库不提供任何服务商专用工厂：`OnlineTileSources` 只保留一个通用的 `Create(urlTemplate, ...)`，
URL、key 轮换等由调用方自行拼装（也可直接 `new HttpTileSource(...)`）。支持 `{z}`、`{x}`、`{y}`、`{-y}` 占位符，自动按
Content-Type 过滤非图片响应并走磁盘缓存。

```csharp
// 星图地球（示例；JPEG 跨平台可直接解码）
globe.TileSource = OnlineTileSources.Create(
    "https://api.open.geovisearth.com/pj/base/v1/2025/{z}/{x}/{y}?format=jpeg&tmsIds=w&token=你的token",
    cacheDirectory: @"C:\TileCache\geovis");

// 天地图（WMTS，img_w 为 Web 墨卡托）
globe.TileSource = OnlineTileSources.Create(
    "https://t0.tianditu.gov.cn/img_w/wmts?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0" +
    "&LAYER=img&STYLE=default&TILEMATRIXSET=w&FORMAT=tiles" +
    "&TILEMATRIX={z}&TILEROW={y}&TILECOL={x}&tk=你的tk",
    cacheDirectory: @"C:\TileCache\tianditu");
```

### 坐标系检测与转换

`TileSourceInfo.CoordinateSystem` 报告数据坐标系；`HttpTileSource` 未显式指定时会根据 URL/名称检测：

- 高德/腾讯（`amap` / `autonavi` / `gtimg`）→ `Gcj02`
- 百度（`bdimg` / `baidu`）→ `Bd09`
- 天地图、星图地球、OSM 及其他标准源 → `Wgs84`

检测是启发式的，可以用构造参数显式覆盖：

```csharp
var source = new HttpTileSource(url, coordinateSystem: CoordinateSystem.Wgs84);
```

MBTiles 会读取 metadata 中的 `crs` / `srs` 字段，也可以显式指定：

```csharp
new MbTilesTileSource(path, coordinateSystem: CoordinateSystem.Gcj02);
```

常见坐标系转 WGS84 / 互转：

```csharp
var wgs = CoordinateConverter.ToWgs84(new GeoPoint(39.907, 116.397), CoordinateSystem.Gcj02);
var (x, y) = CoordinateConverter.Wgs84ToWebMercator(39.9042, 116.4074);
var back = CoordinateConverter.WebMercatorToWgs84(x, y);
```

支持 WGS84、Web 墨卡托（EPSG:3857）、GCJ-02、BD-09；GCJ/BD 境外不偏移。Web 墨卡托是米制投影，
请使用 `WebMercatorToWgs84` / `Wgs84ToWebMercator`，不要当作经纬度传入。

> 控件始终按 WGS84 / Web 墨卡托瓦片索引渲染；`CoordinateSystem` 不会自动扭曲 GCJ-02/BD-09 影像。
> 使用这些源时需要用转换工具处理数据/瓦片，或选择 WGS84 影像。
> 详细说明见 [docs/coordinate-systems.md](docs/coordinate-systems.md)（英文）。

### 多源组合（本地优先，在线兜底）

```csharp
globe.TileSource = new CompositeTileSource(
    new MbTilesTileSource(@"D:\maps\china.mbtiles"),
    new HttpTileSource("https://example.com/{z}/{x}/{y}.png", @"C:\TileCache"));
```

## 交互

| 操作 | 行为 |
| --- | --- |
| 左键拖拽 | 旋转地球；松手瞬间仍在移动时带惯性，拖到位置停稳后再松手不滑动 |
| 右键拖拽 | 倾斜与航向 |
| 滚轮 | 缩放（受 `MinAltitude` / `MaxAltitude` 与 `MinZoomLevel` / `MaxZoomLevel` 约束） |
| 双击 | 飞行到点击位置 |
| 单击 | 拾取最近的标记点，或返回地表经纬度 |
| 测量工具 | 左键单击落点；距离/多边形双击或右键完成；圆形/方形第二次单击完成；Esc 取消草稿（需控件持有键盘焦点，点击地球后获得） |
| 比例尺 | `ShowScaleBar` 开启后按当前相机自动换算为 1/2/5×10^n 的整齐刻度（m / km） |
| 鼠标悬停 | 靠近开启提示的标记点时高亮并在鼠标旁显示内容 |
| 平面模式 | 左键拖动平移、滚轮缩放、双击飞近；右键倾斜不生效 |

## 主要属性

| 属性 | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `CenterLatitude` | double | 32 | 观察中心纬度 |
| `CenterLongitude` | double | 108 | 观察中心经度 |
| `Altitude` | double | 10 200 000 | 相机离地高度（米） |
| `Pitch` | double | 0 | 视线俯仰角，0-88 度（仅三维；平面模式保留数值） |
| `Heading` | double | 0 | 航向角 |
| `MinAltitude` / `MaxAltitude` | double | 10 000 / 51 000 000 | 缩放上下限 |
| `MbTilesPath` | string? | null | MBTiles 路径，自动创建数据源 |
| `TileSource` | ITileSource? | null | 自定义瓦片源 |
| `ShowStars` / `StarIntensity` | bool / double | true / 1.0 | 星空开关与亮度 |
| `AtmosphereColor` / `AtmosphereIntensity` | Color / double | #5AA8FF / 0.85 | 大气颜色与强度 |
| `LightingIntensity` | double | 0.28 | 地表方向光明暗 |
| `ShowLabels` | bool | true | 文字标注开关 |
| `MarkerHoverRadius` | double | 18 | 鼠标接近标记点的默认判定半径（像素） |
| `MarkerHitTolerance` | double | 6 | 单击命中标记点的额外容忍距离（像素） |
| `AutoRotate` / `AutoRotateSpeed` | bool / double | false / 6 | 自动旋转 |
| `IsFlatMode` | bool | false | 平面模式（二维 Web 墨卡托地图） |
| `ZoomLevel` | double | 计算值 | 当前缩放级别，赋值即调用 `ZoomToLevel` |
| `MinZoomLevel` / `MaxZoomLevel` | double | 0 / 24 | 缩放级别上下限（约束缩放接口与滚轮） |
| `ShowZoomLevel` / `ZoomLevelPlacement` | bool / GlobeScaleBarPlacement | false / BottomRight | 缩放级别显示与位置 |
| `ShowScaleBar` / `ScaleBarPlacement` | bool / GlobeScaleBarPlacement | false / BottomLeft | 比例尺开关与位置 |
| `ActiveTool` | GlobeTool | None | 测量工具：距离、多边形/圆形/方形面积 |
| `Measurements` | IReadOnlyList&lt;GlobeMeasurement&gt; | 空 | 已完成的测量结果 |
| `ShowPerformancePanel` | bool | false | 显示性能面板（帧率、刷新率、瓦片与 IO 统计） |
| `Layers` | AvaloniaList&lt;GlobeLayer&gt; | 空 | 图层集合，XAML 默认内容 |

## 技术说明

- 渲染后端：`Avalonia.OpenGL.Controls.OpenGlControlBase` + `Silk.NET.OpenGLES`
- Windows 下由 ANGLE 提供 OpenGL ES 3.0（底层走 D3D11），无需额外运行时
- 瓦片网格用 `vec2` 参数化（9×9 顶点），顶点着色器完成墨卡托 → 球面映射，
  所有瓦片共享同一 VAO，每个瓦片只需更新 4 个 uniform
- 分层 LOD：以数据源最低层级为根做四叉树遍历，每个瓦片按「相机距离 + 掠射角」估计
  所需层级，再叠加「投影尺寸超过一个纹理边长」的屏幕空间误差；单帧叶子数超预算时
  把同层同父兄弟合并回父瓦片（不要求四片齐全，只降精度、不留空洞）。`GetDrawnZoomRange()` /
  `GetDrawnLevelHistogram()` 可查看当前帧的层级分布
- 瓦片加载：请求带优先级与「最近需要帧号」，相机移开后残留请求会被丢弃；失败瓦片在
  冷却期过后自动重试；祖先纹理只作兜底显示，不会阻止更细瓦片的请求
- 纹理上传：GLES 3.0 核心不支持 `GL_BGRA`，解码后统一转换为 RGBA 通道顺序
- 区域填充：顶点在三维球面上按大圆展开，填充前先裁剪到相机可见半球，
  跨 ±180° 经线与越过地平线的区域都能正确闭合，屏幕空间再做近平面与视口裁剪
- 矢量与测量：与区域共用同一条绘制通道（大圆展开 + 可见半球裁剪 + 近平面/视口裁剪）；
  开放折线只连可见点，平面模式按经度裁剪拆段并把交点钉在 ±180° 边界上，避免浮点越界折回另一侧；
  面积用球面环面积公式（经度差按最短路径展开，包住极点的环按较小一侧归一），
  圆形面积按球冠公式计算；比例尺用视口中心附近两点的真实地表距离估算米/像素，
  因此三维倾斜视角与平面模式的刻度都随相机变化
- 极区：墨卡托纬度截断在 ±85.05°，最北/最南一行把到极点的空隙摊到整行几何上，
  正对极点俯视时不会露出没有几何的圆盘状空洞
- 文字与图标由 Avalonia 矢量绘制叠加在 GL 表面之上，中文渲染质量高且可避让
- 图片图标按路径懒加载并缓存（有容量上限的 LRU，默认 64；文件时间/大小变化后自动重读）；
  SVG 由内置的 `SvgMarkerImage` 解析成 Avalonia `Geometry` 后**每帧矢量绘制**，
  缩放时不需要重新栅格化，也不引入 Skia/Svg 等额外依赖，便于国产化平台部署
- 命中测试用「当前相机」重新投影 + 地平线遮挡剔除，再按图标实际尺寸算包围盒：
  鼠标接近按「到包围盒的距离」判定，单击按「最近的一个」命中，层级靠后的标记点优先
- 惯性：`PointerReleased` 时若指针已静止超过 120 ms，则视为停稳松手，不触发惯性滑动
- 平面模式：`OrbitCamera.IsFlat` 驱动 Web 墨卡托平面投影；瓦片用独立顶点着色器按屏幕 NDC 矩形铺开，
  层级由「世界像素宽度 / 瓦片边长」估算；瓦片采样与区域/标记投影直接使用经纬度，避免 ±180° 端点
  经过三维向量往返时被浮点误差翻到另一侧；区域按经度裁剪拆分绘制
- 坐标系：`TileSourceInfo.CoordinateSystem` 记录源坐标系；`HttpTileSource` 按 URL/名称检测常见服务商，
  `MbTilesTileSource` 读取 metadata 的 crs/srs，`CompositeTileSource` 混合时报告 `Unknown`；
  `CoordinateConverter` 支持 WGS84 / Web 墨卡托 / GCJ-02 / BD-09 互转（GCJ/BD 境外不偏移）
- 性能统计：帧耗时按最近 120 帧环形缓冲统计；空闲超过 1 秒后帧耗时/刷新率归零（面板显示 `—`），
  评分以固定 60 FPS 为满分线，避免用帧间隔估算的刷新率做分母造成循环归一化；瓦片 IO 由
  `MbTilesTileSource` / `HttpTileSource` 用 `Interlocked` 计数（逻辑读取量）；面板每 500 ms 采样一次，
  关闭时不创建定时器也不绘制；`GetPerformanceSnapshot()` 可在不显示面板时自行取数
- 依赖：Avalonia 12.1.1（包依赖声明为 12.1.1，Avalonia 12.1.1+ 项目可直接引用）、Silk.NET.OpenGLES 2.22.0、Microsoft.Data.Sqlite 8.0.11

## 已知限制

- 区域跨越 ±180° 经线时按「最短路径」解释边：若起点到终点的经度差超过 180°，
  实际连的是另一侧；需要绘制超宽区域时请在 ±180° 处拆分顶点
- 主流瓦片服务在 ±85° 以上没有影像，极冠由最边一行的纹理拉伸填充，放大后会看出拉伸痕迹
- 需要可用的 OpenGL ES 3.0 环境，暂未提供软件渲染回退
- GIF 只取首帧，不做动画播放
- 内置 SVG 渲染支持 `svg / g / rect / circle / ellipse / line / polyline / polygon / path`
  与 `fill / stroke / stroke-width / opacity / transform`，渐变只取首个色标近似；
  滤镜、文本、外部引用等特性请自行解码为 `IImage` 后赋给 `Image`
- 图标缓存最多保留 64 个文件（`MarkerImage.MaxCachedImages`），已移除的标记点若仍引用
  图标对象，内存由该引用本身决定，缓存淘汰不会让它们失效
- `CoordinateSystem` 只是数据源元数据：控件始终按 WGS84 / Web 墨卡托瓦片索引渲染，
  不会自动对 GCJ-02 / BD-09 影像做几何纠正；使用这些源时需用 `CoordinateConverter` 处理数据，
  或改用 WGS84 影像
- 测量结果按球面（大圆）计算：距离是地表测地线长度，多边形面积用球面环面积公式，
  圆形/方形按等距大圆定义（圆心到边界距离恒定）；平面模式下同样按球面计算，
  不随 Web 墨卡托的纬度拉伸变化
- 多边形面积按较小一侧（≤ 2πR²）报告：包住极点或超过半球的多边形会得到其余面积；
  距离/面积使用球面而非 WGS84 椭球，典型场景误差约 0.5%

## 示例工程

`samples/Globe3D.Demo` 展示影像瓦片、城市标记、图片图标、三维高空标记、区域、矢量图形、
比例尺与测量工具的完整用法：

### 配置自己的地图密钥

示例工程**不包含任何地图服务密钥**，在线底图需要你使用自己的 key：

1. 复制 `samples/Globe3D.Demo/keys.sample.json` 为同目录下的 `keys.local.json`
2. 填入自己的天地图 `tk` / 星图地球 `token`（支持数组，多个 key 会在启动时随机轮换）
3. 该文件已被 `.gitignore` 忽略，不会提交到仓库；也可用环境变量 `GLOBE3D_KEYS_FILE` 指向其它位置的配置文件

```json
{
  "tianditu": [ "你的天地图 tk" ],
  "geovis": [ "你的星图地球 token" ]
}
```

未配置密钥时示例仍可运行（本地 MBTiles 与内置图层），但不会注册在线底图；请勿把填有密钥的
`keys.local.json` 提交到仓库或随程序分发。多个 key 的轮换是示例自己的实现，库不提供密钥管理接口。

```
dotnet run --project samples/Globe3D.Demo                              # 默认（有本地 MBTiles 时优先用本地）
dotnet run --project samples/Globe3D.Demo -- --basemap=geovis         # 星图地球在线影像（需配置密钥）
dotnet run --project samples/Globe3D.Demo -- --basemap=tianditu       # 天地图在线影像（需配置密钥）
dotnet run --project samples/Globe3D.Demo -- --hover-test             # 图片标记 + 接近提示 + 鼠标旁浮层
dotnet run --project samples/Globe3D.Demo -- --hit-test               # 背面标记的命中回归（应查不到）
dotnet run --project samples/Globe3D.Demo -- --horizon-test           # 倾斜视角查看分层 LOD
dotnet run --project samples/Globe3D.Demo -- --seam-test              # 查看跨 180° 经线区域
dotnet run --project samples/Globe3D.Demo -- --seam-close             # 贴地斜视时跨 180° 区域的半球裁剪
dotnet run --project samples/Globe3D.Demo -- --deep-test              # 88 km 贴地视角
dotnet run --project samples/Globe3D.Demo -- --street-test            # 3 km 街区级视角（深层级瓦片）
dotnet run --project samples/Globe3D.Demo -- --pole-test              # 正对极点的极区填充
dotnet run --project samples/Globe3D.Demo -- --stats-test             # 输出一次 [PERF] 性能快照到 demo.log
dotnet run --project samples/Globe3D.Demo -- --flat                   # 二维平面模式
dotnet run --project samples/Globe3D.Demo -- --flat-toggle-test       # 平面/三维切换与平面平移回归
dotnet run --project samples/Globe3D.Demo -- --orientation-test       # 平面模式下 Heading/Pitch 不生效、切回三维恢复
dotnet run --project samples/Globe3D.Demo -- --coordinate-test        # 坐标系检测与 GCJ/BD/墨卡托转换回归
dotnet run --project samples/Globe3D.Demo -- --vector-test            # 矢量图形/测量/比例尺与图层显隐回归
dotnet run --project samples/Globe3D.Demo -- --zoom-level-test        # 缩放级别换算、上下限与平面模式回归
dotnet run --project samples/Globe3D.Demo -- --view-api-test          # 切换保级、SetView、中心不回写与低空/高空俯仰回归
dotnet run --project samples/Globe3D.Demo -- --geodesy-test           # 测地线距离/面积与极区多边形回归
dotnet run --project samples/Globe3D.Demo -- --vector-seam-test       # 平面模式跨 ±180° 折线/多边形回归
```

界面右上角的「底图」按钮可在本地 MBTiles、星图地球与天地图之间轮换；「性能面板」按钮控制
左上角性能 HUD 的开关；「平面模式」按钮在二维 Web 墨卡托平面与三维地球之间切换。第二行工具栏
提供比例尺、矢量图层显隐，以及距离/多边形/圆形/方形测量与清除；「缩小/放大」按缩放级别平滑缩放。状态栏会显示当前帧的层级范围、
绘制瓦片数、纹理数与失败数，demo.log 中还会记录各层级的瓦片分布、图标解码结果、测量结果与
命中回归结果，以及 `[GEODESY]` 测地线数值。

## Native AOT 与裁剪

控件不使用反射、`dynamic`、运行时 XAML 解析或序列化框架，库已开启 `IsAotCompatible`
（AOT/裁剪分析器零告警，IL2026/IL2067/IL3050 已提升为编译错误），可直接被 Native AOT 应用引用。

示例工程已启用 `PublishAot`，一条命令即可发布自包含原生程序：

```bash
dotnet publish samples/Globe3D.Demo -c Release -r win-x64
```

产物位于 `samples/Globe3D.Demo/bin/Release/net8.0/win-x64/publish/`，其中：

- `Globe3D.Demo.exe`：原生可执行文件（约 33 MB，运行时不依赖 .NET）
- `av_libglesv2.dll`：ANGLE（OpenGL ES 转 D3D11）
- `e_sqlite3.dll`：MBTiles（SQLite）原生库
- `libSkiaSharp.dll`、`libHarfBuzzSharp.dll`：文字与位图渲染

Windows 上 Native AOT 需要 MSVC C++ 工具链（Visual Studio「使用 C++ 的桌面开发」工作负载）。
已在 AOT 产物上验证：四叉树 LOD 与深缩放、街区/极点/跨经线视角、MBTiles 与在线瓦片及磁盘缓存、
PNG/JPG/GIF/SVG 图标、区域与标注、标记悬停与命中回归、性能面板统计，均与 JIT 版本一致。

> 若本地存在 `samples/Globe3D.Demo/keys.local.json`，它会随发布复制到输出目录；分发示例程序前
> 请将其删除或移至 `%LocalAppData%\Globe3D\keys.local.json`，避免泄露你的地图密钥。

使用注意：

- 自定义 XAML 请保持编译绑定（`AvaloniaUseCompiledBindingsByDefault=true`），
  `{Binding}` 改为 `{CompiledBinding}`，避免依赖裁剪后的反射入口
- 不要在运行时用 `XamlReader` 加载未编译的 XAML 资源
- 应用自身若使用反射/动态代码，仍需按常规 AOT 方式标注；控件本身无需额外配置

详细说明（英文）见 [docs/native-aot.md](docs/native-aot.md)。

## 打包与发布

```bash
# 生成 nupkg 与 snupkg（输出到 src/Globe3D.Avalonia/bin/Release）
dotnet pack src/Globe3D.Avalonia/Globe3D.Avalonia.csproj -c Release

# 发布到 nuget.org（需要先配置 API Key）
dotnet nuget push src/Globe3D.Avalonia/bin/Release/Globe3D.Avalonia.3.0.0.nupkg \
    --api-key $NUGET_API_KEY --source https://api.nuget.org/v3/index.json
```

仓库地址已在 `Globe3D.Avalonia.csproj` 的 `RepositoryUrl` / `PackageProjectUrl` 中配置，
发布前请按 [语义化版本](https://semver.org/lang/zh-CN/) 更新 `Version` 与 [CHANGELOG.md](CHANGELOG.md)。

## 版本记录

见 [CHANGELOG.md](CHANGELOG.md)。

## 许可

[MIT](LICENSE) © 2026 daipeng0809
