using System.Numerics;
using System.Text;

namespace OpenFD.ConvFD1;

/// <summary>
/// Ecole (scripts FD1 ecole_Main / _Scenar / _Event / _Trigger) : « La tempête ».
///  • acte 1 : la grue du chantier est tombée sur l'école ; le chantier brûle au pied de la grue, un ouvrier est bloqué
///    dans un baraquement ; le groupe électrogène du chantier est sous tension (on le coupe) ;
///  • acte 2 : le bâtiment administratif ; la chaufferie (un transformateur au pyralène) brûle et dégage de la dioxine qui
///    gagne les pièces de l'étage l'une après l'autre ; casser la fenêtre d'une pièce en chasse la fumée ; éteindre la
///    chaufferie arrête tout ; deux employées à sauver ; la grande échelle arrive en renfort, l'ouvrier prête son bulldozer ;
///  • acte 3 : l'école ; l'incendie gagne l'étage où la grue est tombée ; trois professeurs dans les salles de classe ;
///    risque de flash-over, un plancher s'effondre ; un sapeur arrive en renfort ;
///  • victoire : texte selon les victimes ; défaite : l'ambulance détruite.
/// Les groupes de foyers FD1 ne sont pas lisibles dans le niveau : ils sont reconstitués par zone (EcoleGroupOf).
/// </summary>
public sealed partial class MissionConverter
{
    /// <summary>
    /// Pièces de l'étage du bâtiment administratif (FD1 : room1 à room6, groupes 11, 9, 10, 7, 8) : numéro FD1 de la pièce, son
    /// groupe de foyers, sa boîte (cm : x min, x max, y min, y max), les fenêtres qui l'aèrent, la victime qu'elle cache.
    /// </summary>
    private static readonly (int room, int group, float x0, float x1, float y0, float y1, string[] windows, string victim)[] EcoleRooms =
    {
        (5, 7, 11997, 13555, 6959, 7989, new[] { "porte0" }, ""),
        (6, 8, 12048, 13585, 7959, 8959, new[] { "porte7", "porte8", "porte9" }, ""),
        (4, 10, 13548, 15055, 7958, 8959, new[] { "porte6" }, ""),
        (3, 9, 13522, 15060, 6959, 7959, new[] { "porte1" }, "civil1"),
        (1, 11, 15017, 16056, 6959, 8959, new[] { "porte2", "porte3", "porte4", "porte5" }, "civil0"),
    };

    /// <summary>Groupes de l'Ecole sans numéro FD1 : la chaufferie, l'étage administratif, l'école, la salle du flash-over.</summary>
    private const int EcoleBoiler = 15;
    private const int EcoleAdmin = 21;
    private const int EcoleSchool = 29;
    private const int EcoleFlashover = 30;
    private const int EcoleCollapse = 19;
    private const int EcoleSite = 5;
    private const int EcoleHut = 25;
    /// <summary>Les feux électriques de la flèche de la grue tombée sur l'école (FD1 : groupe 17, sous tension du câble « porte30 »).</summary>
    private const int EcoleCraneElec = 17;

    /// <summary>
    /// Le feu du chantier au départ (cm) : « au pied même de la grue » (ECO_GRUE01) et jusqu'au baraquement de l'ouvrier, à
    /// moins de 6 m de cette ligne (FD1 : une traînée de foyers à travers le chantier).
    /// </summary>
    private static readonly Vector2 EcoleCraneFoot = new Vector2(19700, 10200);
    private static readonly Vector2 EcoleSiteLineEnd = new Vector2(20000, 13300);

    /// <summary>Délai (s) entre l'ouvrier sauvé (son baraquement éteint) et l'acte 2.</summary>
    private const int EcoleAct2Delay = 15;

    /// <summary>Le baraquement de l'ouvrier (cm) : ses foyers, à moins de 9 m, sont électriques.</summary>
    private static readonly Vector2 EcoleHutCenter = new Vector2(19900, 13900);

    /// <summary>La flèche de la grue tombée sur l'étage de l'école (cm : x min, x max, y min, y max) : ses abords prennent à l'acte 3.</summary>
    private static readonly (float x0, float x1, float y0, float y1) EcoleCraneImpact = (9700, 12300, 12900, 14300);

