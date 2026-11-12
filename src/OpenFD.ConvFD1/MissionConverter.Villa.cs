using System.Numerics;
using System.Text;
using OpenFD.Formats;

namespace OpenFD.ConvFD1;

/// <summary>
/// Villa (scripts FD1 Villa_Main / _Scenar / _Event / _Trigger) :
///  • acte 1 : sauver les deux joueurs de tennis (Steve Roger, le milliardaire, et son ami) ; la remise près du garage,
///    les vieux bidons et les haies du tennis brûlent ;
///  • acte 2 : le renfort à pied (un pompier, un officier technique) ; le feu prend dans le garage (flaque d'huile) ;
///    éteindre le garage, et un ingénieur conduit l'Excalibur (voiture de collection) jusqu'à la route ; les bidons
///    explosent si le feu les détruit (le vent ravive alors les flammes) ;
///  • acte 3 : la réserve de chlore prend feu, sa fumée menace la villa ; sauver les trois invités de la villa (la
///    productrice, la journaliste, le cameraman), récupérer la bande master ; un ingénieur peut mettre la pompe de la
///    piscine en marche (une lance fixe à eau sans fin) ; si le feu gagne la villa, la mission est perdue ;
///  • acte 4 : la serre tropicale brûle (son dôme noir et ses vitres s'enlèvent) ; sauver Debby Love et le jardinier ;
///  • victoire : texte selon la bande master et les voitures perdues ; défaites : ambulance, milliardaire, Debby Love,
///    l'Excalibur, la villa.
/// Les groupes de foyers FD1 ne sont pas lisibles dans le niveau : ils sont reconstitués par zone (VillaGroups), d'après
/// les cinématiques, les zones FD1 (« GARAGE1 », « PAPOMPE », « SERRE_TROPICALE », « RZVILLA ») et l'emplacement des objets.
/// </summary>
public sealed partial class MissionConverter
{
    /// <summary>
    /// Groupes de foyers de la Villa (numéros du script FD1 ; 32 : la remise, partie du groupe 33 du garage), zones (cm :
    /// x min, x max, y min, y max), dans l'ordre où on les essaie (un foyer va au premier groupe qui le contient).
    /// </summary>
    private static readonly (int group, float x0, float x1, float y0, float y1)[] VillaGroups =
    {
        // les vieux bidons au sud du garage
        (36, 5200, 6150, 5550, 6450),
        // la réserve de chlore (le cabanon près de la piscine), puis ses abords (« groupe de l'entrée »)
        (37, 10100, 10900, 12800, 13550),
        (39, 9400, 11600, 12200, 14400),
        // le garage (zone FD1 « GARAGE1 »), puis la remise à côté (zone « GARAGE2 » et ses torches)
        (34, 4919, 7125, 6930, 10072),
        (32, 7000, 8100, 6900, 9950),
        // la villa (zone FD1 « RZVILLA ») ; autour d'elle, le groupe qui la menace (FD1 : 41)
        (40, 10707, 15166, 15729, 19533),
        (41, 10200, 15700, 15200, 20000),
        // la serre tropicale (zone FD1 « SERRE_TROPICALE »), puis ses abords
        (31, 3986, 5940, 15043, 19970),
        (42, 3300, 6800, 14300, 20300),
        // le jardin entre la réserve de chlore et la villa
        (38, 8800, 11100, 14400, 15700),
        // les haies et la pelouse du tennis
        (28, 10200, 15300, 5100, 9250),
        // la pelouse entre le garage, le tennis et la piscine (le vent y pousse le feu : FD1 « FeuPartout »)
        (35, 7500, 15300, 9250, 11000),
    };

    /// <summary>Groupes FD1 de la Villa qui attendent le script (Init_HPScenars) : ils ne brûlent pas avant.</summary>
    private static readonly int[] VillaWaitingGroups = { 34, 35, 37, 38, 39, 31, 42, 40 };

    /// <summary>Intensité donnée aux foyers d'un groupe que le script allume.</summary>
    private const int VillaGroupStart = 20;

    /// <summary>Somme des intensités du groupe 41 (autour de la villa) : l'alerte, puis la villa prend feu (FD1 : 50, 1000).</summary>
    private const int VillaDangerAlert = 150;
    private const int VillaDangerLost = 1000;

