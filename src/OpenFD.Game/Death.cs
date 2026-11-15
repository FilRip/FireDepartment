using System.Numerics;
using OpenFD.Fire;
using OpenFD.Script;

namespace OpenFD.Game;

// ===================================================================================================================
// Mort des civils et des pompiers (réglage caché EnableDeath, désactivé par défaut) :
//  - un civil conscient (pas encore à l'abri) a une barre de vie, invisible elle aussi (ConsciousLife, de 1 à 0) : elle
//    descend en ConsciousTime secondes (5 min), DeathFireFactor fois plus vite près d'un feu ; à 0, il s'évanouit
//    (pas pendant qu'il monte une échelle ou un escalier, ni s'il est mené par le script) ; les pompiers, eux, ont
//    déjà leur endurance, qui les fait s'évanouir ;
//  - une personne inconsciente (civil, pompier, médecin…) a une seconde barre de vie, invisible pour l'instant
//    (UnconsciousLife, de 1 à 0) ; elle descend peu à peu : DeathTime secondes sans danger autour (5 min) ;
//  - un feu tout proche la fait descendre DeathFireFactor fois plus vite (× 2) : à moins de DeathFireRange, ou, si ce
//    réglage vaut 0, dans la LifeDamageRange de ce feu (donnée du jeu : distance où le feu blesse, 1 m par défaut) ;
//  - un civil inconscient porté par un pompier ne s'affaiblit plus : sa barre reste où elle en est ;
//  - quand elle revient à elle (ranimée, soignée), ses barres sont de nouveau pleines ;
//  - à 0 : le civil passe à l'état « mort » (LEV_GetNumVictims le compte) ; le pompier est mort (ENT_GetUnitState : 4,
//    LEV_GetNumKilledFiremen le compte) et ne peut plus être ranimé. Comme dans le jeu d'origine : l'annonce
//    VOCAL_CIVILIAN_KILLED / VOCAL_FIREMAN_KILLED et la fonction du script OnCivilianDeath / OnFiremanDeath
//    ([DAMAGE] ON_LIFEPOINTS0_SCRIPT des classes base_civilian et base_fireman) ; le corps reste DeadDecaySeconds, puis
//    s'efface en DeadVanishSeconds et laisse au sol la silhouette DeadModel (trace_victim).
// ===================================================================================================================
public sealed partial class UnitManager
{
    /// <summary>Mort des personnes inconscientes (réglage caché « EnableDeath »).</summary>
    public static bool EnableDeath;
    /// <summary>Temps (en secondes) qu'une personne inconsciente tient sans danger autour d'elle avant de mourir.</summary>
    public static float DeathTime = 300f;
    /// <summary>Temps (en secondes) qu'un civil conscient tient sans danger autour de lui avant de s'évanouir.</summary>
    public static float ConsciousTime = 300f;
    /// <summary>Un feu tout proche fait mourir la personne inconsciente ce nombre de fois plus vite.</summary>
    public static float DeathFireFactor = 2f;
    /// <summary>Distance (cm) d'un feu « tout proche » ; 0 : la LifeDamageRange de chaque feu (donnée du jeu).</summary>
    public static float DeathFireRange;
    /// <summary>[DAMAGE] des classes base_civilian et base_fireman : DeadDecayTime, DeadVanishTime, DeadModel.</summary>
    public const float DeadDecaySeconds = 5f, DeadVanishSeconds = 4f;

    /// <summary>Pompiers, médecins… morts pendant la partie (LEV_GetNumKilledFiremen).</summary>
    public int KilledFiremen => Units.Count(u => !u.IsVehicle && u.Destroyed);

