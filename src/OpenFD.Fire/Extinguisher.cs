using OpenFD.Formats;

namespace OpenFD.Fire;

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
    /// <summary>Efficacité de chaque agent sur chaque type de feu (FireAgentEfficiency_&lt;AGENT&gt;_TYPE_&lt;X&gt;) : l'agent peut changer en jeu.</summary>
    public readonly Dictionary<(string agent, char type), float> AgentEfficiency = new();
    public static readonly string[] Agents = { "WATER", "FOAM", "HALON", "POWDER", "METAL" };
    /// <summary>Mauvais agent pour ce feu : l'efficacité est divisée par 4.</summary>
    public const float WrongAgentFactor = 0.25f;

    /// <summary>Efficacité de l'agent actuel sur ce type de feu (valeurs de la classe).</summary>
    public float EfficiencyFor(char type) => AgentEfficiency.TryGetValue((Agent, type), out float k) ? k : FireWorld.AgentEfficiency(Agent, type);

    /// <summary>L'agent actuel convient à ce type de feu (A : eau, B : mousse, C : eau, D chimique : anti-métal…).</summary>
    public bool Suits(char type) => EfficiencyFor(type) >= 1f;

    /// <summary>Efficacité réellement appliquée : celle de la classe pour le bon agent, un quart sinon.</summary>
    public float EffectiveEfficiency(char type) => Suits(type) ? EfficiencyFor(type) : WrongAgentFactor;

    public static Extinguisher FromClass(string name, ClassFile c)
    {
        const string A = "CAPACITIES_FIRE_ATTACK", R = "CAPACITIES_RESERVE", P = "CAPACITIES_PROPS";
        Extinguisher e = new Extinguisher
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
        foreach (string a in Agents)
            foreach (char t in "ABCD")
                e.AgentEfficiency[(a, t)] = c.GetFloat(R, $"FireAgentEfficiency_{a}_TYPE_{t}", FireWorld.AgentEfficiency(a, t));
        return e;
    }

    /// <summary>
    /// Arrose <paramref name="hp"/> pendant <paramref name="dt"/> secondes.
    /// [mesuré, Eau0–Eau2] réserve : −Cost × FireAttackCost(difficulté) par seconde (0,8/s pour le médecin en Facile).
    /// [observé par le joueur] feu électrique sous tension : aucun effet, l'unité est électrocutée.
    /// </summary>
    public void Spray(FireWorld world, HotPoint hp, float dt, bool powered = true)
    {
        if (!InfiniteReserve && Reserve <= 0)
            return;
        if (!InfiniteReserve)
            Reserve = Math.Max(0, Reserve - Cost * world.Settings.FireAttackCost * dt);
        if (hp.Class.FireType == 'C' && powered && hp.Powered)   // courant coupé : feu ordinaire
        {
            // [hypothèse sur l'échelle de temps] perte d'endurance par seconde
            Stamina = Math.Max(0, Stamina - ElectricStaminaCoef * world.Settings.ElectricityStamina * dt);
            return;
        }
        world.Attack(hp, Power, dt, EffectiveEfficiency(hp.Class.FireType));
    }
}
