using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace OpenFD.MG;

/// <summary>
/// Effet du décor, à la manière des shaders du jeu d'origine (Data\Shader\FD2Ground.fx et FD2Building.fx) :
///  • sol      : couleur mélangée à moitié avec la texture de saleté, × 2 × éclairage précalculé ;
///  • bâtiment : couleur × saleté × 2 × éclairage précalculé (alpha de la couleur).
/// (Les traces de brûlure et les reflets viendront ensuite.)
/// L'effet est écrit directement en GLSL et assemblé au format MGFX de MonoGame (version OpenGL) au lancement :
/// aucun outil de compilation de shaders n'est nécessaire.
/// </summary>
public sealed class SceneEffect
{
    public const int ModePlain = 0, ModeGround = 1, ModeBuilding = 2;
    public Effect Fx { get; }
    private readonly EffectParameter _wvp, _world, _eye, _fog, _shadowVp, _params, _fogColor, _shadow, _sun, _tint, _diff, _dirt, _light, _shadowMap, _roofMask, _roofMaskTex;

    private const string Vs = @"
uniform vec4 vs_uniforms_vec4[14];
uniform vec4 posFixup;
attribute vec4 vs_v0;
attribute vec4 vs_v1;
attribute vec4 vs_v2;
attribute vec4 vs_v3;
attribute vec4 vs_v4;
varying vec4 vTex0;
varying vec4 vTex1;
varying vec4 vShadow;
varying vec4 vNormal;
varying vec4 vWorld;
void main()
{
    vec4 p = vec4(vs_v0.xyz, 1.0);
    gl_Position = vec4(dot(p, vs_uniforms_vec4[0]), dot(p, vs_uniforms_vec4[1]), dot(p, vs_uniforms_vec4[2]), dot(p, vs_uniforms_vec4[3]));
    vec4 w = vec4(dot(p, vs_uniforms_vec4[4]), dot(p, vs_uniforms_vec4[5]), dot(p, vs_uniforms_vec4[6]), 1.0);
    float d = length(w.xyz - vs_uniforms_vec4[8].xyz);
    float fog = clamp((d - vs_uniforms_vec4[9].x) / max(1.0, vs_uniforms_vec4[9].y - vs_uniforms_vec4[9].x), 0.0, 1.0);
    vShadow = vec4(dot(w, vs_uniforms_vec4[10]), dot(w, vs_uniforms_vec4[11]), dot(w, vs_uniforms_vec4[12]), dot(w, vs_uniforms_vec4[13]));
    vTex0 = vec4(vs_v1.xy, vs_v2.xy);
    vTex1 = vec4(vs_v3.xy, fog, 0.0);
    vWorld = w;
    vNormal = vec4(dot(vs_v4.xyz, vs_uniforms_vec4[4].xyz), dot(vs_v4.xyz, vs_uniforms_vec4[5].xyz), dot(vs_v4.xyz, vs_uniforms_vec4[6].xyz), 0.0);" + GlslEffect.PosFixup + @"
}
";

    private const string Ps = @"
uniform vec4 ps_uniforms_vec4[6];
uniform sampler2D ps_s0;
uniform sampler2D ps_s1;
uniform sampler2D ps_s2;
uniform sampler2D ps_s3;
uniform sampler2D ps_s4;
varying vec4 vTex0;
varying vec4 vTex1;
varying vec4 vShadow;
varying vec4 vNormal;
varying vec4 vWorld;
void main()
{
    // toit par pièce : la carte des pièces découvertes (une case de grille par texel) efface le toit au-dessus
    vec4 rm = ps_uniforms_vec4[5];
    if (rm.z > 0.5 && texture2D(ps_s4, vWorld.xy * rm.xy).r > 0.5) discard;
    vec4 diff = texture2D(ps_s0, vTex0.xy);
    if (diff.a < ps_uniforms_vec4[0].y) discard;
    vec3 c = diff.rgb;
    float mode = ps_uniforms_vec4[0].z;
    if (mode > 0.5)
    {
        vec3 dirt = texture2D(ps_s1, vTex0.zw).rgb;
        if (mode < 1.5) c = mix(dirt, diff.rgb, 0.5);
        else c = diff.rgb * dirt;
    }
    vec3 light = vec3(1.0);
    if (ps_uniforms_vec4[0].w > 0.0) light = texture2D(ps_s2, vTex1.xy).rgb * ps_uniforms_vec4[0].w;
    // ombres portées (carte de profondeur vue du soleil) : 4 échantillons pour adoucir le bord.
    // Les faces tournées vers l'ombre sont déjà sombres dans l'éclairage précalculé : on ne les assombrit pas une seconde fois.
    float shade = 0.0;
    vec4 sh = ps_uniforms_vec4[2];
    float ndl = dot(normalize(vNormal.xyz + vec3(0.0, 0.0, 1e-5)), -ps_uniforms_vec4[3].xyz);
    float facing = (ps_uniforms_vec4[3].w > 0.5) ? smoothstep(0.02, 0.2, ndl) : 1.0;
    if (sh.w > 0.5 && facing > 0.0)
    {
        vec3 s = vShadow.xyz / vShadow.w;
        vec2 uv = vec2(s.x * 0.5 + 0.5, 0.5 - s.y * 0.5);
        if (uv.x > 0.0 && uv.x < 1.0 && uv.y > 0.0 && uv.y < 1.0 && s.z < 1.0)
        {
            float t = sh.z;
            shade += (s.z - sh.x > texture2D(ps_s3, uv + vec2(-t, -t)).r) ? 0.25 : 0.0;
            shade += (s.z - sh.x > texture2D(ps_s3, uv + vec2( t, -t)).r) ? 0.25 : 0.0;
            shade += (s.z - sh.x > texture2D(ps_s3, uv + vec2(-t,  t)).r) ? 0.25 : 0.0;
            shade += (s.z - sh.x > texture2D(ps_s3, uv + vec2( t,  t)).r) ? 0.25 : 0.0;
        }
    }
    // comme le shader d'ombre du jeu (1 - (lumière - ambiante)) : l'ombre marque surtout les endroits bien éclairés ;
    // là où l'éclairage précalculé est déjà sombre (ombre déjà « cuite »), elle ne s'ajoute presque pas
    float lit = (ps_uniforms_vec4[0].w > 0.0) ? smoothstep(0.35, 0.85, dot(light, vec3(0.3333))) : 1.0;
    vec3 col = c * light * ps_uniforms_vec4[0].x * ps_uniforms_vec4[4].rgb * (1.0 - sh.y * shade * facing * lit);
    gl_FragColor = vec4(mix(col, ps_uniforms_vec4[1].rgb, vTex1.z), 1.0);
}
";

