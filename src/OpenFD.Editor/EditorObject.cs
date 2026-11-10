using OpenFD.Fire;
using OpenFD.Formats;
using SN = System.Numerics;

namespace OpenFD.MG;

/// <summary>
/// Un objet de la mission ouverte dans l'éditeur. Sa position et son orientation sont gardées à part : elles ne sont
/// écrites dans l'objet du niveau (Source) qu'à la sauvegarde. Les autres changements (nom, classe, feu…) le sont tout de suite.
/// </summary>
public sealed class EditorObject
{
    public required LevelObject Source { get; set; }
    public required EditorItemKind Kind { get; init; }
    /// <summary>Modèle 3D (chemin dans Gfx) ; null : repère dessiné par l'éditeur.</summary>
    public string? Model { get; set; }
    public SN.Vector3 Position;
    /// <summary>Angles en radians (X, Y, Z), comme dans le niveau.</summary>
    public SN.Vector3 Euler;
    /// <summary>Boîte du modèle, dans le repère de l'objet.</summary>
    public SN.Vector3 BoxMin, BoxMax;
    public bool HasBox;
    /// <summary>Matrice de dessin (posée sur le trottoir comme dans le jeu), et sphère qui l'entoure.</summary>
    public SN.Matrix4x4 Shown = SN.Matrix4x4.Identity;
    public SN.Vector3 Center;
    public float Radius = 100;
    /// <summary>Foyer : classe (paramètres, type de feu), allumé au départ, intensité de départ.</summary>
    public HotPointClass? FireClass;
    public bool Lit;
    public float Intensity;
    /// <summary>Bloqueur (cases de 1 m) ou masque de toit (cm) : taille.</summary>
    public SN.Vector2 Size = new SN.Vector2(1, 1);
    /// <summary>Ajouté dans l'éditeur (pas encore dans le fichier du niveau).</summary>
    public bool Added;
    /// <summary>Arbre : modèle SpeedTree, hauteur (cm) et graine de sa classe.</summary>
    public string? TreeModel;
    public float TreeHeight;
    public int TreeSeed;
    /// <summary>Pièces du modèle à ne pas dessiner (outils qu'un pompier n'a pas) : leur rang dans UnitModel.</summary>
    public HashSet<int>? HiddenParts;

    public string Name => Source.Name;

    /// <summary>Copie (annuler / rétablir) : mêmes valeurs, ensemble des pièces cachées recopié.</summary>
    public EditorObject Copy()
    {
        EditorObject c = (EditorObject)MemberwiseClone();
        c.HiddenParts = HiddenParts != null ? new HashSet<int>(HiddenParts) : null;
        return c;
    }

    /// <summary>Même état que <paramref name="o"/> pour l'éditeur (ce qu'annuler / rétablir doit retrouver).</summary>
    public bool SameAs(EditorObject o) =>
        ReferenceEquals(Source, o.Source) && Position == o.Position && Euler == o.Euler && Size == o.Size && Lit == o.Lit
        && Intensity == o.Intensity && ReferenceEquals(FireClass, o.FireClass) && Model == o.Model && TreeModel == o.TreeModel
        && TreeHeight == o.TreeHeight && TreeSeed == o.TreeSeed
        && (HiddenParts == null ? o.HiddenParts == null : o.HiddenParts != null && HiddenParts.SetEquals(o.HiddenParts));
    public SN.Matrix4x4 World => LevelEdit.MatrixOf(Euler, Position);
}
