using System.Numerics;
using OpenFD.Fire;
using OpenFD.Formats;

namespace OpenFD.Game;

public sealed class ExplosionSystem
{
    public List<ExplosiveItem> Items { get; } = new();
    public Dictionary<uint, ExplosiveItem> ById { get; } = new();
    /// <summary>Explosions survenues (pour l'image et le son) ; le jeu retire celles qu'il a traitées.</summary>
    public List<Explosion> Happened { get; } = new();
    public List<string> Log { get; } = new();
    /// <summary>Unités et civils touchés par le souffle (position, rayon, force) : à appliquer par le gestionnaire d'unités.</summary>
    public List<(Vector3 pos, float radius, float power)> Blasts { get; } = new();

    // dégâts du feu : UnitLifeDamage × (intensité / intensité max) × FireDamageCoef × ce facteur, par seconde et par foyer
    public static float FireDamageFactor = float.TryParse(Environment.GetEnvironmentVariable("FD3_DEGATS"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 6f;
    public static float OtherFiresShare = float.TryParse(Environment.GetEnvironmentVariable("FD3_PART"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float p) ? p : 0.25f;
    public const float ItemRadius = 180f;
    /// <summary>
    /// Objet qui ne fait que s'abîmer, suivi par un script (Mriya : le générateur de l'objectif 1) : la formule du jeu ×
    /// WatchedDamageFactor (réglé pour 2 minutes en facile, chronométré sur le jeu d'origine), divisé par
    /// ObjectiveTimeFactor (réglage « Gameplay » du fichier d'options, 2 par défaut : 4 minutes).
    /// </summary>
    public const float WatchedDamageFactor = 2.53f;
    public static float ObjectiveTimeFactor = 2f;
    // part du souffle (LP_Explode_Power) reçue par les autres objets explosifs : limite les réactions en chaîne
    public static float BlastOnItems = float.TryParse(Environment.GetEnvironmentVariable("FD3_SOUFFLE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b) ? b : 0.5f;   // taille d'une voiture autour de son centre

    public static ExplosionSystem Load(Level level, GameFiles files, FireWorld fire, IReadOnlySet<uint>? watched = null)
    {
        ExplosionSystem sys = new ExplosionSystem();
        Dictionary<string, ClassFile> cache = new Dictionary<string, ClassFile>(StringComparer.OrdinalIgnoreCase);
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform))
        {
            ClassFile c;
            try { c = ClassFile.Load(files, "Design\\" + o.ClassPath["design\\".Length..] + ".class", cache); } catch { continue; }
            bool can = c.GetBool("EXPLOSION", "CanExplode", false);
            byte[]? expl = Data(o, 0xA029);
            if (expl != null && expl.Length >= 25)
                can = expl[0] != 0;
            // objet qui ne fait que s'abîmer au feu, quand un script suit sa vie (Mriya : le générateur, objectif 1)
            bool watchedOnly = !can && watched != null && watched.Contains(o.Id) && c.GetBool("DAMAGE", "UseDamage", false);
            if (!can && !watchedOnly)
                continue;
            ExplosiveItem it = new ExplosiveItem
            {
                Id = o.Id, Name = o.Name, Position = o.Position,
                MaxLife = c.GetFloat("DAMAGE", "MaxLifePoints", 100), Life = c.GetFloat("DAMAGE", "LifePoints", 100),
                FireDamageCoef = c.GetFloat("DAMAGE", "FireDamageCoef", 1),
                LpPower = c.GetFloat("EXPLOSION", "LP_Explode_Power", 40), LpRadius = c.GetFloat("EXPLOSION", "LP_Explode_Radius", 400),
                HpPower = c.GetFloat("EXPLOSION", "HP_Explode_Power", 20), HpRadius = c.GetFloat("EXPLOSION", "HP_Explode_Radius", 800),
                StaminaPower = c.GetFloat("EXPLOSION", "Stamina_Explode_Power", 20), StaminaRadius = c.GetFloat("EXPLOSION", "Stamina_Explode_Radius", 1100),
                Sound = c.Get("SOUND_EXPLOSION", "Label")?.Trim('"', ' '),
                CanExplode = can,
            };
            int n = c.GetInt("DAMAGE", "NbDamageSteps", 0);
            it.Thresholds = Enumerable.Range(0, Math.Max(n, 0)).Select(i => c.GetFloat("DAMAGE", $"DamageTresholdStep{i}", 0)).ToArray();
            // valeurs propres à cet objet dans la carte
            if (o.GetComponent(0xA024) is { } dc && o.ComponentBytes(dc) is ReadOnlySpan<byte> db && db.Length >= 21 && db[4] == 0x0C)
            {
                it.Life = BitConverter.ToSingle(db.Slice(5, 4));
                it.MaxLife = BitConverter.ToSingle(db.Slice(9, 4));
                it.FireDamageCoef = BitConverter.ToSingle(db.Slice(17, 4));
            }
            if (expl != null && expl.Length >= 25)
            {
                float F(int k) => BitConverter.ToSingle(expl, 1 + 4 * k);
                it.LpPower = F(0);
                it.LpRadius = F(1);
                it.HpPower = F(2);
                it.HpRadius = F(3);
                it.StaminaPower = F(4);
                it.StaminaRadius = F(5);
            }
            if (Data(o, 0xD007) is { Length: > 8 } sd)
            {
                int len = BitConverter.ToInt32(sd, 0);
                if (len > 1 && 4 + len <= sd.Length)
                    it.Sound = System.Text.Encoding.ASCII.GetString(sd, 4, len - 1);
            }
            if (!float.IsFinite(it.Life) || it.MaxLife <= 0)
                continue;
            // déjà détruit dans la carte (épave, effet d'explosion inerte « explo_multi ») : pas d'explosion au départ
            if (it.Life <= 0)
            {
                it.Life = 0;
                it.Exploded = true;
            }
            foreach (HotPoint h in fire.HotPoints)
                if (Vector3.Distance(h.Position, it.Position) < ItemRadius + h.Class.HeatRangeMax + 100)
                    it.NearFires.Add(h);
            sys.Items.Add(it);
            sys.ById[it.Id] = it;
        }
        return sys;
    }

    // données d'un composant après son en-tête (« … CC CC BB BB »)
    private static byte[]? Data(LevelObject o, ushort id)
    {
        if (o.GetComponent(id) is not { } c)
            return null;
        ReadOnlySpan<byte> b = o.ComponentBytes(c);
        int k = b.IndexOf(new byte[] { 0xCC, 0xCC, 0xBB, 0xBB });
        return k < 0 ? null : b[(k + 4)..].ToArray();
    }

    private static readonly bool Disabled = Environment.GetEnvironmentVariable("FD3_SANS_EXPLOSIONS") == "1";

    public void Step(FireWorld fire, float time, float dt)
    {
        if (Disabled)
            return;
        foreach (ExplosiveItem it in Items)
        {
            if (it.Exploded || (!it.CanExplode && it.Life <= 0))
                continue;
            // le foyer le plus fort compte en entier, les autres un peu (sinon un objet au milieu d'un grand feu fond en 2 s)
            float sum = 0, max = 0;
            foreach (HotPoint h in it.NearFires)
                if (h.IsBurning && Vector3.Distance(h.Position, it.Position) <= h.Class.LifeDamageRange + ItemRadius)
                {
                    float d = h.Class.UnitLifeDamage * Math.Min(1, h.Intensity / Math.Max(1, h.Class.IntensityMax));
                    sum += d;
                    max = MathF.Max(max, d);
                }
            // (objet qui ne fait que s'abîmer, suivi par un script : voir WatchedDamageFactor et ObjectiveTimeFactor)
            float dmg = (max + OtherFiresShare * (sum - max)) * (it.CanExplode ? FireDamageFactor : WatchedDamageFactor / MathF.Max(0.1f, ObjectiveTimeFactor));
            if (dmg > 0)
                it.Life -= dmg * it.FireDamageCoef * dt;
            if (!it.CanExplode)
                it.Life = MathF.Max(0, it.Life);   // détruit sans explosion
        }
        // explosions (en chaîne : une explosion abîme les objets voisins, qui peuvent exploser au pas suivant)
        foreach (ExplosiveItem it in Items.Where(i => i.CanExplode && !i.Exploded && i.Life <= 0).ToList())
            Explode(it, fire, time);
    }

    /// <summary>Explosion : souffle sur les objets (points de vie), les foyers (le feu prend ou grandit) et les personnes.</summary>
    public void Explode(ExplosiveItem it, FireWorld fire, float time)
    {
        if (it.Exploded)
            return;
        it.Exploded = true;
        it.Life = 0;
        Happened.Add(new Explosion(it.Position, time, Math.Clamp(it.HpRadius * 0.9f, 500, 1200), it.Sound, it.Name));
        Log.Add($"{time,6:0} s : explosion de {it.Name}");
        int lit = 0;
        foreach (HotPoint h in fire.HotPoints)
        {
            float d = Vector3.Distance(h.Position, it.Position);
            if (d > it.HpRadius || !h.Enabled)
                continue;
            float k = 1 - d / it.HpRadius;   // décroît jusqu'au bord du souffle
            if (!h.Lit)
                lit++;
            fire.AddIntensity(h, it.HpPower * k);
        }
        foreach (ExplosiveItem o in Items)
        {
            if (o == it || o.Exploded)
                continue;
            float d = Vector3.Distance(o.Position, it.Position);
            if (d <= it.LpRadius)
                o.Life -= it.LpPower * BlastOnItems * (1 - d / it.LpRadius);
        }
        Blasts.Add((it.Position, it.StaminaRadius, it.StaminaPower));
        if (lit > 0)
            Log.Add($"{time,6:0} s : {lit} nouveau(x) foyer(s) allumé(s) par l'explosion");
    }

    /// <summary>Points de vie (scripts : ENT_GetDamage) ; null si l'objet n'est pas un objet explosif.</summary>
    public void SetFireCoef(uint id, float coef)
    {
        if (ById.TryGetValue(id, out ExplosiveItem? it))
            it.FireDamageCoef = coef;
    }

    public float? LifeOf(uint id) => ById.TryGetValue(id, out ExplosiveItem? it) ? it.Life : null;

    /// <summary>Scripts : ENT_SetDamage(id, points de vie) ; 0 fait exploser.</summary>
    public bool SetLife(uint id, float life, FireWorld fire, float time)
    {
        if (!ById.TryGetValue(id, out ExplosiveItem? it))
            return false;
        it.Life = MathF.Max(0, life);
        if (life <= 0 && it.CanExplode)
            Explode(it, fire, time);
        return true;
    }
}
