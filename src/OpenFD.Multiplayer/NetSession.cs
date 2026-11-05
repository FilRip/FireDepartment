using System.Net;
using System.Net.Sockets;

namespace OpenFD.Multiplayer;

/// <summary>
/// Session de jeu en réseau, côté serveur ou côté client.
/// <para>
/// Salon : le serveur tient la liste des joueurs (pseudo, prêt), la mission et la difficulté, et les envoie à tous.
/// </para>
/// <para>
/// Partie : la simulation est déterministe, chaque joueur la calcule lui-même. Seuls les ordres voyagent : un ordre
/// (d'un client ou du serveur) passe par le serveur, qui lui donne le pas de simulation où l'appliquer (le pas actuel
/// du serveur + <see cref="NetProtocol.InputDelay"/>) et l'envoie à tous. Le serveur annonce aussi jusqu'à quel pas
/// tous les ordres sont connus (« Allow ») : un client ne calcule jamais au-delà. Tous appliquent donc les mêmes
/// ordres aux mêmes pas : unités, feux, scripts, cinématiques restent identiques partout. Une empreinte de l'état,
/// tous les <see cref="NetProtocol.HashEvery"/> pas, vérifie que c'est bien le cas.
/// </para>
/// </summary>
public sealed class NetSession : IDisposable
{
    public bool IsServer { get; }
    public NetState State { get; private set; }
    public string MyName { get; private set; }
    public int MyId { get; private set; }
    public string ServerName { get; private set; } = "";
    public string Mission = "";
    public string Difficulty = "EASY";
    /// <summary>Paramètres de la partie, choisis par le serveur : nationalité des unités et réglages de jeu (JSON).</summary>
    public string Nationality = "", Settings = "";
    /// <summary>Les autres joueurs (pour le serveur : les clients ; pour un client : le serveur et les autres clients).</summary>
    public List<PlayerInfo> Players { get; } = new();
    public List<NetEvent> Events { get; } = new();
    public IPEndPoint? ServerEndPoint { get; private set; }

    private readonly NetTransport _net;
    private Connection? _server;
    private int _nextPlayer = 2;
    private double _connectStart;

    // ---- partie : ordres datés ----
    private readonly List<NetCommand> _pending = new();
    private int _lastAssigned = -1;
    private int _allowSent = -1;
    private readonly Dictionary<int, ulong> _hashes = new();
    /// <summary>Client : les ordres sont connus jusqu'à ce pas compris.</summary>
    public int AllowedStep { get; private set; } = -1;
    /// <summary>Serveur : pas atteint par la simulation (mis par le jeu à chaque image).</summary>
    public int CurrentStep;
    /// <summary>Client pendant une resynchronisation : ordres à rejouer depuis le début.</summary>
    public List<NetCommand> ResyncCommands { get; } = new();
    private bool _resyncing;

    private NetSession(bool server, string name, int port)
    {
        IsServer = server;
        MyName = name;
        _net = new NetTransport(port) { AcceptNew = server };
    }

    /// <summary>Crée le serveur : ouvre le port UDP 27999 et attend les joueurs.</summary>
    public static NetSession Host(string name, int port = NetProtocol.Port)
    {
        NetSession s = new NetSession(true, name, port) { MyId = 1, ServerName = name, State = NetState.Lobby };
        return s;
    }

    /// <summary>Rejoint un serveur (« adresse » ou « adresse:port »). La réponse arrive par l'événement Joined, ou Lost.</summary>
    public static NetSession Join(string address, string name)
    {
        IPEndPoint ep = Resolve(address);
        NetSession s = new NetSession(false, name, 0) { State = NetState.Connecting, ServerEndPoint = ep };
        s._server = s._net.Connect(ep);
        s._connectStart = s._net.Now;
        s._net.Send(s._server, NetMessage.Build(MessageType.Hello, w =>
        {
            w.Write(NetProtocol.Version);
            w.Write(name);
        }));
        return s;
    }

