# Vector shapes, scale bar and measurement

`GlobeLayer.Vectors` draws geographic vector geometry on top of the imagery, in the same Avalonia
overlay as regions, markers and labels. Every shape is spherical: edges follow great circles, fills
are clipped to the visible hemisphere in 3D mode, and flat mode projects the same geographic geometry
through Web Mercator.

## Shapes

| Type | Definition | Closed |
| --- | --- | --- |
| `GlobeCircleVector` | `Center` + `RadiusMeters` (equidistant great-circle boundary) | yes |
| `GlobeSquareVector` | `Center` + `SideMeters` + `Heading` (degrees from north) | yes |
| `GlobePolylineVector` | `Points` with `Closed = false` (line / polyline) | no |
| `GlobePolygonVector` | `Points` with `Closed = true` (polygon) | yes |

Common properties:

| Property | Purpose |
| --- | --- |
| `Fill` | Fill brush; `null` draws a hollow shape (outline only) |
| `Stroke` / `StrokeThickness` | Border brush and width in pixels |
| `DashStyle` | Optional dash pattern (`DashStyle.Dash`, `DashStyle.Dot`, custom `DashStyle`) |
| `Label` / `ShowLabel` / `LabelForeground` / `LabelFontSize` | Centred label |
| `IsVisible` | Per-shape visibility |
| `Tag` | Caller data |

```csharp
var layer = new GlobeLayer("Vectors");

layer.Vectors.Add(new GlobeCircleVector
{
    Center = new GeoPoint(39.9042, 116.4074),
    RadiusMeters = 260_000,
    Fill = new SolidColorBrush(Color.Parse("#2E5AC8FF")),
    Stroke = Brushes.DeepSkyBlue,
    Label = "260 km"
});

layer.Vectors.Add(new GlobeSquareVector
{
    Center = new GeoPoint(31.2304, 121.4737),
    SideMeters = 300_000,
    Heading = 20,
    Fill = null,                     // hollow
    Stroke = Brushes.Orange,
    DashStyle = DashStyle.Dash
});

var polygon = new GlobePolygonVector { Fill = Brushes.Transparent, Stroke = Brushes.MediumSeaGreen };
polygon.AddPoint(28, 100).AddPoint(34, 98).AddPoint(36, 109);
layer.Vectors.Add(polygon);

globe.Layers.Add(layer);
layer.IsVisible = false;             // hides the whole layer; the overlay repaints immediately
```

`GlobeLayer` implements `INotifyPropertyChanged`, so changing `IsVisible` repaints the overlay
immediately without calling `globe.Refresh()`. Properties of an individual shape (colour, radius,
points) are read whenever the overlay is redrawn; the control renders on demand, so call
`globe.Refresh()` once after changing them outside an interaction.

Custom shapes: derive from `GlobeVector` and override `BuildRing()` (and `IsClosedRing` when needed).
Return the ring in longitude/latitude order; great-circle expansion, horizon clipping and projection
are handled by the control. `GlobeCircleVector` / `GlobeSquareVector` cache their ring until the
parameters change, so static shapes do not rebuild geometry on every frame.

## Rendering

- Vertex rings are expanded along great circles (≈1.2° steps) before projection, so edges stay on the
  surface and never cut through the globe.
- Fills are clipped to the camera's visible hemisphere; the clip boundary lies exactly on the sphere
  silhouette, so shapes crossing the horizon or ±180° still close properly. The projected polygon is
  then clipped to the viewport and filled with the non-zero rule (no spurious holes).
- Open polylines draw each segment only when both ends are visible; segments behind the horizon are
  skipped instead of being connected through the globe.
- Flat mode unwraps longitudes along the shortest path, clips polygons/polylines to [-180, 180] and
  splits shapes that cross the antimeridian into left and right parts. Screen segments are clipped to
  the viewport before drawing, keeping Skia coordinates bounded at deep zoom.
- `Fill = null` (hollow) still draws the stroke; `Stroke = null` draws only the fill.
- Vector labels follow `GlobeView.ShowLabels`; measurement results and the scale bar are always drawn
  because they are tool output rather than map annotation.

## Measurement tools

`GlobeView.ActiveTool` switches the left button into a measurement tool. Marker hover and marker
clicking are suspended while a tool is active.

