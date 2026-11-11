using System.Text;

namespace OpenFD.ConvFD1;

/// <summary>Lecture des fichiers binaires de Fire Department 1 (petit-boutiste ; chaînes : u32 longueur avec le 0 final, puis les octets).</summary>
public sealed class Fd1Reader
{
    private readonly byte[] _d;

    public int Pos;

    public Fd1Reader(byte[] data, int pos = 0)
    {
        _d = data;
        Pos = pos;
    }

    public int Length => _d.Length;

    public byte U8()
    {
        byte v = _d[Pos];
        Pos++;
        return v;
    }

    public ushort U16()
    {
        ushort v = BitConverter.ToUInt16(_d, Pos);
        Pos += 2;
        return v;
    }

    public int I32()
    {
        int v = BitConverter.ToInt32(_d, Pos);
        Pos += 4;
        return v;
    }

    public uint U32()
    {
        uint v = BitConverter.ToUInt32(_d, Pos);
        Pos += 4;
        return v;
    }

    public float F32()
    {
        float v = BitConverter.ToSingle(_d, Pos);
        Pos += 4;
        return v;
    }

    public string Str()
    {
        int n = I32();
        if (n < 0 || Pos + n > _d.Length)
            throw new InvalidDataException($"chaîne invalide à {Pos - 4}");
        string s = Encoding.Latin1.GetString(_d, Pos, n).TrimEnd('\0');
        Pos += n;
        return s;
    }

    public void Skip(long n)
    {
        if (n < 0 || Pos + n > _d.Length)
            throw new InvalidDataException($"données tronquées à {Pos}");
        Pos += (int)n;
    }
}
