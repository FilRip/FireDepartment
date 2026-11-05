using System.Numerics;
using OpenFD.Fire;

namespace OpenFD.Game;

// ===================================================================================================================
// Agents d'extinction : eau (WATER), mousse (FOAM), anti-métal (METAL), poudre (POWDER) — [CAPACITIES_RESERVE] FireAgent.
// Chaque type de feu a son agent (efficacité ≥ 1 dans FireAgentEfficiency_<AGENT>_TYPE_<X>) : A et C l'eau, B la mousse,
// D (feu chimique, flammes vertes) l'anti-métal. Avec un autre agent, l'efficacité est divisée par 4 (Extinguisher).
//  - l'IA n'attaque d'elle-même que les feux qui conviennent à son agent (FiremanAI.FindVisibleFire) ;
//  - sur ordre, le mauvais agent est accepté, avec le message « VOCAL_FACTORY_Factory_Agent » au premier jet ;
//  - un pompier (pas un canon) change d'agent en allant se recharger à un camion d'un autre agent : il vide d'abord
//    ce qui lui reste (10 s pour une réserve pleine, au prorata), annonce le nouvel agent, puis se recharge normalement.
// ===================================================================================================================
public sealed partial class UnitManager
{
    /// <summary>Temps pour vider une réserve pleine avant de changer d'agent.</summary>
    public const float AgentDrainTime = 10f;

    /// <summary>Message vocal annonçant le nouvel agent.</summary>
    private static string AgentVocal(string agent) => agent switch
    {
        "FOAM" => "VOCAL_FIRE_AGENT_FOAM",
        "METAL" => "VOCAL_FIRE_AGENT_METAL",
        "POWDER" => "VOCAL_FIRE_AGENT_POWDER",
        _ => "VOCAL_FIRE_AGENT_WATER",
    };

    /// <summary>Nom affiché d'un agent.</summary>
    public static string AgentName(string agent) => agent switch
    {
        "FOAM" => "mousse",
        "METAL" => "anti-métal",
        "POWDER" => "poudre",
        "HALON" => "halon",
        _ => "eau",
    };

    /// <summary>Agent que fournit un véhicule ([CAPACITIES_RESERVE] FireAgent, eau par défaut).</summary>
    public static string SupplyAgentOf(Unit v) => v.Nozzle?.Agent ?? v.SupplyAgent;

    /// <summary>
    /// Pompier à côté du camion demandé, qui fournit un autre agent : il vide sa réserve, puis prend le nouvel agent.
    /// Renvoie vrai tant que la vidange n'est pas finie (la recharge attend).
    /// </summary>
    private bool DrainForAgent(Unit u, Extinguisher n, Unit truck, float dt)
    {
        string want = SupplyAgentOf(truck);
        if (n.Agent == want)
            return false;
        if (n.Reserve > 0)
        {
            n.Reserve = MathF.Max(0, n.Reserve - n.ReserveMax / AgentDrainTime * dt);
            u.Status = $"vide son agent ({AgentName(n.Agent)} : {n.Reserve:0}/{n.ReserveMax:0})";
            if (n.Reserve > 0)
                return true;
        }
        n.Agent = want;
        u.Status = $"remplit sa réserve ({AgentName(want)})";
        Sounds.Add((AgentVocal(want), null));
        Log.Add($"{_s.Time,6:0} s : {u.Name} passe à l'agent {AgentName(want)}");
        return false;
    }

    /// <summary>Premier jet sur un feu qui ne convient pas à son agent : on prévient (une fois par feu).</summary>
    private void WarnWrongAgent(Unit u, HotPoint h)
    {
        if (u.Nozzle == null || u.IsCanon || u.Nozzle.Suits(h.Class.FireType) || u.WarnedAgent == h)
            return;
        u.WarnedAgent = h;
        Sounds.Add(("VOCAL_FACTORY_Factory_Agent", null));
    }

    /// <summary>L'IA ne choisit d'elle-même que des feux adaptés à son agent.</summary>
    private static bool AgentSuits(Unit u, HotPoint h) => u.Nozzle == null || u.IsCanon || u.Nozzle.Suits(h.Class.FireType);
}
