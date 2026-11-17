using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Threading;
using Silk.NET.OpenGLES;

namespace Globe3D.Avalonia;

internal sealed partial class GlobeSurface
{
    /// <summary>在线瓦片边长（像素），用于推算所需层级。</summary>
    private const int AssumedTileSize = 256;

    /// <summary>可选择的最高层级（与 TileId 的整数移位上限保持一致）。</summary>
    private const int MaxSelectableZoom = 19;

    /// <summary>瓦片所需层级比当前高出该值时继续四分，避免过度切分。</summary>
    private const double SplitThreshold = 0.55d;

    /// <summary>单帧最多访问的四叉树节点数，异常视角下保护 CPU（每节点约 18 次投影）。</summary>
    private const int MaxVisitedNodesPerFrame = 6_000;

    /// <summary>粗层级瓦片的总数上限，防止数据源 minZoom 很大时根节点遍历爆炸。</summary>
    private const int MaxRootTilesPerAxis = 256;

    /// <summary>每帧为可见性判断准备的屏幕锚点数（中心、四边中点、四角）。</summary>
    private const int MaxScreenAnchors = 9;

    /// <summary>单个瓦片的采样点上限：9 个固定点 + 屏幕锚点。</summary>
    private const int MaxTileSamples = 9 + MaxScreenAnchors;

    private readonly Dictionary<TileId, TileTexture> _textures = new();
    private readonly ConcurrentQueue<DecodedTile> _pendingUploads = new();
    private List<TileDrawItem> DrawList { get; } = new();
    private readonly HashSet<TileId> _frameRequests = new();

    /// <summary>本帧四叉树切分得到的叶子瓦片，值为重要度（所需层级 - 当前层级）。</summary>
    private readonly Dictionary<TileId, double> _leaves = new();

    /// <summary>本帧各层级绘制的瓦片数量。渲染线程写入、UI 线程读取，用整型快照避免锁。</summary>
    private readonly int[] _levelCounts = new int[MaxSelectableZoom + 4];

    /// <summary>纹理缓存数量的快照，避免 UI 线程直接读渲染线程正在改写的 Dictionary。</summary>
    private int _textureCountSnapshot;

    /// <summary>本帧屏幕关键位置对应的地表经纬度，用于“瓦片上离屏幕最近的点”采样。</summary>
    private readonly double[] _anchorLon = new double[MaxScreenAnchors];
    private readonly double[] _anchorLat = new double[MaxScreenAnchors];
    private int _anchorCount;

    /// <summary>本帧绘制瓦片的快照，渲染线程整体替换引用，UI 线程读到完整对象。</summary>
    private IReadOnlyList<TileId> _drawnSnapshot = Array.Empty<TileId>();

    private ITileSource? _tileSource;
    private ITileSource? _pendingSource;
    private volatile bool _sourceChangePending;
    private volatile bool _resetCachesRequested;
    private TileLoader? _loader;
    private bool _clearTextureCacheRequested;
    private long _frameCounter;
    private int _lastTargetZoom = -1;
    private int _minDrawnZoom = -1;
    private int _maxDrawnZoom = -1;

    // 每帧缓存的视图参数，供数十次层级估算复用
    private double _mppFactor;
    private int _visitedNodes;
    private bool _visitLimitHit;

    /// <summary>纹理缓存中当前持有的瓦片数量（上一帧快照）。</summary>
    public int CachedTextureCount => Volatile.Read(ref _textureCountSnapshot);

    /// <summary>本帧实际绘制瓦片使用的层级范围；没有绘制任何瓦片时为 (-1, -1)。</summary>
    public (int Min, int Max) DrawnZoomRange => (_minDrawnZoom, _maxDrawnZoom);

    /// <summary>上一帧四叉树选瓦片（含层级估算）的耗时，毫秒。</summary>
    public double LastSelectionMilliseconds { get; private set; }

    /// <summary>本帧各层级实际绘制的瓦片数量（层级 -> 数量），用于观察混合 LOD 分布。</summary>
    public IReadOnlyList<(int Level, int Count)> GetDrawnLevelHistogram()
    {
        var result = new List<(int Level, int Count)>();
        for (var level = 0; level < _levelCounts.Length; level++)
        {
            var count = _levelCounts[level];
            if (count > 0)
                result.Add((level, count));
        }

        return result;
    }

