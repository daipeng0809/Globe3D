using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Globe3D.Avalonia;
using System.Globalization;

namespace Globe3D.Demo;

public partial class MainWindow : Window
{
    private const string MbTilesPath = @"D:\DMap\mapabc\data\satellite.mbtiles";

    private static readonly string TileCacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Globe3D", "TileCache");

    private readonly List<(string Name, Func<ITileSource> Create)> _basemaps = new();
    private ITileSource? _activeSource;
    private string _activeBasemapName = "无（未配置数据源）";
    private int _basemapIndex;
    private Border? _mouseTip;
    private TextBlock? _mouseTipTitle;
    private TextBlock? _mouseTipDetail;

    public MainWindow()
    {
        InitializeComponent();

        SetupGlobe();
        HookEvents();
        StartStatusTimer();
        var autoClose = ParseAutoCloseSeconds();
        if (autoClose > 0)
        {
            Topmost = true;
            WindowState = WindowState.Maximized;
            Opened += (_, _) =>
            {
                Activate();
                Focus();
            };
            DispatcherTimer.RunOnce(() =>
            {
                Program.Log("[DEMO] auto close");
                Close();
            }, TimeSpan.FromSeconds(autoClose));
        }

        Program.Log("window created");
        Globe.RenderingError += (_, msg) => Program.Log("RENDER ERROR: " + msg);

        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--zoom-test"))
        {
            DispatcherTimer.RunOnce(
                () => Globe.FlyTo(31.2304, 121.4737, 260_000d, TimeSpan.FromMilliseconds(1200)),
                TimeSpan.FromSeconds(2));
        }
        else if (args.Contains("--seam-test"))
        {
            // 飞到 180° 经线上空，检查跨经线区域与瓦片的拼接
            DispatcherTimer.RunOnce(
                () => Globe.FlyTo(14d, 180d, 4_500_000d, TimeSpan.FromMilliseconds(1200)),
                TimeSpan.FromSeconds(2));
        }
        else if (args.Contains("--horizon-test"))
        {
            // 倾斜到接近水平，检查同一帧内近景高层级、地平线低层级的分层效果
            DispatcherTimer.RunOnce(() =>
            {
                Globe.Pitch = 62d;
                Globe.FlyTo(30d, 110d, 850_000d, TimeSpan.FromMilliseconds(1200));
            }, TimeSpan.FromSeconds(2));
        }
        else if (args.Contains("--deep-test"))
        {
            // 贴地视角（低于大气层壳体），检查深层级瓦片与图层叠加
            DispatcherTimer.RunOnce(() =>
            {
                Globe.FlyTo(32.9d, 115.7d, 88_000d, TimeSpan.FromMilliseconds(1500));
            }, TimeSpan.FromSeconds(2));
        }
        else if (args.Contains("--street-test"))
        {
            // 街区级视角：四叉树深度大、屏幕只覆盖少量瓦片，用于回归“放大后出现条状空洞”
            DispatcherTimer.RunOnce(() =>
            {
                Globe.FlyTo(31.2304d, 121.4737d, 3_000d, TimeSpan.FromMilliseconds(1500));
            }, TimeSpan.FromSeconds(2));
        }
        else if (args.Contains("--pole-test"))
        {
            // 正对极点：墨卡托在极点附近退化，检查瓦片与区域填充是否仍能闭合
            DispatcherTimer.RunOnce(() =>
            {
                Globe.FlyTo(85d, 180d, 2_200_000d, TimeSpan.FromMilliseconds(1500));
            }, TimeSpan.FromSeconds(2));
        }
        else if (args.Contains("--seam-close"))
        {
            // 斜视让地平线进入画面，区域北边正好贴到球体轮廓：检查填充是否沿轮廓闭合
            DispatcherTimer.RunOnce(() =>
            {
                Globe.Pitch = 45d;
                Globe.FlyTo(35d, 180d, 1_000_000d, TimeSpan.FromMilliseconds(1500));
            }, TimeSpan.FromSeconds(2));
        }
        else if (args.Contains("--hover-test"))
        {
            // 模拟鼠标停在标记点上：验证接近提示、鼠标旁自定义浮层、最近标记点查找
            DispatcherTimer.RunOnce(() =>
            {
                var target = Globe.Layers.SelectMany(l => l.Markers).FirstOrDefault(m => m.Tag is "北京");
                if (target == null)
                    return;

                if (!Globe.Camera.ProjectGeo(target.Position, Globe.Bounds.Width, Globe.Bounds.Height,
                        out var sx, out var sy, out _))
                    return;

                // 鼠标落在图标中心（地表点 + 像素偏移），这样命中到的是图片标记而不是下方的形状标记
                var point = new Point(sx + target.ImageOffset.X, sy + target.ImageOffset.Y);
                Globe.UpdateHoverMarker(point);
                var nearest = Globe.FindNearestMarker(point, out var distance);
                ShowMouseTip(point, nearest, distance, target.Position);
                Program.Log($"[DEMO] hover-test @{point.X:0},{point.Y:0} nearest={nearest?.Tag} " +
                            $"distance={distance:0.0} hover={Globe.HoverMarker?.Tag}");
            }, TimeSpan.FromSeconds(2.5));
        }
        else if (args.Contains("--stats-test"))
        {
            // 性能面板冒烟：自动旋转 + 飞近上海，制造连续渲染与新瓦片读取，再输出一次快照
            DispatcherTimer.RunOnce(() => Globe.AutoRotate = true, TimeSpan.FromSeconds(1.2));
            DispatcherTimer.RunOnce(
                () => Globe.FlyTo(31.2304d, 121.4737d, 300_000d, TimeSpan.FromMilliseconds(1200)),
                TimeSpan.FromSeconds(2d));
            DispatcherTimer.RunOnce(() =>
            {
                var s = Globe.GetPerformanceSnapshot();
                Globe.AutoRotate = false;
                Program.Log($"[PERF] score={s.SmoothnessScore} fps={s.FramesPerSecond:0.0} frames={s.RenderedFrames} " +
                            $"frame={s.FrameMilliseconds:0.00}ms max={s.MaxFrameMilliseconds:0.00}ms " +
                            $"refresh={s.RefreshRateHz:0}Hz tiles={s.DrawnTiles}/{s.CachedTextures} failed={s.FailedTiles} " +
                            $"lod=z{s.MinZoom}-z{s.MaxZoom} select={s.SelectionMilliseconds:0.00}ms " +
                            $"loader=q{s.QueuedTiles}/l{s.LoadingTiles}/f{s.FailedTiles} " +
                            $"disk={s.DiskReadBytesPerSecond:0}B/s ({s.DiskTilesPerSecond:0}t/s, {s.DiskReadMilliseconds:0.0}ms) " +
                            $"net={s.NetworkBytesPerSecond:0}B/s ({s.NetworkTilesPerSecond:0}t/s, {s.NetworkReadMilliseconds:0.0}ms)");
            }, TimeSpan.FromSeconds(5));
        }
        else if (args.Contains("--hit-test"))
        {
            // 背面遮挡回归：先用旧帧缓存的位置做命中测试，再确认重画后仍查不到
            DispatcherTimer.RunOnce(() =>
            {
                var target = Globe.Layers.SelectMany(l => l.Markers).FirstOrDefault(m => m.Tag is "北京");
                if (target == null)
                    return;

                var width = Globe.Bounds.Width;
                var height = Globe.Bounds.Height;
                if (!Globe.Camera.ProjectGeo(target.Position, width, height, out var sx, out var sy, out _))
                    return;

                var stale = new Point(sx + target.ImageOffset.X, sy + target.ImageOffset.Y);

                // 把北京转到球体背面：此时 overlay 尚未重画，_markerHits 还是前视图的缓存
                Globe.JumpTo(39.9d, 116.4d + 180d, 9_500_000d);

                var staleHit = Globe.FindNearestMarker(stale, out var staleDistance);
                Globe.UpdateHoverMarker(stale);
                Program.Log($"[DEMO] hit-test stale-frame @{stale.X:0},{stale.Y:0} " +
                            $"nearest={staleHit?.Tag ?? "null"} distance={staleDistance:0.0} " +
                            $"hover={Globe.HoverMarker?.Tag ?? "null"}");

                DispatcherTimer.RunOnce(() =>
                {
                    var nearest = Globe.FindNearestMarker(stale, out var distance);
                    Program.Log($"[DEMO] hit-test after-repaint nearest={nearest?.Tag ?? "null"} " +
                                $"distance={distance:0.0} hover={Globe.HoverMarker?.Tag ?? "null"}");
                }, TimeSpan.FromSeconds(1.5));
            }, TimeSpan.FromSeconds(2));
        }
    }

