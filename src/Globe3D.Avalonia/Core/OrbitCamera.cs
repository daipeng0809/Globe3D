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

    /// <summary>俯仰角（度）。0 为正俯视，值越大越接近水平。</summary>
    public double Pitch { get; set; }

    /// <summary>航向角（度），绕视线方向旋转画面。</summary>
    public double Heading { get; set; }

    /// <summary>垂直视场角（度）。</summary>
    public double FieldOfView { get; set; } = 45.0;

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

        var target = GeoMath.LonLatToUnitVector(CenterLongitude, CenterLatitude);

        var east = MathF.Abs(target.Y) > 0.9995f
            ? Vec3.UnitX
            : Vec3.Normalize(Vec3.Cross(Vec3.UnitY, target));
        var north = Vec3.Cross(target, east);

        var pitchRad = (float)(Pitch * MathUtil.DegToRad);
        var cosP = MathF.Cos(pitchRad);
        var sinP = MathF.Sin(pitchRad);

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

    /// <summary>把屏幕像素坐标转换为世界空间射线。</summary>
    public void ScreenToRay(double screenX, double screenY, double viewWidth, double viewHeight,
        out Vec3 origin, out Vec3 direction)
    {
        origin = Position;
        if (viewWidth <= 0 || viewHeight <= 0)
        {
            direction = Forward;
            return;
        }

        var ndcX = (float)(2d * screenX / viewWidth - 1d);
        var ndcY = (float)(1d - 2d * screenY / viewHeight);
        var tanHalf = MathF.Tan((float)(FieldOfView * MathUtil.DegToRad) * 0.5f);
        var dir = Forward + Right * (ndcX * tanHalf * Aspect) + Up * (ndcY * tanHalf);
        direction = Vec3.Normalize(dir);
    }

    /// <summary>把屏幕像素坐标投射到地球表面。</summary>
    public bool ScreenToGround(double screenX, double screenY, double viewWidth, double viewHeight,
        out GeoPoint point)
    {
        point = default;
        ScreenToRay(screenX, screenY, viewWidth, viewHeight, out var origin, out var direction);
        if (!GeoMath.RaySphereIntersect(origin, direction, 1f, out var t))
            return false;

        var hit = origin + direction * t;
        GeoMath.UnitVectorToLonLat(hit, out var lon, out var lat);
        point = new GeoPoint(lat, lon, 0d);
        return true;
    }

    /// <summary>把世界坐标投影到屏幕像素坐标。</summary>
    public bool Project(Vec3 world, double viewWidth, double viewHeight,
        out double screenX, out double screenY, out float ndcDepth)
    {
        screenX = 0;
        screenY = 0;
        ndcDepth = 1f;
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
        var world = GeoMath.LonLatToUnitVector(geo.Longitude, geo.Latitude) *
                    GeoMath.AltitudeToRadius(geo.Altitude);
        return Project(world, viewWidth, viewHeight, out screenX, out screenY, out ndcDepth);
    }

    /// <summary>判断某个世界坐标点是否位于地球遮挡之外可见。</summary>
    public bool IsVisible(Vec3 world)
    {
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
            MaxDistance = MaxDistance
        };
    }

    private static Vec3 TransformDirection(in Mat4 m, in Vec3 v)
    {
        return new Vec3(
            m[0] * v.X + m[4] * v.Y + m[8] * v.Z,
            m[1] * v.X + m[5] * v.Y + m[9] * v.Z,
            m[2] * v.X + m[6] * v.Y + m[10] * v.Z);
    }
}
