using System.Numerics;
using OpenFD.Formats;

namespace OpenFD.Fire;

/// <summary>
/// Fumée (voir docs/MODELE_FEU.md § 9). Règles :
/// 1. un foyer allumé relié à un point de fumée (lien de type 2) le remplit (SmokeGenerationRate par seconde) ;
/// 2. dans une pièce (ou dehors), la fumée diffuse entre points actifs voisins, quantité conservée ;
/// 3. un point extracteur retire ExtractionRate par seconde ;
/// 4. flashover : pièce enfumée au-delà du seuil partout et sans foyer allumé → après le délai, tous les foyers
///    éteints de la pièce reçoivent HotPointIntensityDamage.
/// </summary>
public sealed class SmokeSystem
{
    public List<SmokePoint> Points { get; } = new();
    public Dictionary<uint, SmokePoint> ById { get; } = new();
    public List<(HotPoint hp, SmokePoint sp)> Sources { get; } = new();
    private readonly Dictionary<string, List<SmokePoint>> _rooms = new();
    private readonly Dictionary<string, List<HotPoint>> _roomHotPoints = new();
    private readonly Dictionary<string, (float timer, bool first)> _flashover = new();
    /// <summary>Vitesse d'échange (par seconde, pour un poids 1). Ajustée : 0,3.</summary>
    public float DiffusionRate = 0.3f;
    /// <summary>Si renseignée, deux points séparés par un mur de la grille n'échangent pas.</summary>
    public Grid? Walls;
    private List<(int i, int j, float w)>? _pairs;
    private float[] _delta = Array.Empty<float>();

    private void BuildPairs()
    {
        _pairs = new();
        _delta = new float[Points.Count];
        for (int i = 0; i < Points.Count; i++)
            for (int j = i + 1; j < Points.Count; j++)
            {
                SmokePoint a = Points[i]; SmokePoint b = Points[j];
                if (a.Room != b.Room)
                    continue;
                float r = a.Class.InfluenceRange + b.Class.InfluenceRange;
                float d = Vector2.Distance(new(a.Position.X, a.Position.Y), new(b.Position.X, b.Position.Y));
                if (d >= r)
                    continue;
                if (Walls != null && FireWorld.WallBetween(Walls, a.Position, b.Position))
                    continue;
                _pairs.Add((i, j, 1 - d / r));
            }
    }
    public List<string> Log { get; } = new();

