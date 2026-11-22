using OpenFD.Game;

namespace OpenFD.MG;

// éditeur : clic droit sur un objet de la liste de droite → menu « Exporter en OBJ… » (Blender) ; quand une mission est
// ouverte, le même menu propose aussi d'exporter tout son décor (bâtiments, toits, pièces, couvercles noirs)
public sealed partial class OpenFDGame
{
    private void EdItemMenu(EditorItem it)
    {
        List<string> items = new List<string>();
        List<Action> acts = new List<Action>();
        if (it.Model != null)
        {
            items.Add(Tr("ExportObj", "Exporter en OBJ…"));
            acts.Add(() => EdExportObj(it));
        }
        if (_edLoaded)
        {
            items.Add(Tr("ExportDecorObj", "Exporter le décor de la mission en OBJ…"));
            acts.Add(EdExportDecorObj);
        }
        if (items.Count == 0)
            return;
        EdOpenPopup(it.Name, items, i => acts[i](), _edMx, _edMy, 360 * _scale);
    }

    private void EdExportObj(EditorItem it)
    {
        if (it.Model is not { } model)
            return;
        string name = Path.GetFileName(model.Replace('\\', '/'));
        ObjExport ex = new ObjExport();
        foreach (SceneAssets.Node part in _assets.AllParts(model))
            try
            {
                ex.Add(Path.GetFileName(part.Mesh.Replace('\\', '/')), _assets.LoadMesh(_mission, part.Mesh, "gfx"), part.World);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Export OBJ {model} : {e.Message}");
            }
        EdWriteObj(name, ex);
    }

    private void EdExportDecorObj()
    {
        ObjExport ex = new ObjExport();
        foreach (SceneAssets.Node n in _assets.Scenery(_mission).Concat(_assets.Scenery(_mission, true)))
            try
            {
                ex.Add(n.Mesh, _assets.LoadMesh(_mission, n.Mesh, null), n.World);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Export OBJ décor {n.Mesh} : {e.Message}");
            }
        EdWriteObj(EdShortName + "_decor", ex);
    }

    private void EdWriteObj(string name, ObjExport ex)
    {
        if (ex.Count == 0)
        {
            EdMessage(Tr("ExportObjNothing", "Rien à exporter : ce modèle n'a pas de pièce 3D"), true);
            return;
        }
        string folder = _edExportDir ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string? path = FileDialog.SaveFile(Tr("ExportObjTitle", "Exporter « {0} » en OBJ", name), folder, name + ".obj", "obj", "Wavefront OBJ");
        Sdl.Raise(Window.Handle);
        if (path == null)
            return;
        try
        {
            int tex = ex.Write(path, t => _assets.Texture(_mission, t));
            _edExportDir = Path.GetDirectoryName(path);
            EdMessage(Tr("ExportObjDone", "Exporté : {0} ({1} pièces, {2} textures)", path, ex.Count, tex));
            Console.WriteLine($"Export OBJ : {path} ({ex.Count} pièces, {tex} textures)");
        }
        catch (Exception e)
        {
            EdMessage(Tr("ExportObjError", "Export impossible : {0}", e.Message), true);
            Console.WriteLine($"Export OBJ {path} : {e}");
        }
    }
}
