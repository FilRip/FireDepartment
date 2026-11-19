using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenFD.Multiplayer;

/// <summary>
/// Liste Internet des serveurs : une page web donne les adresses (« Liste : » puis une adresse IP par ligne) ; chaque
/// adresse est interrogée sur le port UDP du jeu, et seules celles qui répondent sont gardées. Un serveur créé
/// s'inscrit sur la liste par add.php.
/// </summary>
public static class ServerDirectory
{
    public const string ListUrl = "http://ptreille.free.fr/OpenFD";
    public const string AddUrl = "http://ptreille.free.fr/OpenFD/add.php?addressip=";
    private const string ListHead = "Liste :";

    /// <summary>
    /// Adresses de la liste ; error : la page n'a pas pu être lue, ou ne commence pas par « Liste : » (alors c'est son
    /// texte, un message d'erreur).
    /// </summary>
    public static async Task<(List<string> addresses, string? error)> FetchAsync()
    {
        string page;
        try
        {
            using HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            page = await http.GetStringAsync(ListUrl);
        }
        catch (Exception e)
        {
            return (new List<string>(), e.Message);
        }
        // la page peut être en texte simple ou en HTML : balises enlevées (<br> et <p> = fin de ligne)
        string text = Regex.Replace(page, @"<\s*(br|/p|p|/div|div|/li|li)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]*>", "");
        text = WebUtility.HtmlDecode(text).Replace("\r", "").TrimStart('﻿', ' ', '\t', '\n');
        if (!text.StartsWith(ListHead, StringComparison.OrdinalIgnoreCase))
            return (new List<string>(), page.Trim());
        List<string> res = new List<string>();
        foreach (string raw in text[ListHead.Length..].Split('\n', ' ', '\t'))
        {
            string a = raw.Trim().TrimEnd(';', ',');
            if (a.Length > 0 && IPAddress.TryParse(a, out IPAddress? ip) && ip.AddressFamily == AddressFamily.InterNetwork && !res.Contains(a))
                res.Add(a);
        }
        return (res, null);
    }

