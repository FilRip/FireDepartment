using System.Globalization;
using System.Numerics;
using System.Text;
using OpenFD.ConvFD1;

namespace OpenFD.MG;

/// <summary>
/// Import d'un objet 3D Wavefront OBJ (+ .mtl et ses textures) dans les fichiers du jeu, pour l'éditeur :
/// Gfx\Placeable\Import\&lt;nom&gt;.sg, ses maillages et matériaux, ses textures (Gfx\Textures\Import\, DDS) et sa classe
/// (décor : Design\Placeable\Import\ ; utilisable par les pompiers : Design\Placeable\Functional\Import\).
/// Le fichier est en mètres avec Y vers le haut (Blender) ; le jeu compte en centimètres avec Z vers le haut.
/// Chaque objet du fichier (« o » ou « g ») devient une pièce du modèle et garde son nom.
/// </summary>
internal sealed class ObjImport
{
    public const string Folder = "Import";
    private const float Scale = 100f;
    private const int MaxVertices = 65535;

    private sealed class Material
    {
        public string? Texture;
        public Vector3 Color = Vector3.One;
        public float Opacity = 1;
    }

    private readonly List<Vector3> _v = new();
    private readonly List<Vector2> _vt = new();
    private readonly List<Vector3> _vn = new();
    // pièces dans l'ordre du fichier : nom → matériau → coins des triangles (sommet, texture, normale ; -1 : absent)
    private readonly List<(string name, List<(string mat, List<(int v, int t, int n)> corners)> groups)> _parts = new();
    private readonly Dictionary<string, Material> _materials = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = new();

    public IReadOnlyList<string> Warnings => _warnings;
    public int PartCount { get; private set; }
    public int TextureCount { get; private set; }
    public int TriangleCount { get; private set; }
    public int MaskCount { get; private set; }

    /// <summary>
    /// Couvercles noirs fabriqués par l'import (Write) : un par pièce « …_ROOM1 », « …_ROOM2 »… du fichier qui n'a pas déjà
    /// le sien (« …_ROOM1_MASK », fait dans Blender) ; sans pièce nommée, un seul qui couvre tout l'objet.
    /// </summary>
    public bool AutoMasks { get; set; }

    /// <summary>Nom de fichier sûr pour le jeu (lettres, chiffres, _ et -).</summary>
    public static string Safe(string s)
    {
        StringBuilder b = new StringBuilder();
        foreach (char c in s.Trim())
            b.Append(c < 128 && (char.IsLetterOrDigit(c) || c == '_' || c == '-') ? c : '_');
        return b.ToString().Trim('_');
    }

    /// <summary>Chemin de la classe (comme dans le niveau) d'un objet importé.</summary>
    public static string ClassPath(string name, bool usable) =>
        (usable ? "design\\placeable\\functional\\" : "design\\placeable\\") + Folder.ToLowerInvariant() + "\\" + name.ToLowerInvariant();

    /// <summary>Le nom est-il déjà pris par un objet importé (modèle ou classe) ?</summary>
    public static bool Exists(string dataDir, string name) =>
        File.Exists(Path.Combine(dataDir, "Gfx", "Placeable", Folder, name + ".sg"))
        || File.Exists(Path.Combine(dataDir, "Design", "Placeable", Folder, name + ".class"))
        || File.Exists(Path.Combine(dataDir, "Design", "Placeable", "Functional", Folder, name + ".class"));

