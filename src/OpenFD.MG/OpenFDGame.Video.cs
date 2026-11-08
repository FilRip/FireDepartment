using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SN = System.Numerics;

namespace OpenFD.MG;

// vidéos du jeu : Data\Video\intro.bik au démarrage (Échap, Entrée, Espace ou clic pour passer), puis
// Data\Video\menu.bik en boucle derrière les menus (à la place de l'image de fond)
public sealed partial class OpenFDGame
{
    private VideoScreen? _intro, _menuVideo;

    /// <summary>
    /// Musique du moment (Data\Sound\Music) : Menu en boucle dans les menus hors partie (principal, options, choix de la
    /// mission…), Briefing en boucle pendant la vidéo de présentation de la mission, Victory ou Defeat une fois à la fin
    /// d'une mission ; rien pendant la partie ni pendant les vidéos d'introduction et le générique.
    /// </summary>
    private void UpdateMusic()
    {
        if (_music == null)
            return;
        if (_intro != null || _splash != null)
            _music.Want(null, false);   // ni pendant l'écran d'accueil ni pendant l'intro
        else if (_briefing != null)
            _music.Want("Briefing", true);
        else if (_ended && !_menu && Host?.Outcome is bool won)
            _music.Want(won ? "Victory" : "Defeat", false);
        else if (_menu && !_inGame)
            _music.Want("Menu", true);
        else if (_session != null && !_ended)
        {
            // en partie : « Intro » puis l'ambiance de la mission en boucle ; « Autres missions » et Tutorial : l'ambiance
            // directement ; sans ambiance dans SoundDB.INI : « Intro » seule, une fois (pas pour le Tutorial)
            bool tutorial = _session.Mission.Equals("Tutorial", StringComparison.OrdinalIgnoreCase);
            string? amb = AmbiencePath();
            if (amb == null)
                _music.Want(tutorial ? null : "Intro", false);
            else if (tutorial || _missions.Any(m => m.Dir.Equals(_session.Mission, StringComparison.OrdinalIgnoreCase)))
                _music.Want(amb, true);
            else
                _music.Want("Intro", false, amb);
        }
        else
            _music.Want(null, false);
        _music.Update();
    }
    private bool _introDone;

    /// <summary>
    /// Ambiance de la mission : [MISSION] AmbienceMusic de la mission (« AMBIENCE_FIREWORKS ») → fichier de la section du
    /// même nom dans SoundDB.INI (Data\Sound\Ambience\Factory.mp3). Pas de section à ce nom (« AMBIENCE_INDUSTRIAL » de
    /// Ch_Plant et Vegas, « AMBIENCE_URBAN » de MonteCristo) : pas d'ambiance du tout.
    /// </summary>
    private string? AmbiencePath()
    {
        if (_audio == null || _session == null)
            return null;
        if (_ambience.TryGetValue(_session.Mission, out string? cached))
            return cached;
        string? label = _session.MissionConfig.Get("MISSION", "AmbienceMusic")?.Trim('"', ' ');
        string? file = !string.IsNullOrEmpty(label) && _audio.Db.TryGet(label, out SoundDb.Entry? e) && File.Exists(e.File) ? e.File : null;
        return _ambience[_session.Mission] = file;
    }
    private readonly Dictionary<string, string?> _ambience = new(StringComparer.OrdinalIgnoreCase);

