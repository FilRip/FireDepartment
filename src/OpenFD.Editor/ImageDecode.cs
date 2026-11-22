namespace OpenFD.MG;

/// <summary>
/// Textures du jeu → pixels RGBA (premier niveau), pour l'export : DDS DXT1, DXT3, DXT5, non compressé 24 ou 32 bits ;
/// TGA non compressé ou RLE, 24 ou 32 bits. Null : format non pris en charge.
/// </summary>
internal static class ImageDecode
{
    public static (int w, int h, byte[] rgba)? Decode(byte[] d)
    {
        if (d.Length >= 128 && BitConverter.ToUInt32(d, 0) == 0x20534444)
            return Dds(d);
        return Tex.DecodeTga(d);   // lignes déjà remises le haut en premier
    }

    private static (int w, int h, byte[] rgba)? Dds(byte[] d)
    {
        int h = BitConverter.ToInt32(d, 12), w = BitConverter.ToInt32(d, 16);
        uint four = BitConverter.ToUInt32(d, 84);
        if (w <= 0 || h <= 0 || w > 16384 || h > 16384)
            return null;
        if (four == 0x31545844 || four == 0x33545844 || four == 0x35545844)
        {
            int kind = four == 0x31545844 ? 1 : four == 0x33545844 ? 3 : 5;
            int block = kind == 1 ? 8 : 16;
            int bw = (w + 3) / 4, bh = (h + 3) / 4;
            if (d.Length < 128 + bw * bh * block)
                return null;
            byte[] rgba = new byte[w * h * 4];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                    Block(d, 128 + (by * bw + bx) * block, kind, rgba, w, h, bx * 4, by * 4);
            return (w, h, rgba);
        }
        if (four != 0)
            return null;
        int bpp = BitConverter.ToInt32(d, 88);
        if (bpp == 32)
            return Tex.DecodeDds32(d);
        if (bpp != 24 || d.Length < 128 + w * h * 3)
            return null;
        byte[] o = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            o[i * 4] = d[128 + i * 3 + 2];
            o[i * 4 + 1] = d[128 + i * 3 + 1];
            o[i * 4 + 2] = d[128 + i * 3];
            o[i * 4 + 3] = 255;
        }
        return (w, h, o);
    }

    private static void Block(byte[] d, int off, int kind, byte[] rgba, int w, int h, int x0, int y0)
    {
        int colorOff = kind == 1 ? off : off + 8;
        ushort c0 = BitConverter.ToUInt16(d, colorOff), c1 = BitConverter.ToUInt16(d, colorOff + 2);
        byte[,] pal = new byte[4, 4];
        Rgb565(c0, pal, 0);
        Rgb565(c1, pal, 1);
        bool four = kind != 1 || c0 > c1;
        for (int c = 0; c < 3; c++)
            if (four)
            {
                pal[2, c] = (byte)((2 * pal[0, c] + pal[1, c]) / 3);
                pal[3, c] = (byte)((pal[0, c] + 2 * pal[1, c]) / 3);
            }
            else
            {
                pal[2, c] = (byte)((pal[0, c] + pal[1, c]) / 2);
                pal[3, c] = 0;
            }
        pal[0, 3] = 255;
        pal[1, 3] = 255;
        pal[2, 3] = 255;
        pal[3, 3] = (byte)(four ? 255 : 0);
        uint bits = BitConverter.ToUInt32(d, colorOff + 4);
        byte[] alpha = new byte[16];
        if (kind == 3)
            for (int i = 0; i < 16; i++)
            {
                int a = d[off + i / 2] >> (i % 2 * 4) & 15;
                alpha[i] = (byte)(a * 17);
            }
        else if (kind == 5)
        {
            byte a0 = d[off], a1 = d[off + 1];
            byte[] ap = new byte[8];
            ap[0] = a0;
            ap[1] = a1;
            for (int i = 2; i < 8; i++)
                ap[i] = a0 > a1 ? (byte)(((8 - i) * a0 + (i - 1) * a1) / 7) : i < 6 ? (byte)(((6 - i) * a0 + (i - 1) * a1) / 5) : i == 6 ? (byte)0 : (byte)255;
            ulong ab = 0;
            for (int i = 0; i < 6; i++)
                ab |= (ulong)d[off + 2 + i] << (8 * i);
            for (int i = 0; i < 16; i++)
                alpha[i] = ap[(int)(ab >> (3 * i) & 7)];
        }
        for (int i = 0; i < 16; i++)
        {
            int x = x0 + i % 4, y = y0 + i / 4;
            if (x >= w || y >= h)
                continue;
            int idx = (int)(bits >> (2 * i) & 3), p = (y * w + x) * 4;
            rgba[p] = pal[idx, 0];
            rgba[p + 1] = pal[idx, 1];
            rgba[p + 2] = pal[idx, 2];
            rgba[p + 3] = kind == 1 ? pal[idx, 3] : alpha[i];
        }
    }

    private static void Rgb565(ushort c, byte[,] pal, int i)
    {
        pal[i, 0] = (byte)((c >> 11 & 31) * 255 / 31);
        pal[i, 1] = (byte)((c >> 5 & 63) * 255 / 63);
        pal[i, 2] = (byte)((c & 31) * 255 / 31);
    }
}
