using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SN = System.Numerics;

namespace OpenFD.MG;

// éditeur : écran de démarrage (logo « OpenFD Mission Editor ») pendant le chargement de la liste des objets,
// montré au moins 2 secondes ; le chargement ne commence qu'une fois le logo affiché
public sealed partial class OpenFDGame
{
    private const double EdSplashMinSeconds = 2;

    private Texture2D? _edSplashLogo;
    private Stopwatch? _edSplashClock;     // lancé au premier affichage du logo
    private bool _edBootPending;           // chargement pas encore fait
    private bool _edSplashOn;              // l'écran de démarrage est montré

    /// <summary>Prépare l'écran de démarrage (sans lui en capture d'écran : chargement tout de suite).</summary>
    private void EdSplashBegin()
    {
        _edBootPending = true;
        // (essai : FD3_EDITEUR_SPLASH=1 le montre aussi en capture d'écran)
        if (CapturePath != null && Environment.GetEnvironmentVariable("FD3_EDITEUR_SPLASH") == null)
            return;
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Assets", "OpenFD_EDITOR_LOGO.png");
            if (File.Exists(path))
            {
                using FileStream fs = File.OpenRead(path);
                _edSplashLogo = Texture2D.FromStream(GraphicsDevice, fs);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("Logo de l'éditeur : " + e.Message);
        }
        _edSplashOn = _edSplashLogo != null;
    }

    /// <summary>
    /// À chaque pas : le chargement est fait dès que le logo a été affiché une fois ; l'écran reste au moins 2 s.
    /// true : l'écran de démarrage est encore là (rien d'autre ne réagit).
    /// </summary>
    private bool EdSplashUpdate()
    {
        if (_edBootPending && (!_edSplashOn || _edSplashClock != null))
        {
            _edBootPending = false;
            EdBoot();
        }
        if (_edSplashOn && !_edBootPending && _edSplashClock != null && _edSplashClock.Elapsed.TotalSeconds >= EdSplashMinSeconds)
        {
            _edSplashOn = false;
            _edSplashLogo?.Dispose();
            _edSplashLogo = null;
        }
        return _edSplashOn || _edBootPending;
    }

    /// <summary>Dessine l'écran de démarrage ; false s'il n'y en a pas.</summary>
    private bool EdSplashDraw()
    {
        if (!_edSplashOn || _edSplashLogo is not { } logo)
            return false;
        _edSplashClock ??= Stopwatch.StartNew();
        GraphicsDevice gd = GraphicsDevice;
        gd.SetRenderTarget(null);
        gd.Viewport = new Viewport(0, 0, _fbW, _fbH);
        gd.Clear(new Color(0.05f, 0.05f, 0.06f));
        // logo centré : au plus 60 % de la largeur et 45 % de la hauteur, proportions gardées
        float w = _fbW * 0.6f, h = w * logo.Height / logo.Width;
        if (h > _fbH * 0.45f)
        {
            h = _fbH * 0.45f;
            w = h * logo.Width / logo.Height;
        }
        float x = (_fbW - w) / 2, y = (_fbH - h) / 2 - _ui.LineHeight;
        _ui.Begin();
        _ui.Image(logo, x, y, w, h, SN.Vector4.One);
        string text = Tr("SplashLoading", "Chargement…");
        _ui.Text((_fbW - _ui.Measure(text)) / 2, y + h + 2 * _ui.LineHeight, text, new SN.Vector4(0.75f, 0.75f, 0.75f, 1));
        _ui.End();
        return true;
    }
}
