using System.Numerics;
using System.Text;

namespace OpenFD.ConvFD1;

/// <summary>
/// Train (scripts FD1 Train_Main / _Scenar / _Event) : « Le poison ».
///  • acte 1 : le convoi a percuté un tracteur au passage à niveau ; la locomotive de tête et les wagons d'isocyanate de
///    méthyle brûlent ; éteindre le convoi (la pollution monte tant que les produits chimiques brûlent), protéger le wagon
///    de gaz, sauver le conducteur du tracteur et le machiniste ;
///  • acte 2 : le feu gagne le village et l'écurie ; sauver le palefrenier, protéger les sept maisons ;
///  • acte 3 : une voiture saute devant le tunnel, un autre wagon chimique brûle dedans ; deux pompiers arrivent en renfort
///    (FD1 : déposés par l'hélicoptère) ; l'aération de secours chasse la fumée toxique ; sauver les deux techniciens ;
///  • victoire : texte selon les victimes, les maisons détruites et la pollution ; défaites : la pollution au maximum, le
///    wagon de gaz détruit, l'ambulance ou la citerne détruite.
/// Les groupes de foyers FD1 ne sont pas lisibles dans le niveau : ils sont reconstitués par zone (TrainGroupOf).
/// </summary>
public sealed partial class MissionConverter
{
    /// <summary>
    /// Groupes de foyers du Train (numéros FD1 quand on les connaît) : 4, les wagons chimiques du passage à niveau ; 15, le
    /// convoi ; 26, l'écurie ; 27, le village et la ferme ; 28, la fumée toxique du tunnel ; 29, le wagon chimique du tunnel ;
    /// 30, l'entrée du tunnel. Sans numéro FD1 : les pesticides de la grange (TrainPesticides), les silos (TrainSilos).
    /// </summary>
    private const int TrainPesticides = 40;
    private const int TrainSilos = 41;

    /// <summary>Les sept maisons du village (cm : x min, x max, y min, y max) et le feu (intensité) qui les détruit (FD1).</summary>
    private static readonly (float x0, float x1, float y0, float y1, int limit)[] TrainHouses =
    {
        (7473, 8874, 14123, 14982, 300), (1380, 3222, 14500, 16936, 350), (7852, 9379, 16071, 17730, 200), (5157, 7475, 17157, 18399, 500),
        (4876, 7337, 20699, 22589, 300), (7892, 9346, 21060, 23002, 350), (1082, 2534, 20338, 22792, 250),
    };

    /// <summary>Les premiers foyers du village à l'acte 2 (cm : les plans de la cinématique CUT_ACTE2), à moins de 8 m.</summary>
    private static readonly Vector2[] TrainVillageStarts = { new Vector2(3255, 13859), new Vector2(8592, 13595), new Vector2(12161, 13207) };

    /// <summary>Le flanc de l'écurie qui prend feu pendant l'acte 2 (cm : plan de CUT_PALEFRENIER), à moins de 8 m.</summary>
    private static readonly Vector2 TrainStable = new Vector2(14271, 18640);

    /// <summary>La locomotive de tête (cm) : quand elle explose, ses foyers à moins de 10 m prennent.</summary>
    private static readonly Vector2 TrainLoco = new Vector2(3822, 12077);

    /// <summary>La voiture qui saute devant le tunnel (cm, acte 3) : ses foyers à moins de 6 m prennent.</summary>
    private static readonly Vector2 TrainTunnelCar = new Vector2(14980, 12218);

    /// <summary>Délai (s) entre l'acte 2 et le feu de l'écurie.</summary>
    private const int TrainStableDelay = 60;

    /// <summary>
    /// Pollution (somme des intensités des feux chimiques, comptée chaque seconde) : messages à 500 000 et 850 000, défaite à
    /// 1 000 000 (FD1) ; un silo qui explose en ajoute 100 000.
    /// </summary>
    private const int TrainPollutionMax = 1000000;

