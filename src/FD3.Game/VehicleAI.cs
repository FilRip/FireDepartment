using System.Numerics;
using FD3.Fire;

namespace FD3.Game;

// ===================================================================================================================
// IA et conduite des véhicules :
//  - véhicule à lance inoccupé : il arrose de lui-même le feu qu'il voit devant lui, à portée (VehicleAutoAttack) ;
//  - attaque d'un feu : il roule jusqu'à portée (VehicleAttack), puis manœuvre pour l'avoir dans l'angle de sa lance
//    (VehicleAim : recule en braquant, puis avance en braquant) ;
//  - conduite : pas de demi-tour sur place, il tourne en avançant et recule d'abord s'il doit repartir dans l'autre sens.
// L'IA des pompiers est dans FiremanAI.cs.
// ===================================================================================================================
public sealed partial class UnitManager
{
    public const float CannonAngle = 0.9f;          // [UNIT] XSprinklerAngleMax : débattement de la lance d'un véhicule

    /// <summary>Véhicule à lance inoccupé : il arrose de lui-même un feu visible, à portée et dans l'angle de sa lance
    /// (pas pendant une cinématique, ni avant que son équipage ait débarqué).</summary>
    private void VehicleAutoAttack(Unit u)
    {
        if (!IdleWithWater(u)) return;
        if (_s.Host?.Cinematic == true || u.Crew.Count > 0) return;
        if ((u.Id + _autoTick) % 3 != 0) return;
        if (FindVisibleFire(u, u.Nozzle!.RangeMax, CannonAngle) is { } best) { u.GoToVehicle = null; u.Target = best; u.Status = $"arrose {best.Name}"; }
    }

    /// <summary>Feu hors de l'angle de la lance : manœuvre (recule en braquant, puis avance en braquant de l'autre côté ;
    /// le cap tourne dans les deux cas). true tant que le véhicule manœuvre.</summary>
    private bool VehicleAim(Unit u, HotPoint h, float diff, float dt)
    {
        if (MathF.Abs(diff) <= CannonAngle * 0.85f) { u.ManeuverLeft = 0; return false; }
        float sp = MathF.Max(120, u.Speed * 0.35f) * dt;
        if (u.ManeuverLeft <= 0) { u.ManeuverLeft = 450; u.ManeuverDir = u.ManeuverDir == -1 ? 1 : -1; }
        bool moved = DriveArc(u, sp, u.ManeuverDir, MathF.Sign(diff));
        if (!moved) { u.ManeuverDir = -u.ManeuverDir; u.ManeuverLeft = 450; moved = DriveArc(u, sp, u.ManeuverDir, MathF.Sign(diff)); }
        if (!moved) u.Heading += MathF.Sign(diff) * 0.15f * dt;   // coincé de partout : tourne très lentement
        u.ManeuverLeft -= sp;
        u.Status = $"manœuvre pour viser {h.Name}";
        return true;
    }

    /// <summary>Véhicule à lance : s'il est trop loin, il roule jusqu'à portée (aux 2/3), l'avant tourné vers le feu.</summary>
    private void VehicleAttack(Unit v, HotPoint h)
    {
        var me = new Vector2(v.Position.X, v.Position.Y); var f = new Vector2(h.Position.X, h.Position.Y);
        float d = Vector2.Distance(me, f), range = v.Nozzle!.RangeMax;
        if (d <= range * 0.95f && d >= v.Nozzle.RangeMin)
        {
            v.Path.Clear(); v.Target = h; v.Status = $"arrose {h.Name}"; return;
        }
        var dir = d > 1 ? Vector2.Normalize(me - f) : Vector2.UnitX;
        float want = Math.Clamp(range * 0.65f, v.Nozzle.RangeMin + 200, range * 0.9f);
        if (!Drive(v, f + dir * want)) { v.Target = null; return; }
        v.Target = h; v.Status = $"va arroser {h.Name}";
    }

