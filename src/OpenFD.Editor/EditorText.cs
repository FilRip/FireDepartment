namespace OpenFD.MG;

/// <summary>
/// Textes de l'éditeur de missions : section [EDITOR] des fichiers du dossier « languages » (FRA.ini, ENG.ini…). Une clé
/// absente ou vide : le texte de FRA.ini, sinon le texte français écrit dans le code.
/// </summary>
public static class EditorText
{
    /// <summary>Langue choisie (« FRA », « ENG »…) : celle du jeu.</summary>
    public static string Language { get; set; } = "FRA";
    private static readonly Dictionary<string, Dictionary<string, string>> Files = new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> Section(string lang)
    {
        if (Files.TryGetValue(lang, out Dictionary<string, string>? d))
            return d;
        d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string dir = Path.Combine(AppContext.BaseDirectory, "languages");
        string? file = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.ini").FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(lang, StringComparison.OrdinalIgnoreCase)) : null;
        bool inside = false;
        if (file != null)
            foreach (string raw in File.ReadAllLines(file, System.Text.Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inside = line.Equals("[EDITOR]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                int eq = line.IndexOf('=');
                if (!inside || line.Length == 0 || line.StartsWith(';') || eq <= 0)
                    continue;
                string v = line[(eq + 1)..].Trim();
                if (v.Length > 1 && v.StartsWith('"') && v.EndsWith('"'))
                    v = v[1..^1];
                if (v.Length > 0)
                    d[line[..eq].Trim()] = v.Replace("\\n", "\n");
            }
        return Files[lang] = d;
    }

    /// <summary>Texte de la clé ; <paramref name="french"/> si aucun fichier ne l'a.</summary>
    public static string T(string key, string french) =>
        Section(Language).TryGetValue(key, out string? v) ? v : Section("FRA").TryGetValue(key, out v) ? v : french;

    /// <summary>Texte avec des valeurs ({0}, {1}…).</summary>
    public static string T(string key, string french, params object?[] args)
    {
        string f = T(key, french);
        try
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, f, args);
        }
        catch (FormatException)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, french, args);
        }
    }
}
