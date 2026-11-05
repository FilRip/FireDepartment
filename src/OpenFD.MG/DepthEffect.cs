using System.Text;
using Microsoft.Xna.Framework.Graphics;

namespace OpenFD.MG;

/// <summary>
/// Effet de profondeur vue du soleil (carte d'ombres) : chaque pixel reçoit la distance au soleil (0 à 1).
/// Détourage alpha pour les feuillages.
/// </summary>
public sealed class DepthEffect
{
    public Effect Fx { get; }
    private readonly EffectParameter _wvp, _params, _tex;
    public DepthEffect(GraphicsDevice gd)
    {
        const string vs = @"
uniform vec4 vs_uniforms_vec4[4];
uniform vec4 posFixup;
attribute vec4 vs_v0;
attribute vec4 vs_v1;
varying vec4 vD;
void main()
{
    vec4 p = vec4(vs_v0.xyz, 1.0);
    gl_Position = vec4(dot(p, vs_uniforms_vec4[0]), dot(p, vs_uniforms_vec4[1]), dot(p, vs_uniforms_vec4[2]), dot(p, vs_uniforms_vec4[3]));
    vD = vec4(gl_Position.z / gl_Position.w, vs_v1.xy, 0.0);" + GlslEffect.PosFixup + @"
}";
        const string ps = @"
uniform vec4 ps_uniforms_vec4[1];
uniform sampler2D ps_s0;
varying vec4 vD;
void main()
{
    if (ps_uniforms_vec4[0].x > 0.0 && texture2D(ps_s0, vD.yz).a < ps_uniforms_vec4[0].x) discard;
    gl_FragColor = vec4(vD.x, vD.x, vD.x, 1.0);
}";
        Fx = GlslEffect.Create(gd, vs, ps, new[] { new GlslEffect.Param("WorldViewProj", 4, true), new GlslEffect.Param("Params", 1, false) },
            new[] { "Tex" }, new[] { ("vs_v0", VertexElementUsage.Position, 0), ("vs_v1", VertexElementUsage.TextureCoordinate, 0) });
        _wvp = Fx.Parameters["WorldViewProj"]; _params = Fx.Parameters["Params"]; _tex = Fx.Parameters["Tex"];
    }

    public void Set(Microsoft.Xna.Framework.Matrix worldLightViewProj, Texture2D? alphaTex)
    {
        _wvp.SetValue(worldLightViewProj);
        _params.SetValue(new Microsoft.Xna.Framework.Vector4(alphaTex != null ? 0.4f : -1f, 0, 0, 0));
        _tex.SetValue(alphaTex ?? Tex.White);
    }
}
