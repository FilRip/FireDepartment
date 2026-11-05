using System.Text.Json;
using OpenFD.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SN = System.Numerics;

namespace OpenFD.MG;

// sauvegarde et chargement d'une partie en cours.
// La simulation est déterministe : la sauvegarde garde la mission et les ordres du joueur (avec le moment où il les a
// donnés) ; le chargement rejoue la mission depuis le début, sans l'afficher, jusqu'au moment de la sauvegarde. Tout
// (feux, fumée, portes cassées, civils, scripts, objectifs) revient donc exactement comme il était.
public sealed partial class OpenFDGame
{
    private List<JournalEntry> _journal = new();
    private bool _replaying;
    private string _gameNationality = "";
    private string? _loadNat;
    private List<(string path, SavedGame game)>? _saves;
    private int _loadIndex;
    private int _loadFrom;   // écran où revenir (0 menu principal, 3 pause)

    private static string SavesDir => Path.Combine(Settings.SaveDir, "FD3_CSharp_parties");

    /// <summary>Note un ordre du joueur (pas pendant un rechargement).</summary>
    private void Rec(string op, long a = 0, long b = 0, float x = 0, float y = 0, string? s = null)
    {
        _session.Units.PlayerCommand = !_replaying;   // ordre du joueur : réponse vocale permise (remis à faux après l'ordre, voir Update)
        if (_replaying || _session == null)
            return;
        _journal.Add(new JournalEntry { Step = _session.Steps, Op = op, A = a, B = b, X = x, Y = y, S = s });
    }

    // ---- ordres notés : chaque appel du jeu passe par ici ----
    private void DoRevive(Unit who, Unit victim)
    {
        if (NetSubmit("revive", who.Id, victim.Id))
            return;
        Rec("revive", who.Id, victim.Id);
        _session.Units.ReviveColleague(who, victim);
    }
    private void DoFetch(Unit who, Civilian c)
    {
        if (NetSubmit("fetch", who.Id, c.Id))
            return;
        Rec("fetch", who.Id, c.Id);
        _session.Units.FetchCivilian(who, c);
    }
    private void DoBoard(Unit u, Unit veh)
    {
        if (NetSubmit("board", u.Id, veh.Id))
            return;
        Rec("board", u.Id, veh.Id);
        _session.Units.BoardVehicle(u, veh);
    }
    private void DoGoToVehicle(Unit u, Unit veh)
    {
        if (NetSubmit("goveh", u.Id, veh.Id))
            return;
        Rec("goveh", u.Id, veh.Id);
        _session.Units.GoToVehicle(u, veh);
    }
    private void DoUse(Unit u, UsableObject o)
    {
        if (NetSubmit("use", u.Id, o.Id))
            return;
        Rec("use", u.Id, o.Id);
        _session.Units.UseObject(u, o);
    }
    private void DoOpening(Unit u, Opening d)
    {
        if (NetSubmit("door", u.Id, d.Id))
            return;
        Rec("door", u.Id, d.Id);
        _session.Units.WorkOnOpening(u, d);
    }
    private void DoAttack(Unit u, OpenFD.Fire.HotPoint h)
    {
        if (NetSubmit("attack", u.Id, h.Id))
            return;
        Rec("attack", u.Id, h.Id);
        _session.Units.Attack(u, h);
    }
    private bool DoOrder(Unit u, SN.Vector2 p)
    {
        if (NetSubmit("order", u.Id, 0, p.X, p.Y))
            return true;
        Rec("order", u.Id, 0, p.X, p.Y);
        return _session.Units.Order(u, p);
    }
    private void DoDrive(Unit v, SN.Vector2 p)
    {
        if (NetSubmit("drive", v.Id, 0, p.X, p.Y))
            return;
        Rec("drive", v.Id, 0, p.X, p.Y);
        _session.Units.Drive(v, p);
    }
    private int DoUnload(Unit v)
    {
        if (NetSubmit("unload", v.Id))
            return 0;
        Rec("unload", v.Id);
        return _session.Units.UnloadPassengers(v);
    }
    private void DoSpray(OpenFD.Fire.HotPoint h, string cls)
    {
        if (NetSubmit("spray", 0, h.Id, s: cls))
            return;
        Rec("spray", 0, h.Id, s: cls);
        _session.Spray(h, cls);
    }
    private void DoGrapple(Unit u, GrapplePoint gp)
    {
        if (NetSubmit("grapple", u.Id, gp.Id))
            return;
        Rec("grapple", u.Id, gp.Id);
        _session.Units.GrappleOver(u, gp);
    }
    private void DoSense(Unit u)
    {
        if (NetSubmit("sense", u.Id))
            return;
        Rec("sense", u.Id);
        _session.Units.DogSense(u);
    }
    private void DoScript(OpenFD.Script.Interpreter sc, string fn)
    {
        if (NetSubmit("script", s: fn))
            return;
        Rec("script", s: fn);
        sc.TryRun(fn);
    }

