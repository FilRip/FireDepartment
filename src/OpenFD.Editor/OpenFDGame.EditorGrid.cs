using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using OpenFD.Formats;
using OpenFD.Game;
using SN = System.Numerics;

namespace OpenFD.MG;

// éditeur : la grille de marche (fichier .grid de la mission, cases d'1 m). Bouton « Voir grille » : vue de dessus (sans
// toits), où un clic choisit une case, et un clic sur la case déjà choisie la fait passer de « marcher » à « mur » (croix
// rouge : zone A = 0, comme le jeu d'origine), puis à « porte » (trait oblique), puis de nouveau à « marcher ». Une case « porte » reste une case où l'on
// marche pour le moteur (zone A différente de 0) : elle marque l'emplacement d'une porte à poser, elle ne la remplace pas.
// Objet importé (Placeable\Import\…) posé dans la scène : ses murs (faces à hauteur d'homme) deviennent des croix rouges,
// et les trous entre deux murs des portes ; déplacé ou enlevé, ses croix suivent ou partent.
public sealed partial class OpenFDGame
{
    /// <summary>Zone A d'une case « porte » (une valeur que les grilles du jeu n'utilisent pas).</summary>
    public const ushort DoorZone = 65000;

    private enum GridCell { Walk, Wall, Door }

    private bool _edGridView;
    private (float yaw, float pitch, bool roofs) _edGridSaved;
    private GridCell? _edGridPaint;                    // état donné à la case cliquée, puis aux cases survolées bouton tenu
    private int _edGridLast = -1;
    private int _edGridHover = -1;
    // zone A d'origine des cases changées (pour rendre « marcher » avec sa vraie zone)
    private readonly Dictionary<int, ushort> _edGridOriginal = new();
    // croix posées par chaque objet importé : case, zone avant, zone posée
    private readonly Dictionary<uint, List<(int cell, ushort before, ushort set)>> _edGridAuto = new();
    // points des murs d'un modèle (repère du modèle), tous les 25 cm, et milieux des trous de porte
    private readonly Dictionary<string, (List<SN.Vector2> walls, List<SN.Vector2> doors)> _edWallPoints = new(StringComparer.OrdinalIgnoreCase);

    private void EdToggleGridView()
    {
        EdCommitFocus();
        if (!_edGridView)
        {
            if (_session.Grid == null)
                return;
            _edGridSaved = (_yaw, _pitch, _roofs);
            _edGridView = true;
            _map = false;
            _roofs = false;
            _yaw = -MathF.PI / 2;   // caméra au sud, qui regarde vers le nord : le nord (+Y) en haut de l'écran
            _pitch = 1.5f;
            EdSelect(null);
            EdMessage(Tr("GridViewHelp", "Grille : un clic choisit une case ; un clic sur la case choisie la change : sol → mur (croix rouge) → porte (trait) → sol ; bouton tenu : les cases survolées aussi"));
        }
        else
        {
            _edGridView = false;
            (_yaw, _pitch, _roofs) = _edGridSaved;
            _edGridPaint = null;
            _edCell = null;
        }
    }

    private GridCell CellState(Grid g, int i) => g.ZoneA[i] == 0 ? GridCell.Wall : g.ZoneA[i] == DoorZone ? GridCell.Door : GridCell.Walk;

    private void SetCell(Grid g, int i, GridCell s)
    {
        if (CellState(g, i) == s)
            return;
        _edGridOriginal.TryAdd(i, g.ZoneA[i]);
        g.ZoneA[i] = s switch
        {
            GridCell.Wall => 0,
            GridCell.Door => DoorZone,
            _ => WalkZone(g, i),
        };
    }

