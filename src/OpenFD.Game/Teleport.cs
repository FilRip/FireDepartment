using System.Numerics;

namespace OpenFD.Game;

/// <summary>
/// Escalier (« teleport_stair », [ITEM] Type=TELEPORT) : deux icônes d'escalier vertes (stair_symbol) reliées ; un
/// pompier qui entre par l'une ressort par l'autre après DURATION secondes (bloc A02B du niveau : actif, durée,
/// fonctions ON_ENTER / ON_LEAVE du script, appelées avec (unité, escalier)).
/// </summary>
public sealed class Teleport
{
    public uint Id;
    public string Name = "";
    /// <summary>Les deux bouts : position des icônes, case de la grille (-1 hors de la carte).</summary>
    public Vector2 EndA, EndB;
    /// <summary>Hauteur des icônes : le plancher de chaque bout (la case de sortie est prise à cette hauteur-là).</summary>
    public float HeightA, HeightB;
    public int CellA = -1, CellB = -1;
    public bool Active;
    /// <summary>Caché au départ dans la carte : utilisable une fois que le script le montre (Suburb : ENT_SetVisibility(ent_tp1, 1)).</summary>
    public bool StartHidden;
    /// <summary>Faux escalier (Lab : les deux icônes au même endroit) : il n'emmène nulle part, il affiche un message (ON_ENTER).</summary>
    public bool Fake;
    public float Duration = 1;
    /// <summary>
    /// Escalier extérieur (« street_stairs », Fire Department 2) : le pompier le monte ou le descend à pied, visible
    /// (animation sc_walk_generic_stair), au lieu de disparaître ; son bout bas est cherché au pied de l'escalier (Foot).
    /// </summary>
    public bool Walk;
    public Vector2 Foot;
    /// <summary>
    /// Échelle verticale fixe (« base_ladder_forest », [ITEM] Type=LADDER, CouldBeClimb : Forest, la tour de guet) : le
    /// pompier grimpe le long des barreaux, de LadderBottom (pied, point Dum_climbup du modèle) à LadderTop (haut des
    /// barreaux, Dum_climbdown), puis passe sur le plancher (EndA : Dum_climbout) ; animations sc_vertical_ladder_*.
    /// </summary>
    public bool Vertical;
    public Vector3 LadderBottom, LadderTop;
    /// <summary>Le pompier fait face à l'échelle (vers le plancher du haut).</summary>
    public float LadderHeading;
    public string OnEnter = "", OnLeave = "";
}
