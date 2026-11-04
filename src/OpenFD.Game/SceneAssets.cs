using System.Collections.Concurrent;
using System.Numerics;
using System.Text.RegularExpressions;
using OpenFD.Formats;

namespace OpenFD.Game;

/// <summary>
/// Tout ce qu'il faut pour dessiner une mission en 3D : décor, objets posés, véhicules, civils, arbres,
/// maillages, matériaux et textures. Partagé par le visualiseur web et l'application de bureau.
/// Les chemins de modèles sont relatifs à Gfx et sans extension : « Placeable\bank_chair_01 »,
/// « Unit\CivilianVehicle\veh_c_ford4 ».
/// </summary>
public sealed class SceneAssets
{
    private readonly GameFiles _files;
    private readonly Dictionary<string, ClassFile> _classCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _texIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MeshAsset> _meshes = new(StringComparer.OrdinalIgnoreCase);

    public SceneAssets(GameFiles files) => _files = files;
    public GameFiles Files => _files;

    public sealed record Node(string Mesh, Matrix4x4 World);
    public sealed record Placed(string Model, string Name, bool Civilian, Matrix4x4 World);
    public sealed record TreeInfo(string Model, string Name, float Height, int Seed, string? Bark, string? Leaves, Matrix4x4 World);
    /// <summary>Groupe de faces : matériau, texture de couleur, texture de saleté (seconde texture des shaders sol / bâtiment), détourage.</summary>
    public sealed record Group(int Start, int Count, string Material, string? Diffuse, bool Alpha, string? Dirt = null);
    public sealed class MeshAsset
    {
        public required Mesh Mesh { get; init; }
        public required List<Group> Groups { get; init; }
        public required float[] Uv0 { get; init; }
        public float[]? Uv1 { get; init; }
        public string? Lightmap { get; init; }
    }

    public string GfxDir(string mission) =>
        _files.List("Gfx\\Mission\\").Select(p => p.Split('\\')[2]).First(m => m.Equals(mission, StringComparison.OrdinalIgnoreCase));

    private ClassFile? Class(LevelObject o)
    {
        try { lock (_classCache) return ClassFile.Load(_files, "Design\\" + o.ClassPath["design\\".Length..] + ".class", _classCache); }
        catch { return null; }
    }

    /// <summary>Décor de la mission : bâtiments (&lt;mission&gt;.sg) et sol (FGD_&lt;mission&gt;.sg).</summary>
    public List<Node> Scenery(string mission)
    {
        string prefix = $"Gfx\\Mission\\{GfxDir(mission)}\\";
        List<Node> list = new List<Node>();
        foreach (string sg in _files.List(prefix).Where(p =>
                 {
                     // décor « London.sg » et sol « FGD_London.sg » ; certaines missions (Institute) : « level_institute.sg »
                     string f = p[(p.LastIndexOf('\\') + 1)..];
                     foreach (string n in new[] { mission, "level_" + mission })
                         if (f.Equals(n + ".sg", StringComparison.OrdinalIgnoreCase) || f.Equals("FGD_" + n + ".sg", StringComparison.OrdinalIgnoreCase))
                             return true;
                     return false;
                 }))
        {
            Dictionary<string, (string parent, Matrix4x4 m)> nodes = Rooms.SceneNodes(_files.Read(sg));
            foreach (string k in nodes.Keys)
            {
                string up = k.ToUpperInvariant();
                // les nœuds « …_ROOM » sont de vrais maillages (sol et cloisons de la pièce, éclairage précalculé) : affichés
                if (up.Contains("_COLL") || up.Contains("MASK"))
                    continue;
                if (_files.TryRead(prefix + k + ".mesh") == null)
                    continue;
                list.Add(new Node(k, Rooms.World(nodes, k)));
            }
        }
        return list;
    }

