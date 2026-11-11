using System.Numerics;

namespace OpenFD.ConvFD1;

/// <summary>
/// Fichier .SKEL de FD1 : 13 octets d'en-tête ; u32 n ; n matrices 4×4 (f32, translation en [12..14]) ;
/// listes d'enfants ; u32 n + n parents (−1 = racine) ; u32 n + n noms.
/// Le premier nœud (« Scene_Root ») porte l'échelle 0,01.
/// </summary>
public sealed class Fd1Skel
{
    public List<string> Names = new();
    public List<Matrix4x4> Local = new();
    public List<int> Parent = new();

    public static Fd1Skel Load(byte[] data)
    {
        Fd1Reader r = new Fd1Reader(data, 13);
        Fd1Skel s = new Fd1Skel();
        int n = r.I32();
        for (int i = 0; i < n; i++)
        {
            float[] f = new float[16];
            for (int k = 0; k < 16; k++)
                f[k] = r.F32();
            s.Local.Add(new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]));
        }
        // (liste des enfants de chaque nœud, de longueur variable) jusqu'au tableau des parents : n, puis −1 pour la racine
        int q = r.Pos;
        while (q + 8 <= r.Length && !(BitConverter.ToInt32(data, q) == n && BitConverter.ToInt32(data, q + 4) == -1))
            q += 4;
        if (q + 8 > r.Length)
            throw new InvalidDataException("squelette : parents introuvables");
        r.Pos = q;
        int np = r.I32();
        for (int i = 0; i < np; i++)
            s.Parent.Add(r.I32());
        int nn = r.I32();
        for (int i = 0; i < nn; i++)
            s.Names.Add(r.Str());
        return s;
    }

    /// <summary>Matrice du nœud jusqu'à la racine, sans le nœud racine (son échelle 0,01 est appliquée à part).</summary>
    public Matrix4x4 BelowRoot(string name)
    {
        int i = Names.FindIndex(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        Matrix4x4 m = Matrix4x4.Identity;
        for (int depth = 0; i > 0 && depth < 64; depth++)
        {
            m *= Local[i];
            i = i < Parent.Count ? Parent[i] : -1;
        }
        return m;
    }
}
