using System.Numerics;
using System.Text;
using OpenFD.Formats;

namespace OpenFD.ConvFD1;

/// <summary>
/// Déroulement de la mission (traduction des scripts FD1) : civils, zones de déclenchement et script du moteur.
/// Entrepot (scripts Entrepot_Main / _Scenar / _Event / _Trigger) :
///  • acte 1 : sauver les deux gardiens (évanouis ; Garde1 paraît quand on entre dans la zone « TCacheToit2 », Garde2
///    quand la porte « Porte3 » est cassée), éteindre le premier hall ; la bombe 3 s'amorce quand ce feu est presque
///    éteint, la bombe 2 50 s après la découverte de Garde1 ;
///  • acte 2 (quand l'acte 1 est fini) : le feu prend dans l'atelier (deuxième hall), les bombes 0 et 4 s'amorcent après
///    2 et 4 minutes, la bombe 1 quand l'atelier est presque éteint ; sauver la secrétaire (évanouie, à l'étage, derrière
///    « Porte4 »), éteindre l'atelier ; un feu prend à l'étage la première fois qu'on y monte ;
///  • indices (facultatifs) : la vidéo du local de sécurité, les documents du bureau, une bombe désamorcée ;
///  • victoire quand l'acte 2 est fini (texte selon les indices), défaites : ambulance détruite, gardien ou secrétaire
///    mort (le moteur ne fait pas encore mourir les civils : prêt pour plus tard).
/// Une bombe amorcée explose 45 s plus tard (3 s pour la bombe 2), sauf si le technicien la désamorce avant.
/// </summary>
public sealed partial class MissionConverter
{
    /// <summary>Délai du premier renfort (s), une fois appelé ; 0 : pas de renfort.</summary>
    private float _reinforcementEta;

    /// <summary>Niveau FD1 en cours de conversion.</summary>
    private Fd1Level? _fd1;

