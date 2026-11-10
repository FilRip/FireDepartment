using OpenFD.Formats;
using OpenFD.Game;

namespace OpenFD.MG;

/// <summary>
/// Tout ce que l'on peut poser avec l'éditeur, lu dans les fichiers du jeu (classes de Design, pour toutes les missions) :
/// objets 3D, objets dynamiques (et repères de l'éditeur), pompiers et véhicules, classes de foyers.
/// </summary>
public sealed class EditorCatalog
{
    public List<EditorItem> Objects { get; } = new();
    public List<EditorItem> Dynamic { get; } = new();
    public List<EditorItem> Firemen { get; } = new();
    public List<EditorItem> Trees { get; } = new();
    /// <summary>Civils : tous les éléments dont le nom commence par « civil » (pris dans les autres listes).</summary>
    public List<EditorItem> Civilians { get; } = new();
    public IEnumerable<EditorItem> All => Objects.Concat(Dynamic).Concat(Civilians).Concat(Firemen).Concat(Trees);
    /// <summary>Classes de foyers (« design\hotpoint\ground_l ») et leur type de feu (A, B, C, D).</summary>
    public List<(string ClassPath, string Name, char Type)> FireClasses { get; } = new();

    public const string MaskClass = "design\\placeable\\levels\\openfd_mask";

    public static EditorCatalog Build(GameFiles files)
    {
        EditorCatalog cat = new EditorCatalog();
        Dictionary<string, ClassFile> cache = new Dictionary<string, ClassFile>(StringComparer.OrdinalIgnoreCase);
        // modèles présents (Gfx\…\nom.sg), pour ne garder que les classes que l'on peut dessiner
        HashSet<string> models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string p in files.List("Gfx\\"))
            if (p.EndsWith(".sg", StringComparison.OrdinalIgnoreCase))
                models.Add(p["Gfx\\".Length..^3]);

        // repères propres à l'éditeur, en tête des objets dynamiques
        cat.Dynamic.Add(new EditorItem { Name = EditorText.T("ItemFire", "Foyer (feu)"), Kind = EditorItemKind.Fire, Entity = EntityKind.HotPoint });
        cat.Dynamic.Add(new EditorItem { Name = EditorText.T("ItemZone", "Zone"), Kind = EditorItemKind.Zone, Entity = EntityKind.Trigger, ClassPath = "design\\trigger\\base_trigger" });
        cat.Dynamic.Add(new EditorItem { Name = EditorText.T("ItemMask", "Masque de toit"), Kind = EditorItemKind.RoofMask, ClassPath = MaskClass });
        cat.Dynamic.Add(new EditorItem { Name = EditorText.T("ItemCamera", "Caméra de départ"), Kind = EditorItemKind.Camera, Entity = EntityKind.Waypoint, ClassPath = "design\\waypoint\\cam_startpos" });
        cat.Dynamic.Add(new EditorItem { Name = EditorText.T("ItemWaypoint", "Point de passage"), Kind = EditorItemKind.Waypoint, Entity = EntityKind.Waypoint, ClassPath = "design\\waypoint\\base_waypoint" });

