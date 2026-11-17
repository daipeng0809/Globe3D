namespace Globe3D.Avalonia;

/// <summary>
/// 瓦片标识，采用 XYZ 约定（Y = 0 位于最北，向下递增），与主流在线瓦片服务一致。
/// </summary>
public readonly record struct TileId(int Z, int X, int Y)
{
    /// <summary>本级瓦片数量（每边）。</summary>
    public int TileCount => 1 << Z;

    /// <summary>父级瓦片标识（Z - 1）。</summary>
    public TileId Parent => new(Z - 1, X >> 1, Y >> 1);

    /// <summary>上溯指定层级后的祖先瓦片。</summary>
    public TileId Ancestor(int levels) => new(Z - levels, X >> levels, Y >> levels);

    /// <summary>是否落在合法范围内。</summary>
    public bool IsValid =>
        Z is >= 0 and <= 22 &&
        X >= 0 && Y >= 0 && X < TileCount && Y < TileCount;

    /// <summary>该瓦片覆盖的墨卡托矩形（0..1 归一化）。</summary>
    public void GetMercatorBounds(out float left, out float top, out float right, out float bottom)
    {
        var n = TileCount;
        left = X / (float)n;
        top = Y / (float)n;
        right = (X + 1) / (float)n;
        bottom = (Y + 1) / (float)n;
    }

    /// <summary>该瓦片覆盖的地理范围。</summary>
    public GeoBounds GetGeoBounds()
    {
        var n = TileCount;
        var west = X / (double)n * 360d - 180d;
        var east = (X + 1) / (double)n * 360d - 180d;
        var north = GeoMath.MercatorYToLatitude(Y / (double)n);
        var south = GeoMath.MercatorYToLatitude((Y + 1) / (double)n);
        return new GeoBounds(west, south, east, north);
    }

    public override string ToString() => $"{Z}/{X}/{Y}";
}

/// <summary>瓦片数据源元信息。</summary>
public sealed class TileSourceInfo
{
    public string Name { get; init; } = "tiles";

    public int MinZoom { get; init; } = 0;

    public int MaxZoom { get; init; } = 18;

    /// <summary>数据覆盖范围（可能为空，表示全球）。</summary>
    public GeoBounds? Bounds { get; init; }

    public string? Format { get; init; }

    /// <summary>
    /// 瓦片数据的坐标系（默认 WGS84）。控件始终按 WGS84 / Web 墨卡托瓦片索引渲染；
    /// 若该值为 <see cref="CoordinateSystem.Gcj02"/> 或 <see cref="CoordinateSystem.Bd09"/>，
    /// 说明数据本身带偏移，需由调用方用 <see cref="CoordinateConverter"/> 处理坐标。
    /// </summary>
    public CoordinateSystem CoordinateSystem { get; init; } = CoordinateSystem.Wgs84;
}

/// <summary>瓦片数据源。实现需保证线程安全，可被多个后台线程并发调用。</summary>
public interface ITileSource
{
    /// <summary>数据源元信息。</summary>
    TileSourceInfo Info { get; }

    /// <summary>获取指定瓦片的原始编码数据（PNG/JPEG），不存在时返回 null。</summary>
    Task<byte[]?> GetTileAsync(TileId id, CancellationToken cancellationToken = default);
}

/// <summary>瓦片加载失败/缺失的判定结果。</summary>
public enum TileFetchStatus
{
    /// <summary>获取成功。</summary>
    Success,

    /// <summary>该层级不存在瓦片。</summary>
    Missing,

    /// <summary>发生错误（可重试）。</summary>
    Failed,

    /// <summary>超出数据源覆盖的层级。</summary>
    OutOfRange
}
