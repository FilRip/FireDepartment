using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SN = System.Numerics;

namespace OpenFD.MG;

// éditeur : la liste de droite (onglets, aperçus 3D deux par ligne, barre de défilement), d'où l'on tire les objets
public sealed partial class OpenFDGame
{
    private int _edTab;
    private readonly ScrollBar[] _edBars = { new(), new(), new(), new(), new() };
    private static string[] EdTabs => new[] { Tr("TabObjects", "Objets 3D"), Tr("TabDynamic", "Dynamiques"), Tr("TabCivilians", "Civils"), Tr("TabFiremen", "Pompiers"), Tr("TabTrees", "Arbres") };
    private float _edTabScroll;          // décalage des onglets (flèches à gauche et à droite quand ils ne tiennent pas)
    private string _edListFilter = "";   // filtre de la liste de droite (noms qui contiennent ce texte)

    // aperçus : une image par élément, faite quand il devient visible ; celui sous la souris tourne sur lui-même
    private readonly Dictionary<EditorItem, RenderTarget2D> _edPreviews = new();
    private readonly LinkedList<EditorItem> _edPreviewAge = new();
    private readonly List<EditorItem> _edWanted = new();
    private EditorItem? _edHoverItem;
    private RenderTarget2D? _edHoverRt;
    private int _edPreviewSize;
    private const int MaxPreviews = 160;
    private const int PreviewsPerFrame = 6;

    private List<EditorItem> EdTabItems => _edCatalog == null ? new List<EditorItem>() : _edTab switch
    {
        0 => _edCatalog.Objects,
        1 => _edCatalog.Dynamic,
        2 => _edCatalog.Civilians,
        3 => _edCatalog.Firemen,
        _ => _edCatalog.Trees,
    };

    /// <summary>Texte d'un champ de filtre : celui que l'on tape (champ en cours), sinon celui validé.</summary>
    private string EdFilterOf(string id, string committed) => _edFocusId == id ? _edFocusText : committed;

    private static bool EdNameMatches(string name, string filter) =>
        filter.Trim().Length == 0 || name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Champ de filtre (une loupe à gauche, le texte « Filtrer… » quand il est vide).</summary>
    private void EdFilterField(string id, float x, float y, float w, float h, string value, Action<string> commit)
    {
        EdField(id, x, y, w, h, value, commit);
        if (_edFocusId != id && value.Length == 0)
            _ui.Text(x + 6 * _scale, y + (h - _ui.LineHeight) / 2, Tr("Filter", "Filtrer…"), new SN.Vector4(EdMuted.X, EdMuted.Y, EdMuted.Z, 0.6f));
    }

    private void EdClearPreviews()
    {
        foreach (RenderTarget2D rt in _edPreviews.Values)
            rt.Dispose();
        _edPreviews.Clear();
        _edPreviewAge.Clear();
        _edHoverRt?.Dispose();
        _edHoverRt = null;
    }

    /// <summary>Aperçus demandés au dessin précédent (quelques-uns par image), et celui qui tourne.</summary>
    private void EdRenderPreviews()
    {
        int size = Math.Max(32, _edPreviewSize);
        if (_edPreviews.Count > 0 && _edPreviews.Values.First().Width != size)
            EdClearPreviews();
        int done = 0;
        foreach (EditorItem it in _edWanted)
        {
            if (done >= PreviewsPerFrame || (it.Model == null && it.TreeModel == null) || _edPreviews.ContainsKey(it))
                continue;
            RenderTarget2D rt = new RenderTarget2D(GraphicsDevice, size, size, false, SurfaceFormat.Color, DepthFormat.Depth24, 4, RenderTargetUsage.PreserveContents);
            EdRenderPreview(it, rt, -0.8f);
            _edPreviews[it] = rt;
            _edPreviewAge.AddLast(it);
            done++;
        }
        _edWanted.Clear();
        while (_edPreviewAge.Count > MaxPreviews && _edPreviewAge.First is { } old)
        {
            _edPreviewAge.RemoveFirst();
            if (old.Value != _edHoverItem && _edPreviews.Remove(old.Value, out RenderTarget2D? rt))
                rt.Dispose();
        }
        if (_edHoverItem is { } hv && (hv.Model != null || hv.TreeModel != null))
        {
            if (_edHoverRt == null || _edHoverRt.Width != size)
            {
                _edHoverRt?.Dispose();
                _edHoverRt = new RenderTarget2D(GraphicsDevice, size, size, false, SurfaceFormat.Color, DepthFormat.Depth24, 4, RenderTargetUsage.PreserveContents);
            }
            EdRenderPreview(_edHoverItem, _edHoverRt, -0.8f + (float)_edClock * 1.2f);
        }
        GraphicsDevice.SetRenderTarget(null);
    }

