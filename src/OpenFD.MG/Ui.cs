using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SN = System.Numerics;

namespace OpenFD.MG;

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

    /// <summary>
    /// Disque plein vu en perspective : l'image d'un cercle de rayon 1 par l'application linéaire (a, b) — a et b sont les
    /// images à l'écran de deux rayons perpendiculaires posés sur le sol. Décomposée en rotation × échelles (SVD 2×2).
    /// </summary>
    public void DiscOnPlane(float cx, float cy, SN.Vector2 a, SN.Vector2 b, SN.Vector4 c)
    {
        float m00 = a.X, m01 = b.X, m10 = a.Y, m11 = b.Y;
        float e = (m00 + m11) / 2, f = (m00 - m11) / 2, g = (m10 + m01) / 2, h = (m10 - m01) / 2;
        float q = MathF.Sqrt(e * e + h * h), r = MathF.Sqrt(f * f + g * g);
        float sx = q + r, sy = MathF.Abs(q - r);
        float rot = (MathF.Atan2(g, f) + MathF.Atan2(h, e)) / 2;
        Vector2 scale = new Vector2(2 * sx / Hard.Width, 2 * sy / Hard.Height);
        _sb.Draw(Hard, new Vector2(cx, cy), null, c.C(), rot, new Vector2(Hard.Width / 2f, Hard.Height / 2f), scale, SpriteEffects.None, 0);
    }

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

    /// <summary>
    /// Texte en italique : la police du jeu n'en a pas, chaque lettre est donc penchée (dessinée ligne de pixels par
    /// ligne de pixels, le haut décalé vers la droite).
    /// </summary>
    public float TextItalic(float x, float y, string s, SN.Vector4 c, float scale = 0f, float slant = 0.22f)
    {
        if (scale <= 0)
            scale = Scale;
        float x0 = x;
        Color col = c.C();
        foreach (char ch in Font.Fit(s))
        {
            if (!Font.Glyphs.TryGetValue(ch, out Font.Glyph g))
            {
                x += Font.LineHeight / 3 * scale;
                continue;
            }
            if (g.W > 1)
                for (int row = 0; row < g.H; row++)
                {
                    // décalage : nul sur la ligne de base, croissant vers le haut de la lettre
                    float lift = (Font.LineHeight - (g.Oy + row)) * scale * slant;
                    int top = (int)MathF.Round(y + (g.Oy + row) * scale), bottom = (int)MathF.Round(y + (g.Oy + row + 1) * scale);
                    _sb.Draw(Font.Texture, new Rectangle((int)MathF.Round(x + g.Ox * scale + lift), top, (int)(g.W * scale), Math.Max(1, bottom - top)),
                        new Rectangle(g.X, g.Y + row, g.W, 1), col);
                }
            x += g.Adv * scale;
        }
        return x - x0;
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
