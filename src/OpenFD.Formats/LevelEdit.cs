using System.Numerics;
using System.Text;

namespace OpenFD.Formats;

/// <summary>
/// Modifications d'un niveau pour l'éditeur de missions. Un objet n'est jamais changé sur place : chaque fonction rend un
/// nouvel objet, ses octets recopiés puis modifiés (le reste de l'objet, que l'on ne comprend pas toujours, reste tel quel).
/// </summary>
public static class LevelEdit
{
    private static LevelObject Reparse(LevelObject o, byte[] raw, int nodeLength, string? classPath = null) =>
        Level.ParseObject(raw, nodeLength, classPath ?? o.ClassPath, o.Kind, o.Extra, o.List);

    /// <summary>Matrice monde d'un objet du jeu : rotations Z, X puis Y (vecteur-ligne), puis la position.</summary>
    public static Matrix4x4 MatrixOf(Vector3 euler, Vector3 position)
    {
        Matrix4x4 m = Matrix4x4.CreateRotationZ(euler.Z) * Matrix4x4.CreateRotationX(euler.X) * Matrix4x4.CreateRotationY(euler.Y);
        m.Translation = position;
        return m;
    }

    /// <summary>
    /// Place et oriente l'objet. Bloc de transformation du composant A001 : angle Z, matrice 4 × 4, angles X Y Z, puis la
    /// position et les angles une seconde fois (26 flottants ; vérifié sur toutes les missions de FD3).
    /// </summary>
    public static LevelObject WithTransform(LevelObject o, Vector3 position, Vector3 euler)
    {
        if (!o.HasTransform)
            return o;
        byte[] raw = (byte[])o.Raw.Clone();
        int t = o.TransformOffset;
        Matrix4x4 m = MatrixOf(euler, position);
        void W(int i, float v)
        {
            if (t + 4 * i + 4 <= raw.Length)
                BitConverter.TryWriteBytes(raw.AsSpan(t + 4 * i), v);
        }
        W(0, euler.Z);
        float[] mm = { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 };
        for (int i = 0; i < 16; i++)
            W(1 + i, mm[i]);
        W(17, euler.X);
        W(18, euler.Y);
        W(19, euler.Z);
        Component? a001 = o.GetComponent(0xA001);
        if (a001 != null && t + 4 * 26 <= a001.Offset + a001.Size)
        {
            W(20, position.X);
            W(21, position.Y);
            W(22, position.Z);
            W(23, euler.X);
            W(24, euler.Y);
            W(25, euler.Z);
        }
        return Reparse(o, raw, o.NodeLength);
    }

    /// <summary>Position du nom dans l'en-tête de l'objet (après les ouvertures de blocs, le numéro et un entier).</summary>
    private static int NameOffset(byte[] raw)
    {
        int p = 0;
        while (Chunks.IsBegin(raw, p))
            p += 5;
        return p + 12;
    }

    public static LevelObject WithName(LevelObject o, string name)
    {
        int p = NameOffset(o.Raw);
        int len = BitConverter.ToInt32(o.Raw, p);
        byte[] nb = Encoding.Latin1.GetBytes(name);
        using MemoryStream ms = new MemoryStream();
        ms.Write(o.Raw, 0, p);
        ms.Write(BitConverter.GetBytes(nb.Length + 1));
        ms.Write(nb);
        ms.WriteByte(0);
        ms.Write(o.Raw, p + 4 + len, o.Raw.Length - (p + 4 + len));
        int delta = nb.Length + 1 - len;
        return Reparse(o, ms.ToArray(), o.NodeLength + delta);
    }

    /// <summary>Nouveau numéro : il est écrit dans l'en-tête et répété au début de chaque composant.</summary>
    public static LevelObject WithId(LevelObject o, uint id)
    {
        byte[] raw = (byte[])o.Raw.Clone();
        byte[] from = BitConverter.GetBytes(o.Id), to = BitConverter.GetBytes(id);
        for (int i = 0; i + 4 <= raw.Length; i++)
            if (raw[i] == from[0] && raw[i + 1] == from[1] && raw[i + 2] == from[2] && raw[i + 3] == from[3])
            {
                to.CopyTo(raw, i);
                i += 3;
            }
        return Reparse(o, raw, o.NodeLength);
    }

