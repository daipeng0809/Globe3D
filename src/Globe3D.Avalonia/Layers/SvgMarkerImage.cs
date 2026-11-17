using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;

namespace Globe3D.Avalonia;

/// <summary>
/// 轻量矢量图标：解析常用 SVG 子集并以矢量方式绘制，任意缩放下边缘依旧清晰。
/// <para>
/// 支持 <c>svg / g / rect / circle / ellipse / line / polyline / polygon / path</c>，
/// 以及 <c>fill / stroke / stroke-width / opacity / fill-opacity / stroke-opacity /
/// fill-rule / transform(translate|scale|rotate|matrix)</c>；渐变按其首个色标近似为纯色。
/// 滤镜、文本、外部引用等高级特性不支持，复杂图标请改用其它 <see cref="IImage"/>（如 Avalonia.Svg.Skia）。
/// </para>
/// </summary>
public sealed class SvgMarkerImage : IImage
{
    private readonly List<SvgShape> _shapes;
    private readonly Rect _viewBox;

    private SvgMarkerImage(List<SvgShape> shapes, Rect viewBox)
    {
        _shapes = shapes;
        _viewBox = viewBox;
        Size = new Size(Math.Max(1d, viewBox.Width), Math.Max(1d, viewBox.Height));
    }

    /// <summary>图像尺寸（SVG 用户单位，通常等于 viewBox 大小）。</summary>
    public Size Size { get; }

#pragma warning disable CS0067 // 静态图标内容不会变化，实现接口要求的事件即可
    /// <inheritdoc />
    public event EventHandler? Invalidated;
#pragma warning restore CS0067

