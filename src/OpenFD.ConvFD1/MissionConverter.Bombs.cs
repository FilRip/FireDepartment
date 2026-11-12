using System.Numerics;
using System.Text;
using OpenFD.Formats;
using OpenFD.MG;

namespace OpenFD.ConvFD1;

/// <summary>
/// Bombes FD1 (Entrepot : « bombe0 » à « bombe4 »). FD1 en pose deux objets l'un sur l'autre : le mécanisme visible
/// (« bombe&lt;n&gt;Mec », classe Mechanisme, modèle « ent_bombe ») que le technicien désamorce (événement « BombePick »), et la
/// charge cachée (« bombe&lt;n&gt; », conteneurcomposant) qui brûle et explose. Pour le moteur : un seul objet par bombe,
/// réparable (seul le technicien répare : il la désamorce), qui explose quand le feu l'a détruite (comme « checkBombes » de
/// FD1, même désamorcée). Le désamorçage appelle la fonction « Desarm&lt;n&gt; » du script de la mission.
/// L'amorçage (bip, compte à rebours, explosion par le script) viendra avec la traduction des scripts FD1.
/// </summary>
public sealed partial class MissionConverter
{
    /// <summary>Modèle FD1 des bombes.</summary>
    private static bool IsBomb(Fd1Object o) => o.Model.Equals("ent_bombe", StringComparison.OrdinalIgnoreCase)
        && (o.Class.Equals("Mechanisme", StringComparison.OrdinalIgnoreCase) || o.Class.Equals("conteneurcomposant", StringComparison.OrdinalIgnoreCase));

    /// <summary>Bombes trouvées dans le niveau FD1 (mécanisme visible, numéro n de « bombe&lt;n&gt;Mec », maillages).</summary>
    private readonly List<(Fd1Object o, int n, List<Fd3Mesh> meshes)> _bombs = new List<(Fd1Object, int, List<Fd3Mesh>)>();

    /// <summary>Points de vie, dégâts et portées des explosions (FD1 : [HP] 100, [DAMMAGE] 50).</summary>
    private const float BombLife = 100f;
    private const float BombPower = 50f;
    private const float BombBlastRadius = 600f;
    private const float BombFireRadius = 1000f;
    private const float BombStaminaRadius = 800f;

