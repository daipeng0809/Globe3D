using System.Text;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Globe3D.Avalonia;

/// <summary>
/// 标记图标加载器：把 png / jpg / gif / bmp / webp / svg 等文件变成可直接赋给
/// <see cref="GlobeMarker.Image"/> 的 <see cref="IImage"/>。
/// <para>
/// 位图交给 Avalonia 的 <see cref="Bitmap"/> 解码（GIF 取首帧）；SVG 由内置的
/// <see cref="SvgMarkerImage"/> 以矢量方式绘制，缩放后依然清晰。
/// </para>
/// <para>
/// 按路径加载时走带容量上限的 LRU 缓存：同一图标被多个标记点复用时只读一次盘，
/// 长时间运行也不会无限增长；文件被修改（时间戳或长度变化）后自动重新读取。
/// 缓存是线程安全的，但解码本身不持锁，多线程同时首次加载同一图标时可能各解码一次，
/// 结果等价、不会互相破坏。
/// </para>
/// </summary>
public static class MarkerImage
{
    /// <summary>缓存上限（个图标）。超出后按最久未使用顺序淘汰。</summary>
    public const int MaxCachedImages = 64;

    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Recency = new();
    private static readonly object Sync = new();

    private sealed class CacheEntry
    {
        public required DateTime Stamp { get; init; }
        public required long Length { get; init; }
        public required IImage Image { get; init; }
        public LinkedListNode<string>? Node { get; set; }
    }

    /// <summary>当前缓存中的图标数量（诊断用）。</summary>
    public static int CachedCount
    {
        get
        {
            lock (Sync)
                return Cache.Count;
        }
    }

    /// <summary>按文件路径加载图标（带 LRU 缓存，文件被修改后自动重新读取）。</summary>
    /// <param name="path">图片文件路径，扩展名可为 png/jpg/jpeg/gif/bmp/webp/svg。</param>
    public static IImage Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("图片路径不能为空。", nameof(path));

        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException("找不到图片文件：" + fullPath, fullPath);

        if (TryGetCached(fullPath, info.LastWriteTimeUtc, info.Length, out var cached))
            return cached;

        // 解码放在锁外，避免一个图标的解码阻塞其它线程的缓存查询
        var image = LoadCore(fullPath);
        Store(fullPath, info.LastWriteTimeUtc, info.Length, image);
        return image;
    }

    /// <summary>按文件路径加载图标；失败时返回 null 并给出错误信息，不抛异常。</summary>
    public static IImage? TryLoad(string path, out string? error)
    {
        try
        {
            error = null;
            return Load(path);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>从数据流加载图标，<paramref name="fileName"/> 仅用于判断 svg 扩展名。</summary>
    public static IImage FromStream(Stream stream, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanSeek)
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            return Decode(buffer, fileName);
        }

        return Decode(stream, fileName);
    }

    /// <summary>从 SVG 文本创建图标。</summary>
    public static IImage FromSvg(string svg) => SvgMarkerImage.Parse(svg);

    /// <summary>清空文件缓存（标记点自身仍持有已加载的图标引用）。</summary>
    public static void ClearCache()
    {
        lock (Sync)
        {
            Cache.Clear();
            Recency.Clear();
        }
    }

    private static IImage Decode(Stream stream, string? fileName)
    {
        var position = stream.Position;
        if (IsSvg(fileName) || LooksLikeSvg(stream))
        {
            stream.Position = position;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                leaveOpen: true);
            return SvgMarkerImage.Parse(reader.ReadToEnd());
        }

        stream.Position = position;
        try
        {
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            var name = string.IsNullOrEmpty(fileName) ? "图片流" : Path.GetFileName(fileName);
            throw new InvalidDataException($"无法解码图片“{name}”：{ex.Message}", ex);
        }
    }

    private static IImage LoadCore(string fullPath)
    {
        using var stream = File.OpenRead(fullPath);
        return Decode(stream, fullPath);
    }

    private static bool TryGetCached(string key, DateTime stamp, long length, out IImage image)
    {
        lock (Sync)
        {
            if (!Cache.TryGetValue(key, out var entry) || entry.Stamp != stamp || entry.Length != length)
            {
                image = null!;
                return false;
            }

            // 命中后把节点移到队首，保证淘汰顺序是“最久未使用”
            if (entry.Node != null)
            {
                Recency.Remove(entry.Node);
                entry.Node = Recency.AddFirst(key);
            }

            image = entry.Image;
            return true;
        }
    }

    private static void Store(string key, DateTime stamp, long length, IImage image)
    {
        lock (Sync)
        {
            if (Cache.TryGetValue(key, out var existing) && existing.Node != null)
                Recency.Remove(existing.Node);

            var entry = new CacheEntry { Stamp = stamp, Length = length, Image = image };
            entry.Node = Recency.AddFirst(key);
            Cache[key] = entry;

            while (Cache.Count > MaxCachedImages && Recency.Last is { } last)
            {
                Recency.RemoveLast();
                Cache.Remove(last.Value);
            }
        }
    }

    /// <summary>扩展名为 .svg 时按矢量图标处理。</summary>
    private static bool IsSvg(string? name) =>
        !string.IsNullOrEmpty(name) &&
        Path.GetExtension(name).Equals(".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>内容嗅探：没有扩展名（或扩展名不可靠）时，看开头是不是 SVG/XML 文本。</summary>
    private static bool LooksLikeSvg(Stream stream)
    {
        if (!stream.CanSeek)
            return false;

        var position = stream.Position;
        var buffer = new byte[512];
        var read = stream.Read(buffer, 0, buffer.Length);
        stream.Position = position;
        if (read <= 0)
            return false;

        var prefix = Encoding.UTF8.GetString(buffer, 0, read)
            .TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return prefix.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
               (prefix.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) &&
                prefix.Contains("<svg", StringComparison.OrdinalIgnoreCase));
    }
}
