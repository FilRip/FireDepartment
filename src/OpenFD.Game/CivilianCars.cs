using System.Numerics;
using OpenFD.Formats;

namespace OpenFD.Game;

// voitures civiles du décor que l'on peut conduire : un pompier (ou toute unité à pied) y monte d'un clic droit
public sealed partial class UnitManager
{
    /// <summary>Places dans une voiture civile.</summary>
    private const int CivilianCarSeats = 1;
    /// <summary>Vie d'une voiture civile devenue véhicule (comme un véhicule de pompiers), au prorata de celle de l'objet.</summary>
    private const float CivilianCarLife = 500f;

    /// <summary>
    /// Voiture civile du décor qu'on peut prendre : un objet qui peut exploser (dessiné à part, on peut donc le cacher)
    /// rangé avec les véhicules du jeu (Design\Placeable\Vehicles) ou dont la classe dit [OPENFD] Drivable = 1.
    /// </summary>
    public bool IsDrivableCar(uint id)
    {
        if (!_s.Explosions.ById.TryGetValue(id, out ExplosiveItem? it) || it.Exploded || it.Life <= 0)
            return false;
        if (_s.Level.Objects.FirstOrDefault(o => o.Id == id) is not { Kind: EntityKind.Placeable } o)
            return false;
        if (_s.Host?.Visibility.TryGetValue(id, out bool shown) == true && !shown)
            return false;
        if (o.ClassPath.StartsWith("design\\placeable\\vehicles\\", StringComparison.OrdinalIgnoreCase))
            return true;
        return Class(EditorClassFile(o.ClassPath)) is { } c && c.GetBool("OPENFD", "Drivable");
    }

    private static string EditorClassFile(string classPath) =>
        classPath.StartsWith("design\\", StringComparison.OrdinalIgnoreCase) ? "Design\\" + classPath["design\\".Length..] + ".class" : classPath + ".class";

    /// <summary>
    /// Clic droit d'une unité à pied sur une voiture civile : la voiture devient un véhicule (l'objet du décor est caché,
    /// ses cases libérées) et l'unité va y monter. Déjà prise : elle y monte simplement (s'il reste une place).
    /// </summary>
    public bool TakeCivilianCar(Unit u, uint carId)
    {
        if (u.IsVehicle || u.OnBoard || u.Unconscious)
            return false;
        Unit? car = ById(carId);
        if (car == null)
        {
            if (!IsDrivableCar(carId) || _s.Host == null)
            {
                u.Status = "on ne peut pas prendre ce véhicule";
                return false;
            }
            car = MakeCivilianCar(carId);
            if (car == null)
                return false;
        }
        if (!car.IsCivilianCar)
            return false;
        BoardVehicle(u, car);
        return u.Boarding;
    }

    private Unit? MakeCivilianCar(uint id)
    {
        LevelObject o = _s.Level.Objects.First(x => x.Id == id);
        ExplosiveItem it = _s.Explosions.ById[id];
        ClassFile? c = Class(EditorClassFile(o.ClassPath));
        string? m = c?.Get("DISPLAY", "Model")?.Trim('"', ' ');
        if (string.IsNullOrEmpty(m))
            return null;
        Unit car = new Unit
        {
            Id = (int)id, Name = o.Name, ClassPath = o.ClassPath, Model = SceneAssets.GfxPath("Placeable", m), IsVehicle = true,
            Position = o.Position, Speed = 15 * VehicleSpeedFactor,
        };
        car.Heading = o.Rotation.Z - VehicleModelYaw;
        car.IsCivilianCar = true;
        car.FromLevel = true;   // modèle à états (…_e1, …_e2) : dessiné comme les véhicules posés dans le niveau
        car.Seats = CivilianCarSeats;
        car.BackSpeed = 5 * VehicleSpeedFactor;
        car.Accel = 8 * VehicleAccelFactor;
        car.Decel = 6 * VehicleAccelFactor;
        car.TurnRadius = TurnRadiusRef;
        car.LifeMax = CivilianCarLife;
        car.Life = CivilianCarLife * Math.Clamp(it.Life / MathF.Max(1, it.MaxLife), 0.05f, 1);
        car.FireDamageCoef = 3;
        car.Status = "garé";
        // l'objet du décor laisse la place au véhicule : caché (ses cases libérées), il n'explose plus
        _s.Host!.Visibility[id] = false;
        it.Exploded = true;
        it.NearFires.Clear();
        Units.Add(car);
        Log.Add($"{_s.Time,6:0} s : voiture civile {o.Name} prise ({m})");
        return car;
    }
}