    public static LevelObject WithClass(LevelObject o, string classPath) => Reparse(o, (byte[])o.Raw.Clone(), o.NodeLength, classPath);

    /// <summary>Copie d'un objet modèle sous un autre numéro et un autre nom (objet ajouté par l'éditeur).</summary>
    public static LevelObject Clone(LevelObject template, uint id, string name, string? classPath = null)
    {
        LevelObject o = WithName(WithId(template, id), name);
        return classPath != null ? WithClass(o, classPath) : o;
    }

    /// <summary>Octet « visible au départ » du composant A001 (voir LevelObject.StartVisible).</summary>
    public static LevelObject WithStartVisible(LevelObject o, bool visible)
    {
        if (o.GetComponent(0xA001) is not { } c || o.StartVisible == visible)
            return o;
        byte[] raw = (byte[])o.Raw.Clone();
        ReadOnlySpan<byte> b = raw.AsSpan(c.Offset, c.Size);
        ReadOnlySpan<byte> mark = stackalloc byte[] { 0xAA, 0xAA, 0xFF, 0xFF, 0x0A };
        int i = b.LastIndexOf(mark);
        if (i < 0 || i + 10 >= b.Length)
            return o;
        raw[c.Offset + i + 10] = (byte)(visible ? 1 : 0);
        return Reparse(o, raw, o.NodeLength);
    }

    /// <summary>Vie d'un objet (composant A024 : octet 4 = 0x0C, vie puis vie maximale en flottants).</summary>
    public static (float life, float max)? Life(LevelObject o)
    {
        if (o.GetComponent(0xA024) is not { } c || c.Size < 13 || o.Raw[c.Offset + 4] != 0x0C)
            return null;
        return (BitConverter.ToSingle(o.Raw, c.Offset + 5), BitConverter.ToSingle(o.Raw, c.Offset + 9));
    }

    public static LevelObject WithLife(LevelObject o, float life)
    {
        if (o.GetComponent(0xA024) is not { } c || c.Size < 13 || o.Raw[c.Offset + 4] != 0x0C)
            return o;
        byte[] raw = (byte[])o.Raw.Clone();
        BitConverter.TryWriteBytes(raw.AsSpan(c.Offset + 5), life);
        return Reparse(o, raw, o.NodeLength);
    }

    /// <summary>
    /// État de départ d'un foyer : composant FE00 (bloc v3 : allumé à +5, intensité à +10), sinon FD00 (intensité seule,
    /// juste après le bloc d'état). Null : format inconnu.
    /// </summary>
    public static (bool lit, float intensity, bool full)? FireState(LevelObject o)
    {
        if (o.GetComponent(0xFE00) is { } c && c.Size >= 140 && Chunks.IsBegin(o.Raw, c.Offset + 17) && o.Raw[c.Offset + 21] == 3)
            return (o.Raw[c.Offset + 27] != 0, BitConverter.ToSingle(o.Raw, c.Offset + 32), true);
        if (FireIntensityOffset(o) is { } at)
        {
            float v = BitConverter.ToSingle(o.Raw, at);
            return (v > 0, v, false);
        }
        return null;
    }

    private static int? FireIntensityOffset(LevelObject o)
    {
        if (o.GetComponent(0xFD00) is not { } d || d.Size < 32)
            return null;
        ReadOnlySpan<byte> b = o.Raw.AsSpan(d.Offset, d.Size);
        if (!Chunks.IsBegin(b, 17) || !Chunks.IsBegin(b, 22))
            return null;
        int e = Chunks.NodeEnd(b, 22);
        return e + 4 <= b.Length ? d.Offset + e : null;
    }

    public static LevelObject WithFire(LevelObject o, bool lit, float intensity)
    {
        if (FireState(o) is { } now && now.lit == lit && now.intensity == intensity)
            return o;
        byte[] raw = (byte[])o.Raw.Clone();
        if (o.GetComponent(0xFE00) is { } c && c.Size >= 140 && Chunks.IsBegin(raw, c.Offset + 17) && raw[c.Offset + 21] == 3)
        {
            // drapeau « allumé » et intensité de départ (un foyer éteint garde son intensité : le jeu ne la prend pas)
            raw[c.Offset + 27] = (byte)(lit ? 1 : 0);
            BitConverter.TryWriteBytes(raw.AsSpan(c.Offset + 32), intensity);
        }
        else if (FireIntensityOffset(o) is { } at)
        {
            // sans drapeau : éteint = intensité nulle (ou négative, déjà éteint)
            float cur = BitConverter.ToSingle(raw, at);
            BitConverter.TryWriteBytes(raw.AsSpan(at), lit ? intensity : cur <= 0 ? cur : 0);
        }
        else
            return o;
        return Reparse(o, raw, o.NodeLength);
    }

