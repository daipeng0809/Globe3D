using Avalonia.Media;

namespace Globe3D.Avalonia;

/// <summary>
/// 矢量图形基类：绘制在地表上的几何图形，支持填充、镂空（<see cref="Fill"/> 为 null）、
/// 自定义边框颜色/宽度/虚线，以及中心文字标签。
/// </summary>
/// <remarks>
/// 可继承本类并重写 <see cref="BuildRing"/> / <see cref="IsClosedRing"/> 实现自定义矢量图形；
/// <see cref="BuildRing"/> 返回按经纬度顺序排列的几何环，大圆插值、明暗面裁剪与投影由控件统一完成。
/// </remarks>
public abstract class GlobeVector
{
    /// <summary>是否可见。</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>图形中心显示的文字，为空则不显示。</summary>
    public string? Label { get; set; }

    /// <summary>填充画刷；null 表示镂空，只绘制边框。</summary>
    public IBrush? Fill { get; set; }

    /// <summary>边框画刷；null 表示不描边。</summary>
    public IBrush? Stroke { get; set; } = Brushes.OrangeRed;

    /// <summary>边框宽度（像素）。</summary>
    public double StrokeThickness { get; set; } = 2d;

    /// <summary>边框虚线样式；null 为实线（可用 <see cref="DashStyle.Dash"/> 等内置样式）。</summary>
    public IDashStyle? DashStyle { get; set; }

    /// <summary>是否显示 <see cref="Label"/>。</summary>
    public bool ShowLabel { get; set; } = true;

    /// <summary>标签字体大小（像素）。</summary>
    public double LabelFontSize { get; set; } = 12d;

    /// <summary>标签前景色。</summary>
    public IBrush LabelForeground { get; set; } = Brushes.White;

    /// <summary>用户自定义数据。</summary>
    public object? Tag { get; set; }

    /// <summary>是否按闭合环绘制（多边形/圆形/方形为 true，线条/折线为 false）。</summary>
    protected internal virtual bool IsClosedRing => true;

    /// <summary>构成本图形的经纬度环（绘制时自动做球面插值与地平线裁剪）。</summary>
    protected internal abstract IReadOnlyList<GeoPoint> BuildRing();
}

/// <summary>
/// 线条 / 折线矢量：按顶点顺序绘制地表路径。将 <see cref="Closed"/> 设为 true 即变成多边形
/// （可直接使用 <see cref="GlobePolygonVector"/>）。
/// </summary>
public class GlobePolylineVector : GlobeVector
{
    /// <summary>顶点集合（首尾不重复）。</summary>
    public IList<GeoPoint> Points { get; } = new List<GeoPoint>();

    /// <summary>是否闭合（线条/折线为 false，多边形为 true）。</summary>
    public bool Closed { get; set; }

    protected internal override bool IsClosedRing => Closed;

    protected internal override IReadOnlyList<GeoPoint> BuildRing() =>
        Points as IReadOnlyList<GeoPoint> ?? Points.ToList();

    /// <summary>追加一个经纬度顶点。</summary>
    public GlobePolylineVector AddPoint(double latitude, double longitude, double altitude = 0d)
    {
        Points.Add(new GeoPoint(latitude, longitude, altitude));
        return this;
    }
}

/// <summary>多边形矢量：闭合折线，可填充或镂空。</summary>
public sealed class GlobePolygonVector : GlobePolylineVector
{
    public GlobePolygonVector()
    {
        Closed = true;
    }
}

/// <summary>圆形矢量：以经纬度圆心 + 半径（米）定义，按球面大圆生成边界。</summary>
public sealed class GlobeCircleVector : GlobeVector
{
    /// <summary>圆心。</summary>
    public GeoPoint Center { get; set; }

    /// <summary>半径（米）。</summary>
    public double RadiusMeters { get; set; } = 1000d;

    /// <summary>边界分段数（12..720），越大越平滑。</summary>
    public int SegmentCount { get; set; } = 96;

    private IReadOnlyList<GeoPoint>? _cachedRing;
    private GeoPoint _cachedCenter;
    private double _cachedRadius = double.NaN;
    private int _cachedSegments = -1;

    protected internal override IReadOnlyList<GeoPoint> BuildRing()
    {
        // 圆/方形环构造包含大量三角函数，相机拖动时每帧重算成本较高；
        // 参数未变时直接复用上一帧的环。
        if (_cachedRing != null &&
            _cachedCenter.Latitude.Equals(Center.Latitude) &&
            _cachedCenter.Longitude.Equals(Center.Longitude) &&
            _cachedRadius.Equals(RadiusMeters) &&
            _cachedSegments == SegmentCount)
            return _cachedRing;

        var ring = Geodesy.CircleRing(Center, RadiusMeters, SegmentCount);
        _cachedCenter = Center;
        _cachedRadius = RadiusMeters;
        _cachedSegments = SegmentCount;
        _cachedRing = ring;
        return ring;
    }
}

/// <summary>方形矢量：以经纬度中心 + 边长（米）+ 旋转角定义。</summary>
public sealed class GlobeSquareVector : GlobeVector
{
    /// <summary>中心点。</summary>
    public GeoPoint Center { get; set; }

    /// <summary>边长（米）。</summary>
    public double SideMeters { get; set; } = 1000d;

    /// <summary>相对正北的旋转角（度）。</summary>
    public double Heading { get; set; }

