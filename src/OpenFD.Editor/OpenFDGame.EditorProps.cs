using System.Globalization;
using Microsoft.Xna.Framework;
using OpenFD.Fire;
using OpenFD.Formats;
using SN = System.Numerics;

namespace OpenFD.MG;

// éditeur : la fenêtre « Propriétés : » en bas à droite (l'objet choisi, ou la mission quand rien n'est choisi)
public sealed partial class OpenFDGame
{
    private readonly ScrollBar _edPropBar = new();
    private float _edPropY, _edPropX, _edPropW, _edPropLabelW, _edPropTop, _edPropBottom;
    private string _edName = "";          // nom court de la mission (fichiers)
    private string _edMisText = "";       // texte du fichier .mis
    private byte[]? _edGrid;              // fichier .grid d'origine (sa fin est gardée à la sauvegarde)
    private bool _edPacked;               // sauvegarde en archives .pak / .bank

    private static readonly string[] Nationalities = { "us", "fr", "gb", "gr", "ua" };
    private static string NationalityName(int i) => i switch
    {
        0 => Tr("NatUS", "États-Unis"),
        1 => Tr("NatFR", "France"),
        2 => Tr("NatGB", "Royaume-Uni"),
        3 => Tr("NatGR", "Allemagne"),
        _ => Tr("NatUA", "Ukraine"),
    };

    private float EdPropRow => _ui.LineHeight + 10 * _scale;

    private void EdDrawProperties()
    {
        (float x, float y, float w, float h) = EdPropsRect();
        float s = _scale, pad = 8 * s, sb = EdScrollW;
        string title = Tr("Properties", "Propriétés :") + " ";
        _ui.Begin();
        _ui.Rect(x, y, w, h, EdPanel);
        _ui.Frame(x + 1, y + 1, w - 2, h - 2, EdLine, MathF.Max(1, s));
        _ui.Text(x + pad, y + pad, title, EdAccent);
        string what = _edSel != null ? $"{EdKindName(_edSel.Kind)} - {_edSel.Name}"
            : _edCell is { } cell ? Tr("CellTitle", "Case ({0}, {1})", cell.x, cell.y)
            : _edLoaded ? Tr("MissionName", "Mission {0}", _edMission) : "";
        _ui.Text(x + pad + _ui.Measure(title), y + pad, EdFit(what, w - 3 * pad - _ui.Measure(title)), EdText);
        float top = y + pad + _ui.LineHeight + 6 * s, area = h - (top - y) - pad;
        if (EdHit(x, top, w, area) && _edWheel != 0 && _edPopup == null)
        {
            _edPropBar.Value -= _edWheel / 120f * EdPropRow * 2;
            _edWheel = 0;
        }
        _edPropBar.Run(_ui, x + w - sb - pad / 2, top, sb, area, _edPropUsed, area, EdPropRow, _edMx, _edMy, ref _edPressL, _edDownL, _edClock);
        _ui.End();
        _edPropX = x + pad;
        _edPropW = w - 2 * pad - sb - 4 * s;
        _edPropLabelW = _edPropW * 0.42f;
        _edPropTop = top;
        _edPropBottom = top + area;
        _edPropY = top - _edPropBar.Value;
        _ui.BeginClipped(new Rectangle((int)x, (int)top, (int)(w - sb - pad), (int)area));
        if (!_edLoaded)
            EdPropNote(Tr("NoMissionOpen", "Aucune mission ouverte."));
        else if (_edSel is { } e)
            EdObjectProps(e);
        else if (_edCell is { } c)
            EdCellProps(c.x, c.y);
        else
            EdMissionProps();
        _ui.End();
        // hauteur du contenu : pour la barre de défilement de l'image suivante
        _edPropUsed = _edPropY + _edPropBar.Value - top + 4 * s;
        _edPropBar.Clamp(_edPropUsed, area);
    }

    private float _edPropUsed;

    private static string EdKindName(EditorItemKind k) => k switch
    {
        EditorItemKind.Object => Tr("KindObject", "Objet"),
        EditorItemKind.Dynamic => Tr("KindDynamic", "Objet dynamique"),
        EditorItemKind.Civilian => Tr("KindCivilian", "Civil"),
        EditorItemKind.Fireman => Tr("KindFireman", "Pompier"),
        EditorItemKind.Vehicle => Tr("KindVehicle", "Véhicule"),
        EditorItemKind.Fire => Tr("KindFire", "Feu"),
        EditorItemKind.Zone => Tr("KindZone", "Zone"),
        EditorItemKind.RoofMask => Tr("KindMask", "Masque de toit"),
        EditorItemKind.Camera => Tr("KindCamera", "Caméra de départ"),
        EditorItemKind.Tree => Tr("KindTree", "Arbre"),
        _ => Tr("KindWaypoint", "Point de passage"),
    };

