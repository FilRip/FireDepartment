using System.Numerics;
using FD3.Formats;

namespace FD3.Game;

/// <summary>Phases du passage d'un mur au grappin (animations SingleChar\sc_grappling_hook_…).</summary>
public enum ClimbPhase { None, Launch, Climb, Stand, Step, Turn, Top, Descend, Land }

/// <summary>
/// Passage au grappin : repère « koshka_symbol » (« кошка », le grappin) posé en haut d'un mur. Dans la grille du jeu,
/// les cases du mur sous le repère sont ouvertes (le jeu y met un lien « grappin ») : seul un pompier qui a le grappin
/// ([CAPACITIES_ACTIONS] CanGrapplin, le « scout ») peut y passer, en grimpant d'un côté et en redescendant de l'autre.
/// </summary>
public sealed class GrapplePoint
{
    public required uint Id { get; init; }
    public required Vector3 Top { get; init; }        // haut du mur (repère)
    public required Vector2 Axis { get; init; }       // le long du mur
    public required Vector2 Perp { get; init; }       // en travers du mur
    public readonly HashSet<int> Cells = new();
    public Vector2 SideA, SideB;                      // points au sol de part et d'autre (côté +Perp, côté −Perp)
}

public sealed partial class UnitManager
{
    public List<GrapplePoint> Grapples { get; } = new();
    private bool[] _grappleCell = Array.Empty<bool>();
    private bool _pathingGrapple;

    private void LoadGrapples(Level level)
    {
        if (_s.Grid is not { } g) return;
        _grappleCell = new bool[g.Width * g.Height];
        foreach (var o in level.Objects.Where(o => o.HasTransform && o.ClassPath.EndsWith("koshka_symbol", StringComparison.OrdinalIgnoreCase)))
        {
            var m = o.Matrix;
            var axis = new Vector2(m.M11, m.M12); var perp = new Vector2(m.M21, m.M22);
            if (axis.LengthSquared() < 1e-6f || perp.LengthSquared() < 1e-6f) continue;
            axis = Vector2.Normalize(axis); perp = Vector2.Normalize(perp);
            var gp = new GrapplePoint { Id = o.Id, Top = o.Position, Axis = axis, Perp = perp };
            var p = new Vector2(o.Position.X, o.Position.Y);
            // cases du mur sous le repère : dans la ligne du mur (à 0,7 m près), sur 2 m de part et d'autre du repère
            for (int cy = 0; cy < g.Height; cy++)
                for (int cx = 0; cx < g.Width; cx++)
                {
                    var c = new Vector2((cx + 0.5f) * Grid.CellSize, (cy + 0.5f) * Grid.CellSize) - p;
                    if (MathF.Abs(Vector2.Dot(c, perp)) > 70 || MathF.Abs(Vector2.Dot(c, axis)) > 220) continue;
                    if (FreeCell(cx, cy)) gp.Cells.Add(g.Index(cx, cy));
                }
            Vector2 Side(float s)
            {
                for (float d = 150; d <= 800; d += 50)
                {
                    var q = p + perp * (s * d);
                    var (cx, cy) = g.CellOf(new Vector3(q, 0));
                    // au sol, bien plus bas que le haut du mur (pas sur l'épaisseur du mur ni sur un chemin de ronde)
                    if (g.Inside(cx, cy) && FreeCell(cx, cy) && !gp.Cells.Contains(g.Index(cx, cy)) && Ground(q) < o.Position.Z - 250) return q;
                }
                return p + perp * (s * 300);
            }
            gp.SideA = Side(1); gp.SideB = Side(-1);
            foreach (var i in gp.Cells) _grappleCell[i] = true;
            Grapples.Add(gp);
            Log.Add($"Passage au grappin : {o.Name} ({gp.Cells.Count} case(s) du mur, haut à {o.Position.Z:0})");
        }
    }

    private bool IsGrappleCell(int i) => i < _grappleCell.Length && _grappleCell[i];