    /// <summary>Délais de la dioxine (s après le début de l'acte 2, FD1 : progres2 à progres5) : les pièces 6, 4, 3 et 1.</summary>
    private static readonly (int delay, int room)[] EcoleProgression = { (40, 6), (140, 4), (300, 3), (360, 1) };

    /// <summary>Victimes de chaque acte.</summary>
    private static readonly string[] EcoleAct2Victims = { "civil0", "civil1" };
    private static readonly string[] EcoleAct3Victims = { "civil10", "civil11", "civil12" };

    /// <summary>Groupes de l'Ecole qui attendent le script.</summary>
    private static readonly int[] EcoleWaitingGroups = { 7, 8, 9, 10, 11, EcoleAdmin, EcoleSchool, EcoleCraneElec, EcoleFlashover, EcoleCollapse };

    /// <summary>Groupe de ce foyer de l'Ecole (−1 : aucun) ; les pièces du bâtiment administratif d'abord.</summary>
    private int EcoleGroupOf(Vector3 at, string type)
    {
        if (!Mission.Equals("ecole", StringComparison.OrdinalIgnoreCase))
            return -1;
        Vector2 p = new Vector2(at.X, at.Y);
        if (at.Z >= 800)
        {
            foreach ((int _, int group, float x0, float x1, float y0, float y1, string[] _, string _) in EcoleRooms)
                if (In(p, x0, x1, y0, y1))
                    return group;
            if (In(p, 11700, 16400, 6650, 9300))
                return EcoleAdmin;
            if (In(p, 7928, 10078, 12918, 15020) && type.Contains("fover", StringComparison.OrdinalIgnoreCase))
                return EcoleFlashover;
            if (In(p, 10516, 12112, 14955, 16931) && type.Contains("fover", StringComparison.OrdinalIgnoreCase))
                return EcoleCollapse;
            if (In(p, 7700, 12400, 10600, 17800))
                return type.Contains("electrique", StringComparison.OrdinalIgnoreCase) ? EcoleCraneElec : EcoleSchool;
            return -1;
        }
        if (In(p, 10900, 12100, 6900, 8000))
            return EcoleBoiler;
        if (Vector2.Distance(p, EcoleHutCenter) < 900)
            return EcoleHut;
        if (p.X >= 17000)
            return EcoleSite;
        return -1;
    }

    /// <summary>Foyers allumés au départ de l'Ecole : le pied de la grue, le baraquement de l'ouvrier, la chaufferie.</summary>
    private bool EcoleLitAtStart(Vector3 at, string type)
    {
        int g = EcoleGroupOf(at, type);
        if (g == EcoleBoiler || g == EcoleHut)
            return true;
        return g == EcoleSite && DistanceToSegment(new Vector2(at.X, at.Y), EcoleCraneFoot, EcoleSiteLineEnd) < 600;
    }

