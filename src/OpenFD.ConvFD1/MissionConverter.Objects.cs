using System.Numerics;
using System.Text;
using OpenFD.Formats;

namespace OpenFD.ConvFD1;

/// <summary>
/// Objets FD1 que le script suit : posés dans le niveau du moteur (et non fondus dans le décor) pour être cachés, montrés ou
/// suivis quand le feu les détruit (composant A024 : fonction du script appelée à leur destruction).
///  • étagères qui tombent (Entrepot « Et04 »… : debout ; « Et04Bis » : couchée, cachée au départ) : détruite par le feu,
///    l'étagère debout disparaît et la couchée paraît (FD1 : etagereCheckStatus) ; le moteur ferme ou rouvre leurs cases
///    selon leur visibilité ;
///  • machines (« Mach01 »… « Mach16 ») : détruites, le script les compte (défaite si toutes brûlent) ;
///  • objets qui explosent quand le feu les détruit (« Truckabc », le camion du parking ; Villa : les vieux bidons, les
///    jerricanes de la remise) ;
///  • voiture à conduire (Villa : l'Excalibur) : un ingénieur la prend (il la « répare »), le script la remplace par un
///    véhicule ; détruite par le feu avant, elle appelle le script comme un objet suivi.
/// </summary>
public sealed partial class MissionConverter
{
    private enum ObjectRole { Standing, Fallen, Watched, Explosive, Drivable, Hidden }

    /// <summary>
    /// Objets cachés au départ que le script montre, par mission (nom FD1). Foret : la voiture accidentée contre le rocher
    /// (FD1 : AccidentVoitureBis), l'épave de l'hélicoptère qui s'écrase (Epave_Helicoptere3Bis).
    /// </summary>
    private static readonly Dictionary<string, string[]> HiddenObjects = new(StringComparer.OrdinalIgnoreCase)
    {
        { "foret", new[] { "Voiture_Accident_Rocher", "Epave_Helicoptere3" } },
    };

    /// <summary>Sensibilité au feu des étagères debout (1 : elles tombent 25 s après que le feu les a prises).</summary>
    private const float ShelfFireCoef = 0.25f;

