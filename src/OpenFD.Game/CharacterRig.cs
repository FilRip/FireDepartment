using System.Numerics;
using OpenFD.Formats;

namespace OpenFD.Game;

/// <summary>
/// Personnage animé : squelette du .sg (Biped « Bip01 … »), pièces à peau (déformées par les os)
/// et pièces rigides accrochées à un os (bouteille, hache…). Les animations viennent de Gfx\Anim\*.anim,
/// choisies d'après la section [ANIM_PLAYER] de la classe (WALK, RUN, ATTACKFIRE, WAIT1, BREAKDOOR, FAINT…).
/// </summary>
public sealed class CharacterRig
{
    public sealed class Part
    {
        public required string Mesh { get; init; }        // chemin Gfx du maillage
        public required int Node { get; init; }           // nœud du .sg
        public Skin? Skin;
        public int[] BoneNode = Array.Empty<int>();       // os de la peau → nœud du squelette
        public Matrix4x4[] InvBind = Array.Empty<Matrix4x4>();
        public float[] BasePos = Array.Empty<float>(), BaseNrm = Array.Empty<float>();
    }

    public string[] Names = Array.Empty<string>();
    public int[] Parent = Array.Empty<int>();
    public Matrix4x4[] BindLocal = Array.Empty<Matrix4x4>();
    public int[] Order = Array.Empty<int>();              // parents avant enfants
    public List<Part> Parts { get; } = new();
    public bool Animated => Parts.Any(p => p.Skin != null);

    public static CharacterRig? Build(SceneAssets assets, GameFiles files, string model)
    {
        byte[]? sgBytes = files.TryRead("Gfx\\" + model + ".sg");
        if (sgBytes == null)
            return null;
        Dictionary<string, (string parent, Matrix4x4 m)> nodes = Rooms.SceneNodes(sgBytes);
        CharacterRig rig = new CharacterRig { Names = nodes.Keys.ToArray() };
        Dictionary<string, int> index = rig.Names.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.OrdinalIgnoreCase);
        rig.Parent = rig.Names.Select(n => nodes[n].parent is string p && p != "" && p != n && index.TryGetValue(p, out int pi) ? pi : -1).ToArray();
        rig.BindLocal = rig.Names.Select(n => nodes[n].m).ToArray();
        List<int> order = new List<int>();
        bool[] seen = new bool[rig.Names.Length];
        void Visit(int i, int depth)
        {
            if (seen[i] || depth > 64)
                return;
            if (rig.Parent[i] >= 0)
                Visit(rig.Parent[i], depth + 1);
            seen[i] = true;
            order.Add(i);
        }
        for (int i = 0; i < rig.Names.Length; i++)
            Visit(i, 0);
        rig.Order = order.ToArray();

        foreach (SceneAssets.Node part in assets.ModelParts(model))
        {
            string leaf = part.Mesh[(part.Mesh.LastIndexOf('\\') + 1)..];
            if (!index.TryGetValue(leaf, out int node))
                continue;
            SceneAssets.MeshAsset asset = assets.LoadMesh("", part.Mesh, "gfx");
            Part p = new Part { Mesh = part.Mesh, Node = node };
            byte[]? meshBytes = files.TryRead("Gfx\\" + part.Mesh + ".mesh");
            Skin? skin = meshBytes != null ? Skin.Parse(meshBytes, asset.Mesh.VertexCount) : null;
            if (skin != null && skin.Bones.All(b => index.ContainsKey(b)))
            {
                p.Skin = skin;
                p.BoneNode = skin.Bones.Select(b => index[b]).ToArray();
                p.InvBind = skin.Bind.Select(m => Matrix4x4.Invert(m, out Matrix4x4 inv) ? inv : Matrix4x4.Identity).ToArray();
                p.BasePos = asset.Mesh.Positions;
                p.BaseNrm = asset.Mesh.Normals;
            }
            rig.Parts.Add(p);
        }
        return rig;
    }

    /// <summary>Matrices monde (repère du modèle) de chaque nœud pour l'animation à l'instant t (pose de référence si null).</summary>
    public Matrix4x4[] Pose(AnimClip? clip, float t, Matrix4x4[]? into = null)
    {
        Matrix4x4[] w = into ?? new Matrix4x4[Names.Length];
        foreach (int i in Order)
        {
            Matrix4x4 local = clip?.Local(Names[i], t) ?? BindLocal[i];
            w[i] = Parent[i] >= 0 ? local * w[Parent[i]] : local;
        }
        return w;
    }

    /// <summary>Déforme une pièce à peau : positions et normales dans le repère du modèle.</summary>
    public void Deform(Part p, Matrix4x4[] world, float[] pos, float[] nrm)
    {
        Skin s = p.Skin!;
        Matrix4x4[] mats = new Matrix4x4[s.Bones.Length];
        for (int b = 0; b < mats.Length; b++)
            mats[b] = p.InvBind[b] * world[p.BoneNode[b]];
        int n = s.Start.Length - 1;
        for (int v = 0; v < n; v++)
        {
            Vector3 bp = new Vector3(p.BasePos[3 * v], p.BasePos[3 * v + 1], p.BasePos[3 * v + 2]);
            Vector3 bn = new Vector3(p.BaseNrm[3 * v], p.BaseNrm[3 * v + 1], p.BaseNrm[3 * v + 2]);
            Vector3 op = Vector3.Zero, on = Vector3.Zero;
            for (int k = s.Start[v]; k < s.Start[v + 1]; k++)
            {
                Matrix4x4 m = mats[s.Bone[k]];
                float wt = s.Weight[k];
                op += Vector3.Transform(bp, m) * wt;
                on += Vector3.TransformNormal(bn, m) * wt;
            }
            if (s.Start[v + 1] == s.Start[v])
            {
                op = bp;
                on = bn;
            }
            pos[3 * v] = op.X;
            pos[3 * v + 1] = op.Y;
            pos[3 * v + 2] = op.Z;
            nrm[3 * v] = on.X;
            nrm[3 * v + 1] = on.Y;
            nrm[3 * v + 2] = on.Z;
        }
    }

    /// <summary>Animations d'une classe : étiquette ([ANIM_PLAYER] AnimNN__Tag) → fichier Gfx\Anim\….anim.</summary>
    public static Dictionary<string, string> AnimTags(ClassFile c)
    {
        Dictionary<string, string> res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i <= 30; i++)
        {
            string? tag = c.Get("ANIM_PLAYER", $"Anim{i:00}__Tag")?.Trim('"', ' ');
            string? file = c.Get("ANIM_PLAYER", $"Anim{i:00}_FileName")?.Trim('"', ' ');
            if (!string.IsNullOrEmpty(tag) && !string.IsNullOrEmpty(file))
                res.TryAdd(tag, file);
        }
        return res;
    }
}
