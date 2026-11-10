using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenFD.Formats;
using OpenFD.Game;
using SN = System.Numerics;

namespace OpenFD.MG;

// éditeur : la barre de menu (Nouveau, Charger, Sauver, Tester, Script) et ce que font ses boutons
public sealed partial class OpenFDGame
{
    private Process? _edScriptProc;
    private int _edControlSeq;
    private string? _edPendingLoad;   // mission choisie dans la liste : ouverte au début de l'image suivante (pas en plein dessin)
    private string? _edPendingMessage;   // message montré quand cette mission est ouverte

    /// <summary>Dossier de travail des scripts de la mission ouverte (la fenêtre des scripts y écrit ; « Sauver » les recopie dans Data).</summary>
    private static string EdWork => Path.Combine(AppContext.BaseDirectory, "Travail", "Scripts");

    private void EdDrawMenu()
    {
        float s = _scale, h = EdMenuH, pad = 5 * s, bw = 110 * s;
        _ui.Begin();
        _ui.Rect(0, 0, _fbW, h, new SN.Vector4(0.1f, 0.1f, 0.12f, 0.96f));
        _ui.Rect(0, h - MathF.Max(1, s), _fbW, MathF.Max(1, s), EdLine);
        float x = pad;
        if (EdButton(x, pad, bw, h - 2 * pad, Tr("New", "Nouveau")))
            EdAskNewMission();
        x += bw + pad;
        if (EdButton(x, pad, bw, h - 2 * pad, Tr("Load", "Charger")))
            EdOpenMissionList(x, h);
        x += bw + pad;
        if (EdButton(x, pad, bw, h - 2 * pad, Tr("Save", "Sauver"), false, _edLoaded))
            EdSave();
        x += bw + pad;
        if (EdButton(x, pad, bw, h - 2 * pad, Tr("Test", "Tester"), false, _edLoaded))
            EdTest();
        x += bw + pad;
        if (EdButton(x, pad, bw, h - 2 * pad, Tr("Script", "Script"), false, _edLoaded))
            EdOpenScripts();
        x += bw + 3 * pad;
        // message du moment (en rouge : une erreur), il pâlit au bout de quelques secondes
        float age = (float)(_edClock - _edStatusAt);
        float a = _edStatusError ? 1 : Math.Clamp(1.3f - age / 8, 0.55f, 1);
        SN.Vector4 col = _edStatusError ? EdError : EdText;
        col.W = a;
        _ui.Text(x, (h - _ui.LineHeight) / 2, EdFit(_edStatus, _fbW - x - pad), col);
        _ui.End();
    }

