using System.Numerics;
using FD3.Formats;

namespace FD3.Fire;

/// <summary>Réglages globaux du feu pour une mission (voir docs/MODELE_FEU.md).</summary>
public sealed class FireSettings
{
    public float TempoFactor = 1f;            // [DIFFICULTY_*] Tempo
    public float GrowthFactor = 1f;           // [DIFFICULTY_*] GrowthRate
    public float FireAttackPower = 1f;        // [DIFFICULTY_*] FireAttackPower (extinction, à venir)
    public float FireAttackCost = 1f;         // [DIFFICULTY_*] FireAttackCost (extinction, à venir)
    public float ElectricityStamina = 0.5f;    // [DIFFICULTY_*] ElectricityStamina (électrocution)
    public float NaturalEvaporationRate = 1.5f;
    public float FireEvaporationFactor = 0f;
    public Vector3 WindDirection = Vector3.UnitX;
    public float WindPower = 1f;              // 1 = pas d'effet

    public static FireSettings FromMission(ClassFile mis, string difficulty = "NORMAL")
    {
        var s = new FireSettings();
        var d = "DIFFICULTY_" + difficulty;
        s.TempoFactor = mis.GetFloat(d, "Tempo", 1f);
        s.GrowthFactor = mis.GetFloat(d, "GrowthRate", 1f);
        s.FireAttackPower = mis.GetFloat(d, "FireAttackPower", 1f);
        s.FireAttackCost = mis.GetFloat(d, "FireAttackCost", 1f);
        s.ElectricityStamina = mis.GetFloat(d, "ElectricityStamina", 0.5f);
        s.NaturalEvaporationRate = mis.GetFloat("WATER", "NaturalEvaporationRate", 1.5f);
        s.FireEvaporationFactor = mis.GetFloat("WATER", "FireEvaporationFactor", 0f);
        return s;
    }
}

/// <summary>Paramètres d'une classe de foyer (section HOTPOINT_PROPS et HOTPOINT_TAGS).</summary>
public sealed record HotPointClass(
    string Name, float InitialIntensity, float IntensityMax, float IntensityMin,
    float LifeDamageRange, float HeatRangeMax, float LightingRange, float LightingThreshold,
    float Tempo, float GrowthRate, float DeathRate, float GrowthTime,
    float SlopeFactor, bool UnderWindInfluence)
{
    public float SmokeGenerationRate { get; init; } = 20f;
    /// <summary>[HOTPOINT_DAMAGE] UnitLifeDamage : dégâts aux objets et aux unités à portée (par seconde, à pleine intensité).</summary>
    public float UnitLifeDamage { get; init; } = 2f;
    /// <summary>[HOTPOINT_DAMAGE] UnitHeatDamage : chaleur (endurance des pompiers) par seconde, à pleine intensité, au contact.</summary>
    public float UnitHeatDamage { get; init; } = 2f;
    /// <summary>Type de feu A (ordinaire), B (liquides : essence…), C (électrique), D (métaux).</summary>
    public char FireType { get; init; } = 'A';
    public static HotPointClass From(string name, ClassFile c)
    {
        const string P = "HOTPOINT_PROPS", T = "HOTPOINT_TAGS";
        return new HotPointClass(name,
            c.GetFloat(P, "InitialIntensity", 1), c.GetFloat(P, "IntensityMax", 100), c.GetFloat(P, "IntensityMin", -100),
            c.GetFloat(P, "LifeDamageRange", 100), c.GetFloat(P, "HeatRangeMax", 250), c.GetFloat(P, "LightingRange", 500),
            c.GetFloat(P, "LightingThreshold", 50), c.GetFloat(P, "Tempo", 15), c.GetFloat(P, "GrowthRate", 2.5f),
            c.GetFloat(P, "DeathRate", -1), c.GetFloat(P, "GrowthTime", 120),
            c.GetFloat(T, "SlopeFactor", 10), c.GetBool(T, "UnderWindInfluence", true))
        {
            SmokeGenerationRate = c.GetFloat(T, "SmokeGenerationRate", 20),
            UnitLifeDamage = c.GetFloat("HOTPOINT_DAMAGE", "UnitLifeDamage", 2),
            UnitHeatDamage = c.GetFloat("HOTPOINT_DAMAGE", "UnitHeatDamage", 2),
            FireType = (c.Get(T, "Type") ?? "FireType_A").Trim('"', ' ').ToUpperInvariant() is var t && t.Length > 0 ? t[^1] : 'A',
        };
    }
}

