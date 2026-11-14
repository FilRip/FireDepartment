using System.IO.Compression;
using System.Text;

namespace OpenFD.MG;

/// <summary>
/// « Exporter » : les fichiers d'une mission sauvée dans Data, réunis dans un zip à extraire à la racine du jeu (le
/// dossier qui contient Data) sur un autre PC.
///  • les fichiers de la mission, d'après son nom : Design\Mission\&lt;nom&gt;\, Script\Mission\&lt;nom&gt;\,
///    Gfx\Mission\&lt;nom&gt;.pak et &lt;nom&gt;_tex_*.bank (ou Gfx\Mission\&lt;nom&gt;\, Gfx\Textures\&lt;nom&gt;\),
///    Gfx\Placeable\&lt;nom&gt;\, Localize\&lt;langue&gt;\&lt;nom&gt;.loc, les casernes de renfort du .mis ;
///  • les fichiers posés à part dans Data dont ils se servent (classes, modèles, matériaux, textures, animations…),
///    trouvés en lisant les chemins écrits dans ces fichiers, puis dans ceux trouvés, et ainsi de suite.
/// Les fichiers du jeu, rangés dans ses archives (design.pak, Gfx\*.pak…), n'y sont pas ; ni les sons, vidéos, polices,
/// réglages et traductions du jeu (dossiers Sound, Video, Fonte, Config, Shader, Overlay, Localize), ni les « .bak ».
/// </summary>
internal static class MissionExport
{
    // dossiers du jeu (fichiers d'origine posés à part) : jamais repris d'après une référence
    private static readonly string[] GameRoots =
    {
        "Sound\\", "Video\\", "Fonte\\", "Config\\", "Shader\\", "Overlay\\", "Localize\\", "Save\\", "Gfx\\TextureCache\\",
    };

