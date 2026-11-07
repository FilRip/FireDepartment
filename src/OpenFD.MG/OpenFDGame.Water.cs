using OpenFD.Formats;
using OpenFD.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SN = System.Numerics;

namespace OpenFD.MG;

/// <summary>
/// Eau du décor (rivière de Sawmill, mer de TMI : maillage « WaterSurface », shader « FD2Water »). Ses sommets sont noirs
/// (le shader du jeu ne s'en sert pas) et sa texture (watersurface_*.tga) est grise : le jeu la teinte et y reflète le
/// ciel (cubemap « Set1_… » du matériau). Dessinée à part : bleu profond sous la caméra, ciel reflété au loin (plus on
/// la voit de biais, plus elle reflète), deux couches de la texture qui défilent chacune à sa vitesse ([WATER]
/// ScrollSpeedX0/Y0 et X1/Y1 de la mission), et le ciel qui glisse lentement dessus.
/// </summary>
public partial class OpenFDGame
{
    private sealed class WaterPatch
    {
        public required SN.Vector3[] Pos;      // monde (découpé finement : le reflet varie d'un sommet à l'autre)
        public required Vector2[] Uv;          // uv d'origine de la texture de l'eau
        public required int[] Index;
        public required Texture2D? Tex, Sky;
        public DynamicVertexBuffer? Vb;
        public IndexBuffer? Ib;
        public VertexPositionColorTexture[]? Work;
        public float[]? Fresnel;
    }

    private readonly List<WaterPatch> _water = new();
    private BasicEffect? _waterFx;
    private SN.Vector2 _waterScroll0, _waterScroll1;
    private float _waterDensity = 0.6f;

    /// <summary>Uv par seconde pour 1 de ScrollSpeed (estimé : des vaguelettes lentes).</summary>
    private const float WaterScrollRate = 0.02f;
    /// <summary>Seconde couche : texture plus fine (uv × 1,7), un peu tournée, qui se mêle à la première.</summary>
    private const float WaterLayer2Scale = 1.7f;
    /// <summary>Découpe des grands triangles de l'eau (côtés plus courts que ça, en unités).</summary>
    private const float WaterTessel = 1500f;
    /// <summary>Couleur de l'eau profonde (vue d'au-dessus) et du ciel reflété (vue de biais).</summary>
    private static readonly Vector3 WaterDeep = new Vector3(0.09f, 0.15f, 0.27f), WaterSkyTint = new Vector3(0.40f, 0.48f, 0.58f);

