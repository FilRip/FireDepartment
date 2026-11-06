using System.Numerics;
using OpenFD.Fire;
using OpenFD.Formats;
using OpenFD.Script;

namespace OpenFD.Game;

/// <summary>
/// Une partie en cours : carte, feu, fumée, scripts de mission et unités qui arrosent.
/// Réglages par défaut = modèle retenu (docs/MODELE_FEU.md § 7 quinquies) : pièces calculées,
/// pas de voisinage par liens de type 0, distances 3D.
/// </summary>
public sealed class Session
{
    public string Mission { get; }
    public string Difficulty { get; }
    public Level Level { get; }
    /// <summary>Fichier de la mission (.mis) : [MISSION] Minimap, [DISPLAY]…</summary>
    public ClassFile MissionConfig { get; }
    public Grid? Grid { get; }
    public FireWorld Fire { get; }
    public SmokeSystem Smoke { get; }
    public IReadOnlyList<Rooms.Room> RoomList { get; }
    /// <summary>Pièce de chaque foyer et point de fumée (nom de la pièce, ou vide dehors).</summary>
    public IReadOnlyDictionary<uint, string> RoomOf { get; private set; } = new Dictionary<uint, string>();
    public Interpreter? Scripts { get; }
    public MissionHost? Host { get; }
    public List<(Extinguisher unit, HotPoint target)> Sprayers { get; } = new();
    public UnitManager Units { get; }
    /// <summary>Objets qui explosent quand le feu les a trop abîmés (voitures…).</summary>
    public ExplosionSystem Explosions { get; }
    private readonly string[] _mainLoops = Array.Empty<string>();
    private readonly GameFiles _files;
    private readonly Dictionary<string, ClassFile> _unitCache = new(StringComparer.OrdinalIgnoreCase);
    private int _steps;
    /// <summary>Pas de simulation faits depuis le début de la mission (0,1 s chacun).</summary>
    public int Steps => _steps;
    public const float Dt = 0.1f;
    /// <summary>Boucle principale des scripts toutes les 0,2 s, comme le jeu d'origine (Mriya : le camion de la cinématique
    /// fait ses 51 pas de 80 unités dans les 11 s que lui laisse le script ; l'avion traverse le hangar en 42 s).</summary>
    public const int MainLoopSteps = 2;
    public const float MainLoopPeriod = MainLoopSteps * Dt;