    // ---- lignes de la fenêtre : une étiquette à gauche, la valeur à droite ----
    private bool EdRowVisible => _edPropY + EdPropRow > _edPropTop && _edPropY < _edPropBottom;
    private bool EdRowHit(float x, float w) => EdRowVisible && _edMy >= _edPropTop && _edMy < _edPropBottom && EdHit(x, _edPropY + 2 * _scale, w, EdPropRow - 4 * _scale);

    private void EdPropLabel(string label)
    {
        if (EdRowVisible)
            _ui.Text(_edPropX, _edPropY + (EdPropRow - _ui.LineHeight) / 2, EdFit(label, _edPropLabelW - 6 * _scale), EdMuted);
    }

    private void EdPropNote(string text, bool warn = false)
    {
        foreach (string line in Wrap(text, _edPropW, _scale))
        {
            if (EdRowVisible)
                _ui.Text(_edPropX, _edPropY + 4 * _scale, line, warn ? EdError : EdMuted);
            _edPropY += _ui.LineHeight + 4 * _scale;
        }
    }

    private void EdPropText(string label, string value)
    {
        EdPropLabel(label);
        if (EdRowVisible)
            _ui.Text(_edPropX + _edPropLabelW, _edPropY + (EdPropRow - _ui.LineHeight) / 2, EdFit(value, _edPropW - _edPropLabelW), EdText);
        _edPropY += EdPropRow;
    }

    /// <summary>Champ de texte de la ligne (largeur : le reste de la ligne, moins <paramref name="reserve"/> à droite).</summary>
    private void EdPropField(string id, string label, string value, Action<string> commit, float reserve = 0)
    {
        EdPropLabel(label);
        float fx = _edPropX + _edPropLabelW, fw = _edPropW - _edPropLabelW - reserve;
        if (EdRowVisible || _edFocusId == id)
        {
            bool click = _edPressL && !EdRowHit(fx, fw);
            bool keep = _edPressL;
            if (click)
                _edPressL = false;   // clic ailleurs : pas pour ce champ
            EdField(id, fx, _edPropY + 3 * _scale, fw, EdPropRow - 6 * _scale, value, commit);
            if (click)
                _edPressL = keep;
        }
        _edPropY += EdPropRow;
    }

