using OpenFD.Game;
using SN = System.Numerics;

namespace OpenFD.MG;

// hauteur réelle du sol : la grille de la mission donne une hauteur par case, mais certaines cartes (Tutorial : la cour
// de la caserne) ont un sol dessiné plus haut que celui de la grille ; les véhicules s'enfonçaient alors dans le bitume.
// On relève, pour chaque case, la face tournée vers le haut la plus haute des maillages du sol (GROUND, Tile_ground,
// routes, trottoirs…) proche de la hauteur de la grille, et la simulation pose les unités dessus.
public sealed partial class Fd3Game
{
    /// <summary>
    /// Objet posé sur le sol de la grille alors que le sol dessiné est plus haut (Tutorial) : remonté sur le sol dessiné.
    /// Les objets en hauteur (étagères, toits…) ne bougent pas.
    /// </summary>
    private SN.Matrix4x4 Lift(SN.Matrix4x4 w)
    {
        if (_session?.Grid is not { } g)
            return w;
        SN.Vector3 t = w.Translation;
        (int cx, int cy) = g.CellOf(t);
        if (!g.Inside(cx, cy))
            return w;
        float grid = g.GroundHeight[g.Index(cx, cy)], surf = _session.Units.GroundZ(new(t.X, t.Y));
        if (surf - grid < 20 || surf - grid > 160 || MathF.Abs(t.Z - grid) > 40 || t.Z >= surf)
            return w;   // (Eurotunnel : objets du niveau bas laissés en place)
        w.M43 += surf - t.Z;
        return w;
    }

