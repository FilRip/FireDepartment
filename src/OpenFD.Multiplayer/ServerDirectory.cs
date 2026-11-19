using System.Diagnostics;
using System.Net;
using System.Net.Http;
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

    /// <summary>Adresses de la liste qui répondent (interrogées toutes en même temps), dans l'ordre de la liste.</summary>
    public static async Task<(List<ServerInfo> servers, string? error)> ListAsync()
    {
        (List<string> addresses, string? error) = await FetchAsync();
        if (error != null)
            return (new List<ServerInfo>(), error);
        ServerInfo?[] found = await Task.WhenAll(addresses.Select(a => QueryAsync(a)));
        return (found.Where(s => s != null).Select(s => s!).ToList(), null);
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
