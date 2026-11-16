using System.Numerics;
using OpenFD.Formats;

namespace OpenFD.Game;

// ===================================================================================================================
// Enrouleurs de tuyau (voir WaterReel) : un pompier à pied arrêté à côté remplit sa réserve, de lui-même
// (AutomaticResupply) ou sur ordre (clic droit sur l'enrouleur) ; la réserve de l'enrouleur est sans fin.
// ===================================================================================================================
public sealed partial class UnitManager
{
    /// <summary>Enrouleurs de tuyau du niveau.</summary>
    public List<WaterReel> WaterReels { get; } = new();

    /// <summary>Un pompier se remplit à cette distance de l'enrouleur (au plus × 1,5).</summary>
    public const float WaterReelReach = 200f;

    // faux véhicule qui ravitaille ([CAPACITIES_ACTIONS] CanResupply) : enrouleur de tuyau
    private void LoadWaterReel(LevelObject o, string classRel)
    {
        ClassFile? c = Class("Design\\Unit\\" + classRel + ".class");
        if (c == null || !c.GetBool("CAPACITIES_ACTIONS", "CanResupply"))
            return;
        float power = c.GetFloat("CAPACITIES_RESERVE", "ResupplyPower", 150);
        if (power <= 0)
            return;
        string? m = c.Get("DISPLAY", "Model")?.Trim('"', ' ');
        WaterReels.Add(new WaterReel
        {
            Id = o.Id, Name = o.Name, Position = o.Position, World = o.Matrix, StartVisible = o.StartVisible, Power = power,
            Agent = (c.Get("CAPACITIES_RESERVE", "FireAgent") ?? "WATER").Trim().ToUpperInvariant(),
            Model = string.IsNullOrEmpty(m) ? "" : SceneAssets.GfxPath("Unit\\FireVehicle", m),
        });
        Log.Add($"Enrouleur de tuyau : {o.Name} ({o.Position.X:0},{o.Position.Y:0}){(o.StartVisible ? "" : ", caché au départ")}");
    }

    /// <summary>Enrouleur visible (le script peut le cacher ou le montrer).</summary>
    public bool WaterReelActive(WaterReel r) =>
        _s.Host?.Visibility.TryGetValue(r.Id, out bool v) == true ? v : r.StartVisible;

    /// <summary>Enrouleur actif le plus proche de ce point, à portée de remplissage, ou null.</summary>
    private WaterReel? WaterReelNear(Vector3 p) =>
        WaterReels.Where(r => WaterReelActive(r) && Vector3.Distance(r.Position, p) < WaterReelReach * 1.5f)
            .OrderBy(r => Vector3.Distance(r.Position, p)).FirstOrDefault();

    /// <summary>Clic droit sur un enrouleur : le pompier y va remplir sa réserve.</summary>
    public void GoToWaterReel(Unit u, WaterReel r)
    {
        if (u.IsVehicle || u.Nozzle == null || !WaterReelActive(r))
            return;
        Vector2 me = new Vector2(u.Position.X, u.Position.Y);
        Vector2 at = new Vector2(r.Position.X, r.Position.Y);
        Vector2 dir = Vector2.Distance(me, at) > 1 ? Vector2.Normalize(me - at) : Vector2.UnitX;
        if (!Order(u, Walkable(at + dir * (WaterReelReach * 0.6f))))
            return;
        u.GoToReel = r;
        u.Status = "va remplir sa réserve";
    }

    /// <summary>
    /// Recharge à un enrouleur (appelée par Resupply quand aucun camion n'est à côté) : vrai si le pompier est près d'un
    /// enrouleur du bon agent. Arrivé sur ordre, il y reste jusqu'à ce que sa réserve soit pleine.
    /// </summary>
    private bool ReelResupply(Unit u, OpenFD.Fire.Extinguisher n, bool asked, float dt)
    {
        WaterReel? r = WaterReelNear(u.Position);
        if (r == null || r.Agent != n.Agent)
            return false;
        if (n.Reserve < n.ReserveMax)
        {
            n.Reserve = MathF.Min(n.ReserveMax, n.Reserve + r.Power * ResupplyScale * dt);
            if (n.Reserve > n.ReserveMax - 0.01f)
                n.Reserve = n.ReserveMax;
            if (asked || u.Status.StartsWith("attend") || u.Status.StartsWith("se recharge"))
                u.Status = $"se recharge ({n.Reserve:0}/{n.ReserveMax:0})";
        }
        if (n.Reserve >= n.ReserveMax)
        {
            if (u.GoToReel != null || u.Status.StartsWith("se recharge"))
                u.Status = "réserve pleine";
            u.GoToReel = null;
        }
        return true;
    }
}
