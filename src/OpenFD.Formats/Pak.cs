using System.IO.Compression;
using System.Text;

namespace OpenFD.Formats;

/// <summary>
/// Archive .pak / .bank de Fire Department 3 : un zip classique dont les signatures
/// commencent par « QL » au lieu de « PK ». Les noms internes utilisent « \ ».
/// </summary>
public sealed class Pak
{
    public sealed record Entry(string Name, int Method, uint CompressedSize, uint Size, uint LocalHeaderOffset);

    private readonly byte[] _data;
    private readonly Dictionary<string, Entry> _byName = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<Entry> Entries { get; }
    public string Path { get; }

    public Pak(string path) : this(File.ReadAllBytes(path), path) { }

    public Pak(byte[] data, string path = "")
    {
        _data = data; Path = path;
        int eocd = -1;
        for (int i = data.Length - 22; i >= Math.Max(0, data.Length - 70_000); i--)
            if ((data[i] == 'Q' || data[i] == 'P') && (data[i + 1] == 'L' || data[i + 1] == 'K') && data[i + 2] == 5 && data[i + 3] == 6)
            {
                eocd = i;
                break;
            }
        if (eocd < 0)
            throw new InvalidDataException("Archive non reconnue : " + path);
        int n = U16(eocd + 10);
        int p = (int)U32(eocd + 16);
        List<Entry> list = new List<Entry>(n);
        for (int i = 0; i < n; i++)
        {
            int fl = U16(p + 28), xl = U16(p + 30), cl = U16(p + 32);
            Entry e = new Entry(Encoding.Latin1.GetString(data, p + 46, fl), U16(p + 10), U32(p + 20), U32(p + 24), U32(p + 42));
            list.Add(e);
            _byName[Normalize(e.Name)] = e;
            p += 46 + fl + xl + cl;
        }
        Entries = list;
    }

    public static string Normalize(string name) => name.Replace('/', '\\').TrimStart('\\');

    public bool Contains(string name) => _byName.ContainsKey(Normalize(name));

    public byte[] Read(string name)
    {
        if (!_byName.TryGetValue(Normalize(name), out Entry? e))
            throw new FileNotFoundException(name + " absent de " + Path);
        return Read(e);
    }

    public byte[] Read(Entry e)
    {
        int p = (int)e.LocalHeaderOffset;
        int start = p + 30 + U16(p + 26) + U16(p + 28);
        if (e.Method == 0)
            return _data.AsSpan(start, (int)e.CompressedSize).ToArray();
        if (e.Method != 8)
            throw new NotSupportedException("Compression " + e.Method);
        using MemoryStream src = new MemoryStream(_data, start, (int)e.CompressedSize, false);
        using DeflateStream z = new DeflateStream(src, CompressionMode.Decompress);
        byte[] outBuf = new byte[e.Size];
        int read = 0;
        while (read < outBuf.Length)
        {
            int k = z.Read(outBuf, read, outBuf.Length - read);
            if (k == 0)
                break;
            read += k;
        }
        return outBuf;
    }

    private int U16(int p) => _data[p] | _data[p + 1] << 8;
    private uint U32(int p) => BitConverter.ToUInt32(_data, p);
}

/// <summary>
/// Système de fichiers du jeu : les fichiers libres de Data\ sont prioritaires sur les archives,
/// comme dans FD3 (c'est ainsi que la mission Vegas est ajoutée).
/// </summary>
public sealed class GameFiles
{
    public string DataDir { get; }
    private readonly List<(string prefix, Pak pak)> _paks = new();

    public GameFiles(string dataDir)
    {
        DataDir = dataDir;
        void Add(string rel, string prefix)
        {
            string p = System.IO.Path.Combine(dataDir, rel);
            if (File.Exists(p))
                _paks.Add((prefix, new Pak(p)));
        }
        Add("design.pak", "Design\\");
        foreach (string f in new[] { "placeable", "anim", "effect", "interface", "unit" }) Add(System.IO.Path.Combine("Gfx", f + ".pak"), "Gfx\\" + char.ToUpper(f[0]) + f[1..] + "\\");
        // graphismes des missions : Gfx\Mission\<mission>.pak, vu comme Gfx\Mission\<mission>\…
        string mdir = System.IO.Path.Combine(dataDir, "Gfx", "Mission");
        if (Directory.Exists(mdir))
            foreach (string f in Directory.EnumerateFiles(mdir, "*.pak"))
                _paks.Add(("Gfx\\Mission\\" + System.IO.Path.GetFileNameWithoutExtension(f) + "\\", new Pak(f)));
        // textures des missions (DDS sans extension) : <mission>_tex_high.bank (ou _med, _low : qualité choisie), vu comme Gfx\Textures\<mission>\…
        _missionDir = mdir;
        LoadCache();
        LoadBanks();
    }

