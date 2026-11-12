using System.Numerics;
using System.Text;
using OpenFD.Formats;

namespace OpenFD.ConvFD1;

/// <summary>
/// Cinématiques FD1 (fichier .ccs de la mission : Entrepot, « Ent_cutscene.ccs ») rejouées comme celles de FD2 et FD3 :
/// un point de caméra par plan (« cine_&lt;cinématique&gt;_&lt;n&gt; ») et, dans le script, CAM_Cinematic, CAM_TeleportToWayPoint
/// puis CAM_CinematicSetToWayPoint plan après plan, les sous-titres FD1 (PNL_PrintSubtitle), enfin CAM_ModePlay. Le joueur
/// peut les passer (Échap).
/// Fichier .ccs : pour chaque cinématique, son nom, puis ses plans : « NULL », le point visé (3 flottants, en unités FD1
/// de 10 m), un entier, l'œil, un entier, puis 8 flottants (angle de vue, 0, durée du trajet depuis le plan précédent en
/// secondes…) ; entre deux plans, les clés des textes à afficher (« ENT_CUT_GARDIEN1 »).
/// </summary>
public sealed partial class MissionConverter
{
    private sealed record CineShot(Vector3 Target, Vector3 Eye, float Duration, string? Text);

    /// <summary>Cinématiques lues dans le .ccs (nom FD1 → plans).</summary>
    private readonly Dictionary<string, List<CineShot>> _cinematics = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Cinématiques coupées avant un texte, par mission (Entrepot, « PASSAGES » : la suite montre le chariot élévateur, que
    /// notre version n'utilise pas).
    /// </summary>
    private static readonly Dictionary<string, (string cine, string beforeText)[]> CinematicCuts = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Entrepot", new[] { ("PASSAGES", "ENT_CUT_PASSAGES41") } },
    };

    /// <summary>Toits FD1 et couvercles noirs des pièces (nœud du décor, boîte dans le monde).</summary>
    private readonly List<(string node, (Vector3 min, Vector3 max) box)> _covers = new();

    /// <summary>Boîte d'un maillage placé dans le monde.</summary>
    private static (Vector3 min, Vector3 max) MeshBox(Fd3Mesh m, Matrix4x4 w)
    {
        Vector3 mn = new Vector3(float.MaxValue);
        Vector3 mx = new Vector3(float.MinValue);
        for (int q = 0; q + 2 < m.Positions.Count; q += 3)
        {
            Vector3 v = Vector3.Transform(new Vector3(m.Positions[q], m.Positions[q + 1], m.Positions[q + 2]), w);
            mn = Vector3.Min(mn, v);
            mx = Vector3.Max(mx, v);
        }
        return (mn, mx);
    }

    /// <summary>
    /// Toits et couvercles entre la caméra et ce qu'elle regarde : le regard de chaque plan (de l'œil vers le point visé,
    /// prolongé jusqu'au sol) passe sous un toit alors que l'œil est au-dessus. FD1 les enlevait avant la cinématique (Entrepot,
    /// GARDIEN1 : « decacheToit »).
    /// </summary>
    private List<string> CoversSeen(List<CineShot> shots)
    {
        List<string> seen = new List<string>();
        foreach (CineShot sh in shots)
        {
            Vector3 d = sh.Target - sh.Eye;
            if (d.Z >= 0)
                continue;
            float floor = sh.Target.Z > 1000 ? 1500 : 0;
            float tEnd = (floor - sh.Eye.Z) / d.Z;
            foreach ((string node, (Vector3 min, Vector3 max) b) in _covers)
            {
                // le dessous du toit (son point le plus bas ; le dessus d'un couvercle)
                float under = node.StartsWith("fd1_lid_", StringComparison.Ordinal) ? b.max.Z : b.min.Z;
                if (seen.Contains(node) || sh.Eye.Z <= under || floor >= under)
                    continue;
                for (int k = 0; k <= 40; k++)
                {
                    Vector3 p = sh.Eye + d * (tEnd * k / 40f);
                    if (p.Z < under && p.X > b.min.X && p.X < b.max.X && p.Y > b.min.Y && p.Y < b.max.Y)
                    {
                        seen.Add(node);
                        break;
                    }
                }
            }
        }
        return seen;
    }

    /// <summary>Temps (s) pendant lequel le dernier plan reste à l'écran.</summary>
    private const float CineHold = 3f;

