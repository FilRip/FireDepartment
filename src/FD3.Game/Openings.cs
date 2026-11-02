using System.Numerics;
using FD3.Fire;
using FD3.Formats;

namespace FD3.Game;

public enum OpeningState { Closed, Open, Broken }

/// <summary>
/// Porte ou fenêtre de la carte (objet dont [OCCUPATION] Occupation_Type = DOOR ou WINDOW).
/// État de départ lu dans le composant A027 (état logique : 0 = « Close », 1 = « Open »).
/// Vie : [DAMAGE] LifePoints ; un coup de hache retire HitDamageCoef × BreakPower × BlowFactor.
/// </summary>
public sealed class Opening
{
    public required uint Id { get; init; }
    public required string Name { get; init; }
    public required bool IsWindow { get; init; }
    public required string? Model { get; init; }
    public required Matrix4x4 World { get; init; }
    public Vector3 Position => World.Translation;
    public bool Breakable;          // [CAPACITIES_ACTIONS] CouldBeBreak et [DAMAGE] UseDamage
    public bool Usable;             // CouldBeUse : s'ouvre et se ferme à la main
    public float Life, MaxLife, HitCoef;
    public OpeningState State;
    public bool OnGround;           // au niveau de la grille (les portes des étages ne touchent pas le chemin)
    public bool IsObstacle;         // gravats, éboulis… à dégager à la hache (repère « axe_symbol » à côté)
    public List<int> Cells = new(); // cases de la grille fermées quand la porte est fermée
    public Vector3 Center;          // milieu du battant (point visé et point de passage)
    public string? SoundLabel;      // [SOUND] LABEL : bruit de la vitre qui casse
    public bool Passable => IsWindow || State != OpeningState.Closed;
    public bool LetsSmokeOut => State != OpeningState.Closed;
}

public sealed partial class UnitManager
{
    public List<Opening> Openings { get; } = new();
    public IEnumerable<Opening> Doors => Openings;
    /// <summary>Réglé pour qu'une porte ordinaire (HitDamageCoef 10,73, 100 points de vie) tombe en 3 coups.</summary>
    public const float BlowFactor = 3.11f, BlowSeconds = 1.2f, UseSeconds = 1.0f, ActionReach = 260f;
    private bool[] _doorClosed = Array.Empty<bool>();

