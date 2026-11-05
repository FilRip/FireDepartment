using System.Text;

namespace OpenFD.Multiplayer;

/// <summary>Un ordre de joueur (même contenu qu'une ligne du journal d'une sauvegarde).</summary>
public sealed class NetCommand
{
    public int Step;
    public int Player;
    public string Op = "";
    public long A, B;
    public float X, Y;
    public string? S;

    public void Write(BinaryWriter w)
    {
        w.Write(Step);
        w.Write(Player);
        w.Write(Op);
        w.Write(A);
        w.Write(B);
        w.Write(X);
        w.Write(Y);
        w.Write(S != null);
        if (S != null)
            w.Write(S);
    }

    public static NetCommand Read(BinaryReader r)
    {
        NetCommand c = new NetCommand { Step = r.ReadInt32(), Player = r.ReadInt32(), Op = r.ReadString(), A = r.ReadInt64(), B = r.ReadInt64(), X = r.ReadSingle(), Y = r.ReadSingle() };
        if (r.ReadBoolean())
            c.S = r.ReadString();
        return c;
    }

    public override string ToString() => $"{Op} {A} {B} ({X:0},{Y:0}) {S} au pas {Step} (joueur {Player})";
}
