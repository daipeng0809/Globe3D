using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using Silk.NET.OpenGLES;

namespace Globe3D.Avalonia;

/// <summary>
/// 三维地球渲染表面。基于 Avalonia 的 OpenGL 控件，负责瓦片贴图、程序化星空、
/// 大气辉光与按需渲染调度。
/// </summary>
internal sealed partial class GlobeSurface : OpenGlControlBase
{
    private const int TileGridSegments = 8;
    private const int SphereLonSegments = 96;
    private const int SphereLatSegments = 48;

    /// <summary>大气层壳体半径（单位球）：地表 1.0 之上约 92 km。</summary>
    private const float AtmosphereShellRadius = 1.0145f;

    /// <summary>帧统计窗口长度（帧）：平均/最大耗时与刷新率估算只用最近这么多帧。</summary>
    private const int FrameStatsCapacity = 120;

    /// <summary>超过该时长没有渲染新帧就认为进入空闲，帧耗时与刷新率不再当作当前值返回。</summary>
    private const double IdleResetSeconds = 1d;

    private GL? _gl;
    private bool _isOpenGlEs = true;
    private bool _glReady;
    private string? _initError;
    private int _consecutiveErrors;

    private GlProgram? _earthProgram;
    private GlProgram? _flatProgram;
    private GlProgram? _starProgram;
    private GlProgram? _atmosphereProgram;

    private uint _tileVao;
    private uint _tileVbo;
    private uint _tileEbo;
    private int _tileIndexCount;

    private uint _sphereVao;
    private uint _sphereVbo;
    private uint _sphereEbo;
    private int _sphereIndexCount;

    private uint _fullscreenVao;
    private uint _fullscreenVbo;

    private Mat4 _viewProjection = Mat4.Identity;
    private readonly float[] _matrixBuffer = new float[16];
    private double _lastRenderWidth = 1;
    private double _lastRenderHeight = 1;
    private float _pixelWidth = 1;
    private float _pixelHeight = 1;
    private double _renderSeconds;

    private readonly object _frameStatsGate = new();
    private readonly double[] _frameDurations = new double[FrameStatsCapacity];
    private readonly double[] _frameIntervals = new double[FrameStatsCapacity];
    private int _frameStatsIndex;
    private int _frameStatsCount;
    private long _renderedFrames;
    private long _lastFrameTimestamp;
    private double _lastFrameMilliseconds;

    /// <summary>相机。</summary>
    public OrbitCamera Camera { get; } = new();

    /// <summary>渲染统计：本帧绘制的瓦片数。</summary>
    public int DrawnTileCount { get; private set; }

    /// <summary>已完成渲染一帧。</summary>
    public event Action? FrameRendered;

    /// <summary>渲染初始化或绘制出现异常。</summary>
    public event Action<string>? ErrorOccurred;

    #region 外观参数

    /// <summary>背景（深空）颜色。</summary>
    public Color BackgroundColor { get; set; } = Color.FromRgb(3, 5, 12);

    /// <summary>是否绘制星空。</summary>
    public bool ShowStars { get; set; } = true;

    /// <summary>星空整体亮度系数。</summary>
    public float StarIntensity { get; set; } = 1.0f;

    /// <summary>大气辉光颜色。</summary>
    public Color AtmosphereColor { get; set; } = Color.FromRgb(90, 168, 255);

    /// <summary>大气外发光强度。</summary>
    public float AtmosphereIntensity { get; set; } = 0.85f;

    /// <summary>地球表面边缘散射强度。</summary>
    public float SurfaceScattering { get; set; } = 0.12f;

    /// <summary>方向光明暗强度（0 = 纯平，1 = 强立体）。</summary>
    public float LightingIntensity { get; set; } = 0.28f;

    /// <summary>环境光下限。</summary>
    public float AmbientIntensity { get; set; } = 0.82f;

    /// <summary>瓦片缺失时的占位颜色。</summary>
    public Color FallbackColor { get; set; } = Color.FromRgb(12, 26, 44);

    /// <summary>纹理缓存上限（张）。</summary>
    public int MaxTextureCount { get; set; } = 512;

    /// <summary>单帧最多绘制瓦片数。</summary>
    public int MaxTilesPerFrame { get; set; } = 600;

    #endregion

    #region 帧统计

