using System.Numerics;
using FD3.Fire;

namespace FD3.Game;

// ===================================================================================================================
// IA des civils et de leur sauvetage :
//  - un pompier ou le médecin va chercher un civil (FetchCivilian) ; arrivé, le médecin réanime un civil inconscient,
//    un civil conscient se met à suivre son guide (RescueCivilian) ;
//  - le civil suit son guide par le chemin de la grille, le même que celui des pompiers (FollowLeader), et il est mis à
//    l'abri quand il arrive avec lui près de l'ambulance (Follow) ;
//  - les déplacements commandés par les scripts (IA_CmdMoveTo : figurants des cinématiques) passent aussi par Follow.
// ===================================================================================================================
public sealed partial class UnitManager
{
    /// <summary>Clic droit sur un civil : l'unité va le chercher (et le médecin le réanime s'il est inconscient).</summary>
    public void FetchCivilian(Unit u, Civilian c)
    {
        if (u.IsVehicle) return;
        if (u.Carrying != null) { u.Status = "porte déjà un civil"; return; }
        if (c.Carrier != null) { u.Status = $"{c.Name} est déjà porté"; return; }
        if (!Order(u, new(c.Position.X, c.Position.Y))) return;
        u.GoTo = c;
        u.Status = $"va chercher {c.Name}";
    }

    /// <summary>Unité arrivée près du civil qu'elle venait chercher : le médecin le réanime s'il est inconscient ; conscient,
    /// il la suit. Un médecin arrêté sans ordre à côté d'un civil inconscient le réanime de lui-même.</summary>
    private void RescueCivilian(Unit u, float dt)
    {
        var me = new Vector2(u.Position.X, u.Position.Y);
        // médecin arrêté sans ordre à côté d'un civil inconscient : il le réanime de lui-même
        if (u.CanHeal && u.GoTo == null && u.GoToVehicle == null && !u.Moving && u.Target == null)
            u.GoTo = Civilians.FirstOrDefault(c => c.State == CivilianState.Unconscious && !c.Hidden && Vector2.Distance(me, new(c.Position.X, c.Position.Y)) < 250);
        if (u.GoTo is { } c && !u.Moving)
        {
            if (Vector2.Distance(me, new(c.Position.X, c.Position.Y)) > ReachDistance * 2.5f) { u.GoTo = null; u.Status = "civil hors d'atteinte"; return; }
            if (c.State == CivilianState.Unconscious)
            {
                // pompier : il soulève le civil et le porte sur son dos jusqu'à l'ambulance ; le médecin le réanime sur place
                if (!u.CanHeal) { u.GoTo = null; if (c.Carrier == null && u.Carrying == null) StartCarry(u, c); return; }
                u.HealTimer += dt; u.Status = $"réanime {c.Name} ({u.HealTimer / HealSeconds:P0})";
                if (u.HealTimer >= HealSeconds) { c.State = CivilianState.Conscious; u.HealTimer = 0; Log.Add($"{_s.Time,6:0} s : {c.Name} réanimé"); }
                return;
            }
            if (c.State == CivilianState.Conscious) { c.Leader = u; u.GoTo = null; u.Status = $"{c.Name} le suit"; }
        }
    }

    // ---- civil inconscient porté par un pompier (animations du jeu : cc_lift_body, sc_walk_generic_body, cc_drop_body) ----
    public const float LiftSeconds = 3f, DropSeconds = 3f;
    /// <summary>Vitesse d'un pompier qui porte un corps (part de sa vitesse normale ; options : Gameplay).</summary>
    public static float CarrySpeed = 0.6f;

    private void StartCarry(Unit u, Civilian c)
    {
        u.Path.Clear(); u.Target = null;
        u.Carrying = c; u.Carry = CarryPhase.Lifting; u.CarryTimer = 0;
        c.Carrier = u; c.Leader = null;
        u.Heading = MathF.Atan2(c.Position.Y - u.Position.Y, c.Position.X - u.Position.X);
        u.Status = $"soulève {c.Name}";
    }

