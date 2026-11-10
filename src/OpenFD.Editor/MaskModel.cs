using System.Text;

namespace OpenFD.MG;

/// <summary>
/// Modèle 3D du masque de toit de l'éditeur : une plaque noire de longueur × largeur choisies et de hauteur fixe, rangée
/// comme le couvercle du toit du hangar de Mriya (Gfx\Placeable\ROOF\Roof.sg, nœud « Angar_ROOM1_MASK », matériau noir
/// Roof1.stdmat) : fichiers .sg, .mesh et .meshdata dans Gfx\Placeable\ROOF.
/// </summary>
public static class MaskModel
{
    /// <summary>Hauteur (épaisseur) de la plaque, en cm.</summary>
    public const float Thickness = 3;

    /// <summary>Nom du modèle (classe et fichier .sg) pour cette taille : « openfd_mask_400x300 ».</summary>
    public static string Name(int w, int h) => $"openfd_mask_{w}x{h}";

    /// <summary>Fichiers du modèle (chemin dans Data → contenu).</summary>
    public static Dictionary<string, byte[]> Files(int w, int h)
    {
        string name = Name(w, h), node = name + "_ROOM1_MASK";
        return new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [$"Gfx\\Placeable\\ROOF\\{name}.sg"] = Scene(node),
            [$"Gfx\\Placeable\\ROOF\\{node}.mesh"] = MeshFile(node, w, h),
            [$"Gfx\\Placeable\\ROOF\\{node}.meshdata"] = MeshData(w, h),
        };
    }

    /// <summary>Texte de la classe (Design\Placeable\Levels\openfd_mask_LxH.class) : objet de décor sans occupation.</summary>
    public static string ClassText(int w, int h) =>
        $"[#INCLUDE]\r\nFILE0 = \"base_levels.class\"\r\n\r\n[DISPLAY]\r\nMODEL = ROOF\\{Name(w, h)}\r\n\r\n[OCCUPATION]\r\nACTIVATED = 0\r\n";

    private static void Str(BinaryWriter b, string s)
    {
        b.Write(s.Length + 1);
        b.Write(Encoding.ASCII.GetBytes(s));
        b.Write((byte)0);
    }

    // fin des fichiers .sg du jeu (nœud racine), identique dans tous les modèles d'un seul nœud (lap, Rock1, Roof…)
    private const string RootRecord = "030000000e0000005b524f4f54204f424a4543545d0001000000000000803f000000000000000000000000000000000000803f"
        + "000000000000000000000000000000000000803f000000000000000000000000000000000000803f00000000000000000000000000000000"
        + "00000000000000000000803f0000803f0000803f0000803f01";

    /// <summary>.sg : un seul nœud, au centre de l'objet.</summary>
    private static byte[] Scene(string node)
    {
        using MemoryStream ms = new MemoryStream();
        using BinaryWriter b = new BinaryWriter(ms);
        b.Write(102);
        b.Write(101);
        b.Write(1);
        b.Write(101);
        b.Write(0);
        b.Write(5);
        Str(b, node);
        Str(b, "[ROOT OBJECT]");
        // matrice (identité), position, rotation (quaternion comme dans les fichiers du jeu), échelle, puis 1
        float[] m = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        foreach (float f in m)
            b.Write(f);
        foreach (float f in new[] { 0f, 0f, 0f, -0f, -0f, -0f, -1f, 1f, 1f, 1f })
            b.Write(f);
        b.Write((byte)1);
        b.Write(Convert.FromHexString(RootRecord));
        b.Flush();
        return ms.ToArray();
    }

    // sommets : dessus (0..3, normale vers le haut), dessous (4..7, normale vers le bas)
    private static readonly ushort[] Indices = { 0, 1, 2, 2, 3, 0, 4, 6, 5, 6, 4, 7 };

    private static (float x, float y, float z)[] Corners(int w, int h)
    {
        float x = w / 2f, y = h / 2f;
        return new[]
        {
            (-x, -y, Thickness), (x, -y, Thickness), (x, y, Thickness), (-x, y, Thickness),
            (-x, -y, 0f), (x, -y, 0f), (x, y, 0f), (-x, y, 0f),
        };
    }

    /// <summary>.mesh : triangles, matériau noir Roof1.stdmat, boîte englobante (comme Angar_ROOM1_MASK.mesh).</summary>
    private static byte[] MeshFile(string node, int w, int h)
    {
        using MemoryStream ms = new MemoryStream();
        using BinaryWriter b = new BinaryWriter(ms);
        b.Write(106);
        b.Write(Indices.Length);
        foreach (ushort i in Indices)
            b.Write(i);
        b.Write(1);
        Str(b, "Roof1.stdmat");
        b.Write(1);
        b.Write(0);
        b.Write(Indices.Length);
        Str(b, node + ".meshdata");
        b.Write(100);
        // centre, rayon, minimum, maximum, 0
        float rx = w / 2f, ry = h / 2f;
        float radius = MathF.Sqrt(rx * rx + ry * ry + Thickness * Thickness / 4);
        foreach (float f in new[] { 0, 0, Thickness / 2, radius, -rx, -ry, 0, rx, ry, Thickness, 0 })
            b.Write(f);
        b.Write(1);
        b.Write((byte)0);
        b.Flush();
        return ms.ToArray();
    }

    /// <summary>.meshdata : positions compressées, normales, coordonnées de texture, couleurs noires, sans shader.</summary>
    private static byte[] MeshData(int w, int h)
    {
        (float x, float y, float z)[] v = Corners(w, h);
        int n = v.Length;
        float[] min = { -w / 2f, -h / 2f, 0 }, ext = { w, h, Thickness };
        using MemoryStream ms = new MemoryStream();
        using BinaryWriter b = new BinaryWriter(ms);
        b.Write(Encoding.ASCII.GetBytes("TRDT"));
        b.Write(0x67);
        b.Write(Encoding.ASCII.GetBytes("MPAK"));
        b.Write(0x64);
        for (int k = 0; k < 3; k++)
        {
            b.Write(n);
            foreach ((float x, float y, float z) p in v)
            {
                float c = k == 0 ? p.x : k == 1 ? p.y : p.z;
                b.Write((ushort)Math.Clamp(MathF.Round((c - min[k]) / ext[k] * 65535), 0, 65535));
            }
        }
        foreach (float f in new[] { ext[0], ext[1], ext[2], min[0], min[1], min[2] })
            b.Write(f);
        // normale (x, y, z), tangente, binormale : 0..255 → −1..1
        for (int k = 0; k < 9; k++)
        {
            b.Write(n);
            for (int i = 0; i < n; i++)
                b.Write(k == 2 ? (byte)(i < 4 ? 0xfe : 0x00) : (byte)0x7f);
        }
        // coordonnées de texture : premier emplacement seulement
        b.Write(n);
        foreach ((float x, float y, float z) p in v)
        {
            b.Write((Half)(p.x > 0 ? 1 : 0));
            b.Write((Half)(p.y > 0 ? 1 : 0));
        }
        for (int s = 1; s < 8; s++)
            b.Write(0);
        b.Write(n);
        for (int i = 0; i < n; i++)
            b.Write(new byte[] { 0, 0, 0, 255 });
        Str(b, "");
        b.Flush();
        return ms.ToArray();
    }
}