    /// <summary>
    /// Paramètres d'un foyer recopiés dans son bloc d'instance (FE00) : ils y remplacent ceux de sa classe ; à refaire quand
    /// on change la classe (le type de feu) d'un foyer. Mêmes positions que FireWorld.InstanceState.
    /// </summary>
    public static LevelObject WithFireParams(LevelObject o, float max, float min, float lifeRange, float heatRange, float lightRange,
        float lightThreshold, float tempo, float growth, float death, float growthTime, float slope)
    {
        if (o.GetComponent(0xFE00) is not { } c || c.Size < 140 || !Chunks.IsBegin(o.Raw, c.Offset + 17) || o.Raw[c.Offset + 21] != 3)
            return o;
        byte[] raw = (byte[])o.Raw.Clone();
        ReadOnlySpan<byte> b = raw.AsSpan(c.Offset, c.Size);
        int last = LastEnd(b, b.Length), prev = LastEnd(b, last);
        if (last < 0 || prev < 0 || last - (prev + 4) != 96)
            return o;
        int t = c.Offset + prev + 4;
        void W(int at, float v) => BitConverter.TryWriteBytes(raw.AsSpan(at), v);
        W(c.Offset + 36, max);
        W(t + 46, min);
        W(t + 34, lifeRange);
        W(t + 54, heatRange);
        W(t + 58, lightRange);
        W(t + 62, lightThreshold);
        W(t + 66, tempo);
        W(t + 70, growth);
        W(t + 74, death);
        W(t + 78, growthTime);
        W(t + 83, slope);
        return Reparse(o, raw, o.NodeLength);
    }

    private static int LastEnd(ReadOnlySpan<byte> b, int before)
    {
        for (int i = before - 4; i >= 0; i--)
            if (Chunks.IsEnd(b, i))
                return i;
        return -1;
    }

    /// <summary>Zone de déclenchement (composant A028) : fonction appelée à l'entrée, à la sortie, active, taille.</summary>
    public static (string onEnter, string onLeave, bool enabled, Vector3 size)? Trigger(LevelObject o)
    {
        if (TriggerParts(o) is not { } t)
            return null;
        return (t.onEnter, t.onLeave, t.enabled, t.size);
    }

    private static (string onEnter, string onLeave, bool enabled, Vector3 size, int start, int end, int sizeAt)? TriggerParts(LevelObject o)
    {
        if (o.GetComponent(0xA028) is not { } c)
            return null;
        byte[] b = o.ComponentBytes(c).ToArray();
        int k = b.AsSpan().IndexOf(new byte[] { 0xCC, 0xCC, 0xBB, 0xBB });
        if (k < 0 || k + 8 > b.Length)
            return null;
        int p = k + 4, len = BitConverter.ToInt32(b, p);
        if (len < 1 || p + 4 + len > b.Length)
            return null;
        string onEnter = Encoding.Latin1.GetString(b, p + 4, len - 1);
        int q = p + 4 + len;
        if (q + 4 > b.Length)
            return null;
        int len2 = BitConverter.ToInt32(b, q);
        if (len2 < 1 || len2 > 256 || q + 4 + len2 >= b.Length)
            return null;
        string onLeave = Encoding.Latin1.GetString(b, q + 4, len2 - 1);
        bool enabled = b[q + 4 + len2] != 0;
        int e = b.Length - 4 - 12;
        Vector3 size = new Vector3(BitConverter.ToSingle(b, e), BitConverter.ToSingle(b, e + 4), BitConverter.ToSingle(b, e + 8));
        return (onEnter, onLeave, enabled, size, p, q + 4 + len2, e);
    }

