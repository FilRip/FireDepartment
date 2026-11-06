using System.Numerics;
using OpenFD.Fire;
using OpenFD.Formats;

namespace OpenFD.Game;

/// <summary>
/// Recherche de chemin sur la grille de la mission : cases libres ou fermées (murs de la grille, objets posés, cloisons,
/// murs des pièces, portes fermées, véhicules garés, murs à grappin), hauteur du sol, chemin le plus court (A*) lissé,
/// ligne droite libre entre deux points.
/// </summary>
public sealed class PathFind
{
    private readonly Session _s;
    private readonly SceneAssets _assets;
    private readonly Func<string, ClassFile?> _class;
    private readonly List<string> _log;

    public PathFind(Session s, SceneAssets assets, Func<string, ClassFile?> classLoader, List<string> log)
    {
        _s = s;
        _assets = assets;
        _class = classLoader;
        _log = log;
    }

    public float Ground(Vector2 p)
    {
        if (_s.Grid is not { } g)
            return 0;
        (int cx, int cy) = g.CellOf(new Vector3(p, 0));
        if (!g.Inside(cx, cy))
            return 0;
        int k = g.Index(cx, cy);
        return Surface != null && !float.IsNaN(Surface[k]) ? MathF.Max(Surface[k], g.GroundHeight[k]) : g.GroundHeight[k];
    }

    // hauteur du sol dessiné, case par case (relevée sur les maillages du décor) ; NaN : celle de la grille
    public float[]? Surface;

    // case libre ; pour un véhicule conduit par le joueur, il faut aussi la place autour (gabarit de 3 × 3 cases)
    public bool Free(int cx, int cy)
    {
        if (Clearance <= 0)
            return FreeCell(cx, cy);
        for (int dx = -Clearance; dx <= Clearance; dx++)
            for (int dy = -Clearance; dy <= Clearance; dy++)
                if (!FreeCell(cx + dx, cy + dy))
                    return false;
        return true;
    }
    /// <summary>Recherche en cours : gabarit autour de la case (véhicule conduit), véhicule qui cherche (ses propres cases restent libres).</summary>
    public int Clearance, SelfId;
    /// <summary>Recherche en cours : véhicule qui suit sa route (ignore les véhicules garés), pompier au grappin (passe les murs à grappin).</summary>
    public bool PathingVehicle, PathingGrapple;

    public bool FreeCell(int cx, int cy) => _s.Grid is not { } g || (g.Inside(cx, cy) && g.ZoneA[g.Index(cx, cy)] != 0 && (Blocked.Length == 0 || !Blocked[g.Index(cx, cy)])
        && (PathingVehicle || VehicleBlocked.Length == 0 || VehicleBlocked[g.Index(cx, cy)] == 0 || VehicleBlocked[g.Index(cx, cy)] == SelfId)
        && (DoorClosed.Length == 0 || !DoorClosed[g.Index(cx, cy)])
        && (PathingGrapple || !IsGrappleCell(g.Index(cx, cy))));   // mur à franchir au grappin : seulement avec un grappin

    /// <summary>Cases fermées par le décor (objets, cloisons, murs des pièces).</summary>
    public bool[] Blocked = Array.Empty<bool>();
    /// <summary>Véhicules garés : numéro du véhicule qui occupe la case (0 : libre).</summary>
    public int[] VehicleBlocked = Array.Empty<int>();
    /// <summary>Portes fermées (et bâti des portails coulissants).</summary>
    public bool[] DoorClosed = Array.Empty<bool>();
    /// <summary>Murs qu'on ne passe qu'au grappin.</summary>
    public bool[] GrappleCell = Array.Empty<bool>();
    public bool IsGrappleCell(int i) => i < GrappleCell.Length && GrappleCell[i];
    public bool IsVehicleBlocked(int index) => index < VehicleBlocked.Length && VehicleBlocked[index] != 0;
    public bool IsClosedDoor(int index) => index < DoorClosed.Length && DoorClosed[index];
    public int BlockedCells => Blocked.Count(b => b);
    /// <summary>Case où marche un pompier ordinaire (débogage : grille affichée avec la touche D).</summary>
    public bool WalkableCell(int cx, int cy) => FreeCell(cx, cy);
    public bool IsBlocked(int index) => index < Blocked.Length && Blocked[index];
    public Dictionary<int, string> BlockedBy { get; } = new();