    private void UpdateDeath(float dt)
    {
        if (!EnableDeath || DeathTime <= 0)
            return;
        foreach (Unit u in Units)
        {
            if (u.IsVehicle || u.Destroyed)
                continue;
            if (!u.Unconscious)
            {
                u.UnconsciousLife = 1;
                continue;
            }
            if (u.OnBoard || u.Hidden)
                continue;
            u.UnconsciousLife -= dt / DeathTime * (FireVeryClose(u.Position) ? DeathFireFactor : 1);
            if (u.UnconsciousLife <= 0)
                KillUnit(u);
        }
        foreach (Civilian c in Civilians)
        {
            if (c.State == CivilianState.Conscious)
            {
                c.UnconsciousLife = 1;
                if (c.Hidden || c.ScriptWalk || ConsciousTime <= 0)
                    continue;
                c.ConsciousLife = MathF.Max(0, c.ConsciousLife - dt / ConsciousTime * (FireVeryClose(c.Position) ? DeathFireFactor : 1));
                // pas au milieu d'une échelle ou d'un escalier : il tient jusqu'en haut (ou en bas)
                if (c.ConsciousLife <= 0 && c.OnLadder == null && c.Stairs == null)
                    Faint(c);
                continue;
            }
            if (c.State != CivilianState.Unconscious || c.Hidden)
                continue;
            c.ConsciousLife = 1;   // ranimé plus tard par le médecin : de nouveau en forme
            if (c.Carrier != null)
                continue;   // porté par un pompier : il ne s'affaiblit plus
            c.UnconsciousLife -= dt / DeathTime * (FireVeryClose(c.Position) ? DeathFireFactor : 1);
            if (c.UnconsciousLife <= 0)
                KillCivilian(c);
        }
    }

    /// <summary>Un feu qui brûle tout près de ce point (au même étage).</summary>
    private bool FireVeryClose(Vector3 p)
    {
        foreach (HotPoint h in _s.Fire.HotPoints)
        {
            if (!h.OpenFlames)
                continue;
            float r = DeathFireRange > 0 ? DeathFireRange : h.Class.LifeDamageRange;
            if (MathF.Abs(h.Position.Z - p.Z) > 350)
                continue;
            float dx = h.Position.X - p.X;
            float dy = h.Position.Y - p.Y;
            if (dx * dx + dy * dy <= r * r)
                return true;
        }
        return false;
    }

    /// <summary>Civil à bout de forces : il tombe inconscient là où il est (un pompier doit le porter, ou le médecin le ranimer).</summary>
    private void Faint(Civilian c)
    {
        c.State = CivilianState.Unconscious;
        c.UnconsciousLife = 1;
        c.Leader = null;
        c.LadderWait = null;
        c.Path.Clear();
        c.FollowPath.Clear();
        c.Position = new Vector3(c.Position.X, c.Position.Y, Ground(new(c.Position.X, c.Position.Y)));
        Log.Add($"{_s.Time,6:0} s : {c.Name} s'évanouit");
    }

    private void KillUnit(Unit u)
    {
        u.UnconsciousLife = 0;
        u.DiedAt = _s.Time;
        u.Destroyed = true;
        u.Status = "mort";
        u.Path.Clear();
        u.Target = null;
        u.GoTo = null;
        u.GoToVehicle = null;
        u.ReviveTarget = null;
        // ceux qui venaient le ranimer s'arrêtent
        foreach (Unit o in Units.Where(o => o.ReviveTarget == u))
        {
            o.ReviveTarget = null;
            o.HealTimer = 0;
            o.Status = "attend les ordres";
        }
        Sounds.Add(("VOCAL_FIREMAN_KILLED", u.Position));
        Log.Add($"{_s.Time,6:0} s : {u.Name} est mort");
        if (_s.Scripts?.HasFunction("OnFiremanDeath") == true)
            _s.Scripts.TryRun("OnFiremanDeath", Value.I(u.Id));
    }

    private void KillCivilian(Civilian c)
    {
        c.UnconsciousLife = 0;
        c.DiedAt = _s.Time;
        c.State = CivilianState.Dead;
        c.Leader = null;
        // porté sur le dos d'un pompier : il le pose là où il est
        if (c.Carrier is { } carrier)
        {
            DropVictimHere(carrier);
            carrier.Path.Clear();
            carrier.Status = "attend les ordres";
        }
        // ceux qui venaient le chercher s'arrêtent
        foreach (Unit o in Units.Where(o => o.GoTo == c))
        {
            o.GoTo = null;
            o.HealTimer = 0;
            o.Status = "attend les ordres";
        }
        Sounds.Add(("VOCAL_CIVILIAN_KILLED", c.Position));
        Log.Add($"{_s.Time,6:0} s : {c.Name} est mort");
        if (_s.Scripts?.HasFunction("OnCivilianDeath") == true)
            _s.Scripts.TryRun("OnCivilianDeath", Value.I(c.Id));
    }
}
