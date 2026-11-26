using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace OpenFD.MG;

/// <summary>
/// Effet des objets et des personnages (comme BasicEffect : texture, éclairage du jeu 0,62 + 0,55 × |n · soleil|, brouillard)
/// avec une carte de normales (normal map) : texture « &lt;nom&gt;_normal » à côté de la texture de couleur. Le relief de la
/// carte modifie la normale avant l'éclairage. Le repère de la surface (tangente, bitangente) est déduit dans le shader des
/// variations de la position et des uv d'un pixel à l'autre : les maillages restent tels quels.
/// Carte au format « OpenGL » (vert vers le haut de l'image, celui de Blender) ; DDS DXT1/DXT5 ou 32 bits.
/// </summary>
public sealed class NormalMapEffect
{
    public Effect Fx { get; }
    private readonly EffectParameter _wvp, _world, _eye, _fog, _ambient, _sun0, _sun1, _sunDir, _fogColor, _diff, _normal;

    private const string Vs = @"
uniform vec4 vs_uniforms_vec4[10];
uniform vec4 posFixup;
attribute vec4 vs_v0;
attribute vec4 vs_v1;
attribute vec4 vs_v2;
varying vec4 vTex0;
varying vec4 vNormal;
varying vec4 vWorld;
void main()
{
    vec4 p = vec4(vs_v0.xyz, 1.0);
    gl_Position = vec4(dot(p, vs_uniforms_vec4[0]), dot(p, vs_uniforms_vec4[1]), dot(p, vs_uniforms_vec4[2]), dot(p, vs_uniforms_vec4[3]));
    vec4 w = vec4(dot(p, vs_uniforms_vec4[4]), dot(p, vs_uniforms_vec4[5]), dot(p, vs_uniforms_vec4[6]), 1.0);
    float d = length(w.xyz - vs_uniforms_vec4[8].xyz);
    float fog = clamp((d - vs_uniforms_vec4[9].x) / max(1.0, vs_uniforms_vec4[9].y - vs_uniforms_vec4[9].x), 0.0, 1.0);
    vTex0 = vec4(vs_v2.xy, fog, 0.0);
    vWorld = w;
    vNormal = vec4(dot(vs_v1.xyz, vs_uniforms_vec4[4].xyz), dot(vs_v1.xyz, vs_uniforms_vec4[5].xyz), dot(vs_v1.xyz, vs_uniforms_vec4[6].xyz), 0.0);" + GlslEffect.PosFixup + @"
}
";

    private const string Ps = @"
uniform vec4 ps_uniforms_vec4[5];
uniform sampler2D ps_s0;
uniform sampler2D ps_s1;
varying vec4 vTex0;
varying vec4 vNormal;
varying vec4 vWorld;
" + Perturb + @"
void main()
{
    vec4 diff = texture2D(ps_s0, vTex0.xy);
    vec3 n = PerturbNormal(normalize(vNormal.xyz + vec3(0.0, 0.0, 1e-5)), vWorld.xyz, vTex0.xy, texture2D(ps_s1, vTex0.xy).xyz);
    vec3 sun = ps_uniforms_vec4[3].xyz;
    vec3 light = ps_uniforms_vec4[0].rgb + max(dot(n, -sun), 0.0) * ps_uniforms_vec4[1].rgb + max(dot(n, sun), 0.0) * ps_uniforms_vec4[2].rgb;
    gl_FragColor = vec4(mix(diff.rgb * light, ps_uniforms_vec4[4].rgb, vTex0.z), 1.0);
}
";

    /// <summary>
    /// Normale de la carte (texel rgb), passée dans le repère de la surface au point p (monde) d'uv uv, de normale n :
    /// repère déduit des dérivées d'écran (méthode « cotangent frame »). Vert inversé : l'image a son v vers le bas.
    /// </summary>
    public const string Perturb = @"
vec3 PerturbNormal(vec3 n, vec3 p, vec2 uv, vec3 texel)
{
    vec3 m = texel * 2.0 - 1.0;
    m.y = -m.y;
    vec3 dp1 = dFdx(p);
    vec3 dp2 = dFdy(p);
    vec2 duv1 = dFdx(uv);
    vec2 duv2 = dFdy(uv);
    vec3 dp2perp = cross(dp2, n);
    vec3 dp1perp = cross(n, dp1);
    vec3 t = dp2perp * duv1.x + dp1perp * duv2.x;
    vec3 b = dp2perp * duv1.y + dp1perp * duv2.y;
    float len = max(dot(t, t), dot(b, b));
    if (len < 1e-20)
        return n;
    float inv = inversesqrt(len);
    return normalize(mat3(t * inv, b * inv, n) * m);
}
";

    public NormalMapEffect(GraphicsDevice gd)
    {
        Fx = GlslEffect.Create(gd, Vs, Ps, new[]
            {
                new GlslEffect.Param("WorldViewProj", 4, true), new GlslEffect.Param("World", 4, true), new GlslEffect.Param("Eye", 1, true),
                new GlslEffect.Param("Fog", 1, true),
                new GlslEffect.Param("Ambient", 1, false), new GlslEffect.Param("Sun0", 1, false), new GlslEffect.Param("Sun1", 1, false),
                new GlslEffect.Param("SunDir", 1, false), new GlslEffect.Param("FogColor", 1, false),
            },
            new[] { "DiffTex", "NormalTex" },
            new[] { ("vs_v0", VertexElementUsage.Position, 0), ("vs_v1", VertexElementUsage.Normal, 0), ("vs_v2", VertexElementUsage.TextureCoordinate, 0) });
        EffectParameterCollection p = Fx.Parameters;
        _wvp = p["WorldViewProj"];
        _world = p["World"];
        _eye = p["Eye"];
        _fog = p["Fog"];
        _ambient = p["Ambient"];
        _sun0 = p["Sun0"];
        _sun1 = p["Sun1"];
        _sunDir = p["SunDir"];
        _fogColor = p["FogColor"];
        _diff = p["DiffTex"];
        _normal = p["NormalTex"];
    }

    private Matrix _vp;

    /// <summary>Comme l'effet éclairé des objets (_lit) : lumière ambiante, soleil (dir) et sa lumière opposée.</summary>
    public void SetFrame(Matrix viewProj, Vector3 eye, float fogStart, float fogEnd, Vector3 fogColor, Vector3 ambient, Vector3 sun0, Vector3 sun1, Vector3 sunDir)
    {
        _vp = viewProj;
        _eye.SetValue(new Vector4(eye, 1));
        _fog.SetValue(new Vector4(fogStart, fogEnd, 0, 0));
        _fogColor.SetValue(new Vector4(fogColor, 1));
        _ambient.SetValue(new Vector4(ambient, 1));
        _sun0.SetValue(new Vector4(sun0, 1));
        _sun1.SetValue(new Vector4(sun1, 1));
        _sunDir.SetValue(new Vector4(Vector3.Normalize(sunDir), 0));
    }

    public void Set(Matrix world, Texture2D diff, Texture2D normal)
    {
        _wvp.SetValue(world * _vp);
        _world.SetValue(world);
        _diff.SetValue(diff);
        _normal.SetValue(normal);
    }
}