    // face verticale d'un mur : points pris dans la bande 0,6 – 1,7 m au-dessus du sol de la grille ; chaque case touchée
    // en plusieurs points est un mur (les cloisons des pièces, que la grille du jeu ne ferme pas toujours)
    private static void WallFace(OpenFD.Formats.Grid g, SN.Vector3 a, SN.Vector3 b, SN.Vector3 c, HashSet<int> cells)
    {
        float area = SN.Vector3.Cross(b - a, c - a).Length() / 2;
        if (area < 400)
            return;   // petits détails (plinthes, moulures)
        // tranches de mur (montants d'une porte ou d'un passage, épaisseur d'une cloison) : étroites à l'horizontale ;
        // elles fermeraient le passage lui-même
        float span = MathF.Max(SN.Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y)), MathF.Max(SN.Vector2.Distance(new(b.X, b.Y), new(c.X, c.Y)), SN.Vector2.Distance(new(a.X, a.Y), new(c.X, c.Y))));
        if (span < 70)
            return;
        // vrai mur : du sol jusqu'au-dessus d'une tête (2,3 m) ; un comptoir, un muret ou une rampe (1,5 m) ne compte pas
        (int gx, int gy) = g.CellOf((a + b + c) / 3);
        if (!g.Inside(gx, gy))
            return;
        float zTop = MathF.Max(a.Z, MathF.Max(b.Z, c.Z)), zLow = MathF.Min(a.Z, MathF.Min(b.Z, c.Z));
        // sol du mur : le plus haut des cases voisines qui reste sous le pied du mur (la case du mur elle-même vaut 0
        // dans un bâtiment surélevé, comme la banque de London)
        float floor = g.GroundHeight[g.Index(gx, gy)];
        for (int ox = -2; ox <= 2; ox++)
            for (int oy = -2; oy <= 2; oy++)
                if (g.Inside(gx + ox, gy + oy) && g.ZoneA[g.Index(gx + ox, gy + oy)] != 0)
                {
                    float h0 = g.GroundHeight[g.Index(gx + ox, gy + oy)];
                    if (h0 > floor && h0 <= zLow + 60)
                        floor = h0;
                }
        if (zTop < floor + 230 || zLow > floor + 80)
            return;
        int n = Math.Clamp((int)(MathF.Sqrt(area) / 25), 3, 60);
        for (int i = 0; i <= n; i++)
            for (int j = 0; i + j <= n; j++)
            {
                float u = (float)i / n, v = (float)j / n;
                SN.Vector3 p = a + (b - a) * u + (c - a) * v;
                (int cx, int cy) = g.CellOf(p);
                if (!g.Inside(cx, cy))
                    continue;
                int k = g.Index(cx, cy);
                float h = p.Z - floor;
                if (h >= 60 && h <= 170)
                    cells.Add(k);
            }
    }

    private void BuildSurface(string mission, IReadOnlyList<SceneAssets.Node> scenery)
    {
        if (_session.Grid is not { } g)
            return;
        int n = g.Width * g.Height;
        float[] surf = new float[n]; Array.Fill(surf, float.NaN);
        // plancher le plus bas des autres maillages (pièces des bâtiments) : sert là où aucun maillage du sol ne passe
        // (Eurotunnel : le sol des bâtiments est 4 m au-dessus de la hauteur de la grille)
        float[] low = new float[n]; Array.Fill(low, float.NaN);
        HashSet<int> wallCells = new HashSet<int>();   // cases traversées par un mur du décor à hauteur d'homme (cloisons)
        const float Cell = OpenFD.Formats.Grid.CellSize;
        string? dbg = Environment.GetEnvironmentVariable("FD3_SOL");
        System.Text.RegularExpressions.Regex groundRx = new System.Text.RegularExpressions.Regex(@"GROUND|FGD_|tile|road|street|walk|parking|asphalt", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (SceneAssets.Node node in scenery)
        {
            bool isGround = groundRx.IsMatch(node.Mesh);
            OpenFD.Formats.Mesh m;
            try { m = _assets.LoadMesh(mission, node.Mesh, null).Mesh; } catch { continue; }
            uint[] idx = m.Indices;
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                SN.Vector3 P(int i) => SN.Vector3.Transform(new SN.Vector3(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]), node.World);
                SN.Vector3 a = P((int)idx[t]); SN.Vector3 b = P((int)idx[t + 1]); SN.Vector3 c = P((int)idx[t + 2]);
                SN.Vector3 nrm = SN.Vector3.Cross(b - a, c - a);
                float len = nrm.Length();
                if (len < 1e-3f)
                    continue;
                if (!isGround && MathF.Abs(nrm.Z) / len < 0.25f)
                {
                    int before = wallCells.Count; string? probe = Environment.GetEnvironmentVariable("FD3_MUR");
                    if (probe != null)
                    {
                        int[] pc = probe.Split(',').Select(int.Parse).ToArray(); HashSet<int> tmp = new HashSet<int>();
                        WallFace(g, a, b, c, tmp);
                        if (tmp.Contains(g.Index(pc[0], pc[1])))
                            Console.WriteLine($"mur case {pc[0]},{pc[1]} : {node.Mesh} ({a.X:0},{a.Y:0},{a.Z:0}) ({b.X:0},{b.Y:0},{b.Z:0}) ({c.X:0},{c.Y:0},{c.Z:0})");
                    }
                    WallFace(g, a, b, c, wallCells); continue;   // mur : à hauteur d'homme ?
                }
                if (MathF.Abs(nrm.Z) / len < 0.8f)
                    continue;   // seulement les faces presque horizontales
                float x0 = MathF.Min(a.X, MathF.Min(b.X, c.X)), x1 = MathF.Max(a.X, MathF.Max(b.X, c.X));
                float y0 = MathF.Min(a.Y, MathF.Min(b.Y, c.Y)), y1 = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));
                int cx0 = Math.Max(0, (int)MathF.Ceiling(x0 / Cell - 0.5f)), cx1 = Math.Min(g.Width - 1, (int)MathF.Floor(x1 / Cell - 0.5f));
                int cy0 = Math.Max(0, (int)MathF.Ceiling(y0 / Cell - 0.5f)), cy1 = Math.Min(g.Height - 1, (int)MathF.Floor(y1 / Cell - 0.5f));
                if (cx1 < cx0 || cy1 < cy0)
                    continue;
                float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                if (MathF.Abs(d) < 1e-6f)
                    continue;
                for (int cy = cy0; cy <= cy1; cy++)
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        float px = (cx + 0.5f) * Cell, py = (cy + 0.5f) * Cell;
                        float l1 = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / d;
                        float l2 = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / d;
                        float l3 = 1 - l1 - l2;
                        if (l1 < -1e-4f || l2 < -1e-4f || l3 < -1e-4f)
                            continue;
                        float z = l1 * a.Z + l2 * b.Z + l3 * c.Z;
                        int k = g.Index(cx, cy);
                        float grid = g.GroundHeight[k];
                        // sol plausible : un peu au-dessus de la grille au plus (bitume, trottoir, dalle), jamais une table ou un toit
                        if (dbg != null && cx == int.Parse(dbg.Split(',')[0]) && cy == int.Parse(dbg.Split(',')[1]))
                            Console.WriteLine($"face case {cx},{cy} : {node.Mesh} z {z:0} (grille {grid:0}) sol? {isGround}");
                        if (!isGround)
                        {
                            int kk = g.Index(cx, cy);
                            if (z >= g.GroundHeight[kk] - 60 && z <= g.GroundHeight[kk] + 500 && (float.IsNaN(low[kk]) || z < low[kk]))
                                low[kk] = z;
                            continue;
                        }
                        if (z < grid - 60 || z > grid + 450)
                            continue;
                        if (float.IsNaN(surf[k]) || z > surf[k])
                            surf[k] = z;
                        if (dbg != null && cx == int.Parse(dbg.Split(',')[0]) && cy == int.Parse(dbg.Split(',')[1]))
                            Console.WriteLine($"sol case {cx},{cy} : {node.Mesh} z {z:0} (grille {grid:0})");
                    }
            }
        }
        for (int k = 0; k < n; k++)
            if (float.IsNaN(surf[k]) && !float.IsNaN(low[k]) && low[k] > g.GroundHeight[k] + 20)
                surf[k] = low[k];
        _session.Units.SetSurface(surf);
        if (Environment.GetEnvironmentVariable("FD3_SANS_CLOISONS") == null)
            _session.Units.AddWallCells(wallCells);
        int raised = 0; for (int k = 0; k < n; k++) if (!float.IsNaN(surf[k]) && surf[k] > g.GroundHeight[k] + 20) raised++;
        if (raised > 0)
            Console.WriteLine($"Sol : {raised} case(s) plus haute(s) que la grille (bitume, dalles)");
    }
}
