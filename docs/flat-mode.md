# Flat (2D) Mode

`GlobeView` can switch between the default 3D globe and a flat 2D Web Mercator map at runtime.
The same tile pyramid, LOD selection, layers, markers, regions, labels, hover and hit testing are
reused in both modes; only the projection and input mapping change.

## Enabling

XAML:

```xml
<g:GlobeView IsFlatMode="True" />
```

Code:

```csharp
globe.IsFlatMode = true;    // 2D Web Mercator plane
globe.IsFlatMode = false;   // back to the 3D globe
```

`IsFlatMode` defaults to `false`. The switch is a normal `StyledProperty`, so it can be bound,
animated in code, or toggled from a toolbar button. The sample exposes a **平面模式 / 三维模式**
button and the `--flat` / `--flat-toggle-test` command-line switches.

## Interaction

| Input | 3D mode | Flat mode |
| --- | --- | --- |
| Left drag | Rotate the globe (with inertia) | Pan the map (with inertia) |
| Right drag | Tilt and heading | Ignored |
| Mouse wheel | Zoom via `Altitude` | Zoom via `Altitude` |
| Double click | Fly to the clicked location | Fly to the clicked location and zoom in |
| Single click | Pick a marker or return the ground coordinate | Same |
| Hover | Marker highlight and tooltip | Same |

`Pitch` and `Heading` keep their values but are ignored while flat mode is active, so switching back
to 3D restores the previous orientation.

## What changes in flat mode

- The globe sphere, star field, atmosphere glow and surface lighting are not rendered; the tile
  imagery is drawn as a flat Web Mercator plane on the control background.
- `Altitude` is reused as the zoom control. `MinAltitude` / `MaxAltitude` still define the zoom range.
- Marker altitude is ignored: markers are placed at their geographic position, and altitude leader
  lines are hidden. `GlobeLabel` and region labels are projected in the same way.
- The map is bounded: it does not wrap horizontally. Panning stops at ±180° and at the Web Mercator
  latitude limit (±85.05°); beyond the data the control shows the fallback tile color / background.
- A region that crosses the antimeridian is split into a left and a right part instead of being
  drawn as a band across the whole map.
- Marker hover, click, `FindNearestMarker` and `GlobePointerMoved` continue to work; screen hit
  testing uses the current flat camera and the marker's geographic position.

## API

```csharp
// Toggle the mode
globe.IsFlatMode = true;

// The underlying camera exposes the flat projection state and helpers
var camera = globe.Camera;
bool flat = camera.IsFlat;

camera.PanFlatByPixels(dx, dy, globe.Bounds.Width, globe.Bounds.Height);
if (camera.TryGetFlatViewportBounds(out var west, out var east, out var south, out var north))
{
    // visible geographic rectangle
}
```

`GetPerformanceSnapshot()` and the performance HUD continue to work in flat mode; frame timing,
tile LOD, loader queue and tile I/O are measured in the same way. The altitude row still reports
the camera altitude, which now controls the flat zoom.

## Implementation notes

- The planar projection is driven by `OrbitCamera.IsFlat`. `FlatScaleFactor` expresses how many
  Mercator world pixels correspond to one viewport height; it is derived from altitude and field of
  view (not from latitude), and is shared by the GL surface and the Avalonia overlay so both use the
  same scale at any DPI. Because the scale is latitude-independent, panning north/south does not
  change zoom or tile LOD.
- Tiles are drawn with a dedicated vertex shader (`FlatVertex`) that maps each tile to a
  pre-computed NDC rectangle. The rectangle is calculated in double precision on the CPU, which
  keeps deep zoom levels stable where a single-precision world-pixel coordinate would lose
  precision.
- Tile LOD uses the same quadtree traversal as 3D mode. The desired zoom is
  `log2(worldPixels / 256)`, where `worldPixels = FlatScaleFactor * viewportHeight`.
- Tile samples, markers and labels that touch ±180° are projected directly from longitude/latitude
  (`ProjectGeo`). Going through a 3D unit vector would otherwise flip the endpoint to the other
  side of the map at deep zoom or at the antimeridian.
- Flat regions are unwrapped in geographic space and clipped to [-180, 180]; the clipping can
  produce one polygon per side of the map, and each part is filled and stroked separately.

## Demo

```bash
dotnet run --project samples/Globe3D.Demo -- --flat
dotnet run --project samples/Globe3D.Demo -- --flat-toggle-test
```

`--flat` starts directly in 2D mode. `--flat-toggle-test` enables 2D, pans the map, switches back
to 3D and writes the results to `demo.log`.

## Compatibility and limitations

- Requires Avalonia **12.1.1** or newer (the package declares Avalonia 12.1.1 as its minimum).
- Works with the same Native AOT configuration as the 3D mode; no reflection or runtime XAML is
  involved. The win-x64 AOT build is verified with both modes.
- The flat map is intentionally non-wrapping and bounded to the Web Mercator extent; it is not a
  tiled infinite canvas.
- Flat mode is a rendering projection, not a change of the data source. Map tile licensing,
  attribution and local surveying/map-publication compliance remain the responsibility of the
  application and its tile provider.
