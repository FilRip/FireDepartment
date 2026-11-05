using System.Numerics;
using OpenFD.Fire;
using OpenFD.Formats;

namespace OpenFD.Game;

public sealed record Explosion(Vector3 Position, float Time, float Radius, string? Sound, string Name);
