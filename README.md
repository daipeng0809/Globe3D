![Globe3D.Avalonia icon](https://cdn.jsdelivr.net/gh/daipeng0809/Globe3D@v3.0.0/assets/globe3d-icon-128.png)

# Globe3D.Avalonia

English | [简体中文](https://github.com/daipeng0809/Globe3D/blob/master/README.zh-CN.md)

[![NuGet](https://img.shields.io/nuget/v/Globe3D.Avalonia.svg)](https://www.nuget.org/packages/Globe3D.Avalonia)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Globe3D.Avalonia.svg)](https://www.nuget.org/packages/Globe3D.Avalonia)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/daipeng0809/Globe3D/blob/master/LICENSE)
[![.NET](https://img.shields.io/badge/.NET-net8.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/Avalonia-12.1.1-8B4FBF.svg)](https://avaloniaui.net/)

A real-time 3D globe control for Avalonia 12. It renders imagery tiles, a procedural star field and
atmospheric scattering with OpenGL ES 3.0, and overlays 3D markers (PNG/JPG/GIF/SVG icons), region
polygons and text labels. The same control can switch to a flat 2D Web Mercator map at runtime.

![Preview](https://cdn.jsdelivr.net/gh/daipeng0809/Globe3D@v3.0.0/docs/screenshot.png)

## Table of contents

- [Features](#features)
- [Requirements & compatibility](#requirements--compatibility)
- [Installation](#installation)
- [Quick start](#quick-start)
  - [Minimal usage](#minimal-usage)
  - [Markers, regions and labels](#markers-regions-and-labels)
  - [Vector shapes and measurement](#vector-shapes-and-measurement)
  - [Image markers](#image-markers)
  - [Camera control](#camera-control)
  - [Zoom levels](#zoom-levels)
  - [Events and hit testing](#events-and-hit-testing)
  - [Performance panel](#performance-panel)
  - [Flat (2D) mode](#flat-2d-mode)
- [Data sources](#data-sources)
  - [MBTiles](#mbtiles)
  - [Generic online tiles](#generic-online-tiles)
  - [Coordinate systems](#coordinate-systems)
  - [Composite sources](#composite-sources)
- [Interaction](#interaction)
- [Main properties](#main-properties)
- [Technical notes](#technical-notes)
- [Known limitations](#known-limitations)
- [Sample project](#sample-project)
- [Native AOT](#native-aot)
- [Packaging and release](#packaging-and-release)
- [Version history](#version-history)
- [License](#license)

## Features

| Capability | Description |
| --- | --- |
| Imagery tiles | Web Mercator XYZ pyramid mapped directly from Mercator parameters to the sphere, without seams |
| Adaptive LOD | Quadtree chooses a zoom per tile from camera distance, grazing angle and projected screen size; near view and horizon use finer levels in the same frame |
| Flat mode | `IsFlatMode` switches between the 3D globe and a north-up 2D Web Mercator map; pan, zoom, markers, regions, labels and hit testing are reused |
| Zoom levels | `GetZoomLevel` / `ZoomToLevel` / `SetView` map tile levels to camera altitude, with atomic centre changes, min/max level limits and an on-screen badge |
| Data sources | MBTiles (offline SQLite), generic HTTP URL templates with disk cache, and composite sources |
| Coordinate systems | Source datum reporting, heuristic detection, and WGS84 / Web Mercator / GCJ-02 / BD-09 conversion |
| Sparse fallback | Missing tiles fall back along the ancestor chain, so gaps do not become black holes |
| Sky | Procedural stars (three hashed layers plus a Milky Way band), no image assets |
| Atmosphere | Fresnel rim scattering and outer glow, colour and intensity configurable |
| Markers | 3D markers with altitude, sphere occlusion, altitude leader lines and image icons |
| Image icons | PNG / JPG / GIF (first frame) / BMP / WebP / SVG; per-marker scale and pixel offset |
| Hover tips | Highlight and tooltip near markers; can be disabled per marker |
| Regions | Spherical subdivision for longitude/latitude vertices, fill and stroke, antimeridian aware |
| Vector shapes | Circles, squares, lines/polylines and polygons with hollow outlines, custom stroke colour/width/dash; layer visibility toggle |
| Measurement | Scale bar; distance measurement and polygon/circle/square area measurement with on-map results |
| Labels | Avalonia vector text with overlap avoidance |
| Interaction | Rotate, tilt, zoom, double-click fly-to, marker/ground picking |
| Performance panel | Optional HUD with smoothness score, FPS, refresh estimate, frame cost, tile loader and disk/network I/O |
| Rendering schedule | On-demand rendering; idle frames do not consume CPU/GPU |
| Native AOT | Library is AOT/trim analyser clean; the sample publishes as a self-contained native executable |

## Requirements & compatibility

| Item | Details |
| --- | --- |
| Target framework | `net8.0` |
| Dependencies | Avalonia **12.1.1** (minimum; any Avalonia 12.1.1+ application can reference it), Silk.NET.OpenGLES 2.22.0, Microsoft.Data.Sqlite 8.0.11 |
| Rendering backend | OpenGL ES 3.0 (`OpenGlControlBase`). Windows uses ANGLE (D3D11 underneath) and needs no extra runtime |
| Platforms | Windows / Linux desktop; Linux and domestic platforms need a GLES 3.0 driver |
| Threading | Control and overlay rendering on the UI thread; tile download/decode on worker threads; icon decoding is thread-safe |
| AOT | `IsAotCompatible=true`; IL2026/IL2067/IL3050 are build errors. The sample enables `PublishAot` |

> Version 3.0.0 is a breaking release: `Pitch` now means the true view pitch at the centre point
> (old values were orbital angles), and `IsFlatMode` keeps the displayed zoom level by default.
> Use `SetFlatMode(true, preserveZoomLevel: false)` for the old altitude behaviour.
>
> Version 2.0.0 is a breaking release: `OnlineMapProvider` and the provider-specific
> `OnlineTileSources` methods were removed. Use the generic `OnlineTileSources.Create(urlTemplate, ...)`
> or `new HttpTileSource(...)`.

## Installation

```bash
dotnet add package Globe3D.Avalonia --version 3.0.0
```

Or in a project file:

```xml
<PackageReference Include="Globe3D.Avalonia" Version="3.0.0" />
```

XAML namespace:

```xml
<Window xmlns:g="clr-namespace:Globe3D.Avalonia;assembly=Globe3D.Avalonia">
```

## Quick start

### Minimal usage

```xml
<g:GlobeView MbTilesPath="D:\maps\satellite.mbtiles" />
```

### Markers, regions and labels

```csharp
var cityLayer = new GlobeLayer("Cities");
cityLayer.Markers.Add(new GlobeMarker
{
    Position = new GeoPoint(39.9042, 116.4074), // latitude, longitude
    Label = "Beijing",
    Shape = MarkerShape.Pin,
    Fill = Brushes.OrangeRed
});

// 3D marker with altitude in metres
cityLayer.Markers.Add(new GlobeMarker
{
    Position = new GeoPoint(35d, 118d, 400_000d),
    Label = "High-altitude target",
    Shape = MarkerShape.Triangle,
    ShowAltitudeLine = true
});
globe.Layers.Add(cityLayer);

var region = new GlobeRegion
{
    Label = "Example region",
    Fill = new SolidColorBrush(Color.Parse("#3348A0FF")),
    Stroke = Brushes.SkyBlue,
    StrokeThickness = 2
};
region.AddPoint(28, 100).AddPoint(34, 98).AddPoint(36, 110).AddPoint(31, 114);

var regionLayer = new GlobeLayer("Regions");
regionLayer.Regions.Add(region);
globe.Layers.Add(regionLayer);

var labelLayer = new GlobeLayer("Labels");
labelLayer.Labels.Add(new GlobeLabel
{
    Position = new GeoPoint(42d, 103d),
    Text = "Inner Mongolia Plateau",
    Foreground = Brushes.Khaki
});
globe.Layers.Add(labelLayer);
```

### Vector shapes and measurement

`GlobeLayer.Vectors` holds vector shapes. `GlobeLayer.IsVisible` shows/hides the whole layer and
invalidates the overlay immediately:

```csharp
var vectorLayer = new GlobeLayer("Vectors");

// Circle: centre + radius, filled
vectorLayer.Vectors.Add(new GlobeCircleVector
{
    Center = new GeoPoint(39.9042, 116.4074),
    RadiusMeters = 260_000,
    Fill = new SolidColorBrush(Color.Parse("#2E5AC8FF")),
    Stroke = Brushes.DeepSkyBlue,
    StrokeThickness = 2,
    Label = "Circle - 260 km"
});

// Square: centre + side + heading; Fill = null means hollow (outline only)
vectorLayer.Vectors.Add(new GlobeSquareVector
{
    Center = new GeoPoint(31.2304, 121.4737),
    SideMeters = 300_000,
    Heading = 20,
    Fill = null,
    Stroke = Brushes.Orange,
    DashStyle = DashStyle.Dash,
    Label = "Hollow square"
});

// Polygon; use GlobePolylineVector (Closed = false) for lines and polylines
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
vectorLayer.IsVisible = false;   // hides the whole layer; set back to true to show
```

Changing `GlobeLayer.IsVisible` repaints immediately without calling `Refresh()`. Changing properties of
an individual shape (colour, radius, points) requires a `globe.Refresh()` call: the control renders on
demand and does not repaint while idle. Custom shapes can derive from `GlobeVector` and override
`BuildRing()` / `IsClosedRing`; great-circle expansion, horizon clipping and projection are handled by
the control.

| Vector property | Description |
| --- | --- |
| `GlobeCircleVector` | `Center` + `RadiusMeters`, boundary generated on great circles |
| `GlobeSquareVector` | `Center` + `SideMeters` + `Heading` (degrees from north) |
| `GlobePolylineVector` / `GlobePolygonVector` | `Points`; `Closed=false` for lines/polylines, `true` for polygons |
| `Fill` | Fill brush; `null` draws a hollow shape |
| `Stroke` / `StrokeThickness` / `DashStyle` | Border colour, width and dash pattern |
| `Label` / `LabelForeground` / `LabelFontSize` | Centred text label |
| `IsVisible` | Per-shape visibility |

Scale bar and measurement tools:

```csharp
globe.ShowScaleBar = true;                                 // draw a scale bar in a viewport corner
globe.ScaleBarPlacement = GlobeScaleBarPlacement.BottomRight;

globe.ActiveTool = GlobeTool.Distance;                     // click to add points, double/right click to finish
// GlobeTool.AreaPolygon / AreaCircle / AreaSquare measure polygon, circle and square areas.
// Circle/square: first click sets the centre, the second click sets the radius/boundary point.

globe.MeasurementCompleted += (_, e) =>
    Console.WriteLine($"{e.Measurement.Tool}: {e.Measurement.Text}");

globe.ClearMeasurements();                                 // remove all finished measurements
globe.CancelMeasurement();                                 // discard the current draft (Esc also works when the control has focus)

// Measurements can also be appended programmatically without touching the current draft
var m = globe.AddMeasurement(GlobeTool.Distance,
    new GeoPoint(39.9042, 116.4074), new GeoPoint(31.2304, 121.4737));
```

`Geodesy` exposes the same spherical maths used for drawing and measuring: `Distance`, `Destination`,
`CircleRing`, `SquareRing`, `PolygonArea`, `CircleArea`, `FormatDistance` and `FormatArea`. Area is
reported as the smaller side of the ring: polygons enclosing a pole or spanning more than half the
sphere return their complement (never more than 2πR²).

See [docs/vector-measure.md](https://github.com/daipeng0809/Globe3D/blob/master/docs/vector-measure.md)
for rendering details and formula notes.

### Image markers

Any Avalonia `IImage` can be used as a marker icon, or just provide a file path and the control loads
and caches it. Supported: PNG / JPG / JPEG / GIF (first frame) / BMP / WebP, plus SVG with built-in
vector rendering.

```csharp
var layer = new GlobeLayer("Image markers");

layer.Markers.Add(new GlobeMarker
{
    Position = new GeoPoint(39.9042, 116.4074),
    ImagePath = @"Assets\pin.png",
    ImageScale = 0.5,
    ImageOffset = new Vector(0, -26), // move the pin tip to the coordinate
    HoverTip = "Beijing"
});

layer.Markers.Add(new GlobeMarker
{
    Position = new GeoPoint(31.2304, 121.4737),
    ImagePath = @"Assets\radar.svg",
    ImageScale = 1.3,
    Tag = "Shanghai radar"
});

globe.Layers.Add(layer);

// Manual loading (cached; at most 64 icons by default)
IImage icon = MarkerImage.Load(@"Assets\dot.gif");
IImage svg = MarkerImage.FromSvg("<svg viewBox='0 0 24 24'>...</svg>");
```

| Marker property | Description |
| --- | --- |
| `Image` / `ImagePath` | Icon source; `Image` wins when both are set. `ImageError` reports a load failure |
| `ImageScale` | Scale relative to the image's pixel size |
| `ImageOffset` | Pixel offset from the anchor (positive right/down) |
| `ShowHoverTip` | Whether this marker participates in hover tips |
| `HoverTip` | Hover text; falls back to `Label`, then `ToolTip` |
| `HoverRadius` | Per-marker hover radius in pixels; `0` uses `MarkerHoverRadius` |

### Camera control

```csharp
globe.JumpTo(31.23, 121.47, 1_200_000);                 // immediate (altitude)
globe.FlyTo(31.23, 121.47, 260_000,
    TimeSpan.FromMilliseconds(1200));                   // smooth flight (altitude)
globe.SetView(31.23, 121.47, 12);                       // atomic centre + zoom level
globe.JumpToLevel(31.23, 121.47, 12);                   // alias for SetView

globe.CenterLatitude = 33;
globe.CenterLongitude = 108;                              // requested centre (flat mode may clamp)
globe.Altitude = 9_500_000;                              // metres above ground
globe.Pitch = 25;                                        // true view pitch, 0-88°
double effectivePitch = globe.GetEffectivePitch();       // actual view pitch (0 in flat mode)
double actualLat = globe.EffectiveCenterLatitude;        // actual drawn centre (may differ at map edge)
double actualLon = globe.EffectiveCenterLongitude;
```

### Zoom levels

```csharp
double level = globe.GetZoomLevel();                        // current level (≈ 256 px tile level)
globe.ZoomToLevel(8);                                       // compute camera altitude from the level
globe.ZoomToLevel(9, TimeSpan.FromMilliseconds(400));       // smooth zoom
globe.ZoomLevel = 8;                                        // equivalent

globe.MinZoomLevel = 3;                                     // clamp ZoomToLevel / ZoomLevel / wheel zoom
globe.MaxZoomLevel = 15;

globe.ShowZoomLevel = true;                                 // "Z 8.4" badge in a corner
globe.ZoomLevelPlacement = GlobeScaleBarPlacement.BottomRight;
```

- In 3D the altitude is solved iteratively for the current pitch, centre latitude and physical pixel
  height, so the requested level matches the displayed level; flat mode uses an analytic inverse.
  `Pitch` is a true view pitch measured at the centre point: 0 looks straight down and larger values
  approach horizontal; the orbital offset is solved for the current altitude so the centre stays in
  front of the globe.
- `SetView(latitude, longitude, level)` (alias `JumpToLevel`) sets the centre and level atomically.
  The altitude is solved once for the target centre and the physical-pixel viewport, avoiding the
  "set centre first, level later" intermediate clamp; it also works before layout.
- `MinZoomLevel` / `MaxZoomLevel` work together with `MinAltitude` / `MaxAltitude`; the stricter limit
  wins. `JumpTo`, `FlyTo` and direct `Altitude` assignments are altitude APIs and are not clamped by
  the level limits.
- `ZoomLevel` is a computed value without Avalonia change notification; use `ShowZoomLevel` for live
  display or `CameraChanged` to observe changes.
- `ZoomToLevel` called before layout is stored and applied after the first rendered frame (once the
  GL surface has measured the physical pixel size).

See [docs/zoom-level.md](https://github.com/daipeng0809/Globe3D/blob/master/docs/zoom-level.md) for the
formulas and limit semantics.

### Events and hit testing

```csharp
globe.GlobeClicked += (_, e) =>
{
    if (e.Location is { } p)
        Console.WriteLine($"latitude {p.Latitude}, longitude {p.Longitude}");
};

globe.GlobePointerMoved += (_, e) =>
{
    if (e.NearestMarker is { } nearest && e.NearestDistance <= globe.MarkerHoverRadius)
        Console.WriteLine($"hover {nearest.Tag} at {e.ScreenPosition}");
};

globe.MarkerHoverChanged += (_, e) => Console.WriteLine(e.Marker?.Tag ?? "left marker");

globe.MarkerClicked += (_, e) => Console.WriteLine(e.Marker.Label);

// direct queries
globe.FindNearestMarker(new Point(x, y), out var pixels);
globe.FindNearestMarker(new Point(x, y), maxDistance: 24);
globe.FindNearestMarker(31.23, 121.47);

globe.UpdateHoverMarker(new Point(x, y));
globe.UpdateHoverMarker(null);

globe.RenderingError += (_, message) => Console.WriteLine(message);
```

Hit testing always re-projects with the **current camera** and culls the sphere's far side and
horizon; it does not rely on the previous frame's screen positions.

### Performance panel

```csharp
globe.ShowPerformancePanel = true;

var stats = globe.GetPerformanceSnapshot();
Console.WriteLine($"{stats.FramesPerSecond:0.0} fps, score {stats.SmoothnessScore}, " +
                  $"{stats.DiskReadBytesPerSecond:0} B/s");
```

The HUD shows a 0-100 smoothness score (frame cost 45%, jitter 20%, achieved FPS 35% against a fixed
60 FPS target), FPS, refresh estimate, frame cost, drawn tiles/textures, LOD and selection time,
loader queue, and MBTiles/disk/network tile I/O. See [docs/performance-panel.md](https://github.com/daipeng0809/Globe3D/blob/master/docs/performance-panel.md)
for the formulas, idle behaviour and limitations.

### Flat (2D) mode

```csharp
globe.IsFlatMode = true;   // flat Web Mercator map, keeps the zoom level
globe.IsFlatMode = false;  // 3D globe, keeps the zoom level
globe.SetFlatMode(true);   // equivalent; pass preserveZoomLevel: false to keep Altitude
```

In flat mode:

- Left drag pans, wheel zooms (reusing `Altitude`), double-click flies closer; right-drag tilt is disabled.
- Star field, atmosphere and surface lighting are not rendered; marker altitude leader lines are hidden.
- The map remains north-up; `Pitch` / `Heading` are retained but do not apply. Switching projection
  keeps the displayed zoom level by default and cancels any running flight/inertia animation.
- The map is bounded to ±180° / ±85.05°; it does not wrap horizontally. The viewport clamp does not
  write back `CenterLatitude` / `CenterLongitude` (only user interactions such as drag, wheel zoom
  and auto rotation write the clamped centre back), so the requested centre is recovered after
  zooming out and back in. Read the actual
  drawn centre from `EffectiveCenterLatitude` / `EffectiveCenterLongitude`; the sample status bar
  uses them. Auto rotation and inertia stop at the flat map bounds.
- Regions crossing the antimeridian are split into two parts.
- The same tile LOD, markers, regions, labels, hover, click and `FindNearestMarker` APIs are used.

![3D vs flat with Heading/Pitch](https://cdn.jsdelivr.net/gh/daipeng0809/Globe3D@v3.0.0/docs/flat-mode-heading-pitch.png)

See [docs/flat-mode.md](https://github.com/daipeng0809/Globe3D/blob/master/docs/flat-mode.md) for details and implementation notes.

## Data sources

### MBTiles

```csharp
globe.MbTilesPath = @"D:\DMap\mapabc\data\satellite.mbtiles";
```

Requirements: standard MBTiles, `tiles` table in TMS row order (south to north), `tile_data` in
PNG/JPEG. For XYZ row order:

```csharp
globe.TileSource = new MbTilesTileSource(path, flipY: false);
```

### Generic online tiles

The library provides no provider-specific factory. `OnlineTileSources` only exposes a generic
`Create(urlTemplate, ...)`; URLs, keys and multi-key rotation belong to the application. The same
result can be obtained with `new HttpTileSource(...)`. Supported placeholders: `{z}`, `{x}`, `{y}`,
`{-y}` (TMS row). Responses are filtered by `Content-Type` and cached on disk.

```csharp
// GeovisEarth (example; JPEG decodes on every platform)
globe.TileSource = OnlineTileSources.Create(
    "https://api.open.geovisearth.com/pj/base/v1/2025/{z}/{x}/{y}?format=jpeg&tmsIds=w&token=YOUR_TOKEN",
    cacheDirectory: @"C:\TileCache\geovis");

// TianDiTu WMTS (img_w is Web Mercator)
globe.TileSource = OnlineTileSources.Create(
    "https://t0.tianditu.gov.cn/img_w/wmts?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0" +
    "&LAYER=img&STYLE=default&TILEMATRIXSET=w&FORMAT=tiles" +
    "&TILEMATRIX={z}&TILEROW={y}&TILECOL={x}&tk=YOUR_TK",
    cacheDirectory: @"C:\TileCache\tianditu");
```

### Coordinate systems

`TileSourceInfo.CoordinateSystem` reports the source datum. `HttpTileSource` detects it from the URL
and name unless overridden:

- AMap / Tencent (`amap`, `autonavi`, `gtimg`, `map.qq.com`) → `Gcj02`
- Baidu (`bdimg`, `baidu.com`, `map.baidu`) → `Bd09`
- TianDiTu, GeovisEarth, OSM and most standard XYZ sources → `Wgs84`

```csharp
var source = new HttpTileSource(url, coordinateSystem: CoordinateSystem.Wgs84);
```

MBTiles reads `crs` / `srs` / `coordinate_system` metadata, and also accepts an explicit value:

```csharp
new MbTilesTileSource(path, coordinateSystem: CoordinateSystem.Gcj02);
```

`CompositeTileSource` reports the common value when all children agree, otherwise `Unknown`.

`CoordinateConverter` converts between the common systems:

```csharp
var wgs = CoordinateConverter.ToWgs84(new GeoPoint(39.907, 116.397), CoordinateSystem.Gcj02);
var bd = CoordinateConverter.FromWgs84(wgs, CoordinateSystem.Bd09);

var (x, y) = CoordinateConverter.Wgs84ToWebMercator(39.9042, 116.4074);
var back = CoordinateConverter.WebMercatorToWgs84(x, y);
```

Supported: WGS84, Web Mercator (EPSG:3857), GCJ-02, BD-09. GCJ/BD offsets are not applied outside
China. Web Mercator uses metres, so use `WebMercatorToWgs84` / `Wgs84ToWebMercator` rather than the
geographic overloads.

> `CoordinateSystem` is metadata only. The control still requests WGS84 Web Mercator tile indices
> and does not warp GCJ-02/BD-09 raster imagery automatically. Convert your data, pre-process the
> tiles, or use a WGS84 source. See [docs/coordinate-systems.md](https://github.com/daipeng0809/Globe3D/blob/master/docs/coordinate-systems.md).

### Composite sources

```csharp
globe.TileSource = new CompositeTileSource(
    new MbTilesTileSource(@"D:\maps\china.mbtiles"),
    new HttpTileSource("https://example.com/{z}/{x}/{y}.png", @"C:\TileCache"));
```

## Interaction

| Input | 3D mode | Flat mode |
| --- | --- | --- |
| Left drag | Rotate the globe; inertia after release | Pan the map; inertia after release |
| Right drag | Tilt and heading | Ignored |
| Wheel | Zoom (clamped by `MinAltitude` / `MaxAltitude` and `MinZoomLevel` / `MaxZoomLevel`) | Cursor-anchored zoom |
| Double click | Fly to the point | Fly to the point and zoom in |
| Single click | Pick nearest marker or ground coordinate | Same |
| Measurement tool | Click to add points; distance/polygon finish on double/right click; circle/square on the second click; Esc cancels (the globe takes focus on click) | Same |
| Scale bar | 1/2/5×10^n bar in m/km derived from the current camera | Same |
| Hover | Highlight and tooltip | Same |

## Main properties

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `CenterLatitude` | double | 32 | Centre latitude |
| `CenterLongitude` | double | 108 | Centre longitude |
| `Altitude` | double | 10 200 000 | Camera altitude in metres |
| `Pitch` | double | 0 | View pitch, 0-88° (3D only; retained in flat mode) |
| `Heading` | double | 0 | Heading (3D only) |
| `MinAltitude` / `MaxAltitude` | double | 10 000 / 51 000 000 | Zoom limits |
| `MbTilesPath` | string? | null | MBTiles path; creates a source automatically |
| `TileSource` | ITileSource? | null | Custom tile source |
| `ShowStars` / `StarIntensity` | bool / double | true / 1.0 | Star field |
| `AtmosphereColor` / `AtmosphereIntensity` | Color / double | #5AA8FF / 0.85 | Atmosphere |
| `LightingIntensity` | double | 0.28 | Surface directional light |
| `ShowLabels` | bool | true | Text labels |
| `MarkerHoverRadius` | double | 18 | Default hover radius in pixels |
| `MarkerHitTolerance` | double | 6 | Extra click tolerance in pixels |
| `AutoRotate` / `AutoRotateSpeed` | bool / double | false / 6 | Auto rotation |
| `IsFlatMode` | bool | false | Flat 2D map |
| `ZoomLevel` | double | computed | Current zoom level; setting it calls `ZoomToLevel` |
| `MinZoomLevel` / `MaxZoomLevel` | double | 0 / 24 | Zoom level limits (API and wheel) |
| `ShowZoomLevel` / `ZoomLevelPlacement` | bool / GlobeScaleBarPlacement | false / BottomRight | Zoom level badge |
| `ShowScaleBar` / `ScaleBarPlacement` | bool / GlobeScaleBarPlacement | false / BottomLeft | Scale bar toggle and corner |
| `ActiveTool` | GlobeTool | None | Measurement tool: distance, polygon/circle/square area |
| `Measurements` | IReadOnlyList&lt;GlobeMeasurement&gt; | empty | Finished measurements |
| `ShowPerformancePanel` | bool | false | Performance HUD |
| `Layers` | AvaloniaList&lt;GlobeLayer&gt; | empty | Layer collection; XAML content |

## Technical notes

- Rendering: `OpenGlControlBase` + `Silk.NET.OpenGLES`; Windows ANGLE (D3D11).
- Tiles use a shared 9×9 parameter mesh; the vertex shader maps Mercator coordinates to the sphere.
  All tiles share one VAO and update only four uniforms.
- LOD: quadtree from the source's minimum zoom. Desired zoom combines distance/grazing and screen
  space error. When the leaf budget is exceeded, same-parent siblings are merged upward without holes.
- Tile loading: priority queue, "last needed frame" tracking, stale request discard, failed-tile
  cooldown and ancestor fallback.
- GLES 3.0 core has no `GL_BGRA`; decoded tiles are converted to RGBA before upload.
- Regions: spherical great-circle expansion and visible-hemisphere clipping; ±180° and horizon
  crossings close correctly; screen-space clipping follows.
- Vectors and measurements reuse the region pipeline (great-circle expansion, visible-hemisphere,
  near-plane and viewport clipping). Open polylines connect only visible vertices; flat mode clips
  them by longitude into separate runs and pins intersection longitudes to the ±180° boundary so
  floating-point overshoot cannot wrap to the other side. Areas use the spherical ring-area formula
  (longitude deltas unwrapped along the shortest path, polar rings normalised to the smaller side)
  and circle areas use the spherical cap formula. The scale bar derives metres-per-pixel from two
  real ground points near the viewport centre, so 3D tilt and flat mode both track the camera.
- Flat mode: `OrbitCamera.IsFlat` drives a Web Mercator plane. Tiles use a dedicated vertex shader
  and pre-computed NDC rectangles (double precision on the CPU), so deep zoom remains stable. Tile
  sampling and overlays project longitude/latitude directly to avoid ±180° flips. Regions are
  unwrapped and clipped to [-180, 180].
- Coordinate systems: `TileSourceInfo.CoordinateSystem`, `CoordinateSystemDetector`,
  `CoordinateConverter`; GCJ-02/BD-09 use the standard China-only offsets.
- Performance: 120-frame ring buffer for frame cost and refresh estimate; rates use the interval
  between snapshot calls; idle values reset after one second.
- AOT: no reflection, `dynamic`, runtime XAML parsing or serializers.
- Dependencies: Avalonia 12.1.1, Silk.NET.OpenGLES 2.22.0, Microsoft.Data.Sqlite 8.0.11.

## Known limitations

- A region edge is interpreted on the shortest great-circle path; split vertices at ±180° for
  extremely wide regions.
- Mainstream tile services have no imagery above ±85°; the last row is stretched into the polar cap.
- OpenGL ES 3.0 is required; there is no software renderer fallback.
- GIF icons use the first frame only.
- Built-in SVG supports `svg / g / rect / circle / ellipse / line / polyline / polygon / path`
  and `fill / stroke / stroke-width / opacity / transform`; gradients use the first stop only.
  Filters, text and external references should be decoded to `IImage`.
- The marker icon cache holds at most 64 files (`MarkerImage.MaxCachedImages`).
- `CoordinateSystem` is metadata; the control never warps GCJ-02/BD-09 tile imagery automatically.
- Measurements are spherical: distance is a great-circle length, polygon area uses the spherical
  ring-area formula, and circle/square shapes are equidistant great-circle constructions. The same
  spherical values are reported in flat mode, independent of Web Mercator latitude scaling.
- Polygon area reports the smaller side (≤ 2πR²): rings enclosing a pole or spanning more than half
  the sphere return their complement. Distances/areas use the sphere, not the WGS84 ellipsoid
  (roughly 0.5 % difference in typical cases). Exact poles are degenerate for ring construction;
  `Geodesy.Destination` nudges the origin inside ±89.999° so polar circles/squares remain usable.

## Sample project

`samples/Globe3D.Demo` demonstrates imagery, city markers, image icons, 3D altitude markers, regions,
vector shapes, the scale bar, measurement tools, the performance panel and flat mode.

### Configure your own map keys

The sample contains **no map service credentials**:

1. Copy `samples/Globe3D.Demo/keys.sample.json` to `keys.local.json` in the same directory.
2. Fill in your TianDiTu `tk` / GeovisEarth `token` values (arrays are supported by the sample).
3. The file is git-ignored; `GLOBE3D_KEYS_FILE` can point to another location.

```json
{
  "tianditu": [ "YOUR_TIANDITU_TK" ],
  "geovis": [ "YOUR_GEOVIS_TOKEN" ]
}
```

The sample implements key rotation itself; the library provides no key-management API. Without keys
the sample still runs with local MBTiles and its built-in layers.

```bash
dotnet run --project samples/Globe3D.Demo
dotnet run --project samples/Globe3D.Demo -- --basemap=geovis
dotnet run --project samples/Globe3D.Demo -- --basemap=tianditu
dotnet run --project samples/Globe3D.Demo -- --hover-test
dotnet run --project samples/Globe3D.Demo -- --hit-test
dotnet run --project samples/Globe3D.Demo -- --horizon-test
dotnet run --project samples/Globe3D.Demo -- --street-test
dotnet run --project samples/Globe3D.Demo -- --pole-test
dotnet run --project samples/Globe3D.Demo -- --seam-test
dotnet run --project samples/Globe3D.Demo -- --stats-test
dotnet run --project samples/Globe3D.Demo -- --flat
dotnet run --project samples/Globe3D.Demo -- --flat-toggle-test
dotnet run --project samples/Globe3D.Demo -- --orientation-test
dotnet run --project samples/Globe3D.Demo -- --coordinate-test
dotnet run --project samples/Globe3D.Demo -- --vector-test
dotnet run --project samples/Globe3D.Demo -- --zoom-level-test
dotnet run --project samples/Globe3D.Demo -- --view-api-test
dotnet run --project samples/Globe3D.Demo -- --geodesy-test
dotnet run --project samples/Globe3D.Demo -- --vector-seam-test
```

The second toolbar row toggles the scale bar and vector layer visibility and starts distance,
polygon, circle or square measurement; the zoom out/in buttons zoom by tile level. `demo.log` also
records finished measurement values, `[GEODESY]` reference numbers and `[ZOOM]` level mappings.

## Native AOT

The library is AOT/trim analyser clean (`IsAotCompatible=true`, IL2026/IL2067/IL3050 are errors).
The sample enables `PublishAot`:

```bash
dotnet publish samples/Globe3D.Demo -c Release -r win-x64
```

The output contains the native executable plus ANGLE (`av_libglesv2.dll`), SQLite
(`e_sqlite3.dll`), Skia and HarfBuzz native libraries. Windows Native AOT requires the MSVC C++
toolchain. See [docs/native-aot.md](https://github.com/daipeng0809/Globe3D/blob/master/docs/native-aot.md).

## Packaging and release

```bash
dotnet pack src/Globe3D.Avalonia/Globe3D.Avalonia.csproj -c Release

dotnet nuget push src/Globe3D.Avalonia/bin/Release/Globe3D.Avalonia.3.0.0.nupkg \
    --api-key $NUGET_API_KEY --source https://api.nuget.org/v3/index.json
```

`RepositoryUrl` and `PackageProjectUrl` are configured in the project file. Update
[CHANGELOG.md](https://github.com/daipeng0809/Globe3D/blob/master/CHANGELOG.md) and the version for each release.

## Version history

See [CHANGELOG.md](https://github.com/daipeng0809/Globe3D/blob/master/CHANGELOG.md).

## License

[MIT](LICENSE) © 2026 daipeng0809
