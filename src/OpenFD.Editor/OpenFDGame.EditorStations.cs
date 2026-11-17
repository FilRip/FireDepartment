using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using OpenFD.Formats;
using SN = System.Numerics;

namespace OpenFD.MG;

// éditeur : fenêtre « Casernes et renforts ». Une ligne par caserne (le départ, puis les renforts) avec les images de ses
// unités : « – » sur une image l'enlève, « + » au bout de la ligne ouvre la liste des unités à ajouter. Ce qui change est
// écrit tout de suite dans le .mis ([FIRESTATION]) ; une caserne dont les unités changent devient une caserne de la mission
// (« openfd_<mission>_<n>.cfg », écrite dans Design\Firestation à la sauvegarde), celles du jeu ne sont jamais modifiées.
public sealed partial class OpenFDGame
{
    private const string StationSection = "FIRESTATION";
    private const int MaxStationUnits = 20;
    private const int MaxReinforcements = 9;

    private bool _edStOpen;                                   // fenêtre ouverte
    private int _edStPick = -1;                               // ligne dont on choisit l'unité à ajouter (-1 : aucune)
    private readonly ScrollBar _edStBar = new();
    private readonly ScrollBar _edStPickBar = new();
    private float _edStUsed;

    /// <summary>Casernes écrites par l'éditeur pour la mission : nom du fichier → texte (écrites à la sauvegarde).</summary>
    private readonly Dictionary<string, string> _edStationCfgs = new(StringComparer.OrdinalIgnoreCase);

    // unités proposées : classes sans nationalité (le jeu prend celle de la mission : Veh_P_pumper → Veh_P_pumper_gb)
    private List<string>? _edStChoices;
    private static readonly string[] NationSuffixes = { "_us", "_fr", "_gb", "_gr", "_ua", "_lp", "_us1" };

    // ---- lire et écrire les casernes de la mission ----

    /// <summary>Casernes de la mission dans l'ordre : le départ (toujours là, même vide), puis les renforts.</summary>
    private List<EdStation> EdStationsRead()
    {
        List<EdStation> list = new();
        string init = (IniText.Get(_edMisText, StationSection, "InitialDispatch") ?? "").Trim('"', ' ');
        EdStation first = EdStationFrom(init);
        first.Spawn = EdMisInt("InitialDispatchSpawnPoint", 1);
        list.Add(first);
        for (int k = 1; k <= MaxReinforcements; k++)
        {
            string r = (IniText.Get(_edMisText, StationSection, $"Reinforcement{k}") ?? "").Trim('"', ' ');
            if (r.Length == 0)
                continue;
            EdStation st = EdStationFrom(r);
            st.Eta = EdMisFloat($"Reinforcement{k}ETA", 60);
            st.Spawn = EdMisInt($"Reinforcement{k}SpawnPoint", 1);
            list.Add(st);
        }
        return list;
    }

