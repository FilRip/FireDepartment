using System.Text;

namespace OpenFD.Multiplayer;

/// <summary>Un joueur du salon.</summary>
public sealed class PlayerInfo
{
    public int Id;
    public string Name = "";
    public bool Ready;
    public bool Loaded;
    public int LastStep;   // pas atteint (partie en cours)
}