    /// <summary>Distance d'un point au segment [a, b].</summary>
    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector2.Distance(p, a + t * ab);
    }

    /// <summary>Ce foyer prend-il quand le script allume son groupe (A_n) ?</summary>
    private static bool EcoleIgnites(int g, Vector3 at, string type)
    {
        Vector2 p = new Vector2(at.X, at.Y);
        if (g is EcoleSchool or EcoleCraneElec)
            return Ignites(type) && In(p, EcoleCraneImpact.x0, EcoleCraneImpact.x1, EcoleCraneImpact.y0, EcoleCraneImpact.y1);
        if (g == EcoleFlashover)
            return true;
        if (g == EcoleCollapse)
            return true;
        // pièces du bâtiment administratif : la fumée noire (dioxine) et ses foyers
        return g is 7 or 8 or 9 or 10 or 11;
    }

    /// <summary>Script de l'Ecole (fonctions, départ, boucle).</summary>
    private (string functions, string start, string main) EcoleScript(Fd1Level fd1)
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
        Dictionary<int, List<string>> all = new Dictionary<int, List<string>>();
        Dictionary<int, List<string>> ignite = new Dictionary<int, List<string>>();
        for (int i = 0; i < fd1.Fires.Count; i++)
        {
            Vector3 at = fd1.Fires[i].at * WorldToCm;
            string type = fd1.Fires[i].type;
            string h = $"foyer_{i + 1}";
            int g = EcoleGroupOf(at, type);
            if (g < 0)
                continue;
            if (!all.TryGetValue(g, out List<string>? list))
                all[g] = list = new List<string>();
            list.Add(h);
            if (!EcoleIgnites(g, at, type))
                continue;
            if (!ignite.TryGetValue(g, out List<string>? il))
                ignite[g] = il = new List<string>();
            il.Add(h);
        }
        Report.Add($"script : groupes de foyers {string.Join(", ", all.OrderBy(g => g.Key).Select(g => $"{g.Key} ({g.Value.Count}, {(ignite.TryGetValue(g.Key, out List<string>? il) ? il.Count : 0)} allumés par le script)"))}");
        string Saved(IEnumerable<string> names) => string.Join(" + ", names.Select(n => $"SCR_GetVarS32(\"Sauve_{n}\")"));

        L("// ===== Ecole : déroulement (traduit des scripts FD1) =====");
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
        L("// une victime paraît (une seule fois : une victime sauvée ne revient pas)");
        L("void Montre(mcString nom)");
        L("{");
        L("\tif(SCR_GetVarS32(\"Vu_\" + nom) == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"Vu_\" + nom, 1);");
        L("\tENT_SetVisibility(ENT_FindByName(nom), 1);");
        L("}");
        L("s32 Cassee(mcString porte)");
        L("{");
        L("\tif(ENT_GetState(ENT_FindByName(porte)) == \"Broken\")");
        L("\t\treturn 1;");
        L("\treturn 0;");
        L("}");
        L("");
        L("void Ecole_Init()");
        L("{");
        L("\ts32 g;");
        L("\tSCR_SetVarS32(\"Acte\", 1);");
        foreach ((int g, List<string> fires) in all.OrderBy(x => x.Key))
        {
            L($"\tg = ENT_GroupCreate(\"groupe_{g}\");");
            foreach (string h in fires)
                L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
            L($"\tSCR_SetVarS32(\"G_{g}\", g);");
            if (EcoleWaitingGroups.Contains(g))
                L("\tFIR_HPEnable(g, 0);");
            if (!ignite.TryGetValue(g, out List<string>? il))
                continue;
            L($"\tg = ENT_GroupCreate(\"allume_{g}\");");
            foreach (string h in il)
                L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
            L($"\tSCR_SetVarS32(\"A_{g}\", g);");
        }
        Cin("\t", "INTRO");
        L("\tSCR_RunDelayed(\"AlerteOuvrier\", 19.0);");
        L($"\tSCR_SetVarS32(\"Obj_Ouvrier\", LEV_AddObjectiveBoolean(\"{T("ECOLE_DES_S1O0")}\"));");
        L($"\tSCR_SetVarS32(\"Obj_Chantier\", LEV_AddObjectiveBoolean(\"{T("ECOLE_DES_S1O1_SEUL")}\"));");
        L("}");
        L("// l'ouvrier bloqué dans le baraquement (FD1 : alertCiv)");
        L("void AlerteOuvrier()");
        L("{");
        Roof("\t", "toitChantier2");
        Show("\t", "ouvrier0");
        L("}");
        L("");
        // ---- acte 1 : le chantier ----
        L("// l'armoire électrique du chantier : l'avertissement, tant que le groupe électrogène tourne (FD1 : presElec)");
        L("void presElec(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tif(SCR_GetVarS32(\"ElecCoupee\") == 1 || SCR_GetVarS32(\"ElecVu\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"ElecVu\", 1);");
        Cin("\t", "EXT_ELEC");
        L("}");
        L("// le groupe électrogène est coupé (FD1 : GEOff)");
        L("void GEOff()");
        L("{");
        L("\tif(SCR_GetVarS32(\"ElecCoupee\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"ElecCoupee\", 1);");
        // les feux électriques du baraquement deviennent des feux ordinaires (FD1 : SetHotpointElectric(…, 0))
        L($"\tFIR_HPEnable(SCR_GetVarS32(\"G_{EcoleHut}\"), 0);");
        Msg("\t", "EVENT_ECO_COUPER_GROUPE");
        L("}");
        L("");
        // ---- acte 2 : le bâtiment administratif ----
        L("// acte 2 (FD1 : transS2) : la grande échelle et le bras élévateur arrivent, la dioxine gagne l'étage du bâtiment");
        L("// administratif");
        L("void Ecole_Acte2()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 2);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Chantier\"), 1);");
        L($"\tCroissance(SCR_GetVarS32(\"G_{EcoleSite}\"), -0.25);");
        L("\tLEV_CallReinforcement(1);");
        Roof("\t", "toitAdmin");
        L($"\tAllume(SCR_GetVarS32(\"G_{EcoleAdmin}\"), 0);");
        L("\tPiece(5);");
        Cin("\t", "CUT_PRES2");
        foreach ((int delay, int room) in EcoleProgression)
            L($"\tSCR_RunDelayed(\"Progression_{room}\", {delay}.0);");
        L($"\tSCR_RunDelayed(\"PresBuldo\", {EcoleProgression[0].delay + 50}.0);");
        L("\tSCR_SetVarS32(\"Contamines\", 1);");
        L($"\tSCR_SetVarS32(\"Obj_Acte2\", LEV_AddObjectiveIterate(\"{T("ECOLE_DES_S2O0_SEUL")}\", {EcoleAct2Victims.Length}));");
        L($"\tSCR_SetVarS32(\"Obj_Pieces\", LEV_AddObjectiveIterate(\"{T("ECOLE_DES_S2O2_SEUL")}\", {EcoleRooms.Length}));");
        L("\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Pieces\"), 1);");
        L("}");
        L("// une pièce de l'étage est contaminée (FD1 : progresStart, progres2 à 5) : sa fumée noire prend");
        L("void Piece(s32 n)");
        L("{");
        foreach ((int room, int group, float _, float _, float _, float _, string[] _, string _) in EcoleRooms)
        {
            L($"\tif(n == {room})");
            L($"\t\tAllume(SCR_GetVarS32(\"G_{group}\"), SCR_GetVarS32(\"A_{group}\"));");
        }
        L("}");
        foreach ((int delay, int room) in EcoleProgression)
        {
            string victim = EcoleRooms.First(r => r.room == room).victim;
            L($"void Progression_{room}()");
            L("{");
            L($"\tif(SCR_GetVarS32(\"Aeree{room}\") == 1 || SCR_GetVarS32(\"TransfoFini\") == 1)");
            L("\t\treturn;");
            L($"\tPiece({room});");
            L("\tSCR_SetVarS32(\"Contamines\", SCR_GetVarS32(\"Contamines\") + 1);");
            L("\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Pieces\"), SCR_GetVarS32(\"Contamines\"));");
            if (room == EcoleProgression[0].room)
                Cin("\t", "CUT_PROGRESSION1");
            else
                Msg("\t", "EVENT_ECO_PIECE_INTOXIQUEE");
            if (victim.Length > 0)
                Show("\t", victim);
            L("}");
        }
        L("// l'ouvrier prête son bulldozer (FD1 : presBuldo)");
        L("void PresBuldo()");
        L("{");
        L("\tENT_SetVisibility(ENT_FindByName(\"buldo\"), 1);");
        Cin("\t", "CUT_BULDO");
        L("}");
        L("// une fenêtre de l'étage cassée : la fumée de la pièce s'en va (FD1 : doorOnBreak, safeRoom) ; la victime paraît");
        L("void Ecole_Fenetres()");
        L("{");
        foreach ((int room, int group, float _, float _, float _, float _, string[] windows, string victim) in EcoleRooms)
        {
            L($"\tif(SCR_GetVarS32(\"Aeree{room}\") == 0 && ({string.Join(" || ", windows.Select(w => $"Cassee(\"{w}\") == 1"))}))");
            L("\t{");
            L($"\t\tSCR_SetVarS32(\"Aeree{room}\", 1);");
            L($"\t\tif(Feu(SCR_GetVarS32(\"G_{group}\")) > 0.0)");
            Msg("\t\t\t", "EVENT_ECO_EVACUE_FUMEE");
            L($"\t\tCroissance(SCR_GetVarS32(\"G_{group}\"), -1.0);");
            if (victim.Length > 0)
                Show("\t\t", victim);
            L("\t}");
        }
        L("}");
        L("// la zone de l'étage près de l'escalier : les pièces 5 et 6 se découvrent (FD1 : decache0 ; le moteur enlève les");
        L("// couvercles noirs des pièces de lui-même)");
        L("void decache0(EntityID Unit, EntityID Zone)");
        L("{");
        L("}");
        L("");
        // ---- acte 3 : l'école ----
        L("// acte 3 (FD1 : transS3) : l'étage de l'école brûle autour de la grue ; un sapeur arrive en renfort");
        L("void Ecole_Acte3()");
        L("{");
        L("\tSCR_SetVarS32(\"Acte\", 3);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Acte2\"), 1);");
        L($"\tAllume(SCR_GetVarS32(\"G_{EcoleSchool}\"), SCR_GetVarS32(\"A_{EcoleSchool}\"));");
        L($"\tAllume(SCR_GetVarS32(\"G_{EcoleCraneElec}\"), SCR_GetVarS32(\"A_{EcoleCraneElec}\"));");
        Roof("\t", "toitEcole");
        Cin("\t", "CUT_PRES3");
        L("\tSCR_RunDelayed(\"Sapeur\", 10.0);");
        L($"\tSCR_SetVarS32(\"Obj_Acte3\", LEV_AddObjectiveIterate(\"{T("ECOLE_DES_S3O0")}\", {EcoleAct3Victims.Length}));");
        L($"\tSCR_SetVarS32(\"Obj_Feux3\", LEV_AddObjectiveBoolean(\"{T("ECOLE_DES_S3O1")}\"));");
        L("}");
        L("void Sapeur()");
        L("{");
        L("\tENT_SetVisibility(ENT_FindByName(\"pompier5\"), 1);");
        L("}");
        L("// une unité entre dans une salle de classe (FD1 : entrePiece, checkFO) : la salle du fond prend feu, risque de");
        L("// flash-over deux minutes plus tard s'il n'est pas maîtrisé");
        L("void EntreeEcole()");
        L("{");
        L("\tif(SCR_GetVarS32(\"Acte\") < 3 || SCR_GetVarS32(\"FoLance\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"FoLance\", 1);");
        L($"\tFIR_HPEnable(SCR_GetVarS32(\"G_{EcoleFlashover}\"), 1);");
        L("\tSCR_RunDelayed(\"FlashoverPres\", 20.0);");
        L("}");
        L("void FlashoverPres()");
        L("{");
        L($"\tFIR_HPSetIntensity(SCR_GetVarS32(\"G_{EcoleFlashover}\"), 10.0);");
        Cin("\t", "CUT_FO_PRES");
        L("\tSCR_RunDelayed(\"Flashover\", 120.0);");
        L("}");
        L("void Flashover()");
        L("{");
        L($"\tif(Feu(SCR_GetVarS32(\"G_{EcoleFlashover}\")) <= 60.0)");
        L("\t\treturn;");
        Cin("\t", "CUT_FO_END");
        L($"\tAllume(SCR_GetVarS32(\"G_{EcoleFlashover}\"), SCR_GetVarS32(\"A_{EcoleFlashover}\"));");
        L($"\tFIR_HPSetIntensity(SCR_GetVarS32(\"G_{EcoleFlashover}\"), 100.0);");
        L("}");
        foreach ((string trigger, string victim) in new[] { ("entrePiece11", "civil11"), ("entrePiece12", "civil12"), ("entrePiece13", ""), ("entrePiece15", "civil10"), ("entrePiece16", "") })
        {
            L($"void {trigger}(EntityID Unit, EntityID Zone)");
            L("{");
            L("\tif(SCR_GetVarS32(\"Acte\") < 3)");
            L("\t\treturn;");
            L("\tEntreeEcole();");
            if (victim.Length > 0)
                Show("\t", victim);
            L("}");
        }
        L("// le plancher de la classe s'effondre (FD1 : launchEffondr)");
        L("void launchEffondr(EntityID Unit, EntityID Zone)");
        L("{");
        L("\tif(SCR_GetVarS32(\"Acte\") < 3 || SCR_GetVarS32(\"Effondre\") == 1)");
        L("\t\treturn;");
        L("\tSCR_SetVarS32(\"Effondre\", 1);");
        L($"\tAllume(SCR_GetVarS32(\"G_{EcoleCollapse}\"), SCR_GetVarS32(\"A_{EcoleCollapse}\"));");
        Cin("\t", "CUT_EFFONDR");
        // le plancher effondré : la porte de la classe est condamnée (le pompier et le civil sortent par une fenêtre, avec
        // l'échelle du camion)
        L("\tENT_SwitchState(ENT_FindByName(\"bloque15\"));");
        L($"\tSCR_RunDelayed(\"FinEffondrement\", 11.0);");
        L("}");
        L("void FinEffondrement()");
        L("{");
        L($"\tCroissance(SCR_GetVarS32(\"G_{EcoleCollapse}\"), -10.0);");
        L("}");
        L("// portes des salles de classe cassées : les professeurs paraissent ; le câble tombé devant l'école est découpé");
        L("void Ecole_Portes()");
        L("{");
        foreach ((string door, string victim) in new[] { ("porte11", "civil11"), ("porte12", "civil12"), ("porte15", "civil10") })
        {
            L($"\tif(Cassee(\"{door}\") == 1)");
            Show("\t\t", victim);
        }
        L("\tif(SCR_GetVarS32(\"CableCoupe\") == 0 && Cassee(\"porte30\") == 1)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"CableCoupe\", 1);");
        Msg("\t\t", "EVENT_ECO_COUPER_CABLE");
        L("\t}");
        // câble découpé : les feux électriques de la flèche de la grue qui brûlent deviennent des feux ordinaires (FD1 :
        // doorOnBreak30, le relais aux feux non électriques) ; tant qu'ils ne brûlent pas (avant l'acte 3), rien à couper
        L($"\tif(SCR_GetVarS32(\"CableCoupe\") == 1 && Feu(SCR_GetVarS32(\"G_{EcoleCraneElec}\")) > 0.0)");
        L($"\t\tFIR_HPEnable(SCR_GetVarS32(\"G_{EcoleCraneElec}\"), 0);");
        L("}");
        L("");
        // ---- victimes et fin ----
        L("// civil mis à l'abri (rappel du moteur)");
        L("void OnCivilianSaved(EntityID Civil)");
        L("{");
        foreach (string c in EcoleAct2Victims.Concat(EcoleAct3Victims).Prepend("ouvrier0"))
        {
            L($"\tif(Civil == ENT_FindByName(\"{c}\"))");
            L($"\t\tSCR_SetVarS32(\"Sauve_{c}\", 1);");
        }
        L("\tif(Civil == ENT_FindByName(\"ouvrier0\"))");
        L("\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Ouvrier\"), 1);");
        L("}");
        L("void Ecole_Defaite()");
        L("{");
        L("\tLEV_TriggerDefeat();");
        L("}");
        L("// victoire (FD1 : transS4) : texte selon les victimes");
        L("void Ecole_Victoire()");
        L("{");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Acte3\"), 1);");
        L("\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Feux3\"), 1);");
        L("\tif(LEV_GetNumVictims() == 0)");
        L($"\t\tLEV_SetDebriefText(\"{T("VICTORY1")}\");");
        L("\telse");
        L($"\t\tLEV_SetDebriefText(\"{T("VICTORY2")}\");");
        L("\tLEV_TriggerVictory();");
        L("}");
        L("");
        L("// boucle : défaite, fenêtres et portes, transformateur, victimes et feux de l'acte en cours");
        L("void Ecole_Main()");
        L("{");
        L("\ts32 n;");
        L("\tif(SCR_GetVarS32(\"Fin\") != 0)");
        L("\t\treturn;");
        L("\tif(LEV_IsHealVehicleRemaining() == 0)");
        L("\t{");
        L("\t\tSCR_SetVarS32(\"Fin\", 2);");
        L($"\t\tLEV_SetDebriefText(\"{T("DEFEAT_MM")}\");");
        L("\t\tSCR_RunDelayed(\"Ecole_Defaite\", 5.0);");
        L("\t\treturn;");
        L("\t}");
        L("\tEcole_Fenetres();");
        L("\tEcole_Portes();");
        L("\tif(SCR_GetVarS32(\"Acte\") == 1)");
        L("\t{");
        // FD1 : le feu du baraquement éteint, l'acte 2 commence quelques minutes après (startS2) ; ici, EcoleAct2Delay s après
        // que l'ouvrier est sauvé et son baraquement éteint, ou au plus tard après dix minutes
        L($"\t\tif(SCR_GetVarS32(\"Acte2Prevu\") == 0 && SCR_GetVarS32(\"Sauve_ouvrier0\") == 1 && Feu(SCR_GetVarS32(\"G_{EcoleHut}\")) <= 0.0)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Acte2Prevu\", 1);");
        L($"\t\t\tSCR_RunDelayed(\"Ecole_Acte2\", {EcoleAct2Delay}.0);");
        L("\t\t}");
        L("\t\tif(SCR_GetVarS32(\"Acte2Prevu\") == 0 && UTL_GetGameTime() > 600.0)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Acte2Prevu\", 1);");
        L("\t\t\tEcole_Acte2();");
        L("\t\t}");
        L("\t\treturn;");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Acte\") == 2)");
        L("\t{");
        // la chaufferie éteinte : la dioxine ne gagne plus de pièce (FD1 : checkScenario3, transfoDone)
        L($"\t\tif(SCR_GetVarS32(\"TransfoFini\") == 0 && Feu(SCR_GetVarS32(\"G_{EcoleBoiler}\")) <= 0.0)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"TransfoFini\", 1);");
        Cin("\t\t\t", "EXT_TRANS");
        L($"\t\t\tif(SCR_GetVarS32(\"Contamines\") < {EcoleRooms.Length})");
        L("\t\t\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Pieces\"), 1);");
        L("\t\t}");
        L($"\t\tif(SCR_GetVarS32(\"Contamines\") >= {EcoleRooms.Length} && SCR_GetVarS32(\"PiecesPerdues\") == 0)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"PiecesPerdues\", 1);");
        L("\t\t\tLEV_ValidateObjective(SCR_GetVarS32(\"Obj_Pieces\"), 0);");
        L("\t\t}");
        L($"\t\tn = {Saved(EcoleAct2Victims)};");
        L("\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Acte2\"), n);");
        // FD1 : les victimes sauvées ou la chaufferie éteinte, l'école prend feu un peu plus tard (startS2 : 100 à 200 s)
        L($"\t\tif(SCR_GetVarS32(\"Acte3Prevu\") == 0 && n >= {EcoleAct2Victims.Length} && SCR_GetVarS32(\"TransfoFini\") == 1)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Acte3Prevu\", 1);");
        L("\t\t\tSCR_RunDelayed(\"Ecole_Acte3\", 100.0);");
        L("\t\t}");
        L("\t\treturn;");
        L("\t}");
        L("\tif(SCR_GetVarS32(\"Acte\") == 3)");
        L("\t{");
        L($"\t\tn = {Saved(EcoleAct3Victims)};");
        L("\t\tLEV_UpdateObjective(SCR_GetVarS32(\"Obj_Acte3\"), n);");
        L($"\t\tif(n >= {EcoleAct3Victims.Length} && Feu(SCR_GetVarS32(\"G_{EcoleSchool}\")) + Feu(SCR_GetVarS32(\"G_{EcoleCraneElec}\")) + Feu(SCR_GetVarS32(\"G_{EcoleFlashover}\")) + Feu(SCR_GetVarS32(\"G_{EcoleCollapse}\")) <= 0.0)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Fin\", 1);");
        L("\t\t\tSCR_RunDelayed(\"Ecole_Victoire\", 5.0);");
        L("\t\t}");
        L("\t}");
        L("}");
        L("");
        return (f.ToString(), "\tEcole_Init();\r\n", "\tEcole_Main();\r\n");
    }
}
