using FD3.Formats;
using FD3.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SN = System.Numerics;

namespace FD3.MG;

// menu de démarrage (choix de la mission et de la difficulté), objectifs de la mission, écran de victoire ou de défaite
public sealed partial class Fd3Game
{
    public bool StartMenu;
    private bool _menu, _ended;
    private sealed record MissionEntry(string Dir, string Title, bool Playable);
    private List<MissionEntry> _missions = new();
    private int _menuIndex, _menuDiff;
    private Texture2D? _menuBack;
    private Texture2D? _iconExit;
    private bool _endSoundPlayed;
    private static readonly string[] Diffs = { "EASY", "NORMAL", "HARD" };
    private static readonly string[] DiffNames = { "Facile", "Normal", "Difficile" };

    // ---------------------------------------------------------------- menu
    private void OpenMenu()
    {
        _menu = true; _ended = false; _screen = 0;
        _audio?.StopAll();
        EnsureMissionList();
        _menuIndex = Math.Max(0, _missions.FindIndex(m => m.Dir.Equals(_mission, StringComparison.OrdinalIgnoreCase)));
        _menuDiff = Math.Max(0, Array.IndexOf(Diffs, _difficulty));
        if (_menuBack == null)
        {
            // fond : une des images du jeu (Data\back1.bmp … back8.bmp)
            var path = Path.Combine(_files.DataDir, $"back{1 + Random.Shared.Next(8)}.bmp");
            if (File.Exists(path) && DecodeBmp(File.ReadAllBytes(path)) is { } img) _menuBack = Tex.FromColors(GraphicsDevice, img.w, img.h, img.px, false);
        }
        Window.Title = "Fire Department 3";
    }