    // zone d'une case rendue praticable : celle d'origine si elle l'était, sinon celle d'une voisine, sinon 1
    private ushort WalkZone(Grid g, int i)
    {
        if (_edGridOriginal.TryGetValue(i, out ushort z) && z != 0 && z != DoorZone)
            return z;
        int x = i % g.Width, y = i / g.Width;
        foreach ((int dx, int dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            if (g.Inside(x + dx, y + dy) && g.ZoneA[g.Index(x + dx, y + dy)] is var n && n != 0 && n != DoorZone)
                return n;
        return 1;
    }

    /// <summary>Souris dans la vue grille : clic sur une case (et bouton tenu : les cases survolées prennent le même état).</summary>
    private void EdGridMouse(MouseState m, bool inView)
    {
        _edGridHover = -1;
        if (_session.Grid is not { } g)
            return;
        int cell = -1;
        if (inView && Ground(m.X, m.Y) is { } p)
        {
            (int cx, int cy) = g.CellOf(new SN.Vector3(p, 0));
            if (g.Inside(cx, cy))
                cell = g.Index(cx, cy);
        }
        _edGridHover = cell;
        if (m.LeftButton == ButtonState.Released)
        {
            _edGridPaint = null;
            _edGridLast = -1;
            _edPressL = false;
            return;
        }
        if (_edPressL && cell >= 0)
        {
            _edPressL = false;
            (int x, int y) clicked = (cell % g.Width, cell / g.Width);
            _edGridLast = cell;
            if (_edCell != clicked)
            {
                // autre case : on la choisit seulement (ses propriétés à droite), sans la changer
                EdCommitFocus();
                _edCell = clicked;
                _edGridPaint = null;
            }
            else
            {
                // case déjà choisie : elle change (sol → mur → porte → sol) ; bouton tenu, les cases survolées aussi
                GridCell next = CellState(g, cell) switch { GridCell.Walk => GridCell.Wall, GridCell.Wall => GridCell.Door, _ => GridCell.Walk };
                _edGridPaint = next;
                SetCell(g, cell, next);
            }
            EdGridCellMessage(g, cell);
        }
        else if (_edGridPaint is { } paint && cell >= 0 && cell != _edGridLast)
        {
            _edGridLast = cell;
            SetCell(g, cell, paint);
            _edCell = (cell % g.Width, cell / g.Width);
        }
    }

    private void EdGridCellMessage(Grid g, int i)
    {
        string state = CellState(g, i) switch
        {
            GridCell.Wall => Tr("GridWall", "mur"),
            GridCell.Door => Tr("GridDoor", "porte"),
            _ => Tr("GridWalk", "marcher"),
        };
        EdMessage(Tr("GridCellState", "Case ({0}, {1}) : {2}", i % g.Width, i / g.Width, state));
    }

    /// <summary>Vue grille : croix rouges (murs), traits obliques (portes), lignes des cases, case sous la souris.</summary>
    private void EdGridOverlay()
    {
        if (!_edGridView || _session.Grid is not { } g)
            return;
        const float C = Grid.CellSize;
        // cases visibles : autour du point visé, selon la distance de la caméra
        float half = MathF.Max(8, _dist * 0.9f / C);
        int x0 = Math.Max(0, (int)(_target.X / C - half)), x1 = Math.Min(g.Width - 1, (int)(_target.X / C + half));
        int y0 = Math.Max(0, (int)(_target.Y / C - half)), y1 = Math.Min(g.Height - 1, (int)(_target.Y / C + half));
        Color red = new Color(235, 40, 30), door = new Color(255, 200, 40), line = new Color(90, 90, 100);
        bool lines = _dist < 6000;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int i = g.Index(x, y);
                float bx = x * C, by = y * C, z = g.GroundHeight[i] + 8;
                if (lines)
                {
                    EdLine3(new(bx, by, z), new(bx + C, by, z), line);
                    EdLine3(new(bx, by, z), new(bx, by + C, z), line);
                }
                switch (CellState(g, i))
                {
                    case GridCell.Wall:
                        EdLine3(new(bx + 15, by + 15, z), new(bx + 85, by + 85, z), red);
                        EdLine3(new(bx + 85, by + 15, z), new(bx + 15, by + 85, z), red);
                        break;
                    case GridCell.Door:
                        EdLine3(new(bx + 15, by + 15, z), new(bx + 85, by + 85, z), door);
                        break;
                }
            }
        if (_edGridHover >= 0 && _edGridHover < g.ZoneA.Length)
        {
            int hx = _edGridHover % g.Width, hy = _edGridHover / g.Width;
            float bx = hx * C, by = hy * C, z = g.GroundHeight[_edGridHover] + 10;
            Color yellow = new Color(255, 230, 80);
            EdLine3(new(bx, by, z), new(bx + C, by, z), yellow);
            EdLine3(new(bx + C, by, z), new(bx + C, by + C, z), yellow);
            EdLine3(new(bx + C, by + C, z), new(bx, by + C, z), yellow);
            EdLine3(new(bx, by + C, z), new(bx, by, z), yellow);
        }
    }

    // ---- croix posées automatiquement par les objets importés -----------------------------------------------------

    private static bool IsImported(EditorObject e) =>
        e.Model != null && e.Model.StartsWith("Placeable\\" + ObjImport.Folder + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Points des murs d'un modèle (repère du modèle) : sous ses faces à hauteur d'homme, tous les 25 cm.</summary>
    private (List<SN.Vector2> walls, List<SN.Vector2> doors) WallPoints(string model)
    {
        if (_edWallPoints.TryGetValue(model, out (List<SN.Vector2> walls, List<SN.Vector2> doors) known))
            return known;
        const float Step = 25, Low = 30, High = 170;
        HashSet<(int, int)> set = new HashSet<(int, int)>();
        try
        {
            foreach (SceneAssets.Node part in _assets.ModelParts(model))
            {
                if (part.Mesh.Contains("ROOF", StringComparison.OrdinalIgnoreCase))
                    continue;
                Mesh m = _assets.LoadMesh(_mission, part.Mesh, "gfx").Mesh;
                SN.Vector3[] v = new SN.Vector3[m.VertexCount];
                for (int i = 0; i < v.Length; i++)
                    v[i] = SN.Vector3.Transform(new SN.Vector3(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]), part.World);
                for (int t = 0; t + 2 < m.Indices.Length; t += 3)
                {
                    SN.Vector3 a = v[m.Indices[t]], b = v[m.Indices[t + 1]], c = v[m.Indices[t + 2]];
                    SN.Vector3 n = SN.Vector3.Cross(b - a, c - a);
                    // murs : faces à peu près verticales qui passent à hauteur d'homme (pas les sols ni les plafonds)
                    if (n.LengthSquared() < 1e-6f || MathF.Abs(SN.Vector3.Normalize(n).Z) > 0.5f)
                        continue;
                    float zmin = MathF.Min(a.Z, MathF.Min(b.Z, c.Z)), zmax = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
                    if (zmin > High || zmax < Low)
                        continue;
                    float len = MathF.Max(SN.Vector3.Distance(a, b), MathF.Max(SN.Vector3.Distance(a, c), SN.Vector3.Distance(b, c)));
                    int k = Math.Clamp((int)(len / Step) + 1, 1, 400);
                    for (int i = 0; i <= k; i++)
                        for (int j = 0; j <= k - i; j++)
                        {
                            SN.Vector3 p = a + (b - a) * ((float)i / k) + (c - a) * ((float)j / k);
                            if (p.Z < Low - 20 || p.Z > High + 20)
                                continue;
                            set.Add(((int)MathF.Floor(p.X / Step), (int)MathF.Floor(p.Y / Step)));
                        }
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"Grille : murs de {model} : {e.Message}");
        }
        List<SN.Vector2> pts = set.Select(k => new SN.Vector2((k.Item1 + 0.5f) * Step, (k.Item2 + 0.5f) * Step)).ToList();
        // trous de porte : sur une ligne de points (dans un sens ou dans l'autre), un vide de 50 cm à 2 m entre deux bouts
        // de mur qui suivent cette ligne sur 75 cm au moins (un mur perpendiculaire, lui, ne fait qu'un point sur la ligne :
        // l'intérieur d'un couloir n'est pas pris pour une porte) ; on garde le milieu du trou (et ses côtés s'il est large)
        List<SN.Vector2> doors = new List<SN.Vector2>();
        foreach (bool alongX in new[] { true, false })
            foreach (IGrouping<int, int> row in set.GroupBy(k => alongX ? k.Item2 : k.Item1, k => alongX ? k.Item1 : k.Item2))
            {
                int[] xs = row.OrderBy(v => v).ToArray();
                for (int i = 0; i + 1 < xs.Length; i++)
                {
                    int gap = xs[i + 1] - xs[i] - 1;
                    if (gap < 2 || gap > 8)
                        continue;
                    int left = 1, right = 1;
                    while (i - left >= 0 && xs[i - left] == xs[i] - left)
                        left++;
                    while (i + 1 + right < xs.Length && xs[i + 1 + right] == xs[i + 1] + right)
                        right++;
                    if (left < 3 || right < 3)
                        continue;
                    float a = (xs[i] + 1) * Step, b = xs[i + 1] * Step, mid = (a + b) / 2, line = (row.Key + 0.5f) * Step;
                    List<float> at = new List<float> { mid };
                    if (b - a >= 150)
                    {
                        at.Add(mid - 50);
                        at.Add(mid + 50);
                    }
                    foreach (float u in at)
                        doors.Add(alongX ? new SN.Vector2(u, line) : new SN.Vector2(line, u));
                }
            }
        _edWallPoints[model] = (pts, doors);
        return (pts, doors);
    }

    /// <summary>Retire les croix posées par un objet importé (celles que l'on n'a pas changées depuis à la main).</summary>
    private void EdGridUnmark(EditorObject e)
    {
        if (_session.Grid is not { } g || !_edGridAuto.Remove(e.Source.Id, out List<(int cell, ushort before, ushort set)>? marks))
            return;
        foreach ((int cell, ushort before, ushort set) in marks)
            if (cell < g.ZoneA.Length && g.ZoneA[cell] == set)
                g.ZoneA[cell] = before;
    }

    /// <summary>
    /// Objet importé posé ou déplacé : ses murs ferment les cases qu'ils traversent (au moins deux points sur seize), et
    /// les cases des trous dans les murs (50 cm à 2 m) deviennent des portes.
    /// </summary>
    private void EdGridMark(EditorObject e)
    {
        if (_session.Grid is not { } g || e.Model == null || !IsImported(e))
            return;
        EdGridUnmark(e);
        (List<SN.Vector2> pts, List<SN.Vector2> doorPts) = WallPoints(e.Model);
        if (pts.Count == 0)
            return;
        Dictionary<int, int> count = new Dictionary<int, int>();
        foreach (SN.Vector2 p in pts)
        {
            SN.Vector3 w = SN.Vector3.Transform(new SN.Vector3(p, 0), e.Shown);
            (int cx, int cy) = g.CellOf(w);
            if (g.Inside(cx, cy))
                count[g.Index(cx, cy)] = count.GetValueOrDefault(g.Index(cx, cy)) + 1;
        }
        HashSet<int> walls = count.Where(kv => kv.Value >= 2).Select(kv => kv.Key).ToHashSet();
        // trous de porte : leur case est une porte, même si un bout de mur y passe (objet pas aligné sur la grille)
        HashSet<int> doors = new HashSet<int>();
        foreach (SN.Vector2 p in doorPts)
        {
            (int cx, int cy) = g.CellOf(SN.Vector3.Transform(new SN.Vector3(p, 0), e.Shown));
            if (g.Inside(cx, cy))
                doors.Add(g.Index(cx, cy));
        }
        walls.ExceptWith(doors);
        List<(int cell, ushort before, ushort set)> marks = new();
        foreach (int c in walls)
        {
            if (g.ZoneA[c] == 0)
                continue;
            _edGridOriginal.TryAdd(c, g.ZoneA[c]);
            marks.Add((c, g.ZoneA[c], 0));
            g.ZoneA[c] = 0;
        }
        foreach (int c in doors)
        {
            if (g.ZoneA[c] == DoorZone)
                continue;
            _edGridOriginal.TryAdd(c, g.ZoneA[c]);
            marks.Add((c, g.ZoneA[c], DoorZone));
            g.ZoneA[c] = DoorZone;
        }
        _edGridAuto[e.Source.Id] = marks;
    }
}
