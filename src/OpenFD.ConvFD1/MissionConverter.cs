using System.Numerics;
using System.Text;
using OpenFD.Formats;
using OpenFD.MG;

namespace OpenFD.ConvFD1;

/// <summary>
/// Conversion d'une mission FD1 vers le dossier Data du moteur (FD3) :
///  • Gfx\Mission\&lt;nom&gt;\ : décor (&lt;nom&gt;.sg, un nœud par maillage) + .mesh/.meshdata, matériaux dans Shaders\ ;
///  • Gfx\Textures\&lt;nom&gt;\ : textures (DDS 32 bits) ;
///  • Design\Mission\&lt;nom&gt;\ : niveau (soleil, caméra, arrivée des équipes repris d'une mission du jeu), grille, .mis ;
///  • Script\Mission\&lt;nom&gt;\ : script de départ.
/// </summary>
public sealed partial class MissionConverter
{
    /// <summary>Monde FD1 (1 = 10 m) → cm.</summary>
    public const float WorldToCm = 1000f;

    public required Fd1Data Fd1 { get; init; }
    public required GameFiles Fd3 { get; init; }
    /// <summary>Dossier de la mission FD1 (Logic\Missions\&lt;Mission&gt;).</summary>
    public required string Mission { get; init; }
    /// <summary>Nom de la mission convertie (dossiers et fichiers du moteur).</summary>
    public required string Name { get; init; }
    public List<string> Report { get; } = new();

    /// <summary>
    /// Classes FD1 qui ne sont pas du décor : personnages (remplacés par des unités et des civils du moteur), véhicules des
    /// pompiers (« Tanker », « MMaster » : remplacés par les véhicules de l'équipe de départ), fanions des points de passage.
    /// </summary>
    private static readonly HashSet<string> NotScenery = new(StringComparer.OrdinalIgnoreCase) { "ActeurUnit", "Tanker", "MMaster", "WayPoint", "FireFighter", "Aerial", "WaterTower" };

