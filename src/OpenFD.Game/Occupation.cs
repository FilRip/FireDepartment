namespace OpenFD.Game;

/// <summary>
/// Zone d'occupation posée par le concepteur de la mission (objet dont le nom contient « occupation » : Port,
/// « occupation_train » sur la rame en gare ; Ch_Plant, « truck_occupation_1 » devant le portail fermé). Ses cases,
/// dessinées dans l'éditeur (LevelObject.OccupationCells), sont fermées tant que l'objet est visible ; le script la pose
/// ou la lève par ENT_SetVisibility.
/// </summary>
public sealed class Occupation
{
    public uint Id;
    public string Name = "";
    /// <summary>Visible au départ (avant que le script n'y touche).</summary>
    public bool StartVisible;
    /// <summary>Posée en ce moment : ses cases sont comptées dans PathFind.Occupied.</summary>
    public bool Active;
    /// <summary>Cases fermées à tous.</summary>
    public List<int> Cells = new();
    /// <summary>Cases fermées aux véhicules : les mêmes et une case autour (un véhicule ne passe pas entre deux wagons).</summary>
    public List<int> VehicleCells = new();
}
