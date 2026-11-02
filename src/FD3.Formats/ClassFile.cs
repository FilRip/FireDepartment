using System.Globalization;
using System.Text;

namespace FD3.Formats;

/// <summary>
/// Fichiers de configuration (.class, .cfg, .mis) : INI à sections avec héritage par [#INCLUDE].
/// Les clés « X#TYPE » décrivent le type et les bornes d'un paramètre ; elles sont conservées à part.
/// </summary>
public sealed class ClassFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _types = new(StringComparer.OrdinalIgnoreCase);
    public string Path { get; private set; } = "";

    public IEnumerable<string> Sections => _values.Keys;

    public string? Get(string section, string key) =>
        _values.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : null;

    public float GetFloat(string section, string key, float fallback = 0f) =>
        float.TryParse(Get(section, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;

    public int GetInt(string section, string key, int fallback = 0) =>
        int.TryParse(Get(section, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

    public bool GetBool(string section, string key, bool fallback = false) => GetInt(section, key, fallback ? 1 : 0) != 0;

    public IReadOnlyDictionary<string, string> Section(string name) =>
        _values.TryGetValue(name, out var s) ? s : new Dictionary<string, string>();

    public string? TypeOf(string section, string key) =>
        _types.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : null;

    /// <summary>Charge un fichier et ses inclusions. relPath est relatif à Data (ex. « Design\Hotpoint\ground_L.class »).</summary>
    public static ClassFile Load(GameFiles files, string relPath, Dictionary<string, ClassFile>? cache = null)
    {
        cache ??= new(StringComparer.OrdinalIgnoreCase);
        relPath = Pak.Normalize(relPath);
        if (cache.TryGetValue(relPath, out var c)) return c;
        var bytes = files.Read(relPath);
        var result = new ClassFile { Path = relPath };
        cache[relPath] = result;
        var dir = relPath.Contains('\\') ? relPath[..relPath.LastIndexOf('\\')] : "";
        var own = Parse(Encoding.Latin1.GetString(bytes), out var includes);
        foreach (var inc in includes)
        {
            var incPath = Combine(dir, inc);
            if (files.TryRead(incPath) == null) continue;
            result.Merge(Load(files, incPath, cache));
        }
        result.Merge(own);
        return result;
    }

    public static ClassFile FromText(string text) => Parse(text, out _);

    private static string Combine(string dir, string rel)
    {
        var parts = new List<string>(dir.Split('\\', StringSplitOptions.RemoveEmptyEntries));
        foreach (var seg in rel.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (seg != ".") parts.Add(seg);
        }
        return string.Join('\\', parts);
    }

    private void Merge(ClassFile other)
    {
        foreach (var (sec, kv) in other._values)
        {
            if (!_values.TryGetValue(sec, out var dst)) _values[sec] = dst = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in kv) dst[k] = v;
        }
        foreach (var (sec, kv) in other._types)
        {
            if (!_types.TryGetValue(sec, out var dst)) _types[sec] = dst = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in kv) dst[k] = v;
        }
    }

    private static ClassFile Parse(string text, out List<string> includes)
    {
        includes = new();
        var cf = new ClassFile();
        string section = "";
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            int sc = line.IndexOf(';'); if (sc >= 0) line = line[..sc];
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '[' && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim(); var val = line[(eq + 1)..].Trim().Trim('"');
            if (section.Equals("#INCLUDE", StringComparison.OrdinalIgnoreCase)) { includes.Add(val); continue; }
            var target = key.EndsWith("#TYPE", StringComparison.OrdinalIgnoreCase) ? cf._types : cf._values;
            if (target == cf._types) key = key[..^5];
            if (!target.TryGetValue(section, out var s)) target[section] = s = new(StringComparer.OrdinalIgnoreCase);
            s[key] = val;
        }
        return cf;
    }
}
