using System.Numerics;
using System.Text;

namespace OpenFD.ConvFD1;

/// <summary>
/// Station-service (scripts FD1 Station_Service_Main / _Scenar / _Event / _Trigger) : « 3 millions de dollars ».
///  • acte 1 : le camion-citerne de GPL brûle ; l'ingénieur le conduit jusqu'à l'unité d'arrosage à mousse, qui l'arrose
///    5 s ; les pompes brûlent tant que la vanne de la supérette n'est pas fermée ;
///  • acte 2 : la fuite de propane a enflammé la supérette ; sauver la caissière, le patron, le client, et les trois
///    victimes de l'accident (le policier, le chauffeur, l'otage) ; la citerne de butane peut exploser ;
///  • acte 3 : le chantier (maison en réfection) prend feu ; ses planches et ses portes cachent trois ouvriers, une porte
///    donne sur un backdraft ; une réserve de peinture ;
///  • acte 4 : une voiture accidentée embrase l'immeuble ouest ; l'échelle arrive en renfort ; sauver les habitants de
///    l'étage, Johnston (le truand) et son sac de 3 millions ; trois portes donnent sur un backdraft ;
///  • victoire : texte selon le sac ; défaites : le camion-citerne détruit, l'ambulance détruite, le sac brûlé.
/// Les groupes de foyers FD1 ne sont pas lisibles dans le niveau : ils sont reconstitués par zone (StationGroupOf).
/// </summary>
public sealed partial class MissionConverter
{
    /// <summary>Nom du camion-citerne à conduire (l'unité ; « Citerne » est l'objet du décor qu'il remplace).</summary>
    private const string StationTruckUnit = "CiterneGPL";

    /// <summary>Groupes de foyers sans numéro FD1 : la fuite de propane de la supérette, les sprinklers de l'unité d'arrosage.</summary>
    private const int StationLeak = 20;
    private const int StationSprinklers = 21;

    /// <summary>Le tuyau de la fuite de propane (« JetFlammes », cm) : les foyers à moins de 5 m sont ceux de la fuite.</summary>
    private static readonly Vector2 StationPipe = new Vector2(12126, 12873);

    /// <summary>Les pompes (cm ; la première : celle que la voiture de police a arrachée) : leurs foyers à moins de 3,5 m.</summary>
    private static readonly Vector2[] StationPumps =
    {
        new Vector2(11320, 11130), new Vector2(11320, 9870), new Vector2(12690, 11130), new Vector2(12690, 9870), new Vector2(14120, 11130),
        new Vector2(14070, 9870),
    };

    /// <summary>Les deux voitures encastrées (cm : x min, x max, y min, y max) : leurs foyers brûlent au départ.</summary>
    private static readonly (float x0, float x1, float y0, float y1)[] StationWrecks =
    {
        (10625, 11237, 11100, 12692), (11712, 12214, 12284, 12900),
    };

    /// <summary>La voiture qui embrase l'immeuble (acte 4, cm) : les foyers à moins de 10 m prennent.</summary>
    private static readonly Vector2 StationCar = new Vector2(7108, 11026);

    /// <summary>Le sac des 3 millions (cm, à l'étage de l'immeuble) : les foyers à moins de 2 m le brûlent.</summary>
    private static readonly Vector2 StationBag = new Vector2(3706, 11461);

    /// <summary>Période de la boucle du script (s : Session.MainLoopPeriod) ; les compteurs de temps du script en dépendent.</summary>
    private const float StationLoopPeriod = 0.2f;

    /// <summary>Temps (s) que le sac tient dans le feu avant d'être perdu.</summary>
    private const int StationBagSeconds = 60;

    /// <summary>
    /// Temps (s) que le camion-citerne tient en feu avant d'exploser (ses foyers sont sur sa citerne, trop haut pour que le
    /// moteur l'abîme) : le script compte le temps où ils brûlent.
    /// </summary>
    private const int StationTruckSeconds = 240;

