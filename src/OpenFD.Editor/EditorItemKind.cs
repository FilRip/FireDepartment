namespace OpenFD.MG;

/// <summary>Sorte d'objet de l'éditeur : elle décide de son dessin, de ses propriétés et de l'entité écrite dans le niveau.</summary>
public enum EditorItemKind
{
    /// <summary>Objet du décor (Design\Placeable, sauf Functional).</summary>
    Object,
    /// <summary>Objet sur lequel on agit : porte, fenêtre, interrupteur, repère qui tourne (Design\Placeable\Functional).</summary>
    Dynamic,
    Civilian,
    Fireman,
    Vehicle,
    /// <summary>Foyer : feu allumé ou non au départ.</summary>
    Fire,
    /// <summary>Zone qui appelle un script quand une unité y entre ou en sort.</summary>
    Zone,
    /// <summary>Carré noir : masque de toit.</summary>
    RoofMask,
    /// <summary>Position de la caméra au début de la mission (repère cam_startpos).</summary>
    Camera,
    /// <summary>Point de passage (arrivée des équipes, caméra des cinématiques…).</summary>
    Waypoint,
    /// <summary>Arbre (classe SPEEDTREE, dessiné comme dans le jeu avec EZ-Tree).</summary>
    Tree,
}
