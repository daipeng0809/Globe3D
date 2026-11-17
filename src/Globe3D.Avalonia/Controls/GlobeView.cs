using System.Collections.Specialized;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Metadata;
using Avalonia.Threading;

namespace Globe3D.Avalonia;

/// <summary>标记点点击事件参数。</summary>
public sealed class GlobeMarkerEventArgs : EventArgs
{
    public GlobeMarkerEventArgs(GlobeMarker marker)
    {
        Marker = marker;
    }

    public GlobeMarker Marker { get; }
}

/// <summary>标记点接近事件参数。鼠标离开所有标记点时 <see cref="Marker"/> 为 null。</summary>
public sealed class GlobeMarkerHoverEventArgs : EventArgs
{
    public GlobeMarkerHoverEventArgs(GlobeMarker? marker, Point screenPosition)
    {
        Marker = marker;
        ScreenPosition = screenPosition;
    }

    /// <summary>当前接近的标记点；null 表示鼠标已离开所有标记点。</summary>
    public GlobeMarker? Marker { get; }

    /// <summary>鼠标在控件内的位置。</summary>
    public Point ScreenPosition { get; }
}

/// <summary>
/// 鼠标在地球上移动的事件参数：包含屏幕坐标、鼠标正下方的经纬度，
/// 以及离鼠标最近的标记点（不限距离，便于在鼠标旁展示自定义内容）。
/// </summary>
public sealed class GlobePointerEventArgs : EventArgs
{
    public GlobePointerEventArgs(Point screenPosition, GeoPoint? location,
        GlobeMarker? nearestMarker, double nearestDistance)
    {
        ScreenPosition = screenPosition;
        Location = location;
        NearestMarker = nearestMarker;
        NearestDistance = nearestDistance;
    }

    /// <summary>鼠标在控件内的位置。</summary>
    public Point ScreenPosition { get; }

    /// <summary>鼠标正下方的地表经纬度；指到太空时为 null。</summary>
    public GeoPoint? Location { get; }

    /// <summary>离鼠标最近的标记点（不限距离）；当前没有可见标记点时为 null。</summary>
    public GlobeMarker? NearestMarker { get; }

    /// <summary>鼠标到最近标记点图标包围盒的距离（像素，盒内为 0）。</summary>
    public double NearestDistance { get; }
}

/// <summary>地球点击事件参数（返回点击处的地理坐标；点在太空时为 null）。</summary>
public sealed class GlobeClickEventArgs : EventArgs
{
    public GlobeClickEventArgs(GeoPoint? location, Point screenPosition, bool isRightButton)
    {
        Location = location;
        ScreenPosition = screenPosition;
        IsRightButton = isRightButton;
    }

    /// <summary>点击到的地表经纬度；未命中地球时为 null。</summary>
    public GeoPoint? Location { get; }

    public Point ScreenPosition { get; }

    public bool IsRightButton { get; }
}

/// <summary>
/// 三维地球控件。基于 OpenGL 渲染影像瓦片与程序化星空，并在上层叠加
/// 标记点、区域与文字标注，支持旋转、缩放、倾斜与飞行定位。
/// </summary>
public partial class GlobeView : Panel
{
    private readonly GlobeSurface _surface;
    private readonly GlobeOverlay _overlay;
    private bool _syncing;
    private IDisposable? _ownedTileSource;
    private string? _lastError;
    private GlobeMarker? _hoverMarker;
    private Cursor? _baseCursor;
    private bool _hoverCursorApplied;

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    public GlobeView()
    {
        Background = Brushes.Transparent;
        ClipToBounds = true;
        Focusable = true;

        _surface = new GlobeSurface();

        _overlay = new GlobeOverlay
        {
            IsHitTestVisible = false,
            Camera = _surface.Camera,
            Layers = Layers,
            HoverRadius = MarkerHoverRadius,
            Measurements = _measurements
        };

        Children.Add(_surface);
        Children.Add(_overlay);
        Children.Add(_performancePanel);

        _surface.FrameRendered += OnSurfaceFrameRendered;
        _surface.ErrorOccurred += OnSurfaceError;
        Layers.CollectionChanged += OnLayersCollectionChanged;
    }

