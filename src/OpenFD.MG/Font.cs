using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SN = System.Numerics;

namespace OpenFD.MG;

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
            f.Texture = new Texture2D(gd, w, h);
            f.Texture.SetData(px);
            return f;
        }
        catch { return null; }
    }
}