    private int EdMisInt(string key, int fallback) =>
        int.TryParse((IniText.Get(_edMisText, StationSection, key) ?? "").Trim('"', ' '), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    private float EdMisFloat(string key, float fallback) =>
        float.TryParse((IniText.Get(_edMisText, StationSection, key) ?? "").Trim('"', ' ').Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

    /// <summary>Caserne d'après son fichier (celles de l'éditeur d'abord, puis celles du jeu).</summary>
    private EdStation EdStationFrom(string cfgName)
    {
        EdStation st = new EdStation { Cfg = cfgName };
        if (cfgName.Length == 0)
            return st;
        ClassFile? c = null;
        try
        {
            c = _edStationCfgs.TryGetValue(cfgName, out string? text)
                ? ClassFile.FromText(text)
                : ClassFile.Load(_files, "Design\\Firestation\\" + cfgName + ".cfg");
        }
        catch (Exception)
        {
        }
        if (c == null)
        {
            st.Code = cfgName;
            return st;
        }
        st.Code = c.Get(StationSection, "CodeName")?.Trim('"', ' ') is { Length: > 0 } code ? code : cfgName;
        st.Name = c.Get(StationSection, "Name")?.Trim('"', ' ') ?? "";
        st.Icon = c.Get(StationSection, "Icon")?.Trim('"', ' ') ?? "";
        st.Air = c.GetBool(StationSection, "AirSupport");
        for (int i = 1; i <= MaxStationUnits; i++)
            if (c.Get(StationSection, $"UNIT{i:00}")?.Trim('"', ' ') is { Length: > 0 } u)
                st.Units.Add(u);
        return st;
    }

    /// <summary>Écrit les casernes dans le .mis ; celles qui ont changé deviennent des casernes de la mission.</summary>
    private void EdStationsWrite(List<EdStation> list)
    {
        foreach (EdStation st in list.Where(s => s.Changed))
        {
            // déjà une caserne de l'éditeur : elle garde son nom ; sinon un nom libre
            if (!st.Cfg.StartsWith("openfd_", StringComparison.OrdinalIgnoreCase))
                st.Cfg = EdNewStationName(list);
            _edStationCfgs[st.Cfg] = EdStationText(st);
            st.Changed = false;
        }
        EdMisSet("InitialDispatch", list[0].Cfg);
        EdMisSet("InitialDispatchSpawnPoint", list[0].Spawn.ToString(CultureInfo.InvariantCulture));
        for (int k = 1; k <= MaxReinforcements; k++)
        {
            if (k < list.Count)
            {
                EdMisSet($"Reinforcement{k}", list[k].Cfg);
                EdMisSet($"Reinforcement{k}ETA", list[k].Eta.ToString("0.##", CultureInfo.InvariantCulture));
                EdMisSet($"Reinforcement{k}SpawnPoint", list[k].Spawn.ToString(CultureInfo.InvariantCulture));
            }
            else
                foreach (string key in new[] { $"Reinforcement{k}", $"Reinforcement{k}ETA", $"Reinforcement{k}SpawnPoint" })
                    if (IniText.Get(_edMisText, StationSection, key) != null)
                        _edMisText = IniText.Set(_edMisText, StationSection, key, "");
        }
    }

    private void EdMisSet(string key, string value) => _edMisText = IniText.Set(_edMisText, StationSection, key, value);

    private string EdNewStationName(List<EdStation> list)
    {
        for (int n = 1; ; n++)
        {
            string name = $"openfd_{EdShortName}_{n}";
            if (!_edStationCfgs.ContainsKey(name) && !list.Any(s => s.Cfg.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return name;
        }
    }

    private string EdStationText(EdStation st)
    {
        System.Text.StringBuilder b = new System.Text.StringBuilder();
        b.AppendLine($"; OpenFD : caserne de la mission « {EdShortName} » (éditeur)");
        b.AppendLine("[FIRESTATION]");
        if (st.Name.Length > 0)
            b.AppendLine($"Name = {st.Name}");
        b.AppendLine($"CodeName = {(st.Code.Length > 0 ? st.Code : "?")}");
        if (st.Icon.Length > 0)
            b.AppendLine($"Icon = {st.Icon}");
        for (int i = 0; i < st.Units.Count && i < MaxStationUnits; i++)
            b.AppendLine($"UNIT{i + 1:00} = {st.Units[i]}");
        return b.ToString();
    }

    /// <summary>Casernes de l'éditeur à écrire avec la mission (seulement celles que le .mis utilise).</summary>
    private void EdStationFiles(Dictionary<string, string> classes)
    {
        foreach (EdStation st in EdStationsRead())
            if (_edStationCfgs.TryGetValue(st.Cfg, out string? text))
                classes["Design\\Firestation\\" + st.Cfg + ".cfg"] = text;
    }

    /// <summary>Change les casernes : lecture, modification, écriture.</summary>
    private void EdStationsChange(Action<List<EdStation>> change)
    {
        List<EdStation> list = EdStationsRead();
        change(list);
        EdStationsWrite(list);
    }

    // ---- ligne de la mission dans les propriétés ----

    private void EdStationsSummary()
    {
        List<EdStation> list = EdStationsRead();
        string first = list[0].Cfg.Length > 0 ? $"{list[0].ShownCode} ({list[0].Units.Count})" : "-";
        EdPropText(Tr("MisTeam", "Équipe de départ"), first);
        EdPropText(Tr("MisReinforcements", "Renforts"), list.Count > 1 ? string.Join(", ", list.Skip(1).Select(s => s.ShownCode)) : "-");
        EdPropButton(Tr("EditStations", "Casernes et renforts…"), () =>
        {
            EdCommitFocus();
            _edPopup = null;
            _edStOpen = true;
            _edStPick = -1;
        });
    }

    // ---- la fenêtre ----

    private (float x, float y, float w, float h) EdStationsRect()
    {
        float s = _scale, w = MathF.Min(_fbW - 40 * s, 1180 * s), h = MathF.Min(_fbH - 40 * s, 820 * s);
        return ((_fbW - w) / 2, (_fbH - h) / 2, w, h);
    }

    private void EdStationsKeys()
    {
        if (!EdKey(Microsoft.Xna.Framework.Input.Keys.Escape))
            return;
        if (_edStPick >= 0)
            _edStPick = -1;
        else
            _edStOpen = false;
    }

    private void EdDrawStations()
    {
        (float x, float y, float w, float h) = EdStationsRect();
        float s = _scale, pad = 14 * s, bh = 34 * s;
        List<EdStation> list = EdStationsRead();
        if (_edStPick >= list.Count)
            _edStPick = -1;
        string? hoverName = null;
        _ui.Begin();
        _ui.Rect(0, 0, _fbW, _fbH, new SN.Vector4(0, 0, 0, 0.45f));
        _ui.Rect(x, y, w, h, EdPanel);
        _ui.Frame(x, y, w, h, EdAccent, 2 * s);
        _ui.Text(x + pad, y + 12 * s, Tr("StationsTitle", "Casernes et renforts"), EdAccent);
        float top = y + 16 * s + _ui.LineHeight + 8 * s, bottom = y + h - bh - 2 * pad, area = bottom - top;
        float sb = EdScrollW;
        bool picking = _edStPick >= 0;
        if (!picking && EdHit(x, top, w, area) && _edWheel != 0)
        {
            _edStBar.Value -= _edWheel / 120f * 60 * s;
            _edWheel = 0;
        }
        if (!picking)
            _edStBar.Run(_ui, x + w - sb - pad / 2, top, sb, area, _edStUsed, area, 40 * s, _edMx, _edMy, ref _edPressL, _edDownL, _edClock);
        _ui.End();

        // lignes des casernes (sous la liste de choix quand elle est ouverte : pas de clic pour elles)
        bool press = _edPressL;
        if (picking)
            _edPressL = false;
        float cw = w - 2 * pad - sb - 8 * s;
        float cell = 66 * s, iconH = 56 * s, headH = _ui.LineHeight + 14 * s;
        float cy = top - _edStBar.Value;
        _ui.BeginClipped(new Rectangle((int)x, (int)top, (int)(w - sb - pad), (int)area));
        for (int r = 0; r < list.Count; r++)
        {
            int row = r;
            EdStation st = list[r];
            float units = MathF.Max(1, (cw - 8 * s) / cell);
            int perLine = Math.Max(1, (int)units);
            int tiles = st.Units.Count + (st.Air ? 0 : 1);
            int lines = Math.Max(1, (tiles + perLine - 1) / perLine);
            float h0 = headH + lines * (iconH + 8 * s) + 10 * s;
            bool visible = cy + h0 > top && cy < bottom;
            if (visible)
                EdStationRow(row, st, x + pad, cy, cw, h0, cell, iconH, headH, perLine, ref hoverName);
            cy += h0 + 8 * s;
        }
        // « + Renfort » sous la dernière ligne
        if (cy + bh > top && cy < bottom && list.Count - 1 < MaxReinforcements)
        {
            bool inArea = _edMy >= top && _edMy < bottom;
            bool keep = _edPressL;
            if (!inArea)
                _edPressL = false;
            if (EdButton(x + pad, cy, 240 * s, bh, "+ " + Tr("AddReinforcement", "Ajouter un renfort")))
                EdStationsChange(l => l.Add(new EdStation { Code = "R" + l.Count, Eta = 60, Spawn = 1, Changed = true }));
            else if (!inArea)
                _edPressL = keep;
        }
        cy += bh + 8 * s;
        _ui.End();
        _edStUsed = cy + _edStBar.Value - top;
        _edStBar.Clamp(_edStUsed, area);
        if (picking)
            _edPressL = press;

        // bas de la fenêtre : nom de l'unité survolée, remarque, « Fermer »
        _ui.Begin();
        float by = y + h - bh - pad;
        string note = hoverName ?? Tr("StationsNote", "Les casernes du jeu ne sont pas modifiées : une caserne changée est copiée pour cette mission (écrite à la sauvegarde). Les scripts appellent les renforts par leur numéro (LEV_CallReinforcement).");
        float nw = w - 2 * pad - 170 * s;
        List<string> nl = Wrap(note, nw, _scale).Take(2).ToList();
        for (int i = 0; i < nl.Count; i++)
            _ui.Text(x + pad, by + i * (_ui.LineHeight + 2 * s), nl[i], hoverName != null ? EdText : EdMuted);
        if (!picking && EdButton(x + w - pad - 150 * s, by, 150 * s, bh, Tr("Close", "Fermer")))
            _edStOpen = false;
        _ui.End();

        if (picking)
            EdDrawStationPicker(list);
        _edPressL = false;
    }

    /// <summary>Une caserne : en-tête (nom, code, arrivée, point d'arrivée, « Supprimer ») puis ses unités.</summary>
    private void EdStationRow(int row, EdStation st, float x, float y, float w, float h, float cell, float iconH, float headH, int perLine, ref string? hoverName)
    {
        float s = _scale, fh = headH - 10 * s, fy = y + 4 * s;
        _ui.Rect(x, y, w, h, new SN.Vector4(0.13f, 0.13f, 0.16f, 1));
        _ui.Frame(x, y, w, h, EdLine, MathF.Max(1, s));
        string title = row == 0 ? Tr("StationStart", "Départ") : Tr("StationReinforcement", "Renfort {0}", row);
        float tx = x + 8 * s;
        _ui.Text(tx, fy + (fh - _ui.LineHeight) / 2, title, EdAccent);
        tx += MathF.Max(_ui.Measure(title) + 12 * s, 110 * s);
        // nom de code
        tx = EdStationLabel(tx, fy, fh, Tr("StationCode", "Code"));
        EdField($"st.code{row}", tx, fy, 80 * s, fh, st.ShownCode, v =>
        {
            string code = v.Trim();
            if (code.Length == 0)
                return;
            EdStationsChange(l =>
            {
                if (row >= l.Count || l[row].ShownCode == code)
                    return;
                l[row].Code = code;
                l[row].Changed = true;
            });
        }, !st.Air);
        tx += 92 * s;
        // renfort : arrivée (s)
        if (row > 0)
        {
            tx = EdStationLabel(tx, fy, fh, Tr("StationEta", "Arrivée (s)"));
            EdField($"st.eta{row}", tx, fy, 64 * s, fh, st.Eta.ToString("0.##", CultureInfo.InvariantCulture), v =>
            {
                if (float.TryParse(v.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float eta) && float.IsFinite(eta) && eta >= 0)
                    EdStationsChange(l =>
                    {
                        if (row < l.Count)
                            l[row].Eta = eta;
                    });
            });
            tx += 76 * s;
        }
        // point d'arrivée (waypoints locstartN_k / locendN_k)
        tx = EdStationLabel(tx, fy, fh, Tr("StationSpawn", "Point d'arrivée"));
        EdField($"st.spawn{row}", tx, fy, 44 * s, fh, st.Spawn.ToString(CultureInfo.InvariantCulture), v =>
        {
            if (int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int sp) && sp >= 1)
                EdStationsChange(l =>
                {
                    if (row < l.Count)
                        l[row].Spawn = sp;
                });
        });
        tx += 56 * s;
        string info = st.Air ? Tr("StationAir", "appui aérien (bombardier d'eau) : unités non modifiables")
            : Tr("StationCount", "{0} unité(s)", st.Units.Count);
        if (st.Cfg.Length > 0 && !st.Cfg.StartsWith("openfd_", StringComparison.OrdinalIgnoreCase))
            info += " · " + st.Cfg;
        float bw = 110 * s, gw = 150 * s;
        _ui.Text(tx, fy + (fh - _ui.LineHeight) / 2, EdFit(info, x + w - tx - bw - gw - 22 * s), EdMuted);
        // repartir d'une caserne du jeu (ses unités et son code ; l'arrivée et le point d'arrivée restent)
        if (EdButton(x + w - bw - gw - 12 * s, fy, gw, fh, Tr("StationFromGame", "Caserne du jeu…")))
        {
            List<string> teams = EdTeams();
            EdOpenPopup(Tr("StationFromGameTitle", "Caserne du jeu pour : {0}", title), teams.Select(EdTeamLabel).ToList(), i => EdStationsChange(l =>
            {
                if (row >= l.Count)
                    return;
                EdStation from = EdStationFrom(teams[i]);
                from.Eta = l[row].Eta;
                from.Spawn = l[row].Spawn;
                l[row] = from;
            }), x + w - bw - gw - 12 * s, fy + fh, 360 * s, teams.FindIndex(t => t.Equals(st.Cfg, StringComparison.OrdinalIgnoreCase)), _fbH * 0.6f);
        }
        if (row > 0 && EdButton(x + w - bw - 6 * s, fy, bw, fh, Tr("Delete", "Supprimer")))
        {
            EdStationsChange(l =>
            {
                if (row < l.Count)
                    l.RemoveAt(row);
            });
            return;
        }

        // unités : image, « – » en haut à droite ; « + » au bout
        float ux = x + 8 * s, uy = y + headH;
        for (int i = 0; i <= st.Units.Count; i++)
        {
            bool plus = i == st.Units.Count;
            if (plus && (st.Air || st.Units.Count >= MaxStationUnits))
                break;
            float tx0 = ux + (i % perLine) * cell, ty0 = uy + (i / perLine) * (iconH + 8 * s), tw = cell - 6 * s;
            if (plus)
            {
                if (EdButton(tx0, ty0, tw, iconH, "+"))
                {
                    _edStPick = row;
                    _edStPickBar.Value = 0;
                }
                continue;
            }
            string cls = st.Units[i];
            bool over = EdHit(tx0, ty0, tw, iconH);
            _ui.Rect(tx0, ty0, tw, iconH, over ? EdHover : new SN.Vector4(0.2f, 0.2f, 0.24f, 1));
            EdUnitIcon(cls, tx0, ty0, tw, iconH);
            if (over)
                hoverName = EdUnitLabel(cls) + "  (" + cls + ")";
            if (st.Air)
                continue;
            float m = 18 * s;
            int idx = i;
            if (EdButton(tx0 + tw - m, ty0, m, m, "-"))
                EdStationsChange(l =>
                {
                    if (row < l.Count && idx < l[row].Units.Count)
                    {
                        l[row].Units.RemoveAt(idx);
                        l[row].Changed = true;
                    }
                });
        }
    }

    /// <summary>Caserne du jeu dans la liste : son fichier, son code et ses unités (« bank1 - E9 : 1 véhicule(s), 4 à pied »).</summary>
    private string EdTeamLabel(string cfg)
    {
        EdStation st = EdStationFrom(cfg);
        if (st.Air)
            return $"{cfg} - {st.ShownCode} : " + Tr("StationAirShort", "appui aérien");
        int veh = st.Units.Count(u => u.StartsWith("Firevehicle", StringComparison.OrdinalIgnoreCase));
        return $"{cfg} - {st.ShownCode} : " + Tr("StationTeamCounts", "{0} véhicule(s), {1} à pied", veh, st.Units.Count - veh);
    }

    private float EdStationLabel(float x, float y, float h, string label)
    {
        _ui.Text(x, y + (h - _ui.LineHeight) / 2, label, EdMuted);
        return x + _ui.Measure(label) + 6 * _scale;
    }

    /// <summary>Image d'une unité (celle du jeu, [UNIT] Icon), centrée dans le cadre ; sinon son nom.</summary>
    private void EdUnitIcon(string cls, float x, float y, float w, float h)
    {
        string shortName = cls[(cls.LastIndexOf('\\') + 1)..];
        bool vehicle = cls.StartsWith("Firevehicle", StringComparison.OrdinalIgnoreCase);
        // comme le jeu : la version de la nationalité choisie d'abord (Veh_P_pumper → Veh_P_pumper_gb), sinon la classe elle-même
        string? icon = null;
        if (OpenFD.Game.UnitManager.Nationality.Length > 0)
            icon = EdClass("design\\unit\\" + (cls + "_" + OpenFD.Game.UnitManager.Nationality).ToLowerInvariant())?.Get("UNIT", "Icon")?.Trim('"', ' ');
        if (string.IsNullOrEmpty(icon))
            icon = EdClass("design\\unit\\" + cls.ToLowerInvariant())?.Get("UNIT", "Icon")?.Trim('"', ' ');
        Texture2D? tex = IconFor(icon, shortName, vehicle, vehicle && shortName.Contains("ambulance", StringComparison.OrdinalIgnoreCase));
        if (tex != null)
        {
            float k = MathF.Min((w - 4 * _scale) / tex.Width, (h - 4 * _scale) / tex.Height);
            _ui.Image(tex, x + (w - tex.Width * k) / 2, y + (h - tex.Height * k) / 2, tex.Width * k, tex.Height * k, new SN.Vector4(1, 1, 1, 1));
        }
        else
            _ui.Text(x + 3 * _scale, y + 3 * _scale, EdFit(shortName, w - 6 * _scale), EdText);
    }

    /// <summary>Nom de l'unité en clair (d'après sa classe).</summary>
    private static string EdUnitLabel(string cls)
    {
        string n = cls[(cls.LastIndexOf('\\') + 1)..].ToLowerInvariant();
        foreach (string suf in NationSuffixes)
            if (n.EndsWith(suf))
                n = n[..^suf.Length];
        return n switch
        {
            "fireman_sp" => Tr("UnitSp", "Pompier"),
            "fireman_medic" => Tr("UnitMedic", "Médecin"),
            "fireman_canon" => Tr("UnitCanon", "Pompier canon"),
            "fireman_des" => Tr("UnitDes", "Désincarcérateur"),
            "fireman_ing" => Tr("UnitIng", "Ingénieur"),
            "fireman_bio" => Tr("UnitBio", "Spécialiste risques chimiques"),
            "fireman_cyno" => Tr("UnitCyno", "Maître-chien"),
            "fireman_igni" => Tr("UnitIgni", "Pompier tenue ignifugée"),
            "fireman_pyro" => Tr("UnitPyro", "Artificier"),
            "fireman_scout" => Tr("UnitScout", "Éclaireur (grappin)"),
            "franc" => Tr("UnitFranc", "Franc"),
            "veh_p_pumper" => Tr("UnitPumper", "Fourgon pompe"),
            "veh_p_ambulance" => Tr("UnitAmbulance", "Ambulance"),
            "veh_p_aerial" => Tr("UnitAerial", "Grande échelle"),
            "veh_p_tanker" => Tr("UnitTanker", "Camion-citerne"),
            "veh_p_foampumper" => Tr("UnitFoamPumper", "Fourgon mousse"),
            "veh_p_foamtanker" => Tr("UnitFoamTanker", "Citerne à mousse"),
            "veh_p_watertower" => Tr("UnitWaterTower", "Bras élévateur (canon à eau)"),
            "veh_p_wildland" => Tr("UnitWildland", "Camion feux de forêt"),
            "veh_p_fireboat" => Tr("UnitFireboat", "Bateau-pompe"),
            "veh_p_helico" => Tr("UnitHelico", "Hélicoptère"),
            "veh_p_helicoassault" => Tr("UnitHelicoAssault", "Hélicoptère d'assaut"),
            "veh_p_canadair" => Tr("UnitCanadair", "Bombardier d'eau"),
            "veh_p_bulldozer" => Tr("UnitBulldozer", "Bulldozer"),
            "vex_p_toxic_buldoze" => Tr("UnitToxicDozer", "Bulldozer blindé (toxique)"),
            "veh_p_toxic" => Tr("UnitToxic", "Véhicule risques chimiques"),
            "veh_p_pyro" => Tr("UnitPyroVeh", "Véhicule artificier"),
            "veh_p_airpumper" => Tr("UnitAirPumper", "Fourgon aéroport"),
            _ => cls[(cls.LastIndexOf('\\') + 1)..],
        };
    }

    /// <summary>Unités proposées : classes de pompiers et de véhicules sans nationalité, hors classes de base et factices.</summary>
    private List<string> EdStationChoices()
    {
        if (_edStChoices != null)
            return _edStChoices;
        List<string> res = new();
        foreach ((string folder, string prefix) in new[] { ("Firevehicle", "Firevehicle\\"), ("Fireman", "Fireman\\") })
        {
            List<string> names = _files.List("Design\\Unit\\" + folder + "\\")
                .Where(p => p.EndsWith(".class", StringComparison.OrdinalIgnoreCase))
                .Select(p => Path.GetFileNameWithoutExtension(p.Replace('\\', '/')))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(n => !n.StartsWith("base_", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("openfd_", StringComparison.OrdinalIgnoreCase)
                            && !n.StartsWith("FD1_", StringComparison.OrdinalIgnoreCase) && !n.Contains("factice", StringComparison.OrdinalIgnoreCase)
                            && !NationSuffixes.Any(suf => n.EndsWith(suf, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(n => EdUnitLabel(prefix + n), StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            res.AddRange(names.Select(n => prefix + n));
        }
        return _edStChoices = res;
    }

    /// <summary>Liste des unités à ajouter (images avec leur nom), par-dessus la fenêtre.</summary>
    private void EdDrawStationPicker(List<EdStation> list)
    {
        (float wx, float wy, float ww, float wh) = EdStationsRect();
        float s = _scale, pad = 14 * s, bh = 34 * s;
        float x = wx + 30 * s, y = wy + 40 * s, w = ww - 60 * s, h = wh - 80 * s;
        int row = _edStPick;
        List<string> choices = EdStationChoices();
        _ui.Begin();
        _ui.Rect(x + 4 * s, y + 4 * s, w, h, new SN.Vector4(0, 0, 0, 0.5f));
        _ui.Rect(x, y, w, h, EdPanel);
        _ui.Frame(x, y, w, h, EdAccent, 2 * s);
        string title = Tr("StationPickTitle", "Ajouter une unité : {0}", row == 0 ? Tr("StationStart", "Départ") : Tr("StationReinforcement", "Renfort {0}", row));
        _ui.Text(x + pad, y + 12 * s, title, EdAccent);
        float top = y + 16 * s + _ui.LineHeight + 8 * s, bottom = y + h - bh - 2 * pad, area = bottom - top, sb = EdScrollW;
        float cell = 128 * s, iconH = 64 * s, tileH = iconH + _ui.LineHeight + 12 * s;
        int perLine = Math.Max(1, (int)((w - 2 * pad - sb) / cell));
        // deux groupes : véhicules, pompiers
        List<(string head, List<string> items)> groups = new()
        {
            (Tr("StationVehicles", "Véhicules"), choices.Where(c => c.StartsWith("Firevehicle", StringComparison.OrdinalIgnoreCase)).ToList()),
            (Tr("StationFiremen", "Unités à pied"), choices.Where(c => !c.StartsWith("Firevehicle", StringComparison.OrdinalIgnoreCase)).ToList()),
        };
        float used = groups.Sum(g => _ui.LineHeight + 10 * s + (g.items.Count + perLine - 1) / perLine * tileH + 8 * s);
        if (EdHit(x, top, w, area) && _edWheel != 0)
        {
            _edStPickBar.Value -= _edWheel / 120f * tileH;
            _edWheel = 0;
        }
        _edStPickBar.Run(_ui, x + w - sb - pad / 2, top, sb, area, used, area, tileH, _edMx, _edMy, ref _edPressL, _edDownL, _edClock);
        _edStPickBar.Clamp(used, area);
        _ui.End();
        string? pick = null;
        string? hover = null;
        _ui.BeginClipped(new Rectangle((int)x, (int)top, (int)(w - sb - pad), (int)area));
        float cy = top - _edStPickBar.Value;
        bool inArea = _edMy >= top && _edMy < bottom;
        foreach ((string head, List<string> items) in groups)
        {
            _ui.Text(x + pad, cy + 4 * s, head, EdAccent);
            cy += _ui.LineHeight + 10 * s;
            for (int i = 0; i < items.Count; i++)
            {
                float tx = x + pad + (i % perLine) * cell, ty = cy + (i / perLine) * tileH, tw = cell - 8 * s;
                if (ty + tileH < top || ty > bottom)
                    continue;
                bool over = inArea && EdHit(tx, ty, tw, tileH - 6 * s);
                _ui.Rect(tx, ty, tw, tileH - 6 * s, over ? EdHover : new SN.Vector4(0.13f, 0.13f, 0.16f, 1));
                EdUnitIcon(items[i], tx + 4 * s, ty + 4 * s, tw - 8 * s, iconH);
                string label = EdUnitLabel(items[i]);
                _ui.Text(tx + 4 * s, ty + iconH + 6 * s, EdFit(label, tw - 8 * s), over ? EdText : EdMuted);
                if (over)
                {
                    hover = label + "  (" + items[i] + ")";
                    if (_edPressL)
                    {
                        _edPressL = false;
                        pick = items[i];
                    }
                }
            }
            cy += (items.Count + perLine - 1) / perLine * tileH + 8 * s;
        }
        _ui.End();
        _ui.Begin();
        float by = y + h - bh - pad;
        _ui.Text(x + pad, by + (bh - _ui.LineHeight) / 2, EdFit(hover ?? Tr("StationPickNote", "Cliquez sur une unité pour l'ajouter. Le jeu prend la version de la nationalité de la mission."), w - 2 * pad - 170 * s), hover != null ? EdText : EdMuted);
        bool cancel = EdButton(x + w - pad - 150 * s, by, 150 * s, bh, Tr("Cancel", "Annuler"));
        // clic hors de la liste : fermée
        if (_edPressL && !EdHit(x, y, w, h))
        {
            _edPressL = false;
            cancel = true;
        }
        _ui.End();
        if (pick != null)
        {
            string add = pick;
            EdStationsChange(l =>
            {
                if (row < l.Count && l[row].Units.Count < MaxStationUnits)
                {
                    l[row].Units.Add(add);
                    l[row].Changed = true;
                }
            });
            _edStPick = -1;
        }
        else if (cancel)
            _edStPick = -1;
    }
}