    /// <summary>
    /// Interroge le serveur sur le port UDP du jeu (le serveur répond par sa partie) ; null s'il ne répond pas à temps.
    /// </summary>
    public static async Task<ServerInfo?> QueryAsync(string address, int timeoutMs = 1500)
    {
        try
        {
            IPEndPoint ep = new IPEndPoint(IPAddress.Parse(address), NetProtocol.Port);
            using UdpClient udp = new UdpClient(AddressFamily.InterNetwork);
            if (OperatingSystem.IsWindows())
                try
                {
                    // Windows : un port fermé renvoie une erreur qui ferait échouer la réception ; on l'ignore
                    udp.Client.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null);
                }
                catch
                {
                }
            byte[] query = NetTransport.QueryPacket();
            Stopwatch sw = Stopwatch.StartNew();
            using CancellationTokenSource cts = new CancellationTokenSource(timeoutMs);
            // deux envois (un paquet UDP peut se perdre)
            await udp.SendAsync(query, query.Length, ep);
            bool resent = false;
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try
                {
                    // premier tiers du délai sans réponse : la question est renvoyée une fois
                    using CancellationTokenSource first = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    if (!resent)
                        first.CancelAfter(timeoutMs / 3);
                    r = await udp.ReceiveAsync(first.Token);
                }
                catch (OperationCanceledException)
                {
                    if (cts.IsCancellationRequested)
                        return null;
                    resent = true;
                    await udp.SendAsync(query, query.Length, ep);
                    continue;
                }
                catch (SocketException)
                {
                    continue;
                }
                if (!r.RemoteEndPoint.Address.Equals(ep.Address) || NetTransport.ReadInfo(r.Buffer) is not { } info)
                    continue;
                info.Address = address;
                info.Ping = (int)sw.ElapsedMilliseconds;
                return info;
            }
        }
        catch
        {
        }
        return null;
    }

    /// <summary>
    /// Serveurs du réseau local et de la liste Internet, cherchés en même temps : ceux du réseau local d'abord
    /// (diffusion), puis ceux de la liste qui répondent, dans son ordre ; un serveur trouvé des deux façons n'est
    /// gardé qu'une fois (celui du réseau local). error : la page de la liste n'a pas pu être lue.
    /// </summary>
    public static async Task<(List<ServerInfo> servers, string? error)> ListAsync()
    {
        Task<List<ServerInfo>> lan = LanAsync();
        (List<string> addresses, string? error) = await FetchAsync();
        ServerInfo?[] found = error != null ? Array.Empty<ServerInfo?>() : await Task.WhenAll(addresses.Select(a => QueryAsync(a)));
        List<ServerInfo> res = await lan;
        foreach (ServerInfo s in found.Where(s => s != null).Select(s => s!))
        {
            // même serveur vu du réseau local (adresse locale) et d'Internet (adresse WAN) : même nom, mission, joueurs
            bool twice = res.Any(l => l.Address == s.Address
                || (l.Name == s.Name && l.Mission == s.Mission && l.Players == s.Players && l.Playing == s.Playing));
            if (!twice)
                res.Add(s);
        }
        return (res, error);
    }

    /// <summary>
    /// Serveurs du réseau local : une seule question envoyée en diffusion (adresse de diffusion de chaque carte réseau,
    /// et 255.255.255.255) sur le port UDP du jeu ; tous les serveurs qui l'entendent répondent.
    /// </summary>
    public static async Task<List<ServerInfo>> LanAsync(int timeoutMs = 1000)
    {
        List<ServerInfo> res = new List<ServerInfo>();
        try
        {
            using UdpClient udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            if (OperatingSystem.IsWindows())
                try
                {
                    udp.Client.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null);
                }
                catch
                {
                }
            byte[] query = NetTransport.QueryPacket();
            List<IPAddress> targets = BroadcastAddresses();
            Stopwatch sw = Stopwatch.StartNew();
            async Task SendAll()
            {
                foreach (IPAddress b in targets)
                    try
                    {
                        await udp.SendAsync(query, query.Length, new IPEndPoint(b, NetProtocol.Port));
                    }
                    catch
                    {
                    }
            }
            await SendAll();
            bool resent = false;
            using CancellationTokenSource cts = new CancellationTokenSource(timeoutMs);
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try
                {
                    // au tiers du délai : la question est renvoyée une fois (un paquet UDP peut se perdre)
                    using CancellationTokenSource first = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    if (!resent)
                        first.CancelAfter(timeoutMs / 3);
                    r = await udp.ReceiveAsync(first.Token);
                }
                catch (OperationCanceledException)
                {
                    if (cts.IsCancellationRequested)
                        break;
                    resent = true;
                    await SendAll();
                    continue;
                }
                catch (SocketException)
                {
                    continue;
                }
                if (NetTransport.ReadInfo(r.Buffer) is not { } info)
                    continue;
                string address = r.RemoteEndPoint.Address.ToString();
                if (res.Any(s => s.Address == address))
                    continue;
                info.Address = address;
                info.Ping = (int)sw.ElapsedMilliseconds;
                info.Lan = true;
                res.Add(info);
            }
        }
        catch
        {
        }
        return res;
    }

    /// <summary>Adresses de diffusion des cartes réseau en service (IPv4), puis 255.255.255.255.</summary>
    private static List<IPAddress> BroadcastAddresses()
    {
        List<IPAddress> res = new List<IPAddress>();
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (UnicastIPAddressInformation u in nic.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null)
                        continue;
                    byte[] ip = u.Address.GetAddressBytes(), mask = u.IPv4Mask.GetAddressBytes();
                    byte[] b = new byte[4];
                    for (int i = 0; i < 4; i++)
                        b[i] = (byte)(ip[i] | ~mask[i]);
                    IPAddress ba = new IPAddress(b);
                    if (!res.Contains(ba))
                        res.Add(ba);
                }
            }
        }
        catch
        {
        }
        res.Add(IPAddress.Broadcast);
        return res;
    }

    /// <summary>Inscrit ce serveur sur la liste ; renvoie null si la page répond « OK », sinon ce qu'elle a répondu.</summary>
    public static async Task<string?> RegisterAsync(string wanAddress)
    {
        try
        {
            using HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            string answer = (await http.GetStringAsync(AddUrl + Uri.EscapeDataString(wanAddress))).Trim();
            string plain = Regex.Replace(answer, "<[^>]*>", "").Trim();
            return plain.Equals("OK", StringComparison.OrdinalIgnoreCase) ? null : (answer.Length > 0 ? answer : "(réponse vide)");
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }
}
