using System.Numerics;

namespace OpenFD.Game;

/// <summary>
/// Jet de flammes (gerbe de feu en ligne droite : la canalisation de propane cassée de la station-service de FD1) : un objet
/// posé dont la classe a une section [FLAMEJET]. Il part de l'objet, vers son axe X, sur LENGTH cm ; tant qu'il est
/// visible (ENT_SetVisibility), il est dessiné et les cases qu'il traverse sont fermées à tous (comme une zone
/// d'occupation) : il coupe la pièce en deux.
/// </summary>
public sealed class FlameJet
{
    public uint Id;
    public string Name = "";
    /// <summary>Départ du jet (sortie du tuyau), en cm.</summary>
    public Vector3 Start;
    /// <summary>Bout du jet, en cm.</summary>
    public Vector3 End;
    /// <summary>Largeur de la gerbe (cm) : les flammes grossissent jusqu'à elle au bout du jet.</summary>
    public float Width;
    /// <summary>Zone qui ferme ses cases (dans UnitManager.Occupations) ; Active : le jet brûle en ce moment.</summary>
    public Occupation Zone = new();
}