    private string? VideoPath(string name)
    {
        string? dir = Directory.Exists(_files.DataDir) ? Directory.EnumerateDirectories(_files.DataDir).FirstOrDefault(d => Path.GetFileName(d).Equals("Video", StringComparison.OrdinalIgnoreCase)) : null;
        return dir == null ? null : Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private float VideoVolume => Math.Clamp(_settings.Volume, 0, 100) / 100f;

    // écran d'accueil (Data\Overlay\Textures\Splash.png) : 3 s sur fond noir, avant la vidéo d'intro
    private Texture2D? _splash;
    private float _splashLeft;
    public const float SplashTime = 3f;

    /// <summary>Au lancement du jeu : l'écran d'accueil (Splash.png), puis la vidéo d'intro, puis le menu principal.</summary>
    private void StartIntro()
    {
        if (_introDone || Environment.GetEnvironmentVariable("FD3_SANS_INTRO") != null)
        {
            _introDone = true;
            return;
        }
        try
        {
            if (FindFile(_files.DataDir, "Overlay", "Textures", "Splash.png") is { } sp)
            {
                using FileStream fs = File.OpenRead(sp);
                _splash = Texture2D.FromStream(GraphicsDevice, fs);
                _splashLeft = SplashTime;
                return;   // la vidéo d'intro démarre à la fin de l'écran d'accueil
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("Splash.png : " + e.Message);
        }
        StartIntroVideo();
    }

    /// <summary>Vidéo d'intro (intro.bik) ; rien si la vidéo ou la bibliothèque manque.</summary>
    private void StartIntroVideo()
    {
        if (VideoPath("intro.bik") is { } p)
            _intro = VideoScreen.Open(GraphicsDevice, p, false, VideoVolume);
        if (_intro == null)
            _introDone = true;
    }

    /// <summary>Menu principal → « Générique » : credits.bik, puis retour au menu (Échap, Entrée, Espace ou clic pour passer).</summary>
    private void StartCredits()
    {
        if (VideoPath("credits.bik") is not { } p)
            return;
        StopMenuVideo();
        _intro = VideoScreen.Open(GraphicsDevice, p, false, VideoVolume);
    }

    /// <summary>true tant que l'écran d'accueil ou l'intro passe (le reste du jeu attend).</summary>
    private bool UpdateIntro(float dt)
    {
        if (_splash != null)
        {
            _splashLeft -= MathF.Min(dt, 0.1f);   // la première image peut arriver tard (chargement) : 3 s vraiment visibles
            if (_splashLeft > 0)
                return true;
            _splash.Dispose();
            _splash = null;
            StartIntroVideo();
        }
        if (_intro == null)
            return false;
        KeyboardState k = Keyboard.GetState();
        MouseState m = Mouse.GetState();
        bool skip = IsActive && (Pressed(k, Keys.Escape) || Pressed(k, Keys.Enter) || Pressed(k, Keys.Space)
            || (m.LeftButton == ButtonState.Pressed && _mPrev.LeftButton == ButtonState.Released));
        _kPrev = k;
        _mPrev = m;
        _intro.Update(dt);
        if (skip || _intro.Finished)
        {
            Console.WriteLine($"Intro terminée ({(skip ? "passée" : "fin de la vidéo")})");
            _intro.Dispose();
            _intro = null;
            _introDone = true;
            return false;
        }
        return true;
    }

    private void DrawIntro()
    {
        GraphicsDevice.Clear(Color.Black);
        if (_splash != null)
        {
            // agrandie à la taille de l'écran, proportions gardées (bandes noires sur les côtés en 16/9)
            float s = MathF.Min((float)_fbW / _splash.Width, (float)_fbH / _splash.Height);
            float w = _splash.Width * s, h = _splash.Height * s;
            _ui.Begin();
            _ui.Image(_splash, (_fbW - w) / 2, (_fbH - h) / 2, w, h, SN.Vector4.One);
            _ui.End();
            return;
        }
        if (_intro == null)
            return;
        _ui.Begin();
        DrawVideoFit(_intro, false, SN.Vector4.One);
        _ui.End();
    }

    // vidéo à l'écran : entière avec bandes noires (intro) ou remplissant l'écran (fond de menu)
    private void DrawVideoFit(VideoScreen v, bool fill, SN.Vector4 tint)
    {
        float s = fill ? MathF.Max((float)_fbW / v.Width, (float)_fbH / v.Height) : MathF.Min((float)_fbW / v.Width, (float)_fbH / v.Height);
        float w = v.Width * s, h = v.Height * s;
        _ui.Image(v.Texture, (_fbW - w) / 2, (_fbH - h) / 2, w, h, tint);
    }

    /// <summary>Fond animé des menus (menu.bik en boucle) ; false s'il n'y en a pas (image fixe à la place).</summary>
    private bool UpdateMenuVideo(float dt)
    {
        if (_menuVideo == null && !_menuVideoTried)
        {
            _menuVideoTried = true;
            if (VideoPath("menu.bik") is { } p)
                _menuVideo = VideoScreen.Open(GraphicsDevice, p, true, VideoVolume * 0.6f);
        }
        _menuVideo?.Update(dt);
        return _menuVideo != null;
    }
    private bool _menuVideoTried;

    /// <summary>Le menu se ferme (mission lancée) : la vidéo de fond s'arrête.</summary>
    private void StopMenuVideo()
    {
        _menuVideo?.Dispose();
        _menuVideo = null;
        _menuVideoTried = false;
    }
}
