namespace Globe3D.Avalonia;

/// <summary>内置 GLSL ES 3.00 着色器源码（桌面 GL 由 GlProgram 自动适配为 330 core）。</summary>
internal static class GlobeShaders
{
    /// <summary>地球瓦片顶点着色器：把墨卡托参数网格映射到球面。</summary>
    public const string EarthVertex = @"#version 300 es
precision highp float;

layout(location = 0) in vec2 aParam;

uniform mat4 uViewProj;
uniform vec4 uTileMerc;      // left, top, right, bottom in normalized mercator
uniform vec2 uUvScale;
uniform vec2 uUvOffset;
uniform vec2 uPolePin;       // x: 顶边吸附北极, y: 底边吸附南极（0/1）
uniform float uRadius;

out vec2 vUv;
out vec3 vNormal;
out vec3 vWorld;

const float PI = 3.141592653589793;

void main()
{
    float mx = mix(uTileMerc.x, uTileMerc.z, aParam.x);
    float my = mix(uTileMerc.y, uTileMerc.w, aParam.y);
    float lat = atan(sinh(PI * (1.0 - 2.0 * my)));

    // 墨卡托纬度截断在 ±85.05°，最北/最南一行把到极点的空隙均匀摊到整行上，
    // 否则正对极点俯视时中央会露出一块没有几何的圆盘状空洞。
    // 85° 以上本就没有瓦片影像，这部分的采样 u 向瓦片中线坍缩，
    // 避免整行纹理被拉成一圈放射状星芒；85° 以下保持原采样，不影响真实影像。
    vec2 uv = uUvOffset + aParam * uUvScale;
    float poleMix = 0.0;
    float poleSign = 0.0;
    if (uPolePin.x > 0.5)
    {
        lat = mix(1.5707963, lat, aParam.y);
        poleMix = smoothstep(1.48353, 1.48702, lat);
        poleSign = 1.0;
    }
    else if (uPolePin.y > 0.5)
    {
        lat = mix(-1.5707963, lat, 1.0 - aParam.y);
        poleMix = smoothstep(1.48353, 1.48702, -lat);
        poleSign = -1.0;
    }

    uv.x = mix(uv.x, uUvOffset.x + 0.5 * uUvScale.x, poleMix);

    float lon = mx * 2.0 * PI - PI;
    float cl = cos(lat);
    vec3 geoNormal = vec3(cl * sin(lon), sin(lat), cl * cos(lon));

    // 极点处的补洞区域统一用极轴法线着色，避免整块区域被逐面片光照出多边形色块；
    // 几何位置仍用真实球面法线，不会把网格捏变形
    vec3 shadeNormal = geoNormal;
    if (poleSign != 0.0)
        shadeNormal = normalize(mix(geoNormal, vec3(0.0, poleSign, 0.0), poleMix));

    vNormal = shadeNormal;
    vWorld = geoNormal * uRadius;
    vUv = uv;

    gl_Position = uViewProj * vec4(vWorld, 1.0);
}
";

    /// <summary>地球瓦片片元着色器：纹理采样 + 轻量明暗。</summary>
    public const string EarthFragment = @"#version 300 es
precision highp float;
precision highp sampler2D;

in vec2 vUv;
in vec3 vNormal;
in vec3 vWorld;

uniform sampler2D uTexture;
uniform float uHasTexture;
uniform vec3 uFallbackColor;
uniform vec3 uCameraPos;
uniform vec3 uLightDir;
uniform float uAmbient;
uniform float uLighting;
uniform float uAtmosphereStrength;
uniform vec3 uAtmosphereColor;

out vec4 fragColor;

const float PI = 3.141592653589793;

void main()
{
    vec3 base = uHasTexture > 0.5 ? texture(uTexture, vUv).rgb : uFallbackColor;

    vec3 n = normalize(vNormal);
    vec3 v = normalize(uCameraPos - vWorld);

    // Soft directional light for volume
    float ndl = max(dot(n, normalize(uLightDir)), 0.0);
    float shade = mix(1.0, uAmbient + (1.0 - uAmbient) * ndl, uLighting);
    base *= shade;

    // Slight atmospheric scattering near the rim
    float rim = pow(1.0 - max(dot(n, v), 0.0), 3.5);
    base += uAtmosphereColor * (rim * uAtmosphereStrength);

    fragColor = vec4(base, 1.0);
}
";

