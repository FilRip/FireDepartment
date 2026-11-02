using System.Numerics;
using FD3.Fire;
using FD3.Formats;

namespace FD3.Game;

/// <summary>Une unité du joueur : véhicule ou pompier (classes Design\Unit\…).</summary>
public sealed class Unit
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string ClassPath { get; init; }     // « Fireman\fireman_sp »
    public required string Model { get; init; }         // chemin Gfx du modèle 3D
    public required bool IsVehicle { get; init; }
    public Vector3 Position;
    public float Heading;                                 // radians, 0 = vers +X
    public float Speed;                                   // unités par seconde
    public Extinguisher? Nozzle;                          // lance (pompiers, médecin)
    public bool HasHose;                                  // (non utilisé : le jeu n'a pas de tuyaux)
    public bool CanHeal;                                  // médecin : peut réanimer un civil inconscient
    public float ResupplyPower;                           // véhicule : eau rendue par seconde aux pompiers (0 = aucune)
    public bool IsAmbulance;
    public Civilian? GoTo;                                // civil vers lequel l'unité se dirige
    public Civilian? Carrying;                            // civil inconscient porté sur le dos
    public CarryPhase Carry;
    public float CarryTimer;
    public Unit? GoToVehicle;                             // véhicule vers lequel l'unité se dirige (remplir, déposer des civils)
    public float HealTimer;
    public bool CanBreak, CanUse;                         // hache (portes, fenêtres) ; ouvrir une porte
    public bool CanRepair;                                // technicien : répare les objets CouldBeRepair
    public UsableObject? UseTarget;                       // objet sur lequel agir (armoire électrique…)
    public float BreakPower = 1;
    public Opening? Opening;                              // porte ou fenêtre à casser / ouvrir
    public bool OpeningBreak;
    public float ActionTimer;
    public int Blows;                                     // coups de hache donnés (affichage)
    public readonly Queue<Vector2> Path = new();
    public HotPoint? Target;                              // feu à éteindre
    public string Status = "";
    public bool Moving => Path.Count > 0;
    /// <summary>Blessé (backdraft, explosion) : au sol, ne peut rien faire pendant ce temps (s).</summary>
    public float InjuredTimer;
    public int Wounds;
    /// <summary>Inconscient (backdraft) : reste au sol jusqu'à ce qu'un autre pompier le ranime.</summary>
    public bool Unconscious;
    public Unit? ReviveTarget;                            // collègue inconscient que l'unité va ranimer
    /// <summary>Véhicule : places ([CAPACITIES_PROPS] EmbarkCapacity) et passagers à bord.</summary>
    public int Seats;
    public readonly List<Unit> Passengers = new();
    public Unit? InVehicle;                               // pompier à bord de ce véhicule (invisible, suit le véhicule)
    public bool Boarding;                                 // en route pour monter dans GoToVehicle
    public bool AutoResupply;                             // [CAPACITIES_PROPS] AutomaticResupply : se recharge près d'un camion
    public bool OnBoard => InVehicle != null;
    /// <summary>Véhicule : équipage à bord (classes), qui descend quand le véhicule est arrivé.</summary>
    public readonly List<string> Crew = new();
    public string? TeamName;                              // nom de l'équipe (journal à l'arrivée)
    /// <summary>Endurance (pompiers : [CAPACITIES_PROPS] StaminaPoints/Max/Regen) : la chaleur des feux la fait baisser ;
    /// à 0, le pompier s'évanouit. Vie (véhicules : [DAMAGE] LifePoints, 500) : à 0, le véhicule est hors service.</summary>
    public float Stamina = 100, StaminaMax = 100, StaminaRegen = 0.5f, HeatCoef = 1, SmokeCoef = 0.7f;
    public float Life = 500, LifeMax = 500, FireDamageCoef = 3;
    public float ScriptHeatCoef = 1;                      // ENT_SetFireDamageCoef sur un pompier (0 : insensible au feu)
    public bool Destroyed;                                // véhicule détruit : inutilisable jusqu'à la fin de la partie
    public bool HealsUnits;                               // ambulance : soigne les pompiers venus à elle
    public string? Icon;                                  // [UNIT] Icon : image de l'unité (Data\Overlay\Textures)
    public float Reversing, ManeuverLeft;                 // véhicule : marche arrière restante ; longueur de la manœuvre en cours
    public int ManeuverDir;                               // -1 recule, 1 avance
    /// <summary>Unité créée par le script de la mission (ENT_CreateUnitPath) : son nom dans le script.</summary>
    public string? ScriptName;
    /// <summary>Cachée par le script (ENT_SetVisibility 0) : ni dessinée, ni choisie, ni comptée.</summary>
    public bool Hidden;
}

public enum CivilianState { Conscious, Unconscious, Rescued, Dead }

/// <summary>Pompier qui porte un civil inconscient : il le soulève, le porte sur son dos, puis le dépose à l'ambulance.</summary>
public enum CarryPhase { None, Lifting, Walking, Dropping }

/// <summary>Civil à sauver (entité Civilian de la carte ; état de départ lu dans le composant A02A).</summary>
public sealed class Civilian
{
    public required uint Id { get; init; }
    public required string Name { get; init; }
    public required string Model { get; init; }
    public string ClassPath = "";                         // Design\Unit\Civilian\….class (animations)
    public Vector3 Position;
    public float Heading;
    public CivilianState State;
    public Unit? Leader;                                  // pompier ou médecin qu'il suit
    public Unit? Carrier;                                 // pompier qui le porte (inconscient)
    public bool Hidden;                                   // caché au départ par le script de mission
    public readonly Queue<Vector2> Path = new();
    /// <summary>Chemin vers son guide (calculé sur la grille), but visé et délai avant de le recalculer.</summary>
    public readonly Queue<Vector2> FollowPath = new();
    public Vector2 FollowGoal; public float Replan;
    public float RunSpeed = 280f;                         // vitesse d'un trajet commandé par le script
}

/// <summary>
/// Unités du joueur : arrivée des secours ([FIRESTATION] de la mission), déplacements sur la grille,
/// attaque des feux. Règles de déplacement simplifiées en attendant l'étude du code d'origine.
/// </summary>
public sealed partial class UnitManager
{
    public List<Unit> Units { get; } = new();
    public List<Civilian> Civilians { get; } = new();
    public int Rescued => Civilians.Count(c => c.State == CivilianState.Rescued);
    public const float HealSeconds = 5f, ReachDistance = 160f, VehicleReach = 450f;
    public List<string> Log { get; } = new();
    private readonly Session _s;
    private readonly GameFiles _files;
    private readonly Dictionary<string, ClassFile> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(float eta, string cfg, int spawn)> _pending = new();
    private int _nextId = 0x40000000;   // loin des numéros des objets de la carte (5 000 000…), que les scripts manipulent aussi
    private readonly HashSet<HotPoint> _reported = new();
    private readonly Dictionary<string, Vector2> _waypoints = new(StringComparer.OrdinalIgnoreCase);

    // vitesses : les classes donnent des km/h pour les piétons et des m/s pour les véhicules ; 1 unité = 1 cm
    // pompiers : RunSpeed 10 → 450 unités/s, accordé à l'animation de course (foulée mesurée : 500 à 550 unités/s)
    public const float FiremanSpeedFactor = 45f, VehicleSpeedFactor = 60f;
    /// <summary>Recul supplémentaire devant le feu (1 m = 100 unités), réglé à l'œil sur le jeu d'origine.</summary>
    public const float ExtraStandoff = 400f;
    /// <summary>Vrai pendant un ordre donné par le joueur (l'interface le met) : les réponses vocales ne concernent que ceux-là.</summary>
    public bool PlayerCommand;
    /// <summary>Portée à laquelle un pompier libre repère un feu et va l'éteindre, en multiple de celle de sa lance (options : Gameplay).</summary>
    public static float AutoAttackRange = 3f;
    /// <summary>Feu tout proche : le pompier recule jusqu'à cette part de sa distance d'arrosage habituelle (options : Gameplay).</summary>
    public static float AttackBackOff = 0.5f;

