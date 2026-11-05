using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SN = System.Numerics;

namespace OpenFD.MG;

/// <summary>Textures du jeu (DDS DXT1/3/5 ou 32 bits, TGA) chargées directement en Texture2D.</summary>
public static class Tex
{
    public static Texture2D White = null!;
    public static Texture2D Soft = null!;      // disque à bord adouci (particules, disques de la carte)

    public static void Init(GraphicsDevice gd)
    {
        White = new Texture2D(gd, 1, 1); White.SetData(new[] { Color.White });
        const int S = 64; Color[] px = new Color[S * S];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float dx = (x + 0.5f) / S * 2 - 1, dy = (y + 0.5f) / S * 2 - 1, r = dx * dx + dy * dy;
                float a = r > 1 ? 0 : (1 - r) * (1 - r);
                px[y * S + x] = new Color(1f, 1f, 1f, a);
            }
        Soft = new Texture2D(gd, S, S); Soft.SetData(px);
    }

    public static Texture2D? Dds(GraphicsDevice gd, byte[] d)
    {
        if (d.Length < 128 || d[0] != 'D' || d[1] != 'D' || d[2] != 'S')
            return null;
        int h = BitConverter.ToInt32(d, 12), w = BitConverter.ToInt32(d, 16), mips = Math.Max(1, BitConverter.ToInt32(d, 28));
        uint pfFlags = BitConverter.ToUInt32(d, 80), fourcc = BitConverter.ToUInt32(d, 84);
        int bits = BitConverter.ToInt32(d, 88), off = 128;
        if ((pfFlags & 4) != 0)
        {
            SurfaceFormat fmt; int block;
            switch (fourcc)
            {
                // textures lues telles quelles (luminosité proche du jeu d'origine) ; FD3_SRGB=1 : lues en sRGB, plus sombres
                case 0x31545844: fmt = Srgb ? SurfaceFormat.Dxt1SRgb : SurfaceFormat.Dxt1; block = 8; break;
                case 0x33545844: fmt = Srgb ? SurfaceFormat.Dxt3SRgb : SurfaceFormat.Dxt3; block = 16; break;
                case 0x35545844: fmt = Srgb ? SurfaceFormat.Dxt5SRgb : SurfaceFormat.Dxt5; block = 16; break;
                default: return null;
            }
            // niveaux présents dans le fichier (le plus petit niveau compressé fait 4 × 4)
            List<(int off, int size)> levels = new List<(int off, int size)>();
            int lw = w, lh = h;
            for (int i = 0; i < mips; i++)
            {
                int size = ((Math.Max(1, lw) + 3) / 4) * ((Math.Max(1, lh) + 3) / 4) * block;
                if (off + size > d.Length)
                    break;
                levels.Add((off, size)); off += size;
                if (lw == 1 && lh == 1)
                    break;
                lw = Math.Max(1, lw / 2); lh = Math.Max(1, lh / 2);
            }
            // DXT1 avec pixels transparents (détourage)
            if (block == 8 && Dxt1HasAlpha(d, levels[0].off, levels[0].size))
            {
                if (!Srgb)
                    fmt = SurfaceFormat.Dxt1a;
                else
                    return FromColors(gd, w, h, DecodeDxt1(d, levels[0].off, w, h), true, SurfaceFormat.ColorSRgb);   // le DXT1 sRGB n'a pas d'alpha
            }
            int full = 1; for (int x = Math.Max(w, h); x > 1; x /= 2) full++;
            bool mip = levels.Count == full;
            Texture2D t = new Texture2D(gd, w, h, mip, fmt);
            for (int i = 0; i < (mip ? levels.Count : 1); i++)
                t.SetData(i, null, d, levels[i].off, levels[i].size);
            return t;
        }
        int bpp = bits / 8;
        Color[] px = new Color[w * h];
        for (int i = 0; i < w * h && off + i * bpp + bpp <= d.Length; i++)
        {
            int s = off + i * bpp;
            px[i] = new Color(d[s + 2], d[s + 1], d[s], bpp == 4 && (pfFlags & 1) != 0 ? d[s + 3] : (byte)255);
        }
        return FromColors(gd, w, h, px, true, Srgb ? SurfaceFormat.ColorSRgb : SurfaceFormat.Color);
    }

    public static readonly bool Srgb = Environment.GetEnvironmentVariable("FD3_SRGB") == "1";

    private static bool Dxt1HasAlpha(byte[] d, int off, int size)
    {
        for (int b = off; b + 8 <= off + size; b += 8)
        {
            ushort c0 = BitConverter.ToUInt16(d, b), c1 = BitConverter.ToUInt16(d, b + 2);
            if (c0 > c1)
                continue;
            uint bits = BitConverter.ToUInt32(d, b + 4);
            for (int i = 0; i < 16; i++)
                if (((bits >> (2 * i)) & 3) == 3)
                    return true;
        }
        return false;
    }

    private static Color DxtColor(ushort c) => new((c >> 11 & 31) * 255 / 31, (c >> 5 & 63) * 255 / 63, (c & 31) * 255 / 31);

    private static Color[] DecodeDxt1(byte[] d, int off, int w, int h)
    {
        Color[] px = new Color[w * h];
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        Color[] pal = new Color[4];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int b = off + (by * bw + bx) * 8;
                ushort c0 = BitConverter.ToUInt16(d, b), c1 = BitConverter.ToUInt16(d, b + 2);
                pal[0] = DxtColor(c0); pal[1] = DxtColor(c1);
                if (c0 > c1)
                {
                    pal[2] = Color.Lerp(pal[0], pal[1], 1 / 3f);
                    pal[3] = Color.Lerp(pal[0], pal[1], 2 / 3f);
                }
                else
                {
                    pal[2] = Color.Lerp(pal[0], pal[1], 0.5f);
                    pal[3] = Color.Transparent;
                }
                uint bits = BitConverter.ToUInt32(d, b + 4);
                for (int i = 0; i < 16; i++)
                {
                    int x = bx * 4 + i % 4, y = by * 4 + i / 4;
                    if (x < w && y < h)
                        px[y * w + x] = pal[(bits >> (2 * i)) & 3];
                }
            }
        return px;
    }

    public static Texture2D FromColors(GraphicsDevice gd, int w, int h, Color[] px, bool mip = true, SurfaceFormat format = SurfaceFormat.Color)
    {
        Texture2D t = new Texture2D(gd, w, h, mip, format);
        t.SetData(px);
        if (mip)
        {
            // niveaux réduits calculés ici (moyenne 2 × 2)
            Color[] cur = px; int cw = w, ch = h, level = 1;
            while (cw > 1 || ch > 1)
            {
                int nw = Math.Max(1, cw / 2), nh = Math.Max(1, ch / 2);
                Color[] next = new Color[nw * nh];
                for (int y = 0; y < nh; y++)
                    for (int x = 0; x < nw; x++)
                    {
                        int r = 0, g = 0, b = 0, a = 0, n = 0;
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int sx = Math.Min(cw - 1, x * 2 + dx), sy = Math.Min(ch - 1, y * 2 + dy); Color c = cur[sy * cw + sx];
                                r += c.R; g += c.G; b += c.B; a += c.A; n++;
                            }
                        next[y * nw + x] = new Color(r / n, g / n, b / n, a / n);
                    }
                t.SetData(level++, null, next, 0, next.Length);
                cur = next; cw = nw; ch = nh;
            }
        }
        return t;
    }

    public static Texture2D? FromRgba(GraphicsDevice gd, int w, int h, byte[] rgba, bool mip = true, bool srgb = true)
    {
        Color[] px = new Color[w * h];
        for (int i = 0; i < px.Length; i++)
            px[i] = new Color(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);
        return FromColors(gd, w, h, px, mip, srgb && Srgb ? SurfaceFormat.ColorSRgb : SurfaceFormat.Color);
    }

    /// <summary>DDS (DXT1 ou 32 bits) → couleurs du premier niveau, pour les images qu'on analyse (mini-carte).</summary>
    public static (int w, int h, Color[] px)? DecodeColors(byte[] d)
    {
        if (d.Length < 128 || BitConverter.ToUInt32(d, 0) != 0x20534444)
            return null;
        int h = BitConverter.ToInt32(d, 12), w = BitConverter.ToInt32(d, 16);
        if (BitConverter.ToUInt32(d, 84) == 0x31545844)
            return d.Length >= 128 + ((w + 3) / 4) * ((h + 3) / 4) * 8 ? (w, h, DecodeDxt1(d, 128, w, h)) : null;
        if (DecodeDds32(d) is not { } r)
            return null;
        Color[] px = new Color[w * h];
        for (int i = 0; i < px.Length; i++)
            px[i] = new Color(r.rgba[i * 4], r.rgba[i * 4 + 1], r.rgba[i * 4 + 2], (byte)255);
        return (w, h, px);
    }

    /// <summary>DDS 32 bits non compressé (premier niveau) → RGBA, d'après les masques de couleur de l'en-tête.</summary>
    public static (int w, int h, byte[] rgba)? DecodeDds32(byte[] d)
    {
        if (d.Length < 128 || BitConverter.ToUInt32(d, 0) != 0x20534444 || BitConverter.ToUInt32(d, 84) != 0 || BitConverter.ToUInt32(d, 88) != 32)
            return null;
        int h = BitConverter.ToInt32(d, 12), w = BitConverter.ToInt32(d, 16);
        uint[] masks = { BitConverter.ToUInt32(d, 92), BitConverter.ToUInt32(d, 96), BitConverter.ToUInt32(d, 100), BitConverter.ToUInt32(d, 104) };
        int Shift(uint m) => m == 0 ? 0 : System.Numerics.BitOperations.TrailingZeroCount(m);
        if (d.Length < 128 + w * h * 4)
            return null;
        byte[] rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            uint px = BitConverter.ToUInt32(d, 128 + i * 4);
            for (int c = 0; c < 4; c++)
                rgba[i * 4 + c] = masks[c] == 0 ? (byte)255 : (byte)((px & masks[c]) >> Shift(masks[c]));
        }
        return (w, h, rgba);
    }

    /// <summary>TGA non compressé ou RLE, 24 ou 32 bits.</summary>
    public static (int w, int h, byte[] rgba)? DecodeTga(byte[] d)
    {
        int idLen = d[0], type = d[2], w = BitConverter.ToUInt16(d, 12), h = BitConverter.ToUInt16(d, 14), bpp = d[16] / 8;
        bool top = (d[17] & 0x20) != 0;
        if ((type != 2 && type != 10) || (bpp != 3 && bpp != 4))
            return null;
        int p = 18 + idLen; byte[] px = new byte[w * h * 4]; int n = 0;
        void Put(int s) { px[n * 4] = d[s + 2]; px[n * 4 + 1] = d[s + 1]; px[n * 4 + 2] = d[s]; px[n * 4 + 3] = bpp == 4 ? d[s + 3] : (byte)255; n++; }
        if (type == 2)
            while (n < w * h && p + bpp <= d.Length)
            {
                Put(p);
                p += bpp;
            }
        else
            while (n < w * h && p < d.Length)
            {
                int c = d[p++];
                if ((c & 0x80) != 0)
                {
                    int s = p;
                    p += bpp;
                    for (int k = 0; k <= (c & 0x7f) && n < w * h; k++)
                        Put(s);
                }
                else
                    for (int k = 0; k <= c && n < w * h; k++)
                    {
                        Put(p);
                        p += bpp;
                    }
            }
        if (!top)
        {
            byte[] flip = new byte[px.Length]; int row = w * 4;
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(px, y * row, flip, (h - 1 - y) * row, row);
            px = flip;
        }
        return (w, h, px);
    }
}
