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
    private static bool IsBreakableDoor(Fd1Object o, IEnumerable<float> floors) => o.Model.StartsWith("sta_porte_", StringComparison.OrdinalIgnoreCase)
        && (o.World.M43 * WorldToCm < 50 || floors.Any(z => MathF.Abs(o.World.M43 * WorldToCm - z) < 50));

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
    private string WriteDoorModel(string model, List<Fd3Mesh> meshes, bool rises, bool usable = true)
    {
        (string safe, List<(string node, Matrix4x4 world)> nodes, float top) = WritePlaceableModel(model, meshes);
        string classDir = Path.Combine(Data, "Design", "placeable", "functional", "opening");
        Directory.CreateDirectory(classDir);
        if (!rises)
        {
            // porte cassable : ni ouverte ni animée ; cassée, elle disparaît (comme dans FD1)
            string plain = $"""
                [#INCLUDE]
                FILE0 = "base_door.class"

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
        (float t, Vector3 p)[] keys = { (0f, Vector3.Zero), (DoorOpenSeconds, new Vector3(0, 0, MathF.Max(100, top - 30))) };
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
                classes[o.Model] = cls = WriteDoorModel(o.Model, meshes, false);
            Vector3 at = new Vector3(o.World.M41, o.World.M42, o.World.M43) * WorldToCm;
            float yaw = MathF.Atan2(o.World.M12, o.World.M11);
            k++;
            // nom FD1 de la porte (« Porte4 » : les scripts FD1 s'en servent), sinon un numéro
            string? fd1Name = InstanceName(o);
            string name = fd1Name != null && System.Text.RegularExpressions.Regex.IsMatch(fd1Name, @"^Porte\d+$") ? fd1Name : $"porte_cassable_{k}";
            LevelObject d = LevelEdit.Clone(door, LevelEdit.NewId(level), name, cls);
            d = LevelEdit.WithTransform(d, at, new Vector3(0, 0, yaw));
            d = LevelEdit.WithStartVisible(d, true);
            d = WithInitialState(d, 0);
            level.Objects.Add(d);
        }
        Report.Add($"portes cassables : {_breakableDoors.Count}");
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
