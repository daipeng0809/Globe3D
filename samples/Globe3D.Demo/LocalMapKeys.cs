using System.Text.Json;

namespace Globe3D.Demo;

/// <summary>
/// 本地地图密钥配置。示例工程不包含任何密钥，用户需自行创建 <c>keys.local.json</c>
/// （已加入 .gitignore，不会提交到仓库），或通过环境变量 <c>GLOBE3D_KEYS_FILE</c> 指定配置文件路径。
/// </summary>
/// <remarks>
/// 配置文件格式（键名固定，值可以是字符串数组或单个字符串）：
/// <code>
/// {
///   "tianditu": [ "你的天地图 tk" ],
///   "geovis": [ "你的星图地球 token" ]
/// }
/// </code>
/// 未配置的项会跳过对应的在线底图；本地 MBTiles 与示例图层仍然可用。
/// </remarks>
internal sealed class LocalMapKeys
{
    /// <summary>本地配置文件文件名。</summary>
    public const string FileName = "keys.local.json";

    private const string FilePathVariable = "GLOBE3D_KEYS_FILE";

    private LocalMapKeys(IReadOnlyList<string> tianditu, IReadOnlyList<string> geovis,
        string? loadedFrom, string? error)
    {
        Tianditu = tianditu;
        Geovis = geovis;
        LoadedFrom = loadedFrom;
        Error = error;
    }

    /// <summary>天地图 tk 列表，按启动随机轮换。</summary>
    public IReadOnlyList<string> Tianditu { get; }

    /// <summary>星图地球 token 列表，按启动随机轮换。</summary>
    public IReadOnlyList<string> Geovis { get; }

    /// <summary>实际加载的配置文件路径；未找到时为 null。</summary>
    public string? LoadedFrom { get; }

    /// <summary>读取或解析失败的原因；成功或未找到文件时为 null。</summary>
    public string? Error { get; }

    public static LocalMapKeys Load()
    {
        var path = FindConfigPath();
        if (path == null)
            return new LocalMapKeys(Array.Empty<string>(), Array.Empty<string>(), null, null);

        try
        {
            // AOT 友好：JsonDocument 不依赖反射序列化
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return new LocalMapKeys(ReadList(root, "tianditu"), ReadList(root, "geovis"), path, null);
        }
        catch (Exception ex)
        {
            return new LocalMapKeys(Array.Empty<string>(), Array.Empty<string>(), path, ex.Message);
        }
    }

    private static string? FindConfigPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable(FilePathVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return Path.GetFullPath(explicitPath);

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, FileName),
            Path.Combine(Environment.CurrentDirectory, FileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Globe3D", FileName)
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static IReadOnlyList<string> ReadList(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element))
            return Array.Empty<string>();

        if (element.ValueKind == JsonValueKind.String)
            return Normalize(new[] { element.GetString() });

        if (element.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                values.Add(item.GetString()!);
        }

        return Normalize(values);
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string?> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!.Trim())
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}
