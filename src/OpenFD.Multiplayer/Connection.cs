using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace OpenFD.Multiplayer;

/// <summary>
/// Liaison avec un autre joueur, sur UDP : les messages arrivent tous, une seule fois et dans l'ordre (numéro de
/// séquence, accusé de réception, renvoi après 200 ms sans accusé). Plus de 15 s sans nouvelles : liaison perdue.
/// </summary>
public sealed class Connection
{
    public IPEndPoint EndPoint { get; }
    public int PlayerId;
    public bool Lost { get; internal set; }
    public double LastHeard;
    public double Ping;   // aller-retour estimé (secondes)

    internal uint NextSend, NextRecv;
    internal readonly SortedDictionary<uint, (byte[] packet, double sent, double first)> Unacked = new();
    internal readonly Queue<byte[]> Waiting = new();   // au-delà de la fenêtre d'envoi
    internal readonly SortedDictionary<uint, byte[]> Early = new();
    internal bool AckPending;
    internal double LastSent;

    internal Connection(IPEndPoint ep, double now)
    {
        EndPoint = ep;
        LastHeard = now;
    }
}
