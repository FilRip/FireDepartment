using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using OpenFD.Fire;
using OpenFD.Game;
using SN = System.Numerics;

namespace OpenFD.MG;

// toits par bâtiment : le toit d'un bâtiment disparaît (pour de bon) dès qu'un pompier y entre, ou s'approche d'une de
// ses portes ouverte ou cassée (ou l'ouvre). La touche T enlève ou remet tous les toits (essais).
// Bâtiment = maillages du décor regroupés quand l'un est presque entièrement dans l'emprise de l'autre
// (London : « Bank_fasad », « Bank_fasad_ROOF », « Bank_ROOM… », « roof_bank_glass » forment la banque).
public sealed partial class OpenFDGame
{
    private sealed class Building { public SN.Vector2 Min, Max;
        public float MinZ = float.MaxValue;
        public readonly List<string> Nodes = new();
        public bool Open;
        public bool HasRooms; }
    private readonly List<Building> _buildings = new();
    private readonly Dictionary<string, int> _nodeBuilding = new(StringComparer.OrdinalIgnoreCase);
    private int _curBuilding = -1;
    private float _roofCheck;

    private bool RoofShown(int bld) => _roofs && !(bld >= 0 && bld < _buildings.Count && _buildings[bld].Open);

    /// <summary>Bâtiment dont le toit s'efface pièce par pièce (carte des pièces) plutôt que d'un coup.</summary>
    private bool RoofByRoom(int bld) => false;   // (ancien essai : toit effacé pièce par pièce ; remplacé par les couvercles noirs)

    // ---- couvercles noirs des pièces (« …_ROOMn_MASK » du décor), comme le jeu d'origine -----------------------------
    // Le toit part d'un bloc (script ou pompier qui entre) ; chaque pièce que l'on ne peut pas encore atteindre reste
    // couverte de son couvercle noir. Pièces : zones de cases libres de la grille séparées par les murs et les ouvertures.
    // Une pièce est atteinte quand on y arrive depuis un pompier en passant par des portes ouvertes ou cassées, ou par
    // des gravats (même intacts) ; une porte fermée arrête. London, cinéma : la porte d'entrée découvre le couloir, la
    // porte de gauche les pièces de gauche, la troisième la salle.
    private int[] _roomOf = Array.Empty<int>();       // case → pièce (−1 : mur, ouverture, ou dehors)
    private bool[] _roomOpen = Array.Empty<bool>();
    private int[] _openingAt = Array.Empty<int>();    // case → indice de l'ouverture qui l'occupe (−1 : aucune)

