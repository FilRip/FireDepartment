using Microsoft.Xna.Framework.Input;
using SN = System.Numerics;

namespace OpenFD.MG;

// éditeur : barres de séparation (comme un GridSplitter) entre la liste de gauche et la vue, entre la vue et la partie
// de droite, et entre la liste des objets à poser et les propriétés ; tirées au bouton gauche, curseur ↔ ou ↕ dessus
public sealed partial class OpenFDGame
{
    /// <summary>Largeur de la liste de gauche, bord gauche de la partie de droite, hauteur de la liste à poser (parts de la fenêtre).</summary>
    private float _edLeftFrac = 0.17f, _edRightFrac = 0.75f, _edListFrac = 0.75f;

    /// <summary>Barre tirée : 0 aucune, 1 gauche, 2 droite, 3 entre liste et propriétés.</summary>
    private int _edSplitDrag;
    private int _edSplitHover;
    private bool _edSplitLoaded;
    private MouseCursor? _edCursorShown;

    /// <summary>Épaisseur d'une barre : 5 pixels de l'écran.</summary>
    private float EdSplitW => MathF.Round(5f * _fbW / Math.Max(1, Window.ClientBounds.Width));

    /// <summary>Barre de gauche : juste après la liste des objets de la carte (si elle est ouverte).</summary>
    private (float x, float y, float w, float h) EdSplitLeft => (EdLeftW, EdMenuH, _edLeftOpen && _edLoaded ? EdSplitW : 0, _fbH - EdMenuH);

    /// <summary>Barre de droite : juste avant la partie de droite.</summary>
    private (float x, float y, float w, float h) EdSplitRight => (EdRightX - EdSplitW, EdMenuH, EdSplitW, _fbH - EdMenuH);

    /// <summary>Barre entre la liste des objets à poser et les propriétés.</summary>
    private (float x, float y, float w, float h) EdSplitMid
    {
        get
        {
            (float x, float y, float w, float h) l = EdListRect();
            return (l.x, l.y + l.h, l.w, EdSplitW);
        }
    }

    private float EdRightX => MathF.Round(_fbW * _edRightFrac);

    private static bool EdIn((float x, float y, float w, float h) r, float px, float py) => r.w > 0 && r.h > 0 && px >= r.x - 1 && px < r.x + r.w + 1 && py >= r.y && py < r.y + r.h;

    /// <summary>Survol et tirage des barres (avant la vue et les panneaux : un clic sur une barre ne sert qu'à elle).</summary>
    private void EdSplitInput(bool active)
    {
        // positions gardées d'une session à l'autre (OpenFD.Editor.ini, à côté de l'exécutable)
        if (!_edSplitLoaded)
        {
            _edSplitLoaded = true;
            _edLeftFrac = Math.Clamp(EditorSettings.Get("LeftList", _edLeftFrac), 0.08f, 0.6f);
            _edRightFrac = Math.Clamp(EditorSettings.Get("RightPanel", _edRightFrac), 0.3f, 0.85f);
            _edListFrac = Math.Clamp(EditorSettings.Get("ObjectList", _edListFrac), 0.15f, 0.85f);
            if (_edRightFrac - _edLeftFrac < 0.15f)
                _edLeftFrac = Math.Max(0.08f, _edRightFrac - 0.15f);
        }
        if (!_edDownL && _edSplitDrag != 0)
        {
            _edSplitDrag = 0;
            EditorSettings.Set(("LeftList", _edLeftFrac), ("RightPanel", _edRightFrac), ("ObjectList", _edListFrac));
        }
        _edSplitHover = 0;
        if (active && _edDialog == null && !_edStOpen && _edPopup == null)
        {
            if (EdIn(EdSplitLeft, _edMx, _edMy))
                _edSplitHover = 1;
            else if (EdIn(EdSplitRight, _edMx, _edMy))
                _edSplitHover = 2;
            else if (EdIn(EdSplitMid, _edMx, _edMy))
                _edSplitHover = 3;
            if (_edPressL && _edSplitHover != 0)
            {
                _edSplitDrag = _edSplitHover;
                _edPressL = false;
            }
        }
        float w = Math.Max(1, _fbW), h = Math.Max(1, _fbH - EdMenuH);
        switch (_edSplitDrag)
        {
            case 1:
                // la liste de gauche : entre 8 % de la fenêtre et la vue gardée d'au moins 15 %
                _edLeftFrac = Math.Clamp(_edMx / w, 0.08f, Math.Max(0.08f, _edRightFrac - 0.15f));
                break;
            case 2:
                // la partie de droite : au moins 15 % de la fenêtre, la vue d'au moins 15 %
                _edRightFrac = Math.Clamp((_edMx + EdSplitW / 2) / w, EdLeftW / w + 0.15f, 0.85f);
                break;
            case 3:
                _edListFrac = Math.Clamp((_edMy - EdMenuH) / h, 0.15f, 0.85f);
                break;
        }
        int show = _edSplitDrag != 0 ? _edSplitDrag : _edSplitHover;
        MouseCursor want = show is 1 or 2 ? MouseCursor.SizeWE : show == 3 ? MouseCursor.SizeNS : MouseCursor.Arrow;
        if (active && want != _edCursorShown)
        {
            try
            {
                Mouse.SetCursor(want);
            }
            catch (Exception)
            {
            }
            _edCursorShown = want;
        }
    }

    /// <summary>Les barres, par-dessus les panneaux (plus claires survolées ou tirées).</summary>
    private void EdDrawSplitters()
    {
        if (!_edLoaded)
            return;
        _ui.Begin();
        EdDrawSplit(EdSplitLeft, 1);
        EdDrawSplit(EdSplitRight, 2);
        EdDrawSplit(EdSplitMid, 3);
        _ui.End();
    }

    private void EdDrawSplit((float x, float y, float w, float h) r, int which)
    {
        if (r.w <= 0 || r.h <= 0)
            return;
        bool lit = _edSplitDrag == which || (_edSplitDrag == 0 && _edSplitHover == which);
        _ui.Rect(r.x, r.y, r.w, r.h, lit ? new SN.Vector4(0.85f, 0.35f, 0.1f, 1) : new SN.Vector4(0.30f, 0.30f, 0.36f, 1));
    }
}
