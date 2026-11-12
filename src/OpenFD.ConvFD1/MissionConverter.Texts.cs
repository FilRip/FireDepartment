using System.Text;
using System.Text.RegularExpressions;

namespace OpenFD.ConvFD1;

/// <summary>
/// Textes de la mission FD1 pour le moteur : Data\Localize\Lang_&lt;XXX&gt;\FD1_&lt;mission&gt;.loc (UTF-16, comme ceux du jeu),
/// pour chaque langue du jeu installé (FD1 a le français, l'anglais et l'allemand : LOCALISATION\Fra, Eng, ger ; les
/// autres langues du jeu, italien, espagnol…, prennent les textes anglais). Chaque clé FD1 devient
/// « FD1_&lt;mission&gt;_&lt;clé&gt; » : objectifs (scenario), messages (Warnings&lt;mission&gt;), textes des cinématiques
/// (&lt;mission&gt;_CutScenes), présentation, victoires et défaites (&lt;mission&gt;_Intro). « &lt;BR&gt; » devient un retour à la ligne.
/// </summary>
public sealed partial class MissionConverter
{
    /// <summary>Langues du moteur → dossier et extension des textes FD1.</summary>
    private static readonly Dictionary<string, (string dir, string ext)> Fd1Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        { "FRA", ("Fra", "fra") },
        { "ENG", ("Eng", "eng") },
        { "GER", ("ger", "ger") },
    };

    /// <summary>Textes FD1 d'une langue que FD1 n'a pas (italien, espagnol…).</summary>
    private static readonly (string dir, string ext) Fd1DefaultLanguage = ("Eng", "eng");

    /// <summary>Mot ajouté aux objectifs facultatifs, par langue.</summary>
    private static readonly Dictionary<string, string> OptionalWord = new(StringComparer.OrdinalIgnoreCase)
    {
        { "FRA", "facultatif" },
        { "ENG", "optional" },
        { "GER", "optional" },
    };

    /// <summary>Clé de texte du moteur pour une clé FD1 de la mission (avec « &amp; » : à traduire).</summary>
    private string TextKey(string fd1Key) => $"&FD1_{Mission}_{fd1Key}";

    /// <summary>La mission a des textes FD1 (au moins une langue écrite).</summary>
    private bool _hasTexts;

    /// <summary>Écrit les fichiers de textes ; rend le nombre de langues écrites.</summary>
    private int WriteTexts()
    {
        string localize = Path.Combine(Data, "Localize");
        if (!Directory.Exists(localize))
            return 0;
        int written = 0;
        List<string> codes = new List<string>();
        // dossiers « Lang_XXX » (parfois « LANG_ITA ») : la casse varie
        foreach (string langDir in Directory.EnumerateDirectories(localize).Where(d => Path.GetFileName(d).StartsWith("Lang_", StringComparison.OrdinalIgnoreCase)))
        {
            string code = Path.GetFileName(langDir)[5..].ToUpperInvariant();
            (string dir, string ext) fd1 = Fd1Languages.TryGetValue(code, out (string dir, string ext) known) ? known : Fd1DefaultLanguage;
            string? source = Directory.EnumerateDirectories(Path.Combine(Fd1.Root, "LOCALISATION"))
                .FirstOrDefault(d => Path.GetFileName(d).Equals(fd1.dir, StringComparison.OrdinalIgnoreCase));
            if (source == null)
                continue;
            Dictionary<string, string> texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in new[] { $"Warnings{Mission}", $"{Mission}_CutScenes", $"{Mission}_Intro" })
                foreach ((string key, string text) in ReadFd1Texts(source, file, fd1.ext))
                    texts[key] = text;
            string prefix = Mission.ToUpperInvariant() + "_";
            foreach ((string key, string text) in ReadFd1Texts(source, "scenario", fd1.ext))
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || key.StartsWith("ALL_", StringComparison.OrdinalIgnoreCase))
                    texts[key] = text;
            if (texts.Count == 0)
                continue;
            // objectif des indices : le texte FD1 finit par « (indices trouvés : » que le jeu FD1 complétait ; ici le
            // compteur est celui de l'objectif, et l'objectif est facultatif
            foreach (string key in texts.Keys.Where(k => k.EndsWith("_DES_S1O1", StringComparison.OrdinalIgnoreCase) || k.EndsWith("_DES_S2O2", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                string t = texts[key];
                int cut = t.IndexOf('\n');
                if (cut > 0)
                    t = t[..cut].TrimEnd();
                t = t.TrimEnd('.', ' ');
                texts[key + "_SEUL"] = key.EndsWith("_DES_S1O1", StringComparison.OrdinalIgnoreCase) && OptionalWord.TryGetValue(Fd1Languages.ContainsKey(code) ? code : "ENG", out string? word) ? $"{t} ({word})" : t;
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("#FILE_VERSION 3\r\n");
            sb.Append($"#= Fire Department 1 : {Mission} (textes convertis de LOCALISATION\\{fd1.dir})\r\n");
            foreach ((string key, string text) in texts.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append($"#FIELD_ID &FD1_{Mission}_{key}\r\n{text.Replace("\n", "\r\n")}\r\n");
            File.WriteAllBytes(Path.Combine(langDir, $"FD1_{Mission}.loc"), Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(sb.ToString())).ToArray());
            written++;
            codes.Add(Fd1Languages.ContainsKey(code) ? code : code + " (textes anglais)");
            _hasTexts = true;
        }
        Report.Add($"textes : {written} langue(s) : {string.Join(", ", codes)} (Data\\Localize\\Lang_…\\FD1_{Mission}.loc)");
        return written;
    }

    /// <summary>
    /// Textes d'un fichier FD1 (UTF-16 : lignes FIELD_ID="clé" puis TEXT="texte") ; « &lt;BR&gt; » → retour à la ligne.
    /// Fichier absent : rien.
    /// </summary>
    private static IEnumerable<(string key, string text)> ReadFd1Texts(string dir, string name, string ext)
    {
        string? path = Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals($"{name}.{ext}", StringComparison.OrdinalIgnoreCase));
        if (path == null)
            yield break;
        string? key = null;
        foreach (string raw in File.ReadAllText(path, Encoding.Unicode).Split('\n'))
        {
            string line = raw.Trim('\r', ' ', '\t');
            Match k = Regex.Match(line, "^FIELD_ID=\"(.*)\"$");
            if (k.Success)
            {
                key = k.Groups[1].Value;
                continue;
            }
            Match t = Regex.Match(line, "^TEXT=\"(.*)\"$");
            if (t.Success && key != null)
            {
                string text = Regex.Replace(t.Groups[1].Value, "<BR>", "\n", RegexOptions.IgnoreCase).Trim();
                yield return (key, text);
                key = null;
            }
        }
    }
}
