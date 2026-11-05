using System.Numerics;
using OpenFD.Formats;

namespace OpenFD.Fire;

public sealed class SmokePoint
{
    public required uint Id { get; init; }
    public required string Name { get; init; }
    public required SmokePointClass Class { get; init; }
    public required Vector3 Position { get; init; }
    public float Intensity;
    public float Max;
    public bool Active;
    public string Room = "";
    public void Add(float v)
    {
        Intensity = Math.Clamp(Intensity + v, 0, Max);
        if (Intensity > 0)
            Active = true;
    }
}