    private void EdPropNumber(string id, string label, float value, Action<float> commit, string format = "0.##")
    {
        EdPropField(id, label, value.ToString(format, CultureInfo.InvariantCulture), t =>
        {
            if (float.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v))
                commit(v);
        });
    }

    /// <summary>Trois nombres sur une ligne (X, Y, Z).</summary>
    private void EdPropVector(string id, string label, SN.Vector3 v, Action<SN.Vector3> commit, string format = "0", int count = 3)
    {
        EdPropLabel(label);
        float fx = _edPropX + _edPropLabelW, fw = (_edPropW - _edPropLabelW - (count - 1) * 4 * _scale) / count;
        for (int i = 0; i < count; i++)
        {
            int k = i;
            string fid = id + i;
            float val = i == 0 ? v.X : i == 1 ? v.Y : v.Z;
            if (MathF.Abs(val) < 0.0005f)
                val = 0;   // pas de « -0 »
            float cx = fx + i * (fw + 4 * _scale);
            if (EdRowVisible || _edFocusId == fid)
            {
                bool click = _edPressL && !EdRowHit(cx, fw);
                bool keep = _edPressL;
                if (click)
                    _edPressL = false;
                EdField(fid, cx, _edPropY + 3 * _scale, fw, EdPropRow - 6 * _scale, val.ToString(format, CultureInfo.InvariantCulture), t =>
                {
                    if (!float.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float n) || !float.IsFinite(n))
                        return;
                    SN.Vector3 r = v;
                    if (k == 0)
                        r.X = n;
                    else if (k == 1)
                        r.Y = n;
                    else
                        r.Z = n;
                    commit(r);
                });
                if (click)
                    _edPressL = keep;
            }
        }
        _edPropY += EdPropRow;
    }

    private void EdPropCheck(string label, bool value, Action<bool> set)
    {
        bool hit = EdRowHit(_edPropX, _edPropW);
        bool keep = _edPressL;
        if (!hit)
            _edPressL = false;
        if (EdRowVisible && EdCheck(_edPropX, _edPropY + 2 * _scale, EdPropRow - 4 * _scale, label, value))
            set(!value);
        else if (!hit)
            _edPressL = keep;
        _edPropY += EdPropRow;
    }

    private void EdPropChoice(string label, string current, Func<(List<string> items, int current)> options, Action<int> pick)
    {
        EdPropLabel(label);
        float fx = _edPropX + _edPropLabelW, fw = _edPropW - _edPropLabelW;
        if (EdRowVisible)
        {
            bool hit = EdRowHit(fx, fw);
            float y = _edPropY + 3 * _scale, h = EdPropRow - 6 * _scale;
            _ui.Rect(fx, y, fw, h, hit ? EdHover : new SN.Vector4(0.09f, 0.09f, 0.11f, 1));
            _ui.Frame(fx, y, fw, h, EdLine, MathF.Max(1, _scale));
            _ui.Text(fx + 5 * _scale, y + (h - _ui.LineHeight) / 2, EdFit(current, fw - 24 * _scale), EdText);
            _ui.Text(fx + fw - 14 * _scale, y + (h - _ui.LineHeight) / 2, "v", EdMuted);
            if (hit && _edPressL)
            {
                _edPressL = false;
                EdCommitFocus();
                (List<string> items, int cur) = options();
                EdOpenPopup(label, items, pick, fx, y + h, MathF.Max(fw, 260 * _scale), cur, _fbH * 0.6f);
            }
        }
        _edPropY += EdPropRow;
    }

    private void EdPropButton(string label, Action act)
    {
        if (EdRowVisible)
        {
            bool hit = EdRowHit(_edPropX, _edPropW);
            bool keep = _edPressL;
            if (!hit)
                _edPressL = false;
            if (EdButton(_edPropX, _edPropY + 3 * _scale, _edPropW, EdPropRow - 6 * _scale, label))
                act();
            else if (!hit)
                _edPressL = keep;
        }
        _edPropY += EdPropRow;
    }

    /// <summary>Petit bouton « ... » au bout de la ligne qui vient d'être écrite (aligné à droite).</summary>
    private bool EdPropDots(float rowY)
    {
        float bw = 30 * _scale, bx = _edPropX + _edPropW - bw, by = rowY + 3 * _scale, bh = EdPropRow - 6 * _scale;
        if (rowY + EdPropRow <= _edPropTop || rowY >= _edPropBottom)
            return false;
        bool hit = _edMy >= _edPropTop && _edMy < _edPropBottom && EdHit(bx, by, bw, bh);
        bool keep = _edPressL;
        if (!hit)
            _edPressL = false;
        bool r = EdButton(bx, by, bw, bh, "...");
        if (!hit)
            _edPressL = keep;
        return r;
    }

    /// <summary>Fonction d'un script : son nom (ou un champ pour le changer) et « ... » qui ouvre le script à cette fonction.</summary>
    private void EdPropScript(string id, string label, string function, string signature, Action<string>? change)
    {
        float rowY = _edPropY;
        float reserve = 34 * _scale;
        if (change != null)
            EdPropField(id, label, function, change, reserve);
        else
        {
            EdPropLabel(label);
            if (EdRowVisible)
                _ui.Text(_edPropX + _edPropLabelW, _edPropY + (EdPropRow - _ui.LineHeight) / 2, EdFit(function.Length > 0 ? function : "-", _edPropW - _edPropLabelW - reserve), EdText);
            _edPropY += EdPropRow;
        }
        if (EdPropDots(rowY))
        {
            if (function.Length > 0)
                EdGotoScript(function, signature);
            else
                EdMessage(Tr("NoFunction", "Écrivez d'abord le nom de la fonction"), true);
        }
    }

    private static float Deg(float r) => r * 180 / MathF.PI;
    private static float Rad(float d) => d * MathF.PI / 180;

    // ---- l'objet choisi ----
    private void EdObjectProps(EditorObject e)
    {
        string id = "o" + e.Source.Id + ".";
        EdPropField(id + "nom", e.Kind is EditorItemKind.Fireman or EditorItemKind.Vehicle ? Tr("NameStation", "Nom (caserne)") : Tr("Name", "Nom"), e.Name, v =>
        {
            v = v.Trim();
            if (v.Length == 0 || v == e.Name)
                return;
            if (_edLevel.Objects.Any(o => o.Id != e.Source.Id && o.Name.Equals(v, StringComparison.OrdinalIgnoreCase)))
            {
                EdMessage(Tr("NameTaken", "Le nom « {0} » est déjà pris", v), true);
                return;
            }
            EdUpdate(e, LevelEdit.WithName(e.Source, v));
        });
        EdPropText(Tr("Class", "Classe"), e.Source.ClassPath);
        EdPropVector(id + "pos", Tr("Position", "Position (cm)"), e.Position, v =>
        {
            e.Position = v;
            EdPlace(e);
        });
        SN.Vector3 eu = EdWrap(e.Euler);
        EdPropVector(id + "rot", Tr("Rotation", "Rotation (°)"), new SN.Vector3(Deg(eu.X), Deg(eu.Y), Deg(eu.Z)), v =>
        {
            e.Euler = EdWrap(new SN.Vector3(Rad(v.X), Rad(v.Y), Rad(v.Z)));
            EdPlace(e);
        });
        if (e.Kind is EditorItemKind.Object or EditorItemKind.Dynamic or EditorItemKind.Civilian or EditorItemKind.Fireman or EditorItemKind.Vehicle or EditorItemKind.Tree)
            EdPropCheck(e.Kind is EditorItemKind.Fireman or EditorItemKind.Vehicle ? Tr("StartPresent", "Présent au début de la partie") : Tr("StartVisible", "Visible au départ"), e.Source.StartVisible,
                v => EdUpdate(e, LevelEdit.WithStartVisible(e.Source, v)));
        if (LevelEdit.Life(e.Source) is { } life && e.Kind != EditorItemKind.Fire)
        {
            EdPropNumber(id + "vie", Tr("Life", "Vie"), life.life, v => EdUpdate(e, LevelEdit.WithLife(e.Source, Math.Clamp(v, 0, MathF.Max(life.max, v)))), "0");
            EdPropText(Tr("LifeMax", "Vie maximale"), life.max.ToString("0", CultureInfo.InvariantCulture));
        }
        switch (e.Kind)
        {
            case EditorItemKind.Fire:
                EdFireProps(e, id);
                break;
            case EditorItemKind.Fireman:
            case EditorItemKind.Vehicle:
                EdUnitProps(e);
                break;
            case EditorItemKind.Civilian:
            {
                byte st = LevelEdit.ComponentByte(e.Source, 0xA02A, 39) ?? 1;
                string[] names = { Tr("CivConscious", "Conscient (choqué)"), Tr("CivFainted", "Évanoui"), Tr("CivDead", "Mort") };
                byte[] codes = { 1, 3, 4 };
                int cur = Array.IndexOf(codes, st);
                EdPropChoice(Tr("CivState", "État au départ"), cur >= 0 ? names[cur] : $"({st})", () => (names.ToList(), cur), i => EdUpdate(e, LevelEdit.WithComponentByte(e.Source, 0xA02A, 39, codes[i])));
                break;
            }
            case EditorItemKind.Zone:
                EdZoneProps(e, id);
                break;
            case EditorItemKind.RoofMask:
                EdPropVector(id + "taille", Tr("MaskSize", "Taille (cm)"), new SN.Vector3(e.Size.X, e.Size.Y, 0),
                    v => EdResize(e, new SN.Vector2(Math.Clamp(MathF.Round(v.X / 10) * 10, 50, 20000), Math.Clamp(MathF.Round(v.Y / 10) * 10, 50, 20000))), "0", 2);
                EdPropNote(Tr("MaskNote", "Masque de toit : plaque noire (hauteur fixe) posée au-dessus d'une pièce. Le jeu la cache quand un pompier peut atteindre la pièce dessous ; un script peut la cacher ou la remontrer (ENT_SetVisibility)."));
                break;
            case EditorItemKind.Camera:
                EdPropButton(Tr("CamTakeView", "Prendre la vue actuelle"), () =>
                {
                    EdCameraFromView(e);
                    EdMessage(Tr("CamTaken", "Caméra de départ : vue actuelle"));
                });
                EdPropButton(Tr("CamLook", "Voir depuis la caméra"), () => EdViewFromCamera(e));
                break;
            case EditorItemKind.Tree:
                EdPropText(Tr("TreeModel", "Modèle"), e.TreeModel ?? "");
                EdPropText(Tr("TreeHeight", "Hauteur"), e.TreeHeight.ToString("0", CultureInfo.InvariantCulture) + " cm");
                break;
        }
        // fonctions de script appelées par l'objet (actions, destruction) ; « ... » ouvre le script à la fonction
        if (e.Kind != EditorItemKind.Zone)
            foreach ((string label, string function, string signature) in EdScriptRefs(e))
                EdPropScript(id + "scr" + label, label, function, signature, null);
        _edPropY += 4 * _scale;
        EdPropButton(Tr("Remove", "Enlever l'objet (Suppr)"), () => EdDelete(e));
    }

    private void EdFireProps(EditorObject e, string id)
    {
        EdPropCheck(Tr("FireLit", "Allumé au départ"), e.Lit, v =>
        {
            e.Lit = v;
            if (v && e.Intensity <= 0)
                e.Intensity = MathF.Max(10, (e.FireClass?.IntensityMax ?? 100) * 0.5f);
            EdUpdate(e, LevelEdit.WithFire(e.Source, e.Lit, e.Intensity));
            EdPlace(e);
        });
        float max = e.FireClass?.IntensityMax ?? 100;
        EdPropNumber(id + "int", Tr("FireIntensity", "Intensité (vie du feu)"), e.Intensity, v =>
        {
            e.Intensity = Math.Clamp(v, 0, max);
            e.Lit = e.Intensity > 0;
            EdUpdate(e, LevelEdit.WithFire(e.Source, e.Lit, e.Intensity));
            EdPlace(e);
        }, "0.#");
        EdPropText(Tr("FireMax", "Intensité maximale"), max.ToString("0.#", CultureInfo.InvariantCulture));
        List<(string ClassPath, string Name, char Type)> classes = _edCatalog?.FireClasses ?? new();
        int cur = classes.FindIndex(c => c.ClassPath.Equals(e.Source.ClassPath, StringComparison.OrdinalIgnoreCase));
        EdPropChoice(Tr("FireType", "Type de feu"), $"{e.Source.ClassName} ({e.FireClass?.FireType ?? '?'})",
            () => (classes.Select(c => Tr("FireTypeItem", "{0}  -  type {1} ({2})", c.Name, c.Type, FireTypeName(c.Type))).ToList(), cur),
            i => EdSetFireClass(e, classes[i].ClassPath));
        if (e.FireClass is { } fc)
        {
            EdPropText(Tr("FireKind", "Type"), $"{fc.FireType} : {FireTypeName(fc.FireType)}");
            EdPropText(Tr("FireDamage", "Portée des dégâts"), fc.LifeDamageRange.ToString("0", CultureInfo.InvariantCulture));
            EdPropText(Tr("FireLighting", "Portée d'allumage"), fc.LightingRange.ToString("0", CultureInfo.InvariantCulture));
            EdPropText(Tr("FireGrowth", "Croissance"), fc.GrowthRate.ToString("0.##", CultureInfo.InvariantCulture));
        }
        if (FireWorld.BackdraftTags(e.Source) is { make: true } bd)
            EdPropText(Tr("FireBackdraft", "Retour de flamme"), Tr("FireBackdraftYes", "oui (seuil {0:0})", bd.threshold));
        if (_session.RoomOf.TryGetValue(e.Source.Id, out string? room) && room.Length > 0)
            EdPropText(Tr("FireRoom", "Pièce"), room);
    }

    private static string FireTypeName(char t) => t switch
    {
        'A' => Tr("FireA", "ordinaire"),
        'B' => Tr("FireB", "liquides"),
        'C' => Tr("FireC", "électrique"),
        'D' => Tr("FireD", "métaux"),
        _ => "?",
    };

    /// <summary>Nouvelle classe de foyer : le type de feu change, et les paramètres de la classe sont recopiés dans le foyer.</summary>
    private void EdSetFireClass(EditorObject e, string classPath)
    {
        HotPointClass? hc = EdFireClass(classPath);
        LevelObject o = LevelEdit.WithClass(e.Source, classPath);
        if (hc != null)
            o = LevelEdit.WithFireParams(o, hc.IntensityMax, hc.IntensityMin, hc.LifeDamageRange, hc.HeatRangeMax, hc.LightingRange, hc.LightingThreshold,
                hc.Tempo, hc.GrowthRate, hc.DeathRate, hc.GrowthTime, hc.SlopeFactor);
        e.FireClass = hc;
        if (hc != null && e.Intensity > hc.IntensityMax)
            e.Intensity = hc.IntensityMax;
        o = LevelEdit.WithFire(o, e.Lit, e.Intensity);
        EdUpdate(e, o);
        EdPlace(e);
        EdMessage(Tr("FireClassSet", "{0} : feu {1}", e.Name, classPath[(classPath.LastIndexOf('\\') + 1)..]));
    }

    private void EdUnitProps(EditorObject e)
    {
        ClassFile? c = EdClass(e.Source.ClassPath);
        if (c != null)
        {
            string agent = (c.Get("CAPACITIES_RESERVE", "FireAgent") ?? "").Trim('"', ' ');
            EdPropText(Tr("Agent", "Agent"), agent.Length > 0 ? agent.ToUpperInvariant() switch
            {
                "WATER" => Tr("AgentWater", "eau"),
                "FOAM" => Tr("AgentFoam", "mousse"),
                "POWDER" => Tr("AgentPowder", "poudre"),
                "CO2" => "CO2",
                _ => agent,
            } : Tr("AgentNone", "aucun"));
            float reserve = c.GetFloat("CAPACITIES_RESERVE", "ReserveMax", 0);
            if (reserve > 0)
                EdPropText(Tr("Reserve", "Réserve"), reserve.ToString("0", CultureInfo.InvariantCulture));
            EdPropText(Tr("ClassLife", "Vie (classe)"), c.GetFloat("DAMAGE", "MaxLifePoints", 500).ToString("0", CultureInfo.InvariantCulture));
            if (e.Kind == EditorItemKind.Vehicle)
                EdPropText(Tr("Seats", "Places à bord"), c.GetInt("CAPACITIES_PROPS", "EmbarkCapacity", 0).ToString(CultureInfo.InvariantCulture));
            else
                EdPropText(Tr("Stamina", "Endurance"), c.GetFloat("CAPACITIES_PROPS", "StaminaMax", 100).ToString("0", CultureInfo.InvariantCulture));
        }
        // nationalité : la classe sœur « …_us », « …_fr »… si elle existe
        string cls = EdBaseClass(e.Source.ClassPath);
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(cls, "_(us|fr|gb|gr|ua)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        string root = m.Success ? cls[..m.Index] : cls;
        int cur = m.Success ? Array.IndexOf(Nationalities, m.Groups[1].Value.ToLowerInvariant()) : -1;
        List<int> have = new List<int>();
        for (int i = 0; i < Nationalities.Length; i++)
            if (EdClass(root + "_" + Nationalities[i]) != null)
                have.Add(i);
        if (have.Count > 0)
            EdPropChoice(Tr("Nationality", "Nationalité"), cur >= 0 ? NationalityName(cur) : Tr("NatMission", "(celle de la mission)"), () => (have.Select(NationalityName).ToList(), have.IndexOf(cur)), k =>
            {
                string next = root + "_" + Nationalities[have[k]];
                EdUpdate(e, LevelEdit.WithClass(e.Source, next));
                e.Model = EdModelOf(next, e.Kind == EditorItemKind.Fireman ? "Unit\\Fireman" : "Unit\\FireVehicle") ?? e.Model;
                e.HiddenParts = e.Kind == EditorItemKind.Fireman ? EdHiddenTools(next, e.Model) : null;
                EdPlace(e);
            });
        else
            EdPropText(Tr("Nationality", "Nationalité"), Tr("NatSingle", "(une seule version)"));
        if (e.Kind != EditorItemKind.Fireman)
            return;
        // outils : une case par outil ; ceux qu'il n'a pas sont cachés dans la vue
        _edPropY += 4 * _scale;
        EdPropLabel(Tr("Tools", "Outils"));
        _edPropY += EdPropRow;
        bool[] tools = EdToolsOf(e.Source.ClassPath);
        for (int t = 0; t < Tools.Length; t++)
        {
            string key = Tools[t].key;
            EdPropCheck(Tr(Tools[t].text, Tools[t].french), tools[t], _ => EdToggleEquipment(e, key));
        }
        EdPropNote(Tr("FiremanNote", "Pompier posé sur la carte : il attend les ordres dès le début (OpenFD)."));
    }

    private void EdZoneProps(EditorObject e, string id)
    {
        if (LevelEdit.Trigger(e.Source) is not { } t)
        {
            EdPropNote(Tr("ZoneUnknown", "Zone : format inconnu, pas modifiable ici."), true);
            return;
        }
        const string Sig = "EntityID Unit, EntityID Trigger";
        EdPropScript(id + "in", Tr("ZoneEnter", "Script à l'entrée"), t.onEnter, Sig, v => EdUpdate(e, LevelEdit.WithTrigger(e.Source, v.Trim(), t.onLeave, t.enabled, t.size)));
        EdPropScript(id + "out", Tr("ZoneLeave", "Script à la sortie"), t.onLeave, Sig, v => EdUpdate(e, LevelEdit.WithTrigger(e.Source, t.onEnter, v.Trim(), t.enabled, t.size)));
        EdPropCheck(Tr("ZoneActive", "Active au départ"), t.enabled, v => EdUpdate(e, LevelEdit.WithTrigger(e.Source, t.onEnter, t.onLeave, v, t.size)));
        EdPropVector(id + "taille", Tr("ZoneSize", "Taille (cm)"), t.size, v =>
        {
            SN.Vector3 size = new SN.Vector3(MathF.Max(50, v.X), MathF.Max(50, v.Y), MathF.Max(50, v.Z));
            EdUpdate(e, LevelEdit.WithTrigger(e.Source, t.onEnter, t.onLeave, t.enabled, size));
            EdPlace(e);
        });
        EdPropNote(Tr("ZoneNote", "Les scripts sont appelés quand un pompier ou un civil entre dans la zone ou en sort."));
    }

    /// <summary>Masque : la taille fait partie du nom de sa classe (« openfd_mask_400x400 »).</summary>
    private void EdResize(EditorObject e, SN.Vector2 size)
    {
        e.Size = size;
        string cls = EditorCatalog.MaskClass + $"_{(int)size.X}x{(int)size.Y}";
        EdUpdate(e, LevelEdit.WithClass(e.Source, cls));
        EdPlace(e);
    }

    // ---- la mission (rien n'est choisi) ----
    private void EdMissionProps()
    {
        EdPropField("m.court", Tr("ShortName", "Nom court (fichiers)"), _edName, v =>
        {
            string n = new string(v.Trim().Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-').ToArray());
            if (n.Length > 0)
                _edName = n;
        });
        if (!_edName.Equals(_edMission, StringComparison.OrdinalIgnoreCase) && _files.List("Design\\Mission\\" + _edName + "\\").Any())
            EdPropNote(Tr("NameExists", "Une mission « {0} » existe déjà : la sauvegarde la remplacera (les fichiers remplacés sont gardés en .bak).", _edName), true);
        else if (_edName.Equals(_edMission, StringComparison.OrdinalIgnoreCase))
            EdPropNote(Tr("SameName", "Même nom que la mission ouverte : ses fichiers seront remplacés (copies gardées en .bak)."));
        EdMisField("m.nom", Tr("MisName", "Nom"), "MISSION", "Name");
        EdMisField("m.titre", Tr("MisTitle", "Titre (briefing)"), "BRIEFING", "StoryTitleKey");
        EdMisField("m.fond", Tr("MisBackground", "Image de fond"), "DISPLAY", "Background");
        EdMisField("m.mini", Tr("MisMinimap", "Mini-carte"), "MISSION", "Minimap");
        EdMisField("m.musique", Tr("MisMusic", "Musique"), "MISSION", "AmbienceMusic");
        float az = Deg(MathF.Atan2(_sunDir.Y, _sunDir.X)), el = Deg(MathF.Asin(Math.Clamp(-_sunDir.Z, -1, 1)));
        EdPropNumber("m.soleilA", Tr("SunDir", "Soleil : direction (°)"), az, v => EdSetSun(Rad(v), Rad(el)), "0");
        EdPropNumber("m.soleilH", Tr("SunHeight", "Soleil : hauteur (°)"), el, v => EdSetSun(Rad(az), Rad(Math.Clamp(v, 5, 90))), "0");
        // équipe de départ : une des casernes du jeu (Design\Firestation\*.cfg)
        string team = IniText.Get(_edMisText, "FIRESTATION", "InitialDispatch") ?? "";
        EdPropChoice(Tr("MisTeam", "Équipe de départ"), team.Length > 0 ? team : "-", () =>
        {
            List<string> teams = EdTeams();
            return (teams, teams.FindIndex(x => x.Equals(team, StringComparison.OrdinalIgnoreCase)));
        }, i => _edMisText = IniText.Set(_edMisText, "FIRESTATION", "InitialDispatch", EdTeams()[i]));
        EdPropScript("m.script1", Tr("MisScript1", "Script principal"), IniText.Get(_edMisText, "SCRIPT", "MissionScript1") ?? "", "", v => _edMisText = IniText.Set(_edMisText, "SCRIPT", "MissionScript1", v.Trim()));
        EdPropScript("m.script2", Tr("MisScript2", "Second script"), IniText.Get(_edMisText, "SCRIPT", "MissionScript2") ?? "", "", v => _edMisText = IniText.Set(_edMisText, "SCRIPT", "MissionScript2", v.Trim()));
        EdPropScript("m.auto", Tr("MisAutoExec", "Script de départ"), IniText.Get(_edMisText, "SCRIPT", "AutoExecScript") ?? "", "", v => _edMisText = IniText.Set(_edMisText, "SCRIPT", "AutoExecScript", v.Trim()));
        if (_session.Grid is { } g)
            EdPropText(Tr("Grid", "Grille"), Tr("GridSize", "{0} x {1} cases ({0} x {1} m)", g.Width, g.Height));
        EdPropText(Tr("Objects", "Objets"), Tr("ObjectsCount", "{0} ({1} feux au départ)", _edObjects.Count, _edObjects.Count(o => o.Kind == EditorItemKind.Fire && o.Lit)));
        _edPropY += 4 * _scale;
        EdPropLabel(Tr("SaveAs", "Enregistrer"));
        _edPropY += EdPropRow;
        EdPropCheck(Tr("SaveLoose", "Fichiers dans Data"), !_edPacked, v => _edPacked = !v);
        EdPropCheck(Tr("SavePacked", "Archives .pak / .bank"), _edPacked, v => _edPacked = v);
        EdPropNote(Tr("SaveNote", "Le niveau, la grille et le .mis vont toujours dans Data\\Design\\Mission\\<nom>."));
    }

    private List<string> EdTeams() => _files.List("Design\\Firestation\\").Where(p => p.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
        .Select(p => Path.GetFileNameWithoutExtension(p.Replace('\\', '/'))).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    private void EdMisField(string id, string label, string section, string key)
    {
        string value = IniText.Get(_edMisText, section, key) ?? "";
        EdPropField(id, label, value, v => _edMisText = IniText.Set(_edMisText, section, key, v.Trim()));
    }

    /// <summary>
    /// Nouveau soleil : l'objet lumière de la carte est tourné (son axe Z est la direction de la lumière) ; l'éclairage et
    /// les ombres suivent tout de suite.
    /// </summary>
    private void EdSetSun(float azimuth, float elevation)
    {
        SN.Vector3 d = SN.Vector3.Normalize(new SN.Vector3(MathF.Cos(elevation) * MathF.Cos(azimuth), MathF.Cos(elevation) * MathF.Sin(azimuth), -MathF.Sin(elevation)));
        int i = _edLevel.Objects.FindIndex(o => o.Kind == EntityKind.Light && o.HasTransform && EdClass(o.ClassPath)?.Get("LIGHT", "LightType")?.Trim('"', ' ').Equals("SUN", StringComparison.OrdinalIgnoreCase) == true);
        if (i < 0)
        {
            EdMessage(Tr("NoSun", "Pas de soleil (objet lumière) dans cette mission"), true);
            return;
        }
        LevelObject sun = _edLevel.Objects[i];
        // même sens que l'objet d'origine : certaines cartes l'orientent vers le ciel (le jeu inverse alors)
        SN.Vector3 axis = sun.Matrix.M33 > 0 ? -d : d;
        // axe Z de Rz·Rx·Ry = (cos x sin y, −sin x, cos x cos y)
        float x = MathF.Asin(Math.Clamp(-axis.Y, -1, 1)), y = MathF.Atan2(axis.X, axis.Z);
        _edLevel.Objects[i] = LevelEdit.WithTransform(sun, sun.Position, new SN.Vector3(x, y, sun.Rotation.Z));
        _sunDir = d;
        foreach (Microsoft.Xna.Framework.Graphics.BasicEffect fx in new[] { _lit, _plain })
        {
            fx.DirectionalLight0.Direction = _sunDir.X();
            fx.DirectionalLight1.Direction = -_sunDir.X();
        }
    }
}
