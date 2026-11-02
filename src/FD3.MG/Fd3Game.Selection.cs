using FD3.Fire;
using FD3.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SN = System.Numerics;

namespace FD3.MG;

// interface autour des unités :
//  - panneau des unités choisies (bas de l'écran, après la mini-carte) : image de l'unité (Data\Overlay\Textures, nom donné
//    par [UNIT] Icon de sa classe et Data\Overlay\GameGraphicDB*.Ini), sa barre de vie (endurance pour un pompier) et sa
//    barre d'eau ;
//  - barre de vie des feux proches des pompiers ou attaqués ;
//  - repère jaune (Focus_Instant.tga) à l'endroit d'un ordre, et voix « VOCAL_ACKNOWLEDGE » quand l'ordre est accepté.
public sealed partial class Fd3Game
{
    private readonly Dictionary<string, Texture2D?> _overlayTex = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? _widgetFiles;
    private readonly List<(SN.Vector3 p, float t0)> _clickMarks = new();
    private float _uiClock;

    // ---- textures de l'interface du jeu ----
    private Texture2D? OverlayTexture(string file, bool whiteMask = false, bool cropFrame = false)
    {
        var key = file + (whiteMask ? "#w" : "") + (cropFrame ? "#c" : "");
        if (_overlayTex.TryGetValue(key, out var t)) return t;
        t = null;
        try
        {
            var name = Path.GetFileName(file.Replace('\\', '/'));
            var dir = Path.Combine(_files.DataDir, "Overlay", "Textures");
            var path = Directory.Exists(dir) ? Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)) : null;
            if (path != null)
            {
                Color[]? px = null; int w = 0, h = 0;
                if (path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                {
                    if (Tex.DecodeTga(File.ReadAllBytes(path)) is { } img)
                    {
                        (w, h) = (img.w, img.h); px = new Color[w * h];
                        for (int i = 0; i < px.Length; i++) px[i] = new Color(img.rgba[4 * i], img.rgba[4 * i + 1], img.rgba[4 * i + 2], img.rgba[4 * i + 3]);
                    }
                }
                else
                {
                    using var fs = File.OpenRead(path);
                    var tex = Texture2D.FromStream(GraphicsDevice, fs);
                    w = tex.Width; h = tex.Height; px = new Color[w * h]; tex.GetData(px); tex.Dispose();
                }
                if (px != null && cropFrame)
                {
                    // image dont le dessin tient dans un cadre noir en haut à gauche (WID_CHECK_…) : on ne garde que le cadre
                    bool Dark(Color c) => c.A > 128 && Math.Max(c.R, Math.Max(c.G, c.B)) < 40;
                    int fw = 0, fh = 0;
                    while (fw < w && Dark(px[fw])) fw++;
                    while (fh < h && Dark(px[fh * w])) fh++;
                    if (fw >= 4 && fh >= 4 && (fw < w || fh < h))
                    {
                        var cut = new Color[fw * fh];
                        for (int y = 0; y < fh; y++) Array.Copy(px, y * w, cut, y * fw, fw);
                        px = cut; w = fw; h = fh;
                    }
                }
                if (px != null)
                {
                    // repère : on garde la forme (alpha et luminosité) en blanc, pour le teinter
                    if (whiteMask) for (int i = 0; i < px.Length; i++) { var c = px[i]; byte a = (byte)Math.Min(c.A, Math.Max(c.R, Math.Max(c.G, c.B))); px[i] = new Color((byte)255, (byte)255, (byte)255, a); }
                    t = new Texture2D(GraphicsDevice, w, h); t.SetData(px);
                }
            }
        }
        catch (Exception e) { Console.WriteLine($"Interface : {file} : {e.Message}"); }
        return _overlayTex[key] = t;
    }

    // [WID_PUMPER_GB_BIG] FILENAME = Data/Overlay/Textures/PUMPER_GB_big.png
    private string? WidgetFile(string widget)
    {
        if (_widgetFiles == null)
        {
            _widgetFiles = new(StringComparer.OrdinalIgnoreCase);
            var dir = Path.Combine(_files.DataDir, "Overlay");
            if (Directory.Exists(dir))
                foreach (var ini in Directory.EnumerateFiles(dir, "*.ini").Where(f => Path.GetFileName(f).StartsWith("GameGraphicDB", StringComparison.OrdinalIgnoreCase)))
                {
                    string? sec = null;
                    foreach (var raw in File.ReadAllLines(ini, System.Text.Encoding.Latin1))
                    {
                        var l = raw.Trim();
                        if (l.StartsWith('[') && l.Contains(']')) { sec = l[1..l.IndexOf(']')]; continue; }
                        int eq = l.IndexOf('=');
                        if (sec != null && eq > 0 && l[..eq].Trim().Equals("FILENAME", StringComparison.OrdinalIgnoreCase)) _widgetFiles[sec] = l[(eq + 1)..].Trim().Trim('"');
                    }
                }
        }
        return _widgetFiles.TryGetValue(widget, out var f) ? f : null;
    }

    private Texture2D? UnitIcon(Unit u)
    {
        var icon = u.Icon;
        string? file = null;
        if (!string.IsNullOrEmpty(icon)) file = WidgetFile(icon + "_BIG") ?? WidgetFile(icon + "_SMALL") ?? WidgetFile(icon);
        if (file == null)
        {
            // sans [UNIT] Icon : d'après le nom de la classe (fireman_sp_gb → FIREFIGHTER_GB…)
            var n = u.Name.ToUpperInvariant();
            var nat = new[] { "US", "FR", "GB", "GR", "UA" }.FirstOrDefault(x => n.EndsWith("_" + x)) ?? "US";
            file = u.IsVehicle ? (u.IsAmbulance ? $"AMBU_{nat}_big.png" : $"PUMPER_{nat}_big.png")
                : n.Contains("MEDIC") ? "FIREFIGHTER_MEDIC_big.png" : n.Contains("FRANC") ? "FIREFIGHTER_FRANK_big.png"
                : n.Contains("CANON") ? "FIREFIGHTER_CANON_big.png" : n.Contains("ING") ? "FIREFIGHTER_ING_big.png"
                : n.Contains("DES") ? "FIREFIGHTER_DES_big.png" : $"FIREFIGHTER_{nat}_big.png";
        }
        return OverlayTexture(file) ?? OverlayTexture(file.Replace("_big", "_small", StringComparison.OrdinalIgnoreCase));
    }

    // ---- ordre donné : repère jaune et voix ----
    private void CommandWithFeedback(double x, double y)
    {
        // état avant l'ordre (y compris la fin du chemin : un nouvel ordre en cours de route garde « en route »)
        static SN.Vector2? End(Unit u) => u.Path.Count > 0 ? u.Path.Last() : null;
        var before = _sel.ToDictionary(u => u, u => (u.Status, End(u), u.Target, u.Opening, u.GoTo, u.GoToVehicle, u.UseTarget));
        Command(x, y);
        bool accepted = _sel.Any(u => before.TryGetValue(u, out var b)
            && (b.Status != u.Status || b.Item2 != End(u) || b.Target != u.Target || b.Opening != u.Opening || b.GoTo != u.GoTo || b.GoToVehicle != u.GoToVehicle || b.UseTarget != u.UseTarget)
            && !RefusedStatus(u.Status));
        if (Ground(x, y) is { } g) _clickMarks.Add((new SN.Vector3(g, _session.Units.GroundZ(g) + 8), _uiClock));
        if (accepted) Acknowledge();
    }

    private static bool RefusedStatus(string s) => s.StartsWith("impossible") || s.StartsWith("ne peut") || s.Contains("hors service") || s.Contains("hors d'atteinte") || s.StartsWith("ce véhicule");

    private void Acknowledge() => _audio?.PlayAny(Enumerable.Range(1, 6).Select(i => $"VOCAL_ACKNOWLEDGE{i}"));

    private void RenderClickMarks()
    {
        _clickMarks.RemoveAll(c => _uiClock - c.t0 > 0.9f);
        if (_clickMarks.Count == 0 || _map) return;
        var ring = OverlayTexture("Focus_Instant.tga", whiteMask: true);
        foreach (var (p, t0) in _clickMarks)
        {
            float k = (_uiClock - t0) / 0.9f;
            // anneau posé au sol : on projette deux points pour avoir sa taille et son aplatissement à l'écran
            if (Project(p) is not { } c || Project(p + new SN.Vector3(90, 0, 0)) is not { } ex || Project(p + new SN.Vector3(0, 90, 0)) is not { } ey) continue;
            float rx = MathF.Max(MathF.Abs(ex.X - c.X), MathF.Abs(ey.X - c.X)), ry = MathF.Max(MathF.Abs(ex.Y - c.Y), MathF.Abs(ey.Y - c.Y));
            rx = MathF.Max(rx, 12 * _scale); ry = MathF.Max(ry, 7 * _scale);
            float grow = 1.25f - 0.45f * k, a = 1 - k * k;
            var col = new SN.Vector4(1f, 0.9f, 0.15f, a);
            if (ring != null) _ui.Image(ring, c.X - rx * grow, c.Y - ry * grow, 2 * rx * grow, 2 * ry * grow, col);
            else for (int i = 0; i < 24; i++)
                {
                    float a0 = i * MathF.Tau / 24, a1 = (i + 1) * MathF.Tau / 24;
                    _ui.Line(c.X + MathF.Cos(a0) * rx * grow, c.Y + MathF.Sin(a0) * ry * grow, c.X + MathF.Cos(a1) * rx * grow, c.Y + MathF.Sin(a1) * ry * grow, col, 2.5f * _scale);
                }
        }
    }

    // ---- barre de vie des feux : ceux qu'un pompier attaque, ou près desquels il se trouve ----
    private void RenderFireBars()
    {
        if (_map || _session == null) return;
        var men = _session.Units.Active.Where(u => !u.OnBoard).ToList();
        var shown = new HashSet<HotPoint>(men.Where(u => u.Target != null).Select(u => u.Target!));
        foreach (var h in _session.Fire.HotPoints)
        {
            if (!h.IsBurning || shown.Contains(h)) continue;
            if (men.Any(u => !u.IsVehicle && SN.Vector3.DistanceSquared(u.Position, h.Position) < 900f * 900f)) shown.Add(h);
        }
        foreach (var h in shown.Where(h => h.IsBurning))
        {
            if (Project(h.Position + new SN.Vector3(0, 0, 230)) is not { } p || p.X < 0 || p.Y < 0 || p.X > _fbW || p.Y > _fbH) continue;
            float bw = 54 * _scale, bh = 6 * _scale, k = Math.Clamp(h.Intensity / Math.Max(1, h.Class.IntensityMax), 0, 1);
            _ui.Rect(p.X - bw / 2 - 2 * _scale, p.Y - 2 * _scale, bw + 4 * _scale, bh + 4 * _scale, new SN.Vector4(0, 0, 0, 0.7f));
            _ui.Rect(p.X - bw / 2, p.Y, bw * k, bh, new SN.Vector4(1f, 0.35f + 0.4f * (1 - k), 0.05f, 1));
        }
    }

    // ---- panneau des unités choisies ----
    private const int PanelMax = 12;
    private (float x, float y, float cell, float iconH)? SelectionPanelRect()
    {
        if (_map || _cineOn || _sel.Count == 0 || !MiniVisible) return null;
        var r = MiniRect();
        float cell = 74 * _scale, iconH = 62 * _scale, total = iconH + 26 * _scale;
        float x = r.x + r.w + 72 * _scale;      // après la mini-carte et l'icône « faire descendre »
        return (x, _fbH - 12 * _scale - total, cell, iconH);
    }

    private float SelectionPanelHeight => SelectionPanelRect() is { } p ? p.iconH + 26 * _scale + 16 * _scale : 0;

    private void RenderSelectionPanel()
    {
        if (SelectionPanelRect() is not { } pr) return;
        var units = _sel.Where(u => _session.Units.Units.Contains(u)).OrderBy(u => u.IsVehicle ? 0 : 1).Take(PanelMax).ToList();
        if (units.Count == 0) return;
        float pad = 6 * _scale, w = units.Count * pr.cell + pad, h = pr.iconH + 26 * _scale + pad;
        _ui.Rect(pr.x - pad, pr.y - pad, w + pad, h + pad, new SN.Vector4(0.09f, 0.09f, 0.11f, 0.88f));
        var lifeTex = OverlayTexture("WID_PRO_LIFE.png"); var stamTex = OverlayTexture("WID_PRO_STAMINA.png"); var waterTex = OverlayTexture("WID_PRO_RESERVE.png");
        float x = pr.x;
        foreach (var u in units)
        {
            float iw = pr.cell - pad;
            _ui.Rect(x, pr.y, iw, pr.iconH, new SN.Vector4(0.16f, 0.17f, 0.2f, 1));
            if (UnitIcon(u) is { } ic)
            {
                float s = MathF.Min(iw / ic.Width, pr.iconH / ic.Height);
                var tint = u.Destroyed || u.Unconscious ? new SN.Vector4(0.45f, 0.35f, 0.35f, 1) : SN.Vector4.One;
                _ui.Image(ic, x + (iw - ic.Width * s) / 2, pr.y + (pr.iconH - ic.Height * s) / 2, ic.Width * s, ic.Height * s, tint);
            }
            else _ui.Text(x + 4 * _scale, pr.y + 4 * _scale, UnitLabel(u), new SN.Vector4(0.9f, 0.9f, 0.9f, 1));
            if (u.Destroyed) _ui.Text(x + 4 * _scale, pr.y + pr.iconH - _ui.LineHeight, "X", new SN.Vector4(1f, 0.3f, 0.3f, 1));
            // vie (véhicule) ou endurance (pompier), puis eau
            float k = u.IsVehicle ? u.Life / Math.Max(1, u.LifeMax) : u.Stamina / Math.Max(1, u.StaminaMax);
            Bar(x, pr.y + pr.iconH + 4 * _scale, iw, 8 * _scale, Math.Clamp(k, 0, 1), u.IsVehicle ? lifeTex : stamTex,
                new SN.Vector4(1 - Math.Clamp(k, 0, 1), 0.25f + 0.65f * Math.Clamp(k, 0, 1), 0.2f, 1));
            if (u.Nozzle is { } n && !n.InfiniteReserve)
                Bar(x, pr.y + pr.iconH + 15 * _scale, iw, 7 * _scale, Math.Clamp(n.Reserve / Math.Max(1, n.ReserveMax), 0, 1), waterTex, new SN.Vector4(0.25f, 0.6f, 1f, 1));
            x += pr.cell;
        }
        if (_sel.Count > PanelMax) _ui.Text(x, pr.y + pr.iconH / 2, $"+{_sel.Count - PanelMax}", new SN.Vector4(0.9f, 0.9f, 0.9f, 1));
    }

    private void Bar(float x, float y, float w, float h, float k, Texture2D? tex, SN.Vector4 col)
    {
        _ui.Rect(x - 1, y - 1, w + 2, h + 2, new SN.Vector4(0, 0, 0, 0.85f));
        if (k <= 0) return;
        if (tex != null) _ui.Image(tex, x, y, w * k, h, col * 0.5f + new SN.Vector4(0.5f, 0.5f, 0.5f, 0.5f));
        else _ui.Rect(x, y, w * k, h, col);
    }

    /// <summary>Clic gauche sur une image du panneau : ne garder que cette unité.</summary>
    private bool SelectionPanelPress(double x, double y)
    {
        if (SelectionPanelRect() is not { } pr) return false;
        var (sx, sy) = Fb(x, y);
        var units = _sel.Where(u => _session.Units.Units.Contains(u)).OrderBy(u => u.IsVehicle ? 0 : 1).Take(PanelMax).ToList();
        if (sy < pr.y || sy > pr.y + pr.iconH + 26 * _scale || sx < pr.x || sx > pr.x + units.Count * pr.cell) return false;
        int i = (int)((sx - pr.x) / pr.cell);
        if (i >= 0 && i < units.Count) { var u = units[i]; _sel.Clear(); _sel.Add(u); _target.X = u.Position.X; _target.Y = u.Position.Y; }
        return true;
    }
}
