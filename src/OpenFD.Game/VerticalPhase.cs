namespace OpenFD.Game;

/// <summary>
/// Échelle verticale fixe (Forest : la tour de guet) : où en est le pompier. En montant : Climb (il grimpe, animation en
/// boucle), puis Out (il passe sur le plancher du haut) ; en descendant : On (il enjambe le haut de l'échelle), puis Descend.
/// </summary>
public enum VerticalPhase { None, Climb, Out, On, Descend }