    /// <summary>Le pompier pose le civil là où il est (il tombe inconscient, monte dans un véhicule…).</summary>
    private void DropVictimHere(Unit u)
    {
        if (u.Carrying is { } c) { c.Carrier = null; c.Position = new Vector3(u.Position.X, u.Position.Y, Ground(new(u.Position.X, u.Position.Y))); }
        u.Carrying = null; u.Carry = CarryPhase.None; u.CarryTimer = 0;
    }

    /// <summary>Ambulance la plus proche à laquelle on peut aller : le porteur s'y rend de lui-même.</summary>
    private bool GoToNearestAmbulance(Unit u)
    {
        var me = new Vector2(u.Position.X, u.Position.Y);
        foreach (var amb in Units.Where(v => v.IsAmbulance && !v.Destroyed && !v.Hidden).OrderBy(v => Vector2.Distance(me, new(v.Position.X, v.Position.Y))))
        {
            var ap = new Vector2(amb.Position.X, amb.Position.Y);
            var spot = Walkable(ap + Vector2.Normalize(me - ap + new Vector2(0.01f, 0)) * VehicleReach * 0.8f);
            if (Order(u, spot)) { u.Status = $"porte {u.Carrying?.Name} vers l'ambulance"; return true; }
        }
        u.Status = $"porte {u.Carrying?.Name} : pas de chemin vers une ambulance";
        return false;
    }

    private void CarryVictim(Unit u, float dt)
    {
        if (u.Carrying is not { } c) return;
        if (u.Unconscious || u.OnBoard || u.Destroyed || c.State != CivilianState.Unconscious) { DropVictimHere(u); return; }
        u.Target = null;
        switch (u.Carry)
        {
            case CarryPhase.Lifting:
                u.Path.Clear();
                u.CarryTimer += dt;
                c.Position = u.Position; c.Heading = u.Heading;
                if (u.CarryTimer >= LiftSeconds) { u.Carry = CarryPhase.Walking; u.CarryTimer = 0; GoToNearestAmbulance(u); }
                break;
            case CarryPhase.Walking:
                c.Position = u.Position; c.Heading = u.Heading;
                if (u.Moving) break;
                var amb = Units.Where(v => v.IsAmbulance && !v.Destroyed && !v.Hidden).OrderBy(v => Vector3.Distance(v.Position, u.Position)).FirstOrDefault();
                if (amb != null && Vector3.Distance(amb.Position, u.Position) < VehicleReach * 1.5f)
                {
                    u.Carry = CarryPhase.Dropping; u.CarryTimer = 0;
                    u.Heading = MathF.Atan2(amb.Position.Y - u.Position.Y, amb.Position.X - u.Position.X);
                    u.Status = $"dépose {c.Name} à l'ambulance";
                }
                else if (!u.Status.Contains("pas de chemin")) u.Status = $"porte {c.Name}";
                break;
            case CarryPhase.Dropping:
                u.Path.Clear();
                u.CarryTimer += dt;
                c.Position = u.Position; c.Heading = u.Heading;
                if (u.CarryTimer < DropSeconds) break;
                c.Carrier = null; u.Carrying = null; u.Carry = CarryPhase.None; u.CarryTimer = 0;
                Save(c);
                Log.Add($"{_s.Time,6:0} s : {c.Name} déposé dans l'ambulance ({Rescued} sauvé(s))");
                Sounds.Add(("VOCAL_CIVILIAN_SAVED", null));
                u.Status = "attend les ordres";
                break;
        }
    }

    // un civil conscient suit l'unité qui est venue le chercher, en restant un peu derrière
    private void Follow(Civilian c, float dt)
    {
        // déplacement commandé par le script (IA_CmdMoveTo : figurants d'une cinématique)
        if (c.Path.Count > 0 && c.State == CivilianState.Conscious)
        {
            float st = c.RunSpeed * dt;
            while (st > 0 && c.Path.Count > 0)
            {
                var p0 = new Vector2(c.Position.X, c.Position.Y); var t = c.Path.Peek();
                var dv = t - p0; float len = dv.Length();
                if (len > 1e-3f) c.Heading = MathF.Atan2(dv.Y, dv.X);
                if (len <= st) { p0 = t; c.Path.Dequeue(); st -= len; } else { p0 += dv / len * st; st = 0; }
                c.Position = new Vector3(p0, Ground(p0));
            }
            return;
        }
        if (c.Leader is not { } l || c.State != CivilianState.Conscious) return;
        // arrivé à une ambulance avec son guide : il est mis à l'abri
        foreach (var amb in Units.Where(v => v.IsAmbulance && !v.Destroyed))
            if (Vector3.Distance(l.Position, amb.Position) < VehicleReach * 1.5f && Vector3.Distance(c.Position, amb.Position) < VehicleReach * 1.5f)
            {
                Save(c);
                Log.Add($"{_s.Time,6:0} s : {c.Name} mis à l'abri dans l'ambulance ({Rescued} sauvé(s))");
                Sounds.Add(("VOCAL_CIVILIAN_SAVED", null));
                return;
            }
        FollowLeader(c, l, dt);
    }