    public static SmokeSystem Load(Level level, GameFiles files, FireWorld fire, IReadOnlyDictionary<uint, string>? rooms)
    {
        SmokeSystem sys = new SmokeSystem();
        Dictionary<string, ClassFile> cache = new Dictionary<string, ClassFile>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, SmokePointClass> classes = new Dictionary<string, SmokePointClass>(StringComparer.OrdinalIgnoreCase);
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.SmokePoint && o.HasTransform))
        {
            if (!classes.TryGetValue(o.ClassPath, out SmokePointClass? sc))
            {
                string rel = "Design\\" + o.ClassPath["design\\".Length..] + ".class";
                sc = SmokePointClass.From(o.ClassName, ClassFile.Load(files, rel, cache));
                classes[o.ClassPath] = sc;
            }
            SmokePoint sp = new SmokePoint { Id = o.Id, Name = o.Name, Class = sc, Position = o.Position, Max = sc.IntensityMax };
            // état initial : composant FE01, bloc v3 (+5 actif, +10 intensité, +14 max)
            if (o.GetComponent(0xFE01) is { } c)
            {
                ReadOnlySpan<byte> b = o.ComponentBytes(c);
                if (b.Length > 40 && Chunks.IsBegin(b, 17))
                {
                    sp.Active = b[27] != 0;
                    sp.Intensity = BitConverter.ToSingle(b[32..]);
                    sp.Max = BitConverter.ToSingle(b[36..]);
                }
            }
            if (rooms != null && rooms.TryGetValue(o.Id, out string? r))
                sp.Room = r;
            sys.Points.Add(sp); sys.ById[sp.Id] = sp;
        }
        foreach ((uint a, uint b, byte[]? data) in level.HotPointLinks)
        {
            if (data.Length < 26 || BitConverter.ToInt32(data, 13) != 2)
                continue;
            if (fire.ById.TryGetValue(a, out HotPoint? h1) && sys.ById.TryGetValue(b, out SmokePoint? s1))
                sys.Sources.Add((h1, s1));
            else if (fire.ById.TryGetValue(b, out HotPoint? h2) && sys.ById.TryGetValue(a, out SmokePoint? s2))
                sys.Sources.Add((h2, s2));
        }
        foreach (SmokePoint sp in sys.Points.Where(p => p.Room != ""))
        {
            if (!sys._rooms.TryGetValue(sp.Room, out List<SmokePoint>? l))
                sys._rooms[sp.Room] = l = new();
            l.Add(sp);
        }
        if (rooms != null)
            foreach (HotPoint hp in fire.HotPoints)
                if (rooms.TryGetValue(hp.Id, out string? r) && r != "")
                {
                    if (!sys._roomHotPoints.TryGetValue(r, out List<HotPoint>? l))
                        sys._roomHotPoints[r] = l = new();
                    l.Add(hp);
                }
        return sys;
    }

    public void Step(FireWorld fire, float dt)
    {
        // 1. production par les foyers reliés
        foreach ((HotPoint? hp, SmokePoint? sp) in Sources)
            if (hp.IsBurning)
                sp.Add(hp.Class.SmokeGenerationRate * dt);

        // 2. diffusion entre points actifs d'une même pièce (ou tous dehors) : quantité conservée.
        //    Deux points échangent si leurs zones d'influence se touchent (distance < Ri + Rj),
        //    avec un poids qui décroît linéairement avec la distance. Mesuré sur 3 sauvegardes de Fireworks.
        if (_pairs == null)
            BuildPairs();
        Array.Clear(_delta);
        foreach ((int i, int j, float w) in _pairs!)
        {
            SmokePoint a = Points[i]; SmokePoint b = Points[j];
            if (!a.Active && !b.Active)
                continue;   // un point vide reçoit de la fumée d'un voisin actif
            float f = DiffusionRate * w * (b.Intensity - a.Intensity) * dt;
            _delta[i] += f; _delta[j] -= f;
        }
        for (int i = 0; i < Points.Count; i++)
            if (_delta[i] != 0)
            {
                Points[i].Intensity = Math.Clamp(Points[i].Intensity + _delta[i], 0, Points[i].Max);
                if (Points[i].Intensity > 0)
                    Points[i].Active = true;
            }

        // 2 bis. portes ouvertes et fenêtres cassées : la fumée passe d'une pièce à l'autre (ou dehors)
        foreach ((int i, int j, float w) in _openingPairs)
        {
            SmokePoint a = Points[i];
            SmokePoint b = Points[j];
            float f = DiffusionRate * w * (b.Intensity - a.Intensity) * dt;
            a.Intensity = Math.Clamp(a.Intensity + f, 0, a.Max);
            b.Intensity = Math.Clamp(b.Intensity - f, 0, b.Max);
            if (a.Intensity > 0)
                a.Active = true;
            if (b.Intensity > 0)
                b.Active = true;
        }

        // 3. extraction
        foreach (SmokePoint p in Points)
            if (p.Class.Extractor && p.Intensity > 0)
                p.Intensity = Math.Max(0, p.Intensity - p.Class.ExtractionRate * dt);

        // 4. flashover par pièce
        foreach ((string? room, List<SmokePoint>? pts) in _rooms)
        {
            List<HotPoint>? hps = _roomHotPoints.TryGetValue(room, out List<HotPoint>? l) ? l : null;
            if (hps == null || hps.Count == 0)
                continue;
            bool smoky = pts.All(p => p.Intensity >= p.Class.FlashoverThreshold);
            bool burning = hps.Any(h => h.Lit);
            (float timer, bool first) st = _flashover.TryGetValue(room, out (float timer, bool first) s) ? s : (0f, true);
            if (!smoky || burning)
            {
                _flashover[room] = (0f, st.first);
                continue;
            }
            st.timer += dt;
            float tempo = st.first ? pts[0].Class.FirstFlashoverTempo : pts[0].Class.FlashoverTempo;
            if (st.timer >= tempo)
            {
                foreach (HotPoint h in hps.Where(h => !h.Lit))
                    fire.AddIntensity(h, pts[0].Class.HotPointIntensityDamage);
                Log.Add($"{fire.Time,6:0.0} flashover dans {room}");
                st = (0f, false);
            }
            _flashover[room] = st;
        }
    }

    public float Total => Points.Sum(p => p.Intensity);

    private List<(int i, int j, float w)> _openingPairs = new();
    /// <summary>
    /// Ouvertures (portes ouvertes, fenêtres cassées) : les points de fumée de pièces différentes situés à moins de
    /// leur InfluenceRange de l'ouverture échangent à travers elle, avec un poids (1 − da/Ra)(1 − db/Rb).
    /// La quantité totale reste conservée, comme mesuré dans le jeu ; la fumée qui sort se dilue dehors.
    /// (Règle estimée : le débit exact à travers une ouverture n'a pas encore été mesuré.)
    /// </summary>
    public void SetOpenings(IEnumerable<Vector3> openings)
    {
        _openingPairs = new();
        HashSet<(int, int)> seen = new HashSet<(int, int)>();
        foreach (Vector3 o in openings)
        {
            List<(int idx, float w)> near = new List<(int idx, float w)>();
            for (int k = 0; k < Points.Count; k++)
            {
                SmokePoint p = Points[k]; float r = p.Class.InfluenceRange;
                float d = Vector2.Distance(new(p.Position.X, p.Position.Y), new(o.X, o.Y));
                if (d < r && MathF.Abs(p.Position.Z - o.Z) < 400)
                    near.Add((k, 1 - d / r));
            }
            // une seule pièce autour (fenêtre sur rue, porte vers un couloir sans point de fumée) :
            // l'ouverture la relie au point le plus proche d'une autre pièce ou du dehors, à moins de 15 m
            if (near.Count > 0 && near.Select(n => Points[n.idx].Room).Distinct().Count() == 1)
            {
                string room = Points[near[0].idx].Room;
                int best = -1; float bd = 1500;
                for (int k = 0; k < Points.Count; k++)
                    if (Points[k].Room != room && MathF.Abs(Points[k].Position.Z - o.Z) < 400
                        && Vector2.Distance(new(Points[k].Position.X, Points[k].Position.Y), new(o.X, o.Y)) is float d && d < bd)
                    {
                        bd = d;
                        best = k;
                    }
                if (best >= 0)
                    near.Add((best, 0.5f));
            }
            foreach ((int a, float wa) in near)
                foreach ((int b, float wb) in near)
                    if (a != b && Points[a].Room != Points[b].Room && seen.Add((Math.Min(a, b), Math.Max(a, b))))
                        _openingPairs.Add((Math.Min(a, b), Math.Max(a, b), wa * wb));
        }
    }
}