    private void AddWater(SceneAssets.MeshAsset a, SN.Matrix4x4 world)
    {
        Mesh m = a.Mesh;
        foreach (SceneAssets.Group gr in a.Groups)
        {
            List<SN.Vector3> pos = new List<SN.Vector3>();
            List<Vector2> uv = new List<Vector2>();
            List<int> idx = new List<int>();
            for (int k = gr.Start; k + 2 < gr.Start + gr.Count && k + 2 < m.Indices.Length; k += 3)
            {
                (SN.Vector3 p, Vector2 t) V(int v) => (SN.Vector3.Transform(new SN.Vector3(m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2]), world),
                    new Vector2(a.Uv0[2 * v], a.Uv0[2 * v + 1]));
                Split(V((int)m.Indices[k]), V((int)m.Indices[k + 1]), V((int)m.Indices[k + 2]), 0);
            }
            if (idx.Count < 3)
                continue;
            Texture2D? sky = null;
            try
            {
                byte[]? mb = _files.TryRead("Gfx\\Mission\\" + _assets.GfxDir(_mission) + "\\Shaders\\" + gr.Material);
                Material? mat = mb != null ? Material.Load(mb, gr.Material) : null;
                if (mat != null && mat.Textures.Count > 1 && _assets.CubeTexture(_mission, mat.Textures[1]) is { } cube)
                    sky = Tex.Dds(GraphicsDevice, cube);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Ciel de l'eau : {e.Message}");
            }
            _water.Add(new WaterPatch { Pos = pos.ToArray(), Uv = uv.ToArray(), Index = idx.ToArray(), Tex = TexOf(gr.Diffuse), Sky = sky });

            // découpe récursive d'un triangle en quatre tant qu'un côté est trop long
            void Split((SN.Vector3 p, Vector2 t) p0, (SN.Vector3 p, Vector2 t) p1, (SN.Vector3 p, Vector2 t) p2, int depth)
            {
                float longest = MathF.Max(SN.Vector3.Distance(p0.p, p1.p), MathF.Max(SN.Vector3.Distance(p1.p, p2.p), SN.Vector3.Distance(p2.p, p0.p)));
                if (longest > WaterTessel && depth < 7)
                {
                    (SN.Vector3, Vector2) M((SN.Vector3 p, Vector2 t) x, (SN.Vector3 p, Vector2 t) y) => ((x.p + y.p) / 2, (x.t + y.t) / 2);
                    (SN.Vector3, Vector2) a01 = M(p0, p1), a12 = M(p1, p2), a20 = M(p2, p0);
                    Split(p0, a01, a20, depth + 1);
                    Split(a01, p1, a12, depth + 1);
                    Split(a20, a12, p2, depth + 1);
                    Split(a01, a12, a20, depth + 1);
                    return;
                }
                foreach ((SN.Vector3 p, Vector2 t) q in new[] { p0, p1, p2 })
                {
                    idx.Add(pos.Count);
                    pos.Add(q.p);
                    uv.Add(q.t);
                }
            }
        }
        // réglages de la mission (base_mission.mis donne les valeurs par défaut)
        ClassFile c = _session.MissionConfig;
        _waterScroll0 = new SN.Vector2(c.GetFloat("WATER", "ScrollSpeedX0", 1.1f), c.GetFloat("WATER", "ScrollSpeedY0", 0.2f));
        _waterScroll1 = new SN.Vector2(c.GetFloat("WATER", "ScrollSpeedX1", 1.0f), c.GetFloat("WATER", "ScrollSpeedY1", 0f));
        float density = c.GetFloat("WATER", "WaterDensity", 0.6f);
        _waterDensity = density > 0.05f ? density : 0.6f;
    }

    private void ClearWater()
    {
        foreach (WaterPatch w in _water)
        {
            w.Vb?.Dispose();
            w.Ib?.Dispose();
            w.Sky?.Dispose();
        }
        _water.Clear();
    }

    /// <summary>Eau : fond bleu et ciel reflété (selon l'angle de vue), deux couches de vaguelettes, le ciel qui glisse.</summary>
    private void RenderWater(Matrix viewProj)
    {
        if (_water.Count == 0)
            return;
        GraphicsDevice gd = GraphicsDevice;
        _waterFx ??= new BasicEffect(gd) { LightingEnabled = false, TextureEnabled = true, VertexColorEnabled = true };
        _waterFx.View = Matrix.Identity;
        _waterFx.Projection = viewProj;
        _waterFx.World = Matrix.Identity;
        _waterFx.FogEnabled = true;
        _waterFx.FogColor = Sky.X();
        _waterFx.FogStart = 30000;
        _waterFx.FogEnd = 90000;
        float t = _session.Time + (_paused ? 0 : (float)Math.Clamp(_owed, 0, 1) * Session.Dt);
        SN.Vector2 off0 = _waterScroll0 * (t * WaterScrollRate), off1 = _waterScroll1 * (t * WaterScrollRate);
        BlendState oldBlend = gd.BlendState;
        DepthStencilState oldDepth = gd.DepthStencilState;
        SamplerState oldSampler = gd.SamplerStates[0];
        gd.SamplerStates[0] = SamplerState.LinearWrap;
        gd.DepthStencilState = DepthStencilState.DepthRead;
        Vector3 light = (_bright * _tint).X();
        foreach (WaterPatch w in _water)
        {
            if (w.Vb == null)
            {
                w.Vb = new DynamicVertexBuffer(gd, typeof(VertexPositionColorTexture), w.Pos.Length, BufferUsage.WriteOnly);
                w.Ib = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, w.Index.Length, BufferUsage.WriteOnly);
                w.Ib.SetData(w.Index);
                w.Work = new VertexPositionColorTexture[w.Pos.Length];
                w.Fresnel = new float[w.Pos.Length];
            }
            // reflet : nul vu d'aplomb, fort vu de biais (vers l'horizon)
            for (int i = 0; i < w.Pos.Length; i++)
            {
                SN.Vector3 d = SN.Vector3.Normalize(_eye - w.Pos[i]);
                float f = Math.Clamp(1 - d.Z, 0, 1);
                w.Fresnel![i] = Math.Clamp(f * f * 1.8f, 0, 1);
            }
            // couche 1 : la texture de l'eau, teinte du fond vers le ciel selon le reflet (le gris de la texture vaut ~0,42 : × 2,4)
            Layer(w, w.Tex, i => w.Uv[i], off0, 1f, 0f, BlendState.AlphaBlend,
                i => new Vector4(Vector3.Lerp(WaterDeep, WaterSkyTint, w.Fresnel![i] * 0.75f) * light * 2.2f, MathF.Min(1, 0.35f + _waterDensity)));
            // couche 2 : plus fine, un peu tournée : les reflets clairs des vaguelettes, plus visibles de biais
            Layer(w, w.Tex, i => w.Uv[i], off1, WaterLayer2Scale, 0.35f, BlendState.Additive,
                i => new Vector4(light * (0.04f + 0.08f * w.Fresnel![i]), 1));
            // ciel reflété : nuages qui glissent, seulement de biais
            if (w.Sky != null)
                Layer(w, w.Sky, i => new Vector2(w.Pos[i].X, w.Pos[i].Y) / 24000f, off0 * 0.15f, 1f, 0f, BlendState.Additive,
                    i => new Vector4(light * (0.2f * w.Fresnel![i]), 1));
        }
        gd.BlendState = oldBlend;
        gd.DepthStencilState = oldDepth;
        gd.SamplerStates[0] = oldSampler;

        void Layer(WaterPatch w, Texture2D? tex, Func<int, Vector2> uvOf, SN.Vector2 off, float scale, float turn, BlendState blend, Func<int, Vector4> color)
        {
            float cs = MathF.Cos(turn), sn = MathF.Sin(turn);
            for (int i = 0; i < w.Pos.Length; i++)
            {
                Vector2 uv = uvOf(i);
                Vector2 r = new Vector2(uv.X * cs - uv.Y * sn, uv.X * sn + uv.Y * cs) * scale;
                Vector4 c = color(i);
                w.Work![i] = new VertexPositionColorTexture(w.Pos[i].X() + new Vector3(0, 0, 2), new Color(Vector4.Clamp(c, Vector4.Zero, Vector4.One)), r + new Vector2(off.X, off.Y));
            }
            w.Vb!.SetData(w.Work!, 0, w.Work!.Length, SetDataOptions.Discard);
            gd.BlendState = blend;
            _waterFx!.Texture = tex ?? Tex.White;
            _waterFx.DiffuseColor = Vector3.One;
            _waterFx.Alpha = 1;
            gd.SetVertexBuffer(w.Vb);
            gd.Indices = w.Ib;
            foreach (EffectPass pass in _waterFx.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, w.Index.Length / 3);
            }
        }
    }
}