    /// <summary>从 SVG 文件加载。文件不存在或内容非法时抛出异常。</summary>
    public static SvgMarkerImage Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>从 SVG 文本解析。</summary>
    public static SvgMarkerImage Parse(string svg)
    {
        if (string.IsNullOrWhiteSpace(svg))
            throw new ArgumentException("SVG 内容为空。", nameof(svg));

        XDocument document;
        try
        {
            document = XDocument.Parse(svg, LoadOptions.None);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("SVG 解析失败：" + ex.Message, ex);
        }

        var root = document.Root;
        if (root == null || !string.Equals(root.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SVG 根节点必须是 <svg>。");

        var viewBox = ReadViewBox(root);
        var gradients = new Dictionary<string, Color>(StringComparer.Ordinal);
        CollectGradients(root, gradients);

        var shapes = new List<SvgShape>();
        Walk(root, ApplyStyle(root, new SvgStyle(), gradients), shapes, gradients);

        if (shapes.Count == 0)
            throw new InvalidDataException("SVG 中没有可绘制的图形（仅支持 path/rect/circle/ellipse/line/polygon/polyline）。");

        return new SvgMarkerImage(shapes, viewBox);
    }

    /// <inheritdoc />
    public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
    {
        if (_shapes.Count == 0 || _viewBox.Width <= 0d || _viewBox.Height <= 0d)
            return;
        if (destRect.Width <= 0d || destRect.Height <= 0d)
            return;

        var scaleX = destRect.Width / _viewBox.Width;
        var scaleY = destRect.Height / _viewBox.Height;
        var matrix = Matrix.CreateScale(scaleX, scaleY) *
                     Matrix.CreateTranslation(destRect.X - _viewBox.X * scaleX, destRect.Y - _viewBox.Y * scaleY);

        using (context.PushTransform(matrix))
        {
            foreach (var shape in _shapes)
                shape.Draw(context);
        }
    }

    #region 解析

    private static Rect ReadViewBox(XElement root)
    {
        if (TryParseNumbers(Attribute(root, "viewBox"), 4, out var box) && box[2] > 0d && box[3] > 0d)
            return new Rect(box[0], box[1], box[2], box[3]);

        var width = ParseLength(Attribute(root, "width"), 0d);
        var height = ParseLength(Attribute(root, "height"), 0d);
        if (width > 0d && height > 0d)
            return new Rect(0d, 0d, width, height);

        // 没有尺寸信息时按 100×100 处理，保证仍能画出可见图形
        return new Rect(0d, 0d, 100d, 100d);
    }

    private static void CollectGradients(XElement element, Dictionary<string, Color> gradients)
    {
        foreach (var child in element.Elements())
        {
            var name = child.Name.LocalName;
            if (name is "linearGradient" or "radialGradient")
            {
                var id = Attribute(child, "id");
                if (!string.IsNullOrEmpty(id) && !gradients.ContainsKey(id))
                {
                    // 渐变没有直接的纯色，取首个色标做近似，避免图标整块消失
                    var stop = child.Elements().FirstOrDefault(e => e.Name.LocalName == "stop");
                    if (stop != null && TryReadColor(stop, "stop-color", out var color))
                        gradients[id] = color;
                }
            }

            CollectGradients(child, gradients);
        }
    }

    private static void Walk(XElement element, SvgStyle style, List<SvgShape> shapes,
        Dictionary<string, Color> gradients)
    {
        foreach (var child in element.Elements())
        {
            var name = child.Name.LocalName;
            switch (name)
            {
                case "g":
                case "svg":
                case "a":
                    Walk(child, ApplyStyle(child, style.Clone(), gradients), shapes, gradients);
                    break;

                case "defs":
                case "title":
                case "desc":
                case "metadata":
                case "style":
                case "linearGradient":
                case "radialGradient":
                case "clipPath":
                case "mask":
                case "pattern":
                case "filter":
                    break;

                default:
                {
                    var local = ApplyStyle(child, style.Clone(), gradients);
                    var geometry = BuildGeometry(child, name, local.FillRule);
                    if (geometry == null)
                        break;

                    var transform = ParseTransform(Attribute(child, "transform"));
                    if (!transform.IsIdentity)
                        geometry.Transform = new MatrixTransform(transform);

                    var shape = local.ToShape(geometry);
                    if (shape != null)
                        shapes.Add(shape);
                    break;
                }
            }
        }
    }

    private static Geometry? BuildGeometry(XElement element, string name, FillRule fillRule)
    {
        try
        {
            switch (name)
            {
                case "path":
                    var data = Attribute(element, "d");
                    return string.IsNullOrWhiteSpace(data) ? null : Geometry.Parse(data);

                case "rect":
                    return BuildRect(element, fillRule);

                case "circle":
                {
                    var cx = Number(element, "cx");
                    var cy = Number(element, "cy");
                    var r = Number(element, "r");
                    return r > 0d ? BuildEllipse(cx, cy, r, r, fillRule) : null;
                }

                case "ellipse":
                {
                    var cx = Number(element, "cx");
                    var cy = Number(element, "cy");
                    var rx = Number(element, "rx");
                    var ry = Number(element, "ry");
                    return rx > 0d && ry > 0d ? BuildEllipse(cx, cy, rx, ry, fillRule) : null;
                }

                case "line":
                {
                    var x1 = Number(element, "x1");
                    var y1 = Number(element, "y1");
                    var x2 = Number(element, "x2");
                    var y2 = Number(element, "y2");
                    var geometry = new StreamGeometry();
                    using (var ctx = geometry.Open())
                    {
                        ctx.SetFillRule(fillRule);
                        ctx.BeginFigure(new Point(x1, y1), false);
                        ctx.LineTo(new Point(x2, y2));
                        ctx.EndFigure(false);
                    }

                    return geometry;
                }

                case "polyline":
                case "polygon":
                    return BuildPolygon(Attribute(element, "points"), name == "polygon", fillRule);

                default:
                    return null;
            }
        }
        catch (Exception)
        {
            // 单个图元数据写坏时跳过，不影响同一文件里的其它图元
            return null;
        }
    }

    private static Geometry BuildRect(XElement element, FillRule fillRule)
    {
        var x = Number(element, "x");
        var y = Number(element, "y");
        var width = Number(element, "width");
        var height = Number(element, "height");
        if (width <= 0d || height <= 0d)
            return null!;

        var rx = Number(element, "rx");
        var ry = Number(element, "ry");
        if (rx <= 0d && ry <= 0d)
            rx = ry = 0d;
        else if (rx <= 0d)
            rx = ry;
        else if (ry <= 0d)
            ry = rx;

        rx = Math.Min(rx, width * 0.5d);
        ry = Math.Min(ry, height * 0.5d);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.SetFillRule(fillRule);
            if (rx <= 0d || ry <= 0d)
            {
                ctx.BeginFigure(new Point(x, y), true);
                ctx.LineTo(new Point(x + width, y));
                ctx.LineTo(new Point(x + width, y + height));
                ctx.LineTo(new Point(x, y + height));
                ctx.EndFigure(true);
                return geometry;
            }

            var radius = new Size(rx, ry);
            ctx.BeginFigure(new Point(x + rx, y), true);
            ctx.LineTo(new Point(x + width - rx, y));
            ctx.ArcTo(new Point(x + width, y + ry), radius, 0d, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(x + width, y + height - ry));
            ctx.ArcTo(new Point(x + width - rx, y + height), radius, 0d, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(x + rx, y + height));
            ctx.ArcTo(new Point(x, y + height - ry), radius, 0d, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(x, y + ry));
            ctx.ArcTo(new Point(x + rx, y), radius, 0d, false, SweepDirection.Clockwise);
            ctx.EndFigure(true);
        }

        return geometry;
    }

    private static Geometry BuildEllipse(double cx, double cy, double rx, double ry, FillRule fillRule)
    {
        var radius = new Size(rx, ry);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.SetFillRule(fillRule);
            ctx.BeginFigure(new Point(cx + rx, cy), true);
            ctx.ArcTo(new Point(cx - rx, cy), radius, 0d, true, SweepDirection.Clockwise);
            ctx.ArcTo(new Point(cx + rx, cy), radius, 0d, true, SweepDirection.Clockwise);
            ctx.EndFigure(true);
        }

        return geometry;
    }

