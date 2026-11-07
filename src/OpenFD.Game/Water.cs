using System.Numerics;
using OpenFD.Formats;

namespace OpenFD.Game;

/// <summary>
/// Eau du décor (lac et rivière de Sawmill, mer de TMI) : la grille du jeu ne la distingue pas ; ses cases sont celles
/// que recouvre un maillage d'eau (shader « FD2Water ») et dont le sol est à la hauteur de l'eau. Les bateaux
/// ([UNIT] Type=FIREBOAT) n'y roulent que là ; les piétons et les camions n'y vont pas.
/// </summary>
public sealed partial class UnitManager
{
    /// <summary>Tolérance entre la hauteur du sol de la case et celle de l'eau.</summary>
    private const float WaterTolerance = 20f;

    private void FindWater()
    {
        if (_s.Grid is not { } g)
            return;
        bool[] water = new bool[g.Width * g.Height];
        int count = 0;
        try
        {
            SceneAssets assets = _assets ??= new SceneAssets(_files);
            foreach (SceneAssets.Node n in assets.Scenery(_s.Mission).Where(n => n.Mesh.Contains("water", StringComparison.OrdinalIgnoreCase)))
            {
                SceneAssets.MeshAsset a = assets.LoadMesh(_s.Mission, n.Mesh, null);
                if (!a.Mesh.Shader.Contains("Water", StringComparison.OrdinalIgnoreCase))
                    continue;
                Mesh m = a.Mesh;
                Vector3 P(uint v) => Vector3.Transform(new Vector3(m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2]), n.World);
                for (int k = 0; k + 2 < m.Indices.Length; k += 3)
                    count += MarkTriangle(g, water, P(m.Indices[k]), P(m.Indices[k + 1]), P(m.Indices[k + 2]));
            }
        }
        catch (Exception e)
        {
            Log.Add($"Eau : {e.Message}");
        }
        if (count > 0)
        {
            Paths.Water = water;
            Log.Add($"Eau : {count} case(s)");
        }
    }

    // cases de la grille dont le centre est dans le triangle et le sol à la hauteur de l'eau
    private static int MarkTriangle(Grid g, bool[] water, Vector3 a, Vector3 b, Vector3 c)
    {
        int n = 0;
        int x0 = Math.Max(0, (int)(MathF.Min(a.X, MathF.Min(b.X, c.X)) / Grid.CellSize)), x1 = Math.Min(g.Width - 1, (int)(MathF.Max(a.X, MathF.Max(b.X, c.X)) / Grid.CellSize));
        int y0 = Math.Max(0, (int)(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) / Grid.CellSize)), y1 = Math.Min(g.Height - 1, (int)(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) / Grid.CellSize));
        float z = (a.Z + b.Z + c.Z) / 3;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                Vector2 p = new Vector2((x + 0.5f) * Grid.CellSize, (y + 0.5f) * Grid.CellSize);
                int k = g.Index(x, y);
                if (water[k] || MathF.Abs(g.GroundHeight[k] - z) > WaterTolerance || !InTriangle(p, a, b, c))
                    continue;
                water[k] = true;
                n++;
            }
        return n;
    }

    private static bool InTriangle(Vector2 p, Vector3 a, Vector3 b, Vector3 c)
    {
        float d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    private static float Cross(Vector2 p, Vector3 a, Vector3 b) => (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);

    /// <summary>Bateau posé sur la terre (point d'arrivée de la caserne) : remis sur l'eau la plus proche.</summary>
    private void PutOnWater(Unit boat)
    {
        Paths.PathingBoat = true;
        try
        {
            Vector2 p = Walkable(new Vector2(boat.Position.X, boat.Position.Y));
            boat.Position = new Vector3(p, Ground(p));
        }
        finally
        {
            Paths.PathingBoat = false;
        }
    }
}
