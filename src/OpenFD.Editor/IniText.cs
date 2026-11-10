using System.Text.RegularExpressions;

namespace OpenFD.MG;

/// <summary>
/// Lecture et changement d'une valeur dans le texte d'un fichier .mis / .ini, sans toucher au reste (commentaires, ordre,
/// majuscules). Les sections et les clés sont comparées sans tenir compte des majuscules.
/// </summary>
public static class IniText
{
    private static readonly Regex SectionRx = new(@"^\s*\[([^\]]+)\]", RegexOptions.Compiled);

    public static string? Get(string text, string section, string key)
    {
        string[] lines = text.Split('\n');
        string cur = "";
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\r');
            Match sm = SectionRx.Match(line);
            if (sm.Success)
            {
                cur = sm.Groups[1].Value.Trim();
                continue;
            }
            if (!cur.Equals(section, StringComparison.OrdinalIgnoreCase) || KeyOf(line) is not { } k || !k.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            string v = line[(line.IndexOf('=') + 1)..].Trim();
            int c = v.IndexOf(';');
            if (c >= 0 && !v.StartsWith('"'))
                v = v[..c].Trim();
            return v.Trim('"');
        }
        return null;
    }

    /// <summary>Change (ou ajoute) la valeur ; les guillemets d'origine sont gardés.</summary>
    public static string Set(string text, string section, string key, string value)
    {
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        List<string> lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        string cur = "";
        int sectionEnd = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            Match sm = SectionRx.Match(lines[i]);
            if (sm.Success)
            {
                if (cur.Equals(section, StringComparison.OrdinalIgnoreCase))
                    break;
                cur = sm.Groups[1].Value.Trim();
                if (cur.Equals(section, StringComparison.OrdinalIgnoreCase))
                    sectionEnd = i + 1;
                continue;
            }
            if (!cur.Equals(section, StringComparison.OrdinalIgnoreCase))
                continue;
            if (lines[i].Trim().Length > 0 && !lines[i].TrimStart().StartsWith(';') && !lines[i].TrimStart().StartsWith("//"))
                sectionEnd = i + 1;
            if (KeyOf(lines[i]) is not { } k || !k.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            string old = lines[i][(lines[i].IndexOf('=') + 1)..].Trim();
            bool quoted = old.StartsWith('"');
            lines[i] = lines[i][..(lines[i].IndexOf('=') + 1)] + " " + (quoted ? "\"" + value + "\"" : value);
            return string.Join(nl, lines);
        }
        if (sectionEnd < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                lines.Add("");
            lines.Add("[" + section + "]");
            lines.Add(key + " = " + value);
        }
        else
            lines.Insert(sectionEnd, key + " = " + value);
        return string.Join(nl, lines);
    }

    private static string? KeyOf(string line)
    {
        string t = line.Trim();
        if (t.StartsWith(';') || t.StartsWith("//") || t.StartsWith('['))
            return null;
        int eq = t.IndexOf('=');
        return eq > 0 ? t[..eq].Trim() : null;
    }
}