    public SceneEffect(GraphicsDevice gd)
    {
        Fx = GlslEffect.Create(gd, Vs, Ps, new[]
            {
                new GlslEffect.Param("WorldViewProj", 4, true), new GlslEffect.Param("World", 4, true), new GlslEffect.Param("Eye", 1, true),
                new GlslEffect.Param("Fog", 1, true), new GlslEffect.Param("ShadowViewProj", 4, true),
                new GlslEffect.Param("Params", 1, false), new GlslEffect.Param("FogColor", 1, false), new GlslEffect.Param("Shadow", 1, false), new GlslEffect.Param("SunDir", 1, false), new GlslEffect.Param("Tint", 1, false),
                new GlslEffect.Param("RoofMask", 1, false),
            },
            new[] { "DiffTex", "DirtTex", "LightTex", "ShadowMap", "RoofMaskTex" },
            new[] { ("vs_v0", VertexElementUsage.Position, 0), ("vs_v1", VertexElementUsage.TextureCoordinate, 0), ("vs_v2", VertexElementUsage.TextureCoordinate, 1), ("vs_v3", VertexElementUsage.TextureCoordinate, 2), ("vs_v4", VertexElementUsage.Normal, 0) });
        EffectParameterCollection p = Fx.Parameters;
        _wvp = p["WorldViewProj"]; _world = p["World"]; _eye = p["Eye"]; _fog = p["Fog"]; _shadowVp = p["ShadowViewProj"];
        _params = p["Params"]; _fogColor = p["FogColor"]; _shadow = p["Shadow"]; _sun = p["SunDir"]; _tint = p["Tint"];
        _diff = p["DiffTex"]; _dirt = p["DirtTex"]; _light = p["LightTex"]; _shadowMap = p["ShadowMap"];
        _roofMask = p["RoofMask"]; _roofMaskTex = p["RoofMaskTex"];
        _roofMaskTex.SetValue(Tex.White);
    }

    /// <summary>
    /// Carte des pièces découvertes (un texel par case de grille, rouge = toit effacé) appliquée aux toits ;
    /// null : pas de masque. cell : taille d'une case de grille ; w, h : taille de la grille.
    /// </summary>
    public void SetRoofMask(Texture2D? mask, float cell, int w, int h)
    {
        _roofMaskTex.SetValue(mask ?? Tex.White);
        _roofMask.SetValue(mask != null ? new Vector4(1f / (cell * w), 1f / (cell * h), 1, 0) : Vector4.Zero);
    }

    private Matrix _vp;
    /// <param name="tint">teinte de l'éclairage (blanc le jour ; plus sombre et bleutée la nuit)</param>
    public void SetFrame(Matrix viewProj, Vector3 eye, float fogStart, float fogEnd, Vector3 fogColor, Vector3 tint)
    {
        _tint.SetValue(new Vector4(tint, 1));
        _vp = viewProj;
        _eye.SetValue(new Vector4(eye, 1)); _fog.SetValue(new Vector4(fogStart, fogEnd, 0, 0)); _fogColor.SetValue(new Vector4(fogColor, 1));
        _shadow.SetValue(Vector4.Zero);
    }

    /// <summary>Carte d'ombres du soleil (null : pas d'ombres). opacity : part de lumière retirée dans l'ombre.</summary>
    public void SetShadow(Texture2D? map, Matrix lightViewProj, float opacity, float bias, Vector3 sunDir)
    {
        _sun.SetValue(new Vector4(sunDir, 1));
        _shadowMap.SetValue(map ?? Tex.White);
        _shadowVp.SetValue(lightViewProj);
        _shadow.SetValue(map != null ? new Vector4(bias, opacity, 0.7f / map.Width, 1) : Vector4.Zero);
    }

    /// <param name="lightScale">multiplicateur de l'éclairage précalculé (FD2_LIGHT du soleil : 2 le jour à London, 1,1 la nuit à Fireworks)</param>
    public void Set(Matrix world, Texture2D? diff, Texture2D? dirt, Texture2D? light, int mode, bool alpha, float bright, float lightScale = 2f)
    {
        _wvp.SetValue(world * _vp); _world.SetValue(world);
        _diff.SetValue(diff ?? Tex.White); _dirt.SetValue(dirt ?? Tex.White); _light.SetValue(light ?? Tex.White);
        _params.SetValue(new Vector4(bright, alpha ? 0.4f : -1f, dirt != null ? mode : ModePlain, light != null ? lightScale : 0));
    }
}
