using System.Text.RegularExpressions;
using OpenFD.Script;

namespace OpenFD.MG;

/// <summary>
/// « Compiler » les scripts d'une mission : chaque fichier .scp est lu par l'analyseur du jeu (erreurs de syntaxe) ; les
/// appels à des commandes inconnues du jeu sont signalés à part (avertissements : la mission se lance quand même).
/// </summary>
public static class ScriptCheck
{
    public sealed record Problem(string File, int Line, string Message, bool Error);

    private static readonly Regex CallRx = new(@"\b([A-Za-z]{2,6}_[A-Za-z0-9_]+)\s*\(", RegexOptions.Compiled);
    private static readonly Regex LineRx = new(@"^[^:]*:(\d+)\s*(.*)$", RegexOptions.Compiled);

    /// <summary>Commandes connues du jeu, en majuscules.</summary>
    private static readonly HashSet<string> Known = ScriptCommands.All.Select(c => c[..c.IndexOf('(')].ToUpperInvariant()).ToHashSet();

    /// <summary>Textes des scripts (nom du fichier → contenu).</summary>
    public static List<Problem> Check(IReadOnlyDictionary<string, string> scripts, string? commonDir = null)
    {
        List<Problem> res = new List<Problem>();
        HashSet<string> functions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // fonctions des scripts communs (Data\Script\_Common) : elles peuvent être appelées par la mission
        if (commonDir != null && Directory.Exists(commonDir))
            foreach (string f in Directory.EnumerateFiles(commonDir, "*.scp"))
            {
                try
                {
                    foreach (Function fn in Parser.ParseFile(File.ReadAllText(f, System.Text.Encoding.Latin1), Path.GetFileName(f)))
                        functions.Add(fn.Name);
                }
                catch (Exception)
                {
                }
            }
        Dictionary<string, string> ok = new Dictionary<string, string>();
        foreach ((string file, string text) in scripts)
        {
            try
            {
                foreach (Function fn in Parser.ParseFile(text, file))
                    functions.Add(fn.Name);
                ok[file] = text;
            }
            catch (Exception e)
            {
                Match m = LineRx.Match(e.Message);
                int line = m.Success && int.TryParse(m.Groups[1].Value, out int l) ? l : 0;
                res.Add(new Problem(file, line, m.Success ? m.Groups[2].Value : e.Message, true));
            }
        }
        foreach ((string file, string text) in ok)
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string code = lines[i];
                int cut = code.IndexOf("//", StringComparison.Ordinal);
                if (cut >= 0)
                    code = code[..cut];
                foreach (Match m in CallRx.Matches(code))
                {
                    string name = m.Groups[1].Value;
                    if (!Known.Contains(name.ToUpperInvariant()) && !functions.Contains(name))
                        res.Add(new Problem(file, i + 1, EditorText.T("UnknownCommand", "commande inconnue du jeu : {0}", name), false));
                }
            }
        }
        return res;
    }

    /// <summary>Tous les .scp d'un dossier.</summary>
    public static Dictionary<string, string> ReadFolder(string dir)
    {
        Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(dir))
            foreach (string f in Directory.EnumerateFiles(dir, "*.scp").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                d[Path.GetFileName(f)] = File.ReadAllText(f, System.Text.Encoding.Latin1);
        return d;
    }
}
