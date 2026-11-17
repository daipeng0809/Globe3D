using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Globe3D.Avalonia;

/// <summary>解码后的瓦片像素数据（BGRA8888，预乘 alpha）。</summary>
public sealed class DecodedTile
{
    public required TileId Id { get; init; }

    public required byte[] Pixels { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }
}

/// <summary>
/// 异步瓦片加载器：按优先级排队、并发抓取、解码为标准 BGRA 缓冲。
/// 解码在后台线程完成，宿主只需在渲染线程消费 <see cref="TileReady"/> 事件。
/// </summary>
/// <remarks>
/// 宿主每帧调用 <see cref="BeginFrame"/> 并重新提交本帧需要的瓦片。<see cref="Request(TileId)"/> 会记录
/// 每个瓦片最近一次被需要的帧号，连续若干帧都没有人再需要它时（相机已移开或层级已改变），
/// 队列里残留的请求会被直接丢弃，既不占用带宽也不会阻塞新瓦片；
/// 失败的瓦片只在冷却期内生效，冷却结束或被再次需要后会自动重试。
/// </remarks>
public sealed class TileLoader : IDisposable
{
    private readonly ITileSource _source;
    private readonly PriorityQueue<TileId, double> _queue = new();
    private readonly HashSet<TileId> _queued = new();
    private readonly ConcurrentDictionary<TileId, byte> _inFlight = new();
    private readonly ConcurrentDictionary<TileId, long> _failed = new();
    private readonly ConcurrentDictionary<TileId, long> _wantedAt = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Task[] _workers;
    private int _targetZoom = 4;
    private int _frame;
    private int _framesSincePurge;
    private volatile bool _disposed;

