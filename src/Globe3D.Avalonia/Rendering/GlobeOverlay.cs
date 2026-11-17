using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Globe3D.Avalonia;

/// <summary>
/// 覆盖在 GL 表面之上的二维标注层。使用 Avalonia 矢量绘制承担文字与图标，
/// 保证中文标注与抗锯齿质量，并按球体遮挡关系自动隐藏背面元素。
/// </summary>
internal sealed class GlobeOverlay : Control
{
    private static readonly Typeface DefaultTypeface = new(FontFamily.Default);
    private static readonly Typeface BoldTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private static readonly SolidColorBrush MeasureStroke = new(Color.FromArgb(255, 90, 200, 255));
    private static readonly SolidColorBrush MeasureDraftStroke = new(Color.FromArgb(235, 150, 225, 255));
    private static readonly SolidColorBrush MeasureFill = new(Color.FromArgb(34, 90, 200, 255));
    private static readonly SolidColorBrush MeasureVertexFill = new(Color.FromArgb(255, 24, 66, 102));
    private static readonly SolidColorBrush ScaleBarStroke = new(Color.FromArgb(240, 236, 245, 255));
    private static readonly SolidColorBrush ScaleBarShadow = new(Color.FromArgb(170, 0, 0, 0));
    private static readonly SolidColorBrush ScaleBarBackground = new(Color.FromArgb(190, 10, 16, 26));

    private readonly List<Rect> _labelRects = new();
    private readonly List<(GlobeMarker Marker, Rect Bounds, Point Center)> _markerHits = new();

    private FormattedText? _zoomBadgeText;
    private double _zoomBadgeLevel = double.NaN;

    /// <summary>当前鼠标位置（相对覆盖层），为 null 表示鼠标不在控件上。</summary>
    public Point? PointerPosition { get; set; }

    /// <summary>当前高亮的标记点（由控件在鼠标移动时设置）。</summary>
    public GlobeMarker? HoverMarker { get; set; }

    /// <summary>接近判定默认半径（像素），标记点自身设置了 HoverRadius 时以标记点为准。</summary>
    public double HoverRadius { get; set; } = 18d;

    /// <summary>当前相机。</summary>
    public OrbitCamera? Camera { get; set; }

    /// <summary>要绘制的图层。</summary>
    public IReadOnlyList<GlobeLayer> Layers { get; set; } = Array.Empty<GlobeLayer>();

    /// <summary>是否绘制文字标注。</summary>
    public bool ShowLabels { get; set; } = true;

    /// <summary>标签之间的最小间距（像素）。</summary>
    public double LabelPadding { get; set; } = 3d;

    /// <summary>是否对标签做防重叠避让。</summary>
    public bool AvoidLabelOverlap { get; set; } = true;

    /// <summary>是否在视口角落绘制比例尺。</summary>
    public bool ShowScaleBar { get; set; }

    /// <summary>比例尺停靠位置。</summary>
    public GlobeScaleBarPlacement ScaleBarPlacement { get; set; } = GlobeScaleBarPlacement.BottomLeft;

    /// <summary>是否在视口角落显示当前缩放级别。</summary>
    public bool ShowZoomLevel { get; set; }

    /// <summary>缩放级别显示的停靠位置。</summary>
    public GlobeScaleBarPlacement ZoomLevelPlacement { get; set; } = GlobeScaleBarPlacement.BottomRight;

    /// <summary>已完成的测量结果。</summary>
    public IReadOnlyList<GlobeMeasurement> Measurements { get; set; } = Array.Empty<GlobeMeasurement>();

    /// <summary>正在绘制中的测量草稿。</summary>
    public GlobeMeasurementDraft? MeasurementDraft { get; set; }

    public override void Render(DrawingContext context)
    {
        _labelRects.Clear();
        _markerHits.Clear();

        var camera = Camera;
        if (camera == null)
            return;

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 1 || height <= 1)
            return;

        // 先画区域，再画矢量图形，再画标记，最后画文字
        foreach (var layer in Layers)
        {
            if (!layer.IsVisible)
                continue;
            foreach (var region in layer.Regions)
                DrawRegion(context, camera, region, width, height);
        }

        foreach (var layer in Layers)
        {
            if (!layer.IsVisible)
                continue;
            foreach (var vector in layer.Vectors)
                DrawVector(context, camera, vector, width, height);
        }

        foreach (var layer in Layers)
        {
            if (!layer.IsVisible)
                continue;
            foreach (var marker in layer.Markers)
                DrawMarker(context, camera, marker, width, height);
        }

        if (ShowLabels)
        {
            foreach (var layer in Layers)
            {
                if (!layer.IsVisible)
                    continue;
                foreach (var marker in layer.Markers)
                {
                    if (marker.IsVisible && !string.IsNullOrEmpty(marker.Label))
                        DrawMarkerLabel(context, camera, marker, width, height);
                }

                foreach (var label in layer.Labels)
                    DrawLabel(context, camera, label, width, height);
            }
        }

        // 测量结果与比例尺属于工具层，不受 ShowLabels 影响
        foreach (var measurement in Measurements)
            DrawMeasurement(context, camera, measurement, width, height);

        if (MeasurementDraft is { } draft)
            DrawMeasurementDraft(context, camera, draft, width, height);

        if (ShowScaleBar)
            DrawScaleBar(context, camera, width, height);

        if (ShowZoomLevel)
            DrawZoomLevel(context, camera, width, height);

