namespace Globe3D.Avalonia;

/// <summary>
/// 地图控件的实时性能快照，由 <see cref="GlobeView.GetPerformanceSnapshot"/> 返回。
/// </summary>
/// <remarks>
/// 帧率、磁盘/网络速度等速率指标按「两次调用之间的时间差」计算，
/// 因此首次调用（没有上一个采样点）时速率字段为 0；
/// 字节数为逻辑读取量（MBTiles 查询、磁盘缓存、HTTP 响应体），不代表物理磁盘吞吐。
/// </remarks>
public readonly struct GlobePerformanceSnapshot
{
    /// <summary>采样区间内的渲染帧率（帧/秒）。</summary>
    public double FramesPerSecond { get; init; }

    /// <summary>
    /// 综合流畅度评分（0-100）：由帧耗时、帧耗时抖动与「达成帧率/刷新率」加权得出；
    /// 尚未渲染出可评分帧时为 -1。空闲时保留最近一次评分。
    /// </summary>
    public int SmoothnessScore { get; init; }

    /// <summary>控件累计完成的渲染帧数。</summary>
    public long RenderedFrames { get; init; }

    /// <summary>最近窗口内每帧的平均 CPU 渲染耗时（毫秒）；空闲超过 1 秒时为 0。</summary>
    public double FrameMilliseconds { get; init; }

    /// <summary>最近窗口内单帧的最大 CPU 渲染耗时（毫秒）；空闲超过 1 秒时为 0。</summary>
    public double MaxFrameMilliseconds { get; init; }

    /// <summary>由窗口中较快一档（25% 分位）帧间隔估算的刷新率（Hz）；采样不足或空闲超过 1 秒时为 0。</summary>
    public double RefreshRateHz { get; init; }

    /// <summary>本帧实际绘制的瓦片数量。</summary>
    public int DrawnTiles { get; init; }

    /// <summary>纹理缓存中持有的瓦片数量。</summary>
    public int CachedTextures { get; init; }

    /// <summary>加载失败并处于冷却期的瓦片数量。</summary>
    public int FailedTiles { get; init; }

    /// <summary>排队等待下载的瓦片数量。</summary>
    public int QueuedTiles { get; init; }

    /// <summary>正在下载或解码的瓦片数量（不含排队）。</summary>
    public int LoadingTiles { get; init; }

    /// <summary>本帧绘制瓦片使用的最粗层级；没有绘制瓦片时为 -1。</summary>
    public int MinZoom { get; init; }

    /// <summary>本帧绘制瓦片使用的最细层级；没有绘制瓦片时为 -1。</summary>
    public int MaxZoom { get; init; }

    /// <summary>上一帧四叉树选瓦片的耗时（毫秒）。</summary>
    public double SelectionMilliseconds { get; init; }

    /// <summary>采样区间内的磁盘瓦片读取速度（字节/秒）。</summary>
    public double DiskReadBytesPerSecond { get; init; }

    /// <summary>采样区间内的磁盘瓦片读取速率（个/秒）。</summary>
    public double DiskTilesPerSecond { get; init; }

    /// <summary>采样区间内单个磁盘瓦片的平均读取耗时（毫秒）。</summary>
    public double DiskReadMilliseconds { get; init; }

    /// <summary>累计从磁盘读出的瓦片字节数。</summary>
    public long DiskReadBytes { get; init; }

    /// <summary>采样区间内的网络下载速度（字节/秒）。</summary>
    public double NetworkBytesPerSecond { get; init; }

    /// <summary>采样区间内的网络下载速率（个/秒）。</summary>
    public double NetworkTilesPerSecond { get; init; }

    /// <summary>采样区间内单个网络瓦片的平均下载耗时（毫秒，含请求往返）。</summary>
    public double NetworkReadMilliseconds { get; init; }

    /// <summary>累计从网络下载的瓦片字节数。</summary>
    public long NetworkBytes { get; init; }

    /// <summary>相机离地高度（米）。</summary>
    public double AltitudeMeters { get; init; }

    /// <summary>本次采样覆盖的时间区间（秒）；首次调用为 0。</summary>
    public double SampleSeconds { get; init; }
}
