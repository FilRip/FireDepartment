using System.Numerics;
using OpenFD.Formats;

namespace OpenFD.Game;

// jets de flammes (FlameJet.cs) : classe avec une section [FLAMEJET]
//   LENGTH = longueur du jet (cm, défaut 1000), depuis l'objet, vers son axe X
//   WIDTH  = largeur de la gerbe (cm, défaut 120) ; plus de 100 cm : les cases de part et d'autre de la ligne aussi
//   HEIGHT = hauteur du jet au-dessus de l'objet (cm, défaut 0)
// Les cases traversées par le jet (toutes celles que la ligne touche : pas de passage en diagonale) sont fermées tant que
// l'objet est visible, par le même chemin que les zones d'occupation (Occupations.cs).
public sealed partial class UnitManager
{
    public List<FlameJet> FlameJets { get; } = new();

    private void LoadFlameJets(Level level, Grid g)
    {
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform))
        {
            ClassFile? c = Class("Design\\" + o.ClassPath["design\\".Length..] + ".class");
            if (c == null || !c.Sections.Contains("FLAMEJET", StringComparer.OrdinalIgnoreCase))
                continue;
            float length = c.GetFloat("FLAMEJET", "LENGTH", 1000), width = c.GetFloat("FLAMEJET", "WIDTH", 120), height = c.GetFloat("FLAMEJET", "HEIGHT", 0);
            Vector3 dir = Vector3.TransformNormal(Vector3.UnitX, o.Matrix);
            dir.Z = 0;
            dir = dir.LengthSquared() > 1e-6f ? Vector3.Normalize(dir) : Vector3.UnitX;
            Vector3 start = o.Position + new Vector3(0, 0, height);
            FlameJet jet = new FlameJet { Id = o.Id, Name = o.Name, Start = start, End = start + dir * length, Width = width };
            Occupation oc = new Occupation { Id = o.Id, Name = o.Name, StartVisible = o.StartVisible };
            HashSet<int> cells = new HashSet<int>();
            Vector3 side = new Vector3(-dir.Y, dir.X, 0);
            float halfSide = width > 100 ? width / 2 - 50 : 0;
            int steps = Math.Max(1, (int)(length / 10));
            for (int i = 0; i <= steps; i++)
                for (float s = -halfSide; s <= halfSide + 0.1f; s += 50)
                {
                    Vector3 p = start + dir * (length * i / steps) + side * s;
                    (int cx, int cy) = g.CellOf(p);
                    if (g.Inside(cx, cy))
                        cells.Add(g.Index(cx, cy));
                }
            oc.Cells.AddRange(cells);
            HashSet<int> wide = new HashSet<int>();
            foreach (int i in cells)
            {
                int x = i % g.Width, y = i / g.Width;
                for (int ax = -1; ax <= 1; ax++)
                    for (int ay = -1; ay <= 1; ay++)
                        if (g.Inside(x + ax, y + ay))
                            wide.Add(g.Index(x + ax, y + ay));
            }
            oc.VehicleCells.AddRange(wide);
            jet.Zone = oc;
            Occupations.Add(oc);
            FlameJets.Add(jet);
            Log.Add($"Jet de flammes : {o.Name} ({length:0} cm, {oc.Cells.Count} case(s), {(o.StartVisible ? "allumé" : "éteint")} au départ)");
        }
    }
}
