# Coordinate Systems

Globe3D renders tiles as a Web Mercator XYZ pyramid and treats all public coordinates
(`GeoPoint`, markers, regions, labels and the camera) as **WGS84** longitude/latitude.

Many providers publish their imagery in a shifted datum. The library exposes the source datum through
`TileSourceInfo.CoordinateSystem` and provides conversion helpers so application data can be moved
between common systems and WGS84.

## Supported systems

| `CoordinateSystem` | Meaning | Common source |
| --- | --- | --- |
| `Wgs84` | WGS84 longitude/latitude (EPSG:4326) | TianDiTu, GeovisEarth, OpenStreetMap, most XYZ services |
| `WebMercator` | Web Mercator projected metres (EPSG:3857) | projected coordinates exchanged by applications |
| `Gcj02` | GCJ-02 ("Mars" datum) | AMap / AutoNavi, Tencent Maps |
| `Bd09` | BD-09 | Baidu Maps |
| `Unknown` | unknown or mixed | a `CompositeTileSource` whose children disagree |

`TileSourceInfo.CoordinateSystem` defaults to `Wgs84`. It is **metadata**, not a request-time
reprojection: the control still requests WGS84 Web Mercator tile indices. GCJ-02 / BD-09 imagery is
offset relative to those indices, so the application must handle that datum (convert its own data,
pre-process tiles, or use a WGS84 source). The control does not warp raster tiles automatically.

## Detection

`HttpTileSource` detects the source datum from the URL template and display name when no explicit
value is supplied:

| URL / name hint | Detected system |
| --- | --- |
| `amap`, `autonavi`, `gaode`, `gtimg.com`, `map.qq.com`, `高德`, `腾讯` | `Gcj02` |
| `bdimg`, `baidu.com`, `map.baidu`, `bd09`, `百度` | `Bd09` |
| anything else, including TianDiTu / GeovisEarth / OSM | `Wgs84` |

Detection is a heuristic; proxies, mirrors and custom endpoints should be configured explicitly:

```csharp
var source = new HttpTileSource(
    urlTemplate: "https://tiles.example.com/{z}/{x}/{y}.png",
    coordinateSystem: CoordinateSystem.Gcj02);
```

`MbTilesTileSource` reads the `crs` / `srs` / `coordinate_system` metadata keys when they exist, and
also accepts an explicit value:

```csharp
var source = new MbTilesTileSource(path, coordinateSystem: CoordinateSystem.Bd09);
```

`CompositeTileSource` reports the common value when all children agree, otherwise `Unknown`.

## Conversion

`CoordinateConverter` converts between WGS84 and the geographic datums, and exposes dedicated
Web Mercator methods:

```csharp
var gcj = new GeoPoint(39.9070, 116.3970);          // AMap / Tencent coordinate
var wgs = CoordinateConverter.ToWgs84(gcj, CoordinateSystem.Gcj02);

var bd = new GeoPoint(39.9150, 116.4040);           // Baidu coordinate
var wgs2 = CoordinateConverter.ToWgs84(bd, CoordinateSystem.Bd09);

// reverse direction
var gcjAgain = CoordinateConverter.FromWgs84(wgs, CoordinateSystem.Gcj02);

// project / unproject
var (x, y) = CoordinateConverter.Wgs84ToWebMercator(39.9042, 116.4074);
var wgs3 = CoordinateConverter.WebMercatorToWgs84(x, y);
```

`ToWgs84` / `FromWgs84` preserve the altitude of the input `GeoPoint`. For `WebMercator`, use
`WebMercatorToWgs84(x, y)` and `Wgs84ToWebMercator(latitude, longitude)` — the metric X/Y values are
not latitude/longitude and are intentionally rejected by the geographic overloads.

### Accuracy

- GCJ-02 uses the commonly published Krasovsky-ellipsoid offset algorithm; the reverse direction is
  an iterative approximation (10 iterations, sub-metre difference in normal use).
- BD-09 is converted through GCJ-02 using the standard constants.
- GCJ-02 / BD-09 offsets are only applied inside the standard China coverage box; coordinates
  outside it are returned unchanged.
- Web Mercator latitude is clamped to ±85.05112878°.

## Demo

```bash
dotnet run --project samples/Globe3D.Demo -- --coordinate-test
```

The demo converts a Beijing point to GCJ-02 and BD-09, converts it back to WGS84, projects it through
Web Mercator, and detects an AMap URL as `Gcj02`; the residuals are written to `demo.log`.

## Compatibility

The coordinate utilities are pure .NET code with no reflection or dynamic code generation; they are
safe for Native AOT. The library still declares Avalonia 12.1.1 as its minimum UI dependency.