    /// <summary>Lit le .ccs de la mission (celui que nomme [CUTSCENE] du .MIS FD1, sinon le premier trouvé).</summary>
    private void ReadCinematics(string misDir)
    {
        string? ccs = Directory.EnumerateFiles(misDir, "*.ccs").FirstOrDefault();
        if (ccs == null)
            return;
        byte[] d = File.ReadAllBytes(ccs);
        string? current = null;
        string? pendingText = null;
        List<CineShot> shots = new List<CineShot>();
        void Close()
        {
            if (current != null && shots.Count > 0)
                _cinematics[current] = shots;
            shots = new List<CineShot>();
            pendingText = null;
        }
        int i = 0;
        while (i + 8 < d.Length)
        {
            int n = BitConverter.ToInt32(d, i);
            if (n >= 2 && n <= 40 && i + 4 + n <= d.Length && d[i + 4 + n - 1] == 0 && d.AsSpan(i + 4, n - 1).ToArray().All(b => b >= 32 && b < 127))
            {
                string s = Encoding.ASCII.GetString(d, i + 4, n - 1);
                int p = i + 4 + n;
                if (s == "NULL" && p + 64 <= d.Length)
                {
                    Vector3 target = new Vector3(BitConverter.ToSingle(d, p), BitConverter.ToSingle(d, p + 4), BitConverter.ToSingle(d, p + 8)) * WorldToCm;
                    Vector3 eye = new Vector3(BitConverter.ToSingle(d, p + 16), BitConverter.ToSingle(d, p + 20), BitConverter.ToSingle(d, p + 24)) * WorldToCm;
                    float duration = BitConverter.ToSingle(d, p + 32 + 8);
                    // un plan : un œil au-dessus du point visé, dans la carte ; le reste (fondus, fins) est ignoré
                    bool shot = float.IsFinite(target.X) && float.IsFinite(eye.Z) && target.X > 100 && target.Y > 100 && eye.Z > target.Z
                        && eye.Z - target.Z < 5000 && float.IsFinite(duration) && duration >= 0 && duration < 60;
                    if (shot && current != null)
                    {
                        shots.Add(new CineShot(target, eye, duration, pendingText));
                        pendingText = null;
                        i = p + 64;
                        continue;
                    }
                }
                else if (s.StartsWith("ENT_CUT_", StringComparison.OrdinalIgnoreCase) || s.Contains("_CUT_", StringComparison.OrdinalIgnoreCase))
                {
                    // texte : affiché au plan suivant (ou sur le dernier, s'il n'y en a plus)
                    if (current != null && CinematicCuts.TryGetValue(Mission, out (string cine, string beforeText)[]? cuts)
                        && cuts.Any(c => c.cine.Equals(current, StringComparison.OrdinalIgnoreCase) && c.beforeText.Equals(s, StringComparison.OrdinalIgnoreCase)))
                    {
                        // le plan qui ouvre la partie coupée (saut de caméra, durée 0) part avec elle
                        while (shots.Count > 1 && shots[^1].Duration <= 0)
                            shots.RemoveAt(shots.Count - 1);
                        Close();
                        current = null;
                    }
                    else if (pendingText == null)
                        pendingText = s;
                    else if (shots.Count > 0)
                        shots[^1] = shots[^1] with { Text = shots[^1].Text ?? pendingText };
                }
                else if (s != "NULL" && s.All(c => char.IsUpper(c) || char.IsDigit(c) || c == '_'))
                {
                    Close();
                    current = s;
                }
                i = p;
                continue;
            }
            i++;
        }
        // texte lu après le dernier plan d'une cinématique : sur ce dernier plan
        Close();
        Report.Add($"cinématiques : {_cinematics.Count} ({string.Join(", ", _cinematics.Select(c => $"{c.Key} {c.Value.Count} plan(s)"))})");
    }

    /// <summary>Angles (X, 0, Z) d'un point de caméra qui regarde dans la direction f (le moteur : regard = −2e ligne).</summary>
    private static Vector3 CameraEuler(Vector3 f)
    {
        f = Vector3.Normalize(f);
        float rz = MathF.Asin(Math.Clamp(f.X, -1, 1));
        float rx = MathF.Atan2(-f.Z, -f.Y);
        return new Vector3(rx, 0, rz);
    }

