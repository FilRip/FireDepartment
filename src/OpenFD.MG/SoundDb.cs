using System.Numerics;
using System.Text.RegularExpressions;

namespace OpenFD.MG;

/// <summary>
/// Base de sons du jeu (Data\Sound\SoundDB.INI et ses #INCLUDE) : pour chaque étiquette, le fichier, le volume,
/// la boucle, le son 3D et ses distances d'atténuation. Lue telle quelle dans l'installation du joueur.
/// </summary>
public sealed class SoundDb
{
    public sealed record Entry(string Label, string File, float Volume, bool Loop, bool Use3D, float MinDist, float MaxDist, int Priority, float StartDelay);
    public Dictionary<string, Entry> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, Entry> _orig = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Dossiers des voix (langue choisie d'abord, puis Data\Sound\Vocal) : pour les voix absentes de la base.</summary>
    private readonly List<string> _vocalDirs = new();
    private string _baseVocal = "";

    /// <summary>
    /// Étiquette de la base, ou voix « Vocal\étiquette.wav » que la base n'a pas (le jeu a mis en commentaire
    /// VOCAL_ACKNOWLEDGE1…6 dans SoundDB_Vocal.INI alors que les fichiers sont là).
    /// </summary>
    public bool TryGet(string label, out Entry e)
    {
        if (Entries.TryGetValue(label, out e!))
            return true;
        foreach (string dir in _vocalDirs)
        {
            if (!Directory.Exists(dir))
                continue;
            string? f = Directory.EnumerateFiles(dir).FirstOrDefault(x => Path.GetFileNameWithoutExtension(x).Equals(label, StringComparison.OrdinalIgnoreCase) && x.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
            if (f == null)
                continue;
            e = new Entry(label, f, 0.8f, false, false, 700, 4500, 75, -1);
            Entries[label] = e;
            return true;
        }
        return false;
    }

    public static SoundDb Load(string dataDir)
    {
        SoundDb db = new SoundDb();
        string root = Directory.GetParent(dataDir)?.FullName ?? dataDir;
        db.ReadInto(Path.Combine(dataDir, "Sound", "SoundDB.INI"), root);
        db._orig = new(db.Entries, StringComparer.OrdinalIgnoreCase);
        db._baseVocal = Path.Combine(dataDir, "Sound", "Vocal");
        db._vocalDirs.Add(db._baseVocal);
        return db;
    }

    /// <summary>
    /// Voix d'une autre langue : Data\Localize\Lang_XXX\Data\Sound (SoundDB_Vocal.INI et Vocal\*.wav) remplace les voix
    /// installées quand le fichier existe ; vide ou langue sans voix : celles de l'installation.
    /// </summary>
    public void ApplyLanguage(string dataDir, string? lang)
    {
        Entries.Clear();
        foreach (KeyValuePair<string, Entry> kv in _orig)
            Entries[kv.Key] = kv.Value;
        _vocalDirs.Clear(); _vocalDirs.Add(_baseVocal);
        if (string.IsNullOrEmpty(lang))
            return;
        string locRoot = Path.Combine(dataDir, "Localize");
        if (!Directory.Exists(locRoot))
            return;
        string? langDir = Directory.EnumerateDirectories(locRoot).FirstOrDefault(d => Path.GetFileName(d).Equals("Lang_" + lang, StringComparison.OrdinalIgnoreCase));
        if (langDir == null)
            return;
        string? Sub(string dir, string name) => Directory.Exists(dir) ? Directory.EnumerateFileSystemEntries(dir).FirstOrDefault(e => Path.GetFileName(e).Equals(name, StringComparison.OrdinalIgnoreCase)) : null;
        string? vocal = Sub(langDir, "Data") is { } d1 && Sub(d1, "Sound") is { } d2 ? d2 : null;
        if (vocal == null)
            return;
        if (Sub(vocal, "SoundDB_Vocal.INI") is { } ini)
            ReadInto(ini, langDir);
        string? vocalDir = Sub(vocal, "Vocal");
        if (vocalDir != null)
            _vocalDirs.Insert(0, vocalDir);
        if (vocalDir == null)
            return;
        Dictionary<string, string> files = Directory.EnumerateFiles(vocalDir).ToDictionary(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (Entry e in Entries.Values.ToList())
            if (files.TryGetValue(Path.GetFileName(e.File), out string? f) && !string.Equals(f, e.File, StringComparison.OrdinalIgnoreCase)
                && e.File.Replace('\\', '/').Contains("/Vocal/", StringComparison.OrdinalIgnoreCase))
            {
                Entries[e.Label] = e with { File = f };
                n++;
            }
        Console.WriteLine($"Voix : {lang} ({n} sons remplacés)");
    }

    private void ReadInto(string iniPath, string root)
    {
        SoundDb db = this;
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Read(string file)
        {
            string? path = FindFile(file);
            if (path == null || !seen.Add(path))
                return;
            string? section = null; Dictionary<string, string> keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Flush()
            {
                if (section == null)
                    return;
                if (section.Equals("#INCLUDE", StringComparison.OrdinalIgnoreCase))
                    foreach (string v in keys.Values)
                        Read(Path.Combine(Path.GetDirectoryName(path)!, v.Replace('/', Path.DirectorySeparatorChar).TrimStart('.', '\\', '/')));
                else if (keys.TryGetValue("FILENAME", out string? fn))
                {
                    float F(string k, float d) => keys.TryGetValue(k, out string? s) && float.TryParse(s.TrimEnd('f', 'F', ';'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) ? x : d;
                    string full = Resolve(root, fn);
                    db.Entries[section] = new Entry(section, full, F("VOLUME", 100) / 100f, F("LOOP", 0) != 0, F("USE_3D", 0) != 0,
                        F("3D_MIN_DISTANCE", 700), F("3D_MAX_DISTANCE", 4500), (int)F("PRIORITY", 50), F("START_DELAY", -1));
                }
                keys.Clear();
            }
            foreach (string raw in File.ReadAllLines(path, System.Text.Encoding.Latin1))
            {
                string line = raw.Trim();
                int sc = line.IndexOf(';');
                if (sc == 0)
                    continue;
                if (line.StartsWith('[') && line.Contains(']'))
                {
                    Flush();
                    section = line[1..line.IndexOf(']')].Trim();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0 || section == null)
                    continue;
                string val = line[(eq + 1)..].Trim();
                int c2 = val.IndexOf(';');
                if (c2 >= 0 && !val.StartsWith('"'))
                    val = val[..c2].Trim();
                keys[line[..eq].Trim()] = val.Trim('"', ' ', '\t');
            }
            Flush();
        }
        string? FindFile(string p)
        {
            if (File.Exists(p))
                return p;
            string? dir = Path.GetDirectoryName(p); string name = Path.GetFileName(p);
            if (dir == null || !Directory.Exists(dir))
                return null;
            return Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        // chemin « data/Sound/… » relatif au dossier du jeu ; casse ignorée (utile hors de Windows)
        static string Resolve(string root, string rel)
        {
            string cur = root;
            foreach (string part in rel.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
            {
                string next = Path.Combine(cur, part);
                if (!File.Exists(next) && !Directory.Exists(next) && Directory.Exists(cur))
                    next = Directory.EnumerateFileSystemEntries(cur).FirstOrDefault(e => Path.GetFileName(e).Equals(part, StringComparison.OrdinalIgnoreCase)) ?? next;
                cur = next;
            }
            return cur;
        }
        Read(iniPath);
    }
}