    /// <summary>Le modèle seul, cadré dans l'image carrée, vu de trois quarts depuis l'angle donné.</summary>
    private void EdRenderPreview(EditorItem it, RenderTarget2D rt, float angle)
    {
        GraphicsDevice gd = GraphicsDevice;
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.21f, 0.21f, 0.25f));
        if (it.Model == null && it.TreeModel == null)
            return;
        float th = it.TreeHeight * 0.3f;
        (SN.Vector3 min, SN.Vector3 max) box = it.Model != null ? EdModelBox(it.Model) ?? (new SN.Vector3(-50), new SN.Vector3(50)) : (new SN.Vector3(-th, -th, 0), new SN.Vector3(th, th, it.TreeHeight));
        SN.Vector3 c = (box.min + box.max) / 2;
        float r = MathF.Max(10, (box.max - box.min).Length() / 2);
        const float Pitch = 0.45f;
        SN.Vector3 eye = c + r * 2.4f * new SN.Vector3(MathF.Cos(angle) * MathF.Cos(Pitch), MathF.Sin(angle) * MathF.Cos(Pitch), MathF.Sin(Pitch));
        SN.Matrix4x4 view = SN.Matrix4x4.CreateLookAt(eye, c, SN.Vector3.UnitZ);
        SN.Matrix4x4 proj = SN.Matrix4x4.CreatePerspectiveFieldOfView(40f * MathF.PI / 180, 1, r * 0.05f, r * 10);
        foreach (IEffectMatrices e in new IEffectMatrices[] { _lit, _plain, _dual, _alphaFx })
        {
            e.View = view.X();
            e.Projection = proj.X();
        }
        _refl.SetFrame(view.X(), (view * proj).X(), eye.X());
        gd.DepthStencilState = DepthStencilState.Default;
        gd.BlendState = BlendState.Opaque;
        gd.RasterizerState = RasterizerState.CullNone;
        gd.SamplerStates[0] = SamplerState.AnisotropicWrap;
        if (it.TreeModel != null)
            EdDrawTree(it.TreeModel, it.TreeSeed, it.TreeHeight, SN.Matrix4x4.Identity);
        else if (it.Model != null)
            EdDrawModel(it.Model, SN.Matrix4x4.Identity, it.Kind == EditorItemKind.Fireman && it.ClassPath != null ? EdHiddenTools(it.ClassPath, it.Model) : null);
    }

    private void EdDrawPanel()
    {
        (float x, float y, float w, float h) = EdListRect();
        float s = _scale, pad = 6 * s, tabH = _ui.LineHeight + 12 * s, sbW = 12 * s;
        _ui.Begin();
        _ui.Rect(x, y, w, h, EdPanel);
        // filtre, au-dessus des onglets
        float fh = _ui.LineHeight + 10 * s;
        EdFilterField("liste.filtre", x + pad, y + pad, w - 2 * pad, fh, _edListFilter, v => _edListFilter = v);
        float tabY = y + pad + fh + pad;
        // onglets à la largeur de leur nom ; s'ils ne tiennent pas, deux flèches les font défiler
        float[] widths = EdTabs.Select(t => _ui.Measure(t) + 18 * s).ToArray();
        float natural = widths.Sum(), avail = w - 2 * pad, arrowW = tabH * 0.8f;
        bool scrolls = natural > avail;
        float tabsX = x + pad + (scrolls ? arrowW + 2 * s : 0), tabsW = avail - (scrolls ? 2 * (arrowW + 2 * s) : 0);
        _edTabScroll = scrolls ? Math.Clamp(_edTabScroll, 0, natural - tabsW) : 0;
        if (scrolls)
        {
            foreach ((float ax, int dir) in new[] { (x + pad, -1), (x + w - pad - arrowW, 1) })
            {
                bool over = EdHit(ax, tabY, arrowW, tabH);
                bool can = dir < 0 ? _edTabScroll > 0 : _edTabScroll < natural - tabsW;
                _ui.Rect(ax, tabY, arrowW, tabH, over && can ? EdHover : new SN.Vector4(0.12f, 0.12f, 0.14f, 1));
                // triangle plein, pointe vers la gauche ou la droite
                float mx = ax + arrowW / 2, my = tabY + tabH / 2, hh = tabH * 0.22f;
                SN.Vector4 col = can ? EdText : EdLine;
                for (float t = -hh; t <= hh; t += 1)
                {
                    float len = (hh - MathF.Abs(t)) * 1.1f;
                    _ui.Rect(dir < 0 ? mx + hh * 0.55f - len : mx - hh * 0.55f, my + t, len, 1, col);
                }
                if (over && can && _edPressL)
                {
                    _edPressL = false;
                    // une flèche : l'onglet suivant (ou précédent) se montre en entier
                    float start = 0, next = _edTabScroll;
                    foreach (float tw0 in widths)
                    {
                        if (dir < 0 && start < _edTabScroll - 1)
                            next = start;
                        if (dir > 0 && start + tw0 > _edTabScroll + tabsW + 1)
                        {
                            next = start + tw0 - tabsW;
                            break;
                        }
                        start += tw0;
                    }
                    _edTabScroll = Math.Clamp(next, 0, natural - tabsW);
                }
            }
        }
        _ui.End();
        _ui.BeginClipped(new Rectangle((int)tabsX, (int)tabY, (int)tabsW, (int)tabH + 1));
        float tabX = tabsX - _edTabScroll;
        for (int i = 0; i < EdTabs.Length; i++)
        {
            float tw = widths[i], tx = tabX;
            bool on = i == _edTab;
            bool overTab = EdHit(tx, tabY, tw - 2 * s, tabH) && EdHit(tabsX, tabY, tabsW, tabH);
            _ui.Rect(tx, tabY, tw - 2 * s, tabH, on ? new SN.Vector4(0.85f, 0.35f, 0.1f, 0.95f) : overTab ? EdHover : new SN.Vector4(0.12f, 0.12f, 0.14f, 1));
            _ui.Text(tx + (tw - 2 * s - _ui.Measure(EdTabs[i])) / 2, tabY + (tabH - _ui.LineHeight) / 2, EdTabs[i], on ? EdText : EdMuted);
            if (overTab && _edPressL)
            {
                _edPressL = false;
                EdCommitFocus();
                _edTab = i;
            }
            tabX += tw;
        }
        _ui.End();
        _ui.Begin();
        float cx = x + pad, cy = tabY + tabH + pad, cw = w - 2 * pad, ch = h - (cy - y) - pad;
        _ui.Frame(cx - 1, cy - 1, cw + 2, ch + 2, EdLine, MathF.Max(1, s));
        // cadre autour de toute la liste
        _ui.Frame(x + 1, y + 1, w - 2, h - 2, EdLine, MathF.Max(1, s));
        _ui.End();

        string filter = EdFilterOf("liste.filtre", _edListFilter);
        List<EditorItem> items = EdTabItems.Where(i => EdNameMatches(i.Name, filter)).ToList();
        sbW = EdScrollW;
        float gap = 6 * s, tileW = MathF.Max(40, (cw - sbW - 3 * gap) / 2), imgH = tileW, tileH = imgH + _ui.LineHeight + 2 * gap;
        _edPreviewSize = (int)tileW;
        int rows = (items.Count + 1) / 2;
        float total = rows * tileH + gap;
        bool overList = EdHit(cx, cy, cw, ch);
        ScrollBar bar = _edBars[_edTab];
        if (overList && _edWheel != 0 && _edPopup == null)
        {
            bar.Value -= _edWheel / 120f * tileH * 0.75f;
            _edWheel = 0;
        }
        // barre de défilement : flèches haut et bas, poignée à tirer, un clic à côté saute d'une page
        _ui.Begin();
        bar.Run(_ui, cx + cw - sbW, cy, sbW, ch, total, ch, tileH / 2, _edMx, _edMy, ref _edPressL, _edDownL, _edClock);
        _ui.End();
        float scroll = bar.Value;

        EditorItem? hover = null;
        _ui.BeginClipped(new Rectangle((int)cx, (int)cy, (int)(cw - sbW), (int)ch));
        int first = Math.Max(0, (int)((scroll - gap) / tileH)), last = Math.Min(rows - 1, (int)((scroll + ch) / tileH));
        for (int row = first; row <= last; row++)
            for (int col = 0; col < 2; col++)
            {
                int i = row * 2 + col;
                if (i >= items.Count)
                    break;
                EditorItem it = items[i];
                float tx = cx + gap + col * (tileW + gap), ty = cy + gap + row * tileH - scroll;
                bool over = overList && _edPopup == null && EdHit(tx, ty, tileW, tileH - gap);
                if (over)
                    hover = it;
                _ui.Rect(tx, ty, tileW, tileH - gap, over ? EdHover : new SN.Vector4(0.13f, 0.13f, 0.16f, 1));
                RenderTarget2D? img = over && it == _edHoverItem && _edHoverRt != null ? _edHoverRt : _edPreviews.TryGetValue(it, out RenderTarget2D? p) ? p : null;
                if (it.Model == null && it.TreeModel == null)
                    EdDrawIcon(it.Kind, tx, ty, tileW, imgH);
                else if (img != null)
                    _ui.Image(img, tx, ty, tileW, imgH, SN.Vector4.One);
                else
                {
                    _edWanted.Add(it);
                    _ui.Text(tx + tileW / 2 - _ui.Measure("...") / 2, ty + imgH / 2, "...", EdMuted);
                }
                string name = EdFit(it.Name, tileW - 6 * s);
                _ui.Text(tx + (tileW - _ui.Measure(name)) / 2, ty + imgH + gap * 0.5f, name, over ? EdText : EdMuted);
                if (over)
                    _ui.Frame(tx, ty, tileW, tileH - gap, EdAccent, MathF.Max(1, s));
                if (over && _edPressL)
                {
                    // on tire l'objet vers la vue (il est posé quand on le lâche sur le sol)
                    _edPressL = false;
                    EdCommitFocus();
                    if (_edLoaded)
                    {
                        _edDrag = it;
                        _edDragEuler = SN.Vector3.Zero;
                        _edDragDz = 0;
                    }
                    else
                        EdMessage(Tr("OpenFirst", "Ouvrez d'abord une mission (« Charger » ou « Nouveau »)"), true);
                }
            }
        _ui.End();
        _edHoverItem = hover;
        if (hover != null && !_edPreviews.ContainsKey(hover))
            _edWanted.Insert(0, hover);
        _ui.Begin();
        string count = $"{items.Count}";
        _ui.Text(x + w - pad - _ui.Measure(count) * 0.8f - sbW - 4 * s, y + h - pad - _ui.LineHeight * 0.8f, count, EdMuted, _scale * 0.8f);
        _ui.End();
    }

    /// <summary>Image des repères de l'éditeur (pas de modèle 3D).</summary>
    private void EdDrawIcon(EditorItemKind kind, float x, float y, float w, float h)
    {
        float s = _scale, m = w * 0.22f;
        _ui.Rect(x, y, w, h, new SN.Vector4(0.21f, 0.21f, 0.25f, 1));
        float l = x + m, t = y + m, r = x + w - m, b = y + h - m;
        switch (kind)
        {
            case EditorItemKind.Fire:
                if (FlameTexture(DefaultFlameModel) is { } ft)
                {
                    const int Frame = 9;
                    int fw = ft.Width / FlameCols, fh = ft.Height / FlameRows;
                    _ui.Image(ft, l, t - m * 0.5f, r - l, b - t + m * 0.5f, new Rectangle(Frame % FlameCols * fw, Frame / FlameCols * fh, fw, fh), new SN.Vector4(1f, 0.75f, 0.35f, 1));
                }
                else
                    _ui.Disc((l + r) / 2, (t + b) / 2, (r - l) / 3, new SN.Vector4(1, 0.5f, 0.1f, 1));
                break;
            case EditorItemKind.Zone:
                _ui.Rect(l, t, r - l, b - t, new SN.Vector4(0.3f, 0.9f, 0.45f, 0.25f));
                _ui.Frame(l, t, r - l, b - t, new SN.Vector4(0.3f, 0.9f, 0.45f, 1), 3 * s);
                break;
            case EditorItemKind.RoofMask:
                _ui.Rect(l, t, r - l, b - t, new SN.Vector4(0, 0, 0, 1));
                break;
            case EditorItemKind.Camera:
            {
                SN.Vector4 c = new SN.Vector4(0.4f, 0.7f, 1, 1);
                float bh = (b - t) * 0.5f, by = (t + b) / 2 - bh / 2;
                _ui.Rect(l, by, (r - l) * 0.62f, bh, c);
                float lx = l + (r - l) * 0.62f;
                _ui.Line(lx, (t + b) / 2, r, by - bh * 0.2f, c, 4 * s);
                _ui.Line(lx, (t + b) / 2, r, by + bh * 1.2f, c, 4 * s);
                _ui.Line(r, by - bh * 0.2f, r, by + bh * 1.2f, c, 4 * s);
                break;
            }
            case EditorItemKind.Waypoint:
            {
                SN.Vector4 c = new SN.Vector4(0.45f, 0.75f, 1, 1);
                float cxm = (l + r) / 2;
                _ui.Line(cxm, t, cxm, b, c, 4 * s);
                _ui.Rect(cxm, t, (r - l) * 0.4f, (b - t) * 0.3f, c);
                _ui.Disc(cxm, b, 6 * s, c);
                break;
            }
        }
    }

    /// <summary>Nom de l'objet tiré, sous la souris.</summary>
    private void EdDrawDragGhost()
    {
        if (_edDrag is not { } it)
            return;
        _ui.Begin();
        string label = "+ " + it.Name;
        float w = _ui.Measure(label) + 12 * _scale;
        _ui.Rect(_edMx + 16 * _scale, _edMy + 14 * _scale, w, _ui.LineHeight + 6 * _scale, new SN.Vector4(0, 0, 0, 0.7f));
        _ui.Text(_edMx + 22 * _scale, _edMy + 17 * _scale, label, EdAccent);
        _ui.End();
    }
}
