// Éditeur de missions d'Open Fire Department : OpenFD.Editor.exe "<dossier du jeu>"
// (fenêtre des scripts : OpenFD.Editor.exe --script "<dossier Data>" "<dossier des scripts>" <mission> [langue] [fonction|paramètres],
// ouverte par l'éditeur)
using OpenFD.Formats;
using OpenFD.MG;

namespace OpenFD.Editor;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--script")
        {
            using ScriptEditorGame scripts = new ScriptEditorGame(args[1], args[2], args.Length > 3 ? args[3] : "", args.Length > 4 ? args[4] : null, args.Length > 5 ? args[5] : null);
            scripts.Run();
            return 0;
        }
        CrashLog.Start();
        // dossier du jeu : en argument (le jeu ou son dossier Data), sinon FD3_DATA ; Fire Department 3 de préférence
        List<string> given = args.Where(a => !a.StartsWith("--") && Directory.Exists(a)).ToList();
        if (given.Count == 0 && Environment.GetEnvironmentVariable("FD3_DATA") is { } env)
            given.Add(env);
        List<(string License, string DataDir)> games = new List<(string License, string DataDir)>();
        foreach (string g in given)
        {
            string d = Directory.Exists(Path.Combine(g, "Data")) ? Path.Combine(g, "Data") : g;
            if (File.Exists(Path.Combine(d, "design.pak")))
                games.Add(((OpenFD.Game.Localization.StartIni(d, "License") ?? "FD3").ToUpperInvariant(), d));
        }
        if (games.Count == 0)
        {
            Console.WriteLine("Indiquez le dossier du jeu, par exemple :");
            Console.WriteLine("  OpenFD.Editor.exe \"E:\\Jeux\\Fire Department 3\"");
            return 1;
        }
        string dataDir = games.FirstOrDefault(x => x.License == "FD3").DataDir ?? games[0].DataDir;
        int i = Array.IndexOf(args, "--capture");
        try
        {
            using OpenFDGame editor = new OpenFDGame(new GameFiles(dataDir), "", "EASY")
            {
                EditorMode = true,
                ForceWindowed = true,
                CapturePath = i >= 0 && i + 1 < args.Length ? args[i + 1] : null,
                CaptureAfter = int.TryParse(Environment.GetEnvironmentVariable("FD3_IMAGES"), out int n) ? n : 30,
            };
            editor.Run();
        }
        catch (Exception e)
        {
            CrashLog.Report(e, "exception dans l'éditeur");
            throw;
        }
        return 0;
    }
}
