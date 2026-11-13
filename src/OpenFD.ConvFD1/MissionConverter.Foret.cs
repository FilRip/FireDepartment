using System.Numerics;
using System.Text;

namespace OpenFD.ConvFD1;

/// <summary>
/// Foret (scripts FD1 Foret_Main / _Scenar / _Trigger) : « La forêt ».
///  • acte 1 : le front de flammes arrive ; un hélicoptère de secours se pose dans 5 minutes, si les feux autour de son
///    aire (zone bleue, près de la cabane) sont éteints et si aucune unité ne l'occupe ; y mettre le bûcheron évanoui ;
///  • acte 2 : une voiture a percuté un rocher (à 220 s de jeu) ; le bulldozer défriche la clairière de la route pour un
///    second hélicoptère (5 minutes) ; y mettre le couple accidenté ;
///  • acte 3 : le feu menace un camp de scouts ; mener les six scouts au refuge de la colonie de vacances ;
///  • acte 4 : le front d'incendie traverse la zone, l'hélicoptère qui venait les chercher s'écrase ; garder les scouts
///    dans le refuge 5 minutes ;
///  • acte 5 : l'ambulance arrive par la route ; y mettre les scouts ; victoire (texte selon les victimes).
/// Le Canadair (aide aérienne du moteur) est accordé trois fois : à 400 s, 240 s après l'acte 3, 340 s après l'acte 4.
/// Défaites : le bûcheron ou le couple meurt, un hélicoptère détruit, un véhicule détruit sur un héliport, quatre scouts
/// morts. Les groupes de foyers FD1 ne sont pas lisibles dans le niveau : ils sont reconstitués par zone (ForetGroupOf).
/// </summary>
public sealed partial class MissionConverter
{
    /// <summary>Groupes de foyers de la Foret (numéros FD1) : les abords des deux héliports, les feux des actes 2 à 4, le pont, l'hélicoptère écrasé.</summary>
    private const int ForetHeli1 = 0, ForetAct2 = 1, ForetHeli2 = 2, ForetAct3 = 3, ForetAct4 = 4, ForetBridge = 5, ForetCrash = 6;

    /// <summary>Sans numéro FD1 : le front de flammes de l'ouest (allumé au départ), la voiture accidentée (FD1 : foyer 746).</summary>
    private const int ForetFront = 40, ForetCar = 41;

    /// <summary>Aires d'atterrissage des hélicoptères (zones FD1 T1 et T2, cm : x min, x max, y min, y max).</summary>
    private static readonly (float x0, float x1, float y0, float y1) ForetPad1 = (3480, 5030, 5150, 6500);
    private static readonly (float x0, float x1, float y0, float y1) ForetPad2 = (4863, 6524, 17510, 19133);

    /// <summary>Le refuge de la colonie de vacances (zone FD1 T4).</summary>
    private static readonly (float x0, float x1, float y0, float y1) ForetRefuge = (19053, 21553, 6067, 9939);

    private static readonly Vector2 ForetPad1Center = new Vector2(4255, 5825);
    private static readonly Vector2 ForetPad2Center = new Vector2(5693, 18321);
    private static readonly Vector2 ForetRefugeCenter = new Vector2(20303, 8003);
    private static readonly Vector2 ForetBridgeAt = new Vector2(12879, 8843);
    private static readonly Vector2 ForetCrashAt = new Vector2(14003, 3511);
    private static readonly Vector2 ForetCarAt = new Vector2(10800, 12982);
    private static readonly Vector2 ForetAccidentAt = new Vector2(10700, 12700);
    private static readonly Vector2 ForetCampFire = new Vector2(12810, 19823);

    /// <summary>Rayon (cm) des abords d'un héliport : leurs feux doivent être éteints pour l'atterrissage.</summary>
    private const float ForetPadRadius = 2200;

    /// <summary>Le front de flammes de l'ouest, allumé au départ (FD1 : SEQUENCE1, à l'ouest de l'équipe de départ).</summary>
    private const float ForetFrontMaxX = 2600, ForetFrontMinY = 12500;

    /// <summary>Délais (s) : arrivée des hélicoptères, accident de voiture, attente dans le refuge, retours des cinématiques.</summary>
    private const int ForetHelicoDelay = 300, ForetAccidentDelay = 220, ForetShelterDelay = 300, ForetActPause = 3;

