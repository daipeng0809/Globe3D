namespace Globe3D.Avalonia;

/// <summary>
/// 绕地球运行的轨道相机。以「观察中心经纬度 + 地心距离 + 俯仰 + 航向」描述，
/// 内部换算为观察矩阵与投影矩阵，并提供屏幕射线/投影工具。
/// </summary>
public sealed class OrbitCamera
{
    /// <summary>观察中心纬度（度）。</summary>
    public double CenterLatitude { get; set; } = 32.0;

    /// <summary>观察中心经度（度）。</summary>
    public double CenterLongitude { get; set; } = 108.0;

    /// <summary>相机到地心的距离，单位球半径（1 = 地表）。</summary>
    public double Distance { get; set; } = 2.6;

    /// <summary>
    /// 视线俯仰角（度）：视线与中心地表法线的夹角。0 为正俯视（垂直向下），
    /// 值越大越接近水平，最大 88。相机会按当前地心距离把该角度反解为轨道偏移角，
    /// 因此低空时轨道偏移会自动收敛到地平线以内，不会把中心点转到球体背面。
    /// 平面模式下不参与投影。
    /// </summary>
    public double Pitch { get; set; }

    /// <summary>航向角（度），绕视线方向旋转画面。</summary>
    public double Heading { get; set; }

    /// <summary>垂直视场角（度）。</summary>
    public double FieldOfView { get; set; } = 45.0;

    /// <summary>Web 墨卡托瓦片边长（像素），与瓦片 LOD 的假设一致。</summary>
    public const double AssumedTileSize = 256d;

    /// <summary>是否使用二维平面（Web 墨卡托）投影；为 false 时使用三维球面。</summary>
    public bool IsFlat { get; set; }

    /// <summary>平面模式下每个视口高度对应的墨卡托世界像素数。</summary>
    public double FlatScaleFactor { get; private set; } = 1d;

    /// <summary>平面模式视口中心的墨卡托坐标（0 为西/北，1 为东/南）。</summary>
    public double FlatCenterX { get; private set; } = 0.5d;

    /// <summary>平面模式视口中心的墨卡托纵坐标（0 为北，1 为南）。</summary>
    public double FlatCenterY { get; private set; } = 0.5d;

    /// <summary>
    /// 实际显示中心的纬度（度）。平面模式返回视口限制后的中心；三维模式与 <see cref="CenterLatitude"/> 相同。
    /// </summary>
    public double EffectiveCenterLatitude =>
        IsFlat ? GeoMath.MercatorYToLatitude(FlatCenterY) : CenterLatitude;

    /// <summary>
    /// 实际显示中心的经度（度）。平面模式返回视口限制后的中心；三维模式与 <see cref="CenterLongitude"/> 相同。
    /// </summary>
    public double EffectiveCenterLongitude =>
        IsFlat ? FlatCenterX * 360d - 180d : CenterLongitude;

    /// <summary>允许的最小地心距离。</summary>
    public double MinDistance { get; set; } = 1.015;

    /// <summary>允许的最大地心距离。</summary>
    public double MaxDistance { get; set; } = 9.0;

    /// <summary>相机在世界空间中的位置。</summary>
    public Vec3 Position { get; private set; } = new(0f, 0f, 2.6f);

    /// <summary>相机朝向。</summary>
    public Vec3 Forward { get; private set; } = new(0f, 0f, -1f);

    /// <summary>相机右向。</summary>
    public Vec3 Right { get; private set; } = new(1f, 0f, 0f);

    /// <summary>相机上向。</summary>
    public Vec3 Up { get; private set; } = new(0f, 1f, 0f);

    public Mat4 View { get; private set; } = Mat4.Identity;

    public Mat4 Projection { get; private set; } = Mat4.Identity;

    public Mat4 ViewProjection { get; private set; } = Mat4.Identity;

    public float NearPlane { get; private set; } = 0.01f;

    public float FarPlane { get; private set; } = 10f;

    public float Aspect { get; private set; } = 1f;

    /// <summary>当前相机离地高度（米）。</summary>
    public double AltitudeMeters => (Distance - 1d) * GeoMath.EarthRadius;

