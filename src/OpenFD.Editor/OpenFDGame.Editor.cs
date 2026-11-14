using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using OpenFD.Formats;
using OpenFD.Game;
using SN = System.Numerics;

namespace OpenFD.MG;

// Éditeur de missions : la vue 3D du jeu sur les 3/4 gauches de la fenêtre (moins la liste des objets de la carte, touche
// Espace), la liste des objets à poser et les propriétés à droite, la barre de menu en haut. Les mouvements de la caméra
// sont ceux du jeu (mêmes fonctions). Les textes viennent de la section [EDITOR] des fichiers de langue (EditorText).
public sealed partial class OpenFDGame
{
    private EditorCatalog? _edCatalog;
    private RenderTarget2D? _edViewRt;
    private int _edViewX, _edViewW;       // vue 3D : bord gauche et largeur (pixels de l'image)
    private string _edStatus = "";
    private double _edStatusAt;
    private bool _edStatusError;
    private double _edClock;              // temps réel depuis le lancement (s)
    private bool _edLoaded;               // une mission est ouverte

    // souris (pixels de l'image), boutons appuyés et relâchés depuis le dernier dessin
    private float _edMx, _edMy;
    private bool _edPressL, _edReleaseL, _edPressR, _edDownL;
    private int _edWheel;
    private KeyboardState _edKeys, _edKeysPrev;
    private MouseState _edMousePrev;
    private readonly Dictionary<Keys, double> _edRepeat = new();

    private static readonly SN.Vector4 EdBack = new(0.13f, 0.13f, 0.15f, 1);
    private static readonly SN.Vector4 EdPanel = new(0.17f, 0.17f, 0.2f, 1);
    private static readonly SN.Vector4 EdLine = new(0.42f, 0.42f, 0.48f, 1);
    private static readonly SN.Vector4 EdText = new(0.93f, 0.92f, 0.9f, 1);
    private static readonly SN.Vector4 EdMuted = new(0.62f, 0.62f, 0.66f, 1);
    private static readonly SN.Vector4 EdAccent = new(1f, 0.48f, 0.18f, 1);
    private static readonly SN.Vector4 EdHover = new(0.28f, 0.28f, 0.33f, 1);
    private static readonly SN.Vector4 EdError = new(1f, 0.35f, 0.3f, 1);

    /// <summary>Titre de la fenêtre : « Open Fire Department Build N - » puis le titre traduit (languages\*.ini, clé Title).</summary>
    private static string EdWindowTitle => $"Open Fire Department Build {BuildNumber} - {Tr("Title", "Éditeur de missions")}";

    private static string Tr(string key, string french) => EditorText.T(key, french);
    private static string Tr(string key, string french, params object?[] args) => EditorText.T(key, french, args);

    // ---- mise en page (pixels de l'image) ----
    private float EdMenuH => 34 * _scale;
    private float EdScrollW => 16 * _scale;
    private float EdLeftW => _edLeftOpen ? MathF.Round(_fbW * 0.17f) : 0;

    private (float x, float y, float w, float h) EdListRect()
    {
        float y = EdMenuH, x = MathF.Round(_fbW * 0.75f);
        return (x, y, _fbW - x, (_fbH - y) * 0.75f);
    }

    private (float x, float y, float w, float h) EdPropsRect()
    {
        (float x, float y, float w, float h) l = EdListRect();
        return (l.x, l.y + l.h, l.w, _fbH - (l.y + l.h));
    }

    private bool EdInView(float x, float y) => x >= _edViewX && x < _edViewX + _edViewW && y >= EdMenuH && y < _fbH;

    private void EdMessage(string text, bool error = false)
    {
        _edStatus = text;
        _edStatusError = error;
        _edStatusAt = _edClock;
        Console.WriteLine((error ? "Éditeur (erreur) : " : "Éditeur : ") + text);
    }

    partial void EditorStart()
    {
        // Entrée : la touche Entrée et celle du pavé numérique (qui ne donne pas de caractère) : par l'appui de la touche
        Window.KeyDown += (_, e) =>
        {
            if (e.Key == Keys.Enter)
                EdChar('\r');
        };
        _loc ??= Localization.Load(_files.DataDir);
        EditorText.Language = string.IsNullOrEmpty(_settings.Language) ? _loc.Language : _settings.Language;
        _edStatus = Tr("StatusStart", "Cliquez sur « Charger » pour ouvrir une mission, ou « Nouveau » pour en créer une.");
        _miniHidden = true;   // mini-carte cachée au départ (touche M)
        _paused = true;
        Window.Title = EdWindowTitle;
        try
        {
            _edCatalog = EditorCatalog.Build(_files);
        }
        catch (Exception e)
        {
            _edCatalog = new EditorCatalog();
            EdMessage(Tr("CatalogError", "Liste des objets : {0}", e.Message), true);
        }
        if (Environment.GetEnvironmentVariable("FD3_EDITEUR_MISSION") is { Length: > 0 } m)
            EdLoadMission(m);
        if (Environment.GetEnvironmentVariable("FD3_EDITEUR_TEST") is { Length: > 0 } t)
            EdTestHooks(t);
    }

