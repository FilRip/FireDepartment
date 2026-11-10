using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using OpenFD.Formats;
using SN = System.Numerics;

namespace OpenFD.MG;

// éditeur : liste des objets de la carte (Espace), déplacement aux flèches, relief de la grille (A / Q + clic), sol tiré
// de la grille pour les cartes sans décor
public sealed partial class OpenFDGame
{
    private bool _edLeftOpen;
    private readonly ScrollBar _edLeftBar = new();
    private string _edLeftFilter = "";   // filtre de la liste de gauche

    /// <summary>Caméra sur l'objet, objet choisi (ses propriétés à droite), vu sans obstacle si possible (EdClearView).</summary>
    private void EdFocusOn(EditorObject e)
    {
        EdSelect(e);
        _target = e.Center;
        _dist = Math.Clamp(e.Radius * 5, 900, 20000);
        if (_map)
            _mapCenter = new SN.Vector2(e.Position.X, e.Position.Y);
        else
            EdClearView(e);
    }

    /// <summary>
    /// Liste de gauche : le nom de chaque objet de la carte (filtre en haut) ; clic gauche : l'objet est choisi ;
    /// clic droit : choisi, et la caméra se centre sur lui.
    /// </summary>
    private void EdDrawLeftList()
    {
        if (!_edLeftOpen || !_edLoaded)
            return;
        float s = _scale, x = 0, y = EdMenuH, w = EdLeftW, h = _fbH - y, pad = 6 * s, rowH = _ui.LineHeight + 6 * s, sb = EdScrollW;
        List<EditorObject> list = _edObjects.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _ui.Begin();
        _ui.Rect(x, y, w, h, EdPanel);
        _ui.Frame(x + 1, y + 1, w - 2, h - 2, EdLine, MathF.Max(1, s));
        string filter = EdFilterOf("gauche.filtre", _edLeftFilter);
        list = list.Where(o => EdNameMatches(o.Name, filter)).ToList();
        _ui.Text(x + pad, y + pad, EdFit(Tr("LeftTitle", "Objets de la carte ({0})", list.Count), w - 2 * pad), EdAccent);
        float fh = _ui.LineHeight + 10 * s;
        EdFilterField("gauche.filtre", x + pad, y + pad + rowH, w - 2 * pad, fh, _edLeftFilter, v => _edLeftFilter = v);
        float top = y + pad + rowH + fh + pad, area = h - (top - y) - pad, total = list.Count * rowH;
        if (EdHit(x, top, w, area) && _edWheel != 0)
        {
            _edLeftBar.Value -= _edWheel / 120f * rowH * 3;
            _edWheel = 0;
        }
        _edLeftBar.Run(_ui, x + w - sb - pad / 2, top, sb, area, total, area, rowH, _edMx, _edMy, ref _edPressL, _edDownL, _edClock);
        _ui.End();
        float iw = w - sb - pad * 1.5f;
        _ui.BeginClipped(new Rectangle((int)x, (int)top, (int)iw, (int)area));
        int first = Math.Max(0, (int)(_edLeftBar.Value / rowH)), last = Math.Min(list.Count - 1, first + (int)(area / rowH) + 1);
        for (int i = first; i <= last; i++)
        {
            EditorObject e = list[i];
            float ry = top + i * rowH - _edLeftBar.Value;
            bool hover = EdHit(x, ry, iw, rowH) && _edMy >= top && _edMy < top + area;
            if (e == _edSel)
                _ui.Rect(x + 2 * s, ry, iw - 2 * s, rowH - 2 * s, new SN.Vector4(0.85f, 0.35f, 0.1f, 0.45f));
            else if (hover)
                _ui.Rect(x + 2 * s, ry, iw - 2 * s, rowH - 2 * s, EdHover);
            _ui.Rect(x + pad, ry + rowH / 2 - 3 * s, 6 * s, 6 * s, EdKindColor(e.Kind));
            _ui.Text(x + pad + 12 * s, ry + 3 * s, EdFit(e.Name, iw - pad - 16 * s), e == _edSel ? EdText : EdMuted);
            if (hover && _edPressL)
            {
                _edPressL = false;
                EdCommitFocus();
                EdSelect(e);
            }
            else if (hover && _edPressR)
            {
                _edPressR = false;
                EdCommitFocus();
                EdFocusOn(e);
            }
        }
        _ui.End();
    }