    /// <summary>以海拔（米）设置相机距离。</summary>
    public void SetAltitude(double altitudeMeters)
    {
        Distance = MathUtil.Clamp(1d + altitudeMeters / GeoMath.EarthRadius, MinDistance, MaxDistance);
    }

    /// <summary>把角度规范到合法范围：纬度 ±85.05，经度 ±180，俯仰 0..88。</summary>
    public void Normalize()
    {
        CenterLatitude = MathUtil.Clamp(CenterLatitude, -GeoMath.MaxMercatorLatitude, GeoMath.MaxMercatorLatitude);
        CenterLongitude = MathUtil.NormalizeLongitude(CenterLongitude);
        Distance = MathUtil.Clamp(Distance, MinDistance, MaxDistance);
        Pitch = MathUtil.Clamp(Pitch, 0d, 88d);
        Heading = MathUtil.NormalizeLongitude(Heading);
    }

    /// <summary>根据当前参数重新计算所有矩阵。需要在每次参数变化后调用。</summary>
    public void Update(double aspect)
    {
        Normalize();
        Aspect = (float)Math.Max(0.0001d, aspect);

        if (IsFlat)
        {
            UpdateFlat();
            return;
        }

        var target = GeoMath.LonLatToUnitVector(CenterLongitude, CenterLatitude);

        var east = MathF.Abs(target.Y) > 0.9995f
            ? Vec3.UnitX
            : Vec3.Normalize(Vec3.Cross(Vec3.UnitY, target));
        var north = Vec3.Cross(target, east);

        // Pitch 是视线相对地表法线的俯仰角；轨道偏移角随高度收缩，
        // 保证任何高度下中心点都在地平线以内（低空时不会再绕到球体背面）
        var orbitAngle = GetOrbitAngleForViewPitch(Pitch, Distance);
        var cosP = (float)Math.Cos(orbitAngle);
        var sinP = (float)Math.Sin(orbitAngle);

        var d = (float)Distance;
        Position = (target * cosP - north * sinP) * d;

        var forward = Vec3.Normalize(target - Position);
        if (forward.LengthSquared < 1e-12f)
            forward = -target;

        Vec3 right;
        if (MathF.Abs(Vec3.Dot(forward, target)) > 0.995f)
        {
            // 接近正俯视：屏幕上方对齐正北
            right = east;
        }
        else
        {
            right = Vec3.Normalize(Vec3.Cross(forward, target));
        }

        var up = Vec3.Normalize(Vec3.Cross(right, forward));
        right = Vec3.Normalize(Vec3.Cross(forward, up));

        if (Math.Abs(Heading) > 1e-6d)
        {
            var rot = Mat4.RotationAxis(forward, -(float)(Heading * MathUtil.DegToRad));
            right = TransformDirection(rot, right);
            up = TransformDirection(rot, up);
        }

        Forward = forward;
        Right = right;
        Up = up;

        var v = default(Mat4);
        v[0] = right.X;
        v[1] = up.X;
        v[2] = -forward.X;
        v[4] = right.Y;
        v[5] = up.Y;
        v[6] = -forward.Y;
        v[8] = right.Z;
        v[9] = up.Z;
        v[10] = -forward.Z;
        v[12] = -Vec3.Dot(right, Position);
        v[13] = -Vec3.Dot(up, Position);
        v[14] = Vec3.Dot(forward, Position);
        v[15] = 1f;
        View = v;

        var near = MathF.Max(0.0004f, (float)((Distance - 1d) * 0.2d));
        var far = (float)(Distance + 2.4d);
        if (far <= near * 1.5f)
            far = near * 10f;

        NearPlane = near;
        FarPlane = far;
        Projection = Mat4.Perspective((float)(FieldOfView * MathUtil.DegToRad), Aspect, near, far);
        ViewProjection = Mat4.Multiply(Projection, View);
    }

    /// <summary>
    /// 当前视线相对中心地表法线的实际俯仰角（度）。0 为垂直向下，接近 90 为接近水平；
    /// 平面模式固定返回 0。返回值基于最近一次 <see cref="Update"/> 计算的相机姿态。
    /// </summary>
    public double GetEffectivePitch()
    {
        if (IsFlat)
            return 0d;

        var target = GeoMath.LonLatToUnitVector(CenterLongitude, CenterLatitude);
        var forward = Vec3.Normalize(target - Position);
        if (forward.LengthSquared < 1e-12f)
            return Pitch;

        var cos = MathUtil.Clamp(Vec3.Dot(forward, -target), -1f, 1f);
        return Math.Acos(cos) * MathUtil.RadToDeg;
    }

