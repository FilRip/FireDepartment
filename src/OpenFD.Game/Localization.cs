namespace OpenFD.Game;

/// <summary>
/// Textes traduits du jeu : Data\Localize\Lang_XXX\*.loc (UTF-16), entrées « #FIELD_ID &amp;clé » suivies du texte.
/// La langue est celle des voix installées : on compare Data\Sound\Vocal aux voix de chaque Lang_XXX\Data\Sound\Vocal
/// (l'anglais n'en a pas : c'est la langue par défaut). FD3_LANGUE=FRA (ENG, GER…) pour la choisir.
/// </summary>
public sealed class Localization
{
    private readonly Dictionary<string, string> _texts = new(StringComparer.OrdinalIgnoreCase);
    public string Language { get; private set; } = "ENG";
    public int Count => _texts.Count;

    /// <summary>Langues disponibles (dossiers Data\Localize\Lang_XXX), en codes « FRA », « ENG »…</summary>
    public static List<string> Available(string dataDir)
    {
        string root = Path.Combine(dataDir, "Localize");
        if (!Directory.Exists(root))
            return new();
        return Directory.EnumerateDirectories(root).Select(Path.GetFileName).Where(n => n!.StartsWith("Lang_", StringComparison.OrdinalIgnoreCase))
            .Select(n => n![5..].ToUpperInvariant()).Distinct().OrderBy(x => x).ToList();
    }

    /// <summary>Langue imposée (options du jeu) ; vide : celle des voix installées.</summary>
    public static string Forced = "";

    public static Localization Load(string dataDir)
    {
        Localization loc = new Localization();
        string root = Path.Combine(dataDir, "Localize");
        if (!Directory.Exists(root))
            return loc;
        Dictionary<string, string> langs = Directory.EnumerateDirectories(root)
            .Select(d => (dir: d, code: Path.GetFileName(d)))
            .Where(x => x.code.StartsWith("Lang_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(x => x.code[5..].ToUpperInvariant(), x => x.dir);
        string? want = (string.IsNullOrEmpty(Forced) ? Environment.GetEnvironmentVariable("FD3_LANGUE") : Forced)?.ToUpperInvariant();
        // sinon la langue du jeu installé (Data\Config\Start.Ini : « Language FRA »), puis celle des voix installées
        if (want == null || !langs.ContainsKey(want))
            want = StartIni(dataDir, "Language")?.ToUpperInvariant();
        if (want == null || !langs.ContainsKey(want))
            want = DetectFromVoices(dataDir, langs);
        if (!langs.TryGetValue(want, out string? langDir))
        {
            // Fire Department 2 n'a pas de Lang_ENG : la première langue présente
            want = langs.ContainsKey("ENG") ? "ENG" : langs.Keys.OrderBy(x => x).FirstOrDefault() ?? "ENG";
            langs.TryGetValue(want, out langDir);
        }
        if (langDir == null)
            return loc;
        loc.Language = want;
        foreach (string f in Directory.EnumerateFiles(langDir, "*.loc"))
            try { loc.Parse(File.ReadAllText(f)); } catch { }
        // textes propres à l'édition (Data\Localize\Lic_FD2\License_FD2.loc : le nom du jeu…)
        string? lic = StartIni(dataDir, "License");
        string? licDir = lic == null ? null : FindDir(root, "Lic_" + lic);
        if (licDir != null)
            foreach (string f in Directory.EnumerateFiles(licDir, "*.loc"))
                try { loc.Parse(File.ReadAllText(f)); } catch { }
        return loc;
    }

    /// <summary>Valeur d'une ligne de Data\Config\Start.Ini (« License FD2 », « Language FRA ») ; null sans fichier.</summary>
    public static string? StartIni(string dataDir, string key)
    {
        string? cfg = Directory.Exists(dataDir) ? FindDir(dataDir, "Config") : null;
        string? file = cfg == null ? null : Directory.EnumerateFiles(cfg).FirstOrDefault(f => Path.GetFileName(f).Equals("Start.ini", StringComparison.OrdinalIgnoreCase));
        if (file == null)
            return null;
        foreach (string raw in File.ReadAllLines(file))
        {
            string[] parts = raw.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                return parts[1].Trim().Trim('"');
        }
        return null;
    }

    private static string DetectFromVoices(string dataDir, Dictionary<string, string> langs)
    {
        string vocal = Path.Combine(dataDir, "Sound", "Vocal");
        if (!Directory.Exists(vocal))
            return "ENG";
        Dictionary<string, long> installed = Directory.EnumerateFiles(vocal, "*.wav").Take(400)
            .ToDictionary(f => Path.GetFileName(f), f => new FileInfo(f).Length, StringComparer.OrdinalIgnoreCase);
        string best = "ENG";
        int bestScore = 0;
        foreach ((string? code, string? dir) in langs)
        {
            string? v = FindDir(dir, "Data", "Sound", "Vocal");
            if (v == null)
                continue;
            int score = 0;
            foreach (string f in Directory.EnumerateFiles(v, "*.wav"))
                if (installed.TryGetValue(Path.GetFileName(f), out long len) && len == new FileInfo(f).Length)
                    score++;
            if (score > bestScore)
            {
                bestScore = score;
                best = code;
            }
        }
        return best;
    }

    // sous-dossiers sans tenir compte de la casse (« Data\Sound » ou « data\sound »)
    private static string? FindDir(string dir, params string[] parts)
    {
        foreach (string p in parts)
        {
            string? next = Directory.EnumerateDirectories(dir).FirstOrDefault(d => Path.GetFileName(d).Equals(p, StringComparison.OrdinalIgnoreCase));
            if (next == null)
                return null;
            dir = next;
        }
        return dir;
    }

    private void Parse(string text)
    {
        string? key = null;
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        void Flush()
        {
            if (key != null)
                _texts[key] = sb.ToString().Trim().Replace("\\n", "\n");
            key = null;
            sb.Clear();
        }
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("#FIELD_ID", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                key = line["#FIELD_ID".Length..].Trim().TrimStart('&');
                continue;
            }
            if (line.StartsWith("#"))
            {
                Flush();
                continue;
            }
            if (key != null)
            {
                if (sb.Length > 0)
                    sb.Append('\n');
                sb.Append(line);
            }
        }
        Flush();
    }

    /// <summary>Texte d'une clé (« &amp;Bank_cat1_m1 » ou « Bank_cat1_m1 ») ; la clé elle-même si elle est inconnue.</summary>
    public string Get(string key)
    {
        string k = key.Trim().TrimStart('&');
        return _texts.TryGetValue(k, out string? t) ? Expand(t, 0) : k;
    }

    // « Voici un &UNIT_FireFighter : » : un mot précédé de « & » dans un texte renvoie à une autre entrée
    // (souvent dans Unit_XXX.loc) ; on le remplace par son texte s'il existe, sinon on le laisse tel quel.
    private static readonly System.Text.RegularExpressions.Regex Token = new(@"&([A-Za-z0-9_]+)");

    private string Expand(string text, int depth)
    {
        if (depth > 4 || text.IndexOf('&') < 0)
            return text;
        return Token.Replace(text, m => _texts.TryGetValue(m.Groups[1].Value, out string? t) ? Expand(t, depth + 1) : m.Value);
    }
}
