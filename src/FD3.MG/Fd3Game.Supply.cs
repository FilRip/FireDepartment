using FD3.Game;
using SN = System.Numerics;

namespace FD3.MG;

// liaison d'eau : un camion à lance garé à portée d'une bouche d'incendie (ResupplyRadius, 20 m) est relié à elle par une
// ligne de petites flèches vertes posées sur le sol, de la bouche vers le camion, qui défilent doucement
public sealed partial class Fd3Game
{
    // débogage (touche D) : portée où chaque pompier repère un feu (vert), portée de propagation de chaque feu (orange)
    private bool _debugAi = Environment.GetEnvironmentVariable("FD3_DEBUG_IA") != null;
    private void RenderDebugCircles()
    {
        if (!_debugAi || _map || _session == null) return;
        float px = MathF.Max(1, _scale);
        void Circle(SN.Vector3 c, float r, SN.Vector4 col)
        {
            SN.Vector2? prev = null; const int N = 48;
            for (int i = 0; i <= N; i++)
            {
                float a = i * MathF.Tau / N;
                var q = Project(c + new SN.Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, 10));
                if (q is { } cur && prev is { } p0) _ui.Line(p0.X, p0.Y, cur.X, cur.Y, col, 1.5f * px);
                prev = q;
            }
        }
        // grille de marche autour du point visé : croix rouge = case interdite à un pompier ordinaire
        if (_session.Grid is { } g)
        {
            var (tx, ty) = g.CellOf(_target);
            for (int cy = ty - 25; cy <= ty + 25; cy++)
                for (int cx = tx - 25; cx <= tx + 25; cx++)
                {
                    if (!g.Inside(cx, cy) || _session.Units.WalkableCell(cx, cy)) continue;
                    float x = (cx + 0.5f) * FD3.Formats.Grid.CellSize, y = (cy + 0.5f) * FD3.Formats.Grid.CellSize, z = _session.Units.GroundZ(new(x, y)) + 12;
                    if (Project(new(x - 35, y - 35, z)) is { } a && Project(new(x + 35, y + 35, z)) is { } b) _ui.Line(a.X, a.Y, b.X, b.Y, new SN.Vector4(1f, 0.15f, 0.1f, 0.6f), 1.5f * px);
                    if (Project(new(x - 35, y + 35, z)) is { } c && Project(new(x + 35, y - 35, z)) is { } d) _ui.Line(c.X, c.Y, d.X, d.Y, new SN.Vector4(1f, 0.15f, 0.1f, 0.6f), 1.5f * px);
                }
        }
        foreach (var h in _session.Fire.HotPoints.Where(h => h.IsBurning))
            Circle(h.Position, h.Class.LightingRange, new SN.Vector4(1f, 0.55f, 0.1f, 0.7f));
        foreach (var u in _session.Units.Active.Where(u => !u.IsVehicle && u.Nozzle != null && !u.Unconscious))
        {
            float reach = MathF.Max(150, u.Nozzle!.RangeMax) + UnitManager.ExtraStandoff;
            Circle(u.Position, reach * MathF.Max(1, UnitManager.AutoAttackRange), new SN.Vector4(0.3f, 1f, 0.4f, 0.8f));   // il y va de lui-même
            Circle(u.Position, reach, new SN.Vector4(0.3f, 1f, 0.4f, 0.35f));                                              // il arrose d'où il est
        }
    }

    // corde du grappin : de l'accroche en haut du mur jusqu'aux mains du grimpeur
    private void RenderRopes()
    {
        if (_map || _session == null) return;
        float px = MathF.Max(1, _scale);
        foreach (var u in _session.Units.Active.Where(u => u.Climb != ClimbPhase.None && u.Climb != ClimbPhase.Land && !(u.Climb == ClimbPhase.Launch && u.ClimbTimer < 1.2f)))
        {
            var hand = PoseOf(u, u.Position, u.Heading).p + new SN.Vector3(0, 0, u.Climb is ClimbPhase.Stand or ClimbPhase.Step or ClimbPhase.Turn or ClimbPhase.Top ? 60 : 140);
            if (Project(u.ClimbAnchor) is not { } a || Project(hand) is not { } b) continue;
            _ui.Line(a.X, a.Y, b.X, b.Y, new SN.Vector4(0.1f, 0.08f, 0.05f, 0.85f), 3 * px);
            _ui.Line(a.X, a.Y, b.X, b.Y, new SN.Vector4(0.82f, 0.72f, 0.5f, 1f), 1.6f * px);
        }
    }

    private void RenderSupplyLinks(float time)
    {
        if (_map || _session == null) return;
        var green = new SN.Vector4(0.25f, 1f, 0.35f, 0.95f); var shade = new SN.Vector4(0, 0.15f, 0, 0.6f);
        float px = MathF.Max(1, _scale);
        foreach (var v in _session.Units.Active.Where(u => u.IsVehicle && u.Nozzle is { InfiniteReserve: false } && !u.Moving))
        {
            var vp = new SN.Vector2(v.Position.X, v.Position.Y);
            foreach (var sp in _session.Units.Supplies)
            {
                if (sp.Agent != v.Nozzle!.Agent) continue;
                var hp = new SN.Vector2(sp.Position.X, sp.Position.Y);
                float d = SN.Vector2.Distance(vp, hp);
                if (d > sp.Radius || d < 50) continue;
                var dir = (vp - hp) / d;
                // hauteur du sol attendue le long de la ligne : de celle de la bouche à celle du camion
                float z0 = _session.Units.GroundZ(hp), z1 = _session.Units.GroundZ(vp);
                var grid = _session.Grid;
                const float Step = 110f, Margin = 60f;
                float off = (time * 120f) % Step;
                for (float t = Margin + off; t < d - 200; t += Step)
                {
                    var c = hp + dir * t;
                    float zg = _session.Units.GroundZ(c);
                    // pas de flèche sur un toit ou dans un bâtiment : seulement au niveau du sol, hors des cases fermées
                    if (MathF.Abs(zg - (z0 + (z1 - z0) * t / d)) > 80) continue;
                    if (grid != null) { var (gx, gy) = grid.CellOf(new SN.Vector3(c, 0)); if (grid.Inside(gx, gy) && _session.Units.IsBlocked(grid.Index(gx, gy))) continue; }
                    float z = zg + 15;
                    // chevron : pointe vers le camion
                    var tip = c + dir * 30; var side = new SN.Vector2(-dir.Y, dir.X) * 22; var back = c - dir * 10;
                    if (Project(new(tip, z)) is not { } a || Project(new(back + side, z)) is not { } b || Project(new(back - side, z)) is not { } e) continue;
                    _ui.Line(b.X, b.Y, a.X, a.Y, shade, 4 * px); _ui.Line(e.X, e.Y, a.X, a.Y, shade, 4 * px);
                    _ui.Line(b.X, b.Y, a.X, a.Y, green, 2.2f * px); _ui.Line(e.X, e.Y, a.X, a.Y, green, 2.2f * px);
                }
            }
        }
    }
}