    /// <summary>
    /// 把视线俯仰角反解为轨道偏移角（弧度）。
    /// 视线与中心点法线的夹角为 <paramref name="pitchDegrees"/> 时，
    /// 相机方向与中心点径向的夹角由三角关系唯一确定，且始终小于地平线角 acos(1/d)。
    /// </summary>
    internal static double GetOrbitAngleForViewPitch(double pitchDegrees, double distance)
    {
        var d = Math.Max(1d + 1e-9d, distance);
        var pitch = MathUtil.Clamp(pitchDegrees, 0d, 89.99d) * MathUtil.DegToRad;
        var k = Math.Cos(pitch);
        k *= k;

        if (k <= 1e-12d)
            return Math.Acos(1d / d);

        // 三角关系：cos(pitch) = (d·cosγ - 1) / sqrt(1 - 2d·cosγ + d²)
        // 令 u = d·cosγ - 1，解得 u = sqrt(k·(k + d² - 1)) - k
        var u = Math.Sqrt(k * (k + d * d - 1d)) - k;
        return Math.Acos(MathUtil.Clamp((u + 1d) / d, -1d, 1d));
    }

    /// <summary>把屏幕像素坐标转换为世界空间射线。平面模式下从相机中心（0,0,1）指向该屏幕点的地表。</summary>
    public void ScreenToRay(double screenX, double screenY, double viewWidth, double viewHeight,
        out Vec3 origin, out Vec3 direction)
    {
        origin = Position;
        if (viewWidth <= 0 || viewHeight <= 0)
        {
            direction = Forward;
            return;
        }

        if (IsFlat)
        {
            if (!ScreenToGround(screenX, screenY, viewWidth, viewHeight, out var point))
            {
                direction = Forward;
                return;
            }

            var target = GeoMath.LonLatToUnitVector(point.Longitude, point.Latitude);
            var delta = target - origin;
            direction = delta.LengthSquared > 1e-12f ? Vec3.Normalize(delta) : Forward;
            return;
        }

        var ndcX = (float)(2d * screenX / viewWidth - 1d);
        var ndcY = (float)(1d - 2d * screenY / viewHeight);
        var tanHalf = MathF.Tan((float)(FieldOfView * MathUtil.DegToRad) * 0.5f);
        var dir = Forward + Right * (ndcX * tanHalf * Aspect) + Up * (ndcY * tanHalf);
        direction = Vec3.Normalize(dir);
    }

    /// <summary>把屏幕像素坐标投射到地面。平面模式下按 Web 墨卡托反算，越界返回 false。</summary>
    public bool ScreenToGround(double screenX, double screenY, double viewWidth, double viewHeight,
        out GeoPoint point)
    {
        point = default;

        if (IsFlat)
        {
            var height = Math.Max(1d, viewHeight);
            var scale = FlatScaleFactor * height;
            var mercatorX = FlatCenterX + (screenX - viewWidth * 0.5d) / scale;
            var mercatorY = FlatCenterY + (screenY - height * 0.5d) / scale;
            if (mercatorX is < 0d or > 1d || mercatorY is < 0d or > 1d)
                return false;

            point = new GeoPoint(GeoMath.MercatorYToLatitude(mercatorY), mercatorX * 360d - 180d);
            return true;
        }

        ScreenToRay(screenX, screenY, viewWidth, viewHeight, out var origin, out var direction);
        if (!GeoMath.RaySphereIntersect(origin, direction, 1f, out var t))
            return false;

        var hit = origin + direction * t;
        GeoMath.UnitVectorToLonLat(hit, out var lon, out var lat);
        point = new GeoPoint(lat, lon, 0d);
        return true;
    }

