using System.Numerics;

namespace OpenFD.Game;

// ===================================================================================================================
// Unité cynotechnique (fireman_cyno : [UNIT] TYPE=CYNOPHILE, CanSenseVictim=1, action VICTIMSENSE) : le maître-chien.
// Il n'éteint pas, ne casse rien, ne porte personne. Sur ordre (bouton d'action), le chien flaire autour de lui :
// les victimes enterrées (cachées au départ dans la carte) à moins de DogSenseRange deviennent visibles, et les gravats
// posés sur elles deviennent cassables à la hache. Il aboie (animation SENSE_VICTIM : sc_bark, son SENSE_VICTIM).
// Il ne se déplace pas vers les victimes : les pompiers dégagent les gravats et secourent les civils comme d'habitude.
// ===================================================================================================================
public sealed partial class UnitManager
{
    /// <summary>Rayon de détection du chien : 10 m (script de Paris, WoufWouf : Distance &lt;= 10).</summary>
    public const float DogSenseRange = 1000f;
    /// <summary>Durée de l'aboiement (animation SENSE_VICTIM).</summary>
    public const float DogBarkTime = 2.5f;
    /// <summary>Distance (à plat) sous laquelle des gravats recouvrent une victime enterrée.</summary>
    private const float RubbleOverVictim = 250f;

    /// <summary>Au chargement : les gravats posés sur une victime enterrée ne se cassent pas tant que le chien ne l'a pas trouvée.</summary>
    private void LockBuriedRubble()
    {
        foreach (Civilian c in Civilians.Where(c => c.Hidden))
        {
            // case de la victime : les gravats la couvrent (une porte cassable à côté d'une victime cachée, elle, ne la couvre
            // pas : FD1 Station-service, le client derrière la porte de la réserve)
            int cell = -1;
            if (_s.Grid is { } g)
            {
                (int cx, int cy) = g.CellOf(c.Position);
                cell = g.Index(cx, cy);
            }
            // gravats cassables (Paris : « remains », classe de porte) posés sur la victime, à peu près à sa hauteur
            List<Opening> over = Openings.Where(o => o.Breakable && !o.IsWindow
                && Vector2.Distance(new(o.Position.X, o.Position.Y), new(c.Position.X, c.Position.Y)) < RubbleOverVictim
                && MathF.Abs(o.Position.Z - c.Position.Z) < 300
                && (cell < 0 || o.Cells.Count == 0 || o.Cells.Contains(cell))).ToList();
            if (over.Count == 0)
                continue;
            foreach (Opening o in over)
                o.Breakable = false;
            _buriedRubble[c] = over;
        }
    }

    private readonly Dictionary<Civilian, List<Opening>> _buriedRubble = new();

    /// <summary>Victime encore sous ses gravats : on ne peut la secourir qu'une fois tous ses gravats cassés
    /// (Paris : OnWreckBreakN la rend secourable, « CouldBeRescue »).</summary>
    public bool IsBuried(Civilian c) => _buriedRubble.TryGetValue(c, out List<Opening>? l) && l.Any(o => o.State != OpeningState.Broken);

    private IEnumerable<Opening> RubbleOver(Civilian c) => _buriedRubble.TryGetValue(c, out List<Opening>? l) ? l : Enumerable.Empty<Opening>();

    /// <summary>Le chien flaire (bouton d'action) : révèle les victimes cachées à portée ; renvoie leur nombre.</summary>
    public int DogSense(Unit u)
    {
        if (!u.IsDog || u.Unconscious || u.OnBoard || u.Hidden || u.Destroyed)
            return 0;
        u.Path.Clear();
        u.Status = "flaire les décombres";
        u.SenseTimer = DogBarkTime;
        Sounds.Add(("SENSE_VICTIM", u.Position));
        Vector2 me = new Vector2(u.Position.X, u.Position.Y);
        int found = 0;
        foreach (Civilian c in Civilians.Where(c => c.Hidden && c.State != CivilianState.Rescued))
        {
            if (Vector2.Distance(me, new(c.Position.X, c.Position.Y)) > DogSenseRange)
                continue;
            c.Hidden = false;
            foreach (Opening o in RubbleOver(c).Where(o => o.State != OpeningState.Broken))
                o.Breakable = true;   // gravats à dégager à la hache par un pompier
            found++;
        }
        Log.Add(found > 0 ? $"{_s.Time,6:0} s : le chien a trouvé {found} victime(s) sous les décombres" : $"{_s.Time,6:0} s : le chien ne trouve personne ici");
        return found;
    }

    /// <summary>À chaque pas : fin de l'aboiement.</summary>
    private void UpdateDog(Unit u, float dt)
    {
        if (!u.IsDog || u.SenseTimer <= 0)
            return;
        u.SenseTimer = MathF.Max(0, u.SenseTimer - dt);
        if (u.SenseTimer == 0 && u.Status == "flaire les décombres")
            u.Status = "attend les ordres";
    }
}