    /// <summary>
    /// Obstacles des objets posés (voitures, meubles…) : la grille du jeu ne contient que les murs.
    /// Comme le jeu (section [OCCUPATION]) :
    ///  • mode « Rectangle » : Rect_SizeX × Rect_SizeY cases centrées sur l'objet (0 × 0 = ne bloque pas : herbes…) ;
    ///  • mode « AGO » (d'après la forme 3D) : les cases sous les faces du modèle situées à hauteur d'homme ;
    ///    les feuillages en hauteur, auvents ou enseignes ne gênent pas, les tapis et flaques non plus.
    /// </summary>
    /// <summary>
    /// Cloisons d'un bâtiment surélevé (banque de London, plancher à 4 m) : la grille leur donne la hauteur 0 sans les
    /// fermer. Une case nettement plus basse (1,5 m) que les cases de part et d'autre (à 1 ou 2 cases, dans un même axe)
    /// est un mur : on la bloque, sauf si une porte y passe (la porte la ferme ou l'ouvre elle-même).
    /// </summary>
    /// <summary>
    /// Les murs des pièces fermés d'après la zone B ne doivent couper aucun passage : une pièce qu'on atteignait avant
    /// (passage sans porte, arcade) est reliée de nouveau au reste par le chemin le plus court à travers ces murs
    /// (on ne rouvre que les cases de ce chemin). Renvoie le nombre de cases rouvertes.
    /// </summary>
    private int ReconnectRooms(Grid g, HashSet<int> rule)
    {
        if (rule.Count == 0)
            return 0;
        int n = g.Width * g.Height, reopened = 0;
        int[] Label(Func<int, bool> free)
        {
            int[] lab = new int[n]; Array.Fill(lab, -1); int next = 0; Queue<int> q = new Queue<int>();
            for (int s0 = 0; s0 < n; s0++)
            {
                if (lab[s0] >= 0 || !free(s0))
                    continue;
                lab[s0] = next; q.Enqueue(s0);
                while (q.Count > 0)
                {
                    int c = q.Dequeue(), x = c % g.Width, y = c / g.Width;
                    if (x > 0)
                        Try(c - 1);
                    if (x < g.Width - 1)
                        Try(c + 1);
                    if (y > 0)
                        Try(c - g.Width);
                    if (y < g.Height - 1)
                        Try(c + g.Width);
                    void Try(int k)
                    {
                        if (lab[k] < 0 && free(k))
                        {
                            lab[k] = next;
                            q.Enqueue(k);
                        }
                    }
                }
                next++;
            }
            return lab;
        }
        bool After(int i) => g.ZoneA[i] != 0 && !Blocked[i];
        int[] before = Label(i => g.ZoneA[i] != 0 && (!Blocked[i] || rule.Contains(i)));
        int[] after = Label(After);
        // par ensemble d'un seul tenant avant : le plus grand morceau après sert de base
        Dictionary<int, int> size = new Dictionary<int, int>(); for (int i = 0; i < n; i++) if (after[i] >= 0) size[after[i]] = size.GetValueOrDefault(after[i]) + 1;
        Dictionary<int, int> mainOf = new Dictionary<int, int>();
        for (int i = 0; i < n; i++)
            if (after[i] >= 0 && before[i] >= 0 && (!mainOf.TryGetValue(before[i], out int mm) || size[after[i]] > size[mm]))
                mainOf[before[i]] = after[i];
        // 0-1 : parcours depuis les bases, à travers les murs fermés (coût 1) ; chaque morceau isolé est relié par le chemin le moins coûteux
        int[] dist = new int[n]; Array.Fill(dist, int.MaxValue); int[] prev = new int[n]; Array.Fill(prev, -1);
        LinkedList<int> dq = new LinkedList<int>();
        for (int i = 0; i < n; i++)
            if (after[i] >= 0 && before[i] >= 0 && mainOf[before[i]] == after[i])
            {
                dist[i] = 0;
                dq.AddLast(i);
            }
        while (dq.Count > 0)
        {
            int c = dq.First!.Value; dq.RemoveFirst();
            int x = c % g.Width, y = c / g.Width;
            foreach (int k in new[] { x > 0 ? c - 1 : -1, x < g.Width - 1 ? c + 1 : -1, y > 0 ? c - g.Width : -1, y < g.Height - 1 ? c + g.Width : -1 })
            {
                if (k < 0 || before[k] < 0)
                    continue;
                int w = rule.Contains(k) && Blocked[k] ? 1 : 0, nd = dist[c] + w;
                if (nd >= dist[k])
                    continue;
                dist[k] = nd; prev[k] = c;
                if (w == 0)
                    dq.AddFirst(k);
                else
                    dq.AddLast(k);
            }
        }
        Dictionary<int, int> best = new Dictionary<int, int>();   // morceau isolé → sa case la plus proche des bases
        for (int i = 0; i < n; i++)
            if (after[i] >= 0 && before[i] >= 0 && mainOf[before[i]] != after[i] && dist[i] < int.MaxValue && (!best.TryGetValue(after[i], out int b) || dist[i] < dist[b]))
                best[after[i]] = i;
        foreach (int start in best.Values)
            for (int k = start; k >= 0; k = prev[k])
                if (rule.Contains(k) && Blocked[k])
                {
                    Blocked[k] = false;
                    BlockedBy.Remove(k);
                    reopened++;
                }
        return reopened;
    }