    public void Read(string objPath)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        string dir = Path.GetDirectoryName(objPath) ?? ".";
        string part = Path.GetFileNameWithoutExtension(objPath);
        string mat = "";
        List<(string mat, List<(int, int, int)> corners)>? groups = null;
        List<(int, int, int)>? cur = null;
        void Use()
        {
            if (groups == null)
            {
                groups = new();
                _parts.Add((part, groups));
            }
            int k = groups.FindIndex(g => g.mat == mat);
            if (k < 0)
            {
                groups.Add((mat, new()));
                k = groups.Count - 1;
            }
            cur = groups[k].corners;
        }
        foreach (string raw in File.ReadLines(objPath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            string[] t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (t[0])
            {
                case "v" when t.Length >= 4:
                    _v.Add(new Vector3(F(t[1]), F(t[2]), F(t[3])));
                    break;
                case "vt" when t.Length >= 3:
                    _vt.Add(new Vector2(F(t[1]), F(t[2])));
                    break;
                case "vn" when t.Length >= 4:
                    _vn.Add(new Vector3(F(t[1]), F(t[2]), F(t[3])));
                    break;
                case "o":
                case "g":
                    string n = t.Length > 1 ? string.Join("_", t[1..]) : "piece";
                    // « g » sous un « o » du même nom (export de Blender avec les deux) : la même pièce
                    if (groups != null && n == part)
                        break;
                    part = n;
                    groups = null;
                    cur = null;
                    break;
                case "usemtl":
                    mat = t.Length > 1 ? string.Join(" ", t[1..]) : "";
                    cur = null;
                    break;
                case "mtllib":
                    ReadMtl(Path.Combine(dir, line[6..].Trim()));
                    break;
                case "f" when t.Length >= 4:
                    if (cur == null)
                        Use();
                    List<(int, int, int)> poly = new List<(int, int, int)>();
                    for (int i = 1; i < t.Length; i++)
                    {
                        string[] c = t[i].Split('/');
                        int vi = Index(c[0], _v.Count);
                        int ti = c.Length > 1 && c[1].Length > 0 ? Index(c[1], _vt.Count) : -1;
                        int ni = c.Length > 2 && c[2].Length > 0 ? Index(c[2], _vn.Count) : -1;
                        poly.Add((vi, ti, ni));
                    }
                    // polygone en éventail de triangles
                    for (int i = 1; i + 1 < poly.Count; i++)
                    {
                        cur!.Add(poly[0]);
                        cur.Add(poly[i]);
                        cur.Add(poly[i + 1]);
                    }
                    break;
            }
        }
        float F(string s) => float.TryParse(s, NumberStyles.Float, inv, out float f) ? f : 0;
    }

    // index OBJ (à partir de 1, ou négatif : depuis la fin) → index à partir de 0
    private static int Index(string s, int count)
    {
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
            return -1;
        return i > 0 ? i - 1 : i < 0 ? count + i : -1;
    }

