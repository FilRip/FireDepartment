using OpenFD.Formats;
using OpenFD.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SN = System.Numerics;

namespace OpenFD.MG;

// briefing de la mission, joué une fois la mission chargée (avant qu'elle commence) : Data\Video\<mission>_briefing.bik (nom donné par VIDEOFEEDFILE du fichier .mis), avec ses
// sous-titres (<même nom>.subtitle : début et fin en millisecondes, puis la clé du texte dans MISSION_xxx.loc) et la voix
// de chaque sous-titre (<clé>.wav, voix de la langue choisie d'abord). Échap : passer et commencer la mission.
public sealed partial class OpenFDGame
{
    private VideoScreen? _briefing;
    private readonly List<(double from, double to, string key)> _briefSubs = new();
    private int _briefNext;
    private (string dir, string difficulty)? _briefGo;
    private bool _briefFrozen;   // essais (captures) : la vidéo reste sur l'image choisie
    // cadres d'informations à droite de la vidéo : titre (blanc) et lignes (jaune)
    private readonly List<(string title, List<string> lines)> _briefInfo = new();

    /// <summary>
    /// Informations du briefing, tirées du fichier .mis ([BRIEFING] OBJECTIVEn, LocationDatan, FireTypen, SpecificHazardn ;
    /// [FIRESTATION] INITIALDISPATCH, REINFORCEMENTn) et des équipes de Design\FireStation\*.cfg, traduites.
    /// </summary>
    private void LoadBriefingInfo(string dir)
    {
        _briefInfo.Clear();
        _loc ??= Localization.Load(_files.DataDir);
        string? misPath = _files.List("Design\\Mission\\" + dir + "\\").FirstOrDefault(p => p.EndsWith(".mis", StringComparison.OrdinalIgnoreCase));
        if (misPath == null)
            return;
        ClassFile mis;
        try
        {
            mis = ClassFile.Load(_files, misPath);
        }
        catch
        {
            return;
        }
        List<string> Keys(string prefix) => Enumerable.Range(1, 12).Select(i => mis.Get("BRIEFING", prefix + i)?.Trim('"', ' '))
            .Where(k => !string.IsNullOrEmpty(k)).Select(k => _loc!.Get(k!)).ToList();
        _briefInfo.Add((_loc.Get("&PrimaryObjectives"), Keys("Objective")));
        _briefInfo.Add((_loc.Get("&LocationData"), Keys("LocationData")));
        _briefInfo.Add((_loc.Get("&FireTypeProbability"), Keys("FireType")));
        _briefInfo.Add((_loc.Get("&SpecificHazards"), Keys("SpecificHazard")));
        // caserne initiale : nombre d'unités de chaque sorte (nom public de la classe, au pluriel s'il y en a plusieurs)
        List<string> initial = new List<string>();
        string? dispatch = mis.Get("FIRESTATION", "InitialDispatch")?.Trim('"', ' ');
        if (!string.IsNullOrEmpty(dispatch) && StationFile(dispatch!) is { } st)
        {
            List<string> units = Enumerable.Range(1, 20).Select(i => st.Get("FIRESTATION", $"UNIT{i:00}")?.Trim('"', ' '))
                .Where(u => !string.IsNullOrEmpty(u)).Select(u => u!).ToList();
            List<(string name, int count)> groups = new List<(string name, int count)>();
            foreach (string u in units)
            {
                string? key = UnitPublicName(u);
                if (key == null)
                    continue;
                int at = groups.FindIndex(g => g.name == key);
                if (at >= 0)
                    groups[at] = (key, groups[at].count + 1);
                else
                    groups.Add((key, 1));
            }
            foreach ((string key, int count) in groups)
            {
                string plural = key + "_Plural";
                string name = count > 1 && _loc.Get(plural) != plural.TrimStart('&') ? _loc.Get(plural) : _loc.Get(key);
                initial.Add($"{count} {name}");
            }
        }
        _briefInfo.Add((_loc.Get("&InitialDispatch"), initial));
        // renforts : nom de chaque caserne (Caserne Pompe…)
        List<string> reinf = new List<string>();
        for (int i = 1; i <= 9; i++)
        {
            string? r = mis.Get("FIRESTATION", $"Reinforcement{i}")?.Trim('"', ' ');
            if (string.IsNullOrEmpty(r))
                continue;
            string? name = StationFile(r!)?.Get("FIRESTATION", "Name")?.Trim('"', ' ');
            reinf.Add(string.IsNullOrEmpty(name) ? r! : _loc.Get(name!));
        }
        _briefInfo.Add((_loc.Get("&AvailableReinforcements"), reinf));
    }