    /// <param name="openings">Portes et fenêtres : leurs cases restent ouvertes.</param>
    /// <param name="keep">Points à ne jamais fermer (civils, points de passage des scripts).</param>
    public void BlockThinWalls(IReadOnlyList<Opening> openings, IEnumerable<Vector2> keep)
    {
        if (_s.Grid is not { } g)
            return;
        if (Blocked.Length != g.Width * g.Height)
            Blocked = new bool[g.Width * g.Height];
        HashSet<int> doorCells = new HashSet<int>(openings.SelectMany(d => d.Cells));
        int n = 0;
        float H(int x, int y) => g.Inside(x, y) && g.ZoneA[g.Index(x, y)] != 0 ? g.GroundHeight[g.Index(x, y)] : float.NaN;
        for (int y = 0; y < g.Height; y++)
            for (int x = 0; x < g.Width; x++)
            {
                int i = g.Index(x, y);
                if (g.ZoneA[i] == 0 || Blocked[i] || doorCells.Contains(i))
                    continue;
                float h = g.GroundHeight[i];
                bool wall = false;
                foreach ((int dx, int dy) in new[] { (1, 0), (0, 1) })
                {
                    for (int a = 1; a <= 2 && !wall; a++)
                        for (int b = 1; b <= 2 && !wall; b++)
                        {
                            float h1 = H(x - dx * a, y - dy * a), h2 = H(x + dx * b, y + dy * b);
                            if (h1 >= h + 150 && h2 >= h + 150 && MathF.Abs(h1 - h2) < 60)
                                wall = true;
                        }
                    if (wall)
                        break;
                }
                if (wall)
                {
                    Blocked[i] = true;
                    BlockedBy[i] = "cloison";
                    n++;
                }
            }
        if (n > 0)
            _log.Add($"Cloisons fermées dans la grille : {n} case(s)");
        // murs des pièces : la grille du jeu les marque en zone B 0 (zone A gardée), entre deux zones B ; le passage d'une
        // pièce à l'autre ne se fait que par les portes (leurs cases, et autour du centre d'une porte sans case)
        HashSet<int> nearDoor = new HashSet<int>(doorCells);
        foreach (Opening d in openings)
        {
            (int dx, int dy) = g.CellOf(d.Center);
            if (g.Inside(dx, dy))
                nearDoor.Add(g.Index(dx, dy));
            // porte sans case dans la grille (ouverte au départ) : le passage autour de son centre
            if (d.Cells.Count == 0)
                for (int ox = -1; ox <= 1; ox++)
                    for (int oy = -1; oy <= 1; oy++)
                        if (g.Inside(dx + ox, dy + oy))
                            nearDoor.Add(g.Index(dx + ox, dy + oy));
        }
        // jamais sous un civil ni sur un point de passage des scripts
        foreach (Vector2 w in keep)
        {
            (int cx, int cy) = g.CellOf(new Vector3(w, 0));
            if (g.Inside(cx, cy))
                nearDoor.Add(g.Index(cx, cy));
        }
        bool Z(int x, int y) => g.Inside(x, y) && g.ZoneA[g.Index(x, y)] != 0 && g.ZoneB[g.Index(x, y)] == 0;
        int Run(int x, int y, int dx, int dy) { int r = 1; for (int k = 1; k < 6 && Z(x + dx * k, y + dy * k); k++) r++; for (int k = 1; k < 6 && Z(x - dx * k, y - dy * k); k++) r++; return r; }
        int m = 0; HashSet<int> rule = new HashSet<int>();
        if (Environment.GetEnvironmentVariable("FD3_SANS_MURSPIECES") == null)
        for (int y = 0; y < g.Height; y++)
            for (int x = 0; x < g.Width; x++)
            {
                int i = g.Index(x, y);
                if (g.ZoneA[i] == 0 || g.ZoneB[i] != 0 || Blocked[i] || nearDoor.Contains(i))
                    continue;
                // une bande mince (3 cases au plus en travers) : un mur ; une grande étendue en zone B 0 reste praticable
                if (Math.Min(Run(x, y, 1, 0), Run(x, y, 0, 1)) > 3)
                    continue;
                Blocked[i] = true; BlockedBy[i] = "mur de pièce"; m++; rule.Add(i);
            }
        int reopened = ReconnectRooms(g, rule);
        if (m > 0)
        {
            _log.Add($"Murs des pièces (zone B 0) : {m} case(s), {reopened} rouverte(s) pour garder les passages sans porte");
            Console.WriteLine(_log[^1]);
        }
    }

