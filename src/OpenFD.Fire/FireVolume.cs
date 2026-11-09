using OpenFD.Formats;

namespace OpenFD.Fire;

/// <summary>
/// Volume occupé par les flammes d'une classe de foyer ([DISPLAY_COMMON]) : taille au départ et à pleine intensité
/// (en largeur et en hauteur, par rapport à la portée du foyer), forme et opacité minimale.
/// </summary>
public sealed record FireVolume(float StartXY, float StartZ, float SizeXY, float SizeZ, string Shape, float MinOpacity)
{
    public static FireVolume From(ClassFile c)
    {
        const string S = "DISPLAY_COMMON";
        return new FireVolume(c.GetFloat(S, "CoefStartSize_XY", 1), c.GetFloat(S, "CoefStartSize_Z", 0.1f),
            c.GetFloat(S, "CoefSize_XY", 1), c.GetFloat(S, "CoefSize_Z", 0.5f),
            (c.Get(S, "LimitsShape") ?? "cylinder").Trim('"', ' ').ToLowerInvariant(), c.GetFloat(S, "MinOpacity", 0.2f));
    }
}
