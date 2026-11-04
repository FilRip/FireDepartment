using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using OpenFD.Formats;
using SN = System.Numerics;

namespace OpenFD.MG;

// écran de chargement d'une mission : l'image de la mission (campaign.ini : LoadingHintImage, 800 × 600), son titre
// (LoadingHintTitle) et son texte (LoadingHintText) dans les cadres de l'image, et une barre de progression bleue.
// Le tout est agrandi pour remplir l'écran, sans déformer l'image ; le reste est noir.
public sealed partial class OpenFDGame
{
    private sealed record LoadingHint(string? Image, string? TitleKey, string? TextKey);

    private Dictionary<string, LoadingHint>? _loadingHints;
    private float _loadProgress;

    // cadres de l'image (pixels de l'image 800 × 600)
    private static readonly Rectangle HintTitleBox = new(144, 445, 512, 26);
    private static readonly Rectangle HintTextBox = new(113, 487, 579, 97);
    private static readonly Rectangle ProgressBox = new(170, 10, 463, 10);

    /// <summary>Image, titre et texte de chargement de chaque mission (toutes les sections de campaign.ini qui ont un MissionFile).</summary>
    private LoadingHint? HintOf(string mission)
    {
        if (_loadingHints == null)
        {
            _loadingHints = new Dictionary<string, LoadingHint>(StringComparer.OrdinalIgnoreCase);
            try
            {
                ClassFile ini = ClassFile.Load(_files, "Design\\Mission\\campaign.ini");
                foreach (string section in ini.Sections)
                {
                    string? file = Clean(ini.Get(section, "MissionFile"));
                    if (file == null)
                        continue;
                    string[] parts = file.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
                    int at = Array.FindIndex(parts, p => p.Equals("Mission", StringComparison.OrdinalIgnoreCase));
                    if (at < 0 || at + 1 >= parts.Length)
                        continue;
                    _loadingHints[parts[at + 1]] = new LoadingHint(Clean(ini.Get(section, "LoadingHintImage")), Clean(ini.Get(section, "LoadingHintTitle")), Clean(ini.Get(section, "LoadingHintText")));
                }
            }
            catch
            {
            }
        }
        return _loadingHints.TryGetValue(mission, out LoadingHint? h) ? h : null;

        static string? Clean(string? s)
        {
            string? t = s?.Trim().Trim('"').Trim();
            return string.IsNullOrEmpty(t) ? null : t;
        }
    }

    /// <summary>Écran de chargement : dessiné et affiché tout de suite (le chargement bloque la boucle du jeu).</summary>
    private void Status(string text, float progress = -1)
    {
        if (progress >= 0)
            _loadProgress = Math.Clamp(progress, 0, 1);
        DrawLoading();
        GraphicsDevice.Present();
    }

    private void DrawLoading()
    {
        GraphicsDevice gd = GraphicsDevice;
        _fbW = gd.PresentationParameters.BackBufferWidth;
        _fbH = gd.PresentationParameters.BackBufferHeight;
        gd.Clear(Color.Black);
        _ui.Begin();
        // l'image 800 × 600 agrandie au plus grand qui tient dans l'écran, centrée
        float s = MathF.Min(_fbW / 800f, _fbH / 600f);
        float ox = (_fbW - 800 * s) / 2, oy = (_fbH - 600 * s) / 2;
        (float x, float y, float w, float h) Box(Rectangle r) => (ox + r.X * s, oy + r.Y * s, r.Width * s, r.Height * s);
        LoadingHint? hint = HintOf(_mission);
        if (hint?.Image != null && OverlayTexture(hint.Image + ".png") is { } img)
            _ui.Image(img, ox, oy, 800 * s, 600 * s, SN.Vector4.One);
        SN.Vector4 white = new(0.95f, 0.95f, 0.93f, 1);
        if (hint?.TitleKey != null)
        {
            (float x, float y, float w, float h) b = Box(HintTitleBox);
            string title = Loc(hint.TitleKey);
            // taille du texte : la hauteur du cadre, réduite si le titre est trop long
            float ts = b.h * 0.8f / _font.LineHeight;
            while (_font.Measure(title) * ts > b.w && ts > 0.3f)
                ts *= 0.92f;
            _ui.Text(b.x + (b.w - _font.Measure(title) * ts) / 2, b.y + (b.h - _font.LineHeight * ts) / 2, title, white, ts);
        }
        if (hint?.TextKey != null)
        {
            (float x, float y, float w, float h) b = Box(HintTextBox);
            string body = Loc(hint.TextKey);
            // le plus grand texte qui tient dans le cadre
            float ts = 20 * s / _font.LineHeight;
            List<string> lines = Wrap(body, b.w, ts);
            while (lines.Count * _font.LineHeight * ts * 1.1f > b.h && ts > 0.3f)
            {
                ts *= 0.92f;
                lines = Wrap(body, b.w, ts);
            }
            float lh = _font.LineHeight * ts * 1.1f, ty = b.y + (b.h - lines.Count * lh) / 2;
            foreach (string l in lines)
            {
                _ui.Text(b.x + (b.w - _font.Measure(l) * ts) / 2, ty, l, white, ts);
                ty += lh;
            }
        }
        // barre de progression bleue
        (float px, float py, float pw, float ph) = Box(ProgressBox);
        _ui.Rect(px, py, pw, ph, new SN.Vector4(0.03f, 0.05f, 0.12f, 0.85f));
        _ui.Rect(px, py, pw * _loadProgress, ph, new SN.Vector4(0.15f, 0.45f, 1f, 1));
        _ui.Frame(px, py, pw, ph, new SN.Vector4(0.35f, 0.6f, 1f, 0.9f), MathF.Max(1, s));
        _ui.End();
    }
}
