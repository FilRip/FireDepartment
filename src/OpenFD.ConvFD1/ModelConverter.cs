using System.Numerics;
using System.Text.RegularExpressions;

namespace OpenFD.ConvFD1;

/// <summary>
/// Modèle FD1 (.SKIN + .SKEL) → maillages du moteur, dans le repère du modèle et en cm (FD1 : sommets en décimètres
/// sous la racine d'échelle 0,01, monde en unités de 10 m). Un matériau par texture ; au-delà de 65 000 sommets
/// (indices sur 16 bits), le modèle est coupé en plusieurs maillages.
/// </summary>
public sealed class ModelConverter
{
    /// <summary>Sommets FD1 (sous la racine) → cm.</summary>
    public const float SkinToCm = 10f;

    private const int MaxVertices = 65000;

    /// <summary>Textures utilisées (nom de texture dans le moteur → chemin de l'image FD1) ; couleurs unies (nom → ARGB).</summary>
    public Dictionary<string, string> Textures { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, uint> Colors { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Textures à rendre opaques (transparence douce que le moteur ne sait pas mélanger).</summary>
    public HashSet<string> Opaque { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Report { get; } = new();

    private readonly Fd1Data _fd1;
    private readonly string _mission;

    public ModelConverter(Fd1Data fd1, string mission)
    {
        _fd1 = fd1;
        _mission = mission;
    }

    public static string Safe(string s) => Regex.Replace(s, "[^A-Za-z0-9_]", "_");

    /// <summary>Maillages du modèle (noms « &lt;prefix&gt;_0 », « _1 »…) ; liste vide si le modèle est absent ou illisible.</summary>
    public List<Fd3Mesh> Convert(string model, string prefix)
    {
        string? skinPath = _fd1.Skin(model, _mission);
        if (skinPath == null)
        {
            Report.Add($"modèle absent : {model}");
            return new List<Fd3Mesh>();
        }
        List<Fd1SubMesh> subs;
        Fd1Skel? skel = null;
        try
        {
            subs = Fd1Skin.Load(File.ReadAllBytes(skinPath));
            string skelPath = Path.ChangeExtension(skinPath, ".SKEL");
            if (!File.Exists(skelPath))
                skelPath = Path.ChangeExtension(skinPath, ".skel");
            if (File.Exists(skelPath))
                skel = Fd1Skel.Load(File.ReadAllBytes(skelPath));
        }
        catch (Exception e)
        {
            Report.Add($"modèle illisible : {model} ({e.Message})");
            return new List<Fd3Mesh>();
        }
        List<Fd3Mesh> res = new List<Fd3Mesh>();
        Fd3Mesh cur = new Fd3Mesh { Name = prefix + "_0" };
        res.Add(cur);
        // triangles regroupés par matériau du moteur
        Dictionary<string, List<(Fd1SubMesh sub, Matrix4x4 m, int tri)>> byMat = new Dictionary<string, List<(Fd1SubMesh, Matrix4x4, int)>>();
        List<string> order = new List<string>();
        foreach (Fd1SubMesh s in subs)
        {
            Matrix4x4 m = (skel?.BelowRoot(s.Name) ?? Matrix4x4.Identity) * Matrix4x4.CreateScale(SkinToCm);
            for (int t = 0; t < s.Indices.Length / 3; t++)
            {
                int mi = t < s.FaceMaterial.Length ? s.FaceMaterial[t] : 0;
                string? mat = MaterialName(s.Materials.Count > mi ? s.Materials[mi] : null);
                if (mat == null)
                    continue;
                if (!byMat.TryGetValue(mat, out List<(Fd1SubMesh, Matrix4x4, int)>? l))
                {
                    l = new List<(Fd1SubMesh, Matrix4x4, int)>();
                    byMat[mat] = l;
                    order.Add(mat);
                }
                l.Add((s, m, t));
            }
        }
        foreach (string mat in order)
        {
            Dictionary<(Fd1SubMesh, uint), ushort> remap = new Dictionary<(Fd1SubMesh, uint), ushort>();
            int start = cur.Indices.Count;
            foreach ((Fd1SubMesh s, Matrix4x4 m, int t) in byMat[mat])
            {
                if (cur.VertexCount + 3 > MaxVertices)
                {
                    if (cur.Indices.Count > start)
                        cur.Groups.Add((mat, start, cur.Indices.Count - start));
                    cur = new Fd3Mesh { Name = prefix + "_" + res.Count };
                    res.Add(cur);
                    remap.Clear();
                    start = 0;
                }
                for (int k = 0; k < 3; k++)
                {
                    uint vi = s.Indices[3 * t + k];
                    if (!remap.TryGetValue((s, vi), out ushort ni))
                    {
                        ni = (ushort)cur.VertexCount;
                        remap[(s, vi)] = ni;
                        Vector3 p = Vector3.Transform(new Vector3(s.Positions[3 * vi], s.Positions[3 * vi + 1], s.Positions[3 * vi + 2]), m);
                        cur.Positions.Add(p.X);
                        cur.Positions.Add(p.Y);
                        cur.Positions.Add(p.Z);
                        Vector3 nrm = s.Normals != null ? new Vector3(s.Normals[3 * vi], s.Normals[3 * vi + 1], s.Normals[3 * vi + 2]) : Vector3.UnitZ;
                        nrm = Vector3.TransformNormal(nrm, m);
                        nrm = nrm.LengthSquared() > 1e-12f ? Vector3.Normalize(nrm) : Vector3.UnitZ;
                        cur.Normals.Add(nrm.X);
                        cur.Normals.Add(nrm.Y);
                        cur.Normals.Add(nrm.Z);
                        cur.Uv.Add(s.Uv != null ? s.Uv[2 * vi] : 0f);
                        cur.Uv.Add(s.Uv != null ? s.Uv[2 * vi + 1] : 0f);
                    }
                    cur.Indices.Add(ni);
                }
            }
            if (cur.Indices.Count > start)
                cur.Groups.Add((mat, start, cur.Indices.Count - start));
        }
        res.RemoveAll(x => x.Indices.Count == 0);
        foreach (Fd3Mesh m in res.ToList())
            AddBackFaces(m);
        return res;
    }

    /// <summary>
    /// Murs vus des deux côtés : FD1 dessine les deux faces, le moteur seulement celle de devant. Les cloisons des pièces
    /// n'ont souvent qu'une face, tournée vers l'intérieur : vues du dehors, elles disparaissaient. Chaque triangle à peu près
    /// vertical reçoit son double à l'envers (normale retournée), tant que le maillage reste sous la limite des 16 bits.
    /// </summary>
    private static void AddBackFaces(Fd3Mesh m)
    {
        List<(string material, int start, int count)> groups = new List<(string, int, int)>();
        List<ushort> indices = new List<ushort>();
        foreach ((string mat, int start, int count) in m.Groups)
        {
            int s0 = indices.Count;
            for (int i = start; i < start + count; i++)
                indices.Add(m.Indices[i]);
            for (int t = start; t + 2 < start + count; t += 3)
            {
                int a = m.Indices[t];
                int b = m.Indices[t + 1];
                int c = m.Indices[t + 2];
                Vector3 pa = new Vector3(m.Positions[3 * a], m.Positions[3 * a + 1], m.Positions[3 * a + 2]);
                Vector3 pb = new Vector3(m.Positions[3 * b], m.Positions[3 * b + 1], m.Positions[3 * b + 2]);
                Vector3 pc = new Vector3(m.Positions[3 * c], m.Positions[3 * c + 1], m.Positions[3 * c + 2]);
                Vector3 n = Vector3.Cross(pb - pa, pc - pa);
                if (n.LengthSquared() < 1e-6f || MathF.Abs(Vector3.Normalize(n).Z) > 0.7f || m.VertexCount + 3 > MaxVertices)
                    continue;
                foreach (int v in new[] { a, c, b })
                {
                    indices.Add((ushort)m.VertexCount);
                    m.Positions.AddRange(new[] { m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2] });
                    m.Normals.AddRange(new[] { -m.Normals[3 * v], -m.Normals[3 * v + 1], -m.Normals[3 * v + 2] });
                    m.Uv.AddRange(new[] { m.Uv[2 * v], m.Uv[2 * v + 1] });
                }
            }
            groups.Add((mat, s0, indices.Count - s0));
        }
        m.Indices = indices;
        m.Groups = groups;
    }

    /// <summary>
    /// Sol du niveau : une dalle carrée de 10 m par texture « …_Xnn_Ynn » (x, y : numéro de la dalle), à la hauteur 0.
    /// </summary>
    public Fd3Mesh Ground(List<(int x, int y, string texture)> tiles, string name)
    {
        Fd3Mesh m = new Fd3Mesh { Name = name };
        const float size = MissionTile;
        foreach ((int x, int y, string tex) in tiles)
        {
            string mat = MaterialName(new Fd1Material { Name = tex, Texture = tex }) ?? "fd1c_808080";
            int start = m.Indices.Count;
            int b = m.VertexCount;
            float x0 = x * size;
            float y0 = y * size;
            (float px, float py, float u, float v)[] corners = { (x0, y0, 0, 1), (x0 + size, y0, 1, 1), (x0 + size, y0 + size, 1, 0), (x0, y0 + size, 0, 0) };
            foreach ((float px, float py, float u, float v) in corners)
            {
                m.Positions.AddRange(new[] { px, py, 0f });
                m.Normals.AddRange(new[] { 0f, 0f, 1f });
                m.Uv.AddRange(new[] { u, v });
            }
            foreach (int i in new[] { 0, 1, 2, 2, 3, 0 })
                m.Indices.Add((ushort)(b + i));
            m.Groups.Add((mat, start, 6));
        }
        return m;
    }

    /// <summary>
    /// Morceaux séparés d'un maillage (triangles reliés par des sommets au même endroit) : chacun devient un maillage.
    /// Sert aux couvercles des petites pièces (Entrepot : « PIECES_TOP » couvre toutes les pièces d'un seul modèle),
    /// pour que chaque pièce garde son toit jusqu'à ce qu'un pompier passe dessous.
    /// </summary>
    public static List<Fd3Mesh> Islands(Fd3Mesh m)
    {
        int nt = m.Indices.Count / 3;
        int[] parent = Enumerable.Range(0, m.VertexCount).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }
        Dictionary<(int, int, int), int> at = new Dictionary<(int, int, int), int>();
        for (int i = 0; i < m.VertexCount; i++)
        {
            (int, int, int) key = ((int)MathF.Round(m.Positions[3 * i]), (int)MathF.Round(m.Positions[3 * i + 1]), (int)MathF.Round(m.Positions[3 * i + 2]));
            if (at.TryGetValue(key, out int j))
                parent[Find(i)] = Find(j);
            else
                at[key] = i;
        }
        for (int t = 0; t < nt; t++)
        {
            parent[Find(m.Indices[3 * t + 1])] = Find(m.Indices[3 * t]);
            parent[Find(m.Indices[3 * t + 2])] = Find(m.Indices[3 * t]);
        }
        string[] matOf = new string[nt];
        foreach ((string mat, int start, int count) in m.Groups)
            for (int t = start / 3; t < (start + count) / 3; t++)
                matOf[t] = mat;
        Dictionary<int, Fd3Mesh> parts = new Dictionary<int, Fd3Mesh>();
        Dictionary<(int, int), ushort> remap = new Dictionary<(int, int), ushort>();
        for (int t = 0; t < nt; t++)
        {
            int root = Find(m.Indices[3 * t]);
            if (!parts.TryGetValue(root, out Fd3Mesh? part))
            {
                part = new Fd3Mesh { Name = m.Name + "_" + parts.Count };
                parts[root] = part;
            }
            string mat = matOf[t] ?? "";
            if (part.Groups.Count == 0 || part.Groups[^1].material != mat)
                part.Groups.Add((mat, part.Indices.Count, 0));
            for (int k = 0; k < 3; k++)
            {
                int vi = m.Indices[3 * t + k];
                if (!remap.TryGetValue((root, vi), out ushort ni))
                {
                    ni = (ushort)part.VertexCount;
                    remap[(root, vi)] = ni;
                    part.Positions.AddRange(new[] { m.Positions[3 * vi], m.Positions[3 * vi + 1], m.Positions[3 * vi + 2] });
                    part.Normals.AddRange(new[] { m.Normals[3 * vi], m.Normals[3 * vi + 1], m.Normals[3 * vi + 2] });
                    part.Uv.AddRange(new[] { m.Uv[2 * vi], m.Uv[2 * vi + 1] });
                }
                part.Indices.Add(ni);
            }
            (string gm, int gs, int gc) = part.Groups[^1];
            part.Groups[^1] = (gm, gs, gc + 3);
        }
        return parts.Values.ToList();
    }

    /// <summary>Transparence d'une image : « none », « cut » (alpha presque tout à 0 ou 255) ou « soft » (dégradés).</summary>
    private static string AlphaKind(string img)
    {
        if (img.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) || img.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            return "none";
        (int w, int h, byte[] rgba)? im = ImageCodec.Decode(File.ReadAllBytes(img));
        if (im is not { } v)
            return "none";
        int n = v.w * v.h;
        int clear = 0;
        int mid = 0;
        for (int i = 0; i < n; i++)
        {
            byte a = v.rgba[4 * i + 3];
            if (a < 32)
                clear++;
            else if (a < 224)
                mid++;
        }
        if (clear + mid < n / 100)
            return "none";
        return mid > n / 5 ? "soft" : "cut";
    }

    /// <summary>Côté d'une dalle de sol FD1, en cm (1 unité du monde = 10 m).</summary>
    public const float MissionTile = 1000f;

    // texture → nom dans le moteur (null : texture d'ombre à effacer)
    private readonly Dictionary<string, string?> _texName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Matériau du moteur pour un matériau FD1 : « fd1_&lt;texture&gt; », ou une couleur unie « fd1c_RRGGBB ».
    /// Texture avec transparence : nette (grillage, feuillage) → nom en « …_opa » (détourage par le moteur) ;
    /// douce : le moteur ne sait pas la mélanger ; ombre posée au sol (« ent_ombrage_sol ») → null, ces triangles sont
    /// retirés ; autre (vitres teintées) → rendue opaque.
    /// </summary>
    private string? MaterialName(Fd1Material? m)
    {
        if (m != null && m.Texture.Length > 0)
        {
            if (!_texName.TryGetValue(m.Texture, out string? tex))
            {
                tex = "fd1_" + Safe(Path.GetFileNameWithoutExtension(m.Texture.Replace('\\', '/'))).ToLowerInvariant();
                string? img = _fd1.Image(m.Texture);
                if (img == null)
                {
                    Report.Add($"texture absente : {m.Texture}");
                    tex = "";
                }
                else
                {
                    string alpha = AlphaKind(img);
                    bool shadow = System.Text.RegularExpressions.Regex.IsMatch(m.Texture, "ombr|shadow", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (alpha == "soft" && shadow)
                        tex = null;
                    else if (alpha == "soft")
                    {
                        // vitre teintée (« fenetre02.tga » : les murs vitrés du bureau du contremaître) : opaque, comme une
                        // vitre sombre, plutôt que retirée
                        Opaque.Add(tex);
                        Textures[tex] = img;
                    }
                    else
                    {
                        if (alpha == "cut")
                            tex += "_opa";
                        Textures[tex] = img;
                    }
                }
                _texName[m.Texture] = tex;
            }
            if (tex == null)
                return null;
            if (tex.Length > 0)
                return tex;
        }
        uint c = m?.Diffuse ?? 0xFFC0C0C0;
        string col = $"fd1c_{(c >> 16) & 0xFF:x2}{(c >> 8) & 0xFF:x2}{c & 0xFF:x2}";
        Colors[col] = c;
        return col;
    }
}