    private OrbitCamera _camera => _surface.Camera;

    #region 属性

    /// <summary>观察中心纬度（度）。</summary>
    public static readonly StyledProperty<double> CenterLatitudeProperty =
        AvaloniaProperty.Register<GlobeView, double>(nameof(CenterLatitude), 32d);

    /// <summary>观察中心经度（度）。</summary>
    public static readonly StyledProperty<double> CenterLongitudeProperty =
        AvaloniaProperty.Register<GlobeView, double>(nameof(CenterLongitude), 108d);

    /// <summary>相机离地高度（米）。</summary>
    public static readonly StyledProperty<double> AltitudeProperty =
        AvaloniaProperty.Register<GlobeView, double>(nameof(Altitude), 10_200_000d);

    /// <summary>俯仰角（度）。0 为正俯视，最大约 88。</summary>
    public static readonly StyledProperty<double> PitchProperty =
        AvaloniaProperty.Register<GlobeView, double>(nameof(Pitch));

    /// <summary>航向角（度）。</summary>
    public static readonly StyledProperty<double> HeadingProperty =
        AvaloniaProperty.Register<GlobeView, double>(nameof(Heading));

    /// <summary>最小离地高度（米），控制最大放大倍率。</summary>
    public static readonly StyledProperty<double> MinAltitudeProperty =
        AvaloniaProperty.Register<GlobeView, double>(nameof(MinAltitude), 10_000d);

    /// <summary>最大离地高度（米），控制最大缩小倍率。</summary>
    public static readonly StyledProperty<double> MaxAltitudeProperty =
        AvaloniaProperty.Register<GlobeView, double>(nameof(MaxAltitude), 51_000_000d);

    /// <summary>瓦片数据源。</summary>
    public static readonly StyledProperty<ITileSource?> TileSourceProperty =
        AvaloniaProperty.Register<GlobeView, ITileSource?>(nameof(TileSource));

    /// <summary>MBTiles 文件路径（设置后自动创建离线瓦片源）。</summary>
    public static readonly StyledProperty<string?> MbTilesPathProperty =
        AvaloniaProperty.Register<GlobeView, string?>(nameof(MbTilesPath));

    /// <summary>是否绘制星空。</summary>
    public static readonly StyledProperty<bool> ShowStarsProperty =
        AvaloniaProperty.Register<GlobeView, bool>(nameof(ShowStars), true);

    /// <summary>星空亮度系数。</summary>
    public static readonly StyledProperty<double> StarIntensityProperty =
        AvaloniaProperty.Register<GlobeView, double>(nameof(StarIntensity), 1.0d);

    /// <summary>大气颜色。</summary>
    public static readonly StyledProperty<Color> AtmosphereColorProperty =
        AvaloniaProperty.Register<GlobeView, Color>(nameof(AtmosphereColor), Color.FromRgb(90, 168, 255));

    /// <summary>大气外发光强度。</summary>
    public static readonly StyledProperty<double> AtmosphereIntensityProperty =
        AvaloniaProperty.Register<GlobeView, double>(nameof(AtmosphereIntensity), 0.85d);

    /// <summary>地表方向光明暗强度（0-1）。</summary>
    public static readonly StyledProperty<double> LightingIntensityProperty =
        AvaloniaProperty.Register<GlobeView, double>(nameof(LightingIntensity), 0.28d);

    /// <summary>背景颜色（星空和大气之后仍可见的深空底色）。</summary>
    public static readonly StyledProperty<Color> SpaceColorProperty =
        AvaloniaProperty.Register<GlobeView, Color>(nameof(SpaceColor), Color.FromRgb(3, 5, 12));

    /// <summary>是否绘制文字标注。</summary>
    public static readonly StyledProperty<bool> ShowLabelsProperty =
        AvaloniaProperty.Register<GlobeView, bool>(nameof(ShowLabels), true);

