using System.Numerics;
using OpenFD.Fire;

namespace OpenFD.Game;

// ===================================================================================================================
// IA des pompiers (et du médecin, du technicien…) : ce qu'un pompier décide seul, sans ordre du joueur.
//  - éteindre le feu qu'il voit à sa portée quand il est inoccupé (FiremanAutoAttack) ;
//  - (ailleurs, dans le même esprit : se recharger en eau près d'un camion — Units.Resupply —, ranimer un collègue
//    évanoui — Units.Revive —, se soigner près d'une ambulance — Health.UpdateHealth).
// Les véhicules ont leur propre IA : VehicleAI.cs.
// ===================================================================================================================
public sealed partial class UnitManager
{
    /// <summary>À chaque pas : chaque unité inoccupée regarde s'il y a un feu à éteindre (pompier ou véhicule à lance).</summary>
    private void AutoAttack(Unit u)
    {
        if (u.IsVehicle)
            VehicleAutoAttack(u);
        else if (u.IsCanon)
            CanonAutoAttack(u);   // il arrose d'où il est, sans jamais se déplacer de lui-même
        else
            FiremanAutoAttack(u);
    }

    /// <summary>Pompier inoccupé : il arrose de lui-même le feu qu'il voit à portée de sa lance (plus le recul qu'il garde
    /// devant le feu), sans mur entre eux et à peu près au même étage.</summary>
    /// <summary>Feux qu'un pompier a renoncé à atteindre (pas de chemin), et jusqu'à quand : tous retenus, sans quoi deux feux
    /// inaccessibles se relaient et il recherche sans fin un chemin de l'un à l'autre (Petrochemical : la partie se figeait).</summary>
    private readonly Dictionary<int, Dictionary<uint, float>> _autoGiveUp = new();

    private void FiremanAutoAttack(Unit u)
    {
        if (!IdleWithWater(u))
            return;
        if ((u.Id + _autoTick) % 3 != 0)
            return;   // une fois sur trois pas : assez réactif
        float reach = MathF.Max(150, u.Nozzle!.RangeMax) + ExtraStandoff;
        // il repère les feux bien au-delà de sa lance (AutoAttackRange, réglage du jeu) et s'en approche pour arroser
        // le plus proche de lui parmi ceux qu'il voit (après un ordre d'attaque : le plus proche du dernier feu demandé ou
        // éteint, FireFocus), sauf celui qu'il vient de renoncer à atteindre
        if (!_autoGiveUp.TryGetValue(u.Id, out Dictionary<uint, float>? given))
            _autoGiveUp[u.Id] = given = new();
        foreach (uint old in given.Where(kv => kv.Value <= _s.Time).Select(kv => kv.Key).ToList())
            given.Remove(old);
        if (FindVisibleFire(u, reach * MathF.Max(1, AutoAttackRange), float.MaxValue, nearest: true, skip: given) is not { } best)
            return;
        u.GoToVehicle = null;
        float d = Vector2.Distance(new(u.Position.X, u.Position.Y), new(best.Position.X, best.Position.Y));
        if (d <= reach)
        {
            u.Target = best;
            u.Status = $"arrose {best.Name}";
            return;
        }
        // feu hors d'atteinte (pas de chemin) : il n'insiste pas avant 10 s
        Attack(u, best);
        if (!u.Moving && u.Target == null)
            given[best.Id] = _s.Time + 10;
    }

    /// <summary>Unité libre (ni ordre en cours, ni blessée, ni à bord) qui a encore de l'eau.</summary>
    private bool IdleWithWater(Unit u) =>
        !(u.Destroyed || u.InjuredTimer > 0 || u.Unconscious || u.OnBoard || u.Hidden || u.Moving || u.Target != null || u.Nozzle == null
          || (!u.Nozzle.InfiniteReserve && u.Nozzle.Reserve <= 0) || u.GoTo != null || u.Opening != null
          || (u.GoToVehicle != null && (u.Nozzle.Reserve < u.Nozzle.ReserveMax || Civilians.Any(c => c.Leader == u)))
          || u.UseTarget != null || u.ReviveTarget != null || u.Boarding || u.Carrying != null || u.Climb != ClimbPhase.None
          || u.OnLadder != null || u.Stairs != null);

