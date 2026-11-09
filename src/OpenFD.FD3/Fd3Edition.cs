using OpenFD.Game;

namespace OpenFD.FD3;

/// <summary>Fire Department 3 : le comportement de référence du moteur.</summary>
public sealed class Fd3Edition : IEdition
{
    public string License => "FD3";

    public bool WhiteCampaignArrows => false;

    public IReadOnlyList<string> CampaignStory(Localization loc, string missionDir, string? storyKey) =>
        storyKey == null ? Array.Empty<string>() : new[] { storyKey.StartsWith('&') ? loc.Get(storyKey) : storyKey };
}
