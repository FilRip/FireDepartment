using System.Numerics;
using OpenFD.Fire;
using OpenFD.Formats;

namespace OpenFD.Game;

public sealed partial class UnitManager
{
    public List<Opening> Openings { get; } = new();
    public IEnumerable<Opening> Doors => Openings;
    /// <summary>Réglé pour qu'une porte ordinaire (HitDamageCoef 10,73, 100 points de vie) tombe en 3 coups.</summary>
    public const float BlowFactor = 3.11f, BlowSeconds = 1.2f, UseSeconds = 1.0f, ActionReach = 260f;

    /// <summary>Animation de l'état « open » ([LOGICAL_STATE] S0n__NAME = open, S0n_ANIM) ; null s'il n'y en a pas.</summary>
    /// <summary>Vrai si l'animation anime au moins une pièce du modèle.</summary>
    private bool AnimMovesModel(SceneAssets assets, string anim, string? model)
    {
        if (model == null || _files.TryRead("Gfx\\Anim\\" + anim + ".anim") is not { } b)
            return false;
        try
        {
            OpenFD.Formats.AnimClip clip = OpenFD.Formats.AnimClip.Parse(b);
            return assets.ModelParts(model).Any(n => clip.Tracks.ContainsKey(n.Mesh[(n.Mesh.LastIndexOf('\\') + 1)..]));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? StateOpenAnim(ClassFile c)
    {
        for (int k = 1; k <= 6; k++)
        {
            string? name = c.Get("LOGICAL_STATE", $"S{k:00}__NAME")?.Trim('"', ' ');
            string? anim = c.Get("LOGICAL_STATE", $"S{k:00}_ANIM")?.Trim('"', ' ');
            if (string.Equals(name, "open", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(anim))
                return anim;
        }
        return null;
    }

    private void LoadOpenings(Level level)
    {
        SceneAssets assets = _assets ??= new SceneAssets(_files);
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform))
        {
            ClassFile? c = Class("Design\\" + o.ClassPath["design\\".Length..] + ".class");
            string? type = c?.Get("OCCUPATION", "Occupation_Type")?.Trim('"', ' ').ToUpperInvariant();
            // portail animé commandé par le script (Mriya : « angar_door_anim », état « open » → si_angar_door_use) :
            // traité comme une porte que seul le script ouvre (ENT_SwitchState) ; fermé, il bloque le passage
            string? stateOpen = c == null ? null : StateOpenAnim(c);
            if (c == null || (type is not ("DOOR" or "WINDOW") && stateOpen == null))
                continue;
            // un objet que le joueur actionne lui-même (vanne de gaz de Mriya : use → RotateTube) reste un objet à utiliser
            if (type is not ("DOOR" or "WINDOW") && o.GetComponent(0xA027) is { } uc && ParseActions(o.ComponentBytes(uc).ToArray()).Any())
                continue;
            string? m = c.Get("DISPLAY", "Model")?.Trim('"', ' ');
            string? model = string.IsNullOrEmpty(m) || m.Equals("noitem", StringComparison.OrdinalIgnoreCase) ? null : SceneAssets.GfxPath("Placeable", m);
            // un portail du script doit avoir des battants que son animation « open » fait bouger (pas le tonneau de Mriya)
            if (type is not ("DOOR" or "WINDOW") && !AnimMovesModel(assets, stateOpen!, model))
                continue;
            Opening d = new Opening
            {
                Id = o.Id, Name = o.Name, IsWindow = type == "WINDOW", Model = model, World = o.Matrix,
                Breakable = c.GetBool("CAPACITIES_ACTIONS", "CouldBeBreak") && c.GetBool("DAMAGE", "UseDamage", true),
                Usable = type == "DOOR" && c.GetBool("CAPACITIES_ACTIONS", "CouldBeUse"),
                ScriptGate = type != "DOOR" && type != "WINDOW",
                MaxLife = c.GetFloat("DAMAGE", "MaxLifePoints", 100), HitCoef = c.GetFloat("DAMAGE", "HitDamageCoef", 1),
            };
            d.Life = c.GetFloat("DAMAGE", "LifePoints", d.MaxLife);
            d.SoundLabel = c.Get("SOUND", "Label")?.Trim('"', ' ');
            d.State = InitialState(o) == 1 ? OpeningState.Open : OpeningState.Closed;
            // fonctions du script appelées à l'ouverture et à la fermeture (portails de Jail : First_Gates_Open / _Close)
            if (o.GetComponent(0xA027) is { } ac)
            {
                byte[] ab = o.ComponentBytes(ac).ToArray();
                d.Actions.AddRange(ParseActions(ab));
                // animations d'ouverture et de fermeture (« SingleItem\si_Jail_gates_01_open ») : les battants coulissent
                string txt = System.Text.Encoding.Latin1.GetString(ab);
                foreach (System.Text.RegularExpressions.Match mm in System.Text.RegularExpressions.Regex.Matches(txt, @"SingleItem\\[A-Za-z0-9_]+"))
                {
                    if (mm.Value.EndsWith("open", StringComparison.OrdinalIgnoreCase))
                        d.OpenAnim ??= mm.Value;
                    else if (mm.Value.EndsWith("close", StringComparison.OrdinalIgnoreCase))
                        d.CloseAnim ??= mm.Value;
                }
            }
            if (stateOpen != null && d.ScriptGate)
                d.OpenAnim ??= stateOpen;
            d.Center = d.Position;
            if (model != null)
            {
                List<Vector2> pts = PathFind.Footprint(assets, model, 0, 260);
                if (pts.Count > 0)
                {
                    Vector2 mid = pts.Aggregate(Vector2.Zero, (a, p) => a + p) / pts.Count;
                    d.Center = Vector3.Transform(new Vector3(mid, 100), o.Matrix);
                    d.BoxMin = new(float.MaxValue); d.BoxMax = new(float.MinValue);
                    foreach (Vector2 q in pts)
                    {
                        Vector3 wq = Vector3.Transform(new Vector3(q, 0), o.Matrix);
                        d.BoxMin = Vector2.Min(d.BoxMin, new(wq.X, wq.Y));
                        d.BoxMax = Vector2.Max(d.BoxMax, new(wq.X, wq.Y));
                    }
                    if (_s.Grid is { } g)
                    {
                        // sol le plus haut autour (la case de la porte est un mur, de hauteur 0 même dans un bâtiment
                        // surélevé : la banque de London est à 4 m ; ses portes et ses obstacles d'escalier sont à 4 m)
                        float ground = Ground(new(d.Position.X, d.Position.Y));
                        (int dcx, int dcy) = g.CellOf(d.Position);
                        for (int ox = -3; ox <= 3; ox++)
                            for (int oy = -3; oy <= 3; oy++)
                                if (g.Inside(dcx + ox, dcy + oy) && g.ZoneA[g.Index(dcx + ox, dcy + oy)] != 0)
                                {
                                    float gh = g.GroundHeight[g.Index(dcx + ox, dcy + oy)];
                                    if (gh <= d.Position.Z + 60 && gh > ground)
                                        ground = gh;
                                }
                        d.OnGround = d.Position.Z < ground + 200;
                        if (d.OnGround && !d.IsWindow)
                            foreach (Vector2 p in pts)
                            {
                                (int cx, int cy) = g.CellOf(Vector3.Transform(new Vector3(p, 0), o.Matrix));
                                if (g.Inside(cx, cy) && g.ZoneA[g.Index(cx, cy)] != 0 && !d.Cells.Contains(g.Index(cx, cy)))
                                    d.Cells.Add(g.Index(cx, cy));
                            }
                        // portail animé : une fois ouvert, seuls les battants ont bougé ; le reste du modèle (montants, caisson)
                        // et les battants à leur place d'arrivée bloquent toujours le passage
                        if (d.OnGround && !d.IsWindow && d.OpenAnim != null && _files.TryRead("Gfx\\Anim\\" + d.OpenAnim + ".anim") is { } ab2)
                        {
                            AnimClip clip = AnimClip.Parse(ab2);
                            // seulement les portails coulissants (battants déplacés de plus de 2 m), pas les portes qui pivotent
                            bool slides = clip.Tracks.Values.Any(t => t.Pos.Length > 1 && Vector3.Distance(t.Pos[0].v, t.Pos[^1].v) > 200);
                            Matrix4x4 Opened(string mesh, Matrix4x4 w)
                            {
                                if (!clip.Tracks.TryGetValue(mesh[(mesh.LastIndexOf('\\') + 1)..], out AnimClip.Track? tr))
                                    return w;
                                Vector3 pos = tr.Pos.Length > 0 ? tr.Pos[^1].v : w.Translation;
                                if (tr.Rot.Length > 0)
                                    w = Matrix4x4.CreateFromQuaternion(tr.Rot[^1].v);
                                w.Translation = pos; return w;
                            }
                            if (slides && PathFind.Footprint(assets, model, 0, 260, Opened) is { Count: > 0 } open)
                                foreach (Vector2 p in open)
                                {
                                    (int cx, int cy) = g.CellOf(Vector3.Transform(new Vector3(p, 0), o.Matrix));
                                    if (g.Inside(cx, cy) && g.ZoneA[g.Index(cx, cy)] != 0)
                                        d.OpenBlocked.Add(g.Index(cx, cy));
                                }
                            // bâti : boîte du modèle (repère de l'objet : les battants coulissent le long de X), hors du passage
                            List<Vector2> all = slides ? PathFind.Footprint(assets, model, 0, 1000) : new();
                            if (slides && all.Count > 0 && Matrix4x4.Invert(o.Matrix, out Matrix4x4 inv) && d.Cells.Count > 0)
                            {
                                float bx0 = all.Min(q => q.X), bx1 = all.Max(q => q.X), by0 = all.Min(q => q.Y), by1 = all.Max(q => q.Y);
                                List<Vector3> loc = d.Cells.Where(i => !d.OpenBlocked.Contains(i)).DefaultIfEmpty(d.Cells.First()).Select(i => Vector3.Transform(new Vector3(((i % g.Width) + 0.5f) * Grid.CellSize, ((i / g.Width) + 0.5f) * Grid.CellSize, d.Position.Z), inv)).ToList();
                                float px0 = loc.Min(q => q.X), px1 = loc.Max(q => q.X);
                                (int ax, int ay) = g.CellOf(new Vector3(d.BoxMin, 0)); (int bx, int by) = g.CellOf(new Vector3(d.BoxMax, 0));
                                for (int cy = ay - 1; cy <= by + 1; cy++)
                                    for (int cx = ax - 1; cx <= bx + 1; cx++)
                                    {
                                        if (!g.Inside(cx, cy))
                                            continue;
                                        Vector3 q = Vector3.Transform(new Vector3((cx + 0.5f) * Grid.CellSize, (cy + 0.5f) * Grid.CellSize, d.Position.Z), inv);
                                        if (q.X < bx0 - 20 || q.X > bx1 + 20 || q.Y < by0 - 20 || q.Y > by1 + 20)
                                            continue;
                                        if (q.X > px0 - 60 && q.X < px1 + 60)
                                            continue;   // le passage lui-même
                                        int i = g.Index(cx, cy);
                                        if (g.ZoneA[i] != 0)
                                            d.FrameCells.Add(i);
                                    }
                            }
                            Log.Add($"Portail coulissant {d.Name} : {d.Cells.Count} case(s), dont {d.Cells.Count - d.OpenBlocked.Count} libre(s) une fois ouvert, bâti {d.FrameCells.Count}");
                        }
                    }
                }
            }
            Openings.Add(d);
        }
        LoadObstacles(level, assets);
        RefreshDoorCells();
    }

    /// <summary>
    /// Obstacles à dégager à la hache : objets du décor qui ont des points de vie ([DAMAGE] UseDamage, composant A024) et un
    /// repère de hache (« axe_symbol ») à moins de 5 m, comme les gravats qui bloquent l'escalier de la banque de London.
    /// Ils ferment leurs cases jusqu'à ce qu'ils soient cassés ; le script lit leur état avec ENT_GetDamage (0 : dégagé).
    /// </summary>
    private void LoadObstacles(Level level, SceneAssets assets)
    {
        List<Vector3> axes = level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.ClassPath.EndsWith("axe_symbol", StringComparison.OrdinalIgnoreCase)).Select(o => o.Position).ToList();
        if (axes.Count == 0)
            return;
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform))
        {
            if (Openings.Any(d => d.Id == o.Id) || o.ClassPath.EndsWith("axe_symbol", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!axes.Any(a => Vector3.Distance(a, o.Position) < 500))
                continue;
            ClassFile? c = Class("Design\\" + o.ClassPath["design\\".Length..] + ".class");
            if (c == null || !c.GetBool("DAMAGE", "UseDamage"))
                continue;
            float life = c.GetFloat("DAMAGE", "LifePoints", 100);
            if (o.GetComponent(0xA024) is { } comp && comp.Size > 9)
            {
                ReadOnlySpan<byte> b = o.ComponentBytes(comp);
                life = BitConverter.ToSingle(b[5..9]);
            }
            if (life <= 0)
                continue;   // déjà cassé (le morceau « _break » qui le remplace)
            string? m = c.Get("DISPLAY", "Model")?.Trim('"', ' ');
            string? model = string.IsNullOrEmpty(m) ? null : SceneAssets.GfxPath("Placeable", m);
            Opening d = new Opening
            {
                Id = o.Id, Name = o.Name, IsWindow = false, Model = model, World = o.Matrix, IsObstacle = true,
                Breakable = true, Usable = false, MaxLife = c.GetFloat("DAMAGE", "MaxLifePoints", 100), HitCoef = c.GetFloat("DAMAGE", "HitDamageCoef", 1),
                Life = life, State = OpeningState.Closed, OnGround = true,
            };
            d.Center = d.Position;
            if (_s.Grid is { } g)
            {
                // ses cases : celles que le décor lui attribue déjà (BuildObstacles) et son emprise au sol
                foreach ((int i, string? name) in BlockedBy)
                    if (name == o.Name)
                        d.Cells.Add(i);
                if (model != null)
                {
                    List<Vector2> pts = PathFind.Footprint(assets, model, -400, 400);
                    foreach (Vector2 p in pts)
                    {
                        (int cx, int cy) = g.CellOf(Vector3.Transform(new Vector3(p, 0), o.Matrix));
                        if (g.Inside(cx, cy) && g.ZoneA[g.Index(cx, cy)] != 0 && !d.Cells.Contains(g.Index(cx, cy)))
                            d.Cells.Add(g.Index(cx, cy));
                    }
                    if (pts.Count > 0)
                    {
                        Vector2 mid = pts.Aggregate(Vector2.Zero, (a, p) => a + p) / pts.Count;
                        d.Center = Vector3.Transform(new Vector3(mid, 0), o.Matrix);
                    }
                }
                // ces cases ne sont plus des obstacles fixes : elles suivent l'état des gravats
                foreach (int i in d.Cells)
                    if (i < Paths.Blocked.Length && Paths.Blocked[i])
                    {
                        Paths.Blocked[i] = false;
                        BlockedBy.Remove(i);
                    }
            }
            Openings.Add(d);
            Log.Add($"Obstacle à dégager à la hache : {o.Name} ({d.Cells.Count} case(s), {life:0} points de vie)");
        }
    }

    /// <summary>Portes à mécanisme : les rouages (« mechanism_symbol ») posés à moins de 12 m d'une porte qui s'ouvre à la main.</summary>
    private void AssignLevers(Level level)
    {
        List<Vector2> marks = level.Objects.Where(o => o.HasTransform && System.Text.RegularExpressions.Regex.IsMatch(o.ClassPath, @"mechanism_symbol(_blue)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Select(o => new Vector2(o.Position.X, o.Position.Y)).ToList();
        foreach (Opening d in Openings.Where(d => d.Usable && !d.IsWindow && d.OpenAnim != null))   // portails animés seulement
        {
            Vector2 c = new Vector2(d.Center.X, d.Center.Y);
            Vector2? near = marks.Where(m => Vector2.Distance(m, c) < 1200).OrderBy(m => Vector2.Distance(m, c)).Cast<Vector2?>().FirstOrDefault();
            if (near != null)
                d.Lever = near;
        }
    }

    // composant A027 : bloc d'en-tête puis l'indice de l'état logique courant
    private static int InitialState(LevelObject o)
    {
        if (o.GetComponent(0xA027) is not { } comp)
            return 0;
        ReadOnlySpan<byte> b = o.ComponentBytes(comp);
        for (int i = 0; i + 8 <= b.Length; i++)
            if (Chunks.IsEnd(b, i))
                return BitConverter.ToInt32(b[(i + 4)..]);
        return 0;
    }

    private void RefreshDoorCells()
    {
        if (_s.Grid is not { } g)
            return;
        if (Paths.DoorClosed.Length != g.Width * g.Height)
            Paths.DoorClosed = new bool[g.Width * g.Height];
        else
            Array.Clear(Paths.DoorClosed);
        foreach (Opening d in Openings.Where(d => !d.Passable))
            foreach (int i in d.Cells)
                Paths.DoorClosed[i] = true;
        foreach (Opening d in Openings.Where(d => d.Passable))
            foreach (int i in d.OpenBlocked)
                Paths.DoorClosed[i] = true;
        foreach (Opening d in Openings)
            foreach (int i in d.FrameCells)
                Paths.DoorClosed[i] = true;
        _s.Smoke.SetOpenings(Environment.GetEnvironmentVariable("FD3_NOOPEN") != null ? Array.Empty<Vector3>() : Openings.Where(d => d.LetsSmokeOut).Select(d => d.Center));
    }

    /// <summary>Change l'état d'une ouverture (scripts de mission, essais).</summary>
    public void SetOpeningState(Opening d, OpeningState state)
    {
        d.State = state;
        if (state == OpeningState.Broken)
            d.Life = 0;
        RefreshDoorCells();
    }

    /// <summary>
    /// Clic droit sur une porte ou une fenêtre : un pompier qui peut casser (CanBreak) l'enfonce à la hache ;
    /// une porte qui s'ouvre à la main (CouldBeUse) est ouverte ou refermée par toute unité qui peut s'en servir.
    /// </summary>
    public void WorkOnOpening(Unit u, Opening d)
    {
        if (u.IsVehicle)
            return;
        bool breakIt = d.Breakable && u.CanBreak && d.State != OpeningState.Broken && (d.IsWindow || d.State == OpeningState.Closed);
        bool useIt = !breakIt && d.Usable && u.CanUse && d.State != OpeningState.Broken;
        if (!breakIt && !useIt)
        {
            Sounds.Add(("VOCAL_NO_SKILLS_FOR_THIS_ACTION", null));
            u.Status = d.State == OpeningState.Broken ? $"{d.Name} est déjà cassée"
                : d.Breakable && !u.CanBreak ? "ne peut pas casser (il faut un pompier avec une hache)"
                : "cette ouverture ne peut être ni ouverte ni cassée";
            return;
        }
        if (d.Center.Z - Ground(new(d.Center.X, d.Center.Y)) > 400)
        {
            u.Status = $"{d.Name} est à l'étage : hors d'atteinte depuis le sol";
            return;
        }
        // portail à mécanisme : l'unité va sous les rouages, pas au milieu du portail
        Vector2 spot = useIt && d.Lever is { } lv ? Walkable(lv) : ApproachPoint(u, d);
        if (!Order(u, spot))
            return;
        u.Opening = d; u.OpeningBreak = breakIt; u.ActionTimer = 0;
        u.Status = breakIt ? $"va casser {d.Name}" : d.State == OpeningState.Closed ? $"va ouvrir {d.Name}" : $"va fermer {d.Name}";
    }

    // distance d'une unité à l'ouverture : au battant, ou à la case la plus proche d'un obstacle étendu (gravats)
    private float ReachDist(Vector2 p, Opening d)
    {
        float best = Vector2.Distance(p, new(d.Center.X, d.Center.Y));
        if (d.Lever is { } lv)
            best = MathF.Min(best, Vector2.Distance(p, lv));
        if (d.IsObstacle && _s.Grid is { } g)
            foreach (int i in d.Cells)
                best = MathF.Min(best, Vector2.Distance(p, new((i % g.Width + 0.5f) * Grid.CellSize, (i / g.Width + 0.5f) * Grid.CellSize)));
        return best;
    }

    // case libre la plus proche du battant, du côté de l'unité
    private Vector2 ApproachPoint(Unit u, Opening d)
    {
        Vector2 c = new Vector2(d.Center.X, d.Center.Y);
        if (_s.Grid is not { } g)
            return c;
        Vector2 me = new Vector2(u.Position.X, u.Position.Y);
        (int cx, int cy) = g.CellOf(d.Center);
        Vector2 best = Walkable(c); float bestLen = float.MaxValue;
        int r = d.IsObstacle ? 5 : 2;   // gravats : plusieurs cases, on frappe au bord
        // gravats : d'abord les cases qui les touchent (les marches juste en dessous ou au-dessus), sinon un peu plus loin
        if (d.IsObstacle)
            foreach (float reach in new[] { 150f, ActionReach * 0.9f })
            {
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                    {
                        int x = cx + dx, y = cy + dy;
                        if (!Free(x, y))
                            continue;
                        Vector2 p = new Vector2((x + 0.5f) * Grid.CellSize, (y + 0.5f) * Grid.CellSize);
                        if (ReachDist(p, d) > reach)
                            continue;
                        List<Vector2> path = FindPath(me, p, out bool partial);
                        if (partial || (path.Count == 0 && Vector2.Distance(me, p) > 1))
                            continue;
                        float len = 0; Vector2 prev = me; foreach (Vector2 q in path) { len += Vector2.Distance(prev, q); prev = q; }
                        if (len < bestLen)
                        {
                            bestLen = len;
                            best = p;
                        }
                    }
                if (bestLen < float.MaxValue)
                    return best;
            }
        for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            {
                int x = cx + dx, y = cy + dy;
                if (!Free(x, y))
                    continue;
                Vector2 p = new Vector2((x + 0.5f) * Grid.CellSize, (y + 0.5f) * Grid.CellSize);
                if (ReachDist(p, d) > ActionReach * 0.9f)
                    continue;
                List<Vector2> path = FindPath(me, p);
                if (path.Count == 0 && Vector2.Distance(me, p) > 1)
                    continue;
                float len = 0; Vector2 prev = me; foreach (Vector2 q in path) { len += Vector2.Distance(prev, q); prev = q; }
                if (len < bestLen)
                {
                    bestLen = len;
                    best = p;
                }
            }
        return best;
    }

    private void WorkOpening(Unit u, float dt)
    {
        if (u.Opening is not { } d || u.Moving)
            return;
        if (ReachDist(new(u.Position.X, u.Position.Y), d) > ActionReach * 1.5f)
        {
            u.Opening = null;
            u.Status = $"{d.Name} hors d'atteinte";
            return;
        }
        if (d.IsObstacle && _s.Grid is { } g && d.Cells.Count > 0)
        {
            // gravats : le pompier s'avance jusqu'à les toucher (il monte sur les premières marches) avant de frapper
            Vector2 me = new Vector2(u.Position.X, u.Position.Y);
            List<int> cells = d.State == OpeningState.Broken ? new List<int>() : d.Cells;
            Vector2 near = cells.Select(i => new Vector2((i % g.Width + 0.5f) * Grid.CellSize, (i / g.Width + 0.5f) * Grid.CellSize))
                .OrderBy(p => Vector2.DistanceSquared(p, me)).FirstOrDefault();
            if (near != default)
            {
                Vector2 dir = near - me; float dist = dir.Length();
                const float Contact = 85f;   // distance au centre de la case de gravats la plus proche (bord à ~35 cm)
                if (dist > Contact + 5)
                {
                    // derniers pas en marchant (animation de marche, hauteur des marches suivie par Move)
                    u.Path.Enqueue(me + dir / dist * (dist - Contact));
                    u.Status = $"s'approche de {d.Name}";
                    return;
                }
                u.Heading = MathF.Atan2(dir.Y, dir.X);
                // hauteur : celle des marches sous les gravats, pour ne pas taper dans le vide depuis le rez-de-chaussée
                (int ncx, int ncy) = g.CellOf(new Vector3(near, 0));
                float stepZ = g.Inside(ncx, ncy) ? g.GroundHeight[g.Index(ncx, ncy)] : u.Position.Z;
                u.Position = new Vector3(u.Position.X, u.Position.Y, MathF.Max(u.Position.Z, MathF.Min(stepZ, d.Center.Z) - 40));
            }
        }
        else
            u.Heading = MathF.Atan2(d.Center.Y - u.Position.Y, d.Center.X - u.Position.X);
        u.ActionTimer += dt;
        if (u.OpeningBreak)
        {
            if (d.State == OpeningState.Broken)
            {
                u.Opening = null;
                u.Status = "attend les ordres";
                return;
            }
            u.Status = $"casse {d.Name} ({d.Life / d.MaxLife:P0})";
            if (u.ActionTimer < BlowSeconds)
                return;
            u.ActionTimer = 0;
            d.Life -= d.HitCoef * u.BreakPower * BlowFactor;
            u.Blows++;
            if (d.Life <= 0)
            {
                d.Life = 0; d.State = OpeningState.Broken; u.Opening = null; u.Status = "attend les ordres";
                Log.Add($"{_s.Time,6:0} s : {d.Name} cassée");
                if (!string.IsNullOrEmpty(d.SoundLabel))
                    Sounds.Add((d.SoundLabel!, d.Center));
                RefreshDoorCells();
            }
        }
        else
        {
            u.Status = d.State == OpeningState.Closed ? $"ouvre {d.Name}" : $"ferme {d.Name}";
            if (u.ActionTimer < UseSeconds)
                return;
            d.State = d.State == OpeningState.Closed ? OpeningState.Open : OpeningState.Closed;
            Log.Add($"{_s.Time,6:0} s : {d.Name} {(d.State == OpeningState.Open ? "ouverte" : "fermée")}");
            string want = d.State == OpeningState.Open ? "open" : "close";
            foreach ((string? action, string? script) in d.Actions.Where(a => a.action.Equals(want, StringComparison.OrdinalIgnoreCase)).Take(1))
                _s.Scripts?.TryRun(script, Script.Value.I(u.Id), Script.Value.I(d.Id), Script.Value.T(action));
            u.Opening = null; u.Status = "attend les ordres";
            RefreshDoorCells();
        }
    }
}
