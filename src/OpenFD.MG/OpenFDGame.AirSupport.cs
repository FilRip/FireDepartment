using OpenFD.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SN = System.Numerics;

namespace OpenFD.MG;

/// <summary>
/// Aide aérienne (caserne « AS1 ») côté interface : clic sur son cadre vert → le curseur devient la cible bleue du jeu
/// (Gfx\Interface\Target_WaterStrike), posée à plat sur le sol ; clic gauche : appel sur ce point ; Échap ou clic
/// droit : annulé. Pendant l'attente, la cible reste sur le point ; au passage, l'ombre de l'avion (Canadair) glisse sur le
/// sol (l'avion vole à 110 m : on ne voit que son ombre, comme dans le jeu d'origine).
/// </summary>
public partial class OpenFDGame
{
    private bool _airTargeting;
    private BasicEffect? _airFx, _airShadowFx;
    private const string AirTargetModel = "Interface\\Target_WaterStrike";
    /// <summary>Repère du modèle de l'avion : il est posé à 214 m du point de départ de son passage et à 109 m de haut.</summary>
    private static readonly SN.Vector3 PlaneOffset = new SN.Vector3(-21438, 0, 10930);

    private void DoAirStrike(SN.Vector2 p)
    {
        if (NetSubmit("airstrike", x: p.X, y: p.Y))
            return;
        Rec("airstrike", x: p.X, y: p.Y);
        _session.Units.CallAirStrike(p);
    }

    /// <summary>Clic sur le cadre de la caserne aérienne : visée (true si le clic est pris).</summary>
    private bool AirTabPress(string team)
    {
        if (team != _session.Units.AirTeam)
            return false;
        if (_session.Units.AirAvailable)
        {
            _airTargeting = true;
            Log("Bombardier d'eau : cliquez sur la zone à arroser (Échap pour annuler)");
        }
        return true;
    }

    /// <summary>Visée en cours : Échap ou clic droit annulent, clic gauche appelle l'avion. true si l'entrée est prise.</summary>
    private bool AirTargetInput(KeyboardStateLike k, bool leftClick, bool rightClick, double mx, double my)
    {
        if (!_airTargeting)
            return false;
        if (!_session.Units.AirAvailable || k.Escape || rightClick)
        {
            _airTargeting = false;
            IsMouseVisible = true;
            if (k.Escape || rightClick)
                Log("Appel du bombardier annulé");
            return k.Escape || rightClick;
        }
        if (!leftClick)
            return false;
        if (Ground(mx, my) is { } p)
        {
            DoAirStrike(p);
            Log($"Bombardier d'eau appelé : au-dessus de la zone dans {_session.Units.AirEta + _session.Units.AirBefore:0} s");
        }
        _airTargeting = false;
        IsMouseVisible = true;
        return true;
    }

    /// <summary>Touches utiles à la visée (Échap vient d'être pressée).</summary>
    private readonly record struct KeyboardStateLike(bool Escape);

