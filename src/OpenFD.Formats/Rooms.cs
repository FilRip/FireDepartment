using System.Numerics;
using System.Text;

namespace OpenFD.Formats;

/// <summary>
/// Pièces d'une mission. Les concepteurs ont placé dans le décor des volumes invisibles nommés « …_ROOM… ».
/// On lit leur boîte englobante (fin du fichier .mesh) et leur position dans le graphe de scène (.sg),
/// puis on range chaque foyer et chaque point de fumée dans la plus petite pièce qui le contient.
/// Remplace les scripts tools/sg.py, roommap.py et roomall.py.
/// </summary>
public static class Rooms
{
    public sealed record Room(string Name, Vector3 Min, Vector3 Max)
    {
        public float Volume => (Max.X - Min.X) * (Max.Y - Min.Y) * (Max.Z - Min.Z);
        // tolérance : 1 unité à l'horizontale, 60 en hauteur (foyers posés au sol ou au plafond)
        public bool Contains(Vector3 p) =>
            p.X >= Min.X - 1 && p.X <= Max.X + 1 && p.Y >= Min.Y - 1 && p.Y <= Max.Y + 1 && p.Z >= Min.Z - 60 && p.Z <= Max.Z + 60;
    }

    /// <summary>Nœuds du graphe de scène : nom → (parent, matrice locale, convention vecteur-ligne).</summary>
    public static Dictionary<string, (string parent, Matrix4x4 m)> SceneNodes(byte[] d)
    {
        Dictionary<string, (string, Matrix4x4)> res = new Dictionary<string, (string, Matrix4x4)>();
        static bool Printable(ReadOnlySpan<byte> b)
        {
            if (b.Length == 0)
                return false;
            foreach (byte c in b)
                if (c < 32 || c >= 127)
                    return false;
            return true;
        }
        for (int i = 0; i + 80 < d.Length; i++)
        {
            int n = BitConverter.ToInt32(d, i);
            if (n < 2 || n > 80 || i + 3 + n >= d.Length || d[i + 3 + n] != 0)
                continue;
            Span<byte> name = d.AsSpan(i + 4, n - 1);
            if (!Printable(name))
                continue;
            int p = i + 4 + n;
            if (p + 4 > d.Length)
                continue;
            int n2 = BitConverter.ToInt32(d, p);
            if (n2 < 0 || n2 > 80 || (n2 > 0 && (p + 3 + n2 >= d.Length || d[p + 3 + n2] != 0)))
                continue;
            ReadOnlySpan<byte> parent = n2 > 0 ? d.AsSpan(p + 4, n2 - 1) : ReadOnlySpan<byte>.Empty;
            if (n2 > 0 && !Printable(parent))
                continue;
            p += 4 + n2;
            if (p + 64 > d.Length)
                continue;
            float[] f = new float[16];
            bool ok = true;
            for (int k = 0; k < 16; k++)
            {
                f[k] = BitConverter.ToSingle(d, p + 4 * k);
                if (!float.IsFinite(f[k]))
                    ok = false;
            }
            if (!ok || MathF.Abs(f[15] - 1) > 1e-4f || MathF.Abs(f[3]) > 1e-4f || MathF.Abs(f[7]) > 1e-4f || MathF.Abs(f[11]) > 1e-4f)
                continue;
            if (new[] { f[0], f[1], f[2], f[4], f[5], f[6], f[8], f[9], f[10] }.Any(v => MathF.Abs(v) > 1e3f))
                continue;
            Matrix4x4 m = new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
            res.TryAdd(Encoding.ASCII.GetString(name), (Encoding.ASCII.GetString(parent), m));
        }
        return res;
    }

    public static Matrix4x4 World(Dictionary<string, (string parent, Matrix4x4 m)> nodes, string name, int depth = 0)
    {
        (string? par, Matrix4x4 m) = nodes[name];
        // le nœud racine « GROUND » porte un décalage (Sawmill : z −701, Eurotunnel : +400, Tutorial : +125) que le jeu
        // n'applique pas : son maillage et ses enfants sont déjà à leur place (portes, fenêtres et sol en témoignent)
        if (name.Equals("GROUND", StringComparison.OrdinalIgnoreCase))
            return Matrix4x4.Identity;
        if (depth > 64 || par == "" || par == name || !nodes.ContainsKey(par))
            return m;
        return m * World(nodes, par, depth + 1);
    }

    /// <summary>Boîte englobante locale d'un .mesh (11 flottants après « .meshdata\0 » + 4 octets).</summary>
    public static (Vector3 min, Vector3 max)? MeshBox(byte[] d)
    {
        uint ni = BitConverter.ToUInt32(d, 4);
        int start = (int)Math.Min(d.Length, 8 + ni * 2);
        byte[] tag = Encoding.ASCII.GetBytes(".meshdata\0");
        int i = d.AsSpan(start).IndexOf(tag);
        if (i < 0)
            return null;
        int q = start + i + tag.Length + 4;
        if (q + 44 > d.Length)
            return null;
        float F(int k) => BitConverter.ToSingle(d, q + 4 * k);
        return (new Vector3(F(4), F(5), F(6)), new Vector3(F(7), F(8), F(9)));
    }

    /// <summary>Pièces de la mission, lues dans Gfx\Mission\&lt;mission&gt;\&lt;mission&gt;.sg.</summary>
    public static List<Room> Load(GameFiles files, string mission)
    {
        string prefix = "Gfx\\Mission\\" + mission + "\\";
        string? sg = files.List(prefix).FirstOrDefault(p => p[(p.LastIndexOf('\\') + 1)..].Equals(mission + ".sg", StringComparison.OrdinalIgnoreCase));
        if (sg == null)
            return new();
        Dictionary<string, (string parent, Matrix4x4 m)> nodes = SceneNodes(files.Read(sg));
        List<Room> rooms = new List<Room>();
        foreach (string k in nodes.Keys)
        {
            string up = k.ToUpperInvariant();
            if (!up.Contains("_ROOM") || up.Contains("MASK"))
                continue;
            if (files.TryRead(prefix + k + ".mesh") is not { } mesh || MeshBox(mesh) is not { } box)
                continue;
            Matrix4x4 w = World(nodes, k);
            Vector3 lo = new Vector3(float.MaxValue);
            Vector3 hi = new Vector3(float.MinValue);
            foreach (float x in new[] { box.min.X, box.max.X })
                foreach (float y in new[] { box.min.Y, box.max.Y })
                    foreach (float z in new[] { box.min.Z, box.max.Z })
                    {
                        Vector3 c = Vector3.Transform(new Vector3(x, y, z), w);
                        lo = Vector3.Min(lo, c);
                        hi = Vector3.Max(hi, c);
                    }
            rooms.Add(new Room(k, lo, hi));
        }
        return rooms;
    }

    /// <summary>Pièce (la plus petite qui le contient) de chaque foyer et point de fumée ; "" = dehors.</summary>
    public static Dictionary<uint, string> Assign(Level level, List<Room> rooms) =>
        level.Objects.Where(o => (o.Kind == EntityKind.HotPoint || o.Kind == EntityKind.SmokePoint) && o.HasTransform)
            .ToDictionary(o => o.Id, o => rooms.Where(r => r.Contains(o.Position)).OrderBy(r => r.Volume).FirstOrDefault()?.Name ?? "");
}