    private void Replay(JournalEntry e)
    {
        UnitManager um = _session.Units;
        Unit? U(long id) => um.ById(id);
        switch (e.Op)
        {
            case "revive":
                if (U(e.A) is { } a1 && U(e.B) is { } b1)
                    um.ReviveColleague(a1, b1); break;
            case "fetch":
                if (U(e.A) is { } a2 && um.CivilianById(e.B) is { } c2)
                    um.FetchCivilian(a2, c2); break;
            case "board":
                if (U(e.A) is { } a3 && U(e.B) is { } b3)
                    um.BoardVehicle(a3, b3); break;
            case "goveh":
                if (U(e.A) is { } a4 && U(e.B) is { } b4)
                    um.GoToVehicle(a4, b4); break;
            case "use":
                if (U(e.A) is { } a5 && um.Usables.FirstOrDefault(o => o.Id == e.B) is { } o5)
                    um.UseObject(a5, o5); break;
            case "door":
                if (U(e.A) is { } a6 && um.Openings.FirstOrDefault(o => o.Id == e.B) is { } d6)
                    um.WorkOnOpening(a6, d6); break;
            case "attack":
                if (U(e.A) is { } a7 && _session.Fire.HotPoints.FirstOrDefault(h => h.Id == e.B) is { } h7)
                    um.Attack(a7, h7); break;
            case "order":
                if (U(e.A) is { } a8)
                    um.Order(a8, new SN.Vector2(e.X, e.Y)); break;
            case "drive":
                if (U(e.A) is { } a9)
                    um.Drive(a9, new SN.Vector2(e.X, e.Y)); break;
            case "unload":
                if (U(e.A) is { } a10)
                    um.UnloadPassengers(a10); break;
            case "spray":
                if (_session.Fire.HotPoints.FirstOrDefault(h => h.Id == e.B) is { } h11 && e.S != null)
                    _session.Spray(h11, e.S); break;
            case "script":
                if (e.S != null)
                    _session.Scripts?.TryRun(e.S); break;
            case "skipcine": SkipCinematic(); break;
            case "grapple":
                if (U(e.A) is { } a12 && um.Grapples.FirstOrDefault(g => g.Id == e.B) is { } g12)
                    um.GrappleOver(a12, g12); break;
            case "sense":
                if (U(e.A) is { } a13)
                    um.DogSense(a13); break;
        }
    }

