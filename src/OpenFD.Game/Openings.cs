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
    /// <summary>Hauteur au-dessus du sol (sous les pieds) jusqu'où un pompier atteint une fenêtre.</summary>
    public const float ReachHeight = 330f;

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

    /// <summary>
    /// Portes, fenêtres et débris cachés au départ (octet de visibilité du niveau) que le script ne montre jamais
    /// (ENT_SetVisibility) : absents du jeu d'origine (Institute : « remains29 », débris d'une autre carte). Ni chargés,
    /// ni dessinés.
    /// </summary>
    public HashSet<string> HiddenOpenings { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Numéros des objets que les scripts de la mission montrent ou cachent (ENT_SetVisibility).</summary>
    private HashSet<uint> ScriptVisibilityIds()
    {
        HashSet<uint> ids = new HashSet<uint>();
        string dir = Path.Combine(_files.DataDir, "Script", "Mission", _s.Mission);
        if (!Directory.Exists(dir))
            return ids;
        foreach (string f in Directory.EnumerateFiles(dir, "*.scp"))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f, System.Text.Encoding.Latin1), @"ENT_SetVisibility\s*\(\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                ids.Add(uint.Parse(m.Groups[1].Value));
        return ids;
    }

    private void LoadOpenings(Level level)
    {
        SceneAssets assets = _assets ??= new SceneAssets(_files);
        HashSet<uint> scriptShown = ScriptVisibilityIds();
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform))
        {
            ClassFile? c = Class("Design\\" + o.ClassPath["design\\".Length..] + ".class");
            string? type = c?.Get("OCCUPATION", "Occupation_Type")?.Trim('"', ' ').ToUpperInvariant();
            // portail animé commandé par le script (Mriya : « angar_door_anim », état « open » → si_angar_door_use) :
            // traité comme une porte que seul le script ouvre (ENT_SwitchState) ; fermé, il bloque le passage
            string? stateOpen = c == null ? null : StateOpenAnim(c);
            if (c == null || (type is not ("DOOR" or "WINDOW") && stateOpen == null))
                continue;
            if (!o.StartVisible && !scriptShown.Contains(o.Id))
            {
                HiddenOpenings.Add(o.Name);
                continue;
            }
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
                Demolish = c.GetBool("CAPACITIES_ACTIONS", "CouldBeDemolish"), HitSpeed = c.GetFloat("DAMAGE", "HitDamageSpeed", 20),
            };
            d.Life = c.GetFloat("DAMAGE", "LifePoints", d.MaxLife);
            d.SoundLabel = c.Get("SOUND", "Label")?.Trim('"', ' ');
            d.State = InitialState(o) == 1 ? OpeningState.Open : OpeningState.Closed;
            // vie propre à cette ouverture dans la carte (composant A024, lu comme pour les objets explosifs) : 0 = déjà cassée
            // au départ (Factory : window_break_villa2153… ; le jeu les montre brisées dès le début)
            if (o.GetComponent(0xA024) is { } dc && o.ComponentBytes(dc) is ReadOnlySpan<byte> db && db.Length >= 13 && db[4] == 0x0C)
            {
                float life = BitConverter.ToSingle(db.Slice(5, 4)), max = BitConverter.ToSingle(db.Slice(9, 4));
                if (float.IsFinite(max) && max > 0)
                    d.MaxLife = max;
                if (float.IsFinite(life) && life >= 0)
                    d.Life = MathF.Min(life, d.MaxLife);
            }
            if (d.Breakable && d.Life <= 0)
            {
                d.Life = 0;
                d.State = OpeningState.Broken;
            }
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
                    d.BoxMin = new(float.MaxValue);
                    d.BoxMax = new(float.MinValue);
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
                                w.Translation = pos;
                                return w;
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
                                (int ax, int ay) = g.CellOf(new Vector3(d.BoxMin, 0));
                                (int bx, int by) = g.CellOf(new Vector3(d.BoxMax, 0));
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
        LoadExtricable(level, assets);
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
            Opening d = AddObstacle(o, c, life, assets);
            Log.Add($"Obstacle à dégager à la hache : {o.Name} ({d.Cells.Count} case(s), {life:0} points de vie)");
        }
    }

    /// <summary>
    /// Obstacle du décor qui ferme ses cases jusqu'à ce qu'il soit dégagé : les cases que le décor lui attribue déjà
    /// (BuildObstacles) et son emprise au sol.
    /// </summary>
    private Opening AddObstacle(LevelObject o, ClassFile c, float life, SceneAssets assets)
    {
        string? m = c.Get("DISPLAY", "Model")?.Trim('"', ' ');
        string? model = string.IsNullOrEmpty(m) ? null : SceneAssets.GfxPath("Placeable", m);
        Opening d = new Opening
        {
            Id = o.Id, Name = o.Name, IsWindow = false, Model = model, World = o.Matrix, IsObstacle = true,
            Breakable = true, Usable = false, MaxLife = c.GetFloat("DAMAGE", "MaxLifePoints", 100), HitCoef = c.GetFloat("DAMAGE", "HitDamageCoef", 1),
            Life = life, State = OpeningState.Closed, OnGround = true,
            Demolish = c.GetBool("CAPACITIES_ACTIONS", "CouldBeDemolish"), HitSpeed = c.GetFloat("DAMAGE", "HitDamageSpeed", 20),
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
        return d;
    }

    /// <summary>
    /// Portails à découper (Fireworks : « old_gates_break », le portail qui barre la voie du train) : objets du décor marqués
    /// « à désincarcérer » (classe CouldBeExtricate, ou dans le niveau : composant A026, troisième octet du bloc des capacités)
    /// avec une icône de scie (« saw_symbol ») à moins de 8 m. Seul le désincarcérateur (CanExtricate) les ouvre, et seulement
    /// du côté de l'icône. Leur vie (composant A024) descend de ExtricatePower par seconde ; à zéro, la fonction du script
    /// nommée dans A024 (« OnGateBreak ») est appelée.
    /// </summary>
    private void LoadExtricable(Level level, SceneAssets assets)
    {
        List<Vector3> saws = level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform && o.ClassPath.EndsWith("saw_symbol", StringComparison.OrdinalIgnoreCase))
            .Select(o => o.Position).ToList();
        if (saws.Count == 0)
            return;
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform))
        {
            if (Openings.Any(d => d.Id == o.Id) || o.ClassPath.EndsWith("saw_symbol", StringComparison.OrdinalIgnoreCase))
                continue;
            ClassFile? c = Class("Design\\" + o.ClassPath["design\\".Length..] + ".class");
            if (c == null)
                continue;
            bool mark = c.GetBool("CAPACITIES_ACTIONS", "CouldBeExtricate");
            if (o.GetComponent(0xA026) is { } cc)
            {
                ReadOnlySpan<byte> b = o.ComponentBytes(cc);
                if (b.Length > 38 && b[22] == 0xAA && b[26] == 0x09 && b[38] == 1)
                    mark = true;
            }
            if (!mark)
                continue;
            Vector3? saw = saws.Where(p => Vector2.Distance(new(p.X, p.Y), new(o.Position.X, o.Position.Y)) < 800)
                .OrderBy(p => Vector3.Distance(p, o.Position)).Cast<Vector3?>().FirstOrDefault();
            if (saw == null)
                continue;
            float life = c.GetFloat("DAMAGE", "LifePoints", 100), max = c.GetFloat("DAMAGE", "MaxLifePoints", 100);
            string? onDestroy = null;
            if (o.GetComponent(0xA024) is { } dc && dc.Size > 13)
            {
                byte[] b = o.ComponentBytes(dc).ToArray();
                if (b[4] == 0x0C)
                {
                    life = BitConverter.ToSingle(b, 5);
                    max = BitConverter.ToSingle(b, 9);
                }
                onDestroy = ScriptNameIn(b);
            }
            if (life <= 0)
                continue;
            Opening d = AddObstacle(o, c, life, assets);
            d.Breakable = false;
            d.Demolish = false;
            d.Extricate = true;
            d.MaxLife = MathF.Max(max, life);
            d.ExtricateSide = new Vector2(saw.Value.X, saw.Value.Y);
            d.OnDestroy = onDestroy;
            d.FallAnim = d.Model != null && _files.TryRead("Gfx\\" + d.Model + ".anim") != null ? "Gfx\\" + d.Model : null;
            Log.Add($"À désincarcérer : {o.Name} ({d.Cells.Count} case(s), {life:0} points de vie, côté ({saw.Value.X:0},{saw.Value.Y:0}), script « {onDestroy} »)");
        }
    }

    /// <summary>Premier texte du composant précédé de sa longueur (entier) : nom d'une fonction du script.</summary>
    private static string? ScriptNameIn(byte[] b)
    {
        for (int i = 0; i + 4 < b.Length; i++)
        {
            int n = BitConverter.ToInt32(b, i);
            if (n < 3 || n > 64 || i + 4 + n > b.Length || b[i + 4 + n - 1] != 0)
                continue;
            bool ok = true;
            for (int k = 0; k < n - 1 && ok; k++)
                ok = b[i + 4 + k] is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'_';
            if (ok)
                return System.Text.Encoding.ASCII.GetString(b, i + 4, n - 1);
        }
        return null;
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
    public static int InitialState(LevelObject o)
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
        // fenêtres fermées : la vue et l'eau ne passent qu'une fois la vitre cassée (ou la fenêtre ouverte)
        Paths.RefreshWindowShut(Openings);
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
        {
            if (u.CanDemolish && d.Demolish && d.State != OpeningState.Broken)
                DemolishOrder(u, d);
            return;
        }
        // portail à découper (Fireworks) : seul le désincarcérateur
        if (d.Extricate && d.State != OpeningState.Broken)
        {
            ExtricateOrder(u, d);
            return;
        }
        // débris pour bulldozer (Institute) : la hache n'y peut rien
        if (d.Demolish && d.State != OpeningState.Broken)
        {
            Sounds.Add(("VOCAL_NO_SKILLS_FOR_THIS_ACTION", null));
            u.Status = $"{d.Name} : il faut un bulldozer";
            return;
        }
        bool breakIt = d.Breakable && u.CanBreak && d.State != OpeningState.Broken && (d.IsWindow || d.State == OpeningState.Closed);
        bool useIt = !breakIt && d.Usable && u.CanUse && d.State != OpeningState.Broken;
        // échelle dressée devant cette fenêtre : on y monte (casser, ouvrir, entrer) ou on en redescend (voir Ladders.cs)
        if (d.IsWindow && LadderAt(d) != null && u.CanLadder && (u.OnLadder != null || !CanReachFromHere(u, d)) && LadderOrder(u, d, breakIt, useIt))
            return;
        if (!breakIt && !useIt)
        {
            Sounds.Add(("VOCAL_NO_SKILLS_FOR_THIS_ACTION", null));
            u.Status = d.State == OpeningState.Broken ? $"{d.Name} est déjà cassée"
                : d.Breakable && !u.CanBreak ? "ne peut pas casser (il faut un pompier avec une hache)"
                : "cette ouverture ne peut être ni ouverte ni cassée";
            return;
        }
        // portail à mécanisme : l'unité va sous les rouages, pas au milieu du portail
        Vector2 spot = useIt && d.Lever is { } lv ? Walkable(lv) : ApproachPoint(u, d);
        // fenêtre d'un étage : hors d'atteinte depuis le sol où l'on se tiendrait (le sol de la case de la fenêtre ne
        // compte pas : c'est souvent le plancher de l'étage). Fenêtres du rez-de-chaussée : 100 à 250 au-dessus du sol,
        // fenêtres du premier : 500 et plus (Institute, Londres, Paris)
        bool noPlace = d.IsWindow && !useIt && !_approachFound;
        if (d.Center.Z - Ground(spot) > ReachHeight || noPlace)
        {
            Sounds.Add(("VOCAL_IMPOSSIBLE_TO_ACCESS", null));
            u.Status = u.CanLadder && Units.Any(v => v.IsAerial && !v.Hidden && !v.OnBoard && !v.Destroyed)
                ? $"{d.Name} est à l'étage : dressez l'échelle du camion échelle" : $"{d.Name} est à l'étage : hors d'atteinte d'ici";
            return;
        }
        if (!Order(u, spot))
            return;
        u.Opening = d;
        u.OpeningBreak = breakIt;
        u.ActionTimer = 0;
        u.Status = breakIt ? $"va casser {d.Name}" : d.State == OpeningState.Closed ? $"va ouvrir {d.Name}" : $"va fermer {d.Name}";
    }

    /// <summary>
    /// Désincarcérateur : il va se placer contre le portail, du côté de l'icône de scie (l'intérieur), puis le découpe.
    /// De l'autre côté, ou sans chemin jusqu'à ce côté-là : refusé.
    /// </summary>
    private void ExtricateOrder(Unit u, Opening d)
    {
        if (!u.CanExtricate)
        {
            Sounds.Add(("VOCAL_NO_SKILLS_FOR_THIS_ACTION", null));
            u.Status = $"{d.Name} : il faut un désincarcérateur";
            return;
        }
        Vector2 spot = ApproachPoint(u, d);
        if (!_approachFound)
        {
            Sounds.Add(("VOCAL_IMPOSSIBLE_TO_ACCESS", null));
            u.Status = $"{d.Name} : ne se découpe que de l'autre côté";
            return;
        }
        if (!Order(u, spot))
            return;
        u.Opening = d;
        u.OpeningBreak = true;
        u.ActionTimer = 0;
        u.Status = $"va découper {d.Name}";
    }

    /// <summary>Durée de sc_extricate_init : le désincarcérateur met sa pince en place avant de couper.</summary>
    public const float ExtricateStart = 1.17f;

    /// <summary>Le pompier est déjà à l'étage de cette fenêtre (il y travaille comme au sol, sans échelle).</summary>
    private bool CanReachFromHere(Unit u, Opening d) => MathF.Abs(d.Center.Z - u.Position.Z) <= ReachHeight && u.OnLadder == null;

    /// <summary>Bulldozer : il se place devant les débris, face à eux, puis les dégage à coups de pelle (WorkOpening).</summary>
    private void DemolishOrder(Unit u, Opening d)
    {
        Vector2 c = new Vector2(d.Center.X, d.Center.Y), me = new Vector2(u.Position.X, u.Position.Y);
        Vector2 dir = Vector2.Distance(me, c) > 1 ? Vector2.Normalize(me - c) : Vector2.UnitX;
        // devant les débris, à une demi-longueur d'engin (le pivot est au milieu de l'engin)
        Vector2 spot = Walkable(c + dir * DozerStandOff);
        if (!Order(u, spot))
            return;
        u.Opening = d;
        u.OpeningBreak = true;
        u.ActionTimer = 0;
        u.Status = $"va déblayer {d.Name}";
    }

    /// <summary>Distance du pivot du bulldozer aux débris pour les pousser ; durée d'un coup de pelle.</summary>
    public const float DozerStandOff = 420f, DozerBlowSeconds = 1.6f;

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

    // vrai si le dernier ApproachPoint a trouvé une case atteignable (sinon il renvoie la case libre la plus proche)
    private bool _approachFound;

    // portail à découper : seulement du côté de son icône de scie
    private static bool WrongSide(Vector2 p, Opening d)
    {
        if (d.ExtricateSide is not { } side)
            return false;
        Vector2 c = new Vector2(d.Center.X, d.Center.Y);
        return Vector2.Dot(p - c, side - c) <= 0;
    }

    // case libre la plus proche du battant, du côté de l'unité
    private Vector2 ApproachPoint(Unit u, Opening d)
    {
        _approachFound = false;
        Vector2 c = new Vector2(d.Center.X, d.Center.Y);
        if (_s.Grid is not { } g)
            return c;
        Vector2 me = new Vector2(u.Position.X, u.Position.Y);
        (int cx, int cy) = g.CellOf(d.Center);
        Vector2 best = Walkable(c);
        float bestLen = float.MaxValue;
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
                        if (ReachDist(p, d) > reach || WrongSide(p, d))
                            continue;
                        List<Vector2> path = FindPath(me, p, out bool partial);
                        if (partial || (path.Count == 0 && Vector2.Distance(me, p) > 1))
                            continue;
                        float len = 0;
                        Vector2 prev = me;
                        foreach (Vector2 q in path)
                        {
                            len += Vector2.Distance(prev, q);
                            prev = q;
                        }
                        if (len < bestLen)
                        {
                            bestLen = len;
                            best = p;
                        }
                    }
                if (bestLen < float.MaxValue)
                {
                    _approachFound = true;
                    return best;
                }
            }
        for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            {
                int x = cx + dx, y = cy + dy;
                if (!Free(x, y))
                    continue;
                Vector2 p = new Vector2((x + 0.5f) * Grid.CellSize, (y + 0.5f) * Grid.CellSize);
                if (ReachDist(p, d) > ActionReach * 0.9f || WrongSide(p, d))
                    continue;
                // fenêtre : seulement d'un sol d'où on l'atteint (dans la pièce à l'étage, plutôt que dans la rue en dessous)
                if (d.IsWindow && d.Center.Z - Ground(p) > ReachHeight)
                    continue;
                // chemin partiel (case de l'autre côté de la porte fermée, inatteignable) : on ne la retient pas
                List<Vector2> path = FindPath(me, p, out bool partial);
                if (partial || (path.Count == 0 && Vector2.Distance(me, p) > 1))
                    continue;
                float len = 0;
                Vector2 prev = me;
                foreach (Vector2 q in path)
                {
                    len += Vector2.Distance(prev, q);
                    prev = q;
                }
                if (len < bestLen)
                {
                    bestLen = len;
                    best = p;
                    _approachFound = true;
                }
            }
        return best;
    }

    /// <summary>
    /// Coups de pelle du bulldozer : il se tourne vers les débris et avance d'un coup puis recule (Shove, pour le dessin) ;
    /// chaque coup enlève HitDamageSpeed × la durée du coup (Institute : 100 points, 20 par seconde → 4 coups de 1,6 s).
    /// </summary>
    private void Dozer(Unit u, Opening d, float dt)
    {
        if (d.State == OpeningState.Broken)
        {
            u.Opening = null;
            u.Status = "attend les ordres";
            return;
        }
        Vector2 me = new Vector2(u.Position.X, u.Position.Y), c = new Vector2(d.Center.X, d.Center.Y);
        if (Vector2.Distance(me, c) > DozerStandOff + 400)
        {
            u.Opening = null;
            u.Status = $"{d.Name} hors d'atteinte";
            return;
        }
        u.Heading = MathF.Atan2(c.Y - me.Y, c.X - me.X);
        u.ActionTimer += dt;
        u.Shove = MathF.Sin(MathF.PI * Math.Clamp(u.ActionTimer / DozerBlowSeconds, 0, 1));
        u.Status = $"déblaie {d.Name} ({d.Life / d.MaxLife:P0})";
        if (u.ActionTimer < DozerBlowSeconds)
            return;
        u.ActionTimer = 0;
        d.Life -= d.HitSpeed * DozerBlowSeconds;
        u.Blows++;
        Sounds.Add(("BULLDOZER_HORN", u.Position));
        if (d.Life > 0)
            return;
        d.Life = 0;
        d.State = OpeningState.Broken;
        u.Opening = null;
        u.Shove = 0;
        u.Status = "attend les ordres";
        Log.Add($"{_s.Time,6:0} s : {d.Name} déblayé par {u.Name}");
        RefreshDoorCells();
    }

    /// <summary>
    /// Découpe à la pince : après la mise en place (ExtricateStart), la vie du portail descend de ExtricatePower par seconde ;
    /// à zéro, les battants s'ouvrent et tombent (dessin), le passage s'ouvre et le script est prévenu (OnGateBreak).
    /// </summary>
    private void Extricating(Unit u, Opening d, float dt)
    {
        if (d.State == OpeningState.Broken)
        {
            u.Opening = null;
            u.Status = "attend les ordres";
            return;
        }
        u.Status = $"découpe {d.Name} ({d.Life / d.MaxLife:P0})";
        if (u.ActionTimer < ExtricateStart)
            return;
        d.Life -= u.ExtricatePower * dt;
        if (d.Life > 0)
            return;
        d.Life = 0;
        d.State = OpeningState.Broken;
        d.BrokenAt = _s.Time;
        Sounds.Add(("SC_EXTRICATE_END", u.Position));   // il éteint la scie (démarrage et coupe : sons des animations)
        u.Opening = null;
        u.Status = "attend les ordres";
        Log.Add($"{_s.Time,6:0} s : {d.Name} découpé par {u.Name}");
        RefreshDoorCells();
        if (!string.IsNullOrEmpty(d.OnDestroy))
            _s.Scripts?.TryRun(d.OnDestroy!, Script.Value.I(d.Id));
    }

    private void WorkOpening(Unit u, float dt)
    {
        u.Shove = 0;
        if (u.Opening is not { } d || (u.Moving && !u.PassingWindow))
            return;
        if (u.OnLadder != null && u.LadderStep != LadderPhase.Top)
            return;   // il monte à l'échelle : il agira en haut
        if (u.IsVehicle)
        {
            Dozer(u, d, dt);
            return;
        }
        if (!u.PassingWindow && ReachDist(new(u.Position.X, u.Position.Y), d) > ActionReach * 1.5f)
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
                Vector2 dir = near - me;
                float dist = dir.Length();
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
        if (d.Extricate)
        {
            Extricating(u, d, dt);
            return;
        }
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
                d.Life = 0;
                d.State = OpeningState.Broken;
                u.Opening = null;
                u.Status = "attend les ordres";
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
            u.Opening = null;
            u.Status = "attend les ordres";
            RefreshDoorCells();
        }
    }
}
