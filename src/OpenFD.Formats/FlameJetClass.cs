using System.Globalization;

namespace OpenFD.Formats;

/// <summary>
/// Classe d'un jet de flammes (moteur : OpenFD.Game.FlameJet), une par longueur : « design\placeable\levels\openfd_flamejet_1450 »
/// (Design\Placeable\Levels\openfd_flamejet_1450.class). Objet sans modèle (rien à dessiner, pas d'occupation par sa forme) ;
/// sa section [FLAMEJET] dit la longueur du jet (vers l'axe X de l'objet), sa largeur et sa hauteur au-dessus de l'objet.
/// Utilisée par l'éditeur (liste « Dynamiques ») et par le convertisseur FD1 (la fuite de propane de la station-service).
/// </summary>
public static class FlameJetClass
{
    public const string Prefix = "openfd_flamejet";
    public const string Folder = "design\\placeable\\levels\\";
    public const int DefaultLength = 1000, Width = 120, Height = 150;

    /// <summary>Nom de la classe pour cette longueur (cm).</summary>
    public static string Name(int length) => $"{Prefix}_{length}";

    /// <summary>Chemin de la classe dans le niveau (comme LevelObject.ClassPath).</summary>
    public static string ClassPath(int length) => Folder + Name(length);

    /// <summary>Fichier de la classe, dans Data.</summary>
    public static string File(int length) => "Design\\Placeable\\Levels\\" + Name(length) + ".class";

    /// <summary>Longueur (cm) d'après le nom de la classe ; null si ce n'est pas un jet de flammes.</summary>
    public static int? LengthOf(string classPath)
    {
        string leaf = classPath[(classPath.LastIndexOf('\\') + 1)..];
        if (!leaf.StartsWith(Prefix + "_", StringComparison.OrdinalIgnoreCase))
            return null;
        return int.TryParse(leaf[(Prefix.Length + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;
    }

    public static string Text(int length) =>
        "[#INCLUDE]\r\nFILE0 = \"base_levels.class\"\r\n\r\n[DISPLAY]\r\nMODEL = noitem\r\n\r\n[OCCUPATION]\r\nACTIVATED = 0\r\n\r\n"
        + $"[FLAMEJET]\r\nLENGTH = {length}\r\nWIDTH = {Width}\r\nHEIGHT = {Height}\r\n";
}
