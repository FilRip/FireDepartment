using System.Numerics;
using OpenFD.Fire;
using OpenFD.Formats;

namespace OpenFD.Game;

/// <summary>Explosion survenue ; <c>Quake</c> : celle d'un explosif invisible qui sert de secousse (voir ExplosiveItem.Quake).</summary>
public sealed record Explosion(Vector3 Position, float Time, float Radius, string? Sound, string Name, bool Quake = false);
