using OpenFD.Game;

namespace OpenFD.FD3;

/// <summary>Fire Department 3 : le comportement de référence du moteur.</summary>
public sealed class Fd3Edition : IEdition
{
    public string License => "FD3";

    public bool WhiteCampaignArrows => false;

    public (int X, int Y, int W, int H) PageRect(PageIcon icon) => icon switch
    {
        PageIcon.TeamHere => (1876, 688, 68, 46),
        PageIcon.TeamAway => (1944, 736, 68, 46),
        PageIcon.TeamIncoming => (1876, 736, 68, 46),
        PageIcon.FireMark => (1736, 557, 24, 29),
        _ => (1655, 592, 16, 22),
    };

    public IReadOnlyList<string> CampaignStory(Localization loc, string missionDir, string? storyKey) =>
        storyKey == null ? Array.Empty<string>() : new[] { storyKey.StartsWith('&') ? loc.Get(storyKey) : storyKey };
}
