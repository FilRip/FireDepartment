using System.Numerics;
using FD3.Formats;

namespace FD3.Game;

// unités créées et commandées par les scripts de mission (le didacticiel surtout) :
// ENT_CreateUnitPath, ENT_SetPosition, ENT_SetOrientation, IA_CmdMoveTo, IA_CmdEmbark, IA_CmdAttackFire, ENT_SetReserve…
public sealed partial class UnitManager
{
    private readonly Dictionary<string, Unit> _scriptNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Unit> _byId = new();

    public Unit? ById(long id)
    {
        if (id < int.MinValue || id > int.MaxValue) return null;
        if (_byId.TryGetValue((int)id, out var u)) return u;
        u = Units.FirstOrDefault(x => x.Id == id);
        if (u != null) _byId[u.Id] = u;
        return u;
    }

    public Unit? ByScriptName(string name) => _scriptNames.TryGetValue(name, out var u) && Units.Contains(u) ? u : null;

    /// <summary>
    /// ENT_CreateUnitPath("Data/Design/Unit/Fireman/fireman_sp.class", nom, x, y, z) : nouvelle unité posée là, à l'arrêt.
    /// </summary>
    public Unit? SpawnScripted(string classPath, string name, Vector3 pos)
    {
        var rel = classPath.Replace('/', '\\');
        int i = rel.IndexOf("Unit\\", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        rel = rel[(i + 5)..];
        if (rel.EndsWith(".class", StringComparison.OrdinalIgnoreCase)) rel = rel[..^6];
        bool vehicle = rel.StartsWith("Firevehicle", StringComparison.OrdinalIgnoreCase);
        var u = Create(rel, vehicle, new Vector2(pos.X, pos.Y));
        if (u == null) { Log.Add($"Unité du script introuvable : {classPath}"); return null; }
        u.ScriptName = name; u.Status = "attend les ordres";
        _scriptNames[name] = u; _byId[u.Id] = u;
        return u;
    }

    public void Teleport(Unit u, Vector2 p)
    {
        u.Path.Clear();
        u.Position = new Vector3(p, Ground(p));
        foreach (var pas in u.Passengers) pas.Position = u.Position;
    }

    /// <summary>IA_CmdMoveTo : l'unité va à ce point (le véhicule y roule ; sans chemin trouvé, en ligne droite).</summary>
    public void ScriptMove(Unit u, Vector2 dest)
    {
        if (u.OnBoard) return;
        u.Target = null; u.Opening = null; u.GoTo = null; u.GoToVehicle = null; u.Boarding = false;
        if (!Order(u, dest, null, drive: u.IsVehicle, refuseUnreachable: false))
        {
            u.Path.Clear(); u.Path.Enqueue(dest);
        }
        u.Status = u.IsVehicle ? "roule" : "se déplace";
    }

    /// <summary>IA_CmdEmbark : le pompier va monter dans le véhicule.</summary>
    public void ScriptEmbark(Unit u, Unit vehicle)
    {
        if (u.IsVehicle || !vehicle.IsVehicle) return;
        if (vehicle.Seats <= 0) vehicle.Seats = 4;
        if (Vector2.Distance(new(u.Position.X, u.Position.Y), new(vehicle.Position.X, vehicle.Position.Y)) < VehicleReach) { Board(u, vehicle); return; }
        BoardVehicle(u, vehicle);
        if (!u.Boarding) Board(u, vehicle);   // pas de chemin : il monte tout de suite
    }

    // ---- arrivée des équipes commandée par le script ----
    private (string cfg, int spawn)? _initial;
    private bool _initialDeferred, _initialCalled;
    private readonly List<(float eta, string cfg, int spawn)> _toGrant = new();
    private int _deferredCrew;

    /// <summary>
    /// Le script de la mission appelle LEV_CallInitialDispatch (Fireworks, Vegas…) ou LEV_GrantReinforcement (Tutorial…) :
    /// l'équipe de départ n'arrive qu'à cet appel, et chaque renfort n'est envoyé (avec son délai) qu'une fois accordé.
    /// </summary>
    public void ScriptDispatch(bool initialByScript, bool reinforcementsByScript)
    {
        if (initialByScript && _initial is { } ini && !_initialCalled)
        {
            _deferredCrew = FiremenCount;
            Units.Clear(); _byId.Clear(); _initialDeferred = true;
        }
        if (reinforcementsByScript) { _toGrant.AddRange(_pending.OrderBy(p => p.eta)); _pending.Clear(); }
    }

    /// <summary>Pompiers attendus de l'équipe de départ, pas encore appelée par le script (LEV_GetNumFiremen).</summary>
    public int DeferredFiremen => _initialDeferred && !_initialCalled ? _deferredCrew : 0;

    public void CallInitialDispatch()
    {
        if (_initialCalled) return;
        _initialCalled = true;
        if (_initialDeferred && _initial is { } ini) Dispatch(ini.cfg, ini.spawn);
    }

    public void GrantReinforcement()
    {
        if (_toGrant.Count == 0) return;
        var r = _toGrant[0]; _toGrant.RemoveAt(0);
        _pending.Add((_s.Time + r.eta, r.cfg, r.spawn));
        Log.Add($"{_s.Time,6:0} s : renfort accordé ({r.cfg}, dans {r.eta:0} s)");
    }

    /// <summary>ENT_Destroy sur une unité : retirée de la partie (et ses passagers descendent).</summary>
    public void RemoveUnit(Unit u)
    {
        if (u.Passengers.Count > 0) UnloadPassengers(u);
        u.InVehicle?.Passengers.Remove(u);
        Units.Remove(u); _byId.Remove(u.Id);
        if (u.ScriptName != null) _scriptNames.Remove(u.ScriptName);
    }

    public Civilian? CivilianById(long id) => Civilians.FirstOrDefault(c => c.Id == id);

    /// <summary>IA_CmdMoveTo sur un civil (cinématique : des personnages traversent la scène) ; run : au pas de course.</summary>
    public void ScriptMoveCivilian(Civilian c, Vector2 dest, bool run)
    {
        c.Leader = null; c.Path.Clear();
        c.RunSpeed = run ? 450f : 280f;
        var from = new Vector2(c.Position.X, c.Position.Y);
        var path = FindPath(from, dest, out _);
        if (path.Count == 0) path.Add(dest);
        foreach (var p in path) c.Path.Enqueue(p);
    }

    public static float ReserveOf(Unit u) => u.Nozzle?.Reserve ?? 0;
    public static void SetReserve(Unit u, float v) { if (u.Nozzle is { } n) n.Reserve = Math.Clamp(v, 0, n.ReserveMax); }

    // ---- types d'unités (ENT_GetUnitType) : rang de [UNIT] Type dans l'énumération de la classe ----
    private readonly Dictionary<string, int> _unitTypes = new(StringComparer.OrdinalIgnoreCase);
    public int UnitType(Unit u)
    {
        if (_unitTypes.TryGetValue(u.ClassPath, out var t)) return t;
        t = 0;
        var c = Class("Design\\Unit\\" + u.ClassPath + ".class");
        var type = c?.Get("UNIT", "Type")?.Trim('"', ' ');
        var en = c?.Get("UNIT", "Type#TYPE");
        if (type != null && en != null)
        {
            var parts = en.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();   // « ENUM UNKNOWN FIREMAN CANON… »
            int k = parts.FindIndex(p => p.Equals(type, StringComparison.OrdinalIgnoreCase));
            if (k >= 0) t = k;
        }
        return _unitTypes[u.ClassPath] = t;
    }
}
