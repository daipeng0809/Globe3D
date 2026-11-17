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
        var systems = _sources.Select(s => s.Info.CoordinateSystem).Distinct().ToArray();
        Info = new TileSourceInfo
        {
            Name = string.Join(" + ", _sources.Select(s => s.Info.Name)),
            MinZoom = minZoom,
            MaxZoom = maxZoom,
            Bounds = Bounds,
            Format = _sources.Select(s => s.Info.Format).FirstOrDefault(f => !string.IsNullOrEmpty(f)),
            CoordinateSystem = systems.Length == 1 ? systems[0] : CoordinateSystem.Unknown
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
/// <remarks>
/// 不提供任何服务商专用工厂：服务地址、key 轮换等由调用方自行拼装 URL。
/// 未显式指定 <c>coordinateSystem</c> 时，会根据 URL / 名称推断常见服务商（高德/腾讯 → GCJ-02，
/// 百度 → BD-09，其余按 WGS84），并通过 <see cref="TileSourceInfo.CoordinateSystem"/> 暴露。
/// </remarks>
public sealed class HttpTileSource : ITileSource, IDisposable
{
    private readonly string _urlTemplate;
    private readonly HttpClient _http;
    private readonly string? _cacheDirectory;
    private readonly bool _ownsClient;

    public HttpTileSource(string urlTemplate, string? cacheDirectory = null, HttpMessageHandler? handler = null,
        int minZoom = 0, int maxZoom = 19, string? userAgent = null, TimeSpan? timeout = null,
        string? name = null, CoordinateSystem? coordinateSystem = null)
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
            MaxZoom = maxZoom,
            CoordinateSystem = coordinateSystem ?? CoordinateSystemDetector.Detect(urlTemplate, name)
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

/// <summary>
/// 通用在线瓦片工厂。只接收 URL 模板，不绑定任何具体地图服务商；
/// 服务地址、key 管理、多 key 轮换均由调用方自行实现。
/// </summary>
/// <remarks>与直接 <c>new HttpTileSource(...)</c> 等价，保留此入口用于统一的源创建位置。</remarks>
public static class OnlineTileSources
{
    /// <summary>创建一个通用 HTTP XYZ 瓦片源。</summary>
    public static HttpTileSource Create(string urlTemplate, string? cacheDirectory = null,
        HttpMessageHandler? handler = null, int minZoom = 0, int maxZoom = 19,
        string? userAgent = null, TimeSpan? timeout = null, string? name = null,
        CoordinateSystem? coordinateSystem = null) =>
        new(urlTemplate, cacheDirectory, handler, minZoom, maxZoom, userAgent, timeout, name, coordinateSystem);
}
