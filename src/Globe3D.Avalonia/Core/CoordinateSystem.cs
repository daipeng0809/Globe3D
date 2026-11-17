using System.Collections.Generic;

namespace Globe3D.Avalonia;

/// <summary>常见的地理/投影坐标系。</summary>
public enum CoordinateSystem
{
    /// <summary>未知，或组合数据源中存在多种坐标系。</summary>
    Unknown = 0,

    /// <summary>WGS84 经纬度（EPSG:4326）。</summary>
    Wgs84 = 1,

    /// <summary>Web 墨卡托投影坐标（EPSG:3857，单位米）。</summary>
    WebMercator = 2,

    /// <summary>GCJ-02（火星坐标系，高德/腾讯等国内地图使用）。</summary>
    Gcj02 = 3,

    /// <summary>BD-09（百度坐标系）。</summary>
    Bd09 = 4
}

/// <summary>
/// 坐标系检测工具。根据瓦片 URL / 名称 / MBTiles metadata 推断常见服务商使用的坐标系。
/// </summary>
/// <remarks>
/// 检测是启发式的：自定义代理、镜像域名可能无法识别，建议通过
/// <see cref="HttpTileSource"/> 的 <c>coordinateSystem</c> 参数或 MBTiles 构造函数显式指定。
/// </remarks>
public static class CoordinateSystemDetector
{
    private static readonly string[] MetadataKeys = ["crs", "srs", "coordinate_system", "coordinate-system"];

    /// <summary>根据 URL 模板与可选的显示名称推断坐标系；未识别时返回 <see cref="CoordinateSystem.Wgs84"/>。</summary>
    public static CoordinateSystem Detect(string? urlTemplate, string? name = null)
    {
        var text = ((urlTemplate ?? string.Empty) + " " + (name ?? string.Empty)).ToLowerInvariant();
        if (Canonicalize(text) is { } known)
            return known;

        return CoordinateSystem.Wgs84;
    }

    /// <summary>根据 MBTiles metadata 中的 crs/srs/coordinate_system 字段推断坐标系。</summary>
    public static CoordinateSystem DetectMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata == null)
            return CoordinateSystem.Wgs84;

        foreach (var key in MetadataKeys)
        {
            if (metadata.TryGetValue(key, out var value) && Canonicalize(value.ToLowerInvariant()) is { } known)
                return known;
        }

        return CoordinateSystem.Wgs84;
    }

    private static CoordinateSystem? Canonicalize(string text)
    {
        if (text.Length == 0)
            return null;

        // 百度必须先判断，避免名称里同时出现“地图”等泛化词
        if (text.Contains("bdimg") || text.Contains("map.baidu") || text.Contains("baidu.com") ||
            text.Contains("bd-09") || text.Contains("bd09") || text.Contains("百度"))
            return CoordinateSystem.Bd09;

        if (text.Contains("autonavi") || text.Contains("amap") || text.Contains("gaode") ||
            text.Contains("gtimg.com") || text.Contains("map.qq.com") || text.Contains("高德") ||
            text.Contains("腾讯") || text.Contains("gcj-02") || text.Contains("gcj02"))
            return CoordinateSystem.Gcj02;

        if (text.Contains("wgs84") || text.Contains("wgs-84") || text.Contains("epsg:4326") ||
            text.Contains("epsg:3857") || text.Contains("epsg:900913"))
            return CoordinateSystem.Wgs84;

        return null;
    }
}

/// <summary>
/// 常见坐标系之间的转换工具。控件的 <see cref="GeoPoint"/>、标记、相机和区域统一使用 WGS84，
/// 本类用于把用户数据从 GCJ-02 / BD-09 / Web 墨卡托转换到 WGS84（或反向转换）。
/// </summary>
/// <remarks>
/// <para>
/// GCJ-02 / BD-09 转换使用国内常用的 Krasovsky 椭球参数与公开近似算法；境外坐标不进行偏移。
/// </para>
/// <para>
/// Web 墨卡托是投影坐标（单位米），请使用 <see cref="WebMercatorToWgs84"/> /
/// <see cref="Wgs84ToWebMercator"/>，不要把它当作经纬度传入 <see cref="ToWgs84(GeoPoint, CoordinateSystem)"/>。
/// </para>
/// </remarks>
public static class CoordinateConverter
{
    private const double KrasovskyA = 6378245.0;
    private const double KrasovskyEe = 0.00669342162296594323;
    private const double MercatorRadius = 6378137.0;
    private const double MaxMercatorLatitude = 85.05112877980659;

