using System.Numerics;
using OpenFD.Formats;
using OpenFD.Script;

namespace OpenFD.Game;

/// <summary>Point d'eau de la carte (bouche d'incendie…) : classe [CAPACITIES_RESERVE] ResupplyRadius / ResupplyPower.</summary>
public sealed record SupplyPoint(uint Id, string Name, Vector3 Position, float Radius, float Power, string Agent);

/// <summary>Zone de déclenchement de la carte (Trigger) : script appelé quand une unité y entre (composant A028).</summary>
public sealed class TriggerZone
{
    public required uint Id { get; init; }
    public required string Name { get; init; }
    public required string Script { get; set; }
    public required Matrix4x4 Inverse { get; init; }
    public required Vector3 Half { get; init; }
    public Vector3 Position;
    public bool Enabled = true;   // ENT_SetPropertyValueS32(id, "trigger", "ENABLED", 0/1)
    public readonly HashSet<int> Inside = new();
}

public sealed partial class UnitManager
{
    public List<SupplyPoint> Supplies { get; } = new();
    public List<TriggerZone> Triggers { get; } = new();
    /// <summary>Dernière zone déclenchée et quand (une cinématique qu'elle lance la désactive).</summary>
    public TriggerZone? LastTrigger; public double LastTriggerTime = -99;
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
    /// Véhicule à lance dont la réserve est entamée, à l'arrêt : il se recharge près d'une bouche d'incendie (à moins de son
    /// ResupplyRadius, 20 m) ou près d'un véhicule d'un autre type qui recharge (camion-citerne…).
    /// </summary>
    private void VehicleResupply(Unit v, float dt)
    {
        if (v.Nozzle is not { InfiniteReserve: false } n || n.Reserve >= n.ReserveMax || v.Moving || v.OnBoard || v.Destroyed)
            return;
        float power = 0;
        foreach (SupplyPoint sp in Supplies)
            if (sp.Agent == n.Agent && Vector2.Distance(new(sp.Position.X, sp.Position.Y), new(v.Position.X, v.Position.Y)) <= sp.Radius)
                power = MathF.Max(power, sp.Power);
        Unit? donor = null;
        if (power <= 0)
        {
            donor = Units.Where(w => w.IsVehicle && w != v && w.ResupplyPower > 0 && !w.Moving && !w.Destroyed
                    && !string.Equals(w.ClassPath, v.ClassPath, StringComparison.OrdinalIgnoreCase)
                    && (w.Nozzle == null || w.Nozzle.InfiniteReserve || w.Nozzle.Reserve > 0)
                    && Vector3.Distance(w.Position, v.Position) < VehicleReach * 2.5f)
                .OrderByDescending(w => w.ResupplyPower).FirstOrDefault();
            if (donor != null)
                power = donor.ResupplyPower;
        }
        if (power <= 0)
            return;
        float give = MathF.Min(n.ReserveMax - n.Reserve, power * ResupplyScale * 4 * dt);   // un camion se remplit 4 fois plus vite qu'un pompier
        if (donor?.Nozzle is { InfiniteReserve: false } dn)
        {
            give = MathF.Min(give, dn.Reserve);
            dn.Reserve -= give;
        }
        n.Reserve += give;
        if (v.Target == null && !v.Status.StartsWith("arrose"))
            v.Status = $"se recharge en eau ({n.Reserve:0}/{n.ReserveMax:0})";
        if (n.Reserve >= n.ReserveMax && v.Status.StartsWith("se recharge"))
            v.Status = "réserve pleine";
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
            int q = p + 4 + len;
            if (q + 4 <= b.Length)
            {
                int len2 = BitConverter.ToInt32(b, q);
                if (len2 >= 0 && len2 < 128 && q + 4 + len2 < b.Length)
                    enabled = b[q + 4 + len2] != 0;
            }
            // fin du composant : trois flottants = taille de la zone (x, y, z)
            int e = b.Length - 4 - 12;
            Vector3 size = new Vector3(BitConverter.ToSingle(b, e), BitConverter.ToSingle(b, e + 4), BitConverter.ToSingle(b, e + 8));
            if (!float.IsFinite(size.X) || size.X <= 0)
                size = new Vector3(200, 200, 100);
            Matrix4x4.Invert(o.Matrix, out Matrix4x4 inv);
            Triggers.Add(new TriggerZone
            {
                Id = o.Id, Name = o.Name, Script = script, Inverse = inv, Position = o.Position, Enabled = enabled,
                Half = new Vector3(MathF.Max(150, size.X / 2), MathF.Max(150, size.Y / 2), MathF.Max(300, size.Z)),
            });
        }
    }

    private void CheckTriggers()
    {
        if (_s.Scripts is not { } scripts || Triggers.Count == 0)
            return;
        foreach (TriggerZone t in Triggers.ToList())
            foreach (Unit u in Units.ToList())
            {
                if (!Units.Contains(u) || u.OnBoard || u.Hidden || !t.Enabled || string.IsNullOrEmpty(t.Script))
                    continue;
                Vector3 l = Vector3.Transform(u.Position, t.Inverse);
                bool inside = MathF.Abs(l.X) <= t.Half.X && MathF.Abs(l.Y) <= t.Half.Y && MathF.Abs(l.Z) <= t.Half.Z;
                if (!inside)
                {
                    t.Inside.Remove(u.Id);
                    continue;
                }
                if (!t.Inside.Add(u.Id))
                    continue;
                LastTrigger = t; LastTriggerTime = _s.Time;
                scripts.TryRun(t.Script, Value.I(u.Id), Value.I(t.Id));
            }
    }

    /// <summary>Civil mis à l'abri : le script de la mission est prévenu ([COMMAND_CALLBACKS] CmdSaveCivilian).</summary>
    private void Save(Civilian c)
    {
        c.State = CivilianState.Rescued; c.Leader = null;
        string fn = string.IsNullOrEmpty(_saveCivilianScript) ? "OnCivilianSaved" : _saveCivilianScript;
        _s.Scripts?.TryRun(fn, Value.I(c.Id));
    }

    // ---- état de la partie pour les scripts (conditions de défaite) ----
    public bool AnyDispatched => Units.Count > 0 && !(_initialDeferred && !_initialCalled);
    public bool HealVehicleRemaining => !AnyDispatched || Units.Any(u => u.IsAmbulance);
    public bool SupplierRemaining => !AnyDispatched || Units.Any(u => u.IsVehicle && u.ResupplyPower > 0);
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
