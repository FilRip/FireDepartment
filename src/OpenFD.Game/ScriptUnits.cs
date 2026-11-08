using System.Numerics;
using OpenFD.Formats;

namespace OpenFD.Game;

// unités créées et commandées par les scripts de mission (le didacticiel surtout) :
// ENT_CreateUnitPath, ENT_SetPosition, ENT_SetOrientation, IA_CmdMoveTo, IA_CmdEmbark, IA_CmdAttackFire, ENT_SetReserve…
public sealed partial class UnitManager
{
    private readonly Dictionary<string, Unit> _scriptNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Unit> _byId = new();

    public Unit? ById(long id)
    {
        if (id < int.MinValue || id > int.MaxValue)
            return null;
        if (_byId.TryGetValue((int)id, out Unit? u))
            return u;
        u = Units.FirstOrDefault(x => x.Id == id);
        if (u != null)
            _byId[u.Id] = u;
        return u;
    }

    public Unit? ByScriptName(string name) => _scriptNames.TryGetValue(name, out Unit? u) && Units.Contains(u) ? u : null;

    /// <summary>
    /// ENT_CreateUnitPath("Data/Design/Unit/Fireman/fireman_sp.class", nom, x, y, z) : nouvelle unité posée là, à l'arrêt.
    /// </summary>
    public Unit? SpawnScripted(string classPath, string name, Vector3 pos)
    {
        string rel = classPath.Replace('/', '\\');
        int i = rel.IndexOf("Unit\\", StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return null;
        rel = rel[(i + 5)..];
        if (rel.EndsWith(".class", StringComparison.OrdinalIgnoreCase))
            rel = rel[..^6];
        bool vehicle = rel.StartsWith("Firevehicle", StringComparison.OrdinalIgnoreCase);
        Unit? u = Create(rel, vehicle, new Vector2(pos.X, pos.Y));
        if (u == null)
        {
            Log.Add($"Unité du script introuvable : {classPath}");
            return null;
        }
        u.ScriptName = name;
        u.Status = "attend les ordres";
        _scriptNames[name] = u;
        _byId[u.Id] = u;
        return u;
    }

    public void Teleport(Unit u, Vector2 p)
    {
        u.Path.Clear();
        u.Position = new Vector3(p, Ground(p));
        foreach (Unit pas in u.Passengers)
            pas.Position = u.Position;
    }

    /// <summary>IA_CmdMoveTo : l'unité va à ce point (le véhicule y roule ; sans chemin trouvé, en ligne droite).</summary>
    public void ScriptMove(Unit u, Vector2 dest)
    {
        if (u.OnBoard)
            return;
        u.Target = null;
        u.Opening = null;
        u.GoTo = null;
        u.GoToVehicle = null;
        u.Boarding = false;
        if (!Order(u, dest, null, drive: u.IsVehicle, refuseUnreachable: false))
        {
            u.Path.Clear();
            u.Path.Enqueue(dest);
        }
        u.Status = u.IsVehicle ? "roule" : "se déplace";
    }

    /// <summary>IA_CmdEmbark : le pompier va monter dans le véhicule.</summary>
    public void ScriptEmbark(Unit u, Unit vehicle)
    {
        if (u.IsVehicle || !vehicle.IsVehicle)
            return;
        if (vehicle.Seats <= 0)
            vehicle.Seats = 4;
        if (Vector2.Distance(new(u.Position.X, u.Position.Y), new(vehicle.Position.X, vehicle.Position.Y)) < VehicleReach)
        {
            Board(u, vehicle);
            return;
        }
        BoardVehicle(u, vehicle);
        if (!u.Boarding)
            Board(u, vehicle);   // pas de chemin : il monte tout de suite
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
            _deferredCrew = Units.Count(u => !u.IsVehicle && !u.FromLevel) + Units.Sum(v => v.Crew.Count);   // équipe de départ seulement (les renforts sont comptés à part)
            Units.RemoveAll(u => !u.FromLevel);   // (les véhicules posés dans le niveau restent)
            _byId.Clear();
            _initialDeferred = true;
        }
        if (reinforcementsByScript)
        {
            _toGrant.AddRange(_pending.OrderBy(p => p.eta));
            _pending.Clear();
            _allReinforcements = _toGrant.ToList();
        }
    }