    /// <summary>
    /// Numéro de la bombe : le nom FD1 qui suit le modèle dans l'enregistrement (« bombe4Mec » pour le mécanisme, « bombe4 »
    /// pour la charge), -1 sans nom.
    /// </summary>
    private static int BombNumber(Fd1Level level, Fd1Object o)
    {
        foreach ((long offset, string text) in level.Strings)
        {
            if (offset <= o.Offset + 60 || offset >= o.Offset + 140)
                continue;
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(text, @"^bombe(\d+)(Mec)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
                return int.Parse(m.Groups[1].Value);
        }
        return -1;
    }

    /// <summary>
    /// Classe des bombes : Design\placeable\functional\useable\&lt;nom&gt;_&lt;modèle&gt;.class (réparable, pas utilisable,
    /// explosive). Rend son chemin.
    /// </summary>
    private string WriteBombClass(string model, List<Fd3Mesh> meshes)
    {
        (string safe, List<(string node, Matrix4x4 world)> _, float _) = WritePlaceableModel(model, meshes);
        string classDir = Path.Combine(Data, "Design", "placeable", "functional", "useable");
        Directory.CreateDirectory(classDir);
        string power = BombPower.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string cls = $"""
            [#INCLUDE]
            FILE0 = "base_useable.class"

            [DISPLAY]
            MODEL = {Name}\{safe}

            [DAMAGE]
            USEDAMAGE = 1
            CARBONISABLE = 0
            FIREDAMAGECOEF = 1
            LIFEPOINTS = {BombLife}
            MAXLIFEPOINTS = {BombLife}

            [CAPACITIES_ACTIONS]
            COULDBEUSE = 0
            COULDBEREPAIR = 1

            [EXPLOSION]
            CANEXPLODE = 1
            LP_EXPLODE_POWER = {power}
            LP_EXPLODE_RADIUS = {BombBlastRadius}
            HP_EXPLODE_POWER = {power}
            HP_EXPLODE_RADIUS = {BombFireRadius}
            STAMINA_EXPLODE_POWER = {power}
            STAMINA_EXPLODE_RADIUS = {BombStaminaRadius}

            [SOUND_EXPLOSION]
            LABEL = EXPLOSION_GAS_TANK_01_01
            RANGE_MAX = 8000
            RANGE_MIN = 2000

            [LOGICAL_STATE]
            S01_USER_ANIM = ItemChar\ic_pick_up_down
            S01__NAME = use

            """;
        File.WriteAllBytes(Path.Combine(classDir, safe + ".class"), Encoding.Latin1.GetBytes(cls.Replace("\n", "\r\n")));
        return "design\\placeable\\functional\\useable\\" + safe;
    }

    /// <summary>Un objet du jeu dont les actions appellent une fonction du script (Sawmill : la bombe « DeBomb1 ») : modèle des
    /// objets utilisables (bombes, boutons).</summary>
    private LevelObject? ActionTemplate()
    {
        foreach (string lv in Fd3.List("Design\\Mission\\").Where(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Level l = Level.Parse(Fd3.Read(lv));
                foreach (LevelObject o in l.Objects.Where(o => o.HasTransform && o.ClassPath.EndsWith("useable\\bomb", StringComparison.OrdinalIgnoreCase)))
                    if (o.GetComponent(0xA027) is { } c && o.GetComponent(0xA024) != null && o.GetComponent(0xA029) != null
                        && ActionScripts(o.ComponentBytes(c).ToArray()).Count > 0)
                        return o;
            }
            catch (Exception)
            {
            }
        }
        return null;
    }

    /// <summary>Textes (longueur sur 4 octets, texte terminé par 0) du composant qui sont des noms de fonction du script.</summary>
    private static List<(int at, int len)> ActionScripts(byte[] b)
    {
        List<(int, int)> res = new List<(int, int)>();
        for (int p = 0; p + 4 < b.Length; p++)
        {
            int len = BitConverter.ToInt32(b, p);
            if (len < 2 || len > 80 || p + 4 + len > b.Length || b[p + 4 + len - 1] != 0)
                continue;
            string s = Encoding.Latin1.GetString(b, p + 4, len - 1);
            if (!System.Text.RegularExpressions.Regex.IsMatch(s, @"^[ -~]+$"))
                continue;
            bool function = System.Text.RegularExpressions.Regex.IsMatch(s, @"^[A-Za-z_]\w+$")
                && !System.Text.RegularExpressions.Regex.IsMatch(s, @"^(use\d*|repair\d*|open|close)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (function)
                res.Add((p, len));
            p += 3 + len;
        }
        return res;
    }

    /// <summary>Les actions de l'objet appellent cette fonction du script (composant A027).</summary>
    private static LevelObject WithActionScript(LevelObject o, string function)
    {
        if (o.GetComponent(0xA027) is not { } comp)
            return o;
        byte[] b = o.ComponentBytes(comp).ToArray();
        byte[] name = Encoding.Latin1.GetBytes(function + "\0");
        using MemoryStream ms = new MemoryStream();
        int from = 0;
        foreach ((int at, int len) in ActionScripts(b))
        {
            ms.Write(b, from, at - from);
            ms.Write(BitConverter.GetBytes(name.Length));
            ms.Write(name);
            from = at + 4 + len;
        }
        ms.Write(b, from, b.Length - from);
        return LevelEdit.WithComponent(o, comp, ms.ToArray());
    }

    /// <summary>
    /// Points de vie de l'objet (composant A024 : vie, vie maximale, sensibilité au feu) et son explosion (A029 : explose,
    /// puis les dégâts et portées sur les objets, les foyers et les pompiers), comme la classe.
    /// </summary>
    private static LevelObject WithBombDamage(LevelObject o)
    {
        if (o.GetComponent(0xA024) is { } dc)
        {
            byte[] d = o.ComponentBytes(dc).ToArray();
            if (d.Length >= 21 && d[4] == 0x0C)
            {
                BitConverter.GetBytes(BombLife).CopyTo(d, 5);
                BitConverter.GetBytes(BombLife).CopyTo(d, 9);
                BitConverter.GetBytes(1f).CopyTo(d, 17);
                o = LevelEdit.WithComponent(o, dc, d);
            }
        }
        if (o.GetComponent(0xA029) is { } ec)
        {
            byte[] e = o.ComponentBytes(ec).ToArray();
            for (int i = 0; i + 8 <= e.Length; i++)
            {
                if (!Chunks.IsEnd(e, i) || i + 4 + 25 > e.Length)
                    continue;
                int k = i + 4;
                e[k] = 1;
                float[] values = { BombPower, BombBlastRadius, BombPower, BombFireRadius, BombPower, BombStaminaRadius };
                for (int v = 0; v < values.Length; v++)
                    BitConverter.GetBytes(values[v]).CopyTo(e, k + 1 + 4 * v);
                o = LevelEdit.WithComponent(o, ec, e);
                break;
            }
        }
        return o;
    }

    /// <summary>Bombes dans le niveau du moteur : « bombe&lt;n&gt; », désamorcées par « Desarm&lt;n&gt; ».</summary>
    private void AddBombs(Level level)
    {
        if (_bombs.Count == 0)
            return;
        LevelObject? template = ActionTemplate();
        if (template == null)
        {
            Report.Add("bombes : bombe modèle introuvable dans les missions du jeu");
            return;
        }
        _bombs.Sort((a, b) => a.n.CompareTo(b.n));
        string? cls = null;
        foreach ((Fd1Object o, int n, List<Fd3Mesh> meshes) in _bombs)
        {
            cls ??= WriteBombClass(o.Model, meshes);
            Vector3 at = new Vector3(o.World.M41, o.World.M42, o.World.M43) * WorldToCm;
            float yaw = MathF.Atan2(o.World.M12, o.World.M11);
            LevelObject b = LevelEdit.Clone(template, LevelEdit.NewId(level), $"bombe{n}", cls);
            b = LevelEdit.WithTransform(b, at, new Vector3(0, 0, yaw));
            b = LevelEdit.WithStartVisible(b, true);
            b = WithActionScript(b, $"Desarm{n}");
            b = WithBombDamage(b);
            level.Objects.Add(b);
        }
        Report.Add($"bombes : {_bombs.Count} ({string.Join(", ", _bombs.Select(x => "bombe" + x.n))})");
    }

    /// <summary>Script des bombes : départ (états), boucle (bombe détruite par le feu : elle disparaît) et désamorçage.</summary>
    private (string start, string main, string functions) BombScript()
    {
        StringBuilder start = new StringBuilder();
        StringBuilder main = new StringBuilder();
        StringBuilder functions = new StringBuilder();
        foreach ((Fd1Object _, int n, List<Fd3Mesh> _) in _bombs)
        {
            // états de FD1 (InitBombes) : 0 en attente, -1 amorcée, 1 désamorcée, 2 explosée
            start.Append($"\tSCR_SetVarS32(\"bombe{n}Mec\", 0);\r\n");
            main.Append($"\tif(SCR_GetVarS32(\"bombe{n}Mec\") != 2)\r\n\t{{\r\n");
            main.Append($"\t\tif(ENT_GetDamage(ENT_FindByName(\"bombe{n}\")) <= 0.0)\r\n\t\t{{\r\n");
            main.Append($"\t\t\tSCR_SetVarS32(\"bombe{n}Mec\", 2);\r\n");
            main.Append($"\t\t\tENT_SetCapacityTarget(ENT_FindByName(\"bombe{n}\"), \"CouldBeRepair\", 0);\r\n");
            main.Append($"\t\t\tENT_SetVisibility(ENT_FindByName(\"bombe{n}\"), 0);\r\n");
            main.Append("\t\t}\r\n\t}\r\n");
            functions.Append($"// désamorçage de la bombe {n} par le technicien (FD1 : BombePick)\r\n");
            functions.Append($"void Desarm{n}(EntityID User, EntityID Bombe, mcString State)\r\n{{\r\n");
            functions.Append($"\tif(SCR_GetVarS32(\"bombe{n}Mec\") == 0 || SCR_GetVarS32(\"bombe{n}Mec\") == -1)\r\n\t{{\r\n");
            functions.Append($"\t\tSCR_SetVarS32(\"bombe{n}Mec\", 1);\r\n");
            // texte FD1 (« Votre officier technique a désamorcé un engin incendiaire… ») quand la mission a ses textes
            functions.Append($"\t\tPNL_AddMessage(\"\", \"{(_hasTexts ? TextKey("EVENT_ENT_BOMBE_ING") : "Bombe désamorcée")}\", 0.0, 0.0);\r\n");
            functions.Append("\t}\r\n}\r\n\r\n");
        }
        return (start.ToString(), main.ToString(), functions.ToString());
    }
}
