using System.Numerics;
using OpenFD.Formats;

namespace OpenFD.Fire;

public sealed class HotPoint
{
    public required uint Id { get; init; }
    public required string Name { get; init; }
    public required HotPointClass Class { get; init; }
    public required Vector3 Position { get; init; }

    public float Intensity;
    public bool Lit;
    public bool Enabled = true;
    /// <summary>Feu électrique (type C) encore sous tension : on ne peut pas l'arroser tant que le courant n'est pas coupé.</summary>
    public bool Powered = true;
    public bool Electrified => Class.FireType == 'C' && Powered && IsBurning;
    public HotPointState State = HotPointState.Idle;
    public float HeatReceived;     // chaleur de la dernière mise à jour
    public float DelayedHeat;      // apportée par les liens à retardement, consommée à la mise à jour suivante
    public float HeatingTime;      // compteur de chauffe avant allumage
    public float BurnTime;         // temps passé à brûler
    public float GrowthMultiplier = 1f; // modifié par les scripts (FIR_HPMultGrowthRate)
    /// <summary>[HOTPOINT_TAGS] MakeBackdraft / SleepingThreshold (valeurs de la carte) : dans une pièce fermée, ce foyer
    /// « s'endort » faute d'air au-delà du seuil ; si on ouvre une porte, il se réveille d'un coup (backdraft).</summary>
    public bool MakeBackdraft;
    public float SleepingThreshold = 80f;
    public bool Sleeping;
    public float PendingAttack;    // eau/mousse reçue depuis la dernière mise à jour (§ 6)
    public readonly List<HotPoint> HeatSources = new();

    public bool IsBurning => Lit && Intensity > 0;

    /// <summary>Portée des dégâts (§ 3.5).</summary>
    public float DamageRange => Intensity > 0
        ? Class.LifeDamageRange + Intensity / Class.IntensityMax * (Class.HeatRangeMax - Class.LifeDamageRange)
        : Class.LifeDamageRange;

    public void SetIntensity(float value) => Intensity = Math.Clamp(value, Class.IntensityMin, Class.IntensityMax);
}
