namespace OpenFD.Multiplayer;

/// <summary>Un serveur de la liste Internet qui a répondu : son adresse, sa partie, et le temps de réponse.</summary>
public sealed class ServerInfo
{
    public string Address = "";
    public string Name = "";
    public string Mission = "";
    public int Players;
    /// <summary>La partie est lancée (briefing, chargement ou jeu) : on ne peut plus la rejoindre.</summary>
    public bool Playing;
    /// <summary>Temps de réponse (ms).</summary>
    public int Ping;
}
