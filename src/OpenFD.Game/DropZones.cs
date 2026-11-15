using System.Numerics;
using OpenFD.Formats;

namespace OpenFD.Game;

// ===================================================================================================================
// Salles de secours (croix rouge seule, voir DropZone) : un pompier qui porte une victime l'y emmène de lui-même s'il n'y
// a pas d'ambulance plus proche, et l'y pose au sol ; un civil conscient qu'il guide y reste. Le script de la mission
// compte les victimes présentes quand un pompier entre dans la salle (Eurotunnel : CivInRoom, déclencheur de la salle) :
// après chaque victime posée, ce déclencheur est donc de nouveau prévenu, comme si le pompier venait d'entrer.
// ===================================================================================================================
public sealed partial class UnitManager
{
    /// <summary>Salles de secours du niveau.</summary>
    public List<DropZone> DropZones { get; } = new();

    /// <summary>Le porteur pose la victime à cette distance de la croix (au plus × 1,5).</summary>
    public const float DropZoneReach = 400f;

    private void LoadDropZones(Level level)
    {
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform && o.ClassPath.EndsWith("\\cross_symbol", StringComparison.OrdinalIgnoreCase)))
        {
            // croix au-dessus d'un poste de soins (Ch_Plant, Duseldorf) : c'est le poste de soins qui compte
            if (HealPoints.Any(p => Vector2.Distance(new(p.Position.X, p.Position.Y), new(o.Position.X, o.Position.Y)) < 400))
                continue;
            DropZones.Add(new DropZone { Id = o.Id, Name = o.Name, Position = o.Position, StartVisible = o.StartVisible });
            Log.Add($"Salle de secours : {o.Name} ({o.Position.X:0},{o.Position.Y:0}){(o.StartVisible ? "" : ", cachée au départ")}");
        }
    }

    /// <summary>Salle de secours ouverte : sa croix est visible (le script la montre quand l'objectif est donné).</summary>
    public bool DropZoneActive(DropZone z) =>
        _s.Host?.Visibility.TryGetValue(z.Id, out bool v) == true ? v : z.StartVisible;

    /// <summary>
    /// Une victime vient d'être laissée dans la salle de secours : les déclencheurs qui couvrent à la fois la salle et
    /// l'unité seront de nouveau appelés pour elle (au prochain contrôle), pour que le script recompte les victimes.
    /// </summary>
    private void RefireDropZoneTriggers(Unit u, DropZone z)
    {
        foreach (TriggerZone t in Triggers)
            if (InsideTrigger(t, u.Position) && InsideTrigger(t, z.Position))
                t.Inside.Remove(u.Id);
    }

    /// <summary>Salle de secours active la plus proche de ce point, à sa portée (× 1,5), ou null.</summary>
    private DropZone? DropZoneAt(Vector3 p) =>
        DropZones.Where(z => DropZoneActive(z) && Vector3.Distance(z.Position, p) < DropZoneReach * 1.5f && MathF.Abs(z.Position.Z - p.Z) < 300)
            .OrderBy(z => Vector3.Distance(z.Position, p)).FirstOrDefault();
}