    private void LoadOpenings(Level level)
    {
        var assets = _assets ??= new SceneAssets(_files);
        foreach (var o in level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform))
        {
            var c = Class("Design\\" + o.ClassPath["design\\".Length..] + ".class");
            var type = c?.Get("OCCUPATION", "Occupation_Type")?.Trim('"', ' ').ToUpperInvariant();
            if (c == null || type is not ("DOOR" or "WINDOW")) continue;
            var m = c.Get("DISPLAY", "Model")?.Trim('"', ' ');
            var model = string.IsNullOrEmpty(m) || m.Equals("noitem", StringComparison.OrdinalIgnoreCase) ? null : SceneAssets.GfxPath("Placeable", m);
            var d = new Opening
            {
                Id = o.Id, Name = o.Name, IsWindow = type == "WINDOW", Model = model, World = o.Matrix,
                Breakable = c.GetBool("CAPACITIES_ACTIONS", "CouldBeBreak") && c.GetBool("DAMAGE", "UseDamage", true),
                Usable = type == "DOOR" && c.GetBool("CAPACITIES_ACTIONS", "CouldBeUse"),
                MaxLife = c.GetFloat("DAMAGE", "MaxLifePoints", 100), HitCoef = c.GetFloat("DAMAGE", "HitDamageCoef", 1),
            };
            d.Life = c.GetFloat("DAMAGE", "LifePoints", d.MaxLife);
            d.SoundLabel = c.Get("SOUND", "Label")?.Trim('"', ' ');
            d.State = InitialState(o) == 1 ? OpeningState.Open : OpeningState.Closed;
            d.Center = d.Position;
            if (model != null)
            {
                var pts = Footprint(assets, model, 0, 260);
                if (pts.Count > 0)
                {
                    var mid = pts.Aggregate(Vector2.Zero, (a, p) => a + p) / pts.Count;
                    d.Center = Vector3.Transform(new Vector3(mid, 100), o.Matrix);
                    if (_s.Grid is { } g)
                    {
                        // sol le plus haut autour (la case de la porte est un mur, de hauteur 0 même dans un bâtiment
                        // surélevé : la banque de London est à 4 m ; ses portes et ses obstacles d'escalier sont à 4 m)
                        float ground = Ground(new(d.Position.X, d.Position.Y));
                        var (dcx, dcy) = g.CellOf(d.Position);
                        for (int ox = -3; ox <= 3; ox++)
                            for (int oy = -3; oy <= 3; oy++)
                                if (g.Inside(dcx + ox, dcy + oy) && g.ZoneA[g.Index(dcx + ox, dcy + oy)] != 0)
                                {
                                    float gh = g.GroundHeight[g.Index(dcx + ox, dcy + oy)];
                                    if (gh <= d.Position.Z + 60 && gh > ground) ground = gh;
                                }
                        d.OnGround = d.Position.Z < ground + 200;
                        if (d.OnGround && !d.IsWindow)
                            foreach (var p in pts)
                            {
                                var (cx, cy) = g.CellOf(Vector3.Transform(new Vector3(p, 0), o.Matrix));
                                if (g.Inside(cx, cy) && g.ZoneA[g.Index(cx, cy)] != 0 && !d.Cells.Contains(g.Index(cx, cy))) d.Cells.Add(g.Index(cx, cy));
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
        var axes = level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.ClassPath.EndsWith("axe_symbol", StringComparison.OrdinalIgnoreCase)).Select(o => o.Position).ToList();
        if (axes.Count == 0) return;
        foreach (var o in level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform))
        {
            if (Openings.Any(d => d.Id == o.Id) || o.ClassPath.EndsWith("axe_symbol", StringComparison.OrdinalIgnoreCase)) continue;
            if (!axes.Any(a => Vector3.Distance(a, o.Position) < 500)) continue;
            var c = Class("Design\\" + o.ClassPath["design\\".Length..] + ".class");
            if (c == null || !c.GetBool("DAMAGE", "UseDamage")) continue;
            float life = c.GetFloat("DAMAGE", "LifePoints", 100);
            if (o.GetComponent(0xA024) is { } comp && comp.Size > 9) { var b = o.ComponentBytes(comp); life = BitConverter.ToSingle(b[5..9]); }
            if (life <= 0) continue;   // déjà cassé (le morceau « _break » qui le remplace)
            var m = c.Get("DISPLAY", "Model")?.Trim('"', ' ');
            var model = string.IsNullOrEmpty(m) ? null : SceneAssets.GfxPath("Placeable", m);
            var d = new Opening
            {
                Id = o.Id, Name = o.Name, IsWindow = false, Model = model, World = o.Matrix, IsObstacle = true,
                Breakable = true, Usable = false, MaxLife = c.GetFloat("DAMAGE", "MaxLifePoints", 100), HitCoef = c.GetFloat("DAMAGE", "HitDamageCoef", 1),
                Life = life, State = OpeningState.Closed, OnGround = true,
            };
            d.Center = d.Position;
            if (_s.Grid is { } g)
            {
                // ses cases : celles que le décor lui attribue déjà (BuildObstacles) et son emprise au sol
                foreach (var (i, name) in BlockedBy) if (name == o.Name) d.Cells.Add(i);
                if (model != null)
                {
                    var pts = Footprint(assets, model, -400, 400);
                    foreach (var p in pts)
                    {
                        var (cx, cy) = g.CellOf(Vector3.Transform(new Vector3(p, 0), o.Matrix));
                        if (g.Inside(cx, cy) && g.ZoneA[g.Index(cx, cy)] != 0 && !d.Cells.Contains(g.Index(cx, cy))) d.Cells.Add(g.Index(cx, cy));
                    }
                    if (pts.Count > 0) { var mid = pts.Aggregate(Vector2.Zero, (a, p) => a + p) / pts.Count; d.Center = Vector3.Transform(new Vector3(mid, 0), o.Matrix); }
                }
                // ces cases ne sont plus des obstacles fixes : elles suivent l'état des gravats
                foreach (var i in d.Cells) if (i < _blocked.Length && _blocked[i]) { _blocked[i] = false; BlockedBy.Remove(i); }
            }
            Openings.Add(d);
            Log.Add($"Obstacle à dégager à la hache : {o.Name} ({d.Cells.Count} case(s), {life:0} points de vie)");
        }
    }

    // composant A027 : bloc d'en-tête puis l'indice de l'état logique courant
    private static int InitialState(LevelObject o)
    {
        if (o.GetComponent(0xA027) is not { } comp) return 0;
        var b = o.ComponentBytes(comp);
        for (int i = 0; i + 8 <= b.Length; i++)
            if (Chunks.IsEnd(b, i)) return BitConverter.ToInt32(b[(i + 4)..]);
        return 0;
    }

    private void RefreshDoorCells()
    {
        if (_s.Grid is not { } g) return;
        if (_doorClosed.Length != g.Width * g.Height) _doorClosed = new bool[g.Width * g.Height]; else Array.Clear(_doorClosed);
        foreach (var d in Openings.Where(d => !d.Passable)) foreach (var i in d.Cells) _doorClosed[i] = true;
        _s.Smoke.SetOpenings(Environment.GetEnvironmentVariable("FD3_NOOPEN") != null ? Array.Empty<Vector3>() : Openings.Where(d => d.LetsSmokeOut).Select(d => d.Center));
    }

    /// <summary>Change l'état d'une ouverture (scripts de mission, essais).</summary>
    public void SetOpeningState(Opening d, OpeningState state)
    {
        d.State = state; if (state == OpeningState.Broken) d.Life = 0;
        RefreshDoorCells();
    }

    public bool IsClosedDoor(int index) => index < _doorClosed.Length && _doorClosed[index];

    /// <summary>
    /// Clic droit sur une porte ou une fenêtre : un pompier qui peut casser (CanBreak) l'enfonce à la hache ;
    /// une porte qui s'ouvre à la main (CouldBeUse) est ouverte ou refermée par toute unité qui peut s'en servir.
    /// </summary>
    public void WorkOnOpening(Unit u, Opening d)
    {
        if (u.IsVehicle) return;
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
        { u.Status = $"{d.Name} est à l'étage : hors d'atteinte depuis le sol"; return; }
        var spot = ApproachPoint(u, d);
        if (!Order(u, spot)) return;
        u.Opening = d; u.OpeningBreak = breakIt; u.ActionTimer = 0;
        u.Status = breakIt ? $"va casser {d.Name}" : d.State == OpeningState.Closed ? $"va ouvrir {d.Name}" : $"va fermer {d.Name}";
    }

    // distance d'une unité à l'ouverture : au battant, ou à la case la plus proche d'un obstacle étendu (gravats)
    private float ReachDist(Vector2 p, Opening d)
    {
        float best = Vector2.Distance(p, new(d.Center.X, d.Center.Y));
        if (d.IsObstacle && _s.Grid is { } g)
            foreach (var i in d.Cells)
                best = MathF.Min(best, Vector2.Distance(p, new((i % g.Width + 0.5f) * Grid.CellSize, (i / g.Width + 0.5f) * Grid.CellSize)));
        return best;
    }

    // case libre la plus proche du battant, du côté de l'unité
    private Vector2 ApproachPoint(Unit u, Opening d)
    {
        var c = new Vector2(d.Center.X, d.Center.Y);
        if (_s.Grid is not { } g) return c;
        var me = new Vector2(u.Position.X, u.Position.Y);
        var (cx, cy) = g.CellOf(d.Center);
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
                        if (!Free(x, y)) continue;
                        var p = new Vector2((x + 0.5f) * Grid.CellSize, (y + 0.5f) * Grid.CellSize);
                        if (ReachDist(p, d) > reach) continue;
                        var path = FindPath(me, p, out bool partial);
                        if (partial || (path.Count == 0 && Vector2.Distance(me, p) > 1)) continue;
                        float len = 0; var prev = me; foreach (var q in path) { len += Vector2.Distance(prev, q); prev = q; }
                        if (len < bestLen) { bestLen = len; best = p; }
                    }
                if (bestLen < float.MaxValue) return best;
            }
        for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            {
                int x = cx + dx, y = cy + dy;
                if (!Free(x, y)) continue;
                var p = new Vector2((x + 0.5f) * Grid.CellSize, (y + 0.5f) * Grid.CellSize);
                if (ReachDist(p, d) > ActionReach * 0.9f) continue;
                var path = FindPath(me, p);
                if (path.Count == 0 && Vector2.Distance(me, p) > 1) continue;
                float len = 0; var prev = me; foreach (var q in path) { len += Vector2.Distance(prev, q); prev = q; }
                if (len < bestLen) { bestLen = len; best = p; }
            }
        return best;
    }