    /// <summary>Véhicules des pompiers FD1 (classe) → véhicule du moteur.</summary>
    private static readonly Dictionary<string, string> Vehicles = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Tanker", "Firevehicle\\Veh_P_pumper_us" },
        { "MMaster", "Firevehicle\\Veh_P_ambulance_us" },
        // Banque : le « Pumper » (lance à eau), la grande échelle et la tour d'eau
        { "FireFighter", "Firevehicle\\Veh_P_pumper_us" },
        { "Aerial", "Firevehicle\\Veh_P_aerial_us" },
        { "WaterTower", "Firevehicle\\Veh_P_watertower_us" },
    };

    /// <summary>
    /// Poteaux de barrière, par mission (modèle FD1) : un mur les relie l'un à l'autre (BarrierWalls). Banque : les
    /// poteaux des guichets du comptoir en arc de cercle du hall.
    /// </summary>
    private static readonly Dictionary<string, string[]> Barriers = new(StringComparer.OrdinalIgnoreCase)
    {
        { "banque", new[] { "CLONETTE_ETA01" } },
    };

    /// <summary>
    /// Délai du renfort (s) quand le .MIS FD1 n'en donne pas de juste, par mission. Banque : [REP_TIME] y est en
    /// commentaire ; le script FD1 fait rouler les véhicules du renfort dès son appel (acte 3), sous la cinématique.
    /// </summary>
    private static readonly Dictionary<string, int> ReinforcementDelays = new(StringComparer.OrdinalIgnoreCase)
    {
        { "banque", 1 },
        // Quartier : le script FD1 appelle le renfort (acte 3) quand il entre dans la carte
        { "quartier", 1 },
        // Station-service : le script FD1 fait entrer l'échelle et le bras élévateur à l'acte 4 (ArriveeMega)
        { "station_service", 1 },
        // Train : l'hélicoptère dépose les deux pompiers ignifugés à l'acte 3 (HelicoCutScene)
        { "train", 1 },
    };

    /// <summary>Véhicules FD1 (classe) qui arrivent avec le premier renfort, par mission (Banque : acte 3).</summary>
    private static readonly Dictionary<string, string[]> ReinforcementVehicles = new(StringComparer.OrdinalIgnoreCase)
    {
        { "banque", new[] { "Aerial", "WaterTower" } },
        { "station_service", new[] { "Aerial", "WaterTower" } },
    };

    /// <summary>
    /// Unités FD1 du premier renfort désignées par le début du nom de leur instance, par mission. Quartier : « Renfort_Tanker »,
    /// « Renfort_WaterTower », « Renfort_Aerial », « Renfort_Pompier1 »… (acte 3, par le parking au nord).
    /// </summary>
    private static readonly Dictionary<string, string> ReinforcementPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "quartier", "Renfort_" },
    };

    /// <summary>Entrée des véhicules du renfort, par mission, quand ce n'est pas celle du départ (cm).</summary>
    private static readonly Dictionary<string, Vector2> ReinforcementEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        // Quartier : la rue au nord, derrière le parking
        { "quartier", new Vector2(24500, 21100) },
        // Train : les deux pompiers de l'acte 3, sur la route près de l'équipe de départ (FD1 : déposés par l'hélicoptère sur
        // la colline)
        { "train", new Vector2(10700, 9300) },
    };

    /// <summary>
    /// Unités FD1 du premier renfort désignées par le nom de leur instance, par mission. Station-service : les deux pompiers
    /// qui arrivent avec l'échelle (ArriveeMega3).
    /// </summary>
    private static readonly Dictionary<string, string[]> ReinforcementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        { "station_service", new[] { "Pompier5", "Pompier6" } },
        // Train : les deux pompiers que l'hélicoptère dépose à l'acte 3 (l'un ignifugé, l'autre non dans FD1)
        { "train", new[] { "FireProof1", "FireProof2" } },
    };

    /// <summary>
    /// Unités FD1 laissées de côté, par mission (nom de l'instance) : Train, l'hélicoptère qui dépose le renfort (une
    /// ambulance pour FD1 : il ne doit pas entrer dans l'équipe de départ).
    /// </summary>
    private static readonly Dictionary<string, string[]> ExcludedUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        { "train", new[] { "Helico" } },
    };

    /// <summary>Cette unité FD1 vient-elle avec le premier renfort (par le nom de son instance) ?</summary>
    private bool IsReinforcementInstance(Fd1Object o)
    {
        string? n = InstanceName(o);
        if (n == null)
            return false;
        if (ReinforcementPrefixes.TryGetValue(Mission, out string? prefix) && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return true;
        return ReinforcementNames.TryGetValue(Mission, out string[]? names) && names.Contains(n, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Pompiers FD1 (nom de l'objet) → pompier du moteur, et s'il vient en renfort (sinon dans l'équipe de départ).</summary>
    private static readonly Dictionary<string, (string unit, bool reinforcement)> Firemen = new(StringComparer.OrdinalIgnoreCase)
    {
        { "pompier_pompier", ("Fireman\\fireman_sp_us", false) },
        { "pompier_Engenieur", ("Fireman\\fireman_ing", false) },
        { "pompier_commandos", ("Fireman\\fireman_scout", true) },
        { "pompier_FireProof", ("Fireman\\fireman_igni", true) },
        { "pompier_Medic", ("Fireman\\fireman_medic", false) },
        // Villa : le sapeur (un pompier comme les autres pour le moteur)
        { "pompier_sapeur", ("Fireman\\fireman_sp_us", false) },
    };

    /// <summary>
    /// Entrée des véhicules sur la carte, par mission (cm) : la rue devant le portail du parking (Entrepot). Ailleurs : à 3 000 cm
    /// du point d'arrivée, vers le bas de la carte.
    /// </summary>
    private static readonly Dictionary<string, Vector2> Entries = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Entrepot", new Vector2(21300, 10250) },
        // Banque : la grande avenue devant la banque, depuis l'est
        { "banque", new Vector2(23500, 17450) },
        // Villa : la route devant la propriété, depuis l'est
        { "Villa", new Vector2(23500, 4000) },
        // Quartier : la rue devant l'ambassade, depuis l'ouest
        { "quartier", new Vector2(300, 4600) },
        // Train : la route du passage à niveau, depuis le sud-ouest
        { "train", new Vector2(11000, 500) },
    };

    private string Data => Fd3.DataDir;

    /// <summary>Hauteur maximale des murs d'une pièce (cm) : au-dessus, ce n'est plus la pièce (façade, toit du hall).</summary>
    private const float RoomMaxHeight = 450;

    /// <summary>
    /// Objets du décor qui gênent le passage (étagères, caisses, véhicules) : carrés de 25 cm couverts à hauteur d'homme.
    /// Le moteur ne relève que les murs (faces du sol à plus de 2,3 m) ; les étagères FD1 sont faites de cartons empilés,
    /// aucune face ne va du sol au plafond : on ferme leurs cases dans la grille (zone 0), comme la grille d'une mission du jeu.
    /// </summary>
    private readonly HashSet<(int x, int y)> _blockedQuarters = new HashSet<(int, int)>();

    /// <summary>
    /// Plus grand objet (m², rectangle englobant au sol) pris comme obstacle : au-delà, c'est un bâtiment (ses murs : le
    /// moteur). Les objets qui brûlent (« ConteneurComposant » : étagères, caisses, meubles) ne sont jamais des bâtiments :
    /// limite plus large (une étagère tombée en biais, 9 m de long, occupe un rectangle de 70 m²).
    /// </summary>
    private const float ObstacleMaxArea = 60;
    private const float BurnableObstacleMaxArea = 150;

    /// <summary>
    /// Zones hors jeu, par mission : modèles dont toute l'emprise au sol est fermée (Entrepot : « CORNICHE », le bloc noir
    /// entre les deux halls ; ses fausses portes, sous l'auvent et dans le mur de briques, ne mènent nulle part).
    /// </summary>
    private static readonly Dictionary<string, string[]> NoPlayModels = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Entrepot", new[] { "CORNICHE" } },
    };

    /// <summary>
    /// Fausses portes, par mission : modèles fermés comme un mur (Entrepot : « ENT_PT_PORTE_ETAT01 », la porte de la cour
    /// contre le hall de droite, qui ne mène nulle part).
    /// </summary>
    private static readonly Dictionary<string, string[]> FakeDoorModels = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Entrepot", new[] { "ENT_PT_PORTE_ETAT01" } },
    };

    /// <summary>
    /// Toits FD1 sans « TOIT » dans le nom de leur modèle, par mission. Villa : le dôme noir posé sur la serre tropicale
    /// (« Cache_Toit_Serre_Tropicale », enlevé à l'acte 4), ses vitres (« dom_01 »… : transparentes dans FD1, opaques ici :
    /// enlevées à l'acte 4 aussi) et son armature (« Toit_SerreTropicale »).
    /// </summary>
    private static readonly Dictionary<string, string[]> RoofModels = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Villa", new[] { "VILLA_SERRE_DOM_NOIR", "DOM_01", "VILLA_SERRE_STRUCT" } },
    };

    /// <summary>
    /// Modèles du décor descendus de quelques centimètres, par mission (modèle FD1, cm) : Station-service, la grande plaque
    /// de trottoir et de rue « STA_REBORD_PATCH2 », au niveau du sol, dont la texture a une bande noire sur son bord : les
    /// dalles du sol passent devant.
    /// </summary>
    private static readonly Dictionary<string, (string model, float dz)[]> LoweredModels = new(StringComparer.OrdinalIgnoreCase)
    {
        { "station_service", new[] { ("STA_REBORD_PATCH2", -3f) } },
    };

    /// <summary>
    /// Dalles de sol absentes de FD1 à boucher, par mission : dalle (rang de 10 m), dalle voisine dont on prend la texture.
    /// Station-service : entre la supérette et le chantier, devant le camion-citerne (la bande noire de STA_REBORD_PATCH2
    /// s'y voyait).
    /// </summary>
    private static readonly Dictionary<string, (int x, int y, int fromX, int fromY)[]> GroundFills = new(StringComparer.OrdinalIgnoreCase)
    {
        { "station_service", new[] { (14, 13, 14, 14), (15, 13, 15, 14), (16, 13, 16, 14) } },
    };

    /// <summary>Emprises au sol des zones hors jeu (cm : min, max).</summary>
    private readonly List<(Vector2 min, Vector2 max)> _noPlay = new List<(Vector2, Vector2)>();

    /// <summary>Classes FD1 qui ne ferment pas le passage : mécanismes (portes, boutons, bombes : on doit pouvoir y aller).</summary>
    private static readonly HashSet<string> NotObstacle = new(StringComparer.OrdinalIgnoreCase) { "Mechanisme" };

    /// <summary>Relève les carrés de 25 cm couverts par un objet entre 30 cm et 1,7 m au-dessus de son pied.</summary>
    private void AddObstacle(List<Fd3Mesh> meshes, Matrix4x4 w, Vector3 lo, Vector3 hi, string fd1Class, float floor = 0, bool barrier = false)
    {
        float max = fd1Class.Equals("ConteneurComposant", StringComparison.OrdinalIgnoreCase) ? BurnableObstacleMaxArea : ObstacleMaxArea;
        if ((hi.X - lo.X) * (hi.Y - lo.Y) / 10000f > max || hi.Z - lo.Z < 60 || lo.Z > floor + 50)
            return;
        const float Q = 25;
        float low = lo.Z + 30;
        float high = lo.Z + 170;
        HashSet<(int x, int y)> mine = new HashSet<(int, int)>();
        foreach (Fd3Mesh m in meshes)
            for (int t = 0; t + 2 < m.Indices.Count; t += 3)
            {
                Vector3 a = Vector3.Transform(Vertex(m, m.Indices[t]), w);
                Vector3 b = Vector3.Transform(Vertex(m, m.Indices[t + 1]), w);
                Vector3 c = Vector3.Transform(Vertex(m, m.Indices[t + 2]), w);
                if (MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) < low || MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) > high)
                    continue;
                float len = MathF.Max(Vector3.Distance(a, b), MathF.Max(Vector3.Distance(b, c), Vector3.Distance(a, c)));
                int n = Math.Clamp((int)(len / (Q / 2)) + 1, 1, 400);
                for (int i = 0; i <= n; i++)
                    for (int j = 0; i + j <= n; j++)
                    {
                        Vector3 p = a + (b - a) * ((float)i / n) + (c - a) * ((float)j / n);
                        if (p.Z >= low && p.Z <= high)
                            mine.Add(((int)MathF.Floor(p.X / Q), (int)MathF.Floor(p.Y / Q)));
                    }
            }
        if (floor > 0)
        {
            // meuble d'un étage (MissionConverter.Upper.cs : appliqué sur les cases de ce plancher)
            UpperQuarters(_upperBlockedQuarters, floor).UnionWith(mine);
            return;
        }
        _blockedQuarters.UnionWith(mine);
        // poteau d'une barrière (Banque : le comptoir du hall) : relié aux suivants (BarrierWalls)
        if (barrier)
            _barrierPosts.Add(new Vector2((lo.X + hi.X) / 2, (lo.Y + hi.Y) / 2));
        if (fd1Class.Equals("ConteneurComposant", StringComparison.OrdinalIgnoreCase))
            CloseGaps(mine);
    }

    /// <summary>Poteaux des barrières (Barriers), au sol.</summary>
    private readonly List<Vector2> _barrierPosts = new List<Vector2>();

    /// <summary>
    /// Barrières : de poteau en poteau voisin (moins de 7 m, dans l'ordre des x), un mur à travers lequel on ne passe pas.
    /// Banque : le comptoir en arc de cercle entre les poteaux des guichets (le comptoir lui-même est dans le bâtiment du
    /// hall, trop bas pour en être un mur).
    /// </summary>
    private void BarrierWalls()
    {
        const float Q = 25;
        List<Vector2> posts = _barrierPosts.OrderBy(p => p.X).ToList();
        for (int i = 0; i + 1 < posts.Count; i++)
        {
            Vector2 a = posts[i];
            Vector2 b = posts[i + 1];
            float len = Vector2.Distance(a, b);
            if (len > 700)
                continue;
            for (float t = 0; t <= len; t += Q / 2)
            {
                Vector2 p = a + (b - a) * (t / len);
                _wallQuarters.Add(((int)MathF.Floor(p.X / Q), (int)MathF.Floor(p.Y / Q)));
                // deux carrés côte à côte : une case traversée en biais compte au moins deux carrés (règle des murs)
                _wallQuarters.Add(((int)MathF.Floor((p.X + Q / 2) / Q), (int)MathF.Floor(p.Y / Q)));
                _wallQuarters.Add(((int)MathF.Floor(p.X / Q), (int)MathF.Floor((p.Y + Q / 2) / Q)));
            }
        }
    }

    /// <summary>Carrés de 25 cm traversés par un mur de bâtiment à hauteur d'homme (AddWalls).</summary>
    private readonly HashSet<(int x, int y)> _wallQuarters = new HashSet<(int, int)>();

    /// <summary>
    /// Murs des bâtiments, même en plusieurs morceaux : sous une fenêtre FD1 (non cassable), le mur est un muret, une vitre et
    /// un linteau ; aucune face ne va du sol à 2,3 m, le moteur n'y voit pas de mur et on passe à travers. Ici : toute face
    /// verticale large (70 cm au moins : pas les montants d'une porte) qui part de moins de 1,2 m (pas les murs de l'étage) et
    /// passe entre 60 cm et 1,7 m. Une porte n'a rien à cette hauteur : elle reste ouverte.
    /// </summary>
    private void AddWalls(List<Fd3Mesh> meshes, Matrix4x4 w, float floor = 0)
    {
        const float Q = 25;
        // murs d'un étage : mêmes règles, à partir de son plancher (MissionConverter.Upper.cs)
        HashSet<(int x, int y)> target = floor > 0 ? UpperQuarters(_upperWallQuarters, floor) : _wallQuarters;
        foreach (Fd3Mesh m in meshes)
            for (int t = 0; t + 2 < m.Indices.Count; t += 3)
            {
                Vector3 a = Vector3.Transform(Vertex(m, m.Indices[t]), w);
                Vector3 b = Vector3.Transform(Vertex(m, m.Indices[t + 1]), w);
                Vector3 c = Vector3.Transform(Vertex(m, m.Indices[t + 2]), w);
                Vector3 nrm = Vector3.Cross(b - a, c - a);
                float len = nrm.Length();
                if (len < 1e-3f || MathF.Abs(nrm.Z) / len >= 0.25f)
                    continue;
                float zLow = MathF.Min(a.Z, MathF.Min(b.Z, c.Z));
                float zTop = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
                if (zLow > floor + 120 || zTop < floor + 60)
                    continue;
                float span = MathF.Max(Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y)),
                    MathF.Max(Vector2.Distance(new Vector2(b.X, b.Y), new Vector2(c.X, c.Y)), Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(c.X, c.Y))));
                if (span < 70)
                    continue;
                float edge = MathF.Max(Vector3.Distance(a, b), MathF.Max(Vector3.Distance(b, c), Vector3.Distance(a, c)));
                int n = Math.Clamp((int)(edge / (Q / 2)) + 1, 1, 800);
                for (int i = 0; i <= n; i++)
                    for (int j = 0; i + j <= n; j++)
                    {
                        Vector3 p = a + (b - a) * ((float)i / n) + (c - a) * ((float)j / n);
                        if (p.Z >= floor + 60 && p.Z <= floor + 170)
                            target.Add(((int)MathF.Floor(p.X / Q), (int)MathF.Floor(p.Y / Q)));
                    }
            }
    }

    /// <summary>Cases ajoutées entre deux parties d'un même objet (CloseGaps).</summary>
    private readonly HashSet<(int x, int y)> _gapCells = new HashSet<(int, int)>();

    /// <summary>
    /// Étagère tombée (rangées de cartons couchées) : une case libre entre deux cases de l'objet, sur une même ligne, est
    /// fermée aussi ; sinon on marcherait entre les rangées.
    /// </summary>
    private void CloseGaps(HashSet<(int x, int y)> quarters)
    {
        int per = (int)(Grid.CellSize / 25);
        HashSet<(int x, int y)> cells = quarters.GroupBy(q => (Math.DivRem(q.x, per, out _), Math.DivRem(q.y, per, out _)))
            .Where(g => g.Count() > per * per / 2).Select(g => g.Key).ToHashSet();
        foreach ((int x, int y) in cells.ToList())
            foreach ((int dx, int dy) in new[] { (1, 0), (0, 1) })
                if (!cells.Contains((x + dx, y + dy)) && cells.Contains((x + 2 * dx, y + 2 * dy)))
                    _gapCells.Add((x + dx, y + dy));
    }

    private static Vector3 Vertex(Fd3Mesh m, int i) => new Vector3(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]);

    /// <summary>Cases de la grille fermées par les obstacles : l'objet en couvre plus de la moitié (8 carrés sur 16).</summary>
    private int BlockObstacles(Grid g)
    {
        Dictionary<int, int> cover = new Dictionary<int, int>();
        int per = (int)(Grid.CellSize / 25);
        foreach ((int x, int y) in _blockedQuarters)
        {
            int cx = Math.DivRem(x, per, out _);
            int cy = Math.DivRem(y, per, out _);
            if (x < 0 || y < 0 || !g.Inside(cx, cy))
                continue;
            int k = g.Index(cx, cy);
            cover[k] = cover.GetValueOrDefault(k) + 1;
        }
        int n = 0;
        foreach ((int k, int c) in cover)
            if (c > per * per / 2)
            {
                g.ZoneA[k] = 0;
                n++;
            }
        // murs : une case traversée en deux carrés au moins (un mur est mince : une rangée de carrés)
        Dictionary<int, int> wall = new Dictionary<int, int>();
        foreach ((int x, int y) in _wallQuarters)
        {
            int cx = Math.DivRem(x, per, out _);
            int cy = Math.DivRem(y, per, out _);
            if (x >= 0 && y >= 0 && g.Inside(cx, cy))
                wall[g.Index(cx, cy)] = wall.GetValueOrDefault(g.Index(cx, cy)) + 1;
        }
        foreach ((int k, int c) in wall)
            if (c >= 2 && g.ZoneA[k] != 0)
            {
                g.ZoneA[k] = 0;
                n++;
            }
        // zones hors jeu : toutes les cases dont le centre est dans l'emprise
        foreach ((Vector2 min, Vector2 max) in _noPlay)
            for (int cy = (int)(min.Y / Grid.CellSize); cy <= (int)(max.Y / Grid.CellSize); cy++)
                for (int cx = (int)(min.X / Grid.CellSize); cx <= (int)(max.X / Grid.CellSize); cx++)
                {
                    Vector2 c = new Vector2((cx + 0.5f) * Grid.CellSize, (cy + 0.5f) * Grid.CellSize);
                    if (g.Inside(cx, cy) && c.X >= min.X && c.X <= max.X && c.Y >= min.Y && c.Y <= max.Y && g.ZoneA[g.Index(cx, cy)] != 0)
                    {
                        g.ZoneA[g.Index(cx, cy)] = 0;
                        n++;
                    }
                }
        foreach ((int x, int y) in _gapCells)
            if (g.Inside(x, y) && g.ZoneA[g.Index(x, y)] != 0)
            {
                g.ZoneA[g.Index(x, y)] = 0;
                n++;
            }
        return n;
    }

    public void Run()
    {
        string misDir = Directory.EnumerateDirectories(Path.Combine(Fd1.Root, "Logic", "Missions"))
            .FirstOrDefault(d => Path.GetFileName(d).Equals(Mission, StringComparison.OrdinalIgnoreCase))
            ?? throw new DirectoryNotFoundException($"mission FD1 introuvable : Logic\\Missions\\{Mission}");
        string xfl = Directory.EnumerateFiles(misDir, "*.xfl").FirstOrDefault()
            ?? throw new FileNotFoundException($"niveau .xfl introuvable dans {misDir}");
        Fd1Level level = Fd1Level.Load(File.ReadAllBytes(xfl));
        _fd1 = level;
        ReadCinematics(misDir);
        Report.Add($"{level.Objects.Count} objets dans {Path.GetFileName(xfl)}");

        ModelConverter models = new ModelConverter(Fd1, Path.GetFileName(misDir));
        // unités faites d'objets FD1 (Villa : la pompe de la piscine, l'Excalibur) : leurs textures avec celles du décor
        PrepareUnitModels(level, models);
        Dictionary<string, List<Fd3Mesh>> cache = new Dictionary<string, List<Fd3Mesh>>(StringComparer.OrdinalIgnoreCase);
        string gfx = Path.Combine(Data, "Gfx", "Mission", Name);
        Directory.CreateDirectory(Path.Combine(gfx, "Shaders"));
        List<(string node, Matrix4x4 world)> nodes = new List<(string, Matrix4x4)>();
        HashSet<string> materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Vector3 lo = new Vector3(float.MaxValue);
        Vector3 hi = new Vector3(float.MinValue);
        int count = 0;
        // pièces du niveau : modèles nommés dans les zones de son « SMAP_&lt;zone&gt;_&lt;modèle&gt;_&lt;n&gt; » (une par pièce)
        List<string> smaps = level.Strings.Select(x => x.text).Where(t => t.StartsWith("SMAP_", StringComparison.Ordinal)).ToList();
        bool IsRoomModel(string model) => !model.StartsWith("SOL", StringComparison.OrdinalIgnoreCase)
            && smaps.Any(t => t.Contains("_" + model + "_", StringComparison.OrdinalIgnoreCase));
        List<(string name, Vector3 min, Vector3 max)> rooms = new List<(string, Vector3, Vector3)>();
        foreach (Fd1Object o in level.Objects.Where(o => !NotScenery.Contains(o.Class)))
        {
            // couvercles des petites pièces (« PIECES_TOP ») : un toit par pièce
            bool lids = o.Model.EndsWith("_TOP", StringComparison.OrdinalIgnoreCase);
            if (!cache.TryGetValue(o.Model, out List<Fd3Mesh>? meshes))
            {
                meshes = models.Convert(o.Model, "fd1_" + ModelConverter.Safe(o.Model).ToLowerInvariant());
                LiftSurfaces(o.Model, meshes);
                if (lids)
                    meshes = meshes.SelectMany(ModelConverter.Islands).ToList();
                cache[o.Model] = meshes;
            }
            if (meshes.Count == 0)
                continue;
            // objets que le script suit (étagères qui tombent, machines, camion : MissionConverter.Objects.cs), pas du décor
            string? instance = InstanceName(o);
            // objets FD1 remplacés ou laissés de côté (Banque : l'hélicoptère du décor, les portes coupe-feu ouvertes)
            if (instance != null && HiddenDecor.TryGetValue(Mission, out string[]? hidden) && hidden.Contains(instance, StringComparer.OrdinalIgnoreCase))
                continue;
            if (ScriptObjectRole(instance) is { } objectRole)
            {
                // un objet FD1 en double (Villa : deux voitures l'une dans l'autre) n'en fait qu'un
                if (_scriptObjects.Any(x => x.name.Equals(instance, StringComparison.OrdinalIgnoreCase)))
                    continue;
                _scriptObjects.Add((o, meshes, instance!, objectRole.role, objectRole.script));
                foreach (Fd3Mesh m in meshes)
                    foreach ((string mat, int _, int _) in m.Groups)
                        materials.Add(mat);
                // machines et camion : toujours là, leurs cases restent fermées dans la grille (les étagères, cachées ou
                // montrées par le script, ferment leurs cases par le moteur)
                // (la voiture à conduire non plus : le véhicule qui la remplace part de là)
                if (objectRole.role is not (ObjectRole.Standing or ObjectRole.Fallen or ObjectRole.Drivable))
                {
                    Matrix4x4 ow = o.World;
                    ow.M41 *= WorldToCm;
                    ow.M42 *= WorldToCm;
                    ow.M43 *= WorldToCm;
                    List<Vector3> op = meshes.SelectMany(m => Enumerable.Range(0, m.VertexCount).Select(i => Vector3.Transform(Vertex(m, i), ow))).ToList();
                    AddObstacle(meshes, ow, new Vector3(op.Min(p => p.X), op.Min(p => p.Y), op.Min(p => p.Z)), new Vector3(op.Max(p => p.X), op.Max(p => p.Y), op.Max(p => p.Z)), o.Class);
                }
                continue;
            }
            // portes ouvertes par le script et boutons qui les ouvrent (MissionConverter.Upper.cs), pas du décor
            string? scriptDoor = ScriptDoorName(o.Model, instance);
            // bouton : par le nom de l'instance (Banque : « Bobine3 », « CoffreMec1 ») ou par celui de l'objet (Entrepot)
            (string door, string message)? scriptButton = instance != null ? ScriptButtonOf(instance) : null;
            Fd1Object buttonObject = o;
            if (scriptButton != null)
                buttonObject = new Fd1Object { Model = o.Model, Class = o.Class, Name = instance!, Race = o.Race, World = o.World, Offset = o.Offset };
            else
                scriptButton = ScriptButtonOf(o.Name);
            if (scriptDoor != null || scriptButton != null)
            {
                // une porte FD1 en double (Banque : deux portes blindées l'une sur l'autre) n'en fait qu'une
                if (scriptDoor != null && _scriptDoors.Any(d => d.name == scriptDoor))
                    continue;
                if (scriptDoor != null)
                    _scriptDoors.Add((o, meshes, scriptDoor));
                else if (PlaceButton(buttonObject, meshes) is { } placed)
                    _scriptButtons.Add((placed.o, placed.meshes, scriptButton!.Value.door, scriptButton.Value.message));
                foreach (Fd3Mesh m in meshes)
                    foreach ((string mat, int _, int _) in m.Groups)
                        materials.Add(mat);
                continue;
            }
            // bombes : un objet explosif du moteur par bombe (MissionConverter.Bombs.cs), pas du décor ; la charge cachée
            // (conteneurcomposant) se confond avec le mécanisme, seul celui-ci est gardé
            if (IsBomb(o))
            {
                int n = BombNumber(level, o);
                if (o.Class.Equals("Mechanisme", StringComparison.OrdinalIgnoreCase) && n >= 0)
                {
                    _bombs.Add((o, n, meshes));
                    Matrix4x4 bw = o.World;
                    bw.M41 *= WorldToCm;
                    bw.M42 *= WorldToCm;
                    bw.M43 *= WorldToCm;
                    IEnumerable<Vector3> bp = meshes.SelectMany(m => Enumerable.Range(0, m.VertexCount).Select(i => Vector3.Transform(Vertex(m, i), bw)));
                    AddObstacle(meshes, bw, new Vector3(bp.Min(p => p.X), bp.Min(p => p.Y), bp.Min(p => p.Z)), new Vector3(bp.Max(p => p.X), bp.Max(p => p.Y), bp.Max(p => p.Z)), "conteneurcomposant");
                    foreach (Fd3Mesh m in meshes)
                        foreach ((string mat, int _, int _) in m.Groups)
                            materials.Add(mat);
                }
                if (n >= 0)
                    continue;
            }
            // grandes portes : des portes du moteur (MissionConverter.Doors.cs), pas du décor
            if (IsBigDoor(o.Model) || IsBreakableDoor(o, UpperFloorHeights()))
            {
                if (IsBigDoor(o.Model))
                    _bigDoors.Add((o, meshes));
                else
                    _breakableDoors.Add((o, meshes));
                foreach (Fd3Mesh m in meshes)
                    foreach ((string mat, int _, int _) in m.Groups)
                        materials.Add(mat);
                continue;
            }
            Matrix4x4 w = o.World;
            w.M41 *= WorldToCm;
            w.M42 *= WorldToCm;
            w.M43 *= WorldToCm;
            if (LoweredModels.TryGetValue(Mission, out (string model, float dz)[]? lowered))
                foreach ((string lm, float dz) in lowered)
                    if (lm.Equals(o.Model, StringComparison.OrdinalIgnoreCase))
                        w.M43 += dz;
            count++;
            // plancher en hauteur (dessus du bloc noir d'Entrepot) : des cases de la grille à sa hauteur
            if (UpperFloorOf(o.Model) is float upper)
                _upperFloors.Add((meshes, w, upper, SaggingFloors.TryGetValue(Mission, out string[]? sag) && sag.Contains(o.Model, StringComparer.OrdinalIgnoreCase)));
            // toit FD1 (« TOIT_ENTREPOT_01 », « ENT_TOIT_MANUFACTURE ») : nœud « …_ROOF », tout entier un toit pour le moteur
            // (il part quand un pompier passe dessous) ; le reste des bâtiments est coupé à hauteur d'étage par le moteur
            bool roof = lids || o.Model.Contains("TOIT", StringComparison.OrdinalIgnoreCase)
                || (RoofModels.TryGetValue(Mission, out string[]? roofModels) && roofModels.Contains(o.Model, StringComparer.OrdinalIgnoreCase));
            // cache noir FD1 d'une pièce pas encore découverte (« PIECE_01_CACHE ») : couvercle « …_MASK » du moteur
            bool mask = o.Model.Contains("CACHE", StringComparison.OrdinalIgnoreCase);
            bool room = IsRoomModel(o.Model);
            Vector3 rlo = new Vector3(float.MaxValue);
            Vector3 rhi = new Vector3(float.MinValue);
            List<float> heights = new List<float>();
            foreach (Fd3Mesh m in meshes)
            {
                string node = $"{m.Name}_{count}" + (mask ? "_ROOM_MASK" : roof ? "_ROOF" : "");
                Fd3Mesh copy = new Fd3Mesh { Name = node, Positions = m.Positions, Normals = m.Normals, Uv = m.Uv, Indices = m.Indices, Groups = m.Groups };
                File.WriteAllBytes(Path.Combine(gfx, node + ".mesh"), Fd3Writer.MeshFile(copy));
                File.WriteAllBytes(Path.Combine(gfx, node + ".meshdata"), Fd3Writer.MeshData(copy));
                nodes.Add((node, w));
                // toit FD1 : une cinématique qui regarde dessous l'enlève (MissionConverter.Cinematics.cs)
                if (roof && !lids)
                    _covers.Add((node, MeshBox(m, w)));
                // toit que le script enlève (Villa : « Toit_Garage »…)
                if (roof && !lids && instance != null)
                {
                    if (!_roofNodes.TryGetValue(instance, out List<string>? roofNodes))
                        _roofNodes[instance] = roofNodes = new List<string>();
                    roofNodes.Add(node);
                }
                foreach ((string mat, int _, int _) in m.Groups)
                    materials.Add(mat);
                for (int i = 0; i < m.VertexCount; i++)
                {
                    Vector3 p = Vector3.Transform(new Vector3(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]), w);
                    lo = Vector3.Min(lo, p);
                    hi = Vector3.Max(hi, p);
                    rlo = Vector3.Min(rlo, p);
                    rhi = Vector3.Max(rhi, p);
                    if (room)
                        heights.Add(p.Z);
                }
            }
            if (NoPlayModels.TryGetValue(Mission, out string[]? noPlay) && noPlay.Contains(o.Model, StringComparer.OrdinalIgnoreCase))
            {
                // emprise des murs au pied (sommets à moins de 1 m du sol) : pas la corniche qui déborde en haut
                IEnumerable<Vector3> foot = meshes.SelectMany(m => Enumerable.Range(0, m.VertexCount).Select(i => Vector3.Transform(Vertex(m, i), w))).Where(p => p.Z <= rlo.Z + 100);
                _noPlay.Add((new Vector2(foot.Min(p => p.X), foot.Min(p => p.Y)), new Vector2(foot.Max(p => p.X), foot.Max(p => p.Y))));
            }
            if (FakeDoorModels.TryGetValue(Mission, out string[]? fake) && fake.Contains(o.Model, StringComparer.OrdinalIgnoreCase))
                AddWalls(meshes, w);
            if (!room && !roof && !mask && !NotObstacle.Contains(o.Class))
                AddObstacle(meshes, w, rlo, rhi, o.Class, UpperFloorHeights().FirstOrDefault(z => MathF.Abs(rlo.Z - z) <= 50),
                    Barriers.TryGetValue(Mission, out string[]? barriers) && barriers.Contains(o.Model, StringComparer.OrdinalIgnoreCase));
            // bâtiments (pièces, halls, façades) : leurs murs, fenêtres comprises
            bool building = room || ((rhi.X - rlo.X) * (rhi.Y - rlo.Y) / 10000f > ObstacleMaxArea && o.Class.Equals("ObjetDecor", StringComparison.OrdinalIgnoreCase));
            if (building && !roof && !mask)
            {
                AddWalls(meshes, w);
                foreach (float z in UpperFloorHeights())
                    AddWalls(meshes, w, z);
            }
            if (room)
            {
                // haut des murs de la pièce : le plus haut sommet à 4,5 m au plus du sol (certains modèles de pièce
                // contiennent aussi un bout de façade de 10 m : le couvercle n'irait pas si haut)
                float top = heights.Where(z => z <= rlo.Z + RoomMaxHeight).DefaultIfEmpty(rhi.Z).Max();
                rooms.Add((o.Name.Length > 0 ? o.Name : o.Model, rlo, new Vector3(rhi.X, rhi.Y, top)));
            }
        }
        AddRoomLids(rooms, gfx, nodes, materials, models);
        if (level.GroundTiles.Count > 0)
        {
            Fd3Mesh ground = models.Ground(level.GroundTiles, "FGD_" + Name);
            File.WriteAllBytes(Path.Combine(gfx, ground.Name + ".mesh"), Fd3Writer.MeshFile(ground));
            File.WriteAllBytes(Path.Combine(gfx, ground.Name + ".meshdata"), Fd3Writer.MeshData(ground));
            nodes.Add((ground.Name, Matrix4x4.Identity));
            foreach ((string mat, int _, int _) in ground.Groups)
                materials.Add(mat);
            Report.Add($"sol : {level.GroundTiles.Count} dalles de 10 m");
            if (GroundFills.TryGetValue(Mission, out (int x, int y, int fromX, int fromY)[]? fills))
            {
                // dalles absentes de FD1 : la texture d'une voisine, 1 cm sous le sol (les planchers des bâtiments, au niveau
                // du sol, passent devant)
                List<(int x, int y, string texture)> fillTiles = fills
                    .Select(f => (f.x, f.y, level.GroundTiles.FirstOrDefault(t => t.x == f.fromX && t.y == f.fromY).texture))
                    .Where(f => f.texture != null).ToList();
                Fd3Mesh fill = models.Ground(fillTiles, "FGD_" + Name + "_fill", -1);
                File.WriteAllBytes(Path.Combine(gfx, fill.Name + ".mesh"), Fd3Writer.MeshFile(fill));
                File.WriteAllBytes(Path.Combine(gfx, fill.Name + ".meshdata"), Fd3Writer.MeshData(fill));
                nodes.Add((fill.Name, Matrix4x4.Identity));
                foreach ((string mat, int _, int _) in fill.Groups)
                    materials.Add(mat);
                Report.Add($"sol : {fillTiles.Count} dalle(s) bouchée(s)");
            }
        }
        File.WriteAllBytes(Path.Combine(gfx, Name + ".sg"), Fd3Writer.Scene(nodes));
        Report.Add($"décor : {count} objets, {nodes.Count} maillages ({cache.Count} modèles)");
        Report.AddRange(models.Report.Distinct());

        WriteMaterials(gfx, materials);
        WriteTextures(models);
        WriteDesign(lo, hi, level);
    }

    /// <summary>
    /// Équipes de pompiers FD1 → casernes du moteur (Design\FireStation\&lt;nom&gt;_1.cfg, _2.cfg) : les véhicules et les
    /// pompiers posés dans le niveau FD1 (cachés au départ, montrés par le script) forment l'équipe de départ ; le commando
    /// (grappin) et le pompier ignifugé arrivent en premier renfort. Rend les points d'arrivée : (nom du point, position cm).
    /// </summary>
    private List<(string name, Vector2 at)> WriteStations(Fd1Level level, ref string mis)
    {
        List<(string name, Vector2 at)> points = new List<(string, Vector2)>();
        string[] laterClasses = ReinforcementVehicles.TryGetValue(Mission, out string[]? lc) ? lc : Array.Empty<string>();
        // véhicules et pompiers FD1 des équipes (sans ceux posés à part : l'hélicoptère de la Banque et son équipe)
        bool Apart(Fd1Object o) => LevelUnitOf(o) != null || (ExcludedUnits.TryGetValue(Mission, out string[]? ex) && ex.Contains(InstanceName(o) ?? "", StringComparer.OrdinalIgnoreCase));
        List<Fd1Object> allTrucks = level.Objects.Where(o => Vehicles.ContainsKey(o.Class) && !Apart(o)).ToList();
        bool Later(Fd1Object o) => laterClasses.Contains(o.Class, StringComparer.OrdinalIgnoreCase) || IsReinforcementInstance(o);
        List<Fd1Object> trucks = allTrucks.Where(o => !Later(o))
            .OrderBy(o => o.Class.Equals("MMaster", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToList();
        List<Fd1Object> laterTrucks = allTrucks.Where(Later).ToList();
        List<Fd1Object> men = level.Objects.Where(o => o.Class.Equals("ActeurUnit", StringComparison.OrdinalIgnoreCase) && Firemen.ContainsKey(o.Name) && !Apart(o)).ToList();
        List<string> first = trucks.Select(t => Vehicles[t.Class]).ToList();
        // pompiers d'abord, spécialistes ensuite (comme la barre des unités de FD1)
        bool LaterMan(Fd1Object m) => Firemen[m.Name].reinforcement || IsReinforcementInstance(m);
        first.AddRange(men.Where(m => !LaterMan(m)).Select(m => Firemen[m.Name].unit).OrderBy(u => u.EndsWith("_sp_us", StringComparison.OrdinalIgnoreCase) ? 0 : 1));
        List<Fd1Object> later = men.Where(LaterMan).ToList();
        string dir = Path.Combine(Data, "Design", "FireStation");
        Directory.CreateDirectory(dir);
        Vector2 entry = Entries.TryGetValue(Mission, out Vector2 e) ? e : Vector2.Zero;
        if (first.Count > 0)
        {
            File.WriteAllBytes(Path.Combine(dir, Name + "_1.cfg"), Encoding.Latin1.GetBytes(Station("&E9", first)));
            mis = IniText.Set(mis, "FIRESTATION", "INITIALDISPATCH", Name + "_1");
            for (int k = 0; k < trucks.Count; k++)
            {
                Vector2 end = new Vector2(trucks[k].World.M41, trucks[k].World.M42) * WorldToCm;
                Vector2 start = entry != Vector2.Zero ? entry + new Vector2(0, 600 * k) : end - new Vector2(3000, 0);
                points.Add(($"locstart1_{k + 1}", start));
                points.Add(($"locend1_{k + 1}", end));
            }
            Report.Add($"équipe de départ : {first.Count} unités ({trucks.Count} véhicules)");
        }
        if (later.Count > 0 || laterTrucks.Count > 0)
        {
            List<string> units = laterTrucks.Select(t => Vehicles[t.Class]).Concat(later.Select(m => Firemen[m.Name].unit)).ToList();
            File.WriteAllBytes(Path.Combine(dir, Name + "_2.cfg"), Encoding.Latin1.GetBytes(Station("&E10", units)));
            mis = IniText.Set(mis, "FIRESTATION", "REINFORCEMENT1", Name + "_2");
            mis = IniText.Set(mis, "FIRESTATION", "REINFORCEMENT1SPAWNPOINT", "2");
            // [REP_TIME] du .MIS FD1 : délai avant le renfort (s)
            string misFile = Directory.EnumerateFiles(Path.Combine(Fd1.Root, "Logic", "Missions", Mission), "*.MIS").FirstOrDefault() ?? "";
            string rep = misFile.Length > 0 ? File.ReadLines(misFile, Encoding.Latin1).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("[REP_TIME]", StringComparison.OrdinalIgnoreCase)) ?? "" : "";
            string eta = rep.Length > 0 ? rep["[REP_TIME]".Length..].Trim().Split(' ', '\t', '/')[0] : "60";
            // délai imposé par la mission (Banque : FD1 fait entrer le renfort aussitôt, pendant sa cinématique)
            if (ReinforcementDelays.TryGetValue(Mission, out int delay))
                eta = delay.ToString(System.Globalization.CultureInfo.InvariantCulture);
            mis = IniText.Set(mis, "FIRESTATION", "REINFORCEMENT1ETA", eta);
            _reinforcementEta = float.TryParse(eta, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r) ? r : 60;
            if (laterTrucks.Count > 0)
            {
                // véhicules du renfort : ils entrent comme ceux du départ (ou par leur propre entrée) et vont à leur place FD1
                // (l'équipage descend)
                Vector2 entry2 = ReinforcementEntries.TryGetValue(Mission, out Vector2 e2) ? e2 : entry;
                for (int k = 0; k < laterTrucks.Count; k++)
                {
                    Vector2 end = new Vector2(laterTrucks[k].World.M41, laterTrucks[k].World.M42) * WorldToCm;
                    // (entrée propre au renfort : les camions à la file le long de la rue, en x)
                    Vector2 step = ReinforcementEntries.ContainsKey(Mission) ? new Vector2(600 * k, 0) : new Vector2(0, 600 * k);
                    Vector2 start = entry2 != Vector2.Zero ? entry2 + step : end - new Vector2(3000, 0);
                    points.Add(($"locstart2_{k + 1}", start));
                    points.Add(($"locend2_{k + 1}", end));
                }
            }
            else
            {
                // (entrée propre au renfort à pied, sinon la place FD1 du premier)
                Vector2 at = ReinforcementEntries.TryGetValue(Mission, out Vector2 e3) ? e3 : new Vector2(later[0].World.M41, later[0].World.M42) * WorldToCm;
                points.Add(("locstart2_1", at));
                points.Add(("locend2_1", at));
            }
            Report.Add($"renfort : {units.Count} unité(s) après {eta} s");
        }
        return points;
    }

    /// <summary>Fichier de caserne : nom de code de l'équipe et ses unités (véhicules d'abord).</summary>
    private static string Station(string code, List<string> units)
    {
        StringBuilder sb = new StringBuilder();
        // base_classic.cfg (et non classic_pumper.cfg, dont les UNIT03 à UNIT05 s'ajouteraient à une petite équipe)
        sb.Append("[#INCLUDE]\r\nFILE0 = \"base_classic.cfg\"\r\n\r\n[FIRESTATION]\r\nNAME = &Station_Pumper\r\nICON = ICON_FIRESTATION_URBAN_PUMPER\r\n");
        sb.Append($"CODENAME = {code}\r\n");
        for (int i = 0; i < units.Count; i++)
            sb.Append($"UNIT{i + 1:00} = {units[i]}\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Couvercles noirs des pièces, comme dans FD1 : une pièce qu'aucun pompier n'a encore visitée est fermée par un dessus
    /// noir, à la hauteur de ses murs (Entrepot : le bureau du contremaître, « ent_entrepot_part04 »). Couvercle = plaque
    /// noire sur l'emprise de la pièce, nœud « …_ROOF » : le moteur l'enlève quand un pompier passe dessous.
    /// Pièces retenues : hautes d'au moins 2,5 m, de 4 à 400 m² (pas les halls, ni une caisse nommée dans une zone).
    /// </summary>
    private void AddRoomLids(List<(string name, Vector3 min, Vector3 max)> rooms, string gfx, List<(string node, Matrix4x4 world)> nodes, HashSet<string> materials, ModelConverter models)
    {
        const string black = "fd1c_000000";
        int n = 0;
        foreach ((string name, Vector3 mn, Vector3 mx) in rooms)
        {
            float area = (mx.X - mn.X) * (mx.Y - mn.Y) / 10000f;
            if (mx.Z - mn.Z < 250 || area < 4 || area > 400)
                continue;
            Fd3Mesh lid = new Fd3Mesh { Name = $"fd1_lid_{ModelConverter.Safe(name).ToLowerInvariant()}_{n}_ROOF" };
            float z = mx.Z + 2;
            foreach ((float x, float y) in new[] { (mn.X, mn.Y), (mx.X, mn.Y), (mx.X, mx.Y), (mn.X, mx.Y) })
            {
                lid.Positions.AddRange(new[] { x, y, z });
                lid.Normals.AddRange(new[] { 0f, 0f, 1f });
                lid.Uv.AddRange(new[] { 0f, 0f });
            }
            foreach (int i in new[] { 0, 1, 2, 2, 3, 0 })
                lid.Indices.Add((ushort)i);
            lid.Groups.Add((black, 0, 6));
            File.WriteAllBytes(Path.Combine(gfx, lid.Name + ".mesh"), Fd3Writer.MeshFile(lid));
            File.WriteAllBytes(Path.Combine(gfx, lid.Name + ".meshdata"), Fd3Writer.MeshData(lid));
            nodes.Add((lid.Name, Matrix4x4.Identity));
            _covers.Add((lid.Name, MeshBox(lid, Matrix4x4.Identity)));
            n++;
        }
        if (n == 0)
            return;
        models.Colors[black] = 0xFF000000;
        materials.Add(black);
        Report.Add($"pièces : {n} couvercle(s) noir(s)");
    }

    // ---- matériaux : copie d'un matériau du jeu à une seule texture ----
    private void WriteMaterials(string gfx, HashSet<string> materials)
    {
        byte[] template = Fd3.TryRead("Gfx\\Placeable\\Shaders\\lv_Autom_022.stdmat")
            ?? throw new FileNotFoundException("matériau modèle introuvable dans les données du jeu : Gfx\\Placeable\\Shaders\\lv_Autom_022.stdmat");
        foreach (string mat in materials)
            File.WriteAllBytes(Path.Combine(gfx, "Shaders", mat + ".stdmat"), Fd3Writer.Stdmat(template, mat, mat + ".tga"));
        Report.Add($"{materials.Count} matériaux");
    }

    // ---- textures : images FD1 → DDS ; couleurs unies → petites images ----
    private void WriteTextures(ModelConverter models)
    {
        string dir = Path.Combine(Data, "Gfx", "Textures", Name);
        Directory.CreateDirectory(dir);
        int ok = 0;
        foreach ((string tex, string img) in models.Textures)
        {
            if (img.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(img, Path.Combine(dir, tex + ".dds"), true);
                ok++;
                continue;
            }
            (int w, int h, byte[] rgba)? im = ImageCodec.Decode(File.ReadAllBytes(img));
            if (im is not { } v)
            {
                Report.Add($"image non lue : {img}");
                continue;
            }
            if (models.Opaque.Contains(tex))
                for (int i = 3; i < v.rgba.Length; i += 4)
                    v.rgba[i] = 255;
            File.WriteAllBytes(Path.Combine(dir, tex + ".dds"), ImageCodec.EncodeDds(v.w, v.h, v.rgba));
            ok++;
        }
        foreach ((string name, uint argb) in models.Colors)
        {
            byte[] rgba = new byte[4 * 4 * 4];
            for (int i = 0; i < 16; i++)
            {
                rgba[4 * i] = (byte)(argb >> 16);
                rgba[4 * i + 1] = (byte)(argb >> 8);
                rgba[4 * i + 2] = (byte)argb;
                rgba[4 * i + 3] = 255;
            }
            File.WriteAllBytes(Path.Combine(dir, name + ".dds"), ImageCodec.EncodeDds(4, 4, rgba));
        }
        Report.Add($"textures : {ok} images, {models.Colors.Count} couleurs unies");
    }

    // ---- niveau, grille, .mis, script (comme une mission neuve de l'éditeur) ----
    /// <summary>
    /// Foyers allumés au départ, par mission. Le niveau FD1 ne dit pas (de façon lisible) quels feux brûlent au début : on
    /// l'a relevé sur une capture du jeu, sous forme de zones (cm : x min, x max, y min, y max). Entrepot (premier hall) :
    /// le mur de gauche, le bas de la deuxième rangée d'étagères, le côté droit contre le mur entre les deux halls.
    /// Mission sans zones : les torches (grands feux à colonne de flammes).
    /// </summary>
    private static readonly Dictionary<string, (float x0, float x1, float y0, float y1)[]> StartFires = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Entrepot", new[] { (7800f, 13000f, 0f, 6400f), (11000f, 13000f, 6600f, 7400f), (9000f, 12500f, 12700f, 13700f) } },
    };

    private bool LitAtStart(string mission, Vector3 at, string type)
    {
        // Banque : les torches, sauf dans les pièces dont le feu attend le script (groupes de foyers FD1, éteints au départ)
        if (mission.Equals("banque", StringComparison.OrdinalIgnoreCase))
            return type.StartsWith("torche", StringComparison.OrdinalIgnoreCase) && BanqueGroupOf(at, type) == 0;
        // Villa : la remise, les bidons, les haies du tennis (MissionConverter.Villa.cs)
        if (mission.Equals("Villa", StringComparison.OrdinalIgnoreCase))
            return VillaLitAtStart(at, type);
        // Quartier : les voitures accidentées de la rue, l'aile ouest de l'étage (MissionConverter.Quartier.cs)
        if (mission.Equals("quartier", StringComparison.OrdinalIgnoreCase))
            return QuartierLitAtStart(at, type);
        // Station-service : le camion-citerne, les pompes, les deux voitures accidentées (MissionConverter.StationService.cs)
        if (mission.Equals("station_service", StringComparison.OrdinalIgnoreCase))
            return StationLitAtStart(at, type);
        // Train : les wagons chimiques du passage à niveau, la locomotive de tête (MissionConverter.Train.cs)
        if (mission.Equals("train", StringComparison.OrdinalIgnoreCase))
            return TrainLitAtStart(at, type);
        if (StartFires.TryGetValue(mission, out (float x0, float x1, float y0, float y1)[]? zones))
            return zones.Any(z => at.X >= z.x0 && at.X <= z.x1 && at.Y >= z.y0 && at.Y <= z.y1);
        return type.StartsWith("torche", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Points de feu FD1 → foyers du moteur (copies d'un foyer d'une mission du jeu) : tous éteints, sauf ceux du départ
    /// (LitAtStart). Les autres sont le combustible où le feu se propage.
    /// </summary>
    private int AddFires(Level level, Fd1Level fd1)
    {
        Dictionary<string, LevelObject> templates = FireTemplates();
        if (!templates.ContainsKey(FireClass("")))
        {
            Report.Add("feux : aucun foyer modèle dans les missions du jeu");
            return 0;
        }
        int n = 0;
        int lit = 0;
        foreach ((Vector3 at, string type) in fd1.Fires)
        {
            Vector3 cm = at * WorldToCm;
            bool on = LitAtStart(Mission, cm, type);
            LevelObject template = templates.TryGetValue(FireClass(type), out LevelObject? t) ? t : templates[FireClass("")];
            float intensity = LevelEdit.FireState(template) is { intensity: > 0 } st ? st.intensity : 50;
            n++;
            LevelObject o = LevelEdit.Clone(template, LevelEdit.NewId(level), $"foyer_{n}", template.ClassPath);
            o = LevelEdit.WithTransform(o, cm, Vector3.Zero);
            o = LevelEdit.WithStartVisible(o, true);
            o = LevelEdit.WithFire(o, on, intensity);
            level.Objects.Add(o);
            if (on)
                lit++;
        }
        Report.Add($"feux : {n} foyers, dont {lit} allumés au départ");
        return n;
    }

    /// <summary>Type de feu FD1 → classe de foyer du moteur : torche → grand foyer, « light » → petit, autres → moyen.</summary>
    private static string FireClass(string type)
    {
        if (type.StartsWith("torche", StringComparison.OrdinalIgnoreCase))
            return "design\\hotpoint\\ground_l";
        if (type.EndsWith("_light", StringComparison.OrdinalIgnoreCase))
            return "design\\hotpoint\\ground_s";
        return "design\\hotpoint\\ground_m";
    }

    /// <summary>Un foyer de chaque classe utile (FireClass), pris dans les missions du jeu (bloc d'état complet).</summary>
    private Dictionary<string, LevelObject> FireTemplates()
    {
        Dictionary<string, LevelObject> res = new Dictionary<string, LevelObject>(StringComparer.OrdinalIgnoreCase);
        string[] wanted = { FireClass("torche"), FireClass("x_light"), FireClass("") };
        foreach (string lv in Fd3.List("Design\\Mission\\").Where(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase)))
        {
            if (wanted.All(res.ContainsKey))
                break;
            try
            {
                Level l = Level.Parse(Fd3.Read(lv));
                foreach (LevelObject o in l.Objects.Where(o => o.Kind == EntityKind.HotPoint && o.HasTransform && LevelEdit.FireState(o) is { full: true }))
                    if (wanted.Contains(o.ClassPath, StringComparer.OrdinalIgnoreCase) && !res.ContainsKey(o.ClassPath))
                        res[o.ClassPath] = o;
            }
            catch (Exception)
            {
            }
        }
        return res;
    }

    private void WriteDesign(Vector3 lo, Vector3 hi, Fd1Level fd1)
    {
        float width = MathF.Max(hi.X, 2000) + 1000;
        float height = MathF.Max(hi.Y, 2000) + 1000;
        int w = (int)MathF.Ceiling(width / Grid.CellSize);
        int h = (int)MathF.Ceiling(height / Grid.CellSize);
        (Level level, string mis) = ModelMission();
        List<LevelObject> all = level.Objects.ToList();
        LevelObject sun = all.First(IsSun);
        LevelObject? waypoint = all.FirstOrDefault(o => o.Kind == EntityKind.Waypoint && o.HasTransform && o.ClassName.Equals("cam_startpos", StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(o => o.Kind == EntityKind.Waypoint && o.HasTransform);
        level.Objects.Clear();
        level.ClearHotPointLinks();
        float cx = (lo.X + hi.X) / 2;
        float cy = (lo.Y + hi.Y) / 2;
        if (!float.IsFinite(cx) || !float.IsFinite(cy))
        {
            cx = w * Grid.CellSize / 2;
            cy = h * Grid.CellSize / 2;
        }
        level.Objects.Add(LevelEdit.WithTransform(sun, new Vector3(cx, cy, 5000), sun.Rotation));
        for (int i = 1; i <= 9; i++)
            if (IniText.Get(mis, "FIRESTATION", $"REINFORCEMENT{i}") != null)
                mis = IniText.Set(mis, "FIRESTATION", $"REINFORCEMENT{i}", "");
        List<(string name, Vector2 at)> points = WriteStations(fd1, ref mis);
        AddFires(level, fd1);
        AddBigDoors(level, fd1);
        AddBreakableDoors(level);
        AddBombs(level);
        AddScriptDoors(level);
        AddScriptButtons(level);
        AddGrapples(level);
        AddScriptStairs(level);
        AddStory(level, fd1);
        AddLevelUnits(level, fd1);
        AddScriptObjects(level);
        if (waypoint != null)
            AddCinematics(level, waypoint);
        if (waypoint != null)
        {
            // caméra de départ : sur le point d'arrivée de l'équipe, sinon au centre du niveau
            Vector2 look = points.Where(p => p.name == "locend1_1").Select(p => p.at).DefaultIfEmpty(new Vector2(cx, cy)).First();
            Vector3 eye = new Vector3(look.X, look.Y - 2500, 2500);
            Vector3 f = Vector3.Normalize(new Vector3(look.X, look.Y, 0) - eye);
            float rz = MathF.Asin(Math.Clamp(f.X, -1, 1));
            float rx = MathF.Atan2(-f.Z, -f.Y);
            LevelObject cam = LevelEdit.Clone(waypoint, LevelEdit.NewId(level), "cam_startpos", "design\\waypoint\\cam_startpos");
            level.Objects.Add(LevelEdit.WithTransform(cam, eye, new Vector3(rx, 0, rz)));
            if (points.Count == 0)
            {
                points.Add(("locstart1_1", new Vector2(cx, 300)));
                points.Add(("locend1_1", new Vector2(cx, cy)));
            }
            foreach ((string name, Vector2 at) in points)
            {
                LevelObject p = LevelEdit.Clone(waypoint, LevelEdit.NewId(level), name, waypoint.ClassPath);
                level.Objects.Add(LevelEdit.WithTransform(p, new Vector3(at, 0), Vector3.Zero));
            }
        }
        foreach ((string sec, string key, string value) in new[]
        {
            ("MISSION", "Name", Name), ("MISSION", "Minimap", ""), ("DISPLAY", "Background", ""),
            ("BRIEFING", "VIDEOFEEDFILE", ""), ("BRIEFING", "VideoSubtitleFile", ""), ("BRIEFING", "StoryTitleKey", Name),
            ("SIMU", "GridSizeX", w.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("SIMU", "GridSizeY", h.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("SCRIPT", "AUTOEXECSCRIPT", Name + "_Start"), ("SCRIPT", "MISSIONSCRIPT1", Name + "_Main"), ("SCRIPT", "MISSIONSCRIPT2", ""),
            ("FIRESTATION", "InitialDispatchSpawnPoint", "1"),
        })
            mis = IniText.Set(mis, sec, key, value);
        string design = Path.Combine(Data, "Design", "Mission", Name);
        Directory.CreateDirectory(design);
        LevelEdit.SetMissionPath(level, $".\\Data\\Design\\Mission\\{Name}\\mission_{Name}.mis");
        File.WriteAllBytes(Path.Combine(design, Name + ".level"), level.Serialize());
        Grid grid = Grid.Flat(w, h);
        // une seule zone B pour tout le niveau : en zone B 0, le moteur ferme toute bande libre de 3 cases au plus comme un
        // « mur de pièce » (grilles du jeu) ; ici, les allées étroites entre deux étagères ou un bureau et un mur resteraient
        // fermées. Les murs viennent des maillages (le moteur), les obstacles des objets (BlockObstacles).
        Array.Fill(grid.ZoneB, (ushort)1);
        BarrierWalls();
        int blocked = BlockObstacles(grid);
        int upperCells = ApplyUpperFloors(grid);
        if (upperCells > 0)
            Report.Add($"étage : {upperCells} cases en hauteur (planchers, murs des passages au grappin)");
        File.WriteAllBytes(Path.Combine(design, Name + ".grid"), grid.Serialize());
        Report.Add($"grille : {blocked} cases fermées par les objets du décor (étagères, caisses, véhicules)");
        File.WriteAllBytes(Path.Combine(design, "mission_" + Name + ".mis"), Encoding.Latin1.GetBytes(mis));
        string scripts = Path.Combine(Data, "Script", "Mission", Name);
        Directory.CreateDirectory(scripts);
        WriteTexts();
        (string bombStart, string bombMain, string bombFunctions) = BombScript();
        (string storyFunctions, string storyStart, string storyMain) = StoryScript(fd1);
        string script = $"// {Name} : mission convertie de Fire Department 1 ({Mission})\r\n\r\n{bombFunctions}{CinematicScript()}{ButtonScript()}{storyFunctions}void {Name}_Start()\r\n{{\r\n{bombStart}{storyStart}}}\r\n\r\nvoid {Name}_Main()\r\n{{\r\n{bombMain}{storyMain}}}\r\n";
        File.WriteAllBytes(Path.Combine(scripts, Name + ".scp"), Encoding.Latin1.GetBytes(script));
        Report.Add($"niveau : grille {w} x {h} cases, .mis et script dans Design\\Mission\\{Name} et Script\\Mission\\{Name}");
    }

    private bool IsSun(LevelObject o)
    {
        if (o.Kind != EntityKind.Light)
            return false;
        try
        {
            ClassFile c = ClassFile.Load(Fd3, "Design\\" + o.ClassPath["design\\".Length..] + ".class");
            return c.Get("LIGHT", "LightType")?.Trim('"', ' ').Equals("SUN", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Mission du jeu qui sert de modèle : la première dont le niveau a un soleil.</summary>
    private (Level level, string mis) ModelMission()
    {
        foreach (string lv in Fd3.List("Design\\Mission\\").Where(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Level l = Level.Parse(Fd3.Read(lv));
                if (!l.Objects.Any(IsSun))
                    continue;
                int at = l.MissionPath.IndexOf("Design\\", StringComparison.OrdinalIgnoreCase);
                if (at < 0 || Fd3.TryRead(l.MissionPath[at..]) is not { } misBytes)
                    continue;
                return (l, Encoding.Latin1.GetString(misBytes));
            }
            catch (Exception)
            {
            }
        }
        throw new InvalidDataException("aucune mission du jeu ne peut servir de modèle (niveau avec un soleil)");
    }
}
