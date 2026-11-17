using Silk.NET.OpenGLES;

namespace Globe3D.Avalonia;

/// <summary>对 GL 着色器程序的轻量封装，负责编译、链接与 uniform 缓存。</summary>
internal sealed class GlProgram : IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<string, int> _uniformCache = new(StringComparer.Ordinal);
    private readonly float[] _matrixScratch = new float[16];
    private bool _disposed;

    public GlProgram(GL gl, string vertexSource, string fragmentSource, string debugName, bool isOpenGlEs)
    {
        _gl = gl;
        DebugName = debugName;

        var vs = Compile(ShaderType.VertexShader, AdaptSource(vertexSource, isOpenGlEs));
        var fs = Compile(ShaderType.FragmentShader, AdaptSource(fragmentSource, isOpenGlEs));

        Handle = _gl.CreateProgram();
        _gl.AttachShader(Handle, vs);
        _gl.AttachShader(Handle, fs);
        _gl.LinkProgram(Handle);

        _gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out var linked);
        if (linked == 0)
        {
            var log = _gl.GetProgramInfoLog(Handle);
            throw new InvalidOperationException($"着色器程序 {debugName} 链接失败：{log}");
        }

        _gl.DetachShader(Handle, vs);
        _gl.DetachShader(Handle, fs);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
    }

    public uint Handle { get; }

    public string DebugName { get; }

    public void Use() => _gl.UseProgram(Handle);

    public int Uniform(string name)
    {
        if (_uniformCache.TryGetValue(name, out var cached))
            return cached;

        var location = _gl.GetUniformLocation(Handle, name);
        _uniformCache[name] = location;
        return location;
    }

    public unsafe void SetMatrix(string name, in Mat4 value)
    {
        var location = Uniform(name);
        if (location < 0)
            return;

        value.CopyTo(_matrixScratch);
        fixed (float* pointer = _matrixScratch)
            _gl.UniformMatrix4(location, 1, false, pointer);
    }

    public void SetFloat(string name, float value)
    {
        var location = Uniform(name);
        if (location >= 0)
            _gl.Uniform1(location, value);
    }

    public void SetInt(string name, int value)
    {
        var location = Uniform(name);
        if (location >= 0)
            _gl.Uniform1(location, value);
    }

    public void SetVec2(string name, float x, float y)
    {
        var location = Uniform(name);
        if (location >= 0)
            _gl.Uniform2(location, x, y);
    }

    public void SetVec3(string name, Vec3 value)
    {
        var location = Uniform(name);
        if (location >= 0)
            _gl.Uniform3(location, value.X, value.Y, value.Z);
    }

    public void SetVec3(string name, float x, float y, float z)
    {
        var location = Uniform(name);
        if (location >= 0)
            _gl.Uniform3(location, x, y, z);
    }

    public void SetVec4(string name, float x, float y, float z, float w)
    {
        var location = Uniform(name);
        if (location >= 0)
            _gl.Uniform4(location, x, y, z, w);
    }

    private uint Compile(ShaderType type, string source)
    {
        var shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out var status);
        if (status == 0)
        {
            var log = _gl.GetShaderInfoLog(shader);
            _gl.DeleteShader(shader);
            throw new InvalidOperationException($"着色器 {DebugName}/{type} 编译失败：{log}\n--- 源码 ---\n{source}");
        }

        return shader;
    }

    /// <summary>
    /// 把 GLSL ES 3.00 源码适配为桌面 GLSL 330 core，并保证源码为纯 ASCII。
    /// GL 接口按 ANSI 传递源码，非 ASCII 字符可能被替换并吞掉换行，因此统一抹平。
    /// </summary>
    private static string AdaptSource(string source, bool isOpenGlEs)
    {
        source = SanitizeNonAscii(source);

        if (isOpenGlEs)
            return source;

        var adapted = source.Replace("#version 300 es", "#version 330 core", StringComparison.Ordinal);
        // 桌面 GLSL 不支持 precision 限定符
        adapted = System.Text.RegularExpressions.Regex.Replace(
            adapted, @"^\s*precision\s+(lowp|mediump|highp)\s+\w+\s*;\s*$", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Multiline);
        return adapted;
    }

    private static string SanitizeNonAscii(string text)
    {
        var needsSanitize = false;
        foreach (var ch in text)
        {
            if (ch > 127)
            {
                needsSanitize = true;
                break;
            }
        }

        if (!needsSanitize)
            return text;

        var buffer = text.ToCharArray();
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] > 127)
                buffer[i] = ' ';
        }

        return new string(buffer);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (Handle != 0)
            _gl.DeleteProgram(Handle);
    }
}
