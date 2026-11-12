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
            }
        },
    };

    /// <summary>Nom FD1 de l'objet (dernier texte de son enregistrement avant le script) ; null : sans nom.</summary>
    private string? InstanceName(Fd1Object o)
    {
        if (_fd1 == null)
            return null;
        List<string> texts = _fd1.Strings.Where(s => s.offset > o.Offset + 40 && s.offset < o.Offset + 140).Select(s => s.text)
            .Where(t => t != "NULL").ToList();
        int script = texts.FindIndex(t => t.EndsWith(".SCP", StringComparison.OrdinalIgnoreCase));
        if (script >= 0)
            texts = texts.Take(script).ToList();
        return texts.Count > 0 ? texts[^1] : null;
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
                LevelObject? template = TemplateWhere(o => o.Kind == EntityKind.Civilian && o.ClassPath.EndsWith(cls, StringComparison.OrdinalIgnoreCase)
                    && o.GetComponent(0xA02A) is { } c && c.Size > 39 && o.ComponentBytes(c)[39] == 3);
                if (actor == null || template == null)
                {
                    Report.Add($"civils : {name} {(actor == null ? "introuvable dans le niveau FD1" : "sans civil modèle dans les missions du jeu")}");
                    continue;
                }
                Vector3 at = new Vector3(actor.World.M41, actor.World.M42, actor.World.M43) * WorldToCm;
                float yaw = MathF.Atan2(actor.World.M12, actor.World.M11);
                LevelObject c = LevelEdit.Clone(template, LevelEdit.NewId(level), name, template.ClassPath);
                c = LevelEdit.WithTransform(c, at, new Vector3(0, 0, yaw));
                c = LevelEdit.WithStartVisible(c, false);
                level.Objects.Add(c);
                n++;
            }
            Report.Add($"civils : {n} (évanouis, cachés au départ)");
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
                t = LevelEdit.WithTrigger(t, script, "", true, max - min);
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
        foreach (string v in new[] { "FeuAtelier", "GardesSauves", "SecretaireSauvee", "VideoDone", "DocDone", "BombeDone", "Fin", "Feu1Fini", "Feu2Fini", "Garde1Vu", "Garde2Vu", "SecretaireVue", "EtageFeu", "Surprise1", "Surprise3" })
            L($"\tSCR_SetVarS32(\"{v}\", 0);");
        foreach ((string name, List<string> fires) in new[] { ("hall1", hall1), ("hall2", hall2) })
        {
            L($"\tg = ENT_GroupCreate(\"feux_{name}\");");
            foreach (string h in fires)
                L($"\tENT_GroupAddEntity(g, ENT_FindByName(\"{h}\"));");
            L($"\tSCR_SetVarS32(\"G_{name}\", g);");
        }
        L($"\tSCR_SetVarS32(\"Obj_Gardes\", LEV_AddObjectiveIterate(\"{T("ENTREPOT_DES_S1O0")}\", 2));");
        L($"\tSCR_SetVarS32(\"Obj_Feu1\", LEV_AddObjectiveBoolean(\"{T("ENTREPOT_DES_S1O2")}\"));");
        L($"\tSCR_SetVarS32(\"Obj_Indices\", LEV_AddObjectiveIterate(\"{T("ENTREPOT_DES_S1O1_SEUL")}\", 3));");
        L("}");
        L("");
        L("// le scout (renfort) est arrivé : il peut monter au grappin");
        L("void MsgScout()");
        L("{");
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
        L("\t\tAmorce0();");
        L("\t}");
        L("}");
        L("void Bombe4Run()");
        L("{");
        L("\tif(FIR_HPGetIntensity(SCR_GetVarS32(\"G_hall2\")) >= 250.0 && SCR_GetVarS32(\"bombe4Mec\") == 0)");
        L("\t{");
        L("\t\tMsgBombes();");
        L("\t\tAmorce4();");
        L("\t}");
        L("}");
        L("void Bombe2Run()");
        L("{");
        L("\tif(SCR_GetVarS32(\"bombe2Mec\") == 0)");
        L("\t{");
        L("\t\tMsgBombes();");
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
        foreach ((string door, string civil, string flag, string key) in new[] { ("Porte3", "Garde2", "Garde2Vu", "ENT_CUT_GARDIEN2"), ("Porte4", "Secretaire", "SecretaireVue", "ENT_CUT_SECRETAIRE") })
        {
            L($"\tif(SCR_GetVarS32(\"{flag}\") == 0 && ENT_GetState(ENT_FindByName(\"{door}\")) == \"Broken\")");
            L("\t{");
            L($"\t\tSCR_SetVarS32(\"{flag}\", 1);");
            L($"\t\tENT_SetVisibility(ENT_FindByName(\"{civil}\"), 1);");
            Msg("\t\t", key);
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
        L("\t// FD1 : le commando mort avant d'avoir descendu l'échelle 2 (DEFEAT5) ; à faire quand le script saura retrouver le scout");
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
            L($"\tif(v == {v} && d == {d} && b == {b})");
            L($"\t\tLEV_SetDebriefText(\"{T("VICTORY" + k)}\");");
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
        L("\t\t// le feu du premier hall est presque éteint : la bombe 3 s'amorce (FD1 : Go < 400)");
        L("\t\tif(feu < 400.0 && SCR_GetVarS32(\"Surprise3\") == 0)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Surprise3\", 1);");
        L("\t\t\tif(SCR_GetVarS32(\"bombe3Mec\") == 0)");
        L("\t\t\t{");
        L("\t\t\t\tMsgBombes();");
        L("\t\t\t\tAmorce3();");
        L("\t\t\t}");
        L("\t\t}");
        L("\t\tif(feu <= 0.0 && SCR_GetVarS32(\"Feu1Fini\") == 0)");
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
        L("\t\t// l'atelier est presque éteint : la bombe 1 s'amorce (FD1 : Go < 200)");
        L("\t\tif(feu < 200.0 && SCR_GetVarS32(\"Surprise1\") == 0)");
        L("\t\t{");
        L("\t\t\tSCR_SetVarS32(\"Surprise1\", 1);");
        L("\t\t\tif(SCR_GetVarS32(\"bombe1Mec\") == 0)");
        L("\t\t\t{");
        L("\t\t\t\tMsgBombes();");
        L("\t\t\t\tAmorce1();");
        L("\t\t\t}");
        L("\t\t}");
        L("\t\tif(feu <= 0.0 && SCR_GetVarS32(\"Feu2Fini\") == 0)");
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
