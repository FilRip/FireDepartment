using System.Numerics;

namespace OpenFD.Game;

/// <summary>
/// Poste de soins posé dans le niveau (« veh_p_ambulance_factice » : Duseldorf, Ch_Plant, TMI) : une flèche verte au sol
/// (modèle Exit_cliking) sous une croix rouge. Un pompier (pas un véhicule) qui s'y arrête se soigne, comme près d'une
/// ambulance. Actif s'il est visible (visibilité de départ du niveau, puis ENT_SetVisibility).
/// </summary>
public sealed class HealPoint
{
    public uint Id;
    public string Name = "";
    public Vector3 Position;
    /// <summary>Modèle de la flèche (Gfx\…) et sa place dans le monde.</summary>
    public string Model = "";
    public Matrix4x4 World;
    public bool StartVisible;
}