    public UnitManager(Session s, GameFiles files, ClassFile mission)
    {
        _s = s; _files = files;
        foreach (var o in s.Level.Objects.Where(o => o.Kind == EntityKind.Waypoint && o.HasTransform))
            _waypoints[o.Name] = new(o.Position.X, o.Position.Y);
        LoadCivilians(s.Level);
        if (Environment.GetEnvironmentVariable("FD3_NOBLOCK") == null) BuildObstacles(s.Level);
        LoadOpenings(s.Level);
        BlockThinWalls();
        LoadSupplies(s.Level);
        LoadTriggers(s.Level);
        LoadUsables(s.Level);
        _saveCivilianScript = mission.Get("COMMAND_CALLBACKS", "CmdSaveCivilian")?.Trim('"', ' ');
        const string F = "FIRESTATION";
        var initial = mission.Get(F, "InitialDispatch")?.Trim('"', ' ');
        // équipe de départ : en route dès le début (les scripts comptent les pompiers à 0 s : LEV_GetNumFiremen),
        // sauf si le script de la mission l'appelle lui-même (LEV_CallInitialDispatch, voir ScriptDispatch)
        if (!string.IsNullOrEmpty(initial)) { _initial = (initial, mission.GetInt(F, "InitialDispatchSpawnPoint", 1)); Dispatch(initial, _initial.Value.spawn); }
        for (int i = 1; i <= 9; i++)
        {
            var r = mission.Get(F, $"Reinforcement{i}")?.Trim('"', ' ');
            if (string.IsNullOrEmpty(r)) continue;
            // (la mission Fireworks écrit « REINFORECEMENT1ETA » : faute de frappe d'origine, valeur par défaut 60 s)
            _pending.Add((mission.GetFloat(F, $"Reinforcement{i}ETA", 60), r, mission.GetInt(F, $"Reinforcement{i}SpawnPoint", 1)));
        }
    }

    private void LoadCivilians(Level level)
    {
        foreach (var o in level.Objects.Where(o => o.Kind == EntityKind.Civilian && o.HasTransform))
        {
            var c = Class("Design\\" + o.ClassPath["design\\".Length..] + ".class");
            var model = c?.Get("DISPLAY", "Model")?.Trim('"', ' ');
            if (string.IsNullOrEmpty(model)) continue;
            // état de départ : octet 39 du composant A02A (1 = choqué/conscient, 3 = évanoui, 4 = mort)
            int state = o.GetComponent(0xA02A) is { } comp && comp.Size > 39 ? o.ComponentBytes(comp)[39] : 1;
            Civilians.Add(new Civilian
            {
                Id = o.Id, Name = o.Name, Model = SceneAssets.GfxPath("Unit\\Civilian", model), Position = o.Position,
                ClassPath = "Design\\" + o.ClassPath["design\\".Length..] + ".class",
                Heading = o.Rotation.Z,
                State = state == 3 ? CivilianState.Unconscious : state == 4 ? CivilianState.Dead : CivilianState.Conscious,
                Hidden = o.Name.Contains("hide", StringComparison.OrdinalIgnoreCase),
            });
        }
    }

    private ClassFile? Class(string rel)
    {
        try { return ClassFile.Load(_files, rel, _cache); } catch { return null; }
    }

