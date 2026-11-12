using OpenFD.Fire;
using OpenFD.Formats;
using OpenFD.Script;

namespace OpenFD.Game;

/// <summary>
/// Le jeu tel que le voient les scripts de mission, branché sur la simulation du feu.
/// Les fonctions sans effet sur le feu (caméra, son, interface…) renvoient 0 et sont comptées.
/// </summary>
public sealed class MissionHost : IScriptHost
{
    private readonly Level _level;
    private readonly FireWorld _fire;
    private readonly Dictionary<string, LevelObject> _byName;
    private readonly Dictionary<uint, LevelObject> _byId;
    private readonly Random _rng = new(1);
    public double GameTime => _fire.Time;
    public ExplosionSystem? Explosions;            // objets explosifs (ENT_GetDamage / ENT_SetDamage)
    public int Difficulty;                         // 0 facile, 1 normal, 2 difficile
    public bool GroupAddSplits;                    // FIR_HPAddIntensity sur un groupe : réparti (true) ou ajouté à chacun (false)
    public List<string> FireLog { get; } = new();

    // ---- cinématiques : ce que les scripts demandent à la caméra, aux sous-titres et aux sons ----
    /// <summary>Mode cinématique (CAM_Cinematic(1) / CAM_ModeCinematic) : la caméra suit les points de passage, le joueur regarde.</summary>
    public bool Cinematic { get; private set; }
    public float CameraInertia { get; private set; }
    /// <summary>Ordres de caméra en attente : téléportation (duration 0) ou trajet vers le point de passage (durée en s).</summary>
    public List<(LevelObject waypoint, float duration, bool teleport, double at)> CameraOrders { get; } = new();
    /// <summary>Sous-titre affiché (clé de traduction, « &amp;Bank_cat1_m1 »), ou null.</summary>
    public string? Subtitle { get; private set; }
    /// <summary>Sons 2D demandés (étiquettes de la base de sons), à jouer par le jeu.</summary>
    public List<string> Sounds2D { get; } = new();
    /// <summary>Messages (PNL_AddMessage(voix, texte, x, y)) : étiquette de la voix (peut être vide), clé de traduction du texte, point de la carte.</summary>
    public List<(string vocal, string text, float x, float y)> Messages { get; } = new();
    /// <summary>PNL_StartCutscene (FD2) : petites vidéos du jeu (« data/video/street_houses.bik ») à passer pendant la partie.</summary>
    public List<string> Cutscenes { get; } = new();
    /// <summary>PNL_ClearAllMessages demandé depuis la dernière image : le panneau des messages se vide.</summary>
    public bool MessagesCleared { get; set; }
    /// <summary>Visibilité changée par les scripts (ENT_SetVisibility).</summary>
    public Dictionary<uint, bool> Visibility { get; } = new();
    /// <summary>Points des entités sans vie propre, modifiés par ENT_AddDamage (maisons de Residental…), 100 au départ.</summary>
    public Dictionary<uint, double> ExtraDamage { get; } = new();
    /// <summary>Mode de l'interface demandé par PANEL_SetInterfaceMode (« SCORING » : écran des scores).</summary>
    public string? InterfaceMode { get; private set; }
    /// <summary>LEV_RoofVisibility(maillage, 1/0) : toit d'un bâtiment retiré (1) ou remis (0) par le script.</summary>
    public List<(string mesh, bool hidden)> RoofRequests { get; } = new();

    // ---- objectifs, victoire, défaite ----
    public sealed class Objective
    {
        public required int Id { get; init; }
        public required string Text { get; init; }      // clé de traduction (« &Bank_Obj1 »)
        public bool Progress;                            // objectif chiffré (LEV_AddObjectiveProgress)
        public float Value, Min, Max;
        public int Status = 1;                           // 1 en cours, 2 réussi, 0 échoué
        public readonly List<(float x, float y)> Positions = new();
    }
    public List<Objective> Objectives { get; } = new();
    public bool ObjectivesVisible { get; private set; }
    public bool? Outcome { get; private set; }             // true : victoire, false : défaite, null : en cours
    public string? DebriefText { get; private set; }
    public List<(string label, double value, int medals)> Scoring { get; } = new();
    /// <summary>Unités, civils et véhicules (pour LEV_GetNumVictims, LEV_IsHealVehicleRemaining…).</summary>
    public UnitManager? Units;
    public List<(string key, bool success)> ObjectiveEvents { get; } = new();

    /// <summary>Fin forcée de la cinématique (le joueur la passe).</summary>
    public void EndCinematic()
    {
        Cinematic = false;
        Subtitle = null;
        CameraOrders.Clear();
    }
    public Dictionary<uint, bool> SwitchedStates { get; } = new();
    /// <summary>État logique des objets du décor changé par ENT_SwitchState (0 : S01).</summary>
    public Dictionary<uint, int> ObjectStates { get; } = new();
    /// <summary>Objets du décor déplacés par le script (ENT_SetPosition : le train de l'Eurotunnel…).</summary>
    public Dictionary<uint, System.Numerics.Vector2> MovedObjects { get; } = new();
    /// <summary>
    /// Animations lancées par le script (ANM_Play(entité, « SingleItem/si_… », « hold », boucle)) : nom (« SingleItem\\si_… »),
    /// heure de départ (temps de la partie) et lecture en boucle. Forest et Stadium : l'hélicoptère se pose et redécolle.
    /// </summary>
    public Dictionary<uint, (string anim, float start, bool loop)> PlayedAnims { get; } = new();
    /// <summary>Objets du décor tournés par le script (ENT_SetOrientation, en radians comme le niveau : la manche à air de Forest).</summary>
    public Dictionary<uint, float> TurnedObjects { get; } = new();
    /// <summary>Repère du vent du niveau (point de type « wind » : Forest, « wind_direction ») ; null s'il n'y en a pas.</summary>
    private readonly uint? _windId;