    /// <summary>Victimes : le bûcheron, le couple de l'accident, les six scouts.</summary>
    private static readonly string[] ForetCouple = { "Homme_Accident", "Femme_Accident" };
    private static readonly string[] ForetScouts = { "Scout0", "Scout1", "Scout2", "Scout3", "Scout4", "Scout5" };

    /// <summary>Broussailles de la clairière de l'héliport 2 (FD1 : CheckBrousailles).</summary>
    private static readonly string[] ForetPadBrush = { "Brousaille1", "Brousaille2", "Brousaille3", "Brousaille4", "Brousaille5", "Brousaille6", "Brousaille7" };

    /// <summary>Groupes qui attendent le script (FD1 : Init_HPScenars).</summary>
    private static readonly int[] ForetWaitingGroups = { ForetAct2, ForetAct3, ForetAct4, ForetCrash, ForetCar };

    /// <summary>Groupe de ce foyer de la Foret (−1 : aucun).</summary>
    private int ForetGroupOf(Vector3 at, string type)
    {
        if (!Mission.Equals("foret", StringComparison.OrdinalIgnoreCase))
            return -1;
        Vector2 p = new Vector2(at.X, at.Y);
        if (Vector2.Distance(p, ForetPad1Center) < ForetPadRadius)
            return ForetHeli1;
        if (Vector2.Distance(p, ForetPad2Center) < ForetPadRadius)
            return ForetHeli2;
        if (Vector2.Distance(p, ForetBridgeAt) < 1300)
            return ForetBridge;
        if (Vector2.Distance(p, ForetCrashAt) < 1100)
            return ForetCrash;
        if (Vector2.Distance(p, ForetCarAt) < 500)
            return ForetCar;
        float r = Vector2.Distance(p, ForetRefugeCenter);
        if (r >= 2300 && r < 5000)
            return ForetAct4;
        if (In(p, 7000, 14500, 10000, 16000))
            return ForetAct2;
        if (In(p, 6500, 18000, 16000, 24500))
            return ForetAct3;
        if (p.X < ForetFrontMaxX && p.Y > ForetFrontMinY)
            return ForetFront;
        return -1;
    }

    /// <summary>Foyers allumés au départ de la Foret : le front de l'ouest, les abords de l'héliport du bûcheron.</summary>
    private bool ForetLitAtStart(Vector3 at, string type)
    {
        int g = ForetGroupOf(at, type);
        return Ignites(type) && g is ForetFront or ForetHeli1;
    }

    /// <summary>Ce foyer prend-il quand le script allume son groupe (A_n) ?</summary>
    private static bool ForetIgnites(int g, Vector3 at, string type)
    {
        Vector2 p = new Vector2(at.X, at.Y);
        if (g is ForetCrash or ForetCar)
            return true;
        if (!Ignites(type))
            return false;
        return g switch
        {
            ForetAct2 => Vector2.Distance(p, ForetAccidentAt) is >= 1200 and < 2600,
            ForetAct3 => Vector2.Distance(p, ForetCampFire) < 1600,
            ForetAct4 => Vector2.Distance(p, ForetRefugeCenter) is >= 2400 and < 4500,
            _ => false,
        };
    }

