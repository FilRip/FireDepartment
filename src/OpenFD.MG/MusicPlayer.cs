using Microsoft.Xna.Framework.Audio;

namespace OpenFD.MG;

/// <summary>
/// Musiques du jeu (Data\Sound\Music\*.mp3 : Menu, Briefing, Victory, Defeat…) : décodées au fur et à mesure par NLayer
/// (OpenFD.Mp3) et jouées par un son dynamique de MonoGame, en boucle ou une seule fois. Volume : général × ambiance.
/// </summary>
public sealed class MusicPlayer : IDisposable
{
    private readonly string _dir;
    private NLayer.MpegFile? _file;
    private DynamicSoundEffectInstance? _inst;
    private float[] _floats = Array.Empty<float>();
    private byte[] _bytes = Array.Empty<byte>();
    private bool _loop, _ended;
    private string? _current;

    /// <summary>Volume (0 à 1) : volume général × volume d'ambiance.</summary>
    public float Volume = 0.5f;

    public MusicPlayer(string dataDir)
    {
        _dir = Path.Combine(dataDir, "Sound", "Music");
    }

    /// <summary>
    /// Musique voulue maintenant (nom du fichier sans extension, « Menu ») ; null : silence. La même musique continue sans
    /// recommencer, même finie (une musique jouée une fois ne repart pas).
    /// </summary>
    public void Want(string? name, bool loop)
    {
        if (string.Equals(name, _current, StringComparison.OrdinalIgnoreCase))
            return;
        Stop();
        _current = name;
        if (name == null)
            return;
        try
        {
            string? path = Directory.Exists(_dir) ? Directory.EnumerateFiles(_dir).FirstOrDefault(f => Path.GetFileName(f).Equals(name + ".mp3", StringComparison.OrdinalIgnoreCase)) : null;
            if (path == null)
                return;
            _file = new NLayer.MpegFile(path);
            _inst = new DynamicSoundEffectInstance(_file.SampleRate, _file.Channels == 2 ? AudioChannels.Stereo : AudioChannels.Mono);
            _inst.BufferNeeded += (_, _) => Feed();
            _loop = loop;
            _ended = false;
            _inst.Volume = Math.Clamp(Volume, 0, 1);
            Feed();
            Feed();
            _inst.Play();
        }
        catch (Exception e)
        {
            Console.WriteLine($"Musique {name} : {e.Message}");
            Stop();
            _current = name;   // pas de nouvel essai à chaque image
        }
    }

    /// <summary>À chaque image : volume à jour.</summary>
    public void Update()
    {
        if (_inst != null)
            _inst.Volume = Math.Clamp(Volume, 0, 1);
    }

    // environ un quart de seconde de son décodé, envoyé à la carte son
    private void Feed()
    {
        if (_file == null || _inst == null || _ended)
            return;
        int want = _file.SampleRate / 4 * _file.Channels;
        if (_floats.Length < want)
        {
            _floats = new float[want];
            _bytes = new byte[want * 2];
        }
        int n = _file.ReadSamples(_floats, 0, want);
        if (n <= 0 && _loop)
        {
            _file.Position = 0;   // reprise au début
            n = _file.ReadSamples(_floats, 0, want);
        }
        if (n <= 0)
        {
            _ended = true;
            return;
        }
        for (int i = 0; i < n; i++)
        {
            short v = (short)Math.Clamp((int)(_floats[i] * 32767f), short.MinValue, short.MaxValue);
            _bytes[2 * i] = (byte)v;
            _bytes[2 * i + 1] = (byte)(v >> 8);
        }
        _inst.SubmitBuffer(_bytes, 0, n * 2);
    }

    public void Pause(bool pause)
    {
        if (_inst == null)
            return;
        if (pause && _inst.State == SoundState.Playing)
            _inst.Pause();
        else if (!pause && _inst.State == SoundState.Paused)
            _inst.Resume();
    }

    public void Stop()
    {
        try
        {
            _inst?.Stop();
            _inst?.Dispose();
        }
        catch (Exception)
        {
            // carte son absente ou déjà libérée
        }
        _inst = null;
        _file?.Dispose();
        _file = null;
        _current = null;
    }

    public void Dispose() => Stop();
}
