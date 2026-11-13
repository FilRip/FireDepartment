using System.Numerics;
using System.Text;
using OpenFD.Formats;

namespace OpenFD.ConvFD1;

/// <summary>
/// Centrale nucléaire (scripts FD1 Centrale_Main / _Scenar / _Event / _Trigger) : « La centrale nucléaire ».
///  • acte 1 : entrer dans la salle de sécurité (barrière et grandes portes : boutons rouges), sauver le chef de la sécurité,
///    éteindre l'incendie de la zone sécurisée ; les vestiaires enfumés : ventiler (l'ingénieur à la ventilation, ou le
///    ventilateur du mur cassé) avant d'ouvrir le sas 2, sinon backdraft ;
///  • acte 2 : la salle des turbines (à 15 m, par les échelles) ; la turbine 1 fuit (sa vanne coupe la fuite) ; couper le
///    refroidissement d'une turbine qui tourne la fait exploser ; trois turbines détruites : défaite ;
///  • acte 3 : la tranche électrique brûle sous tension ; couper les quatre sections (coupe-circuits), sauver l'ingénieur de
///    la salle de commande, éteindre ;
///  • acte 4 : les portes blindées du réacteur s'ouvrent ; une pompe primaire explose, son générateur de vapeur la suit ;
///    les pompiers aux lances à poudre (agent anti-métal) arrivent pour les feux de sodium ; plus de cinq générateurs
///    détruits : le cœur explose (défaite). Victoire : texte selon les turbines et les générateurs détruits.
/// Les groupes de foyers FD1 ne sont pas lisibles dans le niveau : ils sont reconstitués par zone (CentraleGroupOf).
/// </summary>
public sealed partial class MissionConverter
{
    private const string CentraleMission = "Centrale_Nucleaire";

    /// <summary>
    /// Groupes de foyers (numéros FD1 quand on les connaît) : 0 les vestiaires enfumés, 2 le backdraft, 3 la salle de
    /// sécurité, 4 la salle des turbines, 7 la turbine 1 qui fuit, 8 et 9 les turbines 2 et 3, 10 et 11 leurs fuites d'huile,
    /// 14 la tranche électrique, 19 à 22 ses quatre sections sous tension, 40 le réacteur ; 61 à 68 les feux de sodium autour
    /// des pompes 1 à 8, 71 à 78 ceux des générateurs 1 à 8.
    /// </summary>
    private const int CenLockers = 0, CenBackdraft = 2, CenGuardRoom = 3, CenHall = 4, CenTurbine1 = 7, CenTurbine2 = 8, CenTurbine3 = 9;
    private const int CenLeak2 = 10, CenLeak3 = 11, CenElectric = 14, CenSection1 = 19, CenReactor = 40, CenPump1 = 61, CenGenerator1 = 71;

    /// <summary>Échelles métalliques FD1 : bout en haut (plancher à 15 m), bout en bas (sol), cm.</summary>
    private static readonly (Vector3 top, Vector3 bottom)[] CentraleLadders =
    {
        (new(11815, 13131, 1500), new(11815, 13531, 0)), (new(16872, 13812, 1500), new(16472, 13812, 0)),
        (new(15133, 5317, 1500), new(15533, 5317, 0)), (new(21563, 3130, 1500), new(21563, 3530, 0)),
        (new(15133, 12567, 1500), new(15533, 12567, 0)), (new(2133, 12818, 1500), new(2533, 12818, 0)),
        (new(21684, 14875, 1500), new(21684, 14475, 0)), (new(3242, 10682, 1500), new(2842, 10682, 0)),
        (new(17127, 14444, 1500), new(17527, 14444, 0)), (new(2566, 5140, 1500), new(2566, 5540, 0)),
        (new(4562, 10883, 1500), new(4562, 11283, 0)), (new(9309, 5134, 1500), new(9309, 5534, 0)),
        (new(9313, 7246, 1500), new(9313, 6846, 0)),
    };

    private static readonly Vector2 CenBack = new Vector2(5370, 13230);
    private static readonly Vector2 CenTurbine1At = new Vector2(5008, 8636);
    private static readonly Vector2 CenTurbine2At = new Vector2(7055, 9082);
    private static readonly Vector2 CenTurbine3At = new Vector2(9055, 9081);

    /// <summary>Flaques d'huile des fuites des turbines 2 et 3 (FD1 : T2F1 à 11, T3F1 à 8) : leurs feux prennent quand la turbine casse.</summary>
    private static readonly Vector2[] CenLeak2At =
    {
        new(6245, 7538), new(6366, 7908), new(6681, 7520), new(7202, 10455), new(6619, 10296), new(6486, 10778), new(7025, 10652),
        new(6239, 11390), new(6540, 11890), new(7176, 11548), new(6714, 11440),
    };
    private static readonly Vector2[] CenLeak3At =
    {
        new(9151, 10668), new(8658, 10705), new(8827, 10339), new(9094, 11367), new(8651, 11347), new(8852, 11722), new(8390, 11735), new(8850, 12100),
    };

