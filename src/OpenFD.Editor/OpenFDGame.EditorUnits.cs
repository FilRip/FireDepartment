using OpenFD.Formats;
using OpenFD.Game;

namespace OpenFD.MG;

// éditeur : outils des pompiers (hache, lance, grappin…) — cases à cocher dans les propriétés, outils absents cachés dans
// la vue. Les outils viennent de la classe ([CAPACITIES_ACTIONS] CanBreak…) : un pompier aux outils changés reçoit sa propre
// classe « openfd_<classe>_<outils> » (elle reprend l'autre et change ces clés), écrite avec la mission.
public sealed partial class OpenFDGame
{
    /// <summary>Outil : clé de la classe, clé du texte, texte français, action du jeu, pièces du modèle (noms en majuscules).</summary>
    private static readonly (string key, string text, string french, string action, string[] parts)[] Tools =
    {
        ("CanAttackFire", "ToolNozzle", "Lance", "ATTACKFIRE", new[] { "LAUNCHFIRE" }),
        ("CanBreak", "ToolAxe", "Hache", "BREAK", new[] { "_AXE" }),
        ("CanExtricate", "ToolSaw", "Désincarcérateur (scie)", "EXTRICATE", new[] { "DES_SCIE" }),
        ("CanGrapplin", "ToolGrapple", "Grappin", "GRAPPLIN", new[] { "SCOUT_GRAPPIN", "SCOUT_PISTOLET" }),
        ("CanHeal", "ToolMedic", "Trousse de soins", "HEAL", new[] { "MEDIC_TROUSS" }),
        ("CanRepair", "ToolRepair", "Trousse à outils", "REPAIR", new[] { "ING_TROUSS" }),
        ("CanDetoxify", "ToolDetox", "Détecteur (décontamination)", "DETOXIFY", new[] { "BIO_DETEC" }),
    };

    /// <summary>Outils de la classe (vrai : le pompier l'a).</summary>
    private bool[] EdToolsOf(string classPath)
    {
        ClassFile? c = EdClass(classPath);
        return Tools.Select(t => c != null && c.GetBool("CAPACITIES_ACTIONS", t.key, t.key == "CanAttackFire")).ToArray();
    }

    /// <summary>Pièces du modèle à cacher : les outils que la classe n'a pas, la grosse lance (sauf « canon »), les éclairs.</summary>
    private HashSet<int>? EdHiddenTools(string classPath, string? model)
    {
        if (model == null)
            return null;
        UnitModel(model);
        if (!_unitPartNames.TryGetValue(model, out List<string>? names))
            return null;
        bool[] have = EdToolsOf(classPath);
        bool canon = classPath.Contains("canon", StringComparison.OrdinalIgnoreCase);
        HashSet<int> hidden = new HashSet<int>();
        for (int i = 0; i < names.Count; i++)
        {
            string n = names[i];
            if (n.Contains("NOSHAD") || (n.EndsWith("_CANON") && !canon))
                hidden.Add(i);
            for (int t = 0; t < Tools.Length; t++)
                if (!have[t] && Tools[t].parts.Any(p => n.Contains(p)))
                    hidden.Add(i);
        }
        return hidden;
    }

    /// <summary>Classe d'origine d'une classe de l'éditeur : « design\unit\fireman\openfd_fireman_sp_us_5 » → « …\fireman_sp_us ».</summary>
    private static string EdBaseClass(string classPath)
    {
        int cut = classPath.LastIndexOf('\\') + 1;
        string leaf = classPath[cut..];
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(leaf, @"^openfd_(.+)_[0-9a-f]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? classPath[..cut] + m.Groups[1].Value : classPath;
    }

    /// <summary>Coche ou décoche un outil : le pompier passe à la classe qui a ces outils (faite ici si besoin).</summary>
    private void EdToggleEquipment(EditorObject e, string toolKey)
    {
        int t = Array.FindIndex(Tools, x => x.key.Equals(toolKey, StringComparison.OrdinalIgnoreCase));
        if (t < 0)
            return;
        bool[] have = EdToolsOf(e.Source.ClassPath);
        have[t] = !have[t];
        string baseClass = EdBaseClass(e.Source.ClassPath);
        bool[] baseHave = EdToolsOf(baseClass);
        string cls = baseClass;
        if (!have.SequenceEqual(baseHave))
        {
            int bits = 0;
            for (int i = 0; i < have.Length; i++)
                if (have[i])
                    bits |= 1 << i;
            string leaf = baseClass[(baseClass.LastIndexOf('\\') + 1)..];
            cls = baseClass[..(baseClass.LastIndexOf('\\') + 1)] + $"openfd_{leaf}_{bits:x}";
            string rel = EditorCatalog.ClassFileOf(cls);
            if (!_edNewClasses.ContainsKey(rel) && _files.TryRead(rel) == null)
                _edNewClasses[rel] = EdToolClassText(baseClass, have);
        }
        EdUpdate(e, LevelEdit.WithClass(e.Source, cls));
        e.HiddenParts = EdHiddenTools(cls, e.Model);
        EdMessage(Tr("ToolChanged", "{0} : {1} {2}", e.Name, Tr(Tools[t].text, Tools[t].french), have[t] ? Tr("ToolOn", "ajouté(e)") : Tr("ToolOff", "retiré(e)")));
    }

    /// <summary>
    /// Texte de la classe : elle reprend l'autre ([#INCLUDE]) et change ses outils ; ses actions (boutons du jeu d'origine)
    /// sont celles de l'autre, sans les outils retirés, avec les outils ajoutés.
    /// </summary>
    private string EdToolClassText(string baseClass, bool[] have)
    {
        ClassFile? c = EdClass(baseClass);
        List<string> actions = new List<string>();
        for (int i = 1; i <= 9; i++)
            if (c?.Get("CAPACITIES_ACTIONS", $"Action{i}")?.Trim('"', ' ').ToUpperInvariant() is { Length: > 0 } a && a != "NONE")
                actions.Add(a);
        for (int t = 0; t < Tools.Length; t++)
        {
            if (!have[t])
                actions.Remove(Tools[t].action);
            else if (!actions.Contains(Tools[t].action))
                actions.Add(Tools[t].action);
        }
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        string leaf = baseClass[(baseClass.LastIndexOf('\\') + 1)..];
        sb.Append("; classe faite par l'éditeur de missions d'OpenFD : ").Append(leaf).Append(" avec d'autres outils\r\n");
        sb.Append("[#INCLUDE]\r\nFILE0 = \"").Append(leaf).Append(".class\"\r\n\r\n[CAPACITIES_ACTIONS]\r\n");
        for (int t = 0; t < Tools.Length; t++)
            sb.Append(Tools[t].key).Append(" = ").Append(have[t] ? 1 : 0).Append("\r\n");
        for (int i = 0; i < 9; i++)
            sb.Append("Action").Append(i + 1).Append(" = \"").Append(i < actions.Count ? actions[i] : "NONE").Append("\"\r\n");
        return sb.ToString();
    }
}
