using OpenFD.Fire;
using OpenFD.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SN = System.Numerics;

namespace OpenFD.MG;

// interface autour des unités :
//  - panneau des unités choisies (bas de l'écran, après la mini-carte) : image de l'unité (Data\Overlay\Textures, nom donné
//    par [UNIT] Icon de sa classe et Data\Overlay\GameGraphicDB*.Ini), sa barre de vie (endurance pour un pompier) et sa
//    barre d'eau ;
//  - barre de vie des feux proches des pompiers ou attaqués ;
//  - repère jaune (Focus_Instant.tga) à l'endroit d'un ordre, et voix « VOCAL_ACKNOWLEDGE » quand l'ordre est accepté.
public sealed partial class OpenFDGame
{
    private readonly Dictionary<string, Texture2D?> _overlayTex = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? _widgetFiles;
    private readonly List<(SN.Vector3 p, float t0)> _clickMarks = new();
    private float _uiClock;

    // ---- textures de l'interface du jeu ----
    private Texture2D? OverlayTexture(string file, bool whiteMask = false, bool cropFrame = false, bool grayToWhite = false)
    {
        string key = file + (whiteMask ? "#w" : "") + (cropFrame ? "#c" : "") + (grayToWhite ? "#g" : "");
        if (_overlayTex.TryGetValue(key, out Texture2D? t))
            return t;
        t = null;
        try
        {
            string name = Path.GetFileName(file.Replace('\\', '/'));
            string dir = Path.Combine(_files.DataDir, "Overlay", "Textures");
            string? path = Directory.Exists(dir) ? Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)) : null;
            if (path != null)
            {
                Color[]? px = null;
                int w = 0, h = 0;
                if (path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                {
                    if (Tex.DecodeTga(File.ReadAllBytes(path)) is { } img)
                    {
                        (w, h) = (img.w, img.h);
                        px = new Color[w * h];
                        for (int i = 0; i < px.Length; i++)
                            px[i] = new Color(img.rgba[4 * i], img.rgba[4 * i + 1], img.rgba[4 * i + 2], img.rgba[4 * i + 3]);
                    }
                }
                else
                {
                    using FileStream fs = File.OpenRead(path);
                    Texture2D tex = Texture2D.FromStream(GraphicsDevice, fs);
                    w = tex.Width;
                    h = tex.Height;
                    px = new Color[w * h];
                    tex.GetData(px);
                    tex.Dispose();
                }
                if (px != null && cropFrame)
                {
                    // image dont le dessin tient dans un cadre noir en haut à gauche (WID_CHECK_…) : on ne garde que le cadre
                    bool Dark(Color c) => c.A > 128 && Math.Max(c.R, Math.Max(c.G, c.B)) < 40;
                    int fw = 0, fh = 0;
                    while (fw < w && Dark(px[fw]))
                        fw++;
                    while (fh < h && Dark(px[fh * w]))
                        fh++;
                    if (fw >= 4 && fh >= 4 && (fw < w || fh < h))
                    {
                        Color[] cut = new Color[fw * fh];
                        for (int y = 0; y < fh; y++)
                            Array.Copy(px, y * w, cut, y * fw, fw);
                        px = cut;
                        w = fw;
                        h = fh;
                    }
                }
                if (px != null)
                {
                    // repère : on garde la forme (alpha et luminosité) en blanc, pour le teinter
                    if (whiteMask)
                        for (int i = 0; i < px.Length; i++)
                        {
                            Color c = px[i];
                            byte a = (byte)Math.Min(c.A, Math.Max(c.R, Math.Max(c.G, c.B)));
                            px[i] = new Color((byte)255, (byte)255, (byte)255, a);
                        }
                    // dessin gris (flèches de FD2) : en blanc, le reste de l'image ne change pas
                    if (grayToWhite)
                        for (int i = 0; i < px.Length; i++)
                        {
                            Color c = px[i];
                            int hi = Math.Max(c.R, Math.Max(c.G, c.B));
                            int lo = Math.Min(c.R, Math.Min(c.G, c.B));
                            if (c.A > 0 && hi - lo < 16 && hi > 100)
                                px[i] = new Color((byte)255, (byte)255, (byte)255, c.A);
                        }
                    t = new Texture2D(GraphicsDevice, w, h);
                    t.SetData(px);
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
            string dir = Path.Combine(_files.DataDir, "Overlay");
            if (Directory.Exists(dir))
                foreach (string ini in Directory.EnumerateFiles(dir, "*.ini").Where(f => Path.GetFileName(f).StartsWith("GameGraphicDB", StringComparison.OrdinalIgnoreCase)))
                {
                    string? sec = null;
                    foreach (string raw in File.ReadAllLines(ini, System.Text.Encoding.Latin1))
                    {
                        string l = raw.Trim();
                        if (l.StartsWith('[') && l.Contains(']'))
                        {
                            sec = l[1..l.IndexOf(']')];
                            continue;
                        }
                        int eq = l.IndexOf('=');
                        if (sec != null && eq > 0 && l[..eq].Trim().Equals("FILENAME", StringComparison.OrdinalIgnoreCase))
                            _widgetFiles[sec] = l[(eq + 1)..].Trim().Trim('"');
                    }
                }
        }
        return _widgetFiles.TryGetValue(widget, out string? f) ? f : null;
    }

    private Texture2D? UnitIcon(Unit u) => IconFor(u.Icon, u.Name, u.IsVehicle, u.IsAmbulance);

    /// <summary>Image d'une unité : [UNIT] Icon, sinon d'après le nom de sa classe.</summary>
    private Texture2D? IconFor(string? icon, string name, bool isVehicle, bool isAmbulance)
    {
        string? file = null;
        if (!string.IsNullOrEmpty(icon))
            file = WidgetFile(icon + "_BIG") ?? WidgetFile(icon + "_SMALL") ?? WidgetFile(icon);
        if (file == null)
        {
            // sans [UNIT] Icon : d'après le nom de la classe (fireman_sp_gb → FIREFIGHTER_GB…)
            string n = name.ToUpperInvariant();
            string nat = new[] { "US", "FR", "GB", "GR", "UA" }.FirstOrDefault(x => n.EndsWith("_" + x)) ?? "US";
            file = isVehicle ? (isAmbulance ? $"AMBU_{nat}_big.png" : $"PUMPER_{nat}_big.png")
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
        Dictionary<Unit, (string Status, SN.Vector2?, HotPoint? Target, Opening? Opening, Civilian? GoTo, Unit? GoToVehicle, UsableObject? UseTarget)> before = _sel.ToDictionary(u => u, u => (u.Status, End(u), u.Target, u.Opening, u.GoTo, u.GoToVehicle, u.UseTarget));
        _ignoredOrder = false;
        Command(x, y);
        if (_ignoredOrder)
            return;   // ordre déjà en cours (même feu) : ni repère, ni voix
        bool accepted = _sel.Any(u => before.TryGetValue(u, out (string Status, SN.Vector2?, HotPoint? Target, Opening? Opening, Civilian? GoTo, Unit? GoToVehicle, UsableObject? UseTarget) b)
            && (b.Status != u.Status || b.Item2 != End(u) || b.Target != u.Target || b.Opening != u.Opening || b.GoTo != u.GoTo || b.GoToVehicle != u.GoToVehicle || b.UseTarget != u.UseTarget)
            && !RefusedStatus(u.Status));
        if (Ground(x, y) is { } g)
            _clickMarks.Add((new SN.Vector3(g, _session.Units.GroundZ(g) + 8), _uiClock));
        if (accepted)
            Acknowledge();
    }

    private static bool RefusedStatus(string s) => s.StartsWith("impossible") || s.StartsWith("ne peut") || s.Contains("hors service") || s.Contains("hors d'atteinte") || s.StartsWith("ce véhicule");

    private bool _ignoredOrder;

    private void Acknowledge() => _audio?.PlayAny(Enumerable.Range(1, 6).Select(i => $"VOCAL_ACKNOWLEDGE{i}"));

    private void RenderClickMarks()
    {
        _clickMarks.RemoveAll(c => _uiClock - c.t0 > 0.9f);
        if (_clickMarks.Count == 0 || _map)
            return;
        Texture2D? ring = OverlayTexture("Focus_Instant.tga", whiteMask: true);
        foreach ((SN.Vector3 p, float t0) in _clickMarks)
        {
            float k = (_uiClock - t0) / 0.9f;
            // anneau posé au sol : on projette deux points pour avoir sa taille et son aplatissement à l'écran
            if (Project(p) is not { } c || Project(p + new SN.Vector3(90, 0, 0)) is not { } ex || Project(p + new SN.Vector3(0, 90, 0)) is not { } ey)
                continue;
            float rx = MathF.Max(MathF.Abs(ex.X - c.X), MathF.Abs(ey.X - c.X)), ry = MathF.Max(MathF.Abs(ex.Y - c.Y), MathF.Abs(ey.Y - c.Y));
            rx = MathF.Max(rx, 12 * _scale);
            ry = MathF.Max(ry, 7 * _scale);
            float grow = 1.25f - 0.45f * k, a = 1 - k * k;
            SN.Vector4 col = new SN.Vector4(1f, 0.9f, 0.15f, a);
            if (ring != null)
                _ui.Image(ring, c.X - rx * grow, c.Y - ry * grow, 2 * rx * grow, 2 * ry * grow, col);
            else
                for (int i = 0; i < 24; i++)
                {
                    float a0 = i * MathF.Tau / 24, a1 = (i + 1) * MathF.Tau / 24;
                    _ui.Line(c.X + MathF.Cos(a0) * rx * grow, c.Y + MathF.Sin(a0) * ry * grow, c.X + MathF.Cos(a1) * rx * grow, c.Y + MathF.Sin(a1) * ry * grow, col, 2.5f * _scale);
                }
        }
    }

    // ---- barre de vie des feux : ceux qu'un pompier attaque, ou près desquels il se trouve ----
    private void RenderFireBars()
    {
        if (_map || _session == null)
            return;
        List<Unit> men = _session.Units.Active.Where(u => !u.OnBoard).ToList();
        HashSet<HotPoint> shown = new HashSet<HotPoint>(men.Where(u => u.Target != null).Select(u => u.Target!));
        foreach (HotPoint h in _session.Fire.HotPoints)
        {
            if (!h.IsBurning || shown.Contains(h))
                continue;
            if (men.Any(u => !u.IsVehicle && SN.Vector3.DistanceSquared(u.Position, h.Position) < 900f * 900f))
                shown.Add(h);
        }
        foreach (HotPoint h in shown.Where(h => h.IsBurning))
        {
            if (Project(h.Position + new SN.Vector3(0, 0, 230)) is not { } p || p.X < 0 || p.Y < 0 || p.X > _fbW || p.Y > _fbH)
                continue;
            float bw = 54 * _scale, bh = 6 * _scale, k = Math.Clamp(h.Intensity / Math.Max(1, h.Class.IntensityMax), 0, 1);
            _ui.Rect(p.X - bw / 2 - 2 * _scale, p.Y - 2 * _scale, bw + 4 * _scale, bh + 4 * _scale, new SN.Vector4(0, 0, 0, 0.7f));
            _ui.Rect(p.X - bw / 2, p.Y, bw * k, bh, new SN.Vector4(1f, 0.35f + 0.4f * (1 - k), 0.05f, 1));
        }
    }

    // ---- panneau des unités : une ligne par équipe (« H7 », puis les renforts au-dessus), toutes les unités ----
    // devant chaque ligne, le cadre de l'équipe (Overlay\Pages : vert quand elle est là, gris tant qu'elle n'est pas
    // arrivée) ; les unités choisies sont entourées de vert ; une flamme clignote sur l'image d'une unité dans un feu,
    // un « ? » sur un pompier qui ne fait rien
    private const int PanelMax = 14;
    // icônes de la planche : chaque jeu les range ailleurs (IEdition.PageRect)
    private Rectangle TabOn => Page(PageIcon.TeamHere);
    private Rectangle TabOff => Page(PageIcon.TeamAway);
    private Rectangle TabChoose => Page(PageIcon.TeamIncoming);
    private Rectangle FireMark => Page(PageIcon.FireMark);
    private Rectangle IdleMark => Page(PageIcon.IdleMark);

    private Rectangle Page(PageIcon icon)
    {
        (int x, int y, int w, int h) = _edition.PageRect(icon);
        return new Rectangle(x, y, w, h);
    }

    /// <summary>Lignes du panneau, de bas en haut : (nom de l'équipe, unités). Les unités sans équipe connue vont à la fin.</summary>
    private List<(string team, List<Unit> units)> PanelRows()
    {
        List<(string team, List<Unit> units)> rows = new List<(string team, List<Unit> units)>();
        List<Unit> all = _session.Units.Units.Where(u => !u.Hidden).ToList();
        foreach (string t in _session.Units.Teams)
            rows.Add((t, all.Where(u => u.TeamName == t).OrderBy(u => u.IsVehicle ? 0 : 1).ThenBy(u => u.Id).Take(PanelMax).ToList()));
        List<Unit> others = all.Where(u => u.TeamName == null || !_session.Units.Teams.Contains(u.TeamName)).OrderBy(u => u.IsVehicle ? 0 : 1).ThenBy(u => u.Id).Take(PanelMax).ToList();
        if (others.Count > 0)
            rows.Add(("", others));
        return rows;
    }

    private (float x, float y, float cell, float iconH, float rowH, float tabW)? SelectionPanelRect()
    {
        if (_map || _cineOn || !MiniVisible || _session == null || (_session.Units.Teams.Count == 0 && _session.Units.Units.Count == 0))
            return null;
        (float x, float y, float w, float h) r = MiniRect();
        float iconH = 46 * _scale, cell = 52 * _scale, rowH = iconH + 17 * _scale, tabW = iconH * 68f / 46f;
        float x = r.x + r.w + 72 * _scale;      // après la mini-carte et l'icône « faire descendre »
        return (x, _fbH - 12 * _scale - rowH, cell, iconH, rowH, tabW);   // y : ligne du bas (la première équipe)
    }

    private float SelectionPanelHeight => SelectionPanelRect() is { } p ? PanelRows().Count * p.rowH + 16 * _scale : 0;

    private void RenderSelectionPanel()
    {
        if (SelectionPanelRect() is not { } pr)
            return;
        Texture2D? pages = PagesTexture();
        Texture2D? lifeTex = OverlayTexture("WID_PRO_LIFE.png");
        Texture2D? stamTex = OverlayTexture("WID_PRO_STAMINA.png");
        Texture2D? waterTex = OverlayTexture("WID_PRO_RESERVE.png");
        bool blink = _uiClock % 1f < 0.5f;   // une demi-seconde affiché, une demi-seconde caché
        float pad = 6 * _scale, gap = 6 * _scale;
        List<(string team, List<Unit> units)> rows = PanelRows();
        for (int ri = 0; ri < rows.Count; ri++)
        {
            (string team, List<Unit> units) = rows[ri];
            float y = pr.y - ri * pr.rowH, x = pr.x;
            // cadre de l'équipe et son nom : vert si elle est là, jaune (clignotant) si on peut la choisir comme renfort,
            // gris sinon ; un renfort choisi a une barre d'avancement sous son nom jusqu'à son arrivée (1 minute)
            bool here = units.Count > 0 || (team == _session.Units.AirTeam && _session.Units.AirAvailable);   // bombardier disponible : cadre vert
            bool choosable = _session.Units.ChoosableTeams.Contains(team);
            float? incoming = _session.Units.IncomingProgress(team);
            // (cadre jaune : renfort en route, sa fente noire reçoit la barre d'avancement)
            Rectangle tab = incoming != null ? TabChoose : choosable ? (_uiClock % 1f < 0.6f ? TabOn : TabOff) : here ? TabOn : TabOff;
            if (pages != null)
                _ui.Image(pages, x, y, pr.tabW, pr.iconH, tab, SN.Vector4.One);
            else
                _ui.Rect(x, y, pr.tabW, pr.iconH, here ? new SN.Vector4(0.1f, 0.35f, 0.12f, 1) : new SN.Vector4(0.18f, 0.18f, 0.18f, 1));
            float tw = _ui.Measure(team);
            float ty = incoming != null ? y + (29f / 46f * pr.iconH - _ui.LineHeight) / 2 : y + (pr.iconH - _ui.LineHeight) / 2;
            _ui.Text(x + (pr.tabW - tw) / 2, ty, team, here || choosable || incoming != null ? new SN.Vector4(1, 1, 1, 1) : new SN.Vector4(0.75f, 0.75f, 0.75f, 1));
            if (incoming is { } inc)
            {
                // fente noire du cadre jaune : pixels 8 à 58 en largeur, 29 à 37 en hauteur (sur 68 × 46)
                float k = pr.iconH / 46f, bx = x + 9 * k, bw = 49 * k, by = y + 30 * k, bh = 7 * k;
                if (pages == null)
                    _ui.Rect(bx, by, bw, bh, new SN.Vector4(0, 0, 0, 0.8f));
                _ui.Rect(bx, by, bw * inc, bh, new SN.Vector4(0.35f, 0.95f, 0.35f, 1));
            }
            // souris sur le cadre d'un renfort à choisir (ou en route) : aperçu de ses unités à droite
            if ((choosable || (incoming != null && !here)) && (MouseOver(x, y, pr.tabW, pr.iconH) || Environment.GetEnvironmentVariable("FD3_APERCU") == team))
                DrawTeamPreview(team, x + pr.tabW + gap, y, pr);
            x += pr.tabW + gap;
            if (units.Count > 0)
                _ui.Rect(x - pad / 2, y - pad / 2, units.Count * pr.cell + pad / 2, pr.rowH - pad / 2, new SN.Vector4(0.09f, 0.09f, 0.11f, 0.88f));
            foreach (Unit u in units)
            {
                float iw = pr.cell - pad;
                if (_sel.Contains(u))   // unité choisie : cadre vert
                    _ui.Rect(x - 2 * _scale, y - 2 * _scale, iw + 4 * _scale, pr.iconH + 4 * _scale, new SN.Vector4(0.2f, 0.95f, 0.25f, 1));
                _ui.Rect(x, y, iw, pr.iconH, new SN.Vector4(0.16f, 0.17f, 0.2f, 1));
                if (UnitIcon(u) is { } ic)
                {
                    float s = MathF.Min(iw / ic.Width, pr.iconH / ic.Height);
                    SN.Vector4 tint = u.Destroyed || u.Unconscious ? new SN.Vector4(0.45f, 0.35f, 0.35f, 1) : u.OnBoard ? new SN.Vector4(0.7f, 0.7f, 0.7f, 1) : SN.Vector4.One;
                    _ui.Image(ic, x + (iw - ic.Width * s) / 2, y + (pr.iconH - ic.Height * s) / 2, ic.Width * s, ic.Height * s, tint);
                }
                else
                    _ui.Text(x + 4 * _scale, y + 4 * _scale, UnitLabel(u), new SN.Vector4(0.9f, 0.9f, 0.9f, 1));
                if (u.Destroyed)
                    _ui.Text(x + 4 * _scale, y + pr.iconH - _ui.LineHeight, "X", new SN.Vector4(1f, 0.3f, 0.3f, 1));
                // dans un feu : flamme ; pompier qui ne fait rien : « ? » (une demi-seconde sur deux, sur l'image seulement)
                Rectangle? mark = !blink || u.Destroyed ? null
                    : u.InFire ? FireMark
                    : !u.IsVehicle && !u.Unconscious && !u.OnBoard && !Busy(u) ? IdleMark : null;
                if (mark is { } mk && pages != null)
                {
                    float ms = MathF.Min(pr.iconH * 0.62f / mk.Height, iw * 0.62f / mk.Width);
                    _ui.Image(pages, x + (iw - mk.Width * ms) / 2, y + (pr.iconH - mk.Height * ms) / 2, mk.Width * ms, mk.Height * ms, mk, SN.Vector4.One);
                }
                // vie (véhicule) ou endurance (pompier), puis réserve d'agent
                float k = u.IsVehicle ? u.Life / Math.Max(1, u.LifeMax) : u.Stamina / Math.Max(1, u.StaminaMax);
                Bar(x, y + pr.iconH + 3 * _scale, iw, 6 * _scale, Math.Clamp(k, 0, 1), u.IsVehicle ? lifeTex : stamTex,
                    new SN.Vector4(1 - Math.Clamp(k, 0, 1), 0.25f + 0.65f * Math.Clamp(k, 0, 1), 0.2f, 1));
                if (u.Nozzle is { } n && !n.InfiniteReserve)
                    Bar(x, y + pr.iconH + 11 * _scale, iw, 5 * _scale, Math.Clamp(n.Reserve / Math.Max(1, n.ReserveMax), 0, 1),
                        n.Agent is "WATER" or "" ? waterTex : null, AgentColor(n.Agent));   // autre agent : couleur pleine (l'image de l'eau est bleue)
                x += pr.cell;
            }
        }
    }

    private bool MouseOver(float x, float y, float w, float h)
    {
        MouseState ms = Mouse.GetState();
        (float mx, float my) = Fb(ms.X, ms.Y);
        return mx >= x && mx < x + w && my >= y && my < y + h;
    }

    /// <summary>Aperçu des unités d'une caserne (renfort à choisir) : leurs images, un peu transparentes, sur la ligne.</summary>
    private void DrawTeamPreview(string team, float x, float y, (float x, float y, float cell, float iconH, float rowH, float tabW) pr)
    {
        List<(string cls, string? icon, bool vehicle, bool ambulance)> comp = _session.Units.TeamComposition(team);
        if (comp.Count == 0)
            return;
        float pad = 6 * _scale, iw = pr.cell - pad;
        _ui.Rect(x - pad / 2, y - pad / 2, comp.Count * pr.cell + pad / 2, pr.iconH + pad, new SN.Vector4(0.12f, 0.12f, 0.05f, 0.92f));
        foreach ((string cls, string? icon, bool vehicle, bool ambulance) in comp)
        {
            _ui.Rect(x, y, iw, pr.iconH, new SN.Vector4(0.16f, 0.17f, 0.2f, 1));
            string shortName = cls[(cls.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
            if (IconFor(icon, shortName, vehicle, ambulance) is { } ic)
            {
                float s = MathF.Min(iw / ic.Width, pr.iconH / ic.Height);
                _ui.Image(ic, x + (iw - ic.Width * s) / 2, y + (pr.iconH - ic.Height * s) / 2, ic.Width * s, ic.Height * s, new SN.Vector4(1, 1, 1, 0.85f));
            }
            else
                _ui.Text(x + 4 * _scale, y + 4 * _scale, shortName, new SN.Vector4(0.9f, 0.9f, 0.9f, 1));
            x += pr.cell;
        }
    }

    // planche d'icônes du jeu (Data\Overlay\Pages\TextureDefinitions0__32bpp.dds : 2048 × 1024, BGRA non compressé)
    private Texture2D? _pages;
    private bool _pagesTried;
    private Texture2D? PagesTexture()
    {
        if (_pagesTried)
            return _pages;
        _pagesTried = true;
        try
        {
            string path = Path.Combine(_files.DataDir, "Overlay", "Pages", "TextureDefinitions0__32bpp.dds");
            if (!File.Exists(path))
                return null;
            byte[] b = File.ReadAllBytes(path);
            int h = BitConverter.ToInt32(b, 12), w = BitConverter.ToInt32(b, 16), bits = BitConverter.ToInt32(b, 88);
            if (bits != 32 || b.Length < 128 + w * h * 4)
                return null;
            Color[] px = new Color[w * h];
            for (int i = 0; i < px.Length; i++)
            {
                int o = 128 + 4 * i;
                px[i] = new Color(b[o + 2], b[o + 1], b[o], b[o + 3]);   // BGRA → RGBA
            }
            _pages = new Texture2D(GraphicsDevice, w, h);
            _pages.SetData(px);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Planche d'icônes : {e.Message}");
        }
        return _pages;
    }

    private void Bar(float x, float y, float w, float h, float k, Texture2D? tex, SN.Vector4 col)
    {
        _ui.Rect(x - 1, y - 1, w + 2, h + 2, new SN.Vector4(0, 0, 0, 0.85f));
        if (k <= 0)
            return;
        if (tex != null)
            _ui.Image(tex, x, y, w * k, h, col * 0.5f + new SN.Vector4(0.5f, 0.5f, 0.5f, 0.5f));
        else
            _ui.Rect(x, y, w * k, h, col);
    }

    /// <summary>Clic gauche sur le panneau : une image → cette unité seule (la caméra y va si elle était déjà choisie ;
    /// Ctrl ou Maj : l'ajoute ou la retire) ; le cadre d'une équipe → toutes ses unités.</summary>
    private bool SelectionPanelPress(double x, double y)
    {
        if (SelectionPanelRect() is not { } pr)
            return false;
        (float sx, float sy) = Fb(x, y);
        List<(string team, List<Unit> units)> rows = PanelRows();
        float gap = 6 * _scale;
        for (int ri = 0; ri < rows.Count; ri++)
        {
            float y0 = pr.y - ri * pr.rowH;
            if (sy < y0 || sy > y0 + pr.rowH)
                continue;
            List<Unit> units = rows[ri].units;
            KeyboardState kb = Keyboard.GetState();
            // Maj ou Ctrl : plusieurs unités (ajoute ou retire celle-ci)
            bool shift = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift) || kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);
            if (sx >= pr.x && sx < pr.x + pr.tabW)
            {
                // caserne aérienne : visée de la zone à arroser (OpenFDGame.AirSupport.cs)
                if (AirTabPress(rows[ri].team))
                    return true;
                // renfort accordé : le clic sur le cadre d'une caserne la fait venir
                if (_session.Units.ChoosableTeams.Contains(rows[ri].team))
                {
                    DoChooseReinforcement(rows[ri].team);
                    return true;
                }
                if (units.Count == 0)
                    return true;
                if (!shift)
                    _sel.Clear();
                foreach (Unit u in units.Where(u => !u.Destroyed))
                    _sel.Add(u);
                return true;
            }
            float x0 = pr.x + pr.tabW + gap;
            int i = (int)MathF.Floor((sx - x0) / pr.cell);
            if (sx < x0 || i < 0 || i >= units.Count)
                return false;
            Unit pick = units[i];
            if (shift)
            {
                if (!_sel.Remove(pick))
                    _sel.Add(pick);
                return true;
            }
            // la caméra ne va sur l'unité que si elle était déjà choisie (second clic)
            bool already = _sel.Contains(pick);
            _sel.Clear();
            _sel.Add(pick);
            if (already)
            {
                _target.X = pick.Position.X;
                _target.Y = pick.Position.Y;
            }
            return true;
        }
        return false;
    }

    /// <summary>Couleur de la jauge de réserve selon l'agent : gris pour l'anti-métal, blanc pour la mousse, bleu sinon (eau).</summary>
    private static SN.Vector4 AgentColor(string agent) => agent switch
    {
        "METAL" => new SN.Vector4(0.62f, 0.64f, 0.68f, 1),
        "FOAM" => new SN.Vector4(0.97f, 0.97f, 0.97f, 1),
        _ => new SN.Vector4(0.25f, 0.6f, 1f, 1),
    };
}