    private void SetupGlobe()
    {
        Globe.CenterLatitude = 33d;
        Globe.CenterLongitude = 108d;
        Globe.Altitude = 9_500_000d;
        Globe.Pitch = 0d;
        Globe.StarIntensity = 1.0d;
        Globe.AtmosphereIntensity = 0.95d;
        Globe.LightingIntensity = 0.25d;
        Globe.MinAltitude = 2_000d;

        // 地图密钥从本地 keys.local.json 读取（已加入 .gitignore，不随仓库发布）
        var mapKeys = LocalMapKeys.Load();
        if (mapKeys.LoadedFrom != null)
            Program.Log("[DEMO] 已加载本地地图密钥配置：" + mapKeys.LoadedFrom);
        if (mapKeys.Error != null)
            Program.Log("[DEMO] 地图密钥配置读取失败：" + mapKeys.Error);

        // 底图优先级：本地 MBTiles > 星图地球（JPEG，跨平台）> 天地图
        if (File.Exists(MbTilesPath))
            _basemaps.Add(("本地 MBTiles", () => new MbTilesTileSource(MbTilesPath)));
        else
            Program.Log("[DEMO] MBTiles 未找到：" + MbTilesPath);

        if (mapKeys.Geovis.Count > 0)
            _basemaps.Add(("星图地球", () => OnlineTileSources.CreateGeovisEarth(
                PickKey(mapKeys.Geovis), Path.Combine(TileCacheRoot, "geovis"))));
        if (mapKeys.Tianditu.Count > 0)
            _basemaps.Add(("天地图", () => OnlineTileSources.CreateTiandituSatellite(
                PickKey(mapKeys.Tianditu), Path.Combine(TileCacheRoot, "tianditu"))));

        if (mapKeys.Tianditu.Count == 0 && mapKeys.Geovis.Count == 0)
        {
            Program.Log("[DEMO] 未配置地图密钥：请将 keys.sample.json 复制为 keys.local.json " +
                        "并填入自己的天地图 tk / 星图地球 token（详见 README）");
        }

        ApplyBasemap(FindBasemapIndex());

        var cityLayer = new GlobeLayer("城市");
        AddCity(cityLayer, "北京", 39.9042, 116.4074, MarkerShape.Pin, "#FF6B35");
        AddCity(cityLayer, "上海", 31.2304, 121.4737, MarkerShape.Circle, "#FFC857");
        AddCity(cityLayer, "广州", 23.1291, 113.2644, MarkerShape.Circle, "#5AD2A0");
        AddCity(cityLayer, "成都", 30.5728, 104.0668, MarkerShape.Circle, "#7BB8FF");
        AddCity(cityLayer, "乌鲁木齐", 43.8256, 87.6168, MarkerShape.Diamond, "#C79BFF");
        AddCity(cityLayer, "哈尔滨", 45.8038, 126.5340, MarkerShape.Square, "#66E0E0");
        AddCity(cityLayer, "三亚", 18.2528, 109.5119, MarkerShape.Circle, "#FF8FB1");

        // 三维标记点：高度 400 km
        cityLayer.Markers.Add(new GlobeMarker
        {
            Position = new GeoPoint(35d, 118d, 400_000d),
            Label = "高空目标 400km",
            Shape = MarkerShape.Triangle,
            Fill = new SolidColorBrush(Color.Parse("#FF4D6D")),
            Size = 14d,
            ShowAltitudeLine = true
        });

        Globe.Layers.Add(cityLayer);

        // 图片标记：png / jpg / gif / svg 文件直接作为图标，可分别设置缩放与偏移
        var imageLayer = new GlobeLayer("图片标记");
        AddImageMarker(imageLayer, "北京", 39.9042d, 116.4074d, "pin.png", 0.52d,
            new Vector(0d, -26d), "PNG 图钉：北京（缩放 0.52，向上偏移 26px）");
        AddImageMarker(imageLayer, "上海", 31.2304d, 121.4737d, "radar.svg", 1.30d,
            new Vector(0d, -28d), "SVG 雷达：上海（矢量图标，缩放 1.30）");
        AddImageMarker(imageLayer, "广州", 23.1291d, 113.2644d, "target.jpg", 0.46d,
            new Vector(0d, -18d), "JPG 靶环：广州（缩放 0.46）");
        AddImageMarker(imageLayer, "成都", 30.5728d, 104.0668d, "dot.gif", 1.20d,
            new Vector(0d, -20d), "GIF 圆点：成都（缩放 1.20）");
        AddImageMarker(imageLayer, "三亚", 18.2528d, 109.5119d, "star.svg", 1.60d,
            new Vector(0d, -22d), "SVG 星标：三亚（缩放 1.60）");

        // 这个点关闭了接近提示：鼠标靠近时既不高亮也不弹提示框
        imageLayer.Markers.Add(new GlobeMarker
        {
            Position = new GeoPoint(43.8256d, 87.6168d),
            ImagePath = Path.Combine(AssetsDir, "pin.png"),
            ImageScale = 0.44d,
            ImageOffset = new Vector(0d, -22d),
            HoverTip = "这个点关闭了接近提示（ShowHoverTip = false）",
            ShowHoverTip = false,
            Tag = "乌鲁木齐"
        });

        Globe.Layers.Add(imageLayer);

        var regionLayer = new GlobeLayer("区域");
        var region = new GlobeRegion
        {
            Label = "示例区域",
            Fill = new SolidColorBrush(Color.Parse("#3348A0FF")),
            Stroke = new SolidColorBrush(Color.Parse("#FF9AD1FF")),
            StrokeThickness = 2d
        };
        region.AddPoint(28d, 100d)
              .AddPoint(34d, 98d)
              .AddPoint(36d, 110d)
              .AddPoint(31d, 114d)
              .AddPoint(27d, 108d);
        regionLayer.Regions.Add(region);

        // 跨 ±180° 经线的区域：填充应完整闭合在球面上，不能出现整球填充或空洞
        var seamRegion = new GlobeRegion
        {
            Label = "跨 180° 经线区域",
            Fill = new SolidColorBrush(Color.Parse("#55FFB74D")),
            Stroke = new SolidColorBrush(Color.Parse("#FFFFD08A")),
            StrokeThickness = 2d
        };
        seamRegion.AddPoint(10d, 176d)
                  .AddPoint(18d, 178d)
                  .AddPoint(20d, -178d)
                  .AddPoint(14d, -176d)
                  .AddPoint(6d, 178d);
        regionLayer.Regions.Add(seamRegion);
        if (!Environment.GetCommandLineArgs().Contains("--no-region"))
            Globe.Layers.Add(regionLayer);

        var labelLayer = new GlobeLayer("标注");
        labelLayer.Labels.Add(new GlobeLabel
        {
            Position = new GeoPoint(42d, 103d),
            Text = "内蒙古高原",
            FontSize = 13d,
            Foreground = new SolidColorBrush(Color.Parse("#FFE0B2"))
        });
        labelLayer.Labels.Add(new GlobeLabel
        {
            Position = new GeoPoint(22d, 108d),
            Text = "北部湾",
            FontSize = 13d,
            Foreground = new SolidColorBrush(Color.Parse("#B2EBF2"))
        });
        Globe.Layers.Add(labelLayer);
    }

