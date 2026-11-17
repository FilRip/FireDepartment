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

    /// <summary>Le porteur va à cette distance de la croix (× 0,8) ; sans déclencheur autour de la croix, la salle est ce rayon × 1,5.</summary>
    public const float DropZoneReach = 250f;

    // après les déclencheurs : la salle est celui (le plus petit) qui couvre la croix
    private void LoadDropZones(Level level)
    {
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform && o.ClassPath.EndsWith("\\cross_symbol", StringComparison.OrdinalIgnoreCase)))
        {
            // croix au-dessus d'un poste de soins (Ch_Plant, Duseldorf) : c'est le poste de soins qui compte
            if (HealPoints.Any(p => Vector2.Distance(new(p.Position.X, p.Position.Y), new(o.Position.X, o.Position.Y)) < 400))
                continue;
            TriggerZone? area = Triggers.Where(t => InsideTrigger(t, o.Position)).OrderBy(t => t.Half.X * t.Half.Y).FirstOrDefault();
            DropZones.Add(new DropZone { Id = o.Id, Name = o.Name, Position = o.Position, StartVisible = o.StartVisible, Area = area });
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

    /// <summary>
    /// Salle de secours active où se trouve ce point (dans son déclencheur : pas de l'autre côté du mur, devant la porte),
    /// ou null.
    /// </summary>
    private DropZone? DropZoneAt(Vector3 p) =>
        DropZones.Where(z => DropZoneActive(z) && (z.Area is { } a ? InsideTrigger(a, p)
                : Vector3.Distance(z.Position, p) < DropZoneReach * 1.5f && MathF.Abs(z.Position.Z - p.Z) < 300))
            .OrderBy(z => Vector3.Distance(z.Position, p)).FirstOrDefault();
}