    /// <summary>星空顶点着色器：全屏三角形。</summary>
    public const string StarVertex = @"#version 300 es
precision highp float;

layout(location = 0) in vec2 aPos;

out vec2 vNdc;

void main()
{
    vNdc = aPos;
    gl_Position = vec4(aPos, 0.0, 1.0);
}
";

    /// <summary>星空片元着色器：基于视线方向的程序化多层星点。</summary>
    public const string StarFragment = @"#version 300 es
precision highp float;

in vec2 vNdc;

uniform vec3 uCameraPos;
uniform vec3 uRight;
uniform vec3 uUp;
uniform vec3 uForward;
uniform float uTanHalfFov;
uniform float uAspect;
uniform float uStarIntensity;
uniform float uTime;
uniform vec3 uDeepSpaceColor;

out vec4 fragColor;

float hash13(vec3 p3)
{
    p3 = fract(p3 * 0.1031);
    p3 += dot(p3, p3.zyx + 31.32);
    return fract((p3.x + p3.y) * p3.z);
}

vec3 hash33(vec3 p3)
{
    p3 = fract(p3 * vec3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yxz + 33.33);
    return fract((p3.xxy + p3.yxx) * p3.zyx);
}

vec3 starLayer(vec3 dir, float scale, float density, float sizeScale)
{
    vec3 p = dir * scale;
    vec3 cell = floor(p);
    vec3 f = p - cell;

    float h = hash13(cell);
    if (h <= density)
        return vec3(0.0);

    vec3 sp = hash33(cell + 7.13);
    float d = length(f - sp);
    float radius = (0.07 + 0.11 * hash13(cell + 3.71)) * sizeScale;
    float b = 1.0 - smoothstep(0.0, radius, d);
    b *= b * b;

    float temp = hash13(cell + 19.73);
    vec3 tint = mix(vec3(0.70, 0.81, 1.0), vec3(1.0, 0.90, 0.74), temp);

    // Subtle twinkle for a few stars
    float twinkle = 0.82 + 0.18 * sin(uTime * 1.7 + h * 91.0);

    return tint * (b * (0.30 + 0.70 * h) * twinkle);
}

void main()
{
    vec3 dir = normalize(uForward
        + uRight * (vNdc.x * uTanHalfFov * uAspect)
        + uUp * (vNdc.y * uTanHalfFov));

    vec3 color = uDeepSpaceColor;

    // Milky way band: low frequency density boost along a tilted great circle
    vec3 bandNormal = normalize(vec3(0.42, 0.78, -0.46));
    float band = exp(-pow(dot(dir, bandNormal) * 3.2, 2.0));
    float bandNoise = hash13(floor(dir * 48.0) + 5.0);
    color += vec3(0.045, 0.052, 0.075) * band * (0.55 + 0.45 * bandNoise);

    color += starLayer(dir, 26.0, 0.66, 1.35);
    color += starLayer(dir, 58.0, 0.84, 1.0) * 0.85;
    color += starLayer(dir, 132.0, 0.92, 0.75) * 0.7;

    fragColor = vec4(color * uStarIntensity, 1.0);
}
";

    /// <summary>大气/辅助球体顶点着色器：标准经纬参数球。</summary>
    public const string SphereVertex = @"#version 300 es
precision highp float;

layout(location = 0) in vec2 aParam;

uniform mat4 uViewProj;
uniform float uRadius;

out vec3 vNormal;
out vec3 vWorld;

const float PI = 3.141592653589793;

void main()
{
    float lon = aParam.x * 2.0 * PI - PI;
    float lat = (aParam.y - 0.5) * PI;
    float cl = cos(lat);
    vec3 n = vec3(cl * sin(lon), sin(lat), cl * cos(lon));

    vNormal = n;
    vWorld = n * uRadius;
    gl_Position = uViewProj * vec4(vWorld, 1.0);
}
";

    /// <summary>大气辉光片元着色器：边缘菲涅尔，配合加色混合。</summary>
    public const string AtmosphereFragment = @"#version 300 es
precision highp float;

in vec3 vNormal;
in vec3 vWorld;

uniform vec3 uCameraPos;
uniform vec3 uColor;
uniform float uIntensity;
uniform float uPower;

out vec4 fragColor;

void main()
{
    vec3 n = normalize(vNormal);
    vec3 v = normalize(uCameraPos - vWorld);
    float rim = 1.0 - max(dot(n, v), 0.0);
    float a = pow(rim, uPower) * uIntensity;
    fragColor = vec4(uColor * a, a);
}
";
}
