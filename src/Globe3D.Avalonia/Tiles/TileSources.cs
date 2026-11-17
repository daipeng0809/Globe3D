using System.Diagnostics;
using System.Net;

namespace Globe3D.Avalonia;

/// <summary>
/// 组合瓦片源：按注册顺序依次尝试，返回第一个命中的瓦片。
/// 适用于「本地 MBTiles 优先，缺失时回退到在线服务」的场景。
/// </summary>
public sealed class CompositeTileSource : ITileSource, IDisposable
{
    private readonly ITileSource[] _sources;

    public CompositeTileSource(params ITileSource[] sources)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        if (_sources.Length == 0)
            throw new ArgumentException("至少需要一个瓦片源", nameof(sources));

        var minZoom = _sources.Min(s => s.Info.MinZoom);
        var maxZoom = _sources.Max(s => s.Info.MaxZoom);
        Bounds = MergeBounds(_sources);
        Info = new TileSourceInfo
        {
            Name = string.Join(" + ", _sources.Select(s => s.Info.Name)),
            MinZoom = minZoom,
            MaxZoom = maxZoom,
            Bounds = Bounds,
            Format = _sources.Select(s => s.Info.Format).FirstOrDefault(f => !string.IsNullOrEmpty(f))
        };
    }

    public IReadOnlyList<ITileSource> Sources => _sources;

    /// <summary>合并后的覆盖范围。</summary>
    public GeoBounds? Bounds { get; }

    public TileSourceInfo Info { get; }

    public async Task<byte[]?> GetTileAsync(TileId id, CancellationToken cancellationToken = default)
    {
        if (!id.IsValid)
            return null;

        foreach (var source in _sources)
        {
            if (id.Z < source.Info.MinZoom || id.Z > source.Info.MaxZoom)
                continue;

            try
            {
                var data = await source.GetTileAsync(id, cancellationToken).ConfigureAwait(false);
                if (data is { Length: > 0 })
                    return data;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 单个源失败不影响后续回退
            }
        }

        return null;
    }

    private static GeoBounds? MergeBounds(ITileSource[] sources)
    {
        double west = double.MaxValue, south = double.MaxValue, east = double.MinValue, north = double.MinValue;
        var any = false;
        foreach (var source in sources)
        {
            if (source.Info.Bounds is not { } b)
                return null;
            west = Math.Min(west, b.West);
            south = Math.Min(south, b.South);
            east = Math.Max(east, b.East);
            north = Math.Max(north, b.North);
            any = true;
        }

        return any ? new GeoBounds(west, south, east, north) : null;
    }

    public void Dispose()
    {
        foreach (var source in _sources)
        {
            if (source is IDisposable disposable)
                disposable.Dispose();
        }
    }
}

/// <summary>
/// 在线 XYZ 瓦片源，支持磁盘缓存。URL 模板中可使用 {z}、{x}、{y} 与 {-y}（TMS 行号）。
/// </summary>
public sealed class HttpTileSource : ITileSource, IDisposable
{
    private readonly string _urlTemplate;
    private readonly HttpClient _http;
    private readonly string? _cacheDirectory;
    private readonly bool _ownsClient;

    public HttpTileSource(string urlTemplate, string? cacheDirectory = null, HttpMessageHandler? handler = null,
        int minZoom = 0, int maxZoom = 19, string? userAgent = null, TimeSpan? timeout = null,
        string? name = null)
    {
        if (string.IsNullOrWhiteSpace(urlTemplate))
            throw new ArgumentException("urlTemplate 不能为空", nameof(urlTemplate));

        _urlTemplate = urlTemplate;
        _cacheDirectory = string.IsNullOrWhiteSpace(cacheDirectory) ? null : cacheDirectory;
        _ownsClient = handler == null;

        if (handler == null)
        {
            // 跨平台默认设置：解压、连接数、超时都由这里统一控制，避免个别平台长时间卡住
            var httpHandler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                MaxConnectionsPerServer = 8
            };
            _http = new HttpClient(httpHandler);
        }
        else
        {
            _http = new HttpClient(handler, disposeHandler: false);
        }

        _http.Timeout = timeout ?? TimeSpan.FromSeconds(20);
        if (!string.IsNullOrWhiteSpace(userAgent))
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

        if (_cacheDirectory != null)
            Directory.CreateDirectory(_cacheDirectory);