    /// <summary>Victimes de chaque acte (FD1 : CheckScenario2, 3, 4) ; Voleur : Johnston.</summary>
    private static readonly string[] StationAct2Victims = { "Client", "Policier", "Citernedriver", "Otage1", "Caissiere", "Patron" };
    private static readonly string[] StationAct3Victims = { "Ouvrier0", "Ouvrier1", "Ouvrier3" };
    private static readonly string[] StationAct4Victims = { "Hab1", "Hab2", "Hab3", "Hab4", "Poligote" };

    /// <summary>Groupes de la station qui attendent le script (FD1 : Init_HPScenar2 à 4).</summary>
    private static readonly int[] StationWaitingGroups = { 2, 3, 4, 5, 6, 7, StationLeak, StationSprinklers };

    /// <summary>
    /// Groupe FD1 de ce foyer de la station (−1 : aucun). 0 : le camion-citerne ; 1 : les pompes ; 2 : la supérette ; 3 : le
    /// chantier ; 4 : l'immeuble ouest, côté de la voiture (et son rez-de-chaussée) ; 5 : la pièce du backdraft de l'immeuble ;
    /// 7 : le reste de l'étage de l'immeuble ; 6 : la rue au nord (la citerne de butane) ; 8 : la cour de la station.
    /// </summary>
    private int StationGroupOf(Vector3 at, string type)
    {
        if (!Mission.Equals("station_service", StringComparison.OrdinalIgnoreCase))
            return -1;
        if (type.StartsWith("sprinklers", StringComparison.OrdinalIgnoreCase) || type.StartsWith("fumee", StringComparison.OrdinalIgnoreCase))
            return StationSprinklers;
        Vector2 p = new Vector2(at.X, at.Y);
        if (type.StartsWith("feux_type_v", StringComparison.OrdinalIgnoreCase) && Vector2.Distance(p, StationPipe) < 500)
            return StationLeak;
        if (In(p, 15358, 16545, 10649, 12403))
            return 0;
        if (at.Z < 100 && StationPumps.Any(q => Vector2.Distance(p, q) < 350))
            return 1;
        if (In(p, 9900, 14600, 12700, 15700))
            return 2;
        if (In(p, 16500, 20150, 9500, 14650))
            return 3;
        if (In(p, 3300, 7200, 9500, 16000))
        {
            if (at.Z < 1000)
                return 4;
            if (In(p, 5420, 6930, 12720, 14740))
                return 5;
            return p.X >= 5400 && p.Y < 12720 ? 4 : 7;
        }
        if (In(p, 7400, 10600, 15200, 17800))
            return 6;
        if (In(p, 9000, 16600, 8800, 12700))
            return 8;
        return -1;
    }

    private static bool In(Vector2 p, float x0, float x1, float y0, float y1) => p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1;

    /// <summary>Foyers allumés au départ de la station : le camion-citerne, les pompes, les deux voitures encastrées.</summary>
    private bool StationLitAtStart(Vector3 at, string type)
    {
        int g = StationGroupOf(at, type);
        if (g == 0)
            return Ignites(type);
        if (g == 1)
            return true;
        if (g == 8)
            return StationWrecks.Any(w => at.X >= w.x0 - 150 && at.X <= w.x1 + 150 && at.Y >= w.y0 - 150 && at.Y <= w.y1 + 150);
        return false;
    }

    /// <summary>Ce foyer prend-il quand le script allume son groupe (A_n) ?</summary>
    private static bool StationIgnites(int g, Vector3 at, string type, List<Vector2> paint)
    {
        if (!Ignites(type))
            return false;
        Vector2 p = new Vector2(at.X, at.Y);
        return g switch
        {
            // la fuite a enflammé le magasin : autour du tuyau
            2 => Vector2.Distance(p, StationPipe) < 700,
            // « un foyer secondaire » : la réserve de peinture
            3 => paint.Any(q => Vector2.Distance(p, q) < 700),
            // la voiture contre la façade
            4 => Vector2.Distance(p, StationCar) < 1000,
            5 or 6 => true,
            _ => false,
        };
    }

