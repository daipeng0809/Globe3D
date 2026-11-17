using Avalonia;
using Avalonia.Input;

namespace Globe3D.Avalonia;

/// <summary>
/// 测量工具与比例尺：距离、多边形面积、圆形面积、方形面积。
/// 交互约定：左键单击依次落点；距离/多边形双击或右键完成，圆形/方形第二次单击完成；
/// 右键在点数不足时取消当前草稿，Esc 取消当前草稿。
/// </summary>
public partial class GlobeView
{
    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);

    private readonly List<GlobeMeasurement> _measurements = new();
    private readonly GlobeMeasurementDraft _measurementDraft = new();
    private Cursor? _toolBaseCursor;
    private bool _toolCursorApplied;
    private bool _suppressNextToolClick;

    /// <summary>已完成的测量结果（按完成顺序排列）。</summary>
    public IReadOnlyList<GlobeMeasurement> Measurements => _measurements;

    /// <summary>一次测量完成时触发。</summary>
    public event EventHandler<GlobeMeasurementEventArgs>? MeasurementCompleted;

    /// <summary>当前是否已有尚未完成的测量点。</summary>
    public bool IsMeasureInProgress => _measurementDraft.Points.Count > 0;

    /// <summary>清除全部已完成的测量结果与当前草稿。</summary>
    public void ClearMeasurements()
    {
        if (_measurements.Count == 0 && _measurementDraft.Points.Count == 0)
            return;

        _measurements.Clear();
        ResetMeasurementDraft();
        _overlay.InvalidateVisual();
    }

    /// <summary>放弃当前测量草稿（已完成的测量结果保留）。</summary>
    public void CancelMeasurement()
    {
        if (_measurementDraft.Points.Count == 0)
            return;

        ResetMeasurementDraft();
        _overlay.InvalidateVisual();
    }

    /// <summary>
    /// 直接添加一个测量结果（不需要鼠标交互）。圆形传入 [圆心, 圆周点]，方形传入 [中心, 边界点]，
    /// 距离至少两个点，多边形至少三个点。点数不足或包含非有限坐标时返回 null。
    /// 该方法是纯追加操作，不会清除当前鼠标交互中的草稿。
    /// </summary>
    public GlobeMeasurement? AddMeasurement(GlobeTool tool, params GeoPoint[] points) =>
        CreateMeasurement(tool, points, fromDraft: false);

    private GlobeMeasurement? CreateMeasurement(GlobeTool tool, IReadOnlyList<GeoPoint> points, bool fromDraft)
    {
        var required = tool switch
        {
            GlobeTool.Distance => 2,
            GlobeTool.AreaPolygon => 3,
            GlobeTool.AreaCircle or GlobeTool.AreaSquare => 2,
            _ => int.MaxValue
        };

        if (points.Count < required)
            return null;

        foreach (var point in points)
        {
            if (!Geodesy.IsFinite(point))
                return null;
        }

        if (tool is GlobeTool.AreaCircle or GlobeTool.AreaSquare &&
            Geodesy.Distance(points[0], points[1]) < 0.01d)
            return null;

        var snapshot = points.ToArray();

        // 交互完成时先清草稿再触发事件，保证事件处理器看到的 IsMeasureInProgress 与列表一致
        if (fromDraft)
            ResetMeasurementDraft();

        var measurement = new GlobeMeasurement(tool, snapshot);
        _measurements.Add(measurement);
        _overlay.InvalidateVisual();
        MeasurementCompleted?.Invoke(this, new GlobeMeasurementEventArgs(measurement));
        return measurement;
    }

    #region 工具状态

    private void OnActiveToolChanged()
    {
        _measurementDraft.Tool = ActiveTool;
        _suppressNextToolClick = false;
        ResetMeasurementDraft();

        // 工具激活时收起标记悬停提示，并换成十字光标。
        // 键盘焦点在鼠标按下时获取，避免代码切换工具时抢走宿主输入控件的焦点。
        UpdateHoverMarker(null);
        ApplyToolCursor();
        _overlay.InvalidateVisual();
    }

    private void ApplyToolCursor()
    {
        if (ActiveTool != GlobeTool.None)
        {
            if (!_toolCursorApplied)
            {
                _toolBaseCursor = Cursor;
                _toolCursorApplied = true;
            }

            Cursor = CrossCursor;
        }
        else if (_toolCursorApplied)
        {
            Cursor = _toolBaseCursor;
            _toolCursorApplied = false;
        }
    }

    private void ResetMeasurementDraft()
    {
        _measurementDraft.Points.Clear();
        _measurementDraft.Pointer = null;
        _overlay.MeasurementDraft = ActiveTool == GlobeTool.None ? null : _measurementDraft;
    }

    /// <summary>鼠标移动时更新草稿的预览点（由输入层调用）。</summary>
    internal void UpdateToolPointer(Point position)
    {
        if (ActiveTool == GlobeTool.None)
            return;

        _measurementDraft.Pointer =
            _camera.ScreenToGround(position.X, position.Y, Bounds.Width, Bounds.Height, out var geo)
                ? geo
                : null;
        _overlay.MeasurementDraft = _measurementDraft;
        _overlay.InvalidateVisual();
    }

    /// <summary>鼠标离开控件时清掉预览点，保留已落下的顶点。</summary>
    internal void ClearToolPointer()
    {
        if (ActiveTool == GlobeTool.None || _measurementDraft.Pointer == null)
            return;

        _measurementDraft.Pointer = null;
        _overlay.InvalidateVisual();
    }

    /// <summary>处理工具模式下的一次左键落点。</summary>
    internal void HandleToolClick(GeoPoint location)
    {
        if (ActiveTool == GlobeTool.None)
            return;

        switch (ActiveTool)
        {
            case GlobeTool.AreaCircle:
            case GlobeTool.AreaSquare:
                _measurementDraft.Points.Add(location);
                if (_measurementDraft.Points.Count >= 2)
                {
                    // 圆心/中心与边界点重合等无效情况：丢弃草稿，避免第三次点击继续追加
                    if (!TryCommitMeasurement())
                        ResetMeasurementDraft();
                }
                else
                {
                    _measurementDraft.Pointer = location;
                }
                break;

            default:
                _measurementDraft.Points.Add(location);
                break;
        }

        _overlay.InvalidateVisual();
    }

    /// <summary>右键：距离/多边形尝试完成，圆形/方形或点数不足时取消草稿。</summary>
    internal void HandleToolRightClick()
    {
        if (ActiveTool == GlobeTool.None)
            return;

        if (!TryCommitMeasurement())
            ResetMeasurementDraft();

        _overlay.InvalidateVisual();
    }

    /// <summary>双击结束距离/多边形；点数不足时丢弃草稿（与右键行为一致）。</summary>
    internal void FinishMeasurementDraft()
    {
        if (!TryCommitMeasurement())
            ResetMeasurementDraft();

        _overlay.InvalidateVisual();
    }

    private bool TryCommitMeasurement()
    {
        var tool = _measurementDraft.Tool;
        if (tool == GlobeTool.None || _measurementDraft.Points.Count == 0)
            return false;

        return CreateMeasurement(tool, _measurementDraft.Points.ToArray(), fromDraft: true) != null;
    }

    #endregion
}
