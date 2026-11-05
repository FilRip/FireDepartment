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