    // ---- sauvegarde ----
    private string? SaveGame()
    {
        if (_session == null)
            return null;
        SavedGame g = new SavedGame
        {
            Mission = _mission, Difficulty = _difficulty, Nationality = _gameNationality, Date = DateTime.Now,
            Steps = _session.Steps, Journal = _journal.ToList(),
            Camera = new[] { _target.X, _target.Y, _target.Z, _yaw, _pitch, _dist },
        };
        try
        {
            Directory.CreateDirectory(SavesDir);
            string path = Path.Combine(SavesDir, $"{_mission}_{g.Date:yyyy-MM-dd_HH-mm-ss}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(g, new JsonSerializerOptions { WriteIndented = false }));
            _saves = null;
            return path;
        }
        catch (Exception ex) { Console.WriteLine("Sauvegarde : " + ex.Message); return null; }
    }

    private List<(string path, SavedGame game)> Saves()
    {
        if (_saves != null)
            return _saves;
        _saves = new();
        try
        {
            if (Directory.Exists(SavesDir))
                foreach (string f in Directory.EnumerateFiles(SavesDir, "*.json"))
                    try
                    {
                        if (JsonSerializer.Deserialize<SavedGame>(File.ReadAllText(f)) is { Mission.Length: > 0 } g)
                            _saves.Add((f, g));
                    } catch { }
        }
        catch { }
        _saves.Sort((a, b) => b.game.Date.CompareTo(a.game.Date));
        return _saves;
    }

    // ---- chargement : la mission est rejouée, sans affichage, jusqu'au moment sauvegardé ----
    private void LoadGame(SavedGame g)
    {
        _inGame = false; _audio?.PauseAll(false);
        _loadNat = g.Nationality;   // les unités de la partie sauvegardée, même si l'option a changé depuis
        try { LoadMission(g.Mission, g.Difficulty); } finally { _loadNat = null; }
        _replaying = true;
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            List<JournalEntry> todo = g.Journal.OrderBy(e => e.Step).ToList();
            int i = 0;
            while (true)
            {
                while (i < todo.Count && todo[i].Step <= _session.Steps)
                    Replay(todo[i++]);
                if (_session.Steps >= g.Steps)
                    break;
                _session.Step();
                if (_session.Steps % 300 == 0)
                    Status($"Chargement de la partie... {100 * _session.Steps / Math.Max(1, g.Steps)} %", (float)_session.Steps / Math.Max(1, g.Steps));
            }
            _journal = g.Journal.ToList();
        }
        finally { _replaying = false; }
        Console.WriteLine($"Partie rechargée : {g.Steps} pas en {sw.Elapsed.TotalSeconds:0.0} s");
        // ce qui s'est passé pendant le rechargement n'est ni rejoué à l'écran ni annoncé
        if (Host is { } h)
        {
            h.Sounds2D.Clear();
            h.Messages.Clear();
            h.ObjectiveEvents.Clear();
            h.CameraOrders.Clear();
        }
        _session.Units.Sounds.Clear(); _session.Explosions.Happened.Clear();
        _unitLogSeen = _session.Units.Log.Count;
        _scriptLogSeen = _session.Scripts?.Log.Count ?? 0;
        _audio?.StopAll();
        if (g.Camera.Length == 6)
        {
            _target = new(g.Camera[0], g.Camera[1], g.Camera[2]);
            _yaw = g.Camera[3];
            _pitch = g.Camera[4];
            _dist = g.Camera[5];
        }
        _cineOn = Host?.Cinematic ?? false;
        _paused = false; _owed = 0; _menu = false; _sel.Clear();
        SnapshotPoses();
        Log($"Partie chargée ({g.Mission}, {g.Steps / 600}:{g.Steps / 10 % 60:00})");
    }

    // ---- touches rapides : F5 sauvegarde, F9 recharge la dernière sauvegarde ----
    private bool QuickSaveKeys(KeyboardState k)
    {
        if (Pressed(k, Keys.F5) && !_ended && SaveGame() != null)
            Log(L("&Save", "Sauver") + " : OK");
        if (Pressed(k, Keys.F9) && !_netGame && Saves().FirstOrDefault() is { game: { } last })
        {
            LoadGame(last);
            return true;
        }
        return false;
    }

    // ---- écran 4 : liste des sauvegardes ----
    private void OpenLoadScreen(int from) { _saves = null; _loadFrom = from; _loadIndex = 0; _screen = 4; }

    private void LoadScreenInput(KeyboardState k, MouseState m)
    {
        List<(string path, SavedGame game)> list = Saves();
        if (Pressed(k, Keys.Escape))
        {
            _screen = _loadFrom;
            return;
        }
        if (Pressed(k, Keys.Down))
            _loadIndex = Math.Min(list.Count - 1, _loadIndex + 1);
        if (Pressed(k, Keys.Up))
            _loadIndex = Math.Max(0, _loadIndex - 1);
        bool go = Pressed(k, Keys.Enter);
        (float lx, float ly, float lw, float lh, int n) = LoadLayout(list.Count);
        int first = Math.Clamp(_loadIndex - n + 1, 0, Math.Max(0, list.Count - n));
        (float mx, float my) = Fb(m.X, m.Y);
        bool click = m.LeftButton == ButtonState.Pressed && _mPrev.LeftButton == ButtonState.Released;
        for (int i = 0; i < Math.Min(n, list.Count - first); i++)
        {
            float y = ly + i * lh;
            if (mx < lx || mx > lx + lw || my < y || my > y + lh)
                continue;
            if (m.X != _mPrev.X || m.Y != _mPrev.Y || click)
                _loadIndex = first + i;
            (float cx, float cy, float cs) = DeleteCross(lx, lw, y, lh);
            if (click && mx >= cx - 4 * _scale && mx <= cx + cs + 4 * _scale && my >= cy - 4 * _scale && my <= cy + cs + 4 * _scale)
            {
                // croix rouge : la sauvegarde est effacée
                try { File.Delete(list[first + i].path); } catch { }
                _saves = null; _loadIndex = Math.Clamp(first + i, 0, Math.Max(0, list.Count - 2)); return;
            }
            if (click)
                go = true;
        }
        // « Retour » sous la liste
        float by = ly + n * lh + 10 * _scale;
        if (click && mx >= lx && mx <= lx + lw && my >= by && my <= by + lh)
        {
            _screen = _loadFrom;
            return;
        }
        if (list.Count == 0)
            return;
        _loadIndex = Math.Clamp(_loadIndex, 0, list.Count - 1);
        if (Pressed(k, Keys.Delete))
        {
            try { File.Delete(list[_loadIndex].path); } catch { }
            _saves = null; _loadIndex = Math.Max(0, _loadIndex - 1); return;
        }
        if (go)
            LoadGame(list[_loadIndex].game);
    }

