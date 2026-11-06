using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using OpenFD.Game;
using OpenFD.Multiplayer;
using SN = System.Numerics;

namespace OpenFD.MG;

// Multijoueur (voir OpenFD.Multiplayer) : écrans du menu (6 : liste des serveurs et pseudo, 7 : serveur, 8 : client)
// et partie en réseau. La simulation est la même chez tous : seuls les ordres des joueurs voyagent, chacun appliqué
// au même pas de simulation partout (le serveur donne ce pas). Chaque joueur garde sa caméra et sa sélection.
public sealed partial class OpenFDGame
{
    private NetSession? _net;
    private bool _netGame;                       // une partie en réseau est en cours (briefing compris)
    private string? _netNote;                    // message affiché dans les écrans multijoueur
    private string _lanIp = "", _wanIp = "";
    private Task<string?>? _wanTask;
    private int _serverIndex = -1;               // serveur choisi dans la liste
    private bool _ipDialog;                      // fenêtre « Rejoindre IP »
    private string _ipText = "";
    private int _textFocus;                      // 0 aucun, 1 pseudo, 2 adresse IP
    private int _netMissionIndex;
    private bool _myReady;
    private double _lastHashStep = -1;
    private int _netLastProgress;

    /// <summary>Serveurs connus (la liste viendra plus tard d'un serveur Internet).</summary>
    private readonly List<(string name, string address, string mission, int players)> _serverList = new();

    private string PlayerName
    {
        get => string.IsNullOrWhiteSpace(_settings.PlayerName) ? L("&DefaultAlias", "Pompier") : _settings.PlayerName;
        set
        {
            _settings.PlayerName = value;
            _settings.Save();
        }
    }

    // ------------------------------------------------------------------ ordres des joueurs
    /// <summary>
    /// En partie réseau, un ordre du joueur ne s'applique pas tout de suite : il part au serveur, qui le renvoie à
    /// tous avec le pas où l'appliquer. true si l'ordre est parti (l'appelant ne l'applique pas lui-même).
    /// </summary>
    private bool NetSubmit(string op, long a = 0, long b = 0, float x = 0, float y = 0, string? s = null)
    {
        if (!_netGame || _net == null || _replaying || _net.State != NetState.Playing)
            return false;
        _net.Submit(new NetCommand { Op = op, A = a, B = b, X = x, Y = y, S = s });
        return true;
    }

    private void ApplyNet(NetCommand c)
    {
        JournalEntry e = new JournalEntry { Step = _session.Steps, Op = c.Op, A = c.A, B = c.B, X = c.X, Y = c.Y, S = c.S };
        _journal.Add(e);
        // les réponses vocales (« impossible d'y accéder »…) seulement pour le joueur qui a donné l'ordre
        _session.Units.PlayerCommand = c.Player == _net!.MyId;
        bool was = _replaying;
        _replaying = true;
        // ordre de ce joueur : la voix « reçu » s'il est accepté (comme en solo, mais quand il s'applique)
        Unit? who = c.Player == _net.MyId && c.Op != "spray" && c.Op != "script" && c.Op != "skipcine" ? _session.Units.ById(c.A) : null;
        (string, SN.Vector2?, object?, object?, object?, object?, object?)? before = who != null ? Feedback(who) : null;
        try
        {
            Replay(e);
            if (who != null && before is { } b && !b.Equals(Feedback(who)) && !RefusedStatus(who.Status))
                _netAck = true;
        }
        finally
        {
            _replaying = was;
            _session.Units.PlayerCommand = false;
        }
    }

    private bool _netAck;   // un ordre de ce joueur vient d'être accepté : voix « reçu » (une seule fois par image)

    private static (string, SN.Vector2?, object?, object?, object?, object?, object?) Feedback(Unit u) =>
        (u.Status, u.Path.Count > 0 ? u.Path.Last() : null, u.Target, u.Opening, u.GoTo, u.GoToVehicle, u.UseTarget);

    /// <summary>Pause et vitesse : à tout le monde seul, au serveur seulement en réseau.</summary>
    private bool NetCanControlTime => !_netGame || _net is { IsServer: true };

    // ------------------------------------------------------------------ simulation en réseau
    /// <summary>
    /// Serveur : avance à sa vitesse (pause et vitesse sont à lui), en attendant les joueurs qui chargent encore ou qui
    /// ont pris plus de 6 s de retard. Client : avance au même rythme, rattrape s'il est en retard, et ne dépasse
    /// jamais le pas jusqu'où le serveur a annoncé tous les ordres.
    /// </summary>
    private void SimulateNet(float dt)
    {
        NetSession n = _net!;
        int before = _session.Fire.ExtinguishLog.Count;
        int steps;
        if (n.IsServer)
        {
            n.CurrentStep = _session.Steps;
            bool wait = !n.AllLoaded || n.SlowestStep < _session.Steps - 60;
            if (_paused || wait)
            {
                _owed = 0;
                // tant qu'un joueur charge encore, personne ne commence : rien n'est annoncé
                if (n.AllLoaded)
                    n.AnnounceAllowed();
                return;
            }
            _owed += dt * _speed / Session.Dt;
            steps = (int)Math.Min(_owed, 400);
            _owed -= steps;
        }
        else
        {
            int avail = n.AllowedStep - _session.Steps + 1;
            if (avail <= 0)
            {
                _owed = 0;
                return;
            }
            _owed = Math.Min(_owed + dt / Session.Dt, 3);
            steps = (int)_owed;
            // en retard (serveur en accéléré, image lente) : on rattrape
            if (avail > 2 * NetProtocol.InputDelay + 2)
                steps = Math.Max(steps, avail - NetProtocol.InputDelay);
            steps = Math.Min(steps, avail);
            _owed = Math.Max(0, _owed - steps);
        }
        for (int i = 0; i < steps; i++)
        {
            if (!n.IsServer && !n.CanStep(_session.Steps))
                break;
            if (_session.Steps >= NetTestStop)
                break;   // essais : arrêt à un pas précis
            if (i == steps - 1)
                SnapshotPoses();
            NetStep();
            if (n.IsServer)
                n.CurrentStep = _session.Steps;
        }
        if (_netAck)
        {
            _netAck = false;
            Acknowledge();
            if (Environment.GetEnvironmentVariable("FD3_NET_HOST") != null || Environment.GetEnvironmentVariable("FD3_NET_JOIN") != null)
                Console.WriteLine($"ESSAI réseau : voix « reçu » au pas {_session.Steps}");
        }
        if (n.IsServer)
            n.AnnounceAllowed();
        else if (_session.Steps - _netLastProgress >= 10)
        {
            _netLastProgress = _session.Steps;
            n.ReportProgress(_session.Steps);
        }
        foreach (string l in _session.Fire.ExtinguishLog.Skip(before))
            Log("Éteint : " + l.Trim());
        PullLogs();
    }

