using System.Numerics;
using OpenFD.Fire;
using OpenFD.Formats;

namespace OpenFD.Game;

/// <summary>
/// Backdraft. Certains foyers de la carte sont marqués « MakeBackdraft » (London : deux pièces de la banque).
/// Dans une pièce dont toutes les portes et fenêtres sont fermées, un tel foyer qui atteint son seuil (SleepingThreshold)
/// « s'endort » faute d'air : il ne grandit plus, la fumée et la chaleur restent enfermées (de la fumée filtre sous les portes).
///  • Une porte de la pièce qui s'ouvre (ou qu'on casse) : l'air entre d'un coup, la pièce s'embrase (backdraft),
///    les flammes jaillissent par la porte, les pompiers tout près sont blessés ; voix « VOCAL_BACKDRAFT ».
///  • Une fenêtre cassée avant : la fumée s'échappe, la chaleur retombe, les foyers se réveillent doucement, sans backdraft.
/// ([OPENING] AfterBackdraftIntensity = 50 : feu ajouté près de l'ouverture.)
/// </summary>
public sealed class BackdraftRoom
{
    public required string Name { get; init; }
    public required Rooms.Room Box { get; init; }
    public readonly List<HotPoint> Sleepers = new();      // foyers à backdraft de la pièce
    public readonly List<HotPoint> All = new();           // tous les foyers de la pièce
    public readonly List<Opening> Openings = new();
    public bool Closed => Openings.All(o => o.State == OpeningState.Closed);
    public bool Asleep => Sleepers.Any(h => h.Sleeping);
}
