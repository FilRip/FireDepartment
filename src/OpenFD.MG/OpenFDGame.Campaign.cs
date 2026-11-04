using OpenFD.Formats;
using OpenFD.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SN = System.Numerics;

namespace OpenFD.MG;

// écran « Campagne » : une mission par page, dans l'ordre des campagnes du jeu (Data\Design\Mission\campaign.ini :
// [CAMPAIGNn_MISSIONm]). Flèches en haut à gauche et à droite (BUT_PREVIOUS_CAMPAIGN / BUT_NEXT_CAMPAIGN) pour passer
// d'une mission à l'autre ; à gauche le pays, le nom et l'image de la mission (GameGraphicDB2.Ini), à droite le texte de
// la mission (StoryTextKey du fichier .mis, dans MISSION_xxx.loc) ; en bas à droite « Démarrer ».
public sealed partial class OpenFDGame
{
    private sealed record CampaignPage(int Number, string Dir, string CampaignKey, string NameKey, string? ImageFile, int ImageW, int ImageH,
        string? Background, string? StoryTitleKey, string? StoryKey, string? Briefing);

    private readonly List<CampaignPage> _campaign = new();
    private int _campaignIndex;
    private float _storyScroll;

    /// <summary>Missions de la campagne, numérotées dans l'ordre de campaign.ini.</summary>
    private void EnsureCampaign()
    {
        if (_campaign.Count > 0)
            return;
        ClassFile ini;
        try
        {
            ini = ClassFile.Load(_files, "Design\\Mission\\campaign.ini");
        }
        catch
        {
            return;
        }
        ClassFile? graphics = null;
        try
        {
            graphics = ClassFile.Load(_files, "Overlay\\GameGraphicDB2.Ini");
        }
        catch
        {
        }
        int campaigns = ini.GetInt("GENERAL", "MaxCampaigns", 5);
        int number = 0;
        for (int c = 1; c <= campaigns; c++)
        {
            string campaignKey = Clean(ini.Get($"CAMPAIGN{c}", "Name")) ?? $"Campagne {c}";
            string? background = Clean(ini.Get($"CAMPAIGN{c}", "CampaignBackground"));
            for (int m = 1; m <= 9; m++)
            {
                string section = $"CAMPAIGN{c}_MISSION{m}";
                string? file = Clean(ini.Get(section, "MissionFile"));
                if (file == null)
                    continue;
                string[] parts = file.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
                int at = Array.FindIndex(parts, p => p.Equals("Mission", StringComparison.OrdinalIgnoreCase));
                if (at < 0 || at + 1 >= parts.Length)
                    continue;
                string dir = parts[at + 1];
                // image de la mission : son entrée dans GameGraphicDB2.Ini (fichier et taille)
                string? imageKey = Clean(ini.Get(section, "Image"));
                string? imageFile = null;
                int imageW = 225, imageH = 122;
                if (imageKey != null && graphics != null && Clean(graphics.Get(imageKey, "FILENAME")) is { } f)
                {
                    imageFile = Path.GetFileName(f.Replace('\\', '/'));
                    imageW = graphics.GetInt(imageKey, "WIDTH", imageW);
                    imageH = graphics.GetInt(imageKey, "HEIGHT", imageH);
                }
                // texte de la mission et vidéo du briefing : section [BRIEFING] du fichier .mis
                string? storyTitle = null, story = null, briefing = null;
                try
                {
                    string? mis = _files.List("Design\\Mission\\" + dir + "\\").FirstOrDefault(p => p.EndsWith(".mis", StringComparison.OrdinalIgnoreCase));
                    if (mis != null)
                    {
                        ClassFile settings = ClassFile.Load(_files, mis);
                        storyTitle = Clean(settings.Get("BRIEFING", "StoryTitleKey"));
                        story = Clean(settings.Get("BRIEFING", "StoryTextKey"));
                        briefing = Clean(settings.Get("BRIEFING", "VIDEOFEEDFILE"));
                    }
                }
                catch
                {
                }
                number++;
                _campaign.Add(new CampaignPage(number, dir, campaignKey, Clean(ini.Get(section, "Name")) ?? dir, imageFile, imageW, imageH,
                    background, storyTitle, story, briefing));
            }
        }
        Console.WriteLine($"Campagne : {_campaign.Count} missions");

        static string? Clean(string? s)
        {
            string? t = s?.Trim().Trim('"').Trim();
            return string.IsNullOrEmpty(t) ? null : t;
        }
    }