    private void ReadMtl(string path)
    {
        if (!File.Exists(path))
        {
            _warnings.Add($"matériaux introuvables : {Path.GetFileName(path)}");
            return;
        }
        string dir = Path.GetDirectoryName(path) ?? ".";
        Material? m = null;
        CultureInfo inv = CultureInfo.InvariantCulture;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            string[] t = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string key = t[0].ToLowerInvariant();
            if (key == "newmtl")
            {
                m = new Material();
                _materials[t.Length > 1 ? string.Join(" ", t[1..]) : ""] = m;
            }
            else if (m == null)
                continue;
            else if (key == "kd" && t.Length >= 4)
                m.Color = new Vector3(float.Parse(t[1], inv), float.Parse(t[2], inv), float.Parse(t[3], inv));
            else if (key == "d" && t.Length >= 2)
                m.Opacity = float.Parse(t[1], inv);
            else if (key == "map_kd" && t.Length >= 2)
            {
                // les options (« -s 1 1 1 », « -bm 1 »…) précèdent le nom du fichier : on garde la fin de la ligne
                string file = TexturePath(line[t[0].Length..].Trim());
                m.Texture = Path.IsPathRooted(file) ? file : Path.Combine(dir, file);
            }
        }
    }

    private static string TexturePath(string rest)
    {
        string[] t = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int i = 0;
        while (i < t.Length && t[i].StartsWith('-'))
        {
            int args = t[i] switch { "-s" or "-o" or "-t" => 3, "-mm" => 2, _ => 1 };
            i += 1 + args;
        }
        return i < t.Length ? string.Join(" ", t[i..]).Replace('\\', Path.DirectorySeparatorChar) : rest;
    }

    /// <summary>
    /// Écrit le modèle, ses textures et sa classe dans le dossier Data du jeu. <paramref name="template"/> : un matériau
    /// du jeu à une texture, recopié pour chaque matériau (seuls ses noms changent). Rend le chemin de la classe.
    /// </summary>
    public string Write(string dataDir, string name, bool usable, byte[] template)
    {
        string gfx = Path.Combine(dataDir, "Gfx", "Placeable", Folder);
        string texDir = Path.Combine(dataDir, "Gfx", "Textures", Folder);
        Directory.CreateDirectory(Path.Combine(gfx, "Shaders"));
        Directory.CreateDirectory(texDir);
        Dictionary<string, string> gameMat = new Dictionary<string, string>(StringComparer.Ordinal);
        List<(string node, Matrix4x4 world)> nodes = new List<(string, Matrix4x4)>();
        HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string partName, List<(string mat, List<(int v, int t, int n)> corners)> groups) in _parts)
        {
            if (groups.All(g => g.corners.Count == 0))
                continue;
            string baseNode = name + "_" + (Safe(partName) is { Length: > 0 } sp ? sp : "piece");
            int chunk = 0;
            Fd3Mesh? mesh = null;
            Dictionary<(int, int, int), ushort> map = new();
            void Flush()
            {
                if (mesh == null || mesh.Indices.Count == 0)
                    return;
                string node = mesh.Name;
                File.WriteAllBytes(Path.Combine(gfx, node + ".mesh"), Fd3Writer.MeshFile(mesh));
                File.WriteAllBytes(Path.Combine(gfx, node + ".meshdata"), Fd3Writer.MeshData(mesh));
                nodes.Add((node, Matrix4x4.Identity));
                PartCount++;
            }
            void Start()
            {
                Flush();
                string node = chunk == 0 ? baseNode : baseNode + "_" + chunk;
                for (int k = 2; !used.Add(node); k++)
                    node = baseNode + "_" + k;
                chunk++;
                mesh = new Fd3Mesh { Name = node };
                map.Clear();
            }
            Start();
            foreach ((string mat, List<(int v, int t, int n)> corners) in groups)
            {
                if (corners.Count == 0)
                    continue;
                if (!gameMat.TryGetValue(mat, out string? gm))
                    gameMat[mat] = gm = WriteMaterial(mat, name, gfx, texDir, template);
                int start = mesh!.Indices.Count;
                for (int i = 0; i + 2 < corners.Count; i += 3)
                {
                    // le maillage est plein (index sur 16 bits) : la suite va dans une autre pièce du même nom
                    if (map.Count + 3 > MaxVertices)
                    {
                        if (mesh.Indices.Count > start)
                            mesh.Groups.Add((gm, start, mesh.Indices.Count - start));
                        Start();
                        start = 0;
                    }
                    Vector3 faceN = FaceNormal(corners[i].v, corners[i + 1].v, corners[i + 2].v);
                    for (int c = 0; c < 3; c++)
                        mesh.Indices.Add(Vertex(mesh, map, corners[i + c], faceN));
                    TriangleCount++;
                }
                if (mesh.Indices.Count > start)
                    mesh.Groups.Add((gm, start, mesh.Indices.Count - start));
            }
            Flush();
        }
        if (nodes.Count == 0)
            throw new InvalidDataException("aucun triangle dans le fichier");
        if (AutoMasks)
            WriteMasks(name, gfx, texDir, template, nodes, used);
        File.WriteAllBytes(Path.Combine(gfx, name + ".sg"), Fd3Writer.Scene(nodes));
        return WriteClass(dataDir, name, usable);
    }

    private static readonly System.Text.RegularExpressions.Regex RoomRx = new(@"_ROOM\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // point du fichier (mètres, Y vers le haut) → repère du jeu (centimètres, Z vers le haut)
    private Vector3 Game(int v)
    {
        Vector3 p = v >= 0 && v < _v.Count ? _v[v] : Vector3.Zero;
        return new Vector3(p.X * Scale, -p.Z * Scale, p.Y * Scale);
    }

    /// <summary>
    /// Couvercles noirs (« &lt;pièce&gt;_MASK ») : le moteur les pose sur les pièces que l'on n'a pas encore ouvertes, comme
    /// ceux des bâtiments du jeu, et les retire quand un pompier y arrive. Couvercle d'une pièce : son sol (les faces
    /// tournées vers le haut, en bas de la pièce) remonté à la hauteur du haut de la pièce ; sans sol reconnu, le rectangle
    /// qu'elle occupe. Sans pièce nommée : un rectangle sur tout l'objet (hors toit), à sa hauteur.
    /// </summary>
    private void WriteMasks(string name, string gfx, string texDir, byte[] template, List<(string node, Matrix4x4 world)> nodes, HashSet<string> used)
    {
        HashSet<string> own = new HashSet<string>(_parts.Where(p => p.name.Contains("_MASK", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.name[..p.name.LastIndexOf("_MASK", StringComparison.OrdinalIgnoreCase)]), StringComparer.OrdinalIgnoreCase);
        List<(string room, List<Vector3> tris)> rooms = new();
        foreach ((string partName, List<(string mat, List<(int v, int t, int n)> corners)> groups) in _parts)
            if (RoomRx.IsMatch(partName) && !own.Contains(partName))
                rooms.Add((partName, groups.SelectMany(g => g.corners).Select(c => Game(c.v)).ToList()));
        if (rooms.Count == 0)
        {
            if (own.Count > 0)
                return;   // couvercles faits dans Blender, et pas d'autre pièce
            List<Vector3> all = _parts.Where(p => !p.name.Contains("ROOF", StringComparison.OrdinalIgnoreCase) && !p.name.Contains("_MASK", StringComparison.OrdinalIgnoreCase))
                .SelectMany(p => p.groups).SelectMany(g => g.corners).Select(c => Game(c.v)).ToList();
            if (all.Count == 0)
                return;
            rooms.Add(("ROOM1", all));
        }
        _materials["\u0001couvercle"] = new Material { Color = Vector3.Zero };
        string black = WriteMaterial("\u0001couvercle", name, gfx, texDir, template);
        foreach ((string room, List<Vector3> pts) in rooms)
        {
            if (pts.Count < 3)
                continue;
            float zMin = pts.Min(p => p.Z), zMax = pts.Max(p => p.Z);
            // le couvercle est posé en haut de la pièce (au moins 2,5 m au-dessus de son sol : une pièce faite d'un seul sol)
            float top = MathF.Max(zMax, zMin + 250);
            List<Vector3> floor = new List<Vector3>();
            for (int i = 0; i + 2 < pts.Count; i += 3)
            {
                Vector3 a = pts[i], b = pts[i + 1], c = pts[i + 2];
                Vector3 n = Vector3.Cross(b - a, c - a);
                if (n.LengthSquared() < 1e-6f)
                    continue;
                n = Vector3.Normalize(n);
                if (MathF.Abs(n.Z) > 0.7f && MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) < zMin + 40)
                {
                    floor.Add(a);
                    // vu de dessus dans le sens trigonométrique (face visible d'en haut)
                    if (n.Z > 0)
                    {
                        floor.Add(b);
                        floor.Add(c);
                    }
                    else
                    {
                        floor.Add(c);
                        floor.Add(b);
                    }
                }
            }
            if (floor.Count == 0 || floor.Count > MaxVertices)
            {
                float x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
                floor = new List<Vector3> { new(x0, y0, 0), new(x1, y0, 0), new(x1, y1, 0), new(x0, y0, 0), new(x1, y1, 0), new(x0, y1, 0) };
            }
            string baseNode = name + "_" + Safe(room) + "_MASK";
            string node = baseNode;
            for (int k = 2; !used.Add(node); k++)
                node = baseNode + "_" + k;
            Fd3Mesh mesh = new Fd3Mesh { Name = node };
            foreach (Vector3 p in floor)
            {
                mesh.Positions.Add(p.X);
                mesh.Positions.Add(p.Y);
                mesh.Positions.Add(top);
                mesh.Normals.Add(0);
                mesh.Normals.Add(0);
                mesh.Normals.Add(1);
                mesh.Uv.Add(0);
                mesh.Uv.Add(0);
                mesh.Indices.Add((ushort)(mesh.VertexCount - 1));
            }
            mesh.Groups.Add((black, 0, mesh.Indices.Count));
            File.WriteAllBytes(Path.Combine(gfx, node + ".mesh"), Fd3Writer.MeshFile(mesh));
            File.WriteAllBytes(Path.Combine(gfx, node + ".meshdata"), Fd3Writer.MeshData(mesh));
            nodes.Add((node, Matrix4x4.Identity));
            MaskCount++;
        }
    }

    private ushort Vertex(Fd3Mesh mesh, Dictionary<(int, int, int), ushort> map, (int v, int t, int n) c, Vector3 faceN)
    {
        // sans normale dans le fichier : celle du triangle (chaque triangle a alors ses propres sommets)
        (int, int, int) key = c.n >= 0 ? c : (c.v, c.t, -2 - mesh.Indices.Count);
        if (map.TryGetValue(key, out ushort idx))
            return idx;
        Vector3 p = c.v >= 0 && c.v < _v.Count ? _v[c.v] : Vector3.Zero;
        mesh.Positions.Add(p.X * Scale);
        mesh.Positions.Add(-p.Z * Scale);
        mesh.Positions.Add(p.Y * Scale);
        Vector3 n = c.n >= 0 && c.n < _vn.Count ? _vn[c.n] : faceN;
        if (n.LengthSquared() > 1e-12f)
            n = Vector3.Normalize(n);
        mesh.Normals.Add(n.X);
        mesh.Normals.Add(-n.Z);
        mesh.Normals.Add(n.Y);
        Vector2 uv = c.t >= 0 && c.t < _vt.Count ? _vt[c.t] : Vector2.Zero;
        mesh.Uv.Add(uv.X);
        mesh.Uv.Add(1 - uv.Y);
        idx = (ushort)(mesh.VertexCount - 1);
        map[key] = idx;
        return idx;
    }

    private Vector3 FaceNormal(int a, int b, int c)
    {
        if (a < 0 || b < 0 || c < 0 || a >= _v.Count || b >= _v.Count || c >= _v.Count)
            return Vector3.UnitY;
        Vector3 n = Vector3.Cross(_v[b] - _v[a], _v[c] - _v[a]);
        return n.LengthSquared() > 1e-20f ? Vector3.Normalize(n) : Vector3.UnitY;
    }

    // matériau du jeu (Shaders\<nom>_<matériau>.stdmat) et sa texture (Gfx\Textures\Import\…dds) ; rend son nom
    private string WriteMaterial(string mat, string name, string gfx, string texDir, byte[] template)
    {
        string gm = name + "_" + (Safe(mat) is { Length: > 0 } s ? s : "materiau");
        _materials.TryGetValue(mat, out Material? m);
        (int w, int h, byte[] rgba)? img = null;
        string texBase = gm;
        if (m?.Texture is { } file)
        {
            if (File.Exists(file))
            {
                byte[] bytes = File.ReadAllBytes(file);
                img = PngDecode.Decode(bytes) ?? ImageCodec.Decode(bytes) ?? ImageDecode.Decode(bytes);
                if (img == null)
                    _warnings.Add($"texture illisible (format) : {Path.GetFileName(file)}");
                else
                    texBase = name + "_" + Safe(Path.GetFileNameWithoutExtension(file));
            }
            else
                _warnings.Add($"texture introuvable : {Path.GetFileName(file)}");
        }
        if (img == null)
        {
            // pas de texture : la couleur du matériau (Kd)
            Vector3 c = m?.Color ?? new Vector3(0.8f);
            byte[] px = new byte[4 * 4 * 4];
            for (int i = 0; i < 16; i++)
            {
                px[i * 4] = (byte)Math.Clamp(c.X * 255, 0, 255);
                px[i * 4 + 1] = (byte)Math.Clamp(c.Y * 255, 0, 255);
                px[i * 4 + 2] = (byte)Math.Clamp(c.Z * 255, 0, 255);
                px[i * 4 + 3] = (byte)Math.Clamp((m?.Opacity ?? 1) * 255, 0, 255);
            }
            img = (4, 4, px);
        }
        (int iw, int ih, byte[] data) = img.Value;
        // transparence : le jeu ne découpe que les textures dont le nom le dit (« …_opa »)
        bool alpha = false;
        for (int i = 3; i < data.Length; i += 4)
            if (data[i] < 250)
            {
                alpha = true;
                break;
            }
        string tex = texBase + (alpha ? "_opa" : "");
        string texFile = Path.Combine(texDir, tex + ".dds");
        if (!File.Exists(texFile) || !_written.Contains(tex))
        {
            File.WriteAllBytes(texFile, ImageCodec.EncodeDds(iw, ih, data));
            if (_written.Add(tex))
                TextureCount++;
        }
        File.WriteAllBytes(Path.Combine(gfx, "Shaders", gm + ".stdmat"), Fd3Writer.Stdmat(template, gm, tex + ".dds"));
        return gm;
    }

    private readonly HashSet<string> _written = new(StringComparer.OrdinalIgnoreCase);

    private static string WriteClass(string dataDir, string name, bool usable)
    {
        string dir = usable
            ? Path.Combine(dataDir, "Design", "Placeable", "Functional", Folder)
            : Path.Combine(dataDir, "Design", "Placeable", Folder);
        Directory.CreateDirectory(dir);
        // l'autre sorte d'objet du même nom (import précédent) : retirée, une seule classe par modèle
        string other = !usable
            ? Path.Combine(dataDir, "Design", "Placeable", "Functional", Folder, name + ".class")
            : Path.Combine(dataDir, "Design", "Placeable", Folder, name + ".class");
        if (File.Exists(other))
            File.Delete(other);
        string cls = usable
            ? $"""
              [#INCLUDE]
              FILE0 = "..\Useable\base_useable.class"

              [DISPLAY]
              MODEL = {Folder}\{name}

              [DAMAGE]
              USEDAMAGE = 0
              CARBONISABLE = 0

              [CAPACITIES_ACTIONS]
              COULDBEUSE = 1

              [EXPLOSION]
              CANEXPLODE = 0

              [LOGICAL_STATE]
              S01_USER_ANIM = ItemChar\ic_pick_up_down
              S01__NAME = use

              """
            : $"""
              [#INCLUDE]
              FILE0 = "..\base_placeable.class"

              [DISPLAY]
              MODEL = {Folder}\{name}

              """;
        File.WriteAllBytes(Path.Combine(dir, name + ".class"), Encoding.Latin1.GetBytes(cls.Replace("\r\n", "\n").Replace("\n", "\r\n")));
        return ClassPath(name, usable);
    }
}
