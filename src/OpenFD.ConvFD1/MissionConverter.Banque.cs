using System.Numerics;
using System.Text;
using OpenFD.Formats;

namespace OpenFD.ConvFD1;

/// <summary>
/// Banque (scripts FD1 Banque_Main / _Scenar / _Event / _Trigger) :
///  • acte 1 : entrer dans la banque (zone « T1 » devant l'entrée) ; trois minutes plus tard, le feu prend à l'étage ;
///  • acte 2 : évacuer les cinq victimes du rez-de-chaussée (une cliente et deux gardes dans le hall, deux employés dans les
///    bureaux) ; quand on approche des bureaux (zone « T12 »), le feu y prend, puis ils s'embrasent (flash-over) si on ne
///    l'éteint pas à temps ;
///  • acte 3 : le renfort (grande échelle, tour d'eau, spécialiste au grappin) puis l'hélicoptère sur le toit voisin ;
///    évacuer les quatre victimes de l'étage (le directeur, un client dans le salon VIP, le comptable, l'informaticien) ;
///    récupérer les bandes informatiques (6) et le contenu des coffres (6) avant le feu ;
///  • acte 4 : la salle des coffres s'ouvre ; récupérer le garde et les trois faux pompiers qui s'y sont réfugiés ;
///  • victoire : texte selon les coffres et les bandes sauvés ; défaites : ambulance détruite, directeur mort, toutes les
///    bandes ou tous les coffres perdus.
/// Les groupes de foyers FD1 (SetHotpointGroupActivated) ne sont pas lisibles dans le niveau : ils sont reconstitués par
/// pièce (BanqueGroups), d'après les commentaires du script FD1 et l'emplacement des victimes et des portes.
/// </summary>
public sealed partial class MissionConverter
{
    /// <summary>Objets FD1 laissés de côté, par mission (nom de l'instance) : remplacés par ceux du moteur, ou portes ouvertes.</summary>
    private static readonly Dictionary<string, string[]> HiddenDecor = new(StringComparer.OrdinalIgnoreCase)
    {
        // l'hélicoptère du décor (un vrai hélicoptère du moteur le remplace), les deux portes coupe-feu de l'étage (ouvertes
        // au départ : le passage reste libre), les coffres brûlés et les bandes brûlées (le script FD1 les montre à la place
        // des intacts), la seconde porte blindée posée sur la première
        { "banque", new[] { "Helicoptere", "PorteCoupe0", "PorteCoupe1", "CoffreBrul1", "CoffreBrul2", "CoffreBrul3", "bob1", "bob2", "bob3", "bob4", "bob5", "bob6", "PorteSalleBlindee" } },
        // Train : les débris de la locomotive de tête (FD1 les montre quand elle explose ; le moteur fait son explosion)
        {
            "train", new[]
            {
                "DebriLoco1_01", "DebriLoco1_02", "DebriLoco1_03", "DebriLoco1_04", "DebriLoco1_05", "DebriLoco1_06", "DebriLoco1_07", "DebriLoco1_08", "DebriLoco1_09",
                "DebriLoco1_10", "DebriLoco1_11", "DebriLoco1_12", "DebriLoco1_13", "DebriLoco1_14", "DebriLoco1_15", "DebriLoco1_16", "DebriLoco1_17", "DebriLoco1_18",
            }
        },
        // Ecole : les éclairs bleus autour de la grue et de l'armoire électrique (FD1 : un effet animé, mélangé ; le moteur le
        // dessinerait plein, une grue toute bleue)
        { "ecole", new[] { "effetElec0", "effetElec1" } },
        // Foret : les cercles des héliports que le script FD1 allume et éteint (zone occupée, feux trop proches), l'aire de
        // l'hélicoptère qui s'écrase, l'hélicoptère et la voiture animés des cinématiques (FD1 : leurs épaves, montrées par
        // le script, les remplacent), le Canadair posé hors de la carte
        {
            "foret", new[]
            {
                "Heliport1_NO", "Heliport2_NO", "Zone1_FireOff", "Zone2_FireOff", "Heliport3_YES", "Helicoptere3", "Voiture_Accident",
                "Vehicule_canadair",
            }
        },
        // Centrale : les grandes portes et les sas animés de FD1 (doublons des portes du moteur), le ventilateur en marche
        // (FD1 : VentSap), les flaques d'huile des turbines et de sodium des pompes (le moteur n'a que leurs feux)
        {
            "Centrale_Nucleaire", new[]
            {
                "GPorteAnim1", "GPorteAnim2", "GPorteAnim3", "GPorteAnim10", "GPorteAnim11", "GPorteAnim12", "GPorteAnim20", "SasAnim1",
                "SasAnim2", "VentSap",
                "T2F1", "T2F2", "T2F3", "T2F4", "T2F5", "T2F6", "T2F7", "T2F8", "T2F9", "T2F10", "T2F11", "T3F1", "T3F2", "T3F3", "T3F4", "T3F5", "T3F6", "T3F7", "T3F8",
                "M1S1", "M1S2", "M1S3", "M1S4", "M1S5", "M2S1", "M2S2", "M2S3", "M2S4", "M2S5", "M3S1", "M3S2", "M3S3", "M3S4", "M3S5", "M4S1", "M4S2", "M4S3", "M4S4", "M4S5", "M5S1", "M5S2", "M5S3", "M5S4", "M5S5", "M6S1", "M6S2", "M6S3", "M6S4", "M6S5", "M7S1", "M7S2", "M7S3", "M7S4", "M7S5", "M8S1", "M8S2", "M8S3", "M8S4", "M8S5",
            }
        },
    };