    private void FindRooms()
    {
        if (_session.Grid is not { } g)
            return;
        int w = g.Width, h = g.Height, n = w * h;
        _openingAt = Enumerable.Repeat(-1, n).ToArray();
        IReadOnlyList<Opening> ops = _session.Units.Openings;
        for (int k = 0; k < ops.Count; k++)
            if (!ops[k].IsWindow && ops[k].OnGround)
                foreach (int c in ops[k].Cells)
                    if (c >= 0 && c < n)
                        _openingAt[c] = k;
        _roomOf = Enumerable.Repeat(-1, n).ToArray();
        List<List<int>> comps = new List<List<int>>();
        Queue<int> q = new Queue<int>();
        for (int s0 = 0; s0 < n; s0++)
        {
            if (_roomOf[s0] != -1 || _session.Units.IsBlocked(s0) || _openingAt[s0] >= 0 || g.ZoneA[s0] == 0)
                continue;
            List<int> comp = new List<int>();
            int id = comps.Count;
            _roomOf[s0] = id;
            q.Enqueue(s0);
            while (q.Count > 0)
            {
                int c = q.Dequeue();
                comp.Add(c);
                int cx = c % w, cy = c / w;
                foreach ((int dx, int dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                        continue;
                    int k2 = ny * w + nx;
                    if (_roomOf[k2] != -1 || _session.Units.IsBlocked(k2) || _openingAt[k2] >= 0 || g.ZoneA[k2] == 0)
                        continue;
                    _roomOf[k2] = id;
                    q.Enqueue(k2);
                }
            }
            comps.Add(comp);
        }
        // une pièce : petite zone fermée, posée dans un bâtiment du décor (le dehors, lui, touche le bord ou est immense)
        const float Cell = OpenFD.Formats.Grid.CellSize;
        int rooms = 0;
        for (int id = 0; id < comps.Count; id++)
        {
            List<int> comp = comps[id];
            bool border = comp.Any(c => c % w == 0 || c / w == 0 || c % w == w - 1 || c / w == h - 1);
            SN.Vector2 centre = new SN.Vector2((float)comp.Average(c => c % w + 0.5) * Cell, (float)comp.Average(c => c / w + 0.5) * Cell);
            Building? b = border || comp.Count > n / 20 ? null
                : _buildings.Where(b => b.Nodes.Count > 1 && Inside(centre, b.Min, b.Max, 0)).OrderBy(b => (b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y)).FirstOrDefault();
            if (b == null)
            {
                foreach (int c in comp)
                    _roomOf[c] = -1;
                continue;
            }
            // le bâtiment, et tout toit posé au-dessus (« …_ROOF » seul, parapet…), s'effacent pièce par pièce
            foreach (Building over in _buildings.Where(o => Inside(centre, o.Min, o.Max, 0)))
                over.HasRooms = true;
            rooms++;
        }
        _roomOpen = new bool[comps.Count];
        // essais (captures) : FD3_OUVRIR_PORTES=nom1,nom2 ouvre ces portes au départ
        if (Environment.GetEnvironmentVariable("FD3_OUVRIR_PORTES") is { } names)
            foreach (string nm in names.Split(','))
                foreach (Opening o in ops.Where(o => o.Name.Equals(nm.TrimStart('!'), StringComparison.OrdinalIgnoreCase)))
                    o.State = nm.StartsWith('!') ? OpeningState.Broken : OpeningState.Open;   // « !nom » : cassée
        // essais (captures) : FD3_ALLUMER=préfixe → allume au départ les foyers dont le nom commence ainsi
        if (Environment.GetEnvironmentVariable("FD3_ALLUMER") is { Length: > 0 } fire)
            foreach (HotPoint hp in _session.Fire.HotPoints.Where(hp => hp.Name.StartsWith(fire, StringComparison.OrdinalIgnoreCase)))
                _session.Fire.Ignite(hp, hp.Class.IntensityMax);
        if (rooms == 0)
            return;
        Console.WriteLine($"Pièces : {rooms} dans {_buildings.Count(b => b.HasRooms)} bâtiment(s)");
    }

    /// <summary>Pièces que les pompiers peuvent atteindre : portes fermées = arrêt ; portes ouvertes, cassées et gravats = passage.</summary>
    private void UpdateRoomRoofs()
    {
        if (_roomOf.Length == 0 || _session.Grid is not { } g)
            return;
        int w = g.Width, h = g.Height, n = w * h;
        IReadOnlyList<Opening> ops = _session.Units.Openings;
        bool[] seen = new bool[n];
        Queue<int> q = new Queue<int>();
        foreach (Unit u in _session.Units.Active.Where(u => !u.IsVehicle))
        {
            (int cx, int cy) = g.CellOf(u.Position);
            if (g.Inside(cx, cy) && !seen[g.Index(cx, cy)])
            {
                seen[g.Index(cx, cy)] = true;
                q.Enqueue(g.Index(cx, cy));
            }
        }
        bool changed = false;
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            if (_roomOf[c] >= 0 && !_roomOpen[_roomOf[c]])
            {
                _roomOpen[_roomOf[c]] = true;
                changed = true;
            }
            int cx = c % w, cy = c / w;
            foreach ((int dx, int dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nx = cx + dx, ny = cy + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                    continue;
                int k = ny * w + nx;
                if (seen[k] || g.ZoneA[k] == 0)
                    continue;
                int o = _openingAt[k];
                if (o >= 0)
                {
                    // gravats : on découvre ce qu'il y a derrière avant de les dégager ; porte : seulement ouverte ou cassée
                    if (!ops[o].IsObstacle && ops[o].State == OpeningState.Closed)
                        continue;
                }
                else if (_session.Units.IsBlocked(k))
                    continue;
                seen[k] = true;
                q.Enqueue(k);
            }
        }
        if (changed)
            foreach (RoomLid lid in _lids.Where(l => l.Shown && l.Rooms.Count > 0 && l.Rooms.Any(r => _roomOpen[r])))
                lid.Shown = false;   // une de ses pièces atteinte (la grille coupe parfois une pièce en deux) : le couvercle part
        // couvercle sans pièce reconnue : il part quand un pompier passe dessous
        foreach (RoomLid lid in _lids.Where(l => l.Shown && l.Rooms.Count == 0))
            if (_session.Units.Active.Any(u => !u.IsVehicle && Inside(new(u.Position.X, u.Position.Y), lid.Min, lid.Max, -50)))
                lid.Shown = false;
    }

    private readonly HashSet<uint> _revealedWindows = new();

    /// <summary>
    /// Fenêtre cassée : on découvre ce qu'il y a derrière (Mriya, House_02 : la pièce à backdraft s'attaque de dehors) —
    /// les pièces de part et d'autre perdent leur couvercle noir, et le toit du bâtiment part.
    /// </summary>
    private void RevealBrokenWindows()
    {
        foreach (Opening o in _session.Units.Openings.Where(o => o.IsWindow && o.State == OpeningState.Broken && !_revealedWindows.Contains(o.Id)))
        {
            _revealedWindows.Add(o.Id);
            SN.Vector2 c = new SN.Vector2(o.Center.X, o.Center.Y);
            // pièces autour de la fenêtre (le mur fait une ou deux cases d'épaisseur)
            if (_roomOf.Length > 0 && _session.Grid is { } g)
            {
                (int cx, int cy) = g.CellOf(o.Center);
                for (int dy = -3; dy <= 3; dy++)
                    for (int dx = -3; dx <= 3; dx++)
                        if (g.Inside(cx + dx, cy + dy) && _roomOf[g.Index(cx + dx, cy + dy)] is int r and >= 0)
                            _roomOpen[r] = true;
                foreach (RoomLid lid in _lids.Where(l => l.Shown && l.Rooms.Any(r => _roomOpen[r])))
                    lid.Shown = false;
            }
            // couvercle dont le bord passe par la fenêtre (la fenêtre est dans le mur, au bord du couvercle)
            foreach (RoomLid lid in _lids.Where(l => l.Shown && Inside(c, l.Min, l.Max, 120)))
                lid.Shown = false;
            // toit du bâtiment : le plus petit toit qui couvre la fenêtre (« House_02_ROOF », pas le toit du hangar
            // qui couvre tout), et le bâtiment de plusieurs maillages qui la contient (pas un groupe géant)
            float Area(Building b) => (b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y);
            List<Building> around = _buildings.Where(b => Inside(c, b.Min, b.Max, 150)).ToList();
            Building? roof = around.Where(b => b.Nodes.Count == 1).OrderBy(Area).FirstOrDefault(b => b.Nodes[0].Contains("ROOF", StringComparison.OrdinalIgnoreCase) || b.MinZ > o.Center.Z + 150);
            Building? group = around.Where(b => b.Nodes.Count > 1 && Area(b) <= 0.25f * _gridW * _gridH).OrderBy(Area).FirstOrDefault();
            foreach (Building? b in new[] { roof, group })
            {
                if (b == null || b.Open)
                    continue;
                b.Open = true;
                Log($"Toit retiré (fenêtre cassée : {o.Name}) : {b.Nodes[0]}");
                Console.WriteLine($"Toit retiré (fenêtre cassée : {o.Name}) : {b.Nodes[0]}");
            }
        }
    }

    private sealed class RoomLid
    {
        public required string Name;
        public required VertexBuffer Vb;
        public required int Tris;
        public SN.Vector2 Min, Max;
        public readonly List<int> Rooms = new();
        public bool Shown = true;
    }
    private readonly List<RoomLid> _lids = new();
    private BasicEffect? _lidFx;

    /// <summary>Charge les couvercles noirs et repère les pièces que chacun recouvre (cases dont le centre est sous lui).</summary>
    private void LoadLids(string mission)
    {
        foreach (RoomLid l in _lids)
            l.Vb.Dispose();
        _lids.Clear();
        _revealedWindows.Clear();
        _lidFx ??= new BasicEffect(GraphicsDevice) { LightingEnabled = false, VertexColorEnabled = false, TextureEnabled = false, DiffuseColor = Vector3.Zero };
        Formats.Grid? g = _session.Grid;
        const float Cell = OpenFD.Formats.Grid.CellSize;
        foreach (SceneAssets.Node node in _assets.Masks(mission))
        {
            Formats.Mesh m;
            try
            {
                m = _assets.LoadMesh(mission, node.Mesh, null).Mesh;
            }
            catch
            {
                continue;
            }
            if (m.Indices.Length < 3)
                continue;
            VertexPosition[] v = new VertexPosition[m.Indices.Length];
            SN.Vector2 mn = new(float.MaxValue), mx = new(float.MinValue);
            for (int k = 0; k < m.Indices.Length; k++)
            {
                int i = (int)m.Indices[k];
                SN.Vector3 p = SN.Vector3.Transform(new SN.Vector3(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]), node.World);
                v[k] = new VertexPosition(new Vector3(p.X, p.Y, p.Z + 2));
                mn = SN.Vector2.Min(mn, new(p.X, p.Y));
                mx = SN.Vector2.Max(mx, new(p.X, p.Y));
            }
            VertexBuffer vb = new VertexBuffer(GraphicsDevice, VertexPosition.VertexDeclaration, v.Length, BufferUsage.WriteOnly);
            vb.SetData(v);
            RoomLid lid = new RoomLid { Name = node.Mesh, Vb = vb, Tris = v.Length / 3, Min = mn, Max = mx };
            // pièces dont au moins un tiers des cases est sous le couvercle (vu de dessus)
            if (g != null && _roomOf.Length == g.Width * g.Height)
            {
                Dictionary<int, int> under = new Dictionary<int, int>(), total = new Dictionary<int, int>();
                for (int c = 0; c < _roomOf.Length; c++)
                {
                    int r = _roomOf[c];
                    if (r < 0)
                        continue;
                    total[r] = total.GetValueOrDefault(r) + 1;
                    SN.Vector2 cp = new SN.Vector2((c % g.Width + 0.5f) * Cell, (c / g.Width + 0.5f) * Cell);
                    if (!Inside(cp, mn, mx, 0))
                        continue;
                    for (int t = 0; t + 2 < v.Length; t += 3)
                        if (InTri(cp, v[t].Position, v[t + 1].Position, v[t + 2].Position))
                        {
                            under[r] = under.GetValueOrDefault(r) + 1;
                            break;
                        }
                }
                foreach ((int r, int cnt) in under)
                    if (cnt * 3 >= total[r])
                        lid.Rooms.Add(r);
            }
            _lids.Add(lid);
            if (Environment.GetEnvironmentVariable("FD3_DEBUG_COUVERCLES") != null)
                Console.WriteLine($"  couvercle {lid.Name} ({mn.X:0},{mn.Y:0})-({mx.X:0},{mx.Y:0}) pièces [{string.Join(", ", lid.Rooms)}]");
        }
        if (_lids.Count > 0)
            Console.WriteLine($"Couvercles des pièces : {_lids.Count} ({_lids.Count(l => l.Rooms.Count > 0)} liés à une pièce de la grille)");
    }