    /// <summary>Civils, par mission : nom FD1 du personnage, fin de la classe du moteur (civil évanoui de ce modèle).</summary>
    private static readonly Dictionary<string, (string name, string cls)[]> StoryCivilians = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Entrepot", new[] { ("Garde1", "civilian\\civilian_cop_1"), ("Garde2", "civilian\\civilian_cop_1"), ("Secretaire", "civilian\\civilian_woman1") } },
        {
            // Banque : la cliente et les gardes du hall, les employés des bureaux, les victimes de l'étage, le garde et les
            // trois faux pompiers de la salle des coffres
            "banque", new[]
            {
                ("Client1", "civilian\\civilian_woman2"), ("Garde1", "civilian\\civilian_cop_1"), ("Garde2", "civilian\\civilian_cop_2"),
                ("Employe1", "civilian\\civilian_man2"), ("Employe2", "civilian\\civilian_man3"),
                ("Banquier", "civilian\\civilian_man1"), ("GrosClient", "civilian\\civilian_man4"), ("Comptable", "civilian\\civilian_man5"),
                ("Informaticien", "civilian\\civilian_man6"),
                ("Garde3", "civilian\\civilian_cop_3"), ("Bandit1", "civilian\\civilian_gangster_1"), ("Bandit2", "civilian\\civilian_gangster_2"),
                ("Bandit3", "civilian\\civilian_gangster_3"),
            }
        },
        {
            // Villa : les joueurs de tennis (Steve Roger, le milliardaire, et son ami), les invités de la villa, Debby Love et
            // le jardinier de la serre
            "Villa", new[]
            {
                ("Milliardaire", "civilian\\civilian_man1"), ("Tennisman", "civilian\\civilian_man2"),
                ("Productrice", "civilian\\civilian_woman1"), ("Journaliste", "civilian\\civilian_woman2"), ("Cameraman", "civilian\\civilian_man3"),
                ("PopStar", "civilian\\civilian_star"), ("Jardinier", "civilian\\civilian_worker1"),
            }
        },
        {
            // Quartier : les quatre blessés de la rue, les victimes de l'étage sud, l'ambassadeur (Civil9) et ses invités
            "quartier", new[]
            {
                ("Civil0", "civilian\\civilian_man4"), ("Civil1", "civilian\\civilian_man5"), ("Civil2", "civilian\\civilian_woman3"), ("Civil3", "civilian\\civilian_woman4"), ("Civil4", "civilian\\civilian_woman1"), ("Civil5", "civilian\\civilian_man6"), ("Civil6", "civilian\\civilian_man7"), ("Civil7", "civilian\\civilian_man8"), ("Civil8", "civilian\\civilian_woman2"), ("Civil9", "civilian\\civilian_man1"), ("Civil10", "civilian\\civilian_man2"), ("Civil11", "civilian\\civilian_man3"),
            }
        },
        {
            // Station-service : le policier, le chauffeur du camion-citerne et l'otage (acte 1) ; la caissière, le patron et le
            // client de la supérette (acte 2) ; les ouvriers du chantier (acte 3) ; les habitants de l'immeuble, « Poligote » et
            // Johnston, le truand (acte 4) ; les deux policiers soufflés par la citerne de butane
            "station_service", new[]
            {
                ("Policier", "civilian\\civilian_cop_1"), ("Citernedriver", "civilian\\civilian_worker2"), ("Otage1", "civilian\\civilian_man9"),
                ("Caissiere", "civilian\\civilian_waitress"), ("Patron", "civilian\\civilian_man4"), ("Client", "civilian\\civilian_man5"),
                ("Ouvrier0", "civilian\\civilian_worker1"), ("Ouvrier1", "civilian\\civilian_worker2"), ("Ouvrier3", "civilian\\civilian_worker1"),
                ("Hab1", "civilian\\civilian_woman1"), ("Hab2", "civilian\\civilian_man6"), ("Hab3", "civilian\\civilian_man7"),
                ("Hab4", "civilian\\civilian_woman2"), ("Poligote", "civilian\\civilian_man8"), ("Voleur", "civilian\\civilian_gangster_1"),
                ("PolicierEvanoui", "civilian\\civilian_cop_2"), ("PolicierEvanoui2", "civilian\\civilian_cop_3"),
            }
        },
        {
            // Train : le conducteur du tracteur et le machiniste de la locomotive (acte 1), le palefrenier (acte 2), les deux
            // techniciens du tunnel (acte 3)
            "train", new[]
            {
                ("Conducteur1", "civilian\\civilian_worker1"), ("Conducteur2", "civilian\\civilian_man4"), ("Palefrenier", "civilian\\civilian_worker2"),
                ("Conducteur3", "civilian\\civilian_man5"), ("Conducteur4", "civilian\\civilian_man6"),
            }
        },
        {
            // Ecole : l'ouvrier du chantier (acte 1), les deux employées du bâtiment administratif (acte 2), les trois
            // professeurs de l'école (acte 3)
            "ecole", new[]
            {
                ("ouvrier0", "civilian\\civilian_worker1"), ("civil0", "civilian\\civilian_woman1"), ("civil1", "civilian\\civilian_woman2"),
                ("civil10", "civilian\\civilian_man1"), ("civil11", "civilian\\civilian_man2"), ("civil12", "civilian\\civilian_man3"),
            }
        },
        {
            // Foret (MissionConverter.Foret.cs) : le bûcheron (acte 1), le couple de l'accident de voiture (acte 2), les six
            // scouts du camp (acte 3, conscients : ils suivent les pompiers jusqu'au refuge)
            "foret", new[]
            {
                ("Bucheron", "civilian\\civilian_worker2"), ("Homme_Accident", "civilian\\civilian_man4"), ("Femme_Accident", "civilian\\civilian_woman3"),
                ("Scout0", "civilian\\civilian_man5"), ("Scout1", "civilian\\civilian_man6"), ("Scout2", "civilian\\civilian_woman4"),
                ("Scout3", "civilian\\civilian_man7"), ("Scout4", "civilian\\civilian_woman5"), ("Scout5", "civilian\\civilian_man8"),
            }
        },
    };

    /// <summary>Civils du déroulement qui sont conscients (ils suivent un pompier), par mission ; les autres sont évanouis.</summary>
    private static readonly Dictionary<string, string[]> ConsciousCivilians = new(StringComparer.OrdinalIgnoreCase)
    {
        { "foret", new[] { "Scout0", "Scout1", "Scout2", "Scout3", "Scout4", "Scout5" } },
    };

    /// <summary>
    /// Zones de déclenchement, par mission : nom, fonction du script, coin bas, coin haut (cm). Entrepot : les deux zones FD1
    /// qui découvrent Garde1 (« TCacheToit2 », « TCacheToit3 » → TCacheToit2), l'étage du bureau (premier feu à l'étage),
    /// l'atelier et l'abord de la porte blindée (le feu de l'atelier prend).
    /// </summary>
    private static readonly Dictionary<string, (string name, string script, Vector3 min, Vector3 max)[]> StoryTriggers = new(StringComparer.OrdinalIgnoreCase)
    {
        {
            "Entrepot", new[]
            {
                ("TCacheToit2", "TCacheToit2", new Vector3(13923, 7086, 0), new Vector3(14431, 9610, 300)),
                ("TCacheToit3", "TCacheToit2", new Vector3(6939, 6753, 0), new Vector3(7331, 7617, 300)),
                ("EtageBureau", "EtageBureau", new Vector3(7100, 16650, 1400), new Vector3(8740, 19560, 1700)),
                // le feu de l'atelier prend dès qu'une unité approche de la porte blindée (20 m devant elle, dans le premier
                // hall) ou entre dans l'atelier, par la porte blindée ou par les portes du quai
                ("AvantAtelier", "Zone_FeuAtelier", new Vector3(11100, 11700, 0), new Vector3(13100, 13690, 300)),
                ("Atelier", "Zone_FeuAtelier", new Vector3(8800, 13700, 0), new Vector3(14000, 19600, 300)),
                // flaque électrique (zones FD1 « Flaque » et « FlaqueCut ») : on s'y électrocute tant que le courant n'est
                // pas coupé à l'armoire ; la première fois qu'on approche, l'avertissement
                ("Flaque", "Flaque_Entree", new Vector3(9415, 18691, 0), new Vector3(9953, 19448, 300)),
                ("FlaqueCut", "FlaqueCut", new Vector3(9383, 18722, 0), new Vector3(10006, 19448, 300)),
            }
        },
        {
            // Banque : zones FD1 (Banque.xfl ; cercles → carrés de même rayon)
            "banque", new[]
            {
                ("T1", "T1", new Vector3(11277, 13440, 0), new Vector3(12877, 15040, 300)),
                ("T4", "T4", new Vector3(11710, 14570, 0), new Vector3(12521, 15934, 300)),
                ("T12", "T12", new Vector3(10800, 12800, 0), new Vector3(13300, 13000, 300)),
                ("T15", "T15", new Vector3(15113, 7307, 1400), new Vector3(17032, 7802, 1700)),
                ("T19", "T19", new Vector3(6092, 8856, 1400), new Vector3(10064, 9381, 1700)),
                ("T20", "T20", new Vector3(16237, 12697, 0), new Vector3(16837, 13297, 300)),
                ("T21", "T21", new Vector3(15122, 9591, 0), new Vector3(16222, 10691, 300)),
            }
        },
        {
            // Villa : zones FD1 (Villa.xfl) : le fond de la villa (cercle → carré), l'allée du tennis, la route
            "Villa", new[]
            {
                ("T2", "T2", new Vector3(12777, 18065, 0), new Vector3(13477, 18765, 300)),
                ("T9", "T9", new Vector3(14333, 5052, 0), new Vector3(14888, 13021, 300)),
                ("T10", "T10", new Vector3(0, 2852, 0), new Vector3(24111, 5073, 300)),
            }
        },
        {
            // Quartier : zones FD1 du troisième étage (quartier.xfl ; cercles → carrés de même rayon) : l'arrivée à l'étage
            // (T2, T10, T11, T12, T13, T18, T24), les victimes découvertes (T5 à T7), le couloir effondré, l'escalier en feu,
            // la pièce de la fuite de gaz
            "quartier", new[]
            {
                ("T2", "T2", new Vector3(6957, 8326, 1300), new Vector3(7657, 9026, 1800)),
                ("T10", "T10", new Vector3(5521, 12857, 1300), new Vector3(7208, 13991, 1800)),
                ("T11", "T11", new Vector3(7449, 7850, 1300), new Vector3(8149, 8550, 1800)),
                ("T12", "T12", new Vector3(8455, 7852, 1300), new Vector3(9155, 8552, 1800)),
                ("T13", "T13", new Vector3(9440, 7876, 1300), new Vector3(10140, 8576, 1800)),
                ("T18", "T18", new Vector3(5564, 7800, 1300), new Vector3(7164, 9400, 1800)),
                ("T24", "T24", new Vector3(12547, 7924, 1300), new Vector3(13010, 8433, 1800)),
                ("T5", "T5", new Vector3(10458, 10583, 1300), new Vector3(11158, 11283, 1800)),
                ("T6", "T6", new Vector3(8467, 10300, 1300), new Vector3(9167, 11000, 1800)),
                ("T7", "T7", new Vector3(6006, 10346, 1300), new Vector3(6706, 11046, 1800)),
                ("TriggerTrou", "TriggerTrou", new Vector3(7761, 9224, 1300), new Vector3(9229, 9644, 1800)),
                ("T23", "T23", new Vector3(7306, 11690, 1300), new Vector3(8271, 12100, 1800)),
                ("T21", "T21", new Vector3(5351, 11738, 1300), new Vector3(7277, 14147, 1800)),
            }
        },
        {
            // Station-service : zones FD1 (station_service.xfl ; cercles → carrés de même rayon) : l'unité d'arrosage à mousse
            // (« TriggerTruck »), les pièces du chantier (TCache3 à 5) et de l'immeuble (TCache11, 17, 19) qui découvrent une
            // victime ou la réserve de peinture
            "station_service", new[]
            {
                ("TriggerTruck", "TriggerTruck", new Vector3(16355, 16637, 0), new Vector3(17639, 17384, 500)),
                ("TCache3", "TCache3", new Vector3(17978, 11346, 0), new Vector3(18678, 12046, 300)),
                ("TCache4", "TCache4", new Vector3(17909, 12394, 0), new Vector3(18809, 13294, 300)),
                ("TCache5", "TCache5", new Vector3(19078, 13003, 0), new Vector3(19778, 13703, 300)),
                ("TCache11", "TCache11", new Vector3(6701, 14028, 1300), new Vector3(7201, 14528, 1800)),
                ("TCache17", "TCache17", new Vector3(3552, 12785, 1300), new Vector3(4252, 13485, 1800)),
                ("TCache19", "TCache19", new Vector3(3859, 14681, 1300), new Vector3(4459, 15281, 1800)),
            }
        },
        {
            // Ecole : zones FD1 (ecole.xfl ; cercles → carrés de même rayon) : l'armoire électrique du chantier, l'étage du
            // bâtiment administratif, les entrées des salles de classe de l'école, le plancher qui s'effondre
            "ecole", new[]
            {
                ("presElec", "presElec", new Vector3(19431, 12947, 0), new Vector3(21031, 14547, 300)),
                ("decache0", "decache0", new Vector3(12292, 7506, 1300), new Vector3(13192, 8406, 1800)),
                ("entrePiece12a", "entrePiece12", new Vector3(8313, 10753, 1300), new Vector3(8813, 11253, 1800)),
                ("entrePiece12b", "entrePiece12", new Vector3(9329, 10743, 1300), new Vector3(9829, 11243, 1800)),
                ("piece12Show", "entrePiece12", new Vector3(7949, 12616, 1300), new Vector3(8649, 13316, 1800)),
                ("entrePiece11a", "entrePiece11", new Vector3(11384, 10720, 1300), new Vector3(11884, 11220, 1800)),
                ("entrePiece11b", "entrePiece11", new Vector3(11729, 11635, 1300), new Vector3(12229, 12135, 1800)),
                ("entrePiece11c", "entrePiece11", new Vector3(11729, 12248, 1300), new Vector3(12229, 12748, 1800)),
                ("entrePiece13", "entrePiece13", new Vector3(11764, 14270, 1300), new Vector3(12264, 14770, 1800)),
                ("entrePiece15a", "entrePiece15", new Vector3(11861, 15117, 1300), new Vector3(12361, 15617, 1800)),
                ("entrePiece15b", "entrePiece15", new Vector3(11865, 16242, 1300), new Vector3(12365, 16742, 1800)),
                ("entrePiece15c", "entrePiece15", new Vector3(11395, 16680, 1300), new Vector3(11895, 17180, 1800)),
                ("entrePiece16", "entrePiece16", new Vector3(8281, 16594, 1300), new Vector3(8781, 17094, 1800)),
                ("launchEffondr", "launchEffondr", new Vector3(11628, 16465, 1300), new Vector3(12128, 16965, 1800)),
            }
        },
        {
            // Foret : zones FD1 (foret.xfl) : le nord-ouest de la carte (T0 : la cinématique de l'héliport du bûcheron), les
            // deux héliports (T1, T2 : unités qui gênent l'atterrissage)
            "foret", new[]
            {
                ("ZoneHeliport1", "ForetZone0", new Vector3(257, 203, -200), new Vector3(9360, 12009, 800)),
                ("Heliport1", "ForetEntre1", new Vector3(3480, 5150, -200), new Vector3(5030, 6500, 800)),
                ("Heliport2", "ForetEntre2", new Vector3(4863, 17510, -200), new Vector3(6524, 19133, 800)),
            }
        },
    };

    /// <summary>Cinématique FD1 jouée quand on utilise ce bouton du script (Entrepot : la porte blindée, l'échelle 2).</summary>
    private static readonly Dictionary<string, string> ButtonCinematics = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Ent_Bouton_Echelle1", "FENOUVRE" },
        { "Ent_Bouton_Echelle2", "CUTECHELLE2" },
    };

    /// <summary>Fonction appelée quand une unité sort d'une zone de déclenchement (par nom de zone).</summary>
    private static readonly Dictionary<string, string> StoryTriggerLeave = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Flaque", "Flaque_Sortie" },
        { "Heliport1", "ForetSort1" },
        { "Heliport2", "ForetSort2" },
    };

    /// <summary>Nom FD1 de l'objet (dernier texte de son enregistrement avant le script) ; null : sans nom.</summary>
    private string? InstanceName(Fd1Object o)
    {
        if (_fd1 == null)
            return null;
        // après le nom de la classe (« Mechanisme », « ObjetDecor »…) : le nom de l'objet, puis celui de l'instance (celui des
        // scripts : « CoffreMec1 », « bob3 », « PorteCoupe0 ») ; ce dernier peut manquer
        List<string> texts = _fd1.Strings.Where(s => s.offset > o.Offset && s.offset < o.Offset + 400).Select(s => s.text).ToList();
        int cls = texts.FindIndex(t => t.Equals(o.Class, StringComparison.OrdinalIgnoreCase));
        string? Name(int i) => i < texts.Count && texts[i] != "NULL" && !texts[i].EndsWith(".SCP", StringComparison.OrdinalIgnoreCase)
            && !texts[i].EndsWith(".FLX", StringComparison.OrdinalIgnoreCase) ? texts[i] : null;
        if (cls >= 0)
            return Name(cls + 2) ?? Name(cls + 1);
        List<string> near = _fd1.Strings.Where(s => s.offset > o.Offset + 40 && s.offset < o.Offset + 140).Select(s => s.text)
            .Where(t => t != "NULL").ToList();
        int script = near.FindIndex(t => t.EndsWith(".SCP", StringComparison.OrdinalIgnoreCase));
        if (script >= 0)
            near = near.Take(script).ToList();
        return near.Count > 0 ? near[^1] : null;
    }

    /// <summary>Un objet du jeu de la sorte voulue, pris dans les missions du jeu.</summary>
    private LevelObject? TemplateWhere(Func<LevelObject, bool> ok)
    {
        foreach (string lv in Fd3.List("Design\\Mission\\").Where(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Level l = Level.Parse(Fd3.Read(lv));
                LevelObject? o = l.Objects.FirstOrDefault(x => x.HasTransform && ok(x));
                if (o != null)
                    return o;
            }
            catch (Exception)
            {
            }
        }
        return null;
    }

    /// <summary>Civils (cachés au départ : le script les montre) et zones de déclenchement de la mission.</summary>
    private void AddStory(Level level, Fd1Level fd1)
    {
        if (StoryCivilians.TryGetValue(Mission, out (string name, string cls)[]? civilians))
        {
            int n = 0;
            foreach ((string name, string cls) in civilians)
            {
                Fd1Object? actor = fd1.Objects.FirstOrDefault(o => o.Class.Equals("ActeurUnit", StringComparison.OrdinalIgnoreCase) && InstanceName(o) == name);
                // état de départ (octet 39 du composant A02A) : 3 évanoui, 1 conscient
                byte wanted = ConsciousCivilians.TryGetValue(Mission, out string[]? awake) && awake.Contains(name, StringComparer.OrdinalIgnoreCase) ? (byte)1 : (byte)3;
                LevelObject? template = TemplateWhere(o => o.Kind == EntityKind.Civilian && o.ClassPath.EndsWith(cls, StringComparison.OrdinalIgnoreCase)
                    && o.GetComponent(0xA02A) is { } c && c.Size > 39 && o.ComponentBytes(c)[39] == wanted);
                // pas de civil évanoui de ce modèle dans les missions du jeu : un autre civil évanoui, avec la classe voulue
                string classPath = template?.ClassPath ?? "";
                if (template == null)
                {
                    template = TemplateWhere(o => o.Kind == EntityKind.Civilian && o.GetComponent(0xA02A) is { } c && c.Size > 39 && o.ComponentBytes(c)[39] == wanted);
                    if (template != null)
                        classPath = template.ClassPath[..(template.ClassPath.IndexOf("civilian\\", StringComparison.OrdinalIgnoreCase) is int k and >= 0 ? k : 0)] + cls;
                }
                if (actor == null || template == null)
                {
                    Report.Add($"civils : {name} {(actor == null ? "introuvable dans le niveau FD1" : "sans civil modèle dans les missions du jeu")}");
                    continue;
                }
                Vector3 at = new Vector3(actor.World.M41, actor.World.M42, actor.World.M43) * WorldToCm;
                float yaw = MathF.Atan2(actor.World.M12, actor.World.M11);
                LevelObject c = LevelEdit.Clone(template, LevelEdit.NewId(level), name, classPath);
                c = LevelEdit.WithTransform(c, at, new Vector3(0, 0, yaw));
                c = LevelEdit.WithStartVisible(c, false);
                level.Objects.Add(c);
                n++;
            }
            Report.Add($"civils : {n} (cachés au départ)");
        }
        if (StoryTriggers.TryGetValue(Mission, out (string name, string script, Vector3 min, Vector3 max)[]? triggers))
        {
            LevelObject? template = TemplateWhere(o => o.Kind == EntityKind.Trigger && LevelEdit.Trigger(o) != null);
            if (template == null)
            {
                Report.Add("déclencheurs : zone modèle introuvable dans les missions du jeu");
                return;
            }
            foreach ((string name, string script, Vector3 min, Vector3 max) in triggers)
            {
                LevelObject t = LevelEdit.Clone(template, LevelEdit.NewId(level), name, template.ClassPath);
                t = LevelEdit.WithTransform(t, (min + max) / 2, Vector3.Zero);
                t = LevelEdit.WithTrigger(t, script, StoryTriggerLeave.TryGetValue(name, out string? leave) ? leave : "", true, max - min);
                level.Objects.Add(t);
            }
            Report.Add($"déclencheurs : {triggers.Length}");
        }
    }

    // ---- Entrepot ----

    /// <summary>Zones des deux halls d'Entrepot (cm : x min, x max, y min, y max) : groupes de foyers des objectifs.</summary>
    private static readonly (float x0, float x1, float y0, float y1) EntrepotHall1 = (6900, 14000, 5500, 13690);
    private static readonly (float x0, float x1, float y0, float y1) EntrepotHall2 = (6900, 14000, 13690, 19700);

    /// <summary>
    /// Foyers FD1 (rang dans le niveau) qui prennent feu au début de l'acte 2 (relevés sur deux captures du jeu) : contre le
    /// mur sous le bureau, la rangée de machines du haut de l'atelier, les machines de gauche et les machines rouges.
    /// </summary>
    private static readonly int[] EntrepotAct2Fires = { 61, 292, 193, 104, 320, 298, 218, 307, 309 };

    /// <summary>
    /// Foyer FD1 du premier bureau (meuble) à gauche de l'étage, côté fenêtres : il prend feu la première fois qu'on monte
    /// (le foyer 278, plus à gauche, est de l'autre côté du mur).
    /// </summary>
    private const int EntrepotUpstairsFire = 169;

    /// <summary>Délai des bombes amorcées (s) : bombe 2 3 s, les autres 45 s (FD1 : bibBib).</summary>
    private static float BombDelay(int n) => n == 2 ? 3f : 45f;

    /// <summary>Script du déroulement (fonctions, départ, boucle) ; vide pour une mission sans déroulement.</summary>
    private (string functions, string start, string main) StoryScript(Fd1Level fd1)
    {
        if (Mission.Equals("banque", StringComparison.OrdinalIgnoreCase))
            return BanqueScript(fd1);
        if (Mission.Equals("Villa", StringComparison.OrdinalIgnoreCase))
            return VillaScript(fd1);
        if (Mission.Equals("quartier", StringComparison.OrdinalIgnoreCase))
            return QuartierScript(fd1);
        if (Mission.Equals("station_service", StringComparison.OrdinalIgnoreCase))
            return StationScript(fd1);
        if (Mission.Equals("train", StringComparison.OrdinalIgnoreCase))
            return TrainScript(fd1);
        if (Mission.Equals("ecole", StringComparison.OrdinalIgnoreCase))
            return EcoleScript(fd1);
        if (Mission.Equals("foret", StringComparison.OrdinalIgnoreCase))
            return ForetScript(fd1);
        if (!Mission.Equals("Entrepot", StringComparison.OrdinalIgnoreCase))
            return ("", "", "");
        string T(string key) => _hasTexts ? TextKey(key) : key;
        List<string> hall1 = new List<string>();
        List<string> hall2 = new List<string>();
        for (int i = 0; i < fd1.Fires.Count; i++)
        {
            Vector3 p = fd1.Fires[i].at * WorldToCm;
            if (p.X >= EntrepotHall1.x0 && p.X <= EntrepotHall1.x1 && p.Y >= EntrepotHall1.y0 && p.Y < EntrepotHall1.y1)
                hall1.Add($"foyer_{i + 1}");
            else if (p.X >= EntrepotHall2.x0 && p.X <= EntrepotHall2.x1 && p.Y >= EntrepotHall2.y0 && p.Y <= EntrepotHall2.y1)
                hall2.Add($"foyer_{i + 1}");
        }
        Report.Add($"script : {hall1.Count} foyers dans le premier hall, {hall2.Count} dans l'atelier");
        StringBuilder f = new StringBuilder();
        void L(string line) => f.Append(line).Append("\r\n");
        void Msg(string indent, string key) => L($"{indent}PNL_AddMessage(\"\", \"{T(key)}\", 0.0, 0.0);");

        L("// ===== Entrepot : déroulement (traduit des scripts FD1) =====");
        L("");
        L("void Entrepot_Init()");
        L("{");
        L("\ts32 g;");
        L("\tSCR_SetVarS32(\"Acte\", 1);");
        // cinématique d'arrivée (FD1 : ARRIVEE)
        if (Cine("ARRIVEE").Length > 0)
            L("\t" + Cine("ARRIVEE"));
        foreach (string v in new[] { "NbMach", "Hall1Vu", "Hall2Vu", "FlaqueVue", "FeuAtelier", "GardesSauves", "SecretaireSauvee", "VideoDone", "DocDone", "BombeDone", "Fin", "Feu1Fini", "Feu2Fini", "Garde1Vu", "Garde2Vu", "SecretaireVue", "EtageFeu", "Surprise1", "Surprise3" })
            L($"\tSCR_SetVarS32(\"{v}\", 0);");
        foreach ((string name, List<string> fires) in new[] { ("hall1", hall1), ("hall2", hall2) })
        {
            L($"\tg = ENT_GroupCreate(\"feux_{name}\");");
            foreach (string h in fires)
                L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
            L($"\tSCR_SetVarS32(\"G_{name}\", g);");
        }
        // flaque électrique : les unités dedans (groupe), le courant (1 : il passe)
        L("\tSCR_SetVarS32(\"G_flaque\", ENT_GroupCreate(\"dans_flaque\"));");
        L("\tSCR_SetVarS32(\"FlaqueActive\", 1);");
        // étagères couchées : cachées (le moteur rouvre leurs cases)
        if (FallingShelves.TryGetValue(Mission, out string[]? shelves))
            foreach (string sh in shelves)
                L($"\tENT_SetVisibility(ENT_FindByName(\"{sh}Bis\"), 0);");
        L($"\tSCR_SetVarS32(\"Obj_Gardes\", LEV_AddObjectiveIterate(\"{T("ENTREPOT_DES_S1O0")}\", 2));");
        L($"\tSCR_SetVarS32(\"Obj_Feu1\", LEV_AddObjectiveBoolean(\"{T("ENTREPOT_DES_S1O2")}\"));");
        L($"\tSCR_SetVarS32(\"Obj_Indices\", LEV_AddObjectiveIterate(\"{T("ENTREPOT_DES_S1O1_SEUL")}\", 3));");
        L("}");
        L("");
        L("// le scout (renfort) est arrivé : il peut monter au grappin");
        L("void MsgScout()");
        L("{");
        if (Cine("PASSAGES").Length > 0)
            L("\t" + Cine("PASSAGES"));
        Msg("\t", "ENT_CUT_PASSAGES2");
        Msg("\t", "ENT_CUT_PASSAGES3");
        L("}");
        L("");
        // bombes : amorçage et explosion
        for (int n = 0; n <= 4; n++)
        {
            L($"// bombe {n} : amorcée, elle explose {BombDelay(n):0} s plus tard (puis 1 s de bip rapide)");
            L($"void Amorce{n}()");
            L("{");
            L($"\tif(SCR_GetVarS32(\"bombe{n}Mec\") == 0)");
            L("\t{");
            L($"\t\tSCR_SetVarS32(\"bombe{n}Mec\", -1);");
            L($"\t\tSCR_RunDelayed(\"PreBoum{n}\", {BombDelay(n):0}.0);");
            L("\t}");
            L("}");
            L($"void PreBoum{n}()");
            L("{");
            L($"\tif(SCR_GetVarS32(\"bombe{n}Mec\") == -1)");
            L($"\t\tSCR_RunDelayed(\"Boum{n}\", 1.0);");
            L("}");
            L($"void Boum{n}()");
            L("{");
            L($"\tif(SCR_GetVarS32(\"bombe{n}Mec\") == -1)");
            L($"\t\tENT_SetDamage(ENT_FindByName(\"bombe{n}\"), 0.0);");
            L("}");
            L("");
        }
        L("// les bombes s'amorcent : message de FD1 (cinématique SURPRISE2)");
        L("void MsgBombes()");
        L("{");
        Msg("\t", "ENT_CUT_SURPRISE21");
        Msg("\t", "ENT_CUT_SURPRISE22");
        Msg("\t", "ENT_CUT_SURPRISE23");
        L("}");
        L("void Bombe0Run()");
        L("{");
        L("\tif(SCR_GetVarS32(\"bombe0Mec\") == 0)");
        L("\t{");
        L("\t\tMsgBombes();");
        if (Cine("SURPRISE0").Length > 0)
            L("\t\t" + Cine("SURPRISE0"));
        L("\t\tAmorce0();");
        L("\t}");
        L("}");
        L("void Bombe4Run()");
        L("{");
        L("\tif(FIR_HPGetIntensity(SCR_GetVarS32(\"G_hall2\")) >= 250.0 && SCR_GetVarS32(\"bombe4Mec\") == 0)");
        L("\t{");
        L("\t\tMsgBombes();");
        if (Cine("SURPRISE4").Length > 0)
            L("\t\t" + Cine("SURPRISE4"));
        L("\t\tAmorce4();");
        L("\t}");
        L("}");
        L("void Bombe2Run()");
        L("{");
        L("\tif(SCR_GetVarS32(\"bombe2Mec\") == 0)");
        L("\t{");
        L("\t\tMsgBombes();");
        if (Cine("SURPRISE2").Length > 0)
            L("\t\t" + Cine("SURPRISE2"));
        L("\t\tSCR_RunDelayed(\"Amorce2\", 10.0);");
        L("\t}");
        L("}");
        L("");
        L("// Garde1 : découvert en entrant dans la zone (FD1 : decacheToit)");
        L("void TCacheToit2(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tif(SCR_GetVarS32(\"Garde1Vu\") == 0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"Garde1Vu\", 1);");
        L("\t\tENT_SetVisibility(ENT_FindByName(\"Garde1\"), 1);");
        Msg("\t\t", "ENT_CUT_GARDIEN1");
        if (Cine("GARDIEN1").Length > 0)
            L("\t\t" + Cine("GARDIEN1"));
        L("\t\tSCR_RunDelayed(\"Bombe2Run\", 50.0);");
        L("\t}");
        L("}");
        L("");
        L("// première montée à l'étage du bureau : le feu prend tout à gauche");
        L("void EtageBureau(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tif(SCR_GetVarS32(\"EtageFeu\") == 0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"EtageFeu\", 1);");
        L($"\t\tFIR_HPSetIntensity(ENT_FindByName(\"foyer_{EntrepotUpstairsFire + 1}\"), 50.0);");
        L("\t}");
        L("}");
        L("");
        L("// rien à faire (objet du script dont la destruction ne déclenche rien)");
        L("void FD1_Rien(EntityID Objet)");
        L("{");
        L("}");
        L("");
        if (FallingShelves.TryGetValue(Mission, out string[]? falling))
            foreach (string sh in falling)
            {
                L($"// l'étagère {sh} a brûlé : elle tombe (FD1 : etagereCheckStatus)");
                L($"void Tombe_{sh}(EntityID Objet)");
                L("{");
                L($"\tif(SCR_GetVarS32(\"{sh}Tombee\") == 0)");
                L("\t{");
                L($"\t\tSCR_SetVarS32(\"{sh}Tombee\", 1);");
                L($"\t\tENT_SetVisibility(ENT_FindByName(\"{sh}\"), 0);");
                L($"\t\tENT_SetVisibility(ENT_FindByName(\"{sh}Bis\"), 1);");
                L("\t}");
                L("}");
            }
        L("");
        L("// une machine a brûlé (FD1 : Machines) : toutes détruites, la mission est perdue");
        L("void MachDetruite(EntityID Machine)");
        L("{");
        L("\tSCR_SetVarS32(\"NbMach\", SCR_GetVarS32(\"NbMach\") + 1);");
        L("\tif(SCR_GetVarS32(\"NbMach\") >= 16 && SCR_GetVarS32(\"Fin\") == 0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"Fin\", 2);");
        L($"\t\tLEV_SetDebriefText(\"{T("DEFEAT_MACHINES")}\");");
        L("\t\tSCR_RunDelayed(\"Entrepot_Defaite\", 5.0);");
        L("\t}");
        L("}");
        L("");
        L("// flaque électrique : qui y entre s'y électrocute (FD1 : Flaque) jusqu'à ce qu'on coupe le courant à l'armoire");
        L("void Flaque_Entree(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tENT_GroupAddEntity(SCR_GetVarS32(\"G_flaque\"), Unit);");
        L("}");
        L("void Flaque_Sortie(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tENT_GroupRemoveEntity(SCR_GetVarS32(\"G_flaque\"), Unit);");
        L("}");
        L("void FlaqueCut(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tif(SCR_GetVarS32(\"FlaqueVue\") == 0 && SCR_GetVarS32(\"FlaqueActive\") == 1)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"FlaqueVue\", 1);");
        Msg("\t\t", "ENT_CUT_FLAQUE");
        if (Cine("CUTFLAQUE").Length > 0)
            L("\t\t" + Cine("CUTFLAQUE"));
        L("\t}");
        L("}");
        L("void BoutFlaque()");
        L("{");
        L("\tSCR_SetVarS32(\"FlaqueActive\", 0);");
        Msg("\t", "EVENT_ENT_FLAQUE");
        L("}");
        L("void Entrepot_Flaque()");
        L("{");
        L("\ts32 i;");
        L("\ts32 g;");
        L("\tif(SCR_GetVarS32(\"FlaqueActive\") == 0)");
        L("\t\treturn;");
        L("\tg = SCR_GetVarS32(\"G_flaque\");");
        L("\tfor(i = 0; i < ENT_GroupGetNbEntities(g); i = i + 1)");
        L("\t\tENT_AddDamage(ENT_GroupGetEntityAt(g, i), -1.0);");
        L("}");
        L("");
        L("// indices (facultatifs)");
        L("void VideoPick()");
        L("{");
        L("\tSCR_SetVarS32(\"VideoDone\", 1);");
        Msg("\t", "EVENT_ENT_VIDEO");
        L("\tSCR_RunDelayed(\"VideoPick2\", 1.0);");
        L("}");
        L("void VideoPick2()");
        L("{");
        Msg("\t", "EVENT_ENT_VIDEO2");
        L("}");
        L("void DocPick()");
        L("{");
        L("\tSCR_SetVarS32(\"DocDone\", 1);");
        L("\tENT_SetVisibility(ENT_FindByName(\"ent_documents\"), 0);");
        Msg("\t", "EVENT_ENT_DOCUMENT");
        L("\tSCR_RunDelayed(\"DocPick2\", 3.0);");
        L("}");
        L("void DocPick2()");
        L("{");
        Msg("\t", "EVENT_ENT_DOCUMENT2");
        L("}");
        L("void MsgBombePreuve()");
        L("{");
        Msg("\t", "EVENT_ENT_BOMBE2");
        L("}");
        L("");
        L("// civil mis à l'abri (rappel du moteur)");
        L("void OnCivilianSaved(EntityID Civil)");
        L("{");
        L("\tif(Civil == ENT_FindByName(\"Garde1\") || Civil == ENT_FindByName(\"Garde2\"))");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"GardesSauves\", SCR_GetVarS32(\"GardesSauves\") + 1);");
        L("\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Gardes\"), SCR_GetVarS32(\"GardesSauves\"));");
        L("\t\tif(SCR_GetVarS32(\"GardesSauves\") >= 2)");
        L("\t\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Gardes\"), 1);");
        L("\t}");
        L("\tif(Civil == ENT_FindByName(\"Secretaire\"))");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"SecretaireSauvee\", 1);");
        Msg("\t\t", "EVENT_ENT_SECRETAIRE");
        L("\t\tif(SCR_GetVarS32(\"Acte\") >= 2)");
        L("\t\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Secretaire\"), 1);");
        L("\t}");
        L("}");
        L("");
        L("// civils découverts derrière les portes cassées (FD1 : CheckGlobalCache)");
        L("void Entrepot_Caches()");
        L("{");
        foreach ((string door, string civil, string flag, string key, string cine) in new[] { ("Porte3", "Garde2", "Garde2Vu", "ENT_CUT_GARDIEN2", "GARDIEN2"), ("Porte4", "Secretaire", "SecretaireVue", "ENT_CUT_SECRETAIRE", "SECRETAIRE") })
        {
            L($"\tif(SCR_GetVarS32(\"{flag}\") == 0 && ENT_GetState(ENT_FindByName(\"{door}\")) == \"Broken\")");
            L("\t{");
            L($"\t\tSCR_SetVarS32(\"{flag}\", 1);");
            L($"\t\tENT_SetVisibility(ENT_FindByName(\"{civil}\"), 1);");
            Msg("\t\t", key);
            if (Cine(cine).Length > 0)
                L("\t\t" + Cine(cine));
            L("\t}");
        }
        L("}");
        L("");
        L("// défaites (FD1 : Entrepot_CheckDefeat) : texte du bilan, puis l'échec 5 s plus tard");
        L("void Entrepot_Defaites()");
        L("{");
        L("\tif(SCR_GetVarS32(\"Fin\") != 0)");
        L("\t\treturn;");
        foreach ((string test, string key) in new[]
        {
            ("LEV_IsHealVehicleRemaining() == 0", "DEFEAT4"),
            // civils morts : le moteur ne fait pas encore mourir les civils (ENT_GetDamage d'un civil : 100)
            ("ENT_GetDamage(ENT_FindByName(\"Secretaire\")) <= 0.0", "DEFEAT3"),
            ("ENT_GetDamage(ENT_FindByName(\"Garde1\")) <= 0.0 || ENT_GetDamage(ENT_FindByName(\"Garde2\")) <= 0.0", "DEFEAT6"),
        })
        {
            L($"\tif({test})");
            L("\t{");
            L("\t\tSCR_SetVarS32(\"Fin\", 2);");
            L($"\t\tLEV_SetDebriefText(\"{T(key)}\");");
            L("\t\tSCR_RunDelayed(\"Entrepot_Defaite\", 5.0);");
            L("\t\treturn;");
            L("\t}");
        }
        // le scout : celui qui a appuyé sur le bouton de l'échelle 1 ; mort avant d'avoir descendu l'échelle 2 (FD1 :
        // « ComPlusUtile »), plus personne ne peut monter au bureau
        L("\tif(SCR_GetVarS32(\"User_Ent_Bouton_Echelle1\") != 0 && SCR_GetVarS32(\"Ent_Bouton_Echelle2\") == 0 && ENT_GetUnitState(SCR_GetVarS32(\"User_Ent_Bouton_Echelle1\")) == 4)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"Fin\", 2);");
        L($"\t\tLEV_SetDebriefText(\"{T("DEFEAT5")}\");");
        L("\t\tSCR_RunDelayed(\"Entrepot_Defaite\", 5.0);");
        L("\t\treturn;");
        L("\t}");
        L("}");
        L("void Entrepot_Defaite()");
        L("{");
        L("\tLEV_TriggerDefeat();");
        L("}");
        L("");
        L("// victoire (FD1 : Victory_OnTimer) : texte selon les indices trouvés");
        L("void Entrepot_Victoire()");
        L("{");
        L("\ts32 v;");
        L("\ts32 d;");
        L("\ts32 b;");
        L("\tv = SCR_GetVarS32(\"VideoDone\");");
        L("\td = SCR_GetVarS32(\"DocDone\");");
        L("\tb = SCR_GetVarS32(\"BombeDone\");");
        (int v, int d, int b, int k)[] endings = { (1, 1, 1, 1), (1, 1, 0, 2), (1, 0, 0, 3), (0, 0, 0, 4), (0, 1, 1, 5), (0, 0, 1, 6), (0, 1, 0, 7), (1, 0, 1, 8) };
        foreach ((int v, int d, int b, int k) in endings)
        {
            // plus de 8 machines détruites : le texte « BIS » (FD1)
            L($"\tif(v == {v} && d == {d} && b == {b})");
            L("\t{");
            L("\t\tif(SCR_GetVarS32(\"NbMach\") > 8)");
            L($"\t\t\tLEV_SetDebriefText(\"{T("VICTORY" + k + "BIS")}\");");
            L("\t\telse");
            L($"\t\t\tLEV_SetDebriefText(\"{T("VICTORY" + k)}\");");
            L("\t}");
        }
        L("\tLEV_TriggerVictory();");
        L("}");
        L("");
        L("// le feu prend dans l'atelier (une seule fois) : au passage à l'acte 2, ou plus tôt si une unité approche ; le");
        L("// renfort est appelé en même temps");
        L("void FeuAtelier()");
        L("{");
        L("\tif(SCR_GetVarS32(\"FeuAtelier\") == 0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"FeuAtelier\", 1);");
        if (_reinforcementEta > 0)
        {
            // le renfort (scout et pompier ignifugé) est appelé en même temps : il arrive après son délai (FD1 [REP_TIME])
            L("\t\tLEV_CallReinforcement(1);");
            L($"\t\tSCR_RunDelayed(\"MsgScout\", {_reinforcementEta + 3:0}.0);");
        }
        Msg("\t\t", "ENT_CUT_PASSAGES1");
        foreach (int i in EntrepotAct2Fires)
            L($"\t\tFIR_HPSetIntensity(ENT_FindByName(\"foyer_{i + 1}\"), 50.0);");
        L("\t}");
        L("}");
        L("void Zone_FeuAtelier(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tFeuAtelier();");
        L("}");
        L("");
        L("// passage à l'acte 2");
        L("void Entrepot_Acte2()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 2);");
        L("\tFeuAtelier();");
        L("\tSCR_RunDelayed(\"Bombe0Run\", 120.0);");
        L("\tSCR_RunDelayed(\"Bombe4Run\", 240.0);");
        L($"\tSCR_SetVarS32(\"Obj_Secretaire\", LEV_AddObjectiveBoolean(\"{T("ENTREPOT_DES_S2O0")}\"));");
        L($"\tSCR_SetVarS32(\"Obj_Feu2\", LEV_AddObjectiveBoolean(\"{T("ENTREPOT_DES_S2O2_SEUL")}\"));");
        L("\tif(SCR_GetVarS32(\"SecretaireSauvee\") == 1)");
        L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Secretaire\"), 1);");
        L("}");
        L("");
        L("// boucle : actes, bombes, indices, défaites");
        L("void Entrepot_Main()");
        L("{");
        L("\tf32 feu;");
        L("\ts32 n;");
        L("\tif(SCR_GetVarS32(\"Fin\") != 0)");
        L("\t\treturn;");
        L("\tEntrepot_Caches();");
        L("\tEntrepot_Defaites();");
        L("\tEntrepot_Flaque();");
        if (Cine("TRUCKEXPLOSION").Length > 0)
        {
            // le camion du parking a explosé (FD1 : TruckExpl)
            L("\tif(SCR_GetVarS32(\"TruckAExpl\") == 0 && ENT_GetDamage(ENT_FindByName(\"Truckabc\")) <= 0.0)");
            L("\t{");
            L("\t\tSCR_SetVarS32(\"TruckAExpl\", 1);");
            L("\t\t" + Cine("TRUCKEXPLOSION"));
            L("\t}");
        }
        L("\t// indices : la vidéo, les documents, une bombe désamorcée");
        L("\tif(SCR_GetVarS32(\"BombeDone\") == 0 && (" + string.Join(" || ", Enumerable.Range(0, 5).Select(n => $"SCR_GetVarS32(\"bombe{n}Mec\") == 1")) + "))");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"BombeDone\", 1);");
        L("\t\tSCR_RunDelayed(\"MsgBombePreuve\", 4.0);");
        L("\t}");
        L("\tn = SCR_GetVarS32(\"VideoDone\") + SCR_GetVarS32(\"DocDone\") + SCR_GetVarS32(\"BombeDone\");");
        L("\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Indices\"), n);");
        L("\tif(n >= 3)");
        L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Indices\"), 1);");
        L("\tif(SCR_GetVarS32(\"Acte\") == 1)");
        L("\t{");
        L("\t\tfeu = FIR_HPGetIntensity(SCR_GetVarS32(\"G_hall1\"));");
        L("\t\t// le feu a bien pris (les tout premiers instants, les foyers ne comptent pas encore)");
        L("\t\tif(feu >= 400.0)");
        L("\t\t\tSCR_SetVarS32(\"Hall1Vu\", 1);");
        L("\t\t// le feu du premier hall est presque éteint : la bombe 3 s'amorce (FD1 : Go < 400)");
        L("\t\tif(feu < 400.0 && SCR_GetVarS32(\"Hall1Vu\") == 1 && SCR_GetVarS32(\"Surprise3\") == 0)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Surprise3\", 1);");
        L("\t\t\tif(SCR_GetVarS32(\"bombe3Mec\") == 0)");
        L("\t\t\t{");
        L("\t\t\t\tMsgBombes();");
        if (Cine("SURPRISE3").Length > 0)
            L("\t\t\t\t" + Cine("SURPRISE3"));
        L("\t\t\t\tAmorce3();");
        L("\t\t\t}");
        L("\t\t}");
        L("\t\tif(feu <= 0.0 && SCR_GetVarS32(\"Hall1Vu\") == 1 && SCR_GetVarS32(\"Feu1Fini\") == 0)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Feu1Fini\", 1);");
        L("\t\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Feu1\"), 1);");
        L("\t\t}");
        L("\t\tif(SCR_GetVarS32(\"Feu1Fini\") == 1 && SCR_GetVarS32(\"GardesSauves\") >= 2)");
        L("\t\t\tEntrepot_Acte2();");
        L("\t}");
        L("\telse if(SCR_GetVarS32(\"Acte\") == 2)");
        L("\t{");
        L("\t\tfeu = FIR_HPGetIntensity(SCR_GetVarS32(\"G_hall2\"));");
        L("\t\tif(feu >= 250.0)");
        L("\t\t\tSCR_SetVarS32(\"Hall2Vu\", 1);");
        L("\t\t// l'atelier est presque éteint : la bombe 1 s'amorce (FD1 : Go < 200)");
        L("\t\tif(feu < 200.0 && SCR_GetVarS32(\"Hall2Vu\") == 1 && SCR_GetVarS32(\"Surprise1\") == 0)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Surprise1\", 1);");
        L("\t\t\tif(SCR_GetVarS32(\"bombe1Mec\") == 0)");
        L("\t\t\t{");
        L("\t\t\t\tMsgBombes();");
        if (Cine("SURPRISE").Length > 0)
            L("\t\t\t\t" + Cine("SURPRISE"));
        L("\t\t\t\tAmorce1();");
        L("\t\t\t}");
        L("\t\t}");
        L("\t\tif(feu <= 0.0 && (SCR_GetVarS32(\"Hall2Vu\") == 1 || SCR_GetVarS32(\"FeuAtelier\") == 1) && SCR_GetVarS32(\"Feu2Fini\") == 0)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Feu2Fini\", 1);");
        L("\t\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Feu2\"), 1);");
        L("\t\t}");
        L("\t\t// fin : atelier éteint, secrétaire sauvée, aucune bombe sur le point d'exploser");
        L("\t\tif(SCR_GetVarS32(\"Feu2Fini\") == 1 && SCR_GetVarS32(\"SecretaireSauvee\") == 1 && SCR_GetVarS32(\"bombe1Mec\") != -1 && SCR_GetVarS32(\"bombe4Mec\") != -1)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Acte\", 3);");
        L("\t\t\tSCR_SetVarS32(\"Fin\", 1);");
        L("\t\t\tSCR_RunDelayed(\"Entrepot_Victoire\", 5.0);");
        L("\t\t}");
        L("\t}");
        L("}");
        L("");
        return (f.ToString(), "\tEntrepot_Init();\r\n", "\tEntrepot_Main();\r\n");
    }
}
