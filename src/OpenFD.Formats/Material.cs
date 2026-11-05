using System.Text;

namespace OpenFD.Formats;

/// <summary>Matériau (.stdmat) : on en retient le nom et les textures dans l'ordre (la première = couleur).</summary>
public sealed class Material
{
    public string Name = "";
    public List<string> Textures = new();
    public string? Diffuse => Textures.FirstOrDefault(t => !t.Contains("lightmap", StringComparison.OrdinalIgnoreCase));
    public string? Lightmap => Textures.FirstOrDefault(t => t.Contains("lightmap", StringComparison.OrdinalIgnoreCase));

    public static Material Load(byte[] d, string name)
    {
        Material mat = new Material { Name = name };
        // chaînes préfixées par leur longueur, terminées par 0 ; les textures finissent par .tga/.dds
        for (int i = 0; i + 8 < d.Length; i++)
        {
            int l = BitConverter.ToInt32(d, i);
            if (l < 5 || l > 200 || i + 4 + l > d.Length || d[i + 3 + l] != 0)
                continue;
            string s = Encoding.ASCII.GetString(d, i + 4, l - 1);
            if (s.Any(c => c < 32 || c > 126))
                continue;
            string low = s.ToLowerInvariant();
            if ((low.EndsWith(".tga") || low.EndsWith(".dds") || low.EndsWith(".bmp")) && (mat.Textures.Count == 0 || mat.Textures[^1] != s))
                mat.Textures.Add(s);
            i += 3 + l;
        }
        return mat;
    }
}
