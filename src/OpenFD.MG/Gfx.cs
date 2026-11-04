using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SN = System.Numerics;

namespace OpenFD.MG;

public static class Conv
{
    // System.Numerics et XNA rangent les matrices de la même façon (vecteur-ligne, M11…M44)
    public static Matrix X(this SN.Matrix4x4 m) => new(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
    public static Vector3 X(this SN.Vector3 v) => new(v.X, v.Y, v.Z);
    public static Vector2 X(this SN.Vector2 v) => new(v.X, v.Y);
    public static Color C(this SN.Vector4 c) => new(c.X, c.Y, c.Z, c.W);
}

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
            for (int i = 0; i < (mip ? levels.Count : 1); i++) t.SetData(i, null, d, levels[i].off, levels[i].size);
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
                        for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
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
        for (int i = 0; i < px.Length; i++) px[i] = new Color(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);
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
        for (int i = 0; i < px.Length; i++) px[i] = new Color(r.rgba[i * 4], r.rgba[i * 4 + 1], r.rgba[i * 4 + 2], (byte)255);
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
            for (int c = 0; c < 4; c++) rgba[i * 4 + c] = masks[c] == 0 ? (byte)255 : (byte)((px & masks[c]) >> Shift(masks[c]));
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
            while (n < w * h && p + bpp <= d.Length) { Put(p); p += bpp; }
        else
            while (n < w * h && p < d.Length)
            {
                int c = d[p++];
                if ((c & 0x80) != 0)
                {
                    int s = p;
                    p += bpp;
                    for (int k = 0; k <= (c & 0x7f) && n < w * h; k++) Put(s);
                }
                else
                    for (int k = 0; k <= c && n < w * h; k++) { Put(p); p += bpp; }
            }
        if (!top)
        {
            byte[] flip = new byte[px.Length]; int row = w * 4;
            for (int y = 0; y < h; y++) Buffer.BlockCopy(px, y * row, flip, (h - 1 - y) * row, row);
            px = flip;
        }
        return (w, h, px);
    }
}

/// <summary>Sommet avec deux jeux d'uv : couleur et éclairage précalculé (DualTextureEffect).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VertexDual : IVertexType
{
    public Vector3 Position; public Vector2 Uv0; public Vector2 Uv1;
    public static readonly VertexDeclaration Decl = new(
        new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
        new VertexElement(12, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
        new VertexElement(20, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 1));
    VertexDeclaration IVertexType.VertexDeclaration => Decl;
}

/// <summary>
/// Maillage sur la carte graphique, découpé en groupes (texture, éclairage précalculé, détourage alpha).
/// Dual = sommets à deux uv (décor éclairé par le jeu), sinon position + normale + uv (objets, personnages).
/// </summary>
public sealed class MeshGpu : IDisposable
{
    public VertexBuffer Vb = null!; public IndexBuffer Ib = null!;
    public bool Dual; public int VertexCount;
    public List<(int start, int count, Texture2D? tex, Texture2D? lm, bool alpha)> Groups = new();
    private VertexPositionNormalTexture[]? _dyn;

    public static MeshGpu Create(GraphicsDevice gd, float[] pos, float[] nrm, float[] uv0, uint[] idx, bool dynamic = false)
    {
        int n = pos.Length / 3;
        VertexPositionNormalTexture[] v = new VertexPositionNormalTexture[n];
        for (int i = 0; i < n; i++)
            v[i] = new VertexPositionNormalTexture(new Vector3(pos[3 * i], pos[3 * i + 1], pos[3 * i + 2]), new Vector3(nrm[3 * i], nrm[3 * i + 1], nrm[3 * i + 2]), new Vector2(uv0[2 * i], uv0[2 * i + 1]));
        MeshGpu m = new MeshGpu { VertexCount = n };
        m.Vb = dynamic ? new DynamicVertexBuffer(gd, typeof(VertexPositionNormalTexture), n, BufferUsage.WriteOnly) : new VertexBuffer(gd, typeof(VertexPositionNormalTexture), n, BufferUsage.WriteOnly);
        m.Vb.SetData(v);
        if (dynamic)
            m._dyn = v;
        m.Ib = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, idx.Length, BufferUsage.WriteOnly);
        m.Ib.SetData(idx.Select(x => (int)x).ToArray());
        return m;
    }

    public static MeshGpu CreateDual(GraphicsDevice gd, VertexDual[] v, int[] idx)
    {
        MeshGpu m = new MeshGpu { VertexCount = v.Length, Dual = true };
        m.Vb = new VertexBuffer(gd, VertexDual.Decl, v.Length, BufferUsage.WriteOnly); m.Vb.SetData(v);
        m.Ib = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, idx.Length, BufferUsage.WriteOnly); m.Ib.SetData(idx);
        return m;
    }

    public static MeshGpu CreateLit(GraphicsDevice gd, VertexPositionNormalTexture[] v, int[] idx)
    {
        MeshGpu m = new MeshGpu { VertexCount = v.Length };
        m.Vb = new VertexBuffer(gd, typeof(VertexPositionNormalTexture), v.Length, BufferUsage.WriteOnly); m.Vb.SetData(v);
        m.Ib = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, idx.Length, BufferUsage.WriteOnly); m.Ib.SetData(idx);
        return m;
    }

    /// <summary>Maillage déformé (personnage animé) : remplace positions et normales.</summary>
    public void UpdateVertices(float[] pos, float[] nrm)
    {
        if (_dyn == null)
            return;
        for (int i = 0; i < _dyn.Length; i++)
        {
            _dyn[i].Position = new Vector3(pos[3 * i], pos[3 * i + 1], pos[3 * i + 2]);
            _dyn[i].Normal = new Vector3(nrm[3 * i], nrm[3 * i + 1], nrm[3 * i + 2]);
        }
        ((DynamicVertexBuffer)Vb).SetData(_dyn, 0, _dyn.Length, SetDataOptions.Discard);
    }

    public void Dispose() { Vb?.Dispose(); Ib?.Dispose(); }
}

