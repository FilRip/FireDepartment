using System.Numerics;
using System.Text;

namespace OpenFD.ConvFD1;

/// <summary>
/// Quartier (scripts FD1 Quartier_Main / _Scenar / _Event / _Trigger) : l'ambassade du Kirghizistan.
///  • acte 1 : monter au troisième étage (l'échelle aérienne se dresse contre la façade) ; la rue (voitures accidentées)
///    et l'aile ouest de l'étage brûlent ;
///  • acte 2 : sauver les victimes de la rue et de l'étage, les oeuvres d'art, éteindre la rue et l'étage ; les portes
///    cachent des victimes ; deux portes donnent sur un backdraft ; les fenêtres du deuxième étage finissent par exploser ;
///  • acte 3 : la partie nord (salle de réception) prend feu, une fuite de gaz menace (le robinet est dans le local de la
///    cour) ; le renfort arrive par le parking au nord ; sauver l'ambassadeur et ses invités, tout éteindre ;
///  • victoire : texte selon les oeuvres sauvées ; défaite : ambulance détruite.
/// Les groupes de foyers FD1 ne sont pas lisibles dans le niveau : ils sont reconstitués par zone (QuartierGroups).
/// </summary>
public sealed partial class MissionConverter
{
    /// <summary>
    /// Groupes de foyers du Quartier : 4, les fenêtres du deuxième étage (torches et foyers « fenetre », par le type) ; 7, la
    /// fuite de gaz ; 2, l'aile ouest de l'étage ; 3, le reste de l'étage sud ; 8, l'étage nord (réception) ; 1, la rue.
    /// Zones (cm : x min, x max, y min, y max), à l'étage (en haut) ou au sol.
    /// </summary>
    private static readonly (int group, float x0, float x1, float y0, float y1, bool up)[] QuartierGroups =
    {
        (7, 6300, 7700, 12400, 13400, true),
        (2, 5200, 8300, 8100, 11700, true),
        (3, 8300, 14300, 8100, 11700, true),
        (8, 5200, 14300, 11700, 15400, true),
        (1, -1000, 30000, -1000, 8100, false),
    };

    /// <summary>Groupes du Quartier qui attendent le script (FD1 : Init_HPScenar2 et 3, et les fenêtres).</summary>
    private static readonly int[] QuartierWaitingGroups = { 3, 4, 7, 8 };

    /// <summary>Voitures accidentées de la rue (cm) : les foyers à moins de 5 m brûlent au départ.</summary>
    private static readonly Vector2[] QuartierWrecks =
    {
        new Vector2(9638, 3591), new Vector2(9998, 5294), new Vector2(9703, 6488), new Vector2(12252, 6878), new Vector2(10479, 5496),
    };

    /// <summary>Le foyer de l'aile ouest près de la façade soufflée (cm : x max, y max) brûle au départ.</summary>
    private static readonly Vector2 QuartierBlast = new Vector2(7400, 10000);

    /// <summary>Victimes : la rue (0 à 3), l'étage sud (4 à 7), la réception (8 à 11 ; Civil9 : l'ambassadeur).</summary>
    private static readonly string[] QuartierCivilians = Enumerable.Range(0, 12).Select(i => $"Civil{i}").ToArray();

    /// <summary>Somme des intensités de la rue et de l'étage sud sous laquelle l'acte 2 peut finir (FD1 : 1 300 points).</summary>
    private const int QuartierAct2Fire = 300;

    /// <summary>Secondes entre le début de l'acte 2 et la première fenêtre qui prend feu, puis entre deux fenêtres (FD1 : EF).</summary>
    private const int QuartierWindowsStart = 420;
    private const int QuartierWindowsStep = 30;

