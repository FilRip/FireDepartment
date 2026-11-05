using System.Numerics;
using OpenFD.Formats;
using OpenFD.Script;

namespace OpenFD.Game;

/// <summary>Point d'eau de la carte (bouche d'incendie…) : classe [CAPACITIES_RESERVE] ResupplyRadius / ResupplyPower.</summary>
public sealed record SupplyPoint(uint Id, string Name, Vector3 Position, float Radius, float Power, string Agent);
