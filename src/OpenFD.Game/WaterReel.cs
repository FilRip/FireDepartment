using System.Numerics;

namespace OpenFD.Game;

/// <summary>
/// Enrouleur de tuyau posé dans le niveau (« veh_p_aerial_factice » : faux véhicule, modèle urban_bobine, Eurotunnel) :
/// un point d'eau sans fin ([CAPACITIES_RESERVE] InfiniteReserve, ResupplyPower) où les pompiers à pied remplissent leur
/// réserve, comme près d'un camion. Actif s'il est visible.
/// </summary>
public sealed class WaterReel
{
    public uint Id;
    public string Name = "";
    public Vector3 Position;
    /// <summary>Modèle de l'enrouleur (Gfx\…) et sa place dans le monde.</summary>
    public string Model = "";
    public Matrix4x4 World;
    public bool StartVisible;
    /// <summary>Eau rendue (ResupplyPower, × ResupplyScale par seconde).</summary>
    public float Power = 150f;
    public string Agent = "WATER";
}
