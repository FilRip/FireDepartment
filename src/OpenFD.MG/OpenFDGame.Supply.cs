using OpenFD.Fire;
using OpenFD.Game;
using SN = System.Numerics;

namespace OpenFD.MG;

// liaison d'eau : un camion à lance garé à portée d'une bouche d'incendie (ResupplyRadius, 20 m) est relié à elle par une
// ligne de petites flèches vertes posées sur le sol, de la bouche vers le camion, qui défilent doucement ;
// un pompier « canon » est relié de même à son camion, par des flèches bleues
public sealed partial class OpenFDGame
{
    // débogage (touche D) : portée où chaque pompier repère un feu (vert), portée de propagation de chaque feu (orange)
    private bool _debugAi = Environment.GetEnvironmentVariable("FD3_DEBUG_IA") != null;
    private void RenderDebugCircles()
    {
        if (!_debugAi || _map || _session == null)
            return;
        float px = MathF.Max(1, _scale);
        void Circle(SN.Vector3 c, float r, SN.Vector4 col)
        {
            SN.Vector2? prev = null; const int N = 48;
            for (int i = 0; i <= N; i++)
            {
                float a = i * MathF.Tau / N;
                SN.Vector2? q = Project(c + new SN.Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, 10));
                if (q is { } cur && prev is { } p0)
                    _ui.Line(p0.X, p0.Y, cur.X, cur.Y, col, 1.5f * px);
                prev = q;
            }
        }
        // grille de marche autour du point visé : croix rouge = case interdite à un pompier ordinaire
        if (_session.Grid is { } g)
        {
            (int tx, int ty) = g.CellOf(_target);
            for (int cy = ty - 25; cy <= ty + 25; cy++)
                for (int cx = tx - 25; cx <= tx + 25; cx++)
                {
                    if (!g.Inside(cx, cy) || _session.Units.WalkableCell(cx, cy))
                        continue;
                    float x = (cx + 0.5f) * OpenFD.Formats.Grid.CellSize, y = (cy + 0.5f) * OpenFD.Formats.Grid.CellSize, z = _session.Units.GroundZ(new(x, y)) + 12;
                    if (Project(new(x - 35, y - 35, z)) is { } a && Project(new(x + 35, y + 35, z)) is { } b)
                        _ui.Line(a.X, a.Y, b.X, b.Y, new SN.Vector4(1f, 0.15f, 0.1f, 0.6f), 1.5f * px);
                    if (Project(new(x - 35, y + 35, z)) is { } c && Project(new(x + 35, y - 35, z)) is { } d)
                        _ui.Line(c.X, c.Y, d.X, d.Y, new SN.Vector4(1f, 0.15f, 0.1f, 0.6f), 1.5f * px);
                }
        }
        foreach (Fire.HotPoint h in _session.Fire.HotPoints.Where(h => h.IsBurning))
            Circle(h.Position, h.Class.LightingRange, new SN.Vector4(1f, 0.55f, 0.1f, 0.7f));
        foreach (Unit u in _session.Units.Active.Where(u => !u.IsVehicle && u.Nozzle != null && !u.Unconscious))
        {
            float reach = MathF.Max(150, u.Nozzle!.RangeMax) + UnitManager.ExtraStandoff;
            Circle(u.Position, reach * MathF.Max(1, UnitManager.AutoAttackRange), new SN.Vector4(0.3f, 1f, 0.4f, 0.8f));   // il y va de lui-même
            Circle(u.Position, reach, new SN.Vector4(0.3f, 1f, 0.4f, 0.35f));                                              // il arrose d'où il est
        }
    }

    // corde du grappin : de l'accroche en haut du mur jusqu'aux mains du grimpeur
    private void RenderRopes()
    {
        if (_map || _session == null)
            return;
        float px = MathF.Max(1, _scale);
        foreach (Unit u in _session.Units.Active.Where(u => u.Climb != ClimbPhase.None && u.Climb != ClimbPhase.Land && !(u.Climb == ClimbPhase.Launch && u.ClimbTimer < 1.2f)))
        {
            SN.Vector3 hand = PoseOf(u, u.Position, u.Heading).p + new SN.Vector3(0, 0, u.Climb is ClimbPhase.Stand or ClimbPhase.Step or ClimbPhase.Turn or ClimbPhase.Top ? 60 : 140);
            if (Project(u.ClimbAnchor) is not { } a || Project(hand) is not { } b)
                continue;
            _ui.Line(a.X, a.Y, b.X, b.Y, new SN.Vector4(0.1f, 0.08f, 0.05f, 0.85f), 3 * px);
            _ui.Line(a.X, a.Y, b.X, b.Y, new SN.Vector4(0.82f, 0.72f, 0.5f, 1f), 1.6f * px);
        }
    }

    private void RenderSupplyLinks(float time)
    {
        if (_map || _session == null)
            return;
        SN.Vector4 green = new SN.Vector4(0.25f, 1f, 0.35f, 0.95f); SN.Vector4 greenShade = new SN.Vector4(0, 0.15f, 0, 0.6f);
        foreach (Unit v in _session.Units.Active.Where(u => u.IsVehicle && u.Nozzle is { InfiniteReserve: false } && !u.Moving))
        {
            SN.Vector2 vp = new SN.Vector2(v.Position.X, v.Position.Y);
            foreach (SupplyPoint sp in _session.Units.Supplies)
            {
                if (sp.Agent != v.Nozzle!.Agent)
                    continue;
                SN.Vector2 hp = new SN.Vector2(sp.Position.X, sp.Position.Y);
                float d = SN.Vector2.Distance(vp, hp);
                if (d > sp.Radius || d < 50)
                    continue;
                Chevrons(hp, vp, 200, green, greenShade, time);
            }
        }
        // pompier « canon » : flèches bleues du camion qui l'alimente jusqu'à lui
        SN.Vector4 blue = new SN.Vector4(0.3f, 0.6f, 1f, 0.95f); SN.Vector4 blueShade = new SN.Vector4(0, 0.05f, 0.2f, 0.6f);
        foreach (Unit u in _session.Units.Active.Where(u => u.IsCanon && u.FeedTruck != null))
        {
            SN.Vector2 tp = new SN.Vector2(u.FeedTruck!.Position.X, u.FeedTruck.Position.Y), up = new SN.Vector2(u.Position.X, u.Position.Y);
            if (SN.Vector2.Distance(tp, up) > 150)
                Chevrons(tp, up, 60, blue, blueShade, time);
        }
    }

    /// <summary>Ligne de petits chevrons posés sur le sol, de <paramref name="from"/> vers <paramref name="to"/>, qui défilent doucement.</summary>
    private void Chevrons(SN.Vector2 from, SN.Vector2 to, float endMargin, SN.Vector4 color, SN.Vector4 shade, float time)
    {
        float px = MathF.Max(1, _scale);
        float d = SN.Vector2.Distance(from, to);
        SN.Vector2 dir = (to - from) / d;
        // hauteur du sol attendue le long de la ligne : de celle du départ à celle de l'arrivée
        float z0 = _session!.Units.GroundZ(from), z1 = _session.Units.GroundZ(to);
        Formats.Grid? grid = _session.Grid;
        const float Step = 110f, Margin = 60f;
        float off = (time * 120f) % Step;
        for (float t = Margin + off; t < d - endMargin; t += Step)
        {
            SN.Vector2 c = from + dir * t;
            float zg = _session.Units.GroundZ(c);
            // pas de flèche sur un toit ou dans un bâtiment : seulement au niveau du sol, hors des cases fermées
            if (MathF.Abs(zg - (z0 + (z1 - z0) * t / d)) > 80)
                continue;
            if (grid != null)
            {
                (int gx, int gy) = grid.CellOf(new SN.Vector3(c, 0));
                if (grid.Inside(gx, gy) && _session.Units.IsBlocked(grid.Index(gx, gy)))
                    continue;
            }
            float z = zg + 15;
            // chevron : pointe vers l'arrivée
            SN.Vector2 tip = c + dir * 30; SN.Vector2 side = new SN.Vector2(-dir.Y, dir.X) * 22; SN.Vector2 back = c - dir * 10;
            if (Project(new(tip, z)) is not { } a || Project(new(back + side, z)) is not { } b || Project(new(back - side, z)) is not { } e)
                continue;
            _ui.Line(b.X, b.Y, a.X, a.Y, shade, 4 * px); _ui.Line(e.X, e.Y, a.X, a.Y, shade, 4 * px);
            _ui.Line(b.X, b.Y, a.X, a.Y, color, 2.2f * px); _ui.Line(e.X, e.Y, a.X, a.Y, color, 2.2f * px);
        }
    }

    /// <summary>Feu sous la souris : un rond rouge plein, de 16 pixels, au pied des flammes.</summary>
    private void RenderFireHover()
    {
        if (_map || _session == null || _cineOn)
            return;
        Microsoft.Xna.Framework.Input.MouseState ms = Microsoft.Xna.Framework.Input.Mouse.GetState();
        if ((ms.X < 0 || ms.Y < 0 || ms.X >= _winW || ms.Y >= _winH) && Environment.GetEnvironmentVariable("FD3_SURVOL_FEU") == null)
            return;
        // essai (captures) : FD3_SURVOL_FEU → le feu le plus proche du centre de la vue
        HotPoint? hov = Environment.GetEnvironmentVariable("FD3_SURVOL_FEU") != null
            ? _session.Fire.HotPoints.Where(x => x.IsBurning).OrderBy(x => SN.Vector2.Distance(new(x.Position.X, x.Position.Y), new(_target.X, _target.Y))).FirstOrDefault()
            : FireAt(ms.X, ms.Y);
        if (hov is not { } h)
            return;
        // posé à plat sur le sol, vu en perspective comme une tache du décor ; taille réglée pour faire environ
        // 16 pixels de large à l'écran, quel que soit le zoom
        SN.Vector3 c = h.Position + new SN.Vector3(0, 0, 4);
        if (Project(c) is not { } p || Project(c + new SN.Vector3(100, 0, 0)) is not { } px || Project(c + new SN.Vector3(0, 100, 0)) is not { } py)
            return;
        SN.Vector2 ax = (px - p) / 100, ay = (py - p) / 100;
        float perUnit = MathF.Max(ax.Length(), ay.Length());
        if (perUnit < 1e-4f)
            return;
        float world = 8 * MathF.Max(1, _scale) / perUnit;   // rayon au sol, en unités du jeu
        _ui.DiscOnPlane(p.X, p.Y, ax * world * 1.15f, ay * world * 1.15f, new SN.Vector4(0, 0, 0, 0.45f));   // liseré sombre
        _ui.DiscOnPlane(p.X, p.Y, ax * world, ay * world, new SN.Vector4(0.95f, 0.1f, 0.08f, 0.9f));
    }

    /// <summary>
    /// Feux électriques sous tension : des arcs bleu-gris apparaissent au hasard autour du foyer, chacun une demi-seconde,
    /// sans arrêt jusqu'à ce que le courant soit coupé.
    /// </summary>
    private void RenderElectricArcs()
    {
        if (_map || _session == null)
            return;
        float px = MathF.Max(1, _scale);
        double time = _session.Time + (_paused ? 0 : Math.Clamp(_owed, 0, 1) * Session.Dt);
        foreach (HotPoint h in _session.Fire.HotPoints.Where(h => h.Electrified))
        {
            for (int k = 0; k < 4; k++)
            {
                // chaque « emplacement » d'arc tire un nouvel arc toutes les 0,5 s, décalé des autres
                double phase = time / 0.5 + k * 0.37 + (h.Id % 7) * 0.13;
                long slot = (long)Math.Floor(phase);
                System.Random rnd = new System.Random(unchecked((int)(h.Id * 7919 + k * 104729 + slot * 31)));
                if (rnd.NextDouble() < 0.35)
                    continue;   // pas d'arc cette fois-ci : apparitions irrégulières
                float a0 = (float)(rnd.NextDouble() * MathF.Tau), a1 = a0 + (float)(rnd.NextDouble() - 0.5) * 2.4f;
                float r0 = 40 + (float)rnd.NextDouble() * 140, r1 = 60 + (float)rnd.NextDouble() * 160;
                SN.Vector3 from = h.Position + new SN.Vector3(MathF.Cos(a0) * r0, MathF.Sin(a0) * r0, 10 + (float)rnd.NextDouble() * 220);
                SN.Vector3 to = h.Position + new SN.Vector3(MathF.Cos(a1) * r1, MathF.Sin(a1) * r1, 10 + (float)rnd.NextDouble() * 220);
                // ligne brisée : 7 segments, déviés au hasard (moins aux extrémités)
                const int N = 7;
                SN.Vector2? prev = null;
                float grey = (float)rnd.NextDouble();
                SN.Vector4 core = SN.Vector4.Lerp(new SN.Vector4(0.55f, 0.75f, 1f, 1f), new SN.Vector4(0.82f, 0.85f, 0.9f, 1f), grey);
                SN.Vector4 glow = new SN.Vector4(0.3f, 0.5f, 1f, 0.35f);
                for (int i = 0; i <= N; i++)
                {
                    float t = (float)i / N, j = MathF.Sin(t * MathF.PI) * 45;
                    SN.Vector3 q = SN.Vector3.Lerp(from, to, t) + new SN.Vector3((float)(rnd.NextDouble() - 0.5) * j * 2, (float)(rnd.NextDouble() - 0.5) * j * 2, (float)(rnd.NextDouble() - 0.5) * j * 2);
                    SN.Vector2? cur = Project(q);
                    if (cur is { } c && prev is { } b)
                    {
                        _ui.Line(b.X, b.Y, c.X, c.Y, glow, 5 * px);
                        _ui.Line(b.X, b.Y, c.X, c.Y, core, 1.6f * px);
                    }
                    prev = cur;
                }
            }
        }
    }
}