    /// <summary>
    /// Pompier à grappin en marche : le prochain point de son chemin passe par le mur d'un passage au grappin →
    /// il s'arrête au pied du mur et commence à grimper. true si la montée commence.
    /// </summary>
    private bool StartClimbIfNeeded(Unit u, Vector2 next)
    {
        if (!u.CanGrapple || _s.Grid is not { } g || Grapples.Count == 0) return false;
        // le trajet vers le prochain point traverse-t-il le mur d'un passage ? (le chemin peut l'enjamber d'un seul trait)
        var from = new Vector2(u.Position.X, u.Position.Y);
        int hit = -1; float len = Vector2.Distance(from, next);
        for (float s = 0; s <= len && hit < 0; s += 40)
        {
            var q = len > 0 ? Vector2.Lerp(from, next, s / len) : next;
            var (qx, qy) = g.CellOf(new Vector3(q, 0));
            if (g.Inside(qx, qy) && IsGrappleCell(g.Index(qx, qy)) && Vector2.Distance(q, from) < 160) hit = g.Index(qx, qy);
        }
        if (hit < 0) return false;
        var gp = Grapples.FirstOrDefault(x => x.Cells.Contains(hit));
        if (gp == null) return false;
        var me = new Vector2(u.Position.X, u.Position.Y); var top = new Vector2(gp.Top.X, gp.Top.Y);
        float side = MathF.Sign(Vector2.Dot(me - top, gp.Perp)); if (side == 0) side = 1;
        // points du chemin dans le mur ou encore de ce côté-ci : passés par la montée
        while (u.Path.Count > 0)
        {
            var q = u.Path.Peek(); var (qx, qy) = g.CellOf(new Vector3(q, 0));
            bool inWall = g.Inside(qx, qy) && IsGrappleCell(g.Index(qx, qy));
            bool sameSide = MathF.Sign(Vector2.Dot(q - top, gp.Perp)) == side && Vector2.Distance(q, top) < 400;
            if (!inWall && !sameSide) break;
            u.Path.Dequeue();
        }
        var land = side > 0 ? gp.SideB : gp.SideA;
        // le long du mur : on monte face à soi, pas forcément pile sous le repère
        float along = Math.Clamp(Vector2.Dot(me - top, gp.Axis), -150, 150);
        var wall = top + gp.Axis * along;
        u.Climb = ClimbPhase.Launch; u.ClimbTimer = 0; u.ClimbGrapple = gp;
        u.ClimbFrom = new Vector3(me, u.Position.Z);
        // haut du mur : un mètre sous le repère ; le grimpeur s'arrête les mains au sommet (l'animation « get_on_top »
        // le fait passer par-dessus : 1,5 m en avant et 1,56 m plus bas), puis il redescend de l'autre côté, contre le mur
        float wallTop = gp.Top.Z - 100;
        u.ClimbWall = new Vector3(wall + gp.Perp * (side * 140), wallTop - 60);   // à 1,4 m du milieu du mur : les pieds ne le traversent pas
        // il redescend contre la face de l'autre côté du mur (le mur peut être épais : chemin de ronde) : 40 cm devant la face
        float face = 60;
        for (; face < 900; face += 10)
        {
            var q = wall - gp.Perp * (side * face); var (fx, fy) = g.CellOf(new Vector3(q, 0));
            if (g.Inside(fx, fy) && FreeCell(fx, fy) && !IsGrappleCell(g.Index(fx, fy)) && Ground(q) < gp.Top.Z - 250) break;
        }
        var down = wall - gp.Perp * (side * (face + 40));
        u.ClimbTo = new Vector3(down, Ground(down));
        u.ClimbAnchor = new Vector3(wall, wallTop + 20);
        u.Heading = MathF.Atan2(-gp.Perp.Y * side, -gp.Perp.X * side);   // face au mur
        u.Status = "lance le grappin";
        return true;
    }

    /// <summary>Ordre du joueur sur la flèche du grappin : le scout va au pied du mur, de son côté, puis passe de l'autre.</summary>
    public bool GrappleOver(Unit u, GrapplePoint gp)
    {
        if (u.IsVehicle || !u.CanGrapple) { u.Status = "pas de grappin"; return false; }
        var me = new Vector2(u.Position.X, u.Position.Y);
        bool fromA = Vector2.DistanceSquared(me, gp.SideA) <= Vector2.DistanceSquared(me, gp.SideB);
        var near = fromA ? gp.SideA : gp.SideB; var far = fromA ? gp.SideB : gp.SideA;
        if (!Order(u, near)) return false;
        u.Path.Enqueue(far);   // en passant de l'autre côté, il grimpe (voir StartClimbIfNeeded)
        u.Status = "va au grappin";
        return true;
    }

    public const float GrappleLaunchSeconds = 2.33f, GrappleStandSeconds = 2.0f, GrappleStepSeconds = 0.55f, GrappleTurnSeconds = 0.5f,
        GrappleTopSeconds = 1.67f, GrappleLandSeconds = 1.83f;
    // déplacements du corps dans les animations du jeu (piste « Bip01 ») : le personnage regarde vers −Y
    private const float StandForward = 125, StandRise = 274 - 92.5f;   // sc_grappling_hook_get_out_top : il se hisse et se relève sur le mur
    private const float OverBack = 150, OverDrop = 156 + 43;            // sc_grappling_hook_get_on_top : à reculons par-dessus le bord, puis pendu

