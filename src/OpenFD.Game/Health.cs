using System.Numerics;
using OpenFD.Script;

namespace OpenFD.Game;

// endurance des pompiers et vie des véhicules :
//  - chaque feu dans son rayon de chaleur ([HOTPOINT_PROPS] HeatRangeMax) retire de l'endurance à un pompier :
//    UnitHeatDamage × (intensité / intensité max) × (1 − distance / rayon) × HeatStaminaCoef, par seconde ; plus on est
//    près, plus elle baisse vite ; la fumée épaisse aussi (SmokeStaminaCoef) ; loin du feu, elle remonte (StaminaRegen) ;
//    à 0, le pompier s'évanouit (un collègue doit le ranimer) ;
//  - près d'une ambulance, elle remonte vite (soin), sans être rendue d'un coup ;
//  - un véhicule perd de la vie au contact des flammes (UnitLifeDamage × FireDamageCoef) et des explosions ; à 0, il est
//    hors service jusqu'à la fin de la partie (plus de déplacement ni de lance, ses passagers descendent).
public sealed partial class UnitManager
{
    // réglages (modifiables dans Save\FD3_CSharp_options.json, rubrique « Gameplay », sans apparaître dans le menu)
    /// <summary>Chaleur des feux sur l'endurance des pompiers (× UnitHeatDamage des feux).</summary>
    public static float HeatFactor = 4f;
    /// <summary>Vitesse globale de la perte d'endurance des pompiers (chaleur et fumée) : 0,25 = 4 fois moins vite qu'au début.</summary>
    public static float StaminaLoss = 0.25f;
    /// <summary>Dégâts du feu sur la vie des véhicules (× UnitLifeDamage × FireDamageCoef).</summary>
    public static float VehicleFireFactor = 1.5f;
    /// <summary>Fumée épaisse sur l'endurance des pompiers.</summary>
    public static float SmokeFactor = 0.08f;
    /// <summary>Soin à l'ambulance : endurance rendue par seconde (le jeu ne donne pas de valeur).</summary>
    public static float AmbulanceHeal = 12f;
    /// <summary>Endurance rendue (part de l'endurance maximale) quand un collègue ranime un pompier évanoui.</summary>
    public static float ReviveStamina = 0.35f;
    private int _blastsSeen;

    private void UpdateHealth(float dt)
    {
        // souffle des explosions : endurance (pompiers), vie (véhicules)
        if (_s.Explosions is { } ex)
        {
            for (; _blastsSeen < ex.Blasts.Count; _blastsSeen++)
            {
                (Vector3 pos, float radius, float power) = ex.Blasts[_blastsSeen];
                foreach (Unit u in Units.Where(u => !u.OnBoard && !u.Hidden))
                {
                    float d = Vector3.Distance(u.Position, pos);
                    if (d > radius)
                        continue;
                    float k = 1 - d / radius;
                    if (u.IsVehicle)
                        Damage(u, power * 2 * k);
                    else
                        u.Stamina -= power * k;
                }
            }
        }
        foreach (Unit u in Units)
        {
            u.InFire = false;
            if (u.OnBoard || u.Hidden || (u.IsVehicle && u.Destroyed))
                continue;
            float heat = 0, burn = 0;
            foreach (Fire.HotPoint h in _s.Fire.HotPoints)
            {
                if (!h.IsBurning)
                    continue;
                float r = MathF.Max(h.Class.HeatRangeMax, 100) + (u.IsVehicle ? 150 : 0);
                float dx = h.Position.X - u.Position.X, dy = h.Position.Y - u.Position.Y;
                if (MathF.Abs(dx) > r || MathF.Abs(dy) > r || MathF.Abs(h.Position.Z - u.Position.Z) > 350)
                    continue;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                if (d > r)
                    continue;
                float f = Math.Min(1, h.Intensity / Math.Max(1, h.Class.IntensityMax)) * (1 - d / r);
                heat += h.Class.UnitHeatDamage * f;
                burn += h.Class.UnitLifeDamage * f;
            }
            u.InFire = heat > 0 || burn > 0;   // icône du feu sur son image (panneau des unités)
            if (u.IsVehicle)
            {
                if (burn > 0)
                    Damage(u, burn * u.FireDamageCoef * VehicleFireFactor * dt);
                continue;
            }
            if (u.Unconscious)
                continue;
            float smoke = 0;
            foreach (Fire.SmokePoint sp in _s.Smoke.Points)
                if (sp.Intensity > 1 && Vector3.DistanceSquared(sp.Position, u.Position) < sp.Class.InfluenceRange * sp.Class.InfluenceRange)
                    smoke += Math.Min(1, sp.Intensity / Math.Max(1, sp.Max));
            float loss = (heat * u.HeatCoef * u.ScriptHeatCoef * HeatFactor + smoke * u.SmokeCoef * SmokeFactor * 10) * dt * StaminaLoss;
            bool healing = !u.Moving && Units.Any(a => a.HealsUnits && !a.Destroyed && !a.OnBoard && Vector3.Distance(a.Position, u.Position) < VehicleReach);
            if (loss > 0)
                u.Stamina -= loss;
            else if (healing)
                u.Stamina += AmbulanceHeal * dt;
            else
                u.Stamina += u.StaminaRegen * dt;
            if (healing && loss <= 0 && u.Stamina < u.StaminaMax && (u.Status.StartsWith("attend") || u.Status.StartsWith("se soigne") || u.Status.StartsWith("va au")))
                u.Status = $"se soigne ({100 * u.Stamina / u.StaminaMax:0} %)";
            u.Stamina = Math.Clamp(u.Stamina, 0, u.StaminaMax);
            if (healing && u.Stamina >= u.StaminaMax && u.Status.StartsWith("se soigne"))
                u.Status = "attend les ordres";
            if (u.Stamina <= 0 && !u.Unconscious)
            {
                u.Unconscious = true; u.Wounds++;
                u.Path.Clear(); u.Target = null; u.Opening = null; u.GoTo = null; u.GoToVehicle = null;
                Sounds.Add(("VOCAL_FIREMAN_CRITICAL", u.Position));
                Log.Add($"{_s.Time,6:0} s : un pompier s'évanouit (chaleur) — un collègue doit le ranimer");
            }
        }
    }

    /// <summary>Dégâts à un véhicule ; à 0, il est hors service pour le reste de la partie.</summary>
    public void Damage(Unit v, float amount)
    {
        if (!v.IsVehicle || v.Destroyed || amount <= 0)
            return;
        v.Life = MathF.Max(0, v.Life - amount);
        if (v.Life > 0)
            return;
        v.Destroyed = true;
        v.Path.Clear(); v.Target = null; v.Status = "hors service (détruit)";
        if (v.Passengers.Count > 0)
        {
            List<Unit> saved = v.Passengers.ToList();
            UnloadPassengers(v);
            if (v.Passengers.Count > 0)
                foreach (Unit p in saved)
                    p.InVehicle = null;
            v.Passengers.Clear();
        }
        Sounds.Add(("VOCAL_VEHICLE_KILLED", v.Position));
        Log.Add($"{_s.Time,6:0} s : {v.Name} est détruit, hors service jusqu'à la fin de la mission");
        if (_s.Scripts?.HasFunction("OnVehicleDeath") == true)
            _s.Scripts.TryRun("OnVehicleDeath", Value.I(v.Id));
    }
}