    // ---- conduite des véhicules : pas de demi-tour sur place ; ils tournent en avançant (rayon de braquage), et quand il
    // faut repartir dans l'autre sens, ils reculent d'abord en braquant puis repartent en avant (manœuvre en deux temps)
    public const float TurnRadius = 800f;

    private bool VehicleFits(Unit v, Vector2 p, float heading)
    {
        if (_s.Grid is not { } g) return true;
        var fwd = new Vector2(MathF.Cos(heading), MathF.Sin(heading));
        int saved = _selfId; bool savedPv = _pathingVehicle; _selfId = v.Id; _pathingVehicle = false;
        bool ok = true;
        foreach (float k in new[] { -280f, 0f, 280f })
        {
            var q = p + fwd * k; var (cx, cy) = g.CellOf(new Vector3(q, 0));
            if (!FreeCell(cx, cy)) { ok = false; break; }
        }
        _selfId = saved; _pathingVehicle = savedPv;
        return ok;
    }

    /// <summary>Un pas de manœuvre : avance (dir 1) ou recule (dir -1) de s en tournant vers « turn » (signe) ; false si bloqué.</summary>
    private bool DriveArc(Unit v, float s, int dir, float turn)
    {
        var p = new Vector2(v.Position.X, v.Position.Y);
        float h = v.Heading + turn * s / TurnRadius;
        var np = p + new Vector2(MathF.Cos(h), MathF.Sin(h)) * s * dir;
        if (!VehicleFits(v, np, h)) return false;
        v.Heading = h; v.Position = new Vector3(np, Ground(np));
        return true;
    }

    private void VehicleMove(Unit v, float dt)
    {
        float step = v.Speed * dt;
        int guard = 0;
        while (step > 0.5f && v.Path.Count > 0 && guard++ < 8)
        {
            var p = new Vector2(v.Position.X, v.Position.Y); var t = v.Path.Peek();
            var d = t - p; float len = d.Length();
            if (len < 40) { v.Path.Dequeue(); if (v.Reversing < 0) v.Reversing = 0; continue; }
            float diff = MathF.IEEERemainder(MathF.Atan2(d.Y, d.X) - v.Heading, 2 * MathF.PI);
            // cible derrière : marche arrière en braquant (7 m au plus), jusqu'à pouvoir repartir en avant
            // (Reversing < 0 : marche arrière finie ou impossible, on repart en avant)
            if (v.Reversing > 0 || (v.Reversing == 0 && MathF.Abs(diff) > 2.0f && len > 250))
            {
                if (v.Reversing <= 0) v.Reversing = 700;
                float s = MathF.Min(step, v.Speed * 0.5f * dt);
                if (MathF.Abs(diff) < 1.0f) v.Reversing = -1;
                else if (!DriveArc(v, s, -1, MathF.Sign(diff))) v.Reversing = -1;
                else { v.Reversing -= s; if (v.Reversing <= 0) v.Reversing = -1; step -= s; }
                continue;
            }
            if (v.Reversing < 0 && MathF.Abs(diff) < 1.0f) v.Reversing = 0;
            // en avant : le cap tourne au plus du rayon de braquage ; on suit le chemin (les cases sont sûres)
            float s2 = MathF.Min(step, len);
            float turn = Math.Clamp(diff, -s2 / TurnRadius * 1.6f, s2 / TurnRadius * 1.6f);
            if (MathF.Abs(diff) > 0.6f)
            {
                // virage serré : ralentit et tourne sur un arc, sans quitter les cases libres
                float s3 = s2 * 0.6f;
                if (DriveArc(v, s3, 1, MathF.Sign(diff))) { step -= s2; continue; }
            }
            v.Heading += turn;
            p += d / len * s2;
            v.Position = new Vector3(p, Ground(p));
            if (s2 >= len) v.Path.Dequeue();
            step -= s2;
        }
        if (v.Path.Count == 0) v.Reversing = 0;
        if (v.Path.Count == 0 && v.Status.StartsWith("en route")) v.Status = "garé";
    }

}