    /// <summary>Points de caméra des cinématiques dans le niveau du moteur.</summary>
    private void AddCinematics(Level level, LevelObject waypoint)
    {
        foreach ((string name, List<CineShot> shots) in _cinematics)
            for (int k = 0; k < shots.Count; k++)
            {
                LevelObject cam = LevelEdit.Clone(waypoint, LevelEdit.NewId(level), $"cine_{name}_{k}", "design\\waypoint\\cam_startpos");
                level.Objects.Add(LevelEdit.WithTransform(cam, shots[k].Eye, CameraEuler(shots[k].Target - shots[k].Eye)));
            }
    }

    /// <summary>La caméra va au plan k : en glissant pendant sa durée, ou d'un coup (durée 0 : changement de plan).</summary>
    private static string MoveTo(string name, int k, float duration) => duration <= 0.05f
        ? $"\tCAM_TeleportToWayPoint(ENT_FindByName(\"cine_{name}_{k}\"));"
        : $"\tCAM_CinematicSetToWayPoint(ENT_FindByName(\"cine_{name}_{k}\"), {duration.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)});";

    /// <summary>Appel de la cinématique FD1 de ce nom (vide si le .ccs ne l'a pas).</summary>
    private string Cine(string name) => _cinematics.ContainsKey(name) ? $"Cine_{name}();" : "";

    /// <summary>
    /// Fonctions du script des cinématiques : Cine_&lt;nom&gt;() la lance (une seule à la fois : pendant qu'une cinématique
    /// passe, une autre demandée est ignorée), puis chaque plan est une fonction appelée à son heure (SCR_RunDelayed).
    /// </summary>
    private string CinematicScript()
    {
        if (_cinematics.Count == 0)
            return "";
        string T(string key) => _hasTexts ? TextKey(key) : key;
        StringBuilder f = new StringBuilder();
        void L(string line) => f.Append(line).Append("\r\n");
        L("// ===== cinématiques FD1 (.ccs) =====");
        foreach ((string name, List<CineShot> shots) in _cinematics)
        {
            L($"void Cine_{name}()");
            L("{");
            L("\tif(SCR_GetVarS32(\"CineEnCours\") == 1)");
            L("\t\treturn;");
            L("\tSCR_SetVarS32(\"CineEnCours\", 1);");
            L("\tCAM_Cinematic(1);");
            L("\tCAM_SetInertia(1.0);");
            foreach (string node in CoversSeen(shots))
            {
                L($"\tLEV_RoofVisibility(\"{node}\", 0);");
                Report.Add($"cinématique {name} : enlève {node}");
            }
            L($"\tCAM_TeleportToWayPoint(ENT_FindByName(\"cine_{name}_0\"));");
            // texte d'un plan : affiché quand la caméra part vers lui (il reste jusqu'au texte suivant)
            string? first = shots[0].Text ?? (shots.Count > 1 ? shots[1].Text : null);
            if (first != null)
                L($"\tPNL_PrintSubtitle(\"{T(first)}\");");
            if (shots.Count > 1)
                L(MoveTo(name, 1, shots[1].Duration));
            float at = 0;
            for (int k = 1; k < shots.Count; k++)
            {
                at += MathF.Max(0.1f, shots[k].Duration);
                L($"\tSCR_RunDelayed(\"Cine_{name}_{k}\", {at.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)});");
            }
            at += CineHold;
            L($"\tSCR_RunDelayed(\"Cine_{name}_Fin\", {at.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)});");
            L("}");
            for (int k = 1; k < shots.Count; k++)
            {
                // le plan k : la caméra glisse jusqu'à lui pendant sa durée ; elle y est donc à l'heure de ce plan
                L($"void Cine_{name}_{k}()");
                L("{");
                if (k + 1 < shots.Count)
                {
                    L(MoveTo(name, k + 1, shots[k + 1].Duration));
                    if (shots[k + 1].Text != null)
                        L($"\tPNL_PrintSubtitle(\"{T(shots[k + 1].Text!)}\");");
                }
                L("}");
            }
            L($"void Cine_{name}_Fin()");
            L("{");
            L("\tPNL_ClearSubtitle();");
            L("\tCAM_ModePlay();");
            L("\tSCR_SetVarS32(\"CineEnCours\", 0);");
            L("}");
            L("");
        }
        return f.ToString();
    }
}
