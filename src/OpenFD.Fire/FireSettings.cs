using System.Numerics;
using OpenFD.Formats;

namespace OpenFD.Fire;

/// <summary>Réglages globaux du feu pour une mission (voir docs/MODELE_FEU.md).</summary>
public sealed class FireSettings
{
    public float TempoFactor = 1f;            // [DIFFICULTY_*] Tempo
    public float GrowthFactor = 1f;           // [DIFFICULTY_*] GrowthRate
    public float FireAttackPower = 1f;        // [DIFFICULTY_*] FireAttackPower (extinction, à venir)
    public float FireAttackCost = 1f;         // [DIFFICULTY_*] FireAttackCost (extinction, à venir)
    public float ElectricityStamina = 0.5f;    // [DIFFICULTY_*] ElectricityStamina (électrocution)
    public float NaturalEvaporationRate = 1.5f;
    public float FireEvaporationFactor = 0f;
    public Vector3 WindDirection = Vector3.UnitX;
    public float WindPower = 1f;              // 1 = pas d'effet

    public static FireSettings FromMission(ClassFile mis, string difficulty = "NORMAL")
    {
        FireSettings s = new FireSettings();
        string d = "DIFFICULTY_" + difficulty;
        s.TempoFactor = mis.GetFloat(d, "Tempo", 1f);
        s.GrowthFactor = mis.GetFloat(d, "GrowthRate", 1f);
        s.FireAttackPower = mis.GetFloat(d, "FireAttackPower", 1f);
        s.FireAttackCost = mis.GetFloat(d, "FireAttackCost", 1f);
        s.ElectricityStamina = mis.GetFloat(d, "ElectricityStamina", 0.5f);
        s.NaturalEvaporationRate = mis.GetFloat("WATER", "NaturalEvaporationRate", 1.5f);
        s.FireEvaporationFactor = mis.GetFloat("WATER", "FireEvaporationFactor", 0f);
        return s;
    }
}
