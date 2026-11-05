using System.Text;

namespace OpenFD.Multiplayer;

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
