using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace OpenFD.Multiplayer;

/// <summary>
/// Point d'échange UDP : une prise, un fil qui reçoit les paquets, et les liaisons avec les autres joueurs.
/// Paquet : « OF », type (1 message fiable, 2 accusé seul, 3 présence), numéro, accusé (prochain numéro attendu), message.
/// </summary>
public sealed class NetTransport : IDisposable
{
    private const byte KindData = 1, KindAck = 2, KindPing = 3;
    private const int Header = 11, Window = 128;
    private const double Resend = 0.2, Timeout = 15, PingEvery = 1;

    private readonly Socket _socket;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<(IPEndPoint from, byte[] data)> _inbox = new();
    private readonly Dictionary<IPEndPoint, Connection> _links = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private volatile bool _closed;

    /// <summary>Accepter les paquets d'inconnus (serveur) ou seulement ceux des liaisons ouvertes (client).</summary>
    public bool AcceptNew;
    public double Now => _clock.Elapsed.TotalSeconds;
    public IReadOnlyCollection<Connection> Links => _links.Values;
    public int LocalPort => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    public NetTransport(int port)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Windows : un paquet vers un port fermé fait échouer la réception suivante (« connexion réinitialisée ») ; on l'ignore
        if (OperatingSystem.IsWindows())
            try
            {
                _socket.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch
            {
            }
        _socket.Bind(new IPEndPoint(IPAddress.Any, port));
        _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "OpenFD réseau" };
        _thread.Start();
    }

    private void ReceiveLoop()
    {
        byte[] buf = new byte[65536];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_closed)
        {
            try
            {
                int n = _socket.ReceiveFrom(buf, ref any);
                if (n >= Header && buf[0] == (byte)'O' && buf[1] == (byte)'F')
                    _inbox.Enqueue(((IPEndPoint)any, buf.AsSpan(0, n).ToArray()));
            }
            catch (SocketException)
            {
                if (_closed)
                    return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    public Connection Connect(IPEndPoint ep)
    {
        if (!_links.TryGetValue(ep, out Connection? c))
            _links[ep] = c = new Connection(ep, Now);
        return c;
    }

    public void Drop(Connection c)
    {
        _links.Remove(c.EndPoint);
        c.Lost = true;
    }

    /// <summary>Envoie un message (fiable et ordonné).</summary>
    public void Send(Connection c, byte[] message)
    {
        if (c.Lost)
            return;
        if (c.Unacked.Count >= Window)
        {
            c.Waiting.Enqueue(message);
            return;
        }
        SendNow(c, message);
    }

    private void SendNow(Connection c, byte[] message)
    {
        uint seq = c.NextSend++;
        byte[] p = new byte[Header + message.Length];
        Buffer.BlockCopy(message, 0, p, Header, message.Length);
        p[0] = (byte)'O';
        p[1] = (byte)'F';
        p[2] = KindData;
        BitConverter.TryWriteBytes(p.AsSpan(3), seq);
        double now = Now;
        c.Unacked[seq] = (p, now, now);
        Transmit(c, p);
    }

    private void Transmit(Connection c, byte[] p)
    {
        BitConverter.TryWriteBytes(p.AsSpan(7), c.NextRecv);   // accusé : prochain numéro attendu
        c.AckPending = false;
        c.LastSent = Now;
        try
        {
            _socket.SendTo(p, c.EndPoint);
        }
        catch (SocketException)
        {
        }
    }

    private void SendControl(Connection c, byte kind)
    {
        byte[] p = new byte[Header];
        p[0] = (byte)'O';
        p[1] = (byte)'F';
        p[2] = kind;
        Transmit(c, p);
    }

    /// <summary>
    /// À chaque image : paquets reçus, renvois, accusés, présence. Renvoie les messages arrivés (dans l'ordre) et les
    /// liaisons perdues.
    /// </summary>
    public (List<(Connection from, byte[] message)> received, List<Connection> lost) Poll()
    {
        List<(Connection, byte[])> received = new List<(Connection, byte[])>();
        List<Connection> lost = new List<Connection>();
        double now = Now;
        while (_inbox.TryDequeue(out (IPEndPoint from, byte[] data) item))
        {
            if (!_links.TryGetValue(item.from, out Connection? c))
            {
                // inconnu : seulement un premier message (numéro 0) vers un serveur
                if (!AcceptNew || item.data[2] != KindData || BitConverter.ToUInt32(item.data, 3) != 0)
                    continue;
                c = Connect(item.from);
            }
            c.LastHeard = now;
            uint ack = BitConverter.ToUInt32(item.data, 7);
            foreach (uint s in c.Unacked.Keys.Where(k => k < ack).ToList())
            {
                c.Ping = c.Ping * 0.8 + (now - c.Unacked[s].sent) * 0.2;
                c.Unacked.Remove(s);
            }
            while (c.Unacked.Count < Window && c.Waiting.Count > 0)
                SendNow(c, c.Waiting.Dequeue());
            if (item.data[2] != KindData)
                continue;
            uint seq = BitConverter.ToUInt32(item.data, 3);
            c.AckPending = true;
            if (seq < c.NextRecv || c.Early.ContainsKey(seq))
                continue;
            c.Early[seq] = item.data.AsSpan(Header).ToArray();
            while (c.Early.Remove(c.NextRecv, out byte[]? msg))
            {
                c.NextRecv++;
                received.Add((c, msg));
            }
        }
        foreach (Connection c in _links.Values.ToList())
        {
            if (now - c.LastHeard > Timeout)
            {
                Drop(c);
                lost.Add(c);
                continue;
            }
            foreach (KeyValuePair<uint, (byte[] packet, double sent, double first)> u in c.Unacked.ToList())
                if (now - u.Value.sent > Resend)
                {
                    c.Unacked[u.Key] = (u.Value.packet, now, u.Value.first);
                    Transmit(c, u.Value.packet);
                }
            if (c.AckPending)
                SendControl(c, KindAck);
            else if (now - c.LastSent > PingEvery)
                SendControl(c, KindPing);
        }
        return (received, lost);
    }

    /// <summary>Envoie tout ce qui attend encore (départ propre), au plus pendant le délai donné.</summary>
    public void Flush(double seconds)
    {
        double end = Now + seconds;
        while (Now < end && _links.Values.Any(c => c.Unacked.Count > 0))
        {
            Poll();
            Thread.Sleep(15);
        }
    }

    public void Dispose()
    {
        _closed = true;
        try
        {
            _socket.Close();
        }
        catch
        {
        }
    }
}