    private static bool InTri(SN.Vector2 p, Vector3 a, Vector3 b, Vector3 c)
    {
        float d1 = (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);
        float d2 = (p.X - c.X) * (b.Y - c.Y) - (b.X - c.X) * (p.Y - c.Y);
        float d3 = (p.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (p.Y - a.Y);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    /// <summary>Couvercles noirs encore en place (sous le toit tant qu'il est là).</summary>
    private void RenderLids(Matrix viewProj)
    {
        if (_lidFx == null || _lids.Count == 0 || _map)
            return;
        GraphicsDevice gd = GraphicsDevice;
        _lidFx.World = Matrix.Identity;
        _lidFx.View = Matrix.Identity;
        _lidFx.Projection = viewProj;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.BlendState = BlendState.Opaque;
        gd.RasterizerState = RasterizerState.CullNone;
        foreach (RoomLid lid in _lids.Where(l => l.Shown))
        {
            gd.SetVertexBuffer(lid.Vb);
            foreach (EffectPass pass in _lidFx.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawPrimitives(PrimitiveType.TriangleList, 0, lid.Tris);
            }
        }
    }

    private void FindBuildings(string mission, IReadOnlyList<SceneAssets.Node> scenery)
    {
        _buildings.Clear();
        _nodeBuilding.Clear();
        List<(string name, SN.Vector2 min, SN.Vector2 max)> boxes = new List<(string name, SN.Vector2 min, SN.Vector2 max)>();
        Dictionary<string, float> zmin = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (SceneAssets.Node n in scenery)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(n.Mesh, @"^(FGD_|GROUND|Tile_ground)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                continue;
            try
            {
                Formats.Mesh m = _assets.LoadMesh(mission, n.Mesh, null).Mesh;
                SN.Vector2 mn = new(float.MaxValue), mx = new(float.MinValue);
                for (int i = 0; i < m.VertexCount; i++)
                {
                    SN.Vector3 v = SN.Vector3.Transform(new SN.Vector3(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]), n.World);
                    mn = SN.Vector2.Min(mn, new(v.X, v.Y));
                    mx = SN.Vector2.Max(mx, new(v.X, v.Y));
                    zmin[n.Mesh] = MathF.Min(zmin.TryGetValue(n.Mesh, out float z0) ? z0 : float.MaxValue, v.Z);
                }
                if (mn.X < mx.X)
                    boxes.Add((n.Mesh, mn, mx));
            }
            catch { }
        }
        // regroupement : l'emprise de l'un recouvre au moins 60 % de celle de l'autre
        int[] parent = Enumerable.Range(0, boxes.Count).ToArray();
        float mapArea = _session.Grid is { } gg ? gg.Width * gg.Height * OpenFD.Formats.Grid.CellSize * OpenFD.Formats.Grid.CellSize : float.MaxValue;
        int Find(int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }
        float Area(SN.Vector2 a, SN.Vector2 b) => MathF.Max(0, b.X - a.X) * MathF.Max(0, b.Y - a.Y);
        for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
            {
                (string name, SN.Vector2 min, SN.Vector2 max) a = boxes[i];
                (string name, SN.Vector2 min, SN.Vector2 max) b = boxes[j];
                // un toit nommé « …_ROOF » reste seul : il ne part que quand un pompier est dessous (pas dès qu'il est dans
                // l'emprise de tout un groupe : la place de Maidan couvre presque toute la carte)
                if (a.name.EndsWith("_ROOF", StringComparison.OrdinalIgnoreCase) || b.name.EndsWith("_ROOF", StringComparison.OrdinalIgnoreCase))
                    continue;
                // maillage géant (couloirs et escalators de Maidan sur toute la carte) : il n'avale pas les autres
                if (MathF.Max(Area(a.min, a.max), Area(b.min, b.max)) > 0.25f * mapArea)
                    continue;
                float inter = Area(SN.Vector2.Max(a.min, b.min), SN.Vector2.Min(a.max, b.max));
                float small = MathF.Min(Area(a.min, a.max), Area(b.min, b.max));
                if (small > 0 && inter >= 0.6f * small)
                    parent[Find(i)] = Find(j);
            }
        Dictionary<int, int> ids = new Dictionary<int, int>();
        for (int i = 0; i < boxes.Count; i++)
        {
            int r = Find(i);
            if (!ids.TryGetValue(r, out int id))
            {
                ids[r] = id = _buildings.Count;
                _buildings.Add(new Building { Min = new(float.MaxValue), Max = new(float.MinValue) });
            }
            Building b = _buildings[id];
            b.Min = SN.Vector2.Min(b.Min, boxes[i].min);
            b.Max = SN.Vector2.Max(b.Max, boxes[i].max);
            b.Nodes.Add(boxes[i].name);
            if (zmin.TryGetValue(boxes[i].name, out float bz))
                b.MinZ = MathF.Min(b.MinZ, bz);
            _nodeBuilding[boxes[i].name] = id;
        }
        foreach (Building b in _buildings.Where(b => b.Nodes.Count > 1))
            Console.WriteLine($"Bâtiment : {string.Join(", ", b.Nodes.Take(6))}{(b.Nodes.Count > 6 ? "…" : "")} ({b.Min.X:0},{b.Min.Y:0})-({b.Max.X:0},{b.Max.Y:0})");
    }

    /// <summary>Emprise d'un objet posé en hauteur (2,2 m au moins au-dessus du sol, 4 m² au moins vu de dessus), sinon null.</summary>
    private (SN.Vector2 min, SN.Vector2 max, float zmin)? OverheadBox(SceneAssets.MeshAsset a, SN.Matrix4x4 w)
    {
        Formats.Mesh m = a.Mesh;
        if (m.VertexCount == 0)
            return null;
        SN.Vector2 mn = new(float.MaxValue), mx = new(float.MinValue);
        float zmin = float.MaxValue;
        for (int i = 0; i < m.VertexCount; i++)
        {
            SN.Vector3 v = SN.Vector3.Transform(new SN.Vector3(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]), w);
            mn = SN.Vector2.Min(mn, new(v.X, v.Y));
            mx = SN.Vector2.Max(mx, new(v.X, v.Y));
            zmin = MathF.Min(zmin, v.Z);
        }
        SN.Vector2 c = (mn + mx) / 2;
        float ground = _session.Units.GroundZ(c);
        if (zmin - ground < 220 || (mx.X - mn.X) * (mx.Y - mn.Y) < 40000)
            return null;
        return (mn, mx, zmin);
    }

