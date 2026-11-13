using System.Numerics;
using OpenFD.Formats;

namespace OpenFD.Game;

/// <summary>
/// Camion échelle ([CAPACITIES_ACTIONS] action LADDER) : clic droit sur une fenêtre d'étage, il se gare devant, tourne sa
/// tourelle, lève et allonge son échelle jusqu'à la fenêtre. Les pompiers qui savent monter à l'échelle (CanLadder) y
/// montent pour casser la fenêtre, puis entrent dans l'étage ; ils redescendent par là (avec un civil sur le dos aussi).
/// </summary>
public sealed partial class UnitManager
{
    /// <summary>Durée de la rotation de la tourelle et de la levée de l'échelle (s).</summary>
    public const float LadderRaiseSeconds = 3f;
    /// <summary>Vitesse de montée le long de l'échelle (unités par seconde) ; enjamber le camion, passer la fenêtre (s).</summary>
    public const float LadderClimbSpeed = 160f, LadderMountSeconds = 1.0f, LadderWindowSeconds = 1.3f;
    /// <summary>Distance du camion à la façade ; bout de l'échelle : devant la fenêtre, sous son milieu.</summary>
    public const float LadderTruckStandOff = 1100f, LadderTipOut = 70f, LadderTipBelow = 150f;

    // repère du modèle : la tourelle (ROTZ1), la charnière de l'échelle (LADDER1), le bout de l'échelle repliée (LADDER3)
    private void SetupLadder(Unit u, ClassFile c, string gfx)
    {
        u.LadderSpeed = c.GetFloat("UNIT", "Ladder_Translation_Speed", 200);
        u.LadderMax = c.GetFloat("UNIT", "Ladder_Max_Length", 2500);
        try
        {
            SceneAssets assets = _assets ??= new SceneAssets(_files);
            float tipY = float.NaN;
            foreach (SceneAssets.Node part in assets.ModelParts(gfx))
            {
                string n = part.Mesh.ToUpperInvariant();
                if (n.Contains("ROTZ"))
                    u.LadderPivot = part.World.Translation;
                else if (n.EndsWith("LADDER1"))
                    u.LadderHinge = part.World.Translation;
                else if (n.EndsWith("LADDER3"))
                {
                    Mesh m = assets.LoadMesh("", part.Mesh, "gfx").Mesh;
                    for (int i = 0; i < m.VertexCount; i++)
                    {
                        float y = Vector3.Transform(new Vector3(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]), part.World).Y;
                        tipY = float.IsNaN(tipY) ? y : MathF.Min(tipY, y);
                    }
                }
            }
            if (!float.IsNaN(tipY))
                u.LadderRest = u.LadderHinge.Y - tipY;   // l'échelle repliée pointe vers l'avant du camion (−Y du modèle)
        }
        catch (Exception e)
        {
            Log.Add($"Échelle de {u.Name} : {e.Message}");
        }
    }

    /// <summary>Point du repère du modèle d'un véhicule, dans le monde (les véhicules regardent vers −Y du modèle).</summary>
    public static Vector3 VehicleToWorld(Unit v, Vector3 m) =>
        Vector3.Transform(m, Matrix4x4.CreateRotationZ(v.Heading + MathF.PI / 2)) + v.Position;

    /// <summary>Charnière de l'échelle dans le repère du modèle, la tourelle tournée de <paramref name="yaw"/>.</summary>
    public static Vector3 LadderHingeAt(Unit v, float yaw)
    {
        Vector3 off = v.LadderHinge - v.LadderPivot;
        return v.LadderPivot + Vector3.Transform(off, Matrix4x4.CreateRotationZ(yaw));
    }

    /// <summary>Échelle dressée (ou en route) devant cette fenêtre ; null s'il n'y en a pas.</summary>
    public Ladder? LadderAt(Opening d) => Units.FirstOrDefault(v => v.Ladder is { Retracting: false } l && l.Window == d)?.Ladder;

    /// <summary>Échelles des camions (dessin).</summary>
    public IEnumerable<Ladder> Ladders => Units.Where(v => v.Ladder != null && !v.Hidden && !v.OnBoard).Select(v => v.Ladder!);

    private readonly Dictionary<Opening, bool> _highWindow = new();

    /// <summary>Fenêtre d'étage : trop haute pour être atteinte depuis le sol dehors (il faut l'échelle, ou passer par l'intérieur).</summary>
    public bool IsHighWindow(Opening d)
    {
        if (!d.IsWindow)
            return false;
        if (_highWindow.TryGetValue(d, out bool high))
            return high;
        high = WindowSides(d, out _, out _);
        _highWindow[d] = high;
        return high;
    }

    /// <summary>
    /// Côtés d'une fenêtre d'étage : <paramref name="outward"/> vers la rue (le sol y est bien plus bas que la fenêtre),
    /// <paramref name="inside"/> une case libre du plancher derrière (null : pas de plancher où entrer). false : pas une
    /// fenêtre d'étage donnant sur l'extérieur.
    /// </summary>
    private bool WindowSides(Opening d, out Vector2 outward, out Vector2? inside)
    {
        outward = Vector2.Zero;
        inside = null;
        if (_s.Grid is not { } g)
            return false;
        Vector2 c = new Vector2(d.Center.X, d.Center.Y);
        Vector2 sum = Vector2.Zero;
        for (int k = 0; k < 16; k++)
        {
            Vector2 dir = new Vector2(MathF.Cos(k * MathF.PI / 8), MathF.Sin(k * MathF.PI / 8));
            foreach (float r in new[] { 200f, 300f })
            {
                Vector2 q = c + dir * r;
                (int qx, int qy) = g.CellOf(new Vector3(q, 0));
                if (FreeCell(qx, qy) && Ground(q) < d.Center.Z - ReachHeight)
                    sum += dir;
            }
        }
        if (sum.Length() < 1.5f)
            return false;
        outward = Vector2.Normalize(sum);
        for (float r = 100; r <= 500; r += 50)
        {
            Vector2 q = c - outward * r;
            (int qx, int qy) = g.CellOf(new Vector3(q, 0));
            float z = Ground(q);
            if (FreeCell(qx, qy) && z > d.Center.Z - 450 && z < d.Center.Z)
            {
                inside = new Vector2((qx + 0.5f) * Grid.CellSize, (qy + 0.5f) * Grid.CellSize);
                break;
            }
        }
        return true;
    }

    /// <summary>
    /// Bord d'un haut lieu sans fenêtre (Stadium : le haut des gradins, à 14 m, où deux civils attendent) : depuis le point
    /// <paramref name="p"/>, posé sur une case libre en hauteur, la direction la plus courte (à moins de 12 m) vers une case
    /// libre du sol bien plus bas ; le bord est la dernière case à la hauteur de <paramref name="p"/> dans cette direction.
    /// false : pas un haut lieu (ou pas de sol en dessous à portée).
    /// </summary>
    public bool FindLedge(Vector2 p, out Vector2 edge, out Vector2 outward, out float height)
    {
        edge = p;
        outward = Vector2.Zero;
        height = 0;
        if (_s.Grid is not { } g)
            return false;
        (int px, int py) = g.CellOf(new Vector3(p, 0));
        if (!FreeCell(px, py))
            return false;
        float h = Ground(p);
        float best = float.MaxValue;
        for (int k = 0; k < 16; k++)
        {
            Vector2 dir = new Vector2(MathF.Cos(k * MathF.PI / 8), MathF.Sin(k * MathF.PI / 8));
            Vector2 last = p;
            float lastR = 0;
            for (float r = 50; r <= 1200 && r < best; r += 50)
            {
                Vector2 q = p + dir * r;
                (int qx, int qy) = g.CellOf(new Vector3(q, 0));
                if (!g.Inside(qx, qy))
                    break;
                bool free = FreeCell(qx, qy);
                float z = Ground(q);
                if (free && MathF.Abs(z - h) < 100)
                {
                    last = q;
                    lastR = r;
                    continue;
                }
                // le sol en dessous, juste derrière le bord (un parapet d'une case au plus, pas tout un bâtiment)
                if (free && z < h - 2 * ReachHeight && r - lastR <= 250)
                {
                    best = r;
                    edge = last;
                    outward = dir;
                }
                else if (free || r - lastR > 250)
                    break;   // autre niveau, ou mur épais : pas un bord
            }
        }
        if (best == float.MaxValue)
            return false;
        height = h;
        return true;
    }

    /// <summary>
    /// « Fenêtre » sans modèle posée au bord d'un haut lieu (FindLedge), pour y dresser l'échelle : ouverte, on y monte et on
    /// en descend comme par une fenêtre. La même pour un même bord (son numéro vient de la case). null : pas de bord ici.
    /// </summary>
    public Opening? LedgeAt(Vector2 p)
    {
        if (_s.Grid is not { } g || !FindLedge(p, out Vector2 edge, out Vector2 outward, out float h))
            return null;
        (int ex, int ey) = g.CellOf(new Vector3(edge, 0));
        uint id = 0xFE000000u + (uint)g.Index(ex, ey);
        if (Openings.FirstOrDefault(o => o.Id == id) is { } known)
            return known;
        Vector2 c = new Vector2((ex + 0.5f) * Grid.CellSize, (ey + 0.5f) * Grid.CellSize) + outward * 50;
        Opening d = new Opening
        {
            Id = id, Name = $"bord {ex},{ey}", IsWindow = true, Model = null, World = Matrix4x4.CreateTranslation(c.X, c.Y, h),
            State = OpeningState.Broken, Breakable = false, Usable = false, OnGround = false,
        };
        d.Center = new Vector3(c, h + LadderTipBelow);
        Openings.Add(d);
        return d;
    }

    /// <summary>Clic droit du camion échelle sur un haut lieu sans fenêtre : il dresse son échelle jusqu'au bord (LedgeAt).</summary>
    public bool DeployLadderToLedge(Unit truck, Vector2 p)
    {
        if (!truck.IsAerial || truck.Destroyed)
            return false;
        if (LedgeAt(p) is not { } d)
        {
            truck.Status = "l'échelle ne sert pas ici";
            Sounds.Add(("VOCAL_IMPOSSIBLE_TO_ACCESS", null));
            return false;
        }
        return DeployLadder(truck, d);
    }

    /// <summary>Clic droit du camion échelle sur une fenêtre d'étage : il va se garer devant et dresser son échelle.</summary>
    public bool DeployLadder(Unit truck, Opening d)
    {
        if (!truck.IsAerial || truck.Destroyed)
            return false;
        if (truck.Ladder is { Retracting: false } cur && cur.Window == d)
            return true;
        if (!WindowSides(d, out Vector2 outward, out Vector2? inside))
        {
            truck.Status = $"{d.Name} : l'échelle ne sert pas ici";
            Sounds.Add(("VOCAL_IMPOSSIBLE_TO_ACCESS", null));
            return false;
        }
        if (truck.Ladder is { Placed: true } l)
        {
            // dressée ailleurs : il la replie d'abord
            l.Retracting = true;
            truck.LadderNext = d;
            truck.DriveNext = null;
            truck.Status = "replie l'échelle";
            return true;
        }
        Vector2 c = new Vector2(d.Center.X, d.Center.Y);
        Vector2 spot = Walkable(c + outward * LadderTruckStandOff);
        if (!Order(truck, spot, null, drive: true))
            return false;
        int insideCell = -1;
        if (inside is { } iv && _s.Grid is { } g)
        {
            (int ix, int iy) = g.CellOf(new Vector3(iv, 0));
            insideCell = g.Index(ix, iy);
        }
        truck.Ladder = new Ladder
        {
            Truck = truck, Window = d,
            Tip = new Vector3(c + outward * LadderTipOut, d.Center.Z - LadderTipBelow),
            Inside = inside ?? c, InsideCell = insideCell,
        };
        truck.LadderNext = null;
        truck.DriveNext = null;
        truck.Status = $"va dresser l'échelle devant {d.Name}";
        return true;
    }

    // le camion est arrivé : rotation de la tourelle, inclinaison et longueur de l'échelle jusqu'au bout voulu
    private bool PlaceLadder(Ladder l)
    {
        Unit t = l.Truck;
        Vector3 pivot = VehicleToWorld(t, t.LadderPivot);
        float world = MathF.Atan2(l.Tip.Y - pivot.Y, l.Tip.X - pivot.X);
        float yaw = MathF.IEEERemainder(world - t.Heading, 2 * MathF.PI);
        Vector3 hinge = VehicleToWorld(t, LadderHingeAt(t, yaw));
        float horiz = Vector2.Distance(new(hinge.X, hinge.Y), new(l.Tip.X, l.Tip.Y));
        float len = Vector3.Distance(hinge, l.Tip);
        if (len > t.LadderMax)
            return false;
        l.Yaw = yaw;
        l.Pitch = MathF.Atan2(l.Tip.Z - hinge.Z, horiz);
        l.ExtendWanted = MathF.Max(0, len - t.LadderRest);
        l.Base = hinge;
        // pied de l'échelle : au sol derrière le camion (on y grimpe par l'arrière)
        Vector3 back = VehicleToWorld(t, new Vector3(0, t.LadderHinge.Y + 160, 0));
        l.Foot = Walkable(new Vector2(back.X, back.Y));
        if (_s.Grid is { } g)
        {
            (int fx, int fy) = g.CellOf(new Vector3(l.Foot, 0));
            l.FootCell = g.Inside(fx, fy) ? g.Index(fx, fy) : -1;
        }
        l.Placed = true;
        return true;
    }

    /// <summary>À chaque pas : camions échelle (levée, allongement, repli) et passages par les échelles pour la recherche de chemin.</summary>
    private void UpdateLadders(float dt)
    {
        Paths.Links.Clear();
        foreach (Unit t in Units.Where(v => v.IsAerial && v.Ladder != null))
        {
            Ladder l = t.Ladder!;
            if (t.Destroyed || t.OnBoard)
            {
                foreach (Unit u in Units.Where(x => x.OnLadder == l))
                    LadderDrop(u, l);
                t.Ladder = null;
                continue;
            }
            if (!l.Placed)
            {
                if (t.Moving)
                    continue;
                if (!PlaceLadder(l))
                {
                    t.Ladder = null;
                    t.Status = $"{l.Window.Name} : trop loin pour l'échelle";
                    Sounds.Add(("VOCAL_FIRE_OUT_OF_RANGE", null));
                    continue;
                }
                t.Status = "dresse l'échelle";
                continue;
            }
            if (l.Retracting)
            {
                if (Units.Any(x => x.OnLadder == l))
                {
                    t.Status = "attend que l'échelle soit libre";
                    continue;
                }
                if (l.Extend > 0)
                    l.Extend = MathF.Max(0, l.Extend - t.LadderSpeed * dt);
                else
                    l.Raise = MathF.Max(0, l.Raise - dt / LadderRaiseSeconds);
                if (l.Raise > 0)
                    continue;
                t.Ladder = null;
                t.Status = "attend les ordres";
                if (t.LadderNext is { } next)
                {
                    t.LadderNext = null;
                    DeployLadder(t, next);
                }
                else if (t.DriveNext is { } dest)
                {
                    t.DriveNext = null;
                    Order(t, dest, null, drive: true);
                }
                continue;
            }
            bool wasReady = l.Ready;
            if (l.Raise < 1)
                l.Raise = MathF.Min(1, l.Raise + dt / LadderRaiseSeconds);
            else if (l.Extend < l.ExtendWanted)
                l.Extend = MathF.Min(l.ExtendWanted, l.Extend + t.LadderSpeed * dt);
            if (l.Ready && !wasReady)
            {
                t.Status = $"échelle dressée devant {l.Window.Name}";
                Log.Add($"{_s.Time,6:0} s : échelle de {t.Name} dressée devant {l.Window.Name}");
            }
            // l'échelle fait partie des chemins (fenêtre fermée : pour ceux qui peuvent la casser ou l'ouvrir en passant)
            if (l.Ready && l.FootCell >= 0 && l.InsideCell >= 0)
                Paths.Links.Add((l.FootCell, l.InsideCell, l.Window.State == OpeningState.Closed, l.Window.Breakable, l.Window.Usable, true));
        }
        UpdateTeleportLinks();
    }

    /// <summary>
    /// Pompier en marche dont le prochain point passe par une échelle (de son pied à l'étage, ou l'inverse) : il commence
    /// à monter ou à descendre. L'échelle pas encore dressée : il attend à son pied.
    /// </summary>
    private bool StartLadderIfNeeded(Unit u, Vector2 next)
    {
        if (!u.CanLadder || u.IsVehicle || _s.Grid is not { } g)
            return false;
        (int mx, int my) = g.CellOf(u.Position);
        (int nx, int ny) = g.CellOf(new Vector3(next, 0));
        if (!g.Inside(mx, my) || !g.Inside(nx, ny))
            return false;
        int me = g.Index(mx, my), nc = g.Index(nx, ny);
        foreach (Unit t in Units.Where(v => v.Ladder != null))
        {
            Ladder l = t.Ladder!;
            bool up = me == l.FootCell && (nc == l.InsideCell || next == l.Inside);
            bool down = me == l.InsideCell && nc == l.FootCell;
            if (!up && !down)
                continue;
            if (!l.Ready)
            {
                u.Status = "attend que l'échelle soit dressée";
                return true;
            }
            // descendre depuis l'étage par une fenêtre fermée : il la casse (ou l'ouvre) d'abord, sans quitter son chemin
            if (down && l.Window.State == OpeningState.Closed)
            {
                if (!CanPass(u, l))
                {
                    u.Path.Clear();
                    u.Status = "fenêtre fermée : impossible de passer";
                    return true;
                }
                if (u.Opening != l.Window)
                {
                    u.Opening = l.Window;
                    u.OpeningBreak = !(l.Window.Usable && u.CanUse) && u.CanBreak && l.Window.Breakable;
                    u.ActionTimer = 0;
                    u.PassingWindow = true;
                }
                return true;
            }
            u.PassingWindow = false;
            u.Path.Dequeue();
            u.OnLadder = l;
            u.LadderTimer = 0;
            u.ClimbFrom = u.Position;
            u.LadderEnter = up;
            u.LadderStep = up ? LadderPhase.Mount : LadderPhase.Leave;
            u.Status = up ? "monte à l'échelle" : "descend par l'échelle";
            return true;
        }
        return false;
    }

    /// <summary>Échelle dressée dont les deux bouts (pied, étage) sont les cases de ces deux points (un pas « lien » d'un chemin).</summary>
    private Ladder? LadderBetween(Vector2 a, Vector2 b)
    {
        if (_s.Grid is not { } g)
            return null;
        int ca = CellIndex(g, a), cb = CellIndex(g, b);
        foreach (Unit v in Units)
            if (v.Ladder is { } l && l.Ready && l.FootCell >= 0 && l.InsideCell >= 0
                && ((l.FootCell == ca && l.InsideCell == cb) || (l.FootCell == cb && l.InsideCell == ca)))
                return l;
        return null;
    }

    /// <summary>Personne ne s'engage sur l'échelle (on n'y monte pas l'un dans l'autre : on attend que le premier y soit).</summary>
    private bool LadderFree(Ladder l) =>
        !Units.Any(x => x.OnLadder == l && x.LadderStep is LadderPhase.Mount or LadderPhase.Leave or LadderPhase.Enter or LadderPhase.Dismount)
        && !Civilians.Any(x => x.OnLadder == l && x.LadderStep is LadderPhase.Mount or LadderPhase.Leave or LadderPhase.Enter or LadderPhase.Dismount);

    /// <summary>Un civil qui suit son guide s'engage sur l'échelle : depuis le sol (il monte) ou depuis l'étage (il descend).</summary>
    private void StartCivilianLadder(Civilian c, Ladder l, Vector2 from, Vector2 to)
    {
        c.OnLadder = l;
        c.LadderUp = _s.Grid is { } g && CellIndex(g, to) == l.InsideCell;
        c.LadderStep = c.LadderUp ? LadderPhase.Mount : LadderPhase.Leave;
        c.LadderTimer = 0;
        c.ClimbFrom = new Vector3(from, Ground(from));
        c.Position = c.ClimbFrom;
    }

    /// <summary>
    /// Civil sur l'échelle (il suit son guide) : comme le pompier, sans s'arrêter en haut. Il monte (enjamber le camion,
    /// grimper, passer la fenêtre) ou descend (passer la fenêtre, descendre, quitter le camion). Échelle repliée ou camion
    /// parti, ou civil évanoui : il se retrouve au pied.
    /// </summary>
    private void UpdateCivilianLadder(Civilian c, float dt)
    {
        Ladder l = c.OnLadder!;
        if (!l.Placed || l.Truck.Ladder != l || c.State != CivilianState.Conscious)
        {
            EndCivilianLadder(c, new Vector3(l.Foot, Ground(l.Foot)));
            return;
        }
        float len = MathF.Max(1, Vector3.Distance(l.Base, l.Tip));
        Vector2 toWindow = new Vector2(l.Window.Center.X - l.Tip.X, l.Window.Center.Y - l.Tip.Y);
        float faceWindow = MathF.Atan2(toWindow.Y, toWindow.X);
        float alongLadder = MathF.Atan2(l.Tip.Y - l.Base.Y, l.Tip.X - l.Base.X);
        c.LadderTimer += dt;
        switch (c.LadderStep)
        {
            case LadderPhase.Mount:
            {
                float k = Math.Clamp(c.LadderTimer / LadderMountSeconds, 0, 1);
                c.Position = Vector3.Lerp(c.ClimbFrom, l.Base, k);
                c.Heading = alongLadder;
                if (k >= 1)
                    Next(LadderPhase.Up);
                break;
            }
            case LadderPhase.Up:
            {
                float k = Math.Clamp(c.LadderTimer * LadderClimbSpeed / len, 0, 1);
                c.Position = Vector3.Lerp(l.Base, l.Tip, k);
                c.Heading = alongLadder;
                if (k >= 1)
                {
                    Next(LadderPhase.Enter);
                    c.ClimbFrom = new Vector3(l.Inside, Ground(l.Inside));
                }
                break;
            }
            case LadderPhase.Enter:
            {
                float k = Math.Clamp(c.LadderTimer / LadderWindowSeconds, 0, 1);
                c.Position = Vector3.Lerp(l.Tip, c.ClimbFrom, k);
                c.Heading = faceWindow;
                if (k >= 1)
                    EndCivilianLadder(c, c.ClimbFrom);
                break;
            }
            case LadderPhase.Leave:
            {
                float k = Math.Clamp(c.LadderTimer / LadderWindowSeconds, 0, 1);
                c.Position = Vector3.Lerp(c.ClimbFrom, l.Tip, k);
                c.Heading = faceWindow + MathF.PI;
                if (k >= 1)
                    Next(LadderPhase.Down);
                break;
            }
            case LadderPhase.Down:
            {
                float k = Math.Clamp(c.LadderTimer * LadderClimbSpeed / len, 0, 1);
                c.Position = Vector3.Lerp(l.Tip, l.Base, k);
                c.Heading = alongLadder;
                if (k >= 1)
                {
                    Next(LadderPhase.Dismount);
                    c.ClimbFrom = new Vector3(l.Foot, Ground(l.Foot));
                }
                break;
            }
            case LadderPhase.Dismount:
            {
                float k = Math.Clamp(c.LadderTimer / LadderMountSeconds, 0, 1);
                c.Position = Vector3.Lerp(l.Base, c.ClimbFrom, k);
                if (k >= 1)
                    EndCivilianLadder(c, c.ClimbFrom);
                break;
            }
            default:
                EndCivilianLadder(c, new Vector3(l.Foot, Ground(l.Foot)));
                break;
        }

        void Next(LadderPhase p)
        {
            c.LadderStep = p;
            c.LadderTimer = 0;
        }
    }

    // le civil quitte l'échelle (au bout, ou au pied si elle disparaît) : nouveau chemin vers son guide
    private static void EndCivilianLadder(Civilian c, Vector3 at)
    {
        c.OnLadder = null;
        c.LadderStep = LadderPhase.None;
        c.Position = at;
        c.FollowPath.Clear();
        c.FollowGoal = new Vector2(float.MaxValue, 0);
    }

    /// <summary>Le pompier peut passer par la fenêtre de cette échelle : ouverte, ou il peut la casser ou l'ouvrir.</summary>
    private static bool CanPass(Unit u, Ladder l) =>
        l.InsideCell >= 0 && (l.Window.State != OpeningState.Closed || (u.CanBreak && l.Window.Breakable) || (u.CanUse && l.Window.Usable));

    /// <summary>
    /// Là où le pompier sur l'échelle arrivera (le chemin d'un nouvel ordre part de là) : en montant, par la fenêtre ou de
    /// nouveau au pied, selon ce qui mène le plus vite à <paramref name="dest"/> ; en descendant, au pied.
    /// </summary>
    private Vector2 LadderExit(Unit u, Vector2? dest = null)
    {
        Ladder l = u.OnLadder!;
        bool goingUp = u.LadderStep is LadderPhase.Mount or LadderPhase.Up or LadderPhase.Top;
        if (u.LadderStep == LadderPhase.Enter)
            return l.Inside;
        if (!goingUp || !CanPass(u, l))
        {
            u.LadderEnter = false;
            return l.Foot;
        }
        if (dest is not { } to)
            return u.LadderEnter ? l.Inside : l.Foot;
        // par l'étage ou en redescendant : la montée restante et la descente comptent (en longueur de chemin)
        float climb = Vector3.Distance(l.Base, l.Tip);
        List<Vector2> viaIn = FindPath(l.Inside, to, out bool partIn);
        List<Vector2> viaFoot = FindPath(l.Foot, to, out bool partFoot);
        float lenIn = partIn ? float.MaxValue : PathLength(l.Inside, viaIn);
        float lenFoot = partFoot ? float.MaxValue : PathLength(l.Foot, viaFoot) + climb * 2;
        u.LadderEnter = lenIn <= lenFoot;
        return u.LadderEnter ? l.Inside : l.Foot;
    }

    private static float PathLength(Vector2 from, List<Vector2> path)
    {
        float len = 0;
        foreach (Vector2 p in path)
        {
            len += Vector2.Distance(from, p);
            from = p;
        }
        return len;
    }

    // l'échelle disparaît sous lui (camion détruit) : il se retrouve au pied
    private void LadderDrop(Unit u, Ladder l)
    {
        u.OnLadder = null;
        u.LadderStep = LadderPhase.None;
        u.Position = new Vector3(l.Foot, Ground(l.Foot));
        u.Status = "attend les ordres";
    }

    /// <summary>Montée (enjamber le camion, grimper, passer la fenêtre) ou descente (l'inverse), le long de l'échelle.</summary>
    private void UpdateLadderClimb(Unit u, float dt)
    {
        Ladder l = u.OnLadder!;
        if (!l.Placed || l.Truck.Ladder != l)
        {
            LadderDrop(u, l);
            return;
        }
        if (u.Unconscious)
        {
            LadderDrop(u, l);
            return;
        }
        float slow = u.Carry == CarryPhase.Walking ? CarrySpeed : 1f;   // un civil sur le dos : plus lent
        float len = MathF.Max(1, Vector3.Distance(l.Base, l.Tip));
        Vector2 toWindow = new Vector2(l.Window.Center.X - l.Tip.X, l.Window.Center.Y - l.Tip.Y);
        float faceWindow = MathF.Atan2(toWindow.Y, toWindow.X);
        float alongLadder = MathF.Atan2(l.Tip.Y - l.Base.Y, l.Tip.X - l.Base.X);
        u.LadderTimer += dt * slow;
        switch (u.LadderStep)
        {
            case LadderPhase.Mount:
            {
                float k = Math.Clamp(u.LadderTimer / LadderMountSeconds, 0, 1);
                u.Position = Vector3.Lerp(u.ClimbFrom, l.Base, k);
                u.Heading = alongLadder;
                if (k >= 1)
                    Next(LadderPhase.Up, "monte à l'échelle");
                break;
            }
            case LadderPhase.Up:
            {
                float k = Math.Clamp(u.LadderTimer * LadderClimbSpeed / len, 0, 1);
                u.Position = Vector3.Lerp(l.Base, l.Tip, k);
                u.Heading = alongLadder;
                if (k >= 1)
                    Next(LadderPhase.Top, "en haut de l'échelle");
                break;
            }
            case LadderPhase.Top:
                u.Position = l.Tip;
                u.Heading = faceWindow;
                if (l.Window.State != OpeningState.Closed && l.InsideCell >= 0 && u.LadderEnter)
                {
                    u.PassingWindow = false;
                    Next(LadderPhase.Enter, "entre par la fenêtre");
                    u.ClimbFrom = new Vector3(l.Inside, Ground(l.Inside));
                }
                else if (u.Opening == l.Window)
                    break;   // il casse ou ouvre la fenêtre (WorkOpening)
                else if (u.LadderEnter && CanPass(u, l))
                {
                    // fenêtre fermée sur son chemin : il la casse (ou l'ouvre) en passant
                    u.Opening = l.Window;
                    u.OpeningBreak = !(l.Window.Usable && u.CanUse) && u.CanBreak && l.Window.Breakable;
                    u.ActionTimer = 0;
                    u.PassingWindow = true;
                }
                else if (!u.LadderEnter || u.Path.Count > 0)
                    Next(LadderPhase.Down, "redescend");
                else if (!u.Status.StartsWith("en haut"))
                    u.Status = "en haut de l'échelle";
                break;
            case LadderPhase.Enter:
            {
                float k = Math.Clamp(u.LadderTimer / LadderWindowSeconds, 0, 1);
                u.Position = Vector3.Lerp(l.Tip, u.ClimbFrom, k);
                u.Heading = faceWindow;
                if (k < 1)
                    break;
                u.OnLadder = null;
                u.LadderStep = LadderPhase.None;
                u.Status = u.Path.Count > 0 ? "en route" : "attend les ordres";
                break;
            }
            case LadderPhase.Leave:
            {
                float k = Math.Clamp(u.LadderTimer / LadderWindowSeconds, 0, 1);
                u.Position = Vector3.Lerp(u.ClimbFrom, l.Tip, k);
                u.Heading = faceWindow + MathF.PI;
                if (k >= 1)
                    Next(LadderPhase.Down, "descend par l'échelle");
                break;
            }
            case LadderPhase.Down:
            {
                float k = Math.Clamp(u.LadderTimer * LadderClimbSpeed / len, 0, 1);
                u.Position = Vector3.Lerp(l.Tip, l.Base, k);
                u.Heading = alongLadder;   // face à l'échelle, comme pour monter
                if (k >= 1)
                {
                    Next(LadderPhase.Dismount, "descend du camion");
                    u.ClimbFrom = new Vector3(l.Foot, Ground(l.Foot));
                }
                break;
            }
            case LadderPhase.Dismount:
            {
                float k = Math.Clamp(u.LadderTimer / LadderMountSeconds, 0, 1);
                u.Position = Vector3.Lerp(l.Base, u.ClimbFrom, k);
                if (k < 1)
                    break;
                u.OnLadder = null;
                u.LadderStep = LadderPhase.None;
                u.Status = u.Path.Count > 0 ? "en route" : "attend les ordres";
                break;
            }
        }

        void Next(LadderPhase p, string status)
        {
            u.LadderStep = p;
            u.LadderTimer = 0;
            u.Status = status;
        }
    }

    /// <summary>
    /// Ordre sur une fenêtre d'étage devant laquelle une échelle est dressée : le pompier y monte (pour la casser ou
    /// l'ouvrir s'il le peut, sinon pour entrer), ou redescend s'il est déjà à l'étage. false : pas d'échelle utile ici.
    /// </summary>
    private bool LadderOrder(Unit u, Opening d, bool breakIt, bool useIt)
    {
        if (!u.CanLadder || LadderAt(d) is not { } l)
            return false;
        if (d.State != OpeningState.Closed)
        {
            // fenêtre ouverte ou cassée : on entre ou on sort par l'échelle
            bool upstairs = l.InsideCell >= 0 && MathF.Abs(u.Position.Z - Ground(l.Inside)) < 150 && u.OnLadder == null;
            if (!Order(u, upstairs ? l.Foot : l.Inside))
                return true;
            u.Status = upstairs ? "va redescendre par l'échelle" : "va monter à l'échelle";
            return true;
        }
        if (!breakIt && !useIt)
            return false;
        if (u.OnLadder == l && u.LadderStep == LadderPhase.Top)
        {
            u.Path.Clear();
            u.Target = null;
            u.LadderEnter = true;
        }
        else
        {
            if (!Order(u, l.Foot))
                return true;
            u.Path.Enqueue(l.Inside);   // au pied : il monte (voir StartLadderIfNeeded) et s'arrête en haut, la fenêtre fermée
        }
        u.Opening = d;
        u.OpeningBreak = breakIt;
        u.ActionTimer = 0;
        u.Status = breakIt ? $"va casser {d.Name} depuis l'échelle" : $"va ouvrir {d.Name} depuis l'échelle";
        return true;
    }
}