    /// <summary>本帧实际绘制的瓦片标识（诊断用，返回不可变快照）。</summary>
    public IReadOnlyList<TileId> GetDrawnTiles() => _drawnSnapshot;

    /// <summary>
    /// 设置或替换瓦片数据源。实际切换在渲染线程完成，
    /// 避免 UI 线程改写渲染线程正在遍历的 loader/四叉树状态。
    /// </summary>
    public void SetTileSource(ITileSource? source)
    {
        if (ReferenceEquals(_tileSource, source) && !_sourceChangePending)
            return;

        _pendingSource = source;
        _sourceChangePending = true;
        Invalidate();
    }

    /// <summary>当前使用的瓦片源（可能已排队等待渲染线程应用）。</summary>
    public ITileSource? TileSource => _sourceChangePending ? _pendingSource : _tileSource;

    /// <summary>当前统计信息（Zoom 为混合层级中的最细层级）。</summary>
    public (int Zoom, int Tiles, int Textures, int Failed) GetDiagnostics() =>
        (_lastTargetZoom, DrawnTileCount, Volatile.Read(ref _textureCountSnapshot), _loader?.FailedCount ?? 0);

    /// <summary>瓦片加载器队列统计：排队、加载中与失败数量。</summary>
    public (int Queued, int Loading, int Failed) GetLoaderStats()
    {
        var loader = _loader;
        if (loader == null)
            return (0, 0, 0);

        var queued = loader.QueuedCount;
        return (queued, Math.Max(0, loader.InFlightCount - queued), loader.FailedCount);
    }

    /// <summary>清除所有缓存纹理与失败的加载记录（在渲染线程生效）。</summary>
    public void ResetCaches()
    {
        _resetCachesRequested = true;
        Invalidate();
    }

    /// <summary>渲染线程：应用挂起的瓦片源切换。</summary>
    private void ApplyPendingTileSource()
    {
        if (!_sourceChangePending)
            return;

        _sourceChangePending = false;
        var source = _pendingSource;
        _pendingSource = null;

        DetachLoader();
        _tileSource = source;

        if (source != null)
        {
            _loader = new TileLoader(source) { TargetZoom = _lastTargetZoom < 0 ? 4 : _lastTargetZoom };
            _loader.TileReady += OnTileReady;
        }

        _clearTextureCacheRequested = true;
        _frameRequests.Clear();
        _leaves.Clear();
        _anchorCount = 0;
    }

    /// <summary>渲染线程：应用挂起的缓存清理。</summary>
    private void ApplyPendingCacheReset()
    {
        if (!_resetCachesRequested)
            return;

        _resetCachesRequested = false;
        _clearTextureCacheRequested = true;
        _frameRequests.Clear();
        _leaves.Clear();
        _anchorCount = 0;
        _loader?.ClearFailures();
        _loader?.ClearQueue();
    }

    private void DetachLoader()
    {
        if (_loader == null)
            return;
        _loader.TileReady -= OnTileReady;
        _loader.Dispose();
        _loader = null;
    }

