using OpenFD.Video;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;

namespace OpenFD.MG;

/// <summary>
/// Lecture d'une vidéo du jeu (Data\Video\*.bik) dans une texture, avec son son : intro au démarrage, fond animé du
/// menu (en boucle). Le décodage vient de OpenFD.Video ; ici, seulement l'affichage et le son avec MonoGame.
/// </summary>
public sealed class VideoScreen : IDisposable
{
    private readonly VideoDecoder _dec;
    private readonly GraphicsDevice _gd;
    private readonly byte[] _rgba, _next;
    private readonly short[] _audio;
    private DynamicSoundEffectInstance? _snd;
    private double _clock, _nextPts = -1, _loopOffset;
    private bool _haveNext, _ended;
    public Texture2D Texture { get; }
    public bool Loop;
    public bool Finished => _ended;
    /// <summary>Temps de lecture (secondes).</summary>
    public double Time => _clock;
    public int Width => _dec.Width;
    public int Height => _dec.Height;

    private VideoScreen(GraphicsDevice gd, VideoDecoder dec, bool loop, float volume)
    {
        _gd = gd;
        _dec = dec;
        Loop = loop;
        _rgba = new byte[dec.Width * dec.Height * 4];
        _next = new byte[_rgba.Length];
        _audio = new short[16384];
        Texture = new Texture2D(gd, dec.Width, dec.Height, false, SurfaceFormat.Color);
        if (dec.AudioRate > 0 && volume > 0)
        {
            try
            {
                _snd = new DynamicSoundEffectInstance(dec.AudioRate, dec.AudioChannels == 2 ? AudioChannels.Stereo : AudioChannels.Mono) { Volume = Math.Clamp(volume, 0, 1) };
                _snd.Play();
            }
            catch (Exception e)
            {
                Console.WriteLine("Son de la vidéo : " + e.Message);
                _snd = null;
            }
        }
        Pump(0);   // première image tout de suite
    }

    /// <summary>Ouvre la vidéo ; null si elle est absente ou si la bibliothèque vidéo manque (le jeu continue sans).</summary>
    public static VideoScreen? Open(GraphicsDevice gd, string path, bool loop, float volume)
    {
        VideoDecoder? d = VideoDecoder.Open(path);
        if (d == null)
        {
            Console.WriteLine("Vidéo : " + VideoDecoder.LastError);
            return null;
        }
        Console.WriteLine($"Vidéo : {Path.GetFileName(path)} {d.Width}×{d.Height}, {d.Fps:0.##} images/s, {d.Duration:0.0} s, son {d.AudioRate} Hz");
        return new VideoScreen(gd, d, loop, volume);
    }

    /// <summary>Avance la vidéo de dt secondes : décode jusqu'à l'image à montrer et envoie le son au fur et à mesure.</summary>
    public void Update(double dt)
    {
        if (_ended)
            return;
        // pas d'avance tant que le son est en retard (fenêtre déplacée…) : on suit la vidéo, pas l'horloge
        _clock += Math.Min(dt, 0.1);
        Pump(_clock);
    }

    private bool _shownOnce;
    private void Pump(double until)
    {
        for (int guard = 0; guard < 400; guard++)
        {
            if (_haveNext)
            {
                // l'image décodée d'avance attend son heure (la toute première est montrée tout de suite)
                if (_nextPts + _loopOffset > until && _shownOnce)
                    break;
                Buffer.BlockCopy(_next, 0, _rgba, 0, _rgba.Length);
                Texture.SetData(_rgba);
                _shownOnce = true;
                _haveNext = false;
            }
            VideoDecoder.Item it = _dec.Next(_next, _audio, out double pts, out int n);
            if (it == VideoDecoder.Item.Image)
            {
                _nextPts = pts;
                _haveNext = true;
                continue;
            }
            if (it == VideoDecoder.Item.Sound)
            {
                if (_snd != null && n > 0)
                {
                    byte[] bytes = new byte[n * Math.Max(1, _dec.AudioChannels) * 2];
                    Buffer.BlockCopy(_audio, 0, bytes, 0, bytes.Length);
                    try { _snd.SubmitBuffer(bytes); } catch { }
                }
                continue;
            }
            // fin du fichier : on recommence (menu) ou c'est fini (intro)
            if (Loop && _dec.Rewind())
            {
                _loopOffset = Math.Max(_clock, _nextPts + _loopOffset);
                continue;
            }
            _ended = true;
            Console.WriteLine($"Vidéo terminée à {_clock:0.00} s (dernière image {_nextPts:0.00} s)");
            break;
        }
    }

    public void Dispose()
    {
        try
        {
            _snd?.Stop();
            _snd?.Dispose();
        } catch { }
        Texture.Dispose();
        _dec.Dispose();
    }
}