        Info = new TileSourceInfo
        {
            Name = name ?? new Uri(urlTemplate.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? urlTemplate
                : "https://localhost/" + urlTemplate).Host,
            MinZoom = minZoom,
            MaxZoom = maxZoom
        };
    }

    public TileSourceInfo Info { get; }

    public async Task<byte[]?> GetTileAsync(TileId id, CancellationToken cancellationToken = default)
    {
        if (!id.IsValid)
            return null;

        var cachePath = GetCachePath(id);
        if (cachePath != null && File.Exists(cachePath))
        {
            var cacheStarted = Stopwatch.GetTimestamp();
            try
            {
                var cached = await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false);
                TileIoMeter.ReportDiskRead(cached.Length, Stopwatch.GetTimestamp() - cacheStarted);
                return cached;
            }
            catch
            {
                // 缓存损坏则重新下载
            }
        }

        var url = BuildUrl(id);
        var downloadStarted = Stopwatch.GetTimestamp();
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        // 部分服务在出错时仍返回 200 + JSON 文本，这里按类型过滤，避免把错误信息当图片缓存下来
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType == null || !mediaType.StartsWith("image", StringComparison.OrdinalIgnoreCase))
            return null;

        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (data.Length == 0)
            return null;

        TileIoMeter.ReportNetworkRead(data.Length, Stopwatch.GetTimestamp() - downloadStarted);

        if (cachePath != null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await File.WriteAllBytesAsync(cachePath, data, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 缓存写入失败不影响使用
            }
        }

        return data;
    }

    private string GetCachePath(TileId id)
    {
        if (_cacheDirectory == null)
            return null!;
        return Path.Combine(_cacheDirectory, id.Z.ToString(), id.X.ToString(), id.Y + ".tile");
    }

    private string BuildUrl(TileId id)
    {
        var tmsY = (1 << id.Z) - 1 - id.Y;
        return _urlTemplate
            .Replace("{z}", id.Z.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{x}", id.X.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{-y}", tmsY.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{y}", id.Y.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }
}

/// <summary>内置在线底图服务商。</summary>
public enum OnlineMapProvider
{
    /// <summary>天地图（国家地理信息公共服务平台），瓦片为 JPEG/PNG。</summary>
    Tianditu,

    /// <summary>星图地球开放平台，瓦片支持 WebP / JPEG。</summary>
    GeovisEarth
}

/// <summary>
/// 内置在线瓦片源的快捷工厂。地图 key/token 由调用方传入，控件本身不内置任何凭证。
/// </summary>
/// <remarks>
/// 跨平台说明：天地图返回 JPEG/PNG，所有平台的解码器都能直接使用；星图地球默认请求
/// <c>format=jpeg</c>，避免个别平台缺少 WebP 解码支持时出现空白瓦片。
/// </remarks>
public static class OnlineTileSources
{
    /// <summary>天地图卫星影像（img_w，Web 墨卡托）。</summary>
    public static ITileSource CreateTiandituSatellite(string token, string? cacheDirectory = null, int maxZoom = 18)
        => CreateTianditu(token, "img", "天地图卫星影像", cacheDirectory, maxZoom);

    /// <summary>天地图矢量底图（vec_w，Web 墨卡托）。</summary>
    public static ITileSource CreateTiandituVector(string token, string? cacheDirectory = null, int maxZoom = 18)
        => CreateTianditu(token, "vec", "天地图矢量底图", cacheDirectory, maxZoom);

    private static ITileSource CreateTianditu(string token, string layer, string name,
        string? cacheDirectory, int maxZoom)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("天地图 tk 不能为空", nameof(token));

        // 官方建议按 t0..t7 分域名并发，这里固定使用一个子域，避免每家服务都去轮询
        var subdomain = "t" + Random.Shared.Next(8);
        var url = $"https://{subdomain}.tianditu.gov.cn/{layer}_w/wmts" +
                  $"?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0" +
                  $"&LAYER={layer}&STYLE=default&TILEMATRIXSET=w&FORMAT=tiles" +
                  "&TILEMATRIX={z}&TILEROW={y}&TILECOL={x}&tk=" + token;

        return new HttpTileSource(url, cacheDirectory, maxZoom: maxZoom, name: name,
            userAgent: "Globe3D.Avalonia/1.0");
    }

    /// <summary>
    /// 星图地球卫星影像（pj/base/v1/2025）。默认使用 JPEG，兼容不提供 WebP 解码的平台。
    /// </summary>
    public static ITileSource CreateGeovisEarth(string token, string? cacheDirectory = null,
        int maxZoom = 18, bool preferWebP = false)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("星图地球 token 不能为空", nameof(token));

        var format = preferWebP ? "webp" : "jpeg";
        var url = $"https://api.open.geovisearth.com/pj/base/v1/2025/{{z}}/{{x}}/{{y}}" +
                  $"?format={format}&tmsIds=w&token={token}";

        return new HttpTileSource(url, cacheDirectory, maxZoom: maxZoom, name: "星图地球卫星影像",
            userAgent: "Globe3D.Avalonia/1.0");
    }

    /// <summary>按服务商创建默认的卫星影像瓦片源。</summary>
    public static ITileSource Create(OnlineMapProvider provider, string token,
        string? cacheDirectory = null, int maxZoom = 18) => provider switch
    {
        OnlineMapProvider.Tianditu => CreateTiandituSatellite(token, cacheDirectory, maxZoom),
        _ => CreateGeovisEarth(token, cacheDirectory, maxZoom)
    };
}