    /// <summary>
    /// Montée, puis en haut : il se hisse et se relève (get_out_top), fait un pas vers l'autre bord, se retourne, passe
    /// à reculons par-dessus le bord (get_on_top) et redescend face au mur de l'autre côté (sans ordre : ça fait partie de l'action).
    /// </summary>
    private void UpdateClimb(Unit u, float dt)
    {
        if (u.Climb == ClimbPhase.None) return;
        if (u.Unconscious || u.Destroyed) { u.Climb = ClimbPhase.None; u.Position = new Vector3(u.ClimbTo.X, u.ClimbTo.Y, u.ClimbTo.Z); return; }
        u.ClimbTimer += dt;
        float speed = MathF.Max(30, u.ClimbSpeed);
        var wall = new Vector2(u.ClimbAnchor.X, u.ClimbAnchor.Y);
        var near = new Vector2(u.ClimbWall.X, u.ClimbWall.Y) - wall; near = near.LengthSquared() > 1 ? Vector2.Normalize(near) : Vector2.UnitX;   // vers le côté de départ
        var cw = new Vector2(u.ClimbWall.X, u.ClimbWall.Y);
        var standAt = new Vector3(cw - near * StandForward, u.ClimbWall.Z + StandRise);   // debout sur le mur
        // pendu de l'autre côté, au-dessus du point d'arrivée ; il passe le bord à reculons depuis 1,5 m avant
        var hangXY = new Vector2(u.ClimbTo.X, u.ClimbTo.Y);
        var turnAt = new Vector3(hangXY + near * OverBack, standAt.Z);                     // quelques pas plus loin, vers l'autre bord
        var hangAt = new Vector3(hangXY, turnAt.Z - OverDrop);
        float stepTime = MathF.Max(GrappleStepSeconds, Vector2.Distance(new(standAt.X, standAt.Y), new(turnAt.X, turnAt.Y)) / 150f);
        float up = MathF.Max(0.5f, (u.ClimbWall.Z - u.ClimbFrom.Z) / speed), down = MathF.Max(0.5f, (hangAt.Z - u.ClimbTo.Z) / speed);
        switch (u.Climb)
        {
            case ClimbPhase.Launch:
                u.Position = u.ClimbFrom;
                if (u.ClimbTimer >= GrappleLaunchSeconds) Next(ClimbPhase.Climb, "grimpe");
                break;
            case ClimbPhase.Climb:
            {
                float k = Math.Clamp(u.ClimbTimer / up, 0, 1);
                var xy = Vector2.Lerp(new(u.ClimbFrom.X, u.ClimbFrom.Y), cw, MathF.Min(1, k * 4));
                u.Position = new Vector3(xy, u.ClimbFrom.Z + (u.ClimbWall.Z - u.ClimbFrom.Z) * k);
                if (k >= 1) Next(ClimbPhase.Stand, "se hisse sur le mur");
                break;
            }
            case ClimbPhase.Stand:
                u.Position = u.ClimbWall;
                if (u.ClimbTimer >= GrappleStandSeconds) { u.Position = standAt; Next(ClimbPhase.Step, "s'approche du bord"); }
                break;
            case ClimbPhase.Step:
                u.Position = Vector3.Lerp(standAt, turnAt, Math.Clamp(u.ClimbTimer / stepTime, 0, 1));
                if (u.ClimbTimer >= stepTime) { u.Heading += MathF.PI; Next(ClimbPhase.Turn, "se retourne"); }   // dos au vide
                break;
            case ClimbPhase.Turn:
                u.Position = turnAt;
                if (u.ClimbTimer >= GrappleTurnSeconds) Next(ClimbPhase.Top, "passe le bord");
                break;
            case ClimbPhase.Top:
                u.Position = turnAt;
                if (u.ClimbTimer >= GrappleTopSeconds) { u.Position = hangAt; Next(ClimbPhase.Descend, "redescend"); }   // face au mur, de l'autre côté
                break;
            case ClimbPhase.Descend:
            {
                float k = Math.Clamp(u.ClimbTimer / down, 0, 1);
                u.Position = new Vector3(hangAt.X, hangAt.Y, hangAt.Z + (u.ClimbTo.Z - hangAt.Z) * k);
                if (k >= 1) Next(ClimbPhase.Land, "touche le sol");
                break;
            }
            case ClimbPhase.Land:
                u.Position = u.ClimbTo;
                if (u.ClimbTimer < GrappleLandSeconds) break;
                u.Climb = ClimbPhase.None; u.ClimbGrapple = null;
                u.Status = u.Path.Count > 0 ? "en route" : "attend les ordres";
                break;
        }

        void Next(ClimbPhase p, string status) { u.Climb = p; u.ClimbTimer = 0; u.Status = status; }
    }
}
