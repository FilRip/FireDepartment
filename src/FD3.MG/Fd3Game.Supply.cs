using FD3.Game;
using SN = System.Numerics;

namespace FD3.MG;

// liaison d'eau : un camion à lance garé à portée d'une bouche d'incendie (ResupplyRadius, 20 m) est relié à elle par une
// ligne de petites flèches vertes posées sur le sol, de la bouche vers le camion, qui défilent doucement
public sealed partial class Fd3Game
{
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