    // cache de textures du jeu (véhicules, pompiers…) : Gfx\TextureCache\High, Medium ou Low selon la qualité choisie
    private (string prefix, Pak pak)? _cache;
    private void LoadCache()
    {
        if (_cache is { } old)
            _paks.Remove(old);
        _cache = null;
        string folder = _quality switch { "low" => "Low", "med" or "medium" => "Medium", _ => "High" };
        string root = System.IO.Path.Combine(DataDir, "Gfx", "TextureCache");
        string cache = System.IO.Path.Combine(root, folder, "Cache.pak");
        if (!File.Exists(cache))
            cache = System.IO.Path.Combine(root, "High", "Cache.pak");
        if (!File.Exists(cache))
            return;
        _cache = ("Gfx\\Textures\\_cache\\", new Pak(cache));
        _paks.Add(_cache.Value);
    }

    private string _missionDir = "";
    private string _quality = "high";
    private readonly List<(string prefix, Pak pak)> _banks = new();

    /// <summary>Qualité des textures des missions : « high », « med » ou « low » (fichiers &lt;mission&gt;_tex_&lt;qualité&gt;.bank).</summary>
    public string TextureQuality
    {
        get => _quality;
        set
        {
            string q = (value ?? "high").ToLowerInvariant();
            if (q == _quality)
                return;
            _quality = q;
            LoadCache();
            LoadBanks();
        }
    }

    private void LoadBanks()
    {
        foreach ((string prefix, Pak pak) b in _banks) _paks.Remove(b);
        _banks.Clear();
        if (!Directory.Exists(_missionDir))
            return;
        foreach (string f in Directory.EnumerateFiles(_missionDir, "*_tex_high.bank"))
        {
            string m = System.IO.Path.GetFileNameWithoutExtension(f);
            string name = m[..m.IndexOf("_tex_", StringComparison.OrdinalIgnoreCase)];
            string wanted = System.IO.Path.Combine(_missionDir, name + "_tex_" + _quality + ".bank");
            string file = File.Exists(wanted) ? wanted : f;
            (string, Pak) e = ("Gfx\\Textures\\" + name + "\\", new Pak(file));
            _banks.Add(e);
            // avant le cache de textures du jeu (même ordre qu'au départ)
            int at = _paks.FindIndex(p => p.prefix.StartsWith("Gfx\\Textures\\_cache", StringComparison.OrdinalIgnoreCase));
            if (at < 0)
                _paks.Add(e);
            else
                _paks.Insert(at, e);
        }
    }

    /// <summary>Chemin relatif à Data, par exemple « Design\Hotpoint\ground_L.class ».</summary>
    public byte[]? TryRead(string rel)
    {
        rel = Pak.Normalize(rel);
        string loose = System.IO.Path.Combine(DataDir, rel.Replace('\\', System.IO.Path.DirectorySeparatorChar));
        if (File.Exists(loose))
            return File.ReadAllBytes(loose);
        if (LooseIgnoreCase(rel) is { } lp)
            return File.ReadAllBytes(lp);
        foreach ((string? prefix, Pak? pak) in _paks)
            if (rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string inner = rel[prefix.Length..];
                if (pak.Contains(inner))
                    return pak.Read(inner);
            }
        return null;
    }

    // fichier à part dans Data, sans tenir compte des majuscules (Linux, macOS : « FireStation » / « Firestation »)
    private string? LooseIgnoreCase(string rel)
    {
        if (System.IO.Path.DirectorySeparatorChar == '\\')
            return null;   // Windows : déjà insensible à la casse
        string cur = DataDir;
        foreach (string part in rel.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(cur))
                return null;
            string? next = Directory.EnumerateFileSystemEntries(cur).FirstOrDefault(e => System.IO.Path.GetFileName(e).Equals(part, StringComparison.OrdinalIgnoreCase));
            if (next == null)
                return null;
            cur = next;
        }
        return File.Exists(cur) ? cur : null;
    }

    public byte[] Read(string rel) => TryRead(rel) ?? throw new FileNotFoundException(rel);

    public IEnumerable<string> List(string prefixRel)
    {
        prefixRel = Pak.Normalize(prefixRel);
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // fichiers posés à part dans Data (ajoutés par un patch du jeu : la mission Vegas, ses classes, ses équipes…)
        int cut = prefixRel.LastIndexOf('\\');
        string dirRel = cut >= 0 ? prefixRel[..cut] : "";
        string dir = System.IO.Path.Combine(DataDir, dirRel.Replace('\\', System.IO.Path.DirectorySeparatorChar));
        if (dirRel.Length > 0 && Directory.Exists(dir))
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string rel = System.IO.Path.GetRelativePath(DataDir, f).Replace(System.IO.Path.DirectorySeparatorChar, '\\');
                if (rel.StartsWith(prefixRel, StringComparison.OrdinalIgnoreCase) && seen.Add(rel))
                    yield return rel;
            }
        foreach ((string? prefix, Pak? pak) in _paks)
            foreach (Pak.Entry e in pak.Entries)
            {
                string full = prefix + e.Name;
                if (full.StartsWith(prefixRel, StringComparison.OrdinalIgnoreCase) && seen.Add(full))
                    yield return full;
            }
    }
}
