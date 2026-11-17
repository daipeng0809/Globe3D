using Avalonia;
using Avalonia.Controls;

namespace Globe3D.Avalonia;

/// <summary>
/// 缩放级别接口：按 Web 墨卡托瓦片层级换算相机高度，并提供缩放上限/下限与屏幕显示。
/// </summary>
public partial class GlobeView
{
    private const double DefaultMaxZoomLevel = 24d;
    private double? _pendingZoomLevel;
    private TimeSpan? _pendingZoomDuration;
    private double _pendingZoomLatitude = double.NaN;
    private double _pendingZoomLongitude = double.NaN;
    private double _pendingZoomAppliedHeight = double.NaN;
    private bool _pendingZoomApplied;

    #region 属性

    /// <summary>最小缩放级别（瓦片层级）。</summary>
    public static readonly StyledProperty<double> MinZoomLevelProperty =
        AvaloniaProperty.Register<GlobeView, double>(nameof(MinZoomLevel));

    /// <summary>最大缩放级别（瓦片层级）。</summary>
    public static readonly StyledProperty<double> MaxZoomLevelProperty =
        AvaloniaProperty.Register<GlobeView, double>(nameof(MaxZoomLevel), DefaultMaxZoomLevel);

    /// <summary>是否在视口角落显示当前缩放级别。</summary>
    public static readonly StyledProperty<bool> ShowZoomLevelProperty =
        AvaloniaProperty.Register<GlobeView, bool>(nameof(ShowZoomLevel));

    /// <summary>缩放级别显示的位置（复用比例尺的停靠枚举）。</summary>
    public static readonly StyledProperty<GlobeScaleBarPlacement> ZoomLevelPlacementProperty =
        AvaloniaProperty.Register<GlobeView, GlobeScaleBarPlacement>(nameof(ZoomLevelPlacement),
            GlobeScaleBarPlacement.BottomRight);

    /// <summary>
    /// 最小缩放级别：约束 <see cref="ZoomToLevel"/>、<see cref="ZoomLevel"/> 与滚轮缩放。
    /// 同时受 <see cref="MinAltitude"/> / <see cref="MaxAltitude"/> 限制，实际可达到的级别以更严格者为准。
    /// </summary>
    public double MinZoomLevel
    {
        get => GetValue(MinZoomLevelProperty);
        set => SetValue(MinZoomLevelProperty, value);
    }

    /// <summary>
    /// 最大缩放级别：约束 <see cref="ZoomToLevel"/>、<see cref="ZoomLevel"/> 与滚轮缩放。
    /// 同时受 <see cref="MinAltitude"/> / <see cref="MaxAltitude"/> 限制，实际可达到的级别以更严格者为准。
    /// </summary>
    public double MaxZoomLevel
    {
        get => GetValue(MaxZoomLevelProperty);
        set => SetValue(MaxZoomLevelProperty, value);
    }

    /// <summary>是否显示当前缩放级别（如 <c>Z 8.4</c>）。</summary>
    public bool ShowZoomLevel
    {
        get => GetValue(ShowZoomLevelProperty);
        set => SetValue(ShowZoomLevelProperty, value);
    }

    /// <summary>缩放级别显示的位置。</summary>
    public GlobeScaleBarPlacement ZoomLevelPlacement
    {
        get => GetValue(ZoomLevelPlacementProperty);
        set => SetValue(ZoomLevelPlacementProperty, value);
    }

    /// <summary>
    /// 当前缩放级别（按视口中心与瓦片 LOD 估算）。赋值等价于 <see cref="ZoomToLevel"/>。
    /// 这是实时计算属性，不参与 Avalonia 属性变更通知；需要屏幕显示请用 <see cref="ShowZoomLevel"/>，
    /// 需要监听变化请使用 <see cref="CameraChanged"/>。
    /// </summary>
    public double ZoomLevel
    {
        get => GetZoomLevel();
        set => ZoomToLevel(value);
    }

    #endregion

    #region 公开方法

    /// <summary>
    /// 当前缩放级别（Web 墨卡托瓦片层级，约等于屏幕上 256px 瓦片对应的层级）。
    /// 控件尚未完成布局时返回 <see cref="double.NaN"/>。
    /// </summary>
    public double GetZoomLevel()
    {
        if (Bounds.Width <= 1d || Bounds.Height <= 1d)
            return double.NaN;

        var (width, height) = GetZoomViewport();
        return _camera.GetZoomLevel(width, height);
    }

