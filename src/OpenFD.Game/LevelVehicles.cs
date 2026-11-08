using System.Numerics;
using OpenFD.Formats;

namespace OpenFD.Game;

/// <summary>
/// Véhicules posés dans le niveau (entités FireVehicle) : l'hélicoptère sanitaire de Forest et de Stadium (caché au
/// départ, le script le fait atterrir puis redécoller), ceux de MonteCristo, le bulldozer de Forest (montré par le script
/// une fois l'épave réparée). Ne sont chargés que ceux visibles au départ ou renommés pour le script (« helico »,
/// « bulldozer ») : les autres, cachés et jamais montrés (Mriya), sont des restes de l'éditeur. Les « factices » ne sont
/// pas des véhicules : l'ambulance factice est un poste de soins (HealPoint) ; les autres sont laissés de côté.
/// </summary>
public sealed partial class UnitManager
{
    /// <summary>Postes de soins du niveau (ambulances factices).</summary>
    public List<HealPoint> HealPoints { get; } = new();

    /// <summary>Un pompier se soigne à cette distance d'un poste de soins (la flèche verte), à l'arrêt.</summary>
    public const float HealPointReach = 250f;
    /// <summary>Les modèles des véhicules regardent vers −Y : leur orientation dans le niveau vaut le cap + 90° (OpenFDGame.ModelYaw).</summary>
    private const float VehicleModelYaw = MathF.PI / 2;

    private void LoadLevelVehicles(Level level)
    {
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.FireVehicle && o.HasTransform))
        {
            string classRel = o.ClassPath.StartsWith("design\\unit\\", StringComparison.OrdinalIgnoreCase) ? o.ClassPath["design\\unit\\".Length..] : o.ClassPath;
            if (classRel.Contains("factice", StringComparison.OrdinalIgnoreCase))
            {
                LoadHealPoint(o, classRel);
                continue;
            }
            bool renamed = !o.Name.StartsWith(o.ClassName, StringComparison.OrdinalIgnoreCase);
            if (!o.StartVisible && !renamed)
                continue;
            ClassFile? c = Class("Design\\Unit\\" + classRel + ".class");
            // le script le désigne par son numéro du niveau (ENT_FindByName("helico"))
            Unit? u = Create(classRel, true, new Vector2(o.Position.X, o.Position.Y), (int)o.Id);
            if (c == null || u == null)
                continue;
            u.Position = o.Position;
            u.Heading = o.Rotation.Z - VehicleModelYaw;
            u.Hidden = !o.StartVisible;
            u.FromLevel = true;
            // hélicoptère : il ne roule pas ; posé, il sert d'ambulance (soins, civils mis à l'abri)
            if (string.Equals(c.Get("UNIT", "Type")?.Trim('"', ' '), "HELICO", StringComparison.OrdinalIgnoreCase))
            {
                u.Fixed = true;
                u.Speed = 0;
                u.IsAmbulance = u.HealsUnits;
            }
            Log.Add($"Véhicule du niveau : {o.Name} ({classRel}){(u.Fixed ? ", fixe" : "")}{(u.Hidden ? ", caché au départ" : "")}");
        }
    }

    // ambulance factice (CanHeal) : poste de soins ; son modèle est la flèche verte posée au sol
    private void LoadHealPoint(LevelObject o, string classRel)
    {
        ClassFile? c = Class("Design\\Unit\\" + classRel + ".class");
        if (c == null || !c.GetBool("CAPACITIES_ACTIONS", "CanHeal"))
            return;
        string? m = c.Get("DISPLAY", "Model")?.Trim('"', ' ');
        HealPoints.Add(new HealPoint
        {
            Id = o.Id, Name = o.Name, Position = o.Position, World = o.Matrix, StartVisible = o.StartVisible,
            Model = string.IsNullOrEmpty(m) ? "" : SceneAssets.GfxPath("Unit\\FireVehicle", m),
        });
        Log.Add($"Poste de soins : {o.Name} ({o.Position.X:0},{o.Position.Y:0}){(o.StartVisible ? "" : ", caché au départ")}");
    }

    /// <summary>Poste de soins visible (le script peut le cacher ou le montrer).</summary>
    public bool HealPointActive(HealPoint p) =>
        _s.Host?.Visibility.TryGetValue(p.Id, out bool v) == true ? v : p.StartVisible;
}
