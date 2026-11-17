using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Globe3D.Avalonia;

/// <summary>
/// 基于 MBTiles（SQLite）的离线影像瓦片源。MBTiles 规范使用 TMS 行序（自南向北），
/// 内部会自动与 XYZ（自北向南）互转。
/// </summary>
public sealed class MbTilesTileSource : ITileSource, IDisposable
{
    private readonly string _connectionString;
    private readonly bool _flipY;
    private TileSourceInfo _info;

    /// <summary>
    /// 创建 MBTiles 瓦片源。
    /// </summary>
    /// <param name="filePath">MBTiles 文件路径。</param>
    /// <param name="flipY">是否按 MBTiles 规范的 TMS 行序解释 tile_row，默认 true。</param>
    public MbTilesTileSource(string filePath, bool flipY = true)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("filePath 不能为空", nameof(filePath));
        if (!File.Exists(filePath))
            throw new FileNotFoundException("找不到 MBTiles 文件", filePath);

        FilePath = Path.GetFullPath(filePath);
        _flipY = flipY;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        _info = ReadInfo();
    }

    /// <summary>MBTiles 文件完整路径。</summary>
    public string FilePath { get; }

    public TileSourceInfo Info => _info;

    /// <summary>重新读取元数据（例如文件被替换后）。</summary>
    public void RefreshInfo() => _info = ReadInfo();

    public async Task<byte[]?> GetTileAsync(TileId id, CancellationToken cancellationToken = default)
    {
        if (id.Z < _info.MinZoom || id.Z > _info.MaxZoom || !id.IsValid)
            return null;

        var row = _flipY ? (1 << id.Z) - 1 - id.Y : id.Y;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT tile_data FROM tiles WHERE zoom_level = $z AND tile_column = $x AND tile_row = $y LIMIT 1";
        command.Parameters.AddWithValue("$z", id.Z);
        command.Parameters.AddWithValue("$x", id.X);
        command.Parameters.AddWithValue("$y", row);

        var started = Stopwatch.GetTimestamp();
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var data = result as byte[];
        TileIoMeter.ReportDiskRead(data?.Length ?? 0, Stopwatch.GetTimestamp() - started);
        return data;
    }

    private TileSourceInfo ReadInfo()
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name, value FROM metadata";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                    continue;
                var key = reader.GetString(0);
                var value = reader.IsDBNull(1) ? string.Empty : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty;
                metadata[key] = value;
            }
        }
        catch (SqliteException)
        {
            // 没有 metadata 表时容错处理
        }

        var minZoom = ParseInt(metadata, "minzoom");
        var maxZoom = ParseInt(metadata, "maxzoom");

        if (minZoom == null || maxZoom == null)
        {
            // 注意：MIN()/MAX() 会触发全表扫描（大文件可达数十秒），
            // 这里改用 ORDER BY + LIMIT 1，可直接走 tiles 索引。
            try
            {
                minZoom ??= QueryExtremeZoom(connection, ascending: true);
                maxZoom ??= QueryExtremeZoom(connection, ascending: false);
            }
            catch (SqliteException)
            {
                // 忽略，使用默认值
            }
        }

        GeoBounds? bounds = null;
        if (metadata.TryGetValue("bounds", out var boundsText) && !string.IsNullOrWhiteSpace(boundsText))
        {
            var parts = boundsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 4 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var west) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var south) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var east) &&
                double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var north))
            {
                bounds = new GeoBounds(west, south, east, north);
            }
        }

        return new TileSourceInfo
        {
            Name = metadata.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : Path.GetFileNameWithoutExtension(FilePath),
            MinZoom = minZoom ?? 0,
            MaxZoom = maxZoom ?? 18,
            Bounds = bounds,
            Format = metadata.TryGetValue("format", out var format) ? format : null
        };
    }

    private static int? QueryExtremeZoom(SqliteConnection connection, bool ascending)
    {
        using var command = connection.CreateCommand();
        command.CommandText = ascending
            ? "SELECT zoom_level FROM tiles ORDER BY zoom_level ASC LIMIT 1"
            : "SELECT zoom_level FROM tiles ORDER BY zoom_level DESC LIMIT 1";
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static int? ParseInt(Dictionary<string, string> metadata, string key)
    {
        if (metadata.TryGetValue(key, out var text) &&
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return null;
    }

    public void Dispose()
    {
        // 连接由连接池管理，这里主动清空以释放文件句柄
        SqliteConnection.ClearAllPools();
    }
}