    private static SN.Vector4 EdKindColor(EditorItemKind k) => k switch
    {
        EditorItemKind.Fire => new SN.Vector4(1, 0.5f, 0.12f, 1),
        EditorItemKind.Zone => new SN.Vector4(0.3f, 0.9f, 0.45f, 1),
        EditorItemKind.RoofMask => new SN.Vector4(0.05f, 0.05f, 0.05f, 1),
        EditorItemKind.Camera or EditorItemKind.Waypoint => new SN.Vector4(0.4f, 0.7f, 1, 1),
        EditorItemKind.Vehicle or EditorItemKind.Fireman => new SN.Vector4(0.9f, 0.2f, 0.2f, 1),
        EditorItemKind.Civilian => new SN.Vector4(0.35f, 0.95f, 0.45f, 1),
        EditorItemKind.Tree => new SN.Vector4(0.25f, 0.6f, 0.25f, 1),
        EditorItemKind.Dynamic => new SN.Vector4(0.95f, 0.75f, 0.25f, 1),
        _ => new SN.Vector4(0.7f, 0.7f, 0.75f, 1),
    };

    /// <summary>
    /// Objet choisi : les flèches le déplacent d'un cran (10 cm, Maj : 1 m) dans le sens de la vue (haut : en avant),
    /// Ctrl + Pg.Préc / Pg.Suiv le montent ou le descendent ; il reste à la même hauteur au-dessus du sol.
    /// (Pg.Préc / Pg.Suiv seules : la caméra.)
    /// </summary>
    private void EdArrowMove()
    {
        if (_edSel is not { } e)
            return;
        float step = EdShift ? 100 : 10;
        SN.Vector2 fwd, right;
        if (_map)
        {
            fwd = new SN.Vector2(0, 1);
            right = new SN.Vector2(1, 0);
        }
        else
        {
            // la caméra regarde vers −(cos lacet, sin lacet) au sol
            fwd = new SN.Vector2(-MathF.Cos(_yaw), -MathF.Sin(_yaw));
            right = new SN.Vector2(-fwd.Y, fwd.X) * -1;
        }
        SN.Vector2 d = SN.Vector2.Zero;
        if (EdKeyRepeat(Keys.Up))
            d += fwd;
        if (EdKeyRepeat(Keys.Down))
            d -= fwd;
        if (EdKeyRepeat(Keys.Right))
            d += right;
        if (EdKeyRepeat(Keys.Left))
            d -= right;
        float dz = EdCtrl ? (EdKeyRepeat(Keys.PageUp) ? 1 : 0) - (EdKeyRepeat(Keys.PageDown) ? 1 : 0) : 0;
        if (d == SN.Vector2.Zero && dz == 0)
            return;
        SN.Vector2 from = new SN.Vector2(e.Position.X, e.Position.Y), to = from + d * step;
        float above = e.Position.Z - EdGroundZ(from);
        e.Position = new SN.Vector3(to, EdGroundZ(to) + above + dz * step);
        EdPlace(e);
    }

    // ---- relief : hauteur des cases de la grille ----
    private const float HeightStep = 25;   // un cran : 25 cm
    private (int x, int y) _edHeightCell = (-1, -1);
    private bool _edHeightWarned;

    /// <summary>A tenue : +1 (monter) ; Q tenue : −1 (descendre) ; sinon 0.</summary>
    private int EdHeightKey() => _edKeys.IsKeyDown(Keys.A) ? 1 : _edKeys.IsKeyDown(Keys.Q) ? -1 : 0;

    /// <summary>A ou Q + clic : la case sous la souris monte ou descend d'un cran ; en glissant, chaque case touchée aussi.</summary>
    private bool EdHeightInput(MouseState m, bool inView)
    {
        int dir = EdHeightKey();
        if (dir == 0 || _edDrag != null || _session.Grid is not { } g)
        {
            _edHeightCell = (-1, -1);
            return false;
        }
        if (m.LeftButton == ButtonState.Released)
        {
            _edHeightCell = (-1, -1);
            _edPressL = false;
            return true;
        }
        if (!inView || Ground(m.X, m.Y) is not { } p)
            return true;
        (int cx, int cy) = g.CellOf(new SN.Vector3(p, 0));
        if (!g.Inside(cx, cy) || (cx, cy) == _edHeightCell)
            return true;
        _edPressL = false;
        _edHeightCell = (cx, cy);
        EdStepHeight(cx, cy, dir * HeightStep * (EdShift ? 4 : 1));
        return true;
    }

    /// <summary>Change la hauteur d'une case de la grille (le sol où marchent les unités ; sur une carte sans décor, le sol dessiné).</summary>
    private void EdStepHeight(int cx, int cy, float dz)
    {
        if (_session.Grid is not { } g || !g.Inside(cx, cy))
            return;
        int i = g.Index(cx, cy);
        g.GroundHeight[i] = MathF.Round((g.GroundHeight[i] + dz) / 5) * 5;
        if (_noScenery)
            EdRebuildTerrain();
        else if (!_edHeightWarned)
        {
            _edHeightWarned = true;
            EdMessage(Tr("HeightScenery", "Carte avec décor : seule la hauteur où marchent les unités change, pas le sol dessiné"));
        }
        EdMessage(Tr("HeightCell", "Case ({0}, {1}) : hauteur {2:0} cm", cx, cy, g.GroundHeight[i]));
    }

