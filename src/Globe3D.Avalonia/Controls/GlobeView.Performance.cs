using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;

namespace Globe3D.Avalonia;

public partial class GlobeView
{
    /// <summary>性能面板的采样间隔。</summary>
    private static readonly TimeSpan PerformanceSampleInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>流畅度评分的目标帧率：地图交互以 60 FPS 为满分线。</summary>
    private const double SmoothnessTargetFps = 60d;

    private readonly GlobePerformancePanel _performancePanel = new();
    private DispatcherTimer? _performanceTimer;

    private long _performanceSampleTimestamp;
    private int _smoothnessScore = -1;
    private long _performanceSampleFrames;
    private long _performanceSampleDiskBytes;
    private long _performanceSampleDiskReads;
    private long _performanceSampleDiskTicks;
    private long _performanceSampleNetworkBytes;
    private long _performanceSampleNetworkReads;
    private long _performanceSampleNetworkTicks;

    #region 性能面板

    /// <summary>是否显示性能面板。</summary>
    public static readonly StyledProperty<bool> ShowPerformancePanelProperty =
        AvaloniaProperty.Register<GlobeView, bool>(nameof(ShowPerformancePanel));

    /// <summary>
    /// 是否在控件左上角显示性能面板：帧率、刷新率、帧耗时、瓦片与加载队列数量，
    /// 以及 MBTiles / 磁盘缓存 / 网络的实时读取速度。
    /// </summary>
    public bool ShowPerformancePanel
    {
        get => GetValue(ShowPerformancePanelProperty);
        set => SetValue(ShowPerformancePanelProperty, value);
    }

    private void ApplyPerformancePanelVisibility()
    {
        if (ShowPerformancePanel)
        {
            _performancePanel.IsVisible = true;
            StartPerformanceTimer();
            return;
        }

        _performancePanel.IsVisible = false;
        StopPerformanceTimer();
        _performancePanel.Clear();
    }

    private void StartPerformanceTimer()
    {
        if (_performanceTimer != null)
            return;

        // 重新开始采样：停止期间的时间不计入速率，避免重新打开时速度被拉低
        _performanceSampleTimestamp = 0;
        _performanceTimer = new DispatcherTimer(PerformanceSampleInterval,
            DispatcherPriority.Background, OnPerformanceTick);
        _performanceTimer.Start();

        // 立即显示当前计数，不用等第一个采样周期
        OnPerformanceTick(null, EventArgs.Empty);
    }

    private void StopPerformanceTimer()
    {
        if (_performanceTimer == null)
            return;

        _performanceTimer.Stop();
        _performanceTimer = null;
        _performanceSampleTimestamp = 0;
    }

