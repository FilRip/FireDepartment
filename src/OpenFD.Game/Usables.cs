using System.Numerics;
using OpenFD.Formats;
using OpenFD.Script;

namespace OpenFD.Game;

public sealed partial class UnitManager
{
    public List<UsableObject> Usables { get; } = new();
    public const float UseObjectSeconds = 2.0f, RepairSeconds = 6.0f;

    private void LoadUsables(Level level)
    {
        HashSet<(uint id, string cap)> enabledLater = ScriptEnabledLater();
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform))
        {
            ClassFile? c = Class("Design\\" + o.ClassPath["design\\".Length..] + ".class");
            if (c == null)
                continue;
            bool use = c.GetBool("CAPACITIES_ACTIONS", "CouldBeUse") || c.GetBool("CAPACITIES_TARGET", "CouldBeUse"), repair = c.GetBool("CAPACITIES_ACTIONS", "CouldBeRepair") || c.GetBool("CAPACITIES_TARGET", "CouldBeRepair");
            if (Openings.Any(d => d.Id == o.Id))
                continue;   // portes et fenêtres : voir Openings
            // objet que le script rend utilisable (ou réparable) plus tard : il ne l'est pas encore au départ
            // (Mriya : le calendrier bonus collé au transformateur, rendu utilisable pendant la cinématique de G2_CanUse)
            if (enabledLater.Contains((o.Id, "COULDBEUSE")))
                use = false;
            if (enabledLater.Contains((o.Id, "COULDBEREPAIR")))
                repair = false;
            UsableObject u = new UsableObject { Id = o.Id, Name = o.Name, Position = o.Position, CanBeUsed = use, CanBeRepaired = repair };
            ComputeBox(u, o, c);
            if (o.GetComponent(0xA027) is { } comp)
                u.Actions.AddRange(ParseActions(o.ComponentBytes(comp).ToArray()));
            // objets sans action de script mais utilisables (les scripts peuvent les activer : ENT_SetCapacityTarget)
            if (!use && !repair && u.Actions.Count == 0)
                continue;
            Usables.Add(u);
        }
    }

    /// <summary>Objets que les scripts de la mission activent (ENT_SetCapacityTarget(numéro, « CouldBeUse »/« CouldBeRepair », 1)),
    /// hors lignes en commentaire.</summary>
    private HashSet<(uint id, string cap)> ScriptEnabledLater()
    {
        HashSet<(uint id, string cap)> res = new HashSet<(uint id, string cap)>();
        string dir = Path.Combine(_files.DataDir, "Script", "Mission", _s.Mission);
        if (!Directory.Exists(dir))
            return res;
        foreach (string f in Directory.EnumerateFiles(dir, "*.scp"))
            foreach (string line in File.ReadLines(f, System.Text.Encoding.Latin1))
            {
                string code = line.Contains("//") ? line[..line.IndexOf("//", StringComparison.Ordinal)] : line;
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(code, @"ENT_SetCapacityTarget\s*\(\s*(\d+)\s*,\s*""(\w+)""\s*,\s*1\s*\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    res.Add((uint.Parse(m.Groups[1].Value), m.Groups[2].Value.ToUpperInvariant()));
            }
        return res;
    }

    /// <summary>Emprise au sol du modèle (le symbole de réparation peut être loin du pivot) : BoxMin / BoxMax.</summary>
    private void ComputeBox(UsableObject u, LevelObject o, ClassFile? c)
    {
        u.BoxMin = u.BoxMax = new Vector2(o.Position.X, o.Position.Y);
        string? m = c?.Get("DISPLAY", "Model")?.Trim('"', ' ');
        if (!string.IsNullOrEmpty(m) && !m.Equals("noitem", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                List<Vector2> pts = PathFind.Footprint(_assets ??= new SceneAssets(_files), SceneAssets.GfxPath("Placeable", m), 0, 400);
                foreach (Vector2 q in pts)
                {
                    Vector3 wq = Vector3.Transform(new Vector3(q, 0), o.Matrix);
                    u.BoxMin = Vector2.Min(u.BoxMin, new(wq.X, wq.Y));
                    u.BoxMax = Vector2.Max(u.BoxMax, new(wq.X, wq.Y));
                }
            }
            catch
            {
                // modèle illisible : on vise le pivot
            }
        }
    }

    // chaînes du composant A027 (longueur sur 4 octets, puis le texte terminé par 0) : nom de l'action, animation, fonction
    private static List<(string action, string script)> ParseActions(byte[] b)
    {
        List<string> strings = new List<string>();
        for (int p = 0; p + 4 < b.Length; p++)
        {
            int len = BitConverter.ToInt32(b, p);
            if (len < 2 || len > 80 || p + 4 + len > b.Length || b[p + 4 + len - 1] != 0)
                continue;
            bool ok = true;
            for (int i = 0; i < len - 1 && ok; i++)
                ok = b[p + 4 + i] >= 32 && b[p + 4 + i] < 127;
            if (!ok)
                continue;
            strings.Add(System.Text.Encoding.Latin1.GetString(b, p + 4, len - 1));
            p += 3 + len;
        }
        List<(string, string)> res = new List<(string, string)>();
        string? action = null;
        foreach (string s in strings)
        {
            if (s.Contains('\\') || s.Contains('$'))
                continue;                       // animation (ItemChar\ic_…)
            if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^(use\d*|repair\d*|open|close)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                action = s;
                continue;
            }
            if (action != null && System.Text.RegularExpressions.Regex.IsMatch(s, @"^[A-Za-z_]\w+$"))
            {
                res.Add((action, s));
                action = null;
            }
        }
        return res;
    }

    public UsableObject? UsableAt(Vector2 p, float radius) =>
        Usables.Where(o => o.Enabled && Vector2.Distance(new(o.Position.X, o.Position.Y), p) < radius)
            .OrderBy(o => Vector2.Distance(new(o.Position.X, o.Position.Y), p)).FirstOrDefault();

    /// <summary>Point de l'emprise de l'objet le plus proche de p (un gros transformateur se répare depuis son bord).</summary>
    private static Vector2 NearestPoint(UsableObject o, Vector2 p) => Vector2.Clamp(p, o.BoxMin, o.BoxMax);

    /// <summary>Le pompier peut-il agir sur cet objet ? (le technicien seul répare)</summary>
    public bool CanWorkOn(Unit u, UsableObject o) => !u.IsVehicle && ((o.CanBeUsed && u.CanUse) || (o.CanBeRepaired && !o.Repaired && u.CanRepair));

    /// <summary>Clic droit d'un pompier sur un objet utilisable : il y va et agit dessus.</summary>
    public void UseObject(Unit u, UsableObject o)
    {
        if (!CanWorkOn(u, o))
        {
            u.Status = o.CanBeRepaired && !o.CanBeUsed ? "seul un technicien peut réparer cela" : "ne peut pas utiliser cet objet";
            return;
        }
        u.Target = null;
        u.Opening = null;
        u.GoTo = null;
        u.GoToVehicle = null;
        u.Boarding = false;
        u.UseTarget = o;
        u.ActionTimer = 0;
        Vector2 me = new Vector2(u.Position.X, u.Position.Y);
        Vector2 at = NearestPoint(o, me);
        if (Vector2.Distance(me, at) > ActionReach)
        {
            Vector2 spot = Walkable(at + Vector2.Normalize(me - at + new Vector2(0.01f, 0)) * 120);
            if (!Order(u, spot, refuseUnreachable: false))
            {
                u.UseTarget = null;
                u.Status = "ne peut pas atteindre l'objet";
                return;
            }
        }
        u.Status = o.CanBeRepaired && !o.CanBeUsed ? "va réparer" : "va utiliser l'objet";
    }

    /// <summary>IA_CmdUse(unité, objet) : porte (ouvre ou ferme) ou objet utilisable.</summary>
    public void ScriptUse(Unit u, uint id)
    {
        if (Openings.FirstOrDefault(d => d.Id == id) is { } door)
        {
            WorkOnOpening(u, door);
            return;
        }
        if (Usables.FirstOrDefault(o => o.Id == id) is { } obj)
        {
            bool was = obj.CanBeUsed;
            obj.CanBeUsed = true;
            UseObject(u, obj);
            obj.CanBeUsed = was || obj.CanBeUsed;
        }
    }

    /// <summary>ENT_SetCapacityTarget(objet, « COULDBEUSE » / « COULDBEBREAK » / « COULDBEREPAIR », 0/1).</summary>
    public void SetCapacity(uint id, string capacity, bool on)
    {
        string cap = capacity.ToUpperInvariant();
        if (Openings.FirstOrDefault(d => d.Id == id) is { } d)
        {
            if (cap == "COULDBEBREAK")
                d.Breakable = on;
            else if (cap == "COULDBEUSE")
                d.Usable = on;
            return;
        }
        UsableObject? o = Usables.FirstOrDefault(x => x.Id == id);
        if (o == null)
        {
            LevelObject? lo = _s.Level.Objects.FirstOrDefault(x => x.Id == id);
            if (lo == null)
                return;
            o = new UsableObject { Id = lo.Id, Name = lo.Name, Position = lo.Position };
            ComputeBox(o, lo, lo.ClassPath.StartsWith("design\\", StringComparison.OrdinalIgnoreCase) ? Class("Design\\" + lo.ClassPath["design\\".Length..] + ".class") : null);
            if (lo.GetComponent(0xA027) is { } comp)
                o.Actions.AddRange(ParseActions(lo.ComponentBytes(comp).ToArray()));
            Usables.Add(o);
        }
        if (cap == "COULDBEUSE")
            o.CanBeUsed = on;
        else if (cap == "COULDBEREPAIR")
        {
            o.CanBeRepaired = on;
            if (on)
                o.Repaired = false;
        }
    }

    public bool GetCapacity(uint id, string capacity)
    {
        string cap = capacity.ToUpperInvariant();
        if (Openings.FirstOrDefault(d => d.Id == id) is { } d)
            return cap == "COULDBEBREAK" ? d.Breakable : cap == "COULDBEUSE" && d.Usable;
        if (Usables.FirstOrDefault(x => x.Id == id) is { } o)
            return cap == "COULDBEUSE" ? o.CanBeUsed : cap == "COULDBEREPAIR" && o.CanBeRepaired && !o.Repaired;
        return false;
    }

    // le pompier arrivé près de l'objet agit dessus pendant quelques secondes, puis le script de l'objet est appelé
    private void WorkUsable(Unit u, float dt)
    {
        if (u.UseTarget is not { } o || u.Moving || u.OnBoard || u.Unconscious || u.InjuredTimer > 0)
            return;
        Vector2 me = new Vector2(u.Position.X, u.Position.Y);
        Vector2 at = NearestPoint(o, me);
        if (Vector2.Distance(me, at) > ActionReach + 150)
        {
            u.UseTarget = null;
            u.Status = "trop loin de l'objet";
            return;
        }
        u.Heading = MathF.Atan2(at.Y - me.Y, at.X - me.X);
        bool repair = o.CanBeRepaired && !o.Repaired && u.CanRepair && !o.CanBeUsed;
        float need = repair ? RepairSeconds : UseObjectSeconds;
        u.ActionTimer += dt;
        u.Status = repair ? $"répare ({100 * u.ActionTimer / need:0} %)" : "utilise l'objet";
        if (u.ActionTimer < need)
            return;
        u.ActionTimer = 0;
        u.UseTarget = null;
        string kind = repair ? "repair" : "use";
        List<(string action, string script)> candidates = o.Actions.Where(a => a.action.StartsWith(kind, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
            candidates = o.Actions;
        (string action, string script)? act = candidates.Count > 0 ? candidates[o.Uses % candidates.Count] : ((string action, string script)?)null;
        o.Uses++;
        if (repair)
        {
            o.Repaired = true;
            // réparé : l'objet retrouve toute sa vie (Lab : le script attend ENT_GetDamage(générateur) == 100)
            if (_s.Explosions.ById.TryGetValue(o.Id, out ExplosiveItem? it) && !it.Exploded)
            {
                it.Life = it.MaxLife;
                it.Repaired = true;
            }
        }
        Sounds.Add((repair ? "SC_REPAIR" : "SC_USE_GENERIC", o.Position));
        Log.Add($"{_s.Time,6:0} s : {(repair ? "réparation" : "action")} sur {o.Name}" + (act is { } a0 ? $" ({a0.script})" : ""));
        u.Status = "attend les ordres";
        if (act is { } a1)
            _s.Scripts?.TryRun(a1.script, Value.I(u.Id), Value.I(o.Id), Value.T(a1.action));
        // fin d'une réparation : la mission est prévenue par OnRepairDone(technicien, machine) (Mriya, Military)
        if (repair && _s.Scripts is { } sc && sc.HasFunction("OnRepairDone"))
            sc.TryRun("OnRepairDone", Value.I(u.Id), Value.I(o.Id));
    }
}