    /// <summary>Coupe-circuits des quatre sections de la tranche électrique (FD1 : ce0 à ce3).</summary>
    private static readonly Vector2[] CenBreakers = { new(12569, 11031), new(12576, 7003), new(13531, 11048), new(13532, 6988) };

    /// <summary>Pompes primaires (sol) et générateurs de vapeur (15 m) du réacteur, 1 à 8.</summary>
    private static readonly Vector2[] CenPumps =
    {
        new(17308, 13301), new(18495, 13291), new(19464, 13293), new(20666, 13294), new(17300, 4649), new(18506, 4657), new(19470, 4657), new(20672, 4647),
    };
    private static readonly Vector2[] CenGenerators =
    {
        new(17371, 9475), new(18596, 10641), new(19527, 10634), new(20700, 9401), new(17373, 8505), new(18575, 7303), new(19528, 7277), new(20711, 8458),
    };

    /// <summary>Points des plans de la cinématique de l'acte 2 : la salle des turbines prend feu autour d'eux.</summary>
    private static readonly Vector2[] CenHallFires = { new(2738, 11572), new(2320, 9021), new(3910, 8033), new(7622, 8735) };

    /// <summary>Délais (s) : la machine de la salle de sécurité saute, les actes s'enchaînent, la pompe 1 saute, le renfort.</summary>
    private const int CenStartBlast = 8, CenActPause = 3, CenPumpBlast = 26, CenReinforcement = 38;

    /// <summary>Points de vie qu'un générateur (ou une pompe) perd par passage de la boucle quand son vis-à-vis est détruit (5 s : 1 min).</summary>
    private const float CenDecay = 0.34f;

    /// <summary>Victimes de la centrale.</summary>
    private static readonly string[] CenVictims = { "Garde1", "Scientifique" };

    /// <summary>Groupes qui attendent le script (FD1 : Init_HPScenars).</summary>
    private static bool CenWaiting(int g) => g is CenBackdraft or CenHall or CenTurbine1 or CenLeak2 or CenLeak3 or CenElectric or CenReactor
        || (g >= CenSection1 && g < CenSection1 + 4) || (g >= CenPump1 && g < CenPump1 + 8) || (g >= CenGenerator1 && g < CenGenerator1 + 8);

    private bool IsCentrale => Mission.Equals(CentraleMission, StringComparison.OrdinalIgnoreCase);

    /// <summary>Feu de sodium (FD1 : seules les lances à poudre l'éteignent) : feu de type D du jeu (agent anti-métal).</summary>
    private bool SodiumFire(string type) => IsCentrale && type.Contains("sodium", StringComparison.OrdinalIgnoreCase);

    /// <summary>Feu électrique de la tranche électrique (FD1 : « feux_type_ix_elec »).</summary>
    private bool CentraleElectric(string type) => IsCentrale && type.Contains("ix_elec", StringComparison.OrdinalIgnoreCase);

    /// <summary>Groupe de ce foyer de la centrale (−1 : aucun).</summary>
    private int CentraleGroupOf(Vector3 at, string type)
    {
        if (!IsCentrale)
            return -1;
        Vector2 p = new Vector2(at.X, at.Y);
        bool up = at.Z > 800;
        if (!up)
        {
            for (int i = 0; i < CenPumps.Length; i++)
                if (Vector2.Distance(p, CenPumps[i]) < 1500)
                    return CenPump1 + i;
            if (CenLeak2At.Any(q => Vector2.Distance(p, q) < 500))
                return CenLeak2;
            if (CenLeak3At.Any(q => Vector2.Distance(p, q) < 500))
                return CenLeak3;
            if (Vector2.Distance(p, CenBack) < 900)
                return CenBackdraft;
            if (In(p, 5900, 9100, 12400, 14500))
                return CenGuardRoom;
            if (In(p, 3000, 5950, 12900, 16000))
                return CenLockers;
            return -1;
        }
        if (p.X < 11000)
        {
            if (CenLeak2At.Any(q => Vector2.Distance(p, q) < 500))
                return CenLeak2;
            if (CenLeak3At.Any(q => Vector2.Distance(p, q) < 500))
                return CenLeak3;
            if (Vector2.Distance(p, CenTurbine2At) < 1300)
                return CenTurbine2;
            if (Vector2.Distance(p, CenTurbine3At) < 1300)
                return CenTurbine3;
            if (In(p, 3437, 6578, 6080, 11193))
                return CenTurbine1;
            return CenHall;
        }
        if (p.X < 15000)
        {
            if (CentraleElectric(type))
            {
                int best = 0;
                for (int i = 1; i < CenBreakers.Length; i++)
                    if (Vector2.Distance(p, CenBreakers[i]) < Vector2.Distance(p, CenBreakers[best]))
                        best = i;
                return CenSection1 + best;
            }
            return CenElectric;
        }
        for (int i = 0; i < CenGenerators.Length; i++)
            if (Vector2.Distance(p, CenGenerators[i]) < 600)
                return CenGenerator1 + i;
        return CenReactor;
    }

