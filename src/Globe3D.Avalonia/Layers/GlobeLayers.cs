using Avalonia;
using Avalonia.Collections;
using Avalonia.Media;

namespace Globe3D.Avalonia;

/// <summary>标记点形状。</summary>
public enum MarkerShape
{
    Circle,
    Pin,
    Square,
    Diamond,
    Triangle
}

/// <summary>
/// 三维标记点：经纬度 + 海拔高度。高度大于 0 时会离开地表绘制，
/// 并按球体遮挡关系自动隐藏。
/// </summary>
public class GlobeMarker
{
    /// <summary>地理位置（含高度）。</summary>
    public GeoPoint Position { get; set; }

    /// <summary>标记旁显示的文本，为空则不显示。</summary>
    public string? Label { get; set; }

    /// <summary>填充画刷。</summary>
    public IBrush Fill { get; set; } = Brushes.OrangeRed;

    /// <summary>描边画刷。</summary>
    public IBrush Stroke { get; set; } = Brushes.White;

    /// <summary>图标尺寸（像素）。</summary>
    public double Size { get; set; } = 12d;

    /// <summary>形状。</summary>
    public MarkerShape Shape { get; set; } = MarkerShape.Circle;

    /// <summary>标签字体大小。</summary>
    public double FontSize { get; set; } = 12d;

    /// <summary>标签前景色。</summary>
    public IBrush LabelForeground { get; set; } = Brushes.White;

    /// <summary>是否可见。</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>是否绘制从地表到标记点的高度引线（高度大于 0 时有效）。</summary>
    public bool ShowAltitudeLine { get; set; } = true;

    /// <summary>
    /// 自定义图标。设置后替代 <see cref="Shape"/> 绘制，支持任意 Avalonia 图像：
    /// PNG/JPEG/GIF 用 <c>Bitmap</c>（GIF 取首帧），SVG 由内置的 <see cref="SvgMarkerImage"/> 矢量绘制。
    /// 图像以标记点在屏幕上的投影为中心绘制。
    /// </summary>
    public IImage? Image { get; set; }

    /// <summary>
    /// 图标文件路径（png/jpg/jpeg/gif/bmp/webp/svg），首次绘制时按需加载并缓存。
    /// 同时设置了 <see cref="Image"/> 时以 <see cref="Image"/> 为准。
    /// </summary>
    public string? ImagePath
    {
        get => _imagePath;
        set
        {
            if (string.Equals(_imagePath, value, StringComparison.Ordinal))
                return;

            lock (_imageLock)
            {
                _imagePath = value;
                _loadedImage = null;
                _imageLoaded = false;
                ImageError = null;
            }
        }
    }

    /// <summary>图标加载失败的原因（成功时为 null）。</summary>
    public string? ImageError { get; private set; }

    /// <summary>图标缩放倍数（相对图像自身像素尺寸），默认 1。</summary>
    public double ImageScale { get; set; } = 1d;

    /// <summary>图标相对锚点的像素偏移，正值向右/向下。</summary>
    public Vector ImageOffset { get; set; }

    /// <summary>鼠标靠近时是否显示提示（接近判定半径为 <see cref="HoverRadius"/>）。</summary>
    public bool ShowHoverTip { get; set; } = true;

    /// <summary>
    /// 鼠标接近时显示的内容，为空时依次回退到 <see cref="Label"/>、<see cref="ToolTip"/>。
    /// 三者都为空时只高亮标记点，不弹出提示框。
    /// </summary>
    public string? HoverTip { get; set; }

    /// <summary>接近判定半径（像素）；不大于 0 时使用控件的默认值。</summary>
    public double HoverRadius { get; set; }

    /// <summary>用户自定义数据。</summary>
    public object? Tag { get; set; }

    /// <summary>鼠标悬停提示。</summary>
    public string? ToolTip { get; set; }

    private string? _imagePath;
    private IImage? _loadedImage;
    private bool _imageLoaded;
    private readonly object _imageLock = new();

    /// <summary>
    /// 取得实际要绘制的图标：优先使用 <see cref="Image"/>，否则按 <see cref="ImagePath"/> 懒加载。
    /// 加载失败时返回 null，并把原因写入 <see cref="ImageError"/>，不会中断渲染；
    /// 失败只尝试一次，避免缺文件时每帧重复读盘。加载结果按标记点缓存，缩放/平移不会触发重新解码。
    /// </summary>
    public IImage? ResolveImage()
    {
        if (Image != null)
            return Image;

        if (string.IsNullOrWhiteSpace(_imagePath))
            return null;

        lock (_imageLock)
        {
            if (_imageLoaded)
                return _loadedImage;

            _imageLoaded = true;
            try
            {
                _loadedImage = MarkerImage.Load(_imagePath!);
                ImageError = null;
            }
            catch (Exception ex)
            {
                _loadedImage = null;
                ImageError = ex.Message;
            }

            return _loadedImage;
        }
    }
}

/// <summary>区域多边形：按经纬度顺序给出的顶点，绘制时自动做球面插值。</summary>
public class GlobeRegion
{
    /// <summary>顶点集合（建议首尾不重复）。</summary>
    public IList<GeoPoint> Points { get; } = new List<GeoPoint>();

    /// <summary>区域名称。</summary>
    public string? Label { get; set; }

    /// <summary>填充画刷，null 表示不填充。</summary>
    public IBrush? Fill { get; set; }

    /// <summary>描边画刷，null 表示不描边。</summary>
    public IBrush? Stroke { get; set; } = Brushes.OrangeRed;

    /// <summary>描边宽度（像素）。</summary>
    public double StrokeThickness { get; set; } = 2d;

    /// <summary>是否可见。</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>用户自定义数据。</summary>
    public object? Tag { get; set; }

    /// <summary>追加一个经纬度顶点。</summary>
    public GlobeRegion AddPoint(double latitude, double longitude, double altitude = 0d)
    {
        Points.Add(new GeoPoint(latitude, longitude, altitude));
        return this;
    }
}

/// <summary>纯文本标注（不含图标），用于国家/城市/区域名称。</summary>
public class GlobeLabel
{
    public GeoPoint Position { get; set; }

    public string Text { get; set; } = string.Empty;

    public IBrush Foreground { get; set; } = Brushes.White;

    public IBrush? Background { get; set; }

    public double FontSize { get; set; } = 13d;

    public bool IsVisible { get; set; } = true;

    /// <summary>是否绘制文字阴影，提升在影像上的可读性。</summary>
    public bool ShowShadow { get; set; } = true;

    public object? Tag { get; set; }
}

/// <summary>
/// 命名图层。一个图层包含标记点、区域与文字标注三类元素，可以整体显示/隐藏。
/// </summary>
public class GlobeLayer
{
    public GlobeLayer()
    {
    }

    public GlobeLayer(string name)
    {
        Name = name;
    }

    /// <summary>图层名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>图层整体是否可见。</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>标记点集合。</summary>
    public AvaloniaList<GlobeMarker> Markers { get; } = new();

    /// <summary>区域集合。</summary>
    public AvaloniaList<GlobeRegion> Regions { get; } = new();

    /// <summary>文字标注集合。</summary>
    public AvaloniaList<GlobeLabel> Labels { get; } = new();

    public override string ToString() => string.IsNullOrEmpty(Name) ? "Layer" : Name;
}
