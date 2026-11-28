using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace OpenFD.MG;

/// <summary>
/// Effet des objets, des véhicules et des personnages (à la place de BasicEffect et AlphaTestEffect : texture, éclairage du
/// jeu 0,62 + 0,55 × |n · soleil|, brouillard), avec en plus :
///  • l'ombre du décor : dans l'ombre d'un bâtiment ou d'un arbre (carte d'ombres du décor seul, sans les objets ni les
///    unités : pas d'ombre d'un objet sur lui-même), l'objet est assombri comme le sol (opacité des ombres de la mission) ;
///  • une carte de normales (normal map), si la texture en a une : « &lt;nom&gt;_normal » à côté de la texture de couleur.
///    Le relief de la carte modifie la normale avant l'éclairage. Le repère de la surface (tangente, bitangente) est déduit
///    dans le shader des variations de la position et des uv d'un pixel à l'autre : les maillages restent tels quels.
///    Carte au format « OpenGL » (vert vers le haut de l'image, celui de Blender) ; DDS DXT1/DXT5 ou 32 bits.
/// </summary>
public sealed class NormalMapEffect
{
    public Effect Fx { get; }
    private readonly EffectParameter _wvp, _world, _eye, _fog, _shadowVp, _ambient, _sun0, _sun1, _sunDir, _fogColor, _shadow, _params, _color, _diff, _normal, _shadowMap;

    private const string Vs = @"
uniform vec4 vs_uniforms_vec4[14];
uniform vec4 posFixup;
attribute vec4 vs_v0;
attribute vec4 vs_v1;
attribute vec4 vs_v2;
varying vec4 vTex0;
varying vec4 vNormal;
varying vec4 vWorld;
varying vec4 vShadow;
void main()
{
    vec4 p = vec4(vs_v0.xyz, 1.0);
    gl_Position = vec4(dot(p, vs_uniforms_vec4[0]), dot(p, vs_uniforms_vec4[1]), dot(p, vs_uniforms_vec4[2]), dot(p, vs_uniforms_vec4[3]));
    vec4 w = vec4(dot(p, vs_uniforms_vec4[4]), dot(p, vs_uniforms_vec4[5]), dot(p, vs_uniforms_vec4[6]), 1.0);
    float d = length(w.xyz - vs_uniforms_vec4[8].xyz);
    float fog = clamp((d - vs_uniforms_vec4[9].x) / max(1.0, vs_uniforms_vec4[9].y - vs_uniforms_vec4[9].x), 0.0, 1.0);
    vShadow = vec4(dot(w, vs_uniforms_vec4[10]), dot(w, vs_uniforms_vec4[11]), dot(w, vs_uniforms_vec4[12]), dot(w, vs_uniforms_vec4[13]));
    vTex0 = vec4(vs_v2.xy, fog, 0.0);
    vWorld = w;
    vNormal = vec4(dot(vs_v1.xyz, vs_uniforms_vec4[4].xyz), dot(vs_v1.xyz, vs_uniforms_vec4[5].xyz), dot(vs_v1.xyz, vs_uniforms_vec4[6].xyz), 0.0);" + GlslEffect.PosFixup + @"
}
";