    private IReadOnlyList<GeoPoint>? _cachedRing;
    private GeoPoint _cachedCenter;
    private double _cachedSide = double.NaN;
    private double _cachedHeading = double.NaN;

    protected internal override IReadOnlyList<GeoPoint> BuildRing()
    {
        if (_cachedRing != null &&
            _cachedCenter.Latitude.Equals(Center.Latitude) &&
            _cachedCenter.Longitude.Equals(Center.Longitude) &&
            _cachedSide.Equals(SideMeters) &&
            _cachedHeading.Equals(Heading))
            return _cachedRing;

        var ring = Geodesy.SquareRing(Center, SideMeters, Heading);
        _cachedCenter = Center;
        _cachedSide = SideMeters;
        _cachedHeading = Heading;
        _cachedRing = ring;
        return ring;
    }
}

/// <summary>测量工具类型。</summary>
public enum GlobeTool
{
    /// <summary>不启用测量工具（默认交互：旋转/平移、点击标记）。</summary>
    None,

    /// <summary>距离测量：连续点击添加顶点，双击或右键结束。</summary>
    Distance,

    /// <summary>多边形面积测量：连续点击添加顶点，双击或右键结束。</summary>
    AreaPolygon,

    /// <summary>圆形面积测量：第一次点击定圆心，移动后再次点击确定半径。</summary>
    AreaCircle,

    /// <summary>方形面积测量：第一次点击定中心，移动后再次点击确定边界点（正北朝上）。</summary>
    AreaSquare
}

/// <summary>比例尺在视口中的停靠位置。</summary>
public enum GlobeScaleBarPlacement
{
    BottomLeft,
    BottomRight,
    TopLeft,
    TopRight
}

/// <summary>一次完成的测量结果（距离或面积）。</summary>
public sealed class GlobeMeasurement
{
    internal GlobeMeasurement(GlobeTool tool, IReadOnlyList<GeoPoint> points)
    {
        Tool = tool;
        Points = points;

        switch (tool)
        {
            case GlobeTool.Distance:
                LengthMeters = Geodesy.PathLength(points);
                Text = Geodesy.FormatDistance(LengthMeters);
                break;

            case GlobeTool.AreaCircle when points.Count >= 2:
            {
                var radius = Geodesy.Distance(points[0], points[1]);
                RadiusMeters = radius;
                AreaRing = Geodesy.CircleRing(points[0], radius);
                AreaSquareMeters = Geodesy.CircleArea(radius);

                // 测地线圆的周长：2πR·sin(r/R)，半径较大时与平面 2πr 有明显差异
                var angular = Math.Min(Math.PI, radius / Geodesy.EarthRadius);
                LengthMeters = 2d * Math.PI * Geodesy.EarthRadius * Math.Sin(angular);
                Text = Geodesy.FormatArea(AreaSquareMeters);
                break;
            }

            case GlobeTool.AreaSquare when points.Count >= 2:
            {
                var side = Geodesy.SquareSideFromCorner(points[0], points[1]);
                SideMeters = side;
                var ring = Geodesy.SquareRing(points[0], side);
                AreaRing = ring;
                AreaSquareMeters = Geodesy.PolygonArea(ring);
                LengthMeters = Geodesy.PathLength(ring, closed: true);
                Text = Geodesy.FormatArea(AreaSquareMeters);
                break;
            }

            case GlobeTool.AreaPolygon:
            default:
                AreaSquareMeters = Geodesy.PolygonArea(points);
                LengthMeters = Geodesy.PathLength(points, closed: true);
                Text = Geodesy.FormatArea(AreaSquareMeters);
                break;
        }
    }

    /// <summary>使用的测量工具。</summary>
    public GlobeTool Tool { get; }

    /// <summary>
    /// 参与测量的经纬度点。距离/多边形为全部顶点；圆形与方形为
    /// [圆心或中心, 圆周或边界点] 两个点。
    /// </summary>
    public IReadOnlyList<GeoPoint> Points { get; }

    /// <summary>距离（米）；面积测量时为周长。</summary>
    public double LengthMeters { get; }

    /// <summary>面积（平方米）；距离测量时为 0。</summary>
    public double AreaSquareMeters { get; }

    /// <summary>圆形测量半径（米），其它工具为 0。</summary>
    public double RadiusMeters { get; }

    /// <summary>方形测量边长（米），其它工具为 0。</summary>
    public double SideMeters { get; }

    /// <summary>格式化后的结果文本（如 <c>1.23 km</c>、<c>45.6 km²</c>）。</summary>
    public string Text { get; }

    /// <summary>圆形/方形测量在完成时缓存的边界环（绘制时直接复用，不再重算三角函数）。</summary>
    internal IReadOnlyList<GeoPoint>? AreaRing { get; }

    public override string ToString() => $"{Tool}: {Text}";
}

/// <summary>测量完成事件参数。</summary>
public sealed class GlobeMeasurementEventArgs : EventArgs
{
    public GlobeMeasurementEventArgs(GlobeMeasurement measurement)
    {
        Measurement = measurement;
    }

    public GlobeMeasurement Measurement { get; }
}

/// <summary>绘制中的测量草稿（由控件在鼠标移动/点击时维护）。</summary>
internal sealed class GlobeMeasurementDraft
{
    public GlobeTool Tool { get; set; }

    public List<GeoPoint> Points { get; } = new();

    /// <summary>当前鼠标位置对应的地表点，用于绘制预览线。</summary>
    public GeoPoint? Pointer { get; set; }
}