    /// <summary>
    /// 渲染帧统计快照：累计帧数、最近一帧耗时、最近窗口的平均/最大帧耗时，
    /// 以及按窗口中较快一档帧间隔估算的刷新率（采样不足或空闲超过 1 秒时为 0，表示不可用）。
    /// </summary>
    public (long Frames, double LastMilliseconds, double AverageMilliseconds, double MaxMilliseconds, double PeakFps) GetRenderStats()
    {
        lock (_frameStatsGate)
        {
            // 不渲染时环形缓冲里是上一交互期的旧样本：超过 1 秒没有新帧就不再回放，
            // 否则面板会在空闲时一直显示“上一次”的刷新率与帧耗时，和归零的 FPS 自相矛盾
            var idle = _lastFrameTimestamp == 0 ||
                       Stopwatch.GetElapsedTime(_lastFrameTimestamp).TotalSeconds > IdleResetSeconds;
            if (idle || _frameStatsCount == 0)
                return (_renderedFrames, _lastFrameMilliseconds, 0d, 0d, 0d);

            double total = 0d, max = 0d;
            // 帧间隔取 25% 分位（较快一档）：偶发的背靠背帧不会像“最短间隔”那样把刷新率拉到离谱，
            // 而按众数会受“部分帧错过合成时机”的影响，得出低于实际刷新能力的值
            Span<double> intervals = stackalloc double[FrameStatsCapacity];
            var intervalCount = 0;
            for (var i = 0; i < _frameStatsCount; i++)
            {
                total += _frameDurations[i];
                if (_frameDurations[i] > max)
                    max = _frameDurations[i];

                var interval = _frameIntervals[i];
                if (interval is >= 0.5d and <= 100d)
                    intervals[intervalCount++] = interval;
            }

            var peakFps = 0d;
            if (intervalCount >= 8)
            {
                var slice = intervals[..intervalCount];
                slice.Sort();
                var reference = slice[intervalCount / 4];
                if (reference > 0d)
                    peakFps = 1000d / reference;
            }

            return (_renderedFrames, _lastFrameMilliseconds, total / _frameStatsCount, max, peakFps);
        }
    }

    /// <summary>记录一帧的耗时与帧间隔（渲染线程调用）。</summary>
    private void RecordFrameStats(long frameStarted)
    {
        var finished = Stopwatch.GetTimestamp();
        var duration = (finished - frameStarted) * 1000d / Stopwatch.Frequency;

        lock (_frameStatsGate)
        {
            var interval = _lastFrameTimestamp == 0
                ? 0d
                : (frameStarted - _lastFrameTimestamp) * 1000d / Stopwatch.Frequency;
            _lastFrameTimestamp = frameStarted;

            _frameDurations[_frameStatsIndex] = duration;
            _frameIntervals[_frameStatsIndex] = interval;
            _frameStatsIndex = (_frameStatsIndex + 1) % FrameStatsCapacity;
            if (_frameStatsCount < FrameStatsCapacity)
                _frameStatsCount++;
            _lastFrameMilliseconds = duration;
            _renderedFrames++;
        }
    }

    #endregion

