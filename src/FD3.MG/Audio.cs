using System.Numerics;
using System.Text.RegularExpressions;

namespace FD3.MG;

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
        if (Entries.TryGetValue(label, out e!)) return true;
        foreach (var dir in _vocalDirs)
        {
            if (!Directory.Exists(dir)) continue;
            var f = Directory.EnumerateFiles(dir).FirstOrDefault(x => Path.GetFileNameWithoutExtension(x).Equals(label, StringComparison.OrdinalIgnoreCase) && x.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
            if (f == null) continue;
            e = new Entry(label, f, 0.8f, false, false, 700, 4500, 75, -1);
            Entries[label] = e;
            return true;
        }
        return false;
    }

    public static SoundDb Load(string dataDir)
    {
        var db = new SoundDb();
        var root = Directory.GetParent(dataDir)?.FullName ?? dataDir;
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
        foreach (var kv in _orig) Entries[kv.Key] = kv.Value;
        _vocalDirs.Clear(); _vocalDirs.Add(_baseVocal);
        if (string.IsNullOrEmpty(lang)) return;
        var locRoot = Path.Combine(dataDir, "Localize");
        if (!Directory.Exists(locRoot)) return;
        var langDir = Directory.EnumerateDirectories(locRoot).FirstOrDefault(d => Path.GetFileName(d).Equals("Lang_" + lang, StringComparison.OrdinalIgnoreCase));
        if (langDir == null) return;
        string? Sub(string dir, string name) => Directory.Exists(dir) ? Directory.EnumerateFileSystemEntries(dir).FirstOrDefault(e => Path.GetFileName(e).Equals(name, StringComparison.OrdinalIgnoreCase)) : null;
        var vocal = Sub(langDir, "Data") is { } d1 && Sub(d1, "Sound") is { } d2 ? d2 : null;
        if (vocal == null) return;
        if (Sub(vocal, "SoundDB_Vocal.INI") is { } ini) ReadInto(ini, langDir);
        var vocalDir = Sub(vocal, "Vocal");
        if (vocalDir != null) _vocalDirs.Insert(0, vocalDir);
        if (vocalDir == null) return;
        var files = Directory.EnumerateFiles(vocalDir).ToDictionary(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (var e in Entries.Values.ToList())
            if (files.TryGetValue(Path.GetFileName(e.File), out var f) && !string.Equals(f, e.File, StringComparison.OrdinalIgnoreCase)
                && e.File.Replace('\\', '/').Contains("/Vocal/", StringComparison.OrdinalIgnoreCase))
            { Entries[e.Label] = e with { File = f }; n++; }
        Console.WriteLine($"Voix : {lang} ({n} sons remplacés)");
    }

    private void ReadInto(string iniPath, string root)
    {
        var db = this;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Read(string file)
        {
            var path = FindFile(file);
            if (path == null || !seen.Add(path)) return;
            string? section = null; var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Flush()
            {
                if (section == null) return;
                if (section.Equals("#INCLUDE", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var v in keys.Values) Read(Path.Combine(Path.GetDirectoryName(path)!, v.Replace('/', Path.DirectorySeparatorChar).TrimStart('.', '\\', '/')));
                }
                else if (keys.TryGetValue("FILENAME", out var fn))
                {
                    float F(string k, float d) => keys.TryGetValue(k, out var s) && float.TryParse(s.TrimEnd('f', 'F', ';'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : d;
                    var full = Resolve(root, fn);
                    db.Entries[section] = new Entry(section, full, F("VOLUME", 100) / 100f, F("LOOP", 0) != 0, F("USE_3D", 0) != 0,
                        F("3D_MIN_DISTANCE", 700), F("3D_MAX_DISTANCE", 4500), (int)F("PRIORITY", 50), F("START_DELAY", -1));
                }
                keys.Clear();
            }
            foreach (var raw in File.ReadAllLines(path, System.Text.Encoding.Latin1))
            {
                var line = raw.Trim();
                int sc = line.IndexOf(';'); if (sc == 0) continue;
                if (line.StartsWith('[') && line.Contains(']')) { Flush(); section = line[1..line.IndexOf(']')].Trim(); continue; }
                int eq = line.IndexOf('='); if (eq <= 0 || section == null) continue;
                var val = line[(eq + 1)..].Trim();
                int c2 = val.IndexOf(';'); if (c2 >= 0 && !val.StartsWith('"')) val = val[..c2].Trim();
                keys[line[..eq].Trim()] = val.Trim('"', ' ', '\t');
            }
            Flush();
        }
        string? FindFile(string p)
        {
            if (File.Exists(p)) return p;
            var dir = Path.GetDirectoryName(p); var name = Path.GetFileName(p);
            if (dir == null || !Directory.Exists(dir)) return null;
            return Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        // chemin « data/Sound/… » relatif au dossier du jeu ; casse ignorée (utile hors de Windows)
        static string Resolve(string root, string rel)
        {
            var cur = root;
            foreach (var part in rel.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = Path.Combine(cur, part);
                if (!File.Exists(next) && !Directory.Exists(next) && Directory.Exists(cur))
                    next = Directory.EnumerateFileSystemEntries(cur).FirstOrDefault(e => Path.GetFileName(e).Equals(part, StringComparison.OrdinalIgnoreCase)) ?? next;
                cur = next;
            }
            return cur;
        }
        Read(iniPath);
    }
}

/// <summary>Son décodé : échantillons 16 bits (mono ou stéréo entrelacé) et fréquence.</summary>
public sealed class SoundData
{
    public short[] Samples = Array.Empty<short>();
    public int Channels = 1, Rate = 22050;
    public int Frames => Samples.Length / Channels;

    /// <summary>WAV PCM 8 ou 16 bits (les fichiers du jeu sont en 16 bits mono 22 050 Hz). Autres formats : null.</summary>
    public static SoundData? LoadWav(string path)
    {
        try
        {
            var d = File.ReadAllBytes(path);
            if (d.Length < 44 || d[0] != 'R' || d[8] != 'W') return null;
            int p = 12, fmt = 0, ch = 1, rate = 22050, bits = 16;
            while (p + 8 <= d.Length)
            {
                var id = System.Text.Encoding.ASCII.GetString(d, p, 4); int sz = BitConverter.ToInt32(d, p + 4);
                if (id == "fmt ") { fmt = BitConverter.ToUInt16(d, p + 8); ch = BitConverter.ToUInt16(d, p + 10); rate = BitConverter.ToInt32(d, p + 12); bits = BitConverter.ToUInt16(d, p + 22); }
                else if (id == "data")
                {
                    if (fmt != 1 || ch < 1 || ch > 2) return null;
                    sz = Math.Min(sz, d.Length - p - 8);
                    var s = new SoundData { Channels = ch, Rate = rate };
                    if (bits == 16) { s.Samples = new short[sz / 2]; Buffer.BlockCopy(d, p + 8, s.Samples, 0, s.Samples.Length * 2); }
                    else if (bits == 8) { s.Samples = new short[sz]; for (int i = 0; i < sz; i++) s.Samples[i] = (short)((d[p + 8 + i] - 128) << 8); }
                    else return null;
                    return s;
                }
                p += 8 + sz + (sz & 1);
            }
        }
        catch { }
        return null;
    }
}

/// <summary>
/// Sons par le moteur audio de MonoGame (OpenAL, sur Windows, Linux et macOS).
/// Sons ponctuels (2D ou 3D, atténués et placés gauche-droite d'après la caméra) et sons en boucle
/// entretenus image par image (lance, feu, moteur…) qui s'éteignent en douceur quand on cesse de les entretenir.
/// </summary>
public sealed class Audio : IDisposable
{
    private readonly SoundDb _db;
    public SoundDb Db => _db;
    private readonly Dictionary<string, Microsoft.Xna.Framework.Audio.SoundEffect?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private sealed class Loop { public required Microsoft.Xna.Framework.Audio.SoundEffectInstance Inst; public required string File; public float Gain, Target; }
    private readonly Dictionary<object, Loop> _loops = new();
    private readonly HashSet<object> _touched = new();
    private readonly List<Microsoft.Xna.Framework.Audio.SoundEffectInstance> _oneShots = new();
    private readonly List<(float at, string label, System.Numerics.Vector3? pos, float vol)> _delayed = new();
    private readonly Random _rng = new();
    private float _clock;
    public System.Numerics.Vector3 Listener; public System.Numerics.Vector3 Right = System.Numerics.Vector3.UnitX; public float ExtraDistance;
    public float Master = 0.8f;
    public bool Enabled { get; private set; }
    public int Count => _db.Entries.Count;

    public Audio(SoundDb db)
    {
        _db = db;
        try { Microsoft.Xna.Framework.Audio.SoundEffect.MasterVolume = 1f; Enabled = db.Entries.Count > 0; }
        catch (Exception e) { Console.WriteLine("Son indisponible : " + e.Message); Enabled = false; }
    }

    private Microsoft.Xna.Framework.Audio.SoundEffect? Effect(SoundDb.Entry e)
    {
        if (_cache.TryGetValue(e.File, out var fx)) return fx;
        fx = null;
        try
        {
            if (SoundData.LoadWav(e.File) is { } d && d.Samples.Length > 0)
            {
                var bytes = new byte[d.Samples.Length * 2];
                Buffer.BlockCopy(d.Samples, 0, bytes, 0, bytes.Length);
                fx = new Microsoft.Xna.Framework.Audio.SoundEffect(bytes, d.Rate, d.Channels == 2 ? Microsoft.Xna.Framework.Audio.AudioChannels.Stereo : Microsoft.Xna.Framework.Audio.AudioChannels.Mono);
            }
        }
        catch (Exception ex) { Console.WriteLine($"Son {e.Label} : {ex.Message}"); Enabled = !(ex is Microsoft.Xna.Framework.Audio.NoAudioHardwareException) && Enabled; }
        return _cache[e.File] = fx;
    }

    // atténuation 3D : pleine jusqu'à MinDist, nulle à MaxDist ; panoramique selon la droite de la caméra
    private (float gain, float pan) Spatial(SoundDb.Entry e, System.Numerics.Vector3? pos)
    {
        if (pos is not { } p || !e.Use3D) return (1, 0);
        var dv = p - Listener; float d = MathF.Sqrt(dv.X * dv.X + dv.Y * dv.Y + ExtraDistance * ExtraDistance);
        float g = d <= e.MinDist ? 1 : d >= e.MaxDist ? 0 : 1 - (d - e.MinDist) / (e.MaxDist - e.MinDist);
        float pan = d > 1 ? Math.Clamp(System.Numerics.Vector3.Dot(dv, Right) / d, -1, 1) * 0.6f : 0;
        return (g, pan);
    }

    /// <summary>Son ponctuel (étiquette de la base de sons).</summary>
    public void Play(string label, System.Numerics.Vector3? pos = null, float volume = 1)
    {
        if (!Enabled || !_db.TryGet(label, out var e)) return;
        if (e.StartDelay > 0) { _delayed.Add((_clock + e.StartDelay / 1000f, label, pos, volume)); return; }
        Start(e, pos, volume);
    }

    private void Start(SoundDb.Entry e, System.Numerics.Vector3? pos, float volume)
    {
        if (Effect(e) is not { } fx) return;
        var (g, pan) = Spatial(e, pos);
        if (g <= 0.01f) return;
        try
        {
            var inst = fx.CreateInstance();
            inst.Volume = Math.Clamp(g * e.Volume * volume * Master, 0, 1); inst.Pan = pan; inst.IsLooped = false;
            inst.Play(); _oneShots.Add(inst);
        }
        catch (Exception ex) { Console.WriteLine("Son : " + ex.Message); }
    }

    /// <summary>Une étiquette au hasard parmi celles qui existent (« VOCAL_SELECTION1 » … « VOCAL_SELECTION6 »).</summary>
    public void PlayAny(IEnumerable<string> labels, System.Numerics.Vector3? pos = null)
    {
        var ok = labels.Where(l => _db.TryGet(l, out _)).ToList();
        if (ok.Count > 0) Play(ok[_rng.Next(ok.Count)], pos);
    }

    /// <summary>Son en boucle entretenu : à appeler à chaque image tant qu'il doit jouer (clé = objet qui l'émet).</summary>
    public void Keep(object key, string label, System.Numerics.Vector3 pos, float volume = 1)
    {
        if (!Enabled || !_db.TryGet(label, out var e) || Effect(e) is not { } fx) return;
        var (g, pan) = Spatial(e, pos);
        _touched.Add(key);
        if (!_loops.TryGetValue(key, out var l) || l.File != e.File)
        {
            if (l != null) { l.Inst.Stop(); l.Inst.Dispose(); _loops.Remove(key); }
            if (g <= 0.01f) return;
            try
            {
                var inst = fx.CreateInstance(); inst.IsLooped = true; inst.Volume = 0; inst.Play();
                _loops[key] = l = new Loop { Inst = inst, File = e.File };
            }
            catch { return; }
        }
        l.Target = g * e.Volume * volume * Master;
        l.Inst.Pan = pan;
    }

    /// <summary>Fin d'image : volumes amenés en douceur vers leur cible ; les boucles non entretenues s'éteignent.</summary>
    public void EndFrame(float dt = 1 / 60f)
    {
        _clock += dt;
        for (int i = _delayed.Count - 1; i >= 0; i--)
            if (_clock >= _delayed[i].at && _db.TryGet(_delayed[i].label, out var e)) { Start(e, _delayed[i].pos, _delayed[i].vol); _delayed.RemoveAt(i); }
        foreach (var (k, l) in _loops.ToList())
        {
            if (!_touched.Contains(k)) l.Target = 0;
            l.Gain += (l.Target - l.Gain) * MathF.Min(1, dt * 6);
            if (l.Target == 0 && l.Gain < 0.005f) { l.Inst.Stop(); l.Inst.Dispose(); _loops.Remove(k); continue; }
            l.Inst.Volume = Math.Clamp(l.Gain, 0, 1);
        }
        _touched.Clear();
        _oneShots.RemoveAll(s => { if (s.State == Microsoft.Xna.Framework.Audio.SoundState.Stopped) { s.Dispose(); return true; } return false; });
    }

    /// <summary>Pause du jeu : sons en cours suspendus (true), puis repris (false).</summary>
    public void PauseAll(bool pause)
    {
        foreach (var l in _loops.Values) { if (pause) l.Inst.Pause(); else if (l.Inst.State == Microsoft.Xna.Framework.Audio.SoundState.Paused) l.Inst.Resume(); }
        foreach (var o in _oneShots) { if (pause) o.Pause(); else if (o.State == Microsoft.Xna.Framework.Audio.SoundState.Paused) o.Resume(); }
    }

    public void StopAll()
    {
        foreach (var l in _loops.Values) { l.Inst.Stop(); l.Inst.Dispose(); }
        _loops.Clear();
        foreach (var s in _oneShots) { s.Stop(); s.Dispose(); }
        _oneShots.Clear(); _delayed.Clear();
    }

    public void Dispose() { StopAll(); foreach (var fx in _cache.Values) fx?.Dispose(); _cache.Clear(); }
}