    /// <summary>
    /// Pollution par seconde et par point d'intensité des feux chimiques : les 28 fuites du passage à niveau, à pleine
    /// intensité (100), mènent à la défaite en six minutes.
    /// </summary>
    private const float TrainPollutionRate = 1f;

    /// <summary>Victimes de chaque acte (FD1 : CheckScenario1, 2, 3).</summary>
    private static readonly string[] TrainAct1Victims = { "Conducteur1", "Conducteur2" };
    private static readonly string[] TrainAct3Victims = { "Conducteur3", "Conducteur4" };

    /// <summary>Groupes du Train qui attendent le script (FD1 : Init_HPScenars, Init_Pollution).</summary>
    private static readonly int[] TrainWaitingGroups = { 28, 29 };

    /// <summary>Le foyer FD1 est-il un feu de produits chimiques (il pollue) ?</summary>
    private static bool TrainChemical(string type) => type.Contains("chimique", StringComparison.OrdinalIgnoreCase);

    /// <summary>Groupe de ce foyer du Train (−1 : aucun).</summary>
    private int TrainGroupOf(Vector3 at, string type)
    {
        if (!Mission.Equals("train", StringComparison.OrdinalIgnoreCase))
            return -1;
        Vector2 p = new Vector2(at.X, at.Y);
        if (type.Contains("fumtoxique", StringComparison.OrdinalIgnoreCase))
            return 28;
        if (TrainChemical(type))
        {
            if (p.X > 18000)
                return 29;
            if (p.Y > 18000)
                return TrainPesticides;
            return 4;
        }
        if (p.Y < 5000 && p.X > 13000 && p.X < 17500)
            return TrainSilos;
        if (In(p, 14800, 24000, 10500, 14000))
            return 30;
        if (In(p, 0, 14800, 9800, 13500))
            return 15;
        if (In(p, 13309, 15606, 16907, 20590))
            return 26;
        if (p.X < 16500 && p.Y >= 13500)
            return 27;
        return -1;
    }

    /// <summary>Maison du Train qui contient ce foyer (−1 : aucune).</summary>
    private static int TrainHouseOf(Vector3 at)
    {
        for (int k = 0; k < TrainHouses.Length; k++)
            if (at.X >= TrainHouses[k].x0 - 150 && at.X <= TrainHouses[k].x1 + 150 && at.Y >= TrainHouses[k].y0 - 150 && at.Y <= TrainHouses[k].y1 + 150)
                return k;
        return -1;
    }

    /// <summary>Foyers allumés au départ du Train : les wagons chimiques du passage à niveau, la locomotive de tête.</summary>
    private bool TrainLitAtStart(Vector3 at, string type)
    {
        int g = TrainGroupOf(at, type);
        if (g == 4)
            return true;
        return g == 15 && Ignites(type) && Vector2.Distance(new Vector2(at.X, at.Y), TrainLoco) < 1300;
    }

