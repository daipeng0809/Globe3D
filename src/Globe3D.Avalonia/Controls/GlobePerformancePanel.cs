using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Globe3D.Avalonia;

/// <summary>
/// 位于控件左上角的轻量性能面板：用 Avalonia 矢量绘制帧率、帧耗时、瓦片与 IO 统计，
/// 不参与命中测试，开关关闭时不绘制也不产生开销。
/// </summary>
internal sealed class GlobePerformancePanel : Control
{
    private const double PanelMargin = 12d;
    private const double PanelWidth = 248d;
    private const double PanelPadding = 10d;
    private const double RowHeight = 15d;
    private const double TitleHeight = 16d;
    private const double TitleGap = 4d;
    private const double LabelFontSize = 10.5d;
    private const double ValueFontSize = 10.5d;

    private static readonly Typeface TitleTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface LabelTypeface = new(FontFamily.Default);
    private static readonly Typeface ValueTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private static readonly IBrush PanelBackground = new SolidColorBrush(Color.Parse("#D90B1220"));
    private static readonly IPen PanelBorder = new Pen(new SolidColorBrush(Color.Parse("#4D5AC8FF")), 1d);
    private static readonly IBrush TitleBrush = new SolidColorBrush(Color.Parse("#5AC8FF"));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#8FB4D8"));
    private static readonly IBrush ValueBrush = new SolidColorBrush(Color.Parse("#EAF3FF"));

    // 流畅度评分分档配色：≥90 优秀、≥70 一般、其余需要关注
    private static readonly IBrush ScoreGoodBrush = new SolidColorBrush(Color.Parse("#7BE3A1"));
    private static readonly IBrush ScoreFairBrush = new SolidColorBrush(Color.Parse("#FFD479"));
    private static readonly IBrush ScorePoorBrush = new SolidColorBrush(Color.Parse("#FF8B7B"));

    /// <summary>面板行数据；整体替换引用，渲染线程不会读到写了一半的列表。</summary>
    private IReadOnlyList<PanelRow> _rows = Array.Empty<PanelRow>();

    private string _sampleText = string.Empty;

    public GlobePerformancePanel()
    {
        IsHitTestVisible = false;
        IsVisible = false;
    }

    /// <summary>用最新快照刷新面板内容（UI 线程调用）。</summary>
    public void SetSnapshot(in GlobePerformanceSnapshot snapshot)
    {
        _rows = BuildRows(snapshot);
        _sampleText = snapshot.SampleSeconds > 0d
            ? FormattableString.Invariant($"{snapshot.SampleSeconds:0.00} s")
            : string.Empty;
        InvalidateVisual();
    }

    /// <summary>清空面板内容（关闭面板时调用）。</summary>
    public void Clear()
    {
        _rows = Array.Empty<PanelRow>();
        _sampleText = string.Empty;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var rows = _rows;
        if (rows.Count == 0)
            return;

        var height = PanelPadding * 2d + TitleHeight + TitleGap + rows.Count * RowHeight;
        var panel = new Rect(PanelMargin, PanelMargin, PanelWidth, height);
        context.DrawRectangle(PanelBackground, PanelBorder, panel, 6d, 6d);

        var title = new FormattedText("PERFORMANCE", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            TitleTypeface, 11d, TitleBrush);
        context.DrawText(title, new Point(PanelMargin + PanelPadding, PanelMargin + PanelPadding));

        if (_sampleText.Length > 0)
        {
            var sample = new FormattedText(_sampleText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                LabelTypeface, 10d, LabelBrush);
            context.DrawText(sample, new Point(PanelMargin + PanelWidth - PanelPadding - sample.Width,
                PanelMargin + PanelPadding + 1d));
        }

        var y = PanelMargin + PanelPadding + TitleHeight + TitleGap;
        foreach (var row in rows)
        {
            var label = new FormattedText(row.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                LabelTypeface, LabelFontSize, LabelBrush);
            var value = new FormattedText(row.Value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                ValueTypeface, ValueFontSize, row.Accent ?? ValueBrush);

            context.DrawText(label, new Point(PanelMargin + PanelPadding, y));
            context.DrawText(value, new Point(PanelMargin + PanelWidth - PanelPadding - value.Width, y));
            y += RowHeight;
        }
    }

    private static PanelRow[] BuildRows(in GlobePerformanceSnapshot s)
    {
        var lod = s.MinZoom < 0
            ? "—"
            : FormattableString.Invariant($"z{s.MinZoom}-z{s.MaxZoom} · {s.SelectionMilliseconds:0.0} ms");

        var refresh = s.RefreshRateHz >= 1d
            ? FormattableString.Invariant($"{s.RefreshRateHz:0} Hz")
            : "—";

        // 空闲时帧统计会被重置（平均耗时为 0），此时不展示上一交互期的帧耗时
        var frame = s.FrameMilliseconds > 0d
            ? FormattableString.Invariant($"{s.FrameMilliseconds:0.0} ms · max {s.MaxFrameMilliseconds:0.0}")
            : "—";

        var score = s.SmoothnessScore < 0 ? "—" : FormattableString.Invariant($"{s.SmoothnessScore} / 100");

        return
        [
            new PanelRow("FPS", FormattableString.Invariant($"{s.FramesPerSecond:0.0}")),
            new PanelRow("Score", score, ScoreBrush(s.SmoothnessScore)),
            new PanelRow("Frames", s.RenderedFrames.ToString("N0", CultureInfo.InvariantCulture)),
            new PanelRow("Frame", frame),
            new PanelRow("Refresh", refresh),
            new PanelRow("Tiles", FormattableString.Invariant($"{s.DrawnTiles} drawn · {s.CachedTextures} cached")),
            new PanelRow("LOD", lod),
            new PanelRow("Loader", FormattableString.Invariant($"q{s.QueuedTiles} · l{s.LoadingTiles} · f{s.FailedTiles}")),
            new PanelRow("Disk", FormatIo(s.DiskReadBytesPerSecond, s.DiskTilesPerSecond, s.DiskReadMilliseconds)),
            new PanelRow("Network", FormatIo(s.NetworkBytesPerSecond, s.NetworkTilesPerSecond, s.NetworkReadMilliseconds)),
            new PanelRow("Altitude", FormattableString.Invariant($"{s.AltitudeMeters / 1000d:N0} km"))
        ];
    }

    private static string FormatIo(double bytesPerSecond, double tilesPerSecond, double millisecondsPerTile)
    {
        var rate = FormatRate(bytesPerSecond);
        var tiles = FormattableString.Invariant($"{tilesPerSecond:0} t/s");
        return tilesPerSecond > 0d
            ? FormattableString.Invariant($"{rate} · {tiles} · {millisecondsPerTile:0.0} ms")
            : FormattableString.Invariant($"{rate} · {tiles}");
    }

    private static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024d)
            return FormattableString.Invariant($"{Math.Max(0d, bytesPerSecond):0} B/s");
        if (bytesPerSecond < 1024d * 1024d)
            return FormattableString.Invariant($"{bytesPerSecond / 1024d:0.0} KB/s");
        return FormattableString.Invariant($"{bytesPerSecond / (1024d * 1024d):0.0} MB/s");
    }

    private static IBrush ScoreBrush(int score) => score switch
    {
        < 0 => ValueBrush,
        >= 90 => ScoreGoodBrush,
        >= 70 => ScoreFairBrush,
        _ => ScorePoorBrush
    };

    private readonly record struct PanelRow(string Label, string Value, IBrush? Accent = null);
}
