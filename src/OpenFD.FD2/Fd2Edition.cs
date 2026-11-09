using OpenFD.Game;

namespace OpenFD.FD2;

/// <summary>Fire Department 2 : ce qui diffère de Fire Department 3 pour les mêmes données.</summary>
public sealed class Fd2Edition : IEdition
{
    public string License => "FD2";

    // flèches grises sur fond violet : affichées en blanc
    public bool WhiteCampaignArrows => true;

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
