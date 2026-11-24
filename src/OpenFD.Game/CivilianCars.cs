using System.Numerics;
using OpenFD.Formats;
using OpenFD.Script;

namespace OpenFD.Game;

// voitures civiles du décor que l'on peut conduire : un pompier (ou toute unité à pied) y monte d'un clic droit (Ctrl)
// réglages de la classe de l'objet, section [OPENFD] :
//   Drivable    = 1 : on peut le prendre (les véhicules du jeu, Design\Placeable\Vehicles, le sont toujours)
//   Speed       = vitesse en avant (15 par défaut, comme une voiture), BackSpeed = en arrière (le tiers par défaut)
//   Seats       = places (1 par défaut)
//   CanDemolish = 1 : il dégage les débris comme le bulldozer des pompiers
//   Clearance   = cases libres qu'il lui faut de chaque côté (1 par défaut ; 0 : un petit engin, le chariot élévateur)
// L'action de l'objet (sa fonction du script, comme pour un objet utilisable) est appelée quand on le prend la première
// fois : User, l'objet, « use ». Le script l'interdit ou le permet avec ENT_SetCapacityTarget(objet, "Drivable", 0 / 1).
public sealed partial class UnitManager
{
    /// <summary>Voitures que le script interdit de prendre (ENT_SetCapacityTarget(…, "Drivable", 0)).</summary>
    private readonly HashSet<uint> _drivableOff = new();

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
        if (_drivableOff.Contains(id) || !_s.Explosions.ById.TryGetValue(id, out ExplosiveItem? it) || it.Exploded || it.Life <= 0)
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
            car = MakeCivilianCar(carId, u);
            if (car == null)
                return false;
        }
        if (!car.IsCivilianCar)
            return false;
        BoardVehicle(u, car);
        return u.Boarding;
    }

    /// <summary>Script : ENT_SetCapacityTarget(objet, "Drivable", 0 / 1) ; ENT_GetCapacityTarget le lit.</summary>
    public void SetDrivable(uint id, bool on)
    {
        if (on)
            _drivableOff.Remove(id);
        else
            _drivableOff.Add(id);
    }

    public bool GetDrivable(uint id) => IsDrivableCar(id);

    private Unit? MakeCivilianCar(uint id, Unit user)
    {
        LevelObject o = _s.Level.Objects.First(x => x.Id == id);
        ExplosiveItem it = _s.Explosions.ById[id];
        ClassFile? c = Class(EditorClassFile(o.ClassPath));
        string? m = c?.Get("DISPLAY", "Model")?.Trim('"', ' ');
        if (string.IsNullOrEmpty(m))
            return null;
        float speed = c!.GetFloat("OPENFD", "Speed", 15);
        Unit car = new Unit
        {
            Id = (int)id, Name = o.Name, ClassPath = o.ClassPath, Model = SceneAssets.GfxPath("Placeable", m), IsVehicle = true,
            Position = o.Position, Speed = speed * VehicleSpeedFactor,
        };
        car.Heading = o.Rotation.Z - VehicleModelYaw;
        car.IsCivilianCar = true;
        car.FromLevel = true;   // modèle à états (…_e1, …_e2) : dessiné comme les véhicules posés dans le niveau
        car.Seats = Math.Max(1, c.GetInt("OPENFD", "Seats", CivilianCarSeats));
        car.BackSpeed = c.GetFloat("OPENFD", "BackSpeed", speed / 3) * VehicleSpeedFactor;
        car.CanDemolish = c.GetBool("OPENFD", "CanDemolish");
        car.DriveClearance = Math.Clamp(c.GetInt("OPENFD", "Clearance", 1), 0, 1);
        // long véhicule : on y monte par n'importe quel côté, même loin de son milieu (le bout de ses cases + 1 m)
        if (_s.Grid is { } g && Paths.ObjectCells.TryGetValue(id, out List<int>? cells) && cells.Count > 0)
        {
            float far = cells.Max(i => Vector2.Distance(new Vector2(o.Position.X, o.Position.Y), new Vector2((i % g.Width + 0.5f) * Grid.CellSize, (i / g.Width + 0.5f) * Grid.CellSize)));
            car.ReachExtra = MathF.Max(0, far + 100 - VehicleReach * 1.5f);
        }
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
        // l'action de l'objet : le script sait qu'on l'a pris (Villa : l'Excalibur ; Station-service : le camion-citerne)
        if (ActionsOf(o).FirstOrDefault() is { script.Length: > 0 } a)
            _s.Scripts?.TryRun(a.script, Value.I(user.Id), Value.I((int)id), Value.T(a.action));
        return car;
    }
}