    /// <summary>Croix rouge (WID_CHECK_FAIL) au bout de la ligne d'une sauvegarde : position et taille.</summary>
    private (float x, float y, float s) DeleteCross(float lx, float lw, float y, float lh)
    {
        float s = _ui.LineHeight * 1.05f;
        return (lx + lw - 12 * _scale - s, y + (lh - 4 * _scale - s) / 2, s);
    }

    private (float x, float y, float w, float lh, int n) LoadLayout(int count)
    {
        float lh = _ui.LineHeight * 1.7f + 8 * _scale, w = MathF.Min(_fbW * 0.5f, 640 * _scale);
        int n = Math.Clamp((int)((_fbH * 0.55f) / lh), 3, 12);
        n = Math.Min(n, Math.Max(1, count));
        return ((_fbW - w) / 2, MathF.Max(_fbH * 0.25f, (_fbH - (n + 1) * lh) / 2), w, lh, n);
    }

    private void RenderLoadScreen()
    {
        List<(string path, SavedGame game)> list = Saves();
        if (!_inGame)
            GraphicsDevice.Clear(new Color(0.05f, 0.05f, 0.06f));
        _ui.Begin();
        if (_inGame)
            _ui.Rect(0, 0, _fbW, _fbH, new SN.Vector4(0, 0, 0, 0.35f));
        else
            DrawMenuBack();
        (float lx, float ly, float lw, float lh, int n) = LoadLayout(list.Count);
        SN.Vector4 accent = new SN.Vector4(1f, 0.48f, 0.18f, 1); SN.Vector4 txt = new SN.Vector4(0.93f, 0.92f, 0.9f, 1); SN.Vector4 muted = new SN.Vector4(0.6f, 0.6f, 0.6f, 1);
        string title = L("&LoadSavedGame", "Charger une sauvegarde");
        float ts = _scale * 2f;
        _ui.Rect(0, ly - lh * 2.4f, _fbW, _font.LineHeight * ts + 16 * _scale, new SN.Vector4(0, 0, 0, 0.55f));
        _ui.Text((_fbW - _font.Measure(title) * ts) / 2, ly - lh * 2.4f + 8 * _scale, title, accent, ts);
        _ui.Rect(lx - 12 * _scale, ly - 8 * _scale, lw + 24 * _scale, (n + 1) * lh + 26 * _scale, new SN.Vector4(0.07f, 0.07f, 0.09f, 0.82f));
        if (list.Count == 0)
        {
            string none = O("NoSave");
            _ui.Text(lx + (lw - _ui.Measure(none)) / 2, ly + (lh - _ui.LineHeight) / 2, none, muted);
        }
        int first = Math.Clamp(_loadIndex - n + 1, 0, Math.Max(0, list.Count - n));
        for (int i = 0; i < Math.Min(n, list.Count - first); i++)
        {
            (_, SavedGame? g) = list[first + i]; float y = ly + i * lh, ty = y + (lh - _ui.LineHeight) / 2 - 2 * _scale;
            if (first + i == _loadIndex)
                _ui.Rect(lx, y, lw, lh - 4 * _scale, new SN.Vector4(0.85f, 0.35f, 0.1f, 0.55f));
            string left = $"{g.Mission}", right = $"{g.Steps / 600}:{g.Steps / 10 % 60:00}   {g.Date:dd/MM/yyyy HH:mm}";
            _ui.Text(lx + 14 * _scale, ty, left, txt);
            (float cx, float cy, float cs) = DeleteCross(lx, lw, y, lh);
            _ui.Text(cx - 14 * _scale - _ui.Measure(right), ty, right, muted);
            if (OverlayTexture("WID_CHECK_FAIL.png", cropFrame: true) is { } cross)
                _ui.Image(cross, cx, cy, cs, cs, SN.Vector4.One);
            else
                _ui.Text(cx + (cs - _ui.Measure("X")) / 2, ty, "X", new SN.Vector4(1f, 0.3f, 0.25f, 1));
        }
        float by = ly + n * lh + 10 * _scale; string back = L("&Back", "Retour");
        _ui.Text(lx + (lw - _ui.Measure(back)) / 2, by + (lh - _ui.LineHeight) / 2, back, txt);
        string help = O("HelpLoad");
        _ui.Rect((_fbW - MathF.Max(lw, _ui.Measure(help) + 32 * _scale)) / 2, by + lh + 14 * _scale, MathF.Max(lw, _ui.Measure(help) + 32 * _scale), _ui.LineHeight * 1.6f, new SN.Vector4(0, 0, 0, 0.7f));
        _ui.Text((_fbW - _ui.Measure(help)) / 2, by + lh + 22 * _scale, help, new SN.Vector4(0.65f, 0.65f, 0.65f, 1));
        _ui.End();
    }