    /// <summary>Murs du décor (relevés sur les maillages) : cases fermées, sauf portes, véhicules et unités déjà là.</summary>
    public void AddWallCells(IEnumerable<int> cells, IReadOnlyList<Opening> openings, IEnumerable<Vector2> keepPoints)
    {
        if (_s.Grid is not { } g)
            return;
        if (Blocked.Length != g.Width * g.Height)
            Blocked = new bool[g.Width * g.Height];
        // jamais autour d'une porte : son cadre (montants, linteau) ne doit pas fermer le passage
        HashSet<int> keep = new HashSet<int>();
        foreach (Opening d in openings.Where(d => !d.IsWindow))
        {
            foreach (int c0 in d.Cells)
                for (int ox = -1; ox <= 1; ox++)
                    for (int oy = -1; oy <= 1; oy++)
                    {
                        int x = c0 % g.Width + ox, y = c0 / g.Width + oy;
                        if (g.Inside(x, y))
                            keep.Add(g.Index(x, y));
                    }
            (int dx, int dy) = g.CellOf(d.Center);
            for (int ox = -1; ox <= 1; ox++)
                for (int oy = -1; oy <= 1; oy++)
                    if (g.Inside(dx + ox, dy + oy))
                        keep.Add(g.Index(dx + ox, dy + oy));
        }
        foreach (Vector2 w in keepPoints)
        {
            (int cx, int cy) = g.CellOf(new Vector3(w, 0));
            if (g.Inside(cx, cy))
                keep.Add(g.Index(cx, cy));
        }
        int n = 0;
        foreach (int i in cells)
        {
            if (i < 0 || i >= Blocked.Length || g.ZoneA[i] == 0 || Blocked[i] || keep.Contains(i))
                continue;
            Blocked[i] = true; BlockedBy[i] = "mur"; n++;
        }
        if (n > 0)
            _log.Add($"Murs du décor ajoutés à la grille : {n} case(s)");
        Console.WriteLine($"Murs du décor ajoutés à la grille : {n} case(s)");
    }