    /// <summary>是否使用二维平面模式（Web 墨卡托平面）；false 时为三维地球。</summary>
    public static readonly StyledProperty<bool> IsFlatModeProperty =
        AvaloniaProperty.Register<GlobeView, bool>(nameof(IsFlatMode));

    /// <summary>是否自动旋转。</summary>
    public static readonly StyledProperty<bool> AutoRotateProperty =
        AvaloniaProperty.Register<GlobeView, bool>(nameof(AutoRotate));

    /// <summary>自动旋转速度（度/秒）。</summary>
    public static readonly StyledProperty<double> AutoRotateSpeedProperty =
        AvaloniaProperty.Register<GlobeView, double>(nameof(AutoRotateSpeed), 6d);

    /// <summary>滚轮缩放灵敏度。</summary>
    public static readonly StyledProperty<double> ZoomSensitivityProperty =
        AvaloniaProperty.Register<GlobeView, double>(nameof(ZoomSensitivity), 1.0d);

    /// <summary>鼠标接近标记点的默认判定半径（像素），标记点自身设置 HoverRadius 时以标记点为准。</summary>
    public static readonly StyledProperty<double> MarkerHoverRadiusProperty =
        AvaloniaProperty.Register<GlobeView, double>(nameof(MarkerHoverRadius), 18d);

    /// <summary>点击时判定命中标记点的最大距离（像素），允许点击落在图标旁边一点。</summary>
    public static readonly StyledProperty<double> MarkerHitToleranceProperty =
        AvaloniaProperty.Register<GlobeView, double>(nameof(MarkerHitTolerance), 6d);

    /// <summary>是否在视口角落显示比例尺。</summary>
    public static readonly StyledProperty<bool> ShowScaleBarProperty =
        AvaloniaProperty.Register<GlobeView, bool>(nameof(ShowScaleBar));

    /// <summary>比例尺停靠位置。</summary>
    public static readonly StyledProperty<GlobeScaleBarPlacement> ScaleBarPlacementProperty =
        AvaloniaProperty.Register<GlobeView, GlobeScaleBarPlacement>(nameof(ScaleBarPlacement),
            GlobeScaleBarPlacement.BottomLeft);

    /// <summary>当前启用的测量工具。</summary>
    public static readonly StyledProperty<GlobeTool> ActiveToolProperty =
        AvaloniaProperty.Register<GlobeView, GlobeTool>(nameof(ActiveTool), GlobeTool.None);

    public double CenterLatitude
    {
        get => GetValue(CenterLatitudeProperty);
        set => SetValue(CenterLatitudeProperty, value);
    }

    public double CenterLongitude
    {
        get => GetValue(CenterLongitudeProperty);
        set => SetValue(CenterLongitudeProperty, value);
    }

    public double Altitude
    {
        get => GetValue(AltitudeProperty);
        set => SetValue(AltitudeProperty, value);
    }

    public double Pitch
    {
        get => GetValue(PitchProperty);
        set => SetValue(PitchProperty, value);
    }

