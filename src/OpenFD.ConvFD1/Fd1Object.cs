using System.Numerics;

namespace OpenFD.ConvFD1;

/// <summary>
/// Objet posé dans un niveau FD1 : modèle (.flx, sans extension, en majuscules), classe FD1 (ObjetDecor, ConteneurComposant,
/// Mechanisme, ActeurUnit, Camion…), nom (celui des scripts) et matrice dans le monde (unités FD1 : 1 = 10 m).
/// </summary>
public sealed class Fd1Object
{
    public string Model = "";
    public string Class = "";
    public string Name = "";
    public string Race = "";
    public Matrix4x4 World;
    public long Offset;
}