/// <summary>Police bitmap du jeu (Data\Fonte\font18.fnt) : atlas + rectangles des caractères.</summary>
public sealed class Font
{
    public record struct Glyph(int X, int Y, int W, int H, int Ox, int Oy, int Adv);
    public readonly Dictionary<char, Glyph> Glyphs = new();
    public int AtlasW, AtlasH, LineHeight, Ascent;
    public Texture2D Texture = null!;

    // caractères absents de la police du jeu : remplacés par des équivalents qu'elle contient
    private static readonly Dictionary<char, string> Subst = new()
    {
        ['–'] = "-", ['—'] = "-", ['‒'] = "-", ['−'] = "-", ['…'] = "...", ['←'] = "<", ['→'] = ">", ['↑'] = "^", ['↓'] = "v",
        ['‘'] = "'", ['’'] = "'", ['“'] = "\"", ['”'] = "\"", ['«'] = "\"", ['»'] = "\"", ['\u00a0'] = " ", ['\u202f'] = " ", ['✔'] = "OK", ['✓'] = "OK",
    };

    /// <summary>Le texte tel que la police peut l'écrire (caractères absents remplacés, ou retirés).</summary>
    public string Fit(string s)
    {
        bool ok = true;
        foreach (char c in s)
            if (!Glyphs.ContainsKey(c))
            {
                ok = false;
                break;
            }
        if (ok)
            return s;
        System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            if (Glyphs.ContainsKey(c))
                sb.Append(c);
            else if (Subst.TryGetValue(c, out string? r))
            {
                foreach (char rc in r)
                    if (Glyphs.ContainsKey(rc) || rc == ' ')
                        sb.Append(rc);
            }
            else if (c == ' ')
                sb.Append(c);
        }
        return sb.ToString();
    }

    public float Measure(string s) => Fit(s).Sum(c => Glyphs.TryGetValue(c, out Glyph g) ? g.Adv : LineHeight / 3);

    /// <summary>
    /// Police du jeu (Data\Fonte\fontNN.fnt) : en-tête de 8 entiers (version, largeur et hauteur de l'image, taille…, nombre
    /// de caractères), les codes des caractères (UTF-16), un entier, puis par caractère son rectangle dans l'image
    /// (x0, y0, x1, y1 sur 16 bits), et l'image en 16 bits par pixel (ARGB 4-4-4-4).
    /// </summary>
    public static Font? LoadFnt(GraphicsDevice gd, string path)
    {
        try
        {
            byte[] d = File.ReadAllBytes(path);
            int w = BitConverter.ToInt32(d, 4), h = BitConverter.ToInt32(d, 8), size = BitConverter.ToInt32(d, 12), asc = BitConverter.ToInt32(d, 16), n = BitConverter.ToInt32(d, 28);
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096 || n <= 0 || n > 4096)
                return null;
            int img = d.Length - w * h * 2, rects = img - n * 8;
            if (rects < 32 + n * 2)
                return null;
            Font f = new Font { AtlasW = w, AtlasH = h, LineHeight = size, Ascent = asc };
            for (int i = 0; i < n; i++)
            {
                char c = (char)BitConverter.ToUInt16(d, 32 + 2 * i);
                int p = rects + 8 * i;
                int x0 = BitConverter.ToUInt16(d, p), y0 = BitConverter.ToUInt16(d, p + 2), x1 = BitConverter.ToUInt16(d, p + 4), y1 = BitConverter.ToUInt16(d, p + 6);
                if (x1 < x0 || y1 < y0 || x1 > w || y1 > h)
                    continue;
                f.Glyphs[c] = new Glyph(x0, y0, x1 - x0, y1 - y0, 0, 0, x1 - x0);
            }
            Color[] px = new Color[w * h];
            for (int i = 0; i < px.Length; i++)
            {
                int v = BitConverter.ToUInt16(d, img + 2 * i);
                px[i] = new Color((byte)(((v >> 8) & 15) * 17), (byte)(((v >> 4) & 15) * 17), (byte)((v & 15) * 17), (byte)(((v >> 12) & 15) * 17));
            }
            f.Texture = new Texture2D(gd, w, h); f.Texture.SetData(px);
            return f;
        }
        catch { return null; }
    }
}