    // un pas : les ordres de ce pas, l'empreinte de contrôle, puis la simulation
    private void NetStep()
    {
        int s = _session.Steps;
        foreach (NetCommand c in _net!.TakeDue(s))
            ApplyNet(c);
        s = _session.Steps;   // passer une cinématique fait avancer la simulation
        if (s > 0 && s % NetProtocol.HashEvery == 0 && s != _lastHashStep)
        {
            _lastHashStep = s;
            _net.ReportHash(s, Fnv(StateHash()));
        }
        _session.Step();
    }

    private static ulong Fnv(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h;
    }

    // ------------------------------------------------------------------ réseau : à chaque image
    private void NetUpdate()
    {
        if (_net == null)
            return;
        _net.Update();
        foreach (NetEvent e in _net.Events.ToList())
            HandleNetEvent(e);
        _net.Events.Clear();
        if (_net?.State == NetState.Closed)
        {
            _net.Dispose();
            _net = null;
        }
    }

    private void HandleNetEvent(NetEvent e)
    {
        NetSession n = _net!;
        switch (e.Kind)
        {
            case NetEventKind.Joined:
                _netNote = null;
                _ipDialog = false;
                _myReady = false;
                _menu = true;
                _screen = 8;
                break;
            case NetEventKind.PlayerJoined:
            {
                string joined = L("&PlayerJoin", "a rejoint la partie");
                _netNote = joined.Contains("%s") ? joined.Replace("%s", e.Text) : e.Text + " : " + joined;
            }
                break;
            case NetEventKind.PlayerLeft:
                _netNote = e.Text;
                if (_netGame)
                    Log(e.Text);
                break;
            case NetEventKind.StartGame:
                ApplyNetSettings();
                StartBriefing(n.Mission, n.Difficulty, BriefingOf(n.Mission));
                break;
            case NetEventKind.BeginMission:
                if (_briefing != null)
                {
                    _briefing.Dispose();
                    _briefing = null;
                    _audio?.StopAll();
                }
                _briefWaiting = false;
                n.MissionLoaded();   // mission chargée avant le briefing : le serveur peut compter sur ce joueur
                break;
            case NetEventKind.Desync:
                Console.WriteLine($"Réseau : {e.Text} n'a plus le même état au pas {e.Step} : la partie lui est renvoyée");
                Log($"Synchronisation de {e.Text}…");
                n.Resync(e.Player, _journal.Select(j => new NetCommand { Step = j.Step, Op = j.Op, A = j.A, B = j.B, X = j.X, Y = j.Y, S = j.S, Player = 0 }));
                break;
            case NetEventKind.ResyncBegin:
                Log("Synchronisation avec le serveur…");
                break;
            case NetEventKind.ResyncEnd:
                NetResync(e.Step);
                break;
            case NetEventKind.BackToLobby:
                if (_netGame)
                    EndNetGame();
                _menu = true;
                _screen = 8;
                _myReady = false;
                _netNote = "Le serveur a terminé la partie";
                break;
            case NetEventKind.Lost:
            {
                string why = e.Text.StartsWith('&') ? L(e.Text, e.Text.TrimStart('&')) : e.Text;
                if (_netGame)
                    EndNetGame();
                _briefWaiting = false;
                if (_briefing != null)
                {
                    _briefing.Dispose();
                    _briefing = null;
                }
                OpenMenu();
                _screen = 6;
                _netNote = why;
                break;
            }
        }
    }

    // réglages de la partie : ceux du serveur (nationalité des unités, réglages de jeu qui changent la simulation)
    private void ApplyNetSettings()
    {
        if (_net == null)
            return;
        _loadNat = _net.Nationality;
        try
        {
            if (!string.IsNullOrEmpty(_net.Settings) && JsonSerializer.Deserialize<GameplaySettings>(_net.Settings) is { } gp)
                ApplyGameplay(gp);
        }
        catch
        {
        }
    }

    /// <summary>La mission se charge chez ce joueur (avant le briefing) ; elle commence quand le serveur la lance.</summary>
    private void StartNetMission()
    {
        NetSession n = _net!;
        ApplyNetSettings();
        _netGame = true;
        _journal = new();
        _lastHashStep = -1;
        _netLastProgress = 0;
        _menu = false;
        _ended = false;
        _menuNote = null;
        try
        {
            LoadMission(n.Mission, n.Difficulty);
        }
        finally
        {
            _loadNat = null;
        }
        _paused = false;
        Log(n.IsServer ? "Partie en réseau : en attente des autres joueurs…" : "Partie en réseau : " + n.ServerName);
    }

    // client désynchronisé : la mission est rejouée depuis le début avec tous les ordres reçus, jusqu'au pas du serveur
    private void NetResync(int step)
    {
        NetSession n = _net!;
        ApplyNetSettings();
        List<NetCommand> all = n.ResyncCommands.ToList();
        SavedGame g = new SavedGame
        {
            Mission = n.Mission, Difficulty = n.Difficulty, Nationality = n.Nationality, Steps = step,
            Journal = all.Where(c => c.Step <= step).Select(c => new JournalEntry { Step = c.Step, Op = c.Op, A = c.A, B = c.B, X = c.X, Y = c.Y, S = c.S }).ToList(),
            Camera = new[] { _target.X, _target.Y, _target.Z, _yaw, _pitch, _dist },
        };
        bool menu = _menu;
        LoadGame(g);
        _menu = menu;
        n.AddPending(all.Where(c => c.Step > step));
        _netGame = true;
        Log("Synchronisé avec le serveur");
    }

