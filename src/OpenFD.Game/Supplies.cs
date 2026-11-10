using System.Numerics;
using OpenFD.Formats;
using OpenFD.Script;

namespace OpenFD.Game;

public sealed partial class UnitManager
{
    public List<SupplyPoint> Supplies { get; } = new();
    public List<TriggerZone> Triggers { get; } = new();
    /// <summary>Dernière zone déclenchée et quand (une cinématique qu'elle lance la désactive).</summary>
    public TriggerZone? LastTrigger;
    public double LastTriggerTime = -99;
    private string? _saveCivilianScript;

    private void LoadSupplies(Level level)
    {
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform))
        {
            ClassFile? c = Class("Design\\" + o.ClassPath["design\\".Length..] + ".class");
            if (c == null)
                continue;
            float r = c.GetFloat("CAPACITIES_RESERVE", "ResupplyRadius", 0), p = c.GetFloat("CAPACITIES_RESERVE", "ResupplyPower", 0);
            if (r <= 0 || p <= 0)
                continue;
            Supplies.Add(new SupplyPoint(o.Id, o.Name, o.Position, r, p, (c.Get("CAPACITIES_RESERVE", "FireAgent") ?? "WATER").Trim().ToUpperInvariant()));
        }
    }

    /// <summary>
    /// Véhicule dont la réserve est entamée, à l'arrêt : il se recharge près d'une bouche d'incendie (à moins de son
    /// ResupplyRadius, 20 m), sinon dans le rayon d'action d'un véhicule qui ravitaille les autres (camion-citerne, 30 m ;
    /// bateau-pompe), du même agent. Une citerne sans lance se remplit aussi aux bouches d'incendie.
    /// </summary>
    private void VehicleResupply(Unit v, float dt)
    {
        string agent;
        float have, max;
        if (v.Nozzle is { InfiniteReserve: false } n)
        {
            agent = n.Agent;
            have = n.Reserve;
            max = n.ReserveMax;
        }
        else if (v.Nozzle == null && v.TankMax > 0)
        {
            agent = v.SupplyAgent;
            have = v.TankReserve;
            max = v.TankMax;
        }
        else
            return;
        if (have >= max || v.Moving || v.OnBoard || v.Destroyed)
            return;
        float power = 0;
        foreach (SupplyPoint sp in Supplies)
            if (sp.Agent == agent && Vector2.Distance(new(sp.Position.X, sp.Position.Y), new(v.Position.X, v.Position.Y)) <= sp.Radius)
                power = MathF.Max(power, sp.Power);
        Unit? donor = null;
        if (power <= 0)
        {
            donor = VehicleDonor(v);
            if (donor != null)
                power = donor.VehicleSupplyPower;
        }
        if (power <= 0)
            return;
        float give = MathF.Min(max - have, power * ResupplyScale * 4 * dt);   // un camion se remplit 4 fois plus vite qu'un pompier
        if (donor != null)
        {
            give = MathF.Min(give, SupplyStock(donor));
            if (donor.Nozzle is { InfiniteReserve: false } dn)
                dn.Reserve -= give;
            else if (donor.Nozzle == null && donor.TankMax > 0)
                donor.TankReserve -= give;
        }
        have = MathF.Min(max, have + give);
        if (have > max - 0.01f)
            have = max;
        if (v.Nozzle is { } vn)
            vn.Reserve = have;
        else
            v.TankReserve = have;
        if (v.Target == null && !v.Status.StartsWith("arrose"))
            v.Status = $"se recharge ({AgentName(agent)} {have:0}/{max:0})";
        if (have >= max && v.Status.StartsWith("se recharge"))
            v.Status = "réserve pleine";
    }

    /// <summary>Ce qu'un véhicule peut encore donner (sans fin : bateau-pompe, réserve infinie).</summary>
    private static float SupplyStock(Unit w) =>
        w.Nozzle is { InfiniteReserve: false } wn ? wn.Reserve : w.Nozzle == null && w.TankMax > 0 ? w.TankReserve : float.MaxValue;

    /// <summary>
    /// Véhicule qui ravitaille <paramref name="v"/> (garé dans son rayon d'action, même agent, pas vide) ; null s'il n'y en a
    /// pas. Une citerne ou un bateau-pompe ne se ravitaille pas auprès d'un autre.
    /// </summary>
    public Unit? VehicleDonor(Unit v)
    {
        if (!v.IsVehicle || v.SupplyRadius > 0)
            return null;
        string agent = SupplyAgentOf(v);
        Vector2 me = new Vector2(v.Position.X, v.Position.Y);
        return Units.Where(w => w != v && w.IsVehicle && w.VehicleSupplyPower > 0 && !w.Moving && !w.Destroyed && !w.OnBoard && !w.Hidden
                && SupplyAgentOf(w) == agent && SupplyStock(w) > 0 && Vector2.Distance(me, new(w.Position.X, w.Position.Y)) <= w.SupplyRadius)
            .OrderBy(w => Vector2.Distance(me, new(w.Position.X, w.Position.Y))).FirstOrDefault();
    }

    // ---- zones de déclenchement (scripts de la mission : toits, masques, événements) ----
    private void LoadTriggers(Level level)
    {
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.Trigger && o.HasTransform))
        {
            if (o.GetComponent(0xA028) is not { } comp)
                continue;
            byte[] b = o.ComponentBytes(comp).ToArray();
            int k = b.AsSpan().IndexOf(new byte[] { 0xCC, 0xCC, 0xBB, 0xBB });
            if (k < 0 || k + 8 > b.Length)
                continue;
            int p = k + 4, len = BitConverter.ToInt32(b, p);
            if (len <= 1 || p + 4 + len > b.Length)
                continue;
            string script = System.Text.Encoding.Latin1.GetString(b, p + 4, len - 1);
            // puis On_Leave (longueur + texte) et Enabled (octet) : un déclencheur peut être éteint au départ
            bool enabled = true;
            string onLeave = "";
            int q = p + 4 + len;
            if (q + 4 <= b.Length)
            {
                int len2 = BitConverter.ToInt32(b, q);
                if (len2 >= 0 && len2 < 128 && q + 4 + len2 < b.Length)
                {
                    enabled = b[q + 4 + len2] != 0;
                    if (len2 > 1)
                        onLeave = System.Text.Encoding.Latin1.GetString(b, q + 4, len2 - 1);
                }
            }
            // fin du composant : trois flottants = taille de la zone (x, y, z)
            int e = b.Length - 4 - 12;
            Vector3 size = new Vector3(BitConverter.ToSingle(b, e), BitConverter.ToSingle(b, e + 4), BitConverter.ToSingle(b, e + 8));
            if (!float.IsFinite(size.X) || size.X <= 0)
                size = new Vector3(200, 200, 100);
            Matrix4x4.Invert(o.Matrix, out Matrix4x4 inv);
            Triggers.Add(new TriggerZone
            {
                Id = o.Id, Name = o.Name, Script = script, OnLeave = onLeave, Inverse = inv, Position = o.Position, Enabled = enabled,
                Half = new Vector3(MathF.Max(150, size.X / 2), MathF.Max(150, size.Y / 2), MathF.Max(300, size.Z)),
            });
        }
    }

    private void CheckTriggers()
    {
        if (_s.Scripts is not { } scripts || Triggers.Count == 0)
            return;
        foreach (TriggerZone t in Triggers.ToList())
        {
            foreach (Unit u in Units.ToList())
                if (Units.Contains(u) && !u.OnBoard && !u.Hidden)
                    Visit(t, u.Id, u.Position);
            // civils menés par le script (Port : le conducteur arrive à sa locomotive, le train part)
            foreach (Civilian c in Civilians.ToList())
                if (c.ScriptWalk && !c.Hidden && c.Leader == null && c.Carrier == null && c.State is CivilianState.Conscious or CivilianState.Unconscious)
                    Visit(t, (int)c.Id, c.Position);
        }

        void Visit(TriggerZone t, int id, Vector3 pos)
        {
            if (!t.Enabled || string.IsNullOrEmpty(t.Script))
                return;
            Vector3 l = Vector3.Transform(pos, t.Inverse);
            bool inside = MathF.Abs(l.X) <= t.Half.X && MathF.Abs(l.Y) <= t.Half.Y && MathF.Abs(l.Z) <= t.Half.Z;
            if (!inside)
            {
                // il en sort : ON_LEAVE (Street : le tram repart quand le camion a quitté sa voie)
                if (t.Inside.Remove(id) && t.OnLeave.Length > 0)
                    scripts.TryRun(t.OnLeave, Value.I(id), Value.I(t.Id));
                return;
            }
            if (!t.Inside.Add(id))
                return;
            LastTrigger = t;
            LastTriggerTime = _s.Time;
            scripts.TryRun(t.Script, Value.I(id), Value.I(t.Id));
        }
    }

    /// <summary>Civil mis à l'abri : le script de la mission est prévenu ([COMMAND_CALLBACKS] CmdSaveCivilian).</summary>
    private void Save(Civilian c)
    {
        c.State = CivilianState.Rescued;
        c.Leader = null;
        string fn = string.IsNullOrEmpty(_saveCivilianScript) ? "OnCivilianSaved" : _saveCivilianScript;
        _s.Scripts?.TryRun(fn, Value.I(c.Id));
    }

    /// <summary>
    /// Débogage (F6) : le civil est sauvé tout de suite, comme s'il était arrivé à l'ambulance (compté, script prévenu) ;
    /// il disparaît sur place. Le pompier qui le portait le lâche.
    /// </summary>
    public bool DebugSave(Civilian c)
    {
        if (c.State is not (CivilianState.Conscious or CivilianState.Unconscious))
            return false;
        if (c.Carrier is { } u)
        {
            u.Carrying = null;
            u.Carry = CarryPhase.None;
            u.CarryTimer = 0;
            u.Path.Clear();
            u.Status = "attend les ordres";
        }
        foreach (Unit w in Units.Where(w => w.Carrying == c))
        {
            w.Carrying = null;
            w.Carry = CarryPhase.None;
            w.CarryTimer = 0;
        }
        c.Carrier = null;
        c.Stairs = null;
        Save(c);
        Log.Add($"{_s.Time,6:0} s : {c.Name} sauvé (débogage) ({Rescued} sauvé(s))");
        Sounds.Add(("VOCAL_CIVILIAN_SAVED", null));
        return true;
    }

    // ---- état de la partie pour les scripts (conditions de défaite) ----
    public bool AnyDispatched => Units.Any(u => !u.FromLevel) && !(_initialDeferred && !_initialCalled);   // (les véhicules posés dans le niveau ne sont pas des secours envoyés)
    public bool HealVehicleRemaining => !AnyDispatched || Units.Any(u => u.IsAmbulance && !u.Fixed);
    public bool SupplierRemaining => !AnyDispatched || Units.Any(u => u.IsVehicle && (u.ResupplyPower > 0 || u.VehicleSupplyPower > 0));
    public int Victims => Civilians.Count(c => c.State == CivilianState.Dead && !_deadAtStart.Contains(c));
    private HashSet<Civilian>? _deadAtStartSet;
    private HashSet<Civilian> _deadAtStart => _deadAtStartSet ??= Civilians.Where(c => c.State == CivilianState.Dead).ToHashSet();
    /// <summary>
    /// Pompiers de la mission (LEV_GetNumFiremen) : ceux sur place, ceux des équipes en route et ceux des renforts prévus.
    /// Des missions (MonteCristo) n'ont que des renforts appelés au départ : sans eux, le compte serait 0 et la limite de
    /// pompiers perdus aussi (défaite immédiate).
    /// </summary>
    public int FiremenCount => Units.Count(u => !u.IsVehicle) + Units.Sum(v => v.Crew.Count) + DeferredFiremen
        + _pending.Sum(p => CrewSize(p.cfg)) + _toGrant.Sum(p => CrewSize(p.cfg));
}