    private static string PickKey(IReadOnlyList<string> keys) => keys[Random.Shared.Next(keys.Count)];

    /// <summary>演示用图标目录（构建时复制到输出目录）。</summary>
    private static readonly string AssetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");

    /// <summary>演示用图标：运行时画一个圆环徽标并编码成 PNG，再按文件流方式加载为 Bitmap。</summary>
    private static Bitmap CreateBadge(string text, Color fill)
    {
        const int size = 96;
        using var target = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96d, 96d));
        using (var context = target.CreateDrawingContext())
        {
            context.DrawEllipse(new SolidColorBrush(fill), new Pen(Brushes.White, 6d),
                new Point(size / 2d, size / 2d), 41d, 41d);

            var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, 40d, Brushes.White);
            context.DrawText(formatted,
                new Point((size - formatted.Width) / 2d, (size - formatted.Height) / 2d));
        }

        // 走一遍 PNG 编码/解码，确保演示的是真实的图片标记路径
        using var stream = new MemoryStream();
        target.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        return new Bitmap(stream);
    }

    private static void AddImageMarker(GlobeLayer layer, string name, double latitude, double longitude,
        string fileName, double scale, Vector offset, string hoverTip)
    {
        var marker = new GlobeMarker
        {
            Position = new GeoPoint(latitude, longitude),
            ImagePath = Path.Combine(AssetsDir, fileName),
            ImageScale = scale,
            ImageOffset = offset,
            HoverTip = hoverTip,
            Tag = name,
            Size = 10d
        };

        // 素材缺失时回退到运行时生成的 PNG 徽标，保证示例不会因缺文件而看不到标记
        if (!File.Exists(marker.ImagePath))
        {
            marker.Image = CreateBadge(name[..1], Color.Parse("#546E7A"));
            marker.ImageScale = 0.34d;
        }

        layer.Markers.Add(marker);

        // 提前加载一次，把真正解码出的尺寸写进日志，方便核对 png/jpg/gif/svg 四条加载链路
        var image = marker.ResolveImage();
        Program.Log(image != null
            ? $"[DEMO] 图标 {fileName} -> {image.Size.Width:0}x{image.Size.Height:0}, 缩放 {scale:0.##}, 偏移 {offset.X:0},{offset.Y:0}"
            : $"[DEMO] 图标加载失败 {fileName}: {marker.ImageError}");
    }

    /// <summary>支持 --basemap=local|geovis|tianditu 指定启动时的底图。</summary>
    private int FindBasemapIndex()
    {
        var arg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--basemap=", StringComparison.OrdinalIgnoreCase));
        if (arg == null)
            return 0;

        var key = arg["--basemap=".Length..].ToLowerInvariant();
        var namePart = key switch
        {
            "geovis" or "xingtu" => "星图",
            "tianditu" or "tdt" => "天地图",
            _ => "MBTiles"
        };

        for (var i = 0; i < _basemaps.Count; i++)
        {
            if (_basemaps[i].Name.Contains(namePart, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return 0;
    }

    /// <summary>切换底图；旧数据源在切换完成后释放。</summary>
    private void ApplyBasemap(int index)
    {
        if (_basemaps.Count == 0)
        {
            _activeBasemapName = "无（未配置数据源）";
            return;
        }

        index = ((index % _basemaps.Count) + _basemaps.Count) % _basemaps.Count;

        try
        {
            var source = _basemaps[index].Create();
            var previous = _activeSource;
            _activeSource = source;
            _basemapIndex = index;
            _activeBasemapName = _basemaps[index].Name;
            Globe.TileSource = source;
            (previous as IDisposable)?.Dispose();
            var info = source.Info;
            Program.Log($"[DEMO] basemap -> {info.Name} (z{info.MinZoom}-z{info.MaxZoom})");
        }
        catch (Exception ex)
        {
            Program.Log("[DEMO] basemap failed: " + ex.Message);
        }
    }

    private static void AddCity(GlobeLayer layer, string name, double latitude, double longitude,
        MarkerShape shape, string color)
    {
        layer.Markers.Add(new GlobeMarker
        {
            Position = new GeoPoint(latitude, longitude, 0d),
            Label = name,
            Shape = shape,
            Fill = new SolidColorBrush(Color.Parse(color)),
            Stroke = Brushes.White,
            Size = 11d,
            FontSize = 12d,
            ToolTip = name
        });
    }

    private void HookEvents()
    {
        var statusText = this.FindControl<TextBlock>("StatusText")!;
        var hintBorder = this.FindControl<Border>("HintBorder")!;
        var hintText = this.FindControl<TextBlock>("HintText")!;

        _mouseTip = this.FindControl<Border>("MouseTip");
        _mouseTipTitle = this.FindControl<TextBlock>("MouseTipTitle");
        _mouseTipDetail = this.FindControl<TextBlock>("MouseTipDetail");

        // 鼠标移动事件：靠近标记点时在鼠标旁展示自定义内容
        Globe.GlobePointerMoved += (_, e) =>
            ShowMouseTip(e.ScreenPosition, e.NearestMarker, e.NearestDistance, e.Location);

        Globe.MarkerClicked += (_, e) =>
        {
            hintBorder.IsVisible = true;
            var name = e.Marker.Tag as string ?? e.Marker.Label ?? "(未命名)";
            var nearest = Globe.FindNearestMarker(e.Marker.Position.Latitude, e.Marker.Position.Longitude);
            hintText.Text = $"标记：{name}  {e.Marker.Position}  最近标记：{nearest?.Tag ?? "-"}";
        };

        Globe.MarkerHoverChanged += (_, e) =>
        {
            if (e.Marker is { } marker)
            {
                hintBorder.IsVisible = true;
                hintText.Text = $"接近：{marker.Tag as string ?? marker.HoverTip ?? marker.Label ?? "(标记点)"}";
            }
        };

        Globe.GlobeClicked += (_, e) =>
        {
            hintBorder.IsVisible = true;
            if (e.Location is { } p)
            {
                // 点击空白处也能找到最近的标记点对象（按地表大圆距离）
                var nearest = Globe.FindNearestMarker(p.Latitude, p.Longitude);
                hintText.Text = $"点击地表：纬度 {p.Latitude:0.0000}  经度 {p.Longitude:0.0000}  " +
                                $"最近标记：{nearest?.Tag as string ?? "-"}";
            }
            else
            {
                hintText.Text = "点击：太空区域";
            }
        };

        Globe.RenderingError += (_, message) => statusText.Text = "渲染错误：" + message;

        this.FindControl<Button>("ResetButton")!.Click += (_, _) =>
        {
            Globe.JumpTo(33d, 108d, 9_500_000d);
            Globe.Pitch = 0d;
            Globe.Refresh();
        };

        this.FindControl<Button>("FlyButton")!.Click += (_, _) =>
        {
            Globe.FlyTo(31.2304, 121.4737, 1_200_000d, TimeSpan.FromMilliseconds(1400));
        };

        var rotateButton = this.FindControl<Button>("RotateButton")!;
        rotateButton.Click += (_, _) =>
        {
            Globe.AutoRotate = !Globe.AutoRotate;
            rotateButton.Content = Globe.AutoRotate ? "停止旋转" : "自动旋转";
        };

        var basemapButton = this.FindControl<Button>("BasemapButton")!;
        if (_basemaps.Count > 0)
            basemapButton.Content = "底图：" + _basemaps[_basemapIndex].Name;
        else
        {
            basemapButton.Content = "底图：未配置";
            basemapButton.IsEnabled = false;
        }

        basemapButton.Click += (_, _) =>
        {
            if (_basemaps.Count == 0)
                return;

            ApplyBasemap(_basemapIndex + 1);
            basemapButton.Content = "底图：" + _basemaps[_basemapIndex].Name;
        };

        // 性能面板：演示 ShowPerformancePanel 开关
        var statsButton = this.FindControl<Button>("StatsButton")!;
        Globe.ShowPerformancePanel = true;
        statsButton.Click += (_, _) =>
        {
            Globe.ShowPerformancePanel = !Globe.ShowPerformancePanel;
            statsButton.Content = Globe.ShowPerformancePanel ? "性能面板：开" : "性能面板：关";
        };
    }

    /// <summary>鼠标旁的自定义浮层：靠近标记点时显示名称与距离，否则隐藏。</summary>
    private void ShowMouseTip(Point screenPosition, GlobeMarker? marker, double distance, GeoPoint? location)
    {
        if (_mouseTip == null)
            return;

        if (marker == null || distance > Globe.MarkerHoverRadius)
        {
            _mouseTip.IsVisible = false;
            return;
        }

        if (_mouseTipTitle != null)
            _mouseTipTitle.Text = marker.Tag as string ?? marker.Label ?? "标记点";

        if (_mouseTipDetail != null)
        {
            _mouseTipDetail.Text = location is { } geo
                ? $"距离 {distance:0}px · {geo.Latitude:0.00}°, {geo.Longitude:0.00}°"
                : $"距离 {distance:0}px";
        }

        // 放在库内自绘提示框的下方（鼠标右下 18/78 像素），靠近屏幕边缘时翻到另一侧
        var width = Globe.Bounds.Width;
        var height = Globe.Bounds.Height;
        var x = screenPosition.X + 18d;
        var y = screenPosition.Y + 78d;
        if (x > width - 200d)
            x = Math.Max(4d, screenPosition.X - 200d);
        if (y > height - 70d)
            y = Math.Max(4d, screenPosition.Y - 120d);

        _mouseTip.Margin = new Thickness(x, y, 0d, 0d);
        _mouseTip.IsVisible = true;
    }

    private void StartStatusTimer()
    {
        var statusText = this.FindControl<TextBlock>("StatusText")!;
        var lastHistogram = string.Empty;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) =>
        {
            var (zoom, tiles, textures, failed) = Globe.GetDiagnostics();
            var (lodMin, lodMax) = Globe.GetDrawnZoomRange();
            var lod = lodMin < 0 ? $"z{zoom}" : $"z{lodMin}-z{lodMax}";
            statusText.Text =
                $"纬度 {Globe.CenterLatitude:0.00}°  经度 {Globe.CenterLongitude:0.00}°  " +
                $"高度 {Globe.Altitude / 1000d:N0} km  层级 {lod}  " +
                $"绘制瓦片 {tiles}  纹理 {textures}  失败 {failed}  " +
                $"底图 {_activeBasemapName}";

            var histogram = Globe.GetDrawnLevelHistogram();
            if (histogram.Count > 0)
            {
                var detail = string.Join(" ", histogram.Select(h => $"z{h.Level}:{h.Count}"));
                var selectionMs = Globe.GetLastSelectionMilliseconds();
                if (detail != lastHistogram || selectionMs > 4d)
                {
                    lastHistogram = detail;
                    Program.Log($"[LOD] {detail} 选瓦片 {selectionMs:0.00} ms " +
                                 $"@{Globe.CenterLatitude:0.0},{Globe.CenterLongitude:0.0} alt={Globe.Altitude / 1000d:0}km");
                }
            }
        });
        timer.Start();
    }

    private static double ParseAutoCloseSeconds()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--auto-close" && double.TryParse(args[i + 1], out var seconds))
                return seconds;
        }

        return 0;
    }
}