    public static LevelObject WithTrigger(LevelObject o, string onEnter, string onLeave, bool enabled, Vector3 size)
    {
        if (TriggerParts(o) is not { } t || o.GetComponent(0xA028) is not { } c)
            return o;
        byte[] b = o.ComponentBytes(c).ToArray();
        using MemoryStream ms = new MemoryStream();
        ms.Write(b, 0, t.start);
        foreach (string s in new[] { onEnter, onLeave })
        {
            byte[] sb = Encoding.Latin1.GetBytes(s);
            ms.Write(BitConverter.GetBytes(sb.Length + 1));
            ms.Write(sb);
            ms.WriteByte(0);
        }
        ms.WriteByte((byte)(enabled ? 1 : 0));
        ms.Write(b, t.end + 1, t.sizeAt - (t.end + 1));
        ms.Write(BitConverter.GetBytes(size.X));
        ms.Write(BitConverter.GetBytes(size.Y));
        ms.Write(BitConverter.GetBytes(size.Z));
        ms.Write(b, t.sizeAt + 12, b.Length - (t.sizeAt + 12));
        return WithComponent(o, c, ms.ToArray());
    }

    /// <summary>Remplace le contenu d'un composant (sa taille, écrite juste avant, suit).</summary>
    public static LevelObject WithComponent(LevelObject o, Component c, byte[] data)
    {
        using MemoryStream ms = new MemoryStream();
        ms.Write(o.Raw, 0, c.Offset - 4);
        ms.Write(BitConverter.GetBytes(data.Length));
        ms.Write(data);
        ms.Write(o.Raw, c.Offset + c.Size, o.Raw.Length - (c.Offset + c.Size));
        return Reparse(o, ms.ToArray(), o.NodeLength + data.Length - c.Size);
    }

    /// <summary>Groupe sans ce membre (objet supprimé).</summary>
    public static LevelObject WithoutMember(LevelObject group, uint id)
    {
        if (!group.Members.Contains(id))
            return group;
        uint[] keep = group.Members.Where(m => m != id).ToArray();
        using MemoryStream ms = new MemoryStream();
        ms.Write(group.Raw, 0, group.NodeLength);
        ms.Write(BitConverter.GetBytes(keep.Length));
        foreach (uint m in keep)
            ms.Write(BitConverter.GetBytes(m));
        int after = group.NodeLength + 4 + 4 * group.Members.Length;
        if (after < group.Raw.Length)
            ms.Write(group.Raw, after, group.Raw.Length - after);
        return Reparse(group, ms.ToArray(), group.NodeLength);
    }

    /// <summary>Chemin du fichier .mis écrit dans l'en-tête du niveau (« …\Design\Mission\London\mission_London.mis »).</summary>
    public static void SetMissionPath(Level level, string path)
    {
        byte[] pre = level.Prefix;
        int p = 0;
        Chunks.ReadString(pre, ref p);
        Chunks.ReadString(pre, ref p);
        Chunks.ReadString(pre, ref p);
        p += 4;
        int at = p;
        Chunks.ReadString(pre, ref p);
        byte[] nb = Encoding.Latin1.GetBytes(path);
        using MemoryStream ms = new MemoryStream();
        ms.Write(pre, 0, at);
        ms.Write(BitConverter.GetBytes(nb.Length + 1));
        ms.Write(nb);
        ms.WriteByte(0);
        ms.Write(pre, p, pre.Length - p);
        int delta = (at + 4 + nb.Length + 1) - p;
        level.Prefix = ms.ToArray();
        level.NextIdOffset += delta;
        level.MissionPath = path;
    }

    /// <summary>Un octet d'un composant (null : composant absent ou trop court).</summary>
    public static byte? ComponentByte(LevelObject o, ushort component, int offset) =>
        o.GetComponent(component) is { } c && offset < c.Size ? o.Raw[c.Offset + offset] : null;

    public static LevelObject WithComponentByte(LevelObject o, ushort component, int offset, byte value)
    {
        if (o.GetComponent(component) is not { } c || offset >= c.Size)
            return o;
        byte[] raw = (byte[])o.Raw.Clone();
        raw[c.Offset + offset] = value;
        return Reparse(o, raw, o.NodeLength);
    }

    /// <summary>Numéro libre pour un nouvel objet.</summary>
    public static uint NewId(Level level)
    {
        uint max = level.Objects.Count > 0 ? level.Objects.Max(o => o.Id) : 0;
        uint id = Math.Max(level.NextId, max + 1);
        level.NextId = id + 1;
        return id;
    }
}
