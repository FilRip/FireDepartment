using System.Numerics;
using System.Text;

namespace OpenFD.Formats;

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
        Level L = new Level();
        int p = 0;
        if (Chunks.ReadString(d, ref p) != "LEVEL")
            throw new InvalidDataException("Pas un fichier .level");
        L.Version = Chunks.ReadString(d, ref p);
        L.Build = Chunks.ReadString(d, ref p);
        L.LoadSteps = BitConverter.ToUInt32(d, p);
        p += 4;
        L.MissionPath = Chunks.ReadString(d, ref p);
        if (!Chunks.IsBegin(d, p) || !Chunks.IsBegin(d, p + 9) || !Chunks.IsBegin(d, p + 14))
            throw new InvalidDataException("Structure de niveau inattendue");
        L.NextIdOffset = p + 5;
        L.NextId = BitConverter.ToUInt32(d, L.NextIdOffset);
        p += 19;
        L.Prefix = d[..p];
        List<(string cls, ushort type, uint id, int extra, int list)> entries = new List<(string cls, ushort type, uint id, int extra, int list)>();
        for (int list = 0; list < 2; list++)
        {
            int n = BitConverter.ToInt32(d, p);
            p += 4;
            for (int i = 0; i < n; i++)
            {
                string cls = Chunks.ReadString(d, ref p);
                entries.Add((cls, BitConverter.ToUInt16(d, p), BitConverter.ToUInt32(d, p + 2), BitConverter.ToInt32(d, p + 6), list));
                p += 10;
            }
        }
        foreach ((string cls, ushort type, uint id, int extra, int list) e in entries)
        {
            int end = ObjectEnd(d, p);
            int nodeLen = end - p;
            if (!Chunks.IsBegin(d, end) && !Chunks.IsEnd(d, end))
                end += 4 + 4 * BitConverter.ToInt32(d, end);
            LevelObject o = ParseObject(d[p..end], nodeLen, e.cls, (EntityKind)e.type, e.extra, e.list);
            if (o.Id != e.id)
                throw new InvalidDataException("Table de classes et objets incohérents");
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
        while (Chunks.IsBegin(d, p))
        {
            depth++;
            p += 5;
        }
        p += 8;
        if (!Chunks.IsEnd(d, p))
            throw new InvalidDataException($"En-tête objet inattendu en 0x{p:x}");
        p += 4;
        depth--;
        p += 4 + BitConverter.ToInt32(d[p..]);
        p += 5;
        p = Chunks.NodeEnd(d, p);
        int nc = BitConverter.ToInt32(d[p..]);
        p += 4;
        for (int i = 0; i < nc; i++)
            p += 6 + BitConverter.ToInt32(d[(p + 2)..]);
        while (depth > 0)
        {
            if (!Chunks.IsEnd(d, p))
                p += 4;
            if (!Chunks.IsEnd(d, p))
                throw new InvalidDataException($"Fin d'objet inattendue en 0x{p:x}");
            p += 4;
            depth--;
        }
        return p;
    }

    private static LevelObject ParseObject(byte[] raw, int nodeLen, string cls, EntityKind kind, int extra, int list)
    {
        int p = 0;
        while (Chunks.IsBegin(raw, p))
            p += 5;
        uint id = BitConverter.ToUInt32(raw, p);
        p += 12;
        string name = Chunks.ReadString(raw, ref p);
        p += 5;
        p = Chunks.NodeEnd(raw, p);
        int nc = BitConverter.ToInt32(raw, p);
        p += 4;
        List<Component> comps = new List<Component>(nc);
        int tOff = -1;
        for (int i = 0; i < nc; i++)
        {
            ushort cid = BitConverter.ToUInt16(raw, p);
            int sz = BitConverter.ToInt32(raw, p + 2);
            comps.Add(new Component(cid, p + 6, sz));
            if (cid == 0xA001 && Chunks.IsBegin(raw, p + 6 + 17) && raw[p + 6 + 17 + 4] == 7)
                tOff = p + 6 + 17 + 5;
            p += 6 + sz;
        }
        uint[] members = Array.Empty<uint>();
        if (nodeLen < raw.Length)
        {
            int n = BitConverter.ToInt32(raw, nodeLen);
            members = new uint[n];
            for (int i = 0; i < n; i++)
                members[i] = BitConverter.ToUInt32(raw, nodeLen + 4 + 4 * i);
        }
        return new LevelObject { Id = id, Kind = kind, ClassPath = cls, Extra = extra, Raw = raw, NodeLength = nodeLen, Name = name, Components = comps, List = list, Members = members, TransformOffset = tOff };
    }

    private static List<(uint, uint, byte[])> ParseLinks(byte[] s)
    {
        List<(uint, uint, byte[])> res = new List<(uint, uint, byte[])>();
        try
        {
            int p = 8;
            for (int k = 0; k < 4; k++)
            {
                if (!Chunks.IsBegin(s, p))
                    return res;
                p += 5;
            }
            int n = BitConverter.ToInt32(s, p + 4);
            p += 8;
            for (int i = 0; i < n; i++)
            {
                uint a = BitConverter.ToUInt32(s, p), b = BitConverter.ToUInt32(s, p + 4);
                p += 8;
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
        using MemoryStream ms = new MemoryStream();
        byte[] pre = (byte[])Prefix.Clone();
        BitConverter.TryWriteBytes(pre.AsSpan(NextIdOffset), NextId);
        ms.Write(pre);
        for (int list = 0; list < 2; list++)
        {
            List<LevelObject> objs = Objects.Where(o => o.List == list).ToList();
            ms.Write(BitConverter.GetBytes(objs.Count));
            foreach (LevelObject o in objs)
            {
                ms.Write(BitConverter.GetBytes(o.ClassPath.Length + 1));
                ms.Write(Encoding.Latin1.GetBytes(o.ClassPath));
                ms.WriteByte(0);
                ms.Write(BitConverter.GetBytes((ushort)o.Kind));
                ms.Write(BitConverter.GetBytes(o.Id));
                ms.Write(BitConverter.GetBytes(o.Extra));
            }
        }
        for (int list = 0; list < 2; list++)
            foreach (LevelObject o in Objects.Where(o => o.List == list))
                ms.Write(o.Raw);
        ms.Write(Suffix);
        return ms.ToArray();
    }
}
