using System.Net;
using System.Net.Sockets;

namespace OpenFD.Multiplayer;

/// <summary>Où en est la session : salon, briefing, chargement, partie.</summary>
public enum NetState { Connecting, Lobby, Briefing, Loading, Playing, Closed }