    /// <summary>Liste des missions trouvées (Design\Mission\…) ; un clic ouvre la mission.</summary>
    private void EdOpenMissionList(float x, float y)
    {
        List<string> dirs = new List<string>();
        foreach (string lv in _files.List("Design\\Mission\\").Where(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase)))
        {
            string[] parts = lv.Split('\\');
            if (parts.Length >= 4 && !dirs.Contains(parts[2], StringComparer.OrdinalIgnoreCase))
                dirs.Add(parts[2]);
        }
        dirs.Sort(StringComparer.OrdinalIgnoreCase);
        List<string> labels = new List<string>();
        HashSet<int> dim = new HashSet<int>();
        for (int i = 0; i < dirs.Count; i++)
        {
            bool gfx = _assets.HasGfx(dirs[i]) || EdTerrainMission(dirs[i]);
            labels.Add(gfx ? dirs[i] : dirs[i] + "   " + Tr("NoGfx", "(graphismes absents)"));
            if (!gfx)
                dim.Add(i);
        }
        EdOpenPopup(Tr("MissionList", "Missions trouvées (un clic l'ouvre)"), labels, i => _edPendingLoad = dirs[i], x, y, 420 * _scale, dirs.FindIndex(d => d.Equals(_edMission, StringComparison.OrdinalIgnoreCase)));
        if (_edPopup != null)
            _edPopup.Dim = dim;
    }

    /// <summary>Mission faite par l'éditeur sans décor ([OPENFD] Terrain = 1 dans son .mis) : son sol est tiré de la grille.</summary>
    private bool EdTerrainMission(string dir)
    {
        try
        {
            string? mis = _files.List("Design\\Mission\\" + dir + "\\").FirstOrDefault(p => p.EndsWith(".mis", StringComparison.OrdinalIgnoreCase));
            return mis != null && ClassFile.Load(_files, mis).GetBool("OPENFD", "Terrain");
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Recopie les scripts de la mission dans le dossier de travail (ceux d'avant sont effacés).</summary>
    private void EdCopyScriptsToWork(string mission)
    {
        try
        {
            Directory.CreateDirectory(EdWork);
            foreach (string f in Directory.EnumerateFiles(EdWork, "*.scp"))
                File.Delete(f);
            string src = Path.Combine(_files.DataDir, "Script", "Mission", mission);
            if (Directory.Exists(src))
                foreach (string f in Directory.EnumerateFiles(src, "*.scp"))
                    File.Copy(f, Path.Combine(EdWork, Path.GetFileName(f)), true);
            EdControl("open " + mission);
        }
        catch (Exception e)
        {
            EdMessage(Tr("ScriptsError", "Scripts : {0}", e.Message), true);
        }
    }

    /// <summary>Ordre à la fenêtre des scripts (fichier « .controle » du dossier de travail, qu'elle lit deux fois par seconde).</summary>
    private void EdControl(string order)
    {
        try
        {
            Directory.CreateDirectory(EdWork);
            File.WriteAllText(Path.Combine(EdWork, ".controle"), $"{++_edControlSeq} {Environment.ProcessId} {order}");
        }
        catch (Exception)
        {
        }
    }

    private string EdShortName => new string(_edName.Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-').ToArray());

    /// <summary>« Sauver » : si des fichiers existent déjà, on demande avant de les écraser.</summary>
    private void EdSave() => EdSaveAsk(null);

    /// <summary>Sauvegarde avec confirmation ; <paramref name="then"/> : ce qui suit si la mission a été sauvée (lancer l'essai).</summary>
    private void EdSaveAsk(Action? then)
    {
        if (!_edLoaded)
            return;
        EdCommitFocus();
        EdUndoTrack(true);   // les derniers changements : une étape à annuler, avant la sauvegarde
        if (EdShortName.Length == 0)
        {
            EdMessage(Tr("NeedName", "Donnez un nom court à la mission (propriétés de la mission)"), true);
            return;
        }
        MissionWriter w = EdWriter();
        List<string> existing = w.ExistingFiles();
        if (existing.Count == 0)
        {
            if (EdDoSave(w))
                then?.Invoke();
            return;
        }
        string data = _files.DataDir.TrimEnd('\\', '/');
        List<string> lines = new List<string> { Tr("OverwriteText", "Ces fichiers existent déjà dans le dossier Data du jeu :") };
        lines.AddRange(existing.Take(8).Select(f => "  " + (f.StartsWith(data, StringComparison.OrdinalIgnoreCase) ? f[(data.Length + 1)..] : f)));
        if (existing.Count > 8)
            lines.Add("  " + Tr("OverwriteMore", "… et {0} autre(s)", existing.Count - 8));
        lines.Add(Tr("OverwriteBak", "Les fichiers remplacés sont gardés une fois en « .bak »."));
        EdAsk(Tr("OverwriteTitle", "Écraser les fichiers de « {0} » ?", EdShortName), lines,
            (Tr("Overwrite", "Écraser"), () =>
            {
                if (EdDoSave(EdWriter()))
                    then?.Invoke();
            }),
            (Tr("Cancel", "Annuler"), () => EdMessage(then != null ? Tr("TestCancelled", "Essai abandonné : rien n'a été écrit") : Tr("SaveCancelled", "Sauvegarde abandonnée"))));
    }

    /// <summary>Ce qui sera écrit : niveau (objets à leur place), grille, .mis, scripts, classes de l'éditeur, textures.</summary>
    private MissionWriter EdWriter()
    {
        // place et orientation : réécrites seulement pour les objets déplacés ou tournés (les autres restent octet pour octet)
        foreach (EditorObject e in _edObjects.Where(o => o.Position != o.Source.Position || o.Euler != o.Source.Rotation))
            EdUpdate(e, LevelEdit.WithTransform(e.Source, e.Position, e.Euler));
        // classes propres à l'éditeur : masques de toit (une par taille), pompiers aux outils choisis
        Dictionary<string, string> classes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (EditorObject e in _edObjects)
        {
            string rel = EditorCatalog.ClassFileOf(e.Source.ClassPath);
            if (e.Kind == EditorItemKind.RoofMask)
            {
                // masque de toit : sa classe et son modèle 3D (plaque noire de cette taille)
                int mw = (int)e.Size.X, mh = (int)e.Size.Y;
                classes["Design\\Placeable\\Levels\\" + MaskModel.Name(mw, mh) + ".class"] = MaskModel.ClassText(mw, mh);
                foreach ((string path, byte[] bytes) in MaskModel.Files(mw, mh))
                    files[path] = bytes;
            }
            else if (_edNewClasses.TryGetValue(rel, out string? text))
                classes[rel] = text;
        }
        // textures des objets posés (pour les recopier si elles viennent d'une autre mission)
        HashSet<string> textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string model in _edObjects.Where(o => o.Model != null && o.Added).Select(o => o.Model!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (SceneAssets.Node part in _assets.ModelParts(model))
                    foreach (SceneAssets.Group g in _assets.LoadMesh(_mission, part.Mesh, "gfx").Groups)
                        foreach (string? t in new[] { g.Diffuse, g.Dirt, g.Env })
                            if (!string.IsNullOrEmpty(t))
                                textures.Add(t);
            }
            catch (Exception)
            {
            }
        }
        string name = EdShortName;
        if (!name.Equals(_edMission, StringComparison.OrdinalIgnoreCase))
            _edMisText = IniText.Set(_edMisText, "MISSION", "Name", IniText.Get(_edMisText, "MISSION", "Name") is { Length: > 0 } old && !old.Equals(_edMission, StringComparison.OrdinalIgnoreCase) ? old : name);
        if (_session.Grid is { } grid)
        {
            _edMisText = IniText.Set(_edMisText, "SIMU", "GridSizeX", grid.Width.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _edMisText = IniText.Set(_edMisText, "SIMU", "GridSizeY", grid.Height.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return new MissionWriter
        {
            Files = _files, Assets = _assets, BaseMission = _edMission, Name = name, Level = _edLevel,
            Grid = _session.Grid?.Serialize(_edGrid), MisText = _edMisText,
            Scripts = ScriptCheck.ReadFolder(EdWork), Packed = _edPacked, ObjectTextures = textures, ExtraClasses = classes, ExtraFiles = files,
            NewMission = _noScenery,
        };
    }

    /// <summary>Écrit la mission (sans rien demander).</summary>
    private bool EdDoSave(MissionWriter? w)
    {
        if (!_edLoaded)
            return false;
        try
        {
            w ??= EdWriter();
            EdControl("save");
            w.Write();
            _files.Rescan();
            _assets.ClearCaches();
            _edClasses.Clear();
            _edOtherLevels = null;
            _edGrid = w.Grid;
            // la sauvegarde écrit les places dans les objets du niveau : ce n'est pas une étape à annuler
            if (_edUndoLast != null)
                _edUndoLast = EdTakeSnapshot();
            EdMessage(Tr("Saved", "Mission « {0} » sauvée : {1}", w.Name, string.Join(" · ", w.Report)));
            return true;
        }
        catch (Exception e)
        {
            EdMessage(Tr("SaveError", "Sauvegarde impossible : {0}", e.Message), true);
            Console.WriteLine(e);
            return false;
        }
    }

    /// <summary>
    /// « Tester » : scripts compilés (arrêt s'il y a une erreur), confirmation si des fichiers existent (refus : l'essai est
    /// abandonné), mission sauvée (au format choisi dans ses propriétés), puis lancée dans OpenFD (fenêtre).
    /// </summary>
    private void EdTest()
    {
        if (!_edLoaded)
            return;
        EdCommitFocus();
        EdControl("save");
        List<ScriptCheck.Problem> problems = ScriptCheck.Check(ScriptCheck.ReadFolder(EdWork), Path.Combine(_files.DataDir, "Script", "_Common"));
        List<ScriptCheck.Problem> errors = problems.Where(p => p.Error).ToList();
        if (errors.Count > 0)
        {
            EdMessage(Tr("TestScriptErrors", "Scripts : {0} erreur(s), la mission n'est pas lancée", errors.Count), true);
            EdOpenPopup(Tr("ScriptErrorsTitle", "Erreurs dans les scripts (un clic ouvre « Script »)"), errors.Select(p => Tr("ScriptErrorLine", "{0} ligne {1} : {2}", p.File, p.Line, p.Message)).ToList(),
                i => EdOpenScripts(EdFunctionAtError(errors[i])), _fbW * 0.2f, EdMenuH, _fbW * 0.6f);
            return;
        }
        int warn = problems.Count - errors.Count;
        // format (archives ou fichiers dans Data) : celui choisi dans les propriétés de la mission
        EdTestSaved(_edPacked, warn);
    }

    private static string? EdFunctionAtError(ScriptCheck.Problem p) => null;

    private void EdTestSaved(bool packed, int warn)
    {
        _edPacked = packed;
        EdSaveAsk(() => EdLaunch(warn));
    }

    private void EdLaunch(int warn)
    {
        string name = EdShortName;
        if (EdGameExe() is not { } exe)
        {
            EdMessage(Tr("NoGame", "OpenFD introuvable : compilez d'abord le jeu (src\\OpenFD.MG)"), true);
            return;
        }
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe.file) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe.dll ?? exe.file) ?? "" };
            if (exe.dll != null)
                psi.ArgumentList.Add(exe.dll);
            psi.ArgumentList.Add(_files.DataDir);
            psi.ArgumentList.Add("-test=" + name);
            psi.ArgumentList.Add("-windowed");
            Process.Start(psi);
            EdMessage(warn > 0 ? Tr("LaunchedWarn", "Mission « {0} » lancée dans OpenFD ({1} commande(s) inconnue(s) du jeu dans les scripts)", name, warn)
                : Tr("Launched", "Mission « {0} » lancée dans OpenFD", name));
        }
        catch (Exception e)
        {
            EdMessage(Tr("LaunchError", "Lancement d'OpenFD impossible : {0}", e.Message), true);
        }
    }

    /// <summary>
    /// Le jeu : à côté de l'éditeur, sinon dans le dossier de compilation de src\OpenFD.MG (même configuration, puis
    /// Release, puis Debug). Sans OpenFD.exe (Linux, macOS) : le lanceur « OpenFD », sinon « dotnet OpenFD.dll ».
    /// </summary>
    private static (string file, string? dll)? EdGameExe()
    {
        string here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        List<string> dirs = new List<string> { here };
        DirectoryInfo? tfm = new DirectoryInfo(here), cfg = tfm.Parent, bin = cfg?.Parent, proj = bin?.Parent, src = proj?.Parent;
        if (src != null && cfg != null)
            foreach (string c in new[] { cfg.Name, "Release", "Debug" })
                dirs.Add(Path.Combine(src.FullName, "OpenFD.MG", "bin", c, tfm.Name));
        foreach (string d in dirs)
        {
            string exe = Path.Combine(d, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "OpenFD.exe" : "OpenFD");
            string dll = Path.Combine(d, "OpenFD.dll");
            if (!File.Exists(dll))
                continue;   // (un lanceur seul, sans le jeu à côté, ne sert à rien)
            if (File.Exists(exe))
                return (exe, null);
            return ("dotnet", dll);
        }
        return null;
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>
    /// « Script » : la fenêtre des scripts (un autre programme), ou la remet devant si elle est déjà ouverte ;
    /// <paramref name="gotoOrder"/> : « fonction|paramètres » à montrer (créée si elle n'existe pas).
    /// </summary>
    private void EdOpenScripts(string? gotoOrder = null)
    {
        if (!_edLoaded)
            return;
        if (_edScriptProc is { HasExited: false } p)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    AllowSetForegroundWindow(p.Id);
                }
                catch (Exception)
                {
                }
            }
            EdControl(gotoOrder != null ? "goto " + gotoOrder : "front");
            return;
        }
        try
        {
            string exe = Environment.ProcessPath ?? "";
            ProcessStartInfo psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                psi.ArgumentList.Add(typeof(OpenFDGame).Assembly.Location);
            psi.ArgumentList.Add("--script");
            psi.ArgumentList.Add(_files.DataDir);
            psi.ArgumentList.Add(EdWork);
            psi.ArgumentList.Add(_edMission);
            psi.ArgumentList.Add(EditorText.Language);
            if (gotoOrder != null)
                psi.ArgumentList.Add(gotoOrder);
            _edScriptProc = Process.Start(psi);
            EdMessage(Tr("ScriptsOpened", "Fenêtre des scripts ouverte"));
        }
        catch (Exception e)
        {
            EdMessage(Tr("ScriptsWindowError", "Fenêtre des scripts : {0}", e.Message), true);
        }
    }

    /// <summary>« ... » d'une fonction de script : la fenêtre des scripts s'ouvre (ou passe devant) à cette fonction.</summary>
    private void EdGotoScript(string function, string signature) => EdOpenScripts(function + "|" + signature);

    /// <summary>
    /// Fonctions de script appelées par un objet : actions du composant A027 (« use », « repair », « open »… : fonction
    /// (Utilisateur, Objet, Action)), fonction appelée à sa destruction (composant A024). Étiquette, fonction, paramètres.
    /// </summary>
    private static List<(string label, string function, string signature)> EdScriptRefs(EditorObject e)
    {
        List<(string, string, string)> res = new List<(string, string, string)>();
        if (e.Source.GetComponent(0xA027) is { } ac)
            foreach ((string action, string script) in ActionsIn(e.Source.ComponentBytes(ac).ToArray()))
                res.Add((Tr("ScriptAction", "Action « {0} »", action), script, "EntityID User, EntityID Object, mcString State"));
        if (e.Source.GetComponent(0xA024) is { } dc && dc.Size > 13 && ScriptNameIn(e.Source.ComponentBytes(dc).ToArray()) is { } destroyed)
            res.Add((Tr("ScriptDestroyed", "À sa destruction"), destroyed, "EntityID Thing"));
        return res;
    }

    /// <summary>Textes du composant (longueur sur 4 octets, puis le texte terminé par 0).</summary>
    private static List<string> StringsIn(byte[] b, int min, int max)
    {
        List<string> strings = new List<string>();
        for (int p = 0; p + 4 < b.Length; p++)
        {
            int len = BitConverter.ToInt32(b, p);
            if (len < min || len > max || p + 4 + len > b.Length || b[p + 4 + len - 1] != 0)
                continue;
            bool ok = true;
            for (int i = 0; i < len - 1 && ok; i++)
                ok = b[p + 4 + i] >= 32 && b[p + 4 + i] < 127;
            if (!ok)
                continue;
            strings.Add(System.Text.Encoding.Latin1.GetString(b, p + 4, len - 1));
            p += 3 + len;
        }
        return strings;
    }

    /// <summary>Actions et fonctions du composant A027 (même lecture que le jeu : Usables.ParseActions).</summary>
    private static List<(string action, string script)> ActionsIn(byte[] b)
    {
        List<(string, string)> res = new List<(string, string)>();
        string? action = null;
        foreach (string s in StringsIn(b, 2, 80))
        {
            if (s.Contains('\\') || s.Contains('$'))
                continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^(use\d*|repair\d*|open|close)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                action = s;
                continue;
            }
            if (action != null && System.Text.RegularExpressions.Regex.IsMatch(s, @"^[A-Za-z_]\w+$"))
            {
                res.Add((action, s));
                action = null;
            }
        }
        return res;
    }

    /// <summary>Fonction appelée à la destruction (composant A024 : premier nom écrit, même lecture que le jeu).</summary>
    private static string? ScriptNameIn(byte[] b) =>
        StringsIn(b, 3, 64).FirstOrDefault(s => System.Text.RegularExpressions.Regex.IsMatch(s, @"^[A-Za-z0-9_]+$"));

    // ---- nouvelle mission ----
    private string _edNewName = "", _edNewW = "150", _edNewH = "150";

    /// <summary>« Nouveau » : nom court et taille de la carte (en cases de 1 m), puis la mission est créée et ouverte.</summary>
    private void EdAskNewMission()
    {
        EdCommitFocus();
        _edPopup = null;
        _edNewName = "";
        EdDialogBox d = new EdDialogBox
        {
            Title = Tr("NewTitle", "Nouvelle mission"),
            Lines = { Tr("NewText", "Taille de la carte : un nombre de cases de 1 m (les missions du jeu font de 140 à 300 cases de côté). La carte est plate : A + clic monte une case, Q + clic la descend.") },
            BodyH = 3 * (EdRowH + 6 * _scale),
        };
        d.Body = (x, y, w) =>
        {
            float lw = w * 0.45f, fh = EdRowH, s = _scale;
            string[] labels = { Tr("NewName", "Nom court (fichiers)"), Tr("NewWidth", "Largeur (cases, X)"), Tr("NewHeight", "Hauteur (cases, Y)") };
            string[] values = { _edNewName, _edNewW, _edNewH };
            for (int i = 0; i < 3; i++)
            {
                int k = i;
                float fy = y + i * (fh + 6 * s);
                _ui.Text(x, fy + (fh - _ui.LineHeight) / 2, labels[i], EdMuted);
                EdField("new." + i, x + lw, fy, w - lw, fh, values[i], v =>
                {
                    if (k == 0)
                        _edNewName = v.Trim();
                    else if (k == 1)
                        _edNewW = v.Trim();
                    else
                        _edNewH = v.Trim();
                });
            }
        };
        d.Buttons.Add((Tr("Create", "Créer"), () =>
        {
            EdCommitFocus();
            if (!int.TryParse(_edNewW, out int nw) || !int.TryParse(_edNewH, out int nh))
            {
                EdMessage(Tr("NewBadSize", "Taille invalide : deux nombres de cases"), true);
                return;
            }
            EdCreateMission(_edNewName, nw, nh);
        }));
        d.Buttons.Add((Tr("Cancel", "Annuler"), () => { }));
        _edDialog = d;
    }

    /// <summary>
    /// Crée une mission vide (fichiers dans Data, puis ouverte) : grille plate de la taille donnée, niveau avec le soleil, la
    /// caméra de départ et l'arrivée des équipes (objets recopiés d'une mission du jeu), .mis et script de départ.
    /// </summary>
    private void EdCreateMission(string name, int w, int h)
    {
        name = new string(name.Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-').ToArray());
        if (name.Length == 0)
        {
            EdMessage(Tr("NeedName", "Donnez un nom court à la mission (propriétés de la mission)"), true);
            return;
        }
        if (_files.List("Design\\Mission\\" + name + "\\").Any() || _assets.HasGfx(name))
        {
            EdMessage(Tr("NewExists", "La mission « {0} » existe déjà : choisissez un autre nom", name), true);
            return;
        }
        if (w < 20 || h < 20 || w > 1000 || h > 1000)
        {
            EdMessage(Tr("NewSizeRange", "Taille : de 20 à 1000 cases de côté"), true);
            return;
        }
        // mission modèle : la première dont le niveau a un soleil (objet lumière de type SUN)
        (Level level, string mis)? model = null;
        foreach (string lv in _files.List("Design\\Mission\\").Where(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Level l = Level.Parse(_files.Read(lv));
                if (!l.Objects.Any(o => o.Kind == EntityKind.Light && EdClass(o.ClassPath)?.Get("LIGHT", "LightType")?.Trim('"', ' ').Equals("SUN", StringComparison.OrdinalIgnoreCase) == true))
                    continue;
                int at = l.MissionPath.IndexOf("Design\\", StringComparison.OrdinalIgnoreCase);
                if (at < 0 || _files.TryRead(l.MissionPath[at..]) is not { } misBytes)
                    continue;
                model = (l, System.Text.Encoding.Latin1.GetString(misBytes));
                break;
            }
            catch (Exception)
            {
            }
        }
        if (model is not { } m)
        {
            EdMessage(Tr("NewNoModel", "Aucune mission du jeu ne peut servir de modèle"), true);
            return;
        }
        Level level = m.level;
        List<LevelObject> all = level.Objects.ToList();
        LevelObject sun = all.First(o => o.Kind == EntityKind.Light && EdClass(o.ClassPath)?.Get("LIGHT", "LightType")?.Trim('"', ' ').Equals("SUN", StringComparison.OrdinalIgnoreCase) == true);
        LevelObject? waypoint = all.FirstOrDefault(o => o.Kind == EntityKind.Waypoint && o.HasTransform && o.ClassName.Equals("cam_startpos", StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(o => o.Kind == EntityKind.Waypoint && o.HasTransform);
        level.Objects.Clear();
        level.ClearHotPointLinks();
        float cx = w * Grid.CellSize / 2, cy = h * Grid.CellSize / 2;
        level.Objects.Add(LevelEdit.WithTransform(sun, new SN.Vector3(cx, cy, 5000), sun.Rotation));
        if (waypoint != null)
        {
            // caméra de départ : au sud du centre, regard vers le nord et vers le bas ; arrivée des équipes : du bord sud vers le centre
            SN.Vector3 eye = new SN.Vector3(cx, cy - 2500, 2500), f = SN.Vector3.Normalize(new SN.Vector3(cx, cy, 0) - eye);
            float z = MathF.Asin(Math.Clamp(f.X, -1, 1)), x = MathF.Atan2(-f.Z, -f.Y);
            LevelObject cam = LevelEdit.Clone(waypoint, LevelEdit.NewId(level), "cam_startpos", "design\\waypoint\\cam_startpos");
            level.Objects.Add(LevelEdit.WithTransform(cam, eye, new SN.Vector3(x, 0, z)));
            LevelObject start = LevelEdit.Clone(waypoint, LevelEdit.NewId(level), "locstart1_1", waypoint.ClassPath);
            level.Objects.Add(LevelEdit.WithTransform(start, new SN.Vector3(cx, 300, 0), SN.Vector3.Zero));
            LevelObject end = LevelEdit.Clone(waypoint, LevelEdit.NewId(level), "locend1_1", waypoint.ClassPath);
            level.Objects.Add(LevelEdit.WithTransform(end, new SN.Vector3(cx, MathF.Min(cy, 2500), 0), SN.Vector3.Zero));
        }
        string mis = m.mis;
        foreach ((string sec, string key, string value) in new[]
        {
            ("MISSION", "Name", name), ("MISSION", "Minimap", ""), ("DISPLAY", "Background", ""),
            ("BRIEFING", "VIDEOFEEDFILE", ""), ("BRIEFING", "VideoSubtitleFile", ""), ("BRIEFING", "StoryTitleKey", name),
            ("SIMU", "GridSizeX", w.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("SIMU", "GridSizeY", h.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("SCRIPT", "AUTOEXECSCRIPT", name + "_Start"), ("SCRIPT", "MISSIONSCRIPT1", name + "_Main"), ("SCRIPT", "MISSIONSCRIPT2", ""),
            ("FIRESTATION", "InitialDispatchSpawnPoint", "1"), ("OPENFD", "Terrain", "1"),
        })
            mis = IniText.Set(mis, sec, key, value);
        for (int i = 1; i <= 9; i++)
            if (IniText.Get(mis, "FIRESTATION", $"REINFORCEMENT{i}") != null)
                mis = IniText.Set(mis, "FIRESTATION", $"REINFORCEMENT{i}", "");
        string script = $"// {name} : script de la mission (créée par l'éditeur d'OpenFD)\r\n\r\n// au chargement de la mission\r\nvoid {name}_Start()\r\n{{\r\n}}\r\n\r\n// appelée tout au long de la partie\r\nvoid {name}_Main()\r\n{{\r\n}}\r\n";
        try
        {
            MissionWriter wr = new MissionWriter
            {
                Files = _files, Assets = _assets, BaseMission = name, Name = name, Level = level, Grid = Grid.Flat(w, h).Serialize(), MisText = mis,
                Scripts = new Dictionary<string, string> { [name + ".scp"] = script }, NewMission = true,
            };
            wr.Write();
            _files.Rescan();
            _assets.ClearCaches();
            _edOtherLevels = null;
        }
        catch (Exception e)
        {
            EdMessage(Tr("NewError", "Création impossible : {0}", e.Message), true);
            return;
        }
        // ouverte au début de l'image suivante (la fenêtre « Nouveau » est traitée en plein dessin)
        _edPendingLoad = name;
        _edPendingMessage = Tr("NewDone", "Mission « {0} » créée : {1} x {2} cases (Data\\Design\\Mission\\{0})", name, w, h);
    }
}