    /// <summary>Foyers allumés au départ de la centrale : la salle de sécurité, la fumée noire des vestiaires.</summary>
    private bool CentraleLitAtStart(Vector3 at, string type)
    {
        int g = CentraleGroupOf(at, type);
        if (g == CenLockers)
            return type.Contains("fover", StringComparison.OrdinalIgnoreCase);
        return g == CenGuardRoom && Ignites(type);
    }

    /// <summary>Ce foyer prend-il quand le script allume son groupe (A_n) ?</summary>
    private static bool CentraleIgnites(int g, Vector3 at, string type)
    {
        Vector2 p = new Vector2(at.X, at.Y);
        if (g is CenBackdraft or CenLeak2 or CenLeak3 || (g >= CenPump1 && g < CenPump1 + 8) || (g >= CenGenerator1 && g < CenGenerator1 + 8)
            || (g >= CenSection1 && g < CenSection1 + 4))
            return true;
        if (g == CenTurbine1)
            return Ignites(type);
        if (g == CenHall)
            return Ignites(type) && CenHallFires.Any(q => Vector2.Distance(p, q) < 1200);
        if (g == CenElectric)
            return Ignites(type) && CenBreakers.Any(q => Vector2.Distance(p, q) < 1200);
        if (g == CenReactor)
            return type.StartsWith("torche", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    /// <summary>Échelles métalliques de la centrale dans le niveau du moteur : des escaliers (« teleport_stair » et ses deux icônes), toujours ouverts.</summary>
    private void AddCentraleLadders(Level level)
    {
        if (!IsCentrale)
            return;
        LevelObject? stair = TemplateObject("teleport_stair");
        LevelObject? symbol = TemplateObject("stair_symbol");
        if (stair == null || symbol == null)
        {
            Report.Add("échelles : escalier modèle introuvable dans les missions du jeu");
            return;
        }
        int k = 0;
        foreach ((Vector3 top, Vector3 bottom) in CentraleLadders)
        {
            k++;
            string name = $"Echelle{k}";
            LevelObject t = LevelEdit.Clone(stair, LevelEdit.NewId(level), name, stair.ClassPath);
            t = LevelEdit.WithTransform(t, (top + bottom) / 2, Vector3.Zero);
            t = LevelEdit.WithStartVisible(t, true);
            // l'escalier modèle (une mission du jeu) appelle « Enter_NavigationRoom » : pas de fonction du script ici
            t = WithComponentScript(t, 0xA02B, "");
            level.Objects.Add(t);
            foreach ((string end, Vector3 at) in new[] { ("haut", top), ("bas", bottom) })
            {
                LevelObject e = LevelEdit.Clone(symbol, LevelEdit.NewId(level), $"{name}_{end}", symbol.ClassPath);
                e = LevelEdit.WithTransform(e, at, Vector3.Zero);
                e = LevelEdit.WithStartVisible(e, true);
                level.Objects.Add(e);
            }
        }
        Report.Add($"échelles : {k} (escaliers entre le sol et les planchers à 15 m)");
    }

    /// <summary>Script de la centrale (fonctions, départ, boucle).</summary>
    private (string functions, string start, string main) CentraleScript(Fd1Level fd1)
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
            L($"{indent}SCR_RunDelayed(\"Centrale_Defaite\", 5.0);");
            L($"{indent}return;");
        }
        void Open(string indent, string door)
        {
            L($"{indent}if(ENT_GetState(ENT_FindByName(\"{door}\")) != \"Open\")");
            L($"{indent}\tENT_SwitchState(ENT_FindByName(\"{door}\"));");
        }
        string G(int g) => $"SCR_GetVarS32(\"G_{g}\")";
        string A(int g) => $"SCR_GetVarS32(\"A_{g}\")";
        // groupes : tous les foyers (G_n), ceux qui prennent quand le script allume le groupe (A_n)
        Dictionary<int, List<string>> all = new Dictionary<int, List<string>>();
        Dictionary<int, List<string>> ignite = new Dictionary<int, List<string>>();
        for (int i = 0; i < fd1.Fires.Count; i++)
        {
            Vector3 at = fd1.Fires[i].at * WorldToCm;
            string type = fd1.Fires[i].type;
            string h = $"foyer_{i + 1}";
            int g = CentraleGroupOf(at, type);
            if (g < 0)
                continue;
            if (!all.TryGetValue(g, out List<string>? list))
                all[g] = list = new List<string>();
            list.Add(h);
            if (!CentraleIgnites(g, at, type))
                continue;
            if (!ignite.TryGetValue(g, out List<string>? il))
                ignite[g] = il = new List<string>();
            il.Add(h);
        }
        Report.Add($"script : groupes de foyers {string.Join(", ", all.OrderBy(g => g.Key).Select(g => $"{g.Key} ({g.Value.Count}, {(ignite.TryGetValue(g.Key, out List<string>? il) ? il.Count : 0)} allumés par le script)"))}");
        string Saved(IEnumerable<string> names) => string.Join(" + ", names.Select(n => $"SCR_GetVarS32(\"Sauve_{n}\")"));
        string HallFire() => string.Join(" + ", new[] { CenHall, CenTurbine1, CenTurbine2, CenTurbine3, CenLeak2, CenLeak3 }.Select(g => $"Feu({G(g)})"));
        string SectionsFire() => string.Join(" + ", Enumerable.Range(CenSection1, 4).Select(g => $"Feu({G(g)})"));

        L("// ===== Centrale nucléaire : déroulement (traduit des scripts FD1) =====");
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
        L("void Centrale_Init()");
        L("{");
        L("\ts32 g;");
        L("\tSCR_SetVarS32(\"Acte\", 1);");
        // une turbine est déjà perdue (FD1 : NbTurbDetruits = 1, la turbine 1)
        L("\tSCR_SetVarS32(\"NbTurb\", 1);");
        foreach ((int g, List<string> fires) in all.OrderBy(x => x.Key))
        {
            L($"\tg = ENT_GroupCreate(\"groupe_{g}\");");
            foreach (string h in fires)
                L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
            L($"\tSCR_SetVarS32(\"G_{g}\", g);");
            if (CenWaiting(g))
                L("\tFIR_HPEnable(g, 0);");
            if (!ignite.TryGetValue(g, out List<string>? il))
                continue;
            L($"\tg = ENT_GroupCreate(\"allume_{g}\");");
            foreach (string h in il)
                L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
            L($"\tSCR_SetVarS32(\"A_{g}\", g);");
        }
        Cin("\t", "CUT_ARRIVEE");
        L($"\tSCR_SetVarS32(\"Obj_Entree\", LEV_AddObjectiveBoolean(\"{T("CENTRALE_DES_S1O0")}\"));");
        L($"\tSCR_SetVarS32(\"Obj_Garde\", LEV_AddObjectiveBoolean(\"{T("CENTRALE_DES_S1O1_SEUL")}\"));");
        L($"\tSCR_SetVarS32(\"Obj_Feux1\", LEV_AddObjectiveBoolean(\"{T("CENTRALE_DES_S1O2")}\"));");
        L($"\tSCR_RunDelayed(\"ExploDebut\", {CenStartBlast}.0);");
        L("\tSCR_RunDelayed(\"EnleveToit1\", 12.0);");
        L("}");
        L("void ExploDebut()");
        L("{");
        L("\tENT_SetDamage(ENT_FindByName(\"ExploDebut\"), 0.0);");
        L("}");
        L("void EnleveToit1()");
        L("{");
        Roof("\t", "Toit1");
        L("}");
        L("");
        // ---- acte 1 : portes, sas, ventilation, backdraft ----
        L("// le bouton de la loge : la barrière et les trois grandes portes de l'entrée s'ouvrent (FD1 : GPorteAll)");
        L("void GPorteAll()");
        L("{");
        Open("\t", "Barriere1");
        Cin("\t", "CUT_OUVREDEBUT");
        L("\tSCR_RunDelayed(\"GPorteAllBis\", 2.0);");
        L("}");
        L("void GPorteAllBis()");
        L("{");
        foreach (string d in new[] { "Porte1", "Porte2", "Porte3" })
            Open("\t", d);
        L("}");
        L("// le bouton du garage : ses trois grandes portes s'ouvrent (FD1 : GPorteAll2)");
        L("void GPorteAll2()");
        L("{");
        foreach (string d in new[] { "Porte10", "Porte11", "Porte12" })
            Open("\t", d);
        L("}");
        L("// la ventilation des vestiaires (FD1 : VentIngenieur, l'ingénieur ; ou le ventilateur du mur cassé, doorOnBreak0) : la");
        L("// fumée s'en va, plus de backdraft à l'ouverture du sas 2");
        L("void VentIngenieur()");
        L("{");
        L("\tVentile();");
        L("}");
        L("void Ventile()");
        L("{");
        L("\tSCR_SetVarS32(\"NbVent\", SCR_GetVarS32(\"NbVent\") + 1);");
        L("\tif(SCR_GetVarS32(\"Backdraft\") == 1)");
        L("\t\treturn;");
        L($"\tif(SCR_GetVarS32(\"NbVent\") >= 2)");
        L($"\t\tCroissance({G(CenLockers)}, -3.0);");
        L("\telse");
        L($"\t\tCroissance({G(CenLockers)}, -1.0);");
        L("}");
        L("// le sas 2 des vestiaires (FD1 : Sas2) : sans ventilation, le backdraft");
        L("void Sas2()");
        L("{");
        Open("\t", "Sas2");
        L("\tif(SCR_GetVarS32(\"NbVent\") == 0 && SCR_GetVarS32(\"Backdraft\") == 0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"Backdraft\", 1);");
        L($"\t\tCroissance({G(CenLockers)}, -3.0);");
        L("\t\tENT_SetDamage(ENT_FindByName(\"Back\"), 0.0);");
        L("\t\tSCR_RunDelayed(\"BackCut\", 1.0);");
        L("\t}");
        L("}");
        L("void BackCut()");
        L("{");
        Cin("\t", "CUT_BACKDRAFT");
        L($"\tAllume({G(CenBackdraft)}, {A(CenBackdraft)});");
        L("}");
        L("// on entre dans la salle de sécurité (FD1 : Acte1Cache3)");
        L("void EntreeSecurite(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tif(SCR_GetVarS32(\"EntreeVu\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"EntreeVu\", 1);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Entree\"), 1);");
        L("}");
        L("// le couloir de la salle de sécurité : le chef de la sécurité paraît (FD1 : Acte1Cache4)");
        L("void GardeVu(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tif(SCR_GetVarS32(\"GardeVu\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"GardeVu\", 1);");
        Show("\t", "Garde1");
        Cin("\t", "CUT_GARDE1");
        L("}");
        L("");
        // ---- acte 2 : turbines ----
        L("// une turbine casse dans le feu (FD1 : CheckExplosions ; le moteur n'appelle pas le script d'un objet qui explose :");
        L("// la boucle surveille sa vie) : sa fuite d'huile prend feu");
        foreach ((int k, int leak) in new[] { (2, CenLeak2), (3, CenLeak3) })
        {
            L($"void TurbineDetruite{k}()");
            L("{");
            L($"\tif(SCR_GetVarS32(\"T{k}Explo\") == 0)");
            L("\t\tSCR_SetVarS32(\"NbTurb\", SCR_GetVarS32(\"NbTurb\") + 1);");
            L($"\tSCR_SetVarS32(\"T{k}Explo\", 1);");
            L($"\tAllume({G(leak)}, {A(leak)});");
            L("}");
        }
        L("// la vanne du refroidissement de la turbine 1 (FD1 : Turbine1Stop) : la fuite s'arrête");
        L("void Turbine1Stop()");
        L("{");
        L($"\tCroissance({G(CenTurbine1)}, -40.0);");
        L("}");
        foreach ((int k, int leak) in new[] { (2, CenLeak2), (3, CenLeak3) })
        {
            L($"// la vanne de la turbine {k} (FD1 : Turbine{k}Stop) : cassée, sa fuite s'arrête ; en marche, elle va exploser");
            L($"void Turbine{k}Stop()");
            L("{");
            L($"\tif(SCR_GetVarS32(\"T{k}Explo\") == 1)");
            L("\t{");
            L($"\t\tCroissance({G(leak)}, -0.25);");
            L("\t\treturn;");
            L("\t}");
            Cin("\t", "CUT_ALERTETURBINE");
            L("\tSCR_SetVarS32(\"NbTurb\", SCR_GetVarS32(\"NbTurb\") + 1);");
            L($"\tSCR_SetVarS32(\"T{k}Explo\", 2);");
            L($"\tSCR_RunDelayed(\"T{k}Boom\", 5.0);");
            L("}");
            L($"void T{k}Boom()");
            L("{");
            L($"\tENT_SetDamage(ENT_FindByName(\"Turbine{k}\"), 0.0);");
            L($"\tSCR_RunDelayed(\"T{k}Fuite\", 1.0);");
            L("}");
            L($"void T{k}Fuite()");
            L("{");
            L($"\tCroissance({G(leak)}, -0.25);");
            L("}");
        }
        L("");
        // ---- acte 3 : tranche électrique ----
        for (int k = 1; k <= 4; k++)
        {
            L($"// coupe-circuit de la section {k} (FD1 : CoupElec{k}) : ses feux ne sont plus sous tension");
            L($"void CoupElec{k}()");
            L("{");
            L($"\tif(SCR_GetVarS32(\"Coupe{k}\") == 1)");
            L("\t\treturn;");
            L($"\tSCR_SetVarS32(\"Coupe{k}\", 1);");
            L($"\tFIR_HPEnable({G(CenSection1 + k - 1)}, 0);");
            Msg("\t", "EVENT_CEN_COURANT");
            L("\tif(SCR_GetVarS32(\"Coupe1\") + SCR_GetVarS32(\"Coupe2\") + SCR_GetVarS32(\"Coupe3\") + SCR_GetVarS32(\"Coupe4\") == 4)");
            L("\t{");
            L("\t\tSCR_SetVarS32(\"CoupeTout\", 1);");
            L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Courant\"), 1);");
            L("\t}");
            L("}");
        }
        L("// on trouve l'ingénieur dans la salle de commande (FD1 : CUT_SCIENTIFIQUE)");
        L("void ScientifiqueVu(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tif(SCR_GetVarS32(\"Acte\") != 3 || SCR_GetVarS32(\"ScientifiqueVu\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"ScientifiqueVu\", 1);");
        Cin("\t", "CUT_SCIENTIFIQUE");
        L("}");
        L("");
        // ---- acte 4 : réacteur ----
        L("// une pompe primaire explose (FD1 : ExploMachine) : le sodium brûle, son générateur de vapeur se dégrade");
        for (int i = 1; i <= 8; i++)
        {
            L($"void PompeExplosee{i}()");
            L("{");
            L($"\tSCR_SetVarS32(\"EtatM{i}\", 1);");
            L($"\tAllume({G(CenPump1 + i - 1)}, {A(CenPump1 + i - 1)});");
            L($"\tif(SCR_GetVarS32(\"EtatG{i}\") == 0)");
            L($"\t\tSCR_SetVarS32(\"EtatG{i}\", 2);");
            L("}");
        }
        L("// un générateur de vapeur explose (FD1 : ExploGenerateur) : sa pompe se dégrade ; la température du cœur monte");
        for (int i = 1; i <= 8; i++)
        {
            L($"void GenerateurExplose{i}()");
            L("{");
            L($"\tSCR_SetVarS32(\"EtatG{i}\", 1);");
            L($"\tAllume({G(CenGenerator1 + i - 1)}, {A(CenGenerator1 + i - 1)});");
            L($"\tif(SCR_GetVarS32(\"EtatM{i}\") == 0)");
            L($"\t\tSCR_SetVarS32(\"EtatM{i}\", 2);");
            L("\tTemperatureCoeur();");
            L("}");
        }
        L("void TemperatureCoeur()");
        L("{");
        L("\ts32 n;");
        L("\tn = SCR_GetVarS32(\"NbGene\") + 1;");
        L("\tSCR_SetVarS32(\"NbGene\", n);");
        Msg("\t", "EVENT_CEN_GENEEXPLO");
        L("\tif(n == 3)");
        Msg("\t\t", "EVENT_CEN_TEMPCOEUR1");
        L("\tif(n == 4)");
        Msg("\t\t", "EVENT_CEN_TEMPCOEUR2");
        L("\tif(n == 5)");
        Msg("\t\t", "EVENT_CEN_TEMPCOEUR3");
        L("}");
        L("// le cœur explose (FD1 : Cut_ExploCoeur)");
        L("void ExploCoeur()");
        L("{");
        Cin("\t", "TEST_CUT");
        L("\tSCR_RunDelayed(\"Centrale_Defaite\", 10.0);");
        L("}");
        L("void ExploPompe1()");
        L("{");
        L("\tENT_SetDamage(ENT_FindByName(\"Machine1\"), 0.0);");
        L("}");
        L("void RenfortPoudre()");
        L("{");
        L("\tLEV_CallReinforcement(1);");
        L("}");
        L("void OuvreBlindee3()");
        L("{");
        Open("\t", "Blindee3");
        L("}");
        L("void OuvreBlindee2()");
        L("{");
        Open("\t", "Blindee2");
        Roof("\t", "Toit4");
        L("}");
        L("");
        // ---- actes ----
        L("// acte 2 (FD1 : transS1) : la salle des turbines brûle");
        L("void Centrale_Acte2()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 2);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Feux1\"), 1);");
        Roof("\t", "Toit2");
        L($"\tAllume({G(CenHall)}, {A(CenHall)});");
        L($"\tAllume({G(CenTurbine1)}, {A(CenTurbine1)});");
        Cin("\t", "CUT_ACTE2");
        L("\tSCR_SetVarF32(\"Acte2T\", UTL_GetGameTime() + 30.0);");
        L($"\tSCR_SetVarS32(\"Obj_Feux2\", LEV_AddObjectiveBoolean(\"{T("CENTRALE_DES_S2O0_SEUL")}\"));");
        L($"\tSCR_SetVarS32(\"Obj_Turbines\", LEV_AddObjectiveBoolean(\"{T("CENTRALE_DES_S2O1_SEUL")}\"));");
        L("}");
        L("// acte 3 (FD1 : transS2) : le feu a gagné la tranche électrique, sous tension ; l'ingénieur est porté manquant");
        L("void Centrale_Acte3()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 3);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Feux2\"), 1);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Turbines\"), 1);");
        Roof("\t", "Toit3");
        for (int k = 0; k < 4; k++)
            L($"\tAllume({G(CenSection1 + k)}, {A(CenSection1 + k)});");
        L($"\tAllume({G(CenElectric)}, {A(CenElectric)});");
        Show("\t", "Scientifique");
        Cin("\t", "CUT_ACTE3");
        L($"\tSCR_SetVarS32(\"Obj_Courant\", LEV_AddObjectiveBoolean(\"{T("CENTRALE_DES_S3O0")}\"));");
        L($"\tSCR_SetVarS32(\"Obj_Ingenieur\", LEV_AddObjectiveBoolean(\"{T("CENTRALE_DES_S3O1")}\"));");
        L($"\tSCR_SetVarS32(\"Obj_Feux3\", LEV_AddObjectiveBoolean(\"{T("CENTRALE_DES_S3O2_SEUL")}\"));");
        L("\tif(SCR_GetVarS32(\"CoupeTout\") == 1)");
        L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Courant\"), 1);");
        L("}");
        L("// acte 4 (FD1 : transS3) : les portes blindées du réacteur s'ouvrent ; une pompe primaire surchauffe ; le renfort");
        L("void Centrale_Acte4()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 4);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Feux3\"), 1);");
        Open("\t", "Blindee1");
        L("\tSCR_RunDelayed(\"OuvreBlindee3\", 2.0);");
        L("\tSCR_RunDelayed(\"OuvreBlindee2\", 4.0);");
        L($"\tAllume({G(CenReactor)}, {A(CenReactor)});");
        Cin("\t", "CUT_ACTE4");
        L($"\tSCR_RunDelayed(\"ExploPompe1\", {CenPumpBlast}.0);");
        L($"\tSCR_RunDelayed(\"RenfortPoudre\", {CenReinforcement}.0);");
        L($"\tSCR_SetVarS32(\"Obj_Generateurs\", LEV_AddObjectiveBoolean(\"{T("CENTRALE_DES_S4O0")}\"));");
        L($"\tSCR_SetVarS32(\"Obj_Feux4\", LEV_AddObjectiveBoolean(\"{T("CENTRALE_DES_S4O1_SEUL")}\"));");
        L("}");
        L("");
        // ---- victimes et fin ----
        L("// civil mis à l'abri (rappel du moteur)");
        L("void OnCivilianSaved(EntityID Civil)");
        L("{");
        foreach (string c in CenVictims)
        {
            L($"\tif(Civil == ENT_FindByName(\"{c}\"))");
            L($"\t\tSCR_SetVarS32(\"Sauve_{c}\", 1);");
        }
        L("\tif(Civil == ENT_FindByName(\"Garde1\"))");
        L("\t{");
        L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Garde\"), 1);");
        Msg("\t\t", "EVENT_CEN_RESPOSECUR");
        L("\t}");
        L("\tif(Civil == ENT_FindByName(\"Scientifique\"))");
        L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Ingenieur\"), 1);");
        L("}");
        L("void Centrale_Defaite()");
        L("{");
        L("\tLEV_TriggerDefeat();");
        L("}");
        L("// victoire (FD1 : Victory_OnTimer) : texte selon les générateurs (plus de 3) et les turbines (plus d'une) détruits");
        L("void Centrale_Victoire()");
        L("{");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Generateurs\"), 1);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Feux4\"), 1);");
        foreach ((string gTest, string tTest, string key) in new[]
        {
            ("< 4", "< 2", "VICTORY1"), ("> 3", "< 2", "VICTORY2"), ("< 4", "> 1", "VICTORY3"), ("> 3", "> 1", "VICTORY4"),
        })
        {
            L($"\tif(SCR_GetVarS32(\"NbGene\") {gTest} && SCR_GetVarS32(\"NbTurb\") {tTest})");
            L($"\t\tLEV_SetDebriefText(\"{T(key)}\");");
        }
        L("\tLEV_TriggerVictory();");
        L("}");
        L("// turbines, pompes et générateurs (FD1 : CheckExplosions, CheckCouple) : la boucle surveille leur vie (détruits : 0) ;");
        L("// celui dont le vis-à-vis est détruit se dégrade jusqu'à exploser");
        L("void Centrale_Couples()");
        L("{");
        L("\ts32 e;");
        foreach (int k in new[] { 2, 3 })
        {
            L($"\tif(SCR_GetVarS32(\"T{k}Explo\") != 1 && ENT_GetDamage(ENT_FindByName(\"Turbine{k}\")) <= 0.0)");
            L($"\t\tTurbineDetruite{k}();");
        }
        for (int i = 1; i <= 8; i++)
            foreach ((string state, string obj, string fn) in new[] { ($"EtatG{i}", $"Generateur{i}", $"GenerateurExplose{i}"), ($"EtatM{i}", $"Machine{i}", $"PompeExplosee{i}") })
            {
                L($"\tif(SCR_GetVarS32(\"{state}\") != 1)");
                L("\t{");
                L($"\t\te = ENT_FindByName(\"{obj}\");");
                L("\t\tif(ENT_GetDamage(e) <= 0.0)");
                L($"\t\t\t{fn}();");
                L($"\t\telse if(SCR_GetVarS32(\"{state}\") == 2)");
                L($"\t\t\tENT_SetDamage(e, ENT_GetDamage(e) - {CenDecay.ToString(System.Globalization.CultureInfo.InvariantCulture)});");
                L("\t}");
            }
        L("}");
        L("");
        L("// boucle : défaites, ventilation, pompes et générateurs, actes");
        L("void Centrale_Main()");
        L("{");
        L("\tif(SCR_GetVarS32(\"Fin\") != 0)");
        L("\t\treturn;");
        L("\tif(SCR_GetVarS32(\"NbGene\") > 5)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"Fin\", 2);");
        L($"\t\tLEV_SetDebriefText(\"{T("DEFEAT1")}\");");
        L("\t\tExploCoeur();");
        L("\t\treturn;");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"NbTurb\") >= 3)");
        L("\t{");
        Lost("\t\t", "DEFEAT5");
        L("\t}");
        L("\tif(LEV_GetNumVictims() >= 5)");
        L("\t{");
        Lost("\t\t", "DEFEAT2");
        L("\t}");
        // le ventilateur du mur des vestiaires cassé (FD1 : doorOnBreak0)
        L("\tif(SCR_GetVarS32(\"VentCasse\") == 0 && Cassee(\"Porte0\") == 1)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"VentCasse\", 1);");
        L("\t\tVentile();");
        L("\t}");
        L("\tCentrale_Couples();");
        L("\tif(SCR_GetVarS32(\"Acte\") == 1)");
        L("\t{");
        L($"\t\tif(Feu({G(CenGuardRoom)}) <= 0.0)");
        L("\t\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Feux1\"), 1);");
        L($"\t\tif(SCR_GetVarS32(\"Sauve_Garde1\") == 1 && Feu({G(CenGuardRoom)}) <= 0.0 && SCR_GetVarS32(\"Acte2Prevu\") == 0)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Acte2Prevu\", 1);");
        L($"\t\t\tSCR_RunDelayed(\"Centrale_Acte2\", {CenActPause}.0);");
        L("\t\t}");
        L("\t\treturn;");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Acte\") == 2)");
        L("\t{");
        L($"\t\tif(UTL_GetGameTime() >= SCR_GetVarF32(\"Acte2T\") && {HallFire()} < 350.0 && SCR_GetVarS32(\"Acte3Prevu\") == 0)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Acte3Prevu\", 1);");
        L($"\t\t\tSCR_RunDelayed(\"Centrale_Acte3\", {CenActPause}.0);");
        L("\t\t}");
        L("\t\treturn;");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Acte\") == 3)");
        L("\t{");
        L($"\t\tif({SectionsFire()} <= 0.0)");
        L("\t\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Feux3\"), 1);");
        L($"\t\tif(SCR_GetVarS32(\"Sauve_Scientifique\") == 1 && SCR_GetVarS32(\"CoupeTout\") == 1 && {SectionsFire()} <= 0.0 && SCR_GetVarS32(\"Acte4Prevu\") == 0)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Acte4Prevu\", 1);");
        L($"\t\t\tSCR_RunDelayed(\"Centrale_Acte4\", {CenActPause}.0);");
        L("\t\t}");
        L("\t\treturn;");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Acte\") == 4)");
        L("\t{");
        L($"\t\tif(Feu({G(CenReactor)}) <= 0.0 && SCR_GetVarS32(\"EtatG1\") == 1 && SCR_GetVarS32(\"NbGene\") < 6)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Fin\", 1);");
        L("\t\t\tSCR_RunDelayed(\"Centrale_Victoire\", 5.0);");
        L("\t\t}");
        L("\t}");
        L("}");
        L("");
        return (f.ToString(), "\tCentrale_Init();\r\n", "\tCentrale_Main();\r\n");
    }
}
