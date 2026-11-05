using System.Net;
using System.Net.Sockets;

namespace OpenFD.Multiplayer;

/// <summary>Ce qui s'est passé sur le réseau depuis la dernière image (le jeu y réagit).</summary>
public enum NetEventKind
{
    Joined,          // client : accepté par le serveur
    PlayerJoined,    // serveur : un joueur est arrivé
    PlayerLeft,      // un joueur est parti
    LobbyChanged,    // joueurs, prêts, mission
    StartGame,       // client : le serveur lance la mission (briefing)
    BeginMission,    // client : fin du briefing, charger la mission
    Desync,          // serveur : un client n'a plus le même état (Player)
    ResyncBegin,     // client : la partie va être rejouée
    ResyncEnd,       // client : tous les ordres sont arrivés, rejouer jusqu'au pas Step
    BackToLobby,     // client : retour au salon
    Lost,            // la liaison est perdue (Text : raison)
}
