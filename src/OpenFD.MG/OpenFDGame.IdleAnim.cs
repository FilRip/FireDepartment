using OpenFD.Formats;

namespace OpenFD.MG;

// attente d'un pompier ou d'un civil qui suit son guide : une pose calme en boucle et, toutes les 3 secondes, une chance sur deux de jouer une fois un
// petit geste (se nettoyer les bras, fouiller sa valise…) ; les animations WAIT1 à WAIT3 de la classe sont triées par
// leur mouvement : les plus agitées sont les gestes, la première calme sert de pose (le médecin a sa valise en WAIT1)
public sealed partial class OpenFDGame
{
    private const float IdleRollEvery = 3f;
    private const float IdleFidgetChance = 0.5f;
    private const float FidgetMotion = 0.8f;
    private static readonly string[] IdleTags = { "WAIT1", "WAIT2", "WAIT3" };
    private readonly Dictionary<string, float> _clipMotion = new(StringComparer.OrdinalIgnoreCase);

    // somme, sur les os, du plus grand écart de rotation par rapport à la première clé
    private float ClipMotion(string file)
    {
        if (_clipMotion.TryGetValue(file, out float m))
            return m;
        m = 0;
        if (Clip(file) is { } clip)
            foreach (AnimClip.Track t in clip.Tracks.Values)
                if (t.Rot.Length > 1)
                    m += t.Rot.Max(r => 1 - MathF.Abs(System.Numerics.Quaternion.Dot(r.v, t.Rot[0].v)));
        return _clipMotion[file] = m;
    }

    private void IdleSetup(CharAnim a)
    {
        a.IdleReady = true;
        foreach (string tag in IdleTags)
        {
            if (!a.Tags.TryGetValue(tag, out string? file))
                continue;
            if (ClipMotion(file) >= FidgetMotion)
            {
                if (!a.Fidgets.Contains(file, StringComparer.OrdinalIgnoreCase))
                    a.Fidgets.Add(file);
            }
            else if (a.IdleBase == null)
                a.IdleBase = file;
        }
        // rien de calme : on garde WAIT1 tel quel, sans geste
        if (a.IdleBase == null)
            a.Fidgets.Clear();
    }

    // étiquette à jouer quand le personnage attend (« WAIT1 » de UnitTag)
    private string IdleTag(CharAnim a, float dt)
    {
        if (!a.IdleReady)
            IdleSetup(a);
        if (a.IdleBase == null)
            return "WAIT1";
        if (a.Fidget != null)
        {
            AnimClip? clip = Clip(a.Fidget[1..]);
            if (a.Tag == a.Fidget && (clip == null || a.Time >= clip.Duration))
            {
                a.Fidget = null;
                a.IdleFor = 0;
            }
            else
                return a.Fidget;
        }
        if (!_paused)
            a.IdleFor += dt;
        if (a.IdleFor >= IdleRollEvery)
        {
            a.IdleFor = 0;
            if (a.Fidgets.Count > 0 && Random.Shared.NextSingle() < IdleFidgetChance)
            {
                a.Fidget = "@" + a.Fidgets[Random.Shared.Next(a.Fidgets.Count)];
                return a.Fidget;
            }
        }
        return "~" + a.IdleBase;
    }

    private static void IdleReset(CharAnim a)
    {
        a.IdleFor = 0;
        a.Fidget = null;
    }
}
