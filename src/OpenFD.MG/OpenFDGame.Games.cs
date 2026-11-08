using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using OpenFD.Game;
using SN = System.Numerics;

namespace OpenFD.MG;

// choix du jeu : Fire Department 2 ou 3. Avec deux dossiers de jeu donnés au lancement (un par édition : Data\Config\Start.Ini,
// « License FD2 » ou « FD3 »), « Campagne » et « Autres missions » passent d'abord par cet écran (n° 9) : deux boutons,
// le logo de chaque jeu (Data\Overlay\Textures\LOGO_HOME.png), sans texte. Choisir l'autre jeu relance le programme sur ses
// données (Program : SwitchTo), directement sur l'écran demandé.
public sealed partial class OpenFDGame
{
    /// <summary>Jeux installés : édition (« FD2 », « FD3 ») et dossier Data. Un seul : pas d'écran de choix.</summary>
    public List<(string License, string DataDir)> Games { get; init; } = new();

    /// <summary>Jeu à relancer (dossier Data) et écran à ouvrir (1 campagne, 5 autres missions), quand le joueur en change.</summary>
    public (string DataDir, int Screen)? SwitchTo { get; private set; }

    /// <summary>Écran ouvert au démarrage (après un changement de jeu) : 1 campagne, 5 autres missions ; 0 menu principal.</summary>
    public int StartScreen { get; init; }

    /// <summary>Ce qui diffère entre les deux jeux pour les mêmes données (OpenFD.Game.IEdition) : FD3 (OpenFD.FD3) ou FD2 (OpenFD.FD2).</summary>
    private readonly IEdition _edition;

    private static IEdition MakeEdition(string dataDir) =>
        (Localization.StartIni(dataDir, "License") ?? "FD3").Equals("FD2", StringComparison.OrdinalIgnoreCase) ? new OpenFD.FD2.Fd2Edition() : new OpenFD.FD3.Fd3Edition();

    private int _gameFor;            // écran voulu après le choix : 1 campagne, 5 autres missions
    private int _gameIndex;
    private readonly Dictionary<string, Texture2D?> _gameLogos = new(StringComparer.OrdinalIgnoreCase);

    // « Campagne » ou « Autres missions » du menu principal : choix du jeu d'abord s'il y en a deux
    private void OpenGameFor(int screen)
    {
        if (Games.Count < 2)
        {
            OpenScreenOfGame(screen);
            return;
        }
        _gameFor = screen;
        _gameIndex = Math.Max(0, Games.FindIndex(g => SameDir(g.DataDir, _files.DataDir)));
        _menuNote = null;
        _screen = 9;
    }

    private void OpenScreenOfGame(int screen)
    {
        if (screen == 1)
        {
            OpenCampaign();
            return;
        }
        EnsureMissionList();
        _screen = 5;
        _menuNote = null;
    }

    private static bool SameDir(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private void ChooseGame(int i)
    {
        string dir = Games[i].DataDir;
        if (SameDir(dir, _files.DataDir))
        {
            OpenScreenOfGame(_gameFor);
            return;
        }
        // l'autre jeu : le programme se relance sur ses données (textures, sons, textes, missions…)
        _settings.Save();
        SwitchTo = (dir, _gameFor);
        Exit();
    }

    private Texture2D? GameLogo(string dataDir)
    {
        if (_gameLogos.TryGetValue(dataDir, out Texture2D? t))
            return t;
        t = null;
        try
        {
            string? path = FindFile(dataDir, "Overlay", "Textures", "LOGO_HOME.png");
            if (path != null)
            {
                using FileStream fs = File.OpenRead(path);
                t = Texture2D.FromStream(GraphicsDevice, fs);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("Logo : " + e.Message);
        }
        _gameLogos[dataDir] = t;
        return t;
    }

    // fichier sous un dossier, sans tenir compte de la casse
    private static string? FindFile(string dir, params string[] parts)
    {
        string cur = dir;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!Directory.Exists(cur))
                return null;
            bool last = i == parts.Length - 1;
            string? next = (last ? Directory.EnumerateFiles(cur) : Directory.EnumerateDirectories(cur))
                .FirstOrDefault(p => Path.GetFileName(p).Equals(parts[i], StringComparison.OrdinalIgnoreCase));
            if (next == null)
                return null;
            cur = next;
        }
        return cur;
    }

    // deux boutons côte à côte, au milieu de l'écran (pixels de l'image)
    private (float x, float y, float w, float h) GameButton(int i)
    {
        float w = MathF.Min(420 * _scale, (_fbW - 80 * _scale) / Games.Count - 24 * _scale), h = w * 0.42f;
        float total = Games.Count * w + (Games.Count - 1) * 40 * _scale;
        return ((_fbW - total) / 2 + i * (w + 40 * _scale), (_fbH - h) / 2, w, h);
    }

    private void GameChoiceInput(KeyboardState k, MouseState m)
    {
        // essai (captures) : FD3_CHOIX_JEU=1 (campagne) ou 5 (autres missions) → l'autre jeu est choisi tout de suite
        if (SwitchTo == null && int.TryParse(Environment.GetEnvironmentVariable("FD3_CHOIX_JEU"), out int testFor) && StartScreen == 0)
        {
            _gameFor = testFor;
            ChooseGame(Games.FindIndex(g => !SameDir(g.DataDir, _files.DataDir)));
            return;
        }
        if (Pressed(k, Keys.Escape))
        {
            _screen = 0;
            return;
        }
        if (Pressed(k, Keys.Left))
            _gameIndex = Math.Max(0, _gameIndex - 1);
        if (Pressed(k, Keys.Right))
            _gameIndex = Math.Min(Games.Count - 1, _gameIndex + 1);
        bool go = Pressed(k, Keys.Enter);
        (float mx, float my) = Fb(m.X, m.Y);
        bool click = m.LeftButton == ButtonState.Pressed && _mPrev.LeftButton == ButtonState.Released;
        for (int i = 0; i < Games.Count; i++)
        {
            (float x, float y, float w, float h) = GameButton(i);
            if (mx < x || mx > x + w || my < y || my > y + h)
                continue;
            if (m.X != _mPrev.X || m.Y != _mPrev.Y || click)
                _gameIndex = i;
            if (click)
                go = true;
        }
        if (go)
            ChooseGame(_gameIndex);
    }

    private void RenderGameChoice()
    {
        GraphicsDevice.Clear(new Color(0.05f, 0.05f, 0.06f));
        _ui.Begin();
        DrawMenuBack();
        for (int i = 0; i < Games.Count; i++)
        {
            (float x, float y, float w, float h) = GameButton(i);
            bool on = i == _gameIndex;
            _ui.Rect(x - 6 * _scale, y - 6 * _scale, w + 12 * _scale, h + 12 * _scale, on ? new SN.Vector4(0.85f, 0.35f, 0.1f, 0.9f) : new SN.Vector4(0.07f, 0.07f, 0.09f, 0.85f));
            _ui.Rect(x, y, w, h, new SN.Vector4(0.07f, 0.07f, 0.09f, 0.95f));
            if (GameLogo(Games[i].DataDir) is { } logo)
            {
                // logo entier, centré, proportions gardées
                float k = MathF.Min((w - 24 * _scale) / logo.Width, (h - 24 * _scale) / logo.Height);
                float lw = logo.Width * k, lh = logo.Height * k;
                _ui.Image(logo, x + (w - lw) / 2, y + (h - lh) / 2, lw, lh, SN.Vector4.One);
            }
        }
        _ui.End();
    }
}
