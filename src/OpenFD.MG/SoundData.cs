using System.Numerics;
using System.Text.RegularExpressions;

namespace OpenFD.MG;

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
            byte[] d = File.ReadAllBytes(path);
            if (d.Length < 44 || d[0] != 'R' || d[8] != 'W')
                return null;
            int p = 12, fmt = 0, ch = 1, rate = 22050, bits = 16;
            while (p + 8 <= d.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(d, p, 4); int sz = BitConverter.ToInt32(d, p + 4);
                if (id == "fmt ")
                {
                    fmt = BitConverter.ToUInt16(d, p + 8);
                    ch = BitConverter.ToUInt16(d, p + 10);
                    rate = BitConverter.ToInt32(d, p + 12);
                    bits = BitConverter.ToUInt16(d, p + 22);
                }
                else if (id == "data")
                {
                    if (fmt != 1 || ch < 1 || ch > 2)
                        return null;
                    sz = Math.Min(sz, d.Length - p - 8);
                    SoundData s = new SoundData { Channels = ch, Rate = rate };
                    if (bits == 16)
                    {
                        s.Samples = new short[sz / 2];
                        Buffer.BlockCopy(d, p + 8, s.Samples, 0, s.Samples.Length * 2);
                    }
                    else if (bits == 8)
                    {
                        s.Samples = new short[sz];
                        for (int i = 0; i < sz; i++)
                            s.Samples[i] = (short)((d[p + 8 + i] - 128) << 8);
                    }
                    else
                        return null;
                    return s;
                }
                p += 8 + sz + (sz & 1);
            }
        }
        catch { }
        return null;
    }
}
