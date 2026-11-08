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
    /// <summary>Faux escalier (Lab : les deux icônes au même endroit) : il n'emmène nulle part, il affiche un message (ON_ENTER).</summary>
    public bool Fake;
    public float Duration = 1;
    public string OnEnter = "", OnLeave = "";
}
