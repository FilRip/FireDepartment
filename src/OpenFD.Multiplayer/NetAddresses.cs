using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace OpenFD.Multiplayer;

/// <summary>Adresses de ce PC : sur le réseau local, et vue d'Internet (donnée par un service web).</summary>
public static class NetAddresses
{
    /// <summary>Adresse sur le réseau local (celle de la carte réseau qui mène à Internet).</summary>
    public static string Lan()
    {
        try
        {
            // aucun paquet n'est envoyé : « connecter » une prise UDP choisit seulement la carte réseau
            using Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 65530);
            return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
        }
        catch
        {
            try
            {
                return Dns.GetHostAddresses(Dns.GetHostName()).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))?.ToString() ?? "?";
            }
            catch
            {
                return "?";
            }
        }
    }

    private static readonly string[] Services = { "https://api.ipify.org", "https://icanhazip.com", "https://checkip.amazonaws.com" };

    /// <summary>Adresse Internet (WAN) : demandée à un service web ; null s'il ne répond pas.</summary>
    public static async Task<string?> WanAsync()
    {
        using HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        foreach (string url in Services)
            try
            {
                string text = (await http.GetStringAsync(url)).Trim();
                if (IPAddress.TryParse(text, out _))
                    return text;
            }
            catch
            {
            }
        return null;
    }
}