    private void OnPerformanceTick(object? sender, EventArgs e) =>
        _performancePanel.SetSnapshot(GetPerformanceSnapshot());

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // 离开可视树后不再采样，避免定时器一直挂在 UI 线程上
        StopPerformanceTimer();
        base.OnDetachedFromVisualTree(e);
    }

    #endregion

    #region 性能快照

    /// <summary>
    /// 获取当前性能快照：帧率、刷新率、帧耗时、瓦片/纹理/加载队列数量，
    /// 以及磁盘与网络瓦片读取速度。适合打日志或自绘性能面板。
    /// </summary>
    /// <remarks>
    /// 速率按本次调用与上一次调用之间的时间差计算，首次调用时速率字段为 0；
    /// 性能面板显示期间也会调用本方法，两者共用同一个采样窗口。
    /// </remarks>
    public GlobePerformanceSnapshot GetPerformanceSnapshot()
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = _performanceSampleTimestamp == 0
            ? 0d
            : Stopwatch.GetElapsedTime(_performanceSampleTimestamp, now).TotalSeconds;

        var render = _surface.GetRenderStats();
        var loader = _surface.GetLoaderStats();
        var io = TileIoMeter.GetSnapshot();
        var (_, drawnTiles, textures, failed) = _surface.GetDiagnostics();
        var (minZoom, maxZoom) = _surface.DrawnZoomRange;

        var frameDelta = render.Frames - _performanceSampleFrames;
        var framesPerSecond = seconds > 0d ? Math.Max(0d, frameDelta / seconds) : 0d;

        // 评分只在采样区间内确实渲染了足够帧数时更新；空闲时保留上一次评分
        if (seconds > 0d && frameDelta >= 3)
            _smoothnessScore = ComputeSmoothnessScore(render.AverageMilliseconds,
                render.MaxMilliseconds, framesPerSecond);

        var diskBytes = io.DiskBytes - _performanceSampleDiskBytes;
        var diskReads = io.DiskReads - _performanceSampleDiskReads;
        var diskTicks = io.DiskTicks - _performanceSampleDiskTicks;
        var networkBytes = io.NetworkBytes - _performanceSampleNetworkBytes;
        var networkReads = io.NetworkReads - _performanceSampleNetworkReads;
        var networkTicks = io.NetworkTicks - _performanceSampleNetworkTicks;

        var snapshot = new GlobePerformanceSnapshot
        {
            FramesPerSecond = framesPerSecond,
            SmoothnessScore = _smoothnessScore,
            RenderedFrames = render.Frames,
            FrameMilliseconds = render.AverageMilliseconds,
            MaxFrameMilliseconds = render.MaxMilliseconds,
            RefreshRateHz = render.PeakFps,
            DrawnTiles = drawnTiles,
            CachedTextures = textures,
            FailedTiles = failed,
            QueuedTiles = loader.Queued,
            LoadingTiles = loader.Loading,
            MinZoom = minZoom,
            MaxZoom = maxZoom,
            SelectionMilliseconds = _surface.LastSelectionMilliseconds,
            DiskReadBytesPerSecond = seconds > 0d ? Math.Max(0d, diskBytes / seconds) : 0d,
            DiskTilesPerSecond = seconds > 0d ? Math.Max(0d, diskReads / seconds) : 0d,
            DiskReadMilliseconds = diskReads > 0
                ? diskTicks * 1000d / Stopwatch.Frequency / diskReads
                : 0d,
            DiskReadBytes = io.DiskBytes,
            NetworkBytesPerSecond = seconds > 0d ? Math.Max(0d, networkBytes / seconds) : 0d,
            NetworkTilesPerSecond = seconds > 0d ? Math.Max(0d, networkReads / seconds) : 0d,
            NetworkReadMilliseconds = networkReads > 0
                ? networkTicks * 1000d / Stopwatch.Frequency / networkReads
                : 0d,
            NetworkBytes = io.NetworkBytes,
            AltitudeMeters = _camera.AltitudeMeters,
            SampleSeconds = seconds
        };

        _performanceSampleTimestamp = now;
        _performanceSampleFrames = render.Frames;
        _performanceSampleDiskBytes = io.DiskBytes;
        _performanceSampleDiskReads = io.DiskReads;
        _performanceSampleDiskTicks = io.DiskTicks;
        _performanceSampleNetworkBytes = io.NetworkBytes;
        _performanceSampleNetworkReads = io.NetworkReads;
        _performanceSampleNetworkTicks = io.NetworkTicks;
        return snapshot;
    }

    /// <summary>
    /// 计算 0-100 的流畅度评分：帧耗时（45%）、帧耗时抖动（20%）与达成帧率（35%，以 60 FPS 为满分线）加权。
    /// 目标帧率固定为 60：不能用“由帧间隔估算的刷新率”做分母，否则帧率低但节奏均匀时会被误判为满分。
    /// </summary>
    private static int ComputeSmoothnessScore(double averageMilliseconds, double maxMilliseconds,
        double framesPerSecond)
    {
        var budget = 1000d / SmoothnessTargetFps;

        var average = Math.Max(0.05d, averageMilliseconds);
        var slowest = Math.Max(average, maxMilliseconds);

        var frameScore = Math.Clamp(budget / average, 0d, 1d);
        var stabilityScore = Math.Clamp(1d - (slowest - average) / (4d * budget), 0d, 1d);
        var cadenceScore = Math.Clamp(framesPerSecond / SmoothnessTargetFps, 0d, 1d);

        var score = 0.45d * frameScore + 0.20d * stabilityScore + 0.35d * cadenceScore;
        return (int)Math.Round(Math.Clamp(score, 0d, 1d) * 100d);
    }

    #endregion
}