| Tool | Interaction |
| --- | --- |
| `GlobeTool.None` | Normal navigation and picking |
| `GlobeTool.Distance` | Click to add vertices; double click or right click finishes |
| `GlobeTool.AreaPolygon` | Click to add vertices; double click or right click finishes (≥3 points) |
| `GlobeTool.AreaCircle` | First click sets the centre, the second click sets the radius |
| `GlobeTool.AreaSquare` | First click sets the centre; the second click sets the square's extent (a north-up square covering the clicked point's latitude/longitude offsets) |

Dragging still rotates/pans; only a click without drag adds a point. `Esc` cancels an incomplete draft;
right click finishes distance/polygon measurements (and cancels when there are not enough points yet).
`Esc` needs the control to hold keyboard focus: clicking the globe while a tool is active focuses it.
Measurements accumulate until `ClearMeasurements()`.

```csharp
globe.ActiveTool = GlobeTool.AreaCircle;

globe.MeasurementCompleted += (_, e) =>
{
    var m = e.Measurement;
    Console.WriteLine($"{m.Tool}: {m.Text} area={m.AreaSquareMeters:0} m² length={m.LengthMeters:0} m");
};

globe.ClearMeasurements();
globe.CancelMeasurement();

// Programmatic measurement (no mouse input); does not touch the current interactive draft
var distance = globe.AddMeasurement(GlobeTool.Distance,
    new GeoPoint(39.9042, 116.4074), new GeoPoint(31.2304, 121.4737)); // ≈ 1 069 km
```

`GlobeMeasurement` exposes `Tool`, `Points` (for circle/square the first two points are the centre and
a boundary point), `LengthMeters` (length or perimeter), `AreaSquareMeters`, `RadiusMeters`,
`SideMeters` and the formatted `Text`.

### Maths

`Geodesy` implements everything on the WGS84 sphere (`R = 6 378 137 m`):

| API | Meaning |
| --- | --- |
| `Distance(a, b)` | Haversine great-circle distance |
| `Destination(origin, distance, bearing)` | Great-circle destination point |
| `PathLength(points, closed)` | Sum of segment distances |
| `PolygonArea(points)` | Spherical ring-area formula; longitude deltas unwrapped along the shortest path, reported as the smaller side (≤ half the sphere) |
| `CircleArea(radius)` | Spherical cap area `2πR²(1 − cos(r/R))` |
| `CircleRing(center, radius)` / `SquareRing(center, side, heading)` | Boundary rings |
| `FormatDistance` / `FormatArea` | `m` / `km` / `m²` / `km²` text |

Values are spherical regardless of 3D or flat mode; Web Mercator latitude scaling does not affect
measured distances or areas. Rings that enclose a pole are normalised by subtracting the raw result
from the whole sphere, so a polar ring reports the enclosed cap rather than the rest of the planet.

## Scale bar

```csharp
globe.ShowScaleBar = true;                                  // default false
globe.ScaleBarPlacement = GlobeScaleBarPlacement.BottomLeft; // BottomRight / TopLeft / TopRight
```

The bar samples the real ground distance between two screen points around the viewport centre, so it
tracks altitude, pitch and flat-mode zoom. It then picks a `1 / 2 / 5 × 10^n` distance whose bar width
is close to 120 px and labels it with `Geodesy.FormatDistance`.

## Sample

```bash
dotnet run --project samples/Globe3D.Demo -- --vector-test
dotnet run --project samples/Globe3D.Demo -- --geodesy-test
dotnet run --project samples/Globe3D.Demo -- --vector-seam-test
```

The demo adds a circle, a hollow dashed square, a polygon and a polyline, shows the scale bar, adds one
measurement of each kind, toggles the vector layer, and switches 3D ↔ flat mode. Results are written to
`demo.log` (`[VECTOR]` / `[MEASURE]`). `--geodesy-test` logs known distance/area cases (including a
polar ring), and `--vector-seam-test` shows a polyline/polygon crossing ±180° in flat mode.

## Notes

- Measurements are spherical approximations: they use the sphere, not the WGS84 ellipsoid, which is
  within ~0.5 % for typical distances.
- Polygon area reports the smaller side of the ring: rings enclosing a pole, or polygons that span more
  than half the sphere, are reported as their complement.
- Exact poles are degenerate for ring construction; `Geodesy.Destination` nudges the origin inside
  ±89.999° so circles/squares centred on a pole still produce a usable ring.
- The square tool is north-up (heading 0); `GlobeSquareVector.Heading` can rotate static squares.
- Circle boundaries use 96 segments by default (`SegmentCount`); raise it for very large circles viewed
  close up.