    /// <summary>Script de la station-service (fonctions, départ, boucle).</summary>
    private (string functions, string start, string main) StationScript(Fd1Level fd1)
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
        void Roof(string indent, string instance)
        {
            foreach (string line in RoofLines(instance, 0))
                L(indent + line);
        }
        void Show(string indent, params string[] names)
        {
            foreach (string n in names)
                L($"{indent}Montre(\"{n}\");");
        }
        void Lost(string indent, string key)
        {
            L($"{indent}SCR_SetVarS32(\"Fin\", 2);");
            L($"{indent}LEV_SetDebriefText(\"{T(key)}\");");
            L($"{indent}SCR_RunDelayed(\"Station_Defaite\", 5.0);");
            L($"{indent}return;");
        }
        // la réserve de peinture : les torches du chantier
        List<Vector2> paint = new List<Vector2>();
        for (int i = 0; i < fd1.Fires.Count; i++)
        {
            Vector3 at = fd1.Fires[i].at * WorldToCm;
            if (StationGroupOf(at, fd1.Fires[i].type) == 3 && fd1.Fires[i].type.StartsWith("torche", StringComparison.OrdinalIgnoreCase))
                paint.Add(new Vector2(at.X, at.Y));
        }
        // groupes : tous les foyers (G_n), ceux qui prennent quand le script allume le groupe (A_n), la réserve de peinture,
        // les foyers autour du sac
        Dictionary<int, List<string>> all = new Dictionary<int, List<string>>();
        Dictionary<int, List<string>> ignite = new Dictionary<int, List<string>>();
        List<string> paintFires = new List<string>();
        List<string> bagFires = new List<string>();
        for (int i = 0; i < fd1.Fires.Count; i++)
        {
            Vector3 at = fd1.Fires[i].at * WorldToCm;
            string type = fd1.Fires[i].type;
            string h = $"foyer_{i + 1}";
            if (at.Z > 1000 && Vector2.Distance(new Vector2(at.X, at.Y), StationBag) < 200)
                bagFires.Add(h);
            int g = StationGroupOf(at, type);
            if (g < 0)
                continue;
            if (g == 3 && type.StartsWith("torche", StringComparison.OrdinalIgnoreCase))
                paintFires.Add(h);
            if (!all.TryGetValue(g, out List<string>? list))
                all[g] = list = new List<string>();
            list.Add(h);
            if (!StationIgnites(g, at, type, paint))
                continue;
            if (!ignite.TryGetValue(g, out List<string>? il))
                ignite[g] = il = new List<string>();
            il.Add(h);
        }
        Report.Add($"script : groupes de foyers {string.Join(", ", all.OrderBy(g => g.Key).Select(g => $"{g.Key} ({g.Value.Count}, {(ignite.TryGetValue(g.Key, out List<string>? il) ? il.Count : 0)} allumés par le script)"))}, sac {bagFires.Count}, peinture {paintFires.Count}");
        string Saved(IEnumerable<string> names) => string.Join(" + ", names.Select(n => $"SCR_GetVarS32(\"Sauve_{n}\")"));

