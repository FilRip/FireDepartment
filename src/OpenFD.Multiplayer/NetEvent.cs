using System.Net;
using System.Net.Sockets;

namespace OpenFD.Multiplayer;

public sealed class NetEvent
{
    public NetEventKind Kind;
    public int Player;
    public int Step;
    public string Text = "";
}
