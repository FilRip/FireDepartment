using System.Numerics;
using OpenFD.Formats;

namespace OpenFD.Fire;

/// <summary>Paramètres d'une classe de point de fumée (SMOKEPOINT_PROPS, _DAMAGE, _TAGS).</summary>
public sealed record SmokePointClass(
    string Name, float IntensityMax, float FlashoverThreshold, float FirstFlashoverTempo, float FlashoverTempo,
    float InfluenceRange, float HotPointIntensityDamage, bool Extractor, float ExtractionRate)
{
    public static SmokePointClass From(string name, ClassFile c)
    {
        const string P = "SMOKEPOINT_PROPS", D = "SMOKEPOINT_DAMAGE", T = "SMOKEPOINT_TAGS";
        return new SmokePointClass(name,
            c.GetFloat(P, "IntensityMax", 100), c.GetFloat(P, "FlashoverThreshold", 75),
            c.GetFloat(P, "FirstFlashoverTempo", 45), c.GetFloat(P, "FlashoverTempo", 1000),
            c.GetFloat(P, "InfluenceRange", 400), c.GetFloat(D, "HotPointIntensityDamage", 50),
            c.GetBool(T, "Extractor"), c.GetFloat(T, "ExtractionRate", 50));
    }
}
