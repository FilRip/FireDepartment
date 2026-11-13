using System.Numerics;
using System.Text;
using OpenFD.Formats;
using OpenFD.MG;

namespace OpenFD.ConvFD1;

/// <summary>
/// Grandes portes FD1 (portes de quai « ENT_PORTEGR » d'Entrepot) : de vraies portes du moteur. Chacune a un bouton rouge
/// (« Ent_Bouton_GP&lt;n&gt; ») qui l'ouvre ou la ferme (script FD1 BGP&lt;n&gt; : animation « eAnimation_Action2 » ou
/// « Action3 », puis passage ouvert ou fermé). Pour le moteur : un objet posé de classe porte (modèle, animation qui la
/// fait monter, jouée à l'envers pour la fermer, non cassable) et un symbole de mécanisme (« mechanism_symbol ») à la
/// place du bouton : le moteur en fait une porte à levier (l'unité va au bouton, la porte s'ouvre ou se ferme).
/// </summary>
public sealed partial class MissionConverter
{
    /// <summary>Modèles FD1 des grandes portes (la porte entière monte à l'ouverture).</summary>
    private static bool IsBigDoor(string model) => model.Contains("PORTEGR", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Modèles FD1 des portes cassables (« sta_porte_01 », « sta_porte_04 » : Porte1 à Porte6 du script, faites d'éléments
    /// qui brûlent ; détruites, elles découvrent la pièce derrière) : un pompier avec une hache les casse pour passer.
    /// Au sol, ou sur un plancher d'étage (Entrepot : la porte du bureau de la secrétaire, à 15 m).
    /// </summary>
    private static bool IsBreakableDoor(Fd1Object o, IEnumerable<float> floors) => (o.Model.StartsWith("sta_porte_", StringComparison.OrdinalIgnoreCase)
            || o.Model.StartsWith("ECO_PORTELOCALGRILLE", StringComparison.OrdinalIgnoreCase)
            // Villa : la grille du garage (entre les portes 4 et 5), les petites grilles du fond de la villa
            || o.Model.StartsWith("VIL_GRILLES02_GARAGE", StringComparison.OrdinalIgnoreCase)
            || o.Model.StartsWith("ECO_PTTGRILLE01_BUT", StringComparison.OrdinalIgnoreCase)
            || QuartierBreakable.Any(m => o.Model.StartsWith(m, StringComparison.OrdinalIgnoreCase))
            // Station-service : les planches qui condamnent les ouvertures du chantier (Porte6 à Porte8)
            || o.Model.StartsWith("sta_planches", StringComparison.OrdinalIgnoreCase)
            // Train : la porte de l'écurie (le palefrenier est derrière)
            || o.Model.StartsWith("TRA_PORTE_ECURIES", StringComparison.OrdinalIgnoreCase)
            // Ecole : les fenêtres du bâtiment administratif (cassées, la fumée des pièces s'en va)
            || o.Model.StartsWith("ECO_FENETRE_BUT1", StringComparison.OrdinalIgnoreCase)
            // Centrale : le ventilateur du mur des vestiaires (Porte0 : le sapeur le casse, la fumée s'en va)
            || o.Model.StartsWith("VENTILO_BUTINABLE", StringComparison.OrdinalIgnoreCase))
        && (o.World.M43 * WorldToCm < 50 || floors.Any(z => MathF.Abs(o.World.M43 * WorldToCm - z) < 50));

    /// <summary>
    /// Quartier : les portes et cloisons cassables du troisième étage (« QUA_PORTE_CASSER », les deux portes du backdraft, la
    /// fenêtre et la cloison à casser, le mur du sapeur) et la grille du local du gaz dans la cour.
    /// </summary>
    private static readonly string[] QuartierBreakable =
    {
        "QUA_PORTE_CASSER", "PORTE_BACKDRAFT", "QUA_FENETRE_BUT", "QUA_CLOISONA_BUT", "QUART_MUR_SAP", "QUA_PORTELOCALGRILLE",
    };

    /// <summary>Portes cassables trouvées dans le niveau FD1.</summary>
    private readonly List<(Fd1Object o, List<Fd3Mesh> meshes)> _breakableDoors = new List<(Fd1Object, List<Fd3Mesh>)>();

    /// <summary>Grandes portes ouvertes au départ, par mission (Entrepot : Init_Global ouvre GPorte5).</summary>
    private static readonly Dictionary<string, string[]> DoorsOpenAtStart = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Entrepot", new[] { "GPorte5" } },
    };

    /// <summary>Durée de l'ouverture d'une grande porte (s).</summary>
    private const float DoorOpenSeconds = 3f;

    /// <summary>Grandes portes trouvées dans le niveau FD1 (objet, maillages en cm dans le repère du modèle).</summary>
    private readonly List<(Fd1Object o, List<Fd3Mesh> meshes)> _bigDoors = new List<(Fd1Object, List<Fd3Mesh>)>();

    /// <summary>
    /// Modèle de la porte (WritePlaceableModel), animation d'ouverture
    /// (Gfx\Anim\SingleItem\&lt;nom&gt;_&lt;modèle&gt;.anim : chaque pièce monte de la hauteur de la porte) et classe
    /// (Design\placeable\functional\opening\&lt;nom&gt;_&lt;modèle&gt;.class). Rend le chemin de la classe. Une porte qui monte
    /// mais pas utilisable (<paramref name="usable"/> faux) ne s'ouvre que par le script (ENT_SwitchState).
    /// </summary>
    private string WriteDoorModel(string model, List<Fd3Mesh> meshes, bool rises, bool usable = true, bool window = false, bool sinks = false)
    {
        (string safe, List<(string node, Matrix4x4 world)> nodes, float top) = WritePlaceableModel(model, meshes);
        string classDir = Path.Combine(Data, "Design", "placeable", "functional", "opening");
        Directory.CreateDirectory(classDir);
        if (!rises)
        {
            // porte cassable : ni ouverte ni animée ; cassée, elle disparaît (comme dans FD1) ; une fenêtre (classe de fenêtre
            // du jeu) se casse aussi depuis l'échelle
            string plain = $"""
                [#INCLUDE]
                FILE0 = "{(window ? "base_window_break.class" : "base_door.class")}"

                [DISPLAY]
                MODEL = {Name}\{safe}

                [CAPACITIES_ACTIONS]
                COULDBEBREAK = 1
                COULDBEUSE = 0

                """;
            File.WriteAllBytes(Path.Combine(classDir, safe + ".class"), Encoding.Latin1.GetBytes(plain.Replace("\n", "\r\n")));
            return "design\\placeable\\functional\\opening\\" + safe;
        }
        // ouverture : la porte monte de sa hauteur (moins 30 cm : le bas reste visible sous le linteau, comme dans FD1)
        // (barrage du script : ouvert, il est caché sous le plancher, de toute sa hauteur ; fermé, il remonte dans l'embrasure)
        (float t, Vector3 p)[] keys = { (0f, Vector3.Zero), (DoorOpenSeconds, new Vector3(0, 0, sinks ? -(top + 20) : MathF.Max(100, top - 30))) };
        string animDir = Path.Combine(Data, "Gfx", "Anim", "SingleItem");
        Directory.CreateDirectory(animDir);
        File.WriteAllBytes(Path.Combine(animDir, safe + ".anim"), Fd3Writer.Anim(nodes.Select(n => (n.node, keys)).ToList()));
        string cls = $"""
            [#INCLUDE]
            FILE0 = "base_door.class"

            [DISPLAY]
            MODEL = {Name}\{safe}

            [DAMAGE]
            HITDAMAGECOEF = 0
            USEDAMAGE = 0
            NBDAMAGESTEPS = 1

            [LOGICAL_STATE]
            S01_ANIMFLAG = HOLD_REVERSE
            S02_USER_ANIM =
            S02_ANIM = SingleItem\{safe}
            S02__NAME = Open
            S01_USER_ANIM =
            S01_ANIM = SingleItem\{safe}
            S01__NAME = Close

            [CAPACITIES_ACTIONS]
            COULDBEBREAK = 0
            COULDBEUSE = {(usable ? 1 : 0)}

            [PICKING]
            USEPICKINGBOX = 0

            """;
        File.WriteAllBytes(Path.Combine(classDir, safe + ".class"), Encoding.Latin1.GetBytes(cls.Replace("\n", "\r\n")));
        return "design\\placeable\\functional\\opening\\" + safe;
    }

    /// <summary>
    /// Modèle d'objet posé : Gfx\Placeable\&lt;nom&gt;\&lt;nom&gt;_&lt;modèle&gt;.sg, ses maillages et ses matériaux. Rend le nom du
    /// modèle, ses nœuds et la hauteur de son plus haut sommet.
    /// </summary>
    private (string safe, List<(string node, Matrix4x4 world)> nodes, float top) WritePlaceableModel(string model, List<Fd3Mesh> meshes)
    {
        string safe = (Name + "_" + ModelConverter.Safe(model)).ToLowerInvariant();
        string dir = Path.Combine(Data, "Gfx", "Placeable", Name);
        Directory.CreateDirectory(Path.Combine(dir, "Shaders"));
        byte[] template = Fd3.TryRead("Gfx\\Placeable\\Shaders\\lv_Autom_022.stdmat")
            ?? throw new FileNotFoundException("matériau modèle introuvable : Gfx\\Placeable\\Shaders\\lv_Autom_022.stdmat");
        List<(string node, Matrix4x4 world)> nodes = new List<(string, Matrix4x4)>();
        float top = 0;
        for (int i = 0; i < meshes.Count; i++)
        {
            string node = $"{safe}_{i}";
            Fd3Mesh m = meshes[i];
            Fd3Mesh copy = new Fd3Mesh { Name = node, Positions = m.Positions, Normals = m.Normals, Uv = m.Uv, Indices = m.Indices, Groups = m.Groups };
            File.WriteAllBytes(Path.Combine(dir, node + ".mesh"), Fd3Writer.MeshFile(copy));
            File.WriteAllBytes(Path.Combine(dir, node + ".meshdata"), Fd3Writer.MeshData(copy));
            nodes.Add((node, Matrix4x4.Identity));
            foreach ((string mat, int _, int _) in m.Groups)
                File.WriteAllBytes(Path.Combine(dir, "Shaders", mat + ".stdmat"), Fd3Writer.Stdmat(template, mat, mat + ".tga"));
            for (int k = 2; k < m.Positions.Count; k += 3)
                top = MathF.Max(top, m.Positions[k]);
        }
        File.WriteAllBytes(Path.Combine(dir, safe + ".sg"), Fd3Writer.Scene(nodes));
        return (safe, nodes, top);
    }

    /// <summary>
    /// Modèles FD1 cassables qui sont des fenêtres (on les casse depuis l'échelle) : Ecole, les fenêtres de l'étage du
    /// bâtiment administratif (aucun escalier n'y mène).
    /// </summary>
    private static readonly string[] WindowModels = { "ECO_FENETRE_BUT1" };

    /// <summary>
    /// Portes cassables doublées d'un « barrage » du script, par mission : nom FD1 de la porte → nom du barrage. Le barrage
    /// est une copie de la porte, ouverte (cachée sous le plancher) au départ, que le joueur ne peut pas actionner ; le script la ferme
    /// (ENT_SwitchState) et le passage est condamné. Ecole : la salle de classe dont le plancher s'effondre (launchEffondr).
    /// </summary>
    private static readonly Dictionary<string, (string door, string gate)[]> BlockingGates = new(StringComparer.OrdinalIgnoreCase)
    {
        { "ecole", new[] { ("porte15", "bloque15") } },
    };

    /// <summary>Portes cassables dans le niveau du moteur (fermées au départ).</summary>
    private void AddBreakableDoors(Level level)
    {
        if (_breakableDoors.Count == 0)
            return;
        LevelObject? door = TemplateObject("opening\\factory_store");
        if (door == null)
        {
            Report.Add("portes cassables : porte modèle introuvable dans les missions du jeu");
            return;
        }
        Dictionary<string, string> classes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int k = 0;
        foreach ((Fd1Object o, List<Fd3Mesh> meshes) in _breakableDoors)
        {
            if (!classes.TryGetValue(o.Model, out string? cls))
                classes[o.Model] = cls = WriteDoorModel(o.Model, meshes, false, true, WindowModels.Any(m => o.Model.StartsWith(m, StringComparison.OrdinalIgnoreCase)));
            Vector3 at = new Vector3(o.World.M41, o.World.M42, o.World.M43) * WorldToCm;
            float yaw = MathF.Atan2(o.World.M12, o.World.M11);
            k++;
            // nom FD1 de la porte (« Porte4 » : les scripts FD1 s'en servent), sinon un numéro
            string? fd1Name = InstanceName(o);
            string name = fd1Name != null && System.Text.RegularExpressions.Regex.IsMatch(fd1Name, @"^Porte\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? fd1Name : $"porte_cassable_{k}";
            LevelObject d = LevelEdit.Clone(door, LevelEdit.NewId(level), name, cls);
            d = LevelEdit.WithTransform(d, at, new Vector3(0, 0, yaw));
            d = LevelEdit.WithStartVisible(d, true);
            d = WithInitialState(d, 0);
            level.Objects.Add(d);
            // son barrage (même modèle, sous un autre nom de fichier : la classe de la porte cassable reste à elle)
            if (fd1Name != null && BlockingGates.TryGetValue(Mission, out (string door, string gate)[]? gates)
                && gates.FirstOrDefault(x => x.door.Equals(fd1Name, StringComparison.OrdinalIgnoreCase)) is { gate: not null } found)
            {
                string gateClass = WriteDoorModel(o.Model + "_bloc", meshes, true, false, false, true);
                LevelObject g = LevelEdit.Clone(door, LevelEdit.NewId(level), found.gate, gateClass);
                g = LevelEdit.WithTransform(g, at, new Vector3(0, 0, yaw));
                g = LevelEdit.WithStartVisible(g, true);
                g = WithInitialState(g, 1);
                level.Objects.Add(g);
                Report.Add($"barrage du script : {found.gate} ({fd1Name})");
            }
        }
        Report.Add($"portes cassables : {_breakableDoors.Count}");
    }

    /// <summary>
    /// Gravats que seul le désincarcérateur découpe, par mission (modèles FD1). Ecole : le bras de grue tombé dans la
    /// première salle de classe, les deux tas de gravats de la salle de classe 4 (FD1 : objets « butinables »), l'éboulis
    /// qui barre le couloir entre les salles de classe et le câble
    /// électrique tombé devant l'école (« porte30 » : découpé, la grue n'est plus sous tension).
    /// </summary>
    private static readonly Dictionary<string, string[]> ExtricableModels = new(StringComparer.OrdinalIgnoreCase)
    {
        { "ecole", new[] { "ECO_GRUE_BUTINABLE01", "ECO_DEBRIS_S06ABUT_ETAT01", "ECO_DEBRIS_S06BBUT_ETAT01", "ECO_ELEMENT_COULOIR_ETAT1",
            "ECO_RESEAU_ELEC_BUT_ETAT1" } },
    };

    /// <summary>
    /// Obstacles que le bulldozer déblaie, par mission (début du modèle FD1). Foret : les broussailles « butinables » que le
    /// Wildland (le bulldozer) défriche ; sept d'entre elles (Brousaille1 à 7) occupent la clairière de l'héliport 2.
    /// </summary>
    private static readonly Dictionary<string, string[]> DemolishModels = new(StringComparer.OrdinalIgnoreCase)
    {
        { "foret", new[] { "FOR_BROUSSAILS_BUTINABLES" } },
    };

    /// <summary>Points de vie des obstacles à déblayer (un coup de pelle en enlève 32).</summary>
    private const float DemolishLife = 100;

    /// <summary>Obstacles à déblayer trouvés dans le niveau FD1 (objet, maillages en cm dans le repère du modèle).</summary>
    private readonly List<(Fd1Object o, List<Fd3Mesh> meshes)> _demolishables = new List<(Fd1Object, List<Fd3Mesh>)>();

    private bool IsDemolishable(string model) =>
        DemolishModels.TryGetValue(Mission, out string[]? models) && models.Any(m => model.StartsWith(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>La carte donne-t-elle à cet objet du jeu la capacité « déblayable » (composant A026, octet 37) ?</summary>
    private static bool CanBeDemolished(LevelObject o)
    {
        if (o.GetComponent(0xA026) is not { } cc)
            return false;
        ReadOnlySpan<byte> b = o.ComponentBytes(cc);
        return b.Length > 37 && b[22] == 0xAA && b[26] == 0x09 && b[37] == 1;
    }

    /// <summary>
    /// Obstacles à déblayer dans le niveau du moteur : copies d'un objet du jeu que la carte marque déblayable (le moteur
    /// en fait un obstacle qui ferme ses cases jusqu'à ce que le bulldozer l'ait enlevé : Openings.LoadDemolishable), sous
    /// leur nom FD1 (le script lit leur état : « Broken » une fois déblayés).
    /// </summary>
    private void AddDemolishables(Level level)
    {
        if (_demolishables.Count == 0)
            return;
        LevelObject? template = TemplateWhere(o => o.Kind == EntityKind.Placeable && CanBeDemolished(o));
        if (template == null)
        {
            Report.Add("obstacles à déblayer : objet modèle introuvable dans les missions du jeu");
            return;
        }
        string classDir = Path.Combine(Data, "Design", "placeable", "levels");
        Directory.CreateDirectory(classDir);
        Dictionary<string, string> classes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int k = 0;
        foreach ((Fd1Object o, List<Fd3Mesh> meshes) in _demolishables)
        {
            if (!classes.TryGetValue(o.Model, out string? cls))
            {
                (string safe, List<(string node, Matrix4x4 world)> _, float _) = WritePlaceableModel(o.Model, meshes);
                string text = $"""
                    [#INCLUDE]
                    FILE0 = "base_levels.class"

                    [DISPLAY]
                    MODEL = {Name}\{safe}

                    [OCCUPATION]
                    OCCUPATION_TYPE = ITEM

                    [DAMAGE]
                    USEDAMAGE = 1
                    FIREDAMAGECOEF = 0
                    LIFEPOINTS = {DemolishLife}
                    MAXLIFEPOINTS = {DemolishLife}

                    [CAPACITIES_ACTIONS]
                    COULDBEDEMOLISH = 1

                    """;
                File.WriteAllBytes(Path.Combine(classDir, safe + "_bulldo.class"), Encoding.Latin1.GetBytes(text.Replace("\n", "\r\n")));
                classes[o.Model] = cls = "design\\placeable\\levels\\" + safe + "_bulldo";
            }
            Vector3 at = new Vector3(o.World.M41, o.World.M42, o.World.M43) * WorldToCm;
            float yaw = MathF.Atan2(o.World.M12, o.World.M11);
            k++;
            string name = InstanceName(o) is { } fd1Name && !fd1Name.Equals(o.Model, StringComparison.OrdinalIgnoreCase) ? fd1Name : $"deblai_{k}";
            LevelObject d = LevelEdit.Clone(template, LevelEdit.NewId(level), name, cls);
            d = LevelEdit.WithTransform(d, at, new Vector3(0, 0, yaw));
            d = LevelEdit.WithStartVisible(d, true);
            level.Objects.Add(d);
        }
        Report.Add($"obstacles à déblayer : {_demolishables.Count}");
    }

    /// <summary>Points de vie des gravats à découper (la scie du désincarcérateur).</summary>
    private const float ExtricableLife = 100;

    /// <summary>Gravats à découper trouvés dans le niveau FD1 (objet, maillages en cm dans le repère du modèle).</summary>
    private readonly List<(Fd1Object o, List<Fd3Mesh> meshes)> _extricables = new List<(Fd1Object, List<Fd3Mesh>)>();

    private bool IsExtricable(string model) =>
        ExtricableModels.TryGetValue(Mission, out string[]? models) && models.Contains(model, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gravats à découper dans le niveau du moteur : un objet posé qui a des points de vie et se laisse désincarcérer (comme
    /// les gravats de la scène de Stadium, « Stadium_remains_stage »), avec une icône de scie posée au milieu (le moteur les associe et en fait un
    /// obstacle qui barre le passage jusqu'à ce qu'il soit découpé : Openings.LoadExtricable).
    /// </summary>
    private void AddExtricables(Level level)
    {
        if (_extricables.Count == 0)
            return;
        // un objet posé quelconque du jeu sert de modèle aux gravats : l'icône de scie elle-même (sa classe est remplacée)
        LevelObject? saw = TemplateObject("saw_symbol");
        if (saw == null)
        {
            Report.Add("gravats à découper : icône de scie modèle introuvable dans les missions du jeu");
            return;
        }
        string classDir = Path.Combine(Data, "Design", "placeable", "levels");
        Directory.CreateDirectory(classDir);
        int k = 0;
        foreach ((Fd1Object o, List<Fd3Mesh> meshes) in _extricables)
        {
            (string safe, List<(string node, Matrix4x4 world)> _, float _) = WritePlaceableModel(o.Model, meshes);
            string cls = $"""
                [#INCLUDE]
                FILE0 = "base_levels.class"

                [DISPLAY]
                MODEL = {Name}\{safe}

                [OCCUPATION]
                OCCUPATION_TYPE = ITEM

                [DAMAGE]
                USEDAMAGE = 1
                FIREDAMAGECOEF = 0
                LIFEPOINTS = {ExtricableLife}
                MAXLIFEPOINTS = {ExtricableLife}

                [CAPACITIES_ACTIONS]
                COULDBEEXTRICATE = 1

                """;
            File.WriteAllBytes(Path.Combine(classDir, safe + ".class"), Encoding.Latin1.GetBytes(cls.Replace("\n", "\r\n")));
            Vector3 at = new Vector3(o.World.M41, o.World.M42, o.World.M43) * WorldToCm;
            float yaw = MathF.Atan2(o.World.M12, o.World.M11);
            k++;
            // nom FD1 (« porte30 » : le script FD1 s'en sert), sinon un numéro
            string? fd1Name = InstanceName(o);
            string name = fd1Name != null && System.Text.RegularExpressions.Regex.IsMatch(fd1Name, @"^Porte\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? fd1Name : $"gravats_{k}";
            LevelObject d = LevelEdit.Clone(saw, LevelEdit.NewId(level), name, "design\\placeable\\levels\\" + safe);
            d = LevelEdit.WithTransform(d, at, new Vector3(0, 0, yaw));
            d = LevelEdit.WithStartVisible(d, true);
            level.Objects.Add(d);
            // l'icône de scie : au milieu de l'emprise au sol des gravats (le moteur fait venir le désincarcérateur du côté
            // de l'icône quand elle est à l'écart du milieu : ici, de n'importe quel côté)
            Matrix4x4 w = o.World;
            w.M41 *= WorldToCm;
            w.M42 *= WorldToCm;
            w.M43 *= WorldToCm;
            Vector2 mid = FootprintMiddle(meshes, w, at.Z);
            LevelObject s = LevelEdit.Clone(saw, LevelEdit.NewId(level), $"{name}_scie", saw.ClassPath);
            s = LevelEdit.WithTransform(s, new Vector3(mid, at.Z), Vector3.Zero);
            s = LevelEdit.WithStartVisible(s, true);
            level.Objects.Add(s);
        }
        Report.Add($"gravats à découper : {_extricables.Count}");
    }

    /// <summary>
    /// Milieu de l'emprise au sol d'un objet (faces à moins de 4 m de sa base, carrés de 25 cm, intérieur rempli), comme le
    /// moteur la calcule (PathFind.Footprint).
    /// </summary>
    private static Vector2 FootprintMiddle(List<Fd3Mesh> meshes, Matrix4x4 w, float baseZ)
    {
        const float Step = 25;
        HashSet<(int x, int y)> set = new HashSet<(int, int)>();
        foreach (Fd3Mesh m in meshes)
            for (int t = 0; t + 2 < m.Indices.Count; t += 3)
            {
                Vector3 a = Vector3.Transform(Vertex(m, m.Indices[t]), w);
                Vector3 b = Vector3.Transform(Vertex(m, m.Indices[t + 1]), w);
                Vector3 c = Vector3.Transform(Vertex(m, m.Indices[t + 2]), w);
                float len = MathF.Max(Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y)), MathF.Max(Vector2.Distance(new(a.X, a.Y), new(c.X, c.Y)), Vector2.Distance(new(b.X, b.Y), new(c.X, c.Y))));
                int n = Math.Clamp((int)(len / Step) + 1, 1, 400);
                for (int i = 0; i <= n; i++)
                    for (int j = 0; j <= n - i; j++)
                    {
                        Vector3 p = a + (b - a) * ((float)i / n) + (c - a) * ((float)j / n);
                        if (p.Z < baseZ - 420 || p.Z > baseZ + 420)
                            continue;
                        set.Add(((int)MathF.Floor(p.X / Step), (int)MathF.Floor(p.Y / Step)));
                    }
            }
        if (set.Count == 0)
            return new Vector2(w.M41, w.M42);
        int x0 = set.Min(q => q.x) - 1, x1 = set.Max(q => q.x) + 1, y0 = set.Min(q => q.y) - 1, y1 = set.Max(q => q.y) + 1;
        int fw = x1 - x0 + 1, fh = y1 - y0 + 1;
        bool[] outside = new bool[fw * fh];
        Stack<(int, int)> stack = new Stack<(int, int)>();
        stack.Push((0, 0));
        outside[0] = true;
        while (stack.Count > 0)
        {
            (int x, int y) = stack.Pop();
            foreach ((int nx, int ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                if (nx >= 0 && ny >= 0 && nx < fw && ny < fh && !outside[ny * fw + nx] && !set.Contains((nx + x0, ny + y0)))
                {
                    outside[ny * fw + nx] = true;
                    stack.Push((nx, ny));
                }
        }
        Vector2 sum = Vector2.Zero;
        int count = 0;
        for (int y = 0; y < fh; y++)
            for (int x = 0; x < fw; x++)
                if (!outside[y * fw + x])
                {
                    sum += new Vector2((x + x0 + 0.5f) * Step, (y + y0 + 0.5f) * Step);
                    count++;
                }
        return sum / count;
    }

    /// <summary>Un objet du jeu dont la classe se termine ainsi (modèle à copier), pris dans les missions du jeu.</summary>
    private LevelObject? TemplateObject(string classSuffix)
    {
        foreach (string lv in Fd3.List("Design\\Mission\\").Where(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Level l = Level.Parse(Fd3.Read(lv));
                LevelObject? o = l.Objects.FirstOrDefault(o => o.HasTransform && o.ClassPath.EndsWith(classSuffix, StringComparison.OrdinalIgnoreCase));
                if (o != null)
                    return o;
            }
            catch (Exception)
            {
            }
        }
        return null;
    }

    /// <summary>État logique de départ (composant A027 : l'entier après la marque de fin) : 1 = ouvert.</summary>
    private static LevelObject WithInitialState(LevelObject o, int state)
    {
        if (o.GetComponent(0xA027) is not { } comp)
            return o;
        byte[] b = o.ComponentBytes(comp).ToArray();
        for (int i = 0; i + 8 <= b.Length; i++)
            if (Chunks.IsEnd(b, i))
            {
                BitConverter.GetBytes(state).CopyTo(b, i + 4);
                return LevelEdit.WithComponent(o, comp, b);
            }
        return o;
    }

    /// <summary>Grandes portes et leurs boutons dans le niveau du moteur.</summary>
    private void AddBigDoors(Level level, Fd1Level fd1)
    {
        if (_bigDoors.Count == 0)
            return;
        LevelObject? door = TemplateObject("opening\\factory_store");
        LevelObject? symbol = TemplateObject("functional\\mechanism_symbol");
        if (door == null || symbol == null)
        {
            Report.Add("grandes portes : porte ou symbole de mécanisme modèle introuvable dans les missions du jeu");
            return;
        }
        // boutons FD1 : « Ent_Bouton_GP<n> » ouvre « GPorte<n> »
        List<(int n, Vector3 at)> buttons = fd1.Objects
            .Where(o => o.Name.StartsWith("Ent_Bouton_GP", StringComparison.OrdinalIgnoreCase) && int.TryParse(o.Name["Ent_Bouton_GP".Length..], out _))
            .Select(o => (int.Parse(o.Name["Ent_Bouton_GP".Length..]), new Vector3(o.World.M41, o.World.M42, o.World.M43) * WorldToCm)).ToList();
        string[] open = DoorsOpenAtStart.TryGetValue(Mission, out string[]? op) ? op : Array.Empty<string>();
        Dictionary<string, string> classes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int k = 0;
        foreach ((Fd1Object o, List<Fd3Mesh> meshes) in _bigDoors)
        {
            if (!classes.TryGetValue(o.Model, out string? cls))
                classes[o.Model] = cls = WriteDoorModel(o.Model, meshes, true);
            Vector3 at = new Vector3(o.World.M41, o.World.M42, o.World.M43) * WorldToCm;
            float yaw = MathF.Atan2(o.World.M12, o.World.M11);
            // le bouton le plus proche (à moins de 12 m : au-delà, le moteur ne l'associe pas à la porte)
            (int n, Vector3 at)? b = buttons.OrderBy(x => Vector2.Distance(new(x.at.X, x.at.Y), new(at.X, at.Y))).Cast<(int, Vector3)?>().FirstOrDefault();
            bool hasButton = b is { } bb && Vector2.Distance(new(bb.at.X, bb.at.Y), new(at.X, at.Y)) < 1200;
            string name = hasButton ? $"GPorte{b!.Value.n}" : $"GPorte_{k}";
            k++;
            LevelObject d = LevelEdit.Clone(door, LevelEdit.NewId(level), name, cls);
            d = LevelEdit.WithTransform(d, at, new Vector3(0, 0, yaw));
            d = LevelEdit.WithStartVisible(d, true);
            d = WithInitialState(d, open.Contains(name, StringComparer.OrdinalIgnoreCase) ? 1 : 0);
            level.Objects.Add(d);
            if (hasButton)
            {
                LevelObject s = LevelEdit.Clone(symbol, LevelEdit.NewId(level), $"Ent_Bouton_GP{b!.Value.n}", symbol.ClassPath);
                s = LevelEdit.WithTransform(s, new Vector3(b.Value.at.X, b.Value.at.Y, b.Value.at.Z), Vector3.Zero);
                s = LevelEdit.WithStartVisible(s, true);
                level.Objects.Add(s);
            }
        }
        Report.Add($"grandes portes : {_bigDoors.Count} ({string.Join(", ", open)} ouverte(s) au départ), boutons : {buttons.Count}");
    }
}