    /// <summary>Empreinte de l'état de la partie (essai : la partie rechargée doit être identique).</summary>
    private string StateHash()
    {
        double a = _session.Time, b = 0, c = 0, d = 0;
        foreach (Unit u in _session.Units.Units)
        {
            a += u.Position.X * 0.001 + u.Position.Y * 0.0007 + u.Life + u.Stamina;
            b += u.Nozzle?.Reserve ?? 0;
        }
        foreach (Fire.HotPoint h in _session.Fire.HotPoints)
            c += h.Intensity + (h.Lit ? 1 : 0);
        foreach (Civilian cv in _session.Units.Civilians)
            d += cv.Position.X * 0.001 + cv.Position.Y * 0.0007 + (int)cv.State;
        int open = _session.Units.Openings.Count(o => o.State != OpeningState.Closed);
        return $"t={_session.Time:0.0} unités={a:0.0000} eau={b:0.00} feu={c:0.0000} civils={d:0.0000} ouvertures={open} fumée={_session.Smoke.Total:0.00}";
    }

    /// <summary>Essai FD3_SAUVE_TEST : ordres à différents moments, sauvegarde, rechargement, comparaison.</summary>
    private void SaveSelfTest()
    {
        UnitManager um = _session.Units;
        void Run(int n) { for (int i = 0; i < n; i++) _session.Step(); }
        SkipCinematic();
        Run(50);
        List<Unit> men = um.Active.Where(u => !u.IsVehicle).ToList();
        Fire.HotPoint? fire = _session.Fire.HotPoints.FirstOrDefault(h => h.IsBurning);
        if (men.Count > 0 && fire != null)
            DoAttack(men[0], fire);
        if (men.Count > 1)
            DoOrder(men[1], new SN.Vector2(men[1].Position.X + 600, men[1].Position.Y + 300));
        Run(200);
        foreach (Opening d in um.Openings.Where(o => !o.IsWindow).Take(3))
            if (men.Count > 2)
                DoOpening(men[2], d);
        Civilian? civ = um.Civilians.FirstOrDefault(c => !c.Hidden);
        if (civ != null && men.Count > 3)
            DoFetch(men[3], civ);
        Run(int.TryParse(Environment.GetEnvironmentVariable("FD3_SAUVE_TEST"), out int extra) && extra > 1 ? extra : 400);
        string before = StateHash();
        string? path = SaveGame();
        Run(100);   // la partie continue un peu avant le rechargement
        SavedGame g = JsonSerializer.Deserialize<SavedGame>(File.ReadAllText(path!))!;
        LoadGame(g);
        string after = StateHash();
        Console.WriteLine($"SAUVE avant   : {before}");
        Console.WriteLine($"SAUVE recharge: {after}");
        Console.WriteLine(before == after ? "SAUVE identique" : "SAUVE DIFFERENT");
        Console.WriteLine($"journal {g.Journal.Count} ordres, {g.Steps} pas, fichier {path}");
    }
}