    private const string Ps = @"
uniform vec4 ps_uniforms_vec4[8];
uniform sampler2D ps_s0;
uniform sampler2D ps_s1;
uniform sampler2D ps_s2;
varying vec4 vTex0;
varying vec4 vNormal;
varying vec4 vWorld;
varying vec4 vShadow;
" + Perturb + @"
void main()
{
    vec4 diff = texture2D(ps_s0, vTex0.xy) * ps_uniforms_vec4[7];
    vec4 prm = ps_uniforms_vec4[6];
    if (diff.a < prm.y) discard;
    vec3 n = normalize(vNormal.xyz + vec3(0.0, 0.0, 1e-5));
    if (prm.x > 0.5) n = PerturbNormal(n, vWorld.xyz, vTex0.xy, texture2D(ps_s1, vTex0.xy).xyz);
    vec3 sun = ps_uniforms_vec4[3].xyz;
    vec3 light = ps_uniforms_vec4[0].rgb + max(dot(n, -sun), 0.0) * ps_uniforms_vec4[1].rgb + max(dot(n, sun), 0.0) * ps_uniforms_vec4[2].rgb;
    // sans éclairage (détourés : grillages, feuillages, comme AlphaTestEffect) : la couleur telle quelle
    if (prm.z < 0.5) light = vec3(1.0);
    // ombre du décor (bâtiments, arbres) : 4 échantillons pour adoucir le bord, comme le sol
    vec4 sh = ps_uniforms_vec4[5];
    float shade = 0.0;
    if (sh.w > 0.5)
    {
        vec3 s = vShadow.xyz / vShadow.w;
        vec2 uv = vec2(s.x * 0.5 + 0.5, 0.5 - s.y * 0.5);
        if (uv.x > 0.0 && uv.x < 1.0 && uv.y > 0.0 && uv.y < 1.0 && s.z < 1.0)
        {
            float t = sh.z;
            shade += (s.z - sh.x > texture2D(ps_s2, uv + vec2(-t, -t)).r) ? 0.25 : 0.0;
            shade += (s.z - sh.x > texture2D(ps_s2, uv + vec2( t, -t)).r) ? 0.25 : 0.0;
            shade += (s.z - sh.x > texture2D(ps_s2, uv + vec2(-t,  t)).r) ? 0.25 : 0.0;
            shade += (s.z - sh.x > texture2D(ps_s2, uv + vec2( t,  t)).r) ? 0.25 : 0.0;
        }
    }
    vec3 col = diff.rgb * light * mix(1.0, 1.0 - sh.y, shade);
    gl_FragColor = vec4(mix(col, ps_uniforms_vec4[4].rgb, vTex0.z), 1.0);
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
                new GlslEffect.Param("Fog", 1, true), new GlslEffect.Param("ShadowViewProj", 4, true),
                new GlslEffect.Param("Ambient", 1, false), new GlslEffect.Param("Sun0", 1, false), new GlslEffect.Param("Sun1", 1, false),
                new GlslEffect.Param("SunDir", 1, false), new GlslEffect.Param("FogColor", 1, false), new GlslEffect.Param("Shadow", 1, false),
                new GlslEffect.Param("Params", 1, false), new GlslEffect.Param("Color", 1, false),
            },
            new[] { "DiffTex", "NormalTex", "ShadowMap" },
            new[] { ("vs_v0", VertexElementUsage.Position, 0), ("vs_v1", VertexElementUsage.Normal, 0), ("vs_v2", VertexElementUsage.TextureCoordinate, 0) });
        EffectParameterCollection p = Fx.Parameters;
        _wvp = p["WorldViewProj"];
        _world = p["World"];
        _eye = p["Eye"];
        _fog = p["Fog"];
        _shadowVp = p["ShadowViewProj"];
        _ambient = p["Ambient"];
        _sun0 = p["Sun0"];
        _sun1 = p["Sun1"];
        _sunDir = p["SunDir"];
        _fogColor = p["FogColor"];
        _shadow = p["Shadow"];
        _params = p["Params"];
        _color = p["Color"];
        _diff = p["DiffTex"];
        _normal = p["NormalTex"];
        _shadowMap = p["ShadowMap"];
        _normal.SetValue(Tex.White);
        _shadowMap.SetValue(Tex.White);
        _shadow.SetValue(Vector4.Zero);
    }

    private Matrix _vp;
    /// <summary>Carte d'ombres donnée à SetShadow (MonoGame ne sait pas relire une texture dans un paramètre).</summary>
    private Texture2D? _shadowTex;

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

    /// <summary>
    /// Carte d'ombres du décor seul (bâtiments, sol, arbres) ; null : pas d'ombre sur les objets. opacity : part de lumière
    /// retirée dans l'ombre (la même que pour le sol).
    /// </summary>
    public void SetShadow(Texture2D? map, Matrix lightViewProj, float opacity, float bias)
    {
        _shadowTex = map;
        _shadowMap.SetValue(map ?? Tex.White);
        _shadowVp.SetValue(lightViewProj);
        _shadow.SetValue(map != null ? new Vector4(bias, opacity, 0.7f / map.Width, 1) : Vector4.Zero);
    }

    /// <summary>Réglages de l'image en cours (caméra, lumière, brouillard, ombres), pour un dessin à part puis le retour.</summary>
    public sealed record State(Matrix Vp, Vector4 Eye, Vector4 Fog, Vector4 FogColor, Vector4 Ambient, Vector4 Sun0, Vector4 Sun1, Vector4 SunDir,
        Vector4 Shadow, Matrix ShadowVp, Texture2D? ShadowMap);

    /// <summary>Les trois premières composantes (couleur, direction) d'un réglage de State.</summary>
    public static Vector3 Xyz(Vector4 v) => new(v.X, v.Y, v.Z);

    public State Save() => new(_vp, _eye.GetValueVector4(), _fog.GetValueVector4(), _fogColor.GetValueVector4(), _ambient.GetValueVector4(),
        _sun0.GetValueVector4(), _sun1.GetValueVector4(), _sunDir.GetValueVector4(), _shadow.GetValueVector4(), _shadowVp.GetValueMatrix(),
        _shadowTex);

    public void Restore(State st)
    {
        _vp = st.Vp;
        _eye.SetValue(st.Eye);
        _fog.SetValue(st.Fog);
        _fogColor.SetValue(st.FogColor);
        _ambient.SetValue(st.Ambient);
        _sun0.SetValue(st.Sun0);
        _sun1.SetValue(st.Sun1);
        _sunDir.SetValue(st.SunDir);
        _shadow.SetValue(st.Shadow);
        _shadowVp.SetValue(st.ShadowVp);
        _shadowTex = st.ShadowMap;
        _shadowMap.SetValue(st.ShadowMap ?? Tex.White);
    }

    /// <param name="normal">carte de normales ; null : aucune</param>
    /// <param name="alphaCut">détourage : pixels plus transparents écartés (comme AlphaTestEffect) ; -1 : aucun</param>
    /// <param name="lit">éclairé par le soleil (sinon la couleur telle quelle, multipliée par color)</param>
    /// <param name="color">couleur qui multiplie la texture (teinte, luminosité)</param>
    public void Set(Matrix world, Texture2D diff, Texture2D? normal, float alphaCut, bool lit, Vector3 color)
    {
        _wvp.SetValue(world * _vp);
        _world.SetValue(world);
        _diff.SetValue(diff);
        _normal.SetValue(normal ?? Tex.White);
        _params.SetValue(new Vector4(normal != null ? 1 : 0, alphaCut, lit ? 1 : 0, 0));
        _color.SetValue(new Vector4(color, 1));
    }
}
