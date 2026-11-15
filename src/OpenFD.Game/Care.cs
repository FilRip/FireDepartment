using System.Numerics;

namespace OpenFD.Game;

// ===================================================================================================================
// Soins du médecin (Ctrl + clic droit sur un pompier, sur lui-même ou sur un civil, médecin choisi) : il va jusqu'à la
// personne et la soigne sur place, où qu'elle soit (HealSeconds) : endurance pleine, ranimée si elle était inconsciente
// (sauf un mort), barres de vie pleines (réglage EnableDeath). Il suit la personne si elle s'est éloignée entre-temps.
// ===================================================================================================================
public sealed partial class UnitManager
{
    /// <summary>Le médecin va soigner ce pompier (ou lui-même).</summary>
    public void TreatUnit(Unit medic, Unit patient)
    {
        if (!CanTreat(medic) || patient.IsVehicle || patient.Destroyed || patient.OnBoard || patient.Hidden || patient.CarriedBy != null)
            return;
        // déjà en pleine forme : « soins inutiles », et l'ordre est annulé
        if (!patient.Unconscious && patient.Stamina >= patient.StaminaMax - 0.5f && patient.UnconsciousLife >= 1)
        {
            HealNotNeeded(medic);
            return;
        }
        if (!GoToPatient(medic, patient.Position))
            return;
        medic.CareUnit = patient;
        medic.CareCivilian = null;
        medic.Status = patient == medic ? "se soigne" : $"va soigner {UnitName(patient)}";
    }

    /// <summary>Le médecin va soigner ce civil (conscient ou non).</summary>
    public void TreatCivilian(Unit medic, Civilian c)
    {
        if (!CanTreat(medic) || c.Hidden || c.Carrier != null || (c.State != CivilianState.Conscious && c.State != CivilianState.Unconscious))
            return;
        // conscient et en pleine forme (sa barre de vie n'existe qu'avec le réglage EnableDeath) : « soins inutiles »
        if (c.State == CivilianState.Conscious && (!EnableDeath || c.ConsciousLife >= 1))
        {
            HealNotNeeded(medic);
            return;
        }
        if (!GoToPatient(medic, c.Position))
            return;
        medic.CareUnit = null;
        medic.CareCivilian = c;
        medic.Status = $"va soigner {c.Name}";
    }

    // le médecin va jusqu'à la personne ; déjà à portée (ou lui-même) : il reste où il est et commence tout de suite
    private bool GoToPatient(Unit medic, Vector3 at)
    {
        if (Vector2.Distance(new(medic.Position.X, medic.Position.Y), new(at.X, at.Y)) > ReachDistance)
            return Order(medic, new(at.X, at.Y));
        medic.Path.Clear();
        medic.Target = null;
        medic.GoTo = null;
        medic.GoToVehicle = null;
        medic.HealTimer = 0;
        return true;
    }

    // VOCAL_HEAL_NOT_NEEDED (réponse à un ordre du joueur) ; le médecin garde ce qu'il faisait
    private void HealNotNeeded(Unit medic)
    {
        if (PlayerCommand)
            Sounds.Add(("VOCAL_HEAL_NOT_NEEDED", null));
        Log.Add($"{_s.Time,6:0} s : {UnitName(medic)} : soins inutiles");
    }

    /// <summary>Un médecin en état de soigner.</summary>
    public static bool CanTreat(Unit u) => u.CanHeal && !u.IsVehicle && !u.Unconscious && !u.OnBoard && !u.IsCarrying;

    private void Treat(Unit u, float dt)
    {
        if (u.CareUnit == null && u.CareCivilian == null)
            return;
        if (u.Moving)
            return;
        Vector3? at = u.CareUnit is { } pu ? pu.Position : u.CareCivilian is { } pc ? pc.Position : null;
        // (un civil qu'un pompier a pris sur son dos entre-temps : il n'est plus à soigner ici)
        bool gone = u.CareUnit is { } p1 ? p1.Destroyed || p1.OnBoard || p1.Hidden || p1.CarriedBy != null
            : u.CareCivilian is not { } c1 || c1.Hidden || c1.Carrier != null || (c1.State != CivilianState.Conscious && c1.State != CivilianState.Unconscious);
        if (at is not { } pos || gone)
        {
            StopCare(u, "attend les ordres");
            return;
        }
        // la personne s'est éloignée : il la rejoint (un seul essai par arrêt ; sans chemin, il abandonne)
        if (u.CareUnit != u && Vector2.Distance(new(u.Position.X, u.Position.Y), new(pos.X, pos.Y)) > ReachDistance * 2)
        {
            Unit? keepUnit = u.CareUnit;
            Civilian? keepCivilian = u.CareCivilian;
            if (!Order(u, new(pos.X, pos.Y)))
            {
                StopCare(u, "personne hors d'atteinte");
                return;
            }
            u.CareUnit = keepUnit;
            u.CareCivilian = keepCivilian;
            return;
        }
        string who = u.CareUnit is { } p2 ? (p2 == u ? "lui-même" : UnitName(p2)) : u.CareCivilian!.Name;
        if (u.CareUnit != u)
            u.Heading = MathF.Atan2(pos.Y - u.Position.Y, pos.X - u.Position.X);
        // un pompier soigné debout : animation sc_being_healed tant qu'il ne bouge pas (recalculé à chaque pas)
        if (u.CareUnit is { Unconscious: false } standing && standing != u)
            standing.BeingHealed = true;
        u.HealTimer += dt;
        u.Status = $"soigne {who} ({u.HealTimer / HealSeconds:P0})";
        if (u.HealTimer < HealSeconds)
            return;
        if (u.CareUnit is { } patient)
        {
            bool wasDown = patient.Unconscious;
            patient.Stamina = patient.StaminaMax;
            patient.Unconscious = false;
            patient.UnconsciousLife = 1;
            if (wasDown)
                patient.Status = "attend les ordres";
            Log.Add($"{_s.Time,6:0} s : {UnitName(patient)} soigné par le médecin");
        }
        else if (u.CareCivilian is { } c)
        {
            if (c.State == CivilianState.Unconscious)
            {
                c.State = CivilianState.Conscious;
                Sounds.Add(("SC_WAKEUP_CIVIL", c.Position));   // le civil revient à lui
            }
            c.ConsciousLife = 1;
            c.UnconsciousLife = 1;
            Log.Add($"{_s.Time,6:0} s : {c.Name} soigné par le médecin");
        }
        StopCare(u, "attend les ordres");
    }

    private static void StopCare(Unit u, string status)
    {
        u.CareUnit = null;
        u.CareCivilian = null;
        u.HealTimer = 0;
        u.Status = status;
    }
}