    private MeshGpu? _edTerrain;

    /// <summary>Sol des cartes sans décor : refait à chaque changement de relief.</summary>
    private void EdRebuildTerrain()
    {
        _edTerrain?.Dispose();
        _edTerrain = null;
        if (_noScenery && _session.Grid is { } g)
            _edTerrain = Upload(TerrainAsset(g));
    }

    /// <summary>Case sous la souris pendant que A ou Q est tenue : un carré vert (monter) ou rouge (descendre).</summary>
    private void EdHeightCursor()
    {
        int dir = EdHeightKey();
        if (dir == 0 || _session.Grid is not { } g || !EdInView(_edFullMx, _edMy))
            return;
        MouseState m = EdViewMouse(Mouse.GetState());
        if (Ground(m.X, m.Y) is not { } p)
            return;
        (int cx, int cy) = g.CellOf(new SN.Vector3(p, 0));
        if (!g.Inside(cx, cy))
            return;
        float z = g.GroundHeight[g.Index(cx, cy)] + 4;
        Color c = dir > 0 ? new Color(80, 230, 110, 140) : new Color(235, 70, 60, 140);
        float x0 = cx * Grid.CellSize, y0 = cy * Grid.CellSize, x1 = x0 + Grid.CellSize, y1 = y0 + Grid.CellSize;
        EdQuad3(new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z), c);
    }

    // ---- case de la grille choisie (clic sur le sol, sans objet) ----
    private (int x, int y)? _edCell;

    /// <summary>Propriétés d'une case : zone A (0 : case fermée, comme dans le jeu d'origine), zone B, hauteur du sol.</summary>
    private void EdCellProps(int cx, int cy)
    {
        if (_session.Grid is not { } g || !g.Inside(cx, cy))
            return;
        int i = g.Index(cx, cy);
        string id = $"case.{cx}.{cy}.";
        EdPropNumber(id + "a", Tr("CellZoneA", "Zone A"), g.ZoneA[i], v =>
        {
            g.ZoneA[i] = (ushort)Math.Clamp(MathF.Round(v), 0, ushort.MaxValue);
            EdMessage(Tr("CellZoneASet", "Case ({0}, {1}) : zone A = {2}", cx, cy, g.ZoneA[i]));
        }, "0");
        EdPropText(Tr("CellZoneB", "Zone B"), g.ZoneB[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
        EdPropNumber(id + "h", Tr("CellHeight", "Hauteur du sol (cm)"), g.GroundHeight[i], v =>
        {
            g.GroundHeight[i] = MathF.Round(v);
            if (_noScenery)
                EdRebuildTerrain();
        }, "0");
        EdPropNote(Tr("CellNote", "Zone A = 0 : case fermée, les unités n'y passent pas (croix rouge). Les cases fermées autour sont marquées d'une croix rouge."));
    }

    /// <summary>Case choisie : cadre jaune ; autour d'elle, une croix rouge sur chaque case fermée (zone A = 0).</summary>
    private void EdCellMarks()
    {
        if (_edCell is not { } c || _session.Grid is not { } g)
            return;
        const int R = 15;
        Color red = new Color(235, 40, 30);
        for (int y = Math.Max(0, c.y - R); y <= Math.Min(g.Height - 1, c.y + R); y++)
            for (int x = Math.Max(0, c.x - R); x <= Math.Min(g.Width - 1, c.x + R); x++)
            {
                int i = g.Index(x, y);
                if (g.ZoneA[i] != 0)
                    continue;
                float x0 = x * Grid.CellSize + 15, y0 = y * Grid.CellSize + 15, z = g.GroundHeight[i] + 6;
                EdLine3(new(x0, y0, z), new(x0 + 70, y0 + 70, z), red);
                EdLine3(new(x0 + 70, y0, z), new(x0, y0 + 70, z), red);
            }
        float cx0 = c.x * Grid.CellSize, cy0 = c.y * Grid.CellSize, cz = g.GroundHeight[g.Index(c.x, c.y)] + 5;
        Color yellow = new Color(255, 210, 40);
        EdLine3(new(cx0, cy0, cz), new(cx0 + Grid.CellSize, cy0, cz), yellow);
        EdLine3(new(cx0 + Grid.CellSize, cy0, cz), new(cx0 + Grid.CellSize, cy0 + Grid.CellSize, cz), yellow);
        EdLine3(new(cx0 + Grid.CellSize, cy0 + Grid.CellSize, cz), new(cx0, cy0 + Grid.CellSize, cz), yellow);
        EdLine3(new(cx0, cy0 + Grid.CellSize, cz), new(cx0, cy0, cz), yellow);
    }
}
