using OpenFD.Formats;
using OpenFD.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SN = System.Numerics;

namespace OpenFD.MG;

// manche à air de l'interface : en haut à droite, à gauche du cadre des infos ; le modèle du jeu (Gfx\Placeable\Manchon,
// celui de la tour de Forest) avec son animation (si_Manchon), vu de trois quarts comme la caméra : elle montre vers où
// souffle le vent sur l'écran. Sans vent dans la mission, elle pend le long du mât ; plus le vent est fort, plus elle se tend
public sealed partial class OpenFDGame
{
    private const string WindSockModel = "Placeable\\Manchon", WindSockAnim = "SingleItem\\si_Manchon";
    private CharAnim? _windSock;
    private bool _windSockTried;
    private bool _windSockHidden;   // touche V
    private float _windDroop = 1.2f;

    private void RenderWindSock(float panelLeft, float top)
    {
        if (_windSockHidden)
            return;
        if (!_windSockTried)
        {
            _windSockTried = true;
            try
            {
                if (CharacterRig.Build(_assets, _files, WindSockModel) is { } rig)
                    _windSock = new CharAnim { Rig = rig };
            }
            catch (Exception)
            {
                _windSock = null;
            }
        }
        if (_windSock?.Rig is not { } r)
            return;
        float size = 120 * _scale, gap = 8 * _scale;
        float x = panelLeft - size - gap, y = top;
        if (x < 0)
            return;
        // fond : le même cadre que les infos
        _ui.Begin();
        _ui.Rect(x, y, size, size, new SN.Vector4(0.09f, 0.09f, 0.11f, 0.88f));
        _ui.End();

        // vent : direction (vers où il souffle) et force (ENT_SetWindPower : 1 sans effet, 3 fort)
        bool wind = Host?.HasWind == true;
        float power = _session.Fire.Settings.WindPower;
        float droopWanted = !wind ? 1.25f : Math.Clamp(0.55f - 0.27f * (power - 1f), 0f, 0.55f);
        _windDroop += (droopWanted - _windDroop) * MathF.Min(1, 0.05f);
        SN.Vector3 dir = _session.Fire.Settings.WindDirection;
        // la manche du modèle s'étire vers +Y : on la tourne vers la direction du vent
        float angle = (wind ? MathF.Atan2(dir.Y, dir.X) : _yaw + MathF.PI * 0.75f) - MathF.PI / 2;

        AnimClip? clip = Clip(WindSockAnim);
        float t = clip != null && clip.Duration > 0 ? _visTime * MathF.Max(0.4f, MathF.Min(1.6f, power)) % clip.Duration : 0;
        SN.Matrix4x4[] world = r.Pose(clip, t, _windSock.World.Length == r.Names.Length ? _windSock.World : null);
        _windSock.World = world;
        // la manche pend autour de son attache au mât (Manchon_bone1) : elle s'étire vers +Y, elle tombe en tournant
        // autour de l'axe X
        SN.Matrix4x4 droop = SN.Matrix4x4.Identity;
        int bone1 = Array.FindIndex(r.Names, n => n.EndsWith("bone1", StringComparison.OrdinalIgnoreCase));
        if (bone1 >= 0 && _windDroop > 0.01f)
        {
            SN.Vector3 pivot = world[bone1].Translation;
            droop = SN.Matrix4x4.CreateTranslation(-pivot) * SN.Matrix4x4.CreateRotationX(-_windDroop) * SN.Matrix4x4.CreateTranslation(pivot);
        }

        // petite caméra de trois quarts, tournée comme la caméra du jeu
        GraphicsDevice gd = GraphicsDevice;
        Viewport full = gd.Viewport;
        gd.Clear(ClearOptions.DepthBuffer, Color.Black, 1f, 0);
        gd.Viewport = new Viewport((int)x, (int)y, (int)size, (int)size);
        gd.DepthStencilState = DepthStencilState.Default;
        gd.BlendState = BlendState.Opaque;
        gd.RasterizerState = RasterizerState.CullNone;
        gd.SamplerStates[0] = SamplerState.LinearWrap;
        float pitch = Math.Clamp(_pitch, 0.3f, 0.75f);
        SN.Vector3 target = new SN.Vector3(0, 0, 300);
        SN.Vector3 eye = target + 680f * new SN.Vector3(MathF.Cos(pitch) * MathF.Cos(_yaw), MathF.Cos(pitch) * MathF.Sin(_yaw), MathF.Sin(pitch));
        Matrix view = SN.Matrix4x4.CreateLookAt(eye, target, SN.Vector3.UnitZ).X();
        Matrix proj = SN.Matrix4x4.CreatePerspectiveFieldOfView(40f * MathF.PI / 180, 1f, 20f, 10000f).X();
        foreach (IEffectMatrices e in new IEffectMatrices[] { _lit, _plain, _alphaFx })
        {
            e.View = view;
            e.Projection = proj;
        }
        SN.Matrix4x4 place = SN.Matrix4x4.CreateRotationZ(angle);
        foreach (CharacterRig.Part part in r.Parts)
        {
            if (r.Names[part.Node].Contains("bone", StringComparison.OrdinalIgnoreCase))
                continue;   // os de la manche : repères du squelette, pas à dessiner
            if (part.Skin != null)
            {
                if (!_windSock.Skinned.TryGetValue(part, out (MeshGpu mesh, float[] pos, float[] nrm) sk))
                {
                    MeshGpu gm = Upload(_assets.LoadMesh(_mission, part.Mesh, "gfx"), dynamic: true);
                    sk = (gm, new float[part.BasePos.Length], new float[part.BaseNrm.Length]);
                    _windSock.Skinned[part] = sk;
                }
                WindSockDeform(r, part, world, droop, sk.pos, sk.nrm);
                sk.mesh.UpdateVertices(sk.pos, sk.nrm);
                DrawMesh(sk.mesh, place.X());
            }
            else
            {
                if (!_partMeshes.TryGetValue(part.Mesh, out MeshGpu? mesh))
                {
                    try { mesh = Upload(_assets.LoadMesh(_mission, part.Mesh, "gfx")); } catch { mesh = null; }
                    _partMeshes[part.Mesh] = mesh;
                }
                if (mesh != null)
                    DrawMesh(mesh, (world[part.Node] * place).X());
            }
        }
        gd.Viewport = full;
        foreach (IEffectMatrices e in new IEffectMatrices[] { _lit, _plain, _alphaFx })
        {
            e.View = _view.X();
            e.Projection = _proj.X();
        }
    }