    public static IPEndPoint Resolve(string address)
    {
        string host = address.Trim();
        int port = NetProtocol.Port;
        int colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(':') == colon && int.TryParse(host[(colon + 1)..], out int p))
        {
            port = p;
            host = host[..colon];
        }
        if (!IPAddress.TryParse(host, out IPAddress? ip))
            ip = Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        return new IPEndPoint(ip, port);
    }

    private IEnumerable<Connection> Clients => _net.Links.Where(c => c.PlayerId != 0);

    private void Broadcast(byte[] msg)
    {
        foreach (Connection c in Clients.ToList())
            _net.Send(c, msg);
    }

    private void Event(NetEventKind kind, int player = 0, string text = "", int step = 0) =>
        Events.Add(new NetEvent { Kind = kind, Player = player, Text = text, Step = step });

    // ------------------------------------------------------------------ à chaque image
    public void Update()
    {
        if (State == NetState.Closed)
            return;
        (List<(Connection from, byte[] message)> received, List<Connection> lost) = _net.Poll();
        foreach ((Connection from, byte[] message) in received)
            try
            {
                Handle(from, message);
            }
            catch (Exception e)
            {
                Console.WriteLine("Réseau : message illisible (" + e.Message + ")");
            }
        foreach (Connection c in lost)
            if (IsServer)
                RemovePlayer(c, "connexion perdue");
            else
            {
                State = NetState.Closed;
                Event(NetEventKind.Lost, text: "&ServerLost");
            }
        if (!IsServer && State == NetState.Connecting && _net.Now - _connectStart > 6)
        {
            State = NetState.Closed;
            Event(NetEventKind.Lost, text: "Pas de réponse du serveur");
        }
    }

    private void RemovePlayer(Connection c, string why)
    {
        PlayerInfo? p = Players.FirstOrDefault(x => x.Id == c.PlayerId);
        _net.Drop(c);
        if (p == null)
            return;
        Players.Remove(p);
        Event(NetEventKind.PlayerLeft, p.Id, p.Name + " (" + why + ")");
        SendLobby();
    }

    private void Handle(Connection from, byte[] message)
    {
        using BinaryReader r = NetMessage.Reader(message, out MessageType type);
        if (IsServer)
            HandleServer(from, type, r);
        else
            HandleClient(type, r);
    }

    // ------------------------------------------------------------------ serveur
    private void HandleServer(Connection from, MessageType type, BinaryReader r)
    {
        PlayerInfo? p = Players.FirstOrDefault(x => x.Id == from.PlayerId);
        switch (type)
        {
            case MessageType.Hello:
            {
                int version = r.ReadInt32();
                string name = r.ReadString().Trim();
                if (version != NetProtocol.Version)
                {
                    Refuse(from, "&WrongVersion");
                    return;
                }
                if (State != NetState.Lobby)
                {
                    Refuse(from, "Partie déjà commencée");
                    return;
                }
                if (Players.Count >= 7)
                {
                    Refuse(from, "&ServerFull");
                    return;
                }
                // pseudo déjà pris : un numéro est ajouté
                string unique = name.Length == 0 ? "Joueur" : name;
                for (int k = 2; unique.Equals(MyName, StringComparison.OrdinalIgnoreCase) || Players.Any(x => x.Name.Equals(unique, StringComparison.OrdinalIgnoreCase)); k++)
                    unique = $"{name} ({k})";
                from.PlayerId = _nextPlayer++;
                PlayerInfo np = new PlayerInfo { Id = from.PlayerId, Name = unique };
                Players.Add(np);
                _net.Send(from, NetMessage.Build(MessageType.Welcome, w =>
                {
                    w.Write(np.Id);
                    w.Write(unique);
                    w.Write(MyName);
                }));
                Event(NetEventKind.PlayerJoined, np.Id, unique);
                SendLobby();
                break;
            }
            case MessageType.SetReady when p != null:
                p.Ready = r.ReadBoolean();
                SendLobby();
                break;
            case MessageType.Bye:
                RemovePlayer(from, "parti");
                break;
            case MessageType.Loaded when p != null:
                p.Loaded = true;
                break;
            case MessageType.Progress when p != null:
                p.LastStep = Math.Max(p.LastStep, r.ReadInt32());
                break;
            case MessageType.CommandRequest when p != null && State == NetState.Playing:
            {
                NetCommand c = NetCommand.Read(r);
                c.Player = p.Id;
                Schedule(c);
                break;
            }
            case MessageType.Hash when p != null:
            {
                int step = r.ReadInt32();
                ulong h = r.ReadUInt64();
                p.LastStep = Math.Max(p.LastStep, step);
                if (_hashes.TryGetValue(step, out ulong mine) && mine != h)
                    Event(NetEventKind.Desync, p.Id, p.Name, step);
                break;
            }
        }
    }

    private void Refuse(Connection c, string why)
    {
        _net.Send(c, NetMessage.Build(MessageType.Refused, w => w.Write(why)));
        _net.Flush(0.3);
        _net.Drop(c);
    }

    /// <summary>Serveur : envoie la liste des joueurs, la mission et la difficulté à tous.</summary>
    public void SendLobby()
    {
        if (!IsServer)
            return;
        Broadcast(NetMessage.Build(MessageType.Lobby, w =>
        {
            w.Write(Mission);
            w.Write(Difficulty);
            w.Write(Players.Count + 1);
            w.Write(MyId);
            w.Write(MyName);
            w.Write(true);
            foreach (PlayerInfo x in Players)
            {
                w.Write(x.Id);
                w.Write(x.Name);
                w.Write(x.Ready);
            }
        }));
        Event(NetEventKind.LobbyChanged);
    }

    public bool AllReady => Players.All(p => p.Ready);
    public bool AllLoaded => Players.All(p => p.Loaded);
    /// <summary>Pas le plus en retard parmi les clients (le serveur attend s'il prend trop d'avance).</summary>
    public int SlowestStep => Players.Count == 0 ? int.MaxValue : Players.Min(p => p.LastStep);

    /// <summary>Serveur : la mission démarre chez tous (briefing).</summary>
    public void StartGame(string nationality, string settings)
    {
        Nationality = nationality;
        Settings = settings;
        ResetGame();
        State = NetState.Briefing;
        foreach (PlayerInfo p in Players)
        {
            p.Loaded = false;
            p.LastStep = 0;
        }
        Broadcast(NetMessage.Build(MessageType.StartGame, w =>
        {
            w.Write(Mission);
            w.Write(Difficulty);
            w.Write(Nationality);
            w.Write(Settings);
        }));
    }

    /// <summary>Serveur : fin du briefing, tout le monde charge la mission.</summary>
    public void BeginMission()
    {
        State = NetState.Loading;
        Broadcast(NetMessage.Build(MessageType.BeginMission));
    }

    /// <summary>Mission chargée : le client le dit au serveur ; le serveur passe en partie.</summary>
    public void MissionLoaded()
    {
        State = NetState.Playing;
        if (!IsServer && _server != null)
            _net.Send(_server, NetMessage.Build(MessageType.Loaded));
    }

    /// <summary>Serveur : fin de la partie, retour au salon (les joueurs restent).</summary>
    public void BackToLobby()
    {
        State = NetState.Lobby;
        foreach (PlayerInfo p in Players)
            p.Ready = false;
        Broadcast(NetMessage.Build(MessageType.BackToLobby));
        SendLobby();
    }

    private void ResetGame()
    {
        _pending.Clear();
        _hashes.Clear();
        _lastAssigned = -1;
        _allowSent = -1;
        AllowedStep = -1;
    }

    // l'ordre reçoit son pas et part chez tous
    private void Schedule(NetCommand c)
    {
        c.Step = Math.Max(CurrentStep + NetProtocol.InputDelay, _lastAssigned);
        _lastAssigned = c.Step;
        _pending.Add(c);
        Broadcast(NetMessage.Build(MessageType.Command, w => c.Write(w)));
    }

    /// <summary>Serveur : à chaque image, annonce jusqu'à quel pas les ordres sont tous connus.</summary>
    public void AnnounceAllowed()
    {
        int allow = CurrentStep + NetProtocol.InputDelay - 1;
        if (allow <= _allowSent)
            return;
        _allowSent = allow;
        Broadcast(NetMessage.Build(MessageType.Allow, w => w.Write(allow)));
    }

    /// <summary>
    /// Serveur : renvoie toute la partie à un client désynchronisé (ordres déjà appliqués et ordres à venir) ; il la
    /// rejoue depuis le début jusqu'au pas actuel du serveur.
    /// </summary>
    public void Resync(int player, IEnumerable<NetCommand> history)
    {
        Connection? c = Clients.FirstOrDefault(x => x.PlayerId == player);
        if (c == null)
            return;
        _net.Send(c, NetMessage.Build(MessageType.ResyncBegin, w =>
        {
            w.Write(Mission);
            w.Write(Difficulty);
            w.Write(Nationality);
            w.Write(Settings);
        }));
        foreach (NetCommand h in history.Concat(_pending))
            _net.Send(c, NetMessage.Build(MessageType.Command, w => h.Write(w)));
        int step = CurrentStep;
        _net.Send(c, NetMessage.Build(MessageType.ResyncEnd, w => w.Write(step)));
        _net.Send(c, NetMessage.Build(MessageType.Allow, w => w.Write(_allowSent)));
    }

    // ------------------------------------------------------------------ client
    private void HandleClient(MessageType type, BinaryReader r)
    {
        switch (type)
        {
            case MessageType.Welcome:
                MyId = r.ReadInt32();
                MyName = r.ReadString();
                ServerName = r.ReadString();
                State = NetState.Lobby;
                Event(NetEventKind.Joined);
                break;
            case MessageType.Refused:
                State = NetState.Closed;
                Event(NetEventKind.Lost, text: r.ReadString());
                break;
            case MessageType.Lobby:
            {
                Mission = r.ReadString();
                Difficulty = r.ReadString();
                int n = r.ReadInt32();
                Players.Clear();
                for (int i = 0; i < n; i++)
                {
                    PlayerInfo p = new PlayerInfo { Id = r.ReadInt32(), Name = r.ReadString(), Ready = r.ReadBoolean() };
                    if (p.Id == 1)
                        ServerName = p.Name;
                    if (p.Id != MyId)
                        Players.Add(p);
                }
                Event(NetEventKind.LobbyChanged);
                break;
            }
            case MessageType.StartGame:
                Mission = r.ReadString();
                Difficulty = r.ReadString();
                Nationality = r.ReadString();
                Settings = r.ReadString();
                ResetGame();
                State = NetState.Briefing;
                Event(NetEventKind.StartGame);
                break;
            case MessageType.BeginMission:
                State = NetState.Loading;
                Event(NetEventKind.BeginMission);
                break;
            case MessageType.Command:
            {
                NetCommand c = NetCommand.Read(r);
                if (_resyncing)
                    ResyncCommands.Add(c);
                else
                    _pending.Add(c);
                break;
            }
            case MessageType.Allow:
                AllowedStep = Math.Max(AllowedStep, r.ReadInt32());
                break;
            case MessageType.ResyncBegin:
                Mission = r.ReadString();
                Difficulty = r.ReadString();
                Nationality = r.ReadString();
                Settings = r.ReadString();
                _resyncing = true;
                ResyncCommands.Clear();
                _pending.Clear();
                Event(NetEventKind.ResyncBegin);
                break;
            case MessageType.ResyncEnd:
                _resyncing = false;
                Event(NetEventKind.ResyncEnd, step: r.ReadInt32());
                break;
            case MessageType.BackToLobby:
                State = NetState.Lobby;
                Event(NetEventKind.BackToLobby);
                break;
            case MessageType.Bye:
                State = NetState.Closed;
                Event(NetEventKind.Lost, text: "&ServerLost");
                break;
        }
    }

    /// <summary>Client : prêt ou non.</summary>
    public void SetReady(bool ready)
    {
        if (_server != null)
            _net.Send(_server, NetMessage.Build(MessageType.SetReady, w => w.Write(ready)));
    }

    // ------------------------------------------------------------------ partie (les deux côtés)
    /// <summary>
    /// Un ordre du joueur local : le serveur lui donne son pas tout de suite, un client l'envoie au serveur.
    /// Il sera appliqué plus tard, au même pas chez tous (voir <see cref="TakeDue"/>).
    /// </summary>
    public void Submit(NetCommand c)
    {
        c.Player = MyId;
        if (IsServer)
            Schedule(c);
        else if (_server != null)
            _net.Send(_server, NetMessage.Build(MessageType.CommandRequest, w => c.Write(w)));
    }

    /// <summary>Ordres à appliquer avant de calculer le pas <paramref name="step"/> (dans l'ordre où le serveur les a reçus).</summary>
    public List<NetCommand> TakeDue(int step)
    {
        List<NetCommand> due = _pending.Where(c => c.Step <= step).ToList();
        if (due.Count > 0)
            _pending.RemoveAll(c => c.Step <= step);
        return due;
    }

    /// <summary>Client après une resynchronisation : les ordres pas encore appliqués reprennent leur place.</summary>
    public void AddPending(IEnumerable<NetCommand> commands) => _pending.AddRange(commands);

    /// <summary>Client : le pas <paramref name="step"/> peut-il être calculé (tous ses ordres sont arrivés) ?</summary>
    public bool CanStep(int step) => IsServer || step <= AllowedStep;

    /// <summary>Empreinte de l'état au pas donné : le serveur la garde, un client l'envoie pour comparaison.</summary>
    public void ReportHash(int step, ulong hash)
    {
        if (IsServer)
        {
            _hashes[step] = hash;
            foreach (int old in _hashes.Keys.Where(k => k < step - 50 * NetProtocol.HashEvery).ToList())
                _hashes.Remove(old);
        }
        else if (_server != null)
            _net.Send(_server, NetMessage.Build(MessageType.Hash, w =>
            {
                w.Write(step);
                w.Write(hash);
            }));
    }

    /// <summary>Client : pas atteint (pour que le serveur ne prenne pas trop d'avance).</summary>
    public void ReportProgress(int step)
    {
        if (!IsServer && _server != null)
            _net.Send(_server, NetMessage.Build(MessageType.Progress, w => w.Write(step)));
    }

    public double PingTo(int player) => IsServer ? Clients.FirstOrDefault(c => c.PlayerId == player)?.Ping ?? 0 : _server?.Ping ?? 0;

    public void Dispose()
    {
        if (State != NetState.Closed)
        {
            byte[] bye = NetMessage.Build(MessageType.Bye);
            if (IsServer)
                Broadcast(bye);
            else if (_server != null)
                _net.Send(_server, bye);
            _net.Flush(0.3);
        }
        State = NetState.Closed;
        _net.Dispose();
    }
}