    private ClassFile? StationFile(string name)
    {
        try
        {
            return ClassFile.Load(_files, "Design\\FireStation\\" + name + ".cfg");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Clé du nom public d'une unité ([UNIT] PublicName, « &amp;UNIT_Canon ») ; null si elle n'en a pas.</summary>
    private string? UnitPublicName(string classRel)
    {
        try
        {
            string? k = ClassFile.Load(_files, "Design\\Unit\\" + classRel + ".class").Get("UNIT", "PublicName")?.Trim('"', ' ');
            return string.IsNullOrEmpty(k) ? null : k;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Les six cadres d'informations, l'un sous l'autre dans la colonne de droite, répartis sur toute la hauteur.</summary>
    private void DrawBriefingInfo(float x, float w)
    {
        if (_briefInfo.Count == 0)
            return;
        float pad = 10 * _scale, margin = 16 * _scale, lh = _ui.LineHeight + 3 * _scale, border = MathF.Max(1, 2 * _scale);
        SN.Vector4 green = new SN.Vector4(0.25f, 0.85f, 0.3f, 1), back = new SN.Vector4(0.16f, 0.16f, 0.17f, 0.95f);
        SN.Vector4 white = SN.Vector4.One, yellow = new SN.Vector4(1f, 0.88f, 0.2f, 1);
        float inner = w - 2 * pad;
        List<(List<string> title, List<string> lines, float h)> boxes = new List<(List<string> title, List<string> lines, float h)>();
        foreach ((string title, List<string> lines) in _briefInfo)
        {
            List<string> t = Wrap(title, inner, _scale);
            List<string> l = lines.SelectMany(x => Wrap(x, inner - 8 * _scale, _scale)).ToList();
            float h = 2 * pad + t.Count * lh + MathF.Max(1, l.Count) * lh + 4 * _scale;
            boxes.Add((t, l, h));
        }
        // hauteurs selon le contenu ; l'espace qui reste se répartit entre les cadres
        float total = boxes.Sum(b => b.h), gap = MathF.Max(6 * _scale, (_fbH - 2 * margin - total) / (boxes.Count - 1));
        float y = margin;
        foreach ((List<string> title, List<string> lines, float h) in boxes)
        {
            _ui.Rect(x, y, w, h, back);
            _ui.Frame(x, y, w, h, green, border);
            float ty = y + pad;
            foreach (string t in title)
            {
                _ui.Text(x + pad, ty, t, white);
                ty += lh;
            }
            ty += 4 * _scale;
            foreach (string l in lines)
            {
                _ui.Text(x + pad + 8 * _scale, ty, l, yellow);
                ty += lh;
            }
            y += h + gap;
        }
    }

    /// <summary>Charge la mission (fichiers du jeu), puis joue son briefing ; la mission commence à la fin du briefing.</summary>
    private void StartBriefing(string dir, string difficulty, string? video)
    {
        StopMenuVideo();
        if (_net != null && _net.State == OpenFD.Multiplayer.NetState.Briefing)
            StartNetMission();
        else
            BeginMission(dir, difficulty);
        _audio?.StopAll();
        string? path = BriefingPath(video ?? dir + "_briefing", dir);
        if (path == null || Environment.GetEnvironmentVariable("FD3_SANS_BRIEFING") != null)
        {
            FinishBriefing(dir, difficulty);
            return;
        }
        _briefing = VideoScreen.Open(GraphicsDevice, path, false, VideoVolume);
        if (_briefing == null)
        {
            FinishBriefing(dir, difficulty);
            return;
        }
        _briefGo = (dir, difficulty);
        LoadBriefingInfo(dir);
        _briefSubs.Clear();
        _briefNext = 0;
        string subtitles = Path.ChangeExtension(path, ".subtitle");
        if (File.Exists(subtitles))
            foreach (string line in File.ReadAllLines(subtitles))
            {
                string[] f = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length >= 3 && double.TryParse(f[0], out double a) && double.TryParse(f[1], out double b))
                    _briefSubs.Add((a / 1000.0, b / 1000.0, f[2]));
            }
        _briefSubs.Sort((p, q) => p.from.CompareTo(q.from));
        _loc ??= Localization.Load(_files.DataDir);
        Console.WriteLine($"Briefing : {Path.GetFileName(path)}, {_briefSubs.Count} sous-titre(s)");
    }

    // fichier du briefing : le nom donné, sinon un briefing qui commence par le nom de la mission (Lab → laboratory_briefing)
    private string? BriefingPath(string name, string dir)
    {
        if (VideoPath(name + ".bik") is { } p)
            return p;
        string? folder = VideoPath("intro.bik") is { } intro ? Path.GetDirectoryName(intro) : null;
        if (folder == null && Directory.Exists(_files.DataDir))
            folder = Directory.EnumerateDirectories(_files.DataDir).FirstOrDefault(d => Path.GetFileName(d).Equals("Video", StringComparison.OrdinalIgnoreCase));
        if (folder == null)
            return null;
        return Directory.EnumerateFiles(folder, "*.bik").FirstOrDefault(f =>
        {
            string n = Path.GetFileNameWithoutExtension(f);
            return n.EndsWith("_briefing", StringComparison.OrdinalIgnoreCase) && n.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Fin du briefing (passé ou terminé) : la mission, déjà chargée, commence. En réseau : le serveur la lance chez
    /// tous ; un client attend que le serveur la lance (il ne peut pas passer le briefing lui-même).
    /// </summary>
    private void FinishBriefing(string dir, string difficulty)
    {
        if (_net == null || _net.State != OpenFD.Multiplayer.NetState.Briefing)
            return;
        if (_net.IsServer)
        {
            _net.BeginMission();
            _net.MissionLoaded();
        }
        else
            _briefWaiting = true;
    }

    /// <summary>Client en réseau : son briefing est fini, le serveur n'a pas encore lancé la mission.</summary>
    private bool _briefWaiting;

    private void BeginMission(string dir, string difficulty)
    {
        _menu = false;
        _ended = false;
        _menuNote = null;
        LoadMission(dir, difficulty);
    }

    /// <summary>true tant que le briefing passe.</summary>
    private bool UpdateBriefing(float dt)
    {
        if (_briefWaiting)
        {
            _kPrev = Keyboard.GetState();
            _mPrev = Mouse.GetState();
            return true;
        }
        if (_briefing == null)
            return false;
        KeyboardState k = Keyboard.GetState();
        // en réseau, seul le serveur passe le briefing
        bool skip = IsActive && Pressed(k, Keys.Escape) && (_net == null || _net.IsServer || _net.State != OpenFD.Multiplayer.NetState.Briefing);
        _kPrev = k;
        _mPrev = Mouse.GetState();
        if (!_briefFrozen)
            _briefing.Update(dt);
        // voix de chaque sous-titre, une seule fois, à son début
        while (_briefNext < _briefSubs.Count && _briefSubs[_briefNext].from <= _briefing.Time)
        {
            _audio?.Play(_briefSubs[_briefNext].key.TrimStart('&'));
            _briefNext++;
        }
        if (!skip && !_briefing.Finished)
            return true;
        _briefing.Dispose();
        _briefing = null;
        _audio?.StopAll();
        if (_briefGo is { } go)
        {
            _briefGo = null;
            FinishBriefing(go.dir, go.difficulty);
        }
        return _briefWaiting;
    }

    private void DrawBriefing()
    {
        GraphicsDevice.Clear(Color.Black);
        if (_briefWaiting)
        {
            _ui.Begin();
            string wait = "En attente du serveur…";
            _ui.Text((_fbW - _ui.Measure(wait)) / 2, _fbH / 2f, wait, new SN.Vector4(0.8f, 0.8f, 0.8f, 1));
            _ui.End();
            return;
        }
        if (_briefing == null)
            return;
        _ui.Begin();
        // vidéo sur les trois quarts de la largeur ; à droite, les cadres d'informations de la mission
        float videoW = _briefInfo.Count > 0 ? _fbW * 0.75f : _fbW;
        float vs = MathF.Min(videoW / _briefing.Width, (float)_fbH / _briefing.Height);
        float vw = _briefing.Width * vs, vh = _briefing.Height * vs;
        _ui.Image(_briefing.Texture, (videoW - vw) / 2, (_fbH - vh) / 2, vw, vh, SN.Vector4.One);
        if (_briefInfo.Count > 0)
            DrawBriefingInfo(videoW + 8 * _scale, _fbW - videoW - 24 * _scale);
        double t = _briefing.Time;
        (double from, double to, string key) sub = _briefSubs.LastOrDefault(s => s.from <= t && t < s.to);
        if (sub.key != null && _loc != null)
        {
            float maxW = MathF.Min(videoW * 0.85f, 1100 * _scale), lh = _ui.LineHeight + 4 * _scale;
            List<string> lines = Wrap(_loc.Get(sub.key), maxW, _scale);
            float h = lines.Count * lh + 16 * _scale, y = _fbH - h - 40 * _scale;
            float w = lines.Max(l => _ui.Measure(l)) + 32 * _scale;
            _ui.Rect((videoW - w) / 2, y, w, h, new SN.Vector4(0, 0, 0, 0.65f));
            y += 8 * _scale;
            foreach (string l in lines)
            {
                _ui.Text((videoW - _ui.Measure(l)) / 2, y, l, new SN.Vector4(0.95f, 0.95f, 0.92f, 1));
                y += lh;
            }
        }
        string hint = _net != null && !_net.IsServer && _net.State == OpenFD.Multiplayer.NetState.Briefing ? "Le serveur lance la mission" : "Échap : passer";
        _ui.Text(videoW - _ui.Measure(hint) - 16 * _scale, 12 * _scale, hint, new SN.Vector4(0.6f, 0.6f, 0.6f, 1));
        _ui.End();
    }
}