        // 悬停高亮与提示框画在标注之上，保证不被其他元素遮挡
        if (HoverMarker is { } hover)
            DrawHover(context, hover, width, height);
    }

    #region 区域与矢量图形

    private void DrawRegion(DrawingContext context, OrbitCamera camera, GlobeRegion region,
        double width, double height)
    {
        if (!region.IsVisible || region.Points.Count < 3)
            return;

        DrawGeoShape(context, camera, AsReadOnly(region.Points), closed: true,
            region.Fill, region.Stroke, region.StrokeThickness, dashStyle: null,
            region.Label, !string.IsNullOrEmpty(region.Label), 13d, Brushes.White,
            boldLabel: true, width, height);
    }

    private void DrawVector(DrawingContext context, OrbitCamera camera, GlobeVector vector,
        double width, double height)
    {
        if (!vector.IsVisible)
            return;

        var ring = vector.BuildRing();
        if (ring is not { Count: >= 2 })
            return;

        DrawGeoShape(context, camera, ring, vector.IsClosedRing, vector.Fill, vector.Stroke,
            vector.StrokeThickness, vector.DashStyle, vector.Label,
            ShowLabels && vector.ShowLabel && !string.IsNullOrEmpty(vector.Label), vector.LabelFontSize,
            vector.LabelForeground, boldLabel: true, width, height);
    }

    /// <summary>
    /// 绘制一段经纬度几何：闭合环支持填充（镂空时 Fill 传 null）与描边，开放折线只描边。
    /// 三维模式按大圆插值并裁剪到可见半球；平面模式按经纬度投影并拆分裂到 ±180° 与视口矩形。
    /// </summary>
    private void DrawGeoShape(DrawingContext context, OrbitCamera camera, IReadOnlyList<GeoPoint> points,
        bool closed, IBrush? fill, IBrush? stroke, double strokeThickness, IDashStyle? dashStyle,
        string? label, bool showLabel, double labelFontSize, IBrush labelForeground, bool boldLabel,
        double width, double height)
    {
        if (points.Count < 2)
            return;

        if (closed && points.Count < 3)
            closed = false;

        var pen = stroke != null && strokeThickness > 0d
            ? new Pen(stroke, Math.Max(0.5d, strokeThickness), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round)
            : null;
        if (pen != null && dashStyle != null)
            pen.DashStyle = dashStyle;

        if (camera.IsFlat)
        {
            DrawFlatGeoShape(context, camera, points, closed, fill, pen, label, showLabel,
                labelFontSize, labelForeground, boldLabel, width, height);
            return;
        }

        // 顶点按大圆插值展开：三维坐标天然没有 ±180° 经线的接缝
        var ring = new List<Vec3>(Math.Max(8, points.Count * 8));
        if (closed)
        {
            for (var i = 0; i < points.Count; i++)
                AppendArc(points[i], points[(i + 1) % points.Count], ring);
        }
        else
        {
            ring.Add(ToWorld(points[0]));
            for (var i = 0; i < points.Count - 1; i++)
                AppendArc(points[i], points[i + 1], ring);
            ring.Add(ToWorld(points[^1]));
        }

        if (ring.Count < 2)
            return;

        var projected = new List<Point>(ring.Count);
        var visible = new List<bool>(ring.Count);
        var anyVisible = false;
        var allVisible = true;

        foreach (var point in ring)
        {
            double px = 0, py = 0;
            float depth = 1f;
            var ok = !GeoMath.IsSegmentOccluded(camera.Position, point, 1f)
                     && camera.Project(point, width, height, out px, out py, out depth)
                     && depth is >= -1f and <= 1f;

            if (ok)
            {
                projected.Add(new Point(px, py));
                visible.Add(true);
                anyVisible = true;
            }
            else
            {
                projected.Add(default);
                visible.Add(false);
                allVisible = false;
            }
        }

        if (!anyVisible)
            return;

        // 填充：先裁剪到相机可见半球再投影，跨地平线的区域也能得到闭合多边形
        List<Point>? fillPoints = null;
        if (closed && fill != null)
        {
            fillPoints = BuildFillPolygon(ring, camera, width, height);
            if (fillPoints != null)
            {
                var geometry = BuildGeometry(fillPoints, closed: true);
                if (geometry != null)
                    context.DrawGeometry(fill, null, geometry);
            }
        }

        if (pen != null)
        {
            if (allVisible)
                DrawPolyline(context, pen, projected, closed);
            else
                DrawVisibleSegments(context, pen, projected, visible, closed);
        }

        if (showLabel && !string.IsNullOrEmpty(label))
        {
            var center = ComputeCentroid(fillPoints) ?? ComputeVisibleCentroid(projected, visible);
            if (center is { } anchor)
                DrawTextWithShadow(context, label, anchor, labelFontSize, labelForeground,
                    boldLabel ? BoldTypeface : DefaultTypeface, centerAligned: true);
        }
    }

    /// <summary>
    /// 平面模式下的几何绘制：按经纬度线性展开（二维地图不需要大圆插值），
    /// 跨 ±180° 经线的环拆成左右两段分别填充/描边。
    /// </summary>
    private void DrawFlatGeoShape(DrawingContext context, OrbitCamera camera, IReadOnlyList<GeoPoint> points,
        bool closed, IBrush? fill, IPen? pen, string? label, bool showLabel, double labelFontSize,
        IBrush labelForeground, bool boldLabel, double width, double height)
    {
        Point? labelAnchor = null;
        var bestCount = 0;

        if (closed)
        {
            foreach (var ring in BuildFlatRegionPolygons(points))
            {
                var projected = new List<Point>(ring.Count);
                foreach (var (lon, lat) in ring)
                {
                    if (!camera.ProjectGeo(new GeoPoint(lat, lon), width, height, out var sx, out var sy, out _))
                        continue;
                    projected.Add(new Point(sx, sy));
                }

                if (projected.Count < 3)
                    continue;

                var clipped = ClipPolygonToRect(projected, new Rect(-8d, -8d, width + 16d, height + 16d));
                if (clipped.Count < 3)
                    continue;

                if (fill != null)
                {
                    var geometry = BuildGeometry(clipped, closed: true);
                    if (geometry != null)
                        context.DrawGeometry(fill, null, geometry);
                }

                if (pen != null)
                    DrawPolyline(context, pen, clipped, closed: true);

                if (clipped.Count > bestCount && ComputeCentroid(clipped) is { } centroid)
                {
                    bestCount = clipped.Count;
                    labelAnchor = centroid;
                }
            }
        }
        else
        {
            foreach (var run in BuildFlatPolylineRuns(points))
                DrawFlatPolyline(context, camera, pen, run, width, height);

            if (labelAnchor == null)
            {
                var middle = points[points.Count / 2];
                if (camera.ProjectGeo(middle, width, height, out var mx, out var my, out _))
                    labelAnchor = new Point(mx, my);
            }
        }

        if (showLabel && !string.IsNullOrEmpty(label) && labelAnchor is { } anchor)
            DrawTextWithShadow(context, label, anchor, labelFontSize, labelForeground,
                boldLabel ? BoldTypeface : DefaultTypeface, centerAligned: true);
    }

    private static IReadOnlyList<GeoPoint> AsReadOnly(IList<GeoPoint> points) =>
        points as IReadOnlyList<GeoPoint> ?? points.ToArray();

    private static Vec3 ToWorld(GeoPoint point) =>
        GeoMath.LonLatToUnitVector(point.Longitude, point.Latitude) *
        GeoMath.AltitudeToRadius(point.Altitude);

    /// <summary>
    /// 平面模式区域环：经度按最短路径展开后，分别平移 -360/0/+360 裁剪到 -180..180，
    /// 因此跨经线的区域会得到左右两段闭合多边形，填充不会横穿整张地图。
    /// </summary>
    private static List<List<(double Lon, double Lat)>> BuildFlatRegionPolygons(IReadOnlyList<GeoPoint> points)
    {
        var result = new List<List<(double Lon, double Lat)>>();
        if (points.Count < 3)
            return result;

        var unwrapped = UnwrapLongitudes(points);

        foreach (var shift in new[] { -360d, 0d, 360d })
        {
            var shifted = new List<(double Lon, double Lat)>(unwrapped.Count);
            foreach (var point in unwrapped)
                shifted.Add((point.Lon + shift, point.Lat));

            var clipped = ClipFlatPolygonToLongitude(shifted, -180d, 180d);
            if (clipped.Count >= 3)
                result.Add(clipped);
        }

        return result;
    }

    /// <summary>把经纬度顶点按最短路径展开成连续的经度（避免 179° → -179° 被当成绕地球一圈）。</summary>
    private static List<(double Lon, double Lat)> UnwrapLongitudes(IReadOnlyList<GeoPoint> points)
    {
        var unwrapped = new List<(double Lon, double Lat)>(points.Count);
        if (points.Count == 0)
            return unwrapped;

        var lon = MathUtil.NormalizeLongitude(points[0].Longitude);
        var lat = MathUtil.Clamp(points[0].Latitude, -GeoMath.MaxMercatorLatitude, GeoMath.MaxMercatorLatitude);
        unwrapped.Add((lon, lat));

        for (var i = 1; i < points.Count; i++)
        {
            lon += MathUtil.NormalizeLongitudeDelta(points[i].Longitude - lon);
            lat = MathUtil.Clamp(points[i].Latitude, -GeoMath.MaxMercatorLatitude, GeoMath.MaxMercatorLatitude);
            unwrapped.Add((lon, lat));
        }

        return unwrapped;
    }

    /// <summary>
    /// 平面模式折线：经度展开后拆成若干段，每段都裁剪到 ±180° 经线内；
    /// 跨经线的折线会得到左右两段，而不是横穿整张地图。
    /// </summary>
    private static List<List<(double Lon, double Lat)>> BuildFlatPolylineRuns(IReadOnlyList<GeoPoint> points)
    {
        var result = new List<List<(double Lon, double Lat)>>();
        if (points.Count < 2)
            return result;

        var unwrapped = UnwrapLongitudes(points);

        foreach (var shift in new[] { -360d, 0d, 360d })
        {
            List<(double Lon, double Lat)>? run = null;

            for (var i = 0; i < unwrapped.Count - 1; i++)
            {
                var start = (Lon: unwrapped[i].Lon + shift, Lat: unwrapped[i].Lat);
                var end = (Lon: unwrapped[i + 1].Lon + shift, Lat: unwrapped[i + 1].Lat);

                if (!ClipFlatSegmentToLongitude(ref start, ref end, -180d, 180d))
                {
                    if (run != null)
                    {
                        result.Add(run);
                        run = null;
                    }

                    continue;
                }

                // 与上一段不连续（中间有段被裁掉）时另起一条折线
                if (run != null && !NearlyEquals(run[^1], start))
                {
                    result.Add(run);
                    run = null;
                }

                run ??= new List<(double Lon, double Lat)> { start };
                if (!NearlyEquals(run[^1], end))
                    run.Add(end);
            }

            if (run is { Count: >= 2 })
                result.Add(run);
        }

        return result;
    }

    private static bool NearlyEquals((double Lon, double Lat) a, (double Lon, double Lat) b) =>
        Math.Abs(a.Lon - b.Lon) < 1e-9d && Math.Abs(a.Lat - b.Lat) < 1e-9d;

    /// <summary>把经纬度线段裁剪到两条经线之间（Liang–Barsky）。</summary>
    private static bool ClipFlatSegmentToLongitude(ref (double Lon, double Lat) start,
        ref (double Lon, double Lat) end, double west, double east)
    {
        var t0 = 0d;
        var t1 = 1d;
        if (!ClipSegmentHalfPlane(ref t0, ref t1, start.Lon - west, end.Lon - west))
            return false;
        if (!ClipSegmentHalfPlane(ref t0, ref t1, east - start.Lon, east - end.Lon))
            return false;

        // 浮点插值可能略微越过 ±180（如 180.00000000000003），
        // 而 ProjectGeo 会把越界值按 ±360 折回另一侧，导致线段横贯整张地图，必须钉到裁剪边界上
        var a = (Lon: Math.Clamp(start.Lon + (end.Lon - start.Lon) * t0, west, east),
            Lat: start.Lat + (end.Lat - start.Lat) * t0);
        var b = (Lon: Math.Clamp(start.Lon + (end.Lon - start.Lon) * t1, west, east),
            Lat: start.Lat + (end.Lat - start.Lat) * t1);
        start = a;
        end = b;
        return true;
    }

    private static bool ClipSegmentHalfPlane(ref double t0, ref double t1, double d0, double d1)
    {
        if (d0 >= 0d && d1 >= 0d)
            return true;
        if (d0 < 0d && d1 < 0d)
            return false;

        var t = d0 / (d0 - d1);
        if (d0 < 0d)
            t0 = Math.Max(t0, t);
        else
            t1 = Math.Min(t1, t);
        return t1 > t0;
    }

    private static void DrawFlatPolyline(DrawingContext context, OrbitCamera camera, IPen? pen,
        List<(double Lon, double Lat)> run, double width, double height)
    {
        if (pen == null || run.Count < 2)
            return;

        var viewport = new Rect(-8d, -8d, width + 16d, height + 16d);
        for (var i = 0; i < run.Count - 1; i++)
        {
            if (!camera.ProjectGeo(new GeoPoint(run[i].Lat, run[i].Lon), width, height,
                    out var ax, out var ay, out _))
                continue;
            if (!camera.ProjectGeo(new GeoPoint(run[i + 1].Lat, run[i + 1].Lon), width, height,
                    out var bx, out var by, out _))
                continue;

            DrawClippedScreenSegment(context, pen, new Point(ax, ay), new Point(bx, by), viewport);
        }
    }

    /// <summary>把屏幕线段裁剪到视口矩形后绘制，避免深层级下超大坐标进入 Skia。</summary>
    private static void DrawClippedScreenSegment(DrawingContext context, IPen pen, Point a, Point b, Rect rect)
    {
        var t0 = 0d;
        var t1 = 1d;
        if (!ClipSegmentHalfPlane(ref t0, ref t1, a.X - rect.Left, b.X - rect.Left))
            return;
        if (!ClipSegmentHalfPlane(ref t0, ref t1, rect.Right - a.X, rect.Right - b.X))
            return;
        if (!ClipSegmentHalfPlane(ref t0, ref t1, a.Y - rect.Top, b.Y - rect.Top))
            return;
        if (!ClipSegmentHalfPlane(ref t0, ref t1, rect.Bottom - a.Y, rect.Bottom - b.Y))
            return;

        var start = new Point(a.X + (b.X - a.X) * t0, a.Y + (b.Y - a.Y) * t0);
        var end = new Point(a.X + (b.X - a.X) * t1, a.Y + (b.Y - a.Y) * t1);
        if (!start.Equals(end))
            context.DrawLine(pen, start, end);
    }

    private static List<(double Lon, double Lat)> ClipFlatPolygonToLongitude(
        List<(double Lon, double Lat)> polygon, double west, double east)
    {
        var result = ClipFlatHalfPlane(polygon, point => point.Lon - west);
        result = ClipFlatHalfPlane(result, point => east - point.Lon);

        // 与折线相同的浮点越界问题：Sutherland–Hodgman 的交点可能多出 1e-14，
        // 经度折回后会在另一侧产生孤立顶点，导致填充/描边异常
        for (var i = 0; i < result.Count; i++)
            result[i] = (Math.Clamp(result[i].Lon, west, east), result[i].Lat);

        return result;
    }

    private static List<(double Lon, double Lat)> ClipFlatHalfPlane(
        List<(double Lon, double Lat)> polygon, Func<(double Lon, double Lat), double> distance)
    {
        var result = new List<(double Lon, double Lat)>(polygon.Count + 4);
        if (polygon.Count == 0)
            return result;

        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var da = distance(a);
            var db = distance(b);

            if (da >= 0d)
                result.Add(a);

            if ((da >= 0d) != (db >= 0d) && Math.Abs(db - da) > 1e-12d)
            {
                var t = da / (da - db);
                result.Add((a.Lon + (b.Lon - a.Lon) * t, a.Lat + (b.Lat - a.Lat) * t));
            }
        }

        return result;
    }

    /// <summary>把一条边按大圆展开成若干三维点（不含终点，保证首尾不重复）。</summary>
    private static void AppendArc(GeoPoint start, GeoPoint end, List<Vec3> ring)
    {
        var a = GeoMath.LonLatToUnitVector(start.Longitude, start.Latitude) *
                GeoMath.AltitudeToRadius(start.Altitude);
        var b = GeoMath.LonLatToUnitVector(end.Longitude, end.Latitude) *
                GeoMath.AltitudeToRadius(end.Altitude);

        var angle = Math.Acos(MathUtil.Clamp(Vec3.Dot(Vec3.Normalize(a), Vec3.Normalize(b)), -1f, 1f));
        var steps = Math.Clamp((int)Math.Ceiling(angle / (1.2d * MathUtil.DegToRad)), 1, 720);

        for (var i = 0; i < steps; i++)
            ring.Add(SphericalLerp(a, b, i / (float)steps));
    }

    /// <summary>球面线性插值：沿最短大圆路径插值，跨 ±180° 经线不会绕远。</summary>
    private static Vec3 SphericalLerp(Vec3 a, Vec3 b, float t)
    {
        var na = Vec3.Normalize(a);
        var nb = Vec3.Normalize(b);
        var radius = MathUtil.Lerp(a.Length, b.Length, t);
        var dot = MathUtil.Clamp(Vec3.Dot(na, nb), -1f, 1f);
        var angle = MathF.Acos(dot);

        // 夹角过小或接近对跖点时 sin(angle) 趋于 0，退化为线性插值
        if (angle < 1e-4f || angle > MathF.PI - 1e-3f)
            return Vec3.Normalize(Vec3.Lerp(a, b, t)) * radius;

        var sin = MathF.Sin(angle);
        var wa = MathF.Sin((1f - t) * angle) / sin;
        var wb = MathF.Sin(t * angle) / sin;
        return Vec3.Normalize(na * wa + nb * wb) * radius;
    }

    /// <summary>把球面环裁剪到相机可见半球（该半球的边界就是球体可见轮廓线）。</summary>
    private static List<Vec3> ClipToVisibleHemisphere(List<Vec3> ring, Vec3 cameraPosition)
    {
        var result = new List<Vec3>(ring.Count + 8);

        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            var insideA = Vec3.Dot(Vec3.Normalize(a), cameraPosition) >= 1f;
            var insideB = Vec3.Dot(Vec3.Normalize(b), cameraPosition) >= 1f;

            if (insideA)
                result.Add(a);

            if (insideA != insideB)
                result.Add(HorizonCrossing(a, b, cameraPosition, insideA));
        }

        return result;
    }

    /// <summary>二分求边与可见半球边界（地平线）的交点。</summary>
    private static Vec3 HorizonCrossing(Vec3 a, Vec3 b, Vec3 cameraPosition, bool insideA)
    {
        var lo = 0f;
        var hi = 1f;

        for (var i = 0; i < 24; i++)
        {
            var mid = (lo + hi) * 0.5f;
            var sample = SphericalLerp(a, b, mid);
            var inside = Vec3.Dot(Vec3.Normalize(sample), cameraPosition) >= 1f;
            if (inside == insideA)
                lo = mid;
            else
                hi = mid;
        }

        return SphericalLerp(a, b, (lo + hi) * 0.5f);
    }

    private static List<Point>? BuildFillPolygon(List<Vec3> ring, OrbitCamera camera, double width, double height)
    {
        // 先裁剪到可见半球（地平线），再裁剪到相机前方，保证每个点都能投影出有限坐标
        var clipped = ClipToVisibleHemisphere(ring, camera.Position);
        if (clipped.Count < 3)
            return null;

        clipped = ClipToForwardPlane(clipped, camera);
        if (clipped.Count < 3)
            return null;

        var points = new List<Point>(clipped.Count);
        foreach (var point in clipped)
        {
            if (!camera.Project(point, width, height, out var px, out var py, out var depth))
                return null;
            if (depth is < -1f or > 1f)
                return null;
            points.Add(new Point(px, py));
        }

        // 相机贴地时多边形会覆盖远超屏幕的范围，裁剪到视口矩形：
        // 既限制几何规模，也避免非零环绕在屏幕外绕出错误的洞
        var viewport = new Rect(-8d, -8d, width + 16d, height + 16d);
        var screenClipped = ClipPolygonToRect(points, viewport);
        return screenClipped is { Count: >= 3 } ? screenClipped : null;
    }

    /// <summary>裁剪三维多边形到相机前方的半空间（近平面）。</summary>
    private static List<Vec3> ClipToForwardPlane(List<Vec3> ring, OrbitCamera camera)
    {
        var forward = camera.Forward;
        var plane = Vec3.Dot(camera.Position, forward) + MathF.Max(camera.NearPlane, 1e-4f);
        var result = new List<Vec3>(ring.Count + 4);

        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            var da = Vec3.Dot(a, forward) - plane;
            var db = Vec3.Dot(b, forward) - plane;

            if (da >= 0f)
                result.Add(a);

            if ((da >= 0f) != (db >= 0f) && MathF.Abs(db - da) > 1e-9f)
            {
                var t = da / (da - db);
                result.Add(a + (b - a) * t);
            }
        }

        return result;
    }

    /// <summary>把投影后的多边形裁剪到视口矩形（Sutherland–Hodgman）。</summary>
    private static List<Point> ClipPolygonToRect(List<Point> polygon, Rect rect)
    {
        var result = ClipHalfPlane(polygon, p => p.X - rect.Left);
        result = ClipHalfPlane(result, p => rect.Right - p.X);
        result = ClipHalfPlane(result, p => p.Y - rect.Top);
        result = ClipHalfPlane(result, p => rect.Bottom - p.Y);
        return result;
    }

    private static List<Point> ClipHalfPlane(List<Point> polygon, Func<Point, double> distance)
    {
        var result = new List<Point>(polygon.Count + 4);
        if (polygon.Count == 0)
            return result;

        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var da = distance(a);
            var db = distance(b);

            if (da >= 0d)
                result.Add(a);

            if ((da >= 0d) != (db >= 0d) && Math.Abs(db - da) > 1e-12d)
            {
                var t = da / (da - db);
                result.Add(new Point(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
            }
        }

        return result;
    }

    private static void DrawVisibleSegments(DrawingContext context, IPen pen, List<Point> points,
        List<bool> visible, bool closed)
    {
        var last = closed ? points.Count : points.Count - 1;
        for (var i = 0; i < last; i++)
        {
            var next = (i + 1) % points.Count;
            if (visible[i] && visible[next] && !points[i].Equals(points[next]))
                context.DrawLine(pen, points[i], points[next]);
        }
    }

    private static void DrawPolyline(DrawingContext context, IPen pen, List<Point> points, bool closed)
    {
        for (var i = 0; i < points.Count - 1; i++)
            context.DrawLine(pen, points[i], points[i + 1]);
        if (closed && points.Count > 2)
            context.DrawLine(pen, points[^1], points[0]);
    }

    private static Geometry? BuildGeometry(List<Point> points, bool closed)
    {
        if (points.Count < 3)
            return null;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            // 非零环绕：跨地平线裁剪后可能出现自相交投影，避免 even-odd 规则产生孔洞
            ctx.SetFillRule(FillRule.NonZero);
            ctx.BeginFigure(points[0], true);
            for (var i = 1; i < points.Count; i++)
                ctx.LineTo(points[i]);
            ctx.EndFigure(closed);
        }

        return geometry;
    }

    private static Point? ComputeCentroid(List<Point>? points)
    {
        if (points is not { Count: > 0 })
            return null;

        double x = 0, y = 0;
        foreach (var p in points)
        {
            x += p.X;
            y += p.Y;
        }

        return new Point(x / points.Count, y / points.Count);
    }

    private static Point? ComputeVisibleCentroid(List<Point> points, List<bool> visible)
    {
        double x = 0, y = 0;
        var count = 0;

        for (var i = 0; i < points.Count; i++)
        {
            if (!visible[i])
                continue;
            x += points[i].X;
            y += points[i].Y;
            count++;
        }

        return count > 0 ? new Point(x / count, y / count) : null;
    }

    #endregion

    #region 测量与比例尺

    private void DrawMeasurement(DrawingContext context, OrbitCamera camera, GlobeMeasurement measurement,
        double width, double height)
    {
        switch (measurement.Tool)
        {
            case GlobeTool.Distance:
                DrawMeasureShape(context, camera, measurement.Points, closed: false, draft: false,
                    measurement.Text, showLabel: true, width, height);
                DrawSegmentLabels(context, camera, measurement.Points, width, height);
                DrawGeoVertices(context, camera, measurement.Points, width, height, 2.6d);
                break;

            case GlobeTool.AreaPolygon:
                DrawMeasureShape(context, camera, measurement.Points, closed: true, draft: false,
                    measurement.Text, showLabel: true, width, height);
                DrawGeoVertices(context, camera, measurement.Points, width, height, 2.6d);
                break;

            case GlobeTool.AreaCircle:
            case GlobeTool.AreaSquare:
            {
                if (measurement.Points.Count < 2)
                    return;

                var center = measurement.Points[0];
                var rim = measurement.Points[1];
                var ring = measurement.AreaRing ?? BuildAreaRing(measurement.Tool, center, rim);
                DrawMeasureShape(context, camera, ring, closed: true, draft: false,
                    measurement.Text, showLabel: true, width, height);

                // 半径/半边长虚线，便于看出尺寸依据
                DrawMeasureGuide(context, camera, center, rim, draft: false, width, height);
                DrawGeoVertices(context, camera, new[] { center, rim }, width, height, 2.6d);
                break;
            }
        }
    }

    private void DrawMeasurementDraft(DrawingContext context, OrbitCamera camera, GlobeMeasurementDraft draft,
        double width, double height)
    {
        switch (draft.Tool)
        {
            case GlobeTool.AreaCircle:
            case GlobeTool.AreaSquare:
            {
                if (draft.Points.Count == 0)
                {
                    if (draft.Pointer is { } preview)
                        DrawGeoVertex(context, camera, preview, width, height, 3d);
                    return;
                }

                var center = draft.Points[0];
                DrawGeoVertex(context, camera, center, width, height, 3d);
                if (draft.Pointer is not { } rim)
                    return;

                var ring = BuildAreaRing(draft.Tool, center, rim);
                var area = draft.Tool == GlobeTool.AreaCircle
                    ? Geodesy.CircleArea(Geodesy.Distance(center, rim))
                    : Geodesy.PolygonArea(ring);

                DrawMeasureShape(context, camera, ring, closed: true, draft: true,
                    Geodesy.FormatArea(area), showLabel: true, width, height);
                DrawMeasureGuide(context, camera, center, rim, draft: true, width, height);
                DrawGeoVertex(context, camera, rim, width, height, 3d);
                return;
            }
        }

        // 距离 / 多边形：已点击的顶点 + 当前鼠标位置形成橡皮筋线
        var points = new List<GeoPoint>(draft.Points);
        if (draft.Pointer is { } pointer &&
            (points.Count == 0 || Geodesy.Distance(points[^1], pointer) > 0.01d))
            points.Add(pointer);

        if (points.Count == 0)
            return;

        var closed = draft.Tool == GlobeTool.AreaPolygon && points.Count >= 3;
        var text = closed ? Geodesy.FormatArea(Geodesy.PolygonArea(points)) : null;

        DrawMeasureShape(context, camera, points, closed, draft: true, text, closed, width, height);

        if (!closed)
            DrawSegmentLabels(context, camera, points, width, height);

        DrawGeoVertices(context, camera, draft.Points, width, height, 2.6d);
    }

    /// <summary>测量图形统一样式：完成后蓝色实线，草稿为浅蓝虚线 + 半透明填充。</summary>
    private void DrawMeasureShape(DrawingContext context, OrbitCamera camera,
        IReadOnlyList<GeoPoint> points, bool closed, bool draft, string? label, bool showLabel,
        double width, double height)
    {
        var stroke = draft ? MeasureDraftStroke : MeasureStroke;
        DrawGeoShape(context, camera, points, closed, closed ? MeasureFill : null, stroke,
            draft ? 1.8d : 2d, draft ? DashStyle.Dash : null, label, showLabel, 12.5d,
            stroke, boldLabel: true, width, height);
    }

    /// <summary>从圆/方形中心到边界点的虚线引导线。</summary>
    private void DrawMeasureGuide(DrawingContext context, OrbitCamera camera,
        GeoPoint center, GeoPoint rim, bool draft, double width, double height)
    {
        var stroke = draft ? MeasureDraftStroke : MeasureStroke;
        DrawGeoShape(context, camera, new[] { center, rim }, closed: false, fill: null,
            stroke, 1.1d, DashStyle.Dash, label: null, showLabel: false, labelFontSize: 11d,
            labelForeground: stroke, boldLabel: false, width, height);
    }

    /// <summary>按圆形/方形的圆心与边界上的点生成经纬度环。</summary>
    private static IReadOnlyList<GeoPoint> BuildAreaRing(GlobeTool tool, GeoPoint center, GeoPoint rim)
    {
        if (tool == GlobeTool.AreaCircle)
            return Geodesy.CircleRing(center, Geodesy.Distance(center, rim));

        return Geodesy.SquareRing(center, Geodesy.SquareSideFromCorner(center, rim));
    }

    private void DrawSegmentLabels(DrawingContext context, OrbitCamera camera, IReadOnlyList<GeoPoint> points,
        double width, double height)
    {
        for (var i = 0; i < points.Count - 1; i++)
        {
            var distance = Geodesy.Distance(points[i], points[i + 1]);
            if (distance < 0.05d)
                continue;

            var middle = GeographicMidpoint(points[i], points[i + 1]);
            if (IsGeoPointVisible(camera, middle) &&
                camera.ProjectGeo(middle, width, height, out var sx, out var sy, out var depth) &&
                depth is >= -1f and <= 1f && !IsOutsideFlatViewport(camera, sx, sy, width, height))
            {
                DrawTextWithShadow(context, Geodesy.FormatDistance(distance),
                    new Point(sx, sy - 10d), 11d, MeasureStroke, DefaultTypeface,
                    centerAligned: true, allowOverlapCheck: true);
            }
        }
    }

    private void DrawGeoVertices(DrawingContext context, OrbitCamera camera, IReadOnlyList<GeoPoint> points,
        double width, double height, double radius)
    {
        foreach (var point in points)
            DrawGeoVertex(context, camera, point, width, height, radius);
    }

    private static void DrawGeoVertex(DrawingContext context, OrbitCamera camera, GeoPoint point,
        double width, double height, double radius)
    {
        if (!IsGeoPointVisible(camera, point))
            return;
        if (!camera.ProjectGeo(point, width, height, out var sx, out var sy, out var depth))
            return;
        if (depth is < -1f or > 1f)
            return;
        if (IsOutsideFlatViewport(camera, sx, sy, width, height))
            return;

        context.DrawEllipse(MeasureVertexFill, new Pen(Brushes.White, 1d), new Point(sx, sy), radius, radius);
    }

    private static bool IsGeoPointVisible(OrbitCamera camera, GeoPoint point) =>
        camera.IsFlat || !GeoMath.IsSegmentOccluded(camera.Position, ToWorld(point), 1f);

    /// <summary>两点间的球面中点（用于在线段中间标注长度）。</summary>
    private static GeoPoint GeographicMidpoint(GeoPoint a, GeoPoint b)
    {
        var va = GeoMath.LonLatToUnitVector(a.Longitude, a.Latitude);
        var vb = GeoMath.LonLatToUnitVector(b.Longitude, b.Latitude);
        var sum = va + vb;
        if (sum.LengthSquared < 1e-9f)
            return a;

        GeoMath.UnitVectorToLonLat(sum, out var longitude, out var latitude);
        return new GeoPoint(latitude, longitude);
    }

    /// <summary>绘制视口角落的比例尺：按当前相机在屏幕中心附近的真实地表距离选取整齐刻度。</summary>
    private void DrawScaleBar(DrawingContext context, OrbitCamera camera, double width, double height)
    {
        var metersPerPixel = GetMetersPerPixel(camera, width, height);
        if (!double.IsFinite(metersPerPixel) || metersPerPixel <= 0d)
            return;

        // 目标宽度 120px，取 1/2/5 × 10^n 中不超过目标的整齐距离
        var target = metersPerPixel * 120d;
        var power = Math.Pow(10d, Math.Floor(Math.Log10(Math.Max(1e-6d, target))));
        var normalized = target / power;
        var nice = normalized >= 5d ? 5d : normalized >= 2d ? 2d : 1d;
        var distance = nice * power;
        var barWidth = distance / metersPerPixel;
        if (!double.IsFinite(barWidth) || barWidth < 20d || barWidth > width - 32d)
            return;

        var top = ScaleBarPlacement is GlobeScaleBarPlacement.TopLeft or GlobeScaleBarPlacement.TopRight;
        var right = ScaleBarPlacement is GlobeScaleBarPlacement.TopRight or GlobeScaleBarPlacement.BottomRight;
        var left = right ? width - 16d - barWidth : 16d;
        var baseline = top ? 36d : height - 26d;

        var background = new Rect(left - 10d, baseline - 26d, barWidth + 20d, 36d);
        context.DrawRectangle(ScaleBarBackground, null, background, 4d, 4d);

        var shadowPen = new Pen(ScaleBarShadow, 2.6d);
        var pen = new Pen(ScaleBarStroke, 1.4d);
        context.DrawLine(shadowPen, new Point(left + 1d, baseline + 1d), new Point(left + barWidth + 1d, baseline + 1d));
        context.DrawLine(pen, new Point(left, baseline), new Point(left + barWidth, baseline));

        foreach (var x in new[] { left, left + barWidth })
        {
            context.DrawLine(shadowPen, new Point(x + 1d, baseline - 4d), new Point(x + 1d, baseline + 5d));
            context.DrawLine(pen, new Point(x, baseline - 5d), new Point(x, baseline + 5d));
        }

        DrawTextWithShadow(context, Geodesy.FormatDistance(distance),
            new Point(left + barWidth * 0.5d, baseline - 14d), 11.5d, ScaleBarStroke,
            DefaultTypeface, centerAligned: true);
    }

    /// <summary>在视口角落绘制当前缩放级别（Z x.x），与比例尺同角时自动错开。</summary>
    private void DrawZoomLevel(DrawingContext context, OrbitCamera camera, double width, double height)
    {
        // LOD 使用物理像素，这里必须乘渲染缩放，否则高 DPI 下显示级别会偏高
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        var level = camera.GetZoomLevel(width * scaling, height * scaling);
        if (!double.IsFinite(level))
            return;

        // 文本布局每帧重做会明显拉低帧率：级别只保留一位小数，未变化时复用
        var rounded = Math.Round(level, 1);
        if (_zoomBadgeText == null || !_zoomBadgeLevel.Equals(rounded))
        {
            _zoomBadgeText = new FormattedText($"Z {rounded:0.0}", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, DefaultTypeface, 11.5d, ScaleBarStroke);
            _zoomBadgeLevel = rounded;
        }

        var formatted = _zoomBadgeText;

        const double padX = 8d;
        const double padY = 4d;
        const double margin = 16d;
        var boxWidth = formatted.Width + padX * 2d;
        var boxHeight = formatted.Height + padY * 2d;

        var top = ZoomLevelPlacement is GlobeScaleBarPlacement.TopLeft or GlobeScaleBarPlacement.TopRight;
        var right = ZoomLevelPlacement is GlobeScaleBarPlacement.TopRight or GlobeScaleBarPlacement.BottomRight;
        var x = right ? width - margin - boxWidth : margin;
        var y = top ? margin : height - margin - boxHeight;

        // 与比例尺停在同一角时留出一行，避免重叠
        if (ShowScaleBar && ZoomLevelPlacement == ScaleBarPlacement)
            y += top ? 44d : -44d;

        x = Math.Clamp(x, 4d, Math.Max(4d, width - boxWidth - 4d));
        y = Math.Clamp(y, 4d, Math.Max(4d, height - boxHeight - 4d));

        var box = new Rect(x, y, boxWidth, boxHeight);
        context.DrawRectangle(ScaleBarBackground, null, box, 4d, 4d);
        context.DrawText(formatted, new Point(x + padX, y + padY));
    }

    /// <summary>以视口中心为基准，用相邻屏幕点对应的地表距离估算当前米/像素。</summary>
    private static double GetMetersPerPixel(OrbitCamera camera, double width, double height)
    {
        var sample = Math.Clamp(width * 0.1d, 24d, 120d);
        var y = height * 0.5d;
        var x0 = width * 0.5d - sample * 0.5d;

        if (camera.ScreenToGround(x0, y, width, height, out var a) &&
            camera.ScreenToGround(x0 + sample, y, width, height, out var b))
        {
            var distance = Geodesy.Distance(a, b);
            if (distance > 0d)
                return distance / sample;
        }

        // 兜底：按高度与垂直视场角估算（正俯视近似），保证比例尺始终有值
        if (camera.IsFlat)
        {
            // 平面模式：世界宽度为赤道周长，按中心纬度收缩为地面米数
            var cosLat = Math.Max(0.01d, Math.Cos(camera.CenterLatitude * MathUtil.DegToRad));
            return 2d * Math.PI * GeoMath.EarthRadius * cosLat /
                   Math.Max(1d, camera.FlatScaleFactor * Math.Max(1d, height));
        }

        var altitude = Math.Max(1d, camera.AltitudeMeters);
        var tanHalf = Math.Tan(camera.FieldOfView * MathUtil.DegToRad * 0.5d);
        return 2d * altitude * tanHalf / Math.Max(1d, height);
    }

    #endregion

    #region 标记点

    private void DrawMarker(DrawingContext context, OrbitCamera camera, GlobeMarker marker,
        double width, double height)
    {
        if (!marker.IsVisible)
            return;

        var radius = GeoMath.AltitudeToRadius(marker.Position.Altitude);
        var surface = GeoMath.LonLatToUnitVector(marker.Position.Longitude, marker.Position.Latitude);
        var world = surface * radius;

        if (!camera.IsFlat && GeoMath.IsSegmentOccluded(camera.Position, world, 1f))
            return;

        if (!camera.ProjectGeo(marker.Position, width, height, out var sx, out var sy, out var depth))
            return;
        if (depth is < -1f or > 1f)
            return;
        if (IsOutsideFlatViewport(camera, sx, sy, width, height))
            return;

        var center = new Point(sx, sy);
        var size = Math.Max(4d, marker.Size);
        var pen = new Pen(marker.Stroke, Math.Max(1d, size * 0.16));

        // 高度引线（平面模式没有高度概念，标记直接落在地图上）
        if (!camera.IsFlat && marker.ShowAltitudeLine && marker.Position.Altitude > 1d)
        {
            if (!GeoMath.IsOccludedBySphere(camera.Position, surface)
                && camera.Project(surface, width, height, out var gx, out var gy, out _))
            {
                var ground = new Point(gx, gy);
                var linePen = new Pen(marker.Fill, 1.2d) { DashStyle = DashStyle.Dash };
                context.DrawLine(linePen, ground, center);
                context.DrawEllipse(marker.Fill, null, ground, 2.2d, 2.2d);
            }
        }

        if (marker.ResolveImage() is { } image)
        {
            // 图片标记：按缩放/偏移绘制，命中区域用图片实际占用的屏幕矩形
            var imageRect = DrawMarkerImage(context, marker, image, center);
            _markerHits.Add((marker, imageRect.Inflate(2d), center));
        }
        else
        {
            DrawMarkerShape(context, marker, center, size, pen);
            _markerHits.Add((marker, GetShapeRect(marker, center), center));
        }
    }

    /// <summary>绘制图片标记（PNG/JPEG/GIF/SVG 均通过 IImage 绘制），返回图标的屏幕包围盒。</summary>
    private static Rect DrawMarkerImage(DrawingContext context, GlobeMarker marker, IImage image, Point center)
    {
        var rect = GetImageRect(marker, image, center);
        if (rect.Width > 0d && rect.Height > 0d)
            context.DrawImage(image, new Rect(image.Size), rect);
        return rect;
    }

    /// <summary>图片图标在屏幕上的矩形：以标记点投影为中心，按 ImageScale 缩放、ImageOffset 偏移。</summary>
    private static Rect GetImageRect(GlobeMarker marker, IImage image, Point center)
    {
        var scale = marker.ImageScale;
        if (!double.IsFinite(scale) || scale <= 0d)
            scale = 1d;

        var source = new Rect(image.Size);
        if (source.Width <= 0d || source.Height <= 0d)
            return new Rect(center.X, center.Y, 0d, 0d);

        var size = new Size(source.Width * scale, source.Height * scale);
        var origin = new Point(
            center.X - size.Width * 0.5d + marker.ImageOffset.X,
            center.Y - size.Height * 0.5d + marker.ImageOffset.Y);
        return new Rect(origin, size);
    }

    /// <summary>形状图标的屏幕包围盒（比图形本身大一圈，方便点击）。</summary>
    private static Rect GetShapeRect(GlobeMarker marker, Point center)
    {
        var size = Math.Max(4d, marker.Size);
        return new Rect(center.X - size, center.Y - size, size * 2d, size * 2d);
    }

    private static void DrawMarkerShape(DrawingContext context, GlobeMarker marker, Point center,
        double size, IPen pen)
    {
        var half = size * 0.5;
        switch (marker.Shape)
        {
            case MarkerShape.Circle:
                context.DrawEllipse(marker.Fill, pen, center, half, half);
                break;

            case MarkerShape.Square:
                context.DrawRectangle(marker.Fill, pen, new Rect(center.X - half, center.Y - half, size, size));
                break;

            case MarkerShape.Diamond:
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(new Point(center.X, center.Y - half), true);
                    ctx.LineTo(new Point(center.X + half, center.Y));
                    ctx.LineTo(new Point(center.X, center.Y + half));
                    ctx.LineTo(new Point(center.X - half, center.Y));
                    ctx.EndFigure(true);
                }

                context.DrawGeometry(marker.Fill, pen, geometry);
                break;
            }

            case MarkerShape.Triangle:
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(new Point(center.X, center.Y - half), true);
                    ctx.LineTo(new Point(center.X + half, center.Y + half));
                    ctx.LineTo(new Point(center.X - half, center.Y + half));
                    ctx.EndFigure(true);
                }

                context.DrawGeometry(marker.Fill, pen, geometry);
                break;
            }

            case MarkerShape.Pin:
            default:
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    var tipY = center.Y + size * 0.95;
                    ctx.BeginFigure(new Point(center.X, tipY), true);
                    ctx.LineTo(new Point(center.X - half * 0.78, center.Y));
                    ctx.LineTo(new Point(center.X, center.Y - half));
                    ctx.LineTo(new Point(center.X + half * 0.78, center.Y));
                    ctx.EndFigure(true);
                }

                context.DrawGeometry(marker.Fill, pen, geometry);
                context.DrawEllipse(Brushes.White, null, new Point(center.X, center.Y), half * 0.34, half * 0.34);
                break;
            }
        }
    }

    private void DrawMarkerLabel(DrawingContext context, OrbitCamera camera, GlobeMarker marker,
        double width, double height)
    {
        var radius = GeoMath.AltitudeToRadius(marker.Position.Altitude);
        var world = GeoMath.LonLatToUnitVector(marker.Position.Longitude, marker.Position.Latitude) * radius;

        if (!camera.IsFlat && GeoMath.IsSegmentOccluded(camera.Position, world, 1f))
            return;
        if (!camera.ProjectGeo(marker.Position, width, height, out var sx, out var sy, out var depth))
            return;
        if (depth is < -1f or > 1f)
            return;
        if (IsOutsideFlatViewport(camera, sx, sy, width, height))
            return;

        var anchor = new Point(sx + marker.Size * 0.75, sy - marker.Size * 0.75);

        // 图片标记的文字贴在图标右外侧，避免压在图案上
        for (var i = _markerHits.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_markerHits[i].Marker, marker))
                continue;
            var bounds = _markerHits[i].Bounds;
            anchor = new Point(bounds.Right + 3d, bounds.Top + 1d);
            break;
        }

        DrawTextWithShadow(context, marker.Label!, anchor, marker.FontSize, marker.LabelForeground,
            DefaultTypeface, centerAligned: false, allowOverlapCheck: true);
    }

    /// <summary>绘制鼠标接近时的标记高亮圈与鼠标旁的提示框。</summary>
    private void DrawHover(DrawingContext context, GlobeMarker marker, double width, double height)
    {
        if (!marker.ShowHoverTip)
            return;

        Rect bounds = default;
        Point center = default;
        var found = false;
        for (var i = _markerHits.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_markerHits[i].Marker, marker))
                continue;
            bounds = _markerHits[i].Bounds;
            center = _markerHits[i].Center;
            found = true;
            break;
        }

        if (!found)
            return;

        var accent = new SolidColorBrush(Color.FromArgb(255, 90, 200, 255));

        // 高亮圈：告诉用户当前提示的是哪个点
        var radius = Math.Max(bounds.Width, bounds.Height) * 0.5d + 4d;
        context.DrawEllipse(null, new Pen(accent, 1.6d), center, radius, radius);

        // 提示内容回退顺序：HoverTip -> Label -> ToolTip
        var text = marker.HoverTip;
        if (string.IsNullOrEmpty(text))
            text = marker.Label;
        if (string.IsNullOrEmpty(text))
            text = marker.ToolTip;
        if (string.IsNullOrEmpty(text))
            return;

        const double maxTextWidth = 260d;
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            DefaultTypeface, 12.5d, marker.LabelForeground)
        {
            MaxTextWidth = maxTextWidth
        };

        const double pad = 8d;
        var boxWidth = Math.Min(formatted.Width, maxTextWidth) + pad * 2d;
        var boxHeight = formatted.Height + pad * 2d;

        var anchorPoint = PointerPosition ?? center;
        var x = anchorPoint.X + 16d;
        if (x + boxWidth > width - 4d)
            x = anchorPoint.X - 16d - boxWidth;
        var y = anchorPoint.Y + 18d;
        if (y + boxHeight > height - 4d)
            y = anchorPoint.Y - 18d - boxHeight;

        x = Math.Clamp(x, 4d, Math.Max(4d, width - boxWidth - 4d));
        y = Math.Clamp(y, 4d, Math.Max(4d, height - boxHeight - 4d));

        var box = new Rect(x, y, boxWidth, boxHeight);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(226, 14, 20, 30)), new Pen(accent, 1d), box, 6d, 6d);
        context.DrawText(formatted, new Point(x + pad, y + pad));
    }

    #endregion

    #region 文字标注

    private void DrawLabel(DrawingContext context, OrbitCamera camera, GlobeLabel label,
        double width, double height)
    {
        if (!label.IsVisible || string.IsNullOrEmpty(label.Text))
            return;

        var radius = GeoMath.AltitudeToRadius(label.Position.Altitude);
        var world = GeoMath.LonLatToUnitVector(label.Position.Longitude, label.Position.Latitude) * radius;

        if (!camera.IsFlat && GeoMath.IsSegmentOccluded(camera.Position, world, 1f))
            return;
        if (!camera.ProjectGeo(label.Position, width, height, out var sx, out var sy, out var depth))
            return;
        if (depth is < -1f or > 1f)
            return;
        if (IsOutsideFlatViewport(camera, sx, sy, width, height))
            return;

        DrawTextWithShadow(context, label.Text, new Point(sx, sy), label.FontSize, label.Foreground,
            DefaultTypeface, centerAligned: true, allowOverlapCheck: true, background: label.Background,
            showShadow: label.ShowShadow);
    }

    private void DrawTextWithShadow(DrawingContext context, string text, Point anchor, double fontSize,
        IBrush foreground, Typeface typeface, bool centerAligned, bool allowOverlapCheck = false,
        IBrush? background = null, bool showShadow = true)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, fontSize, foreground);

        var origin = centerAligned
            ? new Point(anchor.X - formatted.Width * 0.5, anchor.Y - formatted.Height * 0.5)
            : anchor;

        var rect = new Rect(origin.X - LabelPadding, origin.Y - LabelPadding,
            formatted.Width + LabelPadding * 2, formatted.Height + LabelPadding * 2);

        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        if (allowOverlapCheck && AvoidLabelOverlap)
        {
            foreach (var existing in _labelRects)
            {
                if (existing.Intersects(rect))
                    return;
            }
        }

        _labelRects.Add(rect);

        if (background != null)
            context.DrawRectangle(background, null, rect);

        if (showShadow)
        {
            var shadow = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, fontSize, Brushes.Black);
            context.DrawText(shadow, new Point(origin.X + 1.2, origin.Y + 1.2));
        }

        context.DrawText(formatted, origin);
    }

    #endregion

    #region 命中测试

    /// <summary>
    /// 命中测试几何：每次都用当前相机重新投影并做地平线遮挡剔除，再按图标实际尺寸算包围盒。
    /// 不用上一帧缓存的矩形，避免刚旋到球体背面、或刚缩放后仍能“点到”标记点。
    /// </summary>
    private bool TryGetHitBounds(GlobeMarker marker, out Rect bounds, out Point center)
    {
        bounds = default;
        center = default;

        var camera = Camera;
        if (camera == null || !marker.IsVisible)
            return false;

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 1d || height <= 1d)
            return false;

        var world = GeoMath.LonLatToUnitVector(marker.Position.Longitude, marker.Position.Latitude) *
                    GeoMath.AltitudeToRadius(marker.Position.Altitude);

        // 地平线遮挡剔除：背面目标投影后可能仍落在图标附近，必须按相机重新判定可见性。
        // 平面模式没有球体遮挡，且直接用经纬度投影可避开 ±180° 端点经三维向量往返的浮点翻转。
        if (!camera.IsFlat && !camera.IsVisible(world))
            return false;

        if (!camera.ProjectGeo(marker.Position, width, height, out var sx, out var sy, out var depth))
            return false;
        if (!camera.IsFlat && depth is < -1f or > 1f)
            return false;
        if (IsOutsideFlatViewport(camera, sx, sy, width, height))
            return false;

        center = new Point(sx, sy);
        bounds = marker.ResolveImage() is { } image
            ? GetImageRect(marker, image, center)
            : GetShapeRect(marker, center);
        return true;
    }

    /// <summary>命中测试标记点（图标包围盒内），返回最上层的命中项。</summary>
    public GlobeMarker? HitTestMarker(Point position)
    {
        for (var i = _markerHits.Count - 1; i >= 0; i--)
        {
            if (TryGetHitBounds(_markerHits[i].Marker, out var bounds, out _) && bounds.Contains(position))
                return _markerHits[i].Marker;
        }

        return null;
    }

    /// <summary>
    /// 查找离屏幕坐标最近的标记点：距离按到图标包围盒的距离计算（落在盒内为 0），
    /// 超过 maxDistance 像素则忽略；等距时取更上层的一个。球体背面的标记不参与。
    /// </summary>
    /// <param name="position">屏幕坐标。</param>
    /// <param name="maxDistance">最大搜索距离（像素）。</param>
    /// <param name="requireHoverTip">true 时只考虑开启了接近提示的标记点。</param>
    public GlobeMarker? FindNearestMarker(Point position, double maxDistance, bool requireHoverTip = false)
    {
        GlobeMarker? best = null;
        var bestDistance = double.MaxValue;

        for (var i = _markerHits.Count - 1; i >= 0; i--)
        {
            var marker = _markerHits[i].Marker;
            if (requireHoverTip && !marker.ShowHoverTip)
                continue;

            // 每个标记点可以单独设置接近半径，未设置时用默认值
            var limit = requireHoverTip && marker.HoverRadius > 0d
                ? marker.HoverRadius
                : maxDistance;

            if (!TryGetHitBounds(marker, out var bounds, out _))
                continue;

            var distance = DistanceToRect(bounds, position);
            if (distance > limit || distance >= bestDistance)
                continue;

            bestDistance = distance;
            best = marker;
        }

        return best;
    }

    /// <summary>平面模式下屏幕坐标超出视口（含 64px 余量）时不再绘制，避免超大坐标进入 Skia。</summary>
    private static bool IsOutsideFlatViewport(OrbitCamera camera, double screenX, double screenY,
        double width, double height) =>
        camera.IsFlat && (screenX < -64d || screenX > width + 64d || screenY < -64d || screenY > height + 64d);

    /// <summary>点到矩形的最近距离（点在矩形内为 0）。</summary>
    private static double DistanceToRect(in Rect rect, Point point)
    {
        var dx = Math.Max(Math.Max(rect.Left - point.X, 0d), point.X - rect.Right);
        var dy = Math.Max(Math.Max(rect.Top - point.Y, 0d), point.Y - rect.Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>查找离屏幕坐标最近的标记点（不限距离），并通过 <paramref name="distance"/> 返回距离。</summary>
    public GlobeMarker? FindNearestMarker(Point position, out double distance)
    {
        GlobeMarker? best = null;
        var bestDistance = double.MaxValue;

        for (var i = _markerHits.Count - 1; i >= 0; i--)
        {
            var marker = _markerHits[i].Marker;
            if (!TryGetHitBounds(marker, out var bounds, out _))
                continue;

            var candidate = DistanceToRect(bounds, position);
            if (candidate >= bestDistance)
                continue;

            bestDistance = candidate;
            best = marker;
        }

        distance = best == null ? double.MaxValue : bestDistance;
        return best;
    }

    #endregion
}