    public void Update(float time, float dt)
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
            if (time >= _pending[i].eta) { Dispatch(_pending[i].cfg, _pending[i].spawn); _pending.RemoveAt(i); }
        foreach (var u in Units.Where(u => u.Unconscious || u.OnBoard))
        {
            u.Path.Clear(); u.Target = null; u.Opening = null; u.GoTo = null; u.GoToVehicle = null; u.ReviveTarget = null; u.UseTarget = null;
            if (u.Unconscious) u.Status = "inconscient : un collègue doit le ranimer";
            if (u.InVehicle is { } veh) { u.Position = veh.Position; u.Status = $"à bord ({UnitName(veh)})"; }
        }
        foreach (var u in Units.Where(u => u.InjuredTimer > 0))
        {
            u.InjuredTimer = MathF.Max(0, u.InjuredTimer - dt);
            u.Path.Clear(); u.Target = null; u.Opening = null; u.GoTo = null; u.GoToVehicle = null;
            u.Status = u.InjuredTimer > 0 ? "blessé, au sol" : "attend les ordres";
        }
        UpdateHealth(dt);
        foreach (var u in Units) Move(u, dt);
        UpdateVehicleObstacles();
        foreach (var v in Units.Where(u => u.IsVehicle && u.Crew.Count > 0 && !u.Moving).ToList()) Unload(v);
        _autoTick++;
        foreach (var u in Units) AutoAttack(u);
        foreach (var u in Units) Fight(u, dt);
        foreach (var u in Units) Rescue(u, dt);
        foreach (var u in Units) CarryVictim(u, dt);   // voir CivilianAI.cs
        foreach (var u in Units) Revive(u, dt);
        foreach (var u in Units) Resupply(u, dt);
        foreach (var v in Units.Where(v => v.IsVehicle)) VehicleResupply(v, dt);
        CheckTriggers();
        foreach (var u in Units) WorkOpening(u, dt);
        foreach (var u in Units) WorkUsable(u, dt);
        foreach (var c in Civilians) Follow(c, dt);
    }

    /// <summary>Arrivée d'une équipe : les véhicules entrent par locstartN_k et rejoignent locendN_k, l'équipage descend.</summary>
    /// <summary>Nombre de pompiers (hors véhicules) d'une équipe de la caserne.</summary>
    private int CrewSize(string cfgName)
    {
        var cfg = Class("Design\\Firestation\\" + cfgName + ".cfg");
        if (cfg == null) return 0;
        return Enumerable.Range(1, 20).Select(i => cfg.Get("FIRESTATION", $"UNIT{i:00}")?.Trim('"', ' '))
            .Count(x => !string.IsNullOrEmpty(x) && !x.StartsWith("Firevehicle", StringComparison.OrdinalIgnoreCase));
    }

    private void Dispatch(string cfgName, int spawn)
    {
        var cfg = Class("Design\\Firestation\\" + cfgName + ".cfg");
        if (cfg == null) { Log.Add($"Équipe introuvable : {cfgName}"); return; }
        var list = Enumerable.Range(1, 20).Select(i => cfg.Get("FIRESTATION", $"UNIT{i:00}")?.Trim('"', ' ')).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList();
        var vehicles = list.Where(x => x.StartsWith("Firevehicle", StringComparison.OrdinalIgnoreCase)).ToList();
        var crew = list.Where(x => !x.StartsWith("Firevehicle", StringComparison.OrdinalIgnoreCase)).ToList();
        Unit? lead = null; int k = 1;
        foreach (var v in vehicles)
        {
            var start = Wp($"locstart{spawn}_{k}") ?? Wp($"locstart{spawn}_1") ?? Center();
            var end = Wp($"locend{spawn}_{k}") ?? Wp($"locend{spawn}_1") ?? start;
            var u = Create(v, true, start);
            if (u == null) continue;
            u.Heading = MathF.Atan2(end.Y - start.Y, end.X - start.X);
            Order(u, end, refuseUnreachable: false);
            lead ??= u; k++;
        }
        var team = cfg.Get("FIRESTATION", "CodeName")?.Trim('&') ?? cfgName;
        var trucks = Units.Where(u => u.IsVehicle && u.Id >= (lead?.Id ?? int.MaxValue)).ToList();
        if (trucks.Count > 0)
        {
            // l'équipage monte dans les véhicules (répartis à tour de rôle, les camions d'abord) et descend à l'arrivée
            var order = trucks.OrderBy(t => t.IsAmbulance ? 1 : 0).ToList();
            for (int i = 0; i < crew.Count; i++) order[i % order.Count].Crew.Add(crew[i]);
            foreach (var t in trucks) t.TeamName = team;
            Log.Add($"{_s.Time,6:0} s : l'équipe {team} est en route ({list.Count} unités)");
            return;
        }
        var basePos = Wp($"locend{spawn}_1") ?? Center();
        int n = 0;
        foreach (var c in crew)
        {
            float a = n * 0.9f; var pos = basePos + new Vector2(MathF.Cos(a), MathF.Sin(a)) * (250 + 60 * n);
            var u = Create(c, false, Walkable(pos)); n++;
            if (u != null) u.Status = "attend les ordres";
        }
        if (_s.Time > 1) Sounds.Add(("VOCAL_REINFORCEMENT_ARRIVED", null));
        Log.Add($"{_s.Time,6:0} s : arrivée de l'équipe {team} ({list.Count} unités)");
    }

    /// <summary>
    /// Véhicule arrivé : l'équipage descend autour de lui, sur des cases libres (ni décor, ni véhicule, ni porte fermée),
    /// de préférence sur les côtés et à l'arrière, sans se superposer.
    /// </summary>
    private void Unload(Unit v)
    {
        if (v.Crew.Count == 0 || v.Moving || _s.Grid == null) return;
        var spots = DropSpots(v, v.Crew.Count);
        var me = new Vector2(v.Position.X, v.Position.Y);
        int n = 0;
        foreach (var cls in v.Crew)
        {
            var spot = n < spots.Count ? spots[n] : Walkable(me);
            var u = Create(cls, false, spot);
            if (u == null) continue;
            u.Heading = MathF.Atan2(spot.Y - me.Y, spot.X - me.X);
            u.Status = "attend les ordres";
            n++;
        }
        v.Crew.Clear();
        Sounds.Add(("VOCAL_REINFORCEMENT_ARRIVED", null));
        Log.Add($"{_s.Time,6:0} s : arrivée de l'équipe {v.TeamName} ({n} pompier(s) descendent du véhicule)");
    }

    /// <summary>
    /// Places où descendre d'un véhicule : cases libres autour de lui (ni décor, ni véhicule, ni porte fermée), sans mur
    /// entre le véhicule et la case, de préférence sur les côtés et à l'arrière, sans se superposer aux autres pompiers.
    /// </summary>
    private List<Vector2> DropSpots(Unit v, int count)
    {
        var res = new List<Vector2>();
        if (_s.Grid is not { } g) return res;
        UpdateVehicleObstacles();
        var me = new Vector2(v.Position.X, v.Position.Y);
        var fwd = new Vector2(MathF.Cos(v.Heading), MathF.Sin(v.Heading));
        var taken = Units.Where(u => !u.IsVehicle && !u.OnBoard).Select(u => new Vector2(u.Position.X, u.Position.Y)).ToList();
        int saved = _selfId; _selfId = -1;
        var spots = new List<(Vector2 p, float score)>();
        for (float r = 250; r <= 1400 && spots.Count < 40 + count; r += 100)
            for (int k = 0; k < 24; k++)
            {
                float a = k * MathF.PI / 12; var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
                var p = me + dir * r;
                var (cx, cy) = g.CellOf(new Vector3(p, 0));
                if (!FreeCell(cx, cy)) continue;
                _pathingVehicle = true; bool open = LineFree(me, p, ignoreFire: true); _pathingVehicle = false;
                if (!open) continue;
                spots.Add((new Vector2((cx + 0.5f) * Grid.CellSize, (cy + 0.5f) * Grid.CellSize), r + 150 * MathF.Max(0, Vector2.Dot(dir, fwd))));
            }
        _selfId = saved;
        for (int i = 0; i < count; i++)
        {
            var spot = spots.OrderBy(x => x.score).Select(x => x.p).FirstOrDefault(p => taken.All(t => Vector2.Distance(t, p) > 110));
            if (spot == default) spot = Walkable(me + new Vector2(-fwd.Y, fwd.X) * 350);
            res.Add(spot); taken.Add(spot);
        }
        return res;
    }

    private Vector2? Wp(string name) => _waypoints.TryGetValue(name, out var p) ? p : null;
    private Vector2 Center() => _s.Grid is { } g ? new(g.Width * Grid.CellSize / 2, g.Height * Grid.CellSize / 2) : Vector2.Zero;

    /// <summary>
    /// Nationalité des pompiers et véhicules choisie dans les options : « us », « fr », « gb », « gr » (Allemagne), « ua »,
    /// ou vide (celle de la mission). Les classes du jeu existent en variantes (fireman_sp_fr, Veh_P_pumper_gb…).
    /// </summary>
    public static string Nationality = "";
    private static readonly System.Text.RegularExpressions.Regex NatRx = new("_(us|fr|gb|gr|ua)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private string WithNationality(string classRel)
    {
        if (string.IsNullOrEmpty(Nationality)) return classRel;
        var baseRel = NatRx.Replace(classRel, "");
        var cand = baseRel + "_" + Nationality;
        return Class("Design\\Unit\\" + cand + ".class") != null ? cand : classRel;
    }

    private Unit? Create(string classRel, bool vehicle, Vector2 pos)
    {
        classRel = WithNationality(classRel);
        var c = Class("Design\\Unit\\" + classRel + ".class");
        var model = c?.Get("DISPLAY", "Model")?.Trim('"', ' ');
        if (c == null || string.IsNullOrEmpty(model)) return null;
        var gfx = SceneAssets.GfxPath(vehicle ? "Unit\\FireVehicle" : "Unit\\Fireman", model);
        var u = new Unit
        {
            Id = _nextId++, Name = classRel[(classRel.LastIndexOf('\\') + 1)..], ClassPath = classRel, Model = gfx, IsVehicle = vehicle,
            Position = new Vector3(pos, Ground(pos)),
            Speed = vehicle ? c.GetFloat("MOVING", "ForwardSpeed", 15) * VehicleSpeedFactor : c.GetFloat("MOVING", "RunSpeed", 10) * FiremanSpeedFactor,
        };
        if (!vehicle && c.GetFloat("CAPACITIES_FIRE_ATTACK", "FireAttackPower", 0) > 0) u.Nozzle = Extinguisher.FromClass(classRel, c);
        // véhicules à lance (autopompe…) : [CAPACITIES_ACTIONS] CanAttackFire, portée [CAPACITIES_FIRE_ATTACK] (3000 contre 600 à pied)
        if (vehicle && c.GetBool("CAPACITIES_ACTIONS", "CanAttackFire") && c.GetFloat("CAPACITIES_FIRE_ATTACK", "FireAttackPower", 0) > 0
            && c.GetFloat("CAPACITIES_RESERVE", "ReserveMax", 0) > 0)
            u.Nozzle = Extinguisher.FromClass(classRel, c);
        u.Seats = vehicle ? c.GetInt("CAPACITIES_PROPS", "EmbarkCapacity", 0) : 0;
        u.AutoResupply = !vehicle && c.GetBool("CAPACITIES_PROPS", "AutomaticResupply");
        u.CanHeal = !vehicle && c.GetBool("CAPACITIES_ACTIONS", "CanHeal");
        u.CanBreak = !vehicle && c.GetBool("CAPACITIES_ACTIONS", "CanBreak");
        u.CanUse = !vehicle && c.GetBool("CAPACITIES_ACTIONS", "CanUse");
        u.CanRepair = !vehicle && c.GetBool("CAPACITIES_ACTIONS", "CanRepair");
        u.BreakPower = c.GetFloat("CAPACITIES_PROPS", "BreakPower", 1);
        u.IsAmbulance = vehicle && classRel.Contains("ambulance", StringComparison.OrdinalIgnoreCase);
        if (vehicle) u.ResupplyPower = c.GetFloat("CAPACITIES_RESERVE", "ResupplyPower", 0);
        u.StaminaMax = c.GetFloat("CAPACITIES_PROPS", "StaminaMax", 100); u.Stamina = MathF.Min(u.StaminaMax, c.GetFloat("CAPACITIES_PROPS", "StaminaPoints", u.StaminaMax));
        u.StaminaRegen = c.GetFloat("CAPACITIES_PROPS", "StaminaRegen", 0.5f);
        u.HeatCoef = c.GetFloat("CAPACITIES_PROPS", "HeatStaminaCoef", 1); u.SmokeCoef = c.GetFloat("CAPACITIES_PROPS", "SmokeStaminaCoef", 0.7f);
        u.LifeMax = c.GetFloat("DAMAGE", "MaxLifePoints", 500); u.Life = MathF.Min(u.LifeMax, c.GetFloat("DAMAGE", "LifePoints", u.LifeMax));
        u.FireDamageCoef = c.GetFloat("DAMAGE", "FireDamageCoef", vehicle ? 3 : 5);
        u.HealsUnits = vehicle && c.GetBool("CAPACITIES_ACTIONS", "CanHeal");
        u.Icon = c.Get("UNIT", "Icon")?.Trim('"', ' ');
        Units.Add(u);
        return u;
    }

    private float Ground(Vector2 p)
    {
        if (_s.Grid is not { } g) return 0;
        var (cx, cy) = g.CellOf(new Vector3(p, 0));
        if (!g.Inside(cx, cy)) return 0;
        int k = g.Index(cx, cy);
        return _surface != null && !float.IsNaN(_surface[k]) ? MathF.Max(_surface[k], g.GroundHeight[k]) : g.GroundHeight[k];
    }

    // hauteur du sol dessiné, case par case (relevée sur les maillages du décor) ; NaN : celle de la grille
    private float[]? _surface;
    public void SetSurface(float[] surface)
    {
        _surface = surface;
        foreach (var u in Units) u.Position.Z = Ground(new Vector2(u.Position.X, u.Position.Y));
        foreach (var c in Civilians) if (_s.Grid != null) { var z = Ground(new Vector2(c.Position.X, c.Position.Y)); if (z > c.Position.Z) c.Position.Z = z; }
    }
    public float GroundZ(Vector2 p) => Ground(p);

    // case libre ; pour un véhicule conduit par le joueur, il faut aussi la place autour (gabarit de 3 × 3 cases)
    private bool Free(int cx, int cy)
    {
        if (_clearance <= 0) return FreeCell(cx, cy);
        for (int dx = -_clearance; dx <= _clearance; dx++)
            for (int dy = -_clearance; dy <= _clearance; dy++)
                if (!FreeCell(cx + dx, cy + dy)) return false;
        return true;
    }
    private int _clearance, _selfId;

    private bool FreeCell(int cx, int cy) => _s.Grid is not { } g || (g.Inside(cx, cy) && g.ZoneA[g.Index(cx, cy)] != 0 && (_blocked.Length == 0 || !_blocked[g.Index(cx, cy)])
        && (_pathingVehicle || _vehBlocked.Length == 0 || _vehBlocked[g.Index(cx, cy)] == 0 || _vehBlocked[g.Index(cx, cy)] == _selfId)
        && (_doorClosed.Length == 0 || !_doorClosed[g.Index(cx, cy)]));

    // véhicules garés (camions, ambulance) : obstacles mobiles, recalculés quand un véhicule s'arrête ou repart
    private int[] _vehBlocked = Array.Empty<int>();
    private bool _pathingVehicle;
    private string _vehSignature = "";
    private readonly Dictionary<string, (Vector2 min, Vector2 max)?> _vehBoxes = new(StringComparer.OrdinalIgnoreCase);
    public bool IsVehicleBlocked(int index) => index < _vehBlocked.Length && _vehBlocked[index] != 0;
    private const float VehicleMargin = 40f;

    private (Vector2 min, Vector2 max)? ModelBox(string path)
    {
        if (_vehBoxes.TryGetValue(path, out var box)) return box;
        box = null;
        try
        {
            var assets = _assets ??= new SceneAssets(_files);
            Vector2 mn = new(float.MaxValue), mx = new(float.MinValue);
            foreach (var part in assets.ModelParts(path))
            {
                var m = assets.LoadMesh("", part.Mesh, "gfx").Mesh;
                for (int i = 0; i < m.VertexCount; i++)
                {
                    var v = Vector3.Transform(new Vector3(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]), part.World);
                    mn = Vector2.Min(mn, new(v.X, v.Y)); mx = Vector2.Max(mx, new(v.X, v.Y));
                }
            }
            if (mn.X < mx.X) box = (mn - new Vector2(VehicleMargin), mx + new Vector2(VehicleMargin));
        }
        catch { }
        return _vehBoxes[path] = box;
    }
    private SceneAssets? _assets;

    /// <summary>Le point (monde, à plat) est-il sur le véhicule (son gabarit au sol, plus une marge) ?</summary>
    public bool VehicleContains(Unit v, Vector2 p, float margin = 0)
    {
        if (ModelBox(v.Model) is not { } b) return Vector2.Distance(p, new(v.Position.X, v.Position.Y)) < 250 + margin;
        var world = Matrix4x4.CreateRotationZ(v.Heading + MathF.PI / 2) * Matrix4x4.CreateTranslation(v.Position.X, v.Position.Y, 0);
        Matrix4x4.Invert(world, out var inv);
        var l = Vector3.Transform(new Vector3(p, 0), inv);
        return l.X >= b.min.X - margin && l.X <= b.max.X + margin && l.Y >= b.min.Y - margin && l.Y <= b.max.Y + margin;
    }

    private void UpdateVehicleObstacles()
    {
        if (_s.Grid is not { } g) return;
        var parked = Units.Where(v => v.IsVehicle && !v.Moving).ToList();
        var sig = string.Join(";", parked.Select(v => $"{v.Id}:{(int)v.Position.X / 50}:{(int)v.Position.Y / 50}:{(int)(v.Heading * 20)}"));
        if (sig == _vehSignature) return;
        _vehSignature = sig;
        if (_vehBlocked.Length != g.Width * g.Height) _vehBlocked = new int[g.Width * g.Height]; else Array.Clear(_vehBlocked);
        foreach (var v in parked)
        {
            if (ModelBox(v.Model) is not { } b) continue;
            // même orientation que l'affichage : les modèles regardent vers −Y
            var world = Matrix4x4.CreateRotationZ(v.Heading + MathF.PI / 2) * Matrix4x4.CreateTranslation(v.Position.X, v.Position.Y, 0);
            Matrix4x4.Invert(world, out var inv);
            var corners = new[] { b.min, new Vector2(b.max.X, b.min.Y), new Vector2(b.min.X, b.max.Y), b.max }
                .Select(p => { var w = Vector3.Transform(new Vector3(p, 0), world); return new Vector2(w.X, w.Y); }).ToList();
            var (x0, y0) = g.CellOf(new Vector3(corners.Min(p => p.X), corners.Min(p => p.Y), 0));
            var (x1, y1) = g.CellOf(new Vector3(corners.Max(p => p.X), corners.Max(p => p.Y), 0));
            for (int cx = x0; cx <= x1; cx++)
                for (int cy = y0; cy <= y1; cy++)
                {
                    if (!g.Inside(cx, cy)) continue;
                    var l = Vector3.Transform(new Vector3((cx + 0.5f) * Grid.CellSize, (cy + 0.5f) * Grid.CellSize, 0), inv);
                    if (l.X >= b.min.X && l.X <= b.max.X && l.Y >= b.min.Y && l.Y <= b.max.Y) _vehBlocked[g.Index(cx, cy)] = v.Id;
                }
        }
        // les piétons en chemin recalculent leur trajet s'il traverse un véhicule qui vient de se garer
        foreach (var u in Units.Where(u => !u.IsVehicle && u.Moving))
        {
            var pts = new List<Vector2> { new(u.Position.X, u.Position.Y) }; pts.AddRange(u.Path);
            bool crosses = false;
            for (int i = 0; i + 1 < pts.Count && !crosses; i++) crosses = !LineFree(pts[i], pts[i + 1], ignoreFire: true);
            if (!crosses) continue;
            var dest = pts[^1]; var path = FindPath(pts[0], Walkable(dest));
            u.Path.Clear(); foreach (var p in path) u.Path.Enqueue(p);
        }
    }

    private bool[] _blocked = Array.Empty<bool>();
    public int BlockedCells => _blocked.Count(b => b);
    public bool IsBlocked(int index) => index < _blocked.Length && _blocked[index];
    public Dictionary<int, string> BlockedBy { get; } = new();

    /// <summary>
    /// Obstacles des objets posés (voitures, meubles…) : la grille du jeu ne contient que les murs.
    /// Comme le jeu (section [OCCUPATION]) :
    ///  • mode « Rectangle » : Rect_SizeX × Rect_SizeY cases centrées sur l'objet (0 × 0 = ne bloque pas : herbes…) ;
    ///  • mode « AGO » (d'après la forme 3D) : les cases sous les faces du modèle situées à hauteur d'homme ;
    ///    les feuillages en hauteur, auvents ou enseignes ne gênent pas, les tapis et flaques non plus.
    /// </summary>
    /// <summary>
    /// Cloisons d'un bâtiment surélevé (banque de London, plancher à 4 m) : la grille leur donne la hauteur 0 sans les
    /// fermer. Une case nettement plus basse (1,5 m) que les cases de part et d'autre (à 1 ou 2 cases, dans un même axe)
    /// est un mur : on la bloque, sauf si une porte y passe (la porte la ferme ou l'ouvre elle-même).
    /// </summary>
    private void BlockThinWalls()
    {
        if (_s.Grid is not { } g) return;
        if (_blocked.Length != g.Width * g.Height) _blocked = new bool[g.Width * g.Height];
        var doorCells = new HashSet<int>(Openings.SelectMany(d => d.Cells));
        int n = 0;
        float H(int x, int y) => g.Inside(x, y) && g.ZoneA[g.Index(x, y)] != 0 ? g.GroundHeight[g.Index(x, y)] : float.NaN;
        for (int y = 0; y < g.Height; y++)
            for (int x = 0; x < g.Width; x++)
            {
                int i = g.Index(x, y);
                if (g.ZoneA[i] == 0 || _blocked[i] || doorCells.Contains(i)) continue;
                float h = g.GroundHeight[i];
                bool wall = false;
                foreach (var (dx, dy) in new[] { (1, 0), (0, 1) })
                {
                    for (int a = 1; a <= 2 && !wall; a++)
                        for (int b = 1; b <= 2 && !wall; b++)
                        {
                            float h1 = H(x - dx * a, y - dy * a), h2 = H(x + dx * b, y + dy * b);
                            if (h1 >= h + 150 && h2 >= h + 150 && MathF.Abs(h1 - h2) < 60) wall = true;
                        }
                    if (wall) break;
                }
                if (wall) { _blocked[i] = true; BlockedBy[i] = "cloison"; n++; }
            }
        if (n > 0) Log.Add($"Cloisons fermées dans la grille : {n} case(s)");
    }

    /// <summary>Murs du décor (relevés sur les maillages) : cases fermées, sauf portes, véhicules et unités déjà là.</summary>
    public void AddWallCells(IEnumerable<int> cells)
    {
        if (_s.Grid is not { } g) return;
        if (_blocked.Length != g.Width * g.Height) _blocked = new bool[g.Width * g.Height];
        // jamais autour d'une porte : son cadre (montants, linteau) ne doit pas fermer le passage
        var keep = new HashSet<int>();
        foreach (var d in Openings.Where(d => !d.IsWindow))
        {
            foreach (var c0 in d.Cells)
                for (int ox = -1; ox <= 1; ox++)
                    for (int oy = -1; oy <= 1; oy++)
                    {
                        int x = c0 % g.Width + ox, y = c0 / g.Width + oy;
                        if (g.Inside(x, y)) keep.Add(g.Index(x, y));
                    }
            var (dx, dy) = g.CellOf(d.Center);
            for (int ox = -1; ox <= 1; ox++)
                for (int oy = -1; oy <= 1; oy++)
                    if (g.Inside(dx + ox, dy + oy)) keep.Add(g.Index(dx + ox, dy + oy));
        }
        foreach (var u in Units) { var (cx, cy) = g.CellOf(u.Position); if (g.Inside(cx, cy)) keep.Add(g.Index(cx, cy)); }
        foreach (var c in Civilians) { var (cx, cy) = g.CellOf(c.Position); if (g.Inside(cx, cy)) keep.Add(g.Index(cx, cy)); }
        foreach (var w in _waypoints.Values) { var (cx, cy) = g.CellOf(new Vector3(w, 0)); if (g.Inside(cx, cy)) keep.Add(g.Index(cx, cy)); }
        int n = 0;
        foreach (var i in cells)
        {
            if (i < 0 || i >= _blocked.Length || g.ZoneA[i] == 0 || _blocked[i] || keep.Contains(i)) continue;
            _blocked[i] = true; BlockedBy[i] = "mur"; n++;
        }
        if (n > 0) Log.Add($"Murs du décor ajoutés à la grille : {n} case(s)");
        Console.WriteLine($"Murs du décor ajoutés à la grille : {n} case(s)");
    }

    private void BuildObstacles(Level level)
    {
        if (_s.Grid is not { } g) return;
        _blocked = new bool[g.Width * g.Height];
        var assets = _assets ??= new SceneAssets(_files);
        var shapes = new Dictionary<string, List<Vector2>>(StringComparer.OrdinalIgnoreCase);
        var cover = new Dictionary<int, int>(); var names = new Dictionary<int, string>();
        foreach (var o in level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform))
        {
            var c = Class("Design\\" + o.ClassPath["design\\".Length..] + ".class");
            if (c == null || c.GetInt("OCCUPATION", "Activated", 1) == 0 || c.GetInt("OCCUPATION", "Background", 0) == 1) continue;
            var type = c.Get("OCCUPATION", "Occupation_Type")?.Trim('"', ' ').ToUpperInvariant();
            if (type is "GHOST" or "DOOR" or "WINDOW" or "WAYPOINT") continue;
            var mode = c.Get("OCCUPATION", "Occupation_Mode")?.Trim('"', ' ').ToUpperInvariant() ?? "AGO";
            var model = c.Get("DISPLAY", "Model")?.Trim('"', ' ');
            List<Vector2> pts;
            if (mode == "RECTANGLE")
            {
                int sx = c.GetInt("OCCUPATION", "Rect_SizeX", 0), sy = c.GetInt("OCCUPATION", "Rect_SizeY", 0);
                if (sx <= 0 || sy <= 0) continue;
                pts = new();
                for (float x = -sx * 50 + 25; x < sx * 50; x += 50)
                    for (float y = -sy * 50 + 25; y < sy * 50; y += 50) pts.Add(new(x, y));
                Mark(o, pts, model ?? "");
                continue;
            }
            if (string.IsNullOrEmpty(model) || model.Equals("noitem", StringComparison.OrdinalIgnoreCase)) continue;
            var path = SceneAssets.GfxPath("Placeable", model);
            if (!shapes.TryGetValue(path, out pts!)) shapes[path] = pts = Footprint(assets, path);
            Mark(o, pts, model);
        }

        // une case n'est bloquée que si l'objet en couvre plus de la moitié (rangées de sièges, tables : on passe entre)
        foreach (var (i, n) in cover) if (n > CoverMin) { _blocked[i] = true; BlockedBy[i] = names[i]; }

        void Mark(LevelObject o, List<Vector2> local, string model)
        {
            var seen = new HashSet<(int, int)>();
            foreach (var p in local)
            {
                var w = Vector3.Transform(new Vector3(p, 0), o.Matrix);
                if (!seen.Add(((int)MathF.Floor(w.X / 25), (int)MathF.Floor(w.Y / 25)))) continue;
                var (cx, cy) = g.CellOf(w);
                if (!g.Inside(cx, cy)) continue;
                int i = g.Index(cx, cy);
                cover[i] = cover.GetValueOrDefault(i) + 1; names.TryAdd(i, o.Name + " " + model);
            }
        }
    }
    private const int CoverMin = 8;   // sur 16 carrés de 25 cm par case

    /// <summary>Empreinte au sol d'un modèle (repère du modèle) : points tous les 25 cm sous ses faces à hauteur d'homme.</summary>
    private static List<Vector2> Footprint(SceneAssets assets, string path, float Low = 30, float High = 170)
    {
        const float Step = 25;
        var set = new HashSet<(int, int)>();
        try
        {
            foreach (var part in assets.ModelParts(path))
            {
                var m = assets.LoadMesh("", part.Mesh, "gfx").Mesh;
                var v = new Vector3[m.VertexCount];
                for (int i = 0; i < v.Length; i++) v[i] = Vector3.Transform(new Vector3(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]), part.World);
                for (int t = 0; t + 2 < m.Indices.Length; t += 3)
                {
                    var a = v[m.Indices[t]]; var b = v[m.Indices[t + 1]]; var cc = v[m.Indices[t + 2]];
                    float zmin = MathF.Min(a.Z, MathF.Min(b.Z, cc.Z)), zmax = MathF.Max(a.Z, MathF.Max(b.Z, cc.Z));
                    if (zmin > High || zmax < Low) continue;
                    // échantillonnage de la face projetée au sol
                    float len = MathF.Max(Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y)), MathF.Max(Vector2.Distance(new(a.X, a.Y), new(cc.X, cc.Y)), Vector2.Distance(new(b.X, b.Y), new(cc.X, cc.Y))));
                    int n = Math.Clamp((int)(len / Step) + 1, 1, 400);
                    for (int i = 0; i <= n; i++)
                        for (int j = 0; j <= n - i; j++)
                        {
                            float u = (float)i / n, w = (float)j / n;
                            var p = a + (b - a) * u + (cc - a) * w;
                            if (p.Z < Low - 20 || p.Z > High + 20) continue;   // grande face inclinée : seule la partie basse compte
                            set.Add(((int)MathF.Floor(p.X / Step), (int)MathF.Floor(p.Y / Step)));
                        }
                }
            }
        }
        catch { }
        if (set.Count == 0) return new();
        // intérieur fermé (carrosserie d'un bus dont le toit est trop haut, armoire…) : rempli, comme une forme pleine
        int x0 = set.Min(k => k.Item1) - 1, x1 = set.Max(k => k.Item1) + 1, y0 = set.Min(k => k.Item2) - 1, y1 = set.Max(k => k.Item2) + 1;
        int fw = x1 - x0 + 1, fh = y1 - y0 + 1;
        if ((long)fw * fh < 4_000_000)
        {
            var outside = new bool[fw * fh]; var stack = new Stack<(int, int)>(); stack.Push((0, 0)); outside[0] = true;
            while (stack.Count > 0)
            {
                var (x, y) = stack.Pop();
                foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                    if (nx >= 0 && ny >= 0 && nx < fw && ny < fh && !outside[ny * fw + nx] && !set.Contains((nx + x0, ny + y0)))
                    { outside[ny * fw + nx] = true; stack.Push((nx, ny)); }
            }
            for (int y = 0; y < fh; y++) for (int x = 0; x < fw; x++) if (!outside[y * fw + x]) set.Add((x + x0, y + y0));
        }
        return set.Select(k => new Vector2((k.Item1 + 0.5f) * Step, (k.Item2 + 0.5f) * Step)).ToList();
    }

    // passer dans un feu coûte cher : le chemin le contourne quand il peut
    private float FireCost(int cx, int cy)
    {
        var p = new Vector3((cx + 0.5f) * Grid.CellSize, (cy + 0.5f) * Grid.CellSize, 0);
        foreach (var h in _s.Fire.HotPoints)
            if (h.IsBurning && MathF.Abs(h.Position.X - p.X) < 160 && MathF.Abs(h.Position.Y - p.Y) < 160) return 6f;
        return 0f;
    }

    private Vector2 Walkable(Vector2 p)
    {
        if (_s.Grid is not { } g) return p;
        var (cx, cy) = g.CellOf(new Vector3(p, 0));
        if (Free(cx, cy)) return p;
        for (int r = 1; r < 20; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && Free(cx + dx, cy + dy))
                        return new((cx + dx + 0.5f) * Grid.CellSize, (cy + dy + 0.5f) * Grid.CellSize);
        return p;
    }

    /// <summary>
    /// Ordre de déplacement : chemin le plus court sur la grille (A*, 8 directions), puis lissé.
    /// Si le point n'est pas atteignable, l'unité va au point atteignable le plus proche.
    /// <paramref name="drive"/> : véhicule conduit par le joueur (gabarit, évite les autres véhicules garés) ;
    /// sinon un véhicule suit la route d'arrivée du script sans s'occuper des autres.
    /// </summary>
    public bool Order(Unit u, Vector2 dest, HotPoint? target = null, bool drive = false, bool refuseUnreachable = true)
    {
        if (u.Destroyed) { u.Status = "hors service (détruit)"; return false; }
        if (u.Unconscious) return false;
        u.Target = target; u.Path.Clear(); u.GoTo = null; u.GoToVehicle = null; u.HealTimer = 0; u.Opening = null;
        _pathingVehicle = u.IsVehicle && !drive;
        _clearance = u.IsVehicle && drive ? 1 : 0; _selfId = u.IsVehicle ? u.Id : 0;
        bool partial = false;
        try
        {
            var path = FindPath(new(u.Position.X, u.Position.Y), Walkable(dest), out partial);
            foreach (var p in path) u.Path.Enqueue(p);
        }
        finally { _pathingVehicle = false; _clearance = 0; _selfId = 0; }
        // point inatteignable : l'unité ne bouge pas et le dit (« VOCAL_IMPOSSIBLE_TO_ACCESS »)
        if (refuseUnreachable && (partial || (u.Path.Count == 0 && Vector2.Distance(new(u.Position.X, u.Position.Y), dest) > 150)))
        {
            u.Path.Clear(); u.Target = null;
            u.Status = "impossible d'y accéder";
            if (PlayerCommand) Sounds.Add(("VOCAL_IMPOSSIBLE_TO_ACCESS", null));   // voix seulement pour un ordre du joueur, pas pour l'IA
            return false;
        }
        u.Status = target != null ? $"va éteindre {target.Name}" : u.Path.Count == 0 ? "attend les ordres" : "en route";
        return true;
    }

    /// <summary>Sons à jouer (étiquette de la base de sons du jeu, position 3D ou null pour un son d'interface).</summary>
    public List<(string label, Vector3? pos)> Sounds { get; } = new();

    /// <summary>Clic droit avec un véhicule choisi : il roule jusqu'au point (ou au plus près), en contournant les obstacles.</summary>
    public bool Drive(Unit v, Vector2 dest) => Order(v, dest, null, drive: true);

    /// <summary>
    /// Attaque d'un feu : l'unité se place à la distance d'arrosage (portée × 0,72 + recul réglé à l'œil),
    /// en s'approchant ou en reculant, puis arrose.
    /// </summary>
    public void Attack(Unit u, HotPoint h)
    {
        if (u.Nozzle == null || u.Destroyed) { u.Status = u.Destroyed ? "hors service (détruit)" : "ne peut pas éteindre"; return; }
        if (u.IsVehicle) { VehicleAttack(u, h); return; }
        if (u.Carrying != null) { u.Status = "porte un civil : ne peut pas arroser"; return; }
        var me = new Vector2(u.Position.X, u.Position.Y); var f = new Vector2(h.Position.X, h.Position.Y);
        float range = MathF.Max(150, u.Nozzle.RangeMax * 0.8f), d = Vector2.Distance(me, f);
        float want = range * 0.9f + ExtraStandoff;
        // feu tout proche : il ne recule que jusqu'à une partie de cette distance (AttackBackOff, réglage du jeu)
        float near = want * Math.Clamp(AttackBackOff, 0.1f, 1f);
        if (d < want) want = near;
        if (MathF.Abs(d - want) < 80 || (d >= near && d <= want + 80)) { u.Path.Clear(); u.Target = h; u.Status = $"arrose {h.Name}"; return; }
        // chaque unité se place un peu à côté des autres, autour du feu
        var dir = d > 1 ? Vector2.Normalize(me - f) : Vector2.UnitX; float spread = ((u.Id * 7) % 9 - 4) * 0.18f;
        dir = new(dir.X * MathF.Cos(spread) - dir.Y * MathF.Sin(spread), dir.X * MathF.Sin(spread) + dir.Y * MathF.Cos(spread));
        Order(u, f + dir * want, h);
    }

    private List<Vector2> FindPath(Vector2 from, Vector2 to) => FindPath(from, to, out _);

    private List<Vector2> FindPath(Vector2 from, Vector2 to, out bool partial)
    {
        partial = false;
        var g = _s.Grid;
        if (g == null) return new() { to };
        var (sx, sy) = g.CellOf(new Vector3(from, 0)); var (tx, ty) = g.CellOf(new Vector3(to, 0));
        if (!g.Inside(tx, ty)) return new();
        // départ dans un obstacle (un camion s'est garé sur le pompier) : il en sort d'abord par la case libre la plus proche
        if (!Free(sx, sy) && Walkable(from) is var exit && exit != from)
        {
            var rest = FindPath(exit, to, out partial);
            if (rest.Count > 0 || (tx, ty) == g.CellOf(new Vector3(exit, 0))) { rest.Insert(0, exit); return rest; }
        }
        if (sx == tx && sy == ty) return new() { to };
        var open = new PriorityQueue<(int x, int y), float>();
        var cost = new Dictionary<int, float>(); var prev = new Dictionary<int, int>();
        int Key(int x, int y) => y * g.Width + x;
        open.Enqueue((sx, sy), 0); cost[Key(sx, sy)] = 0;
        int found = -1, guard = 0;
        while (open.Count > 0 && guard++ < 200000)
        {
            var (x, y) = open.Dequeue();
            if (x == tx && y == ty) { found = Key(x, y); break; }
            float c0 = cost[Key(x, y)];
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (!g.Inside(nx, ny)) continue;
                    // la case de départ peut être bloquée (unité posée dans un coin) ; on la quitte quand même
                    if (!Free(nx, ny) && !(nx == tx && ny == ty)) continue;
                    if (dx != 0 && dy != 0 && (!Free(x + dx, y) || !Free(x, y + dy))) continue;
                    float c = c0 + (dx != 0 && dy != 0 ? 1.414f : 1f) + FireCost(nx, ny);
                    int k = Key(nx, ny);
                    if (cost.TryGetValue(k, out var old) && old <= c) continue;
                    cost[k] = c; prev[k] = Key(x, y);
                    open.Enqueue((nx, ny), c + MathF.Sqrt((tx - nx) * (tx - nx) + (ty - ny) * (ty - ny)));
                }
        }
        if (found < 0)
        {
            // point inatteignable : on s'arrête à la case explorée la plus proche du point demandé
            int best = -1; float bd = float.MaxValue;
            foreach (var k in cost.Keys)
            {
                int x = k % g.Width, y = k / g.Width; float d = (x - tx) * (x - tx) + (y - ty) * (y - ty);
                if (d < bd && Free(x, y)) { bd = d; best = k; }
            }
            if (best < 0 || best == Key(sx, sy)) { partial = best >= 0; return new(); }
            found = best; partial = true;
            to = new(((best % g.Width) + 0.5f) * Grid.CellSize, ((best / g.Width) + 0.5f) * Grid.CellSize);
        }
        var cells = new List<Vector2>();
        for (int k = found; prev.ContainsKey(k); k = prev[k])
            cells.Add(new(((k % g.Width) + 0.5f) * Grid.CellSize, ((k / g.Width) + 0.5f) * Grid.CellSize));
        cells.Reverse();
        if (cells.Count > 0) cells[^1] = to;
        // lissage : on saute les points intermédiaires quand la ligne droite est libre
        var smooth = new List<Vector2>(); var cur = from; int i = 0;
        while (i < cells.Count)
        {
            int j = cells.Count - 1;
            while (j > i && !LineFree(cur, cells[j])) j--;
            smooth.Add(cells[j]); cur = cells[j]; i = j + 1;
        }
        return smooth;
    }

    private bool LineFree(Vector2 a, Vector2 b, bool ignoreFire = false)
    {
        if (_s.Grid is not { } g) return true;
        int n = (int)(Vector2.Distance(a, b) / 40) + 1;
        for (int i = 1; i < n; i++)
        {
            var p = Vector2.Lerp(a, b, (float)i / n); var (cx, cy) = g.CellOf(new Vector3(p, 0));
            if (!Free(cx, cy) || (!ignoreFire && FireCost(cx, cy) > 0)) return false;
        }
        return true;
    }

    /// <summary>Clic droit sur un véhicule : remplir la réserve (camion) ou y déposer les civils qui suivent (ambulance).</summary>
    public void GoToVehicle(Unit u, Unit vehicle)
    {
        if (u.IsVehicle || !vehicle.IsVehicle) return;
        var me = new Vector2(u.Position.X, u.Position.Y); var v = new Vector2(vehicle.Position.X, vehicle.Position.Y);
        var dir = Vector2.Distance(me, v) > 1 ? Vector2.Normalize(me - v) : Vector2.UnitX;
        if (!Order(u, v + dir * (VehicleReach * 0.6f))) return;
        u.GoToVehicle = vehicle;
        u.Status = vehicle.IsAmbulance ? "va à l'ambulance" : "va remplir sa réserve";
        // les civils qui le suivent l'accompagnent
    }

    private void Rescue(Unit u, float dt)
    {
        RescueCivilian(u, dt);   // voir CivilianAI.cs
        if (u.GoToVehicle is { } v && !u.Moving)
        {
            if (Vector3.Distance(u.Position, v.Position) > VehicleReach * 1.5f) { u.GoToVehicle = null; u.Status = "véhicule hors d'atteinte"; return; }
            if (v.IsAmbulance)
            {
                int n = 0;
                foreach (var civ in Civilians.Where(x => x.Leader == u && x.State == CivilianState.Conscious))
                    if (Vector3.Distance(civ.Position, v.Position) < VehicleReach * 2.5f) { Save(civ); n++; }
                if (n > 0) { Log.Add($"{_s.Time,6:0} s : {n} civil(s) mis à l'abri dans l'ambulance ({Rescued} sauvé(s))"); Sounds.Add(("VOCAL_CIVILIAN_SAVED", null)); }
                u.GoToVehicle = null; u.Status = "attend les ordres";
            }
            if (u.Boarding) { Board(u, v); return; }
            if (u.Nozzle != null && v.ResupplyPower > 0)
            {
                // la recharge elle-même se fait dans Resupply, tant qu'il reste près du camion
                u.Status = $"remplit sa réserve ({u.Nozzle.Reserve:0}/{u.Nozzle.ReserveMax:0})";
                if (u.Nozzle.Reserve >= u.Nozzle.ReserveMax) { u.GoToVehicle = null; u.Status = "réserve pleine"; }
            }
            else { u.GoToVehicle = null; u.Status = "attend les ordres"; }
        }
    }

    private void Move(Unit u, float dt)
    {
        if (u.IsVehicle) { VehicleMove(u, dt); return; }
        float step = u.Speed * dt * (u.Carry == CarryPhase.Walking ? CarrySpeed : 1f);   // chargé d'un corps : plus lent
        while (step > 0 && u.Path.Count > 0)
        {
            var p = new Vector2(u.Position.X, u.Position.Y); var t = u.Path.Peek();
            var d = t - p; float len = d.Length();
            if (len > 1e-3f) u.Heading = MathF.Atan2(d.Y, d.X);
            if (len <= step) { p = t; u.Path.Dequeue(); step -= len; }
            else { p += d / len * step; step = 0; }
            u.Position = new Vector3(p, Ground(p));
        }
        if (u.Path.Count == 0 && u.Status.StartsWith("en route")) u.Status = u.IsVehicle ? "garé" : "attend les ordres";
    }

    /// <summary>
    /// Un pompier à l'arrêt, sans ordre, attaque de lui-même le feu le plus proche à portée de lance
    /// (comme dans le jeu, où les unités réagissent aux feux de leur zone d'attaque).
    /// </summary>
    public const float ReviveSeconds = 4f;
    // vitesse de recharge : ResupplyPower de la classe (autopompe 150) par tranche de 10 s
    // (150 → 15 unités d'eau par seconde : un pompier vide se remplit en un peu plus de 15 s, bien plus vite qu'il ne la vide)
    public const float ResupplyScale = 0.1f;

    private static string UnitName(Unit u) => u.Name;


    /// <summary>Clic droit d'un pompier sur un collègue inconscient : il va le ranimer.</summary>
    public void ReviveColleague(Unit u, Unit victim)
    {
        if (u.IsVehicle || u == victim || !victim.Unconscious || u.Unconscious || u.OnBoard) return;
        if (!Order(u, new(victim.Position.X, victim.Position.Y))) return;
        u.ReviveTarget = victim; u.Status = "va ranimer son collègue";
    }

    private void Revive(Unit u, float dt)
    {
        if (u.ReviveTarget is not { } v || u.Moving) return;
        if (!v.Unconscious) { u.ReviveTarget = null; u.HealTimer = 0; return; }
        if (Vector3.Distance(u.Position, v.Position) > ReachDistance * 2) { u.ReviveTarget = null; u.Status = "collègue hors d'atteinte"; return; }
        u.HealTimer += dt * (u.CanHeal ? 2 : 1);
        u.Status = $"ranime son collègue ({u.HealTimer / ReviveSeconds:P0})";
        u.Heading = MathF.Atan2(v.Position.Y - u.Position.Y, v.Position.X - u.Position.X);
        if (u.HealTimer < ReviveSeconds) return;
        v.Unconscious = false; v.Status = "attend les ordres"; v.Stamina = MathF.Max(v.Stamina, v.StaminaMax * ReviveStamina);
        u.HealTimer = 0; u.ReviveTarget = null; u.Status = "attend les ordres";
        Log.Add($"{_s.Time,6:0} s : un pompier a été ranimé");
    }

    /// <summary>
    /// Recharge en eau : un pompier près d'un camion qui recharge (et à l'arrêt) se remplit peu à peu ; il doit y rester
    /// jusqu'au bout. Avec AutomaticResupply, il le fait de lui-même dès qu'il est à côté, sans ordre.
    /// </summary>
    private void Resupply(Unit u, float dt)
    {
        if (u.IsVehicle || u.OnBoard || u.Unconscious || u.Nozzle is not { InfiniteReserve: false } n || n.Reserve >= n.ReserveMax || u.Moving) return;
        bool asked = u.GoToVehicle is { ResupplyPower: > 0 };
        if (!asked && (!u.AutoResupply || u.Target != null)) return;
        var truck = Units.Where(v => v.IsVehicle && v.ResupplyPower > 0 && !v.Moving && !v.Destroyed && Vector3.Distance(v.Position, u.Position) < VehicleReach * 1.5f)
            .OrderBy(v => Vector3.Distance(v.Position, u.Position)).FirstOrDefault();
        if (truck == null) return;
        float give = MathF.Min(n.ReserveMax - n.Reserve, truck.ResupplyPower * ResupplyScale * dt);
        // l'eau vient de la réserve du camion (qui se recharge à une bouche d'incendie ou auprès d'un autre camion)
        if (truck.Nozzle is { InfiniteReserve: false } tn)
        {
            give = MathF.Min(give, tn.Reserve);
            if (give <= 0) { if (asked) u.Status = "le camion est vide : rechargez-le à une bouche d'incendie"; return; }
            tn.Reserve -= give;
        }
        n.Reserve += give;
        if (!asked && u.Status.StartsWith("attend")) u.Status = $"se recharge ({n.Reserve:0}/{n.ReserveMax:0})";
    }

    /// <summary>Clic droit d'un pompier sur un véhicule (réserve pleine) : il va y monter.</summary>
    public void BoardVehicle(Unit u, Unit vehicle)
    {
        if (u.IsVehicle || !vehicle.IsVehicle || u.OnBoard) return;
        if (vehicle.Destroyed) { u.Status = "ce véhicule est détruit"; return; }
        if (vehicle.Seats <= 0) { u.Status = "ce véhicule n'a pas de place"; return; }
        GoToVehicle(u, vehicle);
        if (u.GoToVehicle == vehicle) { u.Boarding = true; u.Status = "va monter dans le véhicule"; }
    }

    /// <summary>Essais : monte tout de suite à bord.</summary>
    public void BoardNow(Unit u, Unit v) => Board(u, v);

    private void Board(Unit u, Unit v)
    {
        u.Boarding = false; u.GoToVehicle = null;
        if (v.Passengers.Count >= v.Seats) { u.Status = "véhicule plein"; return; }
        v.Passengers.Add(u); u.InVehicle = v;
        foreach (var civ in Civilians.Where(c => c.Leader == u)) civ.Leader = null;
        Sounds.Add(("VEHICLE_EMBARK", v.Position));
        Log.Add($"{_s.Time,6:0} s : un pompier monte à bord ({v.Passengers.Count}/{v.Seats})");
    }

    /// <summary>Clic droit sur un véhicule choisi : tous ses passagers descendent autour de lui.</summary>
    public int UnloadPassengers(Unit v)
    {
        if (v.Passengers.Count == 0 || v.Moving) return 0;
        var spots = DropSpots(v, v.Passengers.Count);
        int n = 0;
        foreach (var u in v.Passengers)
        {
            var p = n < spots.Count ? spots[n] : new Vector2(v.Position.X, v.Position.Y);
            u.InVehicle = null; u.Position = new Vector3(p, Ground(p)); u.Status = "attend les ordres";
            u.Heading = MathF.Atan2(p.Y - v.Position.Y, p.X - v.Position.X); n++;
        }
        v.Passengers.Clear();
        Log.Add($"{_s.Time,6:0} s : {n} pompier(s) descendent du véhicule");
        return n;
    }

    /// <summary>Unités visibles sur le terrain (pas celles qui sont à bord d'un véhicule).</summary>
    public List<Unit> Active => Units.Where(u => !u.OnBoard && !u.Hidden).ToList();

    private void Fight(Unit u, float dt)
    {
        if (u.Target == null || u.Moving || u.Nozzle == null || u.Unconscious || u.OnBoard || u.Destroyed) return;
        var h = u.Target;
        if (!h.Lit)
        {
            if (_reported.Add(h)) Log.Add($"{_s.Time,6:0} s : {h.Name} éteint");
            u.Target = null; u.Status = "attend les ordres"; return;
        }
        _reported.Remove(h);
        float want = MathF.Atan2(h.Position.Y - u.Position.Y, h.Position.X - u.Position.X);
        if (u.IsVehicle)
        {
            // lance à l'avant, orientable de ± XSprinklerAngleMax (0,9 rad) : au-delà, le camion manœuvre pour se tourner
            float diff = MathF.IEEERemainder(want - u.Heading, 2 * MathF.PI);
            float d = Vector2.Distance(new(u.Position.X, u.Position.Y), new(h.Position.X, h.Position.Y));
            if (d > u.Nozzle.RangeMax * 1.05f) { VehicleAttack(u, h); return; }
            if (VehicleAim(u, h, diff, dt)) return;   // voir VehicleAI.cs
        }
        else u.Heading = want;
        // pompier relié à un camion proche : la réserve se remplit en continu
        if (!u.Nozzle.InfiniteReserve && u.Nozzle.Reserve <= 0) { u.Status = "réserve vide : à ramener au camion"; u.Target = null; Sounds.Add(("VOCAL_RESERVE_EMPTY", null)); return; }
        u.Nozzle.Spray(_s.Fire, h, dt);
        u.Status = $"arrose {h.Name}";
    }
}
