using OpenFD.Formats;

namespace OpenFD.Fire;

/// <summary>
/// Une couche de flammes d'une classe de foyer ([DISPLAY_FIRE1] ou [DISPLAY_FIRE2]) : modèle (Gfx\Effect\*.sg),
/// densité des flammes, rayon des flammes (min, max) et décalage en hauteur.
/// </summary>
public sealed record FireLook(string Model, float Density, float MinRadius, float MaxRadius, float HeightOffset, bool Randomize)
{
    /// <summary>Lit une section DISPLAY_FIRE* ; null quand la section n'a pas de modèle (couche absente).</summary>
    public static FireLook? From(ClassFile c, string section)
    {
        string model = (c.Get(section, "Model") ?? "").Trim('"', ' ');
        if (model.Length == 0)
            return null;
        float lo = c.GetFloat(section, "MinParticleRadius", 10), hi = c.GetFloat(section, "MaxParticleRadius", 100);
        return new FireLook(model, c.GetFloat(section, "ParticleDensity", 1), MathF.Min(lo, hi), MathF.Max(lo, hi),
            c.GetFloat(section, "HeightOffset", 0), c.GetBool(section, "Randomize", true));
    }
}