    public double Heading
    {
        get => GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    public double MinAltitude
    {
        get => GetValue(MinAltitudeProperty);
        set => SetValue(MinAltitudeProperty, value);
    }

    public double MaxAltitude
    {
        get => GetValue(MaxAltitudeProperty);
        set => SetValue(MaxAltitudeProperty, value);
    }

    public ITileSource? TileSource
    {
        get => GetValue(TileSourceProperty);
        set => SetValue(TileSourceProperty, value);
    }

    public string? MbTilesPath
    {
        get => GetValue(MbTilesPathProperty);
        set => SetValue(MbTilesPathProperty, value);
    }

    public bool ShowStars
    {
        get => GetValue(ShowStarsProperty);
        set => SetValue(ShowStarsProperty, value);
    }

    public double StarIntensity
    {
        get => GetValue(StarIntensityProperty);
        set => SetValue(StarIntensityProperty, value);
    }

    public Color AtmosphereColor
    {
        get => GetValue(AtmosphereColorProperty);
        set => SetValue(AtmosphereColorProperty, value);
    }

    public double AtmosphereIntensity
    {
        get => GetValue(AtmosphereIntensityProperty);
        set => SetValue(AtmosphereIntensityProperty, value);
    }

    public double LightingIntensity
    {
        get => GetValue(LightingIntensityProperty);
        set => SetValue(LightingIntensityProperty, value);
    }

    public Color SpaceColor
    {
        get => GetValue(SpaceColorProperty);
        set => SetValue(SpaceColorProperty, value);
    }

    public bool ShowLabels
    {
        get => GetValue(ShowLabelsProperty);
        set => SetValue(ShowLabelsProperty, value);
    }

    /// <summary>
    /// 是否使用二维平面模式：开启后地图以 Web 墨卡托平面展示（关闭星空/大气/倾斜），
    /// 关闭时恢复现有三维地球展示。
    /// </summary>
    public bool IsFlatMode
    {
        get => GetValue(IsFlatModeProperty);
        set => SetValue(IsFlatModeProperty, value);
    }

    public bool AutoRotate
    {
        get => GetValue(AutoRotateProperty);
        set => SetValue(AutoRotateProperty, value);
    }

    public double AutoRotateSpeed
    {
        get => GetValue(AutoRotateSpeedProperty);
        set => SetValue(AutoRotateSpeedProperty, value);
    }

    public double ZoomSensitivity
    {
        get => GetValue(ZoomSensitivityProperty);
        set => SetValue(ZoomSensitivityProperty, value);
    }

    public double MarkerHoverRadius
    {
        get => GetValue(MarkerHoverRadiusProperty);
        set => SetValue(MarkerHoverRadiusProperty, value);
    }

    public double MarkerHitTolerance
    {
        get => GetValue(MarkerHitToleranceProperty);
        set => SetValue(MarkerHitToleranceProperty, value);
    }

    public bool ShowScaleBar
    {
        get => GetValue(ShowScaleBarProperty);
        set => SetValue(ShowScaleBarProperty, value);
    }

    public GlobeScaleBarPlacement ScaleBarPlacement
    {
        get => GetValue(ScaleBarPlacementProperty);
        set => SetValue(ScaleBarPlacementProperty, value);
    }

    /// <summary>
    /// 当前测量工具：默认 <see cref="GlobeTool.None"/>。启用后左键单击添加测量点，
    /// 双击/右键完成或取消，测量结果以矢量方式绘制在地表上。
    /// </summary>
    public GlobeTool ActiveTool
    {
        get => GetValue(ActiveToolProperty);
        set => SetValue(ActiveToolProperty, value);
    }

    /// <summary>图层集合。每个图层可包含标记点、区域、矢量图形与文字标注。</summary>
    [Content]
    public AvaloniaList<GlobeLayer> Layers { get; } = new();

    /// <summary>底层相机，可直接修改后调用 <see cref="Refresh"/>。</summary>
    public OrbitCamera Camera => _surface.Camera;

    /// <summary>最近一次错误信息。</summary>
    public string? LastError => _lastError;

    /// <summary>当前鼠标接近的标记点（没有则为 null）。</summary>
    public GlobeMarker? HoverMarker => _hoverMarker;

    #endregion

    #region 事件

    /// <summary>标记点被点击。</summary>
    public event EventHandler<GlobeMarkerEventArgs>? MarkerClicked;

    /// <summary>鼠标接近的标记点发生变化（含鼠标离开，此时 Marker 为 null）。</summary>
    public event EventHandler<GlobeMarkerHoverEventArgs>? MarkerHoverChanged;

    /// <summary>鼠标在地球上移动（拖动旋转/倾斜时不触发）。</summary>
    public event EventHandler<GlobePointerEventArgs>? GlobePointerMoved;

    /// <summary>地球被左键单击（未拖动）。</summary>
    public event EventHandler<GlobeClickEventArgs>? GlobeClicked;

    /// <summary>相机变化。</summary>
    public event EventHandler? CameraChanged;

    /// <summary>渲染错误。</summary>
    public event EventHandler<string>? RenderingError;

    #endregion

    #region 属性同步

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_syncing)
            return;

