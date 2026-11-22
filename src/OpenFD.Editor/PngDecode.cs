using System.IO.Compression;

namespace OpenFD.MG;

/// <summary>
/// Lecture d'une image PNG (textures exportées par Blender) en RGBA, sans librairie : gris, couleurs, palette, avec ou sans
/// transparence, 8 ou 16 bits (et 1, 2, 4 bits pour le gris et la palette). Null : pas un PNG, ou PNG entrelacé.
/// </summary>
internal static class PngDecode
{
    public static (int w, int h, byte[] rgba)? Decode(byte[] d)
    {
        if (d.Length < 33 || d[0] != 0x89 || d[1] != 'P' || d[2] != 'N' || d[3] != 'G')
            return null;
        int w = 0, h = 0, depth = 0, type = 0, interlace = 0;
        byte[]? palette = null;
        byte[]? trns = null;
        using MemoryStream idat = new MemoryStream();
        int p = 8;
        while (p + 8 <= d.Length)
        {
            int len = (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
            string kind = System.Text.Encoding.ASCII.GetString(d, p + 4, 4);
            int data = p + 8;
            if (len < 0 || data + len > d.Length)
                break;
            if (kind == "IHDR")
            {
                w = Be(d, data);
                h = Be(d, data + 4);
                depth = d[data + 8];
                type = d[data + 9];
                interlace = d[data + 12];
            }
            else if (kind == "PLTE")
                palette = d[data..(data + len)];
            else if (kind == "tRNS")
                trns = d[data..(data + len)];
            else if (kind == "IDAT")
                idat.Write(d, data, len);
            else if (kind == "IEND")
                break;
            p = data + len + 4;
        }
        if (w <= 0 || h <= 0 || interlace != 0)
            return null;
        int channels = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0)
            return null;
        int bitsPerPixel = channels * depth;
        int stride = (w * bitsPerPixel + 7) / 8;
        int bpp = Math.Max(1, bitsPerPixel / 8);
        byte[] raw = new byte[(stride + 1) * h];
        idat.Position = 0;
        using (ZLibStream z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int got = 0;
            while (got < raw.Length)
            {
                int n = z.Read(raw, got, raw.Length - got);
                if (n <= 0)
                    break;
                got += n;
            }
        }
        byte[] cur = new byte[stride];
        byte[] prev = new byte[stride];
        byte[] rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int filter = raw[y * (stride + 1)];
            Buffer.BlockCopy(raw, y * (stride + 1) + 1, cur, 0, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                int v = filter switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => 0,
                };
                cur[i] = (byte)(cur[i] + v);
            }
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                byte r, g, bl, al = 255;
                switch (type)
                {
                    case 0:
                    {
                        int v = Sample(cur, x, depth);
                        r = g = bl = Scale(v, depth);
                        if (trns != null && trns.Length >= 2 && v == ((trns[0] << 8) | trns[1]))
                            al = 0;
                        break;
                    }
                    case 3:
                    {
                        int idx = Sample(cur, x, depth);
                        r = palette != null && idx * 3 + 2 < palette.Length ? palette[idx * 3] : (byte)0;
                        g = palette != null && idx * 3 + 2 < palette.Length ? palette[idx * 3 + 1] : (byte)0;
                        bl = palette != null && idx * 3 + 2 < palette.Length ? palette[idx * 3 + 2] : (byte)0;
                        if (trns != null && idx < trns.Length)
                            al = trns[idx];
                        break;
                    }
                    default:
                    {
                        int step = depth == 16 ? 2 : 1;
                        int baseI = x * channels * step;
                        byte C(int k) => cur[baseI + k * step];
                        if (type == 2)
                        {
                            r = C(0);
                            g = C(1);
                            bl = C(2);
                        }
                        else if (type == 4)
                        {
                            r = g = bl = C(0);
                            al = C(1);
                        }
                        else
                        {
                            r = C(0);
                            g = C(1);
                            bl = C(2);
                            al = C(3);
                        }
                        break;
                    }
                }
                rgba[o] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = bl;
                rgba[o + 3] = al;
            }
            (prev, cur) = (cur, prev);
        }
        return (w, h, rgba);
    }

    private static int Be(byte[] d, int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
            return a;
        return pb <= pc ? b : c;
    }

    // valeur du pixel x d'une ligne à 1, 2, 4, 8 ou 16 bits (16 bits : l'octet fort)
    private static int Sample(byte[] line, int x, int depth)
    {
        if (depth == 8)
            return line[x];
        if (depth == 16)
            return line[x * 2];
        int perByte = 8 / depth;
        int shift = 8 - depth * (x % perByte + 1);
        return (line[x / perByte] >> shift) & ((1 << depth) - 1);
    }

    private static byte Scale(int v, int depth) => depth switch
    {
        1 => (byte)(v * 255),
        2 => (byte)(v * 85),
        4 => (byte)(v * 17),
        _ => (byte)v,
    };
}