    private static Geometry? BuildPolygon(string? points, bool closed, FillRule fillRule)
    {
        if (string.IsNullOrWhiteSpace(points) || !TryParseNumbers(points, -1, out var values))
            return null;
        if (values.Length < 4)
            return null;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.SetFillRule(fillRule);
            var count = values.Length - values.Length % 2;
            ctx.BeginFigure(new Point(values[0], values[1]), closed);
            for (var i = 2; i < count; i += 2)
                ctx.LineTo(new Point(values[i], values[i + 1]));
            ctx.EndFigure(closed);
        }

        return geometry;
    }

    #endregion

    #region 样式

    private sealed class SvgStyle
    {
        public Color? Fill { get; set; } = Colors.Black;
        public Color? Stroke { get; set; }
        public double StrokeWidth { get; set; } = 1d;
        public double Opacity { get; set; } = 1d;
        public double FillOpacity { get; set; } = 1d;
        public double StrokeOpacity { get; set; } = 1d;
        public FillRule FillRule { get; set; } = FillRule.NonZero;

        public SvgStyle Clone() => new()
        {
            Fill = Fill,
            Stroke = Stroke,
            StrokeWidth = StrokeWidth,
            Opacity = Opacity,
            FillOpacity = FillOpacity,
            StrokeOpacity = StrokeOpacity,
            FillRule = FillRule
        };

        public SvgShape? ToShape(Geometry geometry)
        {
            IBrush? fill = null;
            if (Fill is { } fillColor)
                fill = new SolidColorBrush(fillColor, fillColor.A / 255d * Opacity * FillOpacity);

            IPen? pen = null;
            if (Stroke is { } strokeColor)
            {
                var brush = new SolidColorBrush(strokeColor, strokeColor.A / 255d * Opacity * StrokeOpacity);
                pen = new Pen(brush, Math.Max(0.001d, StrokeWidth))
                {
                    LineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                };
            }

            if (fill == null && pen == null)
                return null;

            return new SvgShape(geometry, fill, pen);
        }
    }

    private static SvgStyle ApplyStyle(XElement element, SvgStyle style, Dictionary<string, Color> gradients)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in element.Attributes())
            attributes[attribute.Name.LocalName] = attribute.Value;

        // style="fill:#fff;stroke:#000" 的优先级高于同名属性
        var inlineStyle = Attribute(element, "style");
        if (!string.IsNullOrWhiteSpace(inlineStyle))
        {
            foreach (var declaration in inlineStyle.Split(';'))
            {
                var index = declaration.IndexOf(':');
                if (index <= 0)
                    continue;
                var key = declaration[..index].Trim();
                var value = declaration[(index + 1)..].Trim();
                if (key.Length > 0 && value.Length > 0)
                    attributes[key] = value;
            }
        }

        if (attributes.TryGetValue("fill", out var fill))
            style.Fill = ResolvePaint(fill, gradients);
        if (attributes.TryGetValue("stroke", out var stroke))
            style.Stroke = ResolvePaint(stroke, gradients);

        if (attributes.TryGetValue("stroke-width", out var strokeWidth))
            style.StrokeWidth = ParseLength(strokeWidth, style.StrokeWidth);

        if (attributes.TryGetValue("opacity", out var opacity))
            style.Opacity *= Clamp(ParseLength(opacity, 1d), 0d, 1d);
        if (attributes.TryGetValue("fill-opacity", out var fillOpacity))
            style.FillOpacity *= Clamp(ParseLength(fillOpacity, 1d), 0d, 1d);
        if (attributes.TryGetValue("stroke-opacity", out var strokeOpacity))
            style.StrokeOpacity *= Clamp(ParseLength(strokeOpacity, 1d), 0d, 1d);

        if (attributes.TryGetValue("fill-rule", out var fillRule) &&
            string.Equals(fillRule.Trim(), "evenodd", StringComparison.OrdinalIgnoreCase))
            style.FillRule = FillRule.EvenOdd;

        return style;
    }

    private static Color? ResolvePaint(string? value, Dictionary<string, Color> gradients)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Trim();
        if (value.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            var start = value.IndexOf('#');
            var end = value.IndexOf(')', start + 1);
            if (start < 0 || end < 0)
                return null;
            var id = value[(start + 1)..end].Trim();
            return gradients.TryGetValue(id, out var gradientColor) ? gradientColor : null;
        }

        return TryParseColor(value, out var color) ? color : null;
    }

    private static bool TryReadColor(XElement element, string name, out Color color)
    {
        var value = Attribute(element, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            // 有些图标把颜色写在 style 里
            var inlineStyle = Attribute(element, "style");
            if (!string.IsNullOrWhiteSpace(inlineStyle))
            {
                foreach (var declaration in inlineStyle.Split(';'))
                {
                    var index = declaration.IndexOf(':');
                    if (index <= 0)
                        continue;
                    if (string.Equals(declaration[..index].Trim(), name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = declaration[(index + 1)..].Trim();
                        break;
                    }
                }
            }
        }

        return TryParseColor(value, out color);
    }

    private static bool TryParseColor(string? value, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            return false;

        if (value[0] == '#')
        {
            var hex = value[1..];
            switch (hex.Length)
            {
                case 3: // #rgb
                    hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
                    break;
                case 4: // #rgba
                    hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2], hex[3], hex[3]);
                    goto case 8;
                case 8: // SVG/CSS 为 #rrggbbaa，Avalonia 的 #aarrggbb 需要换位
                    if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgba))
                    {
                        color = Color.FromArgb((byte)(rgba & 0xFF), (byte)(rgba >> 24 & 0xFF),
                            (byte)(rgba >> 16 & 0xFF), (byte)(rgba >> 8 & 0xFF));
                        return true;
                    }

                    return false;
                case 6: // #rrggbb
                    if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                    {
                        color = Color.FromRgb((byte)(rgb >> 16 & 0xFF), (byte)(rgb >> 8 & 0xFF), (byte)(rgb & 0xFF));
                        return true;
                    }

                    return false;
            }

            return false;
        }

        var lower = value.ToLowerInvariant();
        if (lower.StartsWith("rgb", StringComparison.Ordinal))
        {
            var open = lower.IndexOf('(');
            var close = lower.IndexOf(')');
            if (open < 0 || close < open)
                return false;

            var parts = lower[(open + 1)..close].Split(new[] { ',', ' ', '/' },
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                return false;

            var r = ParseChannel(parts[0]);
            var g = ParseChannel(parts[1]);
            var b = ParseChannel(parts[2]);
            var a = parts.Length > 3 ? ParseAlpha(parts[3]) : (byte)255;
            color = Color.FromArgb(a, r, g, b);
            return true;
        }

        try
        {
            color = Color.Parse(value);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static byte ParseChannel(string text)
    {
        text = text.Trim();
        if (text.EndsWith('%') && double.TryParse(text[..^1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var percent))
            return (byte)Math.Clamp(percent * 2.55d, 0d, 255d);

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? (byte)Math.Clamp(value, 0d, 255d)
            : (byte)0;
    }

    private static byte ParseAlpha(string text)
    {
        text = text.Trim();
        if (text.EndsWith('%') && double.TryParse(text[..^1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var percent))
            return (byte)Math.Clamp(percent * 2.55d, 0d, 255d);

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? (byte)Math.Clamp(value * 255d, 0d, 255d)
            : (byte)255;
    }

    #endregion

    #region 工具

    private static Matrix ParseTransform(string? text)
    {
        var matrix = Matrix.Identity;
        if (string.IsNullOrWhiteSpace(text))
            return matrix;

        var index = 0;
        while (index < text.Length)
        {
            var open = text.IndexOf('(', index);
            if (open < 0)
                break;
            var close = text.IndexOf(')', open + 1);
            if (close < 0)
                break;

            var name = text[index..open].Trim();
            var body = text[(open + 1)..close];
            index = close + 1;

            if (!TryParseNumbers(body, -1, out var values) || values.Length == 0)
                continue;

            Matrix step;
            switch (name)
            {
                case "translate":
                    step = Matrix.CreateTranslation(values[0], values.Length > 1 ? values[1] : 0d);
                    break;

                case "scale":
                    step = Matrix.CreateScale(values[0], values.Length > 1 ? values[1] : values[0]);
                    break;

                case "rotate":
                {
                    var radians = values[0] * Math.PI / 180d;
                    var rotation = Matrix.CreateRotation(radians);
                    if (values.Length >= 3)
                    {
                        step = Matrix.CreateTranslation(-values[1], -values[2]) *
                               rotation *
                               Matrix.CreateTranslation(values[1], values[2]);
                    }
                    else
                    {
                        step = rotation;
                    }

                    break;
                }

                case "matrix" when values.Length >= 6:
                    step = new Matrix(values[0], values[1], values[2], values[3], values[4], values[5]);
                    break;

                case "skewX":
                    step = new Matrix(1d, 0d, Math.Tan(values[0] * Math.PI / 180d), 1d, 0d, 0d);
                    break;

                case "skewY":
                    step = new Matrix(1d, Math.Tan(values[0] * Math.PI / 180d), 0d, 1d, 0d, 0d);
                    break;

                default:
                    continue;
            }

            matrix *= step;
        }

        return matrix;
    }

    private static string? Attribute(XElement element, string name)
    {
        foreach (var attribute in element.Attributes())
        {
            if (string.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal))
                return attribute.Value;
        }

        return null;
    }

    private static double Number(XElement element, string name) => ParseLength(Attribute(element, name), 0d);

    /// <summary>解析长度：支持 px/pt/pc/mm/cm/in 等单位的绝对值，百分比按 0 处理。</summary>
    private static double ParseLength(string? text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        text = text.Trim();
        if (text.EndsWith('%'))
            return fallback;

        var length = text.Length;
        while (length > 0 && (char.IsLetter(text[length - 1]) || text[length - 1] == '%'))
            length--;

        var numberPart = text[..length];
        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value))
            return fallback;

        var unit = text[length..].ToLowerInvariant();
        return unit switch
        {
            "pt" => value * 4d / 3d,
            "pc" => value * 16d,
            "mm" => value * 96d / 25.4d,
            "cm" => value * 96d / 2.54d,
            "in" => value * 96d,
            _ => value
        };
    }

    private static bool TryParseNumbers(string? text, int expected, out double[] values)
    {
        values = Array.Empty<double>();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (expected > 0 && parts.Length < expected)
            return false;

        var result = new List<double>(parts.Length);
        foreach (var part in parts)
        {
            if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                result.Add(value);
        }

        if (result.Count == 0)
            return false;

        values = result.ToArray();
        return true;
    }

    private static double Clamp(double value, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : max;

    #endregion

    /// <summary>一个已解析好的图元：几何 + 填充 + 描边。</summary>
    private sealed class SvgShape
    {
        private readonly Geometry _geometry;
        private readonly IBrush? _fill;
        private readonly IPen? _stroke;

        public SvgShape(Geometry geometry, IBrush? fill, IPen? stroke)
        {
            _geometry = geometry;
            _fill = fill;
            _stroke = stroke;
        }

        public void Draw(DrawingContext context) => context.DrawGeometry(_fill, _stroke, _geometry);
    }
}