    /// <summary>把世界坐标投影到屏幕像素坐标。平面模式下忽略高度，只按经纬度投影。</summary>
    public bool Project(Vec3 world, double viewWidth, double viewHeight,
        out double screenX, out double screenY, out float ndcDepth)
    {
        screenX = 0;
        screenY = 0;
        ndcDepth = 1f;

        if (IsFlat)
        {
            GeoMath.UnitVectorToLonLat(world, out var lon, out var lat);
            var height = Math.Max(1d, viewHeight);
            var scale = FlatScaleFactor * height;
            var mercatorX = (MathUtil.NormalizeLongitude(lon) + 180d) / 360d;
            var mercatorY = GeoMath.LatitudeToMercatorY(lat);
            screenX = viewWidth * 0.5d + (mercatorX - FlatCenterX) * scale;
            screenY = height * 0.5d + (mercatorY - FlatCenterY) * scale;
            ndcDepth = 0f;
            return true;
        }

        Mat4.TransformPoint(ViewProjection, world, out var x, out var y, out var z, out var w);
        if (w <= 1e-6f)
            return false;

        var ndcX = x / w;
        var ndcY = y / w;
        ndcDepth = z / w;
        screenX = (ndcX * 0.5d + 0.5d) * viewWidth;
        screenY = (0.5d - ndcY * 0.5d) * viewHeight;
        return true;
    }

    /// <summary>把经纬度（可带高度）投影到屏幕像素坐标。</summary>
    public bool ProjectGeo(GeoPoint geo, double viewWidth, double viewHeight,
        out double screenX, out double screenY, out float ndcDepth)
    {
        // 平面模式直接用经纬度换算，避免 ±180° 端点经过单位向量往返时被浮点误差翻到另一侧
        if (IsFlat)
        {
            var height = Math.Max(1d, viewHeight);
            var scale = FlatScaleFactor * height;
            var mercatorX = (MathUtil.NormalizeLongitude(geo.Longitude) + 180d) / 360d;
            var mercatorY = GeoMath.LatitudeToMercatorY(geo.Latitude);
            screenX = viewWidth * 0.5d + (mercatorX - FlatCenterX) * scale;
            screenY = height * 0.5d + (mercatorY - FlatCenterY) * scale;
            ndcDepth = 0f;
            return true;
        }

        var world = GeoMath.LonLatToUnitVector(geo.Longitude, geo.Latitude) *
                    GeoMath.AltitudeToRadius(geo.Altitude);
        return Project(world, viewWidth, viewHeight, out screenX, out screenY, out ndcDepth);
    }

    /// <summary>
    /// 按视口像素尺寸估算当前缩放级别（瓦片层级，与瓦片 LOD 使用同一套公式）。
    /// </summary>
    /// <param name="viewWidth">视口宽度（像素）。为与 LOD 一致，应传物理像素。</param>
    /// <param name="viewHeight">视口高度（像素）。为与 LOD 一致，应传物理像素。</param>
    /// <remarks>
    /// 平面模式：level = log2(FlatScaleFactor × 视口高度 / 256)。
    /// 三维模式：按屏幕中心附近的真实地表米/像素与中心纬度换算。
    /// </remarks>
    public double GetZoomLevel(double viewWidth, double viewHeight)
    {
        var width = Math.Max(1d, viewWidth);
        var height = Math.Max(1d, viewHeight);

        if (IsFlat)
        {
            var worldPixels = FlatScaleFactor * height;
            return Math.Log2(Math.Max(1d, worldPixels) / AssumedTileSize);
        }

        var metersPerPixel = GetMetersPerPixelAtCenter(width, height);
        if (!double.IsFinite(metersPerPixel) || metersPerPixel <= 0d)
            return double.NaN;

        var cosLatitude = Math.Max(0.02d, Math.Cos(CenterLatitude * MathUtil.DegToRad));
        var circumference = 2d * Math.PI * GeoMath.EarthRadius * cosLatitude;
        return Math.Log2(circumference / (AssumedTileSize * metersPerPixel));
    }

