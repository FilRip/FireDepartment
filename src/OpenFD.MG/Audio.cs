using System.Numerics;
using System.Text.RegularExpressions;

namespace OpenFD.MG;

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
    private sealed class Loop { public required Microsoft.Xna.Framework.Audio.SoundEffectInstance Inst;
        public required string File;
        public float Gain, Target; }
    private readonly Dictionary<object, Loop> _loops = new();
    private readonly HashSet<object> _touched = new();
    private readonly List<Microsoft.Xna.Framework.Audio.SoundEffectInstance> _oneShots = new();
    private readonly List<(float at, string label, System.Numerics.Vector3? pos, float vol)> _delayed = new();
    private readonly Random _rng = new();
    private float _clock;
    public System.Numerics.Vector3 Listener;
    public System.Numerics.Vector3 Right = System.Numerics.Vector3.UnitX;
    public float ExtraDistance;
    public float Master = 0.8f;
    /// <summary>Volumes par famille (0 à 1), en plus du volume général : interface et voix, effets, ambiance (musiques).</summary>
    public float VoiceVolume = 1, EffectsVolume = 1, MusicVolume = 1;
    private float GroupVolume(SoundDb.Entry e) => e.Group switch { "voice" => VoiceVolume, "music" => MusicVolume, _ => EffectsVolume };
    public bool Enabled { get; private set; }
    /// <summary>Sons enregistrés trop bas dans le jeu (on ne les entend pas sous la vidéo) : remontés à NormalizePeak au chargement.</summary>
    private static readonly HashSet<string> Louder = new(StringComparer.OrdinalIgnoreCase) { "UI_BRIEFING_TEXT_DISPLAY" };
    private const float NormalizePeak = 0.7f;

    private static void Normalize(short[] samples)
    {
        int peak = 1;
        foreach (short v in samples)
            peak = Math.Max(peak, Math.Abs((int)v));
        float gain = NormalizePeak * short.MaxValue / peak;
        if (gain <= 1)
            return;
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)Math.Clamp(samples[i] * gain, short.MinValue, short.MaxValue);
    }
    public int Count => _db.Entries.Count;

    public Audio(SoundDb db)
    {
        _db = db;
        try
        {
            Microsoft.Xna.Framework.Audio.SoundEffect.MasterVolume = 1f;
            Enabled = db.Entries.Count > 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("Son indisponible : " + e.Message);
            Enabled = false;
        }
    }

    private Microsoft.Xna.Framework.Audio.SoundEffect? Effect(SoundDb.Entry e)
    {
        if (_cache.TryGetValue(e.File, out Microsoft.Xna.Framework.Audio.SoundEffect? fx))
            return fx;
        fx = null;
        try
        {
            if (SoundData.LoadWav(e.File) is { } d && d.Samples.Length > 0)
            {
                if (Louder.Contains(e.Label))
                    Normalize(d.Samples);
                byte[] bytes = new byte[d.Samples.Length * 2];
                Buffer.BlockCopy(d.Samples, 0, bytes, 0, bytes.Length);
                fx = new Microsoft.Xna.Framework.Audio.SoundEffect(bytes, d.Rate, d.Channels == 2 ? Microsoft.Xna.Framework.Audio.AudioChannels.Stereo : Microsoft.Xna.Framework.Audio.AudioChannels.Mono);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Son {e.Label} : {ex.Message}");
            Enabled = !(ex is Microsoft.Xna.Framework.Audio.NoAudioHardwareException) && Enabled;
        }
        return _cache[e.File] = fx;
    }

    // atténuation 3D : pleine jusqu'à MinDist, nulle à MaxDist ; panoramique selon la droite de la caméra
    private (float gain, float pan) Spatial(SoundDb.Entry e, System.Numerics.Vector3? pos)
    {
        if (pos is not { } p || !e.Use3D)
            return (1, 0);
        Vector3 dv = p - Listener;
        float d = MathF.Sqrt(dv.X * dv.X + dv.Y * dv.Y + ExtraDistance * ExtraDistance);
        float g = d <= e.MinDist ? 1 : d >= e.MaxDist ? 0 : 1 - (d - e.MinDist) / (e.MaxDist - e.MinDist);
        float pan = d > 1 ? Math.Clamp(System.Numerics.Vector3.Dot(dv, Right) / d, -1, 1) * 0.6f : 0;
        return (g, pan);
    }

    /// <summary>Son ponctuel (étiquette de la base de sons).</summary>
    public void Play(string label, System.Numerics.Vector3? pos = null, float volume = 1)
    {
        if (!Enabled || !_db.TryGet(label, out SoundDb.Entry? e))
            return;
        if (e.StartDelay > 0)
        {
            _delayed.Add((_clock + e.StartDelay / 1000f, label, pos, volume));
            return;
        }
        Start(e, pos, volume);
    }

    private void Start(SoundDb.Entry e, System.Numerics.Vector3? pos, float volume)
    {
        if (Effect(e) is not { } fx)
            return;
        (float g, float pan) = Spatial(e, pos);
        if (g <= 0.01f)
            return;
        try
        {
            Microsoft.Xna.Framework.Audio.SoundEffectInstance inst = fx.CreateInstance();
            inst.Volume = Math.Clamp(g * e.Volume * volume * Master * GroupVolume(e), 0, 1);
            inst.Pan = pan;
            inst.IsLooped = false;
            inst.Play();
            _oneShots.Add(inst);
        }
        catch (Exception ex) { Console.WriteLine("Son : " + ex.Message); }
    }

    /// <summary>Une étiquette au hasard parmi celles qui existent (« VOCAL_SELECTION1 » … « VOCAL_SELECTION6 »).</summary>
    public void PlayAny(IEnumerable<string> labels, System.Numerics.Vector3? pos = null)
    {
        List<string> ok = labels.Where(l => _db.TryGet(l, out _)).ToList();
        if (ok.Count > 0)
            Play(ok[_rng.Next(ok.Count)], pos);
    }

    /// <summary>Son en boucle entretenu : à appeler à chaque image tant qu'il doit jouer (clé = objet qui l'émet).</summary>
    public void Keep(object key, string label, System.Numerics.Vector3 pos, float volume = 1)
    {
        if (!Enabled || !_db.TryGet(label, out SoundDb.Entry? e) || Effect(e) is not { } fx)
            return;
        (float g, float pan) = Spatial(e, pos);
        _touched.Add(key);
        if (!_loops.TryGetValue(key, out Loop? l) || l.File != e.File)
        {
            if (l != null)
            {
                l.Inst.Stop();
                l.Inst.Dispose();
                _loops.Remove(key);
            }
            if (g <= 0.01f)
                return;
            try
            {
                Microsoft.Xna.Framework.Audio.SoundEffectInstance inst = fx.CreateInstance();
                inst.IsLooped = true;
                inst.Volume = 0;
                inst.Play();
                _loops[key] = l = new Loop { Inst = inst, File = e.File };
            }
            catch { return; }
        }
        l.Target = g * e.Volume * volume * Master * GroupVolume(e);
        l.Inst.Pan = pan;
    }

    /// <summary>Fin d'image : volumes amenés en douceur vers leur cible ; les boucles non entretenues s'éteignent.</summary>
    public void EndFrame(float dt = 1 / 60f)
    {
        _clock += dt;
        for (int i = _delayed.Count - 1; i >= 0; i--)
            if (_clock >= _delayed[i].at && _db.TryGet(_delayed[i].label, out SoundDb.Entry? e))
            {
                Start(e, _delayed[i].pos, _delayed[i].vol);
                _delayed.RemoveAt(i);
            }
        foreach ((object? k, Loop? l) in _loops.ToList())
        {
            if (!_touched.Contains(k))
                l.Target = 0;
            l.Gain += (l.Target - l.Gain) * MathF.Min(1, dt * 6);
            if (l.Target == 0 && l.Gain < 0.005f)
            {
                l.Inst.Stop();
                l.Inst.Dispose();
                _loops.Remove(k);
                continue;
            }
            l.Inst.Volume = Math.Clamp(l.Gain, 0, 1);
        }
        _touched.Clear();
        _oneShots.RemoveAll(s =>
        {
            if (s.State == Microsoft.Xna.Framework.Audio.SoundState.Stopped)
            {
                s.Dispose();
                return true;
            }
            return false;
        });
    }

    /// <summary>Pause du jeu : sons en cours suspendus (true), puis repris (false).</summary>
    public void PauseAll(bool pause)
    {
        foreach (Loop l in _loops.Values)
        {
            if (pause)
                l.Inst.Pause();
            else if (l.Inst.State == Microsoft.Xna.Framework.Audio.SoundState.Paused)
                l.Inst.Resume();
        }
        foreach (Microsoft.Xna.Framework.Audio.SoundEffectInstance o in _oneShots)
        {
            if (pause)
                o.Pause();
            else if (o.State == Microsoft.Xna.Framework.Audio.SoundState.Paused)
                o.Resume();
        }
    }

    public void StopAll()
    {
        foreach (Loop l in _loops.Values)
        {
            l.Inst.Stop();
            l.Inst.Dispose();
        }
        _loops.Clear();
        foreach (Microsoft.Xna.Framework.Audio.SoundEffectInstance s in _oneShots)
        {
            s.Stop();
            s.Dispose();
        }
        _oneShots.Clear();
        _delayed.Clear();
    }

    public void Dispose()
    {
        StopAll();
        foreach (Microsoft.Xna.Framework.Audio.SoundEffect? fx in _cache.Values)
            fx?.Dispose();
        _cache.Clear();
    }
}
