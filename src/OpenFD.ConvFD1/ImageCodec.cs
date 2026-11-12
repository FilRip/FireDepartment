namespace OpenFD.ConvFD1;

/// <summary>Images des textures de FD1 (BMP 8, 24 ou 32 bits ; TGA 24 ou 32 bits, brut ou RLE ; JPEG baseline) → DDS 32 bits (BGRA) lu par le moteur.</summary>
public static class ImageCodec
{
    /// <summary>Image décodée en RGBA (ligne du haut en premier) ; null si le format n'est pas reconnu.</summary>
    public static (int w, int h, byte[] rgba)? Decode(byte[] d)
    {
        if (d.Length > 54 && d[0] == 'B' && d[1] == 'M')
            return DecodeBmp(d);
        // JPEG (Quartier : les dalles de sol)
        if (d.Length > 4 && d[0] == 0xFF && d[1] == 0xD8)
            return JpegDecoder.Decode(d);
        return DecodeTga(d);
    }

    private static (int w, int h, byte[] rgba)? DecodeBmp(byte[] d)
    {
        int off = BitConverter.ToInt32(d, 10);
        int hdr = BitConverter.ToInt32(d, 14);
        int w = BitConverter.ToInt32(d, 18);
        int hRaw = BitConverter.ToInt32(d, 22);
        int bits = BitConverter.ToUInt16(d, 28);
        int compression = BitConverter.ToInt32(d, 30);
        if (w <= 0 || hRaw == 0 || compression != 0 && compression != 3 || bits != 8 && bits != 24 && bits != 32)
            return null;
        bool bottomUp = hRaw > 0;
        int h = Math.Abs(hRaw);
        int stride = (w * bits / 8 + 3) & ~3;
        byte[] rgba = new byte[w * h * 4];
        int palette = 14 + hdr;
        for (int y = 0; y < h; y++)
        {
            int row = off + (bottomUp ? h - 1 - y : y) * stride;
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                if (bits == 8)
                {
                    int idx = row + x < d.Length ? d[row + x] : 0;
                    int p = palette + idx * 4;
                    if (p + 2 >= d.Length)
                        continue;
                    rgba[o] = d[p + 2];
                    rgba[o + 1] = d[p + 1];
                    rgba[o + 2] = d[p];
                    rgba[o + 3] = 255;
                }
                else
                {
                    int s = row + x * (bits / 8);
                    if (s + 2 >= d.Length)
                        continue;
                    rgba[o] = d[s + 2];
                    rgba[o + 1] = d[s + 1];
                    rgba[o + 2] = d[s];
                    rgba[o + 3] = bits == 32 && compression == 0 ? (byte)255 : bits == 32 ? d[s + 3] : (byte)255;
                }
            }
        }
        return (w, h, rgba);
    }

    private static (int w, int h, byte[] rgba)? DecodeTga(byte[] d)
    {
        if (d.Length < 18)
            return null;
        int idLen = d[0];
        int type = d[2];
        int w = BitConverter.ToUInt16(d, 12);
        int h = BitConverter.ToUInt16(d, 14);
        int bits = d[16];
        bool topDown = (d[17] & 0x20) != 0;
        if (type != 2 && type != 10 || bits != 24 && bits != 32 || w == 0 || h == 0)
            return null;
        int bpp = bits / 8;
        int p = 18 + idLen;
        byte[] px = new byte[w * h * bpp];
        if (type == 2)
        {
            if (p + px.Length > d.Length)
                return null;
            Buffer.BlockCopy(d, p, px, 0, px.Length);
        }
        else
        {
            int o = 0;
            while (o < px.Length && p < d.Length)
            {
                int c = d[p];
                p++;
                int n = (c & 0x7F) + 1;
                if ((c & 0x80) != 0)
                {
                    for (int i = 0; i < n && o < px.Length; i++)
                    {
                        for (int k = 0; k < bpp && p + k < d.Length; k++)
                            px[o + k] = d[p + k];
                        o += bpp;
                    }
                    p += bpp;
                }
                else
                {
                    int len = Math.Min(n * bpp, Math.Min(px.Length - o, d.Length - p));
                    Buffer.BlockCopy(d, p, px, o, len);
                    o += n * bpp;
                    p += n * bpp;
                }
            }
        }
        byte[] rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = ((topDown ? y : h - 1 - y) * w + x) * bpp;
                int o = (y * w + x) * 4;
                rgba[o] = px[s + 2];
                rgba[o + 1] = px[s + 1];
                rgba[o + 2] = px[s];
                rgba[o + 3] = bpp == 4 ? px[s + 3] : (byte)255;
            }
        return (w, h, rgba);
    }

    /// <summary>DDS non compressé 32 bits (BGRA, avec alpha), un seul niveau : le moteur fabrique les niveaux réduits.</summary>
    public static byte[] EncodeDds(int w, int h, byte[] rgba)
    {
        byte[] o = new byte[128 + w * h * 4];
        void U32(int at, uint v) => BitConverter.GetBytes(v).CopyTo(o, at);
        U32(0, 0x20534444);
        U32(4, 124);
        U32(8, 0x1 | 0x2 | 0x4 | 0x8 | 0x1000);
        U32(12, (uint)h);
        U32(16, (uint)w);
        U32(20, (uint)(w * 4));
        U32(28, 1);
        U32(76, 32);
        U32(80, 0x41);
        U32(88, 32);
        U32(92, 0x00FF0000);
        U32(96, 0x0000FF00);
        U32(100, 0x000000FF);
        U32(104, 0xFF000000);
        U32(108, 0x1000);
        for (int i = 0; i < w * h; i++)
        {
            o[128 + 4 * i] = rgba[4 * i + 2];
            o[128 + 4 * i + 1] = rgba[4 * i + 1];
            o[128 + 4 * i + 2] = rgba[4 * i];
            o[128 + 4 * i + 3] = rgba[4 * i + 3];
        }
        return o;
    }
}