    /// <summary>
    /// Peau de la manche : ses sommets sont rangés dans le repère de son nœud (Manchon_tissus), ses os dans celui du
    /// modèle ; on passe d'abord les sommets dans le repère du modèle (pose de référence du nœud), puis on applique les os.
    /// </summary>
    private static void WindSockDeform(CharacterRig r, CharacterRig.Part p, SN.Matrix4x4[] world, SN.Matrix4x4 droop, float[] pos, float[] nrm)
    {
        Skin s = p.Skin!;
        SN.Matrix4x4 node = SN.Matrix4x4.Identity;
        for (int k = p.Node, depth = 0; k >= 0 && depth < 64; k = r.Parent[k], depth++)
            node = node * r.BindLocal[k];
        SN.Matrix4x4[] mats = new SN.Matrix4x4[s.Bones.Length];
        for (int b = 0; b < mats.Length; b++)
            mats[b] = node * p.InvBind[b] * world[p.BoneNode[b]] * droop;
        int n = s.Start.Length - 1;
        for (int v = 0; v < n; v++)
        {
            SN.Vector3 bp = new SN.Vector3(p.BasePos[3 * v], p.BasePos[3 * v + 1], p.BasePos[3 * v + 2]);
            SN.Vector3 bn = new SN.Vector3(p.BaseNrm[3 * v], p.BaseNrm[3 * v + 1], p.BaseNrm[3 * v + 2]);
            SN.Vector3 op = SN.Vector3.Zero;
            SN.Vector3 on = SN.Vector3.Zero;
            for (int k = s.Start[v]; k < s.Start[v + 1]; k++)
            {
                op += SN.Vector3.Transform(bp, mats[s.Bone[k]]) * s.Weight[k];
                on += SN.Vector3.TransformNormal(bn, mats[s.Bone[k]]) * s.Weight[k];
            }
            if (s.Start[v + 1] == s.Start[v])
            {
                op = SN.Vector3.Transform(bp, node * droop);
                on = SN.Vector3.TransformNormal(bn, node * droop);
            }
            pos[3 * v] = op.X;
            pos[3 * v + 1] = op.Y;
            pos[3 * v + 2] = op.Z;
            nrm[3 * v] = on.X;
            nrm[3 * v + 1] = on.Y;
            nrm[3 * v + 2] = on.Z;
        }
    }
}
