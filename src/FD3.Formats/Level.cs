using System.Numerics;
using System.Text;

namespace FD3.Formats;

/// <summary>Types d'entités de la table de classes d'un .level.</summary>
public enum EntityKind : ushort
{
    Placeable = 2, Waypoint = 8, HotPoint = 11, SmokePoint = 12, FireVehicle = 14,
    Group = 15, Trigger = 16, Light = 17, Civilian = 18, Sound = 20,
}

/// <summary>Blocs balisés : AA AA FF FF + version ouvre, CC CC BB BB ferme.</summary>
public static class Chunks
{
    public static bool IsBegin(ReadOnlySpan<byte> d, int p) => p + 4 <= d.Length && d[p] == 0xAA && d[p + 1] == 0xAA && d[p + 2] == 0xFF && d[p + 3] == 0xFF;
    public static bool IsEnd(ReadOnlySpan<byte> d, int p) => p + 4 <= d.Length && d[p] == 0xCC && d[p + 1] == 0xCC && d[p + 2] == 0xBB && d[p + 3] == 0xBB;

    /// <summary>Position juste après la fin du bloc qui commence en p (comptage des marqueurs).</summary>
    public static int NodeEnd(ReadOnlySpan<byte> d, int p)
    {
        if (!IsBegin(d, p)) throw new InvalidDataException($"Bloc attendu en 0x{p:x}");
        int depth = 0, i = p;
        while (i < d.Length - 3)
        {
            if (d[i] == 0xAA && IsBegin(d, i)) { depth++; i += 5; continue; }
            if (d[i] == 0xCC && IsEnd(d, i)) { depth--; i += 4; if (depth == 0) return i; continue; }
            i++;
        }
        throw new InvalidDataException($"Bloc non fermé en 0x{p:x}");
    }

    public static string ReadString(ReadOnlySpan<byte> d, ref int p)
    {
        int n = BitConverter.ToInt32(d[p..]);
        var s = Encoding.Latin1.GetString(d.Slice(p + 4, Math.Max(0, n - 1)));
        p += 4 + n;
        return s;
    }
}

public sealed record Component(ushort Id, int Offset, int Size);

/// <summary>Un objet du niveau : octets bruts conservés pour une réécriture exacte.</summary>
public sealed class LevelObject
{
    public required uint Id { get; init; }
    public required EntityKind Kind { get; init; }
    public required string ClassPath { get; init; }   // ex. design\hotpoint\groundmulti_xs
    public required int Extra { get; init; }
    public required byte[] Raw { get; init; }
    public required int NodeLength { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<Component> Components { get; init; }
    public required int List { get; init; }
    public uint[] Members { get; init; } = Array.Empty<uint>();
    public int TransformOffset { get; init; } = -1;

    public string ClassName => ClassPath[(ClassPath.LastIndexOf('\\') + 1)..];
    public bool HasTransform => TransformOffset >= 0;
    public Vector3 Position => HasTransform ? new(F(13), F(14), F(15)) : default;
    /// <summary>Angles d'Euler en radians (X, Y, Z) ; la matrice vaut Rz·Rx·Ry.</summary>
    public Vector3 Rotation => HasTransform ? new(F(17), F(18), F(19)) : default;
    private float F(int i) => BitConverter.ToSingle(Raw, TransformOffset + i * 4);
    /// <summary>Matrice monde 4×4 (convention vecteur-ligne, translation en dernière ligne).</summary>
    public Matrix4x4 Matrix => HasTransform
        ? new(F(1), F(2), F(3), F(4), F(5), F(6), F(7), F(8), F(9), F(10), F(11), F(12), F(13), F(14), F(15), F(16))
        : Matrix4x4.Identity;

    public Component? GetComponent(ushort id) => Components.FirstOrDefault(c => c.Id == id);
    public ReadOnlySpan<byte> ComponentBytes(Component c) => Raw.AsSpan(c.Offset, c.Size);
}

public sealed class Level
{
    public string Version = "", Build = "", MissionPath = "";
    public uint LoadSteps, NextId;
    public List<LevelObject> Objects = new();
    public List<(uint A, uint B, byte[] Data)> HotPointLinks = new();
    public byte[] Prefix = Array.Empty<byte>(), Suffix = Array.Empty<byte>();
    public int NextIdOffset;

