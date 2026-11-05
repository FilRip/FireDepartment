using System.Numerics;
using OpenFD.Formats;

namespace OpenFD.Fire;

public sealed class FireLink
{
    public required HotPoint? A { get; init; }
    public required HotPoint? B { get; init; }
    public required LinkType Type { get; init; }
    public required int Way { get; init; }        // 0 = deux sens, 1 = A vers B, 2 = B vers A (sens 1/2 : hypothèse)
    public required float Tempo { get; init; }
    public required bool Active { get; init; }
    public required uint OpeningId { get; init; }
    public float Elapsed;
    public bool Pushing;

    public IEnumerable<(HotPoint src, HotPoint dst)> Directions()
    {
        if (A == null || B == null)
            yield break;
        if (Way is 0 or 1)
            yield return (A, B);
        if (Way is 0 or 2)
            yield return (B, A);
    }
}
