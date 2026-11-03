using System.Text.Json;
using FD3.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SN = System.Numerics;

namespace FD3.MG;

/// <summary>Réglages du joueur, enregistrés dans options.json (dossier de l'utilisateur : %APPDATA%\FD3_CSharp).</summary>
public sealed class Settings
{
    public bool InvertX { get; set; }
    public bool InvertY { get; set; }
    public int Volume { get; set; } = 100;          // %
    public int Brightness { get; set; } = 150;      // % (150 : réglage habituel)
    public string Resolution { get; set; } = "";    // « 1920x1080 » ; vide : fenêtre à la taille de l'écran
    public bool Fullscreen { get; set; }
    /// <summary>Réglages du jeu sans place dans le menu : modifiables à la main dans ce fichier.</summary>
    public GameplaySettings Gameplay { get; set; } = new();
    public string Textures { get; set; } = "high";  // high, med, low
    public string Language { get; set; } = "";      // vide : celle des voix installées ; sinon FRA, ENG…
    public string Nationality { get; set; } = "";   // vide : celle de la mission ; sinon us, fr, gb, gr, ua

    /// <summary>Dossier « Save » du jeu (à côté de Data) : options.json y est enregistré.</summary>
    public static string SaveDir = "";
    private static string PathOf() => System.IO.Path.Combine(SaveDir, "FD3_CSharp_options.json");

    public static Settings Load(string dataDir)
    {
        var root = Directory.GetParent(dataDir)?.FullName ?? dataDir;
        SaveDir = Directory.Exists(root) ? Directory.EnumerateDirectories(root).FirstOrDefault(d => System.IO.Path.GetFileName(d).Equals("Save", StringComparison.OrdinalIgnoreCase)) ?? System.IO.Path.Combine(root, "Save") : System.IO.Path.Combine(root, "Save");
        // anciennes options (%APPDATA%\FD3_CSharp\options.json) : reprises une fois
        var old = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FD3_CSharp", "options.json");
        try { if (!File.Exists(PathOf()) && File.Exists(old)) { Directory.CreateDirectory(SaveDir); File.Copy(old, PathOf()); } } catch { }
        try { if (File.Exists(PathOf())) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathOf())) ?? new(); } catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOf())!);
            File.WriteAllText(PathOf(), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Console.WriteLine("Options : " + e.Message); }
    }
}

/// <summary>Réglages du jeu modifiables à la main dans Save\FD3_CSharp_options.json (rubrique « Gameplay »).</summary>
public sealed class GameplaySettings
{
    public float HeatFactor { get; set; } = 4f;            // chaleur des feux → endurance des pompiers
    public float SmokeFactor { get; set; } = 0.08f;        // fumée épaisse → endurance des pompiers
    public float AmbulanceHeal { get; set; } = 12f;        // endurance rendue par seconde à l'ambulance
    public float ReviveStamina { get; set; } = 0.35f;      // endurance (part du maximum) après une réanimation
    public float VehicleFireFactor { get; set; } = 1.5f;   // feu → vie des véhicules
    public float AutoAttackRange { get; set; } = 2f;       // portée où un pompier libre va de lui-même éteindre un feu (× portée de sa lance)
    public float AttackBackOff { get; set; } = 0.5f;       // feu tout proche : recul du pompier (part de sa distance d'arrosage habituelle)
    public float StaminaLoss { get; set; } = 0.25f;        // vitesse de la perte d'endurance des pompiers (chaleur + fumée), multiplie HeatFactor et SmokeFactor
    public float CarrySpeed { get; set; } = 0.6f;          // pompier qui porte un civil inconscient : part de sa vitesse normale
    public float ArrowShiftSpeed { get; set; } = 1.6f;     // flèches + Maj : vitesse du défilement de la vue (× la vitesse normale)
    public float FlameSpeed { get; set; } = 10f;           // animation des flammes : images par seconde
    public float FlameSpeedVariation { get; set; } = 4f;   // écart d'une flamme à l'autre (images par seconde en plus, au hasard)
}

