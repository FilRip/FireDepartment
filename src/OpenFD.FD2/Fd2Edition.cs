using OpenFD.Game;

namespace OpenFD.FD2;

/// <summary>Fire Department 2 : ce qui diffère de Fire Department 3 pour les mêmes données.</summary>
public sealed class Fd2Edition : IEdition
{
    public string License => "FD2";

    // flèches grises sur fond violet : affichées en blanc
    public bool WhiteCampaignArrows => true;

    // la planche d'icônes de FD2 a la même taille que celle de FD3, mais ses icônes sont rangées ailleurs
    // (retrouvées pixel pour pixel : mêmes dessins qu'en FD3)
    public (int X, int Y, int W, int H) PageRect(PageIcon icon) => icon switch
    {
        PageIcon.TeamHere => (1944, 516, 68, 46),
        PageIcon.TeamAway => (1880, 612, 68, 46),
        PageIcon.TeamIncoming => (1880, 564, 68, 46),
        PageIcon.FireMark => (2000, 421, 24, 29),
        _ => (943, 428, 16, 22),
    };

    // pas d'histoire dans FD2 : les lignes du briefing de la mission (« &BriefingSuburb_Line1 », « …_Line2 »…)
    public IReadOnlyList<string> CampaignStory(Localization loc, string missionDir, string? storyKey)
    {
        List<string> lines = new List<string>();
        for (int i = 1; i < 30; i++)
        {
            string key = $"Briefing{missionDir}_Line{i}";
            string t = loc.Get(key);
            if (t == key)
                break;
            lines.Add(t);
        }
        return lines;
    }
}
