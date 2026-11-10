using OpenFD.Formats;

namespace OpenFD.MG;

/// <summary>Un élément de la liste de droite de l'éditeur : ce que l'on peut poser sur la carte.</summary>
public sealed class EditorItem
{
    /// <summary>Nom affiché sous l'aperçu.</summary>
    public required string Name { get; init; }
    public required EditorItemKind Kind { get; init; }
    /// <summary>Classe telle qu'elle est écrite dans le niveau (« design\placeable\furniture\chair_1 ») ; null pour un repère.</summary>
    public string? ClassPath { get; init; }
    /// <summary>Modèle 3D (chemin dans Gfx, sans extension : « Placeable\chair_1 ») ; null : repère dessiné par l'éditeur.</summary>
    public string? Model { get; init; }
    /// <summary>Entité du niveau créée quand on pose l'élément.</summary>
    public EntityKind Entity { get; init; } = EntityKind.Placeable;
    /// <summary>Dossier de la classe (Furniture, Urban…), pour l'ordre de la liste.</summary>
    public string Folder { get; init; } = "";
    /// <summary>Arbre : modèle SpeedTree, hauteur (cm) et graine de sa classe.</summary>
    public string? TreeModel { get; init; }
    public float TreeHeight { get; init; }
    public int TreeSeed { get; init; }
}
