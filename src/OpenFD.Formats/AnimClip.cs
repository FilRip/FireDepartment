using System.Numerics;
using System.Text;

namespace OpenFD.Formats;

/// <summary>
/// Animation (.anim) : u32 ?, u32 nombre de pistes ; chaque piste : u32 100, chaîne (nom de l'os), 11 × u32
/// (?, ?, 1, 1, 1, début et nombre des clés de position, de rotation, d'échelle) ; puis trois tableaux :
/// positions (temps, x, y, z), rotations (temps, quaternion x y z w), échelles (temps, x, y, z).
/// Temps en « ticks » : 4 800 par seconde (160 par image à 30 images/s). Valeurs locales (par rapport au parent).
/// </summary>
public sealed class AnimClip
{
    public const float TicksPerSecond = 4800f;
    public sealed class Track
    {
        public (float t, Vector3 v)[] Pos = Array.Empty<(float, Vector3)>();
        public (float t, Quaternion v)[] Rot = Array.Empty<(float, Quaternion)>();
        public (float t, Vector3 v)[] Scale = Array.Empty<(float, Vector3)>();
    }
    public Dictionary<string, Track> Tracks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public float Duration;   // secondes

    public static AnimClip Parse(byte[] d)
    {
        AnimClip a = new AnimClip();
        int nt = BitConverter.ToInt32(d, 4), p = 8;
        List<(string name, int ps, int pc, int rs, int rc, int ss, int sc)> heads = new List<(string name, int ps, int pc, int rs, int rc, int ss, int sc)>();
        for (int i = 0; i < nt; i++)
        {
            int l = BitConverter.ToInt32(d, p + 4);
            string name = Encoding.ASCII.GetString(d, p + 8, Math.Max(0, l - 1));
            p += 8 + l;
            int U(int k) => BitConverter.ToInt32(d, p + 4 * k);
            heads.Add((name, U(5), U(6), U(7), U(8), U(9), U(10)));
            p += 44;
        }
        float F(int o) => BitConverter.ToSingle(d, o);
        int np = BitConverter.ToInt32(d, p); int P = p + 4; p = P + 16 * np;
        int nr = BitConverter.ToInt32(d, p); int R = p + 4; p = R + 20 * nr;
        int ns = p + 4 <= d.Length ? BitConverter.ToInt32(d, p) : 0; int S = p + 4;
        if (S + 16L * ns > d.Length)
            ns = 0;
        float maxT = 0;
        foreach ((string name, int ps, int pc, int rs, int rc, int ss, int sc) h in heads)
        {
            Track t = new Track();
            if (h.ps >= 0 && h.ps + h.pc <= np)
                t.Pos = Enumerable.Range(h.ps, h.pc).Select(i => { int o = P + 16 * i; return (F(o) / TicksPerSecond, new Vector3(F(o + 4), F(o + 8), F(o + 12))); }).ToArray();
            if (h.rs >= 0 && h.rs + h.rc <= nr)
                t.Rot = Enumerable.Range(h.rs, h.rc).Select(i => { int o = R + 20 * i; return (F(o) / TicksPerSecond, new Quaternion(F(o + 4), F(o + 8), F(o + 12), F(o + 16))); }).ToArray();
            if (h.ss >= 0 && h.ss + h.sc <= ns)
                t.Scale = Enumerable.Range(h.ss, h.sc).Select(i => { int o = S + 16 * i; return (F(o) / TicksPerSecond, new Vector3(F(o + 4), F(o + 8), F(o + 12))); }).ToArray();
            foreach ((float t, Vector3 v) k in t.Pos)
                maxT = MathF.Max(maxT, k.t);
            foreach ((float t, Quaternion v) k in t.Rot)
                maxT = MathF.Max(maxT, k.t);
            a.Tracks[h.name] = t;
        }
        a.Duration = MathF.Max(maxT, 1 / 30f);
        return a;
    }

    private static int Find<T>((float t, T v)[] keys, float time)
    {
        int lo = 0, hi = keys.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (keys[mid].t <= time)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Matrice locale de l'os à l'instant t (échelle × rotation × translation), ou null s'il n'est pas animé.</summary>
    public Matrix4x4? Local(string bone, float time)
    {
        if (!Tracks.TryGetValue(bone, out Track? tr) || (tr.Pos.Length == 0 && tr.Rot.Length == 0))
            return null;
        Vector3 Lerp((float t, Vector3 v)[] k, Vector3 def)
        {
            if (k.Length == 0)
                return def;
            int i = Find(k, time);
            if (i >= k.Length - 1 || time <= k[i].t)
                return k[i].v;
            float f = (time - k[i].t) / MathF.Max(1e-6f, k[i + 1].t - k[i].t);
            return Vector3.Lerp(k[i].v, k[i + 1].v, Math.Clamp(f, 0, 1));
        }
        Quaternion q = Quaternion.Identity;
        if (tr.Rot.Length > 0)
        {
            int i = Find(tr.Rot, time);
            if (i >= tr.Rot.Length - 1 || time <= tr.Rot[i].t)
                q = tr.Rot[i].v;
            else
            {
                float f = (time - tr.Rot[i].t) / MathF.Max(1e-6f, tr.Rot[i + 1].t - tr.Rot[i].t);
                q = Quaternion.Slerp(tr.Rot[i].v, tr.Rot[i + 1].v, Math.Clamp(f, 0, 1));
            }
        }
        Vector3 s = Lerp(tr.Scale, Vector3.One); Vector3 pos = Lerp(tr.Pos, Vector3.Zero);
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q)) * Matrix4x4.CreateTranslation(pos);
    }
}