    private bool InCampaign(string dir) => _campaign.Any(p => p.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase));

    private void OpenCampaign()
    {
        EnsureCampaign();
        if (_campaign.Count == 0)
        {
            EnsureMissionList();
            _screen = 5;
            return;
        }
        int i = _campaign.FindIndex(p => p.Dir.Equals(_mission, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
            _campaignIndex = i;
        _campaignIndex = Math.Clamp(_campaignIndex, 0, _campaign.Count - 1);
        _storyScroll = 0;
        _menuNote = null;
        _screen = 1;
    }

    // ---- disposition de la page (pixels de l'image)
    private float CampaignMargin => 24 * _scale;
    private const float StoryTextSize = 1.25f;
    private float ArrowSize => 56 * _scale;
    private (float x, float y, float w, float h) PrevArrowRect() => (CampaignMargin, CampaignMargin, ArrowSize, ArrowSize);
    private (float x, float y, float w, float h) NextArrowRect() => (_fbW - CampaignMargin - ArrowSize, CampaignMargin, ArrowSize, ArrowSize);
    private (float x, float y, float w, float h) StartButtonRect()
    {
        float w = 180 * 1.5f * _scale, h = 30 * 1.5f * _scale;
        return (_fbW - CampaignMargin - w, _fbH - CampaignMargin - h, w, h);
    }
    private (float x, float y, float w, float h) DifficultyRect(int i, float top, float left, float width)
    {
        float gap = 8 * _scale, w = (width - 2 * gap) / 3, h = _ui.LineHeight * 1.5f + 6 * _scale;
        return (left + i * (w + gap), top, w, h);
    }

    private static bool Inside((float x, float y, float w, float h) r, float px, float py) => px >= r.x && px <= r.x + r.w && py >= r.y && py <= r.y + r.h;

    private void CampaignInput(KeyboardState k, MouseState m)
    {
        if (Pressed(k, Keys.Escape))
        {
            _screen = 0;
            return;
        }
        (float mx, float my) = Fb(m.X, m.Y);
        bool click = m.LeftButton == ButtonState.Pressed && _mPrev.LeftButton == ButtonState.Released;
        int before = _campaignIndex;
        if (Pressed(k, Keys.Left) || (click && Inside(PrevArrowRect(), mx, my)))
            _campaignIndex = Math.Max(0, _campaignIndex - 1);
        if (Pressed(k, Keys.Right) || (click && Inside(NextArrowRect(), mx, my)))
            _campaignIndex = Math.Min(_campaign.Count - 1, _campaignIndex + 1);
        if (_campaignIndex != before)
            _storyScroll = 0;
        if (Pressed(k, Keys.Up))
            _menuDiff = Math.Max(0, _menuDiff - 1);
        if (Pressed(k, Keys.Down))
            _menuDiff = Math.Min(2, _menuDiff + 1);
        (float left, float top, float width) = DifficultyArea();
        for (int i = 0; i < 3; i++)
            if (click && Inside(DifficultyRect(i, top, left, width), mx, my))
                _menuDiff = i;
        int wheel = m.ScrollWheelValue - _mPrev.ScrollWheelValue;
        if (wheel != 0)
            _storyScroll = MathF.Max(0, _storyScroll - wheel / 120f * _ui.LineHeight * 3);
        bool go = Pressed(k, Keys.Enter) || (click && Inside(StartButtonRect(), mx, my));
        if (!go)
            return;
        CampaignPage page = _campaign[_campaignIndex];
        if (!_files.List("Gfx\\Mission\\" + page.Dir + "\\").Any())
        {
            _menuNote = $"{page.Dir} : graphismes absents (Data\\Gfx\\Mission\\{page.Dir}.pak)";
            return;
        }
        _menuNote = null;
        StartBriefing(page.Dir, Diffs[_menuDiff], page.Briefing);
    }

    // zone des boutons de difficulté : sous l'image, dans la moitié gauche
    private (float left, float top, float width) DifficultyArea()
    {
        (float lx, float lw, float imgTop, float imgH) = CampaignLeftColumn();
        return (lx, imgTop + imgH + 24 * _scale, lw);
    }

    private (float x, float w, float imageTop, float imageH) CampaignLeftColumn()
    {
        float x = CampaignMargin * 2, w = _fbW / 2f - CampaignMargin * 3;
        CampaignPage page = _campaign[_campaignIndex];
        float top = CampaignMargin + ArrowSize + 20 * _scale + (_ui.LineHeight + 8 * _scale) + _font.LineHeight * _scale * 1.8f + 16 * _scale;
        float s = MathF.Min(w / page.ImageW, _fbH * 0.42f / page.ImageH);
        return (x, w, top, page.ImageH * s);
    }

    private void RenderCampaign()
    {
        GraphicsDevice.Clear(new Color(0.05f, 0.05f, 0.06f));
        _ui.Begin();
        CampaignPage page = _campaign[_campaignIndex];
        // fond : l'image de la campagne (son pays), assombrie ; sinon celle des menus
        Texture2D? back = page.Background != null ? OverlayTexture(page.Background + ".png") : null;
        if (back != null)
        {
            float s = MathF.Max((float)_fbW / back.Width, (float)_fbH / back.Height);
            _ui.Image(back, (_fbW - back.Width * s) / 2, (_fbH - back.Height * s) / 2, back.Width * s, back.Height * s, new SN.Vector4(0.45f, 0.45f, 0.45f, 1));
        }
        else
            DrawMenuBack();
        SN.Vector4 accent = new(1f, 0.48f, 0.18f, 1), text = new(0.93f, 0.92f, 0.9f, 1), muted = new(0.65f, 0.65f, 0.65f, 1);
        SN.Vector4 panel = new(0.05f, 0.05f, 0.07f, 0.82f);
        (float mx, float my) = Fb(Mouse.GetState().X, Mouse.GetState().Y);

        // flèches : première image au repos, deuxième au survol
        DrawSprite("BUT_PREVIOUS_CAMPAIGN.png", PrevArrowRect(), new Rectangle(0, 0, 48, 48), 48, Inside(PrevArrowRect(), mx, my) ? 1 : 0, _campaignIndex > 0);
        DrawSprite("BUT_NEXT_CAMPAIGN.png", NextArrowRect(), new Rectangle(0, 0, 48, 48), 48, Inside(NextArrowRect(), mx, my) ? 1 : 0, _campaignIndex < _campaign.Count - 1);
        string counter = $"Mission {page.Number} / {_campaign.Count}";
        float ts = _scale * 1.4f;
        _ui.Text((_fbW - _font.Measure(counter) * ts) / 2, CampaignMargin + (ArrowSize - _font.LineHeight * ts) / 2, counter, accent, ts);

        // moitié gauche : pays, nom, image, difficulté
        (float lx, float lw, float imgTop, float imgH) = CampaignLeftColumn();
        float y = CampaignMargin + ArrowSize + 20 * _scale;
        (float dl, float dt, float dw) = DifficultyArea();
        float leftBottom = dt + _ui.LineHeight * 1.5f + 6 * _scale;
        _ui.Rect(lx - 16 * _scale, y - 12 * _scale, lw + 32 * _scale, leftBottom - y + 28 * _scale, panel);
        _ui.Text(lx, y, Loc(page.CampaignKey).ToUpperInvariant(), muted);
        y += _ui.LineHeight + 8 * _scale;
        float ns = _scale * 1.8f;
        string name = Loc(page.NameKey);
        while (_font.Measure(name) * ns > lw && ns > _scale)
            ns *= 0.92f;
        _ui.Text(lx, y, name, accent, ns);
        Texture2D? image = page.ImageFile != null ? OverlayTexture(page.ImageFile) : null;
        float iw = imgH * page.ImageW / page.ImageH;
        if (image != null)
        {
            _ui.Image(image, lx, imgTop, iw, imgH, SN.Vector4.One);
            _ui.Frame(lx, imgTop, iw, imgH, new SN.Vector4(0.5f, 0.5f, 0.55f, 1), MathF.Max(1, _scale));
        }
        else
            _ui.Frame(lx, imgTop, iw, imgH, muted, MathF.Max(1, _scale));
        for (int i = 0; i < 3; i++)
        {
            (float bx, float by, float bw, float bh) = DifficultyRect(i, dt, dl, dw);
            bool hover = Inside((bx, by, bw, bh), mx, my);
            _ui.Rect(bx, by, bw, bh, i == _menuDiff ? new SN.Vector4(0.85f, 0.35f, 0.1f, 0.95f) : hover ? new SN.Vector4(0.2f, 0.2f, 0.24f, 0.95f) : new SN.Vector4(0.1f, 0.1f, 0.12f, 0.9f));
            _ui.Text(bx + (bw - _ui.Measure(DiffNames[i])) / 2, by + (bh - _ui.LineHeight) / 2, DiffNames[i], text);
        }

        // moitié droite : le texte de la mission (molette pour le faire défiler)
        float rx = _fbW / 2f + CampaignMargin, rw = _fbW / 2f - CampaignMargin * 3, ry = CampaignMargin + ArrowSize + 20 * _scale;
        (float sx, float sy, float sw, float sh) = StartButtonRect();
        float rh = sy - 20 * _scale - ry;
        _ui.Rect(rx - 16 * _scale, ry - 12 * _scale, rw + 32 * _scale, rh + 24 * _scale, panel);
        List<(string s, SN.Vector4 c)> lines = new();
        float storyScale = _scale * StoryTextSize;   // texte de la mission un peu plus grand que celui des menus
        if (page.StoryTitleKey != null)
            foreach (string l in Wrap(Loc(page.StoryTitleKey), rw, storyScale))
                lines.Add((l, accent));
        if (lines.Count > 0)
            lines.Add(("", text));
        if (page.StoryKey != null)
            foreach (string l in Wrap(Loc(page.StoryKey), rw, storyScale))
                lines.Add((l, text));
        float lh = _font.LineHeight * storyScale + 4 * _scale;
        float maxScroll = MathF.Max(0, lines.Count * lh - rh);
        _storyScroll = MathF.Min(_storyScroll, maxScroll);
        float ty = ry - _storyScroll;
        foreach ((string s, SN.Vector4 c) in lines)
        {
            if (ty >= ry - 1 && ty + lh <= ry + rh + 1)
                _ui.Text(rx, ty, s, c, storyScale);
            ty += lh;
        }
        if (maxScroll > 0)
        {
            float barH = rh * rh / (rh + maxScroll);
            _ui.Rect(rx + rw + 8 * _scale, ry + (rh - barH) * _storyScroll / maxScroll, 3 * _scale, barH, muted);
        }

        // bouton « Démarrer »
        bool overStart = Inside((sx, sy, sw, sh), mx, my);
        if (!DrawSprite("BUT_STRT_MISS.png", (sx, sy, sw, sh), new Rectangle(2, 4, 180, 30), 38, overStart ? 1 : 0, true))
            _ui.Rect(sx, sy, sw, sh, overStart ? new SN.Vector4(0.2f, 0.55f, 0.95f, 1) : new SN.Vector4(0.1f, 0.1f, 0.12f, 0.95f));
        string start = L("&TEMP_Proceed", "Démarrer");
        _ui.Text(sx + (sw - _ui.Measure(start)) / 2, sy + (sh - _ui.LineHeight) / 2, start, text);

        string help = _menuNote ?? "← → : mission · ↑ ↓ : difficulté · Entrée : démarrer · Échap : retour";
        _ui.Text(CampaignMargin, _fbH - CampaignMargin - _ui.LineHeight, help, _menuNote != null ? accent : muted);
        _ui.End();
    }

    private string Loc(string key)
    {
        _loc ??= Localization.Load(_files.DataDir);
        return key.StartsWith('&') ? _loc.Get(key) : key;
    }

    /// <summary>
    /// Une image d'une planche de boutons empilés : <paramref name="first"/> est la première, les suivantes sont
    /// <paramref name="pitch"/> pixels plus bas (rangée <paramref name="row"/> : 0 au repos, 1 au survol) ; false si la texture manque.
    /// </summary>
    private bool DrawSprite(string file, (float x, float y, float w, float h) r, Rectangle first, int pitch, int row, bool enabled)
    {
        Texture2D? t = OverlayTexture(file);
        if (t == null)
            return false;
        SN.Vector4 tint = enabled ? SN.Vector4.One : new SN.Vector4(0.35f, 0.35f, 0.35f, 0.6f);
        Rectangle src = first;
        src.Y += (enabled ? row : 0) * pitch;
        _ui.Image(t, r.x, r.y, r.w, r.h, src, tint);
        return true;
    }
}
