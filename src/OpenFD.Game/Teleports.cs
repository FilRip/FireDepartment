using System.Numerics;
using OpenFD.Formats;
using OpenFD.Script;

namespace OpenFD.Game;

/// <summary>
/// Escaliers du niveau (TMI, Paris, Lab, Eurotunnel, Tutorial) : passages entre deux icônes d'escalier, souvent d'un étage
/// à l'autre, parfois d'un bout de la carte à l'autre (TMI). Ils font partie des chemins (comme l'échelle) ; un ordre
/// donné sur une icône y emmène le pompier, qui ressort par l'autre.
/// </summary>
public sealed partial class UnitManager
{
    public List<Teleport> Teleports { get; } = new();

    private void LoadTeleports(Level level)
    {
        Dictionary<uint, LevelObject> byId = level.Objects.ToDictionary(o => o.Id, o => o);
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.ClassPath.EndsWith("teleport_stair", StringComparison.OrdinalIgnoreCase)))
        {
            // les deux icônes suivent l'escalier dans le niveau (numéros +1 et +2)
            if (!byId.TryGetValue(o.Id + 1, out LevelObject? a) || !byId.TryGetValue(o.Id + 2, out LevelObject? b)
                || !a.ClassPath.EndsWith("stair_symbol", StringComparison.OrdinalIgnoreCase) || !b.ClassPath.EndsWith("stair_symbol", StringComparison.OrdinalIgnoreCase))
                continue;
            Teleport t = new Teleport
            {
                Id = o.Id, Name = o.Name,
                EndA = new Vector2(a.Position.X, a.Position.Y), EndB = new Vector2(b.Position.X, b.Position.Y),
                HeightA = a.Position.Z, HeightB = b.Position.Z,
                Active = o.StartVisible,
            };
            ReadTeleportBlock(o, t);
            t.Fake = Vector2.Distance(t.EndA, t.EndB) < 50;   // « faux escalier » (Lab : Fake_Stair_Message) : les deux icônes au même endroit
            Teleports.Add(t);
        }
        LoadStreetStairs(level);
    }

    /// <summary>
    /// Escaliers extérieurs (« street_stairs », modèle buildvicto_escal : Fire Department 2, Street) : une volée de marches
    /// entre la rue et l'étage d'un immeuble, fermée dans la grille. Le haut est le point « Dum_use1 » du modèle (sur le
    /// palier de l'étage) ; le bas, la case libre au pied des marches, de l'autre côté, nettement plus basse (cherchée
    /// quand la grille est prête : UpdateTeleportLinks). Toujours ouvert ; durée selon la longueur de la volée.
    /// </summary>
    private void LoadStreetStairs(Level level)
    {
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform && o.ClassPath.EndsWith("street_stairs", StringComparison.OrdinalIgnoreCase)))
        {
            ClassFile? c = Class("Design\\" + o.ClassPath["design\\".Length..] + ".class");
            string? m = c?.Get("DISPLAY", "Model")?.Trim('"', ' ');
            if (string.IsNullOrEmpty(m))
                continue;
            Vector3? top = null;
            try
            {
                CharacterRig? rig = CharacterRig.Build(_assets ??= new SceneAssets(_files), _files, SceneAssets.GfxPath("Placeable", m));
                int k = rig == null ? -1 : Array.FindIndex(rig.Names, n => n.StartsWith("Dum_use", StringComparison.OrdinalIgnoreCase));
                if (rig != null && k >= 0)
                    top = Vector3.Transform(rig.Pose(null, 0)[k].Translation, o.Matrix);
            }
            catch { }
            if (top is not { } tp)
                continue;
            Vector2 at = new Vector2(o.Position.X, o.Position.Y), up = new Vector2(tp.X, tp.Y);
            Teleports.Add(new Teleport
            {
                Id = o.Id, Name = o.Name, EndA = up, HeightA = tp.Z, Active = true, Walk = true,
                Foot = at + (at - up),   // au pied : à l'opposé du palier
                Duration = 1,
            });
        }
    }

    // pied d'un escalier extérieur : la case libre la plus proche du pied, plus basse que le palier d'au moins une marche,
    // et du côté opposé au palier
    private int FootCell(Grid g, Teleport t)
    {
        (int cx, int cy) = g.CellOf(new Vector3(t.Foot, 0));
        Vector2 mid = (t.Foot + t.EndA) / 2;
        for (int r = 0; r < 10; r++)
        {
            int best = -1;
            float bd = float.MaxValue;
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r || !Free(x, y) || g.GroundHeight[g.Index(x, y)] > t.HeightA - PathFind.MaxStep)
                        continue;
                    Vector2 p = CellCenter(g, g.Index(x, y));
                    if (Vector2.Dot(p - mid, t.EndA - mid) > 0)
                        continue;
                    float d = Vector2.Distance(p, t.Foot);
                    if (d < bd)
                    {
                        bd = d;
                        best = g.Index(x, y);
                    }
                }
            if (best >= 0)
                return best;
        }
        return -1;
    }

    // bloc A02B : actif (octet), durée (réel), ON_ENTER, ON_LEAVE (chaînes : longueur sur 4 octets, puis le texte)
    private static void ReadTeleportBlock(LevelObject o, Teleport t)
    {
        if (o.GetComponent(0xA02B) is not { } c)
            return;
        byte[] b = o.ComponentBytes(c).ToArray();
        int i = ((ReadOnlySpan<byte>)b).IndexOf(new byte[] { 0xCC, 0xCC, 0xBB, 0xBB });
        if (i < 0 || i + 4 + 1 + 4 + 8 > b.Length)
            return;
        i += 4;
        bool active = b[i] != 0;
        i++;
        t.Duration = BitConverter.ToSingle(b, i);
        i += 4;
        t.OnEnter = Str(ref i);
        t.OnLeave = Str(ref i);
        t.Active = t.Active && active;
        if (!(t.Duration > 0 && t.Duration < 60))
            t.Duration = 1;

        string Str(ref int k)
        {
            if (k + 4 > b.Length)
                return "";
            int n = BitConverter.ToInt32(b, k);
            k += 4;
            if (n <= 0 || k + n > b.Length)
                return "";
            string s = System.Text.Encoding.Latin1.GetString(b, k, n).TrimEnd('\0');
            k += n;
            return s;
        }
    }

    /// <summary>Vitesse dans un escalier extérieur (unités par seconde, le long de la volée).</summary>
    private const float StairsWalkSpeed = 180f;

    /// <summary>Icône d'un escalier utilisable sous ce point (curseur « entrer »).</summary>
    public bool TeleportEndNear(Vector2 p) => Teleports.Any(t => TeleportUsable(t) && (Vector2.Distance(p, t.EndA) < 130 || Vector2.Distance(p, t.EndB) < 130));

    /// <summary>ENT_ActivateTeleport(escalier, 1/0) (Tutorial).</summary>
    public void ActivateTeleport(long id, bool on)
    {
        if (Teleports.FirstOrDefault(t => t.Id == id) is { } t)
            t.Active = on;
    }

    // escalier utilisable : actif, et visible (le script peut le cacher ou le montrer : ENT_SetVisibility)
    private bool TeleportUsable(Teleport t) =>
        t.Active && t.CellA >= 0 && t.CellB >= 0 && !(_s.Host?.Visibility.TryGetValue(t.Id, out bool vis) == true && !vis);

    /// <summary>À chaque pas : cases des bouts (une fois) et passages des escaliers utilisables pour la recherche de chemin.</summary>
    private void UpdateTeleportLinks()
    {
        if (_s.Grid is not { } g)
            return;
        foreach (Teleport t in Teleports)
        {
            if (t.CellA < 0)
                t.CellA = EndCell(g, t.EndA, t.HeightA);
            if (t.CellB < 0 && t.Walk)
            {
                // escalier extérieur : son pied, une fois la grille prête ; durée : la volée à pas de marche
                t.CellB = FootCell(g, t);
                if (t.CellB >= 0)
                {
                    t.EndB = CellCenter(g, t.CellB);
                    t.HeightB = g.GroundHeight[t.CellB];
                    t.Duration = Math.Clamp(Vector3.Distance(new Vector3(t.EndA, t.HeightA), new Vector3(t.EndB, t.HeightB)) / StairsWalkSpeed, 1, 12);
                }
            }
            else if (t.CellB < 0)
                t.CellB = EndCell(g, t.EndB, t.HeightB);
            if (TeleportUsable(t) && !t.Fake)
                Paths.Links.Add((t.CellA, t.CellB, false, false, false, false));
        }
    }

    private int CellIndex(Grid g, Vector2 p)
    {
        (int cx, int cy) = g.CellOf(new Vector3(Walkable(p), 0));
        return g.Inside(cx, cy) ? g.Index(cx, cy) : -1;
    }

    /// <summary>
    /// Case d'un bout d'escalier : la case libre la plus proche de l'icône, au niveau de son plancher. L'icône est souvent
    /// posée sur une case fermée ; la plus proche des cases libres peut être un autre étage (Tutorial : palier à 11,75 m à
    /// côté de l'icône à 8,75 m) d'où l'on ne peut plus repartir.
    /// </summary>
    private int EndCell(Grid g, Vector2 p, float z)
    {
        (int cx, int cy) = g.CellOf(new Vector3(p, 0));
        for (int r = 0; r < 8; r++)
        {
            int best = -1;
            float bd = float.MaxValue;
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r || !Free(x, y) || MathF.Abs(g.GroundHeight[g.Index(x, y)] - z) > PathFind.MaxStep)
                        continue;
                    float d = Vector2.Distance(p, CellCenter(g, g.Index(x, y)));
                    if (d < bd)
                    {
                        bd = d;
                        best = g.Index(x, y);
                    }
                }
            if (best >= 0)
                return best;
        }
        return CellIndex(g, p);
    }

    private Vector2 CellCenter(Grid g, int cell) => new Vector2((cell % g.Width + 0.5f) * Grid.CellSize, (cell / g.Width + 0.5f) * Grid.CellSize);

    /// <summary>Ordre donné sur une icône d'escalier : le chemin se termine par l'autre bout (il entre, il ressort là-bas).</summary>
    private void TeleportAtDestination(Unit u, Vector2 dest)
    {
        if (u.IsVehicle || _s.Grid is not { } g)
            return;
        int cell = CellIndex(g, dest);
        foreach (Teleport t in Teleports.Where(TeleportUsable))
        {
            if (t.Fake)
            {
                if (cell == t.CellA || cell == t.CellB)
                {
                    u.FakeStairs = t;   // faux escalier : il y va, le message vient à l'arrivée (ReachFakeStairs)
                    return;
                }
                continue;
            }
            if (cell == t.CellA)
                u.Path.Enqueue(CellCenter(g, t.CellB));
            else if (cell == t.CellB)
                u.Path.Enqueue(CellCenter(g, t.CellA));
            else
                continue;
            return;
        }
    }

    /// <summary>Prochain point de l'autre côté d'un escalier, et le pompier sur l'icône : il entre (caché pendant DURATION).</summary>
    private bool StartTeleportIfNeeded(Unit u, Vector2 next)
    {
        if (u.IsVehicle || _s.Grid is not { } g || Teleports.Count == 0)
            return false;
        (int mx, int my) = g.CellOf(u.Position);
        (int nx, int ny) = g.CellOf(new Vector3(next, 0));
        if (!g.Inside(mx, my) || !g.Inside(nx, ny))
            return false;
        int me = g.Index(mx, my), nc = g.Index(nx, ny);
        foreach (Teleport t in Teleports.Where(t => !t.Fake))
        {
            bool ab = me == t.CellA && nc == t.CellB, ba = me == t.CellB && nc == t.CellA;
            if (!ab && !ba)
                continue;
            if (!TeleportUsable(t))
            {
                u.Path.Clear();
                u.Status = "escalier fermé";
                return true;
            }
            u.Path.Dequeue();
            u.Stairs = t;
            u.StairsTimer = t.Duration;
            u.StairsTo = ab ? t.EndB : t.EndA;
            u.StairsFrom = u.Position;
            u.Status = "prend l'escalier";
            if (!string.IsNullOrEmpty(t.OnEnter))
                _s.Scripts?.TryRun(t.OnEnter, Value.I(u.Id), Value.I(t.Id));
            return true;
        }
        return false;
    }

    /// <summary>Arrivé sur un faux escalier : la fonction du script (le message) ; il ne va nulle part.</summary>
    private void ReachFakeStairs(Unit u)
    {
        Teleport t = u.FakeStairs!;
        u.FakeStairs = null;
        if (_s.Grid is not { } g)
            return;
        (int cx, int cy) = g.CellOf(u.Position);
        int me = g.Inside(cx, cy) ? g.Index(cx, cy) : -1;
        if (me != t.CellA && me != t.CellB)
            return;
        if (!string.IsNullOrEmpty(t.OnEnter))
            _s.Scripts?.TryRun(t.OnEnter, Value.I(u.Id), Value.I(t.Id));
    }

    /// <summary>Dans l'escalier : il ressort par l'autre icône au bout de la durée.</summary>
    private void UpdateTeleport(Unit u, float dt)
    {
        u.StairsTimer -= dt;
        Teleport t = u.Stairs!;
        if (t.Walk && u.StairsTimer > 0)
        {
            // escalier extérieur : il suit la volée, du point de départ au bout, en montant ou en descendant
            float k = 1 - u.StairsTimer / t.Duration;
            float zTo = u.StairsTo == t.EndA ? t.HeightA : t.HeightB;
            Vector3 to3 = new Vector3(u.StairsTo, zTo);
            u.Position = Vector3.Lerp(u.StairsFrom, to3, k);
            Vector2 d = u.StairsTo - new Vector2(u.StairsFrom.X, u.StairsFrom.Y);
            if (d.LengthSquared() > 1)
                u.Heading = MathF.Atan2(d.Y, d.X);
            return;
        }
        if (u.StairsTimer > 0)
            return;
        // il ressort sur la case du bout d'escalier (au bon étage), pas sur la case libre la plus proche de l'icône
        Vector2 to = Walkable(u.StairsTo);
        if (_s.Grid is { } g && (u.StairsTo == t.EndA ? t.CellA : t.CellB) is int end && end >= 0)
            to = CellCenter(g, end);
        u.Position = new Vector3(to, Ground(to));
        u.Stairs = null;
        u.Status = u.Path.Count > 0 ? "en route" : "attend les ordres";
        if (!string.IsNullOrEmpty(t.OnLeave))
            _s.Scripts?.TryRun(t.OnLeave, Value.I(u.Id), Value.I(t.Id));
    }
}
