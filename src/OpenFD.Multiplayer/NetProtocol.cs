using System.Text;

namespace OpenFD.Multiplayer;

/// <summary>Constantes du jeu en réseau.</summary>
public static class NetProtocol
{
    /// <summary>Port UDP du jeu (serveur).</summary>
    public const int Port = 27999;
    /// <summary>Version du protocole : un client d'une autre version est refusé.</summary>
    public const int Version = 1;
    /// <summary>
    /// Avance donnée aux ordres : un ordre reçu par le serveur au pas N est appliqué partout au pas N + InputDelay
    /// (3 pas = 0,3 s), le temps qu'il arrive chez tous les joueurs.
    /// </summary>
    public const int InputDelay = 3;
    /// <summary>Contrôle de synchronisation : empreinte de l'état de la partie tous les HashEvery pas.</summary>
    public const int HashEvery = 50;
}

/// <summary>Types des messages échangés.</summary>
public enum MessageType : byte
{
    Hello = 1,          // client → serveur : version, pseudo
    Welcome,            // serveur → client : numéro du joueur
    Refused,            // serveur → client : raison
    Lobby,              // serveur → clients : joueurs, mission, difficulté
    SetReady,           // client → serveur : prêt ou non
    Bye,                // départ (les deux sens)
    StartGame,          // serveur → clients : la mission démarre (briefing)
    BeginMission,       // serveur → clients : fin du briefing, chargement de la mission
    Loaded,             // client → serveur : mission chargée
    CommandRequest,     // client → serveur : un ordre du joueur
    Command,            // serveur → clients : un ordre, avec le pas où l'appliquer
    Allow,              // serveur → clients : les ordres jusqu'à ce pas sont tous connus
    Hash,               // client → serveur : empreinte de l'état à un pas
    ResyncBegin,        // serveur → client : désynchronisé, la partie va être rejouée
    ResyncEnd,          // serveur → client : fin des ordres à rejouer, jusqu'au pas donné
    BackToLobby,        // serveur → clients : retour au salon (fin de partie)
    Progress,           // client → serveur : pas atteint (le serveur attend les joueurs en retard)
}

/// <summary>Un ordre de joueur (même contenu qu'une ligne du journal d'une sauvegarde).</summary>
public sealed class NetCommand
{
    public int Step;
    public int Player;
    public string Op = "";
    public long A, B;
    public float X, Y;
    public string? S;

    public void Write(BinaryWriter w)
    {
        w.Write(Step);
        w.Write(Player);
        w.Write(Op);
        w.Write(A);
        w.Write(B);
        w.Write(X);
        w.Write(Y);
        w.Write(S != null);
        if (S != null)
            w.Write(S);
    }

    public static NetCommand Read(BinaryReader r)
    {
        NetCommand c = new NetCommand { Step = r.ReadInt32(), Player = r.ReadInt32(), Op = r.ReadString(), A = r.ReadInt64(), B = r.ReadInt64(), X = r.ReadSingle(), Y = r.ReadSingle() };
        if (r.ReadBoolean())
            c.S = r.ReadString();
        return c;
    }

    public override string ToString() => $"{Op} {A} {B} ({X:0},{Y:0}) {S} au pas {Step} (joueur {Player})";
}

/// <summary>Un joueur du salon.</summary>
public sealed class PlayerInfo
{
    public int Id;
    public string Name = "";
    public bool Ready;
    public bool Loaded;
    public int LastStep;   // pas atteint (partie en cours)
}

/// <summary>Écriture et lecture des messages (un message = son type puis ses champs).</summary>
public static class NetMessage
{
    public static byte[] Build(MessageType type, Action<BinaryWriter>? body = null)
    {
        using MemoryStream ms = new MemoryStream();
        using (BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            w.Write((byte)type);
            body?.Invoke(w);
        }
        return ms.ToArray();
    }

    public static BinaryReader Reader(byte[] payload, out MessageType type)
    {
        BinaryReader r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
        type = (MessageType)r.ReadByte();
        return r;
    }
}
