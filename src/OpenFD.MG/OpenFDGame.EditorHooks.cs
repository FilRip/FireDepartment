namespace OpenFD.MG;

/// <summary>
/// Points d'accroche de l'éditeur de missions (projet OpenFD.Editor, qui compile ces mêmes fichiers avec les siens). Dans
/// le jeu, ces méthodes n'ont pas de corps : le compilateur retire leurs appels, rien ne change.
/// </summary>
public sealed partial class OpenFDGame
{
    /// <summary>Lancé par l'éditeur : décor des missions sans script, interface de l'éditeur à la place du jeu.</summary>
    public bool EditorMode { get; init; }

    /// <summary>Après le chargement des effets et des réglages : l'éditeur prend la main.</summary>
    partial void EditorStart();

    partial void EditorUpdate(float dt);

    partial void EditorDraw(float time);

    partial void EditorChar(char ch);

    /// <summary>Chargement d'une mission, décor fait : les objets du niveau, dessinés un par un.</summary>
    partial void EditorLoadObjects();

    /// <summary>Rendu 3D, après le décor et les arbres : les objets de l'éditeur.</summary>
    partial void EditorRender3D(float time);

    /// <summary>Carte d'ombres, après le décor et les arbres : les objets de l'éditeur.</summary>
    partial void EditorShadows();

    /// <summary>Couvercles noirs des pièces : l'éditeur peut les cacher.</summary>
    partial void EditorLids(ref bool show);

    /// <summary>Écran de chargement : l'éditeur dessine le sien (sans l'image de la mission) et met <paramref name="drawn"/> à vrai.</summary>
    partial void EditorLoading(ref bool drawn);
}