    /// <summary>
    /// 按缩放级别估算相机离地高度（正俯视、中心纬度近似），用于根据瓦片层级定位镜头。
    /// </summary>
    /// <param name="level">缩放级别（瓦片层级）。</param>
    /// <param name="viewHeight">视口高度（像素）。为与 LOD 一致，应传物理像素。</param>
    /// <remarks>
    /// 平面模式与纬度无关；三维模式按中心纬度做 cos 收缩。倾斜视角下实际显示级别会有少量偏差。
    /// </remarks>
    public double GetAltitudeForZoomLevel(double level, double viewHeight)
    {
        var height = Math.Max(1d, viewHeight);
        var tanHalf = Math.Max(1e-4d, Math.Tan(FieldOfView * MathUtil.DegToRad * 0.5d));
        var worldPixels = AssumedTileSize * Math.Pow(2d, Math.Clamp(level, -30d, 40d));

        if (IsFlat)
            return Math.PI * GeoMath.EarthRadius * height / (worldPixels * tanHalf);

        var cosLatitude = Math.Max(0.02d, Math.Cos(CenterLatitude * MathUtil.DegToRad));
        return Math.PI * GeoMath.EarthRadius * cosLatitude * height / (worldPixels * tanHalf);
    }

    /// <summary>屏幕中心附近的真实地表米/像素；射线未命中时按高度与视场角估算。</summary>
    private double GetMetersPerPixelAtCenter(double viewWidth, double viewHeight)
    {
        var sample = Math.Clamp(viewWidth * 0.1d, 24d, 160d);
        var x0 = viewWidth * 0.5d - sample * 0.5d;
        var y = viewHeight * 0.5d;

        if (ScreenToGround(x0, y, viewWidth, viewHeight, out var a) &&
            ScreenToGround(x0 + sample, y, viewWidth, viewHeight, out var b))
        {
            var distance = Geodesy.Distance(a, b);
            if (distance > 0d)
                return distance / sample;
        }

        var altitude = Math.Max(1d, AltitudeMeters);
        var tanHalf = Math.Tan(FieldOfView * MathUtil.DegToRad * 0.5d);
        return 2d * altitude * tanHalf / Math.Max(1d, viewHeight);
    }

    /// <summary>判断某个世界坐标点是否位于可见区域。平面模式下按屏幕范围判断。</summary>
    public bool IsVisible(Vec3 world)
    {
        if (IsFlat)
        {
            GeoMath.UnitVectorToLonLat(world, out var lon, out var lat);
            var mercatorX = (MathUtil.NormalizeLongitude(lon) + 180d) / 360d;
            var mercatorY = GeoMath.LatitudeToMercatorY(lat);
            var flatNdcX = (mercatorX - FlatCenterX) * 2d * FlatScaleFactor / Math.Max(0.0001d, Aspect);
            var flatNdcY = (mercatorY - FlatCenterY) * 2d * FlatScaleFactor;
            return Math.Abs(flatNdcX) <= 1.35d && Math.Abs(flatNdcY) <= 1.35d;
        }

        if (GeoMath.IsSegmentOccluded(Position, world, 1f))
            return false;

        Mat4.TransformPoint(ViewProjection, world, out var x, out var y, out var z, out var w);
        if (w <= 1e-6f)
            return false;

        var ndcX = x / w;
        var ndcY = y / w;
        var ndcZ = z / w;
        return ndcX is >= -1.35f and <= 1.35f && ndcY is >= -1.35f and <= 1.35f && ndcZ is >= -1f and <= 1f;
    }

    /// <summary>复制一份当前状态用于动画插值。</summary>
    public OrbitCamera Clone()
    {
        return new OrbitCamera
        {
            CenterLatitude = CenterLatitude,
            CenterLongitude = CenterLongitude,
            Distance = Distance,
            Pitch = Pitch,
            Heading = Heading,
            FieldOfView = FieldOfView,
            MinDistance = MinDistance,
            MaxDistance = MaxDistance,
            IsFlat = IsFlat
        };
    }

    /// <summary>平面模式下按屏幕像素平移地图中心。</summary>
    public void PanFlatByPixels(double dx, double dy, double viewWidth, double viewHeight)
    {
        if (!IsFlat)
            return;

        var height = Math.Max(1d, viewHeight);
        var scale = FlatScaleFactor * height;
        var halfWidth = Math.Min(0.5d, Math.Max(0d, viewWidth) / (2d * scale));
        var halfHeight = Math.Min(0.5d, height / (2d * scale));

        FlatCenterX = MathUtil.Clamp(FlatCenterX - dx / scale, halfWidth, 1d - halfWidth);
        FlatCenterY = MathUtil.Clamp(FlatCenterY - dy / scale, halfHeight, 1d - halfHeight);
        CenterLongitude = FlatCenterX * 360d - 180d;
        CenterLatitude = GeoMath.MercatorYToLatitude(FlatCenterY);
        Normalize();
    }