    private void OnTileReady(DecodedTile tile)
    {
        _pendingUploads.Enqueue(tile);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                RequestNextFrameRendering();
            }
            catch
            {
                // 控件已卸载
            }
        }, DispatcherPriority.Background);
    }

    private void ReleaseAllTextures(GL g)
    {
        foreach (var entry in _textures.Values)
        {
            if (entry.Handle != 0)
                DeleteTextureHandle(g, entry.Handle);
        }

        _textures.Clear();
        Volatile.Write(ref _textureCountSnapshot, 0);

        while (_pendingUploads.TryDequeue(out _))
        {
            // 丢弃待上传队列
        }
    }

    private unsafe void ProcessPendingUploads(GL g)
    {
        if (_clearTextureCacheRequested)
        {
            ReleaseAllTextures(g);
            _clearTextureCacheRequested = false;
        }

        var uploaded = 0;
        while (uploaded < 32 && _pendingUploads.TryDequeue(out var tile))
        {
            if (_textures.ContainsKey(tile.Id))
                continue;

            var handle = g.GenTextures(1);
            if (handle == 0)
                continue;

            g.BindTexture(TextureTarget.Texture2D, handle);

            // 非 2 的幂尺寸不能使用 mipmap 过滤，否则纹理不完整会被采样为黑色
            var isPowerOfTwo = (tile.Width & (tile.Width - 1)) == 0 && (tile.Height & (tile.Height - 1)) == 0;
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                (int)(isPowerOfTwo ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear));
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);

            fixed (byte* pixels = tile.Pixels)
            {
                g.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                    (uint)tile.Width, (uint)tile.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            }

            if (isPowerOfTwo)
                g.GenerateMipmap(TextureTarget.Texture2D);

            _textures[tile.Id] = new TileTexture { Handle = handle, LastFrame = _frameCounter };
            uploaded++;
        }

        g.BindTexture(TextureTarget.Texture2D, 0);
        TrimTextures(g);
        Volatile.Write(ref _textureCountSnapshot, _textures.Count);
    }

    private void TrimTextures(GL g)
    {
        var capacity = Math.Max(32, MaxTextureCount);
        if (_textures.Count <= capacity)
            return;

        var ordered = new List<KeyValuePair<TileId, TileTexture>>(_textures);
        ordered.Sort(static (a, b) => a.Value.LastFrame.CompareTo(b.Value.LastFrame));

        var removeCount = _textures.Count - capacity;
        for (var i = 0; i < removeCount && i < ordered.Count; i++)
        {
            var entry = ordered[i];
            if (entry.Value.Handle != 0)
                DeleteTextureHandle(g, entry.Value.Handle);
            _textures.Remove(entry.Key);
        }
    }

    /// <summary>
    /// 选择当前视角下需要绘制的瓦片：从数据源最低层级开始做四叉树遍历，
    /// 每个瓦片按自身位置估算所需层级，近景与地平线附近继续四分，远处提前收敛，
    /// 因此同一帧内可以同时出现多个层级。
    /// </summary>
    private void SelectTiles()
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            SelectTilesCore();
        }
        finally
        {
            LastSelectionMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }

    private void SelectTilesCore()
    {
        DrawList.Clear();
        DrawnTileCount = 0;
        _minDrawnZoom = -1;
        _maxDrawnZoom = -1;
        _leaves.Clear();
        Array.Clear(_levelCounts);

        var source = _tileSource;
        if (source == null || _lastRenderWidth <= 1 || _lastRenderHeight <= 1)
            return;

        var info = source.Info;
        var minZoom = Math.Clamp(info.MinZoom, 0, MaxSelectableZoom);
        var maxZoom = Math.Clamp(info.MaxZoom, minZoom, MaxSelectableZoom);
        if (maxZoom < minZoom)
            return;

        _frameRequests.Clear();
        _loader?.BeginFrame(_frameCounter);

        _mppFactor = 2d * Math.Tan(Camera.FieldOfView * MathUtil.DegToRad * 0.5d) /
                     Math.Max(1f, _pixelHeight);
        BuildScreenAnchors();

        if (!TryGetRootTileRange(minZoom, out var x0, out var x1, out var y0, out var y1))
        {
            _lastTargetZoom = minZoom;
            return;
        }

        _visitedNodes = 0;
        _visitLimitHit = false;
        var n = 1 << minZoom;

        for (var y = y0; y <= y1; y++)
        {
            for (var ix = x0; ix <= x1; ix++)
            {
                var x = ((ix % n) + n) % n;
                TraverseTile(new TileId(minZoom, x, y), info, maxZoom);
            }
        }

        // 单个瓦片最少也要有纹理缓存空间：超出预算时把同层四兄弟合并成父瓦片，保证不留空洞
        PruneLeaves(Math.Max(16, MaxTilesPerFrame));

        foreach (var pair in _leaves)
            AddTileDrawItem(pair.Key, pair.Value);

        // 诊断快照：整体替换引用，UI 线程不会读到半个列表
        var snapshot = new TileId[DrawList.Count];
        for (var i = 0; i < DrawList.Count; i++)
            snapshot[i] = DrawList[i].Id;
        _drawnSnapshot = snapshot;

        _lastTargetZoom = _maxDrawnZoom < 0 ? minZoom : _maxDrawnZoom;
        if (_loader != null)
            _loader.TargetZoom = _lastTargetZoom;
    }

    /// <summary>
    /// 记录屏幕关键位置（中心、四边中点、四角）对应的地表点。
    /// 遍历瓦片时把锚点夹取到瓦片范围内，就能拿到“瓦片上离屏幕最近的点”：
    /// 高倍放大时屏幕往往只覆盖粗瓦片的一条窄边，仅靠瓦片自身的固定采样点会把它误判为不可见，
    /// 整棵子树被剪掉后就会出现竖条状的空洞。
    /// </summary>
    private void BuildScreenAnchors()
    {
        _anchorCount = 0;
        var w = _lastRenderWidth;
        var h = _lastRenderHeight;

        Span<(double X, double Y)> positions = stackalloc (double, double)[MaxScreenAnchors]
        {
            (w * 0.5, h * 0.5),
            (w * 0.5, 0), (w * 0.5, h), (0, h * 0.5), (w, h * 0.5),
            (0, 0), (w, 0), (0, h), (w, h)
        };

        foreach (var (x, y) in positions)
        {
            if (!Camera.ScreenToGround(x, y, w, h, out var point))
                continue;

            _anchorLon[_anchorCount] = point.Longitude;
            _anchorLat[_anchorCount] = point.Latitude;
            _anchorCount++;
        }
    }

    /// <summary>四叉树遍历：可见的瓦片按需继续细分，不可分的作为叶子输出。</summary>
    private void TraverseTile(TileId id, TileSourceInfo info, int maxZoom)
    {
        if (_visitLimitHit)
            return;

        if (_visitedNodes++ >= MaxVisitedNodesPerFrame || _leaves.Count >= MaxTilesPerFrame * 4)
        {
            _visitLimitHit = true;
            return;
        }

        var bounds = id.GetGeoBounds();
        Span<double> lons = stackalloc double[MaxTileSamples];
        Span<double> lats = stackalloc double[MaxTileSamples];
        Span<Vec3> points = stackalloc Vec3[MaxTileSamples];
        Span<double> screenX = stackalloc double[MaxTileSamples];
        Span<double> screenY = stackalloc double[MaxTileSamples];

        var sampleCount = BuildTileSamples(bounds, lons, lats, points);

        // 投影只算一次：既用于可见性判断，也用于屏幕空间误差（层级估算）。
        // 注意只能传入实际填写的采样点，否则会读到栈上残留的无效点。
        // 平面模式额外传入经纬度：瓦片在 ±180° 边缘用三维向量往返会被浮点误差翻到另一侧，
        // 必须直接用经纬度投影，否则边缘瓦片 bbox 横跨全图、层级被误判成 z10。
        if (!IsTileVisible(points[..sampleCount], lons[..sampleCount], lats[..sampleCount],
                screenX, screenY, out var screenSizeUsable,
                out var minX, out var minY, out var maxX, out var maxY))
            return;

        if (info.Bounds is { } dataBounds && !IntersectsBounds(id, dataBounds))
        {
            // 位于数据覆盖范围之外的瓦片按占位色绘制，不触发下载
            _leaves[id] = 0d;
            return;
        }

        var desired = ComputeDesiredZoom(id, points, sampleCount, screenSizeUsable, minX, minY, maxX, maxY);
        if (id.Z < maxZoom && desired > id.Z + SplitThreshold)
        {
            var childZ = id.Z + 1;
            var childX = id.X << 1;
            var childY = id.Y << 1;
            var before = _leaves.Count;

            TraverseTile(new TileId(childZ, childX, childY), info, maxZoom);
            TraverseTile(new TileId(childZ, childX + 1, childY), info, maxZoom);
            TraverseTile(new TileId(childZ, childX, childY + 1), info, maxZoom);
            TraverseTile(new TileId(childZ, childX + 1, childY + 1), info, maxZoom);

            if (_leaves.Count > before || _visitLimitHit)
                return;
        }

        _leaves[id] = desired - id.Z;
    }

    /// <summary>构建一个瓦片的采样点：9 个固定点 + 屏幕锚点（夹取到瓦片边界）。</summary>
    private int BuildTileSamples(in GeoBounds bounds, Span<double> lons, Span<double> lats, Span<Vec3> points)
    {
        GetTileSamples(bounds, lons, lats);
        var count = 9;

        for (var i = 0; i < _anchorCount && count < MaxTileSamples; i++)
        {
            lons[count] = Math.Clamp(_anchorLon[i], bounds.West, bounds.East);
            lats[count] = Math.Clamp(_anchorLat[i], bounds.South, bounds.North);
            count++;
        }

        for (var i = 0; i < count; i++)
            points[i] = GeoMath.LonLatToUnitVector(lons[i], lats[i]);

        return count;
    }

    /// <summary>
    /// 逐采样点做投影与遮挡判断，同时输出屏幕包围盒（含被遮挡点，供层级估算）。
    /// <paramref name="screenSizeUsable"/> 为 false 表示投影不可靠（接近切线、w 接近 0 的点会把坐标拉到无穷），
    /// 此时层级估算只用距离项，避免地平线附近的瓦片被误判成“铺满屏幕”而一路细分。
    /// </summary>
    private bool IsTileVisible(ReadOnlySpan<Vec3> points, ReadOnlySpan<double> lons,
        ReadOnlySpan<double> lats, Span<double> screenX, Span<double> screenY, out bool screenSizeUsable,
        out double minX, out double minY, out double maxX, out double maxY)
    {
        screenSizeUsable = false;
        minX = minY = double.MaxValue;
        maxX = maxY = double.MinValue;

        var w = _lastRenderWidth;
        var h = _lastRenderHeight;
        var anyVisible = false;
        var anyProjected = false;
        var maxNormalized = 0d;
        const double margin = 96d;

        for (var i = 0; i < points.Length; i++)
        {
            screenX[i] = double.NaN;
            screenY[i] = double.NaN;

            var point = points[i];
            var occluded = !Camera.IsFlat && GeoMath.IsOccludedBySphere(Camera.Position, point);

            double sx;
            double sy;
            float depth;
            var projected = Camera.IsFlat
                ? Camera.ProjectGeo(new GeoPoint(lats[i], lons[i]), w, h, out sx, out sy, out depth)
                : Camera.Project(point, w, h, out sx, out sy, out depth);
            if (!projected)
                continue;

            screenX[i] = sx;
            screenY[i] = sy;

            // 归一化坐标超出几个屏幕就认为该点处于切线附近，投影值不可信
            maxNormalized = Math.Max(maxNormalized, Math.Max(Math.Abs(sx) / w, Math.Abs(sy) / h));

            var bx = MathUtil.Clamp(sx, -8d * w, 8d * w);
            var by = MathUtil.Clamp(sy, -8d * h, 8d * h);
            if (anyProjected)
            {
                if (bx < minX) minX = bx;
                if (bx > maxX) maxX = bx;
                if (by < minY) minY = by;
                if (by > maxY) maxY = by;
            }
            else
            {
                minX = maxX = bx;
                minY = maxY = by;
                anyProjected = true;
            }

            if (occluded || depth is < -1f or > 1f)
                continue;

            if (sx >= -margin && sx <= w + margin && sy >= -margin && sy <= h + margin)
                anyVisible = true;
        }

        screenSizeUsable = anyProjected && maxNormalized <= 4d;
        return anyVisible;
    }

    /// <summary>
    /// 叶子数量超出单帧预算时，把最深一层的同父兄弟合并成父瓦片。
    /// 这里不要求四个兄弟齐全：只要有两个以上同父叶子就合并，
    /// 否则视野只扫到父瓦片一角的场景（常见于放大后贴着屏幕边缘的条带）会一个都合并不掉，
    /// 叶子数停在上限之上，绘制端只能按数量截断，屏幕上就会出现少了一块瓦片的空洞。
    /// 父瓦片覆盖四个子瓦片的总范围，合并不会在球面上留下空洞。
    /// </summary>
    private void PruneLeaves(int budget)
    {
        // 每一轮至少减少一片，理论上以叶子数为上界；再加一个层数上界防止极端情况死循环
        for (var pass = 0; pass < MaxSelectableZoom + 2 && _leaves.Count > budget; pass++)
        {
            var maxLevel = -1;
            foreach (var id in _leaves.Keys)
            {
                if (id.Z > maxLevel)
                    maxLevel = id.Z;
            }

            if (maxLevel <= 0)
                return;

            var groups = new Dictionary<TileId, int>();
            foreach (var id in _leaves.Keys)
            {
                if (id.Z != maxLevel)
                    continue;
                var parent = id.Parent;
                groups.TryGetValue(parent, out var count);
                groups[parent] = count + 1;
            }

            var merged = 0;
            foreach (var pair in groups)
            {
                if (pair.Value < 2)
                    continue;

                var removed = 0;
                for (var dy = 0; dy < 2; dy++)
                {
                    for (var dx = 0; dx < 2; dx++)
                    {
                        if (_leaves.Remove(new TileId(maxLevel, pair.Key.X * 2 + dx, pair.Key.Y * 2 + dy)))
                            removed++;
                    }
                }

                if (removed < 2)
                    continue;

                if (!_leaves.ContainsKey(pair.Key))
                    _leaves[pair.Key] = 0.5d;
                merged++;
                if (_leaves.Count <= budget)
                    break;
            }

            // 最深一层的叶子都是孤立兄弟：再往上一层已经没有可合并的组，停止
            if (merged == 0)
                return;
        }
    }

    /// <summary>计算瓦片需要的层级：按瓦片自身到相机的距离与掠射角估算地表分辨率。</summary>
    private double ComputeDesiredZoom(TileId id, ReadOnlySpan<Vec3> points, int sampleCount,
        bool screenSizeUsable, double minX, double minY, double maxX, double maxY)
    {
        // 距离/掠射估算：取整块瓦片上最需要细节的采样点
        var desired = double.MinValue;
        for (var i = 0; i < sampleCount; i++)
            desired = Math.Max(desired, ZoomAtPoint(points[i]));

        // 距离估算在地平线附近会偏低：那里地表被极细地压缩到几个像素上，
        // 再叠加屏幕空间误差（瓦片投影尺寸超过一个纹理边长就继续细分）。
        return Math.Max(desired, screenSizeUsable ? ZoomFromScreenSize(id, minX, minY, maxX, maxY) : double.MinValue);
    }

    /// <summary>瓦片上的 9 个采样点：中心、四角与四边中点。</summary>
    private static void GetTileSamples(in GeoBounds bounds, Span<double> lons, Span<double> lats)
    {
        var centerLon = (bounds.West + bounds.East) * 0.5d;
        var centerLat = (bounds.South + bounds.North) * 0.5d;

        lons[0] = centerLon;
        lats[0] = centerLat;
        lons[1] = bounds.West;
        lats[1] = bounds.North;
        lons[2] = bounds.East;
        lats[2] = bounds.North;
        lons[3] = bounds.West;
        lats[3] = bounds.South;
        lons[4] = bounds.East;
        lats[4] = bounds.South;
        lons[5] = centerLon;
        lats[5] = bounds.North;
        lons[6] = centerLon;
        lats[6] = bounds.South;
        lons[7] = bounds.West;
        lats[7] = centerLat;
        lons[8] = bounds.East;
        lats[8] = centerLat;
    }

    private double ZoomAtPoint(Vec3 normal)
    {
        // 平面模式下墨卡托瓦片的屏幕尺寸与纬度无关：全景像素数 / 256 就是所需层级
        if (Camera.IsFlat)
        {
            var worldPixels = Camera.FlatScaleFactor * Math.Max(1f, _pixelHeight);
            return Math.Log2(Math.Max(1d, worldPixels) / AssumedTileSize);
        }

        var toCamera = Camera.Position - normal;
        var distance = Math.Max(1e-4f, toCamera.Length);

        // 视线与地表法线夹角越大（掠射），单个像素覆盖的地表范围越大，需要更细的层级
        var cosView = Vec3.Dot(normal, toCamera / distance);
        var grazing = 1d / Math.Max(0.2f, cosView);

        var metersPerPixel = distance * GeoMath.EarthRadius * _mppFactor * grazing;
        var latitude = MathF.Asin(MathUtil.Clamp(normal.Y, -1f, 1f)) * MathUtil.RadToDeg;
        var circumference = 2d * Math.PI * GeoMath.EarthRadius *
                            Math.Max(0.02d, Math.Cos(latitude * MathUtil.DegToRad));

        return Math.Log2(circumference / (AssumedTileSize * metersPerPixel));
    }

    /// <summary>
    /// 按瓦片在屏幕上的投影尺寸推算所需层级。
    /// 只统计“落在视口内”的部分：仅一条边扫过屏幕的远处瓦片，其完整投影可能横跨十几个屏幕（靠近相机平面），
    /// 用整个包围盒会把它一路细分到最高层级，造成单帧几百片瓦片的爆炸。
    /// </summary>
    private double ZoomFromScreenSize(TileId id, double minX, double minY, double maxX, double maxY)
    {
        var left = Math.Max(minX, 0d);
        var top = Math.Max(minY, 0d);
        var right = Math.Min(maxX, _lastRenderWidth);
        var bottom = Math.Min(maxY, _lastRenderHeight);
        if (right <= left || bottom <= top)
            return double.MinValue;

        var size = Math.Max(right - left, bottom - top);
        if (size < 1d || double.IsNaN(size) || double.IsInfinity(size))
            return double.MinValue;

        return id.Z + Math.Log2(size / AssumedTileSize);
    }

    /// <summary>以数据源最低层级为四叉树根，估算需要遍历的根瓦片范围。</summary>
    private bool TryGetRootTileRange(int zoom, out int x0, out int x1, out int y0, out int y1)
    {
        var n = 1 << zoom;
        x0 = y0 = 0;
        x1 = y1 = n - 1;

        if (!TryGetViewportGeoBounds(out var west, out var east, out var south, out var north))
            return true;

        var fx0 = GeoMath.LongitudeToTileX(west, zoom);
        var fx1 = GeoMath.LongitudeToTileX(east, zoom);
        if (fx1 < fx0)
            fx1 += n;

        x0 = (int)Math.Floor(fx0);
        x1 = (int)Math.Floor(fx1);
        if (x1 < x0)
            x1 = x0;
        // 跨 ±180° 时列号可能超出 2^zoom，限制到一圈以内避免重复遍历同一瓦片
        x1 = Math.Min(x1, x0 + n - 1);

        y0 = Math.Max(0, (int)Math.Floor(GeoMath.LatitudeToTileY(north, zoom)));
        y1 = Math.Min(n - 1, (int)Math.Floor(GeoMath.LatitudeToTileY(south, zoom)));
        if (y1 < y0)
            y1 = y0;

        if (x1 - x0 + 1 > MaxRootTilesPerAxis)
            x1 = x0 + MaxRootTilesPerAxis - 1;
        if (y1 - y0 + 1 > MaxRootTilesPerAxis)
            y1 = y0 + MaxRootTilesPerAxis - 1;

        return true;
    }

    private static bool IntersectsBounds(TileId id, GeoBounds bounds)
    {
        var tile = id.GetGeoBounds();
        return tile.East >= bounds.West && tile.West <= bounds.East
                                        && tile.North >= bounds.South && tile.South <= bounds.North;
    }

    private void AddTileDrawItem(TileId id, double importance)
    {
        id.GetMercatorBounds(out var left, out var top, out var right, out var bottom);

        // 墨卡托只能表示到 ±85.05°，最北/最南一行的外边缘在顶点着色器里吸附到极点，
        // 否则正对极点时会露出一块没有几何的圆盘状空洞
        var lastRow = (1 << id.Z) - 1;

        var item = new TileDrawItem
        {
            Id = id,
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom,
            PinNorthPole = id.Y == 0 ? 1f : 0f,
            PinSouthPole = id.Y == lastRow ? 1f : 0f
        };

        if (TryResolveTexture(id, out var texture, out var resolvedId,
                out var scaleX, out var scaleY, out var offsetX, out var offsetY))
        {
            item.Texture = texture;
            item.UvScaleX = scaleX;
            item.UvScaleY = scaleY;
            item.UvOffsetX = offsetX;
            item.UvOffsetY = offsetY;

            if (_textures.TryGetValue(resolvedId, out var entry))
                entry.LastFrame = _frameCounter;
        }

        // 即使祖先纹理已经兜底可用，只要自身纹理还没到位就继续请求，
        // 否则放大时会永远停在低分辨率的祖先瓦片上（表现为“放大不刷新”）。
        if (!_textures.ContainsKey(id))
            RequestTileChain(id, importance);

        DrawList.Add(item);

        if (id.Z >= 0 && id.Z < _levelCounts.Length)
            _levelCounts[id.Z]++;

        if (_minDrawnZoom < 0 || id.Z < _minDrawnZoom)
            _minDrawnZoom = id.Z;
        if (id.Z > _maxDrawnZoom)
            _maxDrawnZoom = id.Z;
    }

    /// <summary>沿祖先链查找可用纹理；命中时返回子瓦片相对祖先的 UV 变换。</summary>
    private bool TryResolveTexture(TileId id, out uint texture, out TileId resolvedId,
        out float scaleX, out float scaleY, out float offsetX, out float offsetY)
    {
        var levels = 0;
        var current = id;

        while (current.Z >= 0)
        {
            if (_textures.TryGetValue(current, out var entry))
            {
                texture = entry.Handle;
                resolvedId = current;
                var factor = 1 << levels;
                scaleX = 1f / factor;
                scaleY = 1f / factor;
                offsetX = (id.X - current.X * factor) / (float)factor;
                offsetY = (id.Y - current.Y * factor) / (float)factor;
                return true;
            }

            if (current.Z == 0)
                break;

            current = current.Parent;
            levels++;
        }

        texture = 0;
        resolvedId = default;
        scaleX = scaleY = 1f;
        offsetX = offsetY = 0f;
        return false;
    }

    private void RequestTileChain(TileId id, double importance)
    {
        var loader = _loader;
        if (loader == null)
            return;

        // 重要度越高（所需层级与当前层级差距越大）越先加载
        RequestOnce(id, -importance);

        // 同时请求祖先，保证先有低分辨率兜底
        var ancestor = id;
        for (var level = 0; level < 2 && ancestor.Z > 0; level++)
        {
            ancestor = ancestor.Parent;
            if (_textures.ContainsKey(ancestor))
                break;
            RequestOnce(ancestor, -importance - 10d - level * 10d);
        }
    }

    private void RequestOnce(TileId id, double priority)
    {
        if (!_frameRequests.Add(id))
            return;
        _loader?.Request(id, priority);
    }

    /// <summary>估算当前视口覆盖的经纬度范围（以中心经度为基准处理跨 180 度）。</summary>
    private bool TryGetViewportGeoBounds(out double west, out double east, out double south, out double north)
    {
        west = -180;
        east = 180;
        south = -85;
        north = 85;

        var w = _lastRenderWidth;
        var h = _lastRenderHeight;
        if (w <= 1 || h <= 1)
            return false;

        if (Camera.IsFlat)
            return Camera.TryGetFlatViewportBounds(out west, out east, out south, out north);

        var centerLon = Camera.CenterLongitude;
        double westDelta = double.MaxValue;
        double eastDelta = double.MinValue;
        var southLat = double.MaxValue;
        var northLat = double.MinValue;
        var any = false;

        Span<(double X, double Y)> samples = stackalloc (double, double)[]
        {
            (w * 0.5, h * 0.5),
            (0, 0), (w, 0), (0, h), (w, h),
            (w * 0.5, 0), (w * 0.5, h), (0, h * 0.5), (w, h * 0.5),
            (w * 0.25, h * 0.5), (w * 0.75, h * 0.5)
        };

        foreach (var (sx, sy) in samples)
        {
            if (!Camera.ScreenToGround(sx, sy, w, h, out var point))
                continue;

            var delta = MathUtil.NormalizeLongitudeDelta(point.Longitude - centerLon);
            westDelta = Math.Min(westDelta, delta);
            eastDelta = Math.Max(eastDelta, delta);
            southLat = Math.Min(southLat, point.Latitude);
            northLat = Math.Max(northLat, point.Latitude);
            any = true;
        }

        if (!any)
            return false;

        // 覆盖范围过小时给出下限，避免只覆盖单点
        if (eastDelta - westDelta < 1d)
        {
            westDelta -= 0.5d;
            eastDelta += 0.5d;
        }

        if (northLat - southLat < 1d)
        {
            northLat += 0.5d;
            southLat -= 0.5d;
        }

        var marginLon = Math.Max(1.0d, (eastDelta - westDelta) * 0.08d);
        var marginLat = Math.Max(1.0d, (northLat - southLat) * 0.08d);

        west = centerLon + westDelta - marginLon;
        east = centerLon + eastDelta + marginLon;
        south = Math.Max(-85.06d, southLat - marginLat);
        north = Math.Min(85.06d, northLat + marginLat);
        return true;
    }

    private sealed class TileTexture
    {
        public uint Handle;

        public long LastFrame;
    }

    private struct TileDrawItem
    {
        public TileId Id;

        public float Left;

        public float Top;

        public float Right;

        public float Bottom;

        public uint Texture;

        public float UvScaleX;

        public float UvScaleY;

        public float UvOffsetX;

        public float UvOffsetY;

        /// <summary>1 表示该瓦片的顶边属于最北一行，顶点需吸附到北极。</summary>
        public float PinNorthPole;

        /// <summary>1 表示该瓦片的底边属于最南一行，顶点需吸附到南极。</summary>
        public float PinSouthPole;
    }
}
