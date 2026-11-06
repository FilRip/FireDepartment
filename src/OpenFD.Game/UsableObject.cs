using System.Numerics;
using OpenFD.Formats;
using OpenFD.Script;

namespace OpenFD.Game;

/// <summary>
/// Objet sur lequel un pompier agit (armoire électrique, vanne, transformateur…) : classe [CAPACITIES_TARGET] CouldBeUse
/// (n'importe quel pompier) ou CouldBeRepair (le technicien, [CAPACITIES_ACTIONS] CanRepair).
/// Le composant A027 de l'objet donne, par action (« use », « use2 », « repair »), la fonction du script à appeler :
/// Fonction(Utilisateur, Objet, Action).
/// </summary>
public sealed class UsableObject
{
    public required uint Id { get; init; }
    public required string Name { get; init; }
    public required Vector3 Position { get; init; }
    public bool CanBeUsed, CanBeRepaired;
    public readonly List<(string action, string script)> Actions = new();
    public int Uses;                      // nombre de fois utilisé (les actions alternent : use, use2…)
    public bool Repaired;
    /// <summary>Emprise du modèle au sol (vue de dessus) : on peut viser l'objet n'importe où dessus, pas seulement son pivot
    /// (Mriya : le pivot du générateur est à un bout, la clé à molette 4,5 m plus loin).</summary>
    public Vector2 BoxMin, BoxMax;
    public float Height = 160;
    public bool Enabled => CanBeUsed || (CanBeRepaired && !Repaired);
}