    /// <summary>平面模式下当前视口覆盖的经纬度范围。</summary>
    public bool TryGetFlatViewportBounds(out double west, out double east, out double south, out double north)
    {
        west = east = south = north = 0d;
        if (!IsFlat)
            return false;

        var halfWidth = Aspect / (2d * FlatScaleFactor);
        var halfHeight = 1d / (2d * FlatScaleFactor);
        var left = MathUtil.Clamp(FlatCenterX - halfWidth, 0d, 1d);
        var right = MathUtil.Clamp(FlatCenterX + halfWidth, 0d, 1d);
        var top = MathUtil.Clamp(FlatCenterY - halfHeight, 0d, 1d);
        var bottom = MathUtil.Clamp(FlatCenterY + halfHeight, 0d, 1d);

        west = left * 360d - 180d;
        east = right * 360d - 180d;
        north = GeoMath.MercatorYToLatitude(top);
        south = GeoMath.MercatorYToLatitude(bottom);
        return right > left && bottom > top;
    }

    /// <summary>平面模式下按墨卡托坐标设置地图中心（用于以光标为锚缩放）。</summary>
    public void SetFlatCenter(double mercatorX, double mercatorY, double viewWidth, double viewHeight)
    {
        if (!IsFlat)
            return;

        var height = Math.Max(1d, viewHeight);
        var scale = FlatScaleFactor * height;
        var halfWidth = Math.Min(0.5d, Math.Max(0d, viewWidth) / (2d * scale));
        var halfHeight = Math.Min(0.5d, height / (2d * scale));

        FlatCenterX = MathUtil.Clamp(mercatorX, halfWidth, 1d - halfWidth);
        FlatCenterY = MathUtil.Clamp(mercatorY, halfHeight, 1d - halfHeight);
        CenterLongitude = FlatCenterX * 360d - 180d;
        CenterLatitude = GeoMath.MercatorYToLatitude(FlatCenterY);
        Normalize();
    }

    private void UpdateFlat()
    {
        var altitude = Math.Max(1d, AltitudeMeters);
        var tanHalf = Math.Max(1e-4d, Math.Tan(FieldOfView * MathUtil.DegToRad * 0.5d));

        // 比例尺只由高度与视场角决定：Web 墨卡托的“世界像素数”不随中心纬度变化，
        // 否则纯平移会改变缩放比例尺，导致拖拽点跟不上光标、瓦片 LOD 反复跳级。
        var factor = Math.PI * GeoMath.EarthRadius / (altitude * tanHalf);
        FlatScaleFactor = Math.Max(factor, Math.Max(Aspect, 1d));

        var halfWidth = Aspect / (2d * FlatScaleFactor);
        var halfHeight = 1d / (2d * FlatScaleFactor);
        FlatCenterX = MathUtil.Clamp((MathUtil.NormalizeLongitude(CenterLongitude) + 180d) / 360d,
            halfWidth, 1d - halfWidth);
        FlatCenterY = MathUtil.Clamp(GeoMath.LatitudeToMercatorY(CenterLatitude),
            halfHeight, 1d - halfHeight);

        // 视口夹取只作用于实际投影中心，不回写 CenterLatitude/CenterLongitude：
        // 这两个属性保留调用方请求的目标中心，放大或缩小后仍可回到该位置；
        // 用户拖拽/缩放后由 PanFlatByPixels / SetFlatCenter 显式回写实际中心。
        // 平面模式不使用轨道相机，保留稳定的正交朝向供外部读取
        Position = new Vec3(0f, 0f, 1f);
        Forward = new Vec3(0f, 0f, -1f);
        Right = Vec3.UnitX;
        Up = Vec3.UnitY;
        NearPlane = 0.01f;
        FarPlane = 10f;
        View = Projection = ViewProjection = Mat4.Identity;
    }

    private static Vec3 TransformDirection(in Mat4 m, in Vec3 v)
    {
        return new Vec3(
            m[0] * v.X + m[4] * v.Y + m[8] * v.Z,
            m[1] * v.X + m[5] * v.Y + m[9] * v.Z,
            m[2] * v.X + m[6] * v.Y + m[10] * v.Z);
    }
}