    public MissionHost(Level level, FireWorld fire)
    {
        _level = level;
        _fire = fire;
        _byId = level.Objects.ToDictionary(o => o.Id);
        _byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (LevelObject o in level.Objects)
            _byName.TryAdd(o.Name, o);
        // vent : sa direction est celle du repère (rotation autour de Z) ; sa force reste 1 (sans effet) tant que le
        // script ne l'a pas réglée (ENT_SetWindPower)
        if (level.Objects.FirstOrDefault(o => o.ClassPath.EndsWith("\\winddirection", StringComparison.OrdinalIgnoreCase) && o.HasTransform) is { } wind)
        {
            _windId = wind.Id;
            SetWindDirection(wind.Rotation.Z);
        }
    }

    /// <summary>La mission a un vent (repère « winddirection » : Forest, Mriya) ; sinon pas de vent (manche à air au repos).</summary>
    public bool HasWind => _windId != null;

    /// <summary>Le vent souffle vers cet angle (radians, 0 : vers +X) : le feu se propage plus vite dans ce sens.</summary>
    private void SetWindDirection(float angle) =>
        _fire.Settings.WindDirection = new System.Numerics.Vector3(MathF.Cos(angle), MathF.Sin(angle), 0);

    private List<HotPoint> HotPointsOf(long id)
    {
        List<HotPoint> res = new List<HotPoint>();
        void Walk(uint i, int depth)
        {
            if (_fire.ById.TryGetValue(i, out HotPoint? h))
            {
                res.Add(h);
                return;
            }
            if (depth < 6 && _byId.TryGetValue(i, out LevelObject? o))
                foreach (uint m in o.Members)
                    Walk(m, depth + 1);
            // groupe fait par le script (ENT_GroupCreate : FD1 Entrepot, les foyers de chaque hall)
            else if (depth < 6 && _groups.TryGetValue(i, out List<long>? g))
                foreach (long m in g)
                    Walk((uint)m, depth + 1);
        }
        Walk((uint)id, 0);
        return res;
    }
    /// <summary>
    /// Une cinématique lancée par une zone de déclenchement (dans la seconde qui suit son entrée) : cette zone est
    /// désactivée, pour qu'une cinématique déjà vue ne se rejoue pas à chaque passage.
    /// </summary>
    private void CinematicFromTrigger()
    {
        if (Units?.LastTrigger is { } t && GameTime - Units.LastTriggerTime < 1.5 && t.Enabled)
        {
            t.Enabled = false;
            FireLog.Add($"{GameTime,6:0.0} zone {t.Name} désactivée (cinématique déjà lancée)");
        }
    }

    private readonly Dictionary<long, List<long>> _groups = new();
    /// <summary>Noms des groupes créés par ENT_GroupCreate("nom"), retrouvés ensuite par ENT_FindByName (tutoriel : « group_soldat_8_1 »).</summary>
    private readonly Dictionary<string, long> _groupNames = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Entités d'un groupe du script (ENT_GroupCreate), ou l'entité elle-même.</summary>
    /// <summary>
    /// Groupe que le script modifie (ENT_GroupAddEntity, ENT_GroupRemEntity) : créé par lui, ou groupe du niveau copié à sa
    /// première retouche (College : « ElectricityDamage », « GroupHPLabo1 » perdent un membre à chaque court-circuit ou
    /// explosion de gaz ; sans cela le groupe ne se vide jamais et l'événement, avec son message, revient sans fin).
    /// </summary>
    private List<long>? EditableGroup(long id)
    {
        if (_groups.TryGetValue(id, out List<long>? l))
            return l;
        if (!_byId.TryGetValue((uint)id, out LevelObject? g) || g.Kind != EntityKind.Group)
            return null;
        l = g.Members.Select(m => (long)m).ToList();
        _groups[id] = l;
        return l;
    }

    private IEnumerable<long> Members(long id) => _groups.TryGetValue(id, out List<long>? l) ? l.ToList() : new List<long> { id };

    /// <summary>
    /// ENT_SetVisibility : le groupe lui-même (comme avant), et, pour un groupe du niveau, chacun de ses objets du décor
    /// (sous-groupes compris) ; un groupe créé par le script : ses membres ; l'entité seule sinon.
    /// </summary>
    private IEnumerable<long> VisibilityMembers(long id)
    {
        if (_groups.ContainsKey(id))
            return Members(id);
        List<long> all = new List<long> { id };
        if (Units?.ById(id) != null || !_byId.TryGetValue((uint)id, out LevelObject? g) || g.Kind != EntityKind.Group)
            return all;
        Stack<LevelObject> todo = new Stack<LevelObject>();
        todo.Push(g);
        HashSet<uint> seen = new HashSet<uint> { g.Id };
        while (todo.Count > 0)
            foreach (uint m in todo.Pop().Members)
            {
                if (!seen.Add(m) || !_byId.TryGetValue(m, out LevelObject? mo))
                    continue;
                if (mo.Kind == EntityKind.Group)
                    todo.Push(mo);
                else if (mo.Kind == EntityKind.Placeable)
                    all.Add(m);   // seulement les objets du décor : les unités et les civils d'un groupe restent comme avant
            }
        return all;
    }

    /// <summary>Membres d'un groupe créé par le script ou d'un groupe du niveau (« group_soldat_8_1 ») ; l'entité seule sinon.</summary>
    private IEnumerable<long> UnitMembers(long id)
    {
        if (_groups.TryGetValue(id, out List<long>? l))
            return l.ToList();
        if (Units?.ById(id) == null && _byId.TryGetValue((uint)id, out LevelObject? g) && g.Members.Length > 0)
            return g.Members.Select(m => (long)m).ToList();
        return new List<long> { id };
    }