    // fichiers lus pour y trouver des chemins (textes, niveaux, modèles, matériaux) ; les autres (textures, sons,
    // données des maillages, archives) ne citent pas d'autres fichiers
    private static readonly HashSet<string> Scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".level", ".mis", ".scp", ".class", ".sg", ".mesh", ".stdmat", ".ini", ".cfg", ".txt", ".spt", ".sspray", ".gravity",
    };

    /// <summary>Fichiers de la mission et ceux dont elle a besoin (chemins relatifs à Data, avec « \ »), triés.</summary>
    public static List<string> Collect(string dataDir, string mission)
    {
        List<string> all = new List<string>();
        foreach (string f in Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories))
            all.Add(Path.GetRelativePath(dataDir, f).Replace(Path.DirectorySeparatorChar, '\\'));
        string m = mission.ToLowerInvariant();
        string[] ownDirs =
        {
            $"design\\mission\\{m}\\", $"script\\mission\\{m}\\", $"gfx\\mission\\{m}\\", $"gfx\\textures\\{m}\\",
            $"gfx\\placeable\\{m}\\",
        };
        HashSet<string> chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Queue<string> toScan = new Queue<string>();
        void Take(string rel)
        {
            if (!chosen.Add(rel))
                return;
            if (Scanned.Contains(Path.GetExtension(rel)))
                toScan.Enqueue(rel);
        }
        // fichiers de la mission, d'après son nom
        foreach (string rel in all)
        {
            string low = rel.ToLowerInvariant();
            if (low.EndsWith(".bak"))
                continue;
            string[] parts = low.Split('\\');
            bool own = ownDirs.Any(low.StartsWith)
                || low == $"gfx\\mission\\{m}.pak"
                || (low.StartsWith($"gfx\\mission\\{m}_tex_") && low.EndsWith(".bank"))
                || (parts.Length == 3 && parts[0] == "localize" && parts[2] == m + ".loc");
            if (own)
                Take(rel);
        }
        // fichiers à part que l'on peut citer : ni ceux du jeu, ni les archives, ni ceux des autres missions
        Dictionary<string, List<string>> byLeaf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string rel in all)
        {
            string low = rel.ToLowerInvariant();
            if (GameRoots.Any(r => low.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (low.EndsWith(".bak") || low.EndsWith(".pak") || low.EndsWith(".bank"))
                continue;
            if (OtherMissionFile(low, m))
                continue;
            // (chemins avec « \ » : Path.GetFileName ne les coupe pas hors de Windows)
            string leaf = rel[(rel.LastIndexOf('\\') + 1)..];
            foreach (string key in new[] { leaf, WithoutExtension(leaf) })
            {
                if (!byLeaf.TryGetValue(key, out List<string>? list))
                    byLeaf[key] = list = new List<string>();
                if (!list.Contains(rel))
                    list.Add(rel);
            }
        }
        // les chemins cités, de fichier en fichier
        while (toScan.Count > 0)
        {
            string rel = toScan.Dequeue();
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(Path.Combine(dataDir, rel.Replace('\\', Path.DirectorySeparatorChar)));
            }
            catch (Exception)
            {
                continue;
            }
            string folder = rel.Contains('\\') ? rel[..rel.LastIndexOf('\\')] : "";
            foreach (string token in Tokens(bytes))
                foreach (string found in Resolve(token, folder, byLeaf))
                    Take(found);
        }
        List<string> res = chosen.ToList();
        res.Sort(StringComparer.OrdinalIgnoreCase);
        return res;
    }

    /// <summary>Écrit le zip : chaque fichier sous « Data/… » (un zip déjà là est remplacé).</summary>
    public static long WriteZip(string dataDir, IEnumerable<string> files, string zipPath)
    {
        string tmp = zipPath + ".tmp";
        if (File.Exists(tmp))
            File.Delete(tmp);
        long bytes = 0;
        using (ZipArchive zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            foreach (string rel in files)
            {
                string src = Path.Combine(dataDir, rel.Replace('\\', Path.DirectorySeparatorChar));
                zip.CreateEntryFromFile(src, "Data/" + rel.Replace('\\', '/'), CompressionLevel.Optimal);
                bytes += new FileInfo(src).Length;
            }
        File.Move(tmp, zipPath, true);
        return bytes;
    }

    // fichier d'une autre mission (ses dossiers à elle) : jamais repris
    private static bool OtherMissionFile(string low, string m)
    {
        foreach (string root in new[] { "design\\mission\\", "script\\mission\\", "gfx\\mission\\", "gfx\\textures\\" })
            if (low.StartsWith(root))
            {
                string rest = low[root.Length..];
                int cut = rest.IndexOf('\\');
                return cut < 0 || rest[..cut] != m;
            }
        return false;
    }

    /// <summary>Morceaux de texte lisible (au moins 3 lettres) : les valeurs entre guillemets, après « = »…</summary>
    private static IEnumerable<string> Tokens(byte[] bytes)
    {
        StringBuilder run = new StringBuilder();
        for (int i = 0; i <= bytes.Length; i++)
        {
            byte b = i < bytes.Length ? bytes[i] : (byte)0;
            if (b >= 0x20 && b < 0x7F)
            {
                run.Append((char)b);
                continue;
            }
            if (run.Length >= 3)
                foreach (string piece in run.ToString().Split('"', '=', ',', ';', '(', ')', '\t', '<', '>', '|', '*', '?'))
                {
                    string t = piece.Trim();
                    if (t.Length >= 3)
                        yield return t;
                }
            run.Clear();
        }
    }

    /// <summary>
    /// Fichiers que désigne ce texte : un chemin (« Design\Unit\…\X », « FD1_Villa\objet ») ou un nom de fichier avec son
    /// extension (« fenetre.stdmat », « base_useable.class » : de préférence le plus près du fichier qui le cite). Un mot
    /// seul, sans « \ » ni extension, n'est un fichier que pour une caserne de renfort (Design\FireStation\&lt;mot&gt;.cfg).
    /// </summary>
    private static IEnumerable<string> Resolve(string token, string folder, Dictionary<string, List<string>> byLeaf)
    {
        string t = token.Replace('/', '\\').Replace("\\\\", "\\").Trim('\\', ' ');
        while (t.StartsWith(".\\") || t.StartsWith("..\\"))
            t = t[(t.IndexOf('\\') + 1)..];
        if (t.StartsWith("data\\", StringComparison.OrdinalIgnoreCase))
            t = t[5..];
        if (t.Length < 3)
            return Array.Empty<string>();
        string leaf = t.Contains('\\') ? t[(t.LastIndexOf('\\') + 1)..] : t;
        string ext = Path.GetExtension(leaf);
        bool hasExt = ext.Length >= 2 && ext.Length <= 8 && ext.Skip(1).All(char.IsLetterOrDigit);
        if (!byLeaf.TryGetValue(leaf, out List<string>? candidates))
            return Array.Empty<string>();
        // un mot seul : seulement une caserne de renfort (« Reinforcement1 = FD1_Villa_1 » du .mis : Design\FireStation\…cfg)
        if (!t.Contains('\\') && !hasExt)
            return candidates.Where(c => c.StartsWith("Design\\FireStation\\", StringComparison.OrdinalIgnoreCase) && c.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)).ToList();
        List<string> matches = new List<string>();
        foreach (string c in candidates)
        {
            string key = hasExt && Path.GetExtension(c).Equals(ext, StringComparison.OrdinalIgnoreCase) ? c : WithoutExtension(c);
            if (key.Equals(t, StringComparison.OrdinalIgnoreCase) || key.EndsWith("\\" + t, StringComparison.OrdinalIgnoreCase))
                matches.Add(c);
        }
        // un nom seul trouvé à plusieurs endroits : le plus proche du fichier qui le cite (son dossier, un sous-dossier
        // comme « Shaders », sinon le plus long début de chemin commun)
        if (!t.Contains('\\') && matches.Count > 1)
        {
            int best = matches.Max(c => Common(c, folder));
            return matches.Where(c => Common(c, folder) == best).ToList();
        }
        return matches;
    }

    // nombre de dossiers communs au début des deux chemins
    private static int Common(string rel, string folder)
    {
        string[] a = rel.Split('\\');
        string[] b = folder.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        int n = 0;
        while (n < a.Length - 1 && n < b.Length && a[n].Equals(b[n], StringComparison.OrdinalIgnoreCase))
            n++;
        return n;
    }

    private static string WithoutExtension(string rel)
    {
        int dot = rel.LastIndexOf('.');
        return dot > rel.LastIndexOf('\\') ? rel[..dot] : rel;
    }
}
