using System.Text;

namespace OpenFD.Multiplayer;

/// <summary>Écriture et lecture des messages (un message = son type puis ses champs).</summary>
public static class NetMessage
{
    public static byte[] Build(MessageType type, Action<BinaryWriter>? body = null)
    {
        using MemoryStream ms = new MemoryStream();
        using (BinaryWriter w = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            w.Write((byte)type);
            body?.Invoke(w);
        }
        return ms.ToArray();
    }

    public static BinaryReader Reader(byte[] payload, out MessageType type)
    {
        BinaryReader r = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
        type = (MessageType)r.ReadByte();
        return r;
    }
}