    /// <summary>
    /// Points de vie d'une entité (ENT_GetDamage) : porte ou fenêtre, objet qui peut exploser, entité abîmée par
    /// ENT_AddDamage (100 au départ) ; un groupe (du niveau ou du script) : la somme de ses membres
    /// (TMI : « zcontainers », comparé à 100, 200 ou 300 selon la difficulté).
    /// </summary>
    private double DamageOf(long id, int depth)
    {
        if (Units?.Openings.FirstOrDefault(d => d.Id == (uint)id) is { } od)
            return od.State == OpeningState.Broken ? 0.0 : Math.Max(0.001, od.Life);
        if (Explosions?.LifeOf((uint)id) is { } life)
            return life;
        if (ExtraDamage.TryGetValue((uint)id, out double xd))
            return xd;
        if (depth < 4)
        {
            if (_groups.TryGetValue(id, out List<long>? sg))
                return sg.Sum(m => DamageOf(m, depth + 1));
            if (_byId.TryGetValue((uint)id, out LevelObject? lg) && lg.Members.Length > 0)
                return lg.Members.Sum(m => DamageOf(m, depth + 1));
        }
        return 100.0;
    }

    private System.Numerics.Vector3? PositionOf(long id)
    {
        if (Units?.ById(id) is { } u)
            return u.Position;
        if (MovedObjects.TryGetValue((uint)id, out System.Numerics.Vector2 mv) && _byId.TryGetValue((uint)id, out LevelObject? mo))
            return new(mv, mo.Position.Z);
        if (Units?.Civilians.FirstOrDefault(c => c.Id == id) is { } c)
            return c.Position;
        return _byId.TryGetValue((uint)id, out LevelObject? o) ? o.Position : null;
    }

    // propriétés des objets lues ou changées par les scripts : état des portes, déclencheurs, points de vie
    private void SetProperty(long id, string comp, string prop, Value v)
    {
        if (Units == null)
            return;
        if (comp.Equals("LOGICAL_STATE", StringComparison.OrdinalIgnoreCase) && prop.Equals("CurrentState", StringComparison.OrdinalIgnoreCase)
            && Units.Openings.FirstOrDefault(d => d.Id == id) is { } door)
        {
            OpeningState want = v.AsText.Equals("Open", StringComparison.OrdinalIgnoreCase) ? OpeningState.Open : OpeningState.Closed;
            if (door.State != OpeningState.Broken)
                Units.SetOpeningState(door, want);
            return;
        }
        if (comp.Equals("trigger", StringComparison.OrdinalIgnoreCase) && Units.Triggers.FirstOrDefault(t => t.Id == id) is { } trg)
        {
            if (prop.Equals("ON_ENTER", StringComparison.OrdinalIgnoreCase))
                trg.Script = v.AsText;
            else if (prop.Equals("ENABLED", StringComparison.OrdinalIgnoreCase))
            {
                trg.Enabled = v.AsInt != 0;
                trg.Inside.Clear();
            }
        }
    }

    private Value? GetProperty(long id, string comp, string prop)
    {
        if (Units?.Openings.FirstOrDefault(d => d.Id == id) is { } door)
        {
            if (prop.Equals("CurrentState", StringComparison.OrdinalIgnoreCase))
                return Value.T(door.State == OpeningState.Closed ? "Close" : "Open");
            if (prop.Equals("LIFEPOINTS", StringComparison.OrdinalIgnoreCase))
                return Value.F(door.State == OpeningState.Broken ? 0 : Math.Max(door.Life, 0.001));
        }
        if (prop.Equals("LIFEPOINTS", StringComparison.OrdinalIgnoreCase))
            return Value.F(Explosions?.LifeOf((uint)id) ?? 100.0);
        return null;
    }

    private Objective? Obj(long id) => Objectives.FirstOrDefault(o => o.Id == id);
    private string NameOf(long id) => _byId.TryGetValue((uint)id, out LevelObject? o) ? o.Name : id.ToString();

