using System.Numerics;
using System.Text;

namespace OpenFD.ConvFD1;

/// <summary>
/// Niveau FD1 (.xfl). Le format n'est pas documenté : on en tire ce qui sert, d'après des repères sûrs.
/// Objets posés : chaque objet nomme son modèle en majuscules (« ENTREPOT_PART02.FLX ») ; sa matrice monde (16 f32) est
/// juste avant (80 octets avant la longueur de la chaîne) ; suivent le modèle en minuscules, trois chaînes (« NULL » ou
/// la « race » d'un personnage), la classe FD1 et le nom de l'objet.
/// </summary>
public sealed class Fd1Level
{
    public List<Fd1Object> Objects = new();
    /// <summary>Sol : une dalle de 1 × 1 unité (10 m) par texture « &lt;MISSION&gt;_Xnn_Ynn.BMP » nommée dans le niveau.</summary>
    public List<(int x, int y, string texture)> GroundTiles = new();
    public List<(long offset, string text)> Strings = new();
    /// <summary>
    /// Points de feu FD1 (enregistrements 0x2329 : position monde, nom du type de feu « Feux_type_I.ini », « torche_… ») ;
    /// sans ceux attachés à un objet (position nulle : feux des véhicules).
    /// </summary>
    public List<(Vector3 at, string type)> Fires = new();