    /// <summary>
    /// Le civil suit son guide par le chemin de la grille (comme les pompiers) : il recalcule son chemin vers le guide
    /// quand celui-ci s'est déplacé, marche à sa vitesse (au plus celle d'un pompier qui court), et s'arrête un peu derrière lui.
    /// </summary>
    private void FollowLeader(Civilian c, Unit l, float dt)
    {
        var me = new Vector2(c.Position.X, c.Position.Y); var to = new Vector2(l.Position.X, l.Position.Y);
        float d = Vector2.Distance(me, to);
        if (d < FollowDistance) { c.FollowPath.Clear(); return; }
        // nouveau chemin : guide parti loin du point visé, chemin fini, ou de temps en temps
        c.Replan -= dt;
        if (c.FollowPath.Count == 0 || Vector2.Distance(c.FollowGoal, to) > 150 || c.Replan <= 0)
        {
            c.Replan = 0.6f; c.FollowGoal = to; c.FollowPath.Clear();
            var path = FindPath(me, to, out _);
            foreach (var p in path) c.FollowPath.Enqueue(p);
            if (c.FollowPath.Count == 0) c.FollowPath.Enqueue(to);
        }
        float speed = MathF.Min(MathF.Max(280f, l.Speed * 0.97f), 470f);
        if (d > 600) speed *= 1.1f;   // il rattrape son retard
        float st = speed * dt;
        while (st > 0 && c.FollowPath.Count > 0)
        {
            var t = c.FollowPath.Peek();
            // inutile d'aller jusqu'au guide : il s'arrête à FollowDistance de lui
            if (c.FollowPath.Count == 1 && Vector2.Distance(t, to) < 1) { var away = me - to; t = to + (away.LengthSquared() > 1 ? Vector2.Normalize(away) : Vector2.UnitX) * (FollowDistance - 20); }
            var dv = t - me; float len = dv.Length();
            if (len > 1e-3f) c.Heading = MathF.Atan2(dv.Y, dv.X);
            if (len <= st) { me = t; c.FollowPath.Dequeue(); st -= len; } else { me += dv / len * st; st = 0; }
        }
        c.Position = new Vector3(me, Ground(me));
        if (d > 3500) { var back = to - Vector2.Normalize(to - me) * 150; c.Position = new Vector3(back, Ground(back)); c.FollowPath.Clear(); }   // perdu : il rejoint son guide
    }

    public const float FollowDistance = 140f;

    /// <summary>Essais : le point est-il atteignable à pied depuis cette position (chemin complet sur la grille) ?</summary>
    public string AsciiMap(int x0, int y0, int x1, int y1)
    {
        if (_s.Grid is not { } g) return "";
        var sb = new System.Text.StringBuilder();
        for (int y = y1 / 100; y >= y0 / 100; y--)
        {
            for (int x = x0 / 100; x <= x1 / 100; x++)
            {
                if (!g.Inside(x, y)) { sb.Append(' '); continue; }
                int i = g.Index(x, y);
                sb.Append(g.ZoneA[i] == 0 ? '#' : _blocked.Length > i && _blocked[i] ? (BlockedBy.TryGetValue(i, out var by) && by == "mur" ? 'm' : 'x') : IsClosedDoor(i) ? 'D' : Openings.Any(d => d.Cells.Contains(i)) ? 'd' : '.');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public bool CanReach(Vector2 from, Vector2 to) { var p = FindPath(from, to, out bool partial); return !partial && (p.Count > 0 || Vector2.Distance(from, to) < 100); }
}