public enum HotPointState { Idle, Heating, Consumed, Growing, Declining }

public sealed class HotPoint
{
    public required uint Id { get; init; }
    public required string Name { get; init; }
    public required HotPointClass Class { get; init; }
    public required Vector3 Position { get; init; }

    public float Intensity;
    public bool Lit;
    public bool Enabled = true;
    public HotPointState State = HotPointState.Idle;
    public float HeatReceived;     // chaleur de la dernière mise à jour
    public float DelayedHeat;      // apportée par les liens à retardement, consommée à la mise à jour suivante
    public float HeatingTime;      // compteur de chauffe avant allumage
    public float BurnTime;         // temps passé à brûler
    public float GrowthMultiplier = 1f; // modifié par les scripts (FIR_HPMultGrowthRate)
    /// <summary>[HOTPOINT_TAGS] MakeBackdraft / SleepingThreshold (valeurs de la carte) : dans une pièce fermée, ce foyer
    /// « s'endort » faute d'air au-delà du seuil ; si on ouvre une porte, il se réveille d'un coup (backdraft).</summary>
    public bool MakeBackdraft;
    public float SleepingThreshold = 80f;
    public bool Sleeping;
    public float PendingAttack;    // eau/mousse reçue depuis la dernière mise à jour (§ 6)
    public readonly List<HotPoint> HeatSources = new();

    public bool IsBurning => Lit && Intensity > 0;

    /// <summary>Portée des dégâts (§ 3.5).</summary>
    public float DamageRange => Intensity > 0
        ? Class.LifeDamageRange + Intensity / Class.IntensityMax * (Class.HeatRangeMax - Class.LifeDamageRange)
        : Class.LifeDamageRange;

    public void SetIntensity(float value) => Intensity = Math.Clamp(value, Class.IntensityMin, Class.IntensityMax);
}

public enum LinkType { Neighbour = 0, Delayed = 1, HotToSmoke = 2 }

public sealed class FireLink
{
    public required HotPoint? A { get; init; }
    public required HotPoint? B { get; init; }
    public required LinkType Type { get; init; }
    public required int Way { get; init; }        // 0 = deux sens, 1 = A vers B, 2 = B vers A (sens 1/2 : hypothèse)
    public required float Tempo { get; init; }
    public required bool Active { get; init; }
    public required uint OpeningId { get; init; }
    public float Elapsed;
    public bool Pushing;

    public IEnumerable<(HotPoint src, HotPoint dst)> Directions()
    {
        if (A == null || B == null) yield break;
        if (Way is 0 or 1) yield return (A, B);
        if (Way is 0 or 2) yield return (B, A);
    }
}

/// <summary>Simulation du feu d'une mission. Pas de temps fixe ; aucune dépendance graphique.</summary>
public sealed class FireWorld
{
    public FireSettings Settings { get; }
    public List<HotPoint> HotPoints { get; } = new();
    public List<FireLink> Links { get; } = new();
    public Dictionary<uint, HotPoint> ById { get; } = new();
    public float Time { get; private set; }
    /// <summary>[hypothèse] Une porte ou fenêtre fermée bloque un lien qui la traverse.</summary>
    public bool ClosedOpeningsBlock;

    public FireWorld(FireSettings settings) => Settings = settings;