        L("// ===== Station-service : déroulement (traduit des scripts FD1) =====");
        L("");
        L("// un groupe de foyers FD1 prend feu (SetHotpointGroupActivated(n, 1)) : il peut brûler, les foyers de a prennent");
        L("void Allume(s32 g, s32 a)");
        L("{");
        L("\tif(g != 0)");
        L("\t\tFIR_HPEnable(g, 1);");
        L("\tif(a != 0)");
        L($"\t\tFIR_HPSetIntensity(a, {BanqueGroupStart}.0);");
        L("}");
        L("// un groupe s'éteint et ne peut plus brûler (SetHotpointGroupActivated(n, 0))");
        L("void Eteint(s32 g)");
        L("{");
        L("\tif(g == 0)");
        L("\t\treturn;");
        L("\tFIR_HPSetIntensity(g, 0.0);");
        L("\tFIR_HPEnable(g, 0);");
        L("}");
        L("// intensité d'un groupe (0 s'il n'a aucun foyer)");
        L("f32 Feu(s32 g)");
        L("{");
        L("\tif(g == 0)");
        L("\t\treturn 0.0;");
        L("\treturn FIR_HPGetIntensity(g);");
        L("}");
        L("// une victime paraît (une seule fois : une victime sauvée ne revient pas)");
        L("void Montre(mcString nom)");
        L("{");
        L("\tif(SCR_GetVarS32(\"Vu_\" + nom) == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"Vu_\" + nom, 1);");
        L("\tENT_SetVisibility(ENT_FindByName(nom), 1);");
        L("}");
        L("");
        L("void Station_Init()");
        L("{");
        L("\ts32 g;");
        L("\tSCR_SetVarS32(\"Acte\", 1);");
        foreach ((int g, List<string> fires) in all.OrderBy(x => x.Key))
        {
            L($"\tg = ENT_GroupCreate(\"groupe_{g}\");");
            foreach (string h in fires)
                L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
            L($"\tSCR_SetVarS32(\"G_{g}\", g);");
            if (StationWaitingGroups.Contains(g))
                L("\tFIR_HPEnable(g, 0);");
            if (!ignite.TryGetValue(g, out List<string>? il))
                continue;
            L($"\tg = ENT_GroupCreate(\"allume_{g}\");");
            foreach (string h in il)
                L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
            L($"\tSCR_SetVarS32(\"A_{g}\", g);");
        }
        foreach ((string name, List<string> fires) in new[] { ("Peinture", paintFires), ("Sac", bagFires) })
        {
            if (fires.Count == 0)
                continue;
            L($"\tg = ENT_GroupCreate(\"feux_{name.ToLowerInvariant()}\");");
            foreach (string h in fires)
                L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
            L($"\tSCR_SetVarS32(\"G_{name}\", g);");
        }
        Show("\t", "Policier", "Citernedriver", "Otage1");
        Cin("\t", "CUT_ARRIVEE");
        L($"\tSCR_SetVarS32(\"Obj_Acte1\", LEV_AddObjectiveBoolean(\"{T("STATION_DES_S1O0")}\"));");
        L("}");
        L("");
        // ---- acte 1 : le camion-citerne ----
        L("// l'ingénieur prend le camion-citerne (il le « répare ») : le camion du décor laisse la place au véhicule à conduire ;");
        L("// le feu du camion part avec lui");
        L("void Use_Citerne(EntityID User, EntityID Bouton, mcString State)");
        L("{");
        L("\tif(SCR_GetVarS32(\"Conduite\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"Conduite\", 1);");
        L("\tSCR_SetVarS32(\"Arrosage\", 1);");
        L("\tEteint(SCR_GetVarS32(\"G_0\"));");
        L("\tENT_SetVisibility(ENT_FindByName(\"Citerne\"), 0);");
        L($"\tENT_SetVisibility(ENT_FindByName(\"{StationTruckUnit}\"), 1);");
        L("}");
        L("// le camion-citerne entre dans l'unité d'arrosage à mousse (FD1 : Trigger_truckrescue) : 5 s d'arrosage");
        L("void TriggerTruck(EntityID Unit, EntityID Zone)");
        L("{");
        L($"\tif(Unit != ENT_FindByName(\"{StationTruckUnit}\") || SCR_GetVarS32(\"Arrose\") != 0)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"Arrose\", 1);");
        L($"\tif(SCR_GetVarS32(\"G_{StationSprinklers}\") != 0)");
        L($"\t\tFIR_HPSetSprinkler(SCR_GetVarS32(\"G_{StationSprinklers}\"), 10.0);");
        L("\tSCR_RunDelayed(\"ApplySprinkler\", 5.0);");
        L("}");
        L("// fin de l'arrosage (FD1 : ApplySprinkler) : le camion est hors de danger");
        L("void ApplySprinkler()");
        L("{");
        L($"\tif(SCR_GetVarS32(\"G_{StationSprinklers}\") != 0)");
        L($"\t\tFIR_HPSetSprinkler(SCR_GetVarS32(\"G_{StationSprinklers}\"), 0.0);");
        L("\tEteint(SCR_GetVarS32(\"G_0\"));");
        L("\tSCR_SetVarS32(\"CamionSauve\", 1);");
        L("\tif(SCR_GetVarS32(\"Acte\") == 1)");
        L("\t\tStation_Acte2();");
        L("}");
        L("// la vanne de la supérette (FD1 : ValveEssence) : les pompes et la fuite de propane s'éteignent");
        L("void ValveEssence()");
        L("{");
        L("\tif(SCR_GetVarS32(\"ValveDone\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"ValveDone\", 1);");
        L("\tEteint(SCR_GetVarS32(\"G_1\"));");
        L($"\tEteint(SCR_GetVarS32(\"G_{StationLeak}\"));");
        Cin("\t", "VALVE");
        L("}");
        L("// la fuite de propane se rallume toutes les 10 s tant que la vanne est ouverte (FD1 : JetFlamme)");
        L("void JetFlamme()");
        L("{");
        L("\tif(SCR_GetVarS32(\"ValveDone\") == 1)");
        L("\t\treturn;");
        L($"\tAllume(SCR_GetVarS32(\"G_{StationLeak}\"), SCR_GetVarS32(\"G_{StationLeak}\"));");
        L("\tSCR_RunDelayed(\"JetFlamme\", 10.0);");
        L("}");
        L("");
        // ---- actes ----
        L("// acte 2 (FD1 : Scenario1 réussi) : la supérette prend feu");
        L("void Station_Acte2()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 2);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Acte1\"), 1);");
        Show("\t", "Caissiere", "Patron", "Client");
        L("\tAllume(SCR_GetVarS32(\"G_2\"), SCR_GetVarS32(\"A_2\"));");
        L("\tJetFlamme();");
        Cin("\t", "SUPERETTE");
        Roof("\t", "Scenar_Toit2");
        L($"\tSCR_SetVarS32(\"Obj_Acte2\", LEV_AddObjectiveIterate(\"{T("STATION_DES_S2O0")}\", {StationAct2Victims.Length}));");
        L("}");
        L("// acte 3 (FD1 : Scenario2 réussi) : le chantier prend feu");
        L("void Station_Acte3()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 3);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Acte2\"), 1);");
        L("\tAllume(SCR_GetVarS32(\"G_3\"), SCR_GetVarS32(\"A_3\"));");
        Cin("\t", "REFECTION");
        Roof("\t", "Scenar_Toit3");
        L($"\tSCR_SetVarS32(\"Obj_Acte3\", LEV_AddObjectiveIterate(\"{T("STATION_DES_S3O0")}\", {StationAct3Victims.Length}));");
        L("}");
        L("// acte 4 (FD1 : Scenario3 réussi) : la cour de la station s'éteint d'elle-même, une voiture embrase l'immeuble ouest");
        L("void Station_Acte4()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 4);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Acte3\"), 1);");
        L("\tif(SCR_GetVarS32(\"G_8\") != 0)");
        L("\t\tFIR_HPSetGrowthRate(SCR_GetVarS32(\"G_8\"), -2.0);");
        L("\tSCR_RunDelayed(\"ExplVoiture\", 1.0);");
        L("\tSCR_RunDelayed(\"EtageCut\", 2.0);");
        Show("\t", "Hab2");
        L("\tAllume(SCR_GetVarS32(\"G_7\"), 0);");
        Roof("\t", "Scenar_Toit4");
        L($"\tSCR_SetVarS32(\"Obj_Habitants\", LEV_AddObjectiveIterate(\"{T("STATION_DES_S4O0")}\", {StationAct4Victims.Length}));");
        L($"\tSCR_SetVarS32(\"Obj_Voleur\", LEV_AddObjectiveBoolean(\"{T("STATION_DES_S4O1")}\"));");
        L($"\tSCR_SetVarS32(\"Obj_Sac\", LEV_AddObjectiveBoolean(\"{T("STATION_DES_S4O2")}\"));");
        L("\tif(SCR_GetVarS32(\"SacOK\") == 1)");
        L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Sac\"), 1);");
        L("\tif(SCR_GetVarS32(\"Sauve_Voleur\") == 1)");
        L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Voleur\"), 1);");
        L("}");
        L("void ExplVoiture()");
        L("{");
        L("\tENT_SetDamage(ENT_FindByName(\"VoitActe4\"), 0.0);");
        L("\tAllume(SCR_GetVarS32(\"G_4\"), SCR_GetVarS32(\"A_4\"));");
        L("}");
        L("// la cinématique de l'immeuble, puis l'échelle et le bras élévateur arrivent (FD1 : EtageCut, ArriveeMega)");
        L("void EtageCut()");
        L("{");
        Cin("\t", "ETAGE");
        L("\tSCR_RunDelayed(\"Renfort\", 9.0);");
        L("}");
        L("void Renfort()");
        L("{");
        L("\tLEV_CallReinforcement(1);");
        L("}");
        L("");
        // ---- portes et zones ----
        L("s32 Cassee(mcString porte)");
        L("{");
        L("\tif(ENT_GetState(ENT_FindByName(porte)) == \"Broken\")");
        L("\t\treturn 1;");
        L("\treturn 0;");
        L("}");
        L("void Back1Cut()");
        L("{");
        Cin("\t", "BACK1");
        L("}");
        L("void Back2Cut()");
        L("{");
        Cin("\t", "BACK2");
        L("}");
        L("// portes cassées (à partir de l'acte 3, FD1 : CheckGlobalCache) : victimes découvertes, backdrafts");
        L("void Station_Portes()");
        L("{");
        // chantier : les planches 6 découvrent Ouvrier1 (le backdraft de la porte 4 n'a plus lieu) ; la porte 4 d'abord : le
        // backdraft
        L("\tif(SCR_GetVarS32(\"Cache1\") == 0 && Cassee(\"Porte6\") == 1)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"Cache1\", 1);");
        Show("\t\t", "Ouvrier1");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Cache1\") == 0 && Cassee(\"Porte4\") == 1)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"Cache1\", 1);");
        L("\t\tENT_SetDamage(ENT_FindByName(\"Back1\"), 0.0);");
        L("\t\tSCR_RunDelayed(\"Back1Cut\", 1.0);");
        Show("\t\t", "Ouvrier1");
        L("\t}");
        L("\tif(Cassee(\"Porte3\") == 1)");
        Show("\t\t", "Ouvrier3");
        L("\tif(Cassee(\"Porte5\") == 1 || Cassee(\"Porte7\") == 1 || Cassee(\"Porte8\") == 1)");
        Show("\t\t", "Ouvrier0");
        // immeuble
        L("\tif(Cassee(\"Porte9\") == 1)");
        L("\t{");
        Show("\t\t", "Hab3", "Hab4");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Cache14\") == 0 && (Cassee(\"Porte12\") == 1 || Cassee(\"Porte13\") == 1 || Cassee(\"Porte14\") == 1))");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"Cache14\", 1);");
        L("\t\tAllume(SCR_GetVarS32(\"G_5\"), SCR_GetVarS32(\"A_5\"));");
        L("\t\tENT_SetDamage(ENT_FindByName(\"Back2\"), 0.0);");
        L("\t\tENT_SetDamage(ENT_FindByName(\"Back3\"), 0.0);");
        L("\t\tENT_SetDamage(ENT_FindByName(\"Back4\"), 0.0);");
        L("\t\tSCR_RunDelayed(\"Back2Cut\", 1.0);");
        L("\t}");
        L("\tif(Cassee(\"Porte13\") == 1 || Cassee(\"Porte14\") == 1)");
        Show("\t\t", "Hab1");
        L("\tif(Cassee(\"Porte15\") == 1)");
        L("\t{");
        Show("\t\t", "Poligote", "Voleur");
        L("\t}");
        L("}");
        L("// zones FD1 qui découvrent une pièce (TCache3, 5, 11, 17, 19) ; la réserve de peinture (TCache4)");
        foreach ((string trigger, string who) in new[] { ("TCache3", "Ouvrier3"), ("TCache5", "Ouvrier0"), ("TCache11", "Hab1"), ("TCache17", "Poligote"), ("TCache19", "Voleur") })
        {
            L($"void {trigger}(EntityID Unit, EntityID Zone)");
            L("{");
            L(who.StartsWith("Ouvrier", StringComparison.Ordinal) ? "\tif(SCR_GetVarS32(\"Acte\") < 3)" : "\tif(SCR_GetVarS32(\"Acte\") < 4)");
            L("\t\treturn;");
            Show("\t", who);
            L("}");
        }
        L("void TCache4(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tif(SCR_GetVarS32(\"PeintureVue\") == 1 || Feu(SCR_GetVarS32(\"G_Peinture\")) <= 1.0)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"PeintureVue\", 1);");
        Cin("\t", "PEINTURE");
        L("}");
        L("");
        // ---- objets ----
        L("// la citerne de butane détruite par le feu (FD1 : CheckBombonne) : deux policiers soufflés, la rue prend feu");
        L("void BombonneExplosee(EntityID Bombonne)");
        L("{");
        L("\tSCR_SetVarS32(\"BombonneExp\", 1);");
        Show("\t", "PolicierEvanoui", "PolicierEvanoui2");
        L("\tAllume(SCR_GetVarS32(\"G_6\"), SCR_GetVarS32(\"A_6\"));");
        Cin("\t", "BOMBONNE");
        L("}");
        L("// le sac des 3 millions (FD1 : OnPickMe)");
        L("void Pick_Sac()");
        L("{");
        L("\tif(SCR_GetVarS32(\"SacOK\") == 1 || SCR_GetVarS32(\"SacBrule\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"SacOK\", 1);");
        L("\tENT_SetVisibility(ENT_FindByName(\"Sac\"), 0);");
        Msg("\t", "EVENT_STA_BUTIN");
        L("\tif(SCR_GetVarS32(\"Acte\") == 4)");
        L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Sac\"), 1);");
        L("}");
        L("// civil mis à l'abri (rappel du moteur)");
        L("void OnCivilianSaved(EntityID Civil)");
        L("{");
        foreach (string c in StationAct2Victims.Concat(StationAct3Victims).Concat(StationAct4Victims).Append("Voleur"))
        {
            L($"\tif(Civil == ENT_FindByName(\"{c}\"))");
            L($"\t\tSCR_SetVarS32(\"Sauve_{c}\", 1);");
        }
        L("\tif(Civil == ENT_FindByName(\"Voleur\"))");
        L("\t{");
        Msg("\t\t", "EVENT_STA_GANGSTER");
        L("\t\tif(SCR_GetVarS32(\"Acte\") == 4)");
        L("\t\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Voleur\"), 1);");
        L("\t}");
        L("}");
        L("void Station_Defaite()");
        L("{");
        L("\tLEV_TriggerDefeat();");
        L("}");
        L("// victoire (FD1 : Victory_OnTimer) : le sac sauvé ou non");
        L("void Station_Victoire()");
        L("{");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Habitants\"), 1);");
        L("\tif(SCR_GetVarS32(\"SacOK\") == 1)");
        L($"\t\tLEV_SetDebriefText(\"{T("VICTORY1")}\");");
        L("\telse");
        L($"\t\tLEV_SetDebriefText(\"{T("VICTORY2")}\");");
        L("\tLEV_TriggerVictory();");
        L("}");
        L("");
        L("// boucle : défaites, citerne de butane, sac, portes, victimes de l'acte en cours");
        L("void Station_Main()");
        L("{");
        L("\ts32 n;");
        L("\tif(SCR_GetVarS32(\"Fin\") != 0)");
        L("\t\treturn;");
        L("\tif(LEV_IsHealVehicleRemaining() == 0)");
        L("\t{");
        Lost("\t\t", "DEFEAT4");
        L("\t}");
        // le camion-citerne détruit : l'objet du décor avant que l'ingénieur le prenne, le véhicule ensuite (état 4)
        L("\tif(SCR_GetVarS32(\"Conduite\") == 0 && ENT_GetDamage(ENT_FindByName(\"Citerne\")) <= 0.0)");
        L("\t{");
        Lost("\t\t", "DEFEAT1");
        L("\t}");
        L($"\tif(SCR_GetVarS32(\"Conduite\") == 1 && SCR_GetVarS32(\"CamionSauve\") == 0 && ENT_GetUnitState(ENT_FindByName(\"{StationTruckUnit}\")) == 4)");
        L("\t{");
        Lost("\t\t", "DEFEAT1");
        L("\t}");
        // le camion-citerne brûle depuis trop longtemps (FD1 : ses points de vie à 0)
        L("\tif(SCR_GetVarS32(\"Conduite\") == 0 && Feu(SCR_GetVarS32(\"G_0\")) > 0.0)");
        L("\t\tSCR_SetVarS32(\"CiterneChaleur\", SCR_GetVarS32(\"CiterneChaleur\") + 1);");
        L($"\tif(SCR_GetVarS32(\"CiterneChaleur\") >= {(int)(StationTruckSeconds / StationLoopPeriod)})");
        L("\t{");
        L("\t\tENT_SetDamage(ENT_FindByName(\"Citerne\"), 0.0);");
        Lost("\t\t", "DEFEAT1");
        L("\t}");
        // le sac brûle s'il reste une minute dans le feu (FD1 : CheckSacPognon)
        L("\tif(SCR_GetVarS32(\"SacOK\") == 0 && Feu(SCR_GetVarS32(\"G_Sac\")) > 0.0)");
        L("\t\tSCR_SetVarS32(\"SacChaleur\", SCR_GetVarS32(\"SacChaleur\") + 1);");
        L($"\tif(SCR_GetVarS32(\"SacOK\") == 0 && SCR_GetVarS32(\"SacChaleur\") >= {(int)(StationBagSeconds / StationLoopPeriod)})");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"SacBrule\", 1);");
        L("\t\tENT_SetVisibility(ENT_FindByName(\"Sac\"), 0);");
        L("\t\tif(SCR_GetVarS32(\"Acte\") == 4)");
        L("\t\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Sac\"), 0);");
        Lost("\t\t", "DEFEAT7");
        L("\t}");
        // la citerne de butane entamée par le feu (FD1 : CheckBombonneDanger)
        L("\tif(SCR_GetVarS32(\"BombonneDanger\") == 0 && SCR_GetVarS32(\"BombonneExp\") == 0 && ENT_GetDamage(ENT_FindByName(\"Bombonne\")) < 99.0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"BombonneDanger\", 1);");
        Cin("\t\t", "BOMBONNEDANGER");
        L("\t}");
        // le feu du camion-citerne éteint : il faut encore le conduire à l'arrosage (FD1 : CutArrosage)
        L("\tif(Feu(SCR_GetVarS32(\"G_0\")) > 0.0)");
        L("\t\tSCR_SetVarS32(\"CamionEnFeu\", 1);");
        L("\tif(SCR_GetVarS32(\"Arrosage\") == 0 && SCR_GetVarS32(\"CamionEnFeu\") == 1 && Feu(SCR_GetVarS32(\"G_0\")) <= 0.0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"Arrosage\", 1);");
        Cin("\t\t", "ARROSAGE");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Acte\") >= 3)");
        L("\t\tStation_Portes();");
        L("\tif(SCR_GetVarS32(\"Acte\") == 2)");
        L("\t{");
        L($"\t\tn = {Saved(StationAct2Victims)};");
        L("\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Acte2\"), n);");
        L($"\t\tif(n >= {StationAct2Victims.Length})");
        L("\t\t\tStation_Acte3();");
        L("\t\treturn;");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Acte\") == 3)");
        L("\t{");
        L($"\t\tn = {Saved(StationAct3Victims)};");
        L("\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Acte3\"), n);");
        L($"\t\tif(n >= {StationAct3Victims.Length})");
        L("\t\t\tStation_Acte4();");
        L("\t\treturn;");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Acte\") == 4)");
        L("\t{");
        L($"\t\tn = {Saved(StationAct4Victims)};");
        L("\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Habitants\"), n);");
        L($"\t\tif(n >= {StationAct4Victims.Length} && SCR_GetVarS32(\"Sauve_Voleur\") == 1)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Fin\", 1);");
        L("\t\t\tSCR_RunDelayed(\"Station_Victoire\", 5.0);");
        L("\t\t}");
        L("\t}");
        L("}");
        L("");
        return (f.ToString(), "\tStation_Init();\r\n", "\tStation_Main();\r\n");
    }
}