    /// <summary>Liste des missions (titres dans la langue choisie) ; refaite après un changement de langue.</summary>
    private void EnsureMissionList()
    {
        _loc ??= Localization.Load(_files.DataDir);
        if (_missions.Count == 0)
        {
            foreach (var lv in _files.List("Design\\Mission\\").Where(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase)))
            {
                var parts = lv.Split('\\'); if (parts.Length < 4) continue;
                var dir = parts[2];
                if (_missions.Any(m => m.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase))) continue;
                bool gfx = _files.List("Gfx\\Mission\\" + dir + "\\").Any();
                string title = dir;
                try
                {
                    var mis = _files.List("Design\\Mission\\" + dir + "\\").FirstOrDefault(p => p.EndsWith(".mis", StringComparison.OrdinalIgnoreCase));
                    if (mis != null)
                    {
                        var c = ClassFile.Load(_files, mis);
                        var key = c.Get("BRIEFING", "StoryTitleKey")?.Trim('"', ' ');
                        var t = !string.IsNullOrEmpty(key) ? _loc.Get(key) : null;
                        // « JOURNAL DE MICHAEL » puis le lieu : on garde le lieu
                        var place = t?.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).LastOrDefault(x => x.Length > 0);
                        if (!string.IsNullOrEmpty(place) && !place.Equals(key!.TrimStart('&'), StringComparison.OrdinalIgnoreCase)) title = $"{place.TrimEnd('.')}  ({dir})";
                    }
                }
                catch { }
                _missions.Add(new MissionEntry(dir, title, gfx));
            }
            _missions = _missions.OrderBy(m => m.Playable ? 0 : 1).ThenBy(m => m.Dir).ToList();
        }
        _menuIndex = Math.Clamp(_menuIndex, 0, Math.Max(0, _missions.Count - 1));
    }

    private static (int w, int h, Color[] px)? DecodeBmp(byte[] d)
    {
        if (d.Length < 54 || d[0] != 'B' || d[1] != 'M') return null;
        int off = BitConverter.ToInt32(d, 10), w = BitConverter.ToInt32(d, 18), h = BitConverter.ToInt32(d, 22), bpp = BitConverter.ToInt16(d, 28);
        if (bpp != 24 && bpp != 32) return null;
        bool bottomUp = h > 0; h = Math.Abs(h);
        int stride = (w * bpp / 8 + 3) & ~3; var px = new Color[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = off + (bottomUp ? h - 1 - y : y) * stride;
            for (int x = 0; x < w; x++)
            {
                int o = row + x * bpp / 8; if (o + 2 >= d.Length) break;
                px[y * w + x] = new Color(d[o + 2], d[o + 1], d[o], (byte)255);
            }
        }
        return (w, h, px);
    }

    // rectangles des lignes du menu (pixels de l'image)
    private (float x, float y, float w, float lh) MenuLayout()
    {
        float lh = _ui.LineHeight * 1.5f + 6 * _scale, w = MathF.Min(_fbW * 0.6f, 900 * _scale);
        return ((_fbW - w) / 2, _fbH * 0.2f, w, lh);
    }

    private void MenuInput(KeyboardState k, MouseState m)
    {
        if (Pressed(k, Keys.Escape)) { _screen = 0; return; }
        if (Pressed(k, Keys.Down)) _menuIndex = Math.Min(_missions.Count - 1, _menuIndex + 1);
        if (Pressed(k, Keys.Up)) _menuIndex = Math.Max(0, _menuIndex - 1);
        if (Pressed(k, Keys.Left)) _menuDiff = Math.Max(0, _menuDiff - 1);
        if (Pressed(k, Keys.Right)) _menuDiff = Math.Min(2, _menuDiff + 1);
        bool go = Pressed(k, Keys.Enter);
        var (lx, ly, lw, lh) = MenuLayout();
        var (mx, my) = Fb(m.X, m.Y);
        // difficulté : trois boutons sous le titre
        float bw = 180 * _scale, by = ly - lh * 1.4f;
        for (int i = 0; i < 3; i++)
        {
            float bx = _fbW / 2f + (i - 1.5f) * (bw + 12 * _scale) + 6 * _scale;
            if (m.LeftButton == ButtonState.Pressed && _mPrev.LeftButton == ButtonState.Released && mx >= bx && mx <= bx + bw && my >= by && my <= by + lh) _menuDiff = i;
        }
        for (int i = 0; i < _missions.Count; i++)
        {
            float y = ly + i * lh;
            if (mx < lx || mx > lx + lw || my < y || my > y + lh) continue;
            _menuIndex = i;
            if (m.LeftButton == ButtonState.Pressed && _mPrev.LeftButton == ButtonState.Released) go = true;
        }
        if (go && _missions.Count > 0)
        {
            var e = _missions[_menuIndex];
            if (!e.Playable) { _menuNote = $"{e.Dir} : graphismes absents (Data\\Gfx\\Mission\\{e.Dir}.pak)"; return; }
            _menu = false; _ended = false; _menuNote = null;
            LoadMission(e.Dir, Diffs[_menuDiff]);
        }
    }
    private string? _menuNote;

    private void RenderMenu()
    {
        var gd = GraphicsDevice;
        gd.Clear(new Color(0.05f, 0.05f, 0.06f));
        _ui.Begin();
        DrawMenuBack();
        var (lx, ly, lw, lh) = MenuLayout();
        var accent = new SN.Vector4(1f, 0.48f, 0.18f, 1); var txt = new SN.Vector4(0.93f, 0.92f, 0.9f, 1); var muted = new SN.Vector4(0.6f, 0.6f, 0.6f, 1);
        const string title = "Fire Department 3 - choisissez une mission";
        float ts = _scale * 1.6f;
        _ui.Rect(0, ly - lh * 3.5f, _fbW, _font.LineHeight * ts + 16 * _scale, new SN.Vector4(0, 0, 0, 0.55f));
        _ui.Text((_fbW - _font.Measure(title) * ts) / 2, ly - lh * 3.5f + 8 * _scale, title, accent, ts);
        // difficulté
        float bw = 180 * _scale, by = ly - lh * 1.4f;
        for (int i = 0; i < 3; i++)
        {
            float bx = _fbW / 2f + (i - 1.5f) * (bw + 12 * _scale) + 6 * _scale;
            _ui.Rect(bx, by, bw, lh, i == _menuDiff ? new SN.Vector4(0.85f, 0.35f, 0.1f, 0.95f) : new SN.Vector4(0.1f, 0.1f, 0.12f, 0.85f));
            _ui.Text(bx + (bw - _ui.Measure(DiffNames[i])) / 2, by + (lh - _ui.LineHeight) / 2, DiffNames[i], txt);
        }
        _ui.Rect(lx - 12 * _scale, ly - 8 * _scale, lw + 24 * _scale, _missions.Count * lh + 16 * _scale, new SN.Vector4(0.07f, 0.07f, 0.09f, 0.85f));
        for (int i = 0; i < _missions.Count; i++)
        {
            var e = _missions[i]; float y = ly + i * lh;
            if (i == _menuIndex) _ui.Rect(lx, y, lw, lh - 4 * _scale, new SN.Vector4(0.85f, 0.35f, 0.1f, 0.55f));
            _ui.Text(lx + 14 * _scale, y + (lh - _ui.LineHeight) / 2 - 2 * _scale, e.Title, e.Playable ? txt : muted);
            if (!e.Playable) { const string na = "non installée"; _ui.Text(lx + lw - _ui.Measure(na) - 14 * _scale, y + (lh - _ui.LineHeight) / 2 - 2 * _scale, na, muted); }
        }
        string help = _menuNote ?? "Flèches haut/bas ou souris : mission · < > : difficulté · Entrée ou clic : jouer · Échap : retour";
        _ui.Text((_fbW - _ui.Measure(help)) / 2, ly + _missions.Count * lh + 24 * _scale, help, _menuNote != null ? accent : muted);
        _ui.End();
    }

    // ---------------------------------------------------------------- objectifs, fin de mission
    /// <summary>À chaque image : nouveaux objectifs, objectifs atteints ou manqués, victoire ou défaite.</summary>
    // [INTERFACE_SCRIPT] OnCameraMove / OnCameraRotate / OnCameraZoom du fichier .mis : le didacticiel attend que le joueur
    // déplace, tourne et approche la caméra
    private (SN.Vector3 t, float yaw, float pitch, float dist)? _camPrev;
    private float _camCbWait;
    private void CameraCallbacks(float dt)
    {
        if (_session?.Scripts is not { } sc || _cineOn || _paused) { _camPrev = null; return; }
        var cur = (_target, _yaw, _pitch, _dist);
        if (_camPrev is { } p && (_camCbWait -= dt) <= 0)
        {
            var cfg = _session.MissionConfig;
            string? Cb(string k) => cfg.Get("INTERFACE_SCRIPT", k)?.Trim('"', ' ') is { Length: > 0 } v ? v : null;
            bool fired = false;
            if (SN.Vector2.Distance(new(p.t.X, p.t.Y), new(cur._target.X, cur._target.Y)) > 30 && Cb("OnCameraMove") is { } mv) { DoScript(sc, mv); fired = true; }
            if ((MathF.Abs(p.yaw - cur._yaw) > 0.02f || MathF.Abs(p.pitch - cur._pitch) > 0.02f) && Cb("OnCameraRotate") is { } rt) { DoScript(sc, rt); fired = true; }
            if (MathF.Abs(p.dist - cur._dist) > 20 && Cb("OnCameraZoom") is { } zm) { DoScript(sc, zm); fired = true; }
            if (fired) _camCbWait = 0.3f;
        }
        _camPrev = cur;
    }

    private void UpdateMission()
    {
        if (Host is not { } h) return;
        _loc ??= Localization.Load(_files.DataDir);
        foreach (var (key, ok) in h.ObjectiveEvents)
        {
            var o = h.Objectives.LastOrDefault(x => x.Text == key);
            if (o == null) continue;
            if (o.Status == 1) { _showObjectives = true; Log("Nouvel objectif : " + _loc.Get(key)); _audio?.Play("VOCAL_NEW_OBJECTIVE"); }
            else if (o.Status == 2) { Log("Objectif atteint : " + _loc.Get(key)); _audio?.Play("VOCAL_OBJECTIVE_COMPLETED"); }
            else { Log("Objectif manqué : " + _loc.Get(key)); _audio?.Play("VOCAL_OBJECTIVE_FAILED"); }
        }
        h.ObjectiveEvents.Clear();
        if (h.Outcome != null && !_ended)
        {
            _ended = true; _paused = true; _sel.Clear();
            if (!_endSoundPlayed) { _audio?.Play(h.Outcome == true ? "VOCAL_MISSION_SUCCESS" : "VOCAL_MISSION_FAILURE"); _endSoundPlayed = true; }
        }
    }

    private void EndInput(KeyboardState k)
    {
        if (Pressed(k, Keys.Enter)) LoadMission(_mission, _difficulty);
        else if (Pressed(k, Keys.Escape)) OpenMenu();
    }

    // panneau des objectifs (en haut à gauche) : une case devant chaque objectif (currentaction.png), remplacée par une
    // croix verte quand il est atteint (WID_CHECK_SUCCESS.png) ou rouge quand il est manqué (WID_CHECK_FAIL.png)
    private void RenderObjectives()
    {
        if (Host is not { } h || h.Objectives.Count == 0 || _loc == null || !_showObjectives) return;
        float m = 12 * _scale, pad = 12 * _scale, lh = _ui.LineHeight + 4 * _scale, maxW = MathF.Min(520 * _scale, _fbW * 0.36f);
        float box = _ui.LineHeight * 1.05f, gap = box + 8 * _scale;
        var rows = new List<(string s, SN.Vector4 c, int status, bool first)>();
        foreach (var o in h.Objectives)
        {
            var col = o.Status == 2 ? new SN.Vector4(0.45f, 0.95f, 0.45f, 1) : o.Status == 0 ? new SN.Vector4(1f, 0.4f, 0.35f, 1) : new SN.Vector4(0.98f, 0.92f, 0.45f, 1);
            var text = _loc.Get(o.Text) + (o.Progress && o.Status == 1 ? $" ({o.Value:0})" : "");
            var lines = Wrap(text, maxW - 2 * pad - gap, _scale);
            for (int k = 0; k < lines.Count; k++) rows.Add((lines[k], col, o.Status, k == 0));
        }
        float w = MathF.Min(maxW, rows.Max(r => _ui.Measure(r.s)) + 2 * pad + gap), hgt = rows.Count * lh + 16 * _scale + lh;
        float x = m;
        _ui.Rect(x, m, w, hgt, new SN.Vector4(0.09f, 0.09f, 0.11f, 0.88f));
        _ui.Text(x + pad, m + 6 * _scale, L("&Objectives", "Objectifs"), new SN.Vector4(1f, 0.48f, 0.18f, 1));
        float y = m + 6 * _scale + lh;
        foreach (var (s, c, st, first) in rows)
        {
            if (first)
            {
                var icon = st == 1 ? OverlayTexture("currentaction.png") : OverlayTexture(st == 2 ? "WID_CHECK_SUCCESS.png" : "WID_CHECK_FAIL.png", cropFrame: true);
                float by = y + (_ui.LineHeight - box) / 2;
                if (icon != null) _ui.Image(icon, x + pad, by, box, box, SN.Vector4.One);
                else _ui.Frame(x + pad, by, box, box, c, MathF.Max(1, 2 * _scale));   // texture absente : simple case
            }
            _ui.Text(x + pad + gap, y, s, c); y += lh;
        }
    }

    private void RenderEndScreen()
    {
        if (!_ended || Host is not { } h || _loc == null) return;
        bool win = h.Outcome == true;
        float w = MathF.Min(_fbW * 0.6f, 900 * _scale), pad = 24 * _scale, lh = _ui.LineHeight + 6 * _scale;
        var lines = new List<(string s, SN.Vector4 c)>();
        if (!string.IsNullOrEmpty(h.DebriefText))
            foreach (var l in Wrap(_loc.Get(h.DebriefText!), w - 2 * pad, _scale)) lines.Add((l, new SN.Vector4(0.92f, 0.92f, 0.9f, 1)));
        if (h.Scoring.Count > 0) lines.Add(("", default));
        foreach (var (label, value, medals) in h.Scoring)
            lines.Add(($"{_loc.Get(label)} : {value:0}   ({Math.Clamp(medals, 0, 5)} médaille{(Math.Clamp(medals, 0, 5) > 1 ? "s" : "")} sur 5)", new SN.Vector4(0.98f, 0.85f, 0.4f, 1)));
        lines.Add(("", default));
        lines.Add(("Entrée : rejouer · Échap : menu des missions", new SN.Vector4(0.65f, 0.65f, 0.65f, 1)));
        float ts = _scale * 2f, hgt = _font.LineHeight * ts + lines.Count * lh + 3 * pad;
        float x = (_fbW - w) / 2, y = (_fbH - hgt) / 2;
        _ui.Rect(0, 0, _fbW, _fbH, new SN.Vector4(0, 0, 0, 0.45f));
        _ui.Rect(x, y, w, hgt, new SN.Vector4(0.08f, 0.08f, 0.1f, 0.95f));
        _ui.Frame(x, y, w, hgt, win ? new SN.Vector4(0.4f, 0.9f, 0.4f, 1) : new SN.Vector4(0.95f, 0.35f, 0.3f, 1), 2 * _scale);
        string title = win ? "Mission réussie" : "Mission échouée";
        _ui.Text(x + (w - _font.Measure(title) * ts) / 2, y + pad, title, win ? new SN.Vector4(0.45f, 0.95f, 0.45f, 1) : new SN.Vector4(1f, 0.4f, 0.35f, 1), ts);
        float yy = y + pad + _font.LineHeight * ts + pad;
        foreach (var (s, c) in lines) { if (s.Length > 0) _ui.Text(x + pad, yy, s, c); yy += lh; }
    }

    // ---------------------------------------------------------------- icône « faire descendre » au-dessus du véhicule choisi
    private (Unit v, float x, float y, float s)? ExitIcon()
    {
        if (_map || _cineOn || !MiniVisible) return null;
        var v = _sel.FirstOrDefault(u => u.IsVehicle && u.Passengers.Count > 0);
        if (v == null) return null;
        // juste à droite de la mini-carte, en haut
        var r = MiniRect(); float s = 44 * _scale;
        return (v, r.x + r.w + 12 * _scale, r.y + 4 * _scale, s);
    }

    private bool ExitIconHit(double x, double y)
    {
        if (ExitIcon() is not { } ic) return false;
        var (sx, sy) = Fb(x, y);
        if (sx < ic.x || sx > ic.x + ic.s || sy < ic.y || sy > ic.y + ic.s) return false;
        int n = DoUnload(ic.v);
        Log(n > 0 ? $"{n} pompier(s) descendent du véhicule" : "Le véhicule doit être arrêté pour faire descendre");
        return true;
    }

    private void RenderExitIcon()
    {
        if (ExitIcon() is not { } ic) return;
        if (_iconExit == null)
        {
            // icône « entrer » du jeu (curseurs : case 14, un carré ouvert à droite traversé par une flèche), sans la petite flèche du pointeur
            var img = _files.TryRead("Gfx\\Textures\\_cache\\MouseCursors.dds") is { } d ? Tex.DecodeDds32(d) : null;
            if (img is not { } im) return;
            var px = new Color[32 * 32];
            int cx = 14 % 8 * 32, cy = 14 / 8 * 32;
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    int o = ((cy + y) * im.w + cx + x) * 4;
                    px[y * 32 + x] = x + y < 15 ? Color.Transparent : new Color(im.rgba[o], im.rgba[o + 1], im.rgba[o + 2], im.rgba[o + 3]);
                }
            _iconExit = new Texture2D(GraphicsDevice, 32, 32); _iconExit.SetData(px);
        }
        var (sx, sy) = Fb(Mouse.GetState().X, Mouse.GetState().Y);
        bool over = sx >= ic.x && sx <= ic.x + ic.s && sy >= ic.y && sy <= ic.y + ic.s;
        _ui.Rect(ic.x - 4 * _scale, ic.y - 4 * _scale, ic.s + 8 * _scale, ic.s + 8 * _scale, over ? new SN.Vector4(0.85f, 0.35f, 0.1f, 0.9f) : new SN.Vector4(0.08f, 0.08f, 0.1f, 0.8f));
        _ui.Image(_iconExit, ic.x, ic.y, ic.s, ic.s, SN.Vector4.One);
        string t = $"{ic.v.Passengers.Count}";
        _ui.Text(ic.x + ic.s + 6 * _scale, ic.y + (ic.s - _ui.LineHeight) / 2, t, new SN.Vector4(1, 1, 1, 1));
    }
}
