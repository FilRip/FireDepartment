using System.Numerics;
using System.Text;

namespace OpenFD.Formats;

/// <summary>Types d'entités de la table de classes d'un .level.</summary>
public enum EntityKind : ushort
{
    Placeable = 2, Waypoint = 8, HotPoint = 11, SmokePoint = 12, FireVehicle = 14,
    Group = 15, Trigger = 16, Light = 17, Civilian = 18, Sound = 20,
}