    public LevelObject? Find(uint id) => Objects.FirstOrDefault(o => o.Id == id);

    public static Level Parse(byte[] d)
    {
        var L = new Level();
        int p = 0;
        if (Chunks.ReadString(d, ref p) != "LEVEL") throw new InvalidDataException("Pas un fichier .level");
        L.Version = Chunks.ReadString(d, ref p);
        L.Build = Chunks.ReadString(d, ref p);
        L.LoadSteps = BitConverter.ToUInt32(d, p); p += 4;
        L.MissionPath = Chunks.ReadString(d, ref p);
        if (!Chunks.IsBegin(d, p) || !Chunks.IsBegin(d, p + 9) || !Chunks.IsBegin(d, p + 14)) throw new InvalidDataException("Structure de niveau inattendue");
        L.NextIdOffset = p + 5;
        L.NextId = BitConverter.ToUInt32(d, L.NextIdOffset);
        p += 19;
        L.Prefix = d[..p];
        var entries = new List<(string cls, ushort type, uint id, int extra, int list)>();
        for (int list = 0; list < 2; list++)
        {
            int n = BitConverter.ToInt32(d, p); p += 4;
            for (int i = 0; i < n; i++)
            {
                var cls = Chunks.ReadString(d, ref p);
                entries.Add((cls, BitConverter.ToUInt16(d, p), BitConverter.ToUInt32(d, p + 2), BitConverter.ToInt32(d, p + 6), list));
                p += 10;
            }
        }
        foreach (var e in entries)
        {
            int end = ObjectEnd(d, p);
            int nodeLen = end - p;
            if (!Chunks.IsBegin(d, end) && !Chunks.IsEnd(d, end)) end += 4 + 4 * BitConverter.ToInt32(d, end);
            var o = ParseObject(d[p..end], nodeLen, e.cls, (EntityKind)e.type, e.extra, e.list);
            if (o.Id != e.id) throw new InvalidDataException("Table de classes et objets incohérents");
            L.Objects.Add(o);
            p = end;
        }
        L.Suffix = d[p..];
        L.HotPointLinks = ParseLinks(L.Suffix);
        return L;
    }

    // Fin d'un objet en suivant la taille déclarée des composants : leur contenu peut contenir
    // par hasard des octets identiques aux marqueurs (cas de la mission Institute).
    private static int ObjectEnd(ReadOnlySpan<byte> d, int p)
    {
        int depth = 0;
        while (Chunks.IsBegin(d, p)) { depth++; p += 5; }
        p += 8; if (!Chunks.IsEnd(d, p)) throw new InvalidDataException($"En-tête objet inattendu en 0x{p:x}");
        p += 4; depth--;
        p += 4 + BitConverter.ToInt32(d[p..]);
        p += 5;
        p = Chunks.NodeEnd(d, p);
        int nc = BitConverter.ToInt32(d[p..]); p += 4;
        for (int i = 0; i < nc; i++) p += 6 + BitConverter.ToInt32(d[(p + 2)..]);
        while (depth > 0)
        {
            if (!Chunks.IsEnd(d, p)) p += 4;
            if (!Chunks.IsEnd(d, p)) throw new InvalidDataException($"Fin d'objet inattendue en 0x{p:x}");
            p += 4; depth--;
        }
        return p;
    }

    private static LevelObject ParseObject(byte[] raw, int nodeLen, string cls, EntityKind kind, int extra, int list)
    {
        int p = 0;
        while (Chunks.IsBegin(raw, p)) p += 5;
        uint id = BitConverter.ToUInt32(raw, p);
        p += 12;
        var name = Chunks.ReadString(raw, ref p);
        p += 5;
        p = Chunks.NodeEnd(raw, p);
        int nc = BitConverter.ToInt32(raw, p); p += 4;
        var comps = new List<Component>(nc);
        int tOff = -1;
        for (int i = 0; i < nc; i++)
        {
            ushort cid = BitConverter.ToUInt16(raw, p); int sz = BitConverter.ToInt32(raw, p + 2);
            comps.Add(new Component(cid, p + 6, sz));
            if (cid == 0xA001 && Chunks.IsBegin(raw, p + 6 + 17) && raw[p + 6 + 17 + 4] == 7) tOff = p + 6 + 17 + 5;
            p += 6 + sz;
        }
        var members = Array.Empty<uint>();
        if (nodeLen < raw.Length)
        {
            int n = BitConverter.ToInt32(raw, nodeLen);
            members = new uint[n];
            for (int i = 0; i < n; i++) members[i] = BitConverter.ToUInt32(raw, nodeLen + 4 + 4 * i);
        }
        return new LevelObject { Id = id, Kind = kind, ClassPath = cls, Extra = extra, Raw = raw, NodeLength = nodeLen, Name = name, Components = comps, List = list, Members = members, TransformOffset = tOff };
    }

