using System.Numerics;
using OpenFD.Fire;
using OpenFD.Formats;

namespace OpenFD.Game;

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
    // véhicules : ForwardSpeed de la classe (15 pour un camion, 20 le wildland, 10 l'ambulance…) × 120 unités/s ;
    // doublé (60 → 120) après comparaison avec le jeu d'origine, où les camions roulaient environ deux fois plus vite
    public const float FiremanSpeedFactor = 45f, VehicleSpeedFactor = 120f;
    /// <summary>
    /// Accélération et freinage des véhicules : [MOVING] AcceleratePower (8) et DeceleratePower (6) × 60 unités/s² ; un camion
    /// met environ 4 s à atteindre sa vitesse. Sur les petites cartes (Institute), les trajets sont courts : il n'a pas le
    /// temps d'aller vite, comme dans le jeu d'origine.
    /// </summary>
    public const float VehicleAccelFactor = 60f;
    /// <summary>Recul supplémentaire devant le feu (1 m = 100 unités), réglé à l'œil sur le jeu d'origine.</summary>
    public const float ExtraStandoff = 400f;
    /// <summary>Vrai pendant un ordre donné par le joueur (l'interface le met) : les réponses vocales ne concernent que ceux-là.</summary>
    public bool PlayerCommand;
    /// <summary>
    /// Ordre du joueur en cours d'exécution, y compris rejoué (chargement d'une partie, ordre d'un autre joueur en
    /// multijoueur) : contrairement à PlayerCommand (réponses vocales), il change le jeu de la même façon partout.
    /// </summary>
    public bool PlayerOrder;
    /// <summary>Portée à laquelle un pompier libre repère un feu et va l'éteindre, en multiple de celle de sa lance (options : Gameplay).</summary>
    public static float AutoAttackRange = 2f;
    /// <summary>Feu tout proche : le pompier recule jusqu'à cette part de sa distance d'arrosage habituelle (options : Gameplay).</summary>
    public static float AttackBackOff = 0.5f;

    public UnitManager(Session s, GameFiles files, ClassFile mission)
    {
        _s = s;
        _files = files;
        Paths = new PathFind(s, _assets ??= new SceneAssets(files), Class, Log);
        foreach (LevelObject o in s.Level.Objects.Where(o => o.Kind == EntityKind.Waypoint && o.HasTransform))
            _waypoints[o.Name] = new(o.Position.X, o.Position.Y);
        LoadCivilians(s.Level);
        if (Environment.GetEnvironmentVariable("FD3_NOBLOCK") == null)
            Paths.BuildObstacles(s.Level);
        LoadOpenings(s.Level);
        LoadTeleports(s.Level);
        LoadOccupations(s.Level);
        LoadLevelVehicles(s.Level);
        FindWater();
        LockBuriedRubble();   // voir Dog.cs
        AssignLevers(s.Level);
        Paths.BlockThinWalls(Openings, KeepPoints());
        LoadSupplies(s.Level);
        LoadTriggers(s.Level);
        LoadUsables(s.Level);
        LoadGrapples(s.Level);   // voir Grapple.cs
        _saveCivilianScript = mission.Get("COMMAND_CALLBACKS", "CmdSaveCivilian")?.Trim('"', ' ');
        const string F = "FIRESTATION";
        string? initial = mission.Get(F, "InitialDispatch")?.Trim('"', ' ');
        // équipe de départ : en route dès le début (les scripts comptent les pompiers à 0 s : LEV_GetNumFiremen),
        // sauf si le script de la mission l'appelle lui-même (LEV_CallInitialDispatch, voir ScriptDispatch)
        if (!string.IsNullOrEmpty(initial))
        {
            _initial = (initial, mission.GetInt(F, "InitialDispatchSpawnPoint", 1));
            Teams.Add(TeamCode(initial));
            Dispatch(initial, _initial.Value.spawn);
        }
        for (int i = 1; i <= 9; i++)
        {
            string? r = mission.Get(F, $"Reinforcement{i}")?.Trim('"', ' ');
            if (string.IsNullOrEmpty(r))
                continue;
            // (la mission Fireworks écrit « REINFORECEMENT1ETA » : faute de frappe d'origine, valeur par défaut 60 s)
            float eta = mission.GetFloat(F, $"Reinforcement{i}ETA", 60);
            // caserne aérienne (bombardier d'eau) : appelée par le joueur sur une zone, voir AirSupport.cs
            if (SetupAirStation(r, mission.Get(F, $"Reinforcement{i}ETA") != null ? eta : 30))
            {
                if (!Teams.Contains(TeamCode(r)))
                    Teams.Add(TeamCode(r));
                continue;
            }
            // même caserne qu'une équipe déjà là (TMI : deux « E9 » de plus en renfort) : une équipe à part, « E9-2 », « E9-3 »
            string code = TeamCode(r);
            int same = 1;
            while (Teams.Contains(same == 1 ? code : $"{code}-{same}"))
                same++;
            if (same > 1)
                r += "#" + same;
            _pending.Add((eta, r, mission.GetInt(F, $"Reinforcement{i}SpawnPoint", 1)));
            Teams.Add(TeamCode(r));
        }
    }

    private void LoadCivilians(Level level)
    {
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.Civilian && o.HasTransform))
        {
            ClassFile? c = Class("Design\\" + o.ClassPath["design\\".Length..] + ".class");
            string? model = c?.Get("DISPLAY", "Model")?.Trim('"', ' ');
            if (string.IsNullOrEmpty(model))
                continue;
            // état de départ : octet 39 du composant A02A (1 = choqué/conscient, 3 = évanoui, 4 = mort)
            int state = o.GetComponent(0xA02A) is { } comp && comp.Size > 39 ? o.ComponentBytes(comp)[39] : 1;
            Civilians.Add(new Civilian
            {
                Id = o.Id, Name = o.Name, Model = SceneAssets.GfxPath("Unit\\Civilian", model), Position = o.Position,
                ClassPath = "Design\\" + o.ClassPath["design\\".Length..] + ".class",
                Heading = o.Rotation.Z,
                State = state == 3 ? CivilianState.Unconscious : state == 4 ? CivilianState.Dead : CivilianState.Conscious,
                Hidden = o.Name.Contains("hide", StringComparison.OrdinalIgnoreCase) || !o.StartVisible,   // enterré : révélé par le chien
            });
        }
    }

    private ClassFile? Class(string rel)
    {
        try { return ClassFile.Load(_files, rel, _cache); } catch { return null; }
    }

    public void Update(float time, float dt)
    {
        PlayerOrder = false;   // l'ordre du joueur est appliqué : ce qui suit est l'IA (même chose en direct et rejoué)
        UpdateOccupations();
        UpdateDemolishVisibility();
        for (int i = _pending.Count - 1; i >= 0; i--)
            if (time >= _pending[i].eta)
            {
                Dispatch(_pending[i].cfg, _pending[i].spawn);
                _incoming.Remove(TeamCode(_pending[i].cfg));
                _pending.RemoveAt(i);
            }
        foreach (Unit u in Units.Where(u => u.Unconscious || u.OnBoard))
        {
            u.Path.Clear();
            u.Target = null;
            u.Opening = null;
            u.GoTo = null;
            u.GoToVehicle = null;
            u.ReviveTarget = null;
            u.UseTarget = null;
            if (u.Unconscious)
                u.Status = "inconscient : un collègue doit le ranimer";
            if (u.InVehicle is { } veh)
            {
                u.Position = veh.Position;
                u.Status = $"à bord ({UnitName(veh)})";
            }
        }
        foreach (Unit u in Units.Where(u => u.InjuredTimer > 0))
        {
            u.InjuredTimer = MathF.Max(0, u.InjuredTimer - dt);
            u.Path.Clear();
            u.Target = null;
            u.Opening = null;
            u.GoTo = null;
            u.GoToVehicle = null;
            u.Status = u.InjuredTimer > 0 ? "blessé, au sol" : "attend les ordres";
        }
        UpdateHealth(dt);
        UpdateLadders(dt);
        UpdateWaterTowers(dt);
        UpdateAirStrike();
        foreach (Unit u in Units)
            OpenScriptDoors(u);
        foreach (Unit u in Units)
            Move(u, dt);
        UpdateVehicleObstacles();
        foreach (Unit v in Units.Where(u => u.IsVehicle && u.Crew.Count > 0 && !u.Moving).ToList())
            Unload(v);
        _autoTick++;
        foreach (Unit u in Units)
            UpdateCanonLink(u);
        foreach (Unit u in Units)
            UpdateDog(u, dt);
        foreach (Unit u in Units)
            AutoAttack(u);
        foreach (Unit u in Units)
            Fight(u, dt);
        foreach (Unit u in Units)
            Rescue(u, dt);
        foreach (Unit u in Units)
            CarryVictim(u, dt);   // voir CivilianAI.cs
        foreach (Unit u in Units)
            Revive(u, dt);
        foreach (Unit u in Units)
            Resupply(u, dt);
        foreach (Unit v in Units.Where(v => v.IsVehicle))
            VehicleResupply(v, dt);
        CheckTriggers();
        foreach (Unit u in Units)
            WorkOpening(u, dt);
        foreach (Unit u in Units)
            WorkUsable(u, dt);
        foreach (Civilian c in Civilians)
            Follow(c, dt);
    }

    /// <summary>Arrivée d'une équipe : les véhicules entrent par locstartN_k et rejoignent locendN_k, l'équipage descend.</summary>
    /// <summary>Nombre de pompiers (hors véhicules) d'une équipe de la caserne.</summary>
    private int CrewSize(string cfgName)
    {
        ClassFile? cfg = Class("Design\\Firestation\\" + CfgFile(cfgName) + ".cfg");
        if (cfg == null)
            return 0;
        return Enumerable.Range(1, 20).Select(i => cfg.Get("FIRESTATION", $"UNIT{i:00}")?.Trim('"', ' '))
            .Count(x => !string.IsNullOrEmpty(x) && !x.StartsWith("Firevehicle", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Équipes de la mission dans l'ordre (départ, puis renforts) : leur nom de code (« H7 », « H8 »).</summary>
    public List<string> Teams { get; } = new();

    /// <summary>Nom de code d'une équipe de la caserne ([FIRESTATION] CodeName), sinon le nom du fichier.</summary>
    /// <summary>
    /// Code de l'équipe (« E9 ») ; un renfort qui reprend la caserne d'une autre équipe est noté « classic_E9#2 » et devient
    /// « E9-2 » (une ligne à part dans le cadre des équipes).
    /// </summary>
    private string TeamCode(string cfgName)
    {
        string file = CfgFile(cfgName);
        string code = Class("Design\\Firestation\\" + file + ".cfg")?.Get("FIRESTATION", "CodeName")?.Trim('&', '"', ' ') is { Length: > 0 } c ? c : file;
        int hash = cfgName.IndexOf('#');
        return hash >= 0 ? $"{code}-{cfgName[(hash + 1)..]}" : code;
    }

    /// <summary>Nom du fichier de caserne, sans le numéro d'équipe en double (« classic_E9#2 » → « classic_E9 »).</summary>
    private static string CfgFile(string cfgName) => cfgName.IndexOf('#') is int h and >= 0 ? cfgName[..h] : cfgName;

    private void Dispatch(string cfgName, int spawn)
    {
        ClassFile? cfg = Class("Design\\Firestation\\" + CfgFile(cfgName) + ".cfg");
        if (cfg == null)
        {
            Log.Add($"Équipe introuvable : {cfgName}");
            return;
        }
        List<string> list = Enumerable.Range(1, 20).Select(i => cfg.Get("FIRESTATION", $"UNIT{i:00}")?.Trim('"', ' ')).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList();
        List<string> vehicles = list.Where(x => x.StartsWith("Firevehicle", StringComparison.OrdinalIgnoreCase)).ToList();
        List<string> crew = list.Where(x => !x.StartsWith("Firevehicle", StringComparison.OrdinalIgnoreCase)).ToList();
        Unit? lead = null;
        int k = 1;
        foreach (string v in vehicles)
        {
            Vector2 start = Wp($"locstart{spawn}_{k}") ?? Wp($"locstart{spawn}_1") ?? Center();
            Vector2 end = Wp($"locend{spawn}_{k}") ?? Wp($"locend{spawn}_1") ?? start;
            Unit? u = Create(v, true, start);
            if (u == null)
                continue;
            if (u.IsBoat)
                PutOnWater(u);
            u.Heading = MathF.Atan2(end.Y - start.Y, end.X - start.X);
            Order(u, end, refuseUnreachable: false);
            lead ??= u;
            k++;
        }
        string team = TeamCode(cfgName);
        if (!Teams.Contains(team))
            Teams.Add(team);
        List<Unit> trucks = Units.Where(u => u.IsVehicle && u.Id >= (lead?.Id ?? int.MaxValue)).ToList();
        if (trucks.Count > 0)
        {
            // l'équipage monte dans les véhicules (répartis à tour de rôle, les camions d'abord) et descend à l'arrivée
            List<Unit> order = trucks.OrderBy(t => t.IsAmbulance ? 1 : 0).ToList();
            for (int i = 0; i < crew.Count; i++)
                order[i % order.Count].Crew.Add(crew[i]);
            foreach (Unit t in trucks)
                t.TeamName = team;
            Log.Add($"{_s.Time,6:0} s : l'équipe {team} est en route ({list.Count} unités)");
            return;
        }
        Vector2 basePos = Wp($"locend{spawn}_1") ?? Center();
        int n = 0;
        foreach (string c in crew)
        {
            float a = n * 0.9f;
            Vector2 pos = basePos + new Vector2(MathF.Cos(a), MathF.Sin(a)) * (250 + 60 * n);
            Unit? u = Create(c, false, Walkable(pos));
            n++;
            if (u != null)
            {
                u.Status = "attend les ordres";
                u.TeamName = team;
            }
        }
        if (_s.Time > 1)
            Sounds.Add(("VOCAL_REINFORCEMENT_ARRIVED", null));
        Log.Add($"{_s.Time,6:0} s : arrivée de l'équipe {team} ({list.Count} unités)");
    }

    /// <summary>
    /// Véhicule arrivé : l'équipage descend autour de lui, sur des cases libres (ni décor, ni véhicule, ni porte fermée),
    /// de préférence sur les côtés et à l'arrière, sans se superposer.
    /// </summary>
    private void Unload(Unit v)
    {
        if (v.Crew.Count == 0 || v.Moving || _s.Grid == null)
            return;
        List<Vector2> spots = DropSpots(v, v.Crew.Count);
        Vector2 me = new Vector2(v.Position.X, v.Position.Y);
        int n = 0;
        foreach (string cls in v.Crew)
        {
            Vector2 spot = n < spots.Count ? spots[n] : Walkable(me);
            Unit? u = Create(cls, false, spot);
            if (u == null)
                continue;
            u.Heading = MathF.Atan2(spot.Y - me.Y, spot.X - me.X);
            u.Status = "attend les ordres";
            u.TeamName = v.TeamName;
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
        List<Vector2> res = new List<Vector2>();
        if (_s.Grid is not { } g)
            return res;
        UpdateVehicleObstacles();
        Vector2 me = new Vector2(v.Position.X, v.Position.Y);
        Vector2 fwd = new Vector2(MathF.Cos(v.Heading), MathF.Sin(v.Heading));
        List<Vector2> taken = Units.Where(u => !u.IsVehicle && !u.OnBoard).Select(u => new Vector2(u.Position.X, u.Position.Y)).ToList();
        int saved = Paths.SelfId;
        Paths.SelfId = -1;
        List<(Vector2 p, float score)> spots = new List<(Vector2 p, float score)>();
        for (float r = 250; r <= 1400 && spots.Count < 40 + count; r += 100)
            for (int k = 0; k < 24; k++)
            {
                float a = k * MathF.PI / 12;
                Vector2 dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
                Vector2 p = me + dir * r;
                (int cx, int cy) = g.CellOf(new Vector3(p, 0));
                if (!FreeCell(cx, cy))
                    continue;
                Paths.PathingVehicle = true;
                bool open = LineFree(me, p, ignoreFire: true);
                Paths.PathingVehicle = false;
                if (!open)
                    continue;
                spots.Add((new Vector2((cx + 0.5f) * Grid.CellSize, (cy + 0.5f) * Grid.CellSize), r + 150 * MathF.Max(0, Vector2.Dot(dir, fwd))));
            }
        Paths.SelfId = saved;
        for (int i = 0; i < count; i++)
        {
            Vector2 spot = spots.OrderBy(x => x.score).Select(x => x.p).FirstOrDefault(p => taken.All(t => Vector2.Distance(t, p) > 110));
            if (spot == default)
                spot = Walkable(me + new Vector2(-fwd.Y, fwd.X) * 350);
            res.Add(spot);
            taken.Add(spot);
        }
        return res;
    }

    private Vector2? Wp(string name) => _waypoints.TryGetValue(name, out Vector2 p) ? p : null;
    private Vector2 Center() => _s.Grid is { } g ? new(g.Width * Grid.CellSize / 2, g.Height * Grid.CellSize / 2) : Vector2.Zero;

    /// <summary>
    /// Nationalité des pompiers et véhicules choisie dans les options : « us », « fr », « gb », « gr » (Allemagne), « ua »,
    /// ou vide (celle de la mission). Les classes du jeu existent en variantes (fireman_sp_fr, Veh_P_pumper_gb…).
    /// </summary>
    public static string Nationality = "";
    private static readonly System.Text.RegularExpressions.Regex NatRx = new("_(us|fr|gb|gr|ua)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private string WithNationality(string classRel)
    {
        if (string.IsNullOrEmpty(Nationality))
            return classRel;
        string baseRel = NatRx.Replace(classRel, "");
        string cand = baseRel + "_" + Nationality;
        return Class("Design\\Unit\\" + cand + ".class") != null ? cand : classRel;
    }

    /// <summary>Nouvelle unité de cette classe ; <paramref name="id"/> : son numéro imposé (véhicule posé dans le niveau).</summary>
    private Unit? Create(string classRel, bool vehicle, Vector2 pos, int? id = null)
    {
        classRel = WithNationality(classRel);
        ClassFile? c = Class("Design\\Unit\\" + classRel + ".class");
        string? model = c?.Get("DISPLAY", "Model")?.Trim('"', ' ');
        if (c == null || string.IsNullOrEmpty(model))
            return null;
        string gfx = SceneAssets.GfxPath(vehicle ? "Unit\\FireVehicle" : "Unit\\Fireman", model);
        Unit u = new Unit
        {
            Id = id ?? _nextId++, Name = classRel[(classRel.LastIndexOf('\\') + 1)..], ClassPath = classRel, Model = gfx, IsVehicle = vehicle,
            Position = new Vector3(pos, Ground(pos)),
            Speed = vehicle ? c.GetFloat("MOVING", "ForwardSpeed", 15) * VehicleSpeedFactor : c.GetFloat("MOVING", "RunSpeed", 10) * FiremanSpeedFactor,
        };
        if (!vehicle && c.GetFloat("CAPACITIES_FIRE_ATTACK", "FireAttackPower", 0) > 0)
            u.Nozzle = Extinguisher.FromClass(classRel, c);
        // véhicules à lance (autopompe…) : [CAPACITIES_ACTIONS] CanAttackFire, portée [CAPACITIES_FIRE_ATTACK] (3000 contre 600 à pied)
        if (vehicle && c.GetBool("CAPACITIES_ACTIONS", "CanAttackFire") && c.GetFloat("CAPACITIES_FIRE_ATTACK", "FireAttackPower", 0) > 0
            && c.GetFloat("CAPACITIES_RESERVE", "ReserveMax", 0) > 0)
            u.Nozzle = Extinguisher.FromClass(classRel, c);
        u.Seats = vehicle ? c.GetInt("CAPACITIES_PROPS", "EmbarkCapacity", 0) : 0;
        u.AutoResupply = !vehicle && c.GetBool("CAPACITIES_PROPS", "AutomaticResupply");
        u.IsCanon = !vehicle && u.Nozzle != null && string.Equals(c.Get("UNIT", "Type")?.Trim('"', ' '), "CANON", StringComparison.OrdinalIgnoreCase);
        u.IsDog = !vehicle && string.Equals(c.Get("UNIT", "Type")?.Trim('"', ' '), "CYNOPHILE", StringComparison.OrdinalIgnoreCase);
        if (u.IsDog || (!vehicle && !c.GetBool("CAPACITIES_ACTIONS", "CanAttackFire", true)))
            u.Nozzle = null;   // le chien n'éteint pas (CanAttackFire=0)
        if (u.IsCanon)
        {
            u.Nozzle!.InfiniteReserve = true;   // pas de réserve à lui : l'eau vient du camion (Canon.cs)
            u.Speed = c.GetFloat("MOVING", "WalkSpeed", 3.5f) * FiremanSpeedFactor;   // il marche avec sa grosse lance (sc_walk_canon)
        }
        u.CanHeal = !vehicle && c.GetBool("CAPACITIES_ACTIONS", "CanHeal");
        u.CanBreak = !vehicle && c.GetBool("CAPACITIES_ACTIONS", "CanBreak");
        u.CanDemolish = c.GetBool("CAPACITIES_ACTIONS", "CanDemolish");
        u.CanExtricate = !vehicle && c.GetBool("CAPACITIES_ACTIONS", "CanExtricate");
        u.ExtricatePower = c.GetFloat("CAPACITIES_PROPS", "ExtricatePower", 20);
        u.CanUse = !vehicle && c.GetBool("CAPACITIES_ACTIONS", "CanUse");
        u.CanRepair = !vehicle && c.GetBool("CAPACITIES_ACTIONS", "CanRepair");
        u.CanGrapple = !vehicle && c.GetBool("CAPACITIES_ACTIONS", "CanGrapplin");
        u.CanLadder = !vehicle && c.GetBool("CAPACITIES_ACTIONS", "CanLadder");
        u.IsBoat = vehicle && (string.Equals(c.Get("UNIT", "Type")?.Trim('"', ' '), "FIREBOAT", StringComparison.OrdinalIgnoreCase) || c.GetBool("MOVING", "NoGroundContact"));
        u.IsAerial = vehicle && Enumerable.Range(1, 9).Any(i => string.Equals(c.Get("CAPACITIES_ACTIONS", $"Action{i}")?.Trim('"', ' '), "LADDER", StringComparison.OrdinalIgnoreCase));
        if (u.IsAerial)
            SetupLadder(u, c, gfx);
        u.IsWaterTower = u.IsAerial && (string.Equals(c.Get("UNIT", "Type")?.Trim('"', ' '), "WATERTOWER", StringComparison.OrdinalIgnoreCase) || c.GetBool("CAPACITIES_ACTIONS", "CanAttackFire"));
        if (u.IsWaterTower)
            SetupWaterTower(u, c);
        u.ClimbSpeed = c.GetFloat("MOVING", "ClimbGrapplinSpeed", c.GetFloat("CAPACITIES_PROPS", "ClimbGrapplinSpeed", 133));
        u.BreakPower = c.GetFloat("CAPACITIES_PROPS", "BreakPower", 1);
        u.IsAmbulance = vehicle && classRel.Contains("ambulance", StringComparison.OrdinalIgnoreCase);
        if (vehicle)
        {
            // marche arrière et braquage de la classe : BackwardSpeed (5 par défaut, 8 l'ambulance) ;
            // TurnSpeed (7 par défaut → rayon de 8 m ; plus grand = braque plus court : 8,5 l'ambulance → 6,6 m)
            u.BackSpeed = c.GetFloat("MOVING", "BackwardSpeed", 5) * VehicleSpeedFactor;
            u.Accel = c.GetFloat("MOVING", "AcceleratePower", 8) * VehicleAccelFactor;
            u.Decel = c.GetFloat("MOVING", "DeceleratePower", 6) * VehicleAccelFactor;
            u.TurnRadius = TurnRadiusRef * 7f / MathF.Max(1f, c.GetFloat("MOVING", "TurnSpeed", 7));
            u.ResupplyPower = c.GetFloat("CAPACITIES_RESERVE", "ResupplyPower", 0);
            u.SupplyAgent = (c.Get("CAPACITIES_RESERVE", "FireAgent") ?? "WATER").Trim('"', ' ').ToUpperInvariant();
            // ravitaillement des autres véhicules : seulement ceux qui ont un rayon d'action (citernes, bateau-pompe)
            u.SupplyRadius = c.GetFloat("CAPACITIES_RESERVE", "ResupplyRadius", 0);
            u.VehicleSupplyPower = u.SupplyRadius > 0 ? u.ResupplyPower : 0;
            string? utype = c.Get("UNIT", "Type")?.Trim('"', ' ').ToUpperInvariant();
            u.IsTanker = utype is "TANKER" or "FOAMTANKER";
            if (u.IsTanker)
            {
                u.ResupplyPower = 0;   // les pompiers à pied ne s'y remplissent pas
                if (u.Nozzle == null && !c.GetBool("CAPACITIES_RESERVE", "InfiniteReserve"))
                {
                    u.TankMax = c.GetFloat("CAPACITIES_RESERVE", "ReserveMax", 0);
                    u.TankReserve = MathF.Min(u.TankMax, c.GetFloat("CAPACITIES_RESERVE", "ReservePoints", u.TankMax));
                }
            }
        }
        u.StaminaMax = c.GetFloat("CAPACITIES_PROPS", "StaminaMax", 100);
        u.Stamina = MathF.Min(u.StaminaMax, c.GetFloat("CAPACITIES_PROPS", "StaminaPoints", u.StaminaMax));
        u.StaminaRegen = c.GetFloat("CAPACITIES_PROPS", "StaminaRegen", 0.5f);
        u.HeatCoef = c.GetFloat("CAPACITIES_PROPS", "HeatStaminaCoef", 1);
        u.SmokeCoef = c.GetFloat("CAPACITIES_PROPS", "SmokeStaminaCoef", 0.7f);
        u.LifeMax = c.GetFloat("DAMAGE", "MaxLifePoints", 500);
        u.Life = MathF.Min(u.LifeMax, c.GetFloat("DAMAGE", "LifePoints", u.LifeMax));
        u.FireDamageCoef = c.GetFloat("DAMAGE", "FireDamageCoef", vehicle ? 3 : 5);
        // pompier ignifugé ([CAPACITIES_PROPS] FIRE_RESISTANCE_MAX = 30, [DAMAGE] FIREDAMAGECOEF = 1 contre 5 pour un pompier) :
        // la chaleur l'épuise 5 fois moins vite
        if (!vehicle && c.GetFloat("CAPACITIES_PROPS", "FIRE_RESISTANCE_MAX", 0) > 0)
            u.SuitHeatCoef = Math.Clamp(u.FireDamageCoef / FiremanFireDamageCoef, 0, 1);
        u.HealsUnits = vehicle && c.GetBool("CAPACITIES_ACTIONS", "CanHeal");
        u.Icon = c.Get("UNIT", "Icon")?.Trim('"', ' ');
        // bruits du véhicule ([VEHICLE_SOUND] de la classe, celle de sa nationalité : SIREN_FRA_01, SIREN_UK_03…)
        if (vehicle)
        {
            static string? Label(string? s) => string.IsNullOrWhiteSpace(s) || float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) ? null : s;
            u.RunSound = Label(c.Get("VEHICLE_SOUND", "RUN")?.Trim('"', ' '));
            // (klaxon du bulldozer, du bateau-pompe, du camion bonus : pas une sirène à faire tourner pendant tout le trajet)
            u.SirenSound = Label(c.Get("VEHICLE_SOUND", "SIREN")?.Trim('"', ' ')) is { } siren && !siren.Contains("HORN", StringComparison.OrdinalIgnoreCase) ? siren : null;
            u.SirenVolume = c.GetFloat("VEHICLE_SOUND", "Siren_Volume", 100) / 100f;
        }
        Units.Add(u);
        return u;
    }

    /// <summary>Recherche de chemin et cases libres ou fermées : voir PathFind.cs.</summary>
    public PathFind Paths { get; }
    private float Ground(Vector2 p) => Paths.Ground(p);
    public float GroundZ(Vector2 p) => Paths.Ground(p);
    private bool Free(int cx, int cy) => Paths.Free(cx, cy);
    private bool FreeCell(int cx, int cy) => Paths.FreeCell(cx, cy);
    private Vector2 Walkable(Vector2 p) => Paths.Walkable(p);
    private List<Vector2> FindPath(Vector2 from, Vector2 to) => Paths.FindPath(from, to);
    private List<Vector2> FindPath(Vector2 from, Vector2 to, out bool partial) => Paths.FindPath(from, to, out partial);
    private bool LineFree(Vector2 a, Vector2 b, bool ignoreFire = false) => Paths.LineFree(a, b, ignoreFire);
    private bool IsGrappleCell(int i) => Paths.IsGrappleCell(i);
    public bool WalkableCell(int cx, int cy) => Paths.WalkableCell(cx, cy);
    public bool IsBlocked(int index) => Paths.IsBlocked(index);
    public bool IsVehicleBlocked(int index) => Paths.IsVehicleBlocked(index);
    public bool IsClosedDoor(int index) => Paths.IsClosedDoor(index);
    public int BlockedCells => Paths.BlockedCells;
    public Dictionary<int, string> BlockedBy => Paths.BlockedBy;

    /// <summary>Hauteur du sol dessiné (relevée sur les maillages du décor) : les unités et les civils s'y posent.</summary>
    public void SetSurface(float[] surface)
    {
        Paths.Surface = surface;
        foreach (Unit u in Units)
            u.Position.Z = Ground(new Vector2(u.Position.X, u.Position.Y));
        foreach (Civilian c in Civilians)
        {
            float z = Ground(new Vector2(c.Position.X, c.Position.Y));
            if (z > c.Position.Z)
                c.Position.Z = z;
        }
    }

    /// <summary>Murs du décor relevés sur les maillages : fermés, sauf autour des portes et sous les unités, civils et points de passage.</summary>
    public void AddWallCells(IEnumerable<int> cells) => Paths.AddWallCells(cells, Openings, KeepPoints().Concat(Units.Select(u => new Vector2(u.Position.X, u.Position.Y))));

    // points jamais fermés : civils et points de passage des scripts
    private IEnumerable<Vector2> KeepPoints() => Civilians.Select(c => new Vector2(c.Position.X, c.Position.Y)).Concat(_waypoints.Values).ToList();

    // véhicules garés (camions, ambulance) : obstacles mobiles, recalculés quand un véhicule s'arrête ou repart
    private string _vehSignature = "";
    private readonly Dictionary<string, (Vector2 min, Vector2 max)?> _vehBoxes = new(StringComparer.OrdinalIgnoreCase);
    private const float VehicleMargin = 40f;

    private (Vector2 min, Vector2 max)? ModelBox(string path)
    {
        if (_vehBoxes.TryGetValue(path, out (Vector2 min, Vector2 max)? box))
            return box;
        box = null;
        try
        {
            SceneAssets assets = _assets ??= new SceneAssets(_files);
            Vector2 mn = new(float.MaxValue), mx = new(float.MinValue);
            foreach (SceneAssets.Node part in assets.ModelParts(path))
            {
                Mesh m = assets.LoadMesh("", part.Mesh, "gfx").Mesh;
                for (int i = 0; i < m.VertexCount; i++)
                {
                    Vector3 v = Vector3.Transform(new Vector3(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]), part.World);
                    mn = Vector2.Min(mn, new(v.X, v.Y));
                    mx = Vector2.Max(mx, new(v.X, v.Y));
                }
            }
            if (mn.X < mx.X)
                box = (mn - new Vector2(VehicleMargin), mx + new Vector2(VehicleMargin));
        }
        catch { }
        return _vehBoxes[path] = box;
    }
    private SceneAssets? _assets;

    /// <summary>Le point (monde, à plat) est-il sur le véhicule (son gabarit au sol, plus une marge) ?</summary>
    public bool VehicleContains(Unit v, Vector2 p, float margin = 0)
    {
        if (ModelBox(v.Model) is not { } b)
            return Vector2.Distance(p, new(v.Position.X, v.Position.Y)) < 250 + margin;
        Matrix4x4 world = Matrix4x4.CreateRotationZ(v.Heading + MathF.PI / 2) * Matrix4x4.CreateTranslation(v.Position.X, v.Position.Y, 0);
        Matrix4x4.Invert(world, out Matrix4x4 inv);
        Vector3 l = Vector3.Transform(new Vector3(p, 0), inv);
        return l.X >= b.min.X - margin && l.X <= b.max.X + margin && l.Y >= b.min.Y - margin && l.Y <= b.max.Y + margin;
    }

    private void UpdateVehicleObstacles()
    {
        if (_s.Grid is not { } g)
            return;
        List<Unit> parked = Units.Where(v => v.IsVehicle && !v.Moving).ToList();
        string sig = string.Join(";", parked.Select(v => $"{v.Id}:{(int)v.Position.X / 50}:{(int)v.Position.Y / 50}:{(int)(v.Heading * 20)}"));
        if (sig == _vehSignature)
            return;
        _vehSignature = sig;
        if (Paths.VehicleBlocked.Length != g.Width * g.Height)
            Paths.VehicleBlocked = new int[g.Width * g.Height];
        else
            Array.Clear(Paths.VehicleBlocked);
        foreach (Unit v in parked)
        {
            if (ModelBox(v.Model) is not { } b)
                continue;
            // même orientation que l'affichage : les modèles regardent vers −Y
            Matrix4x4 world = Matrix4x4.CreateRotationZ(v.Heading + MathF.PI / 2) * Matrix4x4.CreateTranslation(v.Position.X, v.Position.Y, 0);
            Matrix4x4.Invert(world, out Matrix4x4 inv);
            List<Vector2> corners = new[] { b.min, new Vector2(b.max.X, b.min.Y), new Vector2(b.min.X, b.max.Y), b.max }
                .Select(p =>
                {
                    Vector3 w = Vector3.Transform(new Vector3(p, 0), world);
                    return new Vector2(w.X, w.Y);
                }).ToList();
            (int x0, int y0) = g.CellOf(new Vector3(corners.Min(p => p.X), corners.Min(p => p.Y), 0));
            (int x1, int y1) = g.CellOf(new Vector3(corners.Max(p => p.X), corners.Max(p => p.Y), 0));
            for (int cx = x0; cx <= x1; cx++)
                for (int cy = y0; cy <= y1; cy++)
                {
                    if (!g.Inside(cx, cy))
                        continue;
                    Vector3 l = Vector3.Transform(new Vector3((cx + 0.5f) * Grid.CellSize, (cy + 0.5f) * Grid.CellSize, 0), inv);
                    if (l.X >= b.min.X && l.X <= b.max.X && l.Y >= b.min.Y && l.Y <= b.max.Y)
                        Paths.VehicleBlocked[g.Index(cx, cy)] = v.Id;
                }
        }
        // les piétons en chemin recalculent leur trajet s'il traverse un véhicule qui vient de se garer
        foreach (Unit u in Units.Where(u => !u.IsVehicle && u.Moving))
        {
            List<Vector2> pts = new List<Vector2> { new(u.Position.X, u.Position.Y) };
            pts.AddRange(u.Path);
            bool crosses = false;
            for (int i = 0; i + 1 < pts.Count && !crosses; i++)
                crosses = !LineFree(pts[i], pts[i + 1], ignoreFire: true);
            if (!crosses)
                continue;
            Vector2 dest = pts[^1];
            List<Vector2> path = FindPath(pts[0], Walkable(dest));
            u.Path.Clear();
            foreach (Vector2 p in path)
                u.Path.Enqueue(p);
        }
    }

    /// <summary>
    /// Ordre de déplacement : chemin le plus court sur la grille (A*, 8 directions), puis lissé.
    /// Si le point n'est pas atteignable, l'unité va au point atteignable le plus proche.
    /// <paramref name="drive"/> : véhicule conduit par le joueur (gabarit, évite les autres véhicules garés) ;
    /// sinon un véhicule suit la route d'arrivée du script sans s'occuper des autres.
    /// </summary>
    public bool Order(Unit u, Vector2 dest, HotPoint? target = null, bool drive = false, bool refuseUnreachable = true)
    {
        if (u.Destroyed)
        {
            u.Status = "hors service (détruit)";
            return false;
        }
        if (u.Unconscious)
            return false;
        if (u.Fixed)
        {
            u.Status = "ne roule pas (posé par le script)";
            return false;
        }
        u.Target = target;
        u.ScriptDoors.Clear();
        u.LadderWait = null;   // nouvel ordre : il quitte la file d'attente d'une échelle (il y revient si son chemin y passe)
        if (PlayerOrder)
            u.ScriptHold = -1;   // le joueur reprend la main
        if (PlayerOrder && target == null)
            u.FireFocus = null;   // simple déplacement demandé par le joueur : l'IA reprend « le plus près de lui »
        u.Path.Clear();
        u.GoTo = null;
        u.GoToVehicle = null;
        u.HealTimer = 0;
        u.Opening = null;
        Paths.PathingVehicle = u.IsVehicle && !drive;
        Paths.PathingGrapple = false;   // le scout ne passe au grappin que sur ordre (clic droit sur la flèche) : sinon il prend le portail comme les autres
        Paths.PathingLadder = u.CanLadder && !u.IsVehicle;   // par les échelles dressées (Ladders.cs)
        Paths.PathingBoat = u.IsBoat;
        u.FakeStairs = null;
        Paths.PathingBreak = u.CanBreak;
        Paths.PathingUse = u.CanUse;
        u.PassingWindow = false;
        Paths.Clearance = u.IsVehicle && drive ? 1 : 0;
        Paths.SelfId = u.IsVehicle ? u.Id : 0;
        bool partial = false;
        // sur l'échelle : il finit d'abord de monter ou de descendre ; le chemin part de là où il arrivera
        // (dans un escalier : depuis l'autre bout)
        Vector2 from = u.OnLadder != null ? LadderExit(u, Walkable(dest)) : u.Stairs != null ? u.StairsTo : new Vector2(u.Position.X, u.Position.Y);
        try
        {
            List<Vector2> path = FindPath(from, Walkable(dest), out partial);
            foreach (Vector2 p in path)
                u.Path.Enqueue(p);
            TeleportAtDestination(u, dest);   // ordre sur une icône d'escalier : il le prend
        }
        finally
        {
            Paths.PathingVehicle = false;
            Paths.Clearance = 0;
            Paths.SelfId = 0;
            Paths.PathingGrapple = false;
            Paths.PathingLadder = false;
            Paths.PathingBoat = false;
            Paths.PathingBreak = false;
            Paths.PathingUse = false;
        }
        // point inatteignable : l'unité ne bouge pas et le dit (« VOCAL_IMPOSSIBLE_TO_ACCESS »)
        if (refuseUnreachable && (partial || (u.Path.Count == 0 && Vector2.Distance(from, dest) > 150)))
        {
            u.Path.Clear();
            u.Target = null;
            u.Status = "impossible d'y accéder";
            if (PlayerCommand)
                Sounds.Add(("VOCAL_IMPOSSIBLE_TO_ACCESS", null));   // voix seulement pour un ordre du joueur, pas pour l'IA
            return false;
        }
        u.Status = target != null ? $"va éteindre {target.Name}" : u.Path.Count == 0 ? "attend les ordres" : "en route";
        return true;
    }

    /// <summary>Sons à jouer (étiquette de la base de sons du jeu, position 3D ou null pour un son d'interface).</summary>
    public List<(string label, Vector3? pos)> Sounds { get; } = new();

    /// <summary>Clic droit avec un véhicule choisi : il roule jusqu'au point (ou au plus près), en contournant les obstacles.</summary>
    public bool Drive(Unit v, Vector2 dest)
    {
        // camion échelle dressée : il replie d'abord son échelle, puis il roule
        if (v.Ladder is { Placed: true } l)
        {
            l.Retracting = true;
            v.LadderNext = null;
            v.DriveNext = dest;
            v.Status = "replie l'échelle";
            return true;
        }
        if (v.Ladder != null)
            v.Ladder = null;   // en route vers une fenêtre : l'ordre de rouler l'annule
        return Order(v, dest, null, drive: true);
    }

    /// <summary>
    /// Attaque d'un feu : l'unité se place à la distance d'arrosage (portée × 0,72 + recul réglé à l'œil),
    /// en s'approchant ou en reculant, puis arrose.
    /// </summary>
    public void Attack(Unit u, HotPoint h)
    {
        AttackFire(u, h);
        // ordre du joueur : la suite (IA) se cherche autour de ce feu-là, puis du dernier éteint (FireFocus)
        if (PlayerOrder && u.Nozzle != null && !u.Destroyed)
            u.FireFocus = new Vector2(h.Position.X, h.Position.Y);
    }

    private void AttackFire(Unit u, HotPoint h)
    {
        if (u.Nozzle == null || u.Destroyed)
        {
            u.Status = u.Destroyed ? "hors service (détruit)" : "ne peut pas éteindre";
            return;
        }
        if (u.IsVehicle)
        {
            VehicleAttack(u, h);
            return;
        }
        if (u.IsCanon)
        {
            CanonAttack(u, h);
            return;
        }
        if (u.Carrying != null)
        {
            u.Status = "porte un civil : ne peut pas arroser";
            return;
        }
        Vector2 me = new Vector2(u.Position.X, u.Position.Y);
        Vector2 f = new Vector2(h.Position.X, h.Position.Y);
        float range = MathF.Max(150, u.Nozzle.RangeMax * 0.8f), d = Vector2.Distance(me, f);
        float want = range * 0.9f + ExtraStandoff;
        // feu tout proche : il ne recule que jusqu'à une partie de cette distance (AttackBackOff, réglage du jeu)
        float near = want * Math.Clamp(AttackBackOff, 0.1f, 1f);
        if (d < want)
            want = near;
        // il arrose d'où il est s'il est à bonne distance et qu'aucun mur (ou porte fermée) ne le sépare du feu
        if ((MathF.Abs(d - want) < 80 || (d >= near && d <= want + 80)) && !WallBetween(me, f) && SameLevel(me, f))
        {
            u.Path.Clear();
            u.Target = h;
            u.Status = $"arrose {h.Name}";
            return;
        }
        // chaque unité se place un peu à côté des autres, autour du feu
        Vector2 dir = d > 1 ? Vector2.Normalize(me - f) : Vector2.UnitX;
        float spread = ((u.Id * 7) % 9 - 4) * 0.18f;
        dir = new(dir.X * MathF.Cos(spread) - dir.Y * MathF.Sin(spread), dir.X * MathF.Sin(spread) + dir.Y * MathF.Cos(spread));
        // feu à l'intérieur : la place doit voir le feu sans mur entre les deux (sinon il arroserait à travers une
        // cloison, depuis dehors) ; on cherche autour du feu, de plus en plus près, la place libre la plus proche de lui
        foreach (Vector2 spot in SprayPlaces(f, dir, want, near, me).Take(6))
            if (Order(u, spot, h))
                return;
        // feu à un étage où l'on ne peut pas monter (ni escalier ni échelle) : refusé
        if (!SameLevel(f + dir * want, f))
        {
            u.Path.Clear();
            u.Target = null;
            u.Status = $"{h.Name} est à un autre étage : inaccessible";
            if (PlayerCommand)
                Sounds.Add(("VOCAL_IMPOSSIBLE_TO_ACCESS", null));
            return;
        }
        Order(u, f + dir * want, h);
    }

    /// <summary>
    /// Même niveau : le sol (plancher) en a et sous le feu b ne diffèrent pas d'un étage. Un feu posé haut (avion, camion)
    /// au-dessus d'un sol au même niveau compte comme le même niveau.
    /// </summary>
    public bool SameLevel(Vector2 a, Vector2 b) => MathF.Abs(Ground(a) - Ground(b)) < FloorStep;

    /// <summary>Différence de hauteur de sol à partir de laquelle on est à un autre étage.</summary>
    public const float FloorStep = 250f;

    /// <summary>
    /// Places d'où arroser le feu f : cases libres à la distance voulue (puis plus près), d'où l'on voit le feu sans mur
    /// ni porte fermée entre les deux ; les plus proches de l'unité d'abord (la direction de l'unité en premier).
    /// </summary>
    private IEnumerable<Vector2> SprayPlaces(Vector2 f, Vector2 dir, float want, float near, Vector2 me)
    {
        if (_s.Grid is not { } g)
            yield break;
        float baseA = MathF.Atan2(dir.Y, dir.X);
        foreach (float dist in new[] { want, (want + near) / 2, near, MathF.Max(150, near * 0.6f) })
        {
            List<(Vector2 p, float score)> found = new List<(Vector2 p, float score)>();
            for (int k = 0; k < 24; k++)
            {
                float a = baseA + (k % 2 == 0 ? 1 : -1) * ((k + 1) / 2) * MathF.Tau / 24;
                Vector2 p = f + new Vector2(MathF.Cos(a), MathF.Sin(a)) * dist;
                (int cx, int cy) = g.CellOf(new Vector3(p, 0));
                if (!Free(cx, cy) || WallBetween(p, f) || !SameLevel(p, f))
                    continue;
                found.Add((p, Vector2.Distance(me, p)));
            }
            foreach ((Vector2 p, float score) in found.OrderBy(x => x.score))
                yield return p;
        }
    }

    /// <summary>Mur, cloison ou porte fermée entre a et le feu b (les derniers 120 unités, autour du foyer, ne comptent pas :
    /// meubles, foyer posé contre un mur).</summary>
    public bool WallBetween(Vector2 a, Vector2 b)
    {
        if (_s.Grid is not { } g)
            return false;
        float len = Vector2.Distance(a, b);
        if (len < 130)
            return false;
        int n = (int)((len - 120) / 30);
        for (int i = 1; i <= n; i++)
        {
            Vector2 p = a + (b - a) * (i * 30 / len);
            (int cx, int cy) = g.CellOf(new Vector3(p, 0));
            if (!g.Inside(cx, cy))
                return true;
            int k = g.Index(cx, cy);
            if (Paths.IsBlocked(k) || Paths.IsClosedDoor(k) || Paths.IsWindowShut(k))
                return true;   // une vitre intacte arrête aussi le jet
        }
        return false;
    }

    /// <summary>Clic droit sur un véhicule : remplir la réserve (camion) ou y déposer les civils qui suivent (ambulance).</summary>
    public void GoToVehicle(Unit u, Unit vehicle)
    {
        if (u.IsVehicle || !vehicle.IsVehicle)
            return;
        Vector2 me = new Vector2(u.Position.X, u.Position.Y);
        Vector2 v = new Vector2(vehicle.Position.X, vehicle.Position.Y);
        Vector2 dir = Vector2.Distance(me, v) > 1 ? Vector2.Normalize(me - v) : Vector2.UnitX;
        if (!Order(u, v + dir * (VehicleReach * 0.6f)))
            return;
        u.GoToVehicle = vehicle;
        u.GoToVehicleAim = VehicleAim(vehicle);
        u.Status = vehicle.IsAmbulance ? "va à l'ambulance" : "va remplir sa réserve";
        // les civils qui le suivent l'accompagnent
    }

    /// <summary>Distance (cm) dont le but d'un véhicule doit changer pour que ceux qui le rejoignent refassent leur chemin.</summary>
    public const float VehicleFollowStep = 300f;

    // là où il faut rejoindre le véhicule : sa destination s'il roule, sinon sa place
    private static Vector2 VehicleAim(Unit v)
    {
        if (v.Moving)
            return v.Path.Last();
        return new Vector2(v.Position.X, v.Position.Y);
    }

    /// <summary>
    /// Le véhicule qu'une unité à pied rejoint a reçu l'ordre de se déplacer : elle repart vers sa nouvelle destination,
    /// en gardant ce qu'elle allait y faire (monter à bord, se recharger, se soigner, déposer des civils).
    /// </summary>
    private void FollowVehicle(Unit u)
    {
        if (u.GoToVehicle is not { } v || u.IsVehicle || u.OnBoard)
            return;
        if (v.Destroyed || v.Hidden)
            return;
        Vector2 aim = VehicleAim(v);
        if (Vector2.Distance(aim, u.GoToVehicleAim) < VehicleFollowStep)
            return;
        bool boarding = u.Boarding;
        string status = u.Status;
        Vector2 me = new Vector2(u.Position.X, u.Position.Y);
        Vector2 dir = Vector2.Distance(me, aim) > 1 ? Vector2.Normalize(me - aim) : Vector2.UnitX;
        if (!Order(u, aim + dir * (VehicleReach * 0.6f)))
        {
            u.Boarding = false;
            return;
        }
        u.GoToVehicle = v;
        u.GoToVehicleAim = aim;
        u.Boarding = boarding;
        u.Status = status;
    }

    private void Rescue(Unit u, float dt)
    {
        RescueCivilian(u, dt);   // voir CivilianAI.cs
        FollowVehicle(u);
        if (u.GoToVehicle is { } v && !u.Moving)
        {
            if (v.Moving)
                return;   // arrivé avant le véhicule : il l'attend à sa destination
            if (Vector3.Distance(u.Position, v.Position) > VehicleReach * 1.5f)
            {
                u.GoToVehicle = null;
                u.Status = "véhicule hors d'atteinte";
                return;
            }
            if (v.IsAmbulance)
            {
                int n = 0;
                foreach (Civilian civ in Civilians.Where(x => x.Leader == u && x.State == CivilianState.Conscious))
                    if (Vector3.Distance(civ.Position, v.Position) < VehicleReach * 2.5f)
                    {
                        Save(civ);
                        n++;
                    }
                if (n > 0)
                {
                    Log.Add($"{_s.Time,6:0} s : {n} civil(s) mis à l'abri dans l'ambulance ({Rescued} sauvé(s))");
                    Sounds.Add(("VOCAL_CIVILIAN_SAVED", null));
                }
                u.GoToVehicle = null;
                u.Status = "attend les ordres";
            }
            if (u.Boarding)
            {
                Board(u, v);
                return;
            }
            if (u.Nozzle != null && v.ResupplyPower > 0)
            {
                // la recharge elle-même se fait dans Resupply, tant qu'il reste près du camion
                if (!u.Status.StartsWith("vide son agent"))
                    u.Status = $"remplit sa réserve ({u.Nozzle.Reserve:0}/{u.Nozzle.ReserveMax:0})";
                if (u.Nozzle.Reserve >= u.Nozzle.ReserveMax && u.Nozzle.Agent == SupplyAgentOf(v))
                {
                    u.GoToVehicle = null;
                    u.Status = "réserve pleine";
                }
            }
            else
            {
                u.GoToVehicle = null;
                u.Status = "attend les ordres";
            }
        }
    }

    private void Move(Unit u, float dt)
    {
        if (u.IsVehicle)
        {
            VehicleMove(u, dt);
            return;
        }
        if (u.Climb != ClimbPhase.None)
        {
            UpdateClimb(u, dt);
            return;
        }   // au grappin : voir Grapple.cs
        u.LadderHold = false;
        if (u.OnLadder != null)
        {
            UpdateLadderClimb(u, dt);
            return;
        }   // sur l'échelle : voir Ladders.cs
        if (u.Path.Count > 0 && StartLadderIfNeeded(u, u.Path.Peek(), dt))
            return;
        if (u.Stairs != null)
        {
            UpdateTeleport(u, dt);
            return;
        }   // dans un escalier : voir Teleports.cs
        if (u.FakeStairs != null && u.Path.Count == 0)
            ReachFakeStairs(u);
        if (u.Path.Count > 0 && StartTeleportIfNeeded(u, u.Path.Peek()))
            return;
        if (u.Path.Count > 0 && StartClimbIfNeeded(u, u.Path.Peek()))
            return;
        float step = u.Speed * dt * (u.Carry == CarryPhase.Walking ? CarrySpeed : 1f);   // chargé d'un corps : plus lent
        while (step > 0 && u.Path.Count > 0)
        {
            Vector2 p = new Vector2(u.Position.X, u.Position.Y);
            Vector2 t = u.Path.Peek();
            Vector2 d = t - p;
            float len = d.Length();
            if (len > 1e-3f)
                u.Heading = MathF.Atan2(d.Y, d.X);
            if (len <= step)
            {
                p = t;
                u.Path.Dequeue();
                step -= len;
            }
            else
            {
                p += d / len * step;
                step = 0;
            }
            u.Position = new Vector3(p, Ground(p));
        }
        if (u.Path.Count == 0 && u.Status.StartsWith("en route"))
            u.Status = u.IsVehicle ? "garé" : "attend les ordres";
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
        if (u.IsVehicle || u == victim || !victim.Unconscious || u.Unconscious || u.OnBoard)
            return;
        if (!Order(u, new(victim.Position.X, victim.Position.Y)))
            return;
        u.ReviveTarget = victim;
        u.Status = "va ranimer son collègue";
    }

    private void Revive(Unit u, float dt)
    {
        if (u.ReviveTarget is not { } v || u.Moving)
            return;
        if (!v.Unconscious)
        {
            u.ReviveTarget = null;
            u.HealTimer = 0;
            return;
        }
        if (Vector3.Distance(u.Position, v.Position) > ReachDistance * 2)
        {
            u.ReviveTarget = null;
            u.Status = "collègue hors d'atteinte";
            return;
        }
        u.HealTimer += dt * (u.CanHeal ? 2 : 1);
        u.Status = $"ranime son collègue ({u.HealTimer / ReviveSeconds:P0})";
        u.Heading = MathF.Atan2(v.Position.Y - u.Position.Y, v.Position.X - u.Position.X);
        if (u.HealTimer < ReviveSeconds)
            return;
        v.Unconscious = false;
        v.Status = "attend les ordres";
        v.Stamina = MathF.Max(v.Stamina, v.StaminaMax * ReviveStamina);
        u.HealTimer = 0;
        u.ReviveTarget = null;
        u.Status = "attend les ordres";
        Log.Add($"{_s.Time,6:0} s : un pompier a été ranimé");
    }

    /// <summary>
    /// Recharge en eau : un pompier près d'un camion qui recharge (et à l'arrêt) se remplit peu à peu ; il doit y rester
    /// jusqu'au bout. Avec AutomaticResupply, il le fait de lui-même dès qu'il est à côté, sans ordre.
    /// </summary>
    private void Resupply(Unit u, float dt)
    {
        if (u.IsVehicle || u.OnBoard || u.Unconscious || u.Nozzle is not { InfiniteReserve: false } n || u.Moving)
            return;
        bool asked = u.GoToVehicle is { ResupplyPower: > 0 };
        if (!asked && (!u.AutoResupply || u.Target != null))
            return;
        // sur ordre : le camion demandé (il peut fournir un autre agent) ; de lui-même : un camion du même agent
        Unit? truck = asked && u.GoToVehicle is { Moving: false, Destroyed: false } gv && Vector3.Distance(gv.Position, u.Position) < VehicleReach * 1.5f ? gv
            : Units.Where(v => v.IsVehicle && v.ResupplyPower > 0 && !v.Moving && !v.Destroyed && SupplyAgentOf(v) == n.Agent && Vector3.Distance(v.Position, u.Position) < VehicleReach * 1.5f)
                .OrderBy(v => Vector3.Distance(v.Position, u.Position)).FirstOrDefault();
        if (truck == null)
            return;
        if (DrainForAgent(u, n, truck, dt))
            return;   // vide encore son ancien agent (voir Agents.cs)
        if (n.Reserve >= n.ReserveMax)
            return;
        float give = MathF.Min(n.ReserveMax - n.Reserve, truck.ResupplyPower * ResupplyScale * dt);
        // l'eau vient de la réserve du camion (qui se recharge à une bouche d'incendie ou auprès d'un autre camion)
        if (truck.Nozzle is { InfiniteReserve: false } tn)
        {
            give = MathF.Min(give, tn.Reserve);
            if (give <= 0)
            {
                if (asked)
                    u.Status = "le camion est vide : rechargez-le à une bouche d'incendie";
                return;
            }
            tn.Reserve -= give;
        }
        n.Reserve += give;
        if (n.Reserve > n.ReserveMax - 0.01f)
            n.Reserve = n.ReserveMax;   // valeur exacte : les scripts comparent avec « == » (tutoriel, étape 8)
        if (!asked && u.Status.StartsWith("attend"))
            u.Status = $"se recharge ({n.Reserve:0}/{n.ReserveMax:0})";
    }

    /// <summary>Clic droit d'un pompier sur un véhicule (réserve pleine) : il va y monter.</summary>
    public void BoardVehicle(Unit u, Unit vehicle)
    {
        if (u.IsVehicle || !vehicle.IsVehicle || u.OnBoard)
            return;
        if (vehicle.Destroyed)
        {
            u.Status = "ce véhicule est détruit";
            return;
        }
        if (vehicle.Seats <= 0)
        {
            u.Status = "ce véhicule n'a pas de place";
            return;
        }
        GoToVehicle(u, vehicle);
        if (u.GoToVehicle == vehicle)
        {
            u.Boarding = true;
            u.Status = "va monter dans le véhicule";
        }
    }

    /// <summary>Essais : monte tout de suite à bord.</summary>
    public void BoardNow(Unit u, Unit v) => Board(u, v);

    private void Board(Unit u, Unit v)
    {
        u.Boarding = false;
        u.GoToVehicle = null;
        if (v.Passengers.Count >= v.Seats)
        {
            u.Status = "véhicule plein";
            return;
        }
        v.Passengers.Add(u);
        u.InVehicle = v;
        foreach (Civilian civ in Civilians.Where(c => c.Leader == u))
            civ.Leader = null;
        Sounds.Add(("VEHICLE_EMBARK", v.Position));
        Log.Add($"{_s.Time,6:0} s : un pompier monte à bord ({v.Passengers.Count}/{v.Seats})");
    }

    /// <summary>Clic droit sur un véhicule choisi : tous ses passagers descendent autour de lui.</summary>
    public int UnloadPassengers(Unit v)
    {
        if (v.Passengers.Count == 0 || v.Moving)
            return 0;
        List<Vector2> spots = DropSpots(v, v.Passengers.Count);
        int n = 0;
        foreach (Unit u in v.Passengers)
        {
            Vector2 p = n < spots.Count ? spots[n] : new Vector2(v.Position.X, v.Position.Y);
            u.InVehicle = null;
            u.Position = new Vector3(p, Ground(p));
            u.Status = "attend les ordres";
            u.Heading = MathF.Atan2(p.Y - v.Position.Y, p.X - v.Position.X);
            n++;
        }
        v.Passengers.Clear();
        Log.Add($"{_s.Time,6:0} s : {n} pompier(s) descendent du véhicule");
        return n;
    }

    /// <summary>Feu électrique sous tension arrosé sur ordre : le pompier prend une petite décharge et s'arrête aussitôt ;
    /// un véhicule s'arrête simplement. Il faut d'abord couper le courant (armoire électrique de la carte).</summary>
    private void Electrocute(Unit u, HotPoint h)
    {
        u.Target = null;
        u.Status = "feu électrique : coupez d'abord le courant";
        if (u.IsVehicle)
            return;
        u.Life = MathF.Max(1, u.Life - u.LifeMax * ElectricShock);
        Sounds.Add(("SC_ELECTRICAL_DAMAGE", u.Position));
        Sounds.Add(("VOCAL_IMPOSSIBLE_TO_ATTACK_ELECTRICAL_FIRE", null));
        Log.Add($"{_s.Time,6:0} s : un pompier s'électrocute sur {h.Name} (courant non coupé)");
    }

    /// <summary>Camion à lance chargé d'eau : il peut arroser un feu électrique même sous tension (sur ordre ou de lui-même).</summary>
    public static bool WaterTruck(Unit u) => u.IsVehicle && u.Nozzle is { Agent: "WATER" };

    /// <summary>Part de la vie perdue par un pompier qui arrose un feu électrique sous tension.</summary>
    public const float ElectricShock = 0.08f;

    /// <summary>Unités visibles sur le terrain (pas celles qui sont à bord d'un véhicule).</summary>
    public List<Unit> Active => Units.Where(u => !u.OnBoard && !u.Hidden).ToList();

    private void Fight(Unit u, float dt)
    {
        if (u.Target == null || u.Moving || u.Nozzle == null || u.Unconscious || u.OnBoard || u.Destroyed)
            return;
        HotPoint h = u.Target;
        if (!h.Lit)
        {
            if (_reported.Add(h))
                Log.Add($"{_s.Time,6:0} s : {h.Name} éteint");
            // il finit le même foyer : le prochain feu se cherche autour de celui-ci
            if (u.FireFocus != null)
                u.FireFocus = new Vector2(h.Position.X, h.Position.Y);
            u.Target = null;
            u.Status = "attend les ordres";
            return;
        }
        _reported.Remove(h);
        if (h.Electrified && !WaterTruck(u))   // la lance d'un camion à eau peut arroser un feu électrique sous tension
        {
            Electrocute(u, h);
            return;
        }
        float want = MathF.Atan2(h.Position.Y - u.Position.Y, h.Position.X - u.Position.X);
        if (u.IsVehicle)
        {
            // lance à l'avant, orientable de ± XSprinklerAngleMax (0,9 rad) : au-delà, le camion manœuvre pour se tourner
            float diff = MathF.IEEERemainder(want - u.Heading, 2 * MathF.PI);
            float d = Vector2.Distance(new(u.Position.X, u.Position.Y), new(h.Position.X, h.Position.Y));
            if (d > u.Nozzle.RangeMax * 1.05f)
            {
                VehicleAttack(u, h);
                return;
            }
            if (VehicleAim(u, h, diff, dt))
                return;   // voir VehicleAI.cs
        }
        else
            u.Heading = want;
        if (u.IsCanon)
        {
            CanonSpray(u, h, dt);   // eau du camion, pas de message « réserve vide »
            return;
        }
        WarnWrongAgent(u, h);   // mauvais agent : accepté, efficacité ÷ 4, annoncé une fois (Agents.cs)
        // pompier relié à un camion proche : la réserve se remplit en continu
        if (!u.Nozzle.InfiniteReserve && u.Nozzle.Reserve <= 0)
        {
            u.Status = "réserve vide : à ramener au camion";
            u.Target = null;
            Sounds.Add(("VOCAL_RESERVE_EMPTY", null));
            return;
        }
        u.Nozzle.Spray(_s.Fire, h, dt, powered: !WaterTruck(u));   // camion à eau : le courant ne l'arrête pas
        u.Status = $"arrose {h.Name}";
    }
}