    public static FireWorld Load(Level level, GameFiles files, FireSettings settings)
    {
        var world = new FireWorld(settings);
        var classCache = new Dictionary<string, ClassFile>(StringComparer.OrdinalIgnoreCase);
        var hpClasses = new Dictionary<string, HotPointClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in level.Objects.Where(o => o.Kind == EntityKind.HotPoint && o.HasTransform))
        {
            if (!hpClasses.TryGetValue(o.ClassPath, out var hc))
            {
                // design\hotpoint\ground_l -> Design\Hotpoint\ground_l.class
                var rel = "Design\\" + o.ClassPath["design\\".Length..] + ".class";
                hc = HotPointClass.From(o.ClassName, ClassFile.Load(files, rel, classCache));
                hpClasses[o.ClassPath] = hc;
            }
            var inst = InstanceState(o, hc);
            var hp = new HotPoint { Id = o.Id, Name = o.Name, Class = inst?.cls ?? hc, Position = o.Position };
            if (BackdraftTags(o) is { } bd) { hp.MakeBackdraft = bd.make; hp.SleepingThreshold = bd.threshold; }
            if (inst is { } st)
            {
                // [confirmé par une sauvegarde] état initial stocké dans FE00 : allumé, intensité, temps de combustion
                hp.Lit = st.lit; hp.Intensity = st.current; hp.BurnTime = st.burnTime;
                hp.State = hp.Lit ? HotPointState.Growing : HotPointState.Idle;
            }
            else
            {
                hp.Intensity = SavedIntensity(o) ?? 0f;
                hp.Lit = hp.Intensity > 0;
                if (hp.Lit) hp.State = HotPointState.Growing;
            }
            world.HotPoints.Add(hp);
            world.ById[hp.Id] = hp;
        }
        foreach (var (a, b, data) in level.HotPointLinks)
        {
            if (data.Length < 26) continue;
            world.ById.TryGetValue(a, out var ha);
            world.ById.TryGetValue(b, out var hb);
            var link = new FireLink
            {
                A = ha, B = hb,
                OpeningId = BitConverter.ToUInt32(data, 0),
                Tempo = BitConverter.ToSingle(data, 4),
                Type = (LinkType)BitConverter.ToInt32(data, 13),
                Way = BitConverter.ToInt32(data, 17),
                Active = BitConverter.ToInt32(data, 21) != 0,
            };
            world.Links.Add(link);
        }
        return world;
    }

    /// <summary>
    /// État et paramètres propres à un foyer, stockés dans le composant FE00 (vérifié sur une sauvegarde) :
    /// début du bloc v3 = actif, allumé, intensité courante, intensité max ;
    /// bloc final de 96 octets = paramètres de l'instance (ils remplacent ceux de la classe) et temps de combustion.
    /// </summary>
    public static (bool lit, float current, float burnTime, HotPointClass cls)? InstanceState(LevelObject o, HotPointClass baseClass)
    {
        var c = o.GetComponent(0xFE00);
        if (c == null) return null;
        var b = o.ComponentBytes(c).ToArray();
        if (b.Length < 140 || !Chunks.IsBegin(b, 17) || b[21] != 3) return null;
        int h = 22;
        bool lit = b[h + 5] != 0;
        float cur = BitConverter.ToSingle(b, h + 10), max = BitConverter.ToSingle(b, h + 14);
        int last = LastIndexOf(b, b.Length), prev = LastIndexOf(b, last);
        if (last < 0 || prev < 0 || last - (prev + 4) != 96) return null;
        int t = prev + 4;
        float F(int off) => BitConverter.ToSingle(b, t + off);
        var cls = baseClass with
        {
            IntensityMax = max, IntensityMin = F(46), LifeDamageRange = F(34), HeatRangeMax = F(54),
            LightingRange = F(58), LightingThreshold = F(62), Tempo = F(66), GrowthRate = F(70),
            DeathRate = F(74), GrowthTime = F(78), SlopeFactor = F(83),
        };
        return (lit, cur, F(42), cls);
    }
    /// <summary>
    /// Bloc d'instance de 96 octets (voir InstanceState), début : octet 17 = MakeBackdraft, flottant 19 = SleepingThreshold
    /// (London : foyers de deux pièces de la banque, seuil 50 ; ailleurs 0 et 80, la valeur de la classe).
    /// </summary>
    public static (bool make, float threshold)? BackdraftTags(LevelObject o)
    {
        var c = o.GetComponent(0xFE00);
        if (c == null) return null;
        var b = o.ComponentBytes(c).ToArray();
        int last = LastIndexOf(b, b.Length), prev = LastIndexOf(b, last);
        if (last < 0 || prev < 0 || last - (prev + 4) != 96) return null;
        int t = prev + 4;
        float th = BitConverter.ToSingle(b, t + 19);
        return (b[t + 17] != 0, float.IsFinite(th) && th > 0 ? th : 80f);
    }

    private static int LastIndexOf(byte[] b, int before)
    {
        for (int i = before - 4; i >= 0; i--) if (Chunks.IsEnd(b, i)) return i;
        return -1;
    }

    /// <summary>Intensité sauvegardée dans le composant FD00 : premier flottant après le bloc d'état.</summary>
    public static float? SavedIntensity(LevelObject o)
    {
        var c = o.GetComponent(0xFD00);
        if (c == null) return null;
        var b = o.ComponentBytes(c);
        if (b.Length < 32 || !Chunks.IsBegin(b, 17) || !Chunks.IsBegin(b, 22)) return null;
        int e = Chunks.NodeEnd(b, 22);
        return BitConverter.ToSingle(b[e..]);
    }

    /// <summary>
    /// § 4.0 : voisinage par proximité, observé dans les vidéos du jeu (165 allumages sur 167
    /// sans lien) : un foyer allumé chauffe tous les foyers situés dans sa LightingRange.
    /// Si une grille est fournie, un mur (cellule infranchissable) entre les deux bloque la chaleur.
    /// </summary>
    /// <summary>Portée utilisée pour la proximité : celle de la cible (true) ou de la source (false).</summary>
    public bool ProximityUsesTargetRange;
    /// <summary>Distance 3D (true, comme le code du jeu) ou à plat (false).</summary>
    public bool Proximity3D;

    public void AddProximityNeighbours(Grid? walls = null, IReadOnlyDictionary<uint, string>? rooms = null)
    {
        foreach (var src in HotPoints)
        {
            foreach (var dst in HotPoints)
            {
                if (ReferenceEquals(src, dst)) continue;
                float range = ProximityUsesTargetRange ? dst.Class.LightingRange : src.Class.LightingRange;
                float r2 = range * range;
                // pièces : la chaleur ne traverse pas directement d'une pièce à l'autre (seulement par les liens)
                if (rooms != null && (rooms.TryGetValue(src.Id, out var ra) ? ra : "") != (rooms.TryGetValue(dst.Id, out var rb) ? rb : "")) continue;
                var dx = dst.Position.X - src.Position.X; var dy = dst.Position.Y - src.Position.Y;
                var dz = Proximity3D ? dst.Position.Z - src.Position.Z : 0f;
                if (dx * dx + dy * dy + dz * dz > r2) continue;
                if (walls != null && WallBetween(walls, src.Position, dst.Position)) continue;
                AddSource(dst, src);
            }
        }
    }

    public static bool WallBetween(Grid g, Vector3 a, Vector3 b)
    {
        var (ax, ay) = g.CellOf(a); var (bx, by) = g.CellOf(b);
        int n = Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay));
        for (int i = 1; i < n; i++)
        {
            int cx = ax + (int)MathF.Round((bx - ax) * i / (float)n), cy = ay + (int)MathF.Round((by - ay) * i / (float)n);
            if (g.Inside(cx, cy) && g.ZoneA[g.Index(cx, cy)] == 0) return true;
        }
        return false;
    }

    // § 4.1 : le voisinage vient des liens de type 0.
    public void BuildNeighbourhoods()
    {
        foreach (var l in Links.Where(l => l.Type == LinkType.Neighbour && l.A != null && l.B != null))
        {
            if (ClosedOpeningsBlock && l.OpeningId != 0) continue;
            if (l.Active)
                foreach (var (src, dst) in l.Directions()) AddSource(dst, src);
            else
            {
                float d = Vector3.Distance(l.A!.Position, l.B!.Position);
                if (d <= l.A.Class.LightingRange) AddSource(l.B, l.A);
                if (d <= l.B.Class.LightingRange) AddSource(l.A, l.B);
            }
        }
    }
    private static void AddSource(HotPoint target, HotPoint source)
    {
        if (!target.HeatSources.Contains(source)) target.HeatSources.Add(source);
    }

    /// <summary>Équivalent de FIR_HPAddIntensity : un foyer éteint qui passe au-dessus de 0 s'allume aussitôt.</summary>
    public void AddIntensity(HotPoint hp, float amount)
    {
        hp.SetIntensity(hp.Intensity + amount);
        if (!hp.Lit && hp.Intensity > 0 && hp.State != HotPointState.Consumed)
        { hp.Lit = true; hp.State = HotPointState.Growing; hp.BurnTime = 0; hp.HeatingTime = 0; }
    }

    public void Ignite(HotPoint hp, float intensity)
    {
        hp.Lit = true; hp.State = HotPointState.Growing; hp.BurnTime = 0; hp.HeatingTime = 0;
        hp.SetIntensity(Math.Max(intensity, 1f));
    }

    public void Step(float dt)
    {
        // 1. liens à retardement (§ 4.2)
        foreach (var l in Links.Where(l => l.Type == LinkType.Delayed && !(ClosedOpeningsBlock && l.OpeningId != 0)))
            foreach (var (src, dst) in l.Directions())
            {
                bool push = src.Lit && src.Intensity >= dst.Class.LightingThreshold;
                if (!push) continue;
                l.Elapsed += dt;
                if (l.Elapsed >= l.Tempo && src.Intensity >= 0) dst.DelayedHeat += src.Intensity;
            }

        LastDt = dt;
        // 2. chaleur reçue, calculée avant toute mise à jour d'intensité
        foreach (var hp in HotPoints) hp.HeatReceived = hp.Lit ? 0f : ComputeHeat(hp);

        // 3. mise à jour de chaque foyer
        foreach (var hp in HotPoints)
        {
            if (!hp.Enabled) continue;
            if (hp.PendingAttack > 0) { ApplyAttack(hp); continue; }
            switch (hp.State)
            {
                case HotPointState.Idle:
                case HotPointState.Heating:
                    if (hp.Intensity >= 0) TryIgnite(hp, dt);
                    else Dry(hp, dt);
                    break;
                case HotPointState.Consumed:
                    if (hp.Intensity < 0) Dry(hp, dt);
                    break;
                case HotPointState.Growing:
                case HotPointState.Declining:
                    Burn(hp, dt);
                    break;
            }
        }
        Time += dt;
    }

    // § 3.2
    private float ComputeHeat(HotPoint hp)
    {
        float total = 0f;
        float slope = hp.Class.SlopeFactor;
        float downFactor = slope > 1f ? 1f - 1f / slope : 0f;
        foreach (var src in hp.HeatSources)
        {
            if (!src.Lit) continue;
            float i = src.Intensity;
            var dir = hp.Position - src.Position;
            float len = dir.Length();
            var u = len > 1e-3f ? dir / len : Vector3.Zero;
            float slopeTerm = 0f;
            if (slope >= 1f)
            {
                if (u.Z >= 0) slopeTerm = (slope - 1f) * u.Z * i;
                else
                {
                    float k = 1f - MathF.Abs(u.Z) * downFactor;
                    if (k < 1f) slopeTerm = (k - 1f) * i;
                }
            }
            float windTerm = 0f;
            if (Settings.WindPower > 1f && hp.Class.UnderWindInfluence && src.Class.UnderWindInfluence)
            {
                float c = Vector3.Dot(Settings.WindDirection, u);
                if (c >= 0) windTerm = (Settings.WindPower - 1f) * c * i;
            }
            float contrib = i + slopeTerm + windTerm;
            if (contrib > 0) total += contrib;
        }
        total += hp.DelayedHeat;
        hp.DelayedHeat = 0f;
        return total;
    }

    /// <summary>
    /// Arrosage (§ 6) : <paramref name="unitPower"/> = FireAttackPower de l'unité (7 pour un pompier),
    /// multiplié par le FireAttackPower de la difficulté et par l'efficacité de l'agent sur ce feu.
    /// À appeler à chaque pas tant que l'unité arrose ; l'effet est appliqué au Step suivant.
    /// </summary>
    public void Attack(HotPoint hp, float unitPower, float dt, float efficiency = 1f)
        => hp.PendingAttack += unitPower * Settings.FireAttackPower * efficiency * dt;

    /// <summary>Efficacité d'un agent sur un type de feu (valeurs de base_fireman.class,
    /// CAPACITIES_FIRE_ATTACK / FireAgentEfficiency_*). L'unité peut les redéfinir dans sa classe.</summary>
    public static float AgentEfficiency(string agent, char fireType) => (agent.ToUpperInvariant(), fireType) switch
    {
        ("WATER", 'A') => 1f, ("WATER", 'B') => 0.3f, ("WATER", 'C') => 1f, ("WATER", 'D') => 0f,
        ("FOAM", 'A') => 0.3f, ("FOAM", 'B') => 3f, ("FOAM", 'C') => 0.3f, ("FOAM", 'D') => 0f,
        ("HALON", _) => 1f,
        ("POWDER", 'A') => 0.2f, ("POWDER", 'B') => 0.2f, ("POWDER", 'C') => 0f, ("POWDER", 'D') => 1f,
        ("METAL", 'D') => 1f, ("METAL", _) => 0f,
        _ => 1f,
    };

    private void ApplyAttack(HotPoint hp)
    {
        float a = hp.PendingAttack; hp.PendingAttack = 0;
        if (hp.Lit)
        {
            // [mesuré, sauvegardes Eau0–3] le feu continue de croître pendant l'arrosage :
            // perte nette = attaque − croissance. Le temps de combustion s'arrête à l'extinction.
            Burn(hp, LastDt);
            hp.SetIntensity(hp.Intensity - a);
            if (hp.Intensity <= 0f)
            {
                // [mesuré] éteint : intensité 0 (pas négative), le foyer redevient « en chauffe » et peut
                // se rallumer après Tempo × difficulté si ses voisins brûlent encore.
                hp.Lit = false; hp.Intensity = 0f; hp.State = HotPointState.Idle; hp.HeatingTime = 0f;
                ExtinguishLog.Add($"{Time,6:0.0} {hp.Name} éteint");
            }
        }
        else
        {
            // [hypothèse] un foyer éteint arrosé se mouille jusqu'à IntensityMin et ne chauffe pas
            hp.SetIntensity(hp.Intensity - a);
            hp.HeatingTime = 0f;
        }
    }
    public List<string> ExtinguishLog { get; } = new();
    private float LastDt = 0.1f;

    // § 3.3
    private void TryIgnite(HotPoint hp, float dt)
    {
        bool heating = hp.HeatReceived > hp.Class.LightingThreshold;
        if (heating) { hp.State = HotPointState.Heating; hp.HeatingTime += dt; }
        if (hp.HeatingTime > hp.Class.Tempo * Settings.TempoFactor)
        {
            if (hp.Intensity == 0f) hp.SetIntensity(1f);
            // [mesuré, Eau4] un foyer rallumé repart de 1 mais garde son temps de combustion (non remis à zéro)
            hp.Lit = true; hp.State = HotPointState.Growing; hp.HeatingTime = 0f;
        }
    }

    // § 3.1
    private void Dry(HotPoint hp, float dt)
    {
        float v = hp.Intensity + (Settings.FireEvaporationFactor * hp.HeatReceived + Settings.NaturalEvaporationRate) * dt;
        hp.SetIntensity(Math.Min(v, 0f));
    }

    // § 3.4
    private void Burn(HotPoint hp, float dt)
    {
        if (hp.Intensity < 0) return;
        hp.BurnTime += dt;
        var c = hp.Class;
        bool growing = c.GrowthTime <= -1f || hp.BurnTime < c.GrowthTime;
        if (growing)
        {
            hp.SetIntensity(hp.Intensity + c.GrowthRate * hp.GrowthMultiplier * Settings.GrowthFactor * dt);
            return;
        }
        hp.State = HotPointState.Declining;
        float v = Math.Min(hp.Intensity + c.DeathRate * dt, c.IntensityMax);
        if (v < 0f)
        {
            hp.Lit = false; hp.State = HotPointState.Consumed;
            hp.SetIntensity(v);
            return;
        }
        hp.SetIntensity(v);
    }
}