    /// <summary>
    /// Essais (captures) : FD3_EDITEUR_TEST=ordre;ordre… — « choisir:nom » (objet choisi, vue sur lui), « carte »,
    /// « mini », « onglet:n », « poser:élément » (posé au centre de la vue), « sauver:nom », « feux », « vue:x,y,z,lacet,tangage,dist »,
    /// « gauche » (liste de gauche), « nouveau:nom,l,h », « nouveaufen » (fenêtre « Nouveau »), « monter:x,y,n » (case relevée n crans), « dialogue », « equipement:clé », « case:x,y » (case choisie), « zonea:x,y,v ».
    /// </summary>
    private void EdTestHooks(string orders)
    {
        foreach (string order in orders.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = order.Split(':', 2);
            string arg = kv.Length > 1 ? kv[1] : "";
            switch (kv[0])
            {
                case "choisir":
                    EditorObject? found = arg switch
                    {
                        "feu" => _edObjects.FirstOrDefault(o => o.Kind == EditorItemKind.Fire && o.Lit),
                        "vehicule" => _edObjects.FirstOrDefault(o => o.Kind == EditorItemKind.Vehicle),
                        "pompier" => _edObjects.FirstOrDefault(o => o.Kind == EditorItemKind.Fireman),
                        "zone" => _edObjects.FirstOrDefault(o => o.Kind == EditorItemKind.Zone),
                        "camera" => _edObjects.FirstOrDefault(o => o.Kind == EditorItemKind.Camera),
                        "civil" => _edObjects.FirstOrDefault(o => o.Kind == EditorItemKind.Civilian),
                        "arbre" => _edObjects.FirstOrDefault(o => o.Kind == EditorItemKind.Tree),
                        "action" => _edObjects.FirstOrDefault(o => o.Source.GetComponent(0xA027) != null && EdScriptRefs(o).Count > 0),
                        _ => _edObjects.FirstOrDefault(o => o.Name.Equals(arg, StringComparison.OrdinalIgnoreCase)),
                    };
                    if (found is { } e)
                        EdFocusOn(e);
                    else
                        EdMessage("essai : objet " + arg + " introuvable", true);
                    break;
                case "carte":
                    _map = true;
                    break;
                case "mini":
                    _miniHidden = false;
                    break;
                case "feux":
                    _edAllFires = true;
                    break;
                case "gauche":
                    _edLeftOpen = true;
                    break;
                case "onglet":
                    _edTab = int.Parse(arg);
                    break;
                case "defiler":
                    _edBars[_edTab].Value = float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "toits":
                    _roofs = false;
                    break;
                case "liste":
                    EdOpenMissionList(5 * _scale, EdMenuH);
                    break;
                case "archives":
                    _edPacked = true;
                    break;
                case "tester":
                    EdTest();
                    break;
                case "dialogue":
                    EdSave();
                    break;
                case "vue":
                    float[] c = arg.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    _target = new SN.Vector3(c[0], c[1], c[2]);
                    _yaw = c[3];
                    _pitch = c[4];
                    _dist = c[5];
                    break;
                case "poser":
                    if (_edCatalog != null && _edCatalog.All.FirstOrDefault(i => i.Name.Equals(arg, StringComparison.OrdinalIgnoreCase)) is { } it)
                        EdDrop(it, new SN.Vector3(_target.X, _target.Y, EdGroundZ(new SN.Vector2(_target.X, _target.Y))));
                    break;
                case "sauver":
                    _edName = arg;
                    EdDoSave(null);
                    break;
                case "nouveau":
                    string[] n = arg.Split(',');
                    EdCreateMission(n[0], int.Parse(n[1]), int.Parse(n[2]));
                    if (_edPendingLoad is { } created)
                    {
                        _edPendingLoad = null;
                        EdLoadMission(created);
                    }
                    break;
                case "proprietes":
                    _edPropBar.Value = float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture);
                    _edPropUsed = 100000;   // (la vraie hauteur n'est connue qu'au premier dessin)
                    break;
                case "case":
                    int[] cc = arg.Split(',').Select(int.Parse).ToArray();
                    EdSelect(null);
                    _edCell = (cc[0], cc[1]);
                    break;
                case "zonea":
                    int[] za = arg.Split(',').Select(int.Parse).ToArray();
                    if (_session.Grid is { } zg && zg.Inside(za[0], za[1]))
                        zg.ZoneA[zg.Index(za[0], za[1])] = (ushort)za[2];
                    break;
                case "centrer":
                    if (_edObjects.FirstOrDefault(o => o.Name.Equals(arg, StringComparison.OrdinalIgnoreCase)) is { } fo)
                        EdFocusOn(fo);
                    break;
                case "filtre":
                    _edListFilter = arg;
                    break;
                case "filtregauche":
                    _edLeftFilter = arg;
                    break;
                case "annuler":
                    EdUndo();
                    break;
                case "retablir":
                    EdRedo();
                    break;
                case "nouveaufen":
                    EdAskNewMission();
                    break;
                case "monter":
                    int[] mv = arg.Split(',').Select(int.Parse).ToArray();
                    for (int i = 0; i < Math.Abs(mv[2]); i++)
                        EdStepHeight(mv[0], mv[1], mv[2] > 0 ? HeightStep : -HeightStep);
                    break;
                case "equipement":
                    if (_edSel is { Kind: EditorItemKind.Fireman } f)
                        EdToggleEquipment(f, arg);
                    break;
            }
        }
    }

    /// <summary>Taille de la vue : les 3/4 gauches de la fenêtre, moins la liste des objets de la carte si elle est ouverte.</summary>
    private void EdLayout()
    {
        _fbW = GraphicsDevice.PresentationParameters.BackBufferWidth;
        _fbH = GraphicsDevice.PresentationParameters.BackBufferHeight;
        _winW = Math.Max(1, Window.ClientBounds.Width);
        _winH = Math.Max(1, Window.ClientBounds.Height);
        _edViewX = (int)EdLeftW;
        _edViewW = Math.Max(64, (int)MathF.Round(_fbW * 0.75f) - _edViewX);
    }

    // la vue 3D : largeur réduite et origine décalée le temps de calculer la caméra, viser le sol ou dessiner
    private int _edFullW, _edFullWinW;
    private float _edFullMx;
    private void EdEnterView()
    {
        _edFullW = _fbW;
        _edFullWinW = _winW;
        _edFullMx = _edMx;
        _winW = Math.Max(1, (int)MathF.Round(_winW * (float)_edViewW / Math.Max(1, _fbW)));
        _fbW = _edViewW;
        _edMx -= _edViewX;
    }

    private void EdLeaveView()
    {
        _fbW = _edFullW;
        _winW = _edFullWinW;
        _edMx = _edFullMx;
    }

    /// <summary>La souris dans le repère de la vue (pixels de la fenêtre, origine au coin de la vue).</summary>
    private MouseState EdViewMouse(MouseState m)
    {
        int off = (int)MathF.Round(_edViewX * (float)Math.Max(1, Window.ClientBounds.Width) / Math.Max(1, GraphicsDevice.PresentationParameters.BackBufferWidth));
        return new MouseState(m.X - off, m.Y, m.ScrollWheelValue, m.LeftButton, m.MiddleButton, m.RightButton, m.XButton1, m.XButton2);
    }

    private bool EdKey(Keys k) => _edKeys.IsKeyDown(k) && !_edKeysPrev.IsKeyDown(k);
    private bool EdCtrl => _edKeys.IsKeyDown(Keys.LeftControl) || _edKeys.IsKeyDown(Keys.RightControl);
    private bool EdShift => _edKeys.IsKeyDown(Keys.LeftShift) || _edKeys.IsKeyDown(Keys.RightShift);
    private bool EdAlt => _edKeys.IsKeyDown(Keys.LeftAlt) || _edKeys.IsKeyDown(Keys.RightAlt);

    /// <summary>Touche tenue : répétée après 0,35 s, toutes les 60 ms.</summary>
    private bool EdKeyRepeat(Keys k)
    {
        if (!_edKeys.IsKeyDown(k))
        {
            _edRepeat.Remove(k);
            return false;
        }
        if (!_edKeysPrev.IsKeyDown(k))
        {
            _edRepeat[k] = _edClock + 0.35;
            return true;
        }
        if (_edRepeat.TryGetValue(k, out double at) && _edClock >= at)
        {
            _edRepeat[k] = _edClock + 0.06;
            return true;
        }
        return false;
    }

    partial void EditorUpdate(float dt)
    {
        _edClock += dt;
        EdLayout();
        if (_edPendingLoad is { } pending)
        {
            _edPendingLoad = null;
            EdLoadMission(pending);
            if (_edLoaded && _edPendingMessage != null)
                EdMessage(_edPendingMessage);
            _edPendingMessage = null;
        }
        KeyboardState k = Keyboard.GetState();
        MouseState m = Mouse.GetState();
        _edKeys = k;
        (float mx, float my) = Fb(m.X, m.Y);
        _edMx = mx;
        _edMy = my;
        bool active = IsActive;
        if (active && m.LeftButton == ButtonState.Pressed && _edMousePrev.LeftButton == ButtonState.Released)
            _edPressL = true;
        if (active && m.LeftButton == ButtonState.Released && _edMousePrev.LeftButton == ButtonState.Pressed)
            _edReleaseL = true;
        if (active && m.RightButton == ButtonState.Pressed && _edMousePrev.RightButton == ButtonState.Released)
            _edPressR = true;
        _edDownL = m.LeftButton == ButtonState.Pressed;
        if (active)
            _edWheel += m.ScrollWheelValue - _edMousePrev.ScrollWheelValue;
        if (active)
            EdKeys();
        if (!_edLoaded && _edDrag != null && !_edDownL)
            _edDrag = null;
        if (active && _edLoaded && _edDialog == null)
            EdViewInput(m, dt);
        EdUndoTrack(false);
        _edKeysPrev = k;
        _edMousePrev = m;
        _kPrev = k;
        _mPrev = m;
    }

    /// <summary>Raccourcis : Échap, Suppr, Ctrl+S, Tab, Espace ; touches du champ de texte en cours, de la fenêtre ouverte.</summary>
    private void EdKeys()
    {
        if (_edFocusId != null)
        {
            EdFieldKeys();
            return;
        }
        if (_edDialog != null)
        {
            EdDialogKeys();
            return;
        }
        if (EdKey(Keys.Escape))
        {
            if (_edPopup != null)
                _edPopup = null;
            else if (_edDrag != null)
                _edDrag = null;
            else if (_edMoving)
                EdCancelMove();
            else
                EdSelect(null);
        }
        if (EdCtrl && EdKey(Keys.S))
            EdSave();
        if (!_edLoaded)
            return;
        // annuler / rétablir
        if (EdCtrl && EdKey(Keys.Z))
        {
            if (EdShift)
                EdRedo();
            else
                EdUndo();
        }
        if (EdCtrl && EdKey(Keys.Y))
            EdRedo();
        if (EdKey(Keys.Tab))
            _map = !_map;
        if (EdKey(Keys.Space))
            _edLeftOpen = !_edLeftOpen;
        if (EdKey(Keys.Delete) && _edSel != null && _edDrag == null)
            EdDelete(_edSel);
        if (EdKey(Keys.F) && !EdCtrl)
        {
            _edAllFires = !_edAllFires;
            EdMessage(_edAllFires ? Tr("FiresAll", "Foyers éteints montrés (points gris)") : Tr("FiresLit", "Seuls les feux allumés au départ sont montrés"));
        }
    }

    partial void EditorChar(char ch)
    {
        if (ch is not ('\r' or '\n'))
            EdChar(ch);
    }

    private void EdChar(char ch)
    {
        if (_edFocusId != null)
        {
            EdFieldChar(ch);
            return;
        }
        if (!_edLoaded || _edDialog != null)
            return;
        switch (char.ToLowerInvariant(ch))
        {
            case 'm':
                _miniHidden = !_miniHidden;
                break;
            case 't':
                _roofs = !_roofs;
                EdMessage(_roofs ? Tr("RoofsOn", "Toits : montrés") : Tr("RoofsOff", "Toits : cachés"));
                break;
            case 'b':
                _shadowsOn = !_shadowsOn;
                break;
            case 'l':
                _edLids = !_edLids;
                EdMessage(_edLids ? Tr("LidsOn", "Couvercles noirs des pièces : montrés") : Tr("LidsOff", "Couvercles noirs des pièces : cachés"));
                break;
        }
    }

    private bool _edLids;
    partial void EditorLids(ref bool show) => show = _edLids;

    /// <summary>Chargement d'une mission : « Chargement... » au milieu de l'écran et la barre de progression, sans image.</summary>
    partial void EditorLoading(ref bool drawn)
    {
        drawn = true;
        GraphicsDevice.Clear(new Color(EdBack.X, EdBack.Y, EdBack.Z));
        float s = MathF.Max(1, _fbH / 720f);
        string text = Tr("Loading", "Chargement...");
        _ui.Begin();
        _ui.Text((_fbW - _ui.Measure(text)) / 2, _fbH / 2f - _ui.LineHeight - 12 * s, text, EdText);
        float pw = MathF.Min(_fbW * 0.5f, 520 * s), ph = 14 * s, px = (_fbW - pw) / 2, py = _fbH / 2f + 4 * s;
        _ui.Rect(px, py, pw, ph, new SN.Vector4(0.03f, 0.05f, 0.12f, 0.85f));
        _ui.Rect(px, py, pw * _loadProgress, ph, new SN.Vector4(0.15f, 0.45f, 1f, 1));
        _ui.Frame(px, py, pw, ph, new SN.Vector4(0.35f, 0.6f, 1f, 0.9f), s);
        _ui.End();
    }

    partial void EditorDraw(float time)
    {
        EdLayout();
        GraphicsDevice gd = GraphicsDevice;
        if (_edTreesDirty)
            EdRebuildTrees();
        EdRenderPreviews();
        EdRenderView(time);
        gd.SetRenderTarget(null);
        gd.Viewport = new Viewport(0, 0, _fbW, _fbH);
        gd.Clear(new Color(EdBack.X, EdBack.Y, EdBack.Z));
        _ui.Begin();
        if (_edViewRt != null)
            _ui.Image(_edViewRt, _edViewX, 0, _edViewW, _fbH, SN.Vector4.One);
        _ui.End();
        EdInterface();
        _edPressL = false;
        _edReleaseL = false;
        _edPressR = false;
        _edWheel = 0;
    }

    /// <summary>La vue 3D (ou la carte vue de dessus), dessinée dans une image à part, puis posée à sa place.</summary>
    private void EdRenderView(float time)
    {
        GraphicsDevice gd = GraphicsDevice;
        int w = _edViewW, h = _fbH;
        if (_edViewRt == null || _edViewRt.Width != w || _edViewRt.Height != h)
        {
            _edViewRt?.Dispose();
            _edViewRt = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.Depth24, gd.PresentationParameters.MultiSampleCount, RenderTargetUsage.DiscardContents);
        }
        EdEnterView();
        try
        {
            if (_edLoaded && !_map)
                RenderShadowMap();
            gd.SetRenderTarget(_edViewRt);
            if (!_edLoaded)
            {
                gd.Clear(new Color(0.06f, 0.06f, 0.07f));
                _ui.Begin();
                string s = Tr("NoMission", "Aucune mission : cliquez sur « Charger » ou « Nouveau »");
                _ui.Text((w - _ui.Measure(s)) / 2, h / 2f, s, EdMuted);
                _ui.End();
                return;
            }
            if (_map)
                EdRenderMap();
            else
            {
                Render3D(time);
                EdOverlay3D();
            }
            RenderMinimap();
            EdViewHelp();
        }
        finally
        {
            EdLeaveView();
        }
    }

    /// <summary>Aide en bas de la vue : les touches.</summary>
    private void EdViewHelp()
    {
        _ui.Begin();
        string help = _edDrag != null || _edMoving
            ? Tr("HelpRotate", "Flèches : tourner l'objet (gauche/droite : autour de la verticale ; haut/bas : basculer) · Pg.Préc/Suiv : hauteur · Échap : annuler")
            : EdHeightKey() != 0
            ? Tr("HelpHeight", "A + clic : monter la case d'un cran · Q + clic : la descendre (Maj : 4 crans) · glisser : plusieurs cases")
            : _edSel != null
            ? Tr("HelpSelected", "Flèches : déplacer l'objet (Maj : plus loin) · Ctrl + Pg.Préc/Suiv : sa hauteur · Pg.Préc/Suiv : monter / descendre la vue · glisser : déplacer · Suppr : enlever · Ctrl+Z / Ctrl+Y : annuler / rétablir · Échap : rien de choisi")
            : Tr("HelpView", "Clic : choisir un objet ou une case · Espace : liste des objets (clic droit : y aller) · Pg.Préc/Suiv : monter / descendre la vue · Tab : vue de dessus · M : mini-carte · T : toits · F : foyers éteints · A / Q + clic : relief · Ctrl+Z / Ctrl+Y : annuler / rétablir");
        float s = _scale * 0.8f;
        float tw = _font.Measure(help) * s;
        _ui.Rect(0, _fbH - _font.LineHeight * s - 8 * _scale, Math.Min(_fbW, tw + 16 * _scale), _font.LineHeight * s + 8 * _scale, new SN.Vector4(0, 0, 0, 0.55f));
        _ui.Text(8 * _scale, _fbH - _font.LineHeight * s - 4 * _scale, help, EdMuted, s);
        _ui.End();
    }

    /// <summary>Barre de menu, listes, propriétés, fenêtres par-dessus.</summary>
    private void EdInterface()
    {
        // une fenêtre de question ouverte : le reste de l'interface ne reçoit rien
        bool press = _edPressL;
        int wheel = _edWheel;
        if (_edDialog != null)
        {
            _edPressL = false;
            _edPressR = false;
            _edWheel = 0;
        }
        EdPopupInput();
        EdFocusClickAway();
        EdDrawLeftList();
        EdDrawPanel();
        EdDrawProperties();
        EdDrawMenu();
        EdDrawDragGhost();
        EdDrawPopup();
        if (_edDialog != null)
        {
            _edPressL = press;
            _edWheel = wheel;
            EdDrawDialog();
        }
        // un clic qui n'a servi à rien laisse le champ en cours
        if (_edPressL && _edFocusId != null)
            EdCommitFocus();
    }

    // ---- petits éléments d'interface (dessinés et cliqués en même temps) ----
    private bool EdHit(float x, float y, float w, float h) => _edMx >= x && _edMx < x + w && _edMy >= y && _edMy < y + h;

    private bool EdButton(float x, float y, float w, float h, string label, bool on = false, bool enabled = true)
    {
        bool hover = enabled && EdHit(x, y, w, h);
        _ui.Rect(x, y, w, h, on ? new SN.Vector4(0.85f, 0.35f, 0.1f, 0.95f) : hover ? EdHover : new SN.Vector4(0.22f, 0.22f, 0.26f, 1));
        _ui.Frame(x, y, w, h, EdLine, MathF.Max(1, _scale));
        string shown = EdFit(label, w - 6 * _scale);
        float tw = _ui.Measure(shown);
        _ui.Text(x + (w - tw) / 2, y + (h - _ui.LineHeight) / 2, shown, enabled ? EdText : EdMuted);
        if (hover && _edPressL)
        {
            _edPressL = false;
            EdCommitFocus();
            return true;
        }
        return false;
    }

    private bool EdCheck(float x, float y, float h, string label, bool value)
    {
        float b = h - 6 * _scale;
        bool hover = EdHit(x, y, b + 8 * _scale + _ui.Measure(label), h);
        _ui.Rect(x, y + 3 * _scale, b, b, hover ? EdHover : new SN.Vector4(0.1f, 0.1f, 0.12f, 1));
        _ui.Frame(x, y + 3 * _scale, b, b, EdLine, MathF.Max(1, _scale));
        if (value)
            _ui.Rect(x + 4 * _scale, y + 7 * _scale, b - 8 * _scale, b - 8 * _scale, EdAccent);
        _ui.Text(x + b + 8 * _scale, y + (h - _ui.LineHeight) / 2, label, EdText);
        if (hover && _edPressL)
        {
            _edPressL = false;
            EdCommitFocus();
            return true;
        }
        return false;
    }

    /// <summary>Texte coupé (« … ») pour tenir dans la largeur.</summary>
    // nom qui défile sous la souris : celui survolé, depuis quand
    private string? _edMarqueeKey;
    private double _edMarqueeAt;

    /// <summary>
    /// Nom à afficher dans <paramref name="w"/> : coupé (« ... ») s'il est trop long ; survolé par la souris, il défile
    /// (une lettre de plus à gauche tous les 0,12 s, après une pause, jusqu'à la fin du nom, puis il recommence).
    /// </summary>
    private string EdMarquee(string s, float w, bool hover, string key)
    {
        if (!hover || _ui.Measure(s) <= w)
            return EdFit(s, w);
        if (_edMarqueeKey != key)
        {
            _edMarqueeKey = key;
            _edMarqueeAt = _edClock;
        }
        // nombre de lettres à enlever au début pour que la fin du nom tienne
        int steps = 0;
        while (steps < s.Length - 1 && _ui.Measure(s[steps..]) > w)
            steps++;
        const double Pause = 0.8, Step = 0.12, End = 1.0;
        double cycle = Pause + steps * Step + End;
        double t = (_edClock - _edMarqueeAt) % cycle;
        int k = t < Pause ? 0 : Math.Min(steps, (int)((t - Pause) / Step));
        return EdFit(s[k..], w);
    }

    private string EdFit(string s, float w)
    {
        if (_ui.Measure(s) <= w)
            return s;
        while (s.Length > 1 && _ui.Measure(s + "...") > w)
            s = s[..^1];
        return s + "...";
    }

    // ---- champ de texte : un seul à la fois ; Entrée, Tab ou un clic ailleurs le valident ----
    private string? _edFocusId;
    private string _edFocusText = "";
    private int _edCaret;
    private Action<string>? _edFocusCommit;
    private (float x, float y, float w, float h) _edFocusRect;

    private void EdField(string id, float x, float y, float w, float h, string value, Action<string> commit, bool enabled = true)
    {
        bool focused = _edFocusId == id;
        bool hover = enabled && EdHit(x, y, w, h);
        _ui.Rect(x, y, w, h, focused ? new SN.Vector4(0.05f, 0.05f, 0.07f, 1) : hover ? new SN.Vector4(0.12f, 0.12f, 0.15f, 1) : new SN.Vector4(0.09f, 0.09f, 0.11f, 1));
        _ui.Frame(x, y, w, h, focused ? EdAccent : EdLine, MathF.Max(1, _scale));
        string shown = focused ? _edFocusText : value;
        float pad = 5 * _scale;
        float ty = y + (h - _ui.LineHeight) / 2;
        // texte trop long : on montre la fin (là où l'on écrit)
        string visible = shown;
        int start = 0;
        if (focused)
        {
            while (start < _edCaret && _ui.Measure(shown[start.._edCaret]) > w - 2 * pad)
                start++;
            visible = shown[start..];
        }
        _ui.Text(x + pad, ty, EdFit(visible, w - 2 * pad), enabled ? EdText : EdMuted);
        if (focused)
        {
            _edFocusRect = (x, y, w, h);
            if ((int)(_edClock * 2) % 2 == 0)
            {
                float cx = x + pad + _ui.Measure(shown[start..Math.Min(_edCaret, shown.Length)]);
                _ui.Rect(cx, y + 3 * _scale, MathF.Max(1, _scale), h - 6 * _scale, EdText);
            }
        }
        if (hover && _edPressL && !focused)
        {
            _edPressL = false;
            EdCommitFocus();
            _edFocusId = id;
            _edFocusText = value;
            _edCaret = value.Length;
            _edFocusCommit = commit;
            _edFocusRect = (x, y, w, h);
        }
        else if (hover && _edPressL && focused)
        {
            // clic dans le champ : le curseur va à cet endroit
            _edPressL = false;
            float px = _edMx - (x + pad);
            int c = start;
            while (c < shown.Length && _ui.Measure(shown[start..(c + 1)]) < px)
                c++;
            _edCaret = c;
        }
    }

    private void EdCommitFocus()
    {
        if (_edFocusId == null)
            return;
        Action<string>? commit = _edFocusCommit;
        string text = _edFocusText;
        _edFocusId = null;
        _edFocusCommit = null;
        commit?.Invoke(text);
    }

    private void EdFocusClickAway()
    {
        if (_edFocusId != null && _edPressL)
        {
            (float x, float y, float w, float h) r = _edFocusRect;
            if (!EdHit(r.x, r.y, r.w, r.h))
                EdCommitFocus();
        }
    }

    private void EdFieldChar(char ch)
    {
        switch (ch)
        {
            case '\b':
                if (_edCaret > 0)
                {
                    _edFocusText = _edFocusText.Remove(_edCaret - 1, 1);
                    _edCaret--;
                }
                return;
            case '\r':
            case '\n':
            case '\t':
                EdCommitFocus();
                return;
        }
        if (ch < ' ' || ch == 127)
            return;
        _edFocusText = _edFocusText.Insert(_edCaret, ch.ToString());
        _edCaret++;
    }

    private void EdFieldKeys()
    {
        if (EdKey(Keys.Escape))
        {
            _edFocusId = null;
            _edFocusCommit = null;
            return;
        }
        if (EdKeyRepeat(Keys.Left))
            _edCaret = Math.Max(0, _edCaret - 1);
        if (EdKeyRepeat(Keys.Right))
            _edCaret = Math.Min(_edFocusText.Length, _edCaret + 1);
        if (EdKey(Keys.Home))
            _edCaret = 0;
        if (EdKey(Keys.End))
            _edCaret = _edFocusText.Length;
        if (EdKeyRepeat(Keys.Delete) && _edCaret < _edFocusText.Length)
            _edFocusText = _edFocusText.Remove(_edCaret, 1);
    }

    // ---- liste déroulante (choix d'une valeur, liste des missions) : par-dessus tout le reste ----
    private sealed class EdPopupList
    {
        public required string Title;
        public required List<string> Items;
        public required Action<int> Pick;
        public required (float x, float y, float w, float h) Rect;
        public readonly ScrollBar Bar = new();
        public int Current = -1;
        public HashSet<int> Dim = new();
    }

    private EdPopupList? _edPopup;

    private float EdRowH => _ui.LineHeight + 8 * _scale;

    private void EdOpenPopup(string title, List<string> items, Action<int> pick, float x, float y, float w, int current = -1, float maxH = 0)
    {
        float rows = Math.Min(items.Count, 18);
        float h = EdRowH * (rows + 1) + 8 * _scale;
        if (maxH > 0)
            h = MathF.Min(h, maxH);
        h = MathF.Min(h, _fbH - 20 * _scale);
        x = Math.Clamp(x, 0, MathF.Max(0, _fbW - w));
        y = Math.Clamp(y, 0, MathF.Max(0, _fbH - h));
        _edPopup = new EdPopupList { Title = title, Items = items, Pick = pick, Rect = (x, y, w, h), Current = current };
        if (current >= 0)
            _edPopup.Bar.Value = MathF.Max(0, (current - 3) * EdRowH);
    }

    private void EdPopupInput()
    {
        if (_edPopup is not { } p)
            return;
        (float x, float y, float w, float h) = p.Rect;
        float top = y + EdRowH + 4 * _scale, area = h - (top - y) - 4 * _scale;
        if (EdHit(x, y, w, h) && _edWheel != 0)
        {
            p.Bar.Value -= _edWheel / 120f * EdRowH * 3;
            p.Bar.Clamp(p.Items.Count * EdRowH, area);
            _edWheel = 0;
        }
        if (!_edPressL && !_edPressR)
            return;
        if (!EdHit(x, y, w, h))
        {
            _edPopup = null;   // clic à côté : fermée
            _edPressL = false;
            _edPressR = false;
            return;
        }
        _edPressR = false;
        // clic sur la barre de défilement : pour elle (au dessin)
        if (!_edPressL || _edMy < top || _edMx >= x + w - EdScrollW - 4 * _scale)
            return;
        _edPressL = false;
        int i = (int)((_edMy - top + p.Bar.Value) / EdRowH);
        if (i >= 0 && i < p.Items.Count && !p.Dim.Contains(i))
        {
            _edPopup = null;
            p.Pick(i);
        }
    }

    private void EdDrawPopup()
    {
        if (_edPopup is not { } p)
            return;
        (float x, float y, float w, float h) = p.Rect;
        float top = y + EdRowH + 4 * _scale, area = h - (top - y) - 4 * _scale, sb = EdScrollW;
        bool scroll = p.Items.Count * EdRowH > area;
        float iw = scroll ? w - sb - 4 * _scale : w;
        _ui.Begin();
        _ui.Rect(x + 4 * _scale, y + 4 * _scale, w, h, new SN.Vector4(0, 0, 0, 0.5f));
        _ui.Rect(x, y, w, h, EdPanel);
        _ui.Frame(x, y, w, h, EdAccent, MathF.Max(1, 2 * _scale));
        _ui.Text(x + 8 * _scale, y + 4 * _scale, EdFit(p.Title, w - 16 * _scale), EdAccent);
        if (scroll)
            p.Bar.Run(_ui, x + w - sb - 2 * _scale, top, sb, area, p.Items.Count * EdRowH, area, EdRowH, _edMx, _edMy, ref _edPressL, _edDownL, _edClock);
        _ui.End();
        _ui.BeginClipped(new Rectangle((int)x, (int)top, (int)iw, (int)area));
        for (int i = 0; i < p.Items.Count; i++)
        {
            float ry = top + i * EdRowH - p.Bar.Value;
            if (ry + EdRowH < top || ry > top + area)
                continue;
            bool hover = EdHit(x, ry, iw, EdRowH) && _edMy >= top && _edMy < top + area;
            if (i == p.Current)
                _ui.Rect(x + 2 * _scale, ry, iw - 4 * _scale, EdRowH - 2 * _scale, new SN.Vector4(0.85f, 0.35f, 0.1f, 0.45f));
            else if (hover && !p.Dim.Contains(i))
                _ui.Rect(x + 2 * _scale, ry, iw - 4 * _scale, EdRowH - 2 * _scale, EdHover);
            _ui.Text(x + 10 * _scale, ry + 4 * _scale, EdFit(p.Items[i], iw - 20 * _scale), p.Dim.Contains(i) ? EdMuted : EdText);
        }
        _ui.End();
    }

    // ---- fenêtre de question (confirmation, choix du format, nouvelle mission) : tout le reste attend ----
    private sealed class EdDialogBox
    {
        public required string Title;
        public List<string> Lines = new();
        public Action<float, float, float>? Body;   // contenu en plus (champs) : x, y, largeur
        public float BodyH;
        public List<(string label, Action act)> Buttons = new();
    }

    private EdDialogBox? _edDialog;

    private void EdAsk(string title, IEnumerable<string> lines, params (string label, Action act)[] buttons)
    {
        EdCommitFocus();
        _edPopup = null;
        _edDialog = new EdDialogBox { Title = title, Lines = lines.ToList(), Buttons = buttons.ToList() };
    }

    private void EdDialogKeys()
    {
        if (_edDialog is not { } d)
            return;
        // Échap : le dernier bouton (Annuler) ; Entrée : le premier
        if (EdKey(Keys.Escape) && d.Buttons.Count > 0)
        {
            _edDialog = null;
            d.Buttons[^1].act();
        }
        else if (EdKey(Keys.Enter) && d.Buttons.Count > 0)
        {
            _edDialog = null;
            d.Buttons[0].act();
        }
    }

    private void EdDrawDialog()
    {
        if (_edDialog is not { } d)
            return;
        float s = _scale, lh = _ui.LineHeight + 4 * s, w = MathF.Min(_fbW - 40 * s, 640 * s);
        List<string> lines = d.Lines.SelectMany(l => Wrap(l, w - 32 * s, _scale)).ToList();
        float bh = 34 * s, h = 24 * s + lh + lines.Count * lh + d.BodyH + 18 * s + bh + 16 * s;
        float x = (_fbW - w) / 2, y = MathF.Max(10 * s, (_fbH - h) / 3);
        _ui.Begin();
        _ui.Rect(0, 0, _fbW, _fbH, new SN.Vector4(0, 0, 0, 0.45f));
        _ui.Rect(x, y, w, h, EdPanel);
        _ui.Frame(x, y, w, h, EdAccent, 2 * s);
        _ui.Text(x + 16 * s, y + 12 * s, EdFit(d.Title, w - 32 * s), EdAccent);
        float ly = y + 16 * s + lh;
        foreach (string l in lines)
        {
            _ui.Text(x + 16 * s, ly, l, EdText);
            ly += lh;
        }
        d.Body?.Invoke(x + 16 * s, ly, w - 32 * s);
        float bw = MathF.Min(220 * s, (w - 32 * s - (d.Buttons.Count - 1) * 8 * s) / Math.Max(1, d.Buttons.Count));
        float bx = x + w - 16 * s - d.Buttons.Count * bw - (d.Buttons.Count - 1) * 8 * s, by = y + h - bh - 14 * s;
        foreach ((string label, Action act) in d.Buttons.ToList())
        {
            if (EdButton(bx, by, bw, bh, label))
            {
                if (_edDialog == d)
                    _edDialog = null;
                act();
            }
            bx += bw + 8 * s;
        }
        _ui.End();
        _edPressL = false;
    }

    /// <summary>Boucle de saisie de la vue (caméra comme dans le jeu, choix et déplacement des objets, relief).</summary>
    private void EdViewInput(MouseState real, float dt)
    {
        if (_edPopup != null || _edFocusId != null)
            return;
        bool inView = EdInView(_edMx, _edMy);
        MouseState m = EdViewMouse(real);
        EdEnterView();
        try
        {
            // Pg.Préc / Pg.Suiv seules (sans autre touche ni bouton de la souris) : la caméra monte ou descend
            bool buttons = m.LeftButton == ButtonState.Pressed || m.RightButton == ButtonState.Pressed || m.MiddleButton == ButtonState.Pressed;
            if (!_map && _edDrag == null && !_edMoving && !buttons && !EdCtrl && !EdShift && !EdAlt)
            {
                float up = (_edKeys.IsKeyDown(Keys.PageUp) ? 1 : 0) - (_edKeys.IsKeyDown(Keys.PageDown) ? 1 : 0);
                if (up != 0)
                    _target.Z = Math.Clamp(_target.Z + up * dt * (300 + _dist * 0.3f), -5000, 50000);
            }
            // flèches : la caméra ; pendant qu'on pose ou déplace un objet, elles le font tourner ; un objet choisi : il avance
            if (_edDrag != null || _edMoving)
                EdRotateKeys(dt);
            else if (_edSel != null)
                EdArrowMove();
            else
            {
                bool edge = _settings.EdgeScroll;
                _settings.EdgeScroll = false;   // pas de défilement par les bords : les listes sont juste à côté
                HandleArrows(_edKeys, dt);
                _settings.EdgeScroll = edge;
            }
            if (inView && _edWheel != 0)
            {
                float dy = _edWheel / 120f;
                if (_map)
                {
                    SN.Vector2 before = ScreenToMap(_edMx, _edMy);
                    _mapScale *= MathF.Exp(dy * 0.15f);
                    SN.Vector2 after = ScreenToMap(_edMx, _edMy);
                    _mapCenter += before - after;
                }
                else
                    _dist = Math.Clamp(_dist * MathF.Exp(-dy * 0.12f), 300, 60000);
                _edWheel = 0;
            }
            // relief : A (monter) ou Q (descendre) + clic sur une case
            if (EdHeightInput(m, inView))
                return;
            // mini-carte : un clic y déplace la caméra
            if (inView && _edPressL && MiniToWorld(m.X, m.Y) is { } mw)
            {
                _edPressL = false;
                MiniLook(mw);
                _dragMini = true;
            }
            if (_dragMini)
            {
                if (m.LeftButton == ButtonState.Released)
                    _dragMini = false;
                else if (MiniToWorld(m.X, m.Y) is { } mw2)
                    MiniLook(mw2);
            }
            // bouton du milieu : tourner ; bouton droit : faire glisser la vue (comme le jeu)
            if (inView && m.MiddleButton == ButtonState.Pressed && _edMousePrev.MiddleButton == ButtonState.Released)
            {
                _dragM = true;
                _mx = m.X;
                _my = m.Y;
            }
            if (inView && _edPressR)
            {
                _edPressR = false;
                _dragR = true;
                _mx = m.X;
                _my = m.Y;
            }
            if (_dragM || _dragR)
            {
                float dx = (float)(m.X - _mx), dyy = (float)(m.Y - _my);
                _mx = m.X;
                _my = m.Y;
                Drag(dx / _scale, dyy / _scale);
            }
            if (_dragM && m.MiddleButton == ButtonState.Released)
                _dragM = false;
            if (_dragR && m.RightButton == ButtonState.Released)
                _dragR = false;
            EdObjectMouse(m, inView);
        }
        finally
        {
            EdLeaveView();
        }
    }
}
