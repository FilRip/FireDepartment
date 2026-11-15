using System.Numerics;

namespace OpenFD.Game;

/// <summary>
/// Salle de secours marquée d'une croix rouge (« cross_symbol » seul, sans poste de soins dessous : Eurotunnel, « Emmenez
/// les victimes dans la salle marquée d'une croix rouge »). Les victimes y sont posées, pas mises à l'abri : c'est le
/// script de la mission qui compte celles qui s'y trouvent. Active si la croix est visible.
/// </summary>
public sealed class DropZone
{
    public uint Id;
    public string Name = "";
    public Vector3 Position;
    public bool StartVisible;
}