    /// <summary>Étagères qui tombent, par mission : noms FD1 des étagères debout (la couchée : même nom + « Bis »).</summary>
    private static readonly Dictionary<string, string[]> FallingShelves = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Entrepot", new[] { "Et04", "Et07", "Et10", "Et16", "Et22", "Et26" } },
    };

    /// <summary>Objets suivis, par mission : préfixe du nom FD1, fonction du script appelée à leur destruction.</summary>
    private static readonly Dictionary<string, (string prefix, string script)[]> WatchedObjects = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Entrepot", new[] { ("Mach", "MachDetruite") } },
        { "Villa", new[] { ("VoitureDeLuxe", "VoitureDetruite") } },
    };

    /// <summary>Objets qui explosent, par mission : nom FD1, fonction du script appelée à leur explosion.</summary>
    private static readonly Dictionary<string, (string name, string script)[]> ExplosiveObjects = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Entrepot", new[] { ("Truckabc", "FD1_Rien") } },
        // Villa : les vieux bidons (FD1 : BarilsExplo), les jerricanes de la remise (le script les fait sauter : FD1 Explode)
        { "Villa", new[] { ("Barils", "BarilsExplo"), ("JerricanExplose", "FD1_Rien") } },
        // Quartier : la charge du backdraft (le script la fait sauter), la voiture accidentée de la rue, les huit fenêtres du
        // deuxième étage
        {
            "quartier", new[]
            {
                ("explosif1", "FD1_Rien"), ("ExploVoiture", "VoitureExplosee"),
                ("Fenetre1", "FenetreExplosee"), ("Fenetre2", "FenetreExplosee"), ("Fenetre3", "FenetreExplosee"), ("Fenetre4", "FenetreExplosee"),
                ("Fenetre5", "FenetreExplosee"), ("Fenetre6", "FenetreExplosee"), ("Fenetre7", "FenetreExplosee"), ("Fenetre8", "FenetreExplosee"),
            }
        },
        // Station-service : les charges des backdrafts du chantier et de l'immeuble, la voiture qui embrase l'immeuble (acte
        // 4) : le script les fait sauter ; la citerne de butane de la supérette, qui explose quand le feu la détruit
        {
            "station_service", new[]
            {
                ("Back1", "FD1_Rien"), ("Back2", "FD1_Rien"), ("Back3", "FD1_Rien"), ("Back4", "FD1_Rien"), ("VoitActe4", "FD1_Rien"),
                ("Bombonne", "BombonneExplosee"),
            }
        },
        // Train : la locomotive de tête, le wagon de gaz (sa destruction perd la mission), les trois silos (la pollution
        // augmente), les voitures et le camion du village, la voiture qui bloque l'entrée du tunnel (acte 3, par le script)
        {
            "train", new[]
            {
                ("Loco1", "LocoExplosee"), ("MiniConvoi", "MiniConvoiExplose"), ("Silo1", "SiloExplose"), ("Silo2", "SiloExplose"),
                ("Silo3", "SiloExplose"), ("Voiture1", "FD1_Rien"), ("Voiture2", "FD1_Rien"), ("Voiture3", "FD1_Rien"), ("Camion1", "FD1_Rien"),
                ("VoitureExplo", "FD1_Rien"),
            }
        },
    };

    /// <summary>
    /// Sensibilité au feu de certains objets du script (1 : détruits 25 s après que le feu les a pris), par mission. Villa :
    /// les vieux bidons tiennent trois fois plus (FD1 : 300 points de vie, alerte à 200).
    /// </summary>
    private static readonly Dictionary<string, (string name, float coef)[]> ObjectFireCoefs = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Villa", new[] { ("Barils", 0.33f) } },
        // Station-service : la citerne de butane tient trois fois plus ; le camion-citerne de GPL, en feu à l'arrivée des
        // pompiers, dix fois plus ; les charges des backdrafts et la voiture de l'acte 4 ne sautent que par le script
        {
            "station_service", new[]
            {
                ("Bombonne", 0.33f), ("Citerne", 0.1f), ("Back1", 0f), ("Back2", 0f), ("Back3", 0f), ("Back4", 0f), ("VoitActe4", 0f),
            }
        },
        // Train : la locomotive et les silos tiennent trois fois plus, le wagon de gaz quatre fois plus ; la voiture de
        // l'entrée du tunnel ne saute que par le script
        {
            "train", new[]
            {
                ("Loco1", 0.33f), ("MiniConvoi", 0.25f), ("Silo1", 0.33f), ("Silo2", 0.33f), ("Silo3", 0.33f), ("VoitureExplo", 0f),
            }
        },
        // Foret : les épaves de la voiture et de l'hélicoptère restent là, au milieu de leurs feux
        { "foret", new[] { ("Voiture_Accident_Rocher", 0f), ("Epave_Helicoptere3", 0f) } },
    };

    /// <summary>Voitures à conduire, par mission : nom FD1, fonction du script appelée quand un ingénieur la prend.</summary>
    private static readonly Dictionary<string, (string name, string script)[]> DrivableObjects = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Villa", new[] { ("VoitureDeLuxe1", "Use_Excalibur") } },
        // Station-service : le camion-citerne de GPL, à conduire jusqu'à l'unité d'arrosage à mousse
        { "station_service", new[] { ("Citerne", "Use_Citerne") } },
    };

    /// <summary>Objets du script trouvés dans le niveau FD1 (objet, maillages, nom, rôle, fonction à la destruction).</summary>
    private readonly List<(Fd1Object o, List<Fd3Mesh> meshes, string name, ObjectRole role, string script)> _scriptObjects = new();

    /// <summary>Rôle de l'objet FD1 de ce nom dans le script (null : du décor).</summary>
    private (ObjectRole role, string script)? ScriptObjectRole(string? name)
    {
        if (name == null)
            return null;
        if (FallingShelves.TryGetValue(Mission, out string[]? shelves))
            foreach (string s in shelves)
            {
                if (name.Equals(s, StringComparison.OrdinalIgnoreCase))
                    return (ObjectRole.Standing, "Tombe_" + s);
                if (name.Equals(s + "Bis", StringComparison.OrdinalIgnoreCase))
                    return (ObjectRole.Fallen, "FD1_Rien");
            }
        if (HiddenObjects.TryGetValue(Mission, out string[]? hidden) && hidden.Contains(name, StringComparer.OrdinalIgnoreCase))
            return (ObjectRole.Hidden, "FD1_Rien");
        if (DrivableObjects.TryGetValue(Mission, out (string name, string script)[]? drivable) && drivable.Any(d => d.name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return (ObjectRole.Drivable, WatchedScript(name) ?? "FD1_Rien");
        if (WatchedObjects.TryGetValue(Mission, out (string prefix, string script)[]? watched))
            foreach ((string prefix, string script) in watched)
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length > prefix.Length && name[prefix.Length..].All(char.IsDigit))
                    return (ObjectRole.Watched, script);
        if (ExplosiveObjects.TryGetValue(Mission, out (string name, string script)[]? explosive))
            foreach ((string n, string script) in explosive)
                if (n.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return (ObjectRole.Explosive, script);
        return null;
    }

    /// <summary>Fonction du script appelée à la destruction de cet objet suivi (null : il n'est pas suivi).</summary>
    private string? WatchedScript(string name)
    {
        if (!WatchedObjects.TryGetValue(Mission, out (string prefix, string script)[]? watched))
            return null;
        foreach ((string prefix, string script) in watched)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length > prefix.Length && name[prefix.Length..].All(char.IsDigit))
                return script;
        return null;
    }

    /// <summary>
    /// Classe d'une voiture à conduire : Design\placeable\functional\useable\&lt;nom&gt;_&lt;modèle&gt;_conduite.class : elle
    /// s'abîme au feu (100 points de vie), seul un ingénieur s'en sert (« réparer »).
    /// </summary>
    private string WriteDrivableClass(string model, List<Fd3Mesh> meshes)
    {
        (string safe, List<(string node, Matrix4x4 world)> _, float _) = WritePlaceableModel(model, meshes);
        string classDir = Path.Combine(Data, "Design", "placeable", "functional", "useable");
        Directory.CreateDirectory(classDir);
        string name = safe + "_conduite";
        string cls = $"""
            [#INCLUDE]
            FILE0 = "base_useable.class"

            [DISPLAY]
            MODEL = {Name}\{safe}

            [DAMAGE]
            USEDAMAGE = 1
            CARBONISABLE = 0
            FIREDAMAGECOEF = 1
            LIFEPOINTS = 100
            MAXLIFEPOINTS = 100

            [CAPACITIES_ACTIONS]
            COULDBEUSE = 0
            COULDBEREPAIR = 1

            [EXPLOSION]
            CANEXPLODE = 0

            [LOGICAL_STATE]
            S01_USER_ANIM = ItemChar\ic_pick_up_down
            S01__NAME = use

            """;
        File.WriteAllBytes(Path.Combine(classDir, name + ".class"), Encoding.Latin1.GetBytes(cls.Replace("\n", "\r\n")));
        return "design\\placeable\\functional\\useable\\" + name;
    }

    /// <summary>
    /// Classe d'un objet du script : Design\placeable\functional\useable\&lt;nom&gt;_&lt;modèle&gt;_feu (ou _explo).class :
    /// il s'abîme au feu (100 points de vie), n'est pas utilisable ; explosif : un gros souffle (camion).
    /// </summary>
    private string WriteObjectClass(string model, List<Fd3Mesh> meshes, bool explosive)
    {
        (string safe, List<(string node, Matrix4x4 world)> _, float _) = WritePlaceableModel(model, meshes);
        string classDir = Path.Combine(Data, "Design", "placeable", "functional", "useable");
        Directory.CreateDirectory(classDir);
        string name = safe + (explosive ? "_explo" : "_feu");
        string explosion = explosive
            ? """
              [EXPLOSION]
              CANEXPLODE = 1
              LP_EXPLODE_POWER = 100
              LP_EXPLODE_RADIUS = 1000
              HP_EXPLODE_POWER = 100
              HP_EXPLODE_RADIUS = 1500
              STAMINA_EXPLODE_POWER = 100
              STAMINA_EXPLODE_RADIUS = 1500

              [SOUND_EXPLOSION]
              LABEL = EXPLOSION_GAS_TANK_01_01
              RANGE_MAX = 8000
              RANGE_MIN = 2000
              """
            : """
              [EXPLOSION]
              CANEXPLODE = 0
              """;
        string cls = $"""
            [#INCLUDE]
            FILE0 = "base_useable.class"

            [DISPLAY]
            MODEL = {Name}\{safe}

            [DAMAGE]
            USEDAMAGE = 1
            CARBONISABLE = 0
            FIREDAMAGECOEF = 1
            LIFEPOINTS = 100
            MAXLIFEPOINTS = 100

            [CAPACITIES_ACTIONS]
            COULDBEUSE = 0

            {explosion}

            """;
        File.WriteAllBytes(Path.Combine(classDir, name + ".class"), Encoding.Latin1.GetBytes(cls.Replace("\n", "\r\n")));
        return "design\\placeable\\functional\\useable\\" + name;
    }

    /// <summary>
    /// Un objet du jeu qui appelle une fonction du script à sa destruction (composant A024) et a une explosion (A029), sans
    /// action (Vegas : les voitures du parking, « On_parking_vehicle_death »).
    /// </summary>
    private LevelObject? DestroyTemplate() =>
        TemplateWhere(o => o.Kind == EntityKind.Placeable && o.GetComponent(0xA024) is { } d && ActionScripts(o.ComponentBytes(d).ToArray()).Count > 0
            && o.GetComponent(0xA029) != null && (o.GetComponent(0xA027) is not { } a || ActionScripts(o.ComponentBytes(a).ToArray()).Count == 0)
            && !o.ClassPath.EndsWith("_symbol", StringComparison.OrdinalIgnoreCase));

    /// <summary>Explose ou non (A029 : premier octet après la marque de fin) et vie pleine (A024).</summary>
    private static LevelObject WithExplosive(LevelObject o, bool explosive, float fireCoef = 1f)
    {
        if (o.GetComponent(0xA029) is { } ec)
        {
            byte[] e = o.ComponentBytes(ec).ToArray();
            for (int i = 0; i + 8 <= e.Length; i++)
                if (Chunks.IsEnd(e, i) && i + 4 + 25 <= e.Length)
                {
                    e[i + 4] = (byte)(explosive ? 1 : 0);
                    if (explosive)
                    {
                        float[] values = { 100, 1000, 100, 1500, 100, 1500 };
                        for (int v = 0; v < values.Length; v++)
                            BitConverter.GetBytes(values[v]).CopyTo(e, i + 5 + 4 * v);
                    }
                    o = LevelEdit.WithComponent(o, ec, e);
                    break;
                }
        }
        if (o.GetComponent(0xA024) is { } dc)
        {
            byte[] d = o.ComponentBytes(dc).ToArray();
            if (d.Length >= 21 && d[4] == 0x0C)
            {
                BitConverter.GetBytes(100f).CopyTo(d, 5);
                BitConverter.GetBytes(100f).CopyTo(d, 9);
                BitConverter.GetBytes(fireCoef).CopyTo(d, 17);
                o = LevelEdit.WithComponent(o, dc, d);
            }
        }
        return o;
    }

    /// <summary>Objets du script dans le niveau du moteur (étagères couchées cachées au départ).</summary>
    private void AddScriptObjects(Level level)
    {
        if (_scriptObjects.Count == 0)
            return;
        LevelObject? template = DestroyTemplate();
        if (template == null)
        {
            Report.Add("objets du script : objet modèle introuvable dans les missions du jeu");
            return;
        }
        Dictionary<(string model, bool explosive), string> classes = new Dictionary<(string, bool), string>();
        foreach ((Fd1Object o, List<Fd3Mesh> meshes, string name, ObjectRole role, string script) in _scriptObjects)
        {
            if (role == ObjectRole.Drivable)
            {
                AddDrivable(level, o, meshes, name, script);
                continue;
            }
            bool explosive = role == ObjectRole.Explosive;
            Matrix4x4 w = o.World;
            w.M41 *= WorldToCm;
            w.M42 *= WorldToCm;
            w.M43 *= WorldToCm;
            Vector3 euler = EulerOf(w);
            string model = o.Model;
            List<Fd3Mesh> parts = meshes;
            if (role == ObjectRole.Fallen)
            {
                // étagère couchée : sa rotation (penchée) est mise dans son propre modèle, posé droit ; le moteur relève la
                // place d'un objet dans le repère de son modèle, à hauteur d'homme : celle d'une étagère couchée doit y être
                Matrix4x4 rot = w;
                rot.M41 = rot.M42 = rot.M43 = 0;
                parts = meshes.Select(m => Rotated(m, rot)).ToList();
                model = o.Model + "_" + name;
                euler = Vector3.Zero;
            }
            if (!classes.TryGetValue((model, explosive), out string? cls))
                classes[(model, explosive)] = cls = WriteObjectClass(model, parts, explosive);
            LevelObject x = LevelEdit.Clone(template, LevelEdit.NewId(level), name, cls);
            x = LevelEdit.WithTransform(x, new Vector3(w.M41, w.M42, w.M43), euler);
            x = LevelEdit.WithStartVisible(x, role is not (ObjectRole.Fallen or ObjectRole.Hidden));
            x = WithComponentScript(x, 0xA024, script);
            // étagère debout : elle tient un moment dans le feu avant de tomber (sinon elle tombe en 25 s)
            float coef = role == ObjectRole.Standing ? ShelfFireCoef : 1f;
            if (ObjectFireCoefs.TryGetValue(Mission, out (string name, float coef)[]? coefs) && coefs.FirstOrDefault(c => c.name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { name: not null } fc)
                coef = fc.coef;
            x = WithExplosive(x, explosive, coef);
            level.Objects.Add(x);
        }
        Report.Add($"objets du script : {_scriptObjects.Count(s => s.role == ObjectRole.Standing)} étagères qui tombent, "
            + $"{_scriptObjects.Count(s => s.role == ObjectRole.Watched)} objets suivis, {_scriptObjects.Count(s => s.role == ObjectRole.Explosive)} explosif(s)");
    }

    /// <summary>
    /// Voiture à conduire dans le niveau du moteur : un objet réparable (modèle : la bombe de Sawmill, qui a une action et
    /// une fonction à sa destruction) ; son action appelle la fonction de DrivableObjects, sa destruction celle des objets
    /// suivis.
    /// </summary>
    private void AddDrivable(Level level, Fd1Object o, List<Fd3Mesh> meshes, string name, string destroyed)
    {
        LevelObject? template = ActionTemplate();
        string? use = DrivableObjects.TryGetValue(Mission, out (string name, string script)[]? drivable)
            ? drivable.FirstOrDefault(d => d.name.Equals(name, StringComparison.OrdinalIgnoreCase)).script : null;
        if (template == null || use == null)
        {
            Report.Add($"voiture à conduire {name} : objet utilisable modèle introuvable dans les missions du jeu");
            return;
        }
        Matrix4x4 w = o.World;
        w.M41 *= WorldToCm;
        w.M42 *= WorldToCm;
        w.M43 *= WorldToCm;
        LevelObject x = LevelEdit.Clone(template, LevelEdit.NewId(level), name, WriteDrivableClass(o.Model, meshes));
        x = LevelEdit.WithTransform(x, new Vector3(w.M41, w.M42, w.M43), EulerOf(w));
        x = LevelEdit.WithStartVisible(x, true);
        x = WithActionScript(x, use);
        x = WithComponentScript(x, 0xA024, destroyed);
        float coef = 1f;
        if (ObjectFireCoefs.TryGetValue(Mission, out (string name, float coef)[]? coefs) && coefs.FirstOrDefault(c => c.name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { name: not null } fc)
            coef = fc.coef;
        x = WithExplosive(x, false, coef);
        level.Objects.Add(x);
        Report.Add($"voiture à conduire : {name} ({use})");
    }

    /// <summary>Copie d'un maillage tourné (sommets et normales).</summary>
    private static Fd3Mesh Rotated(Fd3Mesh m, Matrix4x4 rot)
    {
        Fd3Mesh r = new Fd3Mesh { Name = m.Name, Uv = m.Uv, Indices = m.Indices, Groups = m.Groups };
        for (int i = 0; i + 2 < m.Positions.Count; i += 3)
        {
            Vector3 p = Vector3.Transform(new Vector3(m.Positions[i], m.Positions[i + 1], m.Positions[i + 2]), rot);
            r.Positions.Add(p.X);
            r.Positions.Add(p.Y);
            r.Positions.Add(p.Z);
        }
        for (int i = 0; i + 2 < m.Normals.Count; i += 3)
        {
            Vector3 n = Vector3.Normalize(Vector3.TransformNormal(new Vector3(m.Normals[i], m.Normals[i + 1], m.Normals[i + 2]), rot));
            r.Normals.Add(n.X);
            r.Normals.Add(n.Y);
            r.Normals.Add(n.Z);
        }
        return r;
    }

    /// <summary>
    /// Angles d'Euler (X, Y, Z) d'une matrice de rotation, dans l'ordre de LevelEdit.MatrixOf ; on vérifie en recomposant
    /// la matrice et on garde la décomposition qui la redonne.
    /// </summary>
    private static Vector3 EulerOf(Matrix4x4 w)
    {
        Matrix4x4 r = w;
        r.M41 = r.M42 = r.M43 = 0;
        Vector3 best = new Vector3(0, 0, MathF.Atan2(w.M12, w.M11));
        float bestErr = float.MaxValue;
        // candidats : les deux conventions courantes (Z·Y·X et X·Y·Z), essayées contre LevelEdit.MatrixOf
        foreach (Vector3 c in new[]
        {
            new Vector3(MathF.Atan2(r.M23, r.M33), MathF.Asin(Math.Clamp(-r.M13, -1, 1)), MathF.Atan2(r.M12, r.M11)),
            new Vector3(MathF.Atan2(-r.M32, r.M33), MathF.Asin(Math.Clamp(r.M31, -1, 1)), MathF.Atan2(-r.M21, r.M11)),
        })
        {
            Matrix4x4 m = LevelEdit.MatrixOf(c, Vector3.Zero);
            float err = 0;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    err += MathF.Abs(m[i, j] - r[i, j]);
            if (err < bestErr)
            {
                bestErr = err;
                best = c;
            }
        }
        return best;
    }
}
