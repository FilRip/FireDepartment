using System.Numerics;
using System.Text;

namespace OpenFD.Formats;

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

    /// <summary>
    /// Visible au départ : dans le composant A001, après la transformation, le bloc « AA AA FF FF 0A » commence par un
    /// entier puis un octet de visibilité (Paris : 0 pour les victimes enterrées sous les décombres, que le chien révèle).
    /// </summary>
    public bool StartVisible
    {
        get
        {
            if (GetComponent(0xA001) is not { } c)
                return true;
            ReadOnlySpan<byte> b = ComponentBytes(c);
            ReadOnlySpan<byte> mark = stackalloc byte[] { 0xAA, 0xAA, 0xFF, 0xFF, 0x0A };
            int i = b.LastIndexOf(mark);
            return i < 0 || i + 9 >= b.Length || b[i + 9] != 0;
        }
    }
    public ReadOnlySpan<byte> ComponentBytes(Component c) => Raw.AsSpan(c.Offset, c.Size);
}
