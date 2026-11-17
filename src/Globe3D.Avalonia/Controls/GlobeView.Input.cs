using System.Diagnostics;
using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;

namespace Globe3D.Avalonia;

public partial class GlobeView
{
    private enum DragMode
    {
        None,
        Rotate,
        Tilt
    }

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Stopwatch _moveClock = Stopwatch.StartNew();

    private DispatcherTimer? _ticker;
    private double _lastTickSeconds;

    private DragMode _dragMode;
    private Point _lastPointer;
    private Point? _pointerPosition;
    private bool _dragged;
    private double _dragTravel;
    private double _lastMoveTimestamp;

    private double _inertiaLongitudeSpeed;
    private double _inertiaLatitudeSpeed;
    private bool _inertiaActive;

    /// <summary>松手前静止超过该时长（秒）就不再触发惯性，避免“拖到位置、停住、松手”后还在滑。</summary>
    private const double InertiaReleaseIdleSeconds = 0.12d;

    private FlightState? _flight;

    private bool NeedsContinuousTicker => AutoRotate || _flight != null || _inertiaActive;

    #region 定时器

    private void StartTicker()
    {
        if (_ticker != null)
            return;

        _lastTickSeconds = _clock.Elapsed.TotalSeconds;
        _ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnTick);
        _ticker.Start();
    }

    private void StopTicker()
    {
        if (_ticker == null)
            return;

        _ticker.Stop();
        _ticker = null;
    }

    private void StopTickerIfIdle()
    {
        if (!NeedsContinuousTicker)
            StopTicker();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _lastTickSeconds;
        _lastTickSeconds = now;
        if (dt <= 0 || dt > 0.25)
            dt = 1d / 60d;

        var changed = false;

        if (AutoRotate && _flight == null)
        {
            _camera.CenterLongitude += AutoRotateSpeed * dt;
            _camera.Normalize();
            changed = true;
        }

        if (_flight != null && UpdateFlight(dt))
            changed = true;

        if (_inertiaActive && UpdateInertia(dt))
            changed = true;

        if (changed)
        {
            SyncPropertiesFromCamera();
            InvalidateAll();

            // 相机在动（自动旋转/惯性/飞行）：靠近提示要按新相机重新判定，
            // 否则标记点旋到球体背面后提示会残留
            if (_hoverMarker != null && _pointerPosition is { } pointer)
                UpdateHoverMarker(pointer);
        }

        if (!NeedsContinuousTicker)
            StopTicker();
    }

    #endregion

    #region 惯性与飞行

    private bool UpdateInertia(double dt)
    {
        if (Math.Abs(_inertiaLongitudeSpeed) < 0.08 && Math.Abs(_inertiaLatitudeSpeed) < 0.08)
        {
            _inertiaLongitudeSpeed = 0;
            _inertiaLatitudeSpeed = 0;
            _inertiaActive = false;
            return false;
        }

        _camera.CenterLongitude += _inertiaLongitudeSpeed * dt;
        _camera.CenterLatitude += _inertiaLatitudeSpeed * dt;
        _camera.Normalize();

        var decay = Math.Exp(-2.6d * dt);
        _inertiaLongitudeSpeed *= decay;
        _inertiaLatitudeSpeed *= decay;
        return true;
    }

    private void StartFlight(double latitude, double longitude, double altitudeMeters, TimeSpan duration)
    {
        _inertiaActive = false;
        _inertiaLongitudeSpeed = 0;
        _inertiaLatitudeSpeed = 0;

        _flight = new FlightState
        {
            StartLatitude = _camera.CenterLatitude,
            StartLongitude = _camera.CenterLongitude,
            StartAltitude = Math.Max(1000d, _camera.AltitudeMeters),
            EndLatitude = MathUtil.Clamp(latitude, -GeoMath.MaxMercatorLatitude, GeoMath.MaxMercatorLatitude),
            EndLongitude = MathUtil.NormalizeLongitude(longitude),
            EndAltitude = Math.Max(MinAltitude, altitudeMeters),
            Elapsed = 0,
            Duration = Math.Max(0.08, duration.TotalSeconds)
        };

        StartTicker();
    }

    private bool UpdateFlight(double dt)
    {
        var flight = _flight!;
        flight.Elapsed += dt;
        var t = MathUtil.Clamp(flight.Elapsed / flight.Duration, 0d, 1d);
        var eased = t * t * (3d - 2d * t);

        _camera.CenterLatitude = MathUtil.Lerp((float)flight.StartLatitude, (float)flight.EndLatitude, (float)eased);
        var deltaLon = MathUtil.NormalizeLongitudeDelta(flight.EndLongitude - flight.StartLongitude);
        _camera.CenterLongitude = flight.StartLongitude + deltaLon * eased;

        // 高度按对数插值，远近视距变化更自然
        var startLog = Math.Log(Math.Max(1000d, flight.StartAltitude));
        var endLog = Math.Log(Math.Max(1000d, flight.EndAltitude));
        _camera.SetAltitude(Math.Exp(startLog + (endLog - startLog) * eased));

        _camera.Normalize();

        if (t >= 1d)
        {
            _flight = null;
            _camera.CenterLatitude = flight.EndLatitude;
            _camera.CenterLongitude = flight.EndLongitude;
            _camera.SetAltitude(flight.EndAltitude);
        }

        return true;
    }

    private sealed class FlightState
    {
        public double StartLatitude;
        public double StartLongitude;
        public double StartAltitude;
        public double EndLatitude;
        public double EndLongitude;
        public double EndAltitude;
        public double Elapsed;
        public double Duration;
    }

    #endregion

    #region 鼠标交互

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // 工具模式下点击地球时获取键盘焦点，Esc 才能取消当前草稿；
        // 不在 ActiveTool 属性回调里抢焦点，避免代码切换工具时影响宿主输入控件
        if (ActiveTool != GlobeTool.None)
            Focus();

        var point = e.GetCurrentPoint(this);
        var position = point.Position;

        if (e.ClickCount >= 2 && point.Properties.IsLeftButtonPressed)
        {
            if (ActiveTool != GlobeTool.None)
            {
                // 距离/多边形双击结束；圆形/方形的完成发生在双击的第一次单击，
                // 第二次按下/松手必须整体抑制，否则会残留一个新的草稿点
                if (ActiveTool is GlobeTool.Distance or GlobeTool.AreaPolygon)
                    FinishMeasurementDraft();

                _suppressNextToolClick = true;

                // 双击的第二次按下不进入拖动，清理上一次手势的状态，避免拖动标记残留
                _dragMode = DragMode.None;
                _dragged = false;
                e.Handled = true;
                return;
            }

            if (_camera.ScreenToGround(position.X, position.Y, Bounds.Width, Bounds.Height, out var target))
            {
                var altitude = Math.Max(MinAltitude, _camera.AltitudeMeters * 0.35d);
                FlyTo(target.Latitude, target.Longitude, altitude);
            }

            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
            _dragMode = DragMode.Rotate;
        else if (point.Properties.IsRightButtonPressed)
        {
            // 平面地图没有倾斜/航向概念，右键拖动不处理
            if (_camera.IsFlat)
            {
                _dragMode = DragMode.None;
                _dragged = false;
                e.Handled = true;
                return;
            }

            _dragMode = DragMode.Tilt;
        }
        else
            return;

        _lastPointer = position;
        _dragged = false;
        _dragTravel = 0;
        _lastMoveTimestamp = _moveClock.Elapsed.TotalSeconds;
        _inertiaActive = false;
        _inertiaLongitudeSpeed = 0;
        _inertiaLatitudeSpeed = 0;
        _flight = null;

        // 开始拖动时收起接近提示，避免提示框跟着旋转画面抖动
        UpdateHoverMarker(null);

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var point = e.GetCurrentPoint(this);
        _pointerPosition = point.Position;

        if (_dragMode == DragMode.None)
        {
            // 测量工具激活时只更新测量预览，不做标记悬停
            if (ActiveTool != GlobeTool.None)
            {
                UpdateToolPointer(point.Position);
                RaisePointerMoved(point.Position);
                return;
            }

            // 空闲状态下持续更新“接近标记点”的高亮与提示，并对外广播鼠标移动事件
            UpdateHoverMarker(point.Position);
            RaisePointerMoved(point.Position);
            return;
        }

        if (_dragMode == DragMode.Rotate && !point.Properties.IsLeftButtonPressed)
            return;
        if (_dragMode == DragMode.Tilt && !point.Properties.IsRightButtonPressed)
            return;

        var position = point.Position;
        var dx = position.X - _lastPointer.X;
        var dy = position.Y - _lastPointer.Y;
        _lastPointer = position;

        if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01)
            return;

        _dragTravel += Math.Abs(dx) + Math.Abs(dy);
        if (_dragTravel > 2.5d)
            _dragged = true;

        if (_dragMode == DragMode.Rotate)
            ApplyRotationDrag(dx, dy);
        else
            ApplyTiltDrag(dx, dy);

        SyncPropertiesFromCamera();
        InvalidateAll();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        var mode = _dragMode;
        var dragged = _dragged;
        _dragMode = DragMode.None;
        e.Pointer.Capture(null);

        if (!dragged)
        {
            var point = e.GetCurrentPoint(this);

            if (ActiveTool != GlobeTool.None)
            {
                if (_suppressNextToolClick)
                {
                    _suppressNextToolClick = false;
                }
                else if (e.InitialPressMouseButton == MouseButton.Left)
                {
                    if (_camera.ScreenToGround(point.Position.X, point.Position.Y, Bounds.Width, Bounds.Height,
                            out var geo))
                    {
                        UpdateToolPointer(point.Position);
                        HandleToolClick(geo);
                    }
                }
                else if (e.InitialPressMouseButton == MouseButton.Right)
                {
                    HandleToolRightClick();
                }

                e.Handled = true;
                return;
            }

            // 单击时按“离图标最近”查找：允许点偏一点也能命中最近的标记点
            var marker = _overlay.FindNearestMarker(point.Position, Math.Max(0d, MarkerHitTolerance));
            if (marker != null)
            {
                MarkerClicked?.Invoke(this, new GlobeMarkerEventArgs(marker));
            }
            else
            {
                GeoPoint? location = null;
                if (_camera.ScreenToGround(point.Position.X, point.Position.Y, Bounds.Width, Bounds.Height, out var geo))
                    location = geo;

                GlobeClicked?.Invoke(this, new GlobeClickEventArgs(
                    location, point.Position, e.InitialPressMouseButton == MouseButton.Right));
            }

            // 回到空闲状态后重新判定接近的标记点（点击后可能已经移开了）
            UpdateHoverMarker(point.Position);
        }
        else if (mode == DragMode.Rotate)
        {
            // 松手瞬间鼠标已经停住（超过 120ms 没动）：按“停稳再松手”处理，不触发惯性滑动
            var idleSeconds = _moveClock.Elapsed.TotalSeconds - _lastMoveTimestamp;
            if (idleSeconds > InertiaReleaseIdleSeconds)
            {
                _inertiaLongitudeSpeed = 0d;
                _inertiaLatitudeSpeed = 0d;
            }

            if (Math.Abs(_inertiaLongitudeSpeed) > 0.5d || Math.Abs(_inertiaLatitudeSpeed) > 0.5d)
            {
                _inertiaActive = true;
                StartTicker();
            }
        }

        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragMode = DragMode.None;
        StopTickerIfIdle();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        // 鼠标离开控件时收起接近提示，并清掉测量预览（保留已落下的顶点）
        if (_dragMode == DragMode.None)
        {
            _pointerPosition = null;
            ClearToolPointer();
            UpdateHoverMarker(null);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Esc：放弃当前测量草稿（已完成的测量结果保留）
        if (e.Key == Key.Escape && ActiveTool != GlobeTool.None && IsMeasureInProgress)
        {
            CancelMeasurement();
            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var delta = e.Delta.Y;
        if (Math.Abs(delta) < 0.0001d)
            return;

        var position = e.GetPosition(this);
        var aspect = Bounds.Height > 0 ? Bounds.Width / Bounds.Height : 1d;

        // 平面模式以鼠标位置为锚点缩放：先记录光标下的经纬度，缩放后把它保持在原像素位置
        GeoPoint? anchor = null;
        if (_camera.IsFlat)
        {
            _camera.Update(aspect);
            if (_camera.ScreenToGround(position.X, position.Y, Bounds.Width, Bounds.Height, out var geo))
                anchor = geo;
        }

        var altitude = Math.Max(500d, _camera.AltitudeMeters);
        var factor = Math.Pow(0.80d, delta * Math.Clamp(ZoomSensitivity, 0.1d, 5d));
        var targetAltitude = altitude * factor;

        // 缩放级别上下限：只有目标级别超出限制时才改用级别反算高度，
        // 未设限制时保持原有的“高度乘系数”手感
        var (zoomWidth, zoomHeight) = GetZoomViewport();
        var currentLevel = _camera.GetZoomLevel(zoomWidth, zoomHeight);
        if (double.IsFinite(currentLevel) && double.IsFinite(zoomHeight))
        {
            var targetLevel = currentLevel - Math.Log2(factor);
            var clampedLevel = ClampZoomLevel(targetLevel);
            if (Math.Abs(clampedLevel - targetLevel) > 1e-9d)
                targetAltitude = GetTargetAltitudeForZoomLevel(clampedLevel, zoomWidth, zoomHeight);
        }

        _camera.SetAltitude(targetAltitude);
        _camera.Normalize();

        if (_camera.IsFlat && anchor is { } anchorGeo)
        {
            _camera.Update(aspect);
            var height = Math.Max(1d, Bounds.Height);
            var scale = _camera.FlatScaleFactor * height;
            var mercatorX = (MathUtil.NormalizeLongitude(anchorGeo.Longitude) + 180d) / 360d;
            var mercatorY = GeoMath.LatitudeToMercatorY(anchorGeo.Latitude);
            var centerX = mercatorX - (position.X - Bounds.Width * 0.5d) / scale;
            var centerY = mercatorY - (position.Y - height * 0.5d) / scale;
            _camera.SetFlatCenter(centerX, centerY, Bounds.Width, height);
        }

        SyncPropertiesFromCamera();
        InvalidateAll();
        StartTicker();
        e.Handled = true;
    }

    private void ApplyRotationDrag(double dx, double dy)
    {
        if (_camera.IsFlat)
        {
            ApplyFlatPanDrag(dx, dy);
            return;
        }

        var height = Math.Max(1d, Bounds.Height);
        var altitude = Math.Max(1500d, _camera.AltitudeMeters);
        var tanHalf = Math.Tan(_camera.FieldOfView * MathUtil.DegToRad * 0.5d);
        var metersPerPixel = 2d * altitude * tanHalf / height;
        var degreesPerPixel = metersPerPixel / GeoMath.EarthRadius * MathUtil.RadToDeg;

        var cosLat = Math.Max(0.15d, Math.Cos(_camera.CenterLatitude * MathUtil.DegToRad));
        var deltaLongitude = -dx * degreesPerPixel / cosLat;
        var deltaLatitude = dy * degreesPerPixel;

        _camera.CenterLongitude += deltaLongitude;
        _camera.CenterLatitude += deltaLatitude;
        _camera.Normalize();

        var now = _moveClock.Elapsed.TotalSeconds;
        var elapsed = now - _lastMoveTimestamp;
        _lastMoveTimestamp = now;
        if (elapsed is > 0.002 and < 0.25)
        {
            _inertiaLongitudeSpeed = _inertiaLongitudeSpeed * 0.55d + deltaLongitude / elapsed * 0.45d;
            _inertiaLatitudeSpeed = _inertiaLatitudeSpeed * 0.55d + deltaLatitude / elapsed * 0.45d;
        }
    }

    private void ApplyTiltDrag(double dx, double dy)
    {
        _camera.Pitch = MathUtil.Clamp(_camera.Pitch + dy * 0.22d, 0d, 88d);
        _camera.Heading = MathUtil.NormalizeLongitude(_camera.Heading - dx * 0.22d);
        _camera.Normalize();
    }

    /// <summary>平面模式下左键拖动平移地图，并按拖动速度累积惯性。</summary>
    private void ApplyFlatPanDrag(double dx, double dy)
    {
        var beforeLongitude = _camera.CenterLongitude;
        var beforeLatitude = _camera.CenterLatitude;
        _camera.PanFlatByPixels(dx, dy, Bounds.Width, Bounds.Height);

        var deltaLongitude = MathUtil.NormalizeLongitudeDelta(_camera.CenterLongitude - beforeLongitude);
        var deltaLatitude = _camera.CenterLatitude - beforeLatitude;

        var now = _moveClock.Elapsed.TotalSeconds;
        var elapsed = now - _lastMoveTimestamp;
        _lastMoveTimestamp = now;
        if (elapsed is > 0.002 and < 0.25)
        {
            _inertiaLongitudeSpeed = _inertiaLongitudeSpeed * 0.55d + deltaLongitude / elapsed * 0.45d;
            _inertiaLatitudeSpeed = _inertiaLatitudeSpeed * 0.55d + deltaLatitude / elapsed * 0.45d;
        }
    }

    #endregion
}
