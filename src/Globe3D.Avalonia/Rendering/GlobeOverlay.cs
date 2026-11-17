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

    private readonly List<Rect> _labelRects = new();
    private readonly List<(GlobeMarker Marker, Rect Bounds, Point Center)> _markerHits = new();

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

    public override void Render(DrawingContext context)
    {
        _labelRects.Clear();
        _markerHits.Clear();

        var camera = Camera;
        if (camera == null)
            return;

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 1 || height <= 1 || Layers.Count == 0)
            return;

        // 先画区域，再画标记，最后画文字
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
            foreach (var marker in layer.Markers)
                DrawMarker(context, camera, marker, width, height);
        }

        if (!ShowLabels)
            return;

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

        // 悬停高亮与提示框画在标注之上，保证不被其他元素遮挡
        if (HoverMarker is { } hover)
            DrawHover(context, hover, width, height);
    }

    #region 区域

    private void DrawRegion(DrawingContext context, OrbitCamera camera, GlobeRegion region,
        double width, double height)
    {
        if (!region.IsVisible || region.Points.Count < 3)
            return;
        // 区域顶点全部在球面上按大圆插值展开：三维坐标天然没有 ±180° 经线的接缝
        var ring = new List<Vec3>(region.Points.Count * 16);
        for (var i = 0; i < region.Points.Count; i++)
        {
            var start = region.Points[i];
            var end = region.Points[(i + 1) % region.Points.Count];
            AppendArc(start, end, ring);
        }

        if (ring.Count < 3)
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

        var pen = region.Stroke != null
            ? new Pen(region.Stroke, region.StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round)
            : null;

        // 填充：先把三维环裁剪到相机可见半球再投影。
        // 这样跨 ±180° 经线、以及一部分隐到地平线后面的区域都能得到正确的闭合多边形，
        // 裁剪边恰好落在球体轮廓上，不会出现“整个地球被填满”或“无填充”的情况。
        List<Point>? fillPoints = null;
        if (region.Fill != null)
        {
            fillPoints = BuildFillPolygon(ring, camera, width, height);
            if (fillPoints != null)
            {
                var geometry = BuildGeometry(fillPoints, closed: true);
                if (geometry != null)
                    context.DrawGeometry(region.Fill, null, geometry);
            }
        }

        if (pen != null)
        {
            if (allVisible)
                DrawPolyline(context, pen, projected, closed: true);
            else
                DrawVisibleSegments(context, pen, projected, visible);
        }

        if (!string.IsNullOrEmpty(region.Label))
        {
            var center = ComputeCentroid(fillPoints) ?? ComputeVisibleCentroid(projected, visible);
            if (center is { } point)
                DrawTextWithShadow(context, region.Label, point, 13d, Brushes.White, BoldTypeface, centerAligned: true);
        }
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

    private static void DrawVisibleSegments(DrawingContext context, IPen pen, List<Point> points, List<bool> visible)
    {
        for (var i = 0; i < points.Count; i++)
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

    #region 标记点

    private void DrawMarker(DrawingContext context, OrbitCamera camera, GlobeMarker marker,
        double width, double height)
    {
        if (!marker.IsVisible)
            return;

        var radius = GeoMath.AltitudeToRadius(marker.Position.Altitude);
        var surface = GeoMath.LonLatToUnitVector(marker.Position.Longitude, marker.Position.Latitude);
        var world = surface * radius;

        if (GeoMath.IsSegmentOccluded(camera.Position, world, 1f))
            return;

        if (!camera.Project(world, width, height, out var sx, out var sy, out var depth))
            return;
        if (depth is < -1f or > 1f)
            return;

        var center = new Point(sx, sy);
        var size = Math.Max(4d, marker.Size);
        var pen = new Pen(marker.Stroke, Math.Max(1d, size * 0.16));

        // 高度引线
        if (marker.ShowAltitudeLine && marker.Position.Altitude > 1d)
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

        if (GeoMath.IsSegmentOccluded(camera.Position, world, 1f))
            return;
        if (!camera.Project(world, width, height, out var sx, out var sy, out var depth))
            return;
        if (depth is < -1f or > 1f)
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

        if (GeoMath.IsSegmentOccluded(camera.Position, world, 1f))
            return;
        if (!camera.Project(world, width, height, out var sx, out var sy, out var depth))
            return;
        if (depth is < -1f or > 1f)
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

        // 地平线遮挡剔除：背面目标投影后可能仍落在图标附近，必须按相机重新判定可见性
        if (!camera.IsVisible(world))
            return false;

        if (!camera.Project(world, width, height, out var sx, out var sy, out var depth))
            return false;
        if (depth is < -1f or > 1f)
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
