using System.Globalization;

namespace Globe3D.Avalonia;

/// <summary>
/// 地表测地线工具：两点距离、目标点推算、圆/方形的经纬度环、球面多边形面积，
/// 以及距离/面积的显示格式化。所有几何都按球面（大圆）计算，与三维/平面模式一致。
/// </summary>
public static class Geodesy
{
    /// <summary>地球半径（米），与 <see cref="GeoMath.EarthRadius"/> 相同。</summary>
    public const double EarthRadius = GeoMath.EarthRadius;

    /// <summary>经纬度与高度是否均为有限值。</summary>
    public static bool IsFinite(GeoPoint point) =>
        double.IsFinite(point.Latitude) && double.IsFinite(point.Longitude) && double.IsFinite(point.Altitude);

    /// <summary>两点之间的地表大圆距离（米）。</summary>
    public static double Distance(GeoPoint a, GeoPoint b) =>
        Distance(a.Latitude, a.Longitude, b.Latitude, b.Longitude);

    /// <summary>两点之间的地表大圆距离（米）。</summary>
    public static double Distance(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        var lat1 = latitude1 * MathUtil.DegToRad;
        var lat2 = latitude2 * MathUtil.DegToRad;
        var dLat = (latitude2 - latitude1) * MathUtil.DegToRad;
        var dLon = MathUtil.NormalizeLongitudeDelta(longitude2 - longitude1) * MathUtil.DegToRad;

        var sinLat = Math.Sin(dLat * 0.5d);
        var sinLon = Math.Sin(dLon * 0.5d);
        var h = sinLat * sinLat + Math.Cos(lat1) * Math.Cos(lat2) * sinLon * sinLon;
        return 2d * EarthRadius * Math.Asin(Math.Min(1d, Math.Sqrt(Math.Max(0d, h))));
    }

    /// <summary>从起点沿给定方位角前进指定距离后的经纬度（大圆目标点）。</summary>
    /// <param name="origin">起点。</param>
    /// <param name="distanceMeters">前进距离（米）。</param>
    /// <param name="bearingDegrees">方位角（度，0 为正北，顺时针）。</param>
    public static GeoPoint Destination(GeoPoint origin, double distanceMeters, double bearingDegrees)
    {
        if (!IsFinite(origin) || !double.IsFinite(distanceMeters) || !double.IsFinite(bearingDegrees))
            return origin;

        distanceMeters = Math.Max(0d, distanceMeters);
        var angular = distanceMeters / EarthRadius;
        var bearing = bearingDegrees * MathUtil.DegToRad;

        // 极点附近 cos(纬度) 趋近 0，目标点经度会退化到一条经线；
        // 微小内缩可让圆/方形在极点附近仍能得到完整的经纬度环（误差 < 1 m）。
        var lat1 = Math.Clamp(origin.Latitude, -89.999d, 89.999d) * MathUtil.DegToRad;
        var lon1 = origin.Longitude * MathUtil.DegToRad;

        var sinLat1 = Math.Sin(lat1);
        var cosLat1 = Math.Cos(lat1);
        var sinAngular = Math.Sin(angular);
        var cosAngular = Math.Cos(angular);

        var sinLat2 = sinLat1 * cosAngular + cosLat1 * sinAngular * Math.Cos(bearing);
        var lat2 = Math.Asin(MathUtil.Clamp(sinLat2, -1d, 1d));
        var lon2 = lon1 + Math.Atan2(Math.Sin(bearing) * sinAngular * cosLat1,
            cosAngular - sinLat1 * sinLat2);

        return new GeoPoint(lat2 * MathUtil.RadToDeg, MathUtil.NormalizeLongitude(lon2 * MathUtil.RadToDeg));
    }

    /// <summary>折线（或闭合多边形）的地表长度（米）。</summary>
    public static double PathLength(IReadOnlyList<GeoPoint> points, bool closed = false)
    {
        if (points.Count < 2)
            return 0d;

        var total = 0d;
        for (var i = 0; i < points.Count - 1; i++)
            total += Distance(points[i], points[i + 1]);

        if (closed && points.Count > 2)
            total += Distance(points[^1], points[0]);

        return total;
    }

    /// <summary>
    /// 球面多边形面积（平方米）。采用经纬度环面积公式，经度差按最短路径展开，
    /// 因此跨 ±180° 经线的多边形也能得到正确结果。
    /// </summary>
    /// <remarks>
    /// 环面积公式对包住极点的环会得到“全球面积 − 实际面积”，这里按较小一侧归一：
    /// 当结果超过半个球面时取补集，因此测量结果总是不大于 2πR²。
    /// </remarks>
    public static double PolygonArea(IReadOnlyList<GeoPoint> points)
    {
        if (points.Count < 3)
            return 0d;

        var total = 0d;
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            if (!IsFinite(a) || !IsFinite(b))
                return 0d;

            var dLon = MathUtil.NormalizeLongitudeDelta(b.Longitude - a.Longitude) * MathUtil.DegToRad;
            total += dLon * (2d + Math.Sin(a.Latitude * MathUtil.DegToRad) +
                             Math.Sin(b.Latitude * MathUtil.DegToRad));
        }

