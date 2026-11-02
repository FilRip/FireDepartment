using FD3.Fire;
using FD3.Formats;
using FD3.Script;

namespace FD3.Game;

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
    /// <summary>Messages (PNL_AddMessage) : clés de traduction.</summary>
    public List<string> Messages { get; } = new();
    /// <summary>Visibilité changée par les scripts (ENT_SetVisibility).</summary>
    public Dictionary<uint, bool> Visibility { get; } = new();
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
    public void EndCinematic() { Cinematic = false; Subtitle = null; CameraOrders.Clear(); }
    public Dictionary<uint, bool> SwitchedStates { get; } = new();
    /// <summary>Objets du décor déplacés par le script (ENT_SetPosition : le train de l'Eurotunnel…).</summary>
    public Dictionary<uint, System.Numerics.Vector2> MovedObjects { get; } = new();

    public MissionHost(Level level, FireWorld fire)
    {
        _level = level; _fire = fire;
        _byId = level.Objects.ToDictionary(o => o.Id);
        _byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (var o in level.Objects) _byName.TryAdd(o.Name, o);
    }

    private List<HotPoint> HotPointsOf(long id)
    {
        var res = new List<HotPoint>();
        void Walk(uint i, int depth)
        {
            if (_fire.ById.TryGetValue(i, out var h)) { res.Add(h); return; }
            if (depth < 6 && _byId.TryGetValue(i, out var o)) foreach (var m in o.Members) Walk(m, depth + 1);
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
    /// <summary>Entités d'un groupe du script (ENT_GroupCreate), ou l'entité elle-même.</summary>
    private IEnumerable<long> Members(long id) => _groups.TryGetValue(id, out var l) ? l.ToList() : new List<long> { id };

    private System.Numerics.Vector3? PositionOf(long id)
    {
        if (Units?.ById(id) is { } u) return u.Position;
        if (MovedObjects.TryGetValue((uint)id, out var mv) && _byId.TryGetValue((uint)id, out var mo)) return new(mv, mo.Position.Z);
        if (Units?.Civilians.FirstOrDefault(c => c.Id == id) is { } c) return c.Position;
        return _byId.TryGetValue((uint)id, out var o) ? o.Position : null;
    }

    // propriétés des objets lues ou changées par les scripts : état des portes, déclencheurs, points de vie
    private void SetProperty(long id, string comp, string prop, Value v)
    {
        if (Units == null) return;
        if (comp.Equals("LOGICAL_STATE", StringComparison.OrdinalIgnoreCase) && prop.Equals("CurrentState", StringComparison.OrdinalIgnoreCase)
            && Units.Openings.FirstOrDefault(d => d.Id == id) is { } door)
        {
            var want = v.AsText.Equals("Open", StringComparison.OrdinalIgnoreCase) ? OpeningState.Open : OpeningState.Closed;
            if (door.State != OpeningState.Broken) Units.SetOpeningState(door, want);
            return;
        }
        if (comp.Equals("trigger", StringComparison.OrdinalIgnoreCase) && Units.Triggers.FirstOrDefault(t => t.Id == id) is { } trg)
        {
            if (prop.Equals("ON_ENTER", StringComparison.OrdinalIgnoreCase)) trg.Script = v.AsText;
            else if (prop.Equals("ENABLED", StringComparison.OrdinalIgnoreCase)) { trg.Enabled = v.AsInt != 0; trg.Inside.Clear(); }
        }
    }

    private Value? GetProperty(long id, string comp, string prop)
    {
        if (Units?.Openings.FirstOrDefault(d => d.Id == id) is { } door)
        {
            if (prop.Equals("CurrentState", StringComparison.OrdinalIgnoreCase)) return Value.T(door.State == OpeningState.Closed ? "Close" : "Open");
            if (prop.Equals("LIFEPOINTS", StringComparison.OrdinalIgnoreCase)) return Value.F(door.State == OpeningState.Broken ? 0 : Math.Max(door.Life, 0.001));
        }
        if (prop.Equals("LIFEPOINTS", StringComparison.OrdinalIgnoreCase)) return Value.F(Explosions?.LifeOf((uint)id) ?? 100.0);
        return null;
    }

    private Objective? Obj(long id) => Objectives.FirstOrDefault(o => o.Id == id);
    private string NameOf(long id) => _byId.TryGetValue((uint)id, out var o) ? o.Name : id.ToString();

    public Value? Call(string name, IReadOnlyList<Arg> a)
    {
        Value V(int i) => i < a.Count ? a[i].Value : Value.Zero;
        switch (name.ToUpperInvariant())
        {
            // ---------- entités ----------
            case "ENT_FINDBYNAME": return Value.I(Units?.ByScriptName(V(0).AsText) is { } su ? su.Id : _byName.TryGetValue(V(0).AsText, out var o) ? o.Id : 0);
            case "ENT_GETTYPE": return Value.I(_byId.TryGetValue((uint)V(0).AsInt, out var t) ? (int)t.Kind : 0);
            case "ENT_GETNAME": if (a.Count > 1) a[1].Set(Value.T(NameOf(V(0).AsInt))); return Value.T(NameOf(V(0).AsInt));
            case "ENT_GROUPGETNBENTITIES": return Value.I(_byId.TryGetValue((uint)V(0).AsInt, out var g) ? g.Members.Length : 0);
            case "ENT_GROUPGETENTITYAT":
                return Value.I(_byId.TryGetValue((uint)V(0).AsInt, out var g2) && V(1).AsInt >= 0 && V(1).AsInt < g2.Members.Length ? g2.Members[V(1).AsInt] : 0);
            case "ENT_ISENTITYINGROUP":
                return Value.I(_byId.TryGetValue((uint)V(1).AsInt, out var g3) && g3.Members.Contains((uint)V(0).AsInt) ? 1 : 0);
            case "ENT_GETPOSITION":
                if (a.Count >= 3 && PositionOf(V(0).AsInt) is { } p) { a[1].Set(Value.F(p.X)); a[2].Set(Value.F(p.Y)); }
                return Value.Zero;
            case "ENT_GETZ": return Value.F(PositionOf(V(0).AsInt)?.Z ?? 0);
            case "ENT_GETDAMAGE": return Value.F(Units?.Openings.FirstOrDefault(d => d.Id == (uint)V(0).AsInt) is { } od ? (od.State == OpeningState.Broken ? 0.0 : Math.Max(0.001, od.Life)) : Explosions?.LifeOf((uint)V(0).AsInt) ?? 100.0);
            case "ENT_SETDAMAGE":
                if (Explosions?.SetLife((uint)V(0).AsInt, (float)V(1).AsNum, _fire, _fire.Time) == true) FireLog.Add($"{GameTime,6:0.0} ENT_SetDamage {NameOf(V(0).AsInt)} {V(1).AsNum}");
                return Value.Zero;
            case "ENT_SWITCHSTATE": SwitchedStates[(uint)V(0).AsInt] = true; FireLog.Add($"{GameTime,6:0.0} ENT_SwitchState {NameOf(V(0).AsInt)}"); return Value.Zero;

            // ---------- feu ----------
            case "FIR_HPENABLE":
                foreach (var h in HotPointsOf(V(0).AsInt)) h.Enabled = V(1).AsInt != 0;
                FireLog.Add($"{GameTime,6:0.0} FIR_HPEnable {NameOf(V(0).AsInt)} {V(1).AsInt}");
                return Value.Zero;
            case "FIR_HPISENABLE": return Value.I(HotPointsOf(V(0).AsInt).Count(h => h.Enabled));
            case "FIR_HPGETINTENSITY": return Value.F(HotPointsOf(V(0).AsInt).Sum(h => (double)h.Intensity));
            case "FIR_HPGETACTIVEINTENSITY": return Value.F(HotPointsOf(V(0).AsInt).Where(h => h.IsBurning).Sum(h => (double)h.Intensity));
            case "FIR_HPADDINTENSITY":
            {
                var hs = HotPointsOf(V(0).AsInt);
                float amount = (float)V(1).AsNum;
                if (GroupAddSplits && hs.Count > 1) amount /= hs.Count;
                foreach (var h in hs) _fire.AddIntensity(h, amount);
                FireLog.Add($"{GameTime,6:0.0} FIR_HPAddIntensity {NameOf(V(0).AsInt)} {V(1).AsNum} ({hs.Count} foyers)");
                return Value.Zero;
            }
            case "FIR_HPSETINTENSITY":
                foreach (var h in HotPointsOf(V(0).AsInt)) { h.SetIntensity((float)V(1).AsNum); if (h.Intensity > 0 && !h.Lit) _fire.AddIntensity(h, 0); }
                return Value.Zero;
            case "FIR_HPMULTGROWTHRATE":
                foreach (var h in HotPointsOf(V(0).AsInt)) h.GrowthMultiplier *= (float)V(1).AsNum;
                FireLog.Add($"{GameTime,6:0.0} FIR_HPMultGrowthRate {NameOf(V(0).AsInt)} {V(1).AsNum}");
                return Value.Zero;
            case "FIR_HPSETGROWTHRATE":
                foreach (var h in HotPointsOf(V(0).AsInt)) h.GrowthMultiplier = h.Class.GrowthRate == 0 ? 1 : (float)V(1).AsNum / h.Class.GrowthRate;
                return Value.Zero;
            case "FIR_HPGETGROWTHRATE": { var hs = HotPointsOf(V(0).AsInt); return Value.F(hs.Count > 0 ? hs[0].Class.GrowthRate * hs[0].GrowthMultiplier : 0); }
            case "FIR_SPGETINTENSITY": case "FIR_SPADDINTENSITY": return Value.F(0); // fumée : pas encore simulée

            // ---------- caméra, sous-titres, sons ----------
            case "CAM_CINEMATIC": Cinematic = V(0).AsInt != 0; if (Cinematic) CinematicFromTrigger(); return Value.Zero;
            case "CAM_MODECINEMATIC": Cinematic = true; CinematicFromTrigger(); return Value.Zero;
            // ENT_Destroy : le script supprime une entité (London : la zone qui lance la cinématique du civil se détruit
            // elle-même, pour ne servir qu'une fois)
            case "ENT_DESTROY":
            {
                long id = V(0).AsInt;
                if (Units?.Triggers.FirstOrDefault(t => t.Id == id) is { } trg) { trg.Enabled = false; trg.Script = ""; }
                else if (Units?.ById(id) is { } du) Units.RemoveUnit(du);
                else if (Units?.CivilianById(id) is { } dc) dc.Hidden = true;
                else Visibility[(uint)id] = false;
                return Value.Zero;
            }
            case "CAM_MODEPLAY": Cinematic = false; return Value.Zero;
            case "CAM_SETINERTIA": CameraInertia = (float)V(0).AsNum; return Value.Zero;
            case "CAM_GETINERTIA": return Value.F(CameraInertia);
            case "CAM_TELEPORTTOWAYPOINT":
                if (_byId.TryGetValue((uint)V(0).AsInt, out var wt)) CameraOrders.Add((wt, 0, true, GameTime));
                return Value.Zero;
            case "CAM_CINEMATICSETTOWAYPOINT": case "CAM_SETTOWAYPOINT":
                if (_byId.TryGetValue((uint)V(0).AsInt, out var wg)) CameraOrders.Add((wg, (float)Math.Max(0, V(1).AsNum), false, GameTime));
                return Value.Zero;
            case "LEV_ADDOBJECTIVEBOOLEAN":
            {
                var ob = new Objective { Id = Objectives.Count, Text = V(0).AsText };
                Objectives.Add(ob); ObjectiveEvents.Add((ob.Text, true)); return Value.I(ob.Id);
            }
            case "LEV_ADDOBJECTIVEPROGRESS": case "LEV_ADDOBJECTIVEITERATE":
            {
                var op2 = new Objective { Id = Objectives.Count, Text = V(0).AsText, Progress = true, Min = (float)V(1).AsNum, Max = (float)V(2).AsNum, Value = (float)V(1).AsNum };
                if (name.Equals("LEV_AddObjectiveIterate", StringComparison.OrdinalIgnoreCase)) { op2.Min = 0; op2.Max = (float)V(1).AsNum; op2.Value = 0; }
                Objectives.Add(op2); ObjectiveEvents.Add((op2.Text, true)); return Value.I(op2.Id);
            }
            case "LEV_UPDATEOBJECTIVE":
                if (Obj(V(0).AsInt) is { } ou) ou.Value = (float)V(1).AsNum;
                return Value.Zero;
            case "LEV_VALIDATEOBJECTIVE":
                if (Obj(V(0).AsInt) is { } ov && ov.Status == 1) { ov.Status = V(1).AsInt != 0 ? 2 : 0; ObjectiveEvents.Add((ov.Text, ov.Status == 2)); }
                return Value.Zero;
            case "LEV_GETOBJECTIVESTATUS": return Value.I(Obj(V(0).AsInt)?.Status ?? -1);
            case "LEV_SETOBJECTIVEPOS":
                if (Obj(V(0).AsInt) is { } op) op.Positions.Add(((float)V(1).AsNum, (float)V(2).AsNum));
                return Value.I(0);
            case "LEV_CLEARALLOBJECTIVES": Objectives.Clear(); return Value.Zero;
            case "LEV_ACTIVATEOBJECTIVESPANEL": ObjectivesVisible = V(0).AsInt != 0; return Value.Zero;
            case "LEV_TRIGGERVICTORY": Outcome ??= true; return Value.Zero;
            case "LEV_TRIGGERDEFEAT": Outcome ??= false; return Value.Zero;
            case "LEV_SETDEBRIEFTEXT": DebriefText = V(0).AsText; return Value.Zero;
            case "LEV_ADDSCORINGLINE": Scoring.Add((V(0).AsText, V(1).AsNum, (int)V(2).AsInt)); return Value.Zero;
            case "LEV_GETNUMVICTIMS": return Value.I(Units?.Victims ?? 0);
            case "LEV_GETNUMKILLEDFIREMEN": case "LEV_GETNUMKILLEDVEHICLES": return Value.I(0);
            case "LEV_GETFIREMENWOUNDS": return Value.I(Units?.Units.Sum(u => u.Wounds) ?? 0);
            case "LEV_GETVEHICLEDAMAGES": return Value.I(0);
            case "LEV_GETNUMFIREMEN": return Value.I(Units?.FiremenCount ?? 0);
            case "LEV_ISHEALVEHICLEREMAINING": return Value.I(Units?.HealVehicleRemaining != false ? 1 : 0);
            case "LEV_ISFIREMANSUPPLIERREMAINING": return Value.I(Units?.SupplierRemaining != false ? 1 : 0);
            case "PNL_PRINTSUBTITLE": Subtitle = V(0).AsText; return Value.Zero;
            case "PNL_CLEARSUBTITLE": Subtitle = null; return Value.Zero;
            case "PNL_ADDMESSAGE": Messages.Add(V(0).AsText); return Value.Zero;
            case "PNL_CLEARALLMESSAGES": return Value.Zero;   // le journal du jeu garde ses lignes
            case "ENT_SETSTAMINA": return Value.Zero;
            case "SND_PLAYSOUND2D": Sounds2D.Add(V(0).AsText); return Value.Zero;
            // LEV_RoofVisibility(maillage, 0) : retire le toit (London : « Hide_Roof_Kino ») ; 1 : le remet
            case "LEV_ROOFVISIBILITY": RoofRequests.Add((V(0).AsText, V(1).AsInt == 0)); return Value.Zero;
            case "ENT_SETVISIBILITY":
                foreach (var id in Members(V(0).AsInt))
                    if (Units?.ById(id) is { } vu) vu.Hidden = V(1).AsInt == 0;
                    else if (Units?.CivilianById(id) is { } vc) vc.Hidden = V(1).AsInt == 0;
                    else Visibility[(uint)id] = V(1).AsInt != 0;
                return Value.Zero;

            // ---------- unités commandées par le script ----------
            case "ENT_CREATEUNITPATH":
                return Value.I(Units?.SpawnScripted(V(0).AsText, V(1).AsText, new((float)V(2).AsNum, (float)V(3).AsNum, (float)V(4).AsNum)) is { } nu ? nu.Id : 0);
            case "ENT_SETPOSITION":
                foreach (var id in Members(V(0).AsInt))
                    if (Units?.ById(id) is { } tu) Units.Teleport(tu, new((float)V(1).AsNum, (float)V(2).AsNum));
                    else if (Units?.CivilianById(id) is { } tc) { tc.Path.Clear(); tc.Position = new((float)V(1).AsNum, (float)V(2).AsNum, Units.GroundZ(new((float)V(1).AsNum, (float)V(2).AsNum))); }
                    else MovedObjects[(uint)id] = new((float)V(1).AsNum, (float)V(2).AsNum);
                return Value.Zero;
            case "ENT_SETORIENTATION":   // degrés (0 : vers +X)
                foreach (var id in Members(V(0).AsInt)) if (Units?.ById(id) is { } ru0) ru0.Heading = (float)(V(1).AsNum * Math.PI / 180);
                return Value.Zero;
            case "ENT_GETORIENTATION":
            {
                double deg = Units?.ById(V(0).AsInt) is { } gu ? gu.Heading * 180 / Math.PI : _byId.TryGetValue((uint)V(0).AsInt, out var go) ? go.Rotation.Z * 180 / Math.PI : 0;
                if (a.Count > 1) a[1].Set(Value.F(deg));
                return Value.F(deg);
            }
            case "ENT_GETUNITTYPE": return Value.I(Units?.ById(V(0).AsInt) is { } yu && Units != null ? Units.UnitType(yu) : 0);
            case "ENT_SETRESERVE":
                foreach (var id in Members(V(0).AsInt)) if (Units?.ById(id) is { } ru) UnitManager.SetReserve(ru, (float)V(1).AsNum);
                return Value.Zero;
            case "ENT_GETRESERVE": return Value.F(Units?.ById(V(0).AsInt) is { } gr ? UnitManager.ReserveOf(gr) : 0);
            case "ENT_GETNBPASSENGERS": return Value.I(Units?.ById(V(0).AsInt) is { } pv ? pv.Passengers.Count : 0);
            case "ENT_GROUPCREATE": { long gid = 0x7F000000 + _groups.Count; _groups[gid] = new(); return Value.I(gid); }
            case "ENT_GROUPADDENTITY": if (_groups.TryGetValue(V(0).AsInt, out var gl)) gl.Add(V(1).AsInt); return Value.Zero;
            case "ENT_GROUPREMOVEENTITY": if (_groups.TryGetValue(V(0).AsInt, out var gr2)) gr2.Remove(V(1).AsInt); return Value.Zero;
            case "IA_CMDMOVETO":
                foreach (var id in Members(V(0).AsInt))
                    if (Units?.ById(id) is { } mu) Units.ScriptMove(mu, new((float)V(1).AsNum, (float)V(2).AsNum));
                    else if (Units?.CivilianById(id) is { } mc) Units.ScriptMoveCivilian(mc, new((float)V(1).AsNum, (float)V(2).AsNum), V(3).AsInt != 0);
                return Value.Zero;
            case "IA_CMDEMBARK":
                if (Units?.ById(V(1).AsInt) is { } ev)
                    foreach (var id in Members(V(0).AsInt)) if (Units.ById(id) is { } eu) Units.ScriptEmbark(eu, ev);
                return Value.Zero;
            case "IA_CMDSTOP":
                foreach (var id in Members(V(0).AsInt)) if (Units?.ById(id) is { } su2) { su2.Path.Clear(); su2.Target = null; su2.Opening = null; }
                return Value.Zero;
            case "IA_CMDATTACKFIRE":
            {
                var hs = HotPointsOf(V(1).AsInt);
                foreach (var id in Members(V(0).AsInt))
                    if (Units?.ById(id) is { } au && au.Nozzle != null && hs.Count > 0)
                        Units.Attack(au, hs.OrderByDescending(h => h.Intensity).First());
                return Value.Zero;
            }
            case "IA_CMDUSE":
                foreach (var id in Members(V(0).AsInt)) if (Units?.ById(id) is { } uu) Units.ScriptUse(uu, (uint)V(1).AsInt);
                return Value.Zero;
            case "ENT_SETPROPERTYVALUESTRING": SetProperty(V(0).AsInt, V(1).AsText, V(2).AsText, V(3)); return Value.Zero;
            case "ENT_SETPROPERTYVALUES32": case "ENT_SETPROPERTYVALUEF32": SetProperty(V(0).AsInt, V(1).AsText, V(2).AsText, V(3)); return Value.Zero;
            case "ENT_GETPROPERTYVALUESTRING": return Value.T(GetProperty(V(0).AsInt, V(1).AsText, V(2).AsText)?.AsText ?? "");
            case "ENT_GETPROPERTYVALUEF32": case "ENT_GETPROPERTYVALUES32": return GetProperty(V(0).AsInt, V(1).AsText, V(2).AsText) ?? Value.Zero;
            case "ENT_GETCAPACITYTARGET": return Value.I(Units?.GetCapacity((uint)V(0).AsInt, V(1).AsText) == true ? 1 : 0);
            case "PNL_REMOVEALLFOCUS": return Value.Zero;
            case "ENT_SETCAPACITYTARGET": Units?.SetCapacity((uint)V(0).AsInt, V(1).AsText, V(2).AsInt != 0); return Value.Zero;
            case "LEV_MASKVISIBILITY": RoofRequests.Add((V(0).AsText, V(1).AsInt == 0)); return Value.Zero;
            case "LEV_GRANTREINFORCEMENT": Units?.GrantReinforcement(); return Value.Zero;
            case "LEV_CALLINITIALDISPATCH": Units?.CallInitialDispatch(); return Value.Zero;

            // ---------- niveau et utilitaires ----------
            case "LEV_GETDIFFICULTYLEVEL": return Value.I(Difficulty);
            case "LEV_GETFIRESIZEPERCENT":
            {
                var all = _fire.HotPoints; double max = all.Sum(h => (double)h.Class.IntensityMax);
                return Value.F(max <= 0 ? 0 : 100 * all.Where(h => h.IsBurning).Sum(h => (double)h.Intensity) / max);
            }
            case "UTL_GETRANDOM": return Value.F(_rng.NextDouble());
            case "UTL_GETDELTAGAMETIME": return Value.F(0.1);
        }
        return null;
    }
}
