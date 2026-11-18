using OpenFD.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SN = System.Numerics;

namespace OpenFD.MG;

// gyrophares des véhicules : pièces « giro… » du modèle (éventail de rayons lumineux), qui tournent sur elles-mêmes,
// dessinées en lumière ajoutée (leurs textures giro_rouge, giro_bleu, giro_blanc sont des dégradés sur fond noir) ;
// phares avant (« Phare_… ») et halos (« …Glow… ») de la même façon, mais fixes
public sealed partial class OpenFDGame
{
    private readonly Dictionary<string, List<(MeshGpu mesh, SN.Matrix4x4 part, bool spin)>> _beaconModels = new(StringComparer.OrdinalIgnoreCase);
    private BasicEffect? _beaconFx;

    /// <summary>Tours par seconde d'un gyrophare.</summary>
    private const float BeaconTurnsPerSecond = 1.2f;

    /// <summary>
    /// Mission quittée : les gyrophares chargés sont oubliés (leurs textures sont libérées avec la mission ; gardés, ils
    /// ne s'afficheraient plus en relançant une mission).
    /// </summary>
    private void ClearBeacons()
    {
        foreach (List<(MeshGpu mesh, SN.Matrix4x4 part, bool spin)> l in _beaconModels.Values)
            foreach ((MeshGpu mesh, _, _) in l)
                mesh.Dispose();
        _beaconModels.Clear();
    }

    private List<(MeshGpu mesh, SN.Matrix4x4 part, bool spin)> BeaconModel(string model)
    {
        if (_beaconModels.TryGetValue(model, out List<(MeshGpu mesh, SN.Matrix4x4 part, bool spin)>? l))
            return l;
        l = new();
        try
        {
            foreach (SceneAssets.Node part in _assets.BeaconParts(model))
            {
                // seuls les gyrophares tournent ; phares et halos restent fixes
                bool spin = part.Mesh.Contains("giro", StringComparison.OrdinalIgnoreCase);
                l.Add((Upload(_assets.LoadMesh(_mission, part.Mesh, "gfx")), part.World, spin));
            }
        }
        catch (Exception e) { Console.WriteLine($"{model} (gyrophares) : {e.Message}"); }
        return _beaconModels[model] = l;
    }

    /// <summary>Gyrophares, phares et halos des véhicules en état de marche (ni détruits, ni cachés) : après les objets opaques.</summary>
    private void RenderBeacons()
    {
        if (_session == null)
            return;
        GraphicsDevice gd = GraphicsDevice;
        _beaconFx ??= new BasicEffect(gd) { TextureEnabled = true, LightingEnabled = false, VertexColorEnabled = false, FogEnabled = false };
        _beaconFx.View = _view.X();
        _beaconFx.Projection = _proj.X();
        BlendState blend = gd.BlendState;
        DepthStencilState depth = gd.DepthStencilState;
        RasterizerState raster = gd.RasterizerState;
        gd.BlendState = BlendState.Additive;
        gd.DepthStencilState = DepthStencilState.DepthRead;
        gd.RasterizerState = RasterizerState.CullNone;
        try
        {
            foreach (Unit u in _session.Units.Active.Where(u => u.IsVehicle && !u.Destroyed && !u.Hidden))
            {
                List<(MeshGpu mesh, SN.Matrix4x4 part, bool spin)> parts = BeaconModel(u.Model);
                if (parts.Count == 0)
                    continue;
                (SN.Vector3 p, float h) vp = PoseOf(u, u.Position, u.Heading);
                SN.Matrix4x4 place = SN.Matrix4x4.CreateRotationZ(vp.h + ModelYaw) * SN.Matrix4x4.CreateTranslation(vp.p);
                for (int i = 0; i < parts.Count; i++)
                {
                    // chaque gyrophare tourne avec un petit décalage, comme sur un vrai véhicule
                    float angle = (_clock * BeaconTurnsPerSecond + i * 0.37f + (u.Id % 7) * 0.13f) * MathF.Tau;
                    SN.Matrix4x4 turn = parts[i].spin ? SN.Matrix4x4.CreateRotationZ(angle) : SN.Matrix4x4.Identity;
                    SN.Matrix4x4 world = turn * parts[i].part * place;
                    DrawBeacon(parts[i].mesh, world.X());
                }
            }
        }
        finally
        {
            gd.BlendState = blend;
            gd.DepthStencilState = depth;
            gd.RasterizerState = raster;
        }
    }

    private void DrawBeacon(MeshGpu mesh, Matrix world)
    {
        GraphicsDevice gd = GraphicsDevice;
        gd.SetVertexBuffer(mesh.Vb);
        gd.Indices = mesh.Ib;
        _beaconFx!.World = world;
        foreach ((int start, int count, Texture2D? tex, _, _) in mesh.Groups)
        {
            if (tex == null)
                continue;
            _beaconFx.Texture = tex;
            foreach (EffectPass pass in _beaconFx.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, start, count / 3);
            }
        }
    }
}