    /// <summary>Cible bleue (sous la souris pendant la visée, sur le point appelé pendant l'attente) et ombre de l'avion.</summary>
    private void RenderAirSupport(Matrix viewProj)
    {
        if (_map || _session == null || _cineOn)
            return;
        UnitManager um = _session.Units;
        IsMouseVisible = !_airTargeting;
        SN.Vector2? at = null;
        float alpha = 1;
        if (_airTargeting)
        {
            MouseState ms = Mouse.GetState();
            at = Ground(ms.X, ms.Y);
        }
        else if (um.Strike is { Dropped: false } st)
        {
            at = st.Target;
            alpha = 0.55f + 0.45f * MathF.Abs(MathF.Sin(_uiClock * 3));   // pulse en attendant l'avion
        }
        GraphicsDevice gd = GraphicsDevice;
        DepthStencilState oldDepth = gd.DepthStencilState;
        BlendState oldBlend = gd.BlendState;
        RasterizerState oldRast = gd.RasterizerState;
        gd.RasterizerState = RasterizerState.CullNone;
        if (at is { } c)
        {
            _airFx ??= new BasicEffect(gd) { LightingEnabled = false, TextureEnabled = true, VertexColorEnabled = false };
            _airFx.View = Matrix.Identity;
            _airFx.Projection = viewProj;
            _airFx.DiffuseColor = new Vector3(0.35f, 0.85f, 1f) * alpha;
            gd.DepthStencilState = DepthStencilState.None;   // toujours visible, comme un repère d'interface
            gd.BlendState = BlendState.Additive;              // lumineuse, comme dans le jeu
            // taille : le cercle de la cible couvre l'imprécision du largage (RANDOMRADIUS)
            float scale = MathF.Max(1, um.AirRandom / 246f);
            SN.Matrix4x4 place = SN.Matrix4x4.CreateScale(scale, scale, 1) * SN.Matrix4x4.CreateRotationZ(_uiClock * 0.6f)
                * SN.Matrix4x4.CreateTranslation(c.X, c.Y, GroundAt(c.X, c.Y) + 12);
            List<(MeshGpu mesh, SN.Matrix4x4 part)> parts = UnitModel(AirTargetModel);
            foreach ((MeshGpu mesh, SN.Matrix4x4 part) in parts)
            {
                if (mesh.Dual)
                    continue;
                _airFx.World = (part * place).X();
                gd.SetVertexBuffer(mesh.Vb);
                gd.Indices = mesh.Ib;
                foreach ((int start, int count, Texture2D? tex, _, _) in mesh.Groups)
                {
                    _airFx.Texture = tex ?? Tex.White;
                    foreach (EffectPass pass in _airFx.CurrentTechnique.Passes)
                    {
                        pass.Apply();
                        gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, start, count / 3);
                    }
                }
            }
        }
        // passage de l'avion : son ombre glisse sur le sol, au-dessus du point de largage à mi-passage
        if (um.Strike is { } s && um.AirModel is { } model)
        {
            float now = _session.Time + (_paused ? 0 : (float)Math.Clamp(_owed, 0, 1) * Session.Dt);
            float speed = -PlaneOffset.X / MathF.Max(0.1f, um.AirBefore);
            float t0 = s.Pass - um.AirBefore;
            if (now >= t0 && now <= s.End)
            {
                SN.Vector2 dir = new SN.Vector2(MathF.Cos(s.Heading), MathF.Sin(s.Heading));
                SN.Vector2 p = s.Drop + dir * ((now - s.Pass) * speed);
                _airShadowFx ??= new BasicEffect(gd) { LightingEnabled = false, TextureEnabled = false, VertexColorEnabled = false, DiffuseColor = Vector3.Zero, Alpha = 0.4f };
                _airShadowFx.View = Matrix.Identity;
                _airShadowFx.Projection = viewProj;
                // par-dessus tout (toits, étages) : l'ombre de l'avion tombe aussi sur les bâtiments
                gd.DepthStencilState = DepthStencilState.None;
                gd.BlendState = BlendState.AlphaBlend;
                // à plat : hauteur écrasée, posée juste au-dessus du sol
                SN.Matrix4x4 flat = SN.Matrix4x4.CreateTranslation(-PlaneOffset) * SN.Matrix4x4.CreateScale(1, 1, 0.002f)
                    * SN.Matrix4x4.CreateRotationZ(s.Heading) * SN.Matrix4x4.CreateTranslation(p.X, p.Y, MathF.Max(GroundAt(p.X, p.Y), _session.Units.GroundZ(p)) + 25);
                foreach ((MeshGpu mesh, SN.Matrix4x4 part) in UnitModel(model))
                {
                    if (mesh.Dual)
                        continue;
                    _airShadowFx.World = (part * flat).X();
                    gd.SetVertexBuffer(mesh.Vb);
                    gd.Indices = mesh.Ib;
                    foreach ((int start, int count, _, _, _) in mesh.Groups)
                        foreach (EffectPass pass in _airShadowFx.CurrentTechnique.Passes)
                        {
                            pass.Apply();
                            gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, start, count / 3);
                        }
                }
            }
        }
        gd.DepthStencilState = oldDepth;
        gd.BlendState = oldBlend;
        gd.RasterizerState = oldRast;
    }
}
