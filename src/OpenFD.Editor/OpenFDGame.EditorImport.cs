using Microsoft.Xna.Framework.Graphics;

namespace OpenFD.MG;

// éditeur : bouton « Importer » de la barre de menu → un fichier OBJ (Blender) devient un objet à poser, simple décor ou
// utilisable par les pompiers (ObjImport.cs écrit ses fichiers dans Data) ; il apparaît ensuite dans la liste de droite
public sealed partial class OpenFDGame
{
    private string? _edImportPath;
    private string _edImportName = "";
    private string? _edImportDir;   // dossier du dernier fichier importé : la fenêtre « Ouvrir » s'y ouvre

    private void EdImport()
    {
        EdCommitFocus();
        _edPopup = null;
        string folder = _edImportDir ?? _edExportDir ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string? path = FileDialog.OpenFile(Tr("ImportTitle", "Importer un objet 3D (OBJ)"), folder, "obj", "Wavefront OBJ");
        Sdl.Raise(Window.Handle);
        if (path == null)
            return;
        _edImportDir = Path.GetDirectoryName(path);
        _edImportPath = path;
        _edImportName = ObjImport.Safe(Path.GetFileNameWithoutExtension(path));
        EdDialogBox d = new EdDialogBox
        {
            Title = Tr("ImportAsk", "Importer « {0} »", Path.GetFileName(path)),
            Lines = { Tr("ImportText", "Nom de l'objet dans la liste, puis sa sorte : simple décor, ou objet utilisable par les pompiers (clic avec un pompier choisi, comme une armoire électrique).") },
            BodyH = EdRowH + 6 * _scale,
        };
        d.Body = (x, y, w) =>
        {
            float lw = w * 0.35f;
            _ui.Text(x, y + (EdRowH - _ui.LineHeight) / 2, Tr("ImportName", "Nom"), EdMuted);
            EdField("import.nom", x + lw, y, w - lw, EdRowH, _edImportName, v => _edImportName = ObjImport.Safe(v));
        };
        d.Buttons.Add((Tr("ImportDecor", "Décor"), () => EdImportCheck(false)));
        d.Buttons.Add((Tr("ImportUsable", "Utilisable"), () => EdImportCheck(true)));
        d.Buttons.Add((Tr("Cancel", "Annuler"), () => { }));
        _edDialog = d;
    }

    // nom vide ou déjà pris (un import précédent) : on le dit, ou on demande avant de le remplacer
    private void EdImportCheck(bool usable)
    {
        EdCommitFocus();
        string name = _edImportName;
        if (name.Length == 0)
        {
            EdMessage(Tr("ImportNoName", "Donnez un nom à l'objet (lettres, chiffres, _ et -)"), true);
            return;
        }
        if (ObjImport.Exists(_files.DataDir, name))
            EdAsk(Tr("ImportExistsTitle", "« {0} » existe déjà", name),
                new[] { Tr("ImportExists", "Un objet importé porte déjà ce nom. Le remplacer par ce fichier ? (les objets déjà posés dans les missions prendront le nouveau modèle)") },
                (Tr("Replace", "Remplacer"), () => EdImportMasks(name, usable)),
                (Tr("Cancel", "Annuler"), () => { }));
        else
            EdImportMasks(name, usable);
    }

    // couvercles noirs des pièces : fabriqués par l'éditeur, ou seulement ceux du fichier (« …_MASK » faits dans Blender)
    private void EdImportMasks(string name, bool usable) =>
        EdAsk(Tr("ImportMasksTitle", "Couvercles noirs"),
            new[] { Tr("ImportMasksText", "Fabriquer automatiquement les couvercles noirs des pièces ? (un par objet « …_ROOM1 », « …_ROOM2 »… du fichier qui n'a pas déjà son « …_MASK » ; sans pièce nommée, un seul sur tout l'objet). Ils cachent l'intérieur tant qu'un pompier n'y est pas entré.") },
            (Tr("ImportMasksYes", "Oui, les fabriquer"), () => EdDoImport(name, usable, true)),
            (Tr("ImportMasksNo", "Non, seulement l'objet"), () => EdDoImport(name, usable, false)),
            (Tr("Cancel", "Annuler"), () => { }));

    private void EdDoImport(string name, bool usable, bool masks)
    {
        if (_edImportPath is not { } path)
            return;
        byte[]? template = _files.TryRead("Gfx\\Placeable\\Shaders\\lv_Autom_022.stdmat");
        if (template == null)
        {
            EdMessage(Tr("ImportNoTemplate", "Import impossible : matériau modèle du jeu introuvable (Gfx\\Placeable\\Shaders\\lv_Autom_022.stdmat)"), true);
            return;
        }
        try
        {
            ObjImport imp = new ObjImport { AutoMasks = masks };
            imp.Read(path);
            imp.Write(_files.DataDir, name, usable, template);
            foreach (string w in imp.Warnings)
                Console.WriteLine($"Import OBJ {Path.GetFileName(path)} : {w}");
            Console.WriteLine($"Import OBJ : {path} → {ObjImport.ClassPath(name, usable)} ({imp.PartCount} pièces, {imp.TriangleCount} triangles, {imp.TextureCount} textures, {imp.MaskCount} couvercles fabriqués)");
            EdForgetImported(name);
            // le nouvel objet dans la liste de droite : son onglet, filtré sur son nom
            _edTab = usable ? 1 : 0;
            _edListFilter = name;
            _edBars[_edTab].Value = 0;
            string done = Tr("ImportDone", "Importé : « {0} » ({1} pièces, {2} triangles, {3} textures)", name, imp.PartCount, imp.TriangleCount, imp.TextureCount);
            if (masks)
                done += " — " + Tr("ImportMasksDone", "{0} couvercles noirs fabriqués", imp.MaskCount);
            if (imp.Warnings.Count > 0)
                EdMessage(done + " — " + Tr("ImportWarnings", "attention : {0}", string.Join(" ; ", imp.Warnings)), true);
            else
                EdMessage(done);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Import OBJ {path} : {e}");
            EdMessage(Tr("ImportError", "Import impossible : {0}", e.Message), true);
        }
    }

    // fichiers neufs : relus du disque, liste des objets refaite ; l'ancien modèle de ce nom (remplacé) est oublié
    private void EdForgetImported(string name)
    {
        _files.Rescan();
        _assets.ClearCaches();
        string model = "Placeable\\" + ObjImport.Folder + "\\" + name;
        if (_unitModels.TryGetValue(model, out List<(MeshGpu mesh, System.Numerics.Matrix4x4 part)>? old))
        {
            foreach ((MeshGpu m, _) in old)
                m.Dispose();
            _unitModels.Remove(model);
        }
        foreach (string k in _texCache.Keys.Where(k => k.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _texCache[k]?.Dispose();
            _texCache.Remove(k);
        }
        _edCatalog = EditorCatalog.Build(_files);
        EdClearPreviews();
    }
}