    /// <summary>Type de point FD1 qui n'est pas un feu (lumières, jets d'eau).</summary>
    private static bool NotFire(string ini, bool centrale) =>
        NotFireTypes.Any(t => ini.StartsWith(t, StringComparison.OrdinalIgnoreCase))
        || (centrale && CentraleNotFireTypes.Any(t => ini.StartsWith(t, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Types de points FD1 qui ne sont pas des feux (Villa : lampadaires, jets d'eau ; Quartier : phares, feu orange, lueurs,
    /// jets de la fuite de gaz, fumée de porte). Les gyrophares (« giro_r », « giro_b ») restent des foyers : Entrepot et Banque
    /// en ont, leurs numéros de foyers en dépendent.
    /// </summary>
    private static readonly string[] NotFireTypes =
    {
        "lampad", "fx_fontaine", "phare", "feu_orange", "nico_glow", "jet_flamme", "jet_gaz", "fumee_porte",
    };

    /// <summary>
    /// Centrale seulement : sprinklers, jets d'huile des turbines, coulées de sodium, fumée des ventilateurs. Banque et
    /// StationService ont aussi des « sprinklers » : ils restent des foyers, leurs numéros de foyers en dépendent.
    /// </summary>
    private static readonly string[] CentraleNotFireTypes = { "sprinklers", "jet_oil", "flux_sodium", "fum_ventilo" };

    public static Fd1Level Load(byte[] d, bool centrale)
    {
        Fd1Level lv = new Fd1Level();
        lv.Strings = ScanStrings(d);
        for (int k = 0; k < lv.Strings.Count; k++)
        {
            (long off, string s) = lv.Strings[k];
            // véhicules de décor (camions du quai, voitures de police) : « AUTO » à la place du nom en majuscules,
            // suivi du nom du modèle en minuscules
            bool auto = s == "AUTO" && k + 1 < lv.Strings.Count && lv.Strings[k + 1].text.EndsWith(".flx", StringComparison.Ordinal);
            if (auto)
                s = lv.Strings[k + 1].text.ToUpperInvariant();
            if (!s.EndsWith(".FLX", StringComparison.Ordinal) || s.Equals("DUMMY_DEFAULT.FLX", StringComparison.OrdinalIgnoreCase))
                continue;
            Matrix4x4? world = null;
            for (long i = off - 1; i >= Math.Max(0, off - 300) && world == null; i--)
                world = MatrixAt(d, i);
            if (world == null)
                continue;
            Fd1Object o = new Fd1Object { Model = s[..^4], World = world.Value, Offset = off };
            List<string> next = lv.Strings.Skip(k + 1).Take(8).Select(x => x.text).ToList();
            int at = 0;
            if (next.Count > 0 && next[0].EndsWith(".flx", StringComparison.OrdinalIgnoreCase))
                at = 1;
            for (; at < next.Count; at++)
            {
                string t = next[at];
                if (t == "NULL")
                    continue;
                if (t.ToUpperInvariant() == t && t.Any(char.IsLetter))
                {
                    o.Race = t;
                    continue;
                }
                o.Class = t;
                o.Name = at + 1 < next.Count ? next[at + 1] : t;
                break;
            }
            lv.Objects.Add(o);
        }
        foreach ((long off, string s) in lv.Strings)
        {
            if (off < 152 || !s.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) || BitConverter.ToUInt32(d, (int)off - 152) != 0x2329)
                continue;
            Vector3 at = new Vector3(BitConverter.ToSingle(d, (int)off - 136), BitConverter.ToSingle(d, (int)off - 132), BitConverter.ToSingle(d, (int)off - 128));
            if (!float.IsFinite(at.X) || !float.IsFinite(at.Y) || !float.IsFinite(at.Z) || (at.X == 0 && at.Y == 0))
                continue;
            // effets du décor posés comme des points de feu (Villa : « lampad » les lampadaires, « fx_fontaine » les jets
            // d'eau ; Quartier : « phare ») : ils ne brûlent pas
            if (NotFire(s, centrale))
                continue;
            lv.Fires.Add((at, s[..^4]));
        }
        HashSet<(int, int)> seen = new HashSet<(int, int)>();
        foreach ((long _, string s) in lv.Strings)
        {
            System.Text.RegularExpressions.Match m = TileRx.Match(s);
            if (m.Success && seen.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))))
                lv.GroundTiles.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), s));
        }
        return lv;
    }

    private static readonly System.Text.RegularExpressions.Regex TileRx = new(@"^[A-Za-z0-9_]*?_X(\d+)_Y(\d+)\.BMP$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Matrice affine 4×4 plausible à cet endroit (dernière colonne 0, 0, 0, 1 ; axes de longueur raisonnable).</summary>
    private static Matrix4x4? MatrixAt(byte[] d, long at)
    {
        if (at < 0 || at + 64 > d.Length)
            return null;
        float[] f = new float[16];
        for (int i = 0; i < 16; i++)
        {
            f[i] = BitConverter.ToSingle(d, (int)at + 4 * i);
            if (!float.IsFinite(f[i]) || MathF.Abs(f[i]) > 1e6f)
                return null;
        }
        if (MathF.Abs(f[15] - 1) > 1e-3f || MathF.Abs(f[3]) > 1e-4f || MathF.Abs(f[7]) > 1e-4f || MathF.Abs(f[11]) > 1e-4f)
            return null;
        for (int row = 0; row < 3; row++)
        {
            float len = MathF.Sqrt(f[4 * row] * f[4 * row] + f[4 * row + 1] * f[4 * row + 1] + f[4 * row + 2] * f[4 * row + 2]);
            if (len < 0.05f || len > 20)
                return null;
        }
        return new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
    }

    /// <summary>Toutes les chaînes « u32 longueur + texte imprimable + 0 » du fichier.</summary>
    public static List<(long, string)> ScanStrings(byte[] d)
    {
        List<(long, string)> res = new List<(long, string)>();
        int i = 0;
        while (i + 5 < d.Length)
        {
            int n = BitConverter.ToInt32(d, i);
            if (n >= 2 && n < 260 && i + 4 + n <= d.Length && d[i + 3 + n] == 0 && Printable(d, i + 4, n - 1))
            {
                res.Add((i, Encoding.Latin1.GetString(d, i + 4, n - 1)));
                i += 4 + n;
            }
            else
                i++;
        }
        return res;
    }

    private static bool Printable(byte[] d, int start, int len)
    {
        for (int i = start; i < start + len; i++)
            if (d[i] < 32 || d[i] > 126)
                return false;
        return true;
    }
}
