using System.IO.Compression;
using System.Text;

namespace OpenFD.Formats;

/// <summary>
/// Archive .pak / .bank de Fire Department 3 : un zip classique dont les signatures
/// commencent par « QL » au lieu de « PK ». Les noms internes utilisent « \ ».
/// </summary>
public sealed class Pak
{
    public sealed record Entry(string Name, int Method, uint CompressedSize, uint Size, uint LocalHeaderOffset);

    private readonly byte[] _data;
    private readonly Dictionary<string, Entry> _byName = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<Entry> Entries { get; }
    public string Path { get; }

    public Pak(string path) : this(File.ReadAllBytes(path), path) { }

    public Pak(byte[] data, string path = "")
    {
        _data = data; Path = path;
        // « fin du répertoire central » : 22 octets à la fin de l'archive, suivis d'un commentaire de 65 535 octets au plus.
        // On la cherche donc seulement dans les 22 + 65 535 derniers octets, en partant de la fin : c'est rapide, et une
        // suite d'octets « QL\5\6 » qui se trouverait par hasard au milieu des données compressées n'est jamais prise.
        const int EndRecord = 22, MaxComment = 65_535;
        int eocd = -1;
        for (int i = data.Length - EndRecord; i >= Math.Max(0, data.Length - EndRecord - MaxComment); i--)
            if ((data[i] == 'Q' || data[i] == 'P') && (data[i + 1] == 'L' || data[i + 1] == 'K') && data[i + 2] == 5 && data[i + 3] == 6)
            {
                eocd = i;
                break;
            }
        if (eocd < 0)
            throw new InvalidDataException("Archive non reconnue : " + path);
        int n = U16(eocd + 10);
        int p = (int)U32(eocd + 16);
        List<Entry> list = new List<Entry>(n);
        for (int i = 0; i < n; i++)
        {
            int fl = U16(p + 28), xl = U16(p + 30), cl = U16(p + 32);
            Entry e = new Entry(Encoding.Latin1.GetString(data, p + 46, fl), U16(p + 10), U32(p + 20), U32(p + 24), U32(p + 42));
            list.Add(e);
            _byName[Normalize(e.Name)] = e;
            p += 46 + fl + xl + cl;
        }
        Entries = list;
    }

    public static string Normalize(string name) => name.Replace('/', '\\').TrimStart('\\');

    public bool Contains(string name) => _byName.ContainsKey(Normalize(name));

    public byte[] Read(string name)
    {
        if (!_byName.TryGetValue(Normalize(name), out Entry? e))
            throw new FileNotFoundException(name + " absent de " + Path);
        return Read(e);
    }

    public byte[] Read(Entry e)
    {
        int p = (int)e.LocalHeaderOffset;
        int start = p + 30 + U16(p + 26) + U16(p + 28);
        if (e.Method == 0)
            return _data.AsSpan(start, (int)e.CompressedSize).ToArray();
        if (e.Method != 8)
            throw new NotSupportedException("Compression " + e.Method);
        using MemoryStream src = new MemoryStream(_data, start, (int)e.CompressedSize, false);
        using DeflateStream z = new DeflateStream(src, CompressionMode.Decompress);
        byte[] outBuf = new byte[e.Size];
        int read = 0;
        while (read < outBuf.Length)
        {
            int k = z.Read(outBuf, read, outBuf.Length - read);
            if (k == 0)
                break;
            read += k;
        }
        return outBuf;
    }

    private int U16(int p) => _data[p] | _data[p + 1] << 8;
    private uint U32(int p) => BitConverter.ToUInt32(_data, p);
}