    /// <summary>
    /// Unités posées à part dans le niveau du moteur (cachées au départ, montrées par le script), par mission : nom de
    /// l'instance FD1 → classe du moteur (« Design\Unit\… » sans l'extension). Banque : l'hélicoptère et son équipe.
    /// </summary>
    private static readonly Dictionary<string, (string instance, string cls)[]> LevelUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        {
            "banque", new[]
            {
                ("MM_helicoptere", "@helico"),
                ("Medic_Helico", "fireman\\fireman_medic"),
                ("Pompier1_Helico", "fireman\\fireman_sp_us"),
                ("Pompier2_Helico", "fireman\\fireman_sp_us"),
            }
        },
        {
            // Villa (MissionConverter.Villa.cs) : le renfort à pied de l'acte 2, la pompe de la piscine, l'Excalibur
            "Villa", new[]
            {
                ("Renfort_Pompier", "fireman\\fireman_sp_us"),
                ("Renfort_Ingenieur", "fireman\\fireman_ing"),
                ("Pompe_Arrosage", "@pompe"),
                ("VoitureDeLuxe1", "@excalibur"),
            }
        },
        // Station-service (MissionConverter.StationService.cs) : le camion-citerne de GPL, que l'ingénieur conduit
        { "station_service", new[] { ("Citerne", "@citerne") } },
        // Ecole (MissionConverter.Ecole.cs) : le bulldozer du chantier, que l'ouvrier prête (acte 2), le sapeur de l'acte 3
        { "ecole", new[] { ("buldo", "firevehicle\\Veh_P_bulldozer"), ("pompier5", "fireman\\fireman_des") } },
        // Foret (MissionConverter.Foret.cs) : les deux hélicoptères de secours, qui se posent quand leur héliport est dégagé
        { "foret", new[] { ("MM_Helicoptere1", "@helico"), ("MM_helicoptere2", "@helico") } },
    };

    /// <summary>Unité posée à part pour cet objet FD1 (null : il fait partie des équipes, ou du décor).</summary>
    private string? LevelUnitOf(Fd1Object o)
    {
        if (!LevelUnits.TryGetValue(Mission, out (string instance, string cls)[]? units))
            return null;
        string? name = InstanceName(o);
        return units.FirstOrDefault(u => u.instance.Equals(name, StringComparison.OrdinalIgnoreCase)).cls;
    }

    /// <summary>
    /// Classe de l'hélicoptère de la Banque : celui du jeu (il soigne et met les victimes à l'abri), et il ravitaille en
    /// eau l'équipe qu'il dépose sur le toit (à l'étage, aucun camion ne peut venir). Rend son chemin pour le niveau.
    /// </summary>
    private string WriteHelicoClass()
    {
        string dir = Path.Combine(Data, "Design", "Unit", "Firevehicle");
        Directory.CreateDirectory(dir);
        string name = Name + "_helico";
        string cls = """
            [#INCLUDE]
            FILE0 = "Veh_P_helico.class"

            [CAPACITIES_RESERVE]
            RESUPPLYPOWER = 150

            """;
        File.WriteAllBytes(Path.Combine(dir, name + ".class"), Encoding.Latin1.GetBytes(cls.Replace("\n", "\r\n")));
        return "design\\unit\\firevehicle\\" + name;
    }

    /// <summary>Unités posées à part dans le niveau du moteur (LevelUnits), cachées au départ.</summary>
    private void AddLevelUnits(Level level, Fd1Level fd1)
    {
        if (!LevelUnits.TryGetValue(Mission, out (string instance, string cls)[]? units))
            return;
        LevelObject? template = TemplateWhere(o => o.Kind == EntityKind.FireVehicle && o.ClassPath.Contains("helico", StringComparison.OrdinalIgnoreCase));
        if (template == null)
        {
            Report.Add("unités posées : véhicule modèle introuvable dans les missions du jeu");
            return;
        }
        int n = 0;
        foreach ((string instance, string cls) in units)
        {
            Fd1Object? o = fd1.Objects.FirstOrDefault(x => instance.Equals(InstanceName(x), StringComparison.OrdinalIgnoreCase));
            if (o == null)
                continue;
            // unité faite d'un objet FD1 (la pompe, l'Excalibur : MissionConverter.Villa.cs)
            bool fd1Model = cls.StartsWith('@') && cls != "@helico";
            string? path = cls == "@helico" ? WriteHelicoClass() : fd1Model ? WriteObjectUnitClass(instance, cls) : "design\\unit\\" + cls;
            if (path == null)
                continue;
            string name = cls switch
            {
                "@helico" when Mission.Equals("banque", StringComparison.OrdinalIgnoreCase) => "Helicoptere",
                "@excalibur" => "Excalibur",
                "@citerne" => StationTruckUnit,
                _ => instance,
            };
            Vector3 at = new Vector3(o.World.M41, o.World.M42, o.World.M43) * WorldToCm;
            float yaw = MathF.Atan2(o.World.M12, o.World.M11);
            LevelObject u = LevelEdit.Clone(template, LevelEdit.NewId(level), name, path);
            // modèle FD1 : posé comme l'objet du décor (le moteur le fait rouler vers son −Y) ; véhicule du jeu : tourné d'un quart
            u = LevelEdit.WithTransform(u, at, new Vector3(0, 0, fd1Model ? yaw : yaw + MathF.PI / 2));
            u = LevelEdit.WithStartVisible(u, false);
            level.Objects.Add(u);
            n++;
        }
        Report.Add($"unités posées : {n} (cachées au départ)");
    }

    /// <summary>
    /// Groupes de foyers de la Banque (numéros du script FD1), par pièce : x min, x max, y min, y max (cm), étage, et pour
    /// les bureaux qui s'embrasent : le groupe du flash-over (les foyers « feux_type_fover » de la pièce) et celui du feu
    /// qui le précède (les autres).
    /// </summary>
    private static readonly (int group, int flashGroup, float x0, float x1, float y0, float y1, bool upstairs)[] BanqueGroups =
    {
        // bureaux du rez-de-chaussée (flash-over) : derrière Porte1 (Employe1), le couloir de Porte4, derrière Porte2,
        // derrière Porte3 (Employe2)
        (3, 4, 9150, 10950, 13000, 15060, false),
        (5, 6, 13200, 16100, 12900, 13600, false),
        (7, 8, 13250, 14700, 13600, 15060, false),
        (9, 10, 14700, 16100, 13600, 15060, false),
        // le hall autour des trois premières victimes (« protège les victimes » jusqu'à l'acte 2)
        (19, 0, 10300, 13300, 11500, 12950, false),
        // salle du bouton des sprinklers et du garde, couloir de la salle des coffres, salle des coffres (FD1 : le foyer 966
        // y prend à l'acte 4)
        (16, 0, 16100, 17200, 11300, 15100, false),
        (18, 0, 14700, 16100, 11300, 12900, false),
        (25, 0, 14700, 16800, 9400, 11300, false),
        // étage : le couloir est (« en L »), le couloir ouest, les bureaux est (comptabilité, directeur), la salle
        // informatique (bandes), le salon VIP
        (21, 0, 10800, 16800, 8800, 9800, true),
        (11, 0, 6250, 10800, 8800, 9450, true),
        (12, 0, 13300, 16800, 7250, 8800, true),
        (17, 0, 6250, 9550, 7250, 8800, true),
        (23, 0, 6250, 9100, 9450, 12100, true),
    };

    /// <summary>Groupe FD1 de ce foyer de la Banque (0 : aucun).</summary>
    private int BanqueGroupOf(Vector3 at, string type)
    {
        if (!Mission.Equals("banque", StringComparison.OrdinalIgnoreCase))
            return 0;
        foreach ((int group, int flash, float x0, float x1, float y0, float y1, bool up) in BanqueGroups)
            if (at.X >= x0 && at.X <= x1 && at.Y >= y0 && at.Y <= y1 && (at.Z > 1000) == up)
                return flash != 0 && type.Contains("fover", StringComparison.OrdinalIgnoreCase) ? flash : group;
        return 0;
    }

    /// <summary>
    /// Pertes de la Banque : intensité d'un foyer « fort » ; secondes passées dans un feu fort avant qu'une bande, un petit
    /// coffre, un grand coffre soit perdu (cumulées : arroser arrête le compte, sans le remettre à zéro).
    /// </summary>
    private static readonly (int strong, int bobine, int smallSafe, int bigSafe) BanqueBurnLimits = (30, 120, 150, 180);

    /// <summary>
    /// Pièces arrosées par les sprinklers de la Banque (cm, rez-de-chaussée) : sous les tuyaux du plafond (modèle FD1
    /// « BAN_SPRINCKLERS ») : la salle des coffres, son couloir, la salle du garde.
    /// </summary>
    private static readonly (float x0, float x1, float y0, float y1)[] BanqueSprinklerRooms =
    {
        (15100, 16400, 9450, 11350),
        (15100, 16050, 11300, 12900),
        (16000, 17050, 11700, 12900),
    };

    /// <summary>Puissance des sprinklers sur chaque foyer : le quart de celle d'un pompier (FIREATTACKPOWER = 10).</summary>
    private const float BanqueSprinklerPower = 2.5f;

    /// <summary>Secondes entre l'appel du renfort (acte 3) et sa cinématique : les véhicules sont alors dans l'avenue.</summary>
    private const int BanqueReinforcementCineDelay = 5;

    /// <summary>Intensité donnée aux foyers d'un groupe FD1 quand le script l'allume (il grandit ensuite de lui-même).</summary>
    private const int BanqueGroupStart = 20;

    /// <summary>Victimes de chaque acte de la Banque (FD1 : CheckScenario2 à 4).</summary>
    private static readonly string[][] BanqueVictims =
    {
        new[] { "Client1", "Garde1", "Garde2", "Employe1", "Employe2" },
        new[] { "Banquier", "GrosClient", "Comptable", "Informaticien" },
        new[] { "Garde3", "Bandit1", "Bandit2", "Bandit3" },
    };

    /// <summary>Bandes informatiques et coffres de la Banque : objets FD1 (petits coffres à ramasser, grands coffres à ouvrir).</summary>
    private static readonly string[] BanqueBobines = { "Bobine1", "Bobine2", "Bobine3", "Bobine4", "Bobine5", "Bobine6" };
    private static readonly string[] BanqueSmallSafes = { "Coffre1", "Coffre2", "Coffre3" };
    private static readonly string[] BanqueBigSafes = { "CoffreMec1", "CoffreMec2", "CoffreMec3" };

    /// <summary>Foyer du moteur le plus proche de cet objet FD1 (nom « foyer_N ») ; null s'il n'y en a pas à moins de 5 m.</summary>
    private string? NearestFire(Fd1Level fd1, string instance)
    {
        Fd1Object? o = fd1.Objects.FirstOrDefault(x => instance.Equals(InstanceName(x), StringComparison.OrdinalIgnoreCase));
        if (o == null)
            return null;
        Vector3 at = new Vector3(o.World.M41, o.World.M42, o.World.M43) * WorldToCm;
        int best = -1;
        float bd = 500;
        for (int i = 0; i < fd1.Fires.Count; i++)
        {
            float d = Vector3.Distance(fd1.Fires[i].at * WorldToCm, at);
            if (d < bd)
            {
                bd = d;
                best = i;
            }
        }
        return best < 0 ? null : $"foyer_{best + 1}";
    }

    /// <summary>Script de la Banque (fonctions, départ, boucle).</summary>
    private (string functions, string start, string main) BanqueScript(Fd1Level fd1)
    {
        string T(string key) => _hasTexts ? TextKey(key) : key;
        StringBuilder f = new StringBuilder();
        void L(string line) => f.Append(line).Append("\r\n");
        void Msg(string indent, string key) => L($"{indent}PNL_AddMessage(\"\", \"{T(key)}\", 0.0, 0.0);");
        void Cin(string indent, string name)
        {
            if (Cine(name).Length > 0)
                L(indent + Cine(name));
        }
        Dictionary<int, List<string>> groups = new Dictionary<int, List<string>>();
        for (int i = 0; i < fd1.Fires.Count; i++)
        {
            int g = BanqueGroupOf(fd1.Fires[i].at * WorldToCm, fd1.Fires[i].type);
            if (g == 0)
                continue;
            if (!groups.TryGetValue(g, out List<string>? list))
                groups[g] = list = new List<string>();
            list.Add($"foyer_{i + 1}");
        }
        Report.Add($"script : groupes de foyers {string.Join(", ", groups.OrderBy(g => g.Key).Select(g => $"{g.Key} ({g.Value.Count})"))}");
        int Count(int g) => groups.TryGetValue(g, out List<string>? l) ? l.Count : 0;
        // foyers qui menacent les bandes et les coffres
        Dictionary<string, string> near = new Dictionary<string, string>();
        foreach (string o in BanqueBobines.Concat(BanqueSmallSafes).Concat(BanqueBigSafes))
            if (NearestFire(fd1, o) is { } fire)
                near[o] = fire;

        L("// ===== Banque : déroulement (traduit des scripts FD1) =====");
        L("");
        L("// un groupe de foyers FD1 prend feu (SetHotpointGroupActivated(n, 1)) ; 0 : il s'éteint et ne peut plus brûler");
        L("void Groupe(s32 g, s32 on)");
        L("{");
        L("\tif(g == 0)");
        L("\t\treturn;");
        L("\tif(on == 1)");
        L("\t{");
        L("\t\tFIR_HPEnable(g, 1);");
        L($"\t\tFIR_HPSetIntensity(g, {BanqueGroupStart}.0);");
        L("\t}");
        L("\telse");
        L("\t{");
        L("\t\tFIR_HPSetIntensity(g, 0.0);");
        L("\t\tFIR_HPEnable(g, 0);");
        L("\t}");
        L("}");
        L("");
        L("void Banque_Init()");
        L("{");
        L("\ts32 g;");
        L("\tSCR_SetVarS32(\"Acte\", 1);");
        Cin("\t", "ARRIVEE");
        foreach ((int g, List<string> fires) in groups.OrderBy(x => x.Key))
        {
            L($"\tg = ENT_GroupCreate(\"groupe_{g}\");");
            foreach (string h in fires)
                L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
            L($"\tSCR_SetVarS32(\"G_{g}\", g);");
            // FD1 (Init_HPScenars) : tous ces groupes attendent le script
            L("\tFIR_HPEnable(g, 0);");
        }
        L($"\tSCR_SetVarS32(\"Obj_Acte1\", LEV_AddObjectiveBoolean(\"{T("BANQUE_DES_S1O0")}\"));");
        L("\tSCR_RunDelayed(\"Banque_Pertes\", 1.0);");
        L("}");
        L("");
        // ---- acte 1 → 2 ----
        L("// T1 : une unité devant l'entrée de la banque ; fin de l'acte 1 (FD1 : Result_1, puis FeuxEnHaut 3 minutes après)");
        L("void T1(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tif(SCR_GetVarS32(\"Acte\") != 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"Acte\", 2);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Acte1\"), 1);");
        L("\tSCR_RunDelayed(\"FeuxEnHaut\", 180.0);");
        L("\tGroupe(SCR_GetVarS32(\"G_19\"), 1);");
        foreach (string c in new[] { "Client1", "Garde1", "Garde2" })
            L($"\tENT_SetVisibility(ENT_FindByName(\"{c}\"), 1);");
        Cin("\t", "SEQUENCE2");
        L($"\tSCR_SetVarS32(\"Obj_Acte2\", LEV_AddObjectiveIterate(\"{T("BANQUE_DES_S2O0")}\", 5));");
        L("}");
        L("void FeuxEnHaut()");
        L("{");
        L("\tif(SCR_GetVarS32(\"FeuxHaut\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"FeuxHaut\", 1);");
        L("\tGroupe(SCR_GetVarS32(\"G_11\"), 1);");
        L("\tGroupe(SCR_GetVarS32(\"G_12\"), 1);");
        L("\tGroupe(SCR_GetVarS32(\"G_21\"), 1);");
        L("}");
        L("");
        L("// T4 : l'entrée, où brûlent les liquides incendiaires");
        L("void T4(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tif(SCR_GetVarS32(\"T4Vu\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"T4Vu\", 1);");
        Cin("\t", "AGENT_INCENDIAIRE");
        L("}");
        L("");
        // ---- flash-over ----
        L("// T12 : on approche des bureaux ; 18 s plus tard le feu y prend (FD1 : Flash_OverN_1, CacheFlashOver), puis chaque");
        L("// bureau s'embrase s'il brûle encore fort (Flash_OverN_2)");
        L("void T12(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tif(SCR_GetVarS32(\"T12Vu\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"T12Vu\", 1);");
        L("\tSCR_RunDelayed(\"FlashDebut\", 18.0);");
        L("}");
        L("void FlashDebut()");
        L("{");
        foreach (int g in new[] { 3, 5, 7, 9 })
            L($"\tGroupe(SCR_GetVarS32(\"G_{g}\"), 1);");
        L("\tENT_SetVisibility(ENT_FindByName(\"Employe1\"), 1);");
        L("\tENT_SetVisibility(ENT_FindByName(\"Employe2\"), 1);");
        Cin("\t", "PRESENTATION_FLASHOVER");
        (int pre, int post, float delay, int cine)[] flashes = { (3, 4, 80, 1), (5, 6, 140, 2), (7, 8, 160, 3), (9, 10, 220, 4) };
        foreach ((int pre, int post, float delay, int cine) in flashes)
            L($"\tSCR_RunDelayed(\"Flash{cine}\", {delay:0}.0);");
        L("}");
        foreach ((int pre, int post, float delay, int cine) in flashes)
        {
            // FD1 : le groupe à plus de 85 % de sa force → flash-over ; sinon il s'éteint de lui-même
            L($"void Flash{cine}()");
            L("{");
            L($"\tif(FIR_HPGetIntensity(SCR_GetVarS32(\"G_{pre}\")) >= {Math.Max(1, Count(pre)) * 40}.0)");
            L("\t{");
            L($"\t\tGroupe(SCR_GetVarS32(\"G_{post}\"), 1);");
            L($"\t\tGroupe(SCR_GetVarS32(\"G_{pre}\"), 0);");
            Cin("\t\t", $"FLASHOVER{cine}");
            L("\t}");
            L("\telse");
            L($"\t\tFIR_HPMultGrowthRate(SCR_GetVarS32(\"G_{pre}\"), 0.3);");
            L("}");
        }
        L("");
        // ---- zones de l'étage et des coffres ----
        L("// T15 : le bureau du directeur (FD1 : Cache9) ; T19 : le couloir de la salle informatique (Baboumette)");
        L("void T15(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tENT_SetVisibility(ENT_FindByName(\"Banquier\"), 1);");
        L("}");
        L("void T19(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tBaboumette();");
        L("}");
        L("// T20 : la salle du garde (FD1 : Cache6) ; T21 : la salle des coffres, les cambrioleurs se rendent (CUT_CAPTURE)");
        L("void T20(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tENT_SetVisibility(ENT_FindByName(\"Garde3\"), 1);");
        L("}");
        L("void T21(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tif(SCR_GetVarS32(\"T21Vu\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"T21Vu\", 1);");
        foreach (string b in new[] { "Bandit1", "Bandit2", "Bandit3" })
            L($"\tENT_SetVisibility(ENT_FindByName(\"{b}\"), 1);");
        Cin("\t", "CUT_CAPTURE");
        L("}");
        L("");
        // ---- acte 3 ----
        L("// acte 3 (FD1 : Scenario2 réussi) : feux de l'étage, renfort, hélicoptère 40 s plus tard, salon VIP 3 minutes après");
        L("void Banque_Acte3()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 3);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Acte2\"), 1);");
        L("\tFeuxEnHaut();");
        L("\tLEV_CallReinforcement(1);");
        // la cinématique quand les véhicules entrent dans l'avenue (le renfort part 1 s après l'appel)
        if (Cine("ARRIVEE_RENFORT").Length > 0)
            L($"\tSCR_RunDelayed(\"Cine_ARRIVEE_RENFORT\", {BanqueReinforcementCineDelay}.0);");
        L("\tSCR_RunDelayed(\"Helicoptere\", 40.0);");
        L("\tSCR_RunDelayed(\"AllumVIP\", 180.0);");
        // le comptable (FD1 : montré par la sortie de secours ou derrière Porte7) : dès l'acte 3
        L("\tENT_SetVisibility(ENT_FindByName(\"Comptable\"), 1);");
        L($"\tSCR_SetVarS32(\"Obj_Acte3\", LEV_AddObjectiveIterate(\"{T("BANQUE_DES_S3O0")}\", 4));");
        L($"\tSCR_SetVarS32(\"Obj_Bobines\", LEV_AddObjectiveIterate(\"{T("BANQUE_DES_S3O2_SEUL")}\", 6));");
        L($"\tSCR_SetVarS32(\"Obj_Coffres\", LEV_AddObjectiveIterate(\"{T("BANQUE_DES_S4O1_SEUL")}\", 4));");
        L("}");
        L("void Helicoptere()");
        L("{");
        foreach (string u in new[] { "Helicoptere", "Medic_Helico", "Pompier1_Helico", "Pompier2_Helico" })
            L($"\tENT_SetVisibility(ENT_FindByName(\"{u}\"), 1);");
        // la sortie de secours entre le toit voisin et l'étage s'ouvre (FD1 : BoutonSortieSecours)
        L("\tENT_SwitchState(ENT_FindByName(\"SortieSecours\"));");
        Cin("\t", "ARRIVEE_HELICOPTERE");
        L("}");
        L("void AllumVIP()");
        L("{");
        L("\tENT_SetVisibility(ENT_FindByName(\"GrosClient\"), 1);");
        L("\tGroupe(SCR_GetVarS32(\"G_23\"), 1);");
        Cin("\t", "DANGER_VIP");
        L("\tSCR_RunDelayed(\"Baboumette\", 210.0);");
        L("}");
        L("// la salle informatique : le feu y prend, les bandes sont en danger (FD1 : Baboumette, BaboumInfo)");
        L("void Baboumette()");
        L("{");
        L("\tif(SCR_GetVarS32(\"Babouminet\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"Babouminet\", 1);");
        L("\tGroupe(SCR_GetVarS32(\"G_17\"), 1);");
        L("\tENT_SetVisibility(ENT_FindByName(\"Informaticien\"), 1);");
        Cin("\t", "BABOUMINFOCUT");
        L("}");
        L("");
        // ---- acte 4 ----
        L("// acte 4 (FD1 : Scenario3 réussi) : le couloir des coffres et la salle du garde brûlent, la porte de la salle de");
        L("// surveillance s'ouvre ; 20 s plus tard, la salle des coffres (CUT_COFFRES)");
        L("void Banque_Acte4()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 4);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Acte3\"), 1);");
        L("\tGroupe(SCR_GetVarS32(\"G_18\"), 1);");
        L("\tGroupe(SCR_GetVarS32(\"G_16\"), 1);");
        L("\tENT_SwitchState(ENT_FindByName(\"PorteSurveillance\"));");
        Cin("\t", "COFFRE_FORT");
        L("\tSCR_RunDelayed(\"SalleCoffres\", 20.0);");
        L($"\tSCR_SetVarS32(\"Obj_Acte4\", LEV_AddObjectiveIterate(\"{T("BANQUE_DES_S4O0")}\", 4));");
        L("}");
        L("void SalleCoffres()");
        L("{");
        L("\tFIR_HPEnable(SCR_GetVarS32(\"G_25\"), 1);");
        if (groups.TryGetValue(25, out List<string>? vault) && vault.Count > 0)
            L($"\tFIR_HPSetIntensity(ENT_FindByName(\"{vault[0]}\"), 40.0);");
        L("\tENT_SetVisibility(ENT_FindByName(\"Garde3\"), 1);");
        L("\tENT_SwitchState(ENT_FindByName(\"PorteBlindeeCoffre\"));");
        Cin("\t", "CUT_COFFRES");
        L("}");
        L("");
        // ---- bandes et coffres ----
        L("// bandes informatiques : ramassées (FD1 : OnPickBobine) ou perdues dans le feu (checkBobine)");
        foreach (string b in BanqueBobines)
        {
            L($"void Pick_{b}()");
            L("{");
            L($"\tif(SCR_GetVarS32(\"Perdu_{b}\") == 1)");
            L("\t\treturn;");
            L($"\tSCR_SetVarS32(\"Sauve_{b}\", 1);");
            L("\tSCR_SetVarS32(\"nbBobines\", SCR_GetVarS32(\"nbBobines\") + 1);");
            L($"\tENT_SetVisibility(ENT_FindByName(\"{b}\"), 0);");
            Msg("\t", "EVENT_BAN_BOBINE");
            L("}");
        }
        L("// petits coffres : leur contenu ramassé (FD1 : OnPickCoffre)");
        foreach (string c in BanqueSmallSafes)
        {
            L($"void Pick_{c}()");
            L("{");
            L($"\tif(SCR_GetVarS32(\"Perdu_{c}\") == 1)");
            L("\t\treturn;");
            L($"\tSCR_SetVarS32(\"Sauve_{c}\", 1);");
            L("\tSCR_SetVarS32(\"nbCoffres\", SCR_GetVarS32(\"nbCoffres\") + 1);");
            L($"\tENT_SetVisibility(ENT_FindByName(\"{c}\"), 0);");
            Msg("\t", "EVENT_BAN_COFFRE");
            L("}");
        }
        L("// grands coffres : il faut les codes du directeur (acte 4) ; sinon le coffre reste fermé (FD1 : CoffreMecaN)");
        foreach (string c in BanqueBigSafes)
        {
            L($"void Ouvre_{c}()");
            L("{");
            L($"\tif(SCR_GetVarS32(\"Perdu_{c}\") == 1)");
            L("\t\treturn;");
            L("\tif(SCR_GetVarS32(\"Acte\") < 4)");
            L("\t{");
            Msg("\t\t", "EVENT_BAN_CODE2");
            L($"\t\tSCR_SetVarS32(\"{c}\", 0);");
            L($"\t\tENT_SetCapacityTarget(ENT_FindByName(\"{c}\"), \"CouldBeUse\", 1);");
            L("\t\treturn;");
            L("\t}");
            L($"\tSCR_SetVarS32(\"Sauve_{c}\", 1);");
            L("\tSCR_SetVarS32(\"nbCoffres\", SCR_GetVarS32(\"nbCoffres\") + 1);");
            Msg("\t", "EVENT_BAN_COFFRE");
            L("}");
        }
        // foyers sous les tuyaux des sprinklers (modèle FD1 « BAN_SPRINCKLERS » : salle des coffres, son couloir, salle du garde)
        List<string> sprinkled = new List<string>();
        for (int i = 0; i < fd1.Fires.Count; i++)
        {
            Vector3 at = fd1.Fires[i].at * WorldToCm;
            if (at.Z < 1000 && BanqueSprinklerRooms.Any(r => at.X >= r.x0 && at.X <= r.x1 && at.Y >= r.y0 && at.Y <= r.y1))
                sprinkled.Add($"foyer_{i + 1}");
        }
        Report.Add($"script : {sprinkled.Count} foyers sous les sprinklers");
        L("// l'armoire des sprinklers : les trois pièces sous les tuyaux sont arrosées sans fin (FD1 : DeclencheSprinkler) ;");
        L($"// puissance : le quart de celle d'un pompier");
        L("void Sprinklers()");
        L("{");
        L("\ts32 g;");
        Msg("\t", "EVENT_BAN_SPRINKLER");
        Cin("\t", "CUT_SPRINKLERS");
        L("\tg = ENT_GroupCreate(\"groupe_sprinklers\");");
        foreach (string h in sprinkled)
            L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
        L($"\tFIR_HPSetSprinkler(g, {BanqueSprinklerPower.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture)});");
        L("}");
        // une bande ou un coffre est perdu quand son foyer a brûlé fort assez longtemps (en tout, en secondes : BanqueBurnLimits) ;
        // les grands coffres préviennent dès que leur foyer prend
        L("// toutes les secondes : bandes et coffres que le feu détruit (temps passé dans un feu fort, en secondes)");
        L("void Banque_Pertes()");
        L("{");
        L("\tSCR_RunDelayed(\"Banque_Pertes\", 1.0);");
        L("\tif(SCR_GetVarS32(\"Fin\") != 0)");
        L("\t\treturn;");
        foreach (string o in BanqueBobines.Concat(BanqueSmallSafes).Concat(BanqueBigSafes))
        {
            if (!near.TryGetValue(o, out string? fire))
                continue;
            bool bobine = BanqueBobines.Contains(o);
            bool big = BanqueBigSafes.Contains(o);
            if (big)
            {
                int k = Array.IndexOf(BanqueBigSafes, o) + 1;
                L($"\tif(SCR_GetVarS32(\"Alerte_{o}\") == 0 && SCR_GetVarS32(\"Sauve_{o}\") == 0 && FIR_HPGetIntensity(ENT_FindByName(\"{fire}\")) > 20.0)");
                L("\t{");
                L($"\t\tSCR_SetVarS32(\"Alerte_{o}\", 1);");
                Cin("\t\t", $"DANGER_COFFRE{k}");
                L("\t}");
            }
            int limit = bobine ? BanqueBurnLimits.bobine : big ? BanqueBurnLimits.bigSafe : BanqueBurnLimits.smallSafe;
            L($"\tif(SCR_GetVarS32(\"Perdu_{o}\") == 0 && SCR_GetVarS32(\"Sauve_{o}\") == 0 && FIR_HPGetIntensity(ENT_FindByName(\"{fire}\")) > {BanqueBurnLimits.strong}.0)");
            L($"\t\tSCR_SetVarS32(\"Brule_{o}\", SCR_GetVarS32(\"Brule_{o}\") + 1);");
            L($"\tif(SCR_GetVarS32(\"Perdu_{o}\") == 0 && SCR_GetVarS32(\"Sauve_{o}\") == 0 && SCR_GetVarS32(\"Brule_{o}\") >= {limit})");
            L("\t{");
            L($"\t\tSCR_SetVarS32(\"Perdu_{o}\", 1);");
            L($"\t\tENT_SetCapacityTarget(ENT_FindByName(\"{o}\"), \"CouldBeUse\", 0);");
            if (bobine)
            {
                L("\t\tSCR_SetVarS32(\"nbBobinesPerdues\", SCR_GetVarS32(\"nbBobinesPerdues\") + 1);");
                Msg("\t\t", "EVENT_BAN_PERTE_BOBINE");
            }
            else
            {
                L("\t\tSCR_SetVarS32(\"nbCoffresPerdus\", SCR_GetVarS32(\"nbCoffresPerdus\") + 1);");
                if (big)
                    Cin("\t\t", $"DETRUIT_COFFRE{Array.IndexOf(BanqueBigSafes, o) + 1}");
            }
            L("\t}");
        }
        L("}");
        L("");
        // ---- victimes ----
        L("// civil mis à l'abri (rappel du moteur)");
        L("void OnCivilianSaved(EntityID Civil)");
        L("{");
        foreach (string c in BanqueVictims.SelectMany(v => v))
        {
            L($"\tif(Civil == ENT_FindByName(\"{c}\"))");
            L($"\t\tSCR_SetVarS32(\"Sauve_{c}\", 1);");
        }
        L("\tif(Civil == ENT_FindByName(\"Banquier\"))");
        Msg("\t\t", "EVENT_BAN_BANQUIER");
        L("}");
        L("");
        L("// victimes découvertes derrière les portes cassées (FD1 : CheckGlobalCache, caches 9, 11 et 14)");
        L("void Banque_Caches()");
        L("{");
        foreach ((string door, string civil) in new[] { ("Porte5", "Banquier"), ("Porte7", "Comptable"), ("Porte8", "GrosClient"), ("Porte1", "Employe1"), ("Porte3", "Employe2") })
        {
            L($"\tif(SCR_GetVarS32(\"Vu_{civil}\") == 0 && ENT_GetState(ENT_FindByName(\"{door}\")) == \"Broken\")");
            L("\t{");
            L($"\t\tSCR_SetVarS32(\"Vu_{civil}\", 1);");
            L($"\t\tENT_SetVisibility(ENT_FindByName(\"{civil}\"), 1);");
            L("\t}");
        }
        L("}");
        L("");
        L("// défaites (FD1 : Banque_CheckDefeat) : texte du bilan, puis l'échec 5 s plus tard");
        L("void Banque_Defaites()");
        L("{");
        foreach ((string test, string key) in new[]
        {
            ("LEV_IsHealVehicleRemaining() == 0", "DEFEAT_MM"),
            ("ENT_GetDamage(ENT_FindByName(\"Banquier\")) <= 0.0", "DEFEAT_BANQUIER"),
            ("SCR_GetVarS32(\"nbBobinesPerdues\") >= 6", "DEFEAT_BOBINES"),
            ("SCR_GetVarS32(\"nbCoffresPerdus\") >= 6", "DEFEAT_COFFRES"),
        })
        {
            L($"\tif({test})");
            L("\t{");
            L("\t\tSCR_SetVarS32(\"Fin\", 2);");
            L($"\t\tLEV_SetDebriefText(\"{T(key)}\");");
            L("\t\tSCR_RunDelayed(\"Banque_Defaite\", 5.0);");
            L("\t\treturn;");
            L("\t}");
        }
        L("}");
        L("void Banque_Defaite()");
        L("{");
        L("\tLEV_TriggerDefeat();");
        L("}");
        L("");
        L("// victoire (FD1 : Victory_OnTimer) : texte selon les coffres (C) et les bandes (B) sauvés : aucun, moins de 4, 4 et plus");
        L("void Banque_Victoire()");
        L("{");
        L("\ts32 c;");
        L("\ts32 b;");
        L("\tc = SCR_GetVarS32(\"nbCoffres\");");
        L("\tb = SCR_GetVarS32(\"nbBobines\");");
        L("\tif(c > 0)");
        L("\t{");
        L("\t\tif(c < 4)");
        L("\t\t\tc = 1;");
        L("\t\telse");
        L("\t\t\tc = 2;");
        L("\t}");
        L("\tif(b > 0)");
        L("\t{");
        L("\t\tif(b < 4)");
        L("\t\t\tb = 1;");
        L("\t\telse");
        L("\t\t\tb = 2;");
        L("\t}");
        for (int c = 0; c <= 2; c++)
            for (int b = 0; b <= 2; b++)
            {
                L($"\tif(c == {c} && b == {b})");
                L($"\t\tLEV_SetDebriefText(\"{T($"VICTORYC{c}B{b}")}\");");
            }
        L("\tLEV_TriggerVictory();");
        L("}");
        L("");
        L("// boucle : victimes de l'acte en cours, bandes et coffres, défaites");
        L("void Banque_Main()");
        L("{");
        L("\ts32 n;");
        L("\tif(SCR_GetVarS32(\"Fin\") != 0)");
        L("\t\treturn;");
        L("\tBanque_Caches();");
        L("\tBanque_Defaites();");
        L("\tif(SCR_GetVarS32(\"Fin\") != 0)");
        L("\t\treturn;");
        L("\tif(SCR_GetVarS32(\"Acte\") >= 3)");
        L("\t{");
        L("\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Bobines\"), SCR_GetVarS32(\"nbBobines\"));");
        L("\t\tif(SCR_GetVarS32(\"nbBobines\") >= 6)");
        L("\t\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Bobines\"), 1);");
        L("\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Coffres\"), SCR_GetVarS32(\"nbCoffres\"));");
        L("\t\tif(SCR_GetVarS32(\"nbCoffres\") >= 4)");
        L("\t\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Coffres\"), 1);");
        L("\t}");
        for (int act = 2; act <= 4; act++)
        {
            string[] victims = BanqueVictims[act - 2];
            L($"\tif(SCR_GetVarS32(\"Acte\") == {act})");
            L("\t{");
            L("\t\tn = " + string.Join(" + ", victims.Select(v => $"SCR_GetVarS32(\"Sauve_{v}\")")) + ";");
            L($"\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Acte{act}\"), n);");
            L($"\t\tif(n >= {victims.Length})");
            if (act == 2)
                L("\t\t\tBanque_Acte3();");
            else if (act == 3)
                L("\t\t\tBanque_Acte4();");
            else
            {
                L("\t\t{");
                L("\t\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Acte4\"), 1);");
                L("\t\t\tSCR_SetVarS32(\"Fin\", 1);");
                L("\t\t\tSCR_RunDelayed(\"Banque_Victoire\", 3.0);");
                L("\t\t}");
            }
            L("\t}");
        }
        L("}");
        L("");
        return (f.ToString(), "\tBanque_Init();\r\n", "\tBanque_Main();\r\n");
    }
}
