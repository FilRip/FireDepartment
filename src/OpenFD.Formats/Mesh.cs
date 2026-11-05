using System.Text;

namespace OpenFD.Formats;

/// <summary>
/// Modèle 3D du jeu : fichier .mesh (triangles, matériaux) + fichier .meshdata (sommets compressés).
///
/// .mesh : u32 version (106), u32 nombre d'indices, u16 × indices ; u32 nombre de matériaux, chaînes ;
///         u32 nombre de plages, (début, fin) × plages ; chaîne nom du .meshdata ; u32 100 ; 11 flottants
///         (boîte englobante en [4..7] et [7..10]).
/// .meshdata : « TRDT » u32, « MPAK » u32, puis
///         3 flux u16 non signés (x, y, z ; 0..65535 → minimum..minimum + étendue) suivis de 6 flottants (étendue xyz, minimum xyz) ;
///         9 flux u8 (normale, tangente, binormale ; 0..255 → −1..1) ;
///         8 emplacements de coordonnées de texture (u32 nombre, 0 si absent, puis 2 demi-flottants par sommet) ;
///         couleurs (u32 nombre, 4 octets par sommet) ; chaîne nom du shader.
/// Chaque flux commence par un u32 = nombre de sommets.
/// </summary>
public sealed class Mesh
{
    public string Name = "";
    public int VertexCount;
    public float[] Positions = Array.Empty<float>();   // x, y, z
    public float[] Normals = Array.Empty<float>();     // x, y, z
    public float[]?[] Uv = new float[8][];             // u, v par emplacement
    public byte[]? Colors;                              // b, g, r, a
    public uint[] Indices = Array.Empty<uint>();
    public List<(string material, int start, int count)> Groups = new();
    public string Shader = "";

    public static Mesh Load(byte[] mesh, byte[] meshdata, string name)
    {
        Mesh m = new Mesh { Name = name };
        // --- .mesh
        int p = 4;
        int ni = BitConverter.ToInt32(mesh, p); p += 4;
        m.Indices = new uint[ni];
        for (int i = 0; i < ni; i++)
            m.Indices[i] = BitConverter.ToUInt16(mesh, p + 2 * i);
        p += 2 * ni;
        string Str() { int l = BitConverter.ToInt32(mesh, p); string s = Encoding.ASCII.GetString(mesh, p + 4, Math.Max(0, l - 1)); p += 4 + l; return s; }
        int nm = BitConverter.ToInt32(mesh, p); p += 4;
        List<string> mats = new List<string>();
        for (int i = 0; i < nm; i++)
            mats.Add(Str());
        int nr = BitConverter.ToInt32(mesh, p); p += 4;
        for (int i = 0; i < nr; i++)
        {
            int a = BitConverter.ToInt32(mesh, p + 8 * i), b = BitConverter.ToInt32(mesh, p + 8 * i + 4);
            m.Groups.Add((i < mats.Count ? mats[i] : "", a, b - a));
        }

        // --- .meshdata
        byte[] d = meshdata;
        p = 16;
        int n = BitConverter.ToInt32(d, p);
        m.VertexCount = n;
        ushort[][] q = new ushort[3][];
        for (int k = 0; k < 3; k++)
        {
            q[k] = new ushort[n];
            Buffer.BlockCopy(d, p + 4, q[k], 0, 2 * n);
            p += 4 + 2 * n;
        }
        float F(int o) => BitConverter.ToSingle(d, o);
        float ex = F(p), ey = F(p + 4), ez = F(p + 8), mx = F(p + 12), my = F(p + 16), mz = F(p + 20);
        p += 24;
        m.Positions = new float[3 * n];
        for (int i = 0; i < n; i++)
        {
            m.Positions[3 * i] = mx + q[0][i] / 65535f * ex;
            m.Positions[3 * i + 1] = my + q[1][i] / 65535f * ey;
            m.Positions[3 * i + 2] = mz + q[2][i] / 65535f * ez;
        }
        byte[][] bytes = new byte[9][];
        for (int k = 0; k < 9; k++)
        {
            bytes[k] = d.AsSpan(p + 4, n).ToArray();
            p += 4 + n;
        }
        m.Normals = new float[3 * n];
        for (int i = 0; i < n; i++)
            for (int k = 0; k < 3; k++)
                m.Normals[3 * i + k] = bytes[k][i] / 127.5f - 1f;
        for (int s = 0; s < 8; s++)
        {
            int c = BitConverter.ToInt32(d, p); p += 4;
            if (c == 0)
                continue;
            float[] uv = new float[2 * c];
            for (int i = 0; i < 2 * c; i++)
                uv[i] = (float)BitConverter.ToHalf(d, p + 2 * i);
            m.Uv[s] = uv; p += 4 * c;
        }
        if (p + 4 <= d.Length)
        {
            int c = BitConverter.ToInt32(d, p); p += 4;
            if (c > 0 && p + 4 * c <= d.Length)
            {
                m.Colors = d.AsSpan(p, 4 * c).ToArray();
                p += 4 * c;
            }
            if (p + 4 <= d.Length)
            {
                int l = BitConverter.ToInt32(d, p);
                if (l > 0 && p + 4 + l <= d.Length)
                    m.Shader = Encoding.ASCII.GetString(d, p + 4, l - 1);
            }
        }
        return m;
    }
}