    public static string GfxPath(string baseDir, string model)
    {
        List<string> parts = new List<string>(baseDir.Split('\\'));
        foreach (string seg in model.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (seg == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
            }
            else if (seg != ".")
                parts.Add(seg);
        return string.Join("\\", parts);
    }

    /// <summary>Objets posés et civils : classe → [DISPLAY] Model → Gfx\&lt;dossier&gt;\&lt;modèle&gt;.sg.</summary>
    public List<Placed> Placeables(Level level)
    {
        List<Placed> res = new List<Placed>();
        Dictionary<string, string?> models = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (LevelObject o in level.Objects.Where(o => (o.Kind == EntityKind.Placeable || o.Kind == EntityKind.Civilian) && o.HasTransform))
        {
            if (!models.TryGetValue(o.ClassPath, out string? model))
            {
                string? m = Class(o)?.Get("DISPLAY", "Model")?.Trim('"', ' ');
                model = string.IsNullOrEmpty(m) || m.Equals("noitem", StringComparison.OrdinalIgnoreCase) ? null
                    : GfxPath(o.Kind == EntityKind.Civilian ? "Unit\\Civilian" : "Placeable", m);
                if (model != null && _files.TryRead("Gfx\\" + model + ".sg") == null)
                    model = null;
                models[o.ClassPath] = model;
            }
            if (model != null)
                res.Add(new Placed(model, o.Name, o.Kind == EntityKind.Civilian, o.Matrix));
        }
        return res;
    }

    /// <summary>Arbres SpeedTree : modèle, hauteur, graine et textures d'écorce et de feuillage.</summary>
    public List<TreeInfo> Trees(Level level)
    {
        List<TreeInfo> res = new List<TreeInfo>();
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform))
        {
            ClassFile? c = Class(o);
            string? model = c?.Get("SPEEDTREE", "TreeModel")?.Trim('"', ' ');
            if (c == null || string.IsNullOrEmpty(model))
                continue;
            byte[]? spt = _files.TryRead("Gfx\\Speedtree\\" + model + ".spt");
            List<string> tex = new List<string>();
            if (spt != null)
                foreach (Match mt in Regex.Matches(System.Text.Encoding.Latin1.GetString(spt), @"([A-Za-z0-9_]+)\.tga"))
                    tex.Add(mt.Groups[1].Value);
            string? Have(string n) => _files.TryRead("Gfx\\Speedtree\\General\\" + n + ".tga") != null ? n : null;
            string? bark = tex.Select(Have).FirstOrDefault(t => t != null && t.Contains("Bark", StringComparison.OrdinalIgnoreCase));
            string? leaves = tex.Select(Have).FirstOrDefault(t => t != null && (t.Contains("Frond", StringComparison.OrdinalIgnoreCase) || t.Contains("Needles", StringComparison.OrdinalIgnoreCase)))
                         ?? (model.Contains("palm", StringComparison.OrdinalIgnoreCase) ? null : "AmurCorkFrondBranches");
            res.Add(new TreeInfo(model, o.Name, c.GetFloat("SPEEDTREE", "TreeHeight", 1000), c.GetInt("SPEEDTREE", "TreeSeed", 1), bark, leaves, o.Matrix));
        }
        return res;
    }

    public byte[]? TreeTexture(string name) =>
        Regex.IsMatch(name, "^[A-Za-z0-9_]+$") ? _files.TryRead("Gfx\\Speedtree\\General\\" + name + ".tga") : null;

    private static readonly Regex StateRx = new(@"_e\d$", RegexOptions.IgnoreCase);
    private static readonly Regex SkipRx = new(@"(_LOD[1-9]|_de?br\d+|_deb_\d+|_debris|_dead)$|^gun$|^Phare|Glow|giro", RegexOptions.IgnoreCase);

    /// <summary>Pièces d'un modèle (état intact _e1, niveau de détail maximal, sans débris).</summary>
    public List<Node> ModelParts(string model) => ModelParts(model, "e1");

    /// <summary>
    /// Pièces d'un modèle dans un état d'endommagement : « e1 » intact, « e2 », « e3 » abîmé, « dead » détruit
    /// (véhicules : veh_c_classic1_e1 … _e3, veh_c_classic1_dead). État absent : l'état intact.
    /// </summary>
    public List<Node> ModelParts(string model, string state)
    {
        if (!state.Equals("e1", StringComparison.OrdinalIgnoreCase))
        {
            string dir0 = model.Contains('\\') ? model[..model.LastIndexOf('\\')] : "Placeable";
            string m0 = model.Contains('\\') ? model : "Placeable\\" + model;
            Dictionary<string, (string parent, Matrix4x4 m)> nodes0 = Rooms.SceneNodes(_files.Read("Gfx\\" + m0 + ".sg"));
            string suffix = "_" + state;
            List<string> hit = nodes0.Keys.Where(k => k.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && _files.TryRead("Gfx\\" + dir0 + "\\" + k + ".mesh") != null).ToList();
            if (hit.Count > 0)
            {
                // pièces de cet état, plus celles qui n'ont pas d'état (roues…)
                IEnumerable<string> plain = nodes0.Keys.Where(k => !StateRx.IsMatch(k) && !k.EndsWith("_dead", StringComparison.OrdinalIgnoreCase)
                    && _files.TryRead("Gfx\\" + dir0 + "\\" + k + ".mesh") != null
                    && !k.ToUpperInvariant().Contains("_COLL") && !k.ToUpperInvariant().Contains("MASK") && !SkipRx.IsMatch(k));
                return hit.Concat(state == "dead" ? Enumerable.Empty<string>() : plain).Distinct()
                    .Select(k => new Node(dir0 + "\\" + k, Rooms.World(nodes0, k))).ToList();
            }
        }
        string dir = model.Contains('\\') ? model[..model.LastIndexOf('\\')] : "Placeable";
        if (!model.Contains('\\'))
            model = "Placeable\\" + model;
        Dictionary<string, (string parent, Matrix4x4 m)> nodes = Rooms.SceneNodes(_files.Read("Gfx\\" + model + ".sg"));
        List<string> withMesh = nodes.Keys.Where(k => _files.TryRead("Gfx\\" + dir + "\\" + k + ".mesh") != null).ToList();
        bool hasStates = withMesh.Any(k => StateRx.IsMatch(k));
        return withMesh
            .Where(k => !hasStates || !StateRx.IsMatch(k) || k.EndsWith("_e1", StringComparison.OrdinalIgnoreCase))
            .Where(k => !k.ToUpperInvariant().Contains("_COLL") && !k.ToUpperInvariant().Contains("MASK") && !SkipRx.IsMatch(k))
            .Select(k => new Node(dir + "\\" + k, Rooms.World(nodes, k)))
            // pièces de l'état brûlé (texture « …_brul ») : seulement après destruction
            .Where(n => { try { return !LoadMesh("", n.Mesh, "gfx").Groups.All(g => g.Diffuse != null && g.Diffuse.Contains("_brul", StringComparison.OrdinalIgnoreCase)); } catch { return true; } })
            .ToList();
    }

    /// <summary>
    /// Maillage prêt à dessiner. <paramref name="name"/> : nom du décor de la mission (src null),
    /// ou chemin Gfx complet (src « gfx »).
    /// </summary>
    public MeshAsset LoadMesh(string mission, string name, string? src) =>
        _meshes.GetOrAdd((src ?? mission) + "/" + name, _ =>
        {
            string prefix, baseName = name;
            if (src == "gfx")
            {
                int cut = name.LastIndexOf('\\');
                prefix = "Gfx\\" + name[..(cut + 1)];
                baseName = name[(cut + 1)..];
            }
            else
                prefix = src == "placeable" ? "Gfx\\Placeable\\" : $"Gfx\\Mission\\{GfxDir(mission)}\\";
            Mesh m = Mesh.Load(_files.Read(prefix + baseName + ".mesh"), _files.Read(prefix + baseName + ".meshdata"), baseName);
            List<Group> groups = m.Groups.Select(gr =>
            {
                byte[]? matBytes = _files.TryRead(prefix + "Shaders\\" + gr.material);
                Material? mat = matBytes != null ? Material.Load(matBytes, gr.material) : null;
                // saleté : première texture après la couleur qui n'est ni brûlure, ni éclairage, ni reflet, ni brillance
                string? dirt = mat?.Textures.Skip(1).FirstOrDefault(t => !Regex.IsMatch(t, "burn|light|refl|_spe|_opa|dummy", RegexOptions.IgnoreCase));
                return new Group(gr.start, gr.count, gr.material, mat?.Diffuse, CutOut(mission, mat?.Diffuse), dirt);
            }).ToList();
            // uv0 = couleur (premier emplacement) ; uv1 = éclairage précalculé (dernier emplacement présent)
            int uvA = Array.FindIndex(m.Uv, u => u != null), uvB = Array.FindLastIndex(m.Uv, u => u != null);
            if (uvB == uvA)
                uvB = -1;
            string? lightmap = src != null ? null : $"FLM_{mission}_{baseName}_0";
            if (lightmap != null && ResolveTex(mission, lightmap) == null)
                lightmap = null;
            return new MeshAsset
            {
                Mesh = m, Groups = groups,
                Uv0 = uvA >= 0 ? m.Uv[uvA]! : new float[2 * m.VertexCount],
                Uv1 = uvB >= 0 ? m.Uv[uvB] : null,
                Lightmap = uvB >= 0 ? lightmap : null,
            };
        });

    /// <summary>Chemin de la texture (DDS) dans les banques ; cherche aussi dans celles des autres missions.</summary>
    public string? ResolveTex(string mission, string name, bool fallback = true)
    {
        Dictionary<string, string> idx = _texIndex.GetOrAdd(mission, mi =>
        {
            Dictionary<string, string> dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in _files.List("Gfx\\Textures\\"))
            {
                string[] parts = p.Split('\\');
                if (!parts[2].Equals(mi, StringComparison.OrdinalIgnoreCase))
                    continue;
                string b = parts[^1];
                if (b.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    b = b[..^4];
                int at = b.IndexOf('@');
                if (at >= 0)
                    b = b[..at];
                dict.TryAdd(b.Trim(), p);
            }
            return dict;
        });
        string n = Path.GetFileNameWithoutExtension(name.Replace('\\', '/')).Trim();
        // les entrées combinent parfois deux images : « bank_06bank_06_opa » = bank_06 + son masque d'opacité
        string? found = idx.TryGetValue(n, out string? exact) ? exact
            : idx.Where(kv => kv.Key.Length > n.Length && kv.Key.StartsWith(n, StringComparison.OrdinalIgnoreCase) && IsSecondMap(n, kv.Key[n.Length..]))
                 .Select(kv => kv.Value).FirstOrDefault();
        if (found != null || !fallback)
            return found;
        _missions ??= _files.List("Gfx\\Textures\\").Select(p => p.Split('\\')[2]).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m == "_cache" ? 1 : 0).ToList();
        foreach (string other in _missions)
            if (!other.Equals(mission, StringComparison.OrdinalIgnoreCase) && ResolveTex(other, name, false) is { } f)
                return f;
        return null;
    }

    private List<string>? _missions;

    public byte[]? Texture(string mission, string name) => ResolveTex(mission, name) is { } p ? _files.Read(p) : null;

    private static bool IsSecondMap(string n, string rest) =>
        rest.StartsWith(n, StringComparison.OrdinalIgnoreCase) ||
        (rest.Length > 3 && char.IsLetter(rest[0]) && Regex.IsMatch(rest, @"(_spec?|_opa| chb|_ch)$", RegexOptions.IgnoreCase));

    /// <summary>Transparence par découpe seulement si la texture est combinée à un masque.</summary>
    private bool CutOut(string mission, string? name)
    {
        if (name == null || ResolveTex(mission, name) is not { } p)
            return false;
        string e = p[(p.LastIndexOf('\\') + 1)..].ToLowerInvariant();
        return e.Contains("_opa") || e.Contains(" chb") || e.EndsWith("_ch");
    }
}
