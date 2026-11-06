using System.Numerics;
using OpenFD.Fire;

namespace OpenFD.Game;

// ===================================================================================================================
// Pompier « canon » ([UNIT] TYPE=CANON, fireman_canon_xx) : il porte une grosse lance (le jet des camions,
// WaterSprinkler=sprinkler_pumper, portée 500 à 3000) mais n'a pas d'eau à lui. Il reste relié à un camion à lance
// proche (lien dessiné par des flèches bleues) et arrose avec l'eau de ce camion.
//  - on peut l'envoyer n'importe où ; loin d'un camion il n'arrose pas, mais reste en position (accroupi, lance en main) ;
//  - il arrose de lui-même un feu à portée de sa lance, mais ne change jamais de place de lui-même.
// ===================================================================================================================
public sealed partial class UnitManager
{
    /// <summary>Distance maximale entre le canon et son camion : la vue d'un pompier ordinaire (pour son IA) plus un tiers.</summary>
    public static float CanonLinkRange => (600 + ExtraStandoff) * MathF.Max(1, AutoAttackRange) * 4f / 3f;

    /// <summary>Camions qui peuvent alimenter un canon : véhicules à lance, en état, sur le terrain.</summary>
    private IEnumerable<Unit> FeedTrucks() =>
        Units.Where(v => v.IsVehicle && v.Nozzle != null && !v.Destroyed && !v.Hidden && !v.OnBoard);

    /// <summary>Camion le plus proche de ce point, à portée de liaison ; null s'il n'y en a pas.</summary>
    private Unit? NearestTruck(Vector2 p, float range)
    {
        Unit? best = null;
        float bd = range;
        foreach (Unit v in FeedTrucks())
        {
            float d = Vector2.Distance(p, new(v.Position.X, v.Position.Y));
            if (d <= bd)
            {
                bd = d;
                best = v;
            }
        }
        return best;
    }

    /// <summary>À chaque pas : le canon garde le camion qui l'alimente (le même tant qu'il reste à portée, sinon le plus proche).</summary>
    private void UpdateCanonLink(Unit u)
    {
        if (!u.IsCanon)
            return;
        Vector2 me = new Vector2(u.Position.X, u.Position.Y);
        if (u.OnBoard || u.Unconscious || u.Hidden)
        {
            u.FeedTruck = null;
            return;
        }
        if (u.FeedTruck is { } t && !t.Destroyed && !t.Hidden && !t.OnBoard && Vector2.Distance(me, new(t.Position.X, t.Position.Y)) <= CanonLinkRange)
            return;
        u.FeedTruck = NearestTruck(me, CanonLinkRange);
    }

    /// <summary>Ordre d'arroser un feu donné à un canon : il arrose d'où il est si le feu est à portée, sinon il s'en approche.</summary>
    private void CanonAttack(Unit u, HotPoint h)
    {
        Vector2 me = new Vector2(u.Position.X, u.Position.Y);
        Vector2 f = new Vector2(h.Position.X, h.Position.Y);
        float d = Vector2.Distance(me, f);
        if (d <= u.Nozzle!.RangeMax && d >= u.Nozzle.RangeMin)
        {
            u.Path.Clear();
            u.Target = h;
            u.Status = $"arrose {h.Name}";
            return;
        }
        float want = d > u.Nozzle.RangeMax ? u.Nozzle.RangeMax * 0.8f : MathF.Max(u.Nozzle.RangeMin + 200, u.Nozzle.RangeMax * 0.4f);
        Vector2 dir = d > 1 ? Vector2.Normalize(me - f) : Vector2.UnitX;
        Order(u, f + dir * want, h);
    }

    /// <summary>Canon inoccupé et relié : il arrose le feu visible le plus proche à portée de sa lance, sans bouger.</summary>
    private void CanonAutoAttack(Unit u)
    {
        if (u.FeedTruck == null || !IdleWithWater(u))
            return;
        if ((u.Id + _autoTick) % 3 != 0)
            return;
        if (FindVisibleFire(u, u.Nozzle!.RangeMax, float.MaxValue, nearest: true) is not { } best)
            return;
        u.GoToVehicle = null;
        u.Target = best;
        u.Status = $"arrose {best.Name}";
    }

    /// <summary>Arrosage du canon avec l'eau de son camion ; vrai s'il a pu arroser (sinon il s'arrête, sans message vocal).</summary>
    private bool CanonSpray(Unit u, HotPoint h, float dt)
    {
        float d = Vector2.Distance(new(u.Position.X, u.Position.Y), new(h.Position.X, h.Position.Y));
        if (d > u.Nozzle!.RangeMax * 1.05f)
        {
            u.Target = null;
            u.Status = "feu hors de portée";
            return false;
        }
        if (u.FeedTruck is not { Nozzle: { } tn })
        {
            u.Target = null;
            u.Status = "pas de camion à proximité";
            return false;
        }
        if (!tn.InfiniteReserve && tn.Reserve <= 0)
        {
            u.Target = null;
            u.Status = "le camion n'a plus d'eau";
            return false;
        }
        // l'eau vient du camion : la réserve du canon suit celle du camion le temps du jet
        u.Nozzle.InfiniteReserve = tn.InfiniteReserve;
        u.Nozzle.Reserve = tn.Reserve;
        u.Nozzle.Spray(_s.Fire, h, dt);
        if (!tn.InfiniteReserve)
            tn.Reserve = u.Nozzle.Reserve;
        u.Nozzle.InfiniteReserve = true;
        u.Status = $"arrose {h.Name}";
        return true;
    }
}
