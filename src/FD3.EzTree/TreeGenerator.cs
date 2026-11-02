using System.Numerics;
using System.Text.Json;

namespace FD3.EzTree;

/// <summary>
/// Générateur d'arbres réalistes : portage en C# d'EZ-Tree 1.1 (Daniel Greenheck, licence MIT,
/// https://github.com/dgreenheck/ez-tree). Mêmes réglages (fichiers Assets/trees/*.json), même
/// générateur aléatoire, donc mêmes arbres que dans la vue 3D du navigateur. Axe Y vers le haut.
/// </summary>
public sealed class TreeGenerator
{
    public sealed class Options
    {
        public int Seed; public string Type = "deciduous";
        public string BarkType = "oak"; public Vector3 BarkTint = Vector3.One; public Vector2 BarkScale = Vector2.One;
        public int Levels;
        public float[] Angle = new float[4], Gnarliness = new float[4], Length = new float[4], Radius = new float[4], Start = new float[4], Taper = new float[4], Twist = new float[4];
        public int[] Children = new int[4], Sections = new int[4], Segments = new int[4];
        public Vector3 ForceDir = Vector3.UnitY; public float ForceStrength;
        public string LeafType = "oak"; public bool LeafDouble = true; public float LeafAngle, LeafStart, LeafSize, LeafSizeVariance, LeafAlphaTest = 0.5f;
        public int LeafCount; public Vector3 LeafTint = Vector3.One;

        public static Options FromJson(string json)
        {
            var o = new Options(); var r = JsonDocument.Parse(json).RootElement;
            float[] Arr(JsonElement e) { var a = new float[4]; foreach (var p in e.EnumerateObject()) a[int.Parse(p.Name)] = p.Value.GetSingle(); return a; }
            int[] IArr(JsonElement e) { var a = new int[4]; foreach (var p in e.EnumerateObject()) a[int.Parse(p.Name)] = p.Value.GetInt32(); return a; }
            Vector3 Col(int c) => new(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f);
            o.Seed = r.GetProperty("seed").GetInt32(); o.Type = r.GetProperty("type").GetString()!;
            var b = r.GetProperty("bark"); o.BarkType = b.GetProperty("type").GetString()!; o.BarkTint = Col(b.GetProperty("tint").GetInt32());
            o.BarkScale = new(b.GetProperty("textureScale").GetProperty("x").GetSingle(), b.GetProperty("textureScale").GetProperty("y").GetSingle());
            var br = r.GetProperty("branch");
            o.Levels = br.GetProperty("levels").GetInt32();
            o.Angle = Arr(br.GetProperty("angle")); o.Children = IArr(br.GetProperty("children")); o.Gnarliness = Arr(br.GetProperty("gnarliness"));
            o.Length = Arr(br.GetProperty("length")); o.Radius = Arr(br.GetProperty("radius")); o.Sections = IArr(br.GetProperty("sections"));
            o.Segments = IArr(br.GetProperty("segments")); o.Start = Arr(br.GetProperty("start")); o.Taper = Arr(br.GetProperty("taper")); o.Twist = Arr(br.GetProperty("twist"));
            var f = br.GetProperty("force"); var d = f.GetProperty("direction");
            o.ForceDir = new(d.GetProperty("x").GetSingle(), d.GetProperty("y").GetSingle(), d.GetProperty("z").GetSingle()); o.ForceStrength = f.GetProperty("strength").GetSingle();
            var l = r.GetProperty("leaves");
            o.LeafType = l.GetProperty("type").GetString()!; o.LeafDouble = l.GetProperty("billboard").GetString() == "double";
            o.LeafAngle = l.GetProperty("angle").GetSingle(); o.LeafCount = l.GetProperty("count").GetInt32(); o.LeafStart = l.GetProperty("start").GetSingle();
            o.LeafSize = l.GetProperty("size").GetSingle(); o.LeafSizeVariance = l.GetProperty("sizeVariance").GetSingle();
            o.LeafTint = Col(l.GetProperty("tint").GetInt32()); o.LeafAlphaTest = l.GetProperty("alphaTest").GetSingle();
            return o;
        }
    }