    /// <summary>Le joueur quitte la partie : le serveur ramène tout le monde au salon, un client se déconnecte.</summary>
    private void LeaveNetGame()
    {
        NetSession? n = _net;
        EndNetGame();
        _audio?.PauseAll(false);
        OpenMenu();
        if (n is { IsServer: true })
        {
            n.BackToLobby();
            _screen = 7;
        }
        else
        {
            LeaveNetwork();
            _screen = 6;
        }
    }

    /// <summary>Fin de la partie en réseau chez ce joueur : ses propres réglages reviennent.</summary>
    private void EndNetGame()
    {
        _netGame = false;
        _inGame = false;
        _ended = false;
        _audio?.StopAll();
        ApplySettings();
    }

    private string? BriefingOf(string dir)
    {
        EnsureCampaign();
        return _campaign.FirstOrDefault(p => p.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase))?.Briefing;
    }

    // ------------------------------------------------------------------ écrans
    private void OpenMultiplayer()
    {
        _screen = 6;
        _ipDialog = false;
        _textFocus = 0;
        _netNote = null;
    }

    private string MissionTitle(string dir)
    {
        EnsureCampaign();
        if (_campaign.FirstOrDefault(p => p.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase)) is { } page)
            return $"{page.Number}. {Loc(page.NameKey)}";
        EnsureMissionList();
        return _missions.FirstOrDefault(m => m.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase))?.Title ?? dir;
    }

    // missions proposées au serveur : la campagne dans l'ordre, puis les autres (installées)
    private List<string> NetMissions()
    {
        EnsureCampaign();
        EnsureMissionList();
        List<string> list = _campaign.Select(p => p.Dir).ToList();
        list.AddRange(_missions.Where(m => m.Playable).Select(m => m.Dir));
        return list.Where(d => _files.List("Gfx\\Mission\\" + d + "\\").Any()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void HostServer()
    {
        try
        {
            _net = NetSession.Host(PlayerName);
        }
        catch (Exception ex)
        {
            _netNote = $"Port UDP {NetProtocol.Port} indisponible : {ex.Message}";
            return;
        }
        List<string> missions = NetMissions();
        _netMissionIndex = Math.Clamp(_netMissionIndex, 0, Math.Max(0, missions.Count - 1));
        _net.Mission = missions.Count > 0 ? missions[_netMissionIndex] : "";
        _net.Difficulty = Diffs[_menuDiff];
        _lanIp = NetAddresses.Lan();
        _wanIp = "…";
        _wanTask = NetAddresses.WanAsync();
        _netNote = null;
        _screen = 7;
    }

    private void JoinServer(string address)
    {
        _net?.Dispose();
        try
        {
            _net = NetSession.Join(address, PlayerName);
            _netNote = "Connexion à " + address + "…";
        }
        catch (Exception ex)
        {
            _net = null;
            _netNote = "Adresse invalide : " + ex.Message;
        }
    }

    private void LeaveNetwork()
    {
        _net?.Dispose();
        _net = null;
        _myReady = false;
    }

    // ---- saisie de texte (pseudo, adresse IP) ----
    private bool NetText(char ch)
    {
        if (!_menu || _textFocus == 0)
            return false;
        string t = _textFocus == 1 ? _settings.PlayerName : _ipText;
        if (ch == '\b')
        {
            if (t.Length > 0)
                t = t[..^1];
        }
        else if (ch == '\r' || ch == '\n' || ch == '\t')
        {
            if (_textFocus == 2 && ch != '\t' && _ipText.Trim().Length > 0)
                JoinServer(_ipText);
            _textFocus = 0;
        }
        else if (!char.IsControl(ch) && t.Length < (_textFocus == 1 ? 20 : 60))
            t += ch;
        if (_textFocus == 1)
            PlayerName = t;
        else if (_textFocus == 2)
            _ipText = t;
        return true;
    }

    // ---- disposition commune ----
    private float NetMargin => 28 * _scale;
    private float BtnH => _ui.LineHeight * 1.6f + 8 * _scale;
    private (float x, float y, float w, float h) BottomRight(int slot, float w) => (_fbW - NetMargin - (slot + 1) * w - slot * 12 * _scale, _fbH - NetMargin - BtnH, w, BtnH);
    private (float x, float y, float w, float h) BottomLeft(float w) => (NetMargin, _fbH - NetMargin - BtnH, w, BtnH);

    private bool Clicked(MouseState m, (float x, float y, float w, float h) r)
    {
        (float mx, float my) = Fb(m.X, m.Y);
        return m.LeftButton == ButtonState.Pressed && _mPrev.LeftButton == ButtonState.Released && Inside(r, mx, my);
    }

    private void Button((float x, float y, float w, float h) r, string label, bool enabled = true, bool primary = false)
    {
        (float mx, float my) = Fb(Mouse.GetState().X, Mouse.GetState().Y);
        bool hover = enabled && Inside(r, mx, my);
        SN.Vector4 bg = !enabled ? new SN.Vector4(0.12f, 0.12f, 0.14f, 0.7f)
            : primary ? (hover ? new SN.Vector4(0.95f, 0.45f, 0.15f, 1) : new SN.Vector4(0.85f, 0.35f, 0.1f, 0.95f))
            : hover ? new SN.Vector4(0.24f, 0.24f, 0.28f, 0.95f) : new SN.Vector4(0.12f, 0.12f, 0.15f, 0.92f);
        _ui.Rect(r.x, r.y, r.w, r.h, bg);
        _ui.Frame(r.x, r.y, r.w, r.h, new SN.Vector4(1, 1, 1, enabled ? 0.25f : 0.1f), MathF.Max(1, _scale));
        SN.Vector4 tc = enabled ? new SN.Vector4(0.95f, 0.94f, 0.92f, 1) : new SN.Vector4(0.5f, 0.5f, 0.5f, 1);
        _ui.Text(r.x + (r.w - _ui.Measure(label)) / 2, r.y + (r.h - _ui.LineHeight) / 2, label, tc);
    }

    private void CheckBox(float x, float y, float s, bool on)
    {
        _ui.Rect(x, y, s, s, new SN.Vector4(0.05f, 0.05f, 0.07f, 0.9f));
        _ui.Frame(x, y, s, s, new SN.Vector4(0.8f, 0.8f, 0.8f, 1), MathF.Max(1, _scale));
        if (on)
        {
            float p = s * 0.22f;
            _ui.Line(x + p, y + s * 0.55f, x + s * 0.42f, y + s - p, new SN.Vector4(0.35f, 0.95f, 0.4f, 1), 3 * _scale);
            _ui.Line(x + s * 0.42f, y + s - p, x + s - p, y + p, new SN.Vector4(0.35f, 0.95f, 0.4f, 1), 3 * _scale);
        }
    }

    private void TextField((float x, float y, float w, float h) r, string text, bool focus)
    {
        _ui.Rect(r.x, r.y, r.w, r.h, new SN.Vector4(0.04f, 0.04f, 0.05f, 0.95f));
        _ui.Frame(r.x, r.y, r.w, r.h, focus ? new SN.Vector4(1f, 0.55f, 0.2f, 1) : new SN.Vector4(0.5f, 0.5f, 0.55f, 1), MathF.Max(1, 2 * _scale));
        bool caret = focus && (int)(_clockUi * 2) % 2 == 0;
        _ui.Text(r.x + 10 * _scale, r.y + (r.h - _ui.LineHeight) / 2, text + (caret ? "_" : ""), new SN.Vector4(0.95f, 0.94f, 0.92f, 1));
    }
    private double _clockUi => Environment.TickCount64 / 1000.0;

    private void NetBackground()
    {
        GraphicsDevice.Clear(new Color(0.05f, 0.05f, 0.06f));
        _ui.Begin();
        DrawMenuBack();
        _ui.Rect(0, 0, _fbW, _fbH, new SN.Vector4(0, 0, 0, 0.45f));
    }

    private void NetTitle(string title)
    {
        float ts = _scale * 1.6f;
        _ui.Text((_fbW - _font.Measure(title) * ts) / 2, NetMargin, title, new SN.Vector4(1f, 0.48f, 0.18f, 1), ts);
    }

    private void NetNote()
    {
        if (_netNote == null)
            return;
        float y = _fbH - NetMargin - BtnH - _ui.LineHeight - 14 * _scale;
        _ui.Text((_fbW - _ui.Measure(_netNote)) / 2, y, _netNote, new SN.Vector4(1f, 0.75f, 0.35f, 1));
    }

    // ------------------------------------------------------------------ écran 6 : pseudo, liste des serveurs
    private (float x, float y, float w, float h) NameField() => (_fbW / 2f - 80 * _scale, NetMargin + 70 * _scale, 360 * _scale, BtnH);
    private (float x, float y, float w, float h) ServerListRect() => (NetMargin * 2, NetMargin + 70 * _scale + BtnH + 30 * _scale, _fbW - NetMargin * 4, _fbH - (NetMargin + 70 * _scale + BtnH + 30 * _scale) - NetMargin * 2 - BtnH * 2);
    private (float x, float y, float w, float h) IpDialog() => (_fbW / 2f - 260 * _scale, _fbH / 2f - 90 * _scale, 520 * _scale, 180 * _scale);

    private void NetHomeInput(KeyboardState k, MouseState m)
    {
        float bw = 210 * _scale;
        if (_ipDialog)
        {
            (float dx, float dy, float dw, float dh) = IpDialog();
            (float x, float y, float w, float h) field = (dx + 20 * _scale, dy + 60 * _scale, dw - 40 * _scale, BtnH);
            (float x, float y, float w, float h) ok = (dx + dw - 20 * _scale - 180 * _scale, dy + dh - 20 * _scale - BtnH, 180 * _scale, BtnH);
            (float x, float y, float w, float h) cancel = (dx + 20 * _scale, dy + dh - 20 * _scale - BtnH, 180 * _scale, BtnH);
            if (Pressed(k, Keys.Escape) || Clicked(m, cancel))
            {
                _ipDialog = false;
                _textFocus = 0;
                LeaveNetwork();
                _netNote = null;
                return;
            }
            if (Clicked(m, field))
                _textFocus = 2;
            if (Clicked(m, ok) && _ipText.Trim().Length > 0)
                JoinServer(_ipText);
            return;
        }
        if (Pressed(k, Keys.Escape))
        {
            _textFocus = 0;
            _screen = 0;
            return;
        }
        if (Clicked(m, NameField()))
            _textFocus = 1;
        (float lx, float ly, float lw, float lh) = ServerListRect();
        float rowH = _ui.LineHeight + 12 * _scale;
        for (int i = 0; i < _serverList.Count; i++)
            if (Clicked(m, (lx, ly + rowH * (i + 1), lw, rowH)))
                _serverIndex = i;
        if (Clicked(m, BottomLeft(bw)))
        {
            _screen = 0;
            return;
        }
        if (Clicked(m, BottomRight(2, bw)))
        {
            _ipDialog = true;
            _textFocus = 2;
            _netNote = null;
        }
        if (Clicked(m, BottomRight(1, bw)) && _serverIndex >= 0 && _serverIndex < _serverList.Count)
            JoinServer(_serverList[_serverIndex].address);
        if (Clicked(m, BottomRight(0, bw)))
            HostServer();
    }

    private void RenderNetHome()
    {
        NetBackground();
        NetTitle(L("&Multiplayer", "MULTIJOUEUR"));
        (float fx, float fy, float fw, float fh) = NameField();
        string alias = L("&Alias", "Pseudo") + " :";
        _ui.Text(fx - _ui.Measure(alias) - 14 * _scale, fy + (fh - _ui.LineHeight) / 2, alias, new SN.Vector4(0.9f, 0.9f, 0.9f, 1));
        TextField((fx, fy, fw, fh), _textFocus == 1 ? _settings.PlayerName : PlayerName, _textFocus == 1);
        // liste des serveurs
        (float lx, float ly, float lw, float lh) = ServerListRect();
        _ui.Rect(lx, ly, lw, lh, new SN.Vector4(0.06f, 0.06f, 0.08f, 0.88f));
        float rowH = _ui.LineHeight + 12 * _scale;
        SN.Vector4 head = new SN.Vector4(0.6f, 0.6f, 0.62f, 1), txt = new SN.Vector4(0.93f, 0.92f, 0.9f, 1);
        float[] cols = { 0, 0.4f, 0.75f, 0.88f };
        string[] heads = { L("&GameDesc", "Partie"), L("&MissionName", "Mission"), L("&NumPlayers", "Joueurs"), L("&Ping", "Ping") };
        for (int c = 0; c < 4; c++)
            _ui.Text(lx + 14 * _scale + cols[c] * lw, ly + 6 * _scale, heads[c], head);
        _ui.Rect(lx, ly + rowH, lw, MathF.Max(1, _scale), new SN.Vector4(1, 1, 1, 0.15f));
        if (_serverList.Count == 0)
        {
            const string none = "Aucun serveur pour l'instant (la liste viendra plus tard d'Internet) : utilisez « Rejoindre IP ».";
            _ui.Text(lx + (lw - _ui.Measure(none)) / 2, ly + lh / 2 - _ui.LineHeight / 2, none, head);
        }
        for (int i = 0; i < _serverList.Count; i++)
        {
            float y = ly + rowH * (i + 1);
            if (i == _serverIndex)
                _ui.Rect(lx, y, lw, rowH, new SN.Vector4(0.85f, 0.35f, 0.1f, 0.5f));
            (string name, string address, string mission, int players) s = _serverList[i];
            string[] cells = { s.name, MissionTitle(s.mission), s.players.ToString(), "" };
            for (int c = 0; c < 4; c++)
                _ui.Text(lx + 14 * _scale + cols[c] * lw, y + 6 * _scale, cells[c], txt);
        }
        float bw = 210 * _scale;
        Button(BottomLeft(bw), L("&Back", "Retour"));
        Button(BottomRight(2, bw), L("&JoinIP", "Rejoindre IP"));
        Button(BottomRight(1, bw), L("&Join", "Rejoindre"), _serverIndex >= 0 && _serverIndex < _serverList.Count);
        Button(BottomRight(0, bw), L("&Create", "Créer"), true, true);
        if (_ipDialog)
        {
            _ui.Rect(0, 0, _fbW, _fbH, new SN.Vector4(0, 0, 0, 0.5f));
            (float dx, float dy, float dw, float dh) = IpDialog();
            _ui.Rect(dx, dy, dw, dh, new SN.Vector4(0.09f, 0.09f, 0.11f, 0.98f));
            _ui.Frame(dx, dy, dw, dh, new SN.Vector4(1f, 0.48f, 0.18f, 1), MathF.Max(1, 2 * _scale));
            _ui.Text(dx + 20 * _scale, dy + 18 * _scale, L("&IPAddress", "Adresse IP :") + $"  (port UDP {NetProtocol.Port})", txt);
            TextField((dx + 20 * _scale, dy + 60 * _scale, dw - 40 * _scale, BtnH), _ipText, _textFocus == 2);
            Button((dx + 20 * _scale, dy + dh - 20 * _scale - BtnH, 180 * _scale, BtnH), L("&Back", "Retour"));
            Button((dx + dw - 20 * _scale - 180 * _scale, dy + dh - 20 * _scale - BtnH, 180 * _scale, BtnH), L("&Join", "Rejoindre"), _ipText.Trim().Length > 0 && _net == null, true);
        }
        NetNote();
        _ui.End();
    }

    // ------------------------------------------------------------------ écran 7 : serveur
    private (float x, float y, float w, float h) HostPlayersRect() => (NetMargin * 2, NetMargin + 120 * _scale, _fbW / 2f - NetMargin * 3, _fbH - NetMargin * 3 - BtnH * 2 - 120 * _scale);
    private (float x, float y, float w, float h) HostMissionsRect() => (_fbW / 2f + NetMargin, NetMargin + 120 * _scale + BtnH + 12 * _scale, _fbW / 2f - NetMargin * 3, _fbH - NetMargin * 3 - BtnH * 3 - 132 * _scale);

    private void NetHostInput(KeyboardState k, MouseState m)
    {
        NetSession? n = _net;
        float bw = 230 * _scale;
        if (n == null)
        {
            _screen = 6;
            return;
        }
        if (Pressed(k, Keys.Escape) || Clicked(m, BottomLeft(bw)))
        {
            LeaveNetwork();
            _screen = 6;
            return;
        }
        List<string> missions = NetMissions();
        (float mx, float my, float mw, float mh) = HostMissionsRect();
        float rowH = _ui.LineHeight + 8 * _scale;
        int visible = Math.Max(1, (int)(mh / rowH));
        int first = Math.Clamp(_netMissionIndex - visible / 2, 0, Math.Max(0, missions.Count - visible));
        bool changed = false;
        for (int i = 0; i < Math.Min(visible, missions.Count - first); i++)
            if (Clicked(m, (mx, my + i * rowH, mw, rowH)))
            {
                _netMissionIndex = first + i;
                changed = true;
            }
        if (Pressed(k, Keys.Down))
        {
            _netMissionIndex = Math.Min(missions.Count - 1, _netMissionIndex + 1);
            changed = true;
        }
        if (Pressed(k, Keys.Up))
        {
            _netMissionIndex = Math.Max(0, _netMissionIndex - 1);
            changed = true;
        }
        for (int i = 0; i < 3; i++)
            if (Clicked(m, DifficultyRect(i, NetMargin + 120 * _scale, mx, mw)))
            {
                _menuDiff = i;
                changed = true;
            }
        if (changed && missions.Count > 0)
        {
            n.Mission = missions[Math.Clamp(_netMissionIndex, 0, missions.Count - 1)];
            n.Difficulty = Diffs[_menuDiff];
            n.SendLobby();
        }
        bool canStart = n.AllReady && !string.IsNullOrEmpty(n.Mission);
        if (canStart && (Clicked(m, BottomRight(0, bw)) || Pressed(k, Keys.Enter)))
        {
            n.StartGame(_settings.Nationality, JsonSerializer.Serialize(_settings.Gameplay ?? new GameplaySettings()));
            StartBriefing(n.Mission, n.Difficulty, BriefingOf(n.Mission));
        }
    }

    private void RenderNetHost()
    {
        NetBackground();
        NetSession? n = _net;
        if (n == null)
        {
            _ui.End();
            return;
        }
        if (_wanTask is { IsCompleted: true })
        {
            _wanIp = _wanTask.Result ?? "inconnue";
            _wanTask = null;
        }
        SN.Vector4 accent = new SN.Vector4(1f, 0.48f, 0.18f, 1), txt = new SN.Vector4(0.93f, 0.92f, 0.9f, 1), muted = new SN.Vector4(0.62f, 0.62f, 0.64f, 1);
        float ts = _scale * 1.5f;
        _ui.Text(NetMargin, NetMargin, n.MyName, accent, ts);
        _ui.Text(NetMargin, NetMargin + _font.LineHeight * ts + 4 * _scale, $"Serveur · port UDP {NetProtocol.Port}", muted);
        string lan = $"IP locale (LAN) : {_lanIp}", wan = $"IP Internet (WAN) : {_wanIp}";
        _ui.Text(_fbW - NetMargin - _ui.Measure(lan), NetMargin, lan, txt);
        _ui.Text(_fbW - NetMargin - _ui.Measure(wan), NetMargin + _ui.LineHeight + 6 * _scale, wan, txt);
        // joueurs connectés (sauf nous)
        (float px, float py, float pw, float ph) = HostPlayersRect();
        _ui.Rect(px, py, pw, ph, new SN.Vector4(0.06f, 0.06f, 0.08f, 0.88f));
        float rowH = _ui.LineHeight + 14 * _scale;
        _ui.Text(px + 14 * _scale, py + 8 * _scale, L("&PlayerNameTable", "JOUEURS"), muted);
        string readyHead = L("&ReadyTable", "PRÊT");
        _ui.Text(px + pw - _ui.Measure(readyHead) - 14 * _scale, py + 8 * _scale, readyHead, muted);
        if (n.Players.Count == 0)
            _ui.Text(px + 14 * _scale, py + rowH + 12 * _scale, L("&WaitingPlayers", "En attente des autres joueurs..."), muted);
        for (int i = 0; i < n.Players.Count; i++)
        {
            PlayerInfo p = n.Players[i];
            float y = py + rowH * (i + 1) + 6 * _scale;
            _ui.Text(px + 14 * _scale, y + 4 * _scale, p.Name, txt);
            string ping = $"{n.PingTo(p.Id) * 1000:0} ms";
            float box = _ui.LineHeight + 4 * _scale;
            _ui.Text(px + pw - box - 40 * _scale - _ui.Measure(ping), y + 4 * _scale, ping, muted);
            CheckBox(px + pw - box - 14 * _scale, y, box, p.Ready);
        }
        // mission et difficulté
        (float mx, float my, float mw, float mh) = HostMissionsRect();
        for (int i = 0; i < 3; i++)
        {
            (float bx, float by, float bwid, float bh) = DifficultyRect(i, NetMargin + 120 * _scale, mx, mw);
            _ui.Rect(bx, by, bwid, bh, i == _menuDiff ? new SN.Vector4(0.85f, 0.35f, 0.1f, 0.95f) : new SN.Vector4(0.1f, 0.1f, 0.12f, 0.9f));
            _ui.Text(bx + (bwid - _ui.Measure(DiffNames[i])) / 2, by + (bh - _ui.LineHeight) / 2, DiffNames[i], txt);
        }
        _ui.Rect(mx, my, mw, mh, new SN.Vector4(0.06f, 0.06f, 0.08f, 0.88f));
        List<string> missions = NetMissions();
        float mrow = _ui.LineHeight + 8 * _scale;
        int visible = Math.Max(1, (int)(mh / mrow));
        int first = Math.Clamp(_netMissionIndex - visible / 2, 0, Math.Max(0, missions.Count - visible));
        for (int i = 0; i < Math.Min(visible, missions.Count - first); i++)
        {
            int idx = first + i;
            float y = my + i * mrow;
            if (idx == _netMissionIndex)
                _ui.Rect(mx, y, mw, mrow, new SN.Vector4(0.85f, 0.35f, 0.1f, 0.55f));
            _ui.Text(mx + 12 * _scale, y + 4 * _scale, MissionTitle(missions[idx]), txt);
        }
        float bw = 230 * _scale;
        Button(BottomLeft(bw), L("&Back", "Retour"));
        bool canStart = n.AllReady && !string.IsNullOrEmpty(n.Mission);
        Button(BottomRight(0, bw), L("&StartMission", "Lancer Mission"), canStart, true);
        if (!canStart && n.Players.Count > 0)
        {
            const string wait = "Tous les joueurs doivent être prêts";
            _ui.Text(_fbW - NetMargin - _ui.Measure(wait), _fbH - NetMargin - BtnH - _ui.LineHeight - 8 * _scale, wait, muted);
        }
        NetNote();
        _ui.End();
    }

    // ------------------------------------------------------------------ écran 8 : client
    private void NetClientInput(KeyboardState k, MouseState m)
    {
        NetSession? n = _net;
        float bw = 230 * _scale;
        if (n == null)
        {
            _screen = 6;
            return;
        }
        if (Pressed(k, Keys.Escape) || Clicked(m, BottomLeft(bw)))
        {
            LeaveNetwork();
            _screen = 6;
            _netNote = null;
            return;
        }
        if (Clicked(m, BottomRight(0, bw)) || Pressed(k, Keys.Space) || Pressed(k, Keys.Enter))
        {
            _myReady = !_myReady;
            n.SetReady(_myReady);
        }
    }

    private void RenderNetClient()
    {
        NetBackground();
        NetSession? n = _net;
        if (n == null)
        {
            _ui.End();
            return;
        }
        SN.Vector4 accent = new SN.Vector4(1f, 0.48f, 0.18f, 1), txt = new SN.Vector4(0.93f, 0.92f, 0.9f, 1), muted = new SN.Vector4(0.62f, 0.62f, 0.64f, 1);
        float ts = _scale * 1.5f;
        _ui.Text(_fbW - NetMargin - _font.Measure(n.MyName) * ts, NetMargin, n.MyName, accent, ts);
        // au centre : le serveur, la mission choisie
        float cy = _fbH * 0.28f;
        string server = "Serveur : " + n.ServerName;
        _ui.Text((_fbW - _font.Measure(server) * ts) / 2, cy, server, txt, ts);
        cy += _font.LineHeight * ts + 18 * _scale;
        string mission = string.IsNullOrEmpty(n.Mission) ? "Mission : (pas encore choisie)" : "Mission : " + MissionTitle(n.Mission);
        float ms = _scale * 1.3f;
        _ui.Text((_fbW - _font.Measure(mission) * ms) / 2, cy, mission, accent, ms);
        cy += _font.LineHeight * ms + 8 * _scale;
        int di = Math.Max(0, Array.IndexOf(Diffs, n.Difficulty));
        string diff = "Difficulté : " + DiffNames[di];
        _ui.Text((_fbW - _ui.Measure(diff)) / 2, cy, diff, muted);
        cy += _ui.LineHeight + 30 * _scale;
        // les autres joueurs
        float box = _ui.LineHeight + 4 * _scale, w = 420 * _scale, x0 = (_fbW - w) / 2;
        foreach (PlayerInfo p in n.Players)
        {
            _ui.Text(x0, cy + 2 * _scale, p.Name + (p.Id == 1 ? "  (serveur)" : ""), txt);
            if (p.Id != 1)
                CheckBox(x0 + w - box, cy, box, p.Ready);
            cy += box + 10 * _scale;
        }
        float bw = 230 * _scale;
        Button(BottomLeft(bw), L("&Back", "Retour"));
        (float rx, float ry, float rw, float rh) = BottomRight(0, bw);
        Button((rx, ry, rw, rh), "   " + L("&Ready", "Prêt"), true, _myReady);
        CheckBox(rx + 14 * _scale, ry + (rh - box) / 2, box, _myReady);
        string hint = L("&WaitingPlayers", "En attente des autres joueurs...") + " (le serveur lance la mission)";
        _ui.Text((_fbW - _ui.Measure(hint)) / 2, _fbH - NetMargin - BtnH - _ui.LineHeight - 40 * _scale, hint, muted);
        NetNote();
        _ui.End();
    }

    // ------------------------------------------------------------------ en partie
    /// <summary>Bandeau en haut au centre : joueurs connectés, attente.</summary>
    /// <summary>Partie en réseau pas encore commencée : un joueur charge encore la mission.</summary>
    private bool NetWaitingStart => _netGame && _net != null && _session != null && _session.Steps == 0
        && (_net.IsServer ? !_net.AllLoaded : _net.AllowedStep < 0);

    private void RenderNetHud()
    {
        if (!_netGame || _net == null || _session == null)
            return;
        if (NetWaitingStart)
        {
            // au centre de l'écran : tant que tous n'ont pas fini de charger, personne ne commence
            string wait = L("&WaitingPlayers", "En attente des joueurs...");
            float ws = _scale * 1.6f, ww = _font.Measure(wait) * ws + 60 * _scale, wh = _font.LineHeight * ws + 40 * _scale;
            _ui.Begin();
            _ui.Rect(0, 0, _fbW, _fbH, new SN.Vector4(0, 0, 0, 0.45f));
            _ui.Rect((_fbW - ww) / 2, (_fbH - wh) / 2, ww, wh, new SN.Vector4(0.06f, 0.06f, 0.08f, 0.92f));
            _ui.Frame((_fbW - ww) / 2, (_fbH - wh) / 2, ww, wh, new SN.Vector4(1f, 0.48f, 0.18f, 1), MathF.Max(1, 2 * _scale));
            _ui.Text((_fbW - _font.Measure(wait) * ws) / 2, (_fbH - _font.LineHeight * ws) / 2, wait, new SN.Vector4(0.95f, 0.94f, 0.92f, 1), ws);
            if (_net.IsServer)
            {
                string who = string.Join(", ", _net.Players.Where(p => !p.Loaded).Select(p => p.Name));
                if (who.Length > 0)
                    _ui.Text((_fbW - _ui.Measure(who)) / 2, (_fbH + wh) / 2 + 10 * _scale, who, new SN.Vector4(0.7f, 0.7f, 0.72f, 1));
            }
            _ui.End();
            return;
        }
        string s = $"Réseau : {_net.Players.Count + 1} joueur(s)";
        if (_net.IsServer && (!_net.AllLoaded || _net.SlowestStep < _session.Steps - 60))
            s += " · " + L("&WaitingPlayers", "En attente des autres joueurs...");
        else if (!_net.IsServer && _net.AllowedStep < _session.Steps)
            s += " · en attente du serveur";
        else if (_net.IsServer && _paused)
            s += " · pause";
        float w = _ui.Measure(s) + 24 * _scale;
        _ui.Begin();
        _ui.Rect((_fbW - w) / 2, 8 * _scale, w, _ui.LineHeight + 8 * _scale, new SN.Vector4(0.06f, 0.06f, 0.08f, 0.8f));
        _ui.Text((_fbW - w) / 2 + 12 * _scale, 12 * _scale, s, new SN.Vector4(0.6f, 0.85f, 1f, 1));
        _ui.End();
    }

    // ------------------------------------------------------------------ essais automatiques (deux jeux sur le même PC)
    // FD3_NET_HOST=mission (+ FD3_NET_JOUEURS=n) : crée le serveur, lance quand n joueurs sont prêts ;
    // FD3_NET_JOIN=adresse : rejoint et se met prêt. FD3_NET_FIN=pas : écrit l'empreinte de l'état à ce pas et quitte.
    private bool _netTestStarted, _netTestDone;
    private double _netTestWait, _netTestLinger;
    private static readonly int NetTestStop = int.TryParse(Environment.GetEnvironmentVariable("FD3_NET_FIN"), out int stop) ? stop : int.MaxValue;
    private readonly HashSet<string> _netTestOrders = new();
    private void NetTestHooks()
    {
        if (_frame < 4 || _netTestDone)
            return;
        string? host = Environment.GetEnvironmentVariable("FD3_NET_HOST"), join = Environment.GetEnvironmentVariable("FD3_NET_JOIN");
        if (host == null && join == null)
            return;
        if (!_netTestStarted)
        {
            _netTestStarted = true;
            _menu = true;
            if (host != null)
            {
                HostServer();
                List<string> missions = NetMissions();
                _netMissionIndex = Math.Max(0, missions.FindIndex(m => m.Equals(host, StringComparison.OrdinalIgnoreCase)));
                _net!.Mission = missions[_netMissionIndex];
                _net.SendLobby();
            }
            else
                JoinServer(join!);
            return;
        }
        if (_net == null)
            return;
        if (!_net.IsServer && _screen == 8 && !_myReady && _net.State == NetState.Lobby)
        {
            _myReady = true;
            _net.SetReady(true);
        }
        int want = int.TryParse(Environment.GetEnvironmentVariable("FD3_NET_JOUEURS"), out int nj) ? nj : 1;
        if (_net.IsServer && _net.State == NetState.Lobby && _net.Players.Count >= want && _net.AllReady)
        {
            Console.WriteLine($"ESSAI réseau : {_net.Players.Count} joueur(s) prêt(s), lancement de {_net.Mission}");
            _net.StartGame(_settings.Nationality, JsonSerializer.Serialize(_settings.Gameplay ?? new GameplaySettings()));
            StartBriefing(_net.Mission, _net.Difficulty, BriefingOf(_net.Mission));
        }
        if (_frame % 120 == 0)
            Console.WriteLine($"ESSAI réseau (suivi) : {(_net.IsServer ? "serveur" : "client")} état {_net.State}, partie {_netGame}, pas {_session?.Steps}, autorisé {_net.AllowedStep}, plus lent {_net.SlowestStep}, chargés {_net.AllLoaded}, pause {_paused}, menu {_menu}");
        if (!_netGame || _session == null || _net.State != NetState.Playing)
            return;
        int step = _session.Steps;
        List<Unit> men = _session.Units.Active.Where(u => !u.IsVehicle && !u.Unconscious).OrderBy(u => u.Id).ToList();
        void Once(string key, Action a)
        {
            if (_netTestOrders.Add(key))
            {
                Console.WriteLine($"ESSAI réseau : {key} au pas {step}");
                a();
            }
        }
        if (_net.IsServer)
        {
            if (step >= 5)
                Once("serveur passe la cinématique", () =>
                {
                    if (_cineOn)
                        NetSubmit("skipcine");
                });
            if (step >= 300 && men.Count > 0)
                Once("serveur déplace " + men[0].Name, () => DoOrder(men[0], new SN.Vector2(men[0].Position.X + 500, men[0].Position.Y + 200)));
            if (step >= 320 && _session.Fire.HotPoints.FirstOrDefault(h => h.IsBurning) is { } fire && men.Count > 1)
                Once("serveur envoie " + men[1].Name + " sur un feu", () => DoAttack(men[1], fire));
            if (_speed < 4 && step > 10)
                _speed = 4;
        }
        else
        {
            if (step >= 310 && men.Count > 2)
                Once("client déplace " + men[2].Name, () => DoOrder(men[2], new SN.Vector2(men[2].Position.X - 400, men[2].Position.Y + 300)));
            // essai de resynchronisation : le client déplace une unité sans passer par le réseau (état faussé)
            if (step >= 400 && men.Count > 3 && Environment.GetEnvironmentVariable("FD3_NET_TRICHE") != null)
                Once("client fausse son état", () => men[3].Position += new SN.Vector3(250, 0, 0));
            if (step >= 330 && men.Count > 0)
                Once("client change l'ordre de " + men[0].Name, () => DoOrder(men[0], new SN.Vector2(men[0].Position.X - 300, men[0].Position.Y - 300)));
        }
        if (int.TryParse(Environment.GetEnvironmentVariable("FD3_NET_FIN"), out int end) && step >= end)
        {
            // le serveur attend que les clients soient arrivés au même pas (au plus 60 s) avant de partir
            if (_net.IsServer && _net.SlowestStep < end - 10 && (_netTestWait += 1 / 60.0) < 60)
                return;
            if (_net.IsServer && (_netTestLinger += 1 / 60.0) < 3)
                return;
            // les deux jeux s'arrêtent au même pas exactement pour comparer
            if (step == end)
            {
                Console.WriteLine($"ESSAI réseau : {(_net.IsServer ? "serveur" : "client")} pas {step} : {StateHash()} ; journal {_journal.Count} ordres");
                foreach (Unit u in men.Take(4))
                    Console.WriteLine($"   {u.Name} ({u.Position.X:0.00},{u.Position.Y:0.00}) {u.Status}");
            }
            else
                Console.WriteLine($"ESSAI réseau : pas {step} dépassé ({end} demandé)");
            _netTestDone = true;
            LeaveNetwork();
            Exit();
        }
    }
}