    /// <summary>把指定坐标系的经纬度转换为 WGS84；高度保持不变。</summary>
    public static GeoPoint ToWgs84(GeoPoint point, CoordinateSystem source)
    {
        return source switch
        {
            CoordinateSystem.Wgs84 => point,
            CoordinateSystem.Gcj02 => Convert(point, Gcj02ToWgs84(point.Latitude, point.Longitude)),
            CoordinateSystem.Bd09 => Convert(point, Bd09ToWgs84(point.Latitude, point.Longitude)),
            CoordinateSystem.WebMercator => throw new ArgumentException(
                "WebMercator 是投影坐标，请使用 WebMercatorToWgs84(x, y)", nameof(source)),
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, "不支持的坐标系")
        };
    }

    /// <summary>把指定坐标系的经纬度转换为 WGS84。</summary>
    public static (double Latitude, double Longitude) ToWgs84(double latitude, double longitude,
        CoordinateSystem source)
    {
        var point = ToWgs84(new GeoPoint(latitude, longitude), source);
        return (point.Latitude, point.Longitude);
    }

    /// <summary>把 WGS84 经纬度转换为目标坐标系；高度保持不变。</summary>
    public static GeoPoint FromWgs84(GeoPoint point, CoordinateSystem target)
    {
        return target switch
        {
            CoordinateSystem.Wgs84 => point,
            CoordinateSystem.Gcj02 => Convert(point, Wgs84ToGcj02(point.Latitude, point.Longitude)),
            CoordinateSystem.Bd09 => Convert(point, Wgs84ToBd09(point.Latitude, point.Longitude)),
            CoordinateSystem.WebMercator => throw new ArgumentException(
                "WebMercator 是投影坐标，请使用 Wgs84ToWebMercator(latitude, longitude)", nameof(target)),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "不支持的坐标系")
        };
    }

    /// <summary>把 WGS84 经纬度转换为目标坐标系。</summary>
    public static (double Latitude, double Longitude) FromWgs84(double latitude, double longitude,
        CoordinateSystem target)
    {
        var point = FromWgs84(new GeoPoint(latitude, longitude), target);
        return (point.Latitude, point.Longitude);
    }

    /// <summary>WGS84 经纬度转 GCJ-02。</summary>
    public static (double Latitude, double Longitude) Wgs84ToGcj02(double latitude, double longitude)
    {
        if (OutOfChina(latitude, longitude))
            return (latitude, longitude);

        var dLat = TransformLatitude(longitude - 105d, latitude - 35d);
        var dLon = TransformLongitude(longitude - 105d, latitude - 35d);
        var radLat = latitude * Math.PI / 180d;
        var magic = Math.Sin(radLat);
        magic = 1d - KrasovskyEe * magic * magic;
        var sqrtMagic = Math.Sqrt(magic);
        dLat = dLat * 180d / (KrasovskyA * (1d - KrasovskyEe) / (magic * sqrtMagic) * Math.PI);
        dLon = dLon * 180d / (KrasovskyA / sqrtMagic * Math.Cos(radLat) * Math.PI);
        return (latitude + dLat, longitude + dLon);
    }

    /// <summary>GCJ-02 转 WGS84（迭代逼近）。</summary>
    public static (double Latitude, double Longitude) Gcj02ToWgs84(double latitude, double longitude)
    {
        if (OutOfChina(latitude, longitude))
            return (latitude, longitude);

        var wgsLat = latitude;
        var wgsLon = longitude;
        for (var i = 0; i < 10; i++)
        {
            var (gcjLat, gcjLon) = Wgs84ToGcj02(wgsLat, wgsLon);
            wgsLat -= gcjLat - latitude;
            wgsLon -= gcjLon - longitude;
        }

        return (wgsLat, wgsLon);
    }

    /// <summary>WGS84 经纬度转 BD-09。</summary>
    public static (double Latitude, double Longitude) Wgs84ToBd09(double latitude, double longitude)
    {
        var (gcjLat, gcjLon) = Wgs84ToGcj02(latitude, longitude);
        return Gcj02ToBd09(gcjLat, gcjLon);
    }

    /// <summary>BD-09 转 WGS84。</summary>
    public static (double Latitude, double Longitude) Bd09ToWgs84(double latitude, double longitude)
    {
        var (gcjLat, gcjLon) = Bd09ToGcj02(latitude, longitude);
        return Gcj02ToWgs84(gcjLat, gcjLon);
    }

    /// <summary>GCJ-02 转 BD-09。</summary>
    public static (double Latitude, double Longitude) Gcj02ToBd09(double latitude, double longitude)
    {
        if (OutOfChina(latitude, longitude))
            return (latitude, longitude);

        var z = Math.Sqrt(longitude * longitude + latitude * latitude) +
                0.00002d * Math.Sin(latitude * Math.PI * 3000d / 180d);
        var theta = Math.Atan2(latitude, longitude) +
                    0.000003d * Math.Cos(longitude * Math.PI * 3000d / 180d);
        return (z * Math.Sin(theta) + 0.006d, z * Math.Cos(theta) + 0.0065d);
    }

    /// <summary>BD-09 转 GCJ-02。</summary>
    public static (double Latitude, double Longitude) Bd09ToGcj02(double latitude, double longitude)
    {
        if (OutOfChina(latitude, longitude))
            return (latitude, longitude);

        var x = longitude - 0.0065d;
        var y = latitude - 0.006d;
        var z = Math.Sqrt(x * x + y * y) - 0.00002d * Math.Sin(y * Math.PI * 3000d / 180d);
        var theta = Math.Atan2(y, x) - 0.000003d * Math.Cos(x * Math.PI * 3000d / 180d);
        return (z * Math.Sin(theta), z * Math.Cos(theta));
    }

    /// <summary>WGS84 经纬度转 Web 墨卡托（EPSG:3857，单位米）。</summary>
    public static (double X, double Y) Wgs84ToWebMercator(double latitude, double longitude)
    {
        var clampedLatitude = MathUtil.Clamp(latitude, -MaxMercatorLatitude, MaxMercatorLatitude);
        var x = longitude * Math.PI / 180d * MercatorRadius;
        var y = Math.Log(Math.Tan(Math.PI / 4d + clampedLatitude * Math.PI / 360d)) * MercatorRadius;
        return (x, y);
    }

    /// <summary>Web 墨卡托（EPSG:3857，单位米）转 WGS84。</summary>
    public static GeoPoint WebMercatorToWgs84(double x, double y)
    {
        var longitude = x / MercatorRadius * 180d / Math.PI;
        var latitude = (2d * Math.Atan(Math.Exp(y / MercatorRadius)) - Math.PI / 2d) * 180d / Math.PI;
        return new GeoPoint(latitude, longitude);
    }

    private static GeoPoint Convert(GeoPoint source, (double Latitude, double Longitude) target) =>
        new(target.Latitude, target.Longitude, source.Altitude);

    private static bool OutOfChina(double latitude, double longitude) =>
        longitude < 72.004d || longitude > 137.8347d || latitude < 0.8293d || latitude > 55.8271d;

    private static double TransformLatitude(double x, double y)
    {
        var result = -100d + 2d * x + 3d * y + 0.2d * y * y + 0.1d * x * y + 0.2d * Math.Sqrt(Math.Abs(x));
        result += (20d * Math.Sin(6d * x * Math.PI) + 20d * Math.Sin(2d * x * Math.PI)) * 2d / 3d;
        result += (20d * Math.Sin(y * Math.PI) + 40d * Math.Sin(y / 3d * Math.PI)) * 2d / 3d;
        result += (160d * Math.Sin(y / 12d * Math.PI) + 320d * Math.Sin(y * Math.PI / 30d)) * 2d / 3d;
        return result;
    }

    private static double TransformLongitude(double x, double y)
    {
        var result = 300d + x + 2d * y + 0.1d * x * x + 0.1d * x * y + 0.1d * Math.Sqrt(Math.Abs(x));
        result += (20d * Math.Sin(6d * x * Math.PI) + 20d * Math.Sin(2d * x * Math.PI)) * 2d / 3d;
        result += (20d * Math.Sin(x * Math.PI) + 40d * Math.Sin(x / 3d * Math.PI)) * 2d / 3d;
        result += (150d * Math.Sin(x / 12d * Math.PI) + 300d * Math.Sin(x / 30d * Math.PI)) * 2d / 3d;
        return result;
    }
}
