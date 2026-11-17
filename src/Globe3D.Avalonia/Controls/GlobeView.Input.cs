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

        var point = e.GetCurrentPoint(this);
        var position = point.Position;

        if (e.ClickCount >= 2 && point.Properties.IsLeftButtonPressed)
        {
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
            _dragMode = DragMode.Tilt;
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

        // 鼠标离开控件时收起接近提示
        if (_dragMode == DragMode.None)
        {
            _pointerPosition = null;
            UpdateHoverMarker(null);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var delta = e.Delta.Y;
        if (Math.Abs(delta) < 0.0001d)
            return;

        var altitude = Math.Max(500d, _camera.AltitudeMeters);
        var factor = Math.Pow(0.80d, delta * Math.Clamp(ZoomSensitivity, 0.1d, 5d));
        _camera.SetAltitude(altitude * factor);
        _camera.Normalize();

        SyncPropertiesFromCamera();
        InvalidateAll();
        StartTicker();
        e.Handled = true;
    }

    private void ApplyRotationDrag(double dx, double dy)
    {
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

    #endregion
}