    private static bool Inside(SN.Vector2 p, SN.Vector2 min, SN.Vector2 max, float margin) =>
        p.X >= min.X - margin && p.Y >= min.Y - margin && p.X <= max.X + margin && p.Y <= max.Y + margin;

    /// <summary>Quelques fois par seconde : un pompier dans un bâtiment ou près d'une de ses portes ouvertes en découvre le toit.</summary>
    private void UpdateRoofs(float dt)
    {
        // demandes des scripts (cinématiques : « montrer l'intérieur du hangar »)
        if (Host is { } h)
        {
            // LEV_RoofVisibility / LEV_MaskVisibility : toit d'un bâtiment, ou couvercle noir nommé
            foreach ((string? mesh, bool hidden) in h.RoofRequests)
            {
                foreach (RoomLid lid in _lids.Where(l => l.Name.Equals(mesh, StringComparison.OrdinalIgnoreCase)))
                    lid.Shown = !hidden;
                if (_nodeBuilding.TryGetValue(mesh, out int id))
                    _buildings[id].Open = hidden;
                else
                    foreach (Building b0 in _buildings.Where(b0 => b0.Nodes.Any(n => n.Contains(mesh, StringComparison.OrdinalIgnoreCase))))
                        b0.Open = hidden;
            }
            h.RoofRequests.Clear();
            // toits posés que le script cache ou remontre (ENT_SetVisibility : « angar_roof » de Mriya à l'interrupteur)
            foreach ((uint rid, string rname) in _scriptRoofs)
                if (h.Visibility.TryGetValue(rid, out bool rv) && !rv)
                    foreach (Building b0 in _buildings.Where(b0 => !b0.Open && b0.Nodes.Contains(rname, StringComparer.OrdinalIgnoreCase)))
                        b0.Open = true;
        }
        if ((_roofCheck -= dt) > 0 || _buildings.Count == 0)
            return;
        _roofCheck = 0.25f;
        UpdateRoomRoofs();
        RevealBrokenWindows();
        List<SN.Vector2> men = _session.Units.Active.Where(u => !u.IsVehicle).Select(u => new SN.Vector2(u.Position.X, u.Position.Y)).ToList();
        if (men.Count == 0)
            return;
        // toit seul (place au-dessus d'un souterrain à Maidan, verrière…) : il part quand un pompier passe dessous
        List<SN.Vector3> men3 = _session.Units.Active.Where(u => !u.IsVehicle).Select(u => u.Position).ToList();
        // (de même pour tout maillage seul suspendu au-dessus d'un pompier : verrière, auvent, dalle…)
        foreach (Building b in _buildings.Where(b => !b.Open && b.Nodes.Count == 1))
            if (men3.Any(p => Inside(new(p.X, p.Y), b.Min, b.Max, 0) && p.Z < b.MinZ - 120))
            {
                b.Open = true;
                Log($"Toit retiré : {b.Nodes[0]}");
            }
        List<SN.Vector2> doors = _session.Units.Openings.Where(o => o.OnGround && o.State != OpeningState.Closed)
            .Select(o => new SN.Vector2(o.Center.X, o.Center.Y)).ToList();
        for (int i = 0; i < _buildings.Count; i++)
        {
            Building b = _buildings[i];
            if (b.Open || b.Nodes.Count < 2)
                continue;
            // groupe qui couvre une grande partie de la carte (souterrains de Maidan) : être dans son emprise ne veut rien dire
            if ((b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y) > 0.25f * _gridW * _gridH)
                continue;
            bool inside = men.Any(p => Inside(p, b.Min, b.Max, -200));
            bool atDoor = !inside && doors.Where(d => Inside(d, b.Min, b.Max, 200)).Any(d => men.Any(p => SN.Vector2.Distance(p, d) < 350));
            if (!inside && !atDoor)
                continue;
            b.Open = true;
            Log($"Toit retiré : {b.Nodes.OrderBy(n => n.Contains("ROOM", StringComparison.OrdinalIgnoreCase) || n.Contains("ROOF", StringComparison.OrdinalIgnoreCase) ? 1 : 0).First()}");
        }
    }
}
