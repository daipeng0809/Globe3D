# Zoom levels

`GlobeView` exposes a tile-level zoom API on top of the camera altitude. A level roughly means
"the Web Mercator tile level whose 256 px tiles are shown at native resolution".

```csharp
double level = globe.GetZoomLevel();                       // computed from the current camera
globe.ZoomToLevel(8);                                      // jump
globe.ZoomToLevel(9, TimeSpan.FromMilliseconds(400));      // smooth
globe.ZoomLevel = 8;                                       // same as ZoomToLevel(8)

// Atomic centre + level (one conversion, also before layout)
globe.SetView(31.23, 121.47, 12);
globe.JumpToLevel(31.23, 121.47, 12);                      // alias

globe.MinZoomLevel = 3;
globe.MaxZoomLevel = 15;

globe.ShowZoomLevel = true;                                // "Z 8.4" badge
globe.ZoomLevelPlacement = GlobeScaleBarPlacement.BottomRight;
```

## Mapping

`OrbitCamera.GetZoomLevel(width, height)` mirrors the tile LOD formulas:

| Mode | Level |
| --- | --- |
| Flat (Web Mercator) | `log2(FlatScaleFactor × viewportHeight / 256)` |
| 3D globe | `log2(2πR·cos(latitude) / (256 × metresPerPixel))` |

`metresPerPixel` is sampled from the terrain around the viewport centre (falling back to the
altitude/FOV estimate when the ray misses the globe). Pass **physical pixels** (logical size ×
`RenderScaling`); the tile selector uses the same dimensions, so the displayed level matches the
drawn tile range.

`OrbitCamera.GetAltitudeForZoomLevel(level, viewHeight)` provides the analytic nadir inverse:

| Mode | Altitude |
| --- | --- |
| Flat | `πR × viewportHeight / (256 × 2^level × tan(FOV/2))` |
| 3D | the same, multiplied by `cos(centre latitude)` |

`GlobeView.ZoomToLevel` uses that value only in flat mode. In 3D it solves the altitude numerically
(bisection in log space against `GetZoomLevel`) so the current pitch and centre latitude are taken into
account and the resulting level matches the request; the analytic helper remains a nadir
approximation. The 3D formula clamps `cos(latitude)` to `0.02` to avoid a degenerate zero near the
poles.

## Atomic centre and level

`SetView(latitude, longitude, zoomLevel)` (also exposed as `JumpToLevel`) sets the centre and the
level together. The altitude is solved once for the **target** centre and the current pitch, against
the physical-pixel viewport, and both values are applied in one camera update. This avoids the
"set centre first, set level later" problem where the intermediate high-altitude viewport clamps a
flat-mode centre. `SetView` before layout stores both the centre and the level and applies them once
the GL surface has measured the final viewport size.

`JumpTo(latitude, longitude, altitude)` remains the altitude-based atomic API.

## Mode switches

`IsFlatMode = true/false` and `SetFlatMode(...)` keep the displayed zoom level by default: the level
is read before the projection changes and the altitude is recomputed in the new projection (3D
includes `cos(centre latitude)`, flat does not). Pass `preserveZoomLevel: false` to keep the raw
altitude instead. When the control is not laid out, the switch keeps the current altitude because no
level can be measured; when the level is measurable but the GL surface has not rendered its first
frame, the level is stored and applied once the physical viewport is known. The switch cancels any
running flight/inertia animation.

## Limits

- `MinZoomLevel` / `MaxZoomLevel` clamp `ZoomToLevel`, `ZoomLevel` and wheel zoom. Setting a limit that
  excludes the current level immediately zooms to the nearest allowed level. If `Max` is lower than
  `Min`, the two values are swapped when clamping.
- `MinAltitude` / `MaxAltitude` still apply as hard camera limits; when both kinds of limit are set the
  stricter one wins.
- `JumpTo`, `FlyTo` and direct `Altitude` assignments are altitude APIs and are **not** clamped by the
  zoom-level limits. Use `ZoomToLevel`, `SetView` or `JumpToLevel` when the level must be respected.
- A `ZoomToLevel` / `SetView` call made before layout stores the request and applies it after the first
  rendered frame, when the GL surface has measured the physical pixel size. `SetView` also stores the
  requested centre; an explicit later centre or altitude assignment cancels that pending request.

## Display

`ShowZoomLevel` draws a small badge such as `Z 8.4`; `ZoomLevelPlacement` reuses
`GlobeScaleBarPlacement`. If the badge shares a corner with the scale bar, it is offset automatically.
The badge is derived from the same physical-pixel dimensions as the LOD, so it stays consistent on
high-DPI displays.

## Sample

```bash
dotnet run --project samples/Globe3D.Demo -- --zoom-level-test
dotnet run --project samples/Globe3D.Demo -- --view-api-test
```

The sample zooms to level 8, clamps a request to level 15 with `MaxZoomLevel = 10`, clamps a request to
level 1 with `MinZoomLevel = 5`, switches to flat mode and verifies the same mapping, then restores the
limits. Results are logged as `[ZOOM]`. `--view-api-test` additionally verifies level-preserving mode
switches, `SetView` and flat-mode centre preservation (`[VIEW]`).