    #region 生命周期

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);

        try
        {
            _gl = GL.GetApi(name => gl.GetProcAddress(name));
            _isOpenGlEs = gl.Version?.Contains("ES", StringComparison.OrdinalIgnoreCase) == true;

            var g = _gl;
            g.Enable(EnableCap.DepthTest);
            g.DepthFunc(DepthFunction.Lequal);
            g.Disable(EnableCap.CullFace);
            g.FrontFace(FrontFaceDirection.Ccw);
            g.PixelStore(PixelStoreParameter.UnpackAlignment, 4);

            _earthProgram = new GlProgram(g, GlobeShaders.EarthVertex, GlobeShaders.EarthFragment, "earth", _isOpenGlEs);
            _flatProgram = new GlProgram(g, GlobeShaders.FlatVertex, GlobeShaders.EarthFragment, "flat", _isOpenGlEs);
            _starProgram = new GlProgram(g, GlobeShaders.StarVertex, GlobeShaders.StarFragment, "stars", _isOpenGlEs);
            _atmosphereProgram = new GlProgram(g, GlobeShaders.SphereVertex, GlobeShaders.AtmosphereFragment, "atmosphere", _isOpenGlEs);

            BuildTileMesh(g);
            BuildSphereMesh(g);
            BuildFullscreenTriangle(g);

            _glReady = true;
            _initError = null;
        }
        catch (Exception ex)
        {
            _glReady = false;
            _initError = ex.Message;
            ErrorOccurred?.Invoke("OpenGL 初始化失败：" + ex.Message);
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        try
        {
            DetachLoader();

            if (_gl != null)
            {
                _earthProgram?.Dispose();
                _flatProgram?.Dispose();
                _starProgram?.Dispose();
                _atmosphereProgram?.Dispose();
                _earthProgram = _flatProgram = _starProgram = _atmosphereProgram = null;

                ReleaseAllTextures(_gl);

                DeleteVertexArrayHandle(_gl, _tileVao);
                DeleteBufferHandle(_gl, _tileVbo);
                DeleteBufferHandle(_gl, _tileEbo);
                DeleteVertexArrayHandle(_gl, _sphereVao);
                DeleteBufferHandle(_gl, _sphereVbo);
                DeleteBufferHandle(_gl, _sphereEbo);
                DeleteVertexArrayHandle(_gl, _fullscreenVao);
                DeleteBufferHandle(_gl, _fullscreenVbo);
            }
        }
        catch
        {
            // 上下文销毁阶段允许失败
        }
        finally
        {
            _tileVao = _tileVbo = _tileEbo = 0;
            _sphereVao = _sphereVbo = _sphereEbo = 0;
            _fullscreenVao = _fullscreenVbo = 0;
            _glReady = false;
            _gl = null;
        }

        base.OnOpenGlDeinit(gl);
    }

    protected override void OnOpenGlRender(GlInterface gl, int framebuffer)
    {
        if (!_glReady || _gl == null)
            return;

        var frameStarted = Stopwatch.GetTimestamp();
        var g = _gl!;

        try
        {
            g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)framebuffer);
            MeasureViewport();
            g.Viewport(0, 0, (uint)_pixelWidth, (uint)_pixelHeight);

            // 数据源切换/缓存清理由 UI 线程发起，在这里（渲染线程）应用，避免跨线程改写共享状态
            ApplyPendingTileSource();
            ApplyPendingCacheReset();

            ProcessPendingUploads(g);
            UpdateCamera();

            SelectTiles();

            var background = BackgroundColor;
            g.ClearColor(background.R / 255f, background.G / 255f, background.B / 255f, 1f);
            g.ClearDepth(1f);
            g.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

            if (Camera.IsFlat)
            {
                // 二维平面：不绘制星空/大气，只铺影像瓦片
                RenderFlatGlobe(g);
            }
            else
            {
                if (ShowStars)
                    RenderStarField(g);
                else
                    RenderFallbackSky(g);

                RenderGlobe(g);
                RenderAtmosphere(g);
            }

            g.BindVertexArray(0);
            g.UseProgram(0);
            g.Disable(EnableCap.Blend);
            g.Disable(EnableCap.DepthTest);
            g.DepthMask(true);

            _consecutiveErrors = 0;
            RecordFrameStats(frameStarted);
            _frameCounter++;
            FrameRendered?.Invoke();
        }
        catch (Exception ex)
        {
            _consecutiveErrors++;
            if (_consecutiveErrors <= 3)
                ErrorOccurred?.Invoke("渲染异常：" + ex.Message);
        }
    }

    /// <summary>请求重绘一帧。</summary>
    public void Invalidate()
    {
        if (!_glReady)
            return;

        try
        {
            RequestNextFrameRendering();
        }
        catch
        {
            // 控件已从可视树卸载，渲染线程不再接受请求
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // 不在 UI 线程直接销毁 loader：请求在渲染线程释放（随后 OnOpenGlDeinit 也会兜底）
        SetTileSource(null);
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>初始化失败原因（成功时为 null）。</summary>
    public string? InitializationError => _initError;

    #endregion

    #region 渲染

    private void MeasureViewport()
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var width = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
        var height = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));
        _pixelWidth = width;
        _pixelHeight = height;
        _lastRenderWidth = Bounds.Width;
        _lastRenderHeight = Bounds.Height;
        _renderSeconds += 1d / 60d;
    }

    private void UpdateCamera()
    {
        var aspect = _pixelHeight > 0 ? _pixelWidth / _pixelHeight : 1f;
        Camera.Update(aspect);
        _viewProjection = Camera.ViewProjection;
    }

    private void RenderStarField(GL g)
    {
        var program = _starProgram;
        if (program == null)
            return;

        g.Disable(EnableCap.DepthTest);
        g.DepthMask(false);
        g.Disable(EnableCap.CullFace);

        program.Use();
        program.SetVec3("uCameraPos", Camera.Position);
        program.SetVec3("uRight", Camera.Right);
        program.SetVec3("uUp", Camera.Up);
        program.SetVec3("uForward", Camera.Forward);
        program.SetFloat("uTanHalfFov", MathF.Tan((float)(Camera.FieldOfView * MathUtil.DegToRad) * 0.5f));
        program.SetFloat("uAspect", Camera.Aspect);
        program.SetFloat("uStarIntensity", MathF.Max(0f, StarIntensity));
        program.SetFloat("uTime", (float)_renderSeconds);
        program.SetVec3("uDeepSpaceColor", 0.006f, 0.008f, 0.018f);

        g.BindVertexArray(_fullscreenVao);
        g.DrawArrays(PrimitiveType.Triangles, 0, 3);

        g.DepthMask(true);
    }

    private void RenderFallbackSky(GL g)
    {
        g.Disable(EnableCap.DepthTest);
        g.DepthMask(false);
        g.Clear((uint)ClearBufferMask.ColorBufferBit);
        g.DepthMask(true);
    }

    private void RenderGlobe(GL g)
    {
        var program = _earthProgram;
        if (program == null)
            return;

        g.Enable(EnableCap.DepthTest);
        g.DepthFunc(DepthFunction.Lequal);
        g.DepthMask(true);
        g.Enable(EnableCap.CullFace);
        g.CullFace(TriangleFace.Back);
        g.Disable(EnableCap.Blend);

        program.Use();
        program.SetMatrix("uViewProj", _viewProjection);
        program.SetFloat("uRadius", 1f);
        program.SetVec3("uCameraPos", Camera.Position);
        program.SetInt("uTexture", 0);
        program.SetFloat("uAmbient", MathUtil.Clamp(AmbientIntensity, 0f, 1f));
        program.SetFloat("uLighting", MathUtil.Clamp(LightingIntensity, 0f, 1f));
        program.SetFloat("uAtmosphereStrength", MathF.Max(0f, SurfaceScattering));
        program.SetVec3("uAtmosphereColor",
            AtmosphereColor.R / 255f, AtmosphereColor.G / 255f, AtmosphereColor.B / 255f);

        // 固定方向光，从观察者右上前方照射，保证旋转时立体感稳定
        var light = Vec3.Normalize(Camera.Forward * -1.4f + Camera.Up * 0.55f + Camera.Right * 0.45f);
        program.SetVec3("uLightDir", light);

        g.ActiveTexture(TextureUnit.Texture0);
        g.BindVertexArray(_tileVao);

        var fallback = FallbackColor;
        var drawn = 0;

        foreach (var item in DrawList)
        {
            if (drawn >= MaxTilesPerFrame)
                break;

            program.SetVec4("uTileMerc", item.Left, item.Top, item.Right, item.Bottom);
            program.SetVec2("uPolePin", item.PinNorthPole, item.PinSouthPole);

            if (item.Texture != 0)
            {
                g.BindTexture(TextureTarget.Texture2D, item.Texture);
                program.SetFloat("uHasTexture", 1f);
                program.SetVec2("uUvScale", item.UvScaleX, item.UvScaleY);
                program.SetVec2("uUvOffset", item.UvOffsetX, item.UvOffsetY);
            }
            else
            {
                g.BindTexture(TextureTarget.Texture2D, 0);
                program.SetFloat("uHasTexture", 0f);
                program.SetVec2("uUvScale", 1f, 1f);
                program.SetVec2("uUvOffset", 0f, 0f);
                program.SetVec3("uFallbackColor", fallback.R / 255f, fallback.G / 255f, fallback.B / 255f);
            }

            unsafe
            {
                g.DrawElements(PrimitiveType.Triangles, (uint)_tileIndexCount,
                    DrawElementsType.UnsignedShort, (void*)0);
            }

            drawn++;
        }

        DrawnTileCount = drawn;
        g.Disable(EnableCap.CullFace);
    }

    private void RenderFlatGlobe(GL g)
    {
        var program = _flatProgram;
        if (program == null)
            return;

        g.Disable(EnableCap.DepthTest);
        g.Disable(EnableCap.CullFace);
        g.Disable(EnableCap.Blend);
        g.DepthMask(false);

        program.Use();
        program.SetInt("uTexture", 0);
        program.SetFloat("uAmbient", 1f);
        program.SetFloat("uLighting", 0f);
        program.SetFloat("uAtmosphereStrength", 0f);
        program.SetVec3("uCameraPos", 0f, 0f, 1f);

        var fallback = FallbackColor;
        program.SetVec3("uFallbackColor", fallback.R / 255f, fallback.G / 255f, fallback.B / 255f);

        g.ActiveTexture(TextureUnit.Texture0);
        g.BindVertexArray(_tileVao);

        var scale = Camera.FlatScaleFactor * _pixelHeight;
        var centerX = Camera.FlatCenterX;
        var centerY = Camera.FlatCenterY;
        var halfWidth = Math.Max(1f, _pixelWidth * 0.5f);
        var halfHeight = Math.Max(1f, _pixelHeight * 0.5f);
        var drawn = 0;

        foreach (var item in DrawList)
        {
            if (drawn >= MaxTilesPerFrame)
                break;

            // 用双精度把瓦片屏幕范围直接算成 NDC：避免“世界像素坐标”过大时 float 精度丢失，
            // 深层级放大时瓦片边缘不会抖动或错位
            var left = (float)((item.Left - centerX) * scale / halfWidth);
            var right = (float)((item.Right - centerX) * scale / halfWidth);
            var top = (float)(-(item.Top - centerY) * scale / halfHeight);
            var bottom = (float)(-(item.Bottom - centerY) * scale / halfHeight);
            program.SetVec4("uFlatRect", left, top, right, bottom);

            if (item.Texture != 0)
            {
                g.BindTexture(TextureTarget.Texture2D, item.Texture);
                program.SetFloat("uHasTexture", 1f);
                program.SetVec2("uUvScale", item.UvScaleX, item.UvScaleY);
                program.SetVec2("uUvOffset", item.UvOffsetX, item.UvOffsetY);
            }
            else
            {
                g.BindTexture(TextureTarget.Texture2D, 0);
                program.SetFloat("uHasTexture", 0f);
                program.SetVec2("uUvScale", 1f, 1f);
                program.SetVec2("uUvOffset", 0f, 0f);
            }

            unsafe
            {
                g.DrawElements(PrimitiveType.Triangles, (uint)_tileIndexCount,
                    DrawElementsType.UnsignedShort, (void*)0);
            }

            drawn++;
        }

        DrawnTileCount = drawn;
        g.DepthMask(true);
    }

    private void RenderAtmosphere(GL g)
    {
        var program = _atmosphereProgram;
        if (program == null || AtmosphereIntensity <= 0f)
            return;

        // 相机进入大气层壳体后，壳体背面会整屏叠加，把地表洗成一片蓝（贴地时表现为蓝色图层），
        // 这里根据相机到壳体的距离淡出，贴近地表时彻底关闭。
        var shellRadius = AtmosphereShellRadius;
        var fade = MathUtil.Clamp((float)(Camera.Distance - shellRadius) / 0.02f, 0f, 1f);
        if (fade <= 0f)
            return;

        g.Enable(EnableCap.DepthTest);
        g.DepthFunc(DepthFunction.Lequal);
        g.DepthMask(false);
        g.Enable(EnableCap.CullFace);
        g.CullFace(TriangleFace.Back);
        g.Enable(EnableCap.Blend);
        g.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);

        program.Use();
        program.SetMatrix("uViewProj", _viewProjection);
        program.SetFloat("uRadius", shellRadius);
        program.SetVec3("uCameraPos", Camera.Position);
        program.SetVec3("uColor",
            AtmosphereColor.R / 255f, AtmosphereColor.G / 255f, AtmosphereColor.B / 255f);
        program.SetFloat("uIntensity", MathF.Max(0f, AtmosphereIntensity) * fade);
        program.SetFloat("uPower", 3.2f);

        g.BindVertexArray(_sphereVao);
        unsafe
        {
            g.DrawElements(PrimitiveType.Triangles, (uint)_sphereIndexCount,
                DrawElementsType.UnsignedShort, (void*)0);
        }

        g.Disable(EnableCap.Blend);
        g.Disable(EnableCap.CullFace);
        g.DepthMask(true);
    }

    #endregion

    #region GL 对象辅助

    private static unsafe void DeleteTextureHandle(GL g, uint handle)
    {
        if (handle != 0)
            g.DeleteTextures(1, &handle);
    }

    private static unsafe void DeleteBufferHandle(GL g, uint handle)
    {
        if (handle != 0)
            g.DeleteBuffers(1, &handle);
    }

    private static unsafe void DeleteVertexArrayHandle(GL g, uint handle)
    {
        if (handle != 0)
            g.DeleteVertexArrays(1, &handle);
    }

    #endregion

    #region 网格构建

    private unsafe void BuildTileMesh(GL g)
    {
        const int n = TileGridSegments;
        var vertexCount = (n + 1) * (n + 1);
        var vertices = new float[vertexCount * 2];
        for (var j = 0; j <= n; j++)
        {
            for (var i = 0; i <= n; i++)
            {
                var index = (j * (n + 1) + i) * 2;
                vertices[index] = i / (float)n;
                vertices[index + 1] = j / (float)n;
            }
        }

        var indices = new ushort[n * n * 6];
        var cursor = 0;
        for (var j = 0; j < n; j++)
        {
            for (var i = 0; i < n; i++)
            {
                var i0 = (ushort)(j * (n + 1) + i);
                var i1 = (ushort)(i0 + 1);
                var i2 = (ushort)(i0 + (n + 1));
                var i3 = (ushort)(i2 + 1);

                indices[cursor++] = i0;
                indices[cursor++] = i2;
                indices[cursor++] = i1;
                indices[cursor++] = i1;
                indices[cursor++] = i2;
                indices[cursor++] = i3;
            }
        }

        _tileIndexCount = indices.Length;

        _tileVao = g.GenVertexArrays(1);
        g.BindVertexArray(_tileVao);

        _tileVbo = g.GenBuffers(1);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, _tileVbo);
        fixed (float* ptr = vertices)
            g.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), ptr,
                BufferUsageARB.StaticDraw);

        _tileEbo = g.GenBuffers(1);
        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, _tileEbo);
        fixed (ushort* ptr = indices)
            g.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(ushort)), ptr,
                BufferUsageARB.StaticDraw);

        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);

        g.BindVertexArray(0);
    }

    private unsafe void BuildSphereMesh(GL g)
    {
        const int lonSegments = SphereLonSegments;
        const int latSegments = SphereLatSegments;
        var vertexCount = (lonSegments + 1) * (latSegments + 1);
        var vertices = new float[vertexCount * 2];

        for (var j = 0; j <= latSegments; j++)
        {
            for (var i = 0; i <= lonSegments; i++)
            {
                var index = (j * (lonSegments + 1) + i) * 2;
                vertices[index] = i / (float)lonSegments;
                vertices[index + 1] = j / (float)latSegments;
            }
        }

        var indices = new ushort[lonSegments * latSegments * 6];
        var cursor = 0;
        for (var j = 0; j < latSegments; j++)
        {
            for (var i = 0; i < lonSegments; i++)
            {
                var i0 = (ushort)(j * (lonSegments + 1) + i);
                var i1 = (ushort)(i0 + 1);
                var i2 = (ushort)(i0 + (lonSegments + 1));
                var i3 = (ushort)(i2 + 1);

                indices[cursor++] = i0;
                indices[cursor++] = i2;
                indices[cursor++] = i1;
                indices[cursor++] = i1;
                indices[cursor++] = i2;
                indices[cursor++] = i3;
            }
        }

        _sphereIndexCount = indices.Length;

        _sphereVao = g.GenVertexArrays(1);
        g.BindVertexArray(_sphereVao);

        _sphereVbo = g.GenBuffers(1);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, _sphereVbo);
        fixed (float* ptr = vertices)
            g.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), ptr,
                BufferUsageARB.StaticDraw);

        _sphereEbo = g.GenBuffers(1);
        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, _sphereEbo);
        fixed (ushort* ptr = indices)
            g.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(ushort)), ptr,
                BufferUsageARB.StaticDraw);

        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);

        g.BindVertexArray(0);
    }

    private unsafe void BuildFullscreenTriangle(GL g)
    {
        var vertices = new float[] { -1f, -1f, 3f, -1f, -1f, 3f };

        _fullscreenVao = g.GenVertexArrays(1);
        g.BindVertexArray(_fullscreenVao);

        _fullscreenVbo = g.GenBuffers(1);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, _fullscreenVbo);
        fixed (float* ptr = vertices)
            g.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), ptr,
                BufferUsageARB.StaticDraw);

        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);

        g.BindVertexArray(0);
    }

    #endregion
}
