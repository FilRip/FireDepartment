using OpenFD.Formats;

namespace OpenFD.ConvFD1;

/// <summary>
/// Convertisseur des missions de Fire Department 1 pour OpenFD.
/// Usage : OpenFD.ConvFD1 &lt;dossier du jeu FD1&gt; &lt;dossier Data de FD3&gt; &lt;mission FD1&gt; [nom de la mission convertie]
/// Exemple : OpenFD.ConvFD1 "C:\Jeux\Fire Department" "C:\Jeux\Fire Department 3\Data" Entrepot
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Usage : OpenFD.ConvFD1 <dossier du jeu FD1> <dossier Data de FD3> <mission FD1 | *> [nom]");
            Console.WriteLine("  mission FD1 : un dossier de Logic\\Missions (Entrepot, banque, ecole…), ou * pour toutes");
            return 1;
        }
        string fd1Root = args[0];
        string data = args[1];
        if (!Directory.Exists(Path.Combine(fd1Root, "Logic", "Missions")))
        {
            Console.WriteLine($"Dossier FD1 invalide (pas de Logic\\Missions) : {fd1Root}");
            return 1;
        }
        if (!Directory.Exists(Path.Combine(data, "Design")))
        {
            Console.WriteLine($"Dossier Data du jeu invalide (pas de Design) : {data}");
            return 1;
        }
        Fd1Data fd1 = new Fd1Data(fd1Root);
        GameFiles fd3 = new GameFiles(data);
        List<string> missions = args[2] == "*"
            ? Directory.EnumerateDirectories(Path.Combine(fd1Root, "Logic", "Missions")).Select(Path.GetFileName).Where(n => n != null).Select(n => n!).ToList()
            : new List<string> { args[2] };
        int errors = 0;
        foreach (string mission in missions)
        {
            // nom par défaut : « FD1_ » et le nom du dossier FD1, chaque mot avec une majuscule, sans « _ » (« banque » →
            // FD1_Banque, « station_service » → FD1_StationService)
            string safe = ModelConverter.Safe(mission);
            string camel = string.Concat(safe.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));
            string name = args.Length > 3 && missions.Count == 1 ? args[3] : "FD1_" + camel;
            Console.WriteLine($"== {mission} → {name}");
            MissionConverter c = new MissionConverter { Fd1 = fd1, Fd3 = fd3, Mission = mission, Name = name };
            try
            {
                c.Run();
            }
            catch (Exception e)
            {
                c.Report.Add("ERREUR : " + e.Message);
                errors++;
            }
            foreach (string line in c.Report)
                Console.WriteLine("  " + line);
        }
        return errors == 0 ? 0 : 2;
    }
}
