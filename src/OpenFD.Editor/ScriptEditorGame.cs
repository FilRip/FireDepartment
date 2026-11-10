using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SN = System.Numerics;

namespace OpenFD.MG;

/// <summary>
/// La fenêtre des scripts de l'éditeur de missions (un second programme : une fenêtre MonoGame ne peut pas en ouvrir une
/// autre). Un onglet par fichier .scp du dossier de travail de la mission, en haut sur toute la largeur, avec sa croix
/// rouge pour le supprimer ; « Nouveau » ajoute un fichier ; « Compiler » vérifie les scripts ; à droite, la liste des
/// commandes que le jeu connaît (un clic l'écrit au curseur). Les fichiers sont enregistrés tout seuls peu après chaque
/// changement : l'éditeur les reprend quand on sauve ou teste la mission.
/// </summary>
public sealed class ScriptEditorGame : Microsoft.Xna.Framework.Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _dataDir, _dir;
    private string _mission;
    private Ui _ui = null!;
    private Font _font = null!, _mono = null!;
    private float _scale = 1;
    private readonly List<ScriptDoc> _docs = new();
    private int _cur;
    private double _clock;
    private KeyboardState _k, _kPrev;
    private MouseState _m, _mPrev;
    private float _mx, _my;
    private bool _press, _selecting;
    private int _wheel;
    private string _status = "";
    private bool _statusError;
    private List<ScriptCheck.Problem> _problems = new();
    private readonly ScrollBar _listBar = new(), _problemBar = new(), _codeBar = new();
    private string? _goto;              // « fonction|paramètres » à montrer dès que les scripts sont chargés
    private string? _newName;           // fenêtre « Nouveau fichier » ouverte : le nom tapé
    private int _confirmDelete = -1;    // onglet dont on demande la suppression
    private string _lastControl = "";
    private double _controlCheck, _ownerCheck;
    private int _owner;
    private readonly Dictionary<Keys, double> _repeat = new();

    private static readonly SN.Vector4 Back = new(0.12f, 0.12f, 0.14f, 1);
    private static readonly SN.Vector4 Panel = new(0.17f, 0.17f, 0.2f, 1);
    private static readonly SN.Vector4 LineCol = new(0.42f, 0.42f, 0.48f, 1);
    private static readonly SN.Vector4 TextCol = new(0.92f, 0.91f, 0.88f, 1);
    private static readonly SN.Vector4 Muted = new(0.6f, 0.6f, 0.64f, 1);
    private static readonly SN.Vector4 Accent = new(1f, 0.48f, 0.18f, 1);
    private static readonly SN.Vector4 Hover = new(0.28f, 0.28f, 0.33f, 1);
    private static readonly SN.Vector4 Red = new(0.95f, 0.25f, 0.2f, 1);
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase) { "if", "else", "while", "for", "return", "void", "int", "float", "string", "s32", "f32", "bool" };
    private static readonly HashSet<string> Known = ScriptCommands.All.Select(c => c[..c.IndexOf('(')]).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <param name="language">Langue des textes (« FRA », « ENG »…).</param>
    /// <param name="gotoOrder">« fonction|paramètres » : la fonction montrée à l'ouverture (créée si elle manque).</param>
    public ScriptEditorGame(string dataDir, string dir, string mission, string? language = null, string? gotoOrder = null)
    {
        _dataDir = dataDir;
        _dir = dir;
        _mission = mission;
        if (!string.IsNullOrEmpty(language))
            EditorText.Language = language;
        _goto = gotoOrder;
        _gdm = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 1200, PreferredBackBufferHeight = 780, SynchronizeWithVerticalRetrace = true };
        IsMouseVisible = true;
        IsFixedTimeStep = false;
        Window.AllowUserResizing = true;
        Window.Title = Tr("ScriptTitle", "Open Fire Department - scripts de {0}", mission);
    }

    protected override void Initialize()
    {
        base.Initialize();
        _scale = Display.Scale(Window.Handle);
        DisplayMode mode = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
        _gdm.PreferredBackBufferWidth = (int)Math.Min(1200 * _scale, mode.Width * 0.9f);
        _gdm.PreferredBackBufferHeight = (int)Math.Min(780 * _scale, mode.Height * 0.85f);
        _gdm.ApplyChanges();
        Window.ClientSizeChanged += (_, _) =>
        {
            Rectangle b = Window.ClientBounds;
            if (b.Width > 0 && b.Height > 0 && (b.Width != _gdm.PreferredBackBufferWidth || b.Height != _gdm.PreferredBackBufferHeight))
            {
                _gdm.PreferredBackBufferWidth = b.Width;
                _gdm.PreferredBackBufferHeight = b.Height;
                _gdm.ApplyChanges();
            }
        };
        // Entrée : la touche Entrée et celle du pavé numérique (qui ne donne pas de caractère) : par l'appui de la touche
        Window.TextInput += (_, e) =>
        {
            if (e.Character is not ('\r' or '\n'))
                OnChar(e.Character);
        };
        Window.KeyDown += (_, e) =>
        {
            if (e.Key == Keys.Enter)
                OnChar('\r');
        };
        Exiting += (_, _) => SaveAll();
    }

    protected override void LoadContent()
    {
        Tex.Init(GraphicsDevice);
        Ui.InitHard(GraphicsDevice);
        string fonts = Path.Combine(_dataDir, "Fonte");
        _font = Font.LoadFnt(GraphicsDevice, Path.Combine(fonts, "font18.fnt")) ?? throw new FileNotFoundException("Data\\Fonte\\font18.fnt");
        _mono = Font.LoadFnt(GraphicsDevice, Path.Combine(fonts, "courier18.fnt")) ?? _font;
        _ui = new Ui(GraphicsDevice) { Scale = _scale, Font = _font };
        LoadDocs();
        if (_goto != null)
            GotoFunction(_goto);
        _goto = null;
    }

    private static string Tr(string key, string french) => EditorText.T(key, french);

    private static string Tr(string key, string french, params object?[] args) => EditorText.T(key, french, args);

    private void LoadDocs()
    {
        _docs.Clear();
        Directory.CreateDirectory(_dir);
        foreach (string f in Directory.EnumerateFiles(_dir, "*.scp").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            _docs.Add(ScriptDoc.Load(f));
        _cur = Math.Clamp(_cur, 0, Math.Max(0, _docs.Count - 1));
        _problems.Clear();
        Status(_docs.Count == 0 ? Tr("ScriptNone", "Aucun script : cliquez sur « Nouveau ».") : Tr("ScriptCount", "{0} script(s) de {1}", _docs.Count, _mission));
    }

    private void SaveAll()
    {
        foreach (ScriptDoc d in _docs.Where(d => d.Dirty))
        {
            try
            {
                d.Save();
            }
            catch (Exception e)
            {
                Status($"{d.Name} : {e.Message}", true);
            }
        }
    }

    private void Status(string s, bool error = false)
    {
        _status = s;
        _statusError = error;
    }

    private ScriptDoc? Doc => _cur >= 0 && _cur < _docs.Count ? _docs[_cur] : null;

    // ---- mise en page ----
    private int W => GraphicsDevice.PresentationParameters.BackBufferWidth;
    private int H => GraphicsDevice.PresentationParameters.BackBufferHeight;
    private float BarH => 36 * _scale;
    private float TabH => _ui.LineHeight + 12 * _scale;
    private float ListW => MathF.Min(380 * _scale, W * 0.34f);
    private float ProblemsH => (_ui.LineHeight + 4 * _scale) * 5 + 10 * _scale;
    private float CodeScale => _scale;
    private float CharW => (_mono.Glyphs.TryGetValue('M', out Font.Glyph g) ? g.Adv : _mono.LineHeight * 0.6f) * CodeScale;
    private float CodeLineH => (_mono.LineHeight + 3) * CodeScale;
    private float GutterW => CharW * 5 + 10 * _scale;
    private (float x, float y, float w, float h) CodeRect => (0, BarH + TabH, W - ListW, H - BarH - TabH - ProblemsH);
    private (float x, float y, float w, float h) ListRect => (W - ListW, BarH + TabH, ListW, H - BarH - TabH - ProblemsH);
    private (float x, float y, float w, float h) ProblemsRect => (0, H - ProblemsH, W, ProblemsH);

    private (float sx, float sy) Fb(int x, int y) => (x * (float)W / Math.Max(1, Window.ClientBounds.Width), y * (float)H / Math.Max(1, Window.ClientBounds.Height));
    private bool Hit(float x, float y, float w, float h) => _mx >= x && _mx < x + w && _my >= y && _my < y + h;
    private bool Key(Keys k) => _k.IsKeyDown(k) && !_kPrev.IsKeyDown(k);

    /// <summary>Touche tenue : répétée après 0,4 s, toutes les 35 ms.</summary>
    private bool KeyRepeat(Keys k)
    {
        if (!_k.IsKeyDown(k))
        {
            _repeat.Remove(k);
            return false;
        }
        if (!_kPrev.IsKeyDown(k))
        {
            _repeat[k] = _clock + 0.4;
            return true;
        }
        if (_repeat.TryGetValue(k, out double at) && _clock >= at)
        {
            _repeat[k] = _clock + 0.035;
            return true;
        }
        return false;
    }

    private bool Ctrl => _k.IsKeyDown(Keys.LeftControl) || _k.IsKeyDown(Keys.RightControl);
    private bool Shift => _k.IsKeyDown(Keys.LeftShift) || _k.IsKeyDown(Keys.RightShift);

    protected override void Update(GameTime gameTime)
    {
        float dt = (float)Math.Min(0.1, gameTime.ElapsedGameTime.TotalSeconds);
        _clock += dt;
        _k = Keyboard.GetState();
        _m = Mouse.GetState();
        (_mx, _my) = Fb(_m.X, _m.Y);
        if (IsActive && _m.LeftButton == ButtonState.Pressed && _mPrev.LeftButton == ButtonState.Released)
            _press = true;
        if (IsActive)
            _wheel += _m.ScrollWheelValue - _mPrev.ScrollWheelValue;
        if (IsActive)
            EditKeys();
        // enregistrement automatique, peu après le dernier changement
        foreach (ScriptDoc d in _docs.Where(d => d.Dirty && _clock - d.EditedAt > 0.4))
        {
            try
            {
                d.Save();
            }
            catch (Exception e)
            {
                Status($"{d.Name} : {e.Message}", true);
            }
        }
        if (_clock >= _controlCheck)
        {
            _controlCheck = _clock + 0.5;
            ReadControl();
        }
        // l'éditeur fermé : cette fenêtre se ferme aussi
        if (_owner != 0 && _clock >= _ownerCheck)
        {
            _ownerCheck = _clock + 2;
            try
            {
                if (System.Diagnostics.Process.GetProcessById(_owner).HasExited)
                    Exit();
            }
            catch (ArgumentException)
            {
                Exit();
            }
        }
        _kPrev = _k;
        _mPrev = _m;
        base.Update(gameTime);
    }

    /// <summary>
    /// Ordres de l'éditeur (fichier « .controle ») : « front » (devant), « open » (autre mission), « save »,
    /// « goto fonction|paramètres » (la fonction montrée, créée si elle manque).
    /// </summary>
    private void ReadControl()
    {
        string path = Path.Combine(_dir, ".controle");
        string text;
        try
        {
            if (!File.Exists(path))
                return;
            text = File.ReadAllText(path).Trim();
        }
        catch (IOException)
        {
            return;
        }
        if (text == _lastControl)
            return;
        bool first = _lastControl.Length == 0;
        _lastControl = text;
        string[] parts = text.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return;
        if (int.TryParse(parts[1], out int pid))
            _owner = pid;
        if (first && parts[2] != "front")
            return;   // ordre d'avant l'ouverture de cette fenêtre
        switch (parts[2])
        {
            case "front":
                Sdl.Raise(Window.Handle);
                break;
            case "goto":
                if (parts.Length > 3)
                    GotoFunction(parts[3]);
                Sdl.Raise(Window.Handle);
                break;
            case "save":
                SaveAll();
                break;
            case "open":
                _mission = parts.Length > 3 ? parts[3] : _mission;
                Window.Title = Tr("ScriptTitle", "Open Fire Department - scripts de {0}", _mission);
                _cur = 0;
                LoadDocs();
                Sdl.Raise(Window.Handle);
                break;
        }
    }

    // ---- clavier ----
    private void OnChar(char ch)
    {
        if (!IsActive)
            return;
        if (_newName != null)
        {
            if (ch == '\b')
                _newName = _newName.Length > 0 ? _newName[..^1] : "";
            else if (ch is '\r' or '\n')
                CreateDoc();
            else if (ch >= ' ' && ch != 127 && (char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.'))
                _newName += ch;
            return;
        }
        if (Doc is not { } d || Ctrl)
            return;
        switch (ch)
        {
            case '\b':
                d.Backspace(_clock);
                break;
            case '\r':
            case '\n':
                d.NewLine(_clock);
                break;
            case '\t':
                d.Insert("\t", _clock);
                break;
            default:
                if (ch >= ' ' && ch != 127)
                    d.Insert(ch.ToString(), _clock);
                break;
        }
        ShowCaret(d);
    }

    private void EditKeys()
    {
        if (_newName != null)
        {
            if (Key(Keys.Escape))
                _newName = null;
            return;
        }
        if (_confirmDelete >= 0 && Key(Keys.Escape))
            _confirmDelete = -1;
        if (Ctrl && Key(Keys.S))
        {
            SaveAll();
            Status(Tr("ScriptSaved", "Scripts enregistrés"));
        }
        if (Key(Keys.F7) || (Ctrl && Key(Keys.B)))
            Compile();
        if (Ctrl && Key(Keys.N))
            _newName = "";
        if (Ctrl && Key(Keys.Tab) && _docs.Count > 0)
            _cur = (_cur + (Shift ? _docs.Count - 1 : 1)) % _docs.Count;
        if (Doc is not { } d)
            return;
        bool sel = Shift, word = Ctrl;
        int page = Math.Max(1, (int)(CodeRect.h / CodeLineH) - 1);
        bool moved = true;
        if (KeyRepeat(Keys.Left))
            d.Left(sel, word);
        else if (KeyRepeat(Keys.Right))
            d.Right(sel, word);
        else if (KeyRepeat(Keys.Up))
            d.MoveTo(d.Line - 1, d.Col, sel);
        else if (KeyRepeat(Keys.Down))
            d.MoveTo(d.Line + 1, d.Col, sel);
        else if (KeyRepeat(Keys.PageUp))
            d.MoveTo(d.Line - page, d.Col, sel);
        else if (KeyRepeat(Keys.PageDown))
            d.MoveTo(d.Line + page, d.Col, sel);
        else if (Key(Keys.Home))
        {
            if (Ctrl)
                d.MoveTo(0, 0, sel);
            else
                d.Home(sel);
        }
        else if (Key(Keys.End))
        {
            if (Ctrl)
                d.MoveTo(d.Lines.Count - 1, int.MaxValue, sel);
            else
                d.MoveTo(d.Line, int.MaxValue, sel);
        }
        else if (KeyRepeat(Keys.Delete))
            d.Delete(_clock);
        else
            moved = false;
        if (Ctrl && Key(Keys.A))
            d.SelectAll();
        if (Ctrl && Key(Keys.C) && d.HasSelection)
            Sdl.SetClipboard(d.SelectedText);
        if (Ctrl && Key(Keys.X) && d.HasSelection)
        {
            Sdl.SetClipboard(d.SelectedText);
            d.DeleteSelection(_clock);
            moved = true;
        }
        if (Ctrl && KeyRepeat(Keys.V))
        {
            string clip = Sdl.GetClipboard();
            if (clip.Length > 0)
                d.Insert(clip, _clock);
            moved = true;
        }
        if (Ctrl && KeyRepeat(Keys.Z))
        {
            d.Undo(_clock);
            moved = true;
        }
        if (moved)
            ShowCaret(d);
    }

    /// <summary>La vue suit le curseur.</summary>
    private void ShowCaret(ScriptDoc d)
    {
        int rows = Math.Max(1, (int)(CodeRect.h / CodeLineH) - 1);
        if (d.Line < d.Scroll)
            d.Scroll = d.Line;
        if (d.Line > d.Scroll + rows - 1)
            d.Scroll = d.Line - rows + 1;
        int cols = Math.Max(4, (int)((CodeRect.w - GutterW - 20 * _scale) / CharW));
        int vc = VisualCol(d.Lines[d.Line], d.Col);
        if (vc < d.ScrollX)
            d.ScrollX = Math.Max(0, vc - 4);
        if (vc > d.ScrollX + cols)
            d.ScrollX = vc - cols + 4;
    }

    private const int TabSize = 4;

    private static int VisualCol(string line, int col)
    {
        int v = 0;
        for (int i = 0; i < Math.Min(col, line.Length); i++)
            v = line[i] == '\t' ? (v / TabSize + 1) * TabSize : v + 1;
        return v;
    }

    private static int ColAtVisual(string line, int visual)
    {
        int v = 0;
        for (int i = 0; i < line.Length; i++)
        {
            int next = line[i] == '\t' ? (v / TabSize + 1) * TabSize : v + 1;
            if (visual < (v + next) / 2f + 0.5f)
                return i;
            v = next;
        }
        return line.Length;
    }

    // ---- actions ----
    private void CreateDoc()
    {
        string name = (_newName ?? "").Trim();
        _newName = null;
        if (name.Length == 0)
            return;
        if (!name.EndsWith(".scp", StringComparison.OrdinalIgnoreCase))
            name += ".scp";
        string path = Path.Combine(_dir, name);
        if (_docs.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Status(Tr("ScriptExists", "{0} existe déjà", name), true);
            return;
        }
        string fn = Path.GetFileNameWithoutExtension(name);
        File.WriteAllText(path, $"// {name} : {_mission}\r\n\r\nvoid {fn}_Start()\r\n{{\r\n}}\r\n", System.Text.Encoding.Latin1);
        _docs.Add(ScriptDoc.Load(path));
        _docs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _cur = _docs.FindIndex(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Status(Tr("ScriptCreated", "{0} créé", name));
    }

    private void DeleteDoc(int i)
    {
        _confirmDelete = -1;
        if (i < 0 || i >= _docs.Count)
            return;
        ScriptDoc d = _docs[i];
        try
        {
            File.Delete(d.Path);
        }
        catch (Exception e)
        {
            Status($"{d.Name} : {e.Message}", true);
            return;
        }
        _docs.RemoveAt(i);
        _cur = Math.Clamp(_cur, 0, Math.Max(0, _docs.Count - 1));
        Status(Tr("ScriptDeleted", "{0} supprimé (il disparaîtra de la mission à la prochaine sauvegarde)", d.Name));
    }

    private void Compile()
    {
        SaveAll();
        Dictionary<string, string> texts = _docs.ToDictionary(d => d.Name, d => d.Text, StringComparer.OrdinalIgnoreCase);
        _problems = ScriptCheck.Check(texts, Path.Combine(_dataDir, "Script", "_Common"));
        _problemBar.Value = 0;
        int errors = _problems.Count(p => p.Error), warns = _problems.Count - errors;
        if (errors > 0)
            Status(warns > 0 ? Tr("CompileErrorsWarn", "Compilation : {0} erreur(s), {1} avertissement(s)", errors, warns) : Tr("CompileErrors", "Compilation : {0} erreur(s)", errors), true);
        else
            Status(warns > 0 ? Tr("CompileWarn", "Compilation réussie, {0} avertissement(s)", warns) : Tr("CompileOk", "Compilation réussie : aucune erreur"));
        if (_problems.FirstOrDefault(p => p.Error) is { } first)
            GoTo(first);
    }

    private void GoTo(ScriptCheck.Problem p)
    {
        int i = _docs.FindIndex(d => d.Name.Equals(p.File, StringComparison.OrdinalIgnoreCase));
        if (i < 0)
            return;
        _cur = i;
        ScriptDoc d = _docs[i];
        d.MoveTo(Math.Max(0, p.Line - 1), 0, false);
        d.Home(false);
        ShowCaret(d);
    }

    /// <summary>
    /// « fonction|paramètres » : l'onglet et la ligne de la fonction (« void fonction( »…) ; si elle n'est écrite nulle part,
    /// elle est ajoutée, vide, à la fin du script principal de la mission (créé s'il n'y en a pas).
    /// </summary>
    private void GotoFunction(string order)
    {
        int bar = order.IndexOf('|');
        string fn = (bar >= 0 ? order[..bar] : order).Trim(), sig = bar >= 0 ? order[(bar + 1)..].Trim() : "";
        if (fn.Length == 0)
            return;
        System.Text.RegularExpressions.Regex re = new System.Text.RegularExpressions.Regex(@"^\s*\w+\s+" + System.Text.RegularExpressions.Regex.Escape(fn) + @"\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        for (int i = 0; i < _docs.Count; i++)
            for (int l = 0; l < _docs[i].Lines.Count; l++)
                if (re.IsMatch(_docs[i].Lines[l]))
                {
                    ShowLine(i, l);
                    Status(Tr("GotoFound", "Fonction {0} : {1}, ligne {2}", fn, _docs[i].Name, l + 1));
                    return;
                }
        int di = _docs.FindIndex(d => Path.GetFileNameWithoutExtension(d.Name).Equals(_mission, StringComparison.OrdinalIgnoreCase));
        if (di < 0 && _docs.Count > 0)
            di = 0;
        if (di < 0)
        {
            string path = Path.Combine(_dir, _mission.Length > 0 ? _mission + ".scp" : "mission.scp");
            File.WriteAllText(path, $"// {_mission}\r\n", System.Text.Encoding.Latin1);
            _docs.Add(ScriptDoc.Load(path));
            di = _docs.Count - 1;
        }
        ScriptDoc doc = _docs[di];
        doc.MoveTo(doc.Lines.Count - 1, int.MaxValue, false);
        bool blank = doc.Lines[^1].Trim().Length == 0;
        doc.Insert((blank ? "" : "\r\n") + "\r\n" + $"void {fn}({sig})\r\n{{\r\n\t\r\n}}\r\n", _clock);
        int at = doc.Lines.FindLastIndex(x => re.IsMatch(x));
        ShowLine(di, Math.Max(0, at));
        if (at >= 0)
            doc.MoveTo(at + 2, int.MaxValue, false);
        Status(Tr("GotoCreated", "Fonction {0} ajoutée à la fin de {1}", fn, doc.Name));
    }

    /// <summary>Onglet choisi, ligne en haut de la vue (avec deux lignes avant), curseur au début.</summary>
    private void ShowLine(int doc, int line)
    {
        _cur = doc;
        ScriptDoc d = _docs[doc];
        d.MoveTo(line, 0, false);
        d.Scroll = Math.Max(0, line - 2);
        ShowCaret(d);
    }

    // ---- dessin (et clics) ----
    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice gd = GraphicsDevice;
        gd.Clear(new Color(Back.X, Back.Y, Back.Z));
        if (_newName != null || _confirmDelete >= 0)
            DrawCode(false);
        else
            DrawCode(true);
        DrawList();
        DrawProblems();
        DrawTabs();
        DrawBar();
        DrawDialogs();
        if (_m.LeftButton == ButtonState.Released)
            _selecting = false;
        _press = false;
        _wheel = 0;
        base.Draw(gameTime);
        // essai : FD3_SCRIPT_CAPTURE=image.png (après une compilation si FD3_SCRIPT_COMPILER est là)
        if (Environment.GetEnvironmentVariable("FD3_SCRIPT_CAPTURE") is { Length: > 0 } cap && ++_frames == 20)
        {
            if (Environment.GetEnvironmentVariable("FD3_SCRIPT_COMPILER") != null)
                Compile();
        }
        else if (Environment.GetEnvironmentVariable("FD3_SCRIPT_CAPTURE") is { Length: > 0 } cap2 && _frames == 24)
        {
            Color[] px = new Color[W * H];
            GraphicsDevice.GetBackBufferData(px);
            using (Texture2D t = new Texture2D(GraphicsDevice, W, H))
            {
                t.SetData(px);
                using FileStream fs = File.Create(cap2);
                t.SaveAsPng(fs, W, H);
            }
            Exit();
        }
    }

    private int _frames;

    private bool Button(float x, float y, float w, float h, string label, bool modal = false)
    {
        bool hover = (modal || (_newName == null && _confirmDelete < 0)) && Hit(x, y, w, h);
        _ui.Rect(x, y, w, h, hover ? Hover : new SN.Vector4(0.22f, 0.22f, 0.26f, 1));
        _ui.Frame(x, y, w, h, LineCol, MathF.Max(1, _scale));
        _ui.Text(x + (w - _ui.Measure(label)) / 2, y + (h - _ui.LineHeight) / 2, label, TextCol);
        if (hover && _press)
        {
            _press = false;
            return true;
        }
        return false;
    }

    private string Fit(string s, float w)
    {
        if (_ui.Measure(s) <= w)
            return s;
        while (s.Length > 1 && _ui.Measure(s + "...") > w)
            s = s[..^1];
        return s + "...";
    }

    private void DrawBar()
    {
        float s = _scale, pad = 5 * s, bw = 120 * s, h = BarH;
        _ui.Begin();
        _ui.Rect(0, 0, W, h, new SN.Vector4(0.1f, 0.1f, 0.12f, 1));
        if (Button(pad, pad, bw, h - 2 * pad, Tr("New", "Nouveau")))
            _newName = "";
        if (Button(W - bw - pad, pad, bw, h - 2 * pad, Tr("Compile", "Compiler")))
            Compile();
        float x = pad + bw + 3 * pad;
        _ui.Text(x, (h - _ui.LineHeight) / 2, Fit(_status, W - x - bw - 4 * pad), _statusError ? Red : TextCol);
        _ui.End();
    }

    private void DrawTabs()
    {
        float s = _scale, y = BarH, h = TabH, x = 0;
        _ui.Begin();
        _ui.Rect(0, y, W, h, Panel);
        if (_docs.Count == 0)
            _ui.Text(10 * s, y + (h - _ui.LineHeight) / 2, Tr("ScriptNoFile", "(aucun fichier)"), Muted);
        float cross = h - 10 * s;
        // tous les onglets sur la largeur : ils rétrécissent s'il y en a beaucoup
        float natural = _docs.Sum(d => _ui.Measure(d.Name + (d.Dirty ? " *" : "")) + cross + 26 * s);
        float k = natural > W ? W / natural : 1;
        for (int i = 0; i < _docs.Count; i++)
        {
            ScriptDoc d = _docs[i];
            string label = d.Name + (d.Dirty ? " *" : "");
            float tw = (_ui.Measure(label) + cross + 26 * s) * k;
            bool on = i == _cur;
            bool modal = _newName != null || _confirmDelete >= 0;
            bool hover = !modal && Hit(x, y, tw, h);
            _ui.Rect(x + 1, y + 2 * s, tw - 2, h - 2 * s, on ? new SN.Vector4(0.85f, 0.35f, 0.1f, 0.95f) : hover ? Hover : new SN.Vector4(0.13f, 0.13f, 0.16f, 1));
            _ui.Text(x + 8 * s, y + (h - _ui.LineHeight) / 2, Fit(label, tw - cross - 18 * s), on ? TextCol : Muted);
            // croix rouge, alignée à droite de l'onglet : supprimer le script
            float cx = x + tw - cross - 5 * s, cy = y + 5 * s;
            bool overCross = !modal && Hit(cx, cy, cross, cross);
            _ui.Rect(cx, cy, cross, cross, overCross ? new SN.Vector4(0.5f, 0.1f, 0.08f, 1) : new SN.Vector4(0, 0, 0, 0.25f));
            float m = cross * 0.25f;
            _ui.Line(cx + m, cy + m, cx + cross - m, cy + cross - m, Red, 2.5f * s);
            _ui.Line(cx + cross - m, cy + m, cx + m, cy + cross - m, Red, 2.5f * s);
            if (overCross && _press)
            {
                _press = false;
                _confirmDelete = i;
            }
            else if (hover && _press)
            {
                _press = false;
                _cur = i;
            }
            x += tw;
        }
        _ui.Rect(0, y + h - MathF.Max(1, s), W, MathF.Max(1, s), LineCol);
        _ui.End();
    }

    private void DrawCode(bool interactive)
    {
        (float x, float y, float w, float h) = CodeRect;
        float s = _scale, gutter = GutterW, lh = CodeLineH, cw = CharW, sbW = 14 * s;
        _ui.Begin();
        _ui.Rect(x, y, w, h, new SN.Vector4(0.08f, 0.08f, 0.1f, 1));
        _ui.Rect(x, y, gutter, h, new SN.Vector4(0.11f, 0.11f, 0.13f, 1));
        _ui.End();
        if (Doc is not { } d)
            return;
        int rows = (int)(h / lh) + 1;
        float maxScroll = MathF.Max(0, d.Lines.Count - rows + 2);
        if (interactive && Hit(x, y, w, h) && _wheel != 0)
        {
            d.Scroll = Math.Clamp(d.Scroll - _wheel / 120f * 3, 0, maxScroll);
            _wheel = 0;
        }
        d.Scroll = Math.Clamp(d.Scroll, 0, maxScroll);
        // barre de défilement du texte (flèches : une ligne), en lignes × hauteur de ligne
        float sx = x + w - sbW;
        _codeBar.Value = d.Scroll * lh;
        bool press = interactive && _press;
        _ui.Begin();
        _codeBar.Run(_ui, sx, y, sbW, h, (maxScroll + rows) * lh, rows * lh, lh, _mx, _my, ref press, _m.LeftButton == ButtonState.Pressed, _clock);
        _ui.End();
        if (interactive)
            _press = press;
        d.Scroll = Math.Clamp(_codeBar.Value / lh, 0, maxScroll);
        // clic dans le texte : le curseur ; glisser : sélection
        int first = (int)d.Scroll;
        float yOff = (d.Scroll - first) * lh;
        if (interactive && _press && Hit(x, y, w - sbW, h))
        {
            _press = false;
            (int l, int c) = CharAt(d, first, yOff);
            d.MoveTo(l, c, Shift);
            _selecting = true;
        }
        else if (_selecting && _m.LeftButton == ButtonState.Pressed)
        {
            (int l, int c) = CharAt(d, first, yOff);
            if (d.AnchorLine < 0)
            {
                d.AnchorLine = d.Line;
                d.AnchorCol = d.Col;
            }
            d.Line = l;
            d.Col = c;
        }
        Rectangle clip = new Rectangle((int)x, (int)y, (int)(w - sbW), (int)h);
        _ui.BeginClipped(clip);
        _ui.Font = _mono;
        bool hasSel = d.HasSelection;
        ((int l0, int c0), (int l1, int c1)) = d.Selection;
        for (int r = 0; r <= rows; r++)
        {
            int li = first + r;
            if (li >= d.Lines.Count)
                break;
            float ly = y + r * lh - yOff + 2 * s;
            string line = d.Lines[li];
            if (li == d.Line)
                _ui.Rect(x + gutter, ly - 1 * s, w - gutter, lh, new SN.Vector4(1, 1, 1, 0.04f));
            string num = (li + 1).ToString(CultureInfo.InvariantCulture);
            _ui.Text(x + gutter - 6 * s - num.Length * cw, ly, num, Muted, CodeScale);
            float tx0 = x + gutter + 6 * s - d.ScrollX * cw;
            if (hasSel && li >= l0 && li <= l1)
            {
                int a = li == l0 ? VisualCol(line, c0) : 0, b = li == l1 ? VisualCol(line, c1) : VisualCol(line, line.Length) + 1;
                _ui.Rect(tx0 + a * cw, ly - 1 * s, MathF.Max(cw * 0.5f, (b - a) * cw), lh, new SN.Vector4(0.25f, 0.45f, 0.85f, 0.45f));
            }
            DrawCodeLine(line, tx0, ly, cw);
            if (li == d.Line && (int)(_clock * 2) % 2 == 0)
                _ui.Rect(tx0 + VisualCol(line, d.Col) * cw, ly - 1 * s, MathF.Max(1.5f, 2 * s), lh, Accent);
        }
        _ui.Font = _font;
        _ui.End();
        _ui.Begin();
        string pos = Tr("ScriptPos", "ligne {0}, colonne {1}", d.Line + 1, d.Col + 1);
        _ui.Text(x + w - sbW - _ui.Measure(pos) * 0.8f - 8 * s, y + h - _ui.LineHeight * 0.8f - 4 * s, pos, Muted, _scale * 0.8f);
        _ui.End();
    }

    private (int line, int col) CharAt(ScriptDoc d, int first, float yOff)
    {
        (float x, float y, _, _) = CodeRect;
        int l = Math.Clamp(first + (int)((_my - y + yOff - 2 * _scale) / CodeLineH), 0, d.Lines.Count - 1);
        float vx = (_mx - (x + GutterW + 6 * _scale)) / CharW + d.ScrollX;
        return (l, ColAtVisual(d.Lines[l], (int)MathF.Floor(MathF.Max(0, vx))));
    }

    /// <summary>Une ligne de code en couleurs : commentaires, textes, nombres, mots du langage, commandes du jeu.</summary>
    private void DrawCodeLine(string line, float x0, float y, float cw)
    {
        int i = 0, v = 0;
        while (i < line.Length)
        {
            char c = line[i];
            int start = i;
            SN.Vector4 col = TextCol;
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                i = line.Length;
                col = new SN.Vector4(0.45f, 0.75f, 0.45f, 1);
            }
            else if (c == '"')
            {
                i++;
                while (i < line.Length && line[i] != '"')
                    i++;
                i = Math.Min(line.Length, i + 1);
                col = new SN.Vector4(0.95f, 0.65f, 0.35f, 1);
            }
            else if (char.IsDigit(c))
            {
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '.'))
                    i++;
                col = new SN.Vector4(0.65f, 0.85f, 1f, 1);
            }
            else if (char.IsLetter(c) || c == '_')
            {
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                    i++;
                string w = line[start..i];
                if (Keywords.Contains(w))
                    col = new SN.Vector4(0.55f, 0.65f, 1f, 1);
                else if (Known.Contains(w))
                    col = new SN.Vector4(0.95f, 0.85f, 0.45f, 1);
            }
            else
                i++;
            string part = line[start..i];
            // tabulations : jusqu'à la colonne suivante multiple de 4
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            int vs = v;
            foreach (char pc in part)
            {
                if (pc == '\t')
                {
                    int next = (v / TabSize + 1) * TabSize;
                    sb.Append(' ', next - v);
                    v = next;
                }
                else
                {
                    sb.Append(pc);
                    v++;
                }
            }
            string shown = sb.ToString();
            if (shown.Trim().Length > 0)
            {
                // police à chasse fixe : chaque caractère dans sa case
                for (int k = 0; k < shown.Length; k++)
                    if (shown[k] != ' ')
                        _ui.Text(x0 + (vs + k) * cw, y, shown[k].ToString(), col, CodeScale);
            }
        }
    }

    private void DrawList()
    {
        (float x, float y, float w, float h) = ListRect;
        float s = _scale, pad = 6 * s, sbW = 14 * s, rowH = _ui.LineHeight + 4 * s;
        _ui.Begin();
        _ui.Rect(x, y, w, h, Panel);
        _ui.Frame(x, y, w, h, LineCol, MathF.Max(1, s));
        _ui.Text(x + pad, y + pad, Tr("CommandsTitle", "Commandes du jeu ({0})", ScriptCommands.All.Length), Accent);
        _ui.Text(x + pad, y + pad + rowH, Tr("CommandsHelp", "un clic l'écrit au curseur"), Muted, _scale * 0.85f);
        float top = y + pad + 2 * rowH + 4 * s, area = h - (top - y) - pad;
        float total = ScriptCommands.All.Length * rowH;
        bool modal = _newName != null || _confirmDelete >= 0;
        if (!modal && Hit(x, top, w, area) && _wheel != 0)
        {
            _listBar.Value -= _wheel / 120f * rowH * 3;
            _wheel = 0;
        }
        float sx = x + w - sbW - 2 * s;
        bool press = !modal && _press;
        _listBar.Run(_ui, sx, top, sbW, area, total, area, rowH, _mx, _my, ref press, _m.LeftButton == ButtonState.Pressed, _clock);
        if (!modal)
            _press = press;
        _ui.End();
        float listScroll = _listBar.Value;
        _ui.BeginClipped(new Rectangle((int)x, (int)top, (int)(w - sbW - 4 * s), (int)area));
        for (int i = 0; i < ScriptCommands.All.Length; i++)
        {
            float ry = top + i * rowH - listScroll;
            if (ry + rowH < top || ry > top + area)
                continue;
            string c = ScriptCommands.All[i];
            bool hover = !modal && Hit(x, ry, w - sbW - 4 * s, rowH) && _my >= top && _my < top + area;
            if (hover)
                _ui.Rect(x + 2 * s, ry, w - sbW - 6 * s, rowH, Hover);
            int par = c.IndexOf('(');
            float tx = x + pad;
            tx += _ui.Text(tx, ry + 2 * s, c[..par], new SN.Vector4(0.95f, 0.85f, 0.45f, 1));
            _ui.Text(tx, ry + 2 * s, Fit(c[par..], x + w - sbW - pad - tx), Muted);
            if (hover && _press && Doc is { } d)
            {
                _press = false;
                d.Insert(c[..par] + "(", _clock);
                ShowCaret(d);
            }
        }
        _ui.End();
    }

    private void DrawProblems()
    {
        (float x, float y, float w, float h) = ProblemsRect;
        float s = _scale, pad = 5 * s, rowH = _ui.LineHeight + 4 * s, sbW = 14 * s;
        bool modal = _newName != null || _confirmDelete >= 0;
        _ui.Begin();
        _ui.Rect(x, y, w, h, Panel);
        _ui.Rect(x, y, w, MathF.Max(1, s), LineCol);
        if (!modal && Hit(x, y, w, h) && _wheel != 0)
        {
            _problemBar.Value -= _wheel / 120f * rowH;
            _wheel = 0;
        }
        bool press = !modal && _press;
        _problemBar.Run(_ui, x + w - sbW - 2 * s, y + pad, sbW, h - 2 * pad, _problems.Count * rowH, h - 2 * pad, rowH, _mx, _my, ref press, _m.LeftButton == ButtonState.Pressed, _clock);
        if (!modal)
            _press = press;
        _ui.End();
        float problemScroll = _problemBar.Value;
        w -= sbW + 4 * s;
        _ui.BeginClipped(new Rectangle((int)x, (int)(y + pad), (int)w, (int)(h - 2 * pad)));
        if (_problems.Count == 0)
            _ui.Text(x + 8 * s, y + pad, Tr("CompileHelp", "Compiler (F7) : vérifie les scripts (syntaxe, commandes inconnues du jeu). Ctrl+S : enregistrer. Ctrl+N : nouveau fichier."), Muted);
        for (int i = 0; i < _problems.Count; i++)
        {
            ScriptCheck.Problem p = _problems[i];
            float ry = y + pad + i * rowH - problemScroll;
            if (ry + rowH < y || ry > y + h)
                continue;
            bool hover = !modal && Hit(x, ry, w, rowH);
            if (hover)
                _ui.Rect(x, ry, w, rowH, Hover);
            _ui.Text(x + 8 * s, ry + 2 * s, Tr("ProblemLine", "{0} - {1} ligne {2} : {3}", p.Error ? Tr("Error", "Erreur") : Tr("Warning", "Attention"), p.File, p.Line, p.Message), p.Error ? Red : new SN.Vector4(0.95f, 0.8f, 0.4f, 1));
            if (hover && _press)
            {
                _press = false;
                GoTo(p);
            }
        }
        _ui.End();
    }

    private void DrawDialogs()
    {
        if (_newName == null && _confirmDelete < 0)
            return;
        float s = _scale, w = 520 * s, h = 150 * s, x = (W - w) / 2, y = (H - h) / 3;
        _ui.Begin();
        _ui.Rect(0, 0, W, H, new SN.Vector4(0, 0, 0, 0.45f));
        _ui.Rect(x, y, w, h, Panel);
        _ui.Frame(x, y, w, h, Accent, 2 * s);
        float bw = 120 * s, bh = 32 * s, by = y + h - bh - 12 * s;
        if (_newName != null)
        {
            _ui.Text(x + 14 * s, y + 14 * s, Tr("NewScript", "Nouveau script : nom du fichier (.scp)"), Accent);
            float fy = y + 20 * s + _ui.LineHeight;
            _ui.Rect(x + 14 * s, fy, w - 28 * s, _ui.LineHeight + 10 * s, new SN.Vector4(0.05f, 0.05f, 0.07f, 1));
            _ui.Frame(x + 14 * s, fy, w - 28 * s, _ui.LineHeight + 10 * s, Accent, MathF.Max(1, s));
            float tw = _ui.Text(x + 20 * s, fy + 5 * s, _newName, TextCol);
            if ((int)(_clock * 2) % 2 == 0)
                _ui.Rect(x + 20 * s + tw + 1, fy + 4 * s, 2 * s, _ui.LineHeight + 2 * s, TextCol);
            if (Button(x + w - 2 * bw - 26 * s, by, bw, bh, Tr("Create", "Créer"), true))
                CreateDoc();
            if (Button(x + w - bw - 14 * s, by, bw, bh, Tr("Cancel", "Annuler"), true))
                _newName = null;
        }
        else
        {
            string name = _confirmDelete < _docs.Count ? _docs[_confirmDelete].Name : "?";
            _ui.Text(x + 14 * s, y + 14 * s, Tr("DeleteScript", "Supprimer le script « {0} » ?", name), Accent);
            _ui.Text(x + 14 * s, y + 20 * s + _ui.LineHeight, Tr("DeleteScriptText", "Le fichier est effacé du dossier de travail."), Muted);
            if (Button(x + w - 2 * bw - 26 * s, by, bw, bh, Tr("Delete", "Supprimer"), true))
                DeleteDoc(_confirmDelete);
            if (Button(x + w - bw - 14 * s, by, bw, bh, Tr("Cancel", "Annuler"), true))
                _confirmDelete = -1;
        }
        _ui.End();
        _press = false;
    }
}