    /// <summary>Générateur aléatoire d'EZ-Tree (multiply-with-carry), identique à la version JavaScript.</summary>
    private sealed class Rng
    {
        private uint _w, _z;
        public Rng(int seed) { _w = (uint)(123456789 + seed); _z = (uint)(987654321 - seed); }
        public float Random(float max = 1, float min = 0)
        {
            _z = 36969 * (_z & 65535) + (uint)((int)_z >> 16);
            _w = 18000 * (_w & 65535) + (uint)((int)_w >> 16);
            uint res = (_z << 16) + (_w & 65535);
            return (max - min) * (float)(res / 4294967296.0) + min;
        }
    }

    // ----- angles d'Euler (ordre XYZ, comme three.js) -----
    private static Quaternion FromEuler(Vector3 e)
    {
        float c1 = MathF.Cos(e.X / 2), c2 = MathF.Cos(e.Y / 2), c3 = MathF.Cos(e.Z / 2), s1 = MathF.Sin(e.X / 2), s2 = MathF.Sin(e.Y / 2), s3 = MathF.Sin(e.Z / 2);
        return new Quaternion(s1 * c2 * c3 + c1 * s2 * s3, c1 * s2 * c3 - s1 * c2 * s3, c1 * c2 * s3 + s1 * s2 * c3, c1 * c2 * c3 - s1 * s2 * s3);
    }
    private static Vector3 ToEuler(Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(q);   // m.Mij (vecteur-ligne) = élément (j,i) de three.js
        float m11 = m.M11, m12 = m.M21, m13 = m.M31, m22 = m.M22, m23 = m.M32, m32 = m.M23, m33 = m.M33;
        float y = MathF.Asin(Math.Clamp(m13, -1, 1)), x, z;
        if (MathF.Abs(m13) < 0.9999999f) { x = MathF.Atan2(-m23, m33); z = MathF.Atan2(-m12, m11); }
        else { x = MathF.Atan2(m32, m22); z = 0; }
        return new Vector3(x, y, z);
    }
    private static Vector3 Rot(Vector3 v, Vector3 euler) => Vector3.Transform(v, FromEuler(euler));
    private static Quaternion FromUnitVectors(Vector3 a, Vector3 b)
    {
        float r = Vector3.Dot(a, b) + 1; Quaternion q;
        if (r < 1e-6f) { r = 0; q = MathF.Abs(a.X) > MathF.Abs(a.Z) ? new(-a.Y, a.X, 0, r) : new(0, -a.Z, a.Y, r); }
        else { var c = Vector3.Cross(a, b); q = new(c.X, c.Y, c.Z, r); }
        return Quaternion.Normalize(q);
    }
    private static Quaternion RotateTowards(Quaternion q, Quaternion target, float step)
    {
        float angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(q, target)), -1, 1));
        if (angle == 0) return q;
        float t = MathF.Min(1, step / angle);
        return Slerp3(q, target, t);
    }
    /// <summary>Slerp de three.js (Quaternion.slerp), qui diffère légèrement de celui de .NET aux extrémités.</summary>
    private static Quaternion Slerp3(Quaternion a, Quaternion b, float t)
    {
        if (t == 0) return a; if (t == 1) return b;
        float cos = a.W * b.W + a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        if (cos < 0) { b = new(-b.X, -b.Y, -b.Z, -b.W); cos = -cos; }
        if (cos >= 1) return a;
        float sqrSin = 1 - cos * cos;
        if (sqrSin <= float.Epsilon)
        {
            float s = 1 - t;
            return Quaternion.Normalize(new(s * a.X + t * b.X, s * a.Y + t * b.Y, s * a.Z + t * b.Z, s * a.W + t * b.W));
        }
        float sin = MathF.Sqrt(sqrSin), half = MathF.Atan2(sin, cos);
        float ra = MathF.Sin((1 - t) * half) / sin, rb = MathF.Sin(t * half) / sin;
        return new(a.X * ra + b.X * rb, a.Y * ra + b.Y * rb, a.Z * ra + b.Z * rb, a.W * ra + b.W * rb);
    }

    private record Branch(Vector3 Origin, Vector3 Orientation, float Length, float Radius, int Level, int SectionCount, int SegmentCount);
    private record struct Section(Vector3 Origin, Vector3 Orientation, float Radius);

    public readonly Options O;
    private Rng _rng = null!;
    private readonly Queue<Branch> _queue = new();
    public readonly List<float> BranchPos = new(), BranchNrm = new(), BranchUv = new(), LeafPos = new(), LeafNrm = new(), LeafUv = new();
    public readonly List<uint> BranchIdx = new(), LeafIdx = new();

    public TreeGenerator(Options o) => O = o;

    public void Generate()
    {
        _rng = new Rng(O.Seed);
        _queue.Enqueue(new Branch(Vector3.Zero, Vector3.Zero, O.Length[0], O.Radius[0], 0, O.Sections[0], O.Segments[0]));
        while (_queue.Count > 0) GenerateBranch(_queue.Dequeue());
        ComputeLeafNormals();
    }

    private void GenerateBranch(Branch b)
    {
        uint indexOffset = (uint)(BranchPos.Count / 3);
        var orient = b.Orientation; var origin = b.Origin;
        float sectionLength = b.Length / b.SectionCount;   // (le cas « Deciduous » avec majuscule ne se produit jamais dans l'original)
        var sections = new List<Section>();
        for (int i = 0; i <= b.SectionCount; i++)
        {
            float r = b.Radius;
            if (i == b.SectionCount && b.Level == O.Levels) r = 0.001f;
            else if (O.Type == "deciduous") r *= 1 - O.Taper[b.Level] * ((float)i / b.SectionCount);
            else if (O.Type == "evergreen") r *= 1 - (float)i / b.SectionCount;

            Vector3 fv = default, fn = default; float fuy = 0;
            for (int j = 0; j < b.SegmentCount; j++)
            {
                float a = 2f * MathF.PI * j / b.SegmentCount;
                var dir = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
                var v = Rot(dir * r, orient) + origin;
                var n = Vector3.Normalize(Rot(dir, orient));
                float uy = i % 2 == 0 ? 0 : 1;
                BranchPos.AddRange(new[] { v.X, v.Y, v.Z }); BranchNrm.AddRange(new[] { n.X, n.Y, n.Z }); BranchUv.Add((float)j / b.SegmentCount); BranchUv.Add(uy);
                if (j == 0) { fv = v; fn = n; fuy = uy; }
            }
            BranchPos.AddRange(new[] { fv.X, fv.Y, fv.Z }); BranchNrm.AddRange(new[] { fn.X, fn.Y, fn.Z }); BranchUv.Add(1); BranchUv.Add(fuy);
            sections.Add(new Section(origin, orient, r));

            origin += Rot(new Vector3(0, sectionLength, 0), orient);
            float g = MathF.Max(1, 1 / MathF.Sqrt(r)) * O.Gnarliness[b.Level];
            orient.X += _rng.Random(g, -g);
            orient.Z += _rng.Random(g, -g);

            var qSection = FromEuler(orient);
            var qTwist = Quaternion.CreateFromAxisAngle(Vector3.UnitY, O.Twist[b.Level]);
            var qForce = FromUnitVectors(Vector3.UnitY, O.ForceDir);
            qSection = Mul(qSection, qTwist);
            qSection = RotateTowards(qSection, qForce, O.ForceStrength / r);
            orient = ToEuler(qSection);
        }
        // indices (cylindres sans fond)
        int N = b.SegmentCount + 1;
        for (int i = 0; i < b.SectionCount; i++)
            for (int j = 0; j < b.SegmentCount; j++)
            {
                uint v1 = indexOffset + (uint)(i * N + j), v2 = indexOffset + (uint)(i * N + j + 1), v3 = v1 + (uint)N, v4 = v2 + (uint)N;
                BranchIdx.AddRange(new[] { v1, v3, v2, v2, v3, v4 });
            }

        if (O.Type == "deciduous")
        {
            var last = sections[^1];
            if (b.Level < O.Levels)
                _queue.Enqueue(new Branch(last.Origin, last.Orientation, O.Length[b.Level + 1], last.Radius, b.Level + 1, b.SectionCount, b.SegmentCount));
            else GenerateLeaf(last.Origin, last.Orientation);
        }
        if (b.Level == O.Levels) GenerateLeaves(sections);
        else if (b.Level < O.Levels) GenerateChildBranches(O.Children[b.Level], b.Level + 1, sections);
    }

    /// <summary>Produit de quaternions dans l'ordre de three.js (a.multiply(b) = a × b).</summary>
    private static Quaternion Mul(Quaternion a, Quaternion b) => Quaternion.Concatenate(b, a);

    private (Vector3 origin, Vector3 orient, float alpha, Section a, Section b) Along(List<Section> s, float start)
    {
        int idx = (int)MathF.Floor(start * (s.Count - 1));
        var A = s[idx]; var B = idx == s.Count - 1 ? A : s[idx + 1];
        float alpha = (start - (float)idx / (s.Count - 1)) / (1f / (s.Count - 1));
        var o = Vector3.Lerp(A.Origin, B.Origin, alpha);
        var q = Slerp3(FromEuler(B.Orientation), FromEuler(A.Orientation), alpha);
        return (o, ToEuler(q), alpha, A, B);
    }

    private void GenerateChildBranches(int count, int level, List<Section> sections)
    {
        float radialOffset = _rng.Random();
        for (int i = 0; i < count; i++)
        {
            float start = _rng.Random(1, O.Start[level]);
            var (o, parent, alpha, A, B) = Along(sections, start);
            float radius = O.Radius[level] * ((1 - alpha) * A.Radius + alpha * B.Radius);
            float radial = 2f * MathF.PI * (radialOffset + (float)i / count);
            var q1 = Quaternion.CreateFromAxisAngle(Vector3.UnitX, O.Angle[level] / (180f / MathF.PI));
            var q2 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, radial);
            var q3 = FromEuler(parent);
            var orient = ToEuler(Mul(q3, Mul(q2, q1)));
            float len = O.Length[level] * (O.Type == "evergreen" ? 1f - start : 1f);
            _queue.Enqueue(new Branch(o, orient, len, radius, level, O.Sections[level], O.Segments[level]));
        }
    }

    private void GenerateLeaves(List<Section> sections)
    {
        float radialOffset = _rng.Random();
        for (int i = 0; i < O.LeafCount; i++)
        {
            float start = _rng.Random(1, O.LeafStart);
            var (o, parent, _, _, _) = Along(sections, start);
            float radial = 2f * MathF.PI * (radialOffset + (float)i / O.LeafCount);
            var q1 = Quaternion.CreateFromAxisAngle(Vector3.UnitX, O.LeafAngle / (180f / MathF.PI));
            var q2 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, radial);
            var q3 = FromEuler(parent);
            GenerateLeaf(o, ToEuler(Mul(q3, Mul(q2, q1))));
        }
    }

    private void GenerateLeaf(Vector3 origin, Vector3 orient)
    {
        float size = O.LeafSize * (1 + _rng.Random(O.LeafSizeVariance, -O.LeafSizeVariance));
        float W = size, L = size;
        void Create(float rotation)
        {
            uint i = (uint)(LeafPos.Count / 3);
            foreach (var v0 in new[] { new Vector3(-W / 2, L, 0), new Vector3(-W / 2, 0, 0), new Vector3(W / 2, 0, 0), new Vector3(W / 2, L, 0) })
            {
                var v = Rot(Rot(v0, new Vector3(0, rotation, 0)), orient) + origin;
                LeafPos.AddRange(new[] { v.X, v.Y, v.Z });
            }
            LeafUv.AddRange(new float[] { 0, 1, 0, 0, 1, 0, 1, 1 });
            LeafIdx.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
        }
        Create(0);
        if (O.LeafDouble) Create(MathF.PI / 2);
    }

    private void ComputeLeafNormals()
    {
        var n = new Vector3[LeafPos.Count / 3];
        for (int k = 0; k < LeafIdx.Count; k += 3)
        {
            int a = (int)LeafIdx[k], b = (int)LeafIdx[k + 1], c = (int)LeafIdx[k + 2];
            Vector3 P(int i) => new(LeafPos[i * 3], LeafPos[i * 3 + 1], LeafPos[i * 3 + 2]);
            var f = Vector3.Cross(P(c) - P(b), P(a) - P(b));
            n[a] += f; n[b] += f; n[c] += f;
        }
        foreach (var v in n) { var u = v.LengthSquared() > 0 ? Vector3.Normalize(v) : Vector3.UnitY; LeafNrm.AddRange(new[] { u.X, u.Y, u.Z }); }
    }
}
