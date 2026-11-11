using System.Numerics;
using System.Text;

namespace OpenFD.ConvFD1;

/// <summary>Écriture des fichiers graphiques du moteur : .mesh, .meshdata, .sg (scène de nœuds) et .stdmat (matériau).</summary>
public static class Fd3Writer
{
    private static void Str(BinaryWriter b, string s)
    {
        b.Write(s.Length + 1);
        b.Write(Encoding.ASCII.GetBytes(s));
        b.Write((byte)0);
    }

    // fin des fichiers .sg du jeu (nœud racine), identique dans les modèles d'un seul nœud
    private const string RootRecord = "030000000e0000005b524f4f54204f424a4543545d0001000000000000803f000000000000000000000000000000000000803f"
        + "000000000000000000000000000000000000803f000000000000000000000000000000000000803f00000000000000000000000000000000"
        + "00000000000000000000803f0000803f0000803f0000803f01";

    /// <summary>.sg : un nœud par maillage (nom = nom du fichier .mesh), avec sa matrice dans le monde (cm).</summary>
    public static byte[] Scene(IEnumerable<(string node, Matrix4x4 world)> nodes)
    {
        using MemoryStream ms = new MemoryStream();
        using BinaryWriter b = new BinaryWriter(ms);
        List<(string node, Matrix4x4 world)> list = nodes.ToList();
        b.Write(102);
        b.Write(101);
        b.Write(1);
        b.Write(101);
        b.Write(0);
        b.Write(4 + list.Count);
        foreach ((string node, Matrix4x4 m) in list)
        {
            Str(b, node);
            Str(b, "[ROOT OBJECT]");
            float[] f = { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 };
            foreach (float x in f)
                b.Write(x);
            foreach (float x in new[] { m.M41, m.M42, m.M43, -0f, -0f, -0f, -1f, 1f, 1f, 1f })
                b.Write(x);
            b.Write((byte)1);
        }
        b.Write(Convert.FromHexString(RootRecord));
        b.Flush();
        return ms.ToArray();
    }

    /// <summary>.mesh : triangles (u16), matériaux, plages de triangles par matériau, boîte englobante.</summary>
    public static byte[] MeshFile(Fd3Mesh m)
    {
        using MemoryStream ms = new MemoryStream();
        using BinaryWriter b = new BinaryWriter(ms);
        b.Write(106);
        b.Write(m.Indices.Count);
        foreach (ushort i in m.Indices)
            b.Write(i);
        b.Write(m.Groups.Count);
        foreach ((string material, int _, int _) in m.Groups)
            Str(b, material + ".stdmat");
        b.Write(m.Groups.Count);
        foreach ((string _, int start, int count) in m.Groups)
        {
            b.Write(start);
            b.Write(start + count);
        }
        Str(b, m.Name + ".meshdata");
        b.Write(100);
        (Vector3 lo, Vector3 hi) = Bounds(m);
        Vector3 c = (lo + hi) / 2;
        float radius = (hi - lo).Length() / 2;
        foreach (float f in new[] { c.X, c.Y, c.Z, radius, lo.X, lo.Y, lo.Z, hi.X, hi.Y, hi.Z, 0f })
            b.Write(f);
        b.Write(1);
        b.Write((byte)0);
        b.Flush();
        return ms.ToArray();
    }

    private static (Vector3 lo, Vector3 hi) Bounds(Fd3Mesh m)
    {
        Vector3 lo = new Vector3(float.MaxValue);
        Vector3 hi = new Vector3(float.MinValue);
        for (int i = 0; i < m.VertexCount; i++)
        {
            Vector3 p = new Vector3(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]);
            lo = Vector3.Min(lo, p);
            hi = Vector3.Max(hi, p);
        }
        if (m.VertexCount == 0)
            return (Vector3.Zero, Vector3.Zero);
        return (lo, hi);
    }

    /// <summary>.meshdata : positions compressées (u16), normales (u8), tangentes et binormales neutres, une couche de texture (demi-flottants), couleurs blanches.</summary>
    public static byte[] MeshData(Fd3Mesh m)
    {
        int n = m.VertexCount;
        (Vector3 lo, Vector3 hi) = Bounds(m);
        float[] min = { lo.X, lo.Y, lo.Z };
        float[] ext = { MathF.Max(hi.X - lo.X, 1e-3f), MathF.Max(hi.Y - lo.Y, 1e-3f), MathF.Max(hi.Z - lo.Z, 1e-3f) };
        using MemoryStream ms = new MemoryStream();
        using BinaryWriter b = new BinaryWriter(ms);
        b.Write(Encoding.ASCII.GetBytes("TRDT"));
        b.Write(0x67);
        b.Write(Encoding.ASCII.GetBytes("MPAK"));
        b.Write(0x64);
        for (int k = 0; k < 3; k++)
        {
            b.Write(n);
            for (int i = 0; i < n; i++)
                b.Write((ushort)Math.Clamp(MathF.Round((m.Positions[3 * i + k] - min[k]) / ext[k] * 65535), 0, 65535));
        }
        foreach (float f in new[] { ext[0], ext[1], ext[2], min[0], min[1], min[2] })
            b.Write(f);
        for (int k = 0; k < 9; k++)
        {
            b.Write(n);
            for (int i = 0; i < n; i++)
            {
                float v = k < 3 && m.Normals.Count == 3 * n ? m.Normals[3 * i + k] : 0f;
                b.Write((byte)Math.Clamp(MathF.Round((v + 1f) * 127.5f), 0, 255));
            }
        }
        b.Write(n);
        for (int i = 0; i < n; i++)
        {
            b.Write(m.Uv.Count == 2 * n ? (Half)m.Uv[2 * i] : (Half)0);
            b.Write(m.Uv.Count == 2 * n ? (Half)m.Uv[2 * i + 1] : (Half)0);
        }
        for (int s = 1; s < 8; s++)
            b.Write(0);
        b.Write(n);
        for (int i = 0; i < n; i++)
            b.Write(new byte[] { 255, 255, 255, 255 });
        Str(b, "");
        b.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// .stdmat : copie d'un matériau du jeu à une seule texture (modèle), où l'on remplace le nom du matériau et le nom de la
    /// texture (chaînes « u32 longueur + texte + 0 »).
    /// </summary>
    public static byte[] Stdmat(byte[] template, string name, string texture)
    {
        List<(int at, int len, string text)> strings = new List<(int, int, string)>();
        for (int i = 0; i + 8 < template.Length; i++)
        {
            int l = BitConverter.ToInt32(template, i);
            if (l < 2 || l > 200 || i + 4 + l > template.Length || template[i + 3 + l] != 0)
                continue;
            string s = Encoding.ASCII.GetString(template, i + 4, l - 1);
            if (s.Any(c => c < 32 || c > 126))
                continue;
            strings.Add((i, 4 + l, s));
            i += 3 + l;
        }
        using MemoryStream ms = new MemoryStream();
        using BinaryWriter b = new BinaryWriter(ms);
        int pos = 0;
        bool first = true;
        foreach ((int at, int len, string text) in strings)
        {
            b.Write(template, pos, at - pos);
            string low = text.ToLowerInvariant();
            if (first)
                Str(b, name);
            else if (low.EndsWith(".tga") || low.EndsWith(".dds") || low.EndsWith(".bmp"))
                Str(b, texture);
            else
                b.Write(template, at, len);
            first = false;
            pos = at + len;
        }
        b.Write(template, pos, template.Length - pos);
        b.Flush();
        return ms.ToArray();
    }
}
