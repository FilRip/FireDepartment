using System.Numerics;
using OpenFD.Formats;

namespace OpenFD.Fire;

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
        FireWorld world = new FireWorld(settings);
        Dictionary<string, ClassFile> classCache = new Dictionary<string, ClassFile>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HotPointClass> hpClasses = new Dictionary<string, HotPointClass>(StringComparer.OrdinalIgnoreCase);
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.HotPoint && o.HasTransform))
        {
            if (!hpClasses.TryGetValue(o.ClassPath, out HotPointClass? hc))
            {
                // design\hotpoint\ground_l -> Design\Hotpoint\ground_l.class
                string rel = "Design\\" + o.ClassPath["design\\".Length..] + ".class";
                hc = HotPointClass.From(o.ClassName, ClassFile.Load(files, rel, classCache));
                hpClasses[o.ClassPath] = hc;
            }
            (bool lit, float current, float burnTime, HotPointClass cls)? inst = InstanceState(o, hc);
            HotPoint hp = new HotPoint { Id = o.Id, Name = o.Name, Class = inst?.cls ?? hc, Position = o.Position };
            if (BackdraftTags(o) is { } bd)
            {
                hp.MakeBackdraft = bd.make;
                hp.SleepingThreshold = bd.threshold;
            }
            if (inst is { } st)
            {
                // [confirmé par une sauvegarde] état initial stocké dans FE00 : allumé, intensité, temps de combustion
                hp.Lit = st.lit;
                hp.Intensity = st.current;
                hp.BurnTime = st.burnTime;
                hp.State = hp.Lit ? HotPointState.Growing : HotPointState.Idle;
            }
            else
            {
                hp.Intensity = SavedIntensity(o) ?? 0f;
                hp.Lit = hp.Intensity > 0;
                if (hp.Lit)
                    hp.State = HotPointState.Growing;
            }
            world.HotPoints.Add(hp);
            world.ById[hp.Id] = hp;
        }
        foreach ((uint a, uint b, byte[]? data) in level.HotPointLinks)
        {
            if (data.Length < 26)
                continue;
            world.ById.TryGetValue(a, out HotPoint? ha);
            world.ById.TryGetValue(b, out HotPoint? hb);
            FireLink link = new FireLink
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
        Component? c = o.GetComponent(0xFE00);
        if (c == null)
            return null;
        byte[] b = o.ComponentBytes(c).ToArray();
        if (b.Length < 140 || !Chunks.IsBegin(b, 17) || b[21] != 3)
            return null;
        int h = 22;
        bool lit = b[h + 5] != 0;
        float cur = BitConverter.ToSingle(b, h + 10), max = BitConverter.ToSingle(b, h + 14);
        int last = LastIndexOf(b, b.Length), prev = LastIndexOf(b, last);
        if (last < 0 || prev < 0 || last - (prev + 4) != 96)
            return null;
        int t = prev + 4;
        float F(int off) => BitConverter.ToSingle(b, t + off);
        HotPointClass cls = baseClass with
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
        Component? c = o.GetComponent(0xFE00);
        if (c == null)
            return null;
        byte[] b = o.ComponentBytes(c).ToArray();
        int last = LastIndexOf(b, b.Length), prev = LastIndexOf(b, last);
        if (last < 0 || prev < 0 || last - (prev + 4) != 96)
            return null;
        int t = prev + 4;
        float th = BitConverter.ToSingle(b, t + 19);
        return (b[t + 17] != 0, float.IsFinite(th) && th > 0 ? th : 80f);
    }

    private static int LastIndexOf(byte[] b, int before)
    {
        for (int i = before - 4; i >= 0; i--)
            if (Chunks.IsEnd(b, i))
                return i;
        return -1;
    }

    /// <summary>Intensité sauvegardée dans le composant FD00 : premier flottant après le bloc d'état.</summary>
    public static float? SavedIntensity(LevelObject o)
    {
        Component? c = o.GetComponent(0xFD00);
        if (c == null)
            return null;
        ReadOnlySpan<byte> b = o.ComponentBytes(c);
        if (b.Length < 32 || !Chunks.IsBegin(b, 17) || !Chunks.IsBegin(b, 22))
            return null;
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

    /// <summary>
    /// Rayon de propagation du feu : la portée d'allumage (LightingRange) de chaque foyer est multipliée par ce facteur
    /// (1 : comme le jeu d'origine). Réglage caché « FireSpreadRadiusFactor » du json.
    /// </summary>
    public static float SpreadRadiusFactor = 1f;
    /// <summary>Distance 3D (true, comme le code du jeu) ou à plat (false).</summary>
    public bool Proximity3D;

    public void AddProximityNeighbours(Grid? walls = null, IReadOnlyDictionary<uint, string>? rooms = null)
    {
        foreach (HotPoint src in HotPoints)
        {
            foreach (HotPoint dst in HotPoints)
            {
                if (ReferenceEquals(src, dst))
                    continue;
                float range = SpreadRadiusFactor * (ProximityUsesTargetRange ? dst.Class.LightingRange : src.Class.LightingRange);
                float r2 = range * range;
                // pièces : la chaleur ne traverse pas directement d'une pièce à l'autre (seulement par les liens)
                if (rooms != null && (rooms.TryGetValue(src.Id, out string? ra) ? ra : "") != (rooms.TryGetValue(dst.Id, out string? rb) ? rb : ""))
                    continue;
                float dx = dst.Position.X - src.Position.X;
                float dy = dst.Position.Y - src.Position.Y;
                float dz = Proximity3D ? dst.Position.Z - src.Position.Z : 0f;
                if (dx * dx + dy * dy + dz * dz > r2)
                    continue;
                if (walls != null && WallBetween(walls, src.Position, dst.Position))
                    continue;
                AddSource(dst, src);
            }
        }
    }

    public static bool WallBetween(Grid g, Vector3 a, Vector3 b)
    {
        (int ax, int ay) = g.CellOf(a);
        (int bx, int by) = g.CellOf(b);
        int n = Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay));
        for (int i = 1; i < n; i++)
        {
            int cx = ax + (int)MathF.Round((bx - ax) * i / (float)n), cy = ay + (int)MathF.Round((by - ay) * i / (float)n);
            if (g.Inside(cx, cy) && g.ZoneA[g.Index(cx, cy)] == 0)
                return true;
        }
        return false;
    }

    // § 4.1 : le voisinage vient des liens de type 0.
    public void BuildNeighbourhoods()
    {
        foreach (FireLink l in Links.Where(l => l.Type == LinkType.Neighbour && l.A != null && l.B != null))
        {
            if (ClosedOpeningsBlock && l.OpeningId != 0)
                continue;
            if (l.Active)
                foreach ((HotPoint? src, HotPoint? dst) in l.Directions())
                    AddSource(dst, src);
            else
            {
                float d = Vector3.Distance(l.A!.Position, l.B!.Position);
                if (d <= SpreadRadiusFactor * l.A.Class.LightingRange)
                    AddSource(l.B, l.A);
                if (d <= SpreadRadiusFactor * l.B.Class.LightingRange)
                    AddSource(l.A, l.B);
            }
        }
    }
    private static void AddSource(HotPoint target, HotPoint source)
    {
        if (!target.HeatSources.Contains(source))
            target.HeatSources.Add(source);
    }

    /// <summary>Équivalent de FIR_HPAddIntensity : un foyer éteint qui passe au-dessus de 0 s'allume aussitôt.</summary>
    public void AddIntensity(HotPoint hp, float amount)
    {
        hp.SetIntensity(hp.Intensity + amount);
        if (!hp.Lit && hp.Intensity > 0 && hp.State != HotPointState.Consumed)
        {
            hp.Lit = true;
            hp.State = HotPointState.Growing;
            hp.BurnTime = 0;
            hp.HeatingTime = 0;
        }
    }

    public void Ignite(HotPoint hp, float intensity)
    {
        hp.Lit = true;
        hp.State = HotPointState.Growing;
        hp.BurnTime = 0;
        hp.HeatingTime = 0;
        hp.SetIntensity(Math.Max(intensity, 1f));
    }

    public void Step(float dt)
    {
        // 1. liens à retardement (§ 4.2)
        foreach (FireLink l in Links.Where(l => l.Type == LinkType.Delayed && !(ClosedOpeningsBlock && l.OpeningId != 0)))
            foreach ((HotPoint? src, HotPoint? dst) in l.Directions())
            {
                bool push = src.Lit && src.Intensity >= dst.Class.LightingThreshold;
                if (!push)
                    continue;
                l.Elapsed += dt;
                if (l.Elapsed >= l.Tempo && src.Intensity >= 0)
                    dst.DelayedHeat += src.Intensity;
            }

        // sprinklers : de l'eau en continu, sans fin, comme une lance
        foreach (HotPoint hp in HotPoints)
            if (hp.Sprinkler > 0 && hp.Lit)
                Attack(hp, hp.Sprinkler, dt, AgentEfficiency("WATER", hp.Class.FireType));

        LastDt = dt;
        // 2. chaleur reçue, calculée avant toute mise à jour d'intensité
        foreach (HotPoint hp in HotPoints)
            hp.HeatReceived = hp.Lit ? 0f : ComputeHeat(hp);

        // 3. mise à jour de chaque foyer
        foreach (HotPoint hp in HotPoints)
        {
            if (!hp.Enabled)
            {
                // désactivé par le script (FIR_HPEnable 0) pendant qu'il brûlait : il ne grandit plus et ne s'allume plus,
                // mais l'eau l'éteint encore (College : foyers du groupe électrique, désactivés quand on coupe le courant)
                if (hp.PendingAttack > 0)
                    ApplyAttack(hp, false);
                continue;
            }
            if (hp.PendingAttack > 0)
            {
                ApplyAttack(hp);
                continue;
            }
            switch (hp.State)
            {
                case HotPointState.Idle:
                case HotPointState.Heating:
                    if (hp.Intensity >= 0)
                        TryIgnite(hp, dt);
                    else
                        Dry(hp, dt);
                    break;
                case HotPointState.Consumed:
                    if (hp.Intensity < 0)
                        Dry(hp, dt);
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
        foreach (HotPoint src in hp.HeatSources)
        {
            if (!src.Lit)
                continue;
            float i = src.Intensity;
            Vector3 dir = hp.Position - src.Position;
            float len = dir.Length();
            Vector3 u = len > 1e-3f ? dir / len : Vector3.Zero;
            float slopeTerm = 0f;
            if (slope >= 1f)
            {
                if (u.Z >= 0)
                    slopeTerm = (slope - 1f) * u.Z * i;
                else
                {
                    float k = 1f - MathF.Abs(u.Z) * downFactor;
                    if (k < 1f)
                        slopeTerm = (k - 1f) * i;
                }
            }
            float windTerm = 0f;
            if (Settings.WindPower > 1f && hp.Class.UnderWindInfluence && src.Class.UnderWindInfluence)
            {
                float c = Vector3.Dot(Settings.WindDirection, u);
                if (c >= 0)
                    windTerm = (Settings.WindPower - 1f) * c * i;
            }
            float contrib = i + slopeTerm + windTerm;
            if (contrib > 0)
                total += contrib;
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

    private void ApplyAttack(HotPoint hp, bool grow = true)
    {
        float a = hp.PendingAttack;
        hp.PendingAttack = 0;
        if (hp.Lit)
        {
            // [mesuré, sauvegardes Eau0–3] le feu continue de croître pendant l'arrosage :
            // perte nette = attaque − croissance. Le temps de combustion s'arrête à l'extinction.
            if (grow)
                Burn(hp, LastDt);
            hp.SetIntensity(hp.Intensity - a);
            if (hp.Intensity <= 0f)
            {
                // [mesuré] éteint : intensité 0 (pas négative), le foyer redevient « en chauffe » et peut
                // se rallumer après Tempo × difficulté si ses voisins brûlent encore.
                hp.Lit = false;
                hp.Intensity = 0f;
                hp.State = HotPointState.Idle;
                hp.HeatingTime = 0f;
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

    /// <summary>Éteint un foyer d'un coup (débogage, F4), comme un arrosage qui l'amène à 0.</summary>
    public void Extinguish(HotPoint hp)
    {
        if (!hp.Lit)
            return;
        hp.Lit = false;
        hp.Intensity = 0f;
        hp.State = HotPointState.Idle;
        hp.HeatingTime = 0f;
        ExtinguishLog.Add($"{Time,6:0.0} {hp.Name} éteint (débogage)");
    }

    private float LastDt = 0.1f;

    // § 3.3
    private void TryIgnite(HotPoint hp, float dt)
    {
        bool heating = hp.HeatReceived > hp.Class.LightingThreshold;
        if (heating)
        {
            hp.State = HotPointState.Heating;
            hp.HeatingTime += dt;
        }
        if (hp.HeatingTime > hp.Class.Tempo * Settings.TempoFactor)
        {
            if (hp.Intensity == 0f)
                hp.SetIntensity(1f);
            // [mesuré, Eau4] un foyer rallumé repart de 1 mais garde son temps de combustion (non remis à zéro)
            hp.Lit = true;
            hp.State = HotPointState.Growing;
            hp.HeatingTime = 0f;
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
        if (hp.Intensity < 0)
            return;
        hp.BurnTime += dt;
        HotPointClass c = hp.Class;
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
            hp.Lit = false;
            hp.State = HotPointState.Consumed;
            hp.SetIntensity(v);
            return;
        }
        hp.SetIntensity(v);
    }
}
