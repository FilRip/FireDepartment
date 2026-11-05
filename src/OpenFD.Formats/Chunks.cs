using System.Numerics;
using System.Text;

namespace OpenFD.Formats;

/// <summary>Blocs balisés : AA AA FF FF + version ouvre, CC CC BB BB ferme.</summary>
public static class Chunks
{
    public static bool IsBegin(ReadOnlySpan<byte> d, int p) => p + 4 <= d.Length && d[p] == 0xAA && d[p + 1] == 0xAA && d[p + 2] == 0xFF && d[p + 3] == 0xFF;
    public static bool IsEnd(ReadOnlySpan<byte> d, int p) => p + 4 <= d.Length && d[p] == 0xCC && d[p + 1] == 0xCC && d[p + 2] == 0xBB && d[p + 3] == 0xBB;

    /// <summary>Position juste après la fin du bloc qui commence en p (comptage des marqueurs).</summary>
    public static int NodeEnd(ReadOnlySpan<byte> d, int p)
    {
        if (!IsBegin(d, p))
            throw new InvalidDataException($"Bloc attendu en 0x{p:x}");
        int depth = 0, i = p;
        while (i < d.Length - 3)
        {
            if (d[i] == 0xAA && IsBegin(d, i))
            {
                depth++;
                i += 5;
                continue;
            }
            if (d[i] == 0xCC && IsEnd(d, i))
            {
                depth--;
                i += 4;
                if (depth == 0)
                    return i;
                continue;
            }
            i++;
        }
        throw new InvalidDataException($"Bloc non fermé en 0x{p:x}");
    }

    public static string ReadString(ReadOnlySpan<byte> d, ref int p)
    {
        int n = BitConverter.ToInt32(d[p..]);
        string s = Encoding.Latin1.GetString(d.Slice(p + 4, Math.Max(0, n - 1)));
        p += 4 + n;
        return s;
    }
}