    private void WorkOpening(Unit u, float dt)
    {
        if (u.Opening is not { } d || u.Moving) return;
        if (ReachDist(new(u.Position.X, u.Position.Y), d) > ActionReach * 1.5f)
        { u.Opening = null; u.Status = $"{d.Name} hors d'atteinte"; return; }
        if (d.IsObstacle && _s.Grid is { } g && d.Cells.Count > 0)
        {
            // gravats : le pompier s'avance jusqu'à les toucher (il monte sur les premières marches) avant de frapper
            var me = new Vector2(u.Position.X, u.Position.Y);
            var cells = d.State == OpeningState.Broken ? new List<int>() : d.Cells;
            var near = cells.Select(i => new Vector2((i % g.Width + 0.5f) * Grid.CellSize, (i / g.Width + 0.5f) * Grid.CellSize))
                .OrderBy(p => Vector2.DistanceSquared(p, me)).FirstOrDefault();
            if (near != default)
            {
                var dir = near - me; float dist = dir.Length();
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
                var (ncx, ncy) = g.CellOf(new Vector3(near, 0));
                float stepZ = g.Inside(ncx, ncy) ? g.GroundHeight[g.Index(ncx, ncy)] : u.Position.Z;
                u.Position = new Vector3(u.Position.X, u.Position.Y, MathF.Max(u.Position.Z, MathF.Min(stepZ, d.Center.Z) - 40));
            }
        }
        else u.Heading = MathF.Atan2(d.Center.Y - u.Position.Y, d.Center.X - u.Position.X);
        u.ActionTimer += dt;
        if (u.OpeningBreak)
        {
            if (d.State == OpeningState.Broken) { u.Opening = null; u.Status = "attend les ordres"; return; }
            u.Status = $"casse {d.Name} ({d.Life / d.MaxLife:P0})";
            if (u.ActionTimer < BlowSeconds) return;
            u.ActionTimer = 0;
            d.Life -= d.HitCoef * u.BreakPower * BlowFactor;
            u.Blows++;
            if (d.Life <= 0)
            {
                d.Life = 0; d.State = OpeningState.Broken; u.Opening = null; u.Status = "attend les ordres";
                Log.Add($"{_s.Time,6:0} s : {d.Name} cassée");
                if (!string.IsNullOrEmpty(d.SoundLabel)) Sounds.Add((d.SoundLabel!, d.Center));
                RefreshDoorCells();
            }
        }
        else
        {
            u.Status = d.State == OpeningState.Closed ? $"ouvre {d.Name}" : $"ferme {d.Name}";
            if (u.ActionTimer < UseSeconds) return;
            d.State = d.State == OpeningState.Closed ? OpeningState.Open : OpeningState.Closed;
            Log.Add($"{_s.Time,6:0} s : {d.Name} {(d.State == OpeningState.Open ? "ouverte" : "fermée")}");
            u.Opening = null; u.Status = "attend les ordres";
            RefreshDoorCells();
        }
    }
}
