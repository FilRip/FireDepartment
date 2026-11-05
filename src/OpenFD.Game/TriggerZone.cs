using System.Numerics;
using OpenFD.Formats;
using OpenFD.Script;

namespace OpenFD.Game;

/// <summary>Zone de déclenchement de la carte (Trigger) : script appelé quand une unité y entre (composant A028).</summary>
public sealed class TriggerZone
{
    public required uint Id { get; init; }
    public required string Name { get; init; }
    public required string Script { get; set; }
    public required Matrix4x4 Inverse { get; init; }
    public required Vector3 Half { get; init; }
    public Vector3 Position;
    public bool Enabled = true;   // ENT_SetPropertyValueS32(id, "trigger", "ENABLED", 0/1)
    public readonly HashSet<int> Inside = new();
}