        if (change.Property == CenterLatitudeProperty || change.Property == CenterLongitudeProperty ||
            change.Property == AltitudeProperty || change.Property == PitchProperty ||
            change.Property == HeadingProperty || change.Property == MinAltitudeProperty ||
            change.Property == MaxAltitudeProperty)
        {
            // 用户直接设置高度时，取消布局前暂存的缩放级别请求
            if (change.Property == AltitudeProperty)
                _pendingZoomLevel = null;

            ApplyCameraProperties();
            Refresh();
        }
        else if (change.Property == ShowStarsProperty)
        {
            _surface.ShowStars = ShowStars;
            Refresh();
        }
        else if (change.Property == StarIntensityProperty)
        {
            _surface.StarIntensity = (float)StarIntensity;
            Refresh();
        }
        else if (change.Property == AtmosphereColorProperty)
        {
            _surface.AtmosphereColor = AtmosphereColor;
            Refresh();
        }
        else if (change.Property == AtmosphereIntensityProperty)
        {
            _surface.AtmosphereIntensity = (float)AtmosphereIntensity;
            Refresh();
        }
        else if (change.Property == LightingIntensityProperty)
        {
            _surface.LightingIntensity = (float)LightingIntensity;
            Refresh();
        }
        else if (change.Property == SpaceColorProperty)
        {
            _surface.BackgroundColor = SpaceColor;
            Refresh();
        }
        else if (change.Property == ShowLabelsProperty)
        {
            _overlay.ShowLabels = ShowLabels;
            _overlay.InvalidateVisual();
        }
        else if (change.Property == IsFlatModeProperty)
        {
            _camera.IsFlat = IsFlatMode;
            _camera.Normalize();

            // 模式切换后旧的屏幕命中/悬停结果不再有效，清掉悬停并在下一帧重建
            UpdateHoverMarker(null);
            InvalidateAll();
        }
        else if (change.Property == AutoRotateProperty || change.Property == AutoRotateSpeedProperty)
        {
            if (AutoRotate)
                StartTicker();
            else
                StopTickerIfIdle();
        }
        else if (change.Property == MarkerHoverRadiusProperty)
        {
            _overlay.HoverRadius = MarkerHoverRadius;
        }
        else if (change.Property == ShowScaleBarProperty || change.Property == ScaleBarPlacementProperty)
        {
            _overlay.ShowScaleBar = ShowScaleBar;
            _overlay.ScaleBarPlacement = ScaleBarPlacement;
            _overlay.InvalidateVisual();
        }
        else if (change.Property == ActiveToolProperty)
        {
            OnActiveToolChanged();
        }
        else if (change.Property == MinZoomLevelProperty || change.Property == MaxZoomLevelProperty)
        {
            OnZoomLimitsChanged();
        }
        else if (change.Property == ShowZoomLevelProperty || change.Property == ZoomLevelPlacementProperty)
        {
            OnZoomDisplayChanged();
        }
        else if (change.Property == TileSourceProperty)
        {
            ApplyTileSource();
        }
        else if (change.Property == MbTilesPathProperty)
        {
            ApplyMbTilesPath();
        }
        else if (change.Property == ShowPerformancePanelProperty)
        {
            ApplyPerformancePanelVisibility();
        }
    }

    private void ApplyCameraProperties()
    {
        var camera = _camera;
        camera.CenterLatitude = CenterLatitude;
        camera.CenterLongitude = CenterLongitude;
        camera.Pitch = Pitch;
        camera.Heading = Heading;
        camera.MinDistance = 1d + Math.Max(1d, MinAltitude) / GeoMath.EarthRadius;
        camera.MaxDistance = 1d + Math.Max(MinAltitude + 1d, MaxAltitude) / GeoMath.EarthRadius;
        camera.SetAltitude(Altitude);
        UpdateCameraAspect();
    }

    private void UpdateCameraAspect()
    {
        var aspect = Bounds.Height > 0 ? Bounds.Width / Bounds.Height : 1d;
        var camera = _camera;
        camera.Update(aspect);

        // 平面模式会按视口/缩放把中心限制在地图范围内，把限制后的结果同步回属性，
        // 避免状态栏或绑定仍显示限制前的经纬度
        if (camera.IsFlat &&
            (Math.Abs(camera.CenterLatitude - CenterLatitude) > 1e-9d ||
             Math.Abs(MathUtil.NormalizeLongitudeDelta(camera.CenterLongitude - CenterLongitude)) > 1e-9d))
        {
            SyncPropertiesFromCamera();
        }
    }

    /// <summary>把相机的当前状态写回控件属性（不触发反向同步）。</summary>
    internal void SyncPropertiesFromCamera()
    {
        _syncing = true;
        try
        {
            SetCurrentValue(CenterLatitudeProperty, _camera.CenterLatitude);
            SetCurrentValue(CenterLongitudeProperty, _camera.CenterLongitude);
            SetCurrentValue(PitchProperty, _camera.Pitch);
            SetCurrentValue(HeadingProperty, _camera.Heading);
            SetCurrentValue(AltitudeProperty, _camera.AltitudeMeters);
        }
        finally
        {
            _syncing = false;
        }

        CameraChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyTileSource()
    {
        _surface.SetTileSource(TileSource);
        Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // 控件卸载时 GL 表面会释放 loader，重新挂载后需要把数据源补回去
        ApplyTileSource();

        // 卸载时性能定时器已停止，重新挂载后按当前开关状态恢复
        if (ShowPerformancePanel)
            StartPerformanceTimer();
    }

    private void ApplyMbTilesPath()
    {
        var path = MbTilesPath;
        _ownedTileSource?.Dispose();
        _ownedTileSource = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            if (TileSource == null)
                _surface.SetTileSource(null);
            return;
        }

        try
        {
            var source = new MbTilesTileSource(path);
            _ownedTileSource = source;
            _syncing = true;
            try
            {
                SetCurrentValue(TileSourceProperty, source);
            }
            finally
            {
                _syncing = false;
            }

            _surface.SetTileSource(source);
            Refresh();
        }
        catch (Exception ex)
        {
            ReportError("加载 MBTiles 失败：" + ex.Message);
        }
    }

    private void OnSurfaceError(string message) => ReportError(message);

    private void ReportError(string message)
    {
        _lastError = message;
        RenderingError?.Invoke(this, message);
    }

    #endregion

    #region 布局与刷新

    /// <summary>请求重绘地球与覆盖层。</summary>
    public void Refresh()
    {
        InvalidateAll();
    }

    private void InvalidateAll()
    {
        UpdateCameraAspect();
        _surface.Invalidate();
        _overlay.InvalidateVisual();
    }

    private void OnSurfaceFrameRendered()
    {
        // 首帧后 GL 表面已测出真实的物理像素尺寸；延迟到 UI 线程应用布局前的缩放请求
        if (_pendingZoomLevel is not null)
            Dispatcher.UIThread.Post(ApplyPendingZoomLevel, DispatcherPriority.Render);

        _overlay.InvalidateVisual();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        UpdateCameraAspect();
        return result;
    }

    #endregion

    #region 图层管理

    private void OnLayersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (GlobeLayer layer in e.OldItems)
                DetachLayer(layer);
        }

        if (e.NewItems != null)
        {
            foreach (GlobeLayer layer in e.NewItems)
                AttachLayer(layer);
        }

        _overlay.InvalidateVisual();
        StartTicker();
    }

    private void AttachLayer(GlobeLayer layer)
    {
        layer.PropertyChanged += OnLayerPropertyChanged;
        layer.Markers.CollectionChanged += OnLayerContentChanged;
        layer.Regions.CollectionChanged += OnLayerContentChanged;
        layer.Vectors.CollectionChanged += OnLayerContentChanged;
        layer.Labels.CollectionChanged += OnLayerContentChanged;
    }

    private void DetachLayer(GlobeLayer layer)
    {
        layer.PropertyChanged -= OnLayerPropertyChanged;
        layer.Markers.CollectionChanged -= OnLayerContentChanged;
        layer.Regions.CollectionChanged -= OnLayerContentChanged;
        layer.Vectors.CollectionChanged -= OnLayerContentChanged;
        layer.Labels.CollectionChanged -= OnLayerContentChanged;
    }

    /// <summary>图层整体显示/隐藏时立即重绘覆盖层，无需调用方手动 Refresh。</summary>
    private void OnLayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GlobeLayer.IsVisible))
            _overlay.InvalidateVisual();
    }

    private void OnLayerContentChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _overlay.InvalidateVisual();
        StartTicker();
    }

    #endregion

    #region 公开方法

    /// <summary>立即把视角定位到指定经纬度与高度。</summary>
    public void JumpTo(double latitude, double longitude, double altitudeMeters)
    {
        _pendingZoomLevel = null;

        _syncing = true;
        try
        {
            SetCurrentValue(CenterLatitudeProperty, latitude);
            SetCurrentValue(CenterLongitudeProperty, longitude);
            SetCurrentValue(AltitudeProperty, altitudeMeters);
        }
        finally
        {
            _syncing = false;
        }

        ApplyCameraProperties();
        SyncPropertiesFromCamera();
        Refresh();
    }

    /// <summary>平滑飞行到指定经纬度与高度。</summary>
    public void FlyTo(double latitude, double longitude, double altitudeMeters, TimeSpan? duration = null)
    {
        _pendingZoomLevel = null;
        StartFlight(latitude, longitude, altitudeMeters, duration ?? TimeSpan.FromMilliseconds(900));
    }

    /// <summary>在指定图层中快速添加一个标记点（可指定图标文件）。</summary>
    public GlobeMarker AddMarker(GeoPoint position, string? label = null, MarkerShape shape = MarkerShape.Circle,
        string? imagePath = null)
    {
        if (Layers.Count == 0)
            Layers.Add(new GlobeLayer("默认图层"));

        var marker = new GlobeMarker { Position = position, Label = label, Shape = shape, ImagePath = imagePath };
        Layers[0].Markers.Add(marker);
        return marker;
    }

    /// <summary>获取渲染诊断信息（Zoom 为混合层级中的最细一级）。</summary>
    public (int Zoom, int Tiles, int Textures, int Failed) GetDiagnostics() => _surface.GetDiagnostics();

    /// <summary>本帧实际绘制瓦片使用的层级范围（四叉树混合 LOD）。</summary>
    public (int Min, int Max) GetDrawnZoomRange() => _surface.DrawnZoomRange;

    /// <summary>本帧各层级实际绘制的瓦片数量，便于观察混合 LOD 的分布。</summary>
    public IReadOnlyList<(int Level, int Count)> GetDrawnLevelHistogram() => _surface.GetDrawnLevelHistogram();

    /// <summary>本帧实际绘制的瓦片标识（诊断用）。</summary>
    public IReadOnlyList<TileId> GetDrawnTiles() => _surface.GetDrawnTiles();

    /// <summary>上一帧四叉树选瓦片（含层级估算）的耗时，毫秒。</summary>
    public double GetLastSelectionMilliseconds() => _surface.LastSelectionMilliseconds;

    /// <summary>清除瓦片缓存并重新加载。</summary>
    public void ResetTileCache() => _surface.ResetCaches();

    /// <summary>
    /// 查找离屏幕坐标最近的标记点：距离按到图标包围盒的距离计算（落在盒内为 0），
    /// 超过 maxDistance 像素则忽略；等距时取更上层的一个。
    /// </summary>
    public GlobeMarker? FindNearestMarker(Point screenPosition, double maxDistance = 24d) =>
        _overlay.FindNearestMarker(screenPosition, maxDistance);

    /// <summary>
    /// 查找离屏幕坐标最近的标记点（不限距离），并通过 <paramref name="distance"/> 返回
    /// 鼠标到图标包围盒的像素距离（落在盒内为 0）。适合在鼠标旁展示自定义内容时使用。
    /// </summary>
    public GlobeMarker? FindNearestMarker(Point screenPosition, out double distance) =>
        _overlay.FindNearestMarker(screenPosition, out distance);

    /// <summary>
    /// 查找离给定经纬度最近的标记点（按地表大圆距离），只考虑可见图层中可见的标记点。
    /// </summary>
    public GlobeMarker? FindNearestMarker(double latitude, double longitude)
    {
        GlobeMarker? best = null;
        var bestDistance = double.MaxValue;

        foreach (var layer in Layers)
        {
            if (!layer.IsVisible)
                continue;

            foreach (var marker in layer.Markers)
            {
                if (!marker.IsVisible)
                    continue;

                var distance = GreatCircleDistance(latitude, longitude,
                    marker.Position.Latitude, marker.Position.Longitude);
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                best = marker;
            }
        }

        return best;
    }

    /// <summary>点与点之间的地表大圆距离（米）。</summary>
    private static double GreatCircleDistance(double latitude1, double longitude1,
        double latitude2, double longitude2)
    {
        var lat1 = latitude1 * MathUtil.DegToRad;
        var lat2 = latitude2 * MathUtil.DegToRad;
        var dLat = (latitude2 - latitude1) * MathUtil.DegToRad;
        var dLon = MathUtil.NormalizeLongitudeDelta(longitude2 - longitude1) * MathUtil.DegToRad;

        var a = Math.Sin(dLat * 0.5d) * Math.Sin(dLat * 0.5d) +
                Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon * 0.5d) * Math.Sin(dLon * 0.5d);
        return 2d * GeoMath.EarthRadius * Math.Asin(Math.Min(1d, Math.Sqrt(a)));
    }

    /// <summary>
    /// 更新“鼠标接近的标记点”：靠近开启了接近提示的标记点时高亮并弹出提示框，
    /// 离开后恢复。由输入层在鼠标移动/离开时调用；转发外部输入或做自动化测试时也可直接调用。
    /// </summary>
    public void UpdateHoverMarker(Point? position)
    {
        // 测量工具优先：工具激活时不做标记悬停，避免十字光标与提示框互相干扰
        if (ActiveTool != GlobeTool.None)
            position = null;

        var marker = position is { } point
            ? _overlay.FindNearestMarker(point, MarkerHoverRadius, requireHoverTip: true)
            : null;

        _overlay.PointerPosition = position;
        _overlay.HoverRadius = MarkerHoverRadius;

        var changed = !ReferenceEquals(marker, _hoverMarker);
        _hoverMarker = marker;
        _overlay.HoverMarker = marker;

        // 只接管自己设置的悬停光标，鼠标离开后恢复调用方原来的光标
        if (marker != null)
        {
            if (!_hoverCursorApplied)
            {
                _baseCursor = Cursor;
                _hoverCursorApplied = true;
            }

            Cursor = HandCursor;
        }
        else if (_hoverCursorApplied)
        {
            Cursor = _baseCursor;
            _hoverCursorApplied = false;
        }

        if (changed)
            MarkerHoverChanged?.Invoke(this, new GlobeMarkerHoverEventArgs(marker, position ?? default));

        _overlay.InvalidateVisual();
    }

    /// <summary>触发鼠标移动事件（由输入层调用）。</summary>
    internal void RaisePointerMoved(Point position)
    {
        if (GlobePointerMoved == null)
            return;

        GeoPoint? location = null;
        if (_camera.ScreenToGround(position.X, position.Y, Bounds.Width, Bounds.Height, out var geo))
            location = geo;

        var nearest = _overlay.FindNearestMarker(position, out var distance);
        GlobePointerMoved.Invoke(this, new GlobePointerEventArgs(position, location, nearest, distance));
    }

    #endregion
}