    private static List<(uint, uint, byte[])> ParseLinks(byte[] s)
    {
        var res = new List<(uint, uint, byte[])>();
        try
        {
            int p = 8;
            for (int k = 0; k < 4; k++) { if (!Chunks.IsBegin(s, p)) return res; p += 5; }
            int n = BitConverter.ToInt32(s, p + 4); p += 8;
            for (int i = 0; i < n; i++)
            {
                uint a = BitConverter.ToUInt32(s, p), b = BitConverter.ToUInt32(s, p + 4); p += 8;
                int e = Chunks.NodeEnd(s, p);
                res.Add((a, b, s[(p + 5)..(e - 4)]));
                p = e;
            }
        }
        catch (Exception) { res.Clear(); }
        return res;
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        var pre = (byte[])Prefix.Clone();
        BitConverter.TryWriteBytes(pre.AsSpan(NextIdOffset), NextId);
        ms.Write(pre);
        for (int list = 0; list < 2; list++)
        {
            var objs = Objects.Where(o => o.List == list).ToList();
            ms.Write(BitConverter.GetBytes(objs.Count));
            foreach (var o in objs)
            {
                ms.Write(BitConverter.GetBytes(o.ClassPath.Length + 1));
                ms.Write(Encoding.Latin1.GetBytes(o.ClassPath)); ms.WriteByte(0);
                ms.Write(BitConverter.GetBytes((ushort)o.Kind));
                ms.Write(BitConverter.GetBytes(o.Id));
                ms.Write(BitConverter.GetBytes(o.Extra));
            }
        }
        for (int list = 0; list < 2; list++) foreach (var o in Objects.Where(o => o.List == list)) ms.Write(o.Raw);
        ms.Write(Suffix);
        return ms.ToArray();
    }
}

/// <summary>Grille de simulation (.grid) : cellules de 100 unités, stockées colonne par colonne.</summary>
public sealed class Grid
{
    public const float CellSize = 100f;
    public int Width, Height;
    public float[] GroundHeight = Array.Empty<float>();
    public ushort[] ZoneA = Array.Empty<ushort>(), ZoneB = Array.Empty<ushort>();

    public int Index(int cx, int cy) => cy * Width + cx;
    public bool Inside(int cx, int cy) => cx >= 0 && cy >= 0 && cx < Width && cy < Height;
    public (int cx, int cy) CellOf(Vector3 p) => ((int)MathF.Floor(p.X / CellSize), (int)MathF.Floor(p.Y / CellSize));

    public static Grid Parse(byte[] d)
    {
        int p = 0;
        if (Chunks.ReadString(d, ref p) != "GRD") throw new InvalidDataException("Pas un fichier .grid");
        Chunks.ReadString(d, ref p); Chunks.ReadString(d, ref p);
        var g = new Grid { Width = BitConverter.ToUInt16(d, p), Height = BitConverter.ToUInt16(d, p + 2) };
        p += 4 + 5 + 4;
        if (!Chunks.IsBegin(d, p)) throw new InvalidDataException("Grille inattendue");
        p += 5;
        int n = g.Width * g.Height;
        g.GroundHeight = new float[n]; g.ZoneA = new ushort[n]; g.ZoneB = new ushort[n];
        for (int i = 0; i < n; i++)
        {
            int e = Chunks.NodeEnd(d, p);
            int cx = i / g.Height, cy = i % g.Height, k = g.Index(cx, cy);
            g.ZoneA[k] = BitConverter.ToUInt16(d, p + 5);
            g.ZoneB[k] = BitConverter.ToUInt16(d, p + 7);
            g.GroundHeight[k] = BitConverter.ToSingle(d, p + 9);
            p = e;
        }
        return g;
    }
}