    /// <summary>Script de la Foret (fonctions, départ, boucle).</summary>
    private (string functions, string start, string main) ForetScript(Fd1Level fd1)
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
        void Show(string indent, params string[] names)
        {
            foreach (string n in names)
                L($"{indent}ENT_SetVisibility(ENT_FindByName(\"{n}\"), 1);");
        }
        void Lost(string indent, string key)
        {
            L($"{indent}SCR_SetVarS32(\"Fin\", 2);");
            L($"{indent}LEV_SetDebriefText(\"{T(key)}\");");
            L($"{indent}SCR_RunDelayed(\"Foret_Defaite\", 5.0);");
            L($"{indent}return;");
        }
        string Box((float x0, float x1, float y0, float y1) b) => $"{b.x0:0}.0, {b.x1:0}.0, {b.y0:0}.0, {b.y1:0}.0";
        // groupes : tous les foyers (G_n), ceux qui prennent quand le script allume le groupe (A_n)
        Dictionary<int, List<string>> all = new Dictionary<int, List<string>>();
        Dictionary<int, List<string>> ignite = new Dictionary<int, List<string>>();
        for (int i = 0; i < fd1.Fires.Count; i++)
        {
            Vector3 at = fd1.Fires[i].at * WorldToCm;
            string type = fd1.Fires[i].type;
            string h = $"foyer_{i + 1}";
            int g = ForetGroupOf(at, type);
            if (g < 0)
                continue;
            if (!all.TryGetValue(g, out List<string>? list))
                all[g] = list = new List<string>();
            list.Add(h);
            if (!ForetIgnites(g, at, type))
                continue;
            if (!ignite.TryGetValue(g, out List<string>? il))
                ignite[g] = il = new List<string>();
            il.Add(h);
        }
        Report.Add($"script : groupes de foyers {string.Join(", ", all.OrderBy(g => g.Key).Select(g => $"{g.Key} ({g.Value.Count}, {(ignite.TryGetValue(g.Key, out List<string>? il) ? il.Count : 0)} allumés par le script)"))}");
        string Saved(IEnumerable<string> names) => string.Join(" + ", names.Select(n => $"SCR_GetVarS32(\"Sauve_{n}\")"));