// menu principal (lancer une mission, charger une sauvegarde, options, quitter) et écran des options
public sealed partial class Fd3Game
{
    private Settings _settings = new();
    private int _screen;          // 0 menu principal, 1 choix de la mission, 2 options, 3 pause, 4 sauvegardes
    private int _mainIndex, _optIndex;
    private static readonly string[] TexQ = { "high", "med", "low" };
    private static readonly string[] Nats = { "", "us", "fr", "gb", "gr", "ua" };
    private static readonly string[] NatKeys = { "MissionNat", "USA", "France", "England", "Germany", "Ukraine" };
    private static readonly Dictionary<string, string> LangNames = new(StringComparer.OrdinalIgnoreCase)
        { ["FRA"] = "Français", ["ENG"] = "English", ["GER"] = "Deutsch", ["ITA"] = "Italiano", ["SPA"] = "Español", ["UKR"] = "Ukrainska", ["RUS"] = "Russkiy" };

    /// <summary>
    /// Police du jeu : Data\Localize\Lang_XXX\Data\Fonte\font18.fnt pour la langue choisie s'il y en a une, sinon
    /// Data\Fonte\font18.fnt ; la police du projet ne sert plus qu'aux caractères absents (flèches…).
    /// </summary>
    private void UseGameFont(string? lang)
    {
        string? Find(string dir) => Directory.Exists(dir) ? Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals("font18.fnt", StringComparison.OrdinalIgnoreCase)) : null;
        string? path = null;
        if (!string.IsNullOrEmpty(lang))
        {
            var root = Path.Combine(_files.DataDir, "Localize");
            var ld = Directory.Exists(root) ? Directory.EnumerateDirectories(root).FirstOrDefault(d => Path.GetFileName(d).Equals("Lang_" + lang, StringComparison.OrdinalIgnoreCase)) : null;
            if (ld != null) path = Find(Path.Combine(ld, "Data", "Fonte")) ?? Find(Path.Combine(ld, "data", "fonte"));
        }
        path ??= Find(Path.Combine(_files.DataDir, "Fonte"));
        var f = path != null ? Font.LoadFnt(GraphicsDevice, path) : null;
        if (f == null)
        {
            if (_font != null) return;   // police de la langue introuvable : on garde l'actuelle
            throw new FileNotFoundException("Police du jeu introuvable : Data\\Fonte\\font18.fnt");
        }
        _font?.Texture?.Dispose();
        _font = f; _ui.Font = f;
    }
    private string? _fontLang = "?";

    // ---- résolution de l'écran ----
    private string? _appliedRes;
    private List<string> Resolutions()
    {
        var l = new List<string> { "" };
        try
        {
            l.AddRange(GraphicsAdapter.DefaultAdapter.SupportedDisplayModes.Where(m => m.Width >= 800 && m.Height >= 600)
                .Select(m => (m.Width, m.Height)).Distinct().OrderBy(m => m.Width).ThenBy(m => m.Height).Select(m => $"{m.Width}x{m.Height}"));
        }
        catch { }
        foreach (var r in new[] { "1280x720", "1600x900", "1920x1080" }) if (!l.Contains(r)) l.Add(r);
        if (!l.Contains(_settings.Resolution)) l.Add(_settings.Resolution);
        return l;
    }

    private void ApplyResolution()
    {
        if (CapturePath != null) return;
        var key = _settings.Resolution + (_settings.Fullscreen ? "F" : "W");
        if (_appliedRes == key) return;
        bool first = _appliedRes == null;
        _appliedRes = key;
        var parts = _settings.Resolution.Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
        { _gdm.PreferredBackBufferWidth = w; _gdm.PreferredBackBufferHeight = h; }
        else if (!first || _settings.Fullscreen)
        {
            var mode = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
            if (_settings.Fullscreen) { _gdm.PreferredBackBufferWidth = mode.Width; _gdm.PreferredBackBufferHeight = mode.Height; }
            else { _gdm.PreferredBackBufferWidth = (int)Math.Min(1400 * _scale, mode.Width * 0.92f); _gdm.PreferredBackBufferHeight = (int)Math.Min(860 * _scale, mode.Height * 0.88f); }
        }
        else if (!_settings.Fullscreen && !_gdm.IsFullScreen) return;   // au démarrage, sans réglage : la fenêtre par défaut
        _gdm.IsFullScreen = _settings.Fullscreen;
        _gdm.ApplyChanges();
    }

    /// <summary>Alt + Entrée, partout (menus, partie, vidéos) : bascule plein écran / fenêtre.</summary>
    private void AltEnter()
    {
        if (!IsActive) return;
        var k = Keyboard.GetState();
        bool alt = k.IsKeyDown(Keys.LeftAlt) || k.IsKeyDown(Keys.RightAlt);
        bool down = alt && k.IsKeyDown(Keys.Enter);
        if (down && !_altEnterDown)
        {
            _settings.Fullscreen = !_settings.Fullscreen;
            ApplyResolution(); _settings.Save();
        }
        _altEnterDown = down;
        if (down) _kPrev = k;   // l'Entrée ne valide pas en plus le menu
    }

    private void ApplySettings()
    {
        ApplyResolution();
        if (_fontLang != _settings.Language) { _fontLang = _settings.Language; UseGameFont(_settings.Language); }
        _files.TextureQuality = _settings.Textures;
        if (Localization.Forced != _settings.Language) { Localization.Forced = _settings.Language; _loc = null; _missions.Clear(); _iniTexts.Clear(); }
        UnitManager.Nationality = _settings.Nationality;
        _audio?.Db.ApplyLanguage(_files.DataDir, string.IsNullOrEmpty(_settings.Language) ? null : _settings.Language);
        _bright = Math.Clamp(_settings.Brightness, 40, 200) / 100f;
        var gp = _settings.Gameplay ?? new GameplaySettings();
        UnitManager.HeatFactor = gp.HeatFactor; UnitManager.SmokeFactor = gp.SmokeFactor; UnitManager.AmbulanceHeal = gp.AmbulanceHeal;
        UnitManager.ReviveStamina = Math.Clamp(gp.ReviveStamina, 0.05f, 1f); UnitManager.VehicleFireFactor = gp.VehicleFireFactor;
        UnitManager.AutoAttackRange = Math.Clamp(gp.AutoAttackRange, 1f, 10f); UnitManager.AttackBackOff = Math.Clamp(gp.AttackBackOff, 0.1f, 1f);
        UnitManager.StaminaLoss = Math.Clamp(gp.StaminaLoss, 0f, 5f);
        UnitManager.CarrySpeed = Math.Clamp(gp.CarrySpeed, 0.2f, 1f);
        _arrowShift = Math.Clamp(gp.ArrowShiftSpeed, 1f, 5f);
        _flameFps = Math.Clamp(gp.FlameSpeed, 0.5f, 60f); _flameFpsVar = Math.Clamp(gp.FlameSpeedVariation, 0f, 60f);
        if (_audio != null) _audio.Master = 0.8f * Math.Clamp(_settings.Volume, 0, 100) / 100f;
    }

    // ---- textes des menus : ceux du jeu (Data\Localize\Lang_XXX\Interface_XXX.loc), sinon le texte français donné ----
    private readonly HashSet<string> _missingTexts = new();
    private string L(string key, string fallback)
    {
        _loc ??= Localization.Load(_files.DataDir);
        var t = _loc.Get(key);
        if (string.IsNullOrWhiteSpace(t) || t.Equals(key.TrimStart('&'), StringComparison.Ordinal))
        {
            if (_missingTexts.Add(fallback)) Console.WriteLine($"Texte sans traduction du jeu : « {fallback} »");
            return fallback;
        }
        return Nice(t);
    }
    // « CAMPAGNE » → « Campagne » (le jeu écrit ses boutons en capitales)
    private static string Nice(string t)
    {
        t = t.Trim().Trim('*').Trim().TrimEnd(':').Trim();
        if (t.Length > 1 && t.Any(char.IsLetter) && t.Where(char.IsLetter).All(char.IsUpper))
            t = t.ToLower();
        return t.Length > 0 ? char.ToUpper(t[0]) + t[1..] : t;
    }
    // textes sans équivalent dans les fichiers du jeu : dossier « languages » du projet, un fichier INI par langue
    // (FRA.ini, ENG.ini…) ; valeur vide ou absente : celle de la langue par défaut (FRA.ini), sinon le texte ci-dessous
    private static readonly Dictionary<string, string> OwnTexts = new()
    {
        ["InvertX"] = "Rotation : inverser l'axe horizontal",
        ["InvertY"] = "Rotation : inverser l'axe vertical",
        ["Brightness"] = "Luminosité", ["Resolution"] = "Résolution de l'écran", ["Fullscreen"] = "Plein écran", ["Windowed"] = "Fenêtre",
        ["Textures"] = "Qualité des textures", ["Language"] = "Langue",
        ["Nationality"] = "Nationalité des unités",
        ["Yes"] = "Oui", ["No"] = "Non", ["High"] = "Haute", ["Medium"] = "Moyenne", ["Low"] = "Basse",
        ["Auto"] = "Automatique (langue installée)", ["MissionNat"] = "Celle de la mission",
        ["USA"] = "États-Unis", ["France"] = "France", ["England"] = "Angleterre", ["Germany"] = "Allemagne", ["Ukraine"] = "Ukraine",
        ["NoSave"] = "Aucune sauvegarde", ["HelpLoad"] = "Haut / Bas · Entrée : charger · Suppr : effacer · Échap : retour",
        ["HelpMain"] = "Haut / Bas · Entrée · Échap", ["HelpPause"] = "Haut / Bas · Entrée · Échap / Espace : reprendre",
        ["HelpOptions"] = "Haut / Bas · < > · Échap : retour",
        ["OptionsNote"] = "Textures, langue des textes et nationalité : à la prochaine mission chargée.",
    };
    public const string DefaultLanguage = "FRA";
    private readonly Dictionary<string, Dictionary<string, string>> _iniTexts = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, string> IniTexts(string lang)
    {
        if (_iniTexts.TryGetValue(lang, out var d)) return d;
        d = new(StringComparer.OrdinalIgnoreCase);
        var dir = Path.Combine(AppContext.BaseDirectory, "languages");
        var file = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.ini").FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(lang, StringComparison.OrdinalIgnoreCase)) : null;
        if (file != null)
            foreach (var raw in File.ReadAllLines(file, System.Text.Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('[')) continue;
                int eq = line.IndexOf('='); if (eq <= 0) continue;
                var v = line[(eq + 1)..].Trim().Trim('"');
                if (v.Length > 0) d[line[..eq].Trim()] = v;
            }
        return _iniTexts[lang] = d;
    }

    private string O(string k)
    {
        _loc ??= Localization.Load(_files.DataDir);
        var lang = string.IsNullOrEmpty(_settings.Language) ? _loc.Language : _settings.Language;
        if (IniTexts(lang).TryGetValue(k, out var v)) return v;
        if (IniTexts(DefaultLanguage).TryGetValue(k, out v)) return v;
        return OwnTexts.TryGetValue(k, out v) ? v : k;
    }

    private int _optionsFrom;     // écran où revenir en quittant les options (0 menu principal, 3 pause)
    private bool _inGame;         // menu ouvert par-dessus une partie en cours (pause)
    private int _pauseIndex;
    private bool _savedNote;   // « Sauver » vient d'être choisi dans la pause

    /// <summary>Pause en cours de partie (Échap ou Espace) : menu de pause par-dessus le jeu figé.</summary>
    private void OpenPause()
    {
        if (_session == null) { OpenMenu(); return; }
        _menu = true; _inGame = true; _screen = 3; _pauseIndex = 0; _savedNote = false;
        _audio?.PauseAll(true);
    }

    private void ClosePause()
    {
        _menu = false; _inGame = false; _paused = false; _settings.Save();
        _audio?.PauseAll(false);
    }

    private void ScreenInput(KeyboardState k, MouseState m)
    {
        if (_screen == 1) { MenuInput(k, m); return; }
        if (_screen == 4) { LoadScreenInput(k, m); return; }
        var rows = Rows();
        ref int idx = ref (_screen == 0 ? ref _mainIndex : ref _screen == 3 ? ref _pauseIndex : ref _optIndex);
        if (Pressed(k, Keys.Escape) || (_screen == 3 && Pressed(k, Keys.Space)))
        {
            if (_screen == 0) Exit();
            else if (_screen == 3) ClosePause();
            else { _screen = _optionsFrom; _settings.Save(); }
            return;
        }
        if (Pressed(k, Keys.Down)) idx = Math.Min(rows.Count - 1, idx + 1);
        if (Pressed(k, Keys.Up)) idx = Math.Max(0, idx - 1);
        int dir = Pressed(k, Keys.Right) ? 1 : Pressed(k, Keys.Left) ? -1 : 0;
        bool go = Pressed(k, Keys.Enter);
        var (lx, ly, lw, lh) = ScreenLayout(rows.Count);
        var (mx, my) = Fb(m.X, m.Y);
        bool click = m.LeftButton == ButtonState.Pressed && _mPrev.LeftButton == ButtonState.Released;
        for (int i = 0; i < rows.Count; i++)
        {
            float y = ly + i * lh;
            if (mx < lx || mx > lx + lw || my < y || my > y + lh) continue;
            if (m.X != _mPrev.X || m.Y != _mPrev.Y || click) idx = i;
            if (click) { go = true; dir = mx > lx + lw * 0.6f ? 1 : 0; if (rows[i].Value != null && mx < lx + lw * 0.6f) dir = 0; }
        }
        if (rows.Count == 0) return;
        var row = rows[Math.Clamp(idx, 0, rows.Count - 1)];
        if (row.Enabled && (go || dir != 0)) row.Act(dir == 0 ? 1 : dir);
    }

    private sealed record Row(string Label, string? Value, bool Enabled, Action<int> Act);

    private List<Row> Rows() => _screen switch { 0 => MainRows(), 3 => PauseRows(), _ => OptionRows() };

    private List<Row> MainRows() => new()
    {
        new(L("&Campaign", "Campagne"), null, true, _ => { EnsureMissionList(); _screen = 1; _menuNote = null; }),
        new(L("&LoadSavedGame", "Charger une sauvegarde"), null, true, _ => OpenLoadScreen(0)),
        new(L("&Options", "Options"), null, true, _ => { _optionsFrom = 0; _screen = 2; _optIndex = 0; }),
        new(L("&Credits", "Générique"), null, VideoPath("credits.bik") != null, _ => StartCredits()),
        new(L("&Exit", "Quitter"), null, true, _ => Exit()),
    };

    private List<Row> PauseRows() => new()
    {
        new(L("&ResumeGame", "Reprendre"), null, true, _ => ClosePause()),
        new(_savedNote ? L("&Save", "Sauver") + " - OK" : L("&Save", "Sauver"), null, !_ended, _ => { _savedNote = SaveGame() != null; }),
        new(L("&Load", "Charger"), null, true, _ => OpenLoadScreen(3)),
        new(L("&Options", "Options"), null, true, _ => { _optionsFrom = 3; _screen = 2; _optIndex = 0; }),
        new(L("&RestartMission", "Recommencer la mission"), null, true, _ => { _inGame = false; _audio?.PauseAll(false); LoadMission(_mission, _difficulty); }),
        new(L("&AbortMission", "Abandonner la mission"), null, true, _ => { _inGame = false; _audio?.PauseAll(false); OpenMenu(); }),
        new(L("&ExitGame", "Quitter le jeu"), null, true, _ => Exit()),
    };

    private List<Row> OptionRows()
    {
        var langs = new List<string> { "" }; langs.AddRange(Localization.Available(_files.DataDir));
        string LangName(string c) => c == "" ? O("Auto") : LangNames.TryGetValue(c, out var n) ? $"{n} ({c})" : c;
        T Cycle<T>(T[] arr, T cur, int d) { int i = Array.IndexOf(arr, cur); if (i < 0) i = 0; return arr[((i + d) % arr.Length + arr.Length) % arr.Length]; }
        void Changed() { ApplySettings(); _settings.Save(); }
        string YN(bool b) => b ? O("Yes") : O("No");
        var texNames = new[] { O("High"), O("Medium"), O("Low") };
        return new()
        {
            new(O("InvertX"), YN(_settings.InvertX), true, _ => { _settings.InvertX = !_settings.InvertX; Changed(); }),
            new(O("InvertY"), YN(_settings.InvertY), true, _ => { _settings.InvertY = !_settings.InvertY; Changed(); }),
            new(L("&VolumeMaster", "Volume général"), $"{_settings.Volume} %", true, d => { _settings.Volume = Math.Clamp(_settings.Volume + 10 * d, 0, 100); Changed(); _audio?.Play("VOCAL_SELECTION1"); }),
            new(O("Brightness"), $"{_settings.Brightness} %", true, d => { _settings.Brightness = Math.Clamp(_settings.Brightness + 10 * d, 40, 200); Changed(); }),
            new(O("Resolution"), _settings.Resolution == "" ? O("Auto").Split('(')[0].Trim() : _settings.Resolution.Replace("x", " × "), true, d => { var rs = Resolutions(); _settings.Resolution = rs[((rs.IndexOf(_settings.Resolution) + d) % rs.Count + rs.Count) % rs.Count]; Changed(); }),
            new(O("Fullscreen"), YN(_settings.Fullscreen), true, _ => { _settings.Fullscreen = !_settings.Fullscreen; Changed(); }),
            new(O("Textures"), texNames[Math.Max(0, Array.IndexOf(TexQ, _settings.Textures))], true, d => { _settings.Textures = Cycle(TexQ, _settings.Textures, d); Changed(); }),
            new(O("Language"), LangName(_settings.Language), true, d => { _settings.Language = Cycle(langs.ToArray(), _settings.Language, d); Changed(); }),
            new(O("Nationality"), O(NatKeys[Math.Max(0, Array.IndexOf(Nats, _settings.Nationality))]), true, d => { _settings.Nationality = Cycle(Nats, _settings.Nationality, d); Changed(); }),
            new(L("&Back", "Retour"), null, true, _ => { _screen = _optionsFrom; _settings.Save(); }),
        };
    }

    private (float x, float y, float w, float lh) ScreenLayout(int n)
    {
        // boutons étroits (menu principal, pause) ; les options gardent la place pour le réglage et sa valeur
        float lh = _ui.LineHeight * (_screen == 2 ? 2.35f : 1.7f) + 8 * _scale, w = _screen == 2 ? MathF.Min(_fbW * 0.4f, 560 * _scale) : MathF.Min(_fbW * 0.35f, 480 * _scale);
        return ((_fbW - w) / 2, MathF.Max(_fbH * 0.3f, (_fbH - n * lh) / 2), w, lh);
    }

    private void RenderScreen()
    {
        if (_screen == 1) { RenderMenu(); return; }
        if (_screen == 4) { RenderLoadScreen(); return; }
        var gd = GraphicsDevice;
        var rows = Rows();
        int idx = _screen == 0 ? _mainIndex : _screen == 3 ? _pauseIndex : _optIndex;
        if (!_inGame) gd.Clear(new Color(0.05f, 0.05f, 0.06f));
        _ui.Begin();
        if (_inGame) _ui.Rect(0, 0, _fbW, _fbH, new SN.Vector4(0, 0, 0, 0.22f));   // la partie figée reste visible derrière
        else DrawMenuBack();
        var (lx, ly, lw, lh) = ScreenLayout(rows.Count);
        var accent = new SN.Vector4(1f, 0.48f, 0.18f, 1); var txt = new SN.Vector4(0.93f, 0.92f, 0.9f, 1); var muted = new SN.Vector4(0.5f, 0.5f, 0.5f, 1);
        string title = _screen == 0 ? "Fire Department 3" : _screen == 3 ? L("&GameInPause", "Jeu en pause") : L("&Options", "Options");
        float ts = _scale * 2f;
        if (_screen != 0)   // le titre du jeu est déjà sur l'image de fond
        {
            _ui.Rect(0, ly - lh * 2.4f, _fbW, _font.LineHeight * ts + 16 * _scale, new SN.Vector4(0, 0, 0, 0.55f));
            _ui.Text((_fbW - _font.Measure(title) * ts) / 2, ly - lh * 2.4f + 8 * _scale, title, accent, ts);
        }
        _ui.Rect(lx - 12 * _scale, ly - 8 * _scale, lw + 24 * _scale, rows.Count * lh + 16 * _scale, new SN.Vector4(0.07f, 0.07f, 0.09f, 0.78f));
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; float y = ly + i * lh, ty = y + (lh - _ui.LineHeight) / 2 - 2 * _scale;
            if (i == idx && r.Enabled) _ui.Rect(lx, y, lw, lh - 4 * _scale, new SN.Vector4(0.85f, 0.35f, 0.1f, 0.55f));
            if (r.Value == null) _ui.Text(lx + (lw - _ui.Measure(r.Label)) / 2, ty, r.Label, r.Enabled ? txt : muted);
            else
            {
                // réglage sur deux lignes (boutons étroits) : son nom, puis sa valeur
                float ty1 = y + (lh - 2 * _ui.LineHeight) / 2 - 2 * _scale;
                _ui.Text(lx + (lw - _ui.Measure(r.Label)) / 2, ty1, r.Label, txt);
                string v = $"< {r.Value} >";
                _ui.Text(lx + (lw - _ui.Measure(v)) / 2, ty1 + _ui.LineHeight, v, new SN.Vector4(0.98f, 0.85f, 0.45f, 1));
            }
        }
        string help = _screen == 0 ? O("HelpMain") : _screen == 3 ? O("HelpPause") : O("HelpOptions");
        float hw = MathF.Max(lw + 24 * _scale, MathF.Max(_ui.Measure(help), _screen == 2 ? _ui.Measure(O("OptionsNote")) : 0) + 32 * _scale);
        _ui.Rect((_fbW - hw) / 2, ly + rows.Count * lh + 16 * _scale, hw, _ui.LineHeight * (_screen == 2 ? 3f : 1.6f), new SN.Vector4(0, 0, 0, 0.7f));
        _ui.Text((_fbW - _ui.Measure(help)) / 2, ly + rows.Count * lh + 24 * _scale, help, new SN.Vector4(0.65f, 0.65f, 0.65f, 1));
        if (_screen == 2) { string note = O("OptionsNote"); _ui.Text((_fbW - _ui.Measure(note)) / 2, ly + rows.Count * lh + 24 * _scale + _ui.LineHeight * 1.5f, note, new SN.Vector4(0.62f, 0.62f, 0.62f, 1)); }
        _ui.End();
    }

    private void DrawMenuBack()
    {
        if (_menuVideo != null) { DrawVideoFit(_menuVideo, true, new SN.Vector4(0.9f, 0.9f, 0.9f, 1)); return; }
        if (_menuBack == null) return;
        float s = MathF.Max((float)_fbW / _menuBack.Width, (float)_fbH / _menuBack.Height);
        _ui.Image(_menuBack, (_fbW - _menuBack.Width * s) / 2, (_fbH - _menuBack.Height * s) / 2, _menuBack.Width * s, _menuBack.Height * s, new SN.Vector4(0.92f, 0.92f, 0.92f, 1));
    }
}