    /// <summary>Groupe FD1 de ce foyer du Quartier (0 : aucun).</summary>
    private int QuartierGroupOf(Vector3 at, string type)
    {
        if (!Mission.Equals("quartier", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (at.Z > 300 && at.Z < 1000 && (type.StartsWith("torche", StringComparison.OrdinalIgnoreCase) || type.Contains("fenetre", StringComparison.OrdinalIgnoreCase)))
            return 4;
        foreach ((int group, float x0, float x1, float y0, float y1, bool up) in QuartierGroups)
            if (at.X >= x0 && at.X <= x1 && at.Y >= y0 && at.Y <= y1 && (at.Z > 1000) == up)
                return group;
        return 0;
    }

    /// <summary>Foyers allumés au départ du Quartier : autour des voitures accidentées, l'aile ouest près de la façade soufflée.</summary>
    private bool QuartierLitAtStart(Vector3 at, string type)
    {
        int g = QuartierGroupOf(at, type);
        if (g == 1)
            return QuartierWrecks.Any(w => Vector2.Distance(w, new Vector2(at.X, at.Y)) < 500);
        if (g == 2)
            return at.X <= QuartierBlast.X && at.Y <= QuartierBlast.Y && Ignites(type);
        return false;
    }

    /// <summary>Script du Quartier (fonctions, départ, boucle).</summary>
    private (string functions, string start, string main) QuartierScript(Fd1Level fd1)
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
        // groupes : tous les foyers (G_n) et ceux qui prennent quand le script allume le groupe (A_n : pas l'herbe)
        Dictionary<int, List<string>> all = new Dictionary<int, List<string>>();
        Dictionary<int, List<string>> ignite = new Dictionary<int, List<string>>();
        List<(float x, string fire)> windows = new List<(float, string)>();
        for (int i = 0; i < fd1.Fires.Count; i++)
        {
            Vector3 at = fd1.Fires[i].at * WorldToCm;
            int g = QuartierGroupOf(at, fd1.Fires[i].type);
            if (g == 0)
                continue;
            if (g == 4)
                windows.Add((at.X, $"foyer_{i + 1}"));
            if (!all.TryGetValue(g, out List<string>? list))
                all[g] = list = new List<string>();
            list.Add($"foyer_{i + 1}");
            if (!Ignites(fd1.Fires[i].type))
                continue;
            if (!ignite.TryGetValue(g, out List<string>? il))
                ignite[g] = il = new List<string>();
            il.Add($"foyer_{i + 1}");
        }
        Report.Add($"script : groupes de foyers {string.Join(", ", all.OrderBy(g => g.Key).Select(g => $"{g.Key} ({g.Value.Count})"))}");
        // les fenêtres, d'ouest en est (FD1 : de Fenetre1 à Fenetre8), deux foyers chacune (la torche et le feu de fenêtre)
        List<List<string>> windowFires = windows.GroupBy(w => (int)MathF.Round(w.x / 1000)).OrderBy(w => w.Key).Select(w => w.Select(x => x.fire).ToList()).ToList();

        L("// ===== Quartier : déroulement (traduit des scripts FD1) =====");
        L("");
        L("// un groupe de foyers FD1 prend feu (SetHotpointGroupActivated(n, 1)) : il peut brûler, ses foyers qui ne sont pas de");
        L("// l'herbe prennent");
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
        L("");
        L("void Quartier_Init()");
        L("{");
        L("\ts32 g;");
        L("\tSCR_SetVarS32(\"Acte\", 1);");
        foreach ((int g, List<string> fires) in all.OrderBy(x => x.Key))
        {
            L($"\tg = ENT_GroupCreate(\"groupe_{g}\");");
            foreach (string h in fires)
                L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
            L($"\tSCR_SetVarS32(\"G_{g}\", g);");
            if (QuartierWaitingGroups.Contains(g))
                L("\tFIR_HPEnable(g, 0);");
            if (!ignite.TryGetValue(g, out List<string>? il))
                continue;
            L($"\tg = ENT_GroupCreate(\"allume_{g}\");");
            foreach (string h in il)
                L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
            L($"\tSCR_SetVarS32(\"A_{g}\", g);");
        }
        for (int k = 0; k < windowFires.Count; k++)
        {
            L($"\tg = ENT_GroupCreate(\"fenetre_{k + 1}\");");
            foreach (string h in windowFires[k])
                L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
            L($"\tSCR_SetVarS32(\"F_{k + 1}\", g);");
        }
        Show("\t", "Civil0", "Civil1", "Civil2", "Civil3");
        Cin("\t", "ARRIVEE");
        L($"\tSCR_SetVarS32(\"Obj_Acte1\", LEV_AddObjectiveBoolean(\"{T("QUARTIER_DES_S1O0")}\"));");
        L("}");
        L("");
        // ---- acte 1 → 2 ----
        L("// une unité prend pied au troisième étage (FD1 : T2, T10 à T14, T18, T24 → Result_1) ; la cinématique de l'endroit");
        L("void Etage(s32 cine)");
        L("{");
        L("\tif(SCR_GetVarS32(\"Acte\") != 1)");
        L("\t\treturn;");
        foreach ((int k, string cine) in new[] { (1, "CUT2"), (2, "CUT2_2"), (3, "CUT2_3"), (4, "CUT2_4"), (5, "CUT2_5") })
            if (Cine(cine).Length > 0)
            {
                L($"\tif(cine == {k})");
                L($"\t\t{Cine(cine)}");
            }
        L("\tQuartier_Acte2();");
        L("}");
        foreach ((string trigger, int cine) in new[] { ("T2", 0), ("T10", 0), ("T18", 1), ("T11", 2), ("T12", 3), ("T13", 4), ("T24", 5) })
        {
            L($"void {trigger}(EntityID Unit, EntityID Zone)");
            L("{");
            L($"\tEtage({cine});");
            L("}");
        }
        L("// acte 2 (FD1 : Scenario1 réussi) : le toit s'enlève, le reste de l'étage sud peut brûler");
        L("void Quartier_Acte2()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 2);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Acte1\"), 1);");
        foreach (string line in RoofLines("Toit", 0))
            L("\t" + line);
        L("\tAllume(SCR_GetVarS32(\"G_3\"), 0);");
        L($"\tSCR_SetVarS32(\"Obj_Acte2\", LEV_AddObjectiveIterate(\"{T("QUARTIER_DES_S2O0")}\", 8));");
        L($"\tSCR_SetVarS32(\"Obj_Oeuvres\", LEV_AddObjectiveIterate(\"{T("QUARTIER_DES_S2O1_SEUL")}\", 6));");
        L($"\tSCR_SetVarS32(\"Obj_Feux2\", LEV_AddObjectiveBoolean(\"{T("QUARTIER_DES_S2O2_SEUL")}\"));");
        L($"\tSCR_RunDelayed(\"Fenetre_1\", {QuartierWindowsStart}.0);");
        L("}");
        // ---- fenêtres ----
        L("// les fenêtres du deuxième étage prennent feu l'une après l'autre (FD1 : EF_start, puis toutes les 30 s), puis explosent");
        for (int k = 1; k <= windowFires.Count; k++)
        {
            L($"void Fenetre_{k}()");
            L("{");
            L($"\tAllume(SCR_GetVarS32(\"F_{k}\"), SCR_GetVarS32(\"F_{k}\"));");
            if (k < windowFires.Count)
                L($"\tSCR_RunDelayed(\"Fenetre_{k + 1}\", {QuartierWindowsStep}.0);");
            L("}");
        }
        L("void FenetreExplosee(EntityID Fenetre)");
        L("{");
        L("\tif(SCR_GetVarS32(\"FenetreVue\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"FenetreVue\", 1);");
        Cin("\t", "EXPLOFENETRE");
        L("}");
        L("// la voiture accidentée de la rue explose (FD1 : CheckExploVoiture)");
        L("void VoitureExplosee(EntityID Voiture)");
        L("{");
        Cin("\t", "EXPLOVOITURE");
        L("}");
        L("");
        // ---- zones de l'étage ----
        L("// zones FD1 de l'étage : victimes découvertes (T5, T6 : Civil6 et 7 ; T7 : Civil4), le couloir effondré (TriggerTrou),");
        L("// l'escalier en feu (T23), les pièces de la fuite de gaz (T21, T22)");
        L("void T5(EntityID Unit, EntityID Zone)");
        L("{");
        Show("\t", "Civil6", "Civil7");
        L("}");
        L("void T6(EntityID Unit, EntityID Zone)");
        L("{");
        Show("\t", "Civil6", "Civil7");
        L("}");
        L("void T7(EntityID Unit, EntityID Zone)");
        L("{");
        Show("\t", "Civil4");
        L("}");
        L("void TriggerTrou(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tif(SCR_GetVarS32(\"EboulVu\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"EboulVu\", 1);");
        Cin("\t", "EBOULEMENT1");
        L("}");
        L("void T23(EntityID Unit, EntityID Zone)");
        L("{");
        Msg("\t", "EVENT_QUA_ESCALIER");
        L("}");
        L("void T21(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tif(SCR_GetVarS32(\"GazCoupe\") == 0 && SCR_GetVarS32(\"GazMsg\") == 0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"GazMsg\", 1);");
        Msg("\t\t", "EVENT_QUA_GAZ");
        L("\t}");
        L("}");
        L("");
        // ---- portes ----
        L("// portes cassées : victimes découvertes, backdraft (FD1 : CheckGlobalCache, caches 5, 8, 9, 10, 20)");
        L("s32 Cassee(mcString porte)");
        L("{");
        L("\tif(ENT_GetState(ENT_FindByName(porte)) == \"Broken\")");
        L("\t\treturn 1;");
        L("\treturn 0;");
        L("}");
        L("void Quartier_Portes()");
        L("{");
        L("\tif(SCR_GetVarS32(\"Backdraft\") == 0 && (Cassee(\"Porte1\") == 1 || Cassee(\"Porte14\") == 1))");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"Backdraft\", 1);");
        L("\t\tENT_SetDamage(ENT_FindByName(\"explosif1\"), 0.0);");
        Cin("\t\t", "BACK01");
        Show("\t\t", "Civil4");
        L("\t}");
        L("\tif(Cassee(\"Porte14\") == 1)");
        L("\t{");
        Show("\t\t", "Civil6", "Civil7");
        L("\t}");
        L("\tif(Cassee(\"Porte4\") == 1)");
        Show("\t\t", "Civil5");
        L("\tif(Cassee(\"Porte11\") == 1 || Cassee(\"Porte15\") == 1 || Cassee(\"porte16\") == 1)");
        L("\t{");
        Show("\t\t", "Civil8", "Civil9", "Civil10", "Civil11");
        L("\t}");
        // la fuite de gaz s'enflamme quand on ouvre la réception par la porte 11 sans avoir coupé le gaz (FD1 : CheckGazExpl)
        L("\tif(SCR_GetVarS32(\"Acte\") >= 3 && SCR_GetVarS32(\"GazCoupe\") == 0 && SCR_GetVarS32(\"GazExplose\") == 0 && Cassee(\"Porte11\") == 1)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"GazExplose\", 1);");
        L("\t\tAllume(SCR_GetVarS32(\"G_7\"), SCR_GetVarS32(\"G_7\"));");
        L("\t\tif(SCR_GetVarS32(\"G_7\") != 0)");
        L("\t\t\tFIR_HPSetIntensity(SCR_GetVarS32(\"G_7\"), 50.0);");
        L("\t}");
        L("}");
        L("");
        // ---- objets ----
        L("// oeuvres d'art ramassées (FD1 : OnPickMe1 à 6)");
        for (int k = 1; k <= 6; k++)
        {
            L($"void Pick_Oeuvre{k}()");
            L("{");
            L($"\tSCR_SetVarS32(\"Oeuvre{k}\", 1);");
            L("\tSCR_SetVarS32(\"nbOeuvres\", SCR_GetVarS32(\"nbOeuvres\") + 1);");
            L($"\tENT_SetVisibility(ENT_FindByName(\"Oeuvre{k}\"), 0);");
            Msg("\t", "EVENT_QUA_ART");
            L("}");
        }
        L("// le robinet de gaz du local de la cour (FD1 : CoupeGazGlobal) : la fuite s'arrête, sa flamme s'éteint");
        L("void CoupeGaz()");
        L("{");
        L("\tSCR_SetVarS32(\"GazCoupe\", 1);");
        Cin("\t", "COUPUREGAZ");
        L("\tif(SCR_GetVarS32(\"G_7\") != 0)");
        L("\t\tFIR_HPSetGrowthRate(SCR_GetVarS32(\"G_7\"), -5.0);");
        L("}");
        L("");
        // ---- acte 3 ----
        L("// acte 3 (FD1 : Scenario2 réussi) : la réception prend feu, fuite de gaz ; une minute plus tard la cinématique de");
        L("// l'arrière, 40 s après le renfort par le parking au nord");
        L("void Quartier_Acte3()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 3);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Acte2\"), 1);");
        L("\tAllume(SCR_GetVarS32(\"G_8\"), SCR_GetVarS32(\"A_8\"));");
        Cin("\t", "CUT3");
        L("\tSCR_RunDelayed(\"RenfortArriere\", 60.0);");
        L($"\tSCR_SetVarS32(\"Obj_Acte3\", LEV_AddObjectiveIterate(\"{T("QUARTIER_DES_S3O0")}\", 12));");
        L($"\tSCR_SetVarS32(\"Obj_Feux3\", LEV_AddObjectiveBoolean(\"{T("QUARTIER_DES_S3O2_SEUL")}\"));");
        L("}");
        L("void RenfortArriere()");
        L("{");
        Cin("\t", "RENFORTARRIERE");
        L("\tSCR_RunDelayed(\"Renfort\", 40.0);");
        L("}");
        L("void Renfort()");
        L("{");
        L("\tLEV_CallReinforcement(1);");
        L($"\tSCR_RunDelayed(\"Cine_CUT4\", {BanqueReinforcementCineDelay}.0);");
        L("}");
        L("");
        // ---- victimes et fin ----
        L("// civil mis à l'abri (rappel du moteur)");
        L("void OnCivilianSaved(EntityID Civil)");
        L("{");
        foreach (string c in QuartierCivilians)
        {
            L($"\tif(Civil == ENT_FindByName(\"{c}\"))");
            L($"\t\tSCR_SetVarS32(\"Sauve_{c}\", 1);");
        }
        L("\tif(Civil == ENT_FindByName(\"Civil9\"))");
        Msg("\t\t", "EVENT_QUA_AMBASSADEUR");
        L("}");
        L("void Quartier_Defaite()");
        L("{");
        L("\tLEV_TriggerDefeat();");
        L("}");
        L("// victoire (FD1 : Victory_OnTimer) : toutes les oeuvres sauvées ou non");
        L("void Quartier_Victoire()");
        L("{");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Acte3\"), 1);");
        L("\tif(SCR_GetVarS32(\"nbOeuvres\") >= 6)");
        L($"\t\tLEV_SetDebriefText(\"{T("VICTORY1")}\");");
        L("\telse");
        L($"\t\tLEV_SetDebriefText(\"{T("VICTORY3")}\");");
        L("\tLEV_TriggerVictory();");
        L("}");
        L("");
        string Saved(IEnumerable<string> names) => string.Join(" + ", names.Select(n => $"SCR_GetVarS32(\"Sauve_{n}\")"));
        L("// boucle : portes, défaite, victimes et feux de l'acte en cours");
        L("void Quartier_Main()");
        L("{");
        L("\ts32 n;");
        L("\tf32 feux;");
        L("\tif(SCR_GetVarS32(\"Fin\") != 0)");
        L("\t\treturn;");
        L("\tQuartier_Portes();");
        L("\tif(LEV_IsHealVehicleRemaining() == 0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"Fin\", 2);");
        L($"\t\tLEV_SetDebriefText(\"{T("DEFEAT4")}\");");
        L("\t\tSCR_RunDelayed(\"Quartier_Defaite\", 5.0);");
        L("\t\treturn;");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Acte\") >= 2)");
        L("\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Oeuvres\"), SCR_GetVarS32(\"nbOeuvres\"));");
        L("\tif(SCR_GetVarS32(\"Acte\") >= 2 && SCR_GetVarS32(\"nbOeuvres\") >= 6)");
        L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Oeuvres\"), 1);");
        L("\tif(SCR_GetVarS32(\"Acte\") == 2)");
        L("\t{");
        L($"\t\tn = {Saved(QuartierCivilians.Take(8))};");
        L("\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Acte2\"), n);");
        L("\t\tfeux = Feu(SCR_GetVarS32(\"G_1\")) + Feu(SCR_GetVarS32(\"G_2\")) + Feu(SCR_GetVarS32(\"G_3\"));");
        L("\t\tif(feux <= 0.0)");
        L("\t\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Feux2\"), 1);");
        // FD1 : Civil4, Civil6 et Civil7 sauvés, et le feu presque éteint
        L($"\t\tif({Saved(new[] { "Civil4", "Civil6", "Civil7" })} >= 3 && feux <= {QuartierAct2Fire}.0)");
        L("\t\t\tQuartier_Acte3();");
        L("\t\treturn;");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Acte\") == 3)");
        L("\t{");
        L($"\t\tn = {Saved(QuartierCivilians)};");
        L("\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Acte3\"), n);");
        L("\t\tfeux = Feu(SCR_GetVarS32(\"G_1\")) + Feu(SCR_GetVarS32(\"G_2\")) + Feu(SCR_GetVarS32(\"G_3\")) + Feu(SCR_GetVarS32(\"G_7\")) + Feu(SCR_GetVarS32(\"G_8\"));");
        L("\t\tif(feux <= 0.0)");
        L("\t\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Feux3\"), 1);");
        L($"\t\tif(n >= {QuartierCivilians.Length} && feux <= 0.0)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Fin\", 1);");
        L("\t\t\tSCR_RunDelayed(\"Quartier_Victoire\", 5.0);");
        L("\t\t}");
        L("\t}");
        L("}");
        L("");
        return (f.ToString(), "\tQuartier_Init();\r\n", "\tQuartier_Main();\r\n");
    }
}
