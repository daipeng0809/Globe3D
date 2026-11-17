namespace Globe3D.Avalonia;

/// <summary>瓦片 IO 累计统计快照。字节数为逻辑读取量（MBTiles / 磁盘缓存 / HTTP 响应体），并非物理磁盘吞吐。</summary>
internal readonly record struct TileIoSnapshot(
    long DiskBytes,
    long DiskReads,
    long DiskTicks,
    long NetworkBytes,
    long NetworkReads,
    long NetworkTicks);

/// <summary>
/// 瓦片读取统计器：累计磁盘（MBTiles、磁盘缓存）与网络下载的字节数和耗时，
/// 供性能面板按采样区间换算实时读取速度与单瓦片平均耗时。
/// </summary>
/// <remarks>
/// 计数全部用 <see cref="Interlocked"/> 累加：写入发生在下载线程，读取发生在 UI 线程。
/// </remarks>
internal static class TileIoMeter
{
    private static long _diskBytes;
    private static long _diskReads;
    private static long _diskTicks;
    private static long _networkBytes;
    private static long _networkReads;
    private static long _networkTicks;

    /// <summary>记录一次成功的磁盘读取（MBTiles 查询或磁盘缓存命中）。</summary>
    public static void ReportDiskRead(long bytes, long elapsedTicks)
    {
        if (bytes <= 0)
            return;

        Interlocked.Add(ref _diskBytes, bytes);
        Interlocked.Add(ref _diskTicks, Math.Max(0, elapsedTicks));
        Interlocked.Increment(ref _diskReads);
    }

    /// <summary>记录一次成功的网络下载（含请求往返与响应体读取）。</summary>
    public static void ReportNetworkRead(long bytes, long elapsedTicks)
    {
        if (bytes <= 0)
            return;

        Interlocked.Add(ref _networkBytes, bytes);
        Interlocked.Add(ref _networkTicks, Math.Max(0, elapsedTicks));
        Interlocked.Increment(ref _networkReads);
    }

    public static TileIoSnapshot GetSnapshot() => new(
        Interlocked.Read(ref _diskBytes),
        Interlocked.Read(ref _diskReads),
        Interlocked.Read(ref _diskTicks),
        Interlocked.Read(ref _networkBytes),
        Interlocked.Read(ref _networkReads),
        Interlocked.Read(ref _networkTicks));
}
