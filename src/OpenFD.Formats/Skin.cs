using System.Numerics;
using System.Text;

namespace OpenFD.Formats;

/// <summary>
/// Peau d'un maillage animé (fin du .mesh) : liste des os (nom + matrice de pose de référence, repère du modèle,
/// convention vecteur-ligne), puis pour chaque sommet : nombre d'influences, et pour chacune
/// (u32 os, f32 poids, 2 × f32 inutilisés, u32 inutilisé) = 20 octets. Les poids d'un sommet font 1.
/// </summary>
public sealed class Skin
{
    public string[] Bones = Array.Empty<string>();
    public Matrix4x4[] Bind = Array.Empty<Matrix4x4>();
    public int[] Start = Array.Empty<int>();             // première influence de chaque sommet (taille n + 1)
    public int[] Bone = Array.Empty<int>();
    public float[] Weight = Array.Empty<float>();

    public static Skin? Parse(byte[] mesh, int vertexCount)
    {
        byte[] tag = Encoding.ASCII.GetBytes(".meshdata\0");
        int at = mesh.AsSpan().IndexOf(tag);
        if (at < 0)
            return null;
        // le nombre d'os se trouve un peu après le nom du .meshdata (boîte englobante et matrice entre les deux)
        for (int p = at + tag.Length; p + 8 < mesh.Length && p < at + 400; p++)
        {
            int c = BitConverter.ToInt32(mesh, p);
            if (c < 1 || c > 256)
                continue;
            Skin? s = TryParse(mesh, p, c, vertexCount);
            if (s != null)
                return s;
        }
        return null;
    }

    private static Skin? TryParse(byte[] d, int p, int count, int n)
    {
        Skin s = new Skin { Bones = new string[count], Bind = new Matrix4x4[count] };
        p += 4;
        for (int i = 0; i < count; i++)
        {
            if (p + 4 > d.Length)
                return null;
            int l = BitConverter.ToInt32(d, p);
            if (l < 2 || l > 80 || p + 4 + l + 64 > d.Length || d[p + 3 + l] != 0)
                return null;
            for (int k = 0; k < l - 1; k++)
                if (d[p + 4 + k] < 32 || d[p + 4 + k] >= 127)
                    return null;
            s.Bones[i] = Encoding.ASCII.GetString(d, p + 4, l - 1);
            p += 4 + l;
            float[] f = new float[16];
            for (int k = 0; k < 16; k++)
                f[k] = BitConverter.ToSingle(d, p + 4 * k);
            s.Bind[i] = new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
            p += 64;
        }
        if (p + 4 > d.Length || BitConverter.ToInt32(d, p) != n)
            return null;
        p += 4;
        s.Start = new int[n + 1];
        List<int> bone = new List<int>(n * 3);
        List<float> w = new List<float>(n * 3);
        for (int v = 0; v < n; v++)
        {
            if (p + 4 > d.Length)
                return null;
            int c = BitConverter.ToInt32(d, p);
            p += 4;
            if (c < 0 || c > 16 || p + 20 * c > d.Length)
                return null;
            s.Start[v] = bone.Count;
            for (int i = 0; i < c; i++)
            {
                int b = BitConverter.ToInt32(d, p);
                float wt = BitConverter.ToSingle(d, p + 4);
                p += 20;
                if (b < 0 || b >= count)
                    return null;
                bone.Add(b);
                w.Add(wt);
            }
        }
        s.Start[n] = bone.Count;
        s.Bone = bone.ToArray();
        s.Weight = w.ToArray();
        return s;
    }
}