    /// <summary>Pompiers attendus de l'équipe de départ, pas encore appelée par le script (LEV_GetNumFiremen).</summary>
    public int DeferredFiremen => _initialDeferred && !_initialCalled ? _deferredCrew : 0;

    public void CallInitialDispatch()
    {
        if (_initialCalled)
            return;
        _initialCalled = true;
        if (_initialDeferred && _initial is { } ini)
            Dispatch(ini.cfg, ini.spawn);
    }

    /// <summary>
    /// LEV_GrantReinforcement : un renfort est accordé (voix « renforts accordés »). Le joueur choisit toujours la caserne
    /// qui vient en cliquant sur son cadre dans le panneau des unités (Institute : E10 ou W7), même s'il n'en reste qu'une
    /// (Tutorial : l'étape 19 attend que le joueur appelle lui-même le renfort).
    /// </summary>
    public void GrantReinforcement()
    {
        if (_toGrant.Count - GrantsAvailable <= 0)
            return;
        Sounds.Add(("VOCAL_REINFORCEMENT_GRANTED", null));
        GrantsAvailable++;
        Log.Add($"{_s.Time,6:0} s : renfort accordé, à choisir ({string.Join(" ou ", _toGrant.Select(t => TeamCode(t.cfg)))})");
    }

    /// <summary>
    /// Délai entre le choix d'un renfort et son arrivée sur la carte quand la mission n'en donne pas : celui de la mission
    /// (ETA de la caserne, 25 ou 30 s à l'Institute) correspond au jeu d'origine (une trentaine de secondes).
    /// </summary>
    public const float ReinforcementCallTime = 30f;

    /// <summary>Renforts accordés que le joueur n'a pas encore choisis.</summary>
    public int GrantsAvailable { get; private set; }

    /// <summary>Casernes que le joueur peut choisir maintenant (vide s'il n'a pas de renfort accordé).</summary>
    public IEnumerable<string> ChoosableTeams => GrantsAvailable > 0 ? _toGrant.Select(t => TeamCode(t.cfg)) : Enumerable.Empty<string>();

    // renforts choisis, en route : équipe → (moment du choix, durée)
    private readonly Dictionary<string, (float start, float duration)> _incoming = new();

    /// <summary>Renfort choisi pas encore arrivé : avancement de 0 à 1 (barre sous son nom) ; null sinon.</summary>
    public float? IncomingProgress(string team) =>
        _incoming.TryGetValue(team, out (float start, float duration) t) ? Math.Clamp((_s.Time - t.start) / MathF.Max(0.1f, t.duration), 0, 1) : null;

    /// <summary>Le joueur choisit la caserne qui vient (voix « renfort en approche ») ; elle arrive au bout de son délai (ETA).</summary>
    public bool ChooseReinforcement(string team)
    {
        int i = _toGrant.FindIndex(t => TeamCode(t.cfg) == team);
        if (i < 0)
            return false;
        (float eta, string cfg, int spawn) r = _toGrant[i];
        _toGrant.RemoveAt(i);
        if (GrantsAvailable > 0)
            GrantsAvailable--;
        _granted.Add(r);
        float delay = r.eta > 0 ? r.eta : ReinforcementCallTime;
        _pending.Add((_s.Time + delay, r.cfg, r.spawn));
        _incoming[team] = (_s.Time, delay);
        Sounds.Add(("VOCAL_REINFORCEMENT_CALLED", null));
        Log.Add($"{_s.Time,6:0} s : renfort {team} en approche (dans {delay:0} s)");
        return true;
    }

