using OpenFD.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SN = System.Numerics;

namespace OpenFD.MG;

// briefing avant la mission : Data\Video\<mission>_briefing.bik (nom donné par VIDEOFEEDFILE du fichier .mis), avec ses
// sous-titres (<même nom>.subtitle : début et fin en millisecondes, puis la clé du texte dans MISSION_xxx.loc) et la voix
// de chaque sous-titre (<clé>.wav, voix de la langue choisie d'abord). Échap : passer et commencer la mission.
public sealed partial class Fd3Game
{
    private VideoScreen? _briefing;
    private readonly List<(double from, double to, string key)> _briefSubs = new();
    private int _briefNext;
    private (string dir, string difficulty)? _briefGo;
    private bool _briefFrozen;   // essais (captures) : la vidéo reste sur l'image choisie

    private void StartBriefing(string dir, string difficulty, string? video)
    {
        string? path = BriefingPath(video ?? dir + "_briefing", dir);
        if (path == null || Environment.GetEnvironmentVariable("FD3_SANS_BRIEFING") != null)
        {
            FinishBriefing(dir, difficulty);
            return;
        }
        StopMenuVideo();
        _audio?.StopAll();
        _briefing = VideoScreen.Open(GraphicsDevice, path, false, VideoVolume);
        if (_briefing == null)
        {
            FinishBriefing(dir, difficulty);
            return;
        }
        _briefGo = (dir, difficulty);
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
    /// Fin du briefing (passé ou terminé). En réseau : le serveur lance la mission chez tous ; un client attend que le
    /// serveur la lance (il ne peut pas passer le briefing lui-même).
    /// </summary>
    private void FinishBriefing(string dir, string difficulty)
    {
        if (_net == null || _net.State != OpenFD.Multiplayer.NetState.Briefing)
        {
            BeginMission(dir, difficulty);
            return;
        }
        if (_net.IsServer)
        {
            _net.BeginMission();
            StartNetMission();
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
        DrawVideoFit(_briefing, false, SN.Vector4.One);
        double t = _briefing.Time;
        (double from, double to, string key) sub = _briefSubs.LastOrDefault(s => s.from <= t && t < s.to);
        if (sub.key != null && _loc != null)
        {
            float maxW = MathF.Min(_fbW * 0.8f, 1100 * _scale), lh = _ui.LineHeight + 4 * _scale;
            List<string> lines = Wrap(_loc.Get(sub.key), maxW, _scale);
            float h = lines.Count * lh + 16 * _scale, y = _fbH - h - 40 * _scale;
            float w = lines.Max(l => _ui.Measure(l)) + 32 * _scale;
            _ui.Rect((_fbW - w) / 2, y, w, h, new SN.Vector4(0, 0, 0, 0.65f));
            y += 8 * _scale;
            foreach (string l in lines)
            {
                _ui.Text((_fbW - _ui.Measure(l)) / 2, y, l, new SN.Vector4(0.95f, 0.95f, 0.92f, 1));
                y += lh;
            }
        }
        string hint = _net != null && !_net.IsServer && _net.State == OpenFD.Multiplayer.NetState.Briefing ? "Le serveur lance la mission" : "Échap : passer";
        _ui.Text(_fbW - _ui.Measure(hint) - 16 * _scale, 12 * _scale, hint, new SN.Vector4(0.6f, 0.6f, 0.6f, 1));
        _ui.End();
    }
}