    /// <summary>Victimes de chaque acte de la Villa (FD1 : CheckScenario1, 3, 4).</summary>
    private static readonly string[] VillaAct1Victims = { "Milliardaire", "Tennisman" };
    private static readonly string[] VillaAct3Victims = { "Productrice", "Journaliste", "Cameraman" };
    private static readonly string[] VillaAct4Victims = { "PopStar", "Jardinier" };

    /// <summary>Voitures de collection de la Villa (la 1 : l'Excalibur, à conduire jusqu'à la route).</summary>
    private static readonly string[] VillaCars = { "VoitureDeLuxe1", "VoitureDeLuxe2", "VoitureDeLuxe3", "VoitureDeLuxe4", "VoitureDeLuxe5" };

    /// <summary>Toits FD1 de la Villa enlevés par le script (nom de l'instance → nœuds « …_ROOF » du décor).</summary>
    private readonly Dictionary<string, List<string>> _roofNodes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Modèles convertis à l'avance pour les unités posées à part (nom FD1 de l'instance → modèle, maillages).</summary>
    private readonly Dictionary<string, (string model, List<Fd3Mesh> meshes)> _unitModels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Surfaces FD1 sous le sol, montées juste au-dessus (sinon le sol du moteur les couvre), par mission : modèle, matériau,
    /// hauteur (cm). Villa : l'eau de la piscine (28 cm sous le sol dans FD1, qui dessine le sol sans le cacher).
    /// </summary>
    private static readonly Dictionary<string, (string model, string material, float z)[]> SurfaceLifts = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Villa", new[] { ("VIL_PISCINE", "fd1_water", 3f) } },
    };

    /// <summary>Monte les surfaces de SurfaceLifts de ce modèle FD1 à leur hauteur.</summary>
    private void LiftSurfaces(string model, List<Fd3Mesh> meshes)
    {
        if (!SurfaceLifts.TryGetValue(Mission, out (string model, string material, float z)[]? lifts))
            return;
        foreach ((string m, string material, float z) in lifts)
        {
            if (!m.Equals(model, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (Fd3Mesh mesh in meshes)
                foreach ((string mat, int start, int count) in mesh.Groups)
                {
                    if (!mat.Equals(material, StringComparison.OrdinalIgnoreCase))
                        continue;
                    for (int i = start; i < start + count; i++)
                        mesh.Positions[3 * mesh.Indices[i] + 2] = z;
                }
        }
    }

    /// <summary>Le foyer FD1 brûle-t-il de lui-même (pas de l'herbe : feux de type II et IV) ?</summary>
    private static bool Ignites(string type) =>
        !type.StartsWith("feux_type_ii", StringComparison.OrdinalIgnoreCase) && !type.StartsWith("feux_type_iv", StringComparison.OrdinalIgnoreCase);

    /// <summary>Groupe FD1 de ce foyer de la Villa (0 : aucun).</summary>
    private int VillaGroupOf(Vector3 at)
    {
        if (!Mission.Equals("Villa", StringComparison.OrdinalIgnoreCase))
            return 0;
        foreach ((int group, float x0, float x1, float y0, float y1) in VillaGroups)
            if (at.X >= x0 && at.X <= x1 && at.Y >= y0 && at.Y <= y1)
                return group;
        return 0;
    }

    /// <summary>
    /// Foyers allumés au départ de la Villa : la remise et les bidons (FD1 : la remise a explosé avant l'arrivée des
    /// pompiers), les haies du tennis (« bloqués par les flammes sur le terrain de tennis »).
    /// </summary>
    private bool VillaLitAtStart(Vector3 at, string type)
    {
        int g = VillaGroupOf(at);
        if (g == 32 || g == 36)
            return true;
        if (g == 28)
            return Ignites(type);
        return g == 0 && type.StartsWith("torche", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Modèles des unités posées à part faites d'objets FD1 (la pompe de la piscine, l'Excalibur) : convertis avant
    /// l'écriture des textures du décor, pour qu'elles y soient.
    /// </summary>
    private void PrepareUnitModels(Fd1Level level, ModelConverter models)
    {
        if (!LevelUnits.TryGetValue(Mission, out (string instance, string cls)[]? units))
            return;
        foreach ((string instance, string cls) in units)
        {
            if (!cls.StartsWith('@') || cls == "@helico")
                continue;
            Fd1Object? o = level.Objects.FirstOrDefault(x => instance.Equals(InstanceName(x), StringComparison.OrdinalIgnoreCase));
            if (o == null)
                continue;
            _unitModels[instance] = (o.Model, models.Convert(o.Model, "fd1_" + ModelConverter.Safe(o.Model).ToLowerInvariant()));
        }
    }

    /// <summary>
    /// Classe d'une unité posée à part faite d'un objet FD1 : « @pompe », la pompe de la piscine (une autopompe qui ne roule
    /// pas : sa lance éteint les foyers à sa portée, l'eau de la piscine ne manque jamais) ; « @excalibur », la voiture de
    /// collection (un véhicule sans équipage ni soins, que le joueur conduit). Rend son chemin (null : modèle absent).
    /// </summary>
    private string? WriteObjectUnitClass(string instance, string kind)
    {
        if (!_unitModels.TryGetValue(instance, out (string model, List<Fd3Mesh> meshes) m) || m.meshes.Count == 0)
            return null;
        (string safe, List<(string node, Matrix4x4 world)> _, float _) = WritePlaceableModel(m.model, m.meshes);
        string dir = Path.Combine(Data, "Design", "Unit", "Firevehicle");
        Directory.CreateDirectory(dir);
        string name = Name + "_" + kind.TrimStart('@');
        string cls = kind == "@pompe"
            ? $"""
              [#INCLUDE]
              FILE0 = "Veh_P_pumper_us.class"

              [DISPLAY]
              MODEL = ..\..\Placeable\{Name}\{safe}
              SHADOW =

              [MOVING]
              FORWARDSPEED = 0
              BACKWARDSPEED = 0

              [CAPACITIES_ACTIONS]
              Action2 = ""
              Action5 = ""
              Action6 = ""

              [CAPACITIES_RESERVE]
              RESERVEPOINTS = 1000000
              RESERVEMAX = 1000000

              [DAMAGE]
              FIREDAMAGECOEF = 0

              [ANIM_PLAYER]
              ANIM01_FILENAME =

              """
            : $"""
              [#INCLUDE]
              FILE0 = "Veh_P_ambulance_us.class"

              [DISPLAY]
              MODEL = ..\..\Placeable\{Name}\{safe}
              SHADOW =

              [CAPACITIES_ACTIONS]
              CANHEAL = 0
              ACTION5 = ""
              ACTION6 = ""

              [CAPACITIES_PROPS]
              EMBARKCAPACITY = 0

              [UNIT]
              PUBLICNAME = Excalibur

              [ANIM_PLAYER]
              ANIM01_FILENAME =

              """;
        File.WriteAllBytes(Path.Combine(dir, name + ".class"), Encoding.Latin1.GetBytes(cls.Replace("\n", "\r\n")));
        return "design\\unit\\firevehicle\\" + name;
    }

    /// <summary>Lignes du script qui enlèvent (0) ou remettent (1) un toit FD1 de la Villa (nom de l'instance).</summary>
    private IEnumerable<string> RoofLines(string instance, int visible) =>
        _roofNodes.TryGetValue(instance, out List<string>? nodes) ? nodes.Select(n => $"LEV_RoofVisibility(\"{n}\", {visible});") : Enumerable.Empty<string>();

    /// <summary>Script de la Villa (fonctions, départ, boucle).</summary>
    private (string functions, string start, string main) VillaScript(Fd1Level fd1)
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
        // groupes : tous les foyers (G_n) et ceux qui prennent quand le script allume le groupe (A_n : pas l'herbe)
        Dictionary<int, List<string>> all = new Dictionary<int, List<string>>();
        Dictionary<int, List<string>> ignite = new Dictionary<int, List<string>>();
        for (int i = 0; i < fd1.Fires.Count; i++)
        {
            int g = VillaGroupOf(fd1.Fires[i].at * WorldToCm);
            if (g == 0)
                continue;
            if (!all.TryGetValue(g, out List<string>? list))
                all[g] = list = new List<string>();
            list.Add($"foyer_{i + 1}");
            if (!Ignites(fd1.Fires[i].type))
                continue;
            if (!ignite.TryGetValue(g, out List<string>? il))
                ignite[g] = il = new List<string>();
            il.Add($"foyer_{i + 1}");
        }
        Report.Add($"script : groupes de foyers {string.Join(", ", all.OrderBy(g => g.Key).Select(g => $"{g.Key} ({g.Value.Count}, {(ignite.TryGetValue(g.Key, out List<string>? il) ? il.Count : 0)} qui prennent)"))}");

        L("// ===== Villa : déroulement (traduit des scripts FD1) =====");
        L("");
        L("// un groupe de foyers FD1 prend feu (SetHotpointGroupActivated(n, 1)) : il peut brûler, ses foyers qui ne sont pas de");
        L("// l'herbe prennent ; Eteint : il s'éteint et ne peut plus brûler");
        L("void Allume(s32 g, s32 a)");
        L("{");
        L("\tif(g != 0)");
        L("\t\tFIR_HPEnable(g, 1);");
        L("\tif(a != 0)");
        L($"\t\tFIR_HPSetIntensity(a, {VillaGroupStart}.0);");
        L("}");
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
        L("");
        L("void Villa_Init()");
        L("{");
        L("\ts32 g;");
        L("\tSCR_SetVarS32(\"Acte\", 1);");
        foreach ((int g, List<string> fires) in all.OrderBy(x => x.Key))
        {
            L($"\tg = ENT_GroupCreate(\"groupe_{g}\");");
            foreach (string h in fires)
                L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
            L($"\tSCR_SetVarS32(\"G_{g}\", g);");
            if (VillaWaitingGroups.Contains(g))
                L("\tFIR_HPEnable(g, 0);");
            if (!ignite.TryGetValue(g, out List<string>? il))
                continue;
            L($"\tg = ENT_GroupCreate(\"allume_{g}\");");
            foreach (string h in il)
                L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
            L($"\tSCR_SetVarS32(\"A_{g}\", g);");
        }
        foreach (string c in VillaAct1Victims)
            L($"\tENT_SetVisibility(ENT_FindByName(\"{c}\"), 1);");
        Cin("\t", "CUT_ARRIVEE");
        L($"\tSCR_SetVarS32(\"Obj_Acte1\", LEV_AddObjectiveIterate(\"{T("VILLA_DES_S1O0")}\", {VillaAct1Victims.Length}));");
        L("}");
        L("");
        // ---- zones ----
        L("// T9 : l'allée qui mène au tennis (FD1 : SEQUENCE1, les joueurs bloqués par les flammes)");
        L("void T9(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tif(SCR_GetVarS32(\"T9Vu\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"T9Vu\", 1);");
        Cin("\t", "SEQUENCE1");
        L("}");
        L("// T2 : le fond de la villa ; on y trouve la productrice, la journaliste et le cameraman, mais pas Debby Love (CUT_PABON)");
        L("void T2(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tif(SCR_GetVarS32(\"T2Vu\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"T2Vu\", 1);");
        foreach (string c in VillaAct3Victims)
            L($"\tENT_SetVisibility(ENT_FindByName(\"{c}\"), 1);");
        Cin("\t", "CUT_PABON");
        L("}");
        L("// T10 : la route devant la propriété ; l'Excalibur y est en sécurité (FD1 : CheckT10)");
        L("void T10(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tif(Unit != ENT_FindByName(\"Excalibur\") || SCR_GetVarS32(\"ExcaliburSauve\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"ExcaliburSauve\", 1);");
        Msg("\t", "EVENT_VIL_EXCALIBUR");
        L("\tif(SCR_GetVarS32(\"Acte\") == 2)");
        L("\t{");
        L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Excalibur\"), 1);");
        L("\t\tSCR_RunDelayed(\"Villa_Acte3\", 10.0);");
        L("\t}");
        L("}");
        L("");
        // ---- objets ----
        L("// l'Excalibur : un ingénieur la prend (il la « répare ») ; la voiture du décor laisse la place au véhicule à conduire");
        L("void Use_Excalibur(EntityID User, EntityID Bouton, mcString State)");
        L("{");
        L("\tif(SCR_GetVarS32(\"Conduite\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"Conduite\", 1);");
        L("\tENT_SetVisibility(ENT_FindByName(\"VoitureDeLuxe1\"), 0);");
        L("\tENT_SetVisibility(ENT_FindByName(\"Excalibur\"), 1);");
        L("}");
        L("// une voiture de collection détruite par le feu (l'Excalibur avant d'être sortie : la mission est perdue)");
        L("void VoitureDetruite(EntityID Voiture)");
        L("{");
        foreach (string car in VillaCars)
        {
            L($"\tif(Voiture == ENT_FindByName(\"{car}\") && SCR_GetVarS32(\"Detruite_{car}\") == 0)");
            L("\t{");
            L($"\t\tSCR_SetVarS32(\"Detruite_{car}\", 1);");
            if (car == VillaCars[0])
            {
                L("\t\tif(SCR_GetVarS32(\"Conduite\") == 1)");
                L("\t\t\treturn;");
            }
            L("\t\tSCR_SetVarS32(\"nbVoituresDetruites\", SCR_GetVarS32(\"nbVoituresDetruites\") + 1);");
            if (car == VillaCars[0])
                L("\t\tVilla_Perdu(\"DEFEAT_VOITURE\");");
            L("\t}");
        }
        L("}");
        L("// les portes du garage (FD1 : PorteGarage123, PorteGarage4et5)");
        L("void PortesGarage123()");
        L("{");
        L("\tif(SCR_GetVarS32(\"Portes123\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"Portes123\", 1);");
        foreach (string d in new[] { "PorteGarage1", "PorteGarage2", "PorteGarage3" })
            L($"\tENT_SwitchState(ENT_FindByName(\"{d}\"));");
        L("}");
        L("void PortesGarage45()");
        L("{");
        L("\tif(SCR_GetVarS32(\"Portes45\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"Portes45\", 1);");
        foreach (string d in new[] { "PorteGarage4", "PorteGarage5" })
            L($"\tENT_SwitchState(ENT_FindByName(\"{d}\"));");
        L("}");
        L("// les vieux bidons : détruits par le feu, ils explosent ; le feu repart autour, et le vent le pousse (FD1 : BarilsExplo)");
        L("void BarilsExplo(EntityID Barils)");
        L("{");
        L("\tif(SCR_GetVarS32(\"BarilsExp\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"BarilsExp\", 1);");
        L("\tFIR_HPSetIntensity(SCR_GetVarS32(\"A_36\"), 35.0);");
        L("\tAllume(SCR_GetVarS32(\"G_35\"), SCR_GetVarS32(\"A_35\"));");
        L("\tSCR_RunDelayed(\"FeuPartout\", 20.0);");
        L("}");
        L("void FeuPartout()");
        L("{");
        Cin("\t", "CUTVENT");
        L("}");
        L("// la pompe de la piscine : un ingénieur la met en marche (FD1 : Pompe_Arrosage)");
        L("void Pompe()");
        L("{");
        L("\tENT_SetVisibility(ENT_FindByName(\"Pompe_Arrosage_Mecanisme\"), 0);");
        L("\tENT_SetVisibility(ENT_FindByName(\"Pompe_Arrosage\"), 1);");
        Cin("\t", "CUT_POMPE");
        L("}");
        L("// la bande master du prochain album de Debby Love (FD1 : OnPickBandeSon)");
        L("void Pick_BandeSon()");
        L("{");
        L("\tSCR_SetVarS32(\"BandeSon\", 1);");
        L("\tENT_SetVisibility(ENT_FindByName(\"BandeSon1\"), 0);");
        Msg("\t", "EVENT_VIL_BANDESON");
        L("\tif(SCR_GetVarS32(\"Acte\") >= 3)");
        L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Bande\"), 1);");
        L("}");
        L("");
        // ---- actes ----
        L("// acte 2 (FD1 : Scenario1 réussi) : le tennis s'éteint, le garage prend feu, la remise explose, le renfort arrive");
        L("void Villa_Acte2()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 2);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Acte1\"), 1);");
        L("\tif(SCR_GetVarS32(\"G_28\") != 0)");
        L("\t\tFIR_HPSetGrowthRate(SCR_GetVarS32(\"G_28\"), -1.0);");
        L("\tAllume(SCR_GetVarS32(\"G_34\"), SCR_GetVarS32(\"A_34\"));");
        L("\tENT_SetDamage(ENT_FindByName(\"JerricanExplose\"), 0.0);");
        L("\tENT_SetVisibility(ENT_FindByName(\"Renfort_Pompier\"), 1);");
        L("\tENT_SetVisibility(ENT_FindByName(\"Renfort_Ingenieur\"), 1);");
        Roof("\t", "Toit_Garage");
        Cin("\t", "SEQUENCE2");
        L($"\tSCR_SetVarS32(\"Obj_Garage\", LEV_AddObjectiveBoolean(\"{T("VILLA_DES_S2O0_SEUL")}\"));");
        L($"\tSCR_SetVarS32(\"Obj_Excalibur\", LEV_AddObjectiveBoolean(\"{T("VILLA_DES_S2O1")}\"));");
        L("\tif(SCR_GetVarS32(\"ExcaliburSauve\") == 1)");
        L("\t{");
        L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Excalibur\"), 1);");
        L("\t\tSCR_RunDelayed(\"Villa_Acte3\", 10.0);");
        L("\t}");
        L("}");
        L("// acte 3 (FD1 : l'Excalibur à l'abri, TransitionBidonPipoEtOuiOui) : la réserve de chlore prend feu");
        L("void Villa_Acte3()");
        L("{");
        L("\tif(SCR_GetVarS32(\"Acte\") != 2)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"Acte\", 3);");
        Roof("\t", "Toit_Cabanon");
        L("\tAllume(SCR_GetVarS32(\"G_37\"), SCR_GetVarS32(\"G_37\"));");
        L("\tAllume(SCR_GetVarS32(\"G_39\"), SCR_GetVarS32(\"A_39\"));");
        L("\tAllume(SCR_GetVarS32(\"G_38\"), SCR_GetVarS32(\"A_38\"));");
        L("\tSCR_RunDelayed(\"Fumee\", 45.0);");
        L("\tif(SCR_GetVarS32(\"G_34\") != 0)");
        L("\t\tFIR_HPSetGrowthRate(SCR_GetVarS32(\"G_34\"), -0.5);");
        L("\tif(SCR_GetVarS32(\"G_32\") != 0)");
        L("\t\tFIR_HPSetGrowthRate(SCR_GetVarS32(\"G_32\"), -0.5);");
        Cin("\t", "SEQUENCE3");
        L($"\tSCR_SetVarS32(\"Obj_Acte3\", LEV_AddObjectiveIterate(\"{T("VILLA_DES_S3O0")}\", {VillaAct3Victims.Length}));");
        L($"\tSCR_SetVarS32(\"Obj_VillaSauve\", LEV_AddObjectiveBoolean(\"{T("VILLA_DES_S3O1")}\"));");
        L($"\tSCR_SetVarS32(\"Obj_Bande\", LEV_AddObjectiveBoolean(\"{T("VILLA_DES_S3O2_SEUL")}\"));");
        L("\tif(SCR_GetVarS32(\"BandeSon\") == 1)");
        L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Bande\"), 1);");
        L("}");
        L("// la fumée toxique de la réserve de chlore gagne la villa (FD1 : CUT_FUMEE)");
        L("void Fumee()");
        L("{");
        L("\tif(Feu(SCR_GetVarS32(\"G_37\")) <= 0.0)");
        L("\t\treturn;");
        Roof("\t", "Toit_Patio");
        Cin("\t", "CUT_FUMEE");
        L("}");
        L("// acte 4 (FD1 : Scenario3 réussi) : la serre tropicale brûle, Debby Love et le jardinier y sont bloqués");
        L("void Villa_Acte4()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 4);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Acte3\"), 1);");
        L("\tAllume(SCR_GetVarS32(\"G_31\"), SCR_GetVarS32(\"A_31\"));");
        L("\tAllume(SCR_GetVarS32(\"G_42\"), SCR_GetVarS32(\"A_42\"));");
        // le dôme noir, et les vitres (FD1 : transparentes)
        Roof("\t", "Cache_Toit_Serre_Tropicale");
        for (int k = 1; k <= 10; k++)
            Roof("\t", $"dom_{k:00}");
        foreach (string c in VillaAct4Victims)
            L($"\tENT_SetVisibility(ENT_FindByName(\"{c}\"), 1);");
        Cin("\t", "SEQUENCE4");
        L($"\tSCR_SetVarS32(\"Obj_Acte4\", LEV_AddObjectiveIterate(\"{T("VILLA_DES_S4O0")}\", {VillaAct4Victims.Length}));");
        L("}");
        L("");
        // ---- victimes ----
        L("// civil mis à l'abri (rappel du moteur)");
        L("void OnCivilianSaved(EntityID Civil)");
        L("{");
        foreach (string c in VillaAct1Victims.Concat(VillaAct3Victims).Concat(VillaAct4Victims))
        {
            L($"\tif(Civil == ENT_FindByName(\"{c}\"))");
            L($"\t\tSCR_SetVarS32(\"Sauve_{c}\", 1);");
        }
        L("\tif(Civil == ENT_FindByName(\"Milliardaire\"))");
        Msg("\t\t", "EVENT_VIL_MILLIARDAIRE");
        L("\tif(Civil == ENT_FindByName(\"PopStar\"))");
        Msg("\t\t", "EVENT_VIL_SAMANTHA_LOVE");
        L("}");
        L("// victime sortie d'affaire : sauvée, ou morte (FD1 : CheckStatus)");
        L("s32 Fini(mcString c)");
        L("{");
        L("\tif(SCR_GetVarS32(\"Sauve_\" + c) == 1)");
        L("\t\treturn 1;");
        L("\tif(ENT_GetDamage(ENT_FindByName(c)) <= 0.0)");
        L("\t\treturn 1;");
        L("\treturn 0;");
        L("}");
        L("");
        // ---- fin ----
        L("// défaite : texte du bilan, puis l'échec 10 s plus tard (FD1 : Villa_CheckDefeat, Defeat_OnTimer)");
        L("void Villa_Perdu(mcString cle)");
        L("{");
        L("\tif(SCR_GetVarS32(\"Fin\") != 0)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"Fin\", 2);");
        L("\tif(cle == \"DEFEAT_VOITURE\")");
        L($"\t\tLEV_SetDebriefText(\"{T("DEFEAT_VOITURE")}\");");
        L("\tif(cle == \"DEFEAT_MM\")");
        L($"\t\tLEV_SetDebriefText(\"{T("DEFEAT_MM")}\");");
        L("\tif(cle == \"DEFEAT_MILLIARDAIRE\")");
        L($"\t\tLEV_SetDebriefText(\"{T("DEFEAT_MILLIARDAIRE")}\");");
        L("\tif(cle == \"DEFEAT_LOVE\")");
        L($"\t\tLEV_SetDebriefText(\"{T("DEFEAT_LOVE")}\");");
        L("\tif(cle == \"DEFEAT_VILLA\")");
        L($"\t\tLEV_SetDebriefText(\"{T("DEFEAT_VILLA")}\");");
        L("\tSCR_RunDelayed(\"Villa_Defaite\", 10.0);");
        L("}");
        L("void Villa_Defaite()");
        L("{");
        L("\tLEV_TriggerDefeat();");
        L("}");
        L("// victoire (FD1 : Victory_OnTimer) : texte selon la bande master et le nombre de voitures détruites");
        L("void Villa_Victoire()");
        L("{");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Acte4\"), 1);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_VillaSauve\"), 1);");
        L("\tif(SCR_GetVarS32(\"BandeSon\") == 0 && SCR_GetVarS32(\"nbVoituresDetruites\") > 2)");
        L($"\t\tLEV_SetDebriefText(\"{T("VICTORY1")}\");");
        L("\tif(SCR_GetVarS32(\"BandeSon\") == 0 && SCR_GetVarS32(\"nbVoituresDetruites\") <= 2)");
        L($"\t\tLEV_SetDebriefText(\"{T("VICTORY2")}\");");
        L("\tif(SCR_GetVarS32(\"BandeSon\") == 1 && SCR_GetVarS32(\"nbVoituresDetruites\") > 2)");
        L($"\t\tLEV_SetDebriefText(\"{T("VICTORY3")}\");");
        L("\tif(SCR_GetVarS32(\"BandeSon\") == 1 && SCR_GetVarS32(\"nbVoituresDetruites\") <= 2)");
        L($"\t\tLEV_SetDebriefText(\"{T("VICTORY4")}\");");
        L("\tLEV_TriggerVictory();");
        L("}");
        L("");
        L("// le feu autour de la villa (FD1 : CheckFeuChlore, CheckFeuxVilla)");
        L("void Villa_Feux()");
        L("{");
        L("\tf32 danger;");
        L("\tdanger = Feu(SCR_GetVarS32(\"G_41\"));");
        L("\tif(SCR_GetVarS32(\"FeuAlerte\") == 0 && danger > 1.0 && SCR_GetVarS32(\"CineEnCours\") == 0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"FeuAlerte\", 1);");
        Roof("\t\t", "Toit_RDC_Villa");
        Cin("\t\t", "CUT_FEU");
        L("\t}");
        L($"\tif(SCR_GetVarS32(\"CheckDanger\") == 0 && danger > {VillaDangerAlert}.0 && SCR_GetVarS32(\"CineEnCours\") == 0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"CheckDanger\", 1);");
        Cin("\t\t", "ATTENTIONVILLA");
        L("\t}");
        L($"\tif(SCR_GetVarS32(\"CheckDanger\") < 2 && danger > {VillaDangerLost}.0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"CheckDanger\", 2);");
        L("\t\tAllume(SCR_GetVarS32(\"G_40\"), SCR_GetVarS32(\"G_40\"));");
        L("\t\tSCR_SetVarS32(\"CineEnCours\", 0);");
        Cin("\t\t", "VILLAFEU");
        L("\t\tif(SCR_GetVarS32(\"Acte\") >= 3)");
        L("\t\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_VillaSauve\"), 0);");
        L("\t\tVilla_Perdu(\"DEFEAT_VILLA\");");
        L("\t}");
        L("\t// les abords de la réserve de chlore éteints : son feu s'éteint (FD1 : croissance −10)");
        L("\tif(SCR_GetVarS32(\"Acte\") >= 3 && SCR_GetVarS32(\"ChloreFini\") == 0 && Feu(SCR_GetVarS32(\"G_39\")) <= 0.0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"ChloreFini\", 1);");
        L("\t\tif(SCR_GetVarS32(\"G_37\") != 0)");
        L("\t\t\tFIR_HPSetGrowthRate(SCR_GetVarS32(\"G_37\"), -10.0);");
        L("\t}");
        L("}");
        L("");
        L("// boucle : défaites, victimes de l'acte en cours, garage, bidons, feux de la villa");
        L("void Villa_Main()");
        L("{");
        L("\ts32 n;");
        L("\tif(SCR_GetVarS32(\"Fin\") != 0)");
        L("\t\treturn;");
        L("\tif(LEV_IsHealVehicleRemaining() == 0)");
        L("\t\tVilla_Perdu(\"DEFEAT_MM\");");
        L("\tif(ENT_GetDamage(ENT_FindByName(\"Milliardaire\")) <= 0.0)");
        L("\t\tVilla_Perdu(\"DEFEAT_MILLIARDAIRE\");");
        L("\tif(ENT_GetDamage(ENT_FindByName(\"PopStar\")) <= 0.0)");
        L("\t\tVilla_Perdu(\"DEFEAT_LOVE\");");
        // (le véhicule détruit : état 4)
        L("\tif(SCR_GetVarS32(\"Conduite\") == 1 && SCR_GetVarS32(\"ExcaliburSauve\") == 0 && ENT_GetUnitState(ENT_FindByName(\"Excalibur\")) == 4)");
        L("\t\tVilla_Perdu(\"DEFEAT_VOITURE\");");
        L("\tVilla_Feux();");
        L("\tif(SCR_GetVarS32(\"Fin\") != 0)");
        L("\t\treturn;");
        // les vieux bidons : l'alerte quand le feu les a bien entamés (FD1 : moins de 200 points de vie sur 300)
        L("\tif(SCR_GetVarS32(\"BarilsAlerte\") == 0 && ENT_GetDamage(ENT_FindByName(\"Barils\")) < 70.0 && SCR_GetVarS32(\"CineEnCours\") == 0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"BarilsAlerte\", 1);");
        Cin("\t\t", "ATTENTIONBARILS");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Acte\") == 1)");
        L("\t{");
        L("\t\tn = " + string.Join(" + ", VillaAct1Victims.Select(v => $"SCR_GetVarS32(\"Sauve_{v}\")")) + ";");
        L("\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Acte1\"), n);");
        L($"\t\tif(n >= {VillaAct1Victims.Length})");
        L("\t\t\tVilla_Acte2();");
        L("\t\treturn;");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"GarageFini\") == 0 && Feu(SCR_GetVarS32(\"G_34\")) + Feu(SCR_GetVarS32(\"G_32\")) <= 0.0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"GarageFini\", 1);");
        L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Garage\"), 1);");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Acte\") == 3)");
        L("\t{");
        L("\t\tn = " + string.Join(" + ", VillaAct3Victims.Select(v => $"Fini(\"{v}\")")) + ";");
        L("\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Acte3\"), n);");
        L($"\t\tif(n >= {VillaAct3Victims.Length})");
        L("\t\t\tVilla_Acte4();");
        L("\t\treturn;");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Acte\") == 4)");
        L("\t{");
        L("\t\tn = " + string.Join(" + ", VillaAct4Victims.Select(v => $"SCR_GetVarS32(\"Sauve_{v}\")")) + ";");
        L("\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Acte4\"), n);");
        L($"\t\tif(n >= {VillaAct4Victims.Length})");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Fin\", 1);");
        L("\t\t\tSCR_RunDelayed(\"Villa_Victoire\", 3.0);");
        L("\t\t}");
        L("\t}");
        L("}");
        L("");
        return (f.ToString(), "\tVilla_Init();\r\n", "\tVilla_Main();\r\n");
    }
}
