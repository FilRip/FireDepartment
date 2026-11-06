using System.Numerics;
using OpenFD.Fire;
using OpenFD.Formats;

namespace OpenFD.Game;

/// <summary>
/// Objet qui peut exploser (classe : [EXPLOSION] CanExplode = 1 ; voitures, bouteilles de gaz, fûts…).
/// Ses points de vie baissent quand les foyers proches brûlent ; à 0, il explose.
/// Valeurs de la carte (.level) quand elles y sont : composant A024 (points de vie), A029 (explosion), D007 (son).
/// </summary>
public sealed class ExplosiveItem
{
    public required uint Id { get; init; }
    public required string Name { get; init; }
    public required Vector3 Position { get; init; }
    public float Life, MaxLife = 100, FireDamageCoef = 1;
    public float LpPower, LpRadius, HpPower, HpRadius, StaminaPower, StaminaRadius;
    public string? Sound;
    public float[] Thresholds = Array.Empty<float>();   // [DAMAGE] DamageTresholdStep0…n (points de vie)
    public bool Exploded;
    /// <summary>Faux : objet qui s'abîme au feu sans exploser (Mriya : le générateur, dont le script suit la vie).</summary>
    public bool CanExplode = true;
    public readonly List<HotPoint> NearFires = new();

    /// <summary>État d'affichage : « e1 » intact, « e2 », « e3 » de plus en plus abîmé, « dead » après l'explosion.</summary>
    public string State
    {
        get
        {
            if (Exploded)
                return "dead";
            int step = 1;
            for (int i = 1; i < Thresholds.Length; i++)
                if (Thresholds[i] > 0 && Life < Thresholds[i])
                    step = i + 1;
            return "e" + Math.Min(step, 3);
        }
    }
}