    /// <summary>
    /// 缩放到指定瓦片层级：按视口高度、视场角与中心纬度自动计算相机高度。
    /// </summary>
    /// <param name="level">目标缩放级别（瓦片层级），会按 <see cref="MinZoomLevel"/> / <see cref="MaxZoomLevel"/> 与高度限制收敛。</param>
    /// <param name="duration">可选的平滑动画时长；为 null 或 0 时立即定位。</param>
    /// <remarks>
    /// 三维模式按当前俯仰角与中心纬度迭代求解高度，使实际显示级别贴近目标；平面模式为解析换算。
    /// 控件尚未完成布局时会记录请求，并在首次渲染后应用。
    /// </remarks>
    public void ZoomToLevel(double level, TimeSpan? duration = null)
    {
        if (!double.IsFinite(level))
            return;

        var clamped = ClampZoomLevel(level);
        if (!HasUsableViewport)
        {
            // 等 GL 首帧测出真实物理像素尺寸后再换算，避免按布局中间尺寸得到错误的镜头距离
            ClearPendingZoom();
            _pendingZoomLevel = clamped;
            _pendingZoomDuration = duration;
            return;
        }

        ClearPendingZoom();
        ApplyZoomLevel(clamped, duration);
    }

    /// <summary>
    /// 原子地把视角定位到指定经纬度与缩放级别：中心与高度按同一视口一次性换算并生效，
    /// 不会出现“先设中心被中间高空视口夹取、再设级别”的问题。
    /// </summary>
    /// <param name="latitude">目标中心纬度（会收敛到 ±85.05°）。</param>
    /// <param name="longitude">目标中心经度（会归一化到 ±180°）。</param>
    /// <param name="zoomLevel">目标缩放级别（瓦片层级），会按 <see cref="MinZoomLevel"/> / <see cref="MaxZoomLevel"/> 与高度限制收敛。</param>
    /// <param name="duration">可选的平滑动画时长；为 null 或 0 时立即定位。</param>
    /// <remarks>
    /// 控件尚未完成布局时会暂存中心与级别，并在 GL 首帧测出最终物理像素视口后一次性应用。
    /// 三维模式按目标中心纬度与当前俯仰角迭代求解高度；平面模式为解析换算。
    /// </remarks>
    public void SetView(double latitude, double longitude, double zoomLevel, TimeSpan? duration = null)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || !double.IsFinite(zoomLevel))
            return;

        var level = ClampZoomLevel(zoomLevel);
        var lat = MathUtil.Clamp(latitude, -GeoMath.MaxMercatorLatitude, GeoMath.MaxMercatorLatitude);
        var lon = MathUtil.NormalizeLongitude(longitude);

        if (!HasUsableViewport)
        {
            ClearPendingZoom();
            _pendingZoomLevel = level;
            _pendingZoomDuration = duration;
            _pendingZoomLatitude = lat;
            _pendingZoomLongitude = lon;
            return;
        }

        ClearPendingZoom();
        ApplyZoomLevel(level, duration, lat, lon);
    }

    /// <summary>
    /// <see cref="SetView"/> 的别名：按缩放级别（而不是高度）原子定位中心。
    /// 三维地球已有的 <see cref="JumpTo"/> 第三个参数是高度，C# 无法用相同签名再重载级别版本，
    /// 因此级别版本使用该方法名。
    /// </summary>
    public void JumpToLevel(double latitude, double longitude, double zoomLevel, TimeSpan? duration = null) =>
        SetView(latitude, longitude, zoomLevel, duration);

    #endregion

    #region 内部实现

    private void ApplyZoomLevel(double level, TimeSpan? duration)
        => ApplyZoomLevel(level, duration, _camera.CenterLatitude, _camera.CenterLongitude);

    private void ApplyZoomLevel(double level, TimeSpan? duration, double latitude, double longitude)
    {
        var (width, height) = GetZoomViewport();
        if (!double.IsFinite(height) || height <= 0d)
            return;

        var altitude = GetTargetAltitudeForZoomLevel(level, width, height, latitude, longitude);
        if (!double.IsFinite(altitude) || altitude <= 0d)
            return;

        // 高度硬限制优先于级别限制
        var minAltitude = Math.Max(1d, MinAltitude);
        var maxAltitude = Math.Max(minAltitude + 1d, MaxAltitude);
        altitude = Math.Clamp(altitude, minAltitude, maxAltitude);

        // 缩放请求优先于进行中的惯性/飞行，避免旧动画把镜头拉回原处
        _inertiaActive = false;
        _inertiaLongitudeSpeed = 0;
        _inertiaLatitudeSpeed = 0;

        if (duration is { } span && span > TimeSpan.Zero)
        {
            FlyTo(latitude, longitude, altitude, span);
        }
        else
        {
            _flight = null;
            JumpTo(latitude, longitude, altitude);
        }
    }

    /// <summary>
    /// 按目标级别求相机高度：平面模式用解析反函数；三维模式在倾斜视角下用二分逼近，
    /// 使 <see cref="OrbitCamera.GetZoomLevel"/> 真正落在请求级别上（解析反函数只适用正俯视）。
    /// </summary>
    private double GetTargetAltitudeForZoomLevel(double level, double width, double height,
        double? latitude = null, double? longitude = null)
    {
        if (_camera.IsFlat)
            return _camera.GetAltitudeForZoomLevel(level, height);

        if (width <= 1d || height <= 1d)
            return _camera.GetAltitudeForZoomLevel(level, height);

        var minAltitude = Math.Max(1d, MinAltitude);
        var maxAltitude = Math.Max(minAltitude + 1d, MaxAltitude);
        var aspect = width / height;

        var probe = _camera.Clone();
        probe.CenterLatitude = latitude ?? _camera.CenterLatitude;
        probe.CenterLongitude = longitude ?? _camera.CenterLongitude;
        probe.MinDistance = 1d + minAltitude / GeoMath.EarthRadius;
        probe.MaxDistance = 1d + maxAltitude / GeoMath.EarthRadius;

        var low = minAltitude;
        var high = maxAltitude;
        var lowLevel = Evaluate(low);
        var highLevel = Evaluate(high);
        if (!double.IsFinite(lowLevel) || !double.IsFinite(highLevel))
            return _camera.GetAltitudeForZoomLevel(level, height);

        // 高度越低级别越高；请求级别超出可达范围时返回最近的边界
        if (level <= highLevel)
            return high;
        if (level >= lowLevel)
            return low;

        for (var i = 0; i < 28; i++)
        {
            var middle = Math.Sqrt(low * high);
            var middleLevel = Evaluate(middle);
            if (!double.IsFinite(middleLevel))
                break;

            if (middleLevel > level)
                low = middle;
            else
                high = middle;
        }

        return Math.Sqrt(low * high);

        double Evaluate(double altitude)
        {
            probe.SetAltitude(altitude);
            probe.Update(aspect);
            return probe.GetZoomLevel(width, height);
        }
    }

    private double ClampZoomLevel(double level)
    {
        var min = double.IsFinite(MinZoomLevel) ? MinZoomLevel : 0d;
        var max = double.IsFinite(MaxZoomLevel) ? MaxZoomLevel : DefaultMaxZoomLevel;
        if (max < min)
            (min, max) = (max, min);

        return Math.Clamp(level, min, max);
    }

    /// <summary>物理像素视口尺寸：优先使用 GL 表面最近一帧的实际尺寸，与瓦片 LOD 保持一致。</summary>
    private (double Width, double Height) GetZoomViewport()
    {
        if (_surface.PixelWidth > 1d && _surface.PixelHeight > 1d)
            return (_surface.PixelWidth, _surface.PixelHeight);

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        return (Math.Max(1d, Bounds.Width * scaling), Math.Max(1d, Bounds.Height * scaling));
    }

    /// <summary>布局完成且 GL 已测出物理像素尺寸，可安全按级别换算高度。</summary>
    private bool HasUsableViewport => Bounds.Width > 1d && Bounds.Height > 1d && _surface.PixelHeight > 1d;

    private void ClearPendingZoom()
    {
        _pendingZoomLevel = null;
        _pendingZoomDuration = null;
        _pendingZoomLatitude = double.NaN;
        _pendingZoomLongitude = double.NaN;
        _pendingZoomApplied = false;
        _pendingZoomAppliedHeight = double.NaN;
    }

    /// <summary>缩放上下限变化后，把当前级别收敛进新区间。</summary>
    private void OnZoomLimitsChanged()
    {
        if (Bounds.Width <= 1d || Bounds.Height <= 1d)
            return;

        var current = GetZoomLevel();
        if (!double.IsFinite(current))
            return;

        var clamped = ClampZoomLevel(current);
        if (Math.Abs(clamped - current) > 1e-6d)
            ZoomToLevel(clamped);
    }

    /// <summary>GL 首帧后应用布局前请求的缩放级别/视口（此时物理像素尺寸已准确）。</summary>
    private void ApplyPendingZoomLevel()
    {
        if (_pendingZoomLevel is not { } level)
        {
            ClearPendingZoom();
            return;
        }

        if (!HasUsableViewport)
            return;

        var height = _surface.PixelHeight;

        // 首帧可能还是窗口默认尺寸，最大化/停靠会在后续帧改变视口；
        // 保持请求直到连续两帧高度一致，再清掉暂存，保证按最终视口换算
        if (_pendingZoomApplied && Math.Abs(height - _pendingZoomAppliedHeight) < 0.75d)
        {
            ClearPendingZoom();
            return;
        }

        var duration = _pendingZoomDuration;
        var pendingLatitude = _pendingZoomLatitude;
        var pendingLongitude = _pendingZoomLongitude;
        var latitude = double.IsFinite(pendingLatitude) ? pendingLatitude : _camera.CenterLatitude;
        var longitude = double.IsFinite(pendingLongitude) ? pendingLongitude : _camera.CenterLongitude;

        ApplyZoomLevel(ClampZoomLevel(level), duration, latitude, longitude);

        // ApplyZoomLevel → JumpTo/FlyTo 会取消 pending；此处恢复，等下一帧确认视口稳定
        _pendingZoomLevel = level;
        _pendingZoomDuration = duration;
        _pendingZoomLatitude = pendingLatitude;
        _pendingZoomLongitude = pendingLongitude;
        _pendingZoomApplied = true;
        _pendingZoomAppliedHeight = height;
    }

    private void OnZoomDisplayChanged()
    {
        _overlay.ShowZoomLevel = ShowZoomLevel;
        _overlay.ZoomLevelPlacement = ZoomLevelPlacement;
        _overlay.InvalidateVisual();
    }

    #endregion
}