    /// <summary>Ce foyer prend-il quand le script allume son groupe (A_n) ?</summary>
    private static bool TrainIgnites(int g, Vector3 at, string type)
    {
        Vector2 p = new Vector2(at.X, at.Y);
        if (!Ignites(type) && g is not (28 or 29))
            return false;
        return g switch
        {
            26 => Vector2.Distance(p, TrainStable) < 800,
            27 => TrainVillageStarts.Any(q => Vector2.Distance(p, q) < 800),
            28 or 29 => true,
            30 => Vector2.Distance(p, TrainTunnelCar) < 600 || type.StartsWith("torche", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>Script du Train (fonctions, départ, boucle).</summary>
    private (string functions, string start, string main) TrainScript(Fd1Level fd1)
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
                L($"{indent}ENT_SetVisibility(ENT_FindByName(\"{n}\"), 1);");
        }
        void Lost(string indent, string key)
        {
            L($"{indent}SCR_SetVarS32(\"Fin\", 2);");
            L($"{indent}LEV_SetDebriefText(\"{T(key)}\");");
            L($"{indent}SCR_RunDelayed(\"Train_Defaite\", 5.0);");
            L($"{indent}return;");
        }
        // groupes : tous les foyers (G_n), ceux qui prennent quand le script allume le groupe (A_n), les maisons (M_k), les
        // foyers autour de la locomotive de tête
        Dictionary<int, List<string>> all = new Dictionary<int, List<string>>();
        Dictionary<int, List<string>> ignite = new Dictionary<int, List<string>>();
        List<string>[] houses = Enumerable.Range(0, TrainHouses.Length).Select(_ => new List<string>()).ToArray();
        List<string> loco = new List<string>();
        for (int i = 0; i < fd1.Fires.Count; i++)
        {
            Vector3 at = fd1.Fires[i].at * WorldToCm;
            string type = fd1.Fires[i].type;
            string h = $"foyer_{i + 1}";
            int house = TrainHouseOf(at);
            if (house >= 0)
                houses[house].Add(h);
            if (Ignites(type) && Vector2.Distance(new Vector2(at.X, at.Y), TrainLoco) < 1000)
                loco.Add(h);
            int g = TrainGroupOf(at, type);
            if (g < 0)
                continue;
            if (!all.TryGetValue(g, out List<string>? list))
                all[g] = list = new List<string>();
            list.Add(h);
            if (!TrainIgnites(g, at, type))
                continue;
            if (!ignite.TryGetValue(g, out List<string>? il))
                ignite[g] = il = new List<string>();
            il.Add(h);
        }
        Report.Add($"script : groupes de foyers {string.Join(", ", all.OrderBy(g => g.Key).Select(g => $"{g.Key} ({g.Value.Count}, {(ignite.TryGetValue(g.Key, out List<string>? il) ? il.Count : 0)} allumés par le script)"))}, maisons {string.Join("/", houses.Select(x => x.Count))}, locomotive {loco.Count}");
        string Saved(IEnumerable<string> names) => string.Join(" + ", names.Select(n => $"SCR_GetVarS32(\"Sauve_{n}\")"));
        string Destroyed() => string.Join(" + ", Enumerable.Range(1, TrainHouses.Length).Select(k => $"SCR_GetVarS32(\"Maison{k}Detruite\")"));

        L("// ===== Train : déroulement (traduit des scripts FD1) =====");
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
        L("void Croissance(s32 g, f32 taux)");
        L("{");
        L("\tif(g != 0)");
        L("\t\tFIR_HPSetGrowthRate(g, taux);");
        L("}");
        L("");
        L("void Train_Init()");
        L("{");
        L("\ts32 g;");
        L("\tSCR_SetVarS32(\"Acte\", 1);");
        foreach ((int g, List<string> fires) in all.OrderBy(x => x.Key))
        {
            L($"\tg = ENT_GroupCreate(\"groupe_{g}\");");
            foreach (string h in fires)
                L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
            L($"\tSCR_SetVarS32(\"G_{g}\", g);");
            if (TrainWaitingGroups.Contains(g))
                L("\tFIR_HPEnable(g, 0);");
            if (!ignite.TryGetValue(g, out List<string>? il))
                continue;
            L($"\tg = ENT_GroupCreate(\"allume_{g}\");");
            foreach (string h in il)
                L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
            L($"\tSCR_SetVarS32(\"A_{g}\", g);");
        }
        for (int k = 0; k < houses.Length; k++)
        {
            L($"\tg = ENT_GroupCreate(\"maison_{k + 1}\");");
            foreach (string h in houses[k])
                L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
            L($"\tSCR_SetVarS32(\"M_{k + 1}\", g);");
        }
        L("\tg = ENT_GroupCreate(\"locomotive\");");
        foreach (string h in loco)
            L("\tENT_GroupAddEntity(g, ENT_FindByName(\"" + h + "\"));");
        L("\tSCR_SetVarS32(\"G_Loco\", g);");
        Show("\t", TrainAct1Victims);
        Cin("\t", "CUT_START");
        L($"\tSCR_SetVarS32(\"Obj_Pollution\", LEV_AddObjectiveProgress(\"{T("TRAIN_DES_S1O0")}\", 0.0, 100.0));");
        L($"\tSCR_SetVarS32(\"Obj_Convoi\", LEV_AddObjectiveBoolean(\"{T("TRAIN_DES_S1O1_SEUL")}\"));");
        L($"\tSCR_SetVarS32(\"Obj_Acte1\", LEV_AddObjectiveIterate(\"{T("TRAIN_DES_S1O2")}\", {TrainAct1Victims.Length}));");
        L("}");
        L("");
        // ---- objets ----
        L("// la locomotive de tête explose (FD1 : CheckExplosions, Loco1) : ses abords prennent feu");
        L("void LocoExplosee(EntityID Loco)");
        L("{");
        L("\tAllume(SCR_GetVarS32(\"G_Loco\"), SCR_GetVarS32(\"G_Loco\"));");
        L("}");
        L("// le wagon de gaz détruit : la mission est perdue (FD1 : Train_CheckDefeat)");
        L("void MiniConvoiExplose(EntityID Wagon)");
        L("{");
        L("\tif(SCR_GetVarS32(\"Fin\") != 0)");
        L("\t\treturn;");
        Cin("\t", "PERTE_MINICONVOI");
        Lost("\t", "DEFEAT1");
        L("}");
        L("// un silo explose : la pollution augmente (FD1 : CheckExplosions, Silo1 à 3)");
        L("void SiloExplose(EntityID Silo)");
        L("{");
        L("\tSCR_SetVarF32(\"Pollution\", SCR_GetVarF32(\"Pollution\") + 100000.0);");
        Cin("\t", "CUT_SILOS_EXPLODE");
        L("}");
        L("// l'aération de secours du tunnel (FD1 : Ventilation) : la fumée toxique s'en va");
        L("void Ventilation()");
        L("{");
        L("\tif(SCR_GetVarS32(\"Ventile\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"Ventile\", 1);");
        Cin("\t", "CUT_VENTILATION");
        L("\tCroissance(SCR_GetVarS32(\"G_28\"), -4.0);");
        L("}");
        L("");
        // ---- actes ----
        L("// acte 2 (FD1 : Scenario1 réussi) : le convoi s'éteint, le feu gagne le village ; l'écurie s'embrase un peu après");
        L("void Train_Acte2()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 2);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Acte1\"), 1);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Convoi\"), 1);");
        L("\tCroissance(SCR_GetVarS32(\"G_15\"), -1.0);");
        L("\tAllume(SCR_GetVarS32(\"G_27\"), SCR_GetVarS32(\"A_27\"));");
        Roof("\t", "Toit_Ecurie");
        Show("\t", "Palefrenier");
        Cin("\t", "CUT_ACTE2");
        L($"\tSCR_RunDelayed(\"FeuEcurie\", {TrainStableDelay}.0);");
        L($"\tSCR_SetVarS32(\"Obj_Palefrenier\", LEV_AddObjectiveBoolean(\"{T("TRAIN_DES_S2O1_SEUL")}\"));");
        L($"\tSCR_SetVarS32(\"Obj_Village\", LEV_AddObjectiveBoolean(\"{T("TRAIN_DES_S2O2_SEUL")}\"));");
        L("\tif(SCR_GetVarS32(\"Sauve_Palefrenier\") == 1)");
        L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Palefrenier\"), 1);");
        L("}");
        L("void FeuEcurie()");
        L("{");
        L("\tAllume(SCR_GetVarS32(\"G_26\"), SCR_GetVarS32(\"A_26\"));");
        L("}");
        L("// acte 3 (FD1 : Scenario2 réussi) : la voiture devant le tunnel saute, le wagon chimique du tunnel brûle ; le renfort");
        L("void Train_Acte3()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 3);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Village\"), 1);");
        L("\tCroissance(SCR_GetVarS32(\"G_27\"), -0.5);");
        L("\tAllume(SCR_GetVarS32(\"G_28\"), SCR_GetVarS32(\"A_28\"));");
        L("\tAllume(SCR_GetVarS32(\"G_29\"), SCR_GetVarS32(\"A_29\"));");
        L("\tSCR_RunDelayed(\"VoitureTunnel\", 1.0);");
        Cin("\t", "CUT_ACTE3");
        L("\tSCR_RunDelayed(\"Renfort\", 2.0);");
        L("\tSCR_RunDelayed(\"Techniciens\", 20.0);");
        L($"\tSCR_SetVarS32(\"Obj_Acte3\", LEV_AddObjectiveIterate(\"{T("TRAIN_DES_S3O1")}\", {TrainAct3Victims.Length}));");
        L($"\tSCR_SetVarS32(\"Obj_Feux3\", LEV_AddObjectiveBoolean(\"{T("TRAIN_DES_S3O2_SEUL")}\"));");
        L("}");
        L("void VoitureTunnel()");
        L("{");
        L("\tENT_SetDamage(ENT_FindByName(\"VoitureExplo\"), 0.0);");
        L("\tAllume(SCR_GetVarS32(\"G_30\"), SCR_GetVarS32(\"A_30\"));");
        L("}");
        L("void Renfort()");
        L("{");
        L("\tLEV_CallReinforcement(1);");
        L("}");
        L("// les deux techniciens du tunnel (FD1 : Heli2 enlève la colline et les montre)");
        L("void Techniciens()");
        L("{");
        Roof("\t", "Toit_Tunnel");
        Show("\t", TrainAct3Victims);
        Cin("\t", "CUT_VICTIMES_TUNNEL");
        L("}");
        L("");
        // ---- victimes et fin ----
        L("// civil mis à l'abri (rappel du moteur)");
        L("void OnCivilianSaved(EntityID Civil)");
        L("{");
        foreach (string c in TrainAct1Victims.Append("Palefrenier").Concat(TrainAct3Victims))
        {
            L($"\tif(Civil == ENT_FindByName(\"{c}\"))");
            L($"\t\tSCR_SetVarS32(\"Sauve_{c}\", 1);");
        }
        L("\tif(Civil == ENT_FindByName(\"Palefrenier\") && SCR_GetVarS32(\"Acte\") == 2)");
        L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Palefrenier\"), 1);");
        L("}");
        L("void Train_Defaite()");
        L("{");
        L("\tLEV_TriggerDefeat();");
        L("}");
        L("// victoire (FD1 : Victory_OnTimer) : texte selon les victimes, les maisons détruites (plus de 3) et la pollution");
        L("void Train_Victoire()");
        L("{");
        L("\ts32 maisons;");
        L("\ts32 morts;");
        L("\ts32 pollue;");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Acte3\"), 1);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Feux3\"), 1);");
        L($"\tmaisons = {Destroyed()};");
        L("\tmorts = 0;");
        L("\tif(LEV_GetNumVictims() > 1)");
        L("\t\tmorts = 1;");
        L("\tpollue = 0;");
        L($"\tif(SCR_GetVarF32(\"Pollution\") > {TrainPollutionMax / 2}.0)");
        L("\t\tpollue = 1;");
        L("\telse");
        L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Pollution\"), 1);");
        // FD1 : VICTORY1 à 8 selon (victimes, maisons, pollution)
        foreach ((int dead, int many, int polluted, string key) in new[]
        {
            (0, 0, 0, "VICTORY1"), (0, 1, 0, "VICTORY2"), (1, 0, 0, "VICTORY3"), (0, 0, 1, "VICTORY4"),
            (1, 1, 0, "VICTORY5"), (0, 1, 1, "VICTORY6"), (1, 0, 1, "VICTORY7"), (1, 1, 1, "VICTORY8"),
        })
        {
            string houseTest = many == 1 ? "maisons > 3" : "maisons <= 3";
            L($"\tif(morts == {dead} && {houseTest} && pollue == {polluted})");
            L($"\t\tLEV_SetDebriefText(\"{T(key)}\");");
        }
        L("\tLEV_TriggerVictory();");
        L("}");
        L("");
        // ---- maisons ----
        L("// les maisons du village (FD1 : CheckMaisons) : en feu (message), détruites quand leur feu dépasse la limite (elles");
        L("// s'embrasent alors toutes entières) ; trop de maisons en feu à la fois : un avertissement");
        L("void Train_Maisons()");
        L("{");
        L("\ts32 g;");
        L("\ts32 enfeu;");
        L("\tf32 feu;");
        L("\tenfeu = 0;");
        for (int k = 1; k <= TrainHouses.Length; k++)
        {
            L($"\tg = SCR_GetVarS32(\"M_{k}\");");
            L("\tfeu = Feu(g);");
            L($"\tif(feu > 10.0 && SCR_GetVarS32(\"Maison{k}Detruite\") == 0 && SCR_GetVarS32(\"Maison{k}EnFeu\") == 0)");
            L("\t{");
            L($"\t\tSCR_SetVarS32(\"Maison{k}EnFeu\", 1);");
            Msg("\t\t", "EVENT_TRA_MAISON");
            L("\t}");
            L("\tif(feu <= 0.0)");
            L($"\t\tSCR_SetVarS32(\"Maison{k}EnFeu\", 0);");
            L($"\tif(feu > {TrainHouses[k - 1].limit}.0 && SCR_GetVarS32(\"Maison{k}Detruite\") == 0)");
            L("\t{");
            L($"\t\tSCR_SetVarS32(\"Maison{k}Detruite\", 1);");
            L("\t\tAllume(g, g);");
            L("\t}");
            L($"\tenfeu = enfeu + SCR_GetVarS32(\"Maison{k}EnFeu\");");
        }
        L("\tif(enfeu >= 4 && SCR_GetVarS32(\"TropMaisons\") == 0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"TropMaisons\", 1);");
        Msg("\t\t", "EVENT_TRA_TROPMAISONS");
        L("\t}");
        L("\tif(enfeu < 4)");
        L("\t\tSCR_SetVarS32(\"TropMaisons\", 0);");
        L("}");
        L("");
        L("// boucle : pollution, défaites, cinématiques, maisons, victimes et feux de l'acte en cours");
        L("void Train_Main()");
        L("{");
        L("\ts32 n;");
        L("\tf32 pollution;");
        L("\tif(SCR_GetVarS32(\"Fin\") != 0)");
        L("\t\treturn;");
        // pollution (FD1 : CheckPollution) : la boucle tourne toutes les StationLoopPeriod s
        L($"\tpollution = SCR_GetVarF32(\"Pollution\") + (Feu(SCR_GetVarS32(\"G_4\")) + Feu(SCR_GetVarS32(\"G_{TrainPesticides}\")) + Feu(SCR_GetVarS32(\"G_28\")) + Feu(SCR_GetVarS32(\"G_29\"))) * {(StationLoopPeriod * TrainPollutionRate).ToString(System.Globalization.CultureInfo.InvariantCulture)};");
        L("\tSCR_SetVarF32(\"Pollution\", pollution);");
        L($"\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Pollution\"), pollution / {TrainPollutionMax / 100}.0);");
        L("\tif(pollution >= 500000.0 && SCR_GetVarS32(\"MsgPollution1\") == 0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"MsgPollution1\", 1);");
        Msg("\t\t", "EVENT_TRA_POLLUTION1");
        L("\t}");
        L("\tif(pollution >= 850000.0 && SCR_GetVarS32(\"MsgPollution3\") == 0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"MsgPollution3\", 1);");
        Msg("\t\t", "EVENT_TRA_POLLUTION3");
        L("\t}");
        L($"\tif(pollution >= {TrainPollutionMax}.0)");
        L("\t{");
        L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Pollution\"), 0);");
        Lost("\t\t", "DEFEAT5");
        L("\t}");
        L("\tif(LEV_IsHealVehicleRemaining() == 0)");
        L("\t{");
        Lost("\t\t", "DEFEAT4");
        L("\t}");
        L("\tif(LEV_IsFiremanSupplierRemaining() == 0)");
        L("\t{");
        Lost("\t\t", "DEFEAT3");
        L("\t}");
        // le wagon de gaz entamé par le feu (FD1 : CUT_MINICONVOI, sous 5 500 points de vie)
        L("\tif(SCR_GetVarS32(\"MiniConvoiVu\") == 0 && ENT_GetDamage(ENT_FindByName(\"MiniConvoi\")) < 90.0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"MiniConvoiVu\", 1);");
        Msg("\t\t", "TRA_CUT_MINICONVOI");
        L("\t}");
        // cinématiques des feux (FD1 : CheckCutScenes)
        L("\tif(SCR_GetVarS32(\"Pollution1Vu\") == 0 && Feu(SCR_GetVarS32(\"G_4\")) > 100.0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"Pollution1Vu\", 1);");
        Cin("\t\t", "CUT_POLLUTION1");
        L("\t}");
        L($"\tif(SCR_GetVarS32(\"SilosVu\") == 0 && Feu(SCR_GetVarS32(\"G_{TrainSilos}\")) > 1.0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"SilosVu\", 1);");
        Cin("\t\t", "CUT_SILOS");
        L("\t}");
        L($"\tif(SCR_GetVarS32(\"Pollution2Vu\") == 0 && Feu(SCR_GetVarS32(\"G_{TrainPesticides}\")) > 1.0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"Pollution2Vu\", 1);");
        Cin("\t\t", "CUT_POLLUTION2");
        Roof("\t\t", "ToitDecache");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"PalefrenierVu\") == 0 && Feu(SCR_GetVarS32(\"G_26\")) > 10.0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"PalefrenierVu\", 1);");
        Cin("\t\t", "CUT_PALEFRENIER");
        L("\t}");
        L("\tTrain_Maisons();");
        L("\tif(SCR_GetVarS32(\"Acte\") == 1)");
        L("\t{");
        L($"\t\tn = {Saved(TrainAct1Victims)};");
        L("\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Acte1\"), n);");
        L($"\t\tif(n >= {TrainAct1Victims.Length} && Feu(SCR_GetVarS32(\"G_15\")) <= 0.0)");
        L("\t\t\tTrain_Acte2();");
        L("\t\treturn;");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Acte\") == 2)");
        L("\t{");
        L("\t\tif(SCR_GetVarS32(\"Sauve_Palefrenier\") == 1 && Feu(SCR_GetVarS32(\"G_27\")) <= 300.0)");
        L("\t\t\tTrain_Acte3();");
        L("\t\treturn;");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Acte\") == 3)");
        L("\t{");
        L($"\t\tn = {Saved(TrainAct3Victims)};");
        L("\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Acte3\"), n);");
        L($"\t\tif(n >= {TrainAct3Victims.Length} && Feu(SCR_GetVarS32(\"G_29\")) <= 0.0 && Feu(SCR_GetVarS32(\"G_30\")) <= 200.0)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Fin\", 1);");
        L("\t\t\tSCR_RunDelayed(\"Train_Victoire\", 5.0);");
        L("\t\t}");
        L("\t}");
        L("}");
        L("");
        return (f.ToString(), "\tTrain_Init();\r\n", "\tTrain_Main();\r\n");
    }
}
