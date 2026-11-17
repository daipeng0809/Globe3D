namespace Globe3D.Avalonia;

/// <summary>地理坐标点（WGS84 经纬度，单位度；高度单位米）。</summary>
public readonly struct GeoPoint
{
    public GeoPoint(double latitude, double longitude, double altitude = 0)
    {
        Latitude = latitude;
        Longitude = longitude;
        Altitude = altitude;
    }

    /// <summary>纬度，单位度，范围 -90..90。</summary>
    public double Latitude { get; }

    /// <summary>经度，单位度，范围 -180..180。</summary>
    public double Longitude { get; }

    /// <summary>海拔高度，单位米。</summary>
    public double Altitude { get; }

    public override string ToString() => $"({Latitude:0.######}, {Longitude:0.######}, {Altitude:0.###}m)";
}

/// <summary>经纬度矩形范围。</summary>
public readonly struct GeoBounds
{
    public GeoBounds(double west, double south, double east, double north)
    {
        West = west;
        South = south;
        East = east;
        North = north;
    }

    public double West { get; }

    public double South { get; }

    public double East { get; }

    public double North { get; }

    public override string ToString() =>
        $"[{West:0.####},{South:0.####} .. {East:0.####},{North:0.####}]";
}

/// <summary>
/// 球面地理数学工具。内部统一使用半径为 1 的单位球（Y 轴指向北极）。
/// 单位向量约定：x = cos(lat)·sin(lon)，y = sin(lat)，z = cos(lat)·cos(lon)。
/// </summary>
public static class GeoMath
{
    /// <summary>WGS84 赤道半径，单位米。</summary>
    public const double EarthRadius = 6378137.0;

    /// <summary>Web 墨卡托可表示的最大纬度。</summary>
    public const double MaxMercatorLatitude = 85.05112877980659;

    /// <summary>经纬度转单位球面上的三维向量。</summary>
    public static Vec3 LonLatToUnitVector(double longitudeDeg, double latitudeDeg)
    {
        var lon = (float)(longitudeDeg * MathUtil.DegToRad);
        var lat = (float)(latitudeDeg * MathUtil.DegToRad);
        var cosLat = MathF.Cos(lat);
        return new Vec3(cosLat * MathF.Sin(lon), MathF.Sin(lat), cosLat * MathF.Cos(lon));
    }

    /// <summary>单位向量转经纬度。</summary>
    public static void UnitVectorToLonLat(Vec3 v, out double longitudeDeg, out double latitudeDeg)
    {
        var len = v.Length;
        if (len < 1e-9f)
        {
            longitudeDeg = 0;
            latitudeDeg = 0;
            return;
        }

        var ny = MathUtil.Clamp(v.Y / len, -1f, 1f);
        latitudeDeg = MathF.Asin(ny) * MathUtil.RadToDeg;
        longitudeDeg = MathF.Atan2(v.X, v.Z) * MathUtil.RadToDeg;
        longitudeDeg = MathUtil.NormalizeLongitude(longitudeDeg);
    }

    /// <summary>海拔（米）转单位球半径。</summary>
    public static float AltitudeToRadius(double altitudeMeters) =>
        (float)(1d + altitudeMeters / EarthRadius);

    /// <summary>Web 墨卡托纵向参数（0 为北，1 为南）转纬度。</summary>
    public static double MercatorYToLatitude(double mercatorY01)
    {
        var y = MathUtil.Clamp(mercatorY01, 0d, 1d);
        return Math.Atan(Math.Sinh(Math.PI * (1d - 2d * y))) * MathUtil.RadToDeg;
    }

    /// <summary>纬度转 Web 墨卡托纵向参数（0 为北，1 为南）。</summary>
    public static double LatitudeToMercatorY(double latitudeDeg)
    {
        var lat = MathUtil.Clamp(latitudeDeg, -MaxMercatorLatitude, MaxMercatorLatitude) * MathUtil.DegToRad;
        return (1d - Math.Asinh(Math.Tan(lat)) / Math.PI) * 0.5d;
    }

    /// <summary>经度转瓦片列号（浮点）。</summary>
    public static double LongitudeToTileX(double longitudeDeg, int zoom)
    {
        var n = 1 << zoom;
        return (MathUtil.NormalizeLongitude(longitudeDeg) + 180d) / 360d * n;
    }

    /// <summary>纬度转瓦片行号（浮点，向下为南）。</summary>
    public static double LatitudeToTileY(double latitudeDeg, int zoom)
    {
        var n = 1 << zoom;
        return LatitudeToMercatorY(latitudeDeg) * n;
    }

    /// <summary>射线与以原点为中心的球求交，返回最近的正向交点参数 t。</summary>
    public static bool RaySphereIntersect(Vec3 origin, Vec3 direction, float radius, out float t)
    {
        t = 0f;
        var b = Vec3.Dot(origin, direction);
        var c = origin.LengthSquared - radius * radius;
        var disc = b * b - c;
        if (disc < 0f)
            return false;

        var sqrt = MathF.Sqrt(disc);
        var t0 = -b - sqrt;
        var t1 = -b + sqrt;

        if (t0 > 0f)
        {
            t = t0;
            return true;
        }

        if (t1 > 0f)
        {
            t = t1;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 判断球面上的点（单位球）是否被球体自身遮挡（相对给定相机位置）。
    /// </summary>
    /// <remarks>
    /// 相机在球外时，可见区域是球面上与相机切平面相交的球冠：
    /// 单位方向 u 可见 ⇔ dot(u, cameraPosition) ≥ 1。
    /// （不能用“与视线夹角”判断：远侧与地平线外侧的夹角都会变小，会把背面误判为可见）
    /// </remarks>
    public static bool IsOccludedBySphere(Vec3 cameraPosition, Vec3 point)
    {
        var length = point.Length;
        if (length < 1e-9f)
            return false;

        return Vec3.Dot(point / length, cameraPosition) < 1f;
    }

    /// <summary>
    /// 计算线段（相机到目标点）是否被球体遮挡，适用于带高度的目标点。
    /// </summary>
    public static bool IsSegmentOccluded(Vec3 cameraPosition, Vec3 target, float radius)
    {
        var delta = target - cameraPosition;
        var length = delta.Length;
        if (length < 1e-9f)
            return false;

        return RaySphereIntersect(cameraPosition, delta / length, radius, out var t) && t < length - 1e-4f;
    }
}
