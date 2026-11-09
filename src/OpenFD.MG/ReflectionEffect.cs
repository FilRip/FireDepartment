using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace OpenFD.MG;

/// <summary>
/// Reflet des objets, ajouté par-dessus leur couleur (lumière ajoutée), comme dans les shaders du jeu :
///  • image simple (FD2Item : voitures garées, veh_c_envmap1) lue selon la normale vue de la caméra (n.xy × 0,5 + 0,5) ;
///  • décor en 6 faces (FD2Vehicle, FD2ReflectionBuilding) lu dans la direction du regard réfléchi sur la surface.
/// Dans les deux cas, dosé par l'alpha de la texture de couleur (masque de brillance « _spe »).
/// </summary>
public sealed class ReflectionEffect
{
    public readonly Effect Fx;
    private readonly EffectParameter _wvp, _world, _view, _eye, _mode, _diff, _env, _cube;

    private const string Vs = @"
uniform vec4 vs_uniforms_vec4[13];
uniform vec4 posFixup;
attribute vec4 vs_v0;
attribute vec4 vs_v1;
attribute vec4 vs_v2;
varying vec4 vTex;
varying vec4 vRefl;
varying vec4 vViewNormal;
void main()
{
    vec4 p = vec4(vs_v0.xyz, 1.0);
    gl_Position = vec4(dot(p, vs_uniforms_vec4[0]), dot(p, vs_uniforms_vec4[1]), dot(p, vs_uniforms_vec4[2]), dot(p, vs_uniforms_vec4[3]));
    vec3 w = vec3(dot(p, vs_uniforms_vec4[4]), dot(p, vs_uniforms_vec4[5]), dot(p, vs_uniforms_vec4[6]));
    vec3 n = normalize(vec3(dot(vs_v1.xyz, vs_uniforms_vec4[4].xyz), dot(vs_v1.xyz, vs_uniforms_vec4[5].xyz), dot(vs_v1.xyz, vs_uniforms_vec4[6].xyz)) + vec3(0.0, 0.0, 1e-5));
    vRefl = vec4(reflect(normalize(w - vs_uniforms_vec4[12].xyz), n), 0.0);
    vViewNormal = vec4(dot(n, vs_uniforms_vec4[8].xyz), dot(n, vs_uniforms_vec4[9].xyz), dot(n, vs_uniforms_vec4[10].xyz), 0.0);
    vTex = vec4(vs_v2.xy, 0.0, 0.0);" + GlslEffect.PosFixup + @"
}
";

    private const string Ps = @"
uniform vec4 ps_uniforms_vec4[1];
uniform sampler2D ps_s0;
uniform sampler2D ps_s1;
uniform samplerCube ps_s2;
varying vec4 vTex;
varying vec4 vRefl;
varying vec4 vViewNormal;
void main()
{
    float mask = texture2D(ps_s0, vTex.xy).a;
    vec3 r;
    if (ps_uniforms_vec4[0].x > 0.5)
        r = textureCube(ps_s2, vRefl.xyz).rgb;
    else
        r = texture2D(ps_s1, normalize(vViewNormal.xyz).xy * vec2(0.5) + vec2(0.5)).rgb;
    gl_FragColor = vec4(r * mask * ps_uniforms_vec4[0].y, 1.0);
}
";

    public ReflectionEffect(GraphicsDevice gd)
    {
        Fx = GlslEffect.Create(gd, Vs, Ps, new[]
            {
                new GlslEffect.Param("WorldViewProj", 4, true), new GlslEffect.Param("World", 4, true), new GlslEffect.Param("View", 4, true),
                new GlslEffect.Param("Eye", 1, true), new GlslEffect.Param("Mode", 1, false),
            },
            new[] { "DiffTex", "EnvTex", "CubeTex" },
            new[] { ("vs_v0", VertexElementUsage.Position, 0), ("vs_v1", VertexElementUsage.Normal, 0), ("vs_v2", VertexElementUsage.TextureCoordinate, 0) });
        EffectParameterCollection p = Fx.Parameters;
        _wvp = p["WorldViewProj"];
        _world = p["World"];
        _view = p["View"];
        _eye = p["Eye"];
        _mode = p["Mode"];
        _diff = p["DiffTex"];
        _env = p["EnvTex"];
        _cube = p["CubeTex"];
        _empty = new TextureCube(gd, 1, false, SurfaceFormat.Color);
        for (int f = 0; f < 6; f++)
            _empty.SetData((CubeMapFace)f, new[] { Color.Black });
    }

    private readonly TextureCube _empty;
    /// <summary>Décor vide (noir) : à lier quand le décor n'est pas utilisé (OpenGL refuse une texture 2D dans un cube).</summary>
    public TextureCube Empty => _empty;

    public void SetFrame(Matrix view, Matrix viewProj, Vector3 eye)
    {
        _view.SetValue(view);
        _vpFrame = viewProj;
        _eye.SetValue(new Vector4(eye, 1));
    }
    private Matrix _vpFrame;

    /// <param name="strength">force du reflet (1 : comme le jeu)</param>
    public void Set(Matrix world, Texture2D diffuse, Texture2D? env, TextureCube? cube, float strength)
    {
        _wvp.SetValue(world * _vpFrame);
        _world.SetValue(world);
        _diff.SetValue(diffuse);
        _env.SetValue(env ?? Tex.White);
        _cube.SetValue(cube ?? _empty);
        _mode.SetValue(new Vector4(cube != null ? 1 : 0, strength, 0, 0));
    }
}