    public void BuildObstacles(Level level)
    {
        if (_s.Grid is not { } g)
            return;
        Blocked = new bool[g.Width * g.Height];
        SceneAssets assets = _assets;
        Dictionary<string, List<Vector2>> shapes = new Dictionary<string, List<Vector2>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<int, int> cover = new Dictionary<int, int>(); Dictionary<int, string> names = new Dictionary<int, string>();
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform))
        {
            ClassFile? c = _class("Design\\" + o.ClassPath["design\\".Length..] + ".class");
            if (c == null || c.GetInt("OCCUPATION", "Activated", 1) == 0 || c.GetInt("OCCUPATION", "Background", 0) == 1)
                continue;
            string? type = c.Get("OCCUPATION", "Occupation_Type")?.Trim('"', ' ').ToUpperInvariant();
            if (type is "GHOST" or "DOOR" or "WINDOW" or "WAYPOINT")
                continue;
            string mode = c.Get("OCCUPATION", "Occupation_Mode")?.Trim('"', ' ').ToUpperInvariant() ?? "AGO";
            string? model = c.Get("DISPLAY", "Model")?.Trim('"', ' ');
            List<Vector2> pts;
            if (mode == "RECTANGLE")
            {
                int sx = c.GetInt("OCCUPATION", "Rect_SizeX", 0), sy = c.GetInt("OCCUPATION", "Rect_SizeY", 0);
                if (sx <= 0 || sy <= 0)
                    continue;
                pts = new();
                for (float x = -sx * 50 + 25; x < sx * 50; x += 50)
                    for (float y = -sy * 50 + 25; y < sy * 50; y += 50)
                        pts.Add(new(x, y));
                Mark(o, pts, model ?? "");
                continue;
            }
            if (string.IsNullOrEmpty(model) || model.Equals("noitem", StringComparison.OrdinalIgnoreCase))
                continue;
            string path = SceneAssets.GfxPath("Placeable", model);
            if (!shapes.TryGetValue(path, out pts!))
                shapes[path] = pts = Footprint(assets, path);
            Mark(o, pts, model);
        }

        // une case n'est bloquée que si l'objet en couvre plus de la moitié (rangées de sièges, tables : on passe entre)
        foreach ((int i, int n) in cover)
            if (n > CoverMin)
            {
                Blocked[i] = true;
                BlockedBy[i] = names[i];
            }

        void Mark(LevelObject o, List<Vector2> local, string model)
        {
            HashSet<(int, int)> seen = new HashSet<(int, int)>();
            foreach (Vector2 p in local)
            {
                Vector3 w = Vector3.Transform(new Vector3(p, 0), o.Matrix);
                if (!seen.Add(((int)MathF.Floor(w.X / 25), (int)MathF.Floor(w.Y / 25))))
                    continue;
                (int cx, int cy) = g.CellOf(w);
                if (!g.Inside(cx, cy))
                    continue;
                int i = g.Index(cx, cy);
                cover[i] = cover.GetValueOrDefault(i) + 1; names.TryAdd(i, o.Name + " " + model);
            }
        }
    }
    private const int CoverMin = 8;   // sur 16 carrés de 25 cm par case

    /// <summary>Empreinte au sol d'un modèle (repère du modèle) : points tous les 25 cm sous ses faces à hauteur d'homme.</summary>
    public static List<Vector2> Footprint(SceneAssets assets, string path, float Low = 30, float High = 170, Func<string, Matrix4x4, Matrix4x4>? pose = null)
    {
        const float Step = 25;
        HashSet<(int, int)> set = new HashSet<(int, int)>();
        try
        {
            foreach (SceneAssets.Node part in assets.ModelParts(path))
            {
                Mesh m = assets.LoadMesh("", part.Mesh, "gfx").Mesh;
                Vector3[] v = new Vector3[m.VertexCount];
                Matrix4x4 pw = pose != null ? pose(part.Mesh, part.World) : part.World;   // pièce déplacée (battant d'un portail ouvert)
                for (int i = 0; i < v.Length; i++)
                    v[i] = Vector3.Transform(new Vector3(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]), pw);
                for (int t = 0; t + 2 < m.Indices.Length; t += 3)
                {
                    Vector3 a = v[m.Indices[t]]; Vector3 b = v[m.Indices[t + 1]]; Vector3 cc = v[m.Indices[t + 2]];
                    float zmin = MathF.Min(a.Z, MathF.Min(b.Z, cc.Z)), zmax = MathF.Max(a.Z, MathF.Max(b.Z, cc.Z));
                    if (zmin > High || zmax < Low)
                        continue;
                    // échantillonnage de la face projetée au sol
                    float len = MathF.Max(Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y)), MathF.Max(Vector2.Distance(new(a.X, a.Y), new(cc.X, cc.Y)), Vector2.Distance(new(b.X, b.Y), new(cc.X, cc.Y))));
                    int n = Math.Clamp((int)(len / Step) + 1, 1, 400);
                    for (int i = 0; i <= n; i++)
                        for (int j = 0; j <= n - i; j++)
                        {
                            float u = (float)i / n, w = (float)j / n;
                            Vector3 p = a + (b - a) * u + (cc - a) * w;
                            if (p.Z < Low - 20 || p.Z > High + 20)
                                continue;   // grande face inclinée : seule la partie basse compte
                            set.Add(((int)MathF.Floor(p.X / Step), (int)MathF.Floor(p.Y / Step)));
                        }
                }
            }
        }
        catch { }
        if (set.Count == 0)
            return new();
        // intérieur fermé (carrosserie d'un bus dont le toit est trop haut, armoire…) : rempli, comme une forme pleine
        int x0 = set.Min(k => k.Item1) - 1, x1 = set.Max(k => k.Item1) + 1, y0 = set.Min(k => k.Item2) - 1, y1 = set.Max(k => k.Item2) + 1;
        int fw = x1 - x0 + 1, fh = y1 - y0 + 1;
        if ((long)fw * fh < 4_000_000)
        {
            bool[] outside = new bool[fw * fh]; Stack<(int, int)> stack = new Stack<(int, int)>(); stack.Push((0, 0)); outside[0] = true;
            while (stack.Count > 0)
            {
                (int x, int y) = stack.Pop();
                foreach ((int nx, int ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                    if (nx >= 0 && ny >= 0 && nx < fw && ny < fh && !outside[ny * fw + nx] && !set.Contains((nx + x0, ny + y0)))
                    {
                        outside[ny * fw + nx] = true;
                        stack.Push((nx, ny));
                    }
            }
            for (int y = 0; y < fh; y++)
                for (int x = 0; x < fw; x++)
                    if (!outside[y * fw + x])
                        set.Add((x + x0, y + y0));
        }
        return set.Select(k => new Vector2((k.Item1 + 0.5f) * Step, (k.Item2 + 0.5f) * Step)).ToList();
    }

    // passer dans un feu coûte cher : le chemin le contourne quand il peut. Les cases « en feu » (à moins de 160 unités
    // d'un foyer allumé, en x et en y) sont relevées une fois par pas de simulation, et non à chaque case examinée :
    // Mriya a plus de mille foyers, et une recherche peut examiner 200 000 cases
    private bool[] _fireCells = Array.Empty<bool>();
    private int _fireCellsStep = -1;
    public float FireCost(int cx, int cy)
    {
        if (_s.Grid is not { } g || !g.Inside(cx, cy))
            return 0f;
        if (_fireCellsStep != _s.Steps || _fireCells.Length != g.Width * g.Height)
            BuildFireCells(g);
        return _fireCells[g.Index(cx, cy)] ? 6f : 0f;
    }

    private void BuildFireCells(Grid g)
    {
        if (_fireCells.Length != g.Width * g.Height)
            _fireCells = new bool[g.Width * g.Height];
        else
            Array.Clear(_fireCells);
        _fireCellsStep = _s.Steps;
        const float R = 160, C = Grid.CellSize;
        foreach (HotPoint h in _s.Fire.HotPoints)
        {
            if (!h.IsBurning)
                continue;
            // cases dont le centre est à moins de R du foyer, en x et en y (comme avant)
            int x0 = (int)MathF.Ceiling((h.Position.X - R) / C - 0.5f), x1 = (int)MathF.Floor((h.Position.X + R) / C - 0.5f);
            int y0 = (int)MathF.Ceiling((h.Position.Y - R) / C - 0.5f), y1 = (int)MathF.Floor((h.Position.Y + R) / C - 0.5f);
            for (int y = Math.Max(0, y0); y <= Math.Min(g.Height - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(g.Width - 1, x1); x++)
                    if (MathF.Abs(h.Position.X - (x + 0.5f) * C) < R && MathF.Abs(h.Position.Y - (y + 0.5f) * C) < R)
                        _fireCells[g.Index(x, y)] = true;
        }
    }

    public Vector2 Walkable(Vector2 p)
    {
        if (_s.Grid is not { } g)
            return p;
        (int cx, int cy) = g.CellOf(new Vector3(p, 0));
        if (Free(cx, cy))
            return p;
        for (int r = 1; r < 20; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && Free(cx + dx, cy + dy))
                        return new((cx + dx + 0.5f) * Grid.CellSize, (cy + dy + 0.5f) * Grid.CellSize);
        return p;
    }

    public List<Vector2> FindPath(Vector2 from, Vector2 to) => FindPath(from, to, out _);

    private float[] _cost = Array.Empty<float>();
    private int[] _prev = Array.Empty<int>(), _gen = Array.Empty<int>();
    private int _curGen;

    public List<Vector2> FindPath(Vector2 from, Vector2 to, out bool partial)
    {
        partial = false;
        Grid? g = _s.Grid;
        if (g == null)
            return new() { to };
        (int sx, int sy) = g.CellOf(new Vector3(from, 0)); (int tx, int ty) = g.CellOf(new Vector3(to, 0));
        if (!g.Inside(tx, ty))
            return new();
        // départ dans un obstacle (un camion s'est garé sur le pompier) : il en sort d'abord par la case libre la plus proche
        if (!Free(sx, sy) && Walkable(from) is Vector2 exit && exit != from)
        {
            List<Vector2> rest = FindPath(exit, to, out partial);
            if (rest.Count > 0 || (tx, ty) == g.CellOf(new Vector3(exit, 0)))
            {
                rest.Insert(0, exit);
                return rest;
            }
        }
        if (sx == tx && sy == ty)
            return new() { to };
        PriorityQueue<(int x, int y), float> open = new PriorityQueue<(int x, int y), float>();
        // coûts et prédécesseurs dans des tableaux réutilisés (une « génération » par recherche, sans tout remettre à zéro)
        int n = g.Width * g.Height;
        if (_cost.Length != n)
        {
            _cost = new float[n]; _prev = new int[n]; _gen = new int[n]; _curGen = 0;
        }
        if (++_curGen == int.MaxValue)
        {
            Array.Clear(_gen); _curGen = 1;
        }
        int gen = _curGen;
        List<int> seen = new List<int>();
        bool Has(int k) => _gen[k] == gen;
        void Set(int k, float c, int p)
        {
            if (_gen[k] != gen)
                seen.Add(k);
            _gen[k] = gen; _cost[k] = c; _prev[k] = p;
        }
        int Key(int x, int y) => y * g.Width + x;
        open.Enqueue((sx, sy), 0); Set(Key(sx, sy), 0, -1);
        int found = -1, guard = 0;
        while (open.Count > 0 && guard++ < 200000)
        {
            (int x, int y) = open.Dequeue();
            if (x == tx && y == ty)
            {
                found = Key(x, y);
                break;
            }
            float c0 = _cost[Key(x, y)];
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0)
                        continue;
                    int nx = x + dx, ny = y + dy;
                    if (!g.Inside(nx, ny))
                        continue;
                    // la case de départ peut être bloquée (unité posée dans un coin) ; on la quitte quand même
                    if (!Free(nx, ny) && !(nx == tx && ny == ty))
                        continue;
                    if (dx != 0 && dy != 0 && (!Free(x + dx, y) || !Free(x, y + dy)))
                        continue;
                    float c = c0 + (dx != 0 && dy != 0 ? 1.414f : 1f) + FireCost(nx, ny) + (IsGrappleCell(Key(nx, ny)) ? 15 : 0);   // grimper prend du temps
                    int k = Key(nx, ny);
                    if (Has(k) && _cost[k] <= c)
                        continue;
                    Set(k, c, Key(x, y));
                    open.Enqueue((nx, ny), c + MathF.Sqrt((tx - nx) * (tx - nx) + (ty - ny) * (ty - ny)));
                }
        }
        if (Environment.GetEnvironmentVariable("FD3_DEBUG_CHEMIN") != null)
            Console.WriteLine($"recherche ({sx},{sy}) → ({tx},{ty}) : {guard} cases examinées, {(found >= 0 ? "trouvé" : "non trouvé")}, grille {g.Width}×{g.Height}");
        if (found < 0)
        {
            // point inatteignable : on s'arrête à la case explorée la plus proche du point demandé
            int best = -1; float bd = float.MaxValue;
            foreach (int k in seen)
            {
                int x = k % g.Width, y = k / g.Width; float d = (x - tx) * (x - tx) + (y - ty) * (y - ty);
                if (d < bd && Free(x, y))
                {
                    bd = d;
                    best = k;
                }
            }
            if (best < 0 || best == Key(sx, sy))
            {
                partial = best >= 0;
                return new();
            }
            found = best; partial = true;
            to = new(((best % g.Width) + 0.5f) * Grid.CellSize, ((best / g.Width) + 0.5f) * Grid.CellSize);
        }
        List<Vector2> cells = new List<Vector2>();
        for (int k = found; Has(k) && _prev[k] >= 0; k = _prev[k])
            cells.Add(new(((k % g.Width) + 0.5f) * Grid.CellSize, ((k / g.Width) + 0.5f) * Grid.CellSize));
        cells.Reverse();
        if (cells.Count > 0)
            cells[^1] = to;
        // lissage : on saute les points intermédiaires quand la ligne droite est libre
        List<Vector2> smooth = new List<Vector2>(); Vector2 cur = from; int i = 0;
        while (i < cells.Count)
        {
            int j = cells.Count - 1;
            while (j > i && !LineFree(cur, cells[j]))
                j--;
            smooth.Add(cells[j]); cur = cells[j]; i = j + 1;
        }
        return smooth;
    }

    public bool LineFree(Vector2 a, Vector2 b, bool ignoreFire = false)
    {
        if (_s.Grid is not { } g)
            return true;
        int n = (int)(Vector2.Distance(a, b) / 40) + 1;
        for (int i = 1; i < n; i++)
        {
            Vector2 p = Vector2.Lerp(a, b, (float)i / n); (int cx, int cy) = g.CellOf(new Vector3(p, 0));
            if (!Free(cx, cy) || (!ignoreFire && FireCost(cx, cy) > 0))
                return false;
        }
        return true;
    }
}
