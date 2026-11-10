using System.IO.Compression;
using System.Text;

namespace OpenFD.MG;

/// <summary>
/// Écrit une archive .pak / .bank de Fire Department 3 : un zip ordinaire (fichiers compressés « deflate ») dont les
/// signatures commencent par « QL » au lieu de « PK », noms avec « \ », comme celles du jeu.
/// </summary>
public sealed class PakWriter : IDisposable
{
    private sealed record Entry(byte[] Name, uint Crc, uint Packed, uint Size, uint Offset, ushort Method);

    private readonly Stream _out;
    private readonly List<Entry> _entries = new();
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private const ushort Version = 20;
    private const ushort Date1980 = 0x21;   // 1er janvier 1980 (date DOS)

    public PakWriter(string path)
    {
        _out = new FileStream(path, FileMode.Create, FileAccess.Write);
    }

    public int Count => _entries.Count;

    /// <summary>Ajoute un fichier (nom intérieur : « Bank_fasad.mesh », « Shaders\… ») ; un nom déjà présent est ignoré.</summary>
    public void Add(string name, byte[] data)
    {
        if (!_names.Add(name))
            return;
        byte[] packed;
        using (MemoryStream ms = new MemoryStream())
        {
            using (DeflateStream z = new DeflateStream(ms, CompressionLevel.Fastest, true))
                z.Write(data);
            packed = ms.ToArray();
        }
        ushort method = 8;
        if (packed.Length >= data.Length)
        {
            packed = data;
            method = 0;
        }
        byte[] nb = Encoding.Latin1.GetBytes(name.Replace('/', '\\'));
        Entry e = new Entry(nb, Crc32.Of(data), (uint)packed.Length, (uint)data.Length, (uint)_out.Position, method);
        using BinaryWriter w = new BinaryWriter(_out, Encoding.Latin1, true);
        w.Write(new byte[] { (byte)'Q', (byte)'L', 3, 4 });
        w.Write(Version);
        w.Write((ushort)0);
        w.Write(method);
        w.Write((ushort)0);
        w.Write(Date1980);
        w.Write(e.Crc);
        w.Write(e.Packed);
        w.Write(e.Size);
        w.Write((ushort)nb.Length);
        w.Write((ushort)0);
        w.Write(nb);
        w.Write(packed);
        _entries.Add(e);
    }

    public void Dispose()
    {
        using (BinaryWriter w = new BinaryWriter(_out, Encoding.Latin1, true))
        {
            uint start = (uint)_out.Position;
            foreach (Entry e in _entries)
            {
                w.Write(new byte[] { (byte)'Q', (byte)'L', 1, 2 });
                w.Write(Version);
                w.Write(Version);
                w.Write((ushort)0);
                w.Write(e.Method);
                w.Write((ushort)0);
                w.Write(Date1980);
                w.Write(e.Crc);
                w.Write(e.Packed);
                w.Write(e.Size);
                w.Write((ushort)e.Name.Length);
                w.Write((ushort)0);
                w.Write((ushort)0);
                w.Write((ushort)0);
                w.Write((ushort)0);
                w.Write(0u);
                w.Write(e.Offset);
                w.Write(e.Name);
            }
            uint size = (uint)_out.Position - start;
            w.Write(new byte[] { (byte)'Q', (byte)'L', 5, 6 });
            w.Write((ushort)0);
            w.Write((ushort)0);
            w.Write((ushort)_entries.Count);
            w.Write((ushort)_entries.Count);
            w.Write(size);
            w.Write(start);
            w.Write((ushort)0);
        }
        _out.Dispose();
    }
}