    public TileLoader(ITileSource source, int maxConcurrency = 4)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        MaxConcurrency = Math.Max(1, maxConcurrency);
        _workers = new Task[MaxConcurrency];
        for (var i = 0; i < _workers.Length; i++)
            _workers[i] = Task.Run(WorkerLoopAsync);
    }

    /// <summary>瓦片解码完成（在后台线程触发）。</summary>
    public event Action<DecodedTile>? TileReady;

    /// <summary>并发下载/解码线程数量。</summary>
    public int MaxConcurrency { get; }

    /// <summary>队列中等待的最大瓦片数，超出后新请求被丢弃。</summary>
    public int MaxQueued { get; set; } = 1536;

    /// <summary>请求连续多少帧没有被再次需要即视为过期（不再下载）。</summary>
    public int StaleFrameLimit { get; set; } = 3;

    /// <summary>加载失败后的重试冷却时间（秒）。</summary>
    public double FailureRetrySeconds { get; set; } = 8d;

    /// <summary>当前期望的瓦片层级，仅用于未显式指定优先级的请求排序。</summary>
    public int TargetZoom
    {
        get => _targetZoom;
        set => _targetZoom = value;
    }

    /// <summary>已加载失败的瓦片数量（供诊断）。</summary>
    public int FailedCount => _failed.Count;

    /// <summary>排队等待下载的瓦片数量（供诊断）。</summary>
    public int QueuedCount
    {
        get
        {
            lock (_gate)
                return _queued.Count;
        }
    }

    /// <summary>已排队或正在下载/解码的瓦片数量（供诊断）。</summary>
    public int InFlightCount => _inFlight.Count;

    /// <summary>进入新的渲染帧：用于判断排队请求是否已经过期。</summary>
    public void BeginFrame(long frame)
    {
        // 渲染线程写、下载线程读：用 int 避免 32 位平台上的 long 撕裂（帧号回绕后差值仍然正确）
        Volatile.Write(ref _frame, unchecked((int)frame));
        _framesSincePurge++;
        if (_framesSincePurge < 15)
            return;

        // _queued 由下载线程在 _gate 内增删，这里必须在同一把锁里读，
        // 否则 HashSet 正处 Resize 时会读到中间状态
        bool needPurge;
        lock (_gate)
            needPurge = _queued.Count > MaxQueued / 3;

        if (!needPurge)
            return;

        _framesSincePurge = 0;
        PurgeStale();
    }

    /// <summary>请求加载一个瓦片。重复请求会被忽略。</summary>
    public void Request(TileId id) => Request(id, -id.Z);

    /// <summary>请求加载一个瓦片，并指定排队优先级（数值越小越先加载）。</summary>
    public void Request(TileId id, double priority)
    {
        if (_disposed || !id.IsValid)
            return;

        var info = _source.Info;
        if (id.Z < info.MinZoom || id.Z > info.MaxZoom)
            return;
        if (!IsCovered(id, info))
            return;

        if (_failed.TryGetValue(id, out var failedAt))
        {
            if (Environment.TickCount64 - failedAt < FailureRetrySeconds * 1000d)
                return;
            _failed.TryRemove(id, out _);
        }

        // 即使瓦片已在队列/下载中，也刷新“最近需要”的帧号，避免被误判为过期
        _wantedAt[id] = Volatile.Read(ref _frame);

        if (_inFlight.ContainsKey(id))
            return;

        lock (_gate)
        {
            if (id.Z < info.MinZoom || id.Z > info.MaxZoom)
                return;
            if (_queued.Contains(id))
                return;

            if (_queued.Count >= MaxQueued)
            {
                PurgeStale();
                if (_queued.Count >= MaxQueued)
                    return;
            }

            _queued.Add(id);
            _inFlight[id] = 0;
            if (double.IsNaN(priority))
                priority = Math.Abs(id.Z - _targetZoom) * 1_000d + id.Y * 1_024d + id.X;
            _queue.Enqueue(id, priority);
        }

        _signal.Release();
    }

    /// <summary>判断瓦片是否正在加载或已排队。</summary>
    public bool IsLoading(TileId id) => _inFlight.ContainsKey(id);

    /// <summary>判断瓦片是否已被确认失败（冷却期内）。</summary>
    public bool HasFailed(TileId id)
    {
        if (!_failed.TryGetValue(id, out var failedAt))
            return false;
        return Environment.TickCount64 - failedAt < FailureRetrySeconds * 1000d;
    }

    /// <summary>清除失败记录，允许重新尝试（例如网络恢复后）。</summary>
    public void ClearFailures()
    {
        _failed.Clear();
        _wantedAt.Clear();
    }

    /// <summary>清空待处理队列（相机大范围跳转时调用，避免加载过期瓦片）。</summary>
    public void ClearQueue()
    {
        lock (_gate)
        {
            while (_queue.TryDequeue(out var id, out _))
            {
                _queued.Remove(id);
                _inFlight.TryRemove(id, out _);
            }

            _wantedAt.Clear();
        }
    }

    private bool IsCovered(TileId id, TileSourceInfo info)
    {
        if (info.Bounds is not { } bounds)
            return true;

        var tile = id.GetGeoBounds();
        return tile.East >= bounds.West && tile.West <= bounds.East
                                       && tile.North >= bounds.South && tile.South <= bounds.North;
    }

    private bool IsStale(TileId id) =>
        unchecked(Volatile.Read(ref _frame) - (int)_wantedAt.GetValueOrDefault(id, Volatile.Read(ref _frame))) > StaleFrameLimit;

    /// <summary>丢弃长时间没有被需要的排队请求，为新视角的瓦片腾出队列。</summary>
    private void PurgeStale()
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
                return;

            var stale = new List<TileId>();
            foreach (var id in _queued)
            {
                if (IsStale(id))
                    stale.Add(id);
            }

            if (stale.Count == 0)
                return;

            foreach (var id in stale)
            {
                _queued.Remove(id);
                _inFlight.TryRemove(id, out _);
                _wantedAt.TryRemove(id, out _);
            }

            var kept = new List<(TileId Id, double Priority)>(_queue.Count);
            while (_queue.TryDequeue(out var id, out var priority))
            {
                if (_queued.Contains(id))
                    kept.Add((id, priority));
            }

            foreach (var (id, priority) in kept)
                _queue.Enqueue(id, priority);
        }
    }

    private async Task WorkerLoopAsync()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            TileId id;
            lock (_gate)
            {
                if (!_queue.TryDequeue(out id, out _))
                    continue;
                _queued.Remove(id);
            }

            // 相机已经移开的瓦片直接丢弃，不再占用网络
            if (IsStale(id))
            {
                _inFlight.TryRemove(id, out _);
                continue;
            }

            try
            {
                var data = await _source.GetTileAsync(id, token).ConfigureAwait(false);
                if (data == null || data.Length == 0)
                {
                    _failed[id] = Environment.TickCount64;
                    continue;
                }

                var decoded = Decode(id, data);
                if (decoded == null)
                {
                    _failed[id] = Environment.TickCount64;
                    continue;
                }

                _failed.TryRemove(id, out _);
                TileReady?.Invoke(decoded);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                _failed[id] = Environment.TickCount64;
            }
            finally
            {
                _inFlight.TryRemove(id, out _);
                _wantedAt.TryRemove(id, out _);
            }
        }
    }

    private static DecodedTile? Decode(TileId id, byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var bitmap = new Bitmap(stream);
            var size = bitmap.PixelSize;
            if (size.Width <= 0 || size.Height <= 0)
                return null;

            var stride = size.Width * 4;
            var pixels = new byte[stride * size.Height];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height),
                    handle.AddrOfPinnedObject(), pixels.Length, stride);
            }
            finally
            {
                handle.Free();
            }

            // OpenGL ES 3.0 核心不支持 GL_BGRA 上传，统一转为 RGBA 通道顺序
            var format = bitmap.Format;
            if (format is null || format == PixelFormat.Bgra8888)
            {
                for (var i = 0; i + 3 < pixels.Length; i += 4)
                    (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            }

            return new DecodedTile
            {
                Id = id,
                Pixels = pixels,
                Width = size.Width,
                Height = size.Height
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 停止后台下载线程。不阻塞调用方（可能在渲染线程调用），收尾清理放到后台完成。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            _cts.Cancel();
        }
        catch
        {
            // 已释放
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // 忽略关闭异常
            }
            finally
            {
                try { _signal.Dispose(); } catch { }
                try { _cts.Dispose(); } catch { }
            }
        });
    }
}