        foreach (string rel in files.List("Design\\Placeable\\").Where(p => p.EndsWith(".class", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string leaf = Path.GetFileNameWithoutExtension(rel.Replace('\\', '/'));
            if (leaf.StartsWith("base_", StringComparison.OrdinalIgnoreCase) || leaf.StartsWith("openfd_", StringComparison.OrdinalIgnoreCase))
                continue;
            ClassFile? c = Load(files, rel, cache);
            if (c == null)
                continue;
            // arbres : leur onglet, dessinés comme dans le jeu (EZ-Tree)
            if (c.Get("SPEEDTREE", "TreeModel")?.Trim('"', ' ') is { Length: > 0 } tree)
            {
                cat.Trees.Add(new EditorItem
                {
                    Name = leaf, Kind = EditorItemKind.Tree, ClassPath = ClassPathOf(rel), Folder = "Tree",
                    TreeModel = tree, TreeHeight = c.GetFloat("SPEEDTREE", "TreeHeight", 1000), TreeSeed = c.GetInt("SPEEDTREE", "TreeSeed", 1),
                });
                continue;
            }
            string? model = ModelOf(c, "Placeable", models);
            if (model == null)
                continue;
            string[] parts = rel.Split('\\');
            string folder = parts.Length > 3 ? parts[2] : "";
            bool functional = folder.Equals("Functional", StringComparison.OrdinalIgnoreCase);
            EditorItem item = new EditorItem
            {
                Name = leaf, Kind = functional ? EditorItemKind.Dynamic : EditorItemKind.Object, ClassPath = ClassPathOf(rel), Model = model, Folder = folder,
            };
            (functional ? cat.Dynamic : cat.Objects).Add(item);
        }
        cat.Objects.Sort((a, b) => string.Compare(a.Folder + "\\" + a.Name, b.Folder + "\\" + b.Name, StringComparison.OrdinalIgnoreCase));

        // civils : objets dynamiques (on les sauve)
        foreach (string rel in files.List("Design\\Unit\\Civilian\\").Where(p => p.EndsWith(".class", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string leaf = Path.GetFileNameWithoutExtension(rel.Replace('\\', '/'));
            if (leaf.StartsWith("base_", StringComparison.OrdinalIgnoreCase) || Load(files, rel, cache) is not { } c || ModelOf(c, "Unit\\Civilian", models) is not { } model)
                continue;
            cat.Dynamic.Add(new EditorItem { Name = leaf, Kind = EditorItemKind.Civilian, Entity = EntityKind.Civilian, ClassPath = ClassPathOf(rel), Model = model, Folder = "Civilian" });
        }

        // pompiers puis véhicules
        foreach ((string folder, string gfx, EditorItemKind kind) in new[] { ("Fireman", "Unit\\Fireman", EditorItemKind.Fireman), ("Firevehicle", "Unit\\FireVehicle", EditorItemKind.Vehicle) })
            foreach (string rel in files.List("Design\\Unit\\" + folder + "\\").Where(p => p.EndsWith(".class", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string leaf = Path.GetFileNameWithoutExtension(rel.Replace('\\', '/'));
                if (leaf.StartsWith("base_", StringComparison.OrdinalIgnoreCase) || leaf.StartsWith("openfd_", StringComparison.OrdinalIgnoreCase)
                    || Load(files, rel, cache) is not { } c || ModelOf(c, gfx, models) is not { } model)
                    continue;
                cat.Firemen.Add(new EditorItem { Name = leaf, Kind = kind, Entity = EntityKind.FireVehicle, ClassPath = ClassPathOf(rel), Model = model, Folder = folder });
            }

        foreach (string rel in files.List("Design\\Hotpoint\\").Where(p => p.EndsWith(".class", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string leaf = Path.GetFileNameWithoutExtension(rel.Replace('\\', '/'));
            if (leaf.StartsWith("base_", StringComparison.OrdinalIgnoreCase) || Load(files, rel, cache) is not { } c)
                continue;
            string t = (c.Get("HOTPOINT_TAGS", "Type") ?? "FireType_A").Trim('"', ' ').ToUpperInvariant();
            cat.FireClasses.Add((ClassPathOf(rel), leaf, t.Length > 0 ? t[^1] : 'A'));
        }
        // civils : leur onglet
        foreach (List<EditorItem> list in new[] { cat.Objects, cat.Dynamic, cat.Firemen, cat.Trees })
        {
            cat.Civilians.AddRange(list.Where(i => i.Name.StartsWith("civil", StringComparison.OrdinalIgnoreCase)));
            list.RemoveAll(i => i.Name.StartsWith("civil", StringComparison.OrdinalIgnoreCase));
        }
        Console.WriteLine($"Éditeur : {cat.Civilians.Count} civils, {cat.Objects.Count} objets 3D, {cat.Dynamic.Count} objets dynamiques, {cat.Firemen.Count} pompiers et véhicules, {cat.Trees.Count} arbres, {cat.FireClasses.Count} foyers");
        return cat;
    }

    /// <summary>« Design\Placeable\Furniture\chair_1.class » → « design\placeable\furniture\chair_1 » (comme dans les niveaux).</summary>
    public static string ClassPathOf(string rel) => ("design\\" + rel["Design\\".Length..^".class".Length]).ToLowerInvariant();

    /// <summary>Fichier de la classe d'un niveau : « design\unit\fireman\x » → « Design\unit\fireman\x.class ».</summary>
    public static string ClassFileOf(string classPath) => "Design\\" + classPath["design\\".Length..] + ".class";

    private static ClassFile? Load(GameFiles files, string rel, Dictionary<string, ClassFile> cache)
    {
        try
        {
            return ClassFile.Load(files, rel, cache);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ModelOf(ClassFile c, string baseDir, HashSet<string> models)
    {
        string? m = c.Get("DISPLAY", "Model")?.Trim('"', ' ');
        if (string.IsNullOrEmpty(m) || m.Equals("noitem", StringComparison.OrdinalIgnoreCase))
            return null;
        string model = SceneAssets.GfxPath(baseDir, m);
        return models.Contains(model) ? model : null;
    }
}
