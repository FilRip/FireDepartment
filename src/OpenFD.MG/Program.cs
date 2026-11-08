// FD3 — version MonoGame (DesktopGL) : simulation du feu de Fire Department 3, en 3D et en carte vue de dessus.
// Lancement : dotnet run --project src/OpenFD.MG -c Release -- "<dossier du jeu>" ["<dossier de l'autre jeu>"] [-windowed]
using OpenFD.Formats;
using OpenFD.MG;

namespace OpenFD.MG;

public static class Program
{
    public static int Main(string[] args)
    {
        CrashLog.Start();   // OpenFD.log, et crash_….txt si une exception n'est pas gérée
        List<string> argList = args.ToList();
        string? Opt(string name)
        {
            int i = argList.IndexOf(name);
            return i >= 0 && i + 1 < argList.Count ? argList[i + 1] : null;
        }
        // dossiers de jeu : un ou deux en arguments (Fire Department 3 et Fire Department 2, dans n'importe quel ordre ;
        // ou --fd3 <dossier> --fd2 <dossier>), sinon FD3_DATA. L'édition est lue dans Data\Config\Start.Ini (« License »).
        string[] valued = { "--capture", "--mission", "--difficulte", "--images", "--avance", "--camera", "--portes", "--fd2", "--fd3", "--jeu", "--ecran-depart" };
        List<string> given = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                if (valued.Contains(args[i]))
                    i++;
                continue;
            }
            if (Directory.Exists(args[i]))
                given.Add(args[i]);
        }
        foreach (string o in new[] { "--fd3", "--fd2" })
            if (Opt(o) is { } od && Directory.Exists(od))
                given.Add(od);
        if (given.Count == 0 && Environment.GetEnvironmentVariable("FD3_DATA") is { } env)
            given.Add(env);
        List<(string License, string DataDir)> games = new List<(string License, string DataDir)>();
        foreach (string g in given)
        {
            string d = Directory.Exists(Path.Combine(g, "Data")) ? Path.Combine(g, "Data") : g;
            if (!File.Exists(Path.Combine(d, "design.pak")))
                continue;
            string lic = (OpenFD.Game.Localization.StartIni(d, "License") ?? "FD3").ToUpperInvariant();
            if (!games.Any(x => x.License == lic))
                games.Add((lic, d));
        }
        if (games.Count == 0)
        {
            Console.WriteLine("Indiquez le dossier du jeu (ou les deux : Fire Department 3 et Fire Department 2), par exemple :");
            Console.WriteLine("  OpenFD.MG.exe \"E:\\Jeux\\Fire Department 3\" \"E:\\Jeux\\Fire Department 2\"");
            return 1;
        }
        // boutons dans l'ordre FD2, FD3 ; on démarre sur Fire Department 3 s'il est là
        games = games.OrderBy(x => x.License).ToList();
        string dataDir = games.FirstOrDefault(x => x.License == "FD3").DataDir ?? games[0].DataDir;
        // relancé après le choix de l'autre jeu : --jeu <dossier Data> --ecran-depart 1 (campagne) ou 5 (autres missions)
        if (Opt("--jeu") is { } chosen && games.Any(x => x.DataDir == chosen))
            dataDir = chosen;
        int startScreen = int.TryParse(Opt("--ecran-depart"), out int es) ? es : 0;

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
            Games = games,
            StartScreen = startScreen,
            ForceWindowed = argList.Any(a => a.Equals("-windowed", StringComparison.OrdinalIgnoreCase) || a.Equals("--windowed", StringComparison.OrdinalIgnoreCase)),
        };
        (string DataDir, int Screen)? next;
        try
        {
            using OpenFDGame game = Make();
            game.Run();
            next = game.SwitchTo;
        }
        catch (Exception e) when (e is not Microsoft.Xna.Framework.Graphics.NoSuitableGraphicsDeviceException)
        {
            CrashLog.Report(e, "exception dans le jeu");
            throw;
        }
        catch (Microsoft.Xna.Framework.Graphics.NoSuitableGraphicsDeviceException) when (Environment.GetEnvironmentVariable("FD3_SANS_MSAA") == null)
        {
            // certaines cartes (ou machines virtuelles) refusent l'anticrénelage : on réessaie sans
            Console.WriteLine("Carte graphique : nouvel essai sans anticrénelage.");
            Environment.SetEnvironmentVariable("FD3_SANS_MSAA", "1");
            using OpenFDGame game = Make();
            game.Run();
            next = game.SwitchTo;
        }
        // changement de jeu (écran du choix de Fire Department 2 ou 3) : le programme se relance lui-même sur les données
        // de l'autre jeu (une seconde partie MonoGame dans le même processus n'est pas fiable)
        if (next is { } sw)
            Restart(games, sw.DataDir, sw.Screen, args, valued);
        return 0;
    }

    private static void Restart(List<(string License, string DataDir)> games, string dataDir, int screen, string[] original, string[] valued)
    {
        List<string> args = new List<string>();
        string exe = Environment.ProcessPath ?? "";
        // lancé par « dotnet OpenFD.dll » : on redonne la dll
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            args.Add(typeof(Program).Assembly.Location);
        foreach ((string lic, string dir) in games)
        {
            args.Add(lic == "FD2" ? "--fd2" : "--fd3");
            args.Add(dir);
        }
        args.AddRange(new[] { "--jeu", dataDir, "--ecran-depart", screen.ToString() });
        // les autres options de la ligne de commande (-windowed ; essais : --capture, --images…) sont reprises
        if (original.Any(a => a.Equals("-windowed", StringComparison.OrdinalIgnoreCase)))
            args.Add("-windowed");
        string[] own = { "--fd2", "--fd3", "--jeu", "--ecran-depart" };
        for (int i = 0; i < original.Length; i++)
        {
            if (!original[i].StartsWith("--"))
                continue;
            bool hasValue = valued.Contains(original[i]) && i + 1 < original.Length;
            if (!own.Contains(original[i]))
            {
                args.Add(original[i]);
                if (hasValue)
                    args.Add(original[i + 1]);
            }
            if (hasValue)
                i++;
        }
        System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        try
        {
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception e)
        {
            Console.WriteLine("Relance : " + e.Message);
        }
    }
}