    /// <summary>
    /// Feu le plus intéressant (le plus proche, et le plus fort) que l'unité voit à moins de <paramref name="range"/>,
    /// dans un angle de ± <paramref name="angle"/> autour de son cap (lance d'un véhicule) ; null s'il n'y en a pas.
    /// L'eau passe par-dessus les véhicules : seuls les murs et les portes fermées cachent le feu.
    /// </summary>
    private HotPoint? FindVisibleFire(Unit u, float range, float angle, bool nearest = false, IReadOnlyDictionary<uint, float>? skip = null, float? minRange = null)
    {
        Vector2 me = new Vector2(u.Position.X, u.Position.Y);
        HotPoint? best = null;
        float bs = float.MaxValue;
        foreach (HotPoint h in _s.Fire.HotPoints)
        {
            if (!h.IsBurning || skip?.ContainsKey(h.Id) == true || (h.Electrified && !WaterTruck(u)) || !AgentSuits(u, h))   // feu électrique sous tension, ou mauvais agent : jamais de leur plein gré
                continue;
            Vector2 f = new Vector2(h.Position.X, h.Position.Y);
            float d = Vector2.Distance(me, f);
            if (d > range || d < (minRange ?? u.Nozzle!.RangeMin))
                continue;
            // hauteur : un pompier à pied, son étage ; la lance d'un véhicule porte aussi en hauteur (toits, étages)
            float dz = h.Position.Z - u.Position.Z;
            if (u.IsVehicle ? dz < -350 || dz > VehicleFireHeight : MathF.Abs(dz) > 350)
                continue;
            if (angle < MathF.PI && MathF.Abs(MathF.IEEERemainder(MathF.Atan2(f.Y - me.Y, f.X - me.X) - u.Heading, 2 * MathF.PI)) > angle)
                continue;
            // après un ordre d'attaque : classés selon leur distance au dernier feu demandé ou éteint (FireFocus), pour finir
            // le même foyer ; sinon selon leur distance à l'unité
            float rank = u.FireFocus is { } focus ? Vector2.Distance(focus, f) : d;
            float score = nearest ? rank : rank - 200 * MathF.Min(1, h.Intensity / MathF.Max(1, h.Class.IntensityMax));
            if (score >= bs)
                continue;
            bool saved = Paths.PathingVehicle;
            int savedClear = Paths.Clearance;
            Paths.PathingVehicle = true;
            Paths.Clearance = 0;
            Vector2 end = f - Vector2.Normalize(f - me + new Vector2(0.01f, 0)) * 120;
            // feu en hauteur (sur un toit) : le jet passe au-dessus du bâtiment qui le porte (le bout de la ligne peut y être)
            bool seen = u.IsVehicle ? Paths.WaterLineFree(me, end, elevated: dz > FloorStep) : LineFree(me, end, ignoreFire: true);
            Paths.PathingVehicle = saved;
            Paths.Clearance = savedClear;
            if (!seen)
                continue;
            bs = score;
            best = h;
        }
        return best;
    }

    /// <summary>Hauteur maximale (au-dessus du véhicule) d'un feu que la lance d'un véhicule arrose d'elle-même (toits, étages).</summary>
    public const float VehicleFireHeight = 1500f;

    // ---- diagnostics (OpenFD.Tools) ----
    /// <summary>Pourquoi le véhicule n'arrose pas le feu h de lui-même (essais).</summary>
    public string VehicleAutoDiag(Unit u, HotPoint h)
    {
        Vector2 me = new Vector2(u.Position.X, u.Position.Y), f = new Vector2(h.Position.X, h.Position.Y);
        bool saved = Paths.PathingVehicle;
        Paths.PathingVehicle = true;
        bool seen = Paths.WaterLineFree(me, f - Vector2.Normalize(f - me + new Vector2(0.01f, 0)) * 120);
        Paths.PathingVehicle = saved;
        string why = "";
        Paths.PathingVehicle = true;
        if (_s.Grid is { } g)
        {
            int n = (int)(Vector2.Distance(me, f) / 40) + 1;
            for (int i = 1; i < n && why == ""; i++)
            {
                Vector2 p = Vector2.Lerp(me, f, (float)i / n);
                (int cx, int cy) = g.CellOf(new Vector3(p, 0));
                int k = g.Index(cx, cy);
                if (!Paths.Free(cx, cy))
                    why = $" bloqué en ({p.X:0},{p.Y:0}) à {Vector2.Distance(me, p):0} : zone {g.ZoneA[k]} obstacle {(Paths.BlockedBy.TryGetValue(k, out string? bb) ? bb : Paths.IsBlocked(k) ? "?" : "-")} véhicule {Paths.IsVehicleBlocked(k)} porte {Paths.IsClosedDoor(k)}";
            }
        }
        Paths.PathingVehicle = saved;
        return $"{why} libre {IdleWithWater(u)} équipage {u.Crew.Count} cinématique {_s.Host?.Cinematic} agent {AgentSuits(u, h)} électrique {h.Electrified} dz {h.Position.Z - u.Position.Z:0} vu {seen}";
    }
    private int _autoTick;
    public bool AutoDiagSpot(Unit u, Vector2 p, HotPoint h)
    {
        if (_s.Grid is not { } g)
            return false;
        (int cx, int cy) = g.CellOf(new Vector3(p, 0));
        if (!FreeCell(cx, cy))
            return false;
        Vector2 f = new Vector2(h.Position.X, h.Position.Y);
        bool saved = Paths.PathingVehicle;
        Paths.PathingVehicle = true;
        bool ok = LineFree(p, f - Vector2.Normalize(f - p) * 120, ignoreFire: true);
        Paths.PathingVehicle = saved;
        return ok;
    }

    public string AutoDiag(Unit u)
    {
        Vector2 me = new Vector2(u.Position.X, u.Position.Y);
        HotPoint? h = _s.Fire.HotPoints.Where(h => h.IsBurning).OrderBy(h => Vector2.Distance(me, new(h.Position.X, h.Position.Y))).FirstOrDefault();
        if (h == null)
            return "aucun feu";
        Vector2 f = new Vector2(h.Position.X, h.Position.Y);
        List<HotPoint> near = _s.Fire.HotPoints.Where(x => x.IsBurning && Vector2.Distance(me, new(x.Position.X, x.Position.Y)) < 1500).ToList();
        int vis = near.Count(x =>
        {
            Vector2 g = new Vector2(x.Position.X, x.Position.Y);
            return LineFree(me, g - Vector2.Normalize(g - me) * 120, ignoreFire: true);
        });
        return $"[{near.Count} feux < 15 m, {vis} visibles : {string.Join(",", near.Take(4).Select(x => x.Name))}] {h.Name} d {Vector2.Distance(me, f):0} dz {h.Position.Z - u.Position.Z:0} vue {LineFree(me, f - Vector2.Normalize(f - me) * 120, ignoreFire: true)} angle {MathF.IEEERemainder(MathF.Atan2(f.Y - me.Y, f.X - me.X) - u.Heading, 2 * MathF.PI):0.00}";
    }

}
