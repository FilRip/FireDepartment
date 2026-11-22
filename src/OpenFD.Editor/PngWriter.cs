using System.IO.Compression;

namespace OpenFD.MG;

/// <summary>Écrit une image RGBA (8 bits par composante) en PNG, sans librairie : zlib de .NET et CRC32 de l'éditeur.</summary>
internal static class PngWriter
{
    public static void Write(string path, int w, int h, byte[] rgba)
    {
        using FileStream f = File.Create(path);
        f.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        byte[] ihdr = new byte[13];
        BigEndian(ihdr, 0, (uint)w);
        BigEndian(ihdr, 4, (uint)h);
        ihdr[8] = 8;    // 8 bits
        ihdr[9] = 6;    // RGBA
        Chunk(f, "IHDR", ihdr);
        using MemoryStream raw = new MemoryStream();
        using (ZLibStream z = new ZLibStream(raw, CompressionLevel.Optimal, true))
            for (int y = 0; y < h; y++)
            {
                z.WriteByte(0);   // pas de filtre
                z.Write(rgba, y * w * 4, w * 4);
            }
        Chunk(f, "IDAT", raw.ToArray());
        Chunk(f, "IEND", Array.Empty<byte>());
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        byte[] len = new byte[4];
        BigEndian(len, 0, (uint)data.Length);
        s.Write(len);
        byte[] body = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++)
            body[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, body, 4, data.Length);
        s.Write(body);
        byte[] crc = new byte[4];
        BigEndian(crc, 0, Crc32.Of(body));
        s.Write(crc);
    }

    private static void BigEndian(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24);
        b[o + 1] = (byte)(v >> 16);
        b[o + 2] = (byte)(v >> 8);
        b[o + 3] = (byte)v;
    }
}
