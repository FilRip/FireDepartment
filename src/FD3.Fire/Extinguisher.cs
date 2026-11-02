using FD3.Formats;

namespace FD3.Fire;

/// <summary>
/// Capacité d'extinction d'une unité (sections CAPACITIES_FIRE_ATTACK, CAPACITIES_RESERVE, CAPACITIES_PROPS
/// de sa classe). Voir docs/MODELE_FEU.md § 6.
/// </summary>
public sealed class Extinguisher
{
    public string Name = "";
    public string Agent = "WATER";
    public float Power = 7f, Cost = 1f, Splash = 150f, RangeMin = 0f, RangeMax = 900f;
    public float Reserve = 250f, ReserveMax = 250f;
    public bool InfiniteReserve;
    public float Stamina = 100f, StaminaMax = 100f, StaminaRegen = 0.5f, ElectricStaminaCoef = 20f;
    public readonly Dictionary<char, float> Efficiency = new();

    public static Extinguisher FromClass(string name, ClassFile c)
    {
        const string A = "CAPACITIES_FIRE_ATTACK", R = "CAPACITIES_RESERVE", P = "CAPACITIES_PROPS";
        var e = new Extinguisher
        {
            Name = name,
            Power = c.GetFloat(A, "FireAttackPower", 7), Cost = c.GetFloat(A, "FireAttackCost", 1),
            Splash = c.GetFloat(A, "FireAttackSplash", 150),
            RangeMin = c.GetFloat(A, "FireAttackRangeMin", 0), RangeMax = c.GetFloat(A, "FireAttackRangeMax", 900),
            Reserve = c.GetFloat(R, "ReservePoints", 250), ReserveMax = c.GetFloat(R, "ReserveMax", 250),
            InfiniteReserve = c.GetBool(R, "InfiniteReserve"),
            Agent = (c.Get(R, "FireAgent") ?? "WATER").Trim().ToUpperInvariant(),
            Stamina = c.GetFloat(P, "StaminaPoints", 100), StaminaMax = c.GetFloat(P, "StaminaMax", 100),
            StaminaRegen = c.GetFloat(P, "StaminaRegen", 0.5f), ElectricStaminaCoef = c.GetFloat(P, "ElectricStaminaCoef", 20),
        };
        foreach (var t in "ABCD")
            e.Efficiency[t] = c.GetFloat(R, $"FireAgentEfficiency_{e.Agent}_TYPE_{t}", FireWorld.AgentEfficiency(e.Agent, t));
        return e;
    }

    /// <summary>
    /// Arrose <paramref name="hp"/> pendant <paramref name="dt"/> secondes.
    /// [mesuré, Eau0–Eau2] réserve : −Cost × FireAttackCost(difficulté) par seconde (0,8/s pour le médecin en Facile).
    /// [observé par le joueur] feu électrique sous tension : aucun effet, l'unité est électrocutée.
    /// </summary>
    public void Spray(FireWorld world, HotPoint hp, float dt, bool powered = true)
    {
        if (!InfiniteReserve && Reserve <= 0) return;
        if (!InfiniteReserve) Reserve = Math.Max(0, Reserve - Cost * world.Settings.FireAttackCost * dt);
        if (hp.Class.FireType == 'C' && powered)
        {
            // [hypothèse sur l'échelle de temps] perte d'endurance par seconde
            Stamina = Math.Max(0, Stamina - ElectricStaminaCoef * world.Settings.ElectricityStamina * dt);
            return;
        }
        world.Attack(hp, Power, dt, Efficiency.TryGetValue(hp.Class.FireType, out var k) ? k : 1f);
    }
}