    public Session(GameFiles files, string mission, string difficulty = "EASY", bool runScripts = true)
    {
        _files = files;
        Mission = mission; Difficulty = difficulty.ToUpperInvariant();
        string lvPath = files.List("Design\\Mission\\" + mission + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
        Level = Level.Parse(files.Read(lvPath));
        Grid = files.TryRead(lvPath[..^".level".Length] + ".grid") is { } gb ? Grid.Parse(gb) : null;
        string misRel = "Design\\" + Level.MissionPath[(Level.MissionPath.IndexOf("Design\\", StringComparison.OrdinalIgnoreCase) + 7)..];
        ClassFile mis = ClassFile.Load(files, misRel);
        MissionConfig = mis;
        Fire = FireWorld.Load(Level, files, FireSettings.FromMission(mis, Difficulty));
        Fire.Proximity3D = true;
        RoomList = Rooms.Load(files, mission);
        Dictionary<uint, string> rooms = Rooms.Assign(Level, RoomList.ToList());
        RoomOf = rooms;
        Fire.AddProximityNeighbours(null, rooms);
        Smoke = SmokeSystem.Load(Level, files, Fire, rooms);
        Smoke.Walls = Grid;
        Units = new UnitManager(this, files, mis);
        Explosions = ExplosionSystem.Load(Level, files, Fire, ScriptDamageIds(files, mission));

        if (!runScripts)
            return;
        string scriptRoot = Path.Combine(files.DataDir, "Script");
        string[] dirs = new[] { Path.Combine(scriptRoot, "_Common"), Path.Combine(scriptRoot, "Mission", mission) };
        Host = new MissionHost(Level, Fire) { Explosions = Explosions, Units = Units, Difficulty = Difficulty switch { "EASY" => 0, "HARD" => 2, _ => 1 } };
        Scripts = new Interpreter(Host);
        foreach (string d in dirs.Where(Directory.Exists))
            foreach (string f in Directory.EnumerateFiles(d, "*.scp"))
            {
                try { Scripts.Load(Parser.ParseFile(File.ReadAllText(f, System.Text.Encoding.Latin1), Path.GetFileName(f))); }
                catch (Exception e) { Scripts.Log.Add($"{Path.GetFileName(f)} : {e.Message}"); }
            }
        // le script fait-il venir lui-même les équipes ?
        string text = string.Concat(dirs.Skip(1).Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*.scp")).Select(f => File.ReadAllText(f, System.Text.Encoding.Latin1)));
        bool Calls(string fn) => System.Text.RegularExpressions.Regex.IsMatch(text, @"^[^/\r\n]*\b" + fn + @"\s*\(", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Units.ScriptDispatch(Calls("LEV_CallInitialDispatch"), Calls("LEV_GrantReinforcement") || Calls("LEV_CallReinforcement") || Calls("LEV_GrantReinforcementSet"));
        _mainLoops = new[] { mis.Get("SCRIPT", "MissionScript1"), mis.Get("SCRIPT", "MissionScript2") }
            .Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToArray();
        string? autoexec = mis.Get("SCRIPT", "AutoExecScript");
        if (!string.IsNullOrEmpty(autoexec))
            Scripts.TryRun(autoexec!);
    }

    public float Time => Fire.Time;

    /// <summary>Une unité (classe du jeu, par défaut le médecin) arrose ce foyer jusqu'à extinction ou réserve vide.</summary>
    public Extinguisher Spray(HotPoint target, string unitClass = "Unit\\Fireman\\fireman_medic")
    {
        Extinguisher u = Extinguisher.FromClass(unitClass, ClassFile.Load(_files, "Design\\" + unitClass + ".class", _unitCache));
        Sprayers.Add((u, target));
        return u;
    }

    /// <summary>Objets dont les scripts de la mission suivent la vie (ENT_GetDamage / ENT_SetFireDamageCoef(numéro, …)).</summary>
    private static HashSet<uint> ScriptDamageIds(GameFiles files, string mission)
    {
        HashSet<uint> ids = new HashSet<uint>();
        string dir = Path.Combine(files.DataDir, "Script", "Mission", mission);
        if (!Directory.Exists(dir))
            return ids;
        foreach (string f in Directory.EnumerateFiles(dir, "*.scp"))
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f, System.Text.Encoding.Latin1), @"ENT_(?:GetDamage|SetFireDamageCoef)\s*\(\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                ids.Add(uint.Parse(m.Groups[1].Value));
        return ids;
    }

    public void Step()
    {
        if (Scripts != null)
        {
            Scripts.Update();
            if (_steps % MainLoopSteps == 0)
                foreach (string m in _mainLoops)
                    Scripts.RunMainLoop(m);
        }
        Sprayers.RemoveAll(s => !s.target.Lit || (!s.unit.InfiniteReserve && s.unit.Reserve <= 0));
        foreach ((Extinguisher? u, HotPoint? t) in Sprayers)
            u.Spray(Fire, t, Dt);
        Units.Update(Time, Dt);
        Fire.Step(Dt);
        Smoke.Step(Fire, Dt);
        Explosions.Step(Fire, Time, Dt);
        Units.UpdateBackdrafts(Time, Dt);
        _steps++;
    }

    public HotPoint? NearestHotPoint(Vector2 p, float maxDist) =>
        Fire.HotPoints.Select(h => (h, d: Vector2.Distance(new(h.Position.X, h.Position.Y), p)))
            .Where(x => x.d <= maxDist).OrderBy(x => x.h.Lit ? 0 : 1).ThenBy(x => x.d).Select(x => x.h).FirstOrDefault();
}