/// <summary>Dessin 2D en pixels (interface, carte vue de dessus) avec SpriteBatch : rectangles, disques, images, texte.</summary>
public sealed class Ui
{
    private readonly SpriteBatch _sb;
    public Font Font = null!;
    public float Scale = 1;    // échelle de l'écran (texte de l'interface)
    public Ui(GraphicsDevice gd) { _sb = new SpriteBatch(gd); }
    public float LineHeight => Font.LineHeight * Scale;
    public float Measure(string s) => Font.Measure(s) * Scale;

    public void Begin(SamplerState? sampler = null) => _sb.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, sampler ?? SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
    public void End() => _sb.End();

    public void Rect(float x, float y, float w, float h, SN.Vector4 c) => _sb.Draw(Tex.White, new Rectangle((int)MathF.Round(x), (int)MathF.Round(y), (int)MathF.Max(1, MathF.Round(w)), (int)MathF.Max(1, MathF.Round(h))), c.C());
    public void Image(Texture2D t, float x, float y, float w, float h, SN.Vector4 c) => _sb.Draw(t, new Rectangle((int)x, (int)y, (int)w, (int)h), c.C());
    public void Image(Texture2D t, float x, float y, float w, float h, Rectangle src, SN.Vector4 c) => _sb.Draw(t, new Rectangle((int)MathF.Round(x), (int)MathF.Round(y), (int)MathF.Round(w), (int)MathF.Round(h)), src, c.C());

    /// <summary>Comme Begin, mais rien n'est dessiné hors du rectangle <paramref name="clip"/> (pixels).</summary>
    public void BeginClipped(Rectangle clip, SamplerState? sampler = null)
    {
        _sb.GraphicsDevice.ScissorRectangle = clip;
        _sb.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, sampler ?? SamplerState.LinearClamp, DepthStencilState.None, Scissor);
    }
    private static readonly RasterizerState Scissor = new() { CullMode = CullMode.None, ScissorTestEnable = true };

    /// <summary>Trait de (x0, y0) à (x1, y1), épaisseur t.</summary>
    public void Line(float x0, float y0, float x1, float y1, SN.Vector4 c, float t = 1)
    {
        float dx = x1 - x0, dy = y1 - y0, len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.01f)
            return;
        _sb.Draw(Tex.White, new Vector2(x0, y0), null, c.C(), MathF.Atan2(dy, dx), new Vector2(0, 0.5f), new Vector2(len / Tex.White.Width, t / Tex.White.Height), SpriteEffects.None, 0);
    }
    public void Frame(float x, float y, float w, float h, SN.Vector4 c, float t = 1)
    { Rect(x, y, w, t, c); Rect(x, y + h - t, w, t, c); Rect(x, y, t, h, c); Rect(x + w - t, y, t, h, c); }

    public void Disc(float cx, float cy, float r, SN.Vector4 c, int seg = 16)
    {
        // disque plein : la texture « Soft » a un bord adouci ; on agrandit un peu pour garder le rayon visuel
        float s = r * 2.3f;
        _sb.Draw(Hard, new Vector2(cx, cy), null, c.C(), 0, new Vector2(Hard.Width / 2f, Hard.Height / 2f), s / Hard.Width * 1.0f / 1.15f, SpriteEffects.None, 0);
    }

    private static Texture2D? _hard;
    private static Texture2D Hard => _hard!;
    public static void InitHard(GraphicsDevice gd)
    {
        const int S = 64; Color[] px = new Color[S * S];
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float dx = (x + 0.5f) - S / 2f, dy = (y + 0.5f) - S / 2f, r = MathF.Sqrt(dx * dx + dy * dy);
                float a = Math.Clamp(S / 2f / 1.15f - r + 0.5f, 0, 1);
                px[y * S + x] = new Color(1f, 1f, 1f, a);
            }
        _hard = new Texture2D(gd, S, S); _hard.SetData(px);
    }

    public float Text(float x, float y, string s, SN.Vector4 c, float scale = 0f)
    {
        if (scale <= 0)
            scale = Scale;
        float x0 = x; Color col = c.C();
        foreach (char ch in Font.Fit(s))
        {
            if (!Font.Glyphs.TryGetValue(ch, out Font.Glyph g))
            {
                x += Font.LineHeight / 3 * scale;
                continue;
            }
            if (g.W > 1)
                _sb.Draw(Font.Texture, new Rectangle((int)MathF.Round(x + g.Ox * scale), (int)MathF.Round(y + g.Oy * scale), (int)(g.W * scale), (int)(g.H * scale)),
                    new Rectangle(g.X, g.Y, g.W, g.H), col);
            x += g.Adv * scale;
        }
        return x - x0;
    }
}
