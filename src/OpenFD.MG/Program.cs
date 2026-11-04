// FD3 — version MonoGame (DesktopGL) : simulation du feu de Fire Department 3, en 3D et en carte vue de dessus.
// Lancement : dotnet run --project src/OpenFD.MG -c Release -- "<dossier du jeu>"
using OpenFD.Formats;
using OpenFD.MG;

namespace OpenFD.MG;

public static class Program
{
    public static int Main(string[] args)
    {
        List<string> argList = args.ToList();
        string? Opt(string name) { int i = argList.IndexOf(name); return i >= 0 && i + 1 < argList.Count ? argList[i + 1] : null; }
        string dataDir = args.FirstOrDefault(a => !a.StartsWith("--") && Directory.Exists(a)) ?? Environment.GetEnvironmentVariable("FD3_DATA") ?? "";
        if (Directory.Exists(Path.Combine(dataDir, "Data")))
            dataDir = Path.Combine(dataDir, "Data");
        if (!File.Exists(Path.Combine(dataDir, "design.pak")))
        {
            Console.WriteLine("Indiquez le dossier du jeu, par exemple :");
            Console.WriteLine("  dotnet run --project src/OpenFD.MG -c Release -- \"E:\\Jeux\\Fire Department 3\"");
            return 1;
        }

        // le jeu démarre toujours sur le menu. Seuls les essais automatiques (--capture : image enregistrée puis arrêt)
        // peuvent ouvrir directement une mission (--mission, --difficulte) pour la photographier
        string? capture = Opt("--capture");
        string? testMission = capture != null ? Opt("--mission") : null;
        OpenFDGame Make() => new(new GameFiles(dataDir), testMission ?? "London", (capture != null ? Opt("--difficulte") ?? "EASY" : "EASY").ToUpperInvariant())
        {
            CapturePath = capture,
            CaptureAfter = int.TryParse(Opt("--images"), out int nf) ? nf : 5,
            StartSeconds = float.TryParse(Opt("--avance"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float av) ? av : 0,
            StartMap = argList.Contains("--carte"),
            CameraSpec = Opt("--camera"),
            TestAttack = argList.Contains("--envoyer"),
            TestDoors = Opt("--portes"),
            NoRoofs = argList.Contains("--sans-toits"),
            NoHelp = argList.Contains("--sans-aide"),
            StartMenu = testMission == null || argList.Contains("--menu"),
        };
        try
        {
            using OpenFDGame game = Make();
            game.Run();
        }
        catch (Microsoft.Xna.Framework.Graphics.NoSuitableGraphicsDeviceException) when (Environment.GetEnvironmentVariable("FD3_SANS_MSAA") == null)
        {
            // certaines cartes (ou machines virtuelles) refusent l'anticrénelage : on réessaie sans
            Console.WriteLine("Carte graphique : nouvel essai sans anticrénelage.");
            Environment.SetEnvironmentVariable("FD3_SANS_MSAA", "1");
            using OpenFDGame game = Make();
            game.Run();
        }
        return 0;
    }
}