        L("// ===== Foret : déroulement (traduit des scripts FD1) =====");
        L("");
        L("// un groupe de foyers FD1 prend feu (SetHotpointGroupActivated(n, 1)) : il peut brûler, les foyers de a prennent");
        L("void Allume(s32 g, s32 a)");
        L("{");
        L("\tif(g != 0)");
        L("\t\tFIR_HPEnable(g, 1);");
        L("\tif(a != 0)");
        L($"\t\tFIR_HPSetIntensity(a, {BanqueGroupStart}.0);");
        L("}");
        L("// intensité d'un groupe (0 s'il n'a aucun foyer)");
        L("f32 Feu(s32 g)");
        L("{");
        L("\tif(g == 0)");
        L("\t\treturn 0.0;");
        L("\treturn FIR_HPGetIntensity(g);");
        L("}");
        L("s32 Cassee(mcString nom)");
        L("{");
        L("\tif(ENT_GetState(ENT_FindByName(nom)) == \"Broken\")");
        L("\t\treturn 1;");
        L("\treturn 0;");
        L("}");
        L("void FD1_Rien(EntityID Objet)");
        L("{");
        L("}");
        L("");
        L("void Foret_Init()");
        L("{");
        L("\ts32 g;");
        L("\tSCR_SetVarS32(\"Acte\", 1);");
        foreach ((int g, List<string> fires) in all.OrderBy(x => x.Key))
        {
            L($"\tg = ENT_GroupCreate(\"groupe_{g}\");");
            foreach (string h in fires)
                L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
            L($"\tSCR_SetVarS32(\"G_{g}\", g);");
            if (ForetWaitingGroups.Contains(g))
                L("\tFIR_HPEnable(g, 0);");
            if (!ignite.TryGetValue(g, out List<string>? il))
                continue;
            L($"\tg = ENT_GroupCreate(\"allume_{g}\");");
            foreach (string h in il)
                L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
            L($"\tSCR_SetVarS32(\"A_{g}\", g);");
        }
        // unités sur les aires d'atterrissage (zones Heliport1 et Heliport2 : entrées et sorties)
        L("\tSCR_SetVarS32(\"Dans1\", ENT_GroupCreate(\"dans_heliport1\"));");
        L("\tSCR_SetVarS32(\"Dans2\", ENT_GroupCreate(\"dans_heliport2\"));");
        Show("\t", "Bucheron");
        Cin("\t", "SEQUENCE1");
        L($"\tSCR_SetVarS32(\"Obj_Bucheron\", LEV_AddObjectiveBoolean(\"{T("FORET_DES_S1O0")}\"));");
        L($"\tSCR_SetVarS32(\"Obj_Heliport1\", LEV_AddObjectiveBoolean(\"{T("FORET_DES_S1O1_SEUL")}\"));");
        L($"\tSCR_SetVarF32(\"HelicoT\", UTL_GetGameTime() + {ForetHelicoDelay}.0);");
        L($"\tSCR_RunDelayed(\"AccidentVoiture\", {ForetAccidentDelay}.0);");
        L("\tSCR_RunDelayed(\"PresentationWildland\", 45.0);");
        L("\tSCR_RunDelayed(\"Canadair1\", 400.0);");
        L("}");
        L("");
        // ---- héliports ----
        L("// une unité entre sur une aire d'atterrissage, ou en sort (FD1 : CheckOccupation, T1 et T2) ; l'hélicoptère lui-même");
        L("// ne compte pas");
        foreach (int k in new[] { 1, 2 })
        {
            L($"void ForetEntre{k}(EntityID Unit, EntityID Zone)");
            L("{");
            L($"\tif(Unit == ENT_FindByName(\"{(k == 1 ? "MM_Helicoptere1" : "MM_helicoptere2")}\"))");
            L("\t\treturn;");
            L($"\tif(ENT_IsEntityInGroup(Unit, SCR_GetVarS32(\"Dans{k}\")) == 0)");
            L($"\t\tENT_GroupAddEntity(SCR_GetVarS32(\"Dans{k}\"), Unit);");
            L("}");
            L($"void ForetSort{k}(EntityID Unit, EntityID Zone)");
            L("{");
            L($"\tENT_GroupRemEntity(SCR_GetVarS32(\"Dans{k}\"), Unit);");
            L("}");
        }
        L("// unités encore sur l'aire (x0..x1, y0..y1) : celles qui n'y sont plus (détruites) sont retirées du groupe");
        L("s32 Occupe(s32 g, f32 x0, f32 x1, f32 y0, f32 y1)");
        L("{");
        L("\ts32 i;");
        L("\ts32 e;");
        L("\tf32 x;");
        L("\tf32 y;");
        L("\tfor(i = ENT_GroupGetNbEntities(g) - 1; i >= 0; i = i - 1)");
        L("\t{");
        L("\t\te = ENT_GroupGetEntityAt(g, i);");
        L("\t\tx = -100000.0;");
        L("\t\ty = -100000.0;");
        L("\t\tENT_GetPosition(e, x, y);");
        L("\t\tif(x < x0 || x > x1 || y < y0 || y > y1)");
        L("\t\t\tENT_GroupRemEntity(g, e);");
        L("\t}");
        L("\treturn ENT_GroupGetNbEntities(g);");
        L("}");
        L("// l'héliport k est-il prêt (FD1 : CheckHeliport) ? Feux de ses abords éteints, aire libre ; sinon les messages FD1 (toutes");
        L("// les 45 s) ; l'héliport 2 doit aussi être défriché");
        L("s32 HeliportPret(s32 k, s32 feux, s32 dans, f32 x0, f32 x1, f32 y0, f32 y1)");
        L("{");
        L("\ts32 r;");
        L("\tr = 1;");
        L("\tif(Feu(feux) > 0.0)");
        L("\t{");
        L("\t\tr = 0;");
        L("\t\tif(UTL_GetGameTime() >= SCR_GetVarF32(\"MsgFeuT\"))");
        L("\t\t{");
        L("\t\t\tSCR_SetVarF32(\"MsgFeuT\", UTL_GetGameTime() + 45.0);");
        Msg("\t\t\t", "EVENT_FOR_HELIPORT_FEU");
        L("\t\t}");
        L("\t}");
        L("\tif(Occupe(dans, x0, x1, y0, y1) > 0)");
        L("\t{");
        L("\t\tr = 0;");
        L("\t\tif(UTL_GetGameTime() >= SCR_GetVarF32(\"MsgOqpT\"))");
        L("\t\t{");
        L("\t\t\tSCR_SetVarF32(\"MsgOqpT\", UTL_GetGameTime() + 45.0);");
        Msg("\t\t\t", "EVENT_FOR_HELIPORT_OQP");
        L("\t\t}");
        L("\t}");
        L("\tif(k == 2)");
        L("\t{");
        foreach (string b in ForetPadBrush)
        {
            L($"\t\tif(Cassee(\"{b}\") == 0)");
            L("\t\t\tr = 0;");
        }
        L("\t}");
        L("\treturn r;");
        L("}");
        foreach (int k in new[] { 1, 2 })
        {
            string helico = k == 1 ? "MM_Helicoptere1" : "MM_helicoptere2";
            L($"// l'hélicoptère {k} se pose (FD1 : PoseHelico{k}) ; il emmène {(k == 1 ? "le bûcheron" : "le couple")}, puis il repart");
            L($"void PoseHelico{k}()");
            L("{");
            L($"\tSCR_SetVarS32(\"HelicoPose{k}\", 1);");
            Show("\t", helico);
            Cin("\t", $"ARRIVEE_HELICOPTERE{k}");
            L($"\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Heliport{k}\"), 1);");
            L("}");
            L($"void DecolleHelico{k}()");
            L("{");
            L($"\tENT_SetVisibility(ENT_FindByName(\"{helico}\"), 0);");
            L($"\tLEV_ValidateObjective(SCR_GetVarS32(\"{(k == 1 ? "Obj_Bucheron" : "Obj_Couple")}\"), 1);");
            L($"\tSCR_RunDelayed(\"Foret_Acte{k + 1}\", {ForetActPause}.0);");
            L("}");
            L($"void HelicoNonPose{k}()");
            L("{");
            L("\tSCR_SetVarS32(\"HelicoNoPose\", 0);");
            L("}");
        }
        L("");
        // ---- événements ----
        L("// un pompier ou deux véhicules approchent de l'héliport du bûcheron (FD1 : T0) : la cinématique de sa zone");
        L("void ForetZone0(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tif(SCR_GetVarS32(\"Zone0Vu\") == 1 || SCR_GetVarS32(\"Acte\") != 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"Zone0Vu\", 1);");
        Cin("\t", "SAUVEGARDE_HELIPORT1");
        L("}");
        L("// la voiture qui perd le contrôle et percute le rocher (FD1 : AccidentVoiture, à 220 s) ; elle prend feu");
        L("void AccidentVoiture()");
        L("{");
        Cin("\t", "VOITURE_ZIGZAGBOUM");
        L("\tSCR_RunDelayed(\"AccidentVoitureBis\", 6.0);");
        L("}");
        L("void AccidentVoitureBis()");
        L("{");
        Show("\t", "Voiture_Accident_Rocher");
        L($"\tAllume(SCR_GetVarS32(\"G_{ForetCar}\"), SCR_GetVarS32(\"A_{ForetCar}\"));");
        L("}");
        L("void PresentationWildland()");
        L("{");
        Cin("\t", "PRESENT_WILDLAND");
        L("}");
        L("void PresentationDefrichage()");
        L("{");
        Cin("\t", "DEFRICHAGE_HELIPORT2");
        L("}");
        L("void PresentationColonie()");
        L("{");
        Cin("\t", "COLONNIE_VACS");
        L("}");
        L("// le Canadair (FD1 : CanadairAvailable) : un passage du bombardier d'eau, le joueur choisit la zone");
        foreach (int k in new[] { 1, 2, 4 })
        {
            L($"void Canadair{k}()");
            L("{");
            L("\tif(SCR_GetVarS32(\"Fin\") != 0)");
            L("\t\treturn;");
            Cin("\t", $"CANADAIR{k}");
            L("\tLEV_GrantAirSupport();");
            L("}");
        }
        L("// un véhicule détruit (rappel du moteur) : un hélicoptère, ou un véhicule sur une aire d'atterrissage avant que");
        L("// l'hélicoptère s'y pose (FD1 : Foret_CheckDefeat, DEFEAT_MM et DEFEAT_ZONE)");
        L("void OnVehicleDeath(EntityID Vehicule)");
        L("{");
        L("\tf32 x;");
        L("\tf32 y;");
        L("\tif(SCR_GetVarS32(\"Fin\") != 0)");
        L("\t\treturn;");
        L("\tif(Vehicule == ENT_FindByName(\"MM_Helicoptere1\") || Vehicule == ENT_FindByName(\"MM_helicoptere2\"))");
        L("\t{");
        Lost("\t\t", "DEFEAT_MM");
        L("\t}");
        L("\tx = -100000.0;");
        L("\ty = -100000.0;");
        L("\tENT_GetPosition(Vehicule, x, y);");
        foreach ((int k, (float x0, float x1, float y0, float y1) b) in new[] { (1, ForetPad1), (2, ForetPad2) })
        {
            L($"\tif(SCR_GetVarS32(\"HelicoPose{k}\") == 0 && SCR_GetVarS32(\"Acte\") <= {k} && x >= {b.x0:0}.0 && x <= {b.x1:0}.0 && y >= {b.y0:0}.0 && y <= {b.y1:0}.0)");
            L("\t{");
            Lost("\t\t", "DEFEAT_ZONE");
            L("\t}");
        }
        L("}");
        L("");
        // ---- actes ----
        L("// acte 2 (FD1 : transS1) : les feux de la route prennent, le couple accidenté est sorti de la voiture ; un second");
        L("// hélicoptère arrive dans 5 minutes sur la clairière à défricher");
        L("void Foret_Acte2()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 2);");
        L("\tSCR_SetVarS32(\"Morts2\", LEV_GetNumVictims());");
        L($"\tAllume(SCR_GetVarS32(\"G_{ForetAct2}\"), SCR_GetVarS32(\"A_{ForetAct2}\"));");
        Show("\t", ForetCouple);
        Cin("\t", "VICTIMES_ACCIDENT");
        L("\tSCR_RunDelayed(\"PresentationDefrichage\", 30.0);");
        L("\tSCR_RunDelayed(\"Canadair2\", 240.0);");
        L($"\tSCR_SetVarF32(\"HelicoT\", UTL_GetGameTime() + {ForetHelicoDelay}.0);");
        L($"\tSCR_SetVarS32(\"Obj_Couple\", LEV_AddObjectiveIterate(\"{T("FORET_DES_S2O0_SEUL")}\", {ForetCouple.Length}));");
        L($"\tSCR_SetVarS32(\"Obj_Heliport2\", LEV_AddObjectiveBoolean(\"{T("FORET_DES_S2O1_SEUL")}\"));");
        L("}");
        L("// acte 3 (FD1 : transS2) : le feu menace le camp de scouts ; il faut les mener au refuge de la colonie");
        L("void Foret_Acte3()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 3);");
        L("\tSCR_SetVarS32(\"Morts3\", LEV_GetNumVictims());");
        L($"\tAllume(SCR_GetVarS32(\"G_{ForetAct3}\"), SCR_GetVarS32(\"A_{ForetAct3}\"));");
        Show("\t", ForetScouts);
        Cin("\t", "SEQUENCE3");
        L("\tSCR_RunDelayed(\"PresentationColonie\", 30.0);");
        L($"\tSCR_SetVarS32(\"Obj_Refuge\", LEV_AddObjectiveIterate(\"{T("FORET_DES_S3O1")}\", {ForetScouts.Length}));");
        L("}");
        L("// acte 4 (FD1 : transS3) : le front d'incendie traverse la zone, l'hélicoptère s'écrase ; tenir 5 minutes au refuge");
        L("void Foret_Acte4()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 4);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Refuge\"), 1);");
        L($"\tAllume(SCR_GetVarS32(\"G_{ForetAct4}\"), SCR_GetVarS32(\"A_{ForetAct4}\"));");
        L($"\tSCR_SetVarF32(\"AbriT\", UTL_GetGameTime() + {ForetShelterDelay}.0);");
        L("\tSCR_RunDelayed(\"Canadair4\", 340.0);");
        Cin("\t", "ARRIVEE_HELICOPTERE3");
        L("\tSCR_RunDelayed(\"EpaveHelicoptere\", 12.0);");
        L($"\tSCR_SetVarS32(\"Obj_Abri\", LEV_AddObjectiveProgress(\"{T("FORET_DES_S4O1_SEUL")}\", 0.0, 100.0));");
        L("}");
        L("void EpaveHelicoptere()");
        L("{");
        Show("\t", "Epave_Helicoptere3");
        L($"\tAllume(SCR_GetVarS32(\"G_{ForetCrash}\"), SCR_GetVarS32(\"A_{ForetCrash}\"));");
        L("\tSCR_RunDelayed(\"PresentationFront\", 15.0);");
        L("}");
        L("void PresentationFront()");
        L("{");
        Cin("\t", "SEQUENCE4");
        L("}");
        L("// acte 5 (FD1 : transS4) : l'ambulance et le médecin arrivent par la route ; y mettre les scouts");
        L("void Foret_Acte5()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 5);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Abri\"), 1);");
        L("\tLEV_CallReinforcement(1);");
        Cin("\t", "FIN_MMASTER");
        L($"\tSCR_SetVarS32(\"Obj_Ambulance\", LEV_AddObjectiveIterate(\"{T("FORET_DES_S5O1")}\", {ForetScouts.Length} - (LEV_GetNumVictims() - SCR_GetVarS32(\"Morts3\"))));");
        L("}");
        L("");
        // ---- victimes et fin ----
        L("// civil mis à l'abri (rappel du moteur)");
        L("void OnCivilianSaved(EntityID Civil)");
        L("{");
        foreach (string c in ForetScouts.Concat(ForetCouple).Prepend("Bucheron"))
        {
            L($"\tif(Civil == ENT_FindByName(\"{c}\"))");
            L($"\t\tSCR_SetVarS32(\"Sauve_{c}\", 1);");
        }
        L("\tif(Civil == ENT_FindByName(\"Bucheron\"))");
        L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Bucheron\"), 1);");
        L("}");
        L("void Foret_Defaite()");
        L("{");
        L("\tLEV_TriggerDefeat();");
        L("}");
        L("// victoire (FD1 : Victory_OnTimer) : texte selon les victimes");
        L("void Foret_Victoire()");
        L("{");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Ambulance\"), 1);");
        L("\tLEV_SetDebriefText(\"" + T("VICTORY1") + "\");");
        L("\tif(LEV_GetNumVictims() >= 1)");
        L("\t\tLEV_SetDebriefText(\"" + T("VICTORY2") + "\");");
        L("\tif(LEV_GetNumVictims() >= 3)");
        L("\t\tLEV_SetDebriefText(\"" + T("VICTORY3") + "\");");
        L("\tLEV_TriggerVictory();");
        L("}");
        L("// scouts dans le refuge (zone FD1 T4), vivants ou non");
        L("s32 ScoutsAbrites()");
        L("{");
        L("\ts32 n;");
        L("\tf32 x;");
        L("\tf32 y;");
        L("\tn = 0;");
        foreach (string s in ForetScouts)
        {
            L("\tx = -100000.0;");
            L("\ty = -100000.0;");
            L($"\tENT_GetPosition(ENT_FindByName(\"{s}\"), x, y);");
            L($"\tif(x >= {ForetRefuge.x0:0}.0 && x <= {ForetRefuge.x1:0}.0 && y >= {ForetRefuge.y0:0}.0 && y <= {ForetRefuge.y1:0}.0)");
            L("\t\tn = n + 1;");
        }
        L("\treturn n;");
        L("}");
        L("");
        L("// boucle : défaites, pont, héliports, actes");
        L("void Foret_Main()");
        L("{");
        L("\ts32 n;");
        L("\ts32 vivants;");
        L("\tif(SCR_GetVarS32(\"Fin\") != 0)");
        L("\t\treturn;");
        // défaites (FD1 : Foret_CheckDefeat) : chaque acte n'a que ses victimes en vue
        L("\tif(SCR_GetVarS32(\"Acte\") == 1 && LEV_GetNumVictims() > 0)");
        L("\t{");
        Lost("\t\t", "DEFEAT_BUCHERON");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Acte\") == 2 && LEV_GetNumVictims() > SCR_GetVarS32(\"Morts2\"))");
        L("\t{");
        Lost("\t\t", "DEFEAT_COUPLE");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Acte\") >= 3 && LEV_GetNumVictims() - SCR_GetVarS32(\"Morts3\") >= 4)");
        L("\t{");
        Lost("\t\t", "DEFEAT_QUOTA");
        L("\t}");
        // le pont en feu (FD1 : CheckPontEnFeux) : la cinématique quand son feu passe 50, de nouveau après qu'il est retombé
        L($"\tif(Feu(SCR_GetVarS32(\"G_{ForetBridge}\")) > 50.0 && SCR_GetVarS32(\"PontVu\") == 0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"PontVu\", 1);");
        Cin("\t\t", "PONT_EN_FEUX");
        L("\t}");
        L($"\tif(Feu(SCR_GetVarS32(\"G_{ForetBridge}\")) <= 50.0)");
        L("\t\tSCR_SetVarS32(\"PontVu\", 0);");
        foreach ((int k, int feux, (float x0, float x1, float y0, float y1) pad) in new[] { (1, ForetHeli1, ForetPad1), (2, ForetHeli2, ForetPad2) })
        {
            L($"\tif(SCR_GetVarS32(\"Acte\") == {k})");
            L("\t{");
            L($"\t\tif(SCR_GetVarS32(\"HelicoPose{k}\") == 0)");
            L("\t\t{");
            L($"\t\t\tn = HeliportPret({k}, SCR_GetVarS32(\"G_{feux}\"), SCR_GetVarS32(\"Dans{k}\"), {Box(pad)});");
            L("\t\t\tif(UTL_GetGameTime() >= SCR_GetVarF32(\"HelicoT\"))");
            L("\t\t\t{");
            L("\t\t\t\tif(n == 1)");
            L($"\t\t\t\t\tPoseHelico{k}();");
            L("\t\t\t\telse if(SCR_GetVarS32(\"HelicoNoPose\") == 0)");
            L("\t\t\t\t{");
            L("\t\t\t\t\tSCR_SetVarS32(\"HelicoNoPose\", 1);");
            Cin("\t\t\t\t\t", $"CUT_HELICONO{k}");
            L($"\t\t\t\t\tSCR_RunDelayed(\"HelicoNonPose{k}\", 60.0);");
            L("\t\t\t\t}");
            L("\t\t\t}");
            L("\t\t}");
            string who = k == 1 ? "SCR_GetVarS32(\"Sauve_Bucheron\")" : Saved(ForetCouple);
            int need = k == 1 ? 1 : ForetCouple.Length;
            if (k == 2)
                L($"\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Couple\"), {who});");
            L($"\t\tif(SCR_GetVarS32(\"HelicoPose{k}\") == 1 && SCR_GetVarS32(\"Decolle{k}\") == 0 && {who} >= {need})");
            L("\t\t{");
            L($"\t\t\tSCR_SetVarS32(\"Decolle{k}\", 1);");
            L($"\t\t\tSCR_RunDelayed(\"DecolleHelico{k}\", 5.0);");
            L("\t\t}");
            L("\t\treturn;");
            L("\t}");
        }
        L("\tvivants = 6 - (LEV_GetNumVictims() - SCR_GetVarS32(\"Morts3\"));");
        L("\tif(SCR_GetVarS32(\"Acte\") == 3)");
        L("\t{");
        L("\t\tn = ScoutsAbrites();");
        L("\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Refuge\"), n);");
        L("\t\tif(n >= vivants && SCR_GetVarS32(\"Acte4Prevu\") == 0)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Acte4Prevu\", 1);");
        L($"\t\t\tSCR_RunDelayed(\"Foret_Acte4\", {ForetActPause}.0);");
        L("\t\t}");
        L("\t\treturn;");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Acte\") == 4)");
        L("\t{");
        L($"\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Abri\"), 100.0 - (SCR_GetVarF32(\"AbriT\") - UTL_GetGameTime()) / {ForetShelterDelay / 100f:0.0##});");
        L("\t\tif(UTL_GetGameTime() >= SCR_GetVarF32(\"AbriT\") && SCR_GetVarS32(\"Acte5Prevu\") == 0)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Acte5Prevu\", 1);");
        L($"\t\t\tSCR_RunDelayed(\"Foret_Acte5\", {ForetActPause}.0);");
        L("\t\t}");
        L("\t\treturn;");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Acte\") == 5)");
        L("\t{");
        L($"\t\tn = {Saved(ForetScouts)};");
        L("\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Ambulance\"), n);");
        L("\t\tif(n >= vivants)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Fin\", 1);");
        L("\t\t\tSCR_RunDelayed(\"Foret_Victoire\", 5.0);");
        L("\t\t}");
        L("\t}");
        L("}");
        L("");
        return (f.ToString(), "\tForet_Init();\r\n", "\tForet_Main();\r\n");
    }
}
