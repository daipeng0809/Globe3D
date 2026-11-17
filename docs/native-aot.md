# Native AOT & Trimming

`Globe3D.Avalonia` is AOT- and trim-friendly: the control uses no reflection, `dynamic`, runtime XAML
parsing or serializer frameworks, and the library is built with `IsAotCompatible=true`, so the
.NET AOT/trim analyzers run on every build with zero warnings.

The bundled demo enables `PublishAot`, so the whole sample — globe rendering, MBTiles, online tiles,
image markers, hover/hit testing and the performance panel — can be published as a self-contained
native executable.

## Publishing the demo

```bash
dotnet publish samples/Globe3D.Demo -c Release -r win-x64
```

Windows requirements:

- .NET 8 SDK or newer (the demo targets `net8.0`)
- MSVC C++ toolchain: Visual Studio "Desktop development with C++" workload (the Native AOT linker
  needs `link.exe`; the .NET SDK locates it automatically)

The output lands in `samples/Globe3D.Demo/bin/Release/net8.0/win-x64/publish/`:

| File | Purpose |
| --- | --- |
| `Globe3D.Demo.exe` | Native executable, ~33 MB, no .NET runtime required on the target machine |
| `av_libglesv2.dll` | ANGLE, maps OpenGL ES 3.0 onto D3D11 on Windows |
| `e_sqlite3.dll` | SQLite native library used by `MbTilesTileSource` |
| `libSkiaSharp.dll`, `libHarfBuzzSharp.dll` | Bitmap and text rendering used by Avalonia |
| `*.pdb` | Optional symbols (safe to omit from a shipped build) |

Run the native build exactly like the JIT build, including the regression switches:

```bash
cd samples/Globe3D.Demo/bin/Release/net8.0/win-x64/publish
./Globe3D.Demo.exe --stats-test --auto-close 8
```

The demo reads map service keys from `keys.local.json` (git-ignored). When the file exists it is
copied to the publish folder; never ship that file. Remove it before distribution, or keep your keys
in `%LocalAppData%\Globe3D\keys.local.json` instead.

## Verified scenarios

The published win-x64 native executable was exercised with the same regression switches as the JIT
build; results matched:

| Command | Area | Result |
| --- | --- | --- |
| `--stats-test` | Continuous rendering, flight, deep zoom, MBTiles, performance panel | `score≈81 fps≈38 frame≈2.3 ms refresh≈59 Hz lod=z8-z9 disk≈254 KB/s`, exit code 0 |
| `--zoom-test` | Fly to 260 km over Shanghai | exit code 0, no trimming/AOT exceptions |
| `--street-test` | 3 km street-level view, deepest quadtree levels | exit code 0 |
| `--pole-test` | Polar cap fill (Mercator degeneration) | exit code 0 |
| `--seam-test` | Region crossing ±180° | exit code 0 |
| `--hover-test` | Marker hover and nearest-marker query | `nearest=北京 distance=0.0` |
| `--hit-test` | Back-side marker hit regression | correctly returns `null` before and after repaint |
| `--basemap=geovis --stats-test` | Online HTTP tiles + disk cache + `HttpClient` | `net≈280 KB/s (69 t/s)`, cache reads on disk, exit code 0 |

PNG / JPG / GIF / SVG marker decoding, region and label drawing, the render loop, and the
performance HUD all behave identically to the framework-dependent build.

## Consumer checklist

1. Enable Native AOT in the application project:

   ```xml
   <PropertyGroup>
     <PublishAot>true</PublishAot>
     <BuiltInComInteropSupport>true</BuiltInComInteropSupport> <!-- Windows / Avalonia COM interop -->
   </PropertyGroup>
   ```

2. Publish with a runtime identifier, e.g. `dotnet publish -c Release -r win-x64`.
3. Keep XAML compiled: `AvaloniaUseCompiledBindingsByDefault=true` and `{CompiledBinding}` instead
   of `{Binding}`. Do not load uncompiled XAML with `XamlReader` at runtime.
4. Keep the native assets that ship next to the executable (ANGLE, `e_sqlite3.dll`, Skia/HarfBuzz).
   If you post-process the publish folder, do not drop them.
5. Do not trim the `Globe3D.Avalonia` assembly away — the XAML compiler emits direct references to
   the control, so the trimmer keeps what is used; no extra `rd.xml`, `DynamicDependency` or
   `TrimmerRootDescriptor` is required.

## Notes and limitations

- `PublishAot` only affects `dotnet publish`. `dotnet build` / `dotnet run` still use the JIT and the
  AOT analyzers simply validate the code at build time.
- Native AOT binaries are larger on disk than the managed DLLs (the demo executable is ~33 MB) but
  start faster and have no runtime dependency.
- `MbTilesTileSource` requires the SQLite native library; on platforms where `SQLitePCLRaw` cannot
  provide a prebuilt binary, supply your own and point `SQLitePCL.Batteries_V2.Init()` accordingly.
- Frame-rate numbers may differ slightly between JIT and AOT builds; the performance panel reports
  the AOT build's own timing.
- Trimming can still remove reflection-based features of *your* application (custom converters,
  serializers, plugins). Annotate those in the usual .NET way; nothing in this control needs it.
- The library promotes `IL2026` / `IL2067` / `IL3050` diagnostics to build errors, so an accidental
  reflection dependency fails the build instead of silently degrading AOT/trim compatibility.
  The sample enables `BuiltInComInteropSupport` only on Windows, where Avalonia's COM interop
  (clipboard, drag and drop) needs it.