        var area = Math.Abs(total) * EarthRadius * EarthRadius * 0.5d;
        var wholeSphere = 4d * Math.PI * EarthRadius * EarthRadius;
        return area > wholeSphere * 0.5d ? Math.Max(0d, wholeSphere - area) : area;
    }

    /// <summary>测地线圆覆盖的球面面积（球冠面积，平方米）。</summary>
    public static double CircleArea(double radiusMeters)
    {
        if (!double.IsFinite(radiusMeters) || radiusMeters <= 0d)
            return 0d;

        var angular = Math.Min(Math.PI, radiusMeters / EarthRadius);
        return 2d * Math.PI * EarthRadius * EarthRadius * (1d - Math.Cos(angular));
    }

    /// <summary>生成以 <paramref name="center"/> 为圆心、半径（米）的经纬度环。</summary>
    public static IReadOnlyList<GeoPoint> CircleRing(GeoPoint center, double radiusMeters, int segmentCount = 96)
    {
        var segments = Math.Clamp(segmentCount, 12, 720);
        var ring = new List<GeoPoint>(segments);
        if (!IsFinite(center) || !double.IsFinite(radiusMeters) || radiusMeters <= 0d)
            return ring;

        for (var i = 0; i < segments; i++)
            ring.Add(Destination(center, radiusMeters, i * 360d / segments));

        return ring;
    }

    /// <summary>
    /// 生成以 <paramref name="center"/> 为中心、边长为 <paramref name="sideMeters"/> 的方形经纬度环。
    /// </summary>
    /// <param name="center">中心点。</param>
    /// <param name="sideMeters">边长（米）。</param>
    /// <param name="headingDegrees">方形相对正北的旋转角（度）。</param>
    public static IReadOnlyList<GeoPoint> SquareRing(GeoPoint center, double sideMeters, double headingDegrees = 0d)
    {
        var ring = new List<GeoPoint>(4);
        if (!IsFinite(center) || !double.IsFinite(sideMeters) || !double.IsFinite(headingDegrees) ||
            sideMeters <= 0d)
            return ring;

        // 中心到角点的距离是外接圆半径：side / √2
        var halfDiagonal = sideMeters / Math.Sqrt(2d);
        for (var i = 0; i < 4; i++)
            ring.Add(Destination(center, halfDiagonal, headingDegrees + 45d + i * 90d));

        return ring;
    }

    /// <summary>
    /// 根据「中心 + 角点」推算正北朝上方形的边长：取角点相对中心的南北、东西偏移中的较大者
    /// 作为半边长，因此方形边界总会覆盖到鼠标所在的经纬度范围。
    /// </summary>
    public static double SquareSideFromCorner(GeoPoint center, GeoPoint corner)
    {
        if (!IsFinite(center) || !IsFinite(corner))
            return 0d;

        var north = new GeoPoint(corner.Latitude, center.Longitude);
        var east = new GeoPoint(center.Latitude, corner.Longitude);
        var halfLatitude = Distance(north, corner);
        var halfLongitude = Distance(east, corner);
        return 2d * Math.Max(halfLatitude, halfLongitude);
    }

    /// <summary>把距离格式化为便于阅读的文本（m / km）。</summary>
    public static string FormatDistance(double meters)
    {
        if (!double.IsFinite(meters) || meters < 0d)
            return "-";

        if (meters < 1d)
            return string.Create(CultureInfo.CurrentCulture, $"{meters:0.00} m");
        if (meters < 1000d)
            return string.Create(CultureInfo.CurrentCulture, $"{meters:0.#} m");
        if (meters < 1000_000d)
            return string.Create(CultureInfo.CurrentCulture, $"{meters / 1000d:#,##0.##} km");
        return string.Create(CultureInfo.CurrentCulture, $"{meters / 1000d:#,##0} km");
    }

    /// <summary>把面积格式化为便于阅读的文本（m² / km²）。</summary>
    public static string FormatArea(double squareMeters)
    {
        if (!double.IsFinite(squareMeters) || squareMeters < 0d)
            return "-";

        if (squareMeters < 1d)
            return string.Create(CultureInfo.CurrentCulture, $"{squareMeters:0.00} m²");
        if (squareMeters < 1_000_000d)
            return string.Create(CultureInfo.CurrentCulture, $"{squareMeters:#,##0.#} m²");
        return string.Create(CultureInfo.CurrentCulture, $"{squareMeters / 1_000_000d:#,##0.##} km²");
    }
}
