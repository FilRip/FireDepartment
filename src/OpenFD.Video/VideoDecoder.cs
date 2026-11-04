using System.Runtime.InteropServices;
using System.Text;

namespace OpenFD.Video;

/// <summary>
/// Décodeur de vidéo (Bink .bik du jeu) : images RGBA et son 16 bits entrelacé, dans l'ordre du fichier.
/// S'appuie sur la bibliothèque native « fd3video » (FFmpeg) ; si elle manque, <see cref="Open"/> renvoie null
/// et le jeu continue sans vidéo.
/// </summary>
public sealed class VideoDecoder : IDisposable
{
    private IntPtr _h;
    private readonly string _path;
    public int Width { get; }
    public int Height { get; }
    public double Fps { get; }
    public double Duration { get; }
    public int AudioRate { get; }
    public int AudioChannels { get; }

    /// <summary>Dernière erreur à l'ouverture (bibliothèque absente, fichier illisible…).</summary>
    public static string? LastError { get; private set; }

    private VideoDecoder(IntPtr h, string path)
    {
        _h = h; _path = path;
        Width = Native.fv_width(h); Height = Native.fv_height(h); Fps = Native.fv_fps(h); Duration = Native.fv_duration(h);
        AudioRate = Native.fv_audio_rate(h); AudioChannels = Native.fv_audio_channels(h);
    }

    public static VideoDecoder? Open(string path)
    {
        if (!File.Exists(path))
        {
            LastError = "fichier introuvable : " + path;
            return null;
        }
        try
        {
            nint h = Native.fv_open(Utf8(path));
            if (h == IntPtr.Zero)
            {
                LastError = "vidéo illisible : " + path;
                return null;
            }
            return new VideoDecoder(h, path);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            LastError = "bibliothèque vidéo fd3video absente : " + e.Message;
            return null;
        }
    }

    private static byte[] Utf8(string s) { byte[] b = Encoding.UTF8.GetBytes(s); Array.Resize(ref b, b.Length + 1); return b; }

    public enum Item { End = 0, Image = 1, Sound = 2 }

    /// <summary>
    /// Élément suivant du fichier : une image (copiée dans <paramref name="rgba"/>, Width × Height × 4 octets) ou du son
    /// (dans <paramref name="audio"/>, <paramref name="samples"/> échantillons par canal) ; <paramref name="pts"/> en secondes.
    /// </summary>
    public unsafe Item Next(byte[] rgba, short[] audio, out double pts, out int samples)
    {
        pts = 0; samples = 0;
        if (_h == IntPtr.Zero)
            return Item.End;
        fixed (byte* p = rgba) fixed (short* a = audio)
        {
            double t; int n;
            int r = Native.fv_next(_h, p, &t, a, audio.Length, &n);
            pts = t; samples = n;
            return r == 1 ? Item.Image : r == 2 ? Item.Sound : Item.End;
        }
    }

    /// <summary>Retour au début (lecture en boucle) ; à défaut, le fichier est rouvert.</summary>
    public bool Rewind()
    {
        if (_h == IntPtr.Zero)
            return false;
        if (Native.fv_rewind(_h) == 0)
            return true;
        Native.fv_close(_h);
        _h = Native.fv_open(Utf8(_path));
        return _h != IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_h != IntPtr.Zero)
        {
            Native.fv_close(_h);
            _h = IntPtr.Zero;
        }
    }

    private static unsafe class Native
    {
        private const string Lib = "fd3video";
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr fv_open(byte[] utf8Path);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void fv_close(IntPtr v);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int fv_width(IntPtr v);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int fv_height(IntPtr v);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern double fv_fps(IntPtr v);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern double fv_duration(IntPtr v);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int fv_audio_rate(IntPtr v);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int fv_audio_channels(IntPtr v);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int fv_next(IntPtr v, byte* rgba, double* pts, short* audio, int cap, int* samples);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int fv_rewind(IntPtr v);
    }
}
