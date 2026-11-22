using System.Globalization;
using System.Numerics;
using System.Text;
using OpenFD.Game;

namespace OpenFD.MG;

/// <summary>
/// Export vers Wavefront OBJ (+ .mtl et textures en .png), pour Blender : chaque pièce du modèle est un objet séparé
/// qui garde son nom (« Bank_fasad_ROOF », « Bank_ROOM2 »…). Le jeu compte en centimètres avec Z vers le haut ;
/// le fichier est en mètres avec Y vers le haut (réglages par défaut de l'import OBJ de Blender).
/// </summary>
internal sealed class ObjExport
{
    private const float Scale = 0.01f;
    private readonly List<(string name, SceneAssets.MeshAsset mesh, Matrix4x4 world)> _parts = new();

    public int Count => _parts.Count;

    public void Add(string name, SceneAssets.MeshAsset mesh, Matrix4x4 world) => _parts.Add((name, mesh, world));

    /// <summary>
    /// Écrit « nom.obj », « nom.mtl » et les textures (dossier « nom_textures ») à côté. <paramref name="texture"/> : octets
    /// d'une texture du jeu d'après son nom (null : absente). Rend le nombre de textures écrites.
    /// </summary>
    public int Write(string objPath, Func<string, byte[]?> texture)
    {
        string dir = Path.GetDirectoryName(objPath) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(objPath);
        string texDir = baseName + "_textures";
        Directory.CreateDirectory(Path.Combine(dir, texDir));
        CultureInfo inv = CultureInfo.InvariantCulture;
        StringBuilder obj = new StringBuilder();
        StringBuilder mtl = new StringBuilder();
        obj.AppendLine("# OpenFD : export d'un modèle de Fire Department 3 (mètres, Y vers le haut)");
        obj.AppendLine($"mtllib {baseName}.mtl");
        Dictionary<string, string> materials = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string?> pngs = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        int written = 0;
        int vBase = 1;
        HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, SceneAssets.MeshAsset asset, Matrix4x4 world) in _parts)
        {
            OpenFD.Formats.Mesh m = asset.Mesh;
            string objName = Clean(name);
            for (int n = 2; !names.Add(objName); n++)
                objName = Clean(name) + "_" + n;
            obj.AppendLine($"o {objName}");
            for (int i = 0; i < m.VertexCount; i++)
            {
                Vector3 p = Vector3.Transform(new Vector3(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]), world);
                obj.AppendLine(string.Format(inv, "v {0:0.#####} {1:0.#####} {2:0.#####}", p.X * Scale, p.Z * Scale, -p.Y * Scale));
            }
            bool hasUv = asset.Uv0.Length >= m.VertexCount * 2;
            if (hasUv)
                for (int i = 0; i < m.VertexCount; i++)
                    obj.AppendLine(string.Format(inv, "vt {0:0.######} {1:0.######}", asset.Uv0[i * 2], 1 - asset.Uv0[i * 2 + 1]));
            bool hasN = m.Normals.Length >= m.VertexCount * 3;
            if (hasN)
                for (int i = 0; i < m.VertexCount; i++)
                {
                    Vector3 nv = Vector3.TransformNormal(new Vector3(m.Normals[i * 3], m.Normals[i * 3 + 1], m.Normals[i * 3 + 2]), world);
                    if (nv.LengthSquared() > 1e-12f)
                        nv = Vector3.Normalize(nv);
                    obj.AppendLine(string.Format(inv, "vn {0:0.####} {1:0.####} {2:0.####}", nv.X, nv.Z, -nv.Y));
                }
            foreach (SceneAssets.Group g in asset.Groups)
            {
                string mat = MaterialName(g, materials, mtl, pngs, texture, dir, texDir, ref written);
                obj.AppendLine($"usemtl {mat}");
                for (int t = g.Start; t + 2 < g.Start + g.Count && t + 2 < m.Indices.Length; t += 3)
                {
                    obj.Append('f');
                    for (int c = 0; c < 3; c++)
                    {
                        long v = m.Indices[t + c] + vBase;
                        obj.Append(' ').Append(v.ToString(inv));
                        if (hasUv || hasN)
                            obj.Append('/').Append(hasUv ? v.ToString(inv) : "");
                        if (hasN)
                            obj.Append('/').Append(v.ToString(inv));
                    }
                    obj.AppendLine();
                }
            }
            vBase += m.VertexCount;
        }
        File.WriteAllText(objPath, obj.ToString());
        File.WriteAllText(Path.Combine(dir, baseName + ".mtl"), mtl.ToString());
        return written;
    }

    // un matériau .mtl par matériau du jeu, avec sa texture convertie en .png (une seule fois)
    private static string MaterialName(SceneAssets.Group g, Dictionary<string, string> materials, StringBuilder mtl,
        Dictionary<string, string?> pngs, Func<string, byte[]?> texture, string dir, string texDir, ref int written)
    {
        string key = (g.Material ?? "") + "|" + (g.Diffuse ?? "");
        if (materials.TryGetValue(key, out string? existing))
            return existing;
        string name = Clean(string.IsNullOrEmpty(g.Material) ? "materiau" : Path.GetFileNameWithoutExtension(g.Material));
        while (materials.ContainsValue(name))
            name += "_";
        materials[key] = name;
        mtl.AppendLine($"newmtl {name}");
        mtl.AppendLine("Kd 1 1 1");
        mtl.AppendLine("Ka 0 0 0");
        mtl.AppendLine("Ks 0 0 0");
        if (g.Diffuse is { Length: > 0 } diff)
        {
            // le jeu ne se sert de la transparence que pour les matériaux découpés (feuillages, grilles) ; ailleurs, le canal
            // alpha de la texture contient autre chose (souvent 0 : le sol de London) et Blender rendrait la pièce invisible
            string pngKey = diff + (g.Alpha ? "|alpha" : "");
            if (!pngs.TryGetValue(pngKey, out string? png))
            {
                png = null;
                try
                {
                    if (texture(diff) is { } bytes && ImageDecode.Decode(bytes) is { } img)
                    {
                        string file = Clean(Path.GetFileNameWithoutExtension(diff)) + (g.Alpha ? "_alpha" : "") + ".png";
                        if (!g.Alpha)
                            for (int i = 3; i < img.rgba.Length; i += 4)
                                img.rgba[i] = 255;
                        PngWriter.Write(Path.Combine(dir, texDir, file), img.w, img.h, img.rgba);
                        png = texDir + "/" + file;
                        written++;
                    }
                    else
                        Console.WriteLine($"Export OBJ : texture « {diff} » absente ou format non pris en charge");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Export OBJ : texture « {diff} » : {e.Message}");
                }
                pngs[pngKey] = png;
            }
            if (png != null)
            {
                mtl.AppendLine($"map_Kd {png}");
                if (g.Alpha)
                    mtl.AppendLine($"map_d {png}");
            }
        }
        mtl.AppendLine();
        return name;
    }

    private static string Clean(string s)
    {
        StringBuilder b = new StringBuilder();
        foreach (char c in s)
            b.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' ? c : '_');
        return b.Length == 0 ? "piece" : b.ToString();
    }
}
