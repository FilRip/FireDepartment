using System.Numerics;
using System.Text;
using OpenFD.Formats;
using OpenFD.MG;

namespace OpenFD.ConvFD1;

/// <summary>
/// Étages FD1 et passages vers eux. La grille du moteur n'a qu'un sol par case : un plancher en hauteur ne remplace que des
/// cases fermées au rez-de-chaussée (Entrepot : le dessus du bloc noir entre les deux halls, à 15 m). On y monte au
/// grappin (repère « koshka_symbol » en haut du mur : seul le scout passe). Un bouton (objet utilisable) y appelle une
/// fonction du script qui ouvre une porte fermée (porte qui monte, mais que personne ne peut ouvrir à la main).
/// Entrepot : dans FD1, le technicien ouvre la porte blindée entre les deux halls (« PorteFen ») avec le Fenwick ; ici, le
/// scout monte sur le bloc noir et appuie sur le bouton de l'échelle 1 (« Ent_Bouton_Echelle1 »), qui ouvre cette porte.
/// </summary>
public sealed partial class MissionConverter
{
    /// <summary>Planchers en hauteur, par mission : modèle FD1 et hauteur du plancher (cm).</summary>
    private static readonly Dictionary<string, (string model, float z)[]> UpperFloors = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Entrepot", new[] { ("CORNICHE", 1500f), ("ENT_PASSERELLE", 1500f), ("PIECE02_01", 1500f), ("PIECE02_02", 1500f) } },
        // Banque : l'étage (comptabilité, bureau du directeur, salle informatique, salle VIP, couloirs, toilettes) et le toit
        // de l'immeuble voisin, où se pose l'hélicoptère (« BAN_BAT_EVAC », 14,8 m : même niveau, par la sortie de secours)
        { "banque", new[] { ("BAN_BAT_EVAC", 1500f), ("BAN_BUR_DIR", 1500f), ("BAN_COMPTA_03", 1500f), ("BAN_COULOIR_ANGL_VIP", 1500f),
            ("BAN_COULOIR_DIR", 1500f), ("BAN_COULOIR_VIP", 1500f), ("BAN_INFOR", 1500f), ("BAN_VIP", 1500f), ("BAN_WC", 1500f),
            ("BAN_G_HALL", 1500f), ("BAN_EFFONDRE", 1500f), ("BAN_EBOUL_01", 1500f), ("BAN_EBOUL_02", 1500f), ("BAN_EBOUL_03", 1500f),
            ("BAN_EBOUL_04", 1500f) } },
        // Quartier : le troisième étage de l'ambassade (appartements, salons, salle de réception) ; on y monte à l'échelle
        { "quartier", new[] { ("QUA_APPART01", 1500f), ("QUA_APPART02", 1500f), ("QUA_APPART03", 1500f), ("QUA_APPART04", 1500f), ("QUA_APPART05", 1500f), ("QUA_APPART06", 1500f), ("QUA_APPART07", 1500f), ("QUA_APPART08", 1500f), ("QUA_APPART09", 1500f), ("QUA_APPART10", 1500f), ("QUA_APPART11", 1500f), ("QUA_APPART12", 1500f), ("QUA_APPART13", 1500f), ("QUA_APPART14", 1500f), ("QUA_APPART15", 1500f), ("QUA_APPART16", 1500f), ("QUA_APPART17", 1500f), ("QUA_APPART18", 1500f), ("QUA_APPART19", 1500f), ("QUA_APPART20", 1500f), ("QUA_APPART21", 1500f), ("QUA_APPART22", 1500f), ("QUA_APPART23", 1500f), ("QUA_EBOULEMENT_RD", 1500f), ("QUA_NO_PICKING", 1500f) } },
        // Ecole : l'étage de l'école (salles de classe, couloir, la classe au plancher qui s'effondre) et celui du bâtiment
        // administratif ; on y monte à l'échelle (acte 2)
        {
            "ecole", new[]
            {
                ("ECO_CLASSE01", 1500f), ("ECO_CLASSE02", 1500f), ("ECO_CLASSE03", 1500f), ("ECO_CLASSE04", 1500f),
                ("ECO_CLASSE05", 1500f), ("ECO_COULOIR_CLASSE01", 1500f), ("ECO_PLANCHER_QUI_SEFFONDRE", 1500f), ("ECO_SALLE_PROF01", 1500f),
                ("ECO_SALLE_PROF02", 1500f), ("ECO_SALLE_PROF04", 1500f), ("ECO_SALLE_PROF05", 1500f), ("ECO_SALLE_PROF06", 1500f),
            }
        },
        // Station-service : l'étage de l'immeuble ouest (appartements) ; on y monte à l'échelle (acte 4)
        { "station_service", new[] { ("STA_HABITAT_PIECE01", 1500f), ("STA_HABITAT_PIECE02", 1500f), ("STA_HABITAT_PIECE03", 1500f), ("STA_HABITAT_PIECE04", 1500f), ("STA_HABITAT_PIECE05", 1500f), ("STA_HABITAT_PIECE06", 1500f), ("STA_HABITAT_PIECE07", 1500f), ("STA_HABITAT_PIECE08", 1500f), ("STA_HABITAT_PIECE09", 1500f), ("STA_HABITAT_PIECE10", 1500f), ("STA_HABITAT_PIECE11", 1500f), ("STA_HABITAT_PIECE12", 1500f), ("STA_HABITAT_PIECE13", 1500f), ("STA_HABITAT_PIECE14", 1500f), ("STA_HABITAT_PIECE15", 1500f), ("STA_HABITAT_PIECE16", 1500f) } },
    };

    /// <summary>
    /// Passages au grappin, par mission : haut du mur (repère) et direction du mur (radians, 0 : le long de x). Entrepot : le
    /// bord de la passerelle (15 m, devant le bloc noir côté premier hall, où est le bouton de l'échelle 1) : le scout monte
    /// et redescend au bord, dans l'allée du hall, pas contre le mur sous la passerelle ; et le mur du bureau de la
    /// secrétaire côté deuxième hall, à côté de l'échelle 2.
    /// </summary>
    private static readonly Dictionary<string, (Vector3 top, float yaw)[]> GrappleWalls = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Entrepot", new[] { (new Vector3(9400, 13240, 1430), 0f), (new Vector3(8750, 19200, 1430), MathF.PI / 2) } },
        // Banque : le spécialiste au grappin monte du hall au couloir est de l'étage, à côté de l'escalier effondré
        { "banque", new[] { (new Vector3(13600, 9550, 1430), 0f) } },
    };

    /// <summary>
    /// Échelles FD1 rendues utilisables par un bouton, par mission : nom, bout en haut, bout en bas. Pour le moteur : un
    /// escalier (« teleport_stair » et ses deux icônes), caché et inactif au départ. Entrepot : l'échelle 2, du bureau de la
    /// secrétaire au deuxième hall.
    /// </summary>
    private static readonly Dictionary<string, (string name, Vector3 top, Vector3 bottom)[]> ScriptStairs = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Entrepot", new[] { ("Echelle2", new Vector3(8600, 19400, 1500), new Vector3(8950, 19420, 0)) } },
    };

    /// <summary>
    /// Portes FD1 que seul le script ouvre, par mission : modèle (ou nom de l'instance FD1) et nom de la porte (elle monte,
    /// fermée au départ).
    /// </summary>
    private static readonly Dictionary<string, (string model, string name)[]> ScriptDoors = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Entrepot", new[] { ("ENT_PORTEBLINDEE01", "PorteFen") } },
        // Banque : la sortie de secours (toit voisin ↔ étage, ouverte à l'arrivée de l'hélicoptère), la porte de la salle de
        // surveillance (acte 4) et la porte blindée de la salle des coffres
        { "banque", new[] { ("ECO_PORTECOUPFEU01", "SortieSecours"), ("ECO_PORTECOUPEFEU_01", "PorteSurveillance"), ("BAN_PORTE_BLINDEE_SALLE", "PorteBlindeeCoffre") } },
        // Ecole : les portes coupe-feu entre les pièces de l'étage administratif (FD1 : mécanismes « openDoor » ; le joueur
        // les ouvre, UsableDoors)
        { "ecole", new[] { ("pcf0", "pcf0"), ("pcf1", "pcf1"), ("pcf2", "pcf2"), ("pcf3", "pcf3") } },
        // Villa : les cinq portes du garage (boutons rouges) et la sortie de secours de la serre tropicale
        {
            "Villa", new[]
            {
                ("PorteGarage1", "PorteGarage1"), ("PorteGarage2", "PorteGarage2"), ("PorteGarage3", "PorteGarage3"),
                ("PorteGarage4", "PorteGarage4"), ("PorteGarage5", "PorteGarage5"), ("SortieSerreTropicale", "SortieSerreTropicale"),
            }
        },
    };

    /// <summary>
    /// Objets FD1 utilisables du script, par mission : nom de l'objet, porte qu'il ouvre, échelle qu'il rend utilisable
    /// (ScriptStairs) ou « @fonction » du script de la mission à appeler (MissionConverter.Story.cs), message.
    /// </summary>
    private static readonly Dictionary<string, (string button, string door, string message)[]> ScriptButtons = new(StringComparer.OrdinalIgnoreCase)
    {
        {
            "Entrepot", new[]
            {
                ("Ent_Bouton_Echelle1", "PorteFen", "La porte blindée entre les deux halls s'ouvre"),
                ("Ent_Bouton_Echelle2", "Echelle2", "L'échelle du bureau de la secrétaire est descendue"),
                // indices à ramasser (MissionConverter.Story.cs) : la vidéo du local de sécurité, les documents du bureau
                ("ent_localsecurite01_etat01_meca", "@VideoPick", ""),
                ("ent_documents", "@DocPick", ""),
                // l'armoire qui coupe le courant de la flaque électrique
                ("eco_arm_cont_coupfeu01", "@BoutFlaque", ""),
            }
        },
        {
            // Banque (MissionConverter.Banque.cs) : les bandes et les petits coffres à ramasser, les grands coffres à ouvrir,
            // l'armoire des sprinklers
            "banque", new[]
            {
                ("Bobine1", "@Pick_Bobine1", ""), ("Bobine2", "@Pick_Bobine2", ""), ("Bobine3", "@Pick_Bobine3", ""),
                ("Bobine4", "@Pick_Bobine4", ""), ("Bobine5", "@Pick_Bobine5", ""), ("Bobine6", "@Pick_Bobine6", ""),
                ("Coffre1", "@Pick_Coffre1", ""), ("Coffre2", "@Pick_Coffre2", ""), ("Coffre3", "@Pick_Coffre3", ""),
                ("CoffreMec1", "@Ouvre_CoffreMec1", ""), ("CoffreMec2", "@Ouvre_CoffreMec2", ""), ("CoffreMec3", "@Ouvre_CoffreMec3", ""),
                ("DeclencheSprinkler", "@Sprinklers", ""),
            }
        },
        {
            // Villa (MissionConverter.Villa.cs) : les boutons rouges du garage et de la serre, la pompe de la piscine (un
            // ingénieur la met en marche), la bande master
            "Villa", new[]
            {
                ("BoutonPG1", "@PortesGarage123", ""), ("BoutonPG3", "@PortesGarage123", ""), ("BoutonPG4", "@PortesGarage45", ""),
                ("BoutonSortieSerreTropicale", "SortieSerreTropicale", ""),
                ("Pompe_Arrosage_Mecanisme", "@Pompe", ""),
                ("BandeSon1", "@Pick_BandeSon", ""),
            }
        },
        {
            // Quartier (MissionConverter.Quartier.cs) : les six oeuvres d'art à ramasser, le robinet de gaz du local de la cour
            "quartier", new[]
            {
                ("Oeuvre1", "@Pick_Oeuvre1", ""), ("Oeuvre2", "@Pick_Oeuvre2", ""), ("Oeuvre3", "@Pick_Oeuvre3", ""),
                ("Oeuvre4", "@Pick_Oeuvre4", ""), ("Oeuvre5", "@Pick_Oeuvre5", ""), ("Oeuvre6", "@Pick_Oeuvre6", ""),
                ("qua_arret_gaz", "@CoupeGaz", ""),
            }
        },
        {
            // Station-service (MissionConverter.StationService.cs) : la vanne de la fuite de propane, le sac des 3 millions
            "station_service", new[]
            {
                ("valveessence", "@ValveEssence", ""), ("Sac", "@Pick_Sac", ""),
            }
        },
        // Train (MissionConverter.Train.cs) : l'armoire de l'aération de secours du tunnel
        { "train", new[] { ("eco_arm_cont_coupfeu01", "@Ventilation", "") } },
        // Ecole (MissionConverter.Ecole.cs) : le groupe électrogène du chantier (on coupe le courant)
        { "ecole", new[] { ("grpeElec", "@GEOff", "") } },
    };

    /// <summary>Boutons du script dont seul un ingénieur se sert (il « répare »), par mission (nom FD1).</summary>
    private static readonly Dictionary<string, string[]> EngineerButtons = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Villa", new[] { "Pompe_Arrosage_Mecanisme" } },
    };

    /// <summary>Maillages des planchers en hauteur (dans le repère du monde) et hauteur de chacun.</summary>
    private readonly List<(List<Fd3Mesh> meshes, Matrix4x4 w, float z, bool sagging)> _upperFloors = new List<(List<Fd3Mesh>, Matrix4x4, float, bool)>();

    /// <summary>
    /// Planchers affaissés, par mission (modèle FD1) : leurs bords penchent vers un trou ; on y marche quand même (pente
    /// jusqu'à 35°, jusqu'à 2 m sous le plancher). Banque : la pièce effondrée de l'étage, entre les deux ailes, et les
    /// dalles écroulées qui en couvrent le milieu : on la traverse.
    /// </summary>
    private static readonly Dictionary<string, string[]> SaggingFloors = new(StringComparer.OrdinalIgnoreCase)
    {
        { "banque", new[] { "BAN_EFFONDRE", "BAN_EBOUL_01", "BAN_EBOUL_02", "BAN_EBOUL_03", "BAN_EBOUL_04" } },
    };

    /// <summary>Portes du script trouvées dans le niveau FD1 (objet, maillages, nom).</summary>
    private readonly List<(Fd1Object o, List<Fd3Mesh> meshes, string name)> _scriptDoors = new List<(Fd1Object, List<Fd3Mesh>, string)>();

    /// <summary>Boutons du script trouvés dans le niveau FD1 (objet, maillages, porte, message).</summary>
    private readonly List<(Fd1Object o, List<Fd3Mesh> meshes, string door, string message)> _scriptButtons = new List<(Fd1Object, List<Fd3Mesh>, string, string)>();

    /// <summary>Murs (AddWalls) et meubles (AddObstacle) des étages, par hauteur de plancher : carrés de 25 cm.</summary>
    private readonly Dictionary<float, HashSet<(int x, int y)>> _upperWallQuarters = new Dictionary<float, HashSet<(int, int)>>();
    private readonly Dictionary<float, HashSet<(int x, int y)>> _upperBlockedQuarters = new Dictionary<float, HashSet<(int, int)>>();

    private static HashSet<(int x, int y)> UpperQuarters(Dictionary<float, HashSet<(int x, int y)>> sets, float floor)
    {
        if (!sets.TryGetValue(floor, out HashSet<(int x, int y)>? set))
            sets[floor] = set = new HashSet<(int, int)>();
        return set;
    }

    /// <summary>Hauteurs des planchers d'étage de cette mission.</summary>
    private IEnumerable<float> UpperFloorHeights() =>
        UpperFloors.TryGetValue(Mission, out (string model, float z)[]? floors) ? floors.Select(f => f.z).Distinct() : Enumerable.Empty<float>();

    /// <summary>Plancher en hauteur de ce modèle FD1 dans cette mission (null : aucun).</summary>
    private float? UpperFloorOf(string model)
    {
        if (!UpperFloors.TryGetValue(Mission, out (string model, float z)[]? floors))
            return null;
        foreach ((string m, float z) in floors)
            if (m.Equals(model, StringComparison.OrdinalIgnoreCase))
                return z;
        return null;
    }

    /// <summary>Nom de la porte du script faite de ce modèle FD1, ou de cette instance (null : ce n'en est pas une).</summary>
    private string? ScriptDoorName(string model, string? instance)
    {
        if (!ScriptDoors.TryGetValue(Mission, out (string model, string name)[]? doors))
            return null;
        foreach ((string m, string name) in doors)
            if (m.Equals(model, StringComparison.OrdinalIgnoreCase) || (instance != null && m.Equals(instance, StringComparison.OrdinalIgnoreCase)))
                return name;
        return null;
    }

    /// <summary>Porte et message du bouton du script de ce nom FD1 (null : ce n'en est pas un).</summary>
    private (string door, string message)? ScriptButtonOf(string name)
    {
        if (!ScriptButtons.TryGetValue(Mission, out (string button, string door, string message)[]? buttons))
            return null;
        foreach ((string b, string door, string message) in buttons)
            if (b.Equals(name, StringComparison.OrdinalIgnoreCase))
                return (door, message);
        return null;
    }

    /// <summary>
    /// Planchers en hauteur dans la grille : les cases dont le centre est sous un triangle horizontal du plancher (à 60 cm
    /// près de sa hauteur) s'ouvrent, à cette hauteur. Les cases du mur des passages au grappin aussi (à la hauteur du
    /// plancher : seul le scout les franchit). Rend le nombre de cases.
    /// </summary>
    /// <summary>Missions où une case au sol prise entre deux cases de plancher d'étage (face à face) devient du plancher.</summary>
    private static readonly HashSet<string> LowFloorJoints = new(StringComparer.OrdinalIgnoreCase) { "ecole" };

    private int ApplyUpperFloors(Grid g)
    {
        int n = 0;
        foreach ((List<Fd3Mesh> meshes, Matrix4x4 w, float z, bool sagging) in _upperFloors)
            foreach (Fd3Mesh m in meshes)
                for (int t = 0; t + 2 < m.Indices.Count; t += 3)
                {
                    Vector3 a = Vector3.Transform(Vertex(m, m.Indices[t]), w);
                    Vector3 b = Vector3.Transform(Vertex(m, m.Indices[t + 1]), w);
                    Vector3 c = Vector3.Transform(Vertex(m, m.Indices[t + 2]), w);
                    Vector3 normal = Vector3.Cross(b - a, c - a);
                    if (normal.Length() < 1 || MathF.Abs(Vector3.Normalize(normal).Z) < (sagging ? 0.8f : 0.95f)
                        || (a.Z + b.Z + c.Z) / 3 - z > 60 || z - (a.Z + b.Z + c.Z) / 3 > (sagging ? 200 : 60))
                        continue;
                    int x0 = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / Grid.CellSize);
                    int x1 = (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / Grid.CellSize);
                    int y0 = (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) / Grid.CellSize);
                    int y1 = (int)MathF.Floor(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) / Grid.CellSize);
                    for (int cy = y0; cy <= y1; cy++)
                        for (int cx = x0; cx <= x1; cx++)
                        {
                            Vector2 p = new Vector2((cx + 0.5f) * Grid.CellSize, (cy + 0.5f) * Grid.CellSize);
                            if (!g.Inside(cx, cy) || !InTriangle(p, a, b, c))
                                continue;
                            int k = g.Index(cx, cy);
                            if (g.ZoneA[k] == 0 || g.GroundHeight[k] != z)
                                n++;
                            g.ZoneA[k] = 1;
                            g.GroundHeight[k] = z;
                        }
                }
        // une case fermée entre deux cases de plancher à la même hauteur (en face l'une de l'autre) : le plancher continue
        // (Entrepot : la jointure entre la passerelle et le dessus du bloc noir)
        List<int> joints = new List<int>();
        // cases au sol devenues du plancher (LowFloorJoints) : leur hauteur d'avant
        Dictionary<int, float> lowJoints = new Dictionary<int, float>();
        for (int cy = 1; cy + 1 < g.Height; cy++)
            for (int cx = 1; cx + 1 < g.Width; cx++)
            {
                int k = g.Index(cx, cy);
                // une case fermée, ou (missions de LowFloorJoints) ouverte mais au sol (Ecole : la ligne du mur sous la porte de
                // la classe qui s'effondre, entre le plancher du couloir et celui de la classe)
                if (g.ZoneA[k] != 0 && !(LowFloorJoints.Contains(Mission) && _upperFloors.Any(f => g.GroundHeight[k] < f.z - 200)))
                    continue;
                foreach ((int dx, int dy) in new[] { (1, 0), (0, 1) })
                {
                    int a = g.Index(cx - dx, cy - dy), b = g.Index(cx + dx, cy + dy);
                    bool floorA = g.ZoneA[a] != 0 && _upperFloors.Any(f => f.z == g.GroundHeight[a]) && (g.ZoneA[k] == 0 || g.GroundHeight[a] - g.GroundHeight[k] > 200);
                    if (floorA && g.ZoneA[b] != 0 && g.GroundHeight[b] == g.GroundHeight[a])
                    {
                        joints.Add(k);
                        if (g.ZoneA[k] != 0)
                            lowJoints[k] = g.GroundHeight[k];
                        g.GroundHeight[k] = g.GroundHeight[a];
                        break;
                    }
                }
            }
        foreach (int k in joints)
            g.ZoneA[k] = 1;
        n += joints.Count;
        // murs et meubles des étages, sur les cases de leur plancher seulement (comme au sol : un mur traverse une case en
        // deux carrés au moins, un meuble en couvre plus de la moitié)
        int per = (int)(Grid.CellSize / 25);
        foreach ((Dictionary<float, HashSet<(int x, int y)>> sets, int need) in new[] { (_upperWallQuarters, 2), (_upperBlockedQuarters, per * per / 2 + 1) })
            foreach ((float z, HashSet<(int x, int y)> quarters) in sets)
                foreach (IGrouping<int, (int x, int y)> cell in quarters.Where(q => q.x >= 0 && q.y >= 0).GroupBy(q => g.Inside(q.x / per, q.y / per) ? g.Index(q.x / per, q.y / per) : -1))
                    if (cell.Key >= 0 && cell.Count() >= need && g.ZoneA[cell.Key] != 0 && g.GroundHeight[cell.Key] == z)
                        g.ZoneA[cell.Key] = 0;
        // une case au sol devenue du plancher puis fermée par un mur de l'étage : elle redevient ce qu'elle était (seules
        // restent les cases du passage, la porte)
        foreach ((int k, float h) in lowJoints)
            if (g.ZoneA[k] == 0)
            {
                g.ZoneA[k] = 1;
                g.GroundHeight[k] = h;
                n--;
            }
        if (GrappleWalls.TryGetValue(Mission, out (Vector3 top, float yaw)[]? walls))
            foreach ((Vector3 top, float yaw) in walls)
            {
                // les cases que le moteur prend pour le mur du passage (dans la ligne du mur à 70 cm près, sur 2,2 m de part
                // et d'autre du repère) : ouvertes, à la hauteur du plancher le plus proche
                Vector2 axis = new Vector2(MathF.Cos(yaw), MathF.Sin(yaw));
                Vector2 perp = new Vector2(-axis.Y, axis.X);
                float floor = _upperFloors.Select(f => f.z).OrderBy(z => MathF.Abs(z - top.Z)).DefaultIfEmpty(top.Z).First();
                for (int cy = 0; cy < g.Height; cy++)
                    for (int cx = 0; cx < g.Width; cx++)
                    {
                        Vector2 c = new Vector2((cx + 0.5f) * Grid.CellSize, (cy + 0.5f) * Grid.CellSize) - new Vector2(top.X, top.Y);
                        if (MathF.Abs(Vector2.Dot(c, perp)) > 70 || MathF.Abs(Vector2.Dot(c, axis)) > 220)
                            continue;
                        int k = g.Index(cx, cy);
                        g.ZoneA[k] = 1;
                        g.GroundHeight[k] = floor;
                        n++;
                    }
            }
        return n;
    }

    /// <summary>Le point p est-il dans le triangle abc (vu de dessus) ?</summary>
    private static bool InTriangle(Vector2 p, Vector3 a, Vector3 b, Vector3 c)
    {
        float d1 = (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);
        float d2 = (p.X - c.X) * (b.Y - c.Y) - (b.X - c.X) * (p.Y - c.Y);
        float d3 = (p.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (p.Y - a.Y);
        bool negative = d1 < 0 || d2 < 0 || d3 < 0;
        bool positive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(negative && positive);
    }

    /// <summary>Repères des passages au grappin dans le niveau du moteur.</summary>
    private void AddGrapples(Level level)
    {
        if (!GrappleWalls.TryGetValue(Mission, out (Vector3 top, float yaw)[]? walls))
            return;
        LevelObject? symbol = TemplateObject("functional\\koshka_symbol");
        if (symbol == null)
        {
            Report.Add("grappin : repère modèle introuvable dans les missions du jeu");
            return;
        }
        int k = 0;
        foreach ((Vector3 top, float yaw) in walls)
        {
            k++;
            LevelObject s = LevelEdit.Clone(symbol, LevelEdit.NewId(level), $"grappin_{k}", symbol.ClassPath);
            s = LevelEdit.WithTransform(s, top, new Vector3(0, 0, yaw));
            s = LevelEdit.WithStartVisible(s, true);
            level.Objects.Add(s);
        }
        Report.Add($"grappin : {walls.Length} passage(s)");
    }

    /// <summary>Portes du script que le joueur ouvre lui-même, par mission (nom de la porte).</summary>
    private static readonly Dictionary<string, string[]> UsableDoors = new(StringComparer.OrdinalIgnoreCase)
    {
        { "ecole", new[] { "pcf0", "pcf1", "pcf2", "pcf3" } },
    };

    /// <summary>Portes du script dans le niveau du moteur (fermées, elles montent quand le script les ouvre).</summary>
    private void AddScriptDoors(Level level)
    {
        if (_scriptDoors.Count == 0)
            return;
        LevelObject? door = TemplateObject("opening\\factory_store");
        if (door == null)
        {
            Report.Add("portes du script : porte modèle introuvable dans les missions du jeu");
            return;
        }
        foreach ((Fd1Object o, List<Fd3Mesh> meshes, string name) in _scriptDoors)
        {
            bool usable = UsableDoors.TryGetValue(Mission, out string[]? usables) && usables.Contains(name, StringComparer.OrdinalIgnoreCase);
            string cls = WriteDoorModel(o.Model, meshes, true, usable);
            Vector3 at = new Vector3(o.World.M41, o.World.M42, o.World.M43) * WorldToCm;
            float yaw = MathF.Atan2(o.World.M12, o.World.M11);
            LevelObject d = LevelEdit.Clone(door, LevelEdit.NewId(level), name, cls);
            d = LevelEdit.WithTransform(d, at, new Vector3(0, 0, yaw));
            d = LevelEdit.WithStartVisible(d, true);
            d = WithInitialState(d, 0);
            level.Objects.Add(d);
        }
        Report.Add($"portes du script : {string.Join(", ", _scriptDoors.Select(d => d.name))}");
    }

    /// <summary>
    /// Classe des boutons : Design\placeable\functional\useable\&lt;nom&gt;_&lt;modèle&gt;.class (utilisable par tout pompier qui
    /// sait utiliser un objet ; « _ing » : seul un ingénieur, qui le répare). Rend son chemin.
    /// </summary>
    private string WriteButtonClass(string model, List<Fd3Mesh> meshes, bool engineer)
    {
        (string safe, List<(string node, Matrix4x4 world)> _, float _) = WritePlaceableModel(model, meshes);
        // seul un ingénieur s'en sert : il « répare » (Villa : la pompe de la piscine)
        string actions = engineer ? "COULDBEUSE = 0\nCOULDBEREPAIR = 1" : "COULDBEUSE = 1";
        string file = engineer ? safe + "_ing" : safe;
        string classDir = Path.Combine(Data, "Design", "placeable", "functional", "useable");
        Directory.CreateDirectory(classDir);
        string cls = $"""
            [#INCLUDE]
            FILE0 = "base_useable.class"

            [DISPLAY]
            MODEL = {Name}\{safe}

            [DAMAGE]
            USEDAMAGE = 0
            CARBONISABLE = 0

            [CAPACITIES_ACTIONS]
            {actions}

            [EXPLOSION]
            CANEXPLODE = 0

            [LOGICAL_STATE]
            S01_USER_ANIM = ItemChar\ic_pick_up_down
            S01__NAME = use

            """;
        File.WriteAllBytes(Path.Combine(classDir, file + ".class"), Encoding.Latin1.GetBytes(cls.Replace("\n", "\r\n")));
        return "design\\placeable\\functional\\useable\\" + file;
    }

    /// <summary>
    /// Bouton FD1 tel qu'il sera posé : null s'il est hors de la carte ou si un bouton du même nom est déjà pris (Quartier :
    /// trois robinets de gaz « qua_arret_gaz », dont deux hors de la carte). Un modèle dessiné loin de son origine (le robinet
    /// est à 66 m de la sienne) est recentré : le moteur fait venir le pompier à l'origine de l'objet.
    /// </summary>
    private (Fd1Object o, List<Fd3Mesh> meshes)? PlaceButton(Fd1Object o, List<Fd3Mesh> meshes)
    {
        if (_scriptButtons.Any(b => b.o.Name.Equals(o.Name, StringComparison.OrdinalIgnoreCase)))
            return null;
        Matrix4x4 w = o.World;
        w.M41 *= WorldToCm;
        w.M42 *= WorldToCm;
        w.M43 *= WorldToCm;
        List<Vector3> local = meshes.SelectMany(m => Enumerable.Range(0, m.VertexCount).Select(i => Vertex(m, i))).ToList();
        if (local.Count == 0)
            return (o, meshes);
        Vector3 center = new Vector3((local.Min(p => p.X) + local.Max(p => p.X)) / 2, (local.Min(p => p.Y) + local.Max(p => p.Y)) / 2, 0);
        Vector3 world = Vector3.Transform(center, w);
        if (world.X < 0 || world.Y < 0 || world.X > MaxMapCm || world.Y > MaxMapCm)
            return null;
        if (center.Length() < 300)
            return (o, meshes);
        List<Fd3Mesh> moved = meshes.Select(m => Shifted(m, -center)).ToList();
        Matrix4x4 nw = o.World;
        nw.M41 = world.X / WorldToCm;
        nw.M42 = world.Y / WorldToCm;
        Fd1Object c = new Fd1Object { Model = o.Model + "_C", Class = o.Class, Name = o.Name, Race = o.Race, World = nw, Offset = o.Offset };
        return (c, moved);
    }

    /// <summary>Plus grande coordonnée d'un objet sur la carte (cm) : au-delà, un reste de l'éditeur FD1.</summary>
    private const float MaxMapCm = 30000;

    /// <summary>Copie d'un maillage déplacé.</summary>
    private static Fd3Mesh Shifted(Fd3Mesh m, Vector3 by)
    {
        Fd3Mesh r = new Fd3Mesh { Name = m.Name, Uv = m.Uv, Indices = m.Indices, Groups = m.Groups, Normals = m.Normals };
        for (int i = 0; i + 2 < m.Positions.Count; i += 3)
        {
            r.Positions.Add(m.Positions[i] + by.X);
            r.Positions.Add(m.Positions[i + 1] + by.Y);
            r.Positions.Add(m.Positions[i + 2] + by.Z);
        }
        return r;
    }

    /// <summary>Boutons du script dans le niveau du moteur : leur action appelle « Use_&lt;bouton&gt; ».</summary>
    private void AddScriptButtons(Level level)
    {
        if (_scriptButtons.Count == 0)
            return;
        LevelObject? template = ActionTemplate();
        if (template == null)
        {
            Report.Add("boutons du script : objet utilisable modèle introuvable dans les missions du jeu");
            return;
        }
        foreach ((Fd1Object o, List<Fd3Mesh> meshes, string door, string _) in _scriptButtons)
        {
            bool engineer = EngineerButtons.TryGetValue(Mission, out string[]? eb) && eb.Contains(o.Name, StringComparer.OrdinalIgnoreCase);
            string cls = WriteButtonClass(o.Model, meshes, engineer);
            Vector3 at = new Vector3(o.World.M41, o.World.M42, o.World.M43) * WorldToCm;
            float yaw = MathF.Atan2(o.World.M12, o.World.M11);
            LevelObject b = LevelEdit.Clone(template, LevelEdit.NewId(level), o.Name, cls);
            b = LevelEdit.WithTransform(b, at, new Vector3(0, 0, yaw));
            b = LevelEdit.WithStartVisible(b, true);
            b = WithActionScript(b, "Use_" + o.Name);
            level.Objects.Add(b);
        }
        Report.Add($"boutons du script : {string.Join(", ", _scriptButtons.Select(b => b.o.Name + " → " + b.door))}");
    }

    /// <summary>
    /// Escaliers des échelles du script dans le niveau du moteur : l'escalier « &lt;nom&gt; » et ses icônes
    /// « &lt;nom&gt;_haut » et « &lt;nom&gt;_bas » (numéros qui se suivent : le moteur les trouve ainsi), tous cachés au départ.
    /// </summary>
    private void AddScriptStairs(Level level)
    {
        if (!ScriptStairs.TryGetValue(Mission, out (string name, Vector3 top, Vector3 bottom)[]? stairs))
            return;
        LevelObject? stair = TemplateObject("teleport_stair");
        LevelObject? symbol = TemplateObject("stair_symbol");
        if (stair == null || symbol == null)
        {
            Report.Add("échelles du script : escalier modèle introuvable dans les missions du jeu");
            return;
        }
        foreach ((string name, Vector3 top, Vector3 bottom) in stairs)
        {
            LevelObject t = LevelEdit.Clone(stair, LevelEdit.NewId(level), name, stair.ClassPath);
            t = LevelEdit.WithTransform(t, (top + bottom) / 2, Vector3.Zero);
            t = LevelEdit.WithStartVisible(t, false);
            level.Objects.Add(t);
            foreach ((string end, Vector3 at) in new[] { ("haut", top), ("bas", bottom) })
            {
                LevelObject e = LevelEdit.Clone(symbol, LevelEdit.NewId(level), $"{name}_{end}", symbol.ClassPath);
                e = LevelEdit.WithTransform(e, at, Vector3.Zero);
                e = LevelEdit.WithStartVisible(e, false);
                level.Objects.Add(e);
            }
        }
        Report.Add($"échelles du script : {string.Join(", ", stairs.Select(x => x.name))} (cachées au départ)");
    }

    /// <summary>Le bouton rend utilisable cette échelle du script (sinon : il ouvre une porte).</summary>
    private bool IsScriptStair(string name) =>
        ScriptStairs.TryGetValue(Mission, out (string name, Vector3 top, Vector3 bottom)[]? stairs) && stairs.Any(x => x.name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Script des boutons : chacun ouvre sa porte (ou montre son échelle) une fois, puis ne sert plus.</summary>
    private string ButtonScript()
    {
        StringBuilder s = new StringBuilder();
        foreach ((Fd1Object o, List<Fd3Mesh> _, string door, string message) in _scriptButtons)
        {
            s.Append($"// {o.Name} : {(door.StartsWith('@') ? "appelle" : IsScriptStair(door) ? "rend utilisable l'échelle" : "ouvre")} {door.TrimStart('@')}\r\n");
            s.Append($"void Use_{o.Name}(EntityID User, EntityID Bouton, mcString State)\r\n{{\r\n");
            s.Append($"\tif(SCR_GetVarS32(\"{o.Name}\") == 0)\r\n\t{{\r\n");
            s.Append($"\t\tSCR_SetVarS32(\"{o.Name}\", 1);\r\n");
            // qui a utilisé l'objet (Entrepot : le scout, au bouton de l'échelle 1)
            s.Append($"\t\tSCR_SetVarS32(\"User_{o.Name}\", User);\r\n");
            // sa cinématique FD1 (MissionConverter.Story.cs, Cinematics.cs)
            if (ButtonCinematics.TryGetValue(o.Name, out string? cine) && Cine(cine).Length > 0)
                s.Append($"\t\t{Cine(cine)}\r\n");
            if (door.StartsWith('@'))
                s.Append($"\t\t{door[1..]}();\r\n");
            else if (IsScriptStair(door))
            {
                foreach (string part in new[] { door, door + "_haut", door + "_bas" })
                    s.Append($"\t\tENT_SetVisibility(ENT_FindByName(\"{part}\"), 1);\r\n");
                s.Append($"\t\tENT_ActivateTeleport(ENT_FindByName(\"{door}\"), 1);\r\n");
            }
            else
                s.Append($"\t\tENT_SwitchState(ENT_FindByName(\"{door}\"));\r\n");
            s.Append($"\t\tENT_SetCapacityTarget(Bouton, \"CouldBeUse\", 0);\r\n");
            if (message.Length > 0)
                s.Append($"\t\tPNL_AddMessage(\"\", \"{message}\", 0.0, 0.0);\r\n");
            s.Append("\t}\r\n}\r\n\r\n");
        }
        return s.ToString();
    }
}
