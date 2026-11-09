namespace OpenFD.Game;

/// <summary>
/// Ce qui diffère entre Fire Department 3 et Fire Department 2 pour les mêmes données : rien d'autre. Tout le moteur
/// (unités, feu, chemins, scripts, rendu, objets propres à l'un des deux jeux) est commun ; il ne demande à l'édition
/// que les quelques réponses qui s'opposent d'un jeu à l'autre. FD3 : OpenFD.FD3 ; FD2 : OpenFD.FD2.
/// L'édition est celle de Data\Config\Start.Ini (« License FD3 » ou « FD2 »).
/// </summary>
public interface IEdition
{
    /// <summary>« FD3 » ou « FD2 ».</summary>
    string License { get; }

    /// <summary>
    /// Texte de droite de la page de campagne, en paragraphes. FD3 : l'histoire de la mission (StoryTextKey du .mis) ;
    /// FD2, qui n'a pas d'histoire : les lignes du briefing.
    /// </summary>
    IReadOnlyList<string> CampaignStory(Localization loc, string missionDir, string? storyKey);

    /// <summary>
    /// Flèches de la page de campagne (BUT_PREVIOUS_CAMPAIGN / BUT_NEXT_CAMPAIGN) à éclaircir : celles de FD2 sont grises sur
    /// un fond violet, à peine visibles ; leur gris est alors affiché en blanc.
    /// </summary>
    bool WhiteCampaignArrows { get; }
}
