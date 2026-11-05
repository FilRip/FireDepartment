using System.Numerics;
using OpenFD.Formats;

namespace OpenFD.Fire;

/// <summary>Paramètres d'une classe de foyer (section HOTPOINT_PROPS et HOTPOINT_TAGS).</summary>
public sealed record HotPointClass(
    string Name, float InitialIntensity, float IntensityMax, float IntensityMin,
    float LifeDamageRange, float HeatRangeMax, float LightingRange, float LightingThreshold,
    float Tempo, float GrowthRate, float DeathRate, float GrowthTime,
    float SlopeFactor, bool UnderWindInfluence)
{
    public float SmokeGenerationRate { get; init; } = 20f;
    /// <summary>[HOTPOINT_DAMAGE] UnitLifeDamage : dégâts aux objets et aux unités à portée (par seconde, à pleine intensité).</summary>
    public float UnitLifeDamage { get; init; } = 2f;
    /// <summary>[HOTPOINT_DAMAGE] UnitHeatDamage : chaleur (endurance des pompiers) par seconde, à pleine intensité, au contact.</summary>
    public float UnitHeatDamage { get; init; } = 2f;
    /// <summary>Type de feu A (ordinaire), B (liquides : essence…), C (électrique), D (métaux).</summary>
    public char FireType { get; init; } = 'A';
    public static HotPointClass From(string name, ClassFile c)
    {
        const string P = "HOTPOINT_PROPS", T = "HOTPOINT_TAGS";
        return new HotPointClass(name,
            c.GetFloat(P, "InitialIntensity", 1), c.GetFloat(P, "IntensityMax", 100), c.GetFloat(P, "IntensityMin", -100),
            c.GetFloat(P, "LifeDamageRange", 100), c.GetFloat(P, "HeatRangeMax", 250), c.GetFloat(P, "LightingRange", 500),
            c.GetFloat(P, "LightingThreshold", 50), c.GetFloat(P, "Tempo", 15), c.GetFloat(P, "GrowthRate", 2.5f),
            c.GetFloat(P, "DeathRate", -1), c.GetFloat(P, "GrowthTime", 120),
            c.GetFloat(T, "SlopeFactor", 10), c.GetBool(T, "UnderWindInfluence", true))
        {
            SmokeGenerationRate = c.GetFloat(T, "SmokeGenerationRate", 20),
            UnitLifeDamage = c.GetFloat("HOTPOINT_DAMAGE", "UnitLifeDamage", 2),
            UnitHeatDamage = c.GetFloat("HOTPOINT_DAMAGE", "UnitHeatDamage", 2),
            FireType = (c.Get(T, "Type") ?? "FireType_A").Trim('"', ' ').ToUpperInvariant() is string t && t.Length > 0 ? t[^1] : 'A',
        };
    }
}