    /// <summary>Composition d'une caserne de la mission (aperçu au survol de son cadre) : classe, icône, véhicule ou non.</summary>
    public List<(string cls, string? icon, bool vehicle, bool ambulance)> TeamComposition(string team)
    {
        List<(string cls, string? icon, bool vehicle, bool ambulance)> res = new();
        string? cfgName = _toGrant.Concat(_granted).Select(t => t.cfg).FirstOrDefault(c => TeamCode(c) == team);
        if (cfgName == null || Class("Design\\Firestation\\" + CfgFile(cfgName) + ".cfg") is not { } cfg)
            return res;
        for (int i = 1; i <= 20; i++)
        {
            string? cls = cfg.Get("FIRESTATION", $"UNIT{i:00}")?.Trim('"', ' ');
            if (string.IsNullOrEmpty(cls))
                continue;
            bool vehicle = cls.StartsWith("Firevehicle", StringComparison.OrdinalIgnoreCase);
            string? icon = Class("Design\\Unit\\" + cls + ".class")?.Get("UNIT", "Icon")?.Trim('"', ' ');
            res.Add((cls, icon, vehicle, vehicle && cls.Contains("ambulance", StringComparison.OrdinalIgnoreCase)));
        }
        return res;
    }

    // renforts dans l'ordre de la mission (1, 2, 3…), pour LEV_CallReinforcement(n)
    private List<(float eta, string cfg, int spawn)>? _allReinforcements;
    private readonly List<(float eta, string cfg, int spawn)> _granted = new();

    /// <summary>LEV_CallReinforcement(n) : le renfort n de la mission est envoyé (une seule fois).</summary>
    public void CallReinforcement(int n)
    {
        _allReinforcements ??= _toGrant.ToList();
        if (n < 1 || n > _allReinforcements.Count)
        {
            GrantReinforcement();
            return;
        }
        (float eta, string cfg, int spawn) r = _allReinforcements[n - 1];
        int i = _toGrant.IndexOf(r);
        if (i < 0)
            return;   // déjà envoyé
        _toGrant.RemoveAt(i);
        _granted.Add(r);
        _pending.Add((_s.Time + r.eta, r.cfg, r.spawn));
        Log.Add($"{_s.Time,6:0} s : renfort {n} appelé ({r.cfg}, dans {r.eta:0} s)");
    }

    /// <summary>ENT_Destroy sur une unité : retirée de la partie (et ses passagers descendent).</summary>
    public void RemoveUnit(Unit u)
    {
        if (u.Passengers.Count > 0)
            UnloadPassengers(u);
        u.InVehicle?.Passengers.Remove(u);
        Units.Remove(u);
        _byId.Remove(u.Id);
        if (u.ScriptName != null)
            _scriptNames.Remove(u.ScriptName);
    }

    public Civilian? CivilianById(long id) => Civilians.FirstOrDefault(c => c.Id == id);

    /// <summary>IA_CmdMoveTo sur un civil (cinématique : des personnages traversent la scène) ; run : au pas de course.</summary>
    public void ScriptMoveCivilian(Civilian c, Vector2 dest, bool run)
    {
        c.Leader = null;
        c.Path.Clear();
        c.RunSpeed = run ? 450f : 280f;
        c.ScriptWalk = true;
        Vector2 from = new Vector2(c.Position.X, c.Position.Y);
        List<Vector2> path = FindPath(from, dest, out _);
        if (path.Count == 0)
            path.Add(dest);
        foreach (Vector2 p in path)
            c.Path.Enqueue(p);
    }

    public static float ReserveOf(Unit u) => u.Nozzle?.Reserve ?? 0;
    public static void SetReserve(Unit u, float v)
    {
        if (u.Nozzle is { } n)
            n.Reserve = Math.Clamp(v, 0, n.ReserveMax);
    }

    // ---- types d'unités (ENT_GetUnitType) : rang de [UNIT] Type dans l'énumération de la classe ----
    private readonly Dictionary<string, int> _unitTypes = new(StringComparer.OrdinalIgnoreCase);
    public int UnitType(Unit u)
    {
        if (_unitTypes.TryGetValue(u.ClassPath, out int t))
            return t;
        t = 0;
        ClassFile? c = Class("Design\\Unit\\" + u.ClassPath + ".class");
        string? type = c?.Get("UNIT", "Type")?.Trim('"', ' ');
        string? en = c?.TypeOf("UNIT", "Type") ?? c?.Get("UNIT", "Type#TYPE");
        if (type != null && en != null)
        {
            List<string> parts = en.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();   // « ENUM UNKNOWN FIREMAN CANON… »
            int k = parts.FindIndex(p => p.Equals(type, StringComparison.OrdinalIgnoreCase));
            if (k >= 0)
                t = k;
        }
        return _unitTypes[u.ClassPath] = t;
    }
}