    public Value? Call(string name, IReadOnlyList<Arg> a)
    {
        Value V(int i) => i < a.Count ? a[i].Value : Value.Zero;
        switch (name.ToUpperInvariant())
        {
            // ---------- entités ----------
            case "ENT_FINDBYNAME":
                return Value.I(Units?.ByScriptName(V(0).AsText) is { } su ? su.Id : _byName.TryGetValue(V(0).AsText, out LevelObject? o) ? o.Id
                    : _groupNames.TryGetValue(V(0).AsText, out long sg) ? sg : 0);
            case "ENT_GETTYPE": return Value.I(_byId.TryGetValue((uint)V(0).AsInt, out LevelObject? t) ? (int)t.Kind : 0);
            case "ENT_GETNAME":
                if (a.Count > 1)
                    a[1].Set(Value.T(NameOf(V(0).AsInt)));
                return Value.T(NameOf(V(0).AsInt));
            case "ENT_GROUPGETNBENTITIES":
                if (_groups.TryGetValue(V(0).AsInt, out List<long>? cg))
                    return Value.I(cg.Count);
                return Value.I(_byId.TryGetValue((uint)V(0).AsInt, out LevelObject? g) ? g.Members.Length : 0);
            case "ENT_GROUPGETENTITYAT":
                if (_groups.TryGetValue(V(0).AsInt, out List<long>? cg2))
                    return Value.I(V(1).AsInt >= 0 && V(1).AsInt < cg2.Count ? cg2[(int)V(1).AsInt] : 0);
                return Value.I(_byId.TryGetValue((uint)V(0).AsInt, out LevelObject? g2) && V(1).AsInt >= 0 && V(1).AsInt < g2.Members.Length ? g2.Members[V(1).AsInt] : 0);
            case "ENT_ISENTITYINGROUP":
                if (_groups.TryGetValue(V(1).AsInt, out List<long>? cg3))
                    return Value.I(cg3.Contains(V(0).AsInt) ? 1 : 0);
                return Value.I(_byId.TryGetValue((uint)V(1).AsInt, out LevelObject? g3) && g3.Members.Contains((uint)V(0).AsInt) ? 1 : 0);
            case "ENT_GETPOSITION":
                if (a.Count >= 3 && PositionOf(V(0).AsInt) is { } p)
                {
                    a[1].Set(Value.F(p.X));
                    a[2].Set(Value.F(p.Y));
                }
                return Value.Zero;
            case "ENT_GETZ": return Value.F(PositionOf(V(0).AsInt)?.Z ?? 0);
            case "ENT_GETDAMAGE": return Value.F(DamageOf(V(0).AsInt, 0));
            case "ENT_SETDAMAGE":
                if (Explosions?.SetLife((uint)V(0).AsInt, (float)V(1).AsNum, _fire, _fire.Time) == true)
                    FireLog.Add($"{GameTime,6:0.0} ENT_SetDamage {NameOf(V(0).AsInt)} {V(1).AsNum}");
                return Value.Zero;
            case "ENT_SWITCHSTATE":
                // porte ou portail (interrupteurs de Jail : SW1_open → ENT_SwitchState(porte)) : ouverte ↔ fermée
                if (Units?.Openings.FirstOrDefault(d => d.Id == (uint)V(0).AsInt) is { } sd)
                {
                    if (sd.State != OpeningState.Broken)
                        Units.SetOpeningState(sd, sd.State == OpeningState.Open ? OpeningState.Closed : OpeningState.Open);
                }
                else if (_byId.TryGetValue((uint)V(0).AsInt, out LevelObject? so) && Units?.StateAnims(so) is { Count: > 1 } states)
                {
                    // objet du décor à états (London : les gravats qui s'éboulent derrière le pompier, cinématique cat_3) :
                    // état suivant, et son animation jouée une fois (elle reste sur sa dernière image)
                    int next = ((ObjectStates.TryGetValue(so.Id, out int cur) ? cur : UnitManager.InitialState(so)) + 1) % states.Count;
                    ObjectStates[so.Id] = next;
                    if (states[next] is { } sa)
                    {
                        PlayedAnims[so.Id] = (sa, (float)GameTime, false);
                        Units?.SettleStateObject(so, sa);
                    }
                }
                SwitchedStates[(uint)V(0).AsInt] = !SwitchedStates.TryGetValue((uint)V(0).AsInt, out bool was) || !was;
                FireLog.Add($"{GameTime,6:0.0} ENT_SwitchState {NameOf(V(0).AsInt)}");
                return Value.Zero;

            // ---------- feu ----------
            case "FIR_HPENABLE":
                foreach (HotPoint h in HotPointsOf(V(0).AsInt))
                {
                    // feu électrique en train de brûler : « désactiver » coupe le courant (Paris : armoire électrique) ;
                    // il brûle encore, mais devient un feu ordinaire que l'on peut éteindre
                    if (h.Class.FireType == 'C' && V(1).AsInt == 0 && h.IsBurning)
                    {
                        h.Powered = false;
                        continue;
                    }
                    h.Enabled = V(1).AsInt != 0;
                    if (h.Enabled && h.Class.FireType == 'C')
                        h.Powered = true;
                }
                FireLog.Add($"{GameTime,6:0.0} FIR_HPEnable {NameOf(V(0).AsInt)} {V(1).AsInt}");
                return Value.Zero;
            case "FIR_HPISENABLE": return Value.I(HotPointsOf(V(0).AsInt).Count(h => h.Enabled));
            // (seulement les foyers allumés : un foyer consumé auquel le script a rajouté de l'intensité ne brûle pas ; Stadium
            // attend que tous ses foyers soient à 0 pour finir la mission)
            case "FIR_HPGETINTENSITY": return Value.F(HotPointsOf(V(0).AsInt).Where(h => h.Lit).Sum(h => (double)h.Intensity));
            case "FIR_HPGETACTIVEINTENSITY": return Value.F(HotPointsOf(V(0).AsInt).Where(h => h.IsBurning).Sum(h => (double)h.Intensity));
            case "FIR_HPADDINTENSITY":
            {
                List<HotPoint> hs = HotPointsOf(V(0).AsInt);
                float amount = (float)V(1).AsNum;
                if (GroupAddSplits && hs.Count > 1)
                    amount /= hs.Count;
                foreach (HotPoint h in hs)
                    _fire.AddIntensity(h, amount);
                FireLog.Add($"{GameTime,6:0.0} FIR_HPAddIntensity {NameOf(V(0).AsInt)} {V(1).AsNum} ({hs.Count} foyers)");
                return Value.Zero;
            }
            case "FIR_HPSETINTENSITY":
                foreach (HotPoint h in HotPointsOf(V(0).AsInt))
                {
                    h.SetIntensity((float)V(1).AsNum);
                    if (h.Intensity > 0 && !h.Lit)
                        _fire.AddIntensity(h, 0);
                }
                return Value.Zero;
            case "FIR_HPMULTGROWTHRATE":
                foreach (HotPoint h in HotPointsOf(V(0).AsInt))
                    h.GrowthMultiplier *= (float)V(1).AsNum;
                FireLog.Add($"{GameTime,6:0.0} FIR_HPMultGrowthRate {NameOf(V(0).AsInt)} {V(1).AsNum}");
                return Value.Zero;
            case "FIR_HPSETGROWTHRATE":
                foreach (HotPoint h in HotPointsOf(V(0).AsInt))
                    h.GrowthMultiplier = h.Class.GrowthRate == 0 ? 1 : (float)V(1).AsNum / h.Class.GrowthRate;
                return Value.Zero;
            case "FIR_HPGETGROWTHRATE":
            {
                List<HotPoint> hs = HotPointsOf(V(0).AsInt);
                return Value.F(hs.Count > 0 ? hs[0].Class.GrowthRate * hs[0].GrowthMultiplier : 0);
            }
            case "FIR_SPGETINTENSITY": case "FIR_SPADDINTENSITY": return Value.F(0); // fumée : pas encore simulée

            // ---------- caméra, sous-titres, sons ----------
            case "CAM_CINEMATIC":
                Cinematic = V(0).AsInt != 0;
                if (Cinematic)
                    CinematicFromTrigger();
                else
                    Subtitle = null;
                return Value.Zero;
            case "CAM_MODECINEMATIC":
                Cinematic = true;
                CinematicFromTrigger();
                return Value.Zero;
            // ENT_Destroy : le script supprime une entité (London : la zone qui lance la cinématique du civil se détruit
            // elle-même, pour ne servir qu'une fois)
            case "ENT_DESTROY":
            {
                long id = V(0).AsInt;
                if (Units?.Triggers.FirstOrDefault(t => t.Id == id) is { } trg)
                {
                    trg.Enabled = false;
                    trg.Script = "";
                }
                else if (Units?.ById(id) is { } du)
                    Units.RemoveUnit(du);
                else if (Units?.CivilianById(id) is { } dc)
                    dc.Hidden = true;
                else
                    Visibility[(uint)id] = false;
                return Value.Zero;
            }
            case "CAM_MODEPLAY":
                Cinematic = false;
                Subtitle = null;
                return Value.Zero;   // fin de cinématique : son dernier sous-titre disparaît
            case "CAM_SETINERTIA":
                CameraInertia = (float)V(0).AsNum;
                return Value.Zero;
            case "CAM_GETINERTIA": return Value.F(CameraInertia);
            case "CAM_TELEPORTTOWAYPOINT":
                if (_byId.TryGetValue((uint)V(0).AsInt, out LevelObject? wt))
                    CameraOrders.Add((wt, 0, true, GameTime));
                return Value.Zero;
            case "CAM_CINEMATICSETTOWAYPOINT": case "CAM_SETTOWAYPOINT":
                if (_byId.TryGetValue((uint)V(0).AsInt, out LevelObject? wg))
                    CameraOrders.Add((wg, (float)Math.Max(0, V(1).AsNum), false, GameTime));
                return Value.Zero;
            case "LEV_ADDOBJECTIVEBOOLEAN":
            {
                Objective ob = new Objective { Id = Objectives.Count, Text = V(0).AsText };
                Objectives.Add(ob);
                ObjectiveEvents.Add((ob.Text, true));
                return Value.I(ob.Id);
            }
            case "LEV_ADDOBJECTIVEPROGRESS": case "LEV_ADDOBJECTIVEITERATE":
            {
                Objective op2 = new Objective { Id = Objectives.Count, Text = V(0).AsText, Progress = true, Min = (float)V(1).AsNum, Max = (float)V(2).AsNum, Value = (float)V(1).AsNum };
                if (name.Equals("LEV_AddObjectiveIterate", StringComparison.OrdinalIgnoreCase))
                {
                    op2.Min = 0;
                    op2.Max = (float)V(1).AsNum;
                    op2.Value = 0;
                }
                Objectives.Add(op2);
                ObjectiveEvents.Add((op2.Text, true));
                return Value.I(op2.Id);
            }
            case "LEV_UPDATEOBJECTIVE":
                if (Obj(V(0).AsInt) is { } ou)
                    ou.Value = (float)V(1).AsNum;
                return Value.Zero;
            case "LEV_VALIDATEOBJECTIVE":
                if (Obj(V(0).AsInt) is { } ov && ov.Status == 1)
                {
                    ov.Status = V(1).AsInt != 0 ? 2 : 0;
                    ObjectiveEvents.Add((ov.Text, ov.Status == 2));
                }
                return Value.Zero;
            case "LEV_GETOBJECTIVESTATUS": return Value.I(Obj(V(0).AsInt)?.Status ?? -1);
            case "LEV_SETOBJECTIVEPOS":
                // un seul repère par objectif : un nouvel appel le déplace (Mriya : True_Change le promène toutes les 1,5 s
                // entre les endroits où des civils attendent encore)
                if (Obj(V(0).AsInt) is { } op)
                {
                    op.Positions.Clear();
                    op.Positions.Add(((float)V(1).AsNum, (float)V(2).AsNum));
                }
                return Value.I(0);
            case "LEV_CLEARALLOBJECTIVES":
                Objectives.Clear();
                return Value.Zero;
            case "LEV_ACTIVATEOBJECTIVESPANEL":
                ObjectivesVisible = V(0).AsInt != 0;
                return Value.Zero;
            case "LEV_TRIGGERVICTORY":
                Outcome ??= true;
                return Value.Zero;
            case "LEV_TRIGGERDEFEAT":
                Outcome ??= false;
                return Value.Zero;
            case "LEV_SETDEBRIEFTEXT":
                DebriefText = V(0).AsText;
                return Value.Zero;
            case "LEV_ADDSCORINGLINE":
                Scoring.Add((V(0).AsText, V(1).AsNum, (int)V(2).AsInt));
                return Value.Zero;
            case "LEV_GETNUMVICTIMS": return Value.I(Units?.Victims ?? 0);
            case "LEV_GETNUMKILLEDFIREMEN": case "LEV_GETNUMKILLEDVEHICLES": return Value.I(0);
            case "LEV_GETFIREMENWOUNDS": return Value.I(Units?.Units.Sum(u => u.Wounds) ?? 0);
            case "LEV_GETVEHICLEDAMAGES": return Value.I(0);
            case "LEV_GETNUMFIREMEN": return Value.I(Units?.FiremenCount ?? 0);
            case "LEV_ISHEALVEHICLEREMAINING": return Value.I(Units?.HealVehicleRemaining != false ? 1 : 0);
            case "LEV_ISFIREMANSUPPLIERREMAINING": return Value.I(Units?.SupplierRemaining != false ? 1 : 0);
            case "PNL_PRINTSUBTITLE":
                Subtitle = V(0).AsText;
                return Value.Zero;
            case "PNL_CLEARSUBTITLE":
                Subtitle = null;
                return Value.Zero;
            case "PNL_ADDMESSAGE":
                Messages.Add((V(0).AsText, V(1).AsText, (float)V(2).AsNum, (float)V(3).AsNum));
                return Value.Zero;
            case "PNL_STARTCUTSCENE":
                Cutscenes.Add(V(0).AsText);
                return Value.Zero;
            case "PNL_CLEARALLMESSAGES":
                Messages.Clear();
                MessagesCleared = true;
                return Value.Zero;
            case "ENT_SETSTAMINA": return Value.Zero;
            case "SND_PLAYSOUND2D":
                Sounds2D.Add(V(0).AsText);
                return Value.Zero;
            // LEV_RoofVisibility(maillage, 0) : retire le toit (London : « Hide_Roof_Kino ») ; 1 : le remet
            case "LEV_ROOFVISIBILITY":
                RoofRequests.Add((V(0).AsText, V(1).AsInt == 0));
                return Value.Zero;
            case "ENT_ACTIVATETELEPORT":
                Units?.ActivateTeleport(V(0).AsInt, V(1).AsInt != 0);
                return Value.Zero;
            case "ENT_SETVISIBILITY":
                // un groupe du niveau (Street : « CCTrash », les débris du tramway renversé) : chacun de ses membres
                foreach (long id in VisibilityMembers(V(0).AsInt))
                    if (Units?.ById(id) is { } vu)
                        vu.Hidden = V(1).AsInt == 0;
                    else if (Units?.CivilianById(id) is { } vc)
                        vc.Hidden = V(1).AsInt == 0;
                    else
                        Visibility[(uint)id] = V(1).AsInt != 0;
                return Value.Zero;

            // ---------- unités commandées par le script ----------
            case "ENT_CREATEUNITPATH":
                return Value.I(Units?.SpawnScripted(V(0).AsText, V(1).AsText, new((float)V(2).AsNum, (float)V(3).AsNum, (float)V(4).AsNum)) is { } nu ? nu.Id : 0);
            case "ENT_SETPOSITION":
                foreach (long id in Members(V(0).AsInt))
                    if (Units?.ById(id) is { } tu)
                        Units.Teleport(tu, new((float)V(1).AsNum, (float)V(2).AsNum));
                    else if (Units?.CivilianById(id) is { } tc)
                    {
                        tc.Path.Clear();
                        tc.Position = new((float)V(1).AsNum, (float)V(2).AsNum, Units.GroundZ(new((float)V(1).AsNum, (float)V(2).AsNum)));
                    }
                    else
                        MovedObjects[(uint)id] = new((float)V(1).AsNum, (float)V(2).AsNum);
                return Value.Zero;
            case "ENT_SETORIENTATION":   // unités : degrés (0 : vers +X) ; objets du décor et repère du vent : radians
                foreach (long id in Members(V(0).AsInt))
                    if (Units?.ById(id) is { } ru0)
                        ru0.Heading = (float)(V(1).AsNum * Math.PI / 180);
                    else
                    {
                        TurnedObjects[(uint)id] = (float)V(1).AsNum;
                        if (_windId == id)
                            SetWindDirection((float)V(1).AsNum);
                    }
                return Value.Zero;
            case "ANM_PLAY":
                foreach (long id in Members(V(0).AsInt))
                {
                    string anim = V(1).AsText.Replace('/', '\\');
                    PlayedAnims[(uint)id] = (anim, (float)GameTime, a.Count > 3 && V(3).AsInt != 0);
                    // portail que le script ouvre en jouant son animation d'ouverture (Petrochemical, Renfort_2 : les rideaux
                    // factices bdoor2 et bdoor3 du garage, « si_Factory_store ») : ouvert, les véhicules sortent
                    if (Units?.Openings.FirstOrDefault(d => d.Id == (uint)id) is { } od && od.State == OpeningState.Closed
                        && od.OpenAnim != null && od.OpenAnim.Equals(anim, StringComparison.OrdinalIgnoreCase))
                        Units.SetOpeningState(od, OpeningState.Open);
                }
                return Value.Zero;
            case "ANM_STOP":
                foreach (long id in Members(V(0).AsInt))
                    PlayedAnims.Remove((uint)id);
                return Value.Zero;
            // ENT_SetWindPower(force) : force du vent (Forest : 1 à 1,5, puis 3 quand il tourne) ; 1 : sans effet
            case "ENT_SETWINDPOWER":
                _fire.Settings.WindPower = MathF.Max(1, (float)V(0).AsNum);
                return Value.Zero;
            case "ENT_GETORIENTATION":
            {
                double deg = Units?.ById(V(0).AsInt) is { } gu ? gu.Heading * 180 / Math.PI : _byId.TryGetValue((uint)V(0).AsInt, out LevelObject? go) ? go.Rotation.Z * 180 / Math.PI : 0;
                if (a.Count > 1)
                    a[1].Set(Value.F(deg));
                return Value.F(deg);
            }
            case "ENT_GETUNITTYPE": return Value.I(Units?.ById(V(0).AsInt) is { } yu && Units != null ? Units.UnitType(yu) : 0);
            case "ENT_SETRESERVE":
                foreach (long id in UnitMembers(V(0).AsInt))
                    if (Units?.ById(id) is { } ru)
                        UnitManager.SetReserve(ru, (float)V(1).AsNum);
                return Value.Zero;
            // sur un groupe : la somme des réserves (le tutoriel attend 750 pour trois sapeurs pleins)
            case "ENT_GETRESERVE":
                return Value.F(UnitMembers(V(0).AsInt).Sum(id => Units?.ById(id) is { } gr ? (double)UnitManager.ReserveOf(gr) : 0));
            case "ENT_GETNBPASSENGERS": return Value.I(Units?.ById(V(0).AsInt) is { } pv ? pv.Passengers.Count : 0);
            case "ENT_GROUPCREATE":
            {
                long gid = 0x7F000000 + _groups.Count;
                _groups[gid] = new();
                if (V(0).AsText.Length > 0)
                    _groupNames[V(0).AsText] = gid;
                return Value.I(gid);
            }
            case "ENT_GROUPADDENTITY":
                if (EditableGroup(V(0).AsInt) is { } gl)
                    gl.Add(V(1).AsInt);
                return Value.Zero;
            case "ENT_GROUPREMOVEENTITY":
            case "ENT_GROUPREMENTITY":
                EditableGroup(V(0).AsInt)?.Remove(V(1).AsInt);
                return Value.Zero;
            case "IA_CMDMOVETO":
                foreach (long id in Members(V(0).AsInt))
                    if (Units?.ById(id) is { } mu)
                        Units.ScriptMove(mu, new((float)V(1).AsNum, (float)V(2).AsNum));
                    else if (Units?.CivilianById(id) is { } mc)
                        Units.ScriptMoveCivilian(mc, new((float)V(1).AsNum, (float)V(2).AsNum), V(3).AsInt != 0);
                return Value.Zero;
            case "IA_CMDEMBARK":
                if (Units?.ById(V(1).AsInt) is { } ev)
                    foreach (long id in Members(V(0).AsInt))
                        if (Units.ById(id) is { } eu)
                            Units.ScriptEmbark(eu, ev);
                return Value.Zero;
            case "IA_CMDSTOP":
                foreach (long id in Members(V(0).AsInt))
                    if (Units?.ById(id) is { } su2)
                        Units.ScriptStop(su2);
                return Value.Zero;
            case "IA_CMDATTACKFIRE":
            {
                List<HotPoint> hs = HotPointsOf(V(1).AsInt);
                foreach (long id in Members(V(0).AsInt))
                    if (Units?.ById(id) is { } au && au.Nozzle != null && hs.Count > 0)
                        Units.Attack(au, hs.OrderByDescending(h => h.Intensity).First());
                return Value.Zero;
            }
            case "IA_CMDUSE":
                foreach (long id in Members(V(0).AsInt))
                    if (Units?.ById(id) is { } uu)
                        Units.ScriptUse(uu, (uint)V(1).AsInt);
                return Value.Zero;
            case "ENT_SETPROPERTYVALUESTRING":
                SetProperty(V(0).AsInt, V(1).AsText, V(2).AsText, V(3));
                return Value.Zero;
            case "ENT_SETPROPERTYVALUES32": case "ENT_SETPROPERTYVALUEF32":
                SetProperty(V(0).AsInt, V(1).AsText, V(2).AsText, V(3));
                return Value.Zero;
            case "ENT_GETPROPERTYVALUESTRING": return Value.T(GetProperty(V(0).AsInt, V(1).AsText, V(2).AsText)?.AsText ?? "");
            case "ENT_GETPROPERTYVALUEF32": case "ENT_GETPROPERTYVALUES32": return GetProperty(V(0).AsInt, V(1).AsText, V(2).AsText) ?? Value.Zero;
            case "ENT_GETCAPACITYTARGET": return Value.I(Units?.GetCapacity((uint)V(0).AsInt, V(1).AsText) == true ? 1 : 0);
            case "PNL_REMOVEALLFOCUS": return Value.Zero;
            case "ENT_SETCAPACITYTARGET":
                Units?.SetCapacity((uint)V(0).AsInt, V(1).AsText, V(2).AsInt != 0);
                return Value.Zero;
            case "LEV_MASKVISIBILITY":
                RoofRequests.Add((V(0).AsText, V(1).AsInt == 0));
                return Value.Zero;
            case "LEV_GRANTREINFORCEMENT":
                Units?.GrantReinforcement();
                return Value.Zero;
            case "LEV_CALLINITIALDISPATCH":
                Units?.CallInitialDispatch();
                return Value.Zero;

            // ---------- niveau et utilitaires ----------
            case "LEV_GETDIFFICULTYLEVEL": return Value.I(Difficulty);
            case "LEV_GETFIRESIZEPERCENT":
            {
                List<HotPoint> all = _fire.HotPoints;
                double max = all.Sum(h => (double)h.Class.IntensityMax);
                // une fraction (0 à 1) : les scripts des deux jeux écrivent « 100.0 * LEV_GetFireSizePercent() » pour le
                // pourcentage et « LEV_GetFireSizePercent() > 0.45 » pour un seuil (Street : renfort quand le feu dépasse 45 %)
                return Value.F(max <= 0 ? 0 : all.Where(h => h.IsBurning).Sum(h => (double)h.Intensity) / max);
            }
            case "UTL_GETRANDOM": return Value.F(_rng.NextDouble());
            case "UTL_GETDELTAGAMETIME": return Value.F(Session.MainLoopPeriod);

            // ---------- fonctions ajoutées après l'essai de toutes les missions (OpenFD.Tools toutesmissions) ----------
            // ENT_GetVisibility(entité) : 1 visible, 0 cachée (ENT_SetVisibility)
            case "ENT_GETVISIBILITY":
            {
                long id = V(0).AsInt;
                if (Units?.ById(id) is { } gu)
                    return Value.I(gu.Hidden ? 0 : 1);
                if (Units?.CivilianById(id) is { } gc)
                    return Value.I(gc.Hidden ? 0 : 1);
                return Value.I(Visibility.TryGetValue((uint)id, out bool vis) && !vis ? 0 : 1);
            }
            // ENT_GetUnitState(unité) : 4 = hors de combat (mort / véhicule détruit) ; 0 sinon
            case "ENT_GETUNITSTATE": return Value.I(Units?.ById(V(0).AsInt) is { Destroyed: true } ? 4 : 0);
            // ENT_GetStamina(pompier) : endurance en % (100 = pleine forme)
            case "ENT_GETSTAMINA": return Value.F(Units?.ById(V(0).AsInt) is { } stu && stu.StaminaMax > 0 ? Math.Round(100.0 * stu.Stamina / stu.StaminaMax, 3) : 100.0);
            // ENT_AddDamage(entité, valeur) : ajoute à ses points (négatif : l'abîme) ; véhicule : sa vie, pompier : son endurance
            case "ENT_ADDDAMAGE":
            {
                long id = V(0).AsInt;
                float v = (float)V(1).AsNum;
                if (Units?.ById(id) is { } du)
                {
                    if (du.IsVehicle)
                    {
                        if (v < 0)
                            Units.Damage(du, -v);
                        else if (!du.Destroyed)
                            du.Life = Math.Min(du.LifeMax, du.Life + v);
                    }
                    else
                        du.Stamina = Math.Clamp(du.Stamina + v, 0, du.StaminaMax);
                    return Value.Zero;
                }
                if (Explosions?.LifeOf((uint)id) is { } life)
                {
                    Explosions.SetLife((uint)id, life + v, _fire, _fire.Time);
                    return Value.Zero;
                }
                ExtraDamage[(uint)id] = (ExtraDamage.TryGetValue((uint)id, out double cur) ? cur : 100.0) + v;
                return Value.Zero;
            }
            // ENT_SetFireDamageCoef(entité, coef) : sensibilité au feu (0 : insensible)
            case "ENT_SETFIREDAMAGECOEF":
                if (Units?.ById(V(0).AsInt) is { } fu)
                {
                    if (fu.IsVehicle)
                        fu.FireDamageCoef = (float)V(1).AsNum;
                    else
                        fu.ScriptHeatCoef = (float)V(1).AsNum;
                }
                else
                    Explosions?.SetFireCoef((uint)V(0).AsInt, (float)V(1).AsNum);
                return Value.Zero;
            // ENT_GetState(porte ou portail) : « Open », « Close » ou « Broken »
            case "ENT_GETSTATE":
                if (Units?.Openings.FirstOrDefault(d => d.Id == (uint)V(0).AsInt) is { } st)
                    return Value.T(st.State switch { OpeningState.Open => "Open", OpeningState.Broken => "Broken", _ => "Close" });
                return Value.T(SwitchedStates.TryGetValue((uint)V(0).AsInt, out bool sw) && sw ? "Open" : "Close");
            // LEV_GrantSpecificReinforcement(n) : le renfort n (1, 2, 3…) est proposé au joueur, seul (Train : le renfort mousse)
            case "LEV_GRANTSPECIFICREINFORCEMENT":
                Units?.GrantSpecificReinforcement((int)V(0).AsInt);
                return Value.Zero;
            // LEV_CallReinforcement(n) : envoie le renfort n (1, 2, 3…)
            case "LEV_CALLREINFORCEMENT":
                Units?.CallReinforcement((int)V(0).AsInt);
                return Value.Zero;
            // LEV_GrantReinforcementSet(1, 1, 0, 0) : les renforts marqués 1 (ici le 1 et le 2) sont proposés ensemble ; le
            // joueur en choisit un (Suburb : W5 ou W6) ; l'appel suivant propose ceux qui restent
            case "LEV_GRANTREINFORCEMENTSET":
                Units?.GrantReinforcementSet(Enumerable.Range(0, a.Count).Where(i => V(i).AsInt != 0).Select(i => i + 1).ToList());
                return Value.Zero;
            // PANEL_SetInterfaceMode(« SCORING ») : écran des scores de fin (affiché par le jeu à la fin de mission)
            case "PANEL_SETINTERFACEMODE":
                InterfaceMode = V(0).AsText;
                return Value.Zero;
            case "FIR_HPSETGROWTHTIME":
                // durée de croissance (s) jusqu'à l'intensité maximale
                foreach (HotPoint h in HotPointsOf(V(0).AsInt))
                    if (V(1).AsNum > 0 && h.Class.GrowthRate > 0)
                        h.GrowthMultiplier = (float)(h.Class.IntensityMax / V(1).AsNum / h.Class.GrowthRate);
                return Value.Zero;
            case "SND_PLAYSTATICSOUND":
                Sounds2D.Add(V(0).AsText);
                return Value.Zero;
            case "SND_STOPSTATICSOUND": return Value.Zero;
            case "ENT_GETORIENTATIONTOENTITY":
            {
                System.Numerics.Vector3? p0 = PositionOf(V(0).AsInt);
                System.Numerics.Vector3? p1 = PositionOf(V(1).AsInt);
                double deg = p0 is { } q0 && p1 is { } q1 ? Math.Atan2(q1.Y - q0.Y, q1.X - q0.X) * 180 / Math.PI : 0;
                if (a.Count > 2)
                    a[2].Set(Value.F(deg));
                return Value.F(deg);
            }
            // aide aérienne (bombardier d'eau) : un passage accordé au joueur (AirSupport.cs)
            case "LEV_GRANTAIRSUPPORT":
                Units?.GrantAirSupport();
                return Value.Zero;
        }
        return null;
    }
}
