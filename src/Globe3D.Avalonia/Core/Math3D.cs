using System.Runtime.CompilerServices;

namespace Globe3D.Avalonia;

/// <summary>三维向量（单精度）。</summary>
public readonly struct Vec3 : IEquatable<Vec3>
{
    public readonly float X;
    public readonly float Y;
    public readonly float Z;

    public Vec3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static readonly Vec3 Zero = new(0f, 0f, 0f);
    public static readonly Vec3 UnitX = new(1f, 0f, 0f);
    public static readonly Vec3 UnitY = new(0f, 1f, 0f);
    public static readonly Vec3 UnitZ = new(0f, 0f, 1f);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);

    public static Vec3 operator *(Vec3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);

    public static Vec3 operator *(float s, Vec3 a) => new(a.X * s, a.Y * s, a.Z * s);

    public static Vec3 operator /(Vec3 a, float s) => new(a.X / s, a.Y / s, a.Z / s);

    public float LengthSquared => X * X + Y * Y + Z * Z;

    public float Length => MathF.Sqrt(LengthSquared);

    public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vec3 Cross(Vec3 a, Vec3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    public static Vec3 Normalize(Vec3 a)
    {
        var len = a.Length;
        return len > 1e-12f ? a / len : Zero;
    }

    public static Vec3 Lerp(Vec3 a, Vec3 b, float t) => new(
        a.X + (b.X - a.X) * t,
        a.Y + (b.Y - a.Y) * t,
        a.Z + (b.Z - a.Z) * t);

    public bool Equals(Vec3 other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

    public override bool Equals(object? obj) => obj is Vec3 v && Equals(v);

    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
}

/// <summary>
/// 4x4 单精度矩阵，采用与 OpenGL 一致的列主序存储（索引 = 列 * 4 + 行），
/// 可直接以 transpose = false 上传给 GL。
/// </summary>
public struct Mat4
{
    private Float16 _e;

    /// <summary>按列主序索引访问元素。</summary>
    public float this[int index]
    {
        get => _e[index];
        set => _e[index] = value;
    }

    public static Mat4 Identity
    {
        get
        {
            var m = default(Mat4);
            m[0] = 1f;
            m[5] = 1f;
            m[10] = 1f;
            m[15] = 1f;
            return m;
        }
    }

    /// <summary>返回 a * b（先应用 b，再应用 a）。</summary>
    public static Mat4 Multiply(in Mat4 a, in Mat4 b)
    {
        var r = default(Mat4);
        for (var c = 0; c < 4; c++)
        {
            for (var row = 0; row < 4; row++)
            {
                float sum = 0f;
                for (var k = 0; k < 4; k++)
                    sum += a[k * 4 + row] * b[c * 4 + k];
                r[c * 4 + row] = sum;
            }
        }

        return r;
    }

    /// <summary>以 w = 1 变换点。</summary>
    public static void TransformPoint(in Mat4 m, in Vec3 v, out float x, out float y, out float z, out float w)
    {
        x = m[0] * v.X + m[4] * v.Y + m[8] * v.Z + m[12];
        y = m[1] * v.X + m[5] * v.Y + m[9] * v.Z + m[13];
        z = m[2] * v.X + m[6] * v.Y + m[10] * v.Z + m[14];
        w = m[3] * v.X + m[7] * v.Y + m[11] * v.Z + m[15];
    }

    /// <summary>OpenGL 透视投影矩阵（右手系，深度范围 -1..1）。</summary>
    public static Mat4 Perspective(float fovYRadians, float aspect, float near, float far)
    {
        var f = 1f / MathF.Tan(fovYRadians * 0.5f);
        var m = default(Mat4);
        m[0] = f / aspect;
        m[5] = f;
        m[10] = (far + near) / (near - far);
        m[11] = -1f;
        m[14] = 2f * far * near / (near - far);
        return m;
    }

    /// <summary>右手系观察矩阵。</summary>
    public static Mat4 LookAt(Vec3 eye, Vec3 target, Vec3 up)
    {
        var f = Vec3.Normalize(target - eye);
        var s = Vec3.Normalize(Vec3.Cross(f, up));
        if (s.LengthSquared < 1e-12f)
            s = Vec3.Normalize(Vec3.Cross(f, MathF.Abs(f.Y) > 0.99f ? Vec3.UnitZ : Vec3.UnitY));
        var u = Vec3.Cross(s, f);

        var m = default(Mat4);
        m[0] = s.X;
        m[1] = u.X;
        m[2] = -f.X;
        m[4] = s.Y;
        m[5] = u.Y;
        m[6] = -f.Y;
        m[8] = s.Z;
        m[9] = u.Z;
        m[10] = -f.Z;
        m[12] = -Vec3.Dot(s, eye);
        m[13] = -Vec3.Dot(u, eye);
        m[14] = Vec3.Dot(f, eye);
        m[15] = 1f;
        return m;
    }

    /// <summary>绕任意轴旋转。</summary>
    public static Mat4 RotationAxis(Vec3 axis, float radians)
    {
        var a = Vec3.Normalize(axis);
        var c = MathF.Cos(radians);
        var s = MathF.Sin(radians);
        var t = 1f - c;

        var m = Mat4.Identity;
        m[0] = t * a.X * a.X + c;
        m[1] = t * a.X * a.Y + s * a.Z;
        m[2] = t * a.X * a.Z - s * a.Y;
        m[4] = t * a.X * a.Y - s * a.Z;
        m[5] = t * a.Y * a.Y + c;
        m[6] = t * a.Y * a.Z + s * a.X;
        m[8] = t * a.X * a.Z + s * a.Y;
        m[9] = t * a.Y * a.Z - s * a.X;
        m[10] = t * a.Z * a.Z + c;
        return m;
    }

    public void CopyTo(float[] destination)
    {
        for (var i = 0; i < 16; i++)
            destination[i] = _e[i];
    }

    [InlineArray(16)]
    private struct Float16
    {
        private float _element0;
    }
}

/// <summary>常用数值工具。</summary>
public static class MathUtil
{
    public const float DegToRad = MathF.PI / 180f;
    public const float RadToDeg = 180f / MathF.PI;

    public static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;

    public static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>把角度差归一化到 -180..180。</summary>
    public static double NormalizeLongitudeDelta(double delta)
    {
        while (delta > 180d) delta -= 360d;
        while (delta < -180d) delta += 360d;
        return delta;
    }

    public static double NormalizeLongitude(double lon)
    {
        while (lon > 180d) lon -= 360d;
        while (lon < -180d) lon += 360d;
        return lon;
    }

    /// <summary>与帧率无关的指数平滑系数。</summary>
    public static double SmoothFactor(double dt, double halfLifeSeconds) =>
        halfLifeSeconds <= 0 ? 1d : 1d - Math.Pow(0.5d, dt / halfLifeSeconds);
}
