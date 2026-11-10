using SN = System.Numerics;

namespace OpenFD.MG;

/// <summary>
/// Barre de défilement verticale des listes de l'éditeur : une flèche en haut (remonter), une en bas (descendre), la
/// poignée entre les deux (à tirer) ; un clic à côté de la poignée saute d'une page. Une flèche tenue défile en continu.
/// </summary>
public sealed class ScrollBar
{
    /// <summary>Décalage (en pixels de l'image) du haut du contenu.</summary>
    public float Value;
    private bool _drag;
    private int _hold;
    private double _next;

    private static readonly SN.Vector4 Track = new(0.1f, 0.1f, 0.12f, 1);
    private static readonly SN.Vector4 Thumb = new(0.45f, 0.45f, 0.5f, 1);
    private static readonly SN.Vector4 Active = new(1f, 0.48f, 0.18f, 1);
    private static readonly SN.Vector4 Button = new(0.24f, 0.24f, 0.28f, 1);
    private static readonly SN.Vector4 ButtonHover = new(0.34f, 0.34f, 0.4f, 1);

    public void Clamp(float total, float view) => Value = Math.Clamp(Value, 0, MathF.Max(0, total - view));

    /// <summary>
    /// Mise à jour et dessin (dans un Ui déjà ouvert). <paramref name="press"/> : clic du moment, remis à false s'il est
    /// pris par la barre ; <paramref name="down"/> : bouton tenu.
    /// </summary>
    public void Run(Ui ui, float x, float y, float w, float h, float total, float view, float step, float mx, float my, ref bool press, bool down, double clock)
    {
        float max = MathF.Max(0, total - view);
        float a = MathF.Min(w, h / 3), ty = y + a, th = h - 2 * a;
        float thumbH = max > 0 ? MathF.Max(MathF.Min(th, 18), th * view / MathF.Max(view, total)) : th;
        float thumbY = ty + (th - thumbH) * (max > 0 ? Value / max : 0);
        bool Hit(float hx, float hy, float hw, float hh) => mx >= hx && mx < hx + hw && my >= hy && my < hy + hh;
        if (press && Hit(x, y, w, h))
        {
            press = false;
            if (Hit(x, y, w, a))
            {
                Value -= step;
                _hold = -1;
                _next = clock + 0.35;
            }
            else if (Hit(x, y + h - a, w, a))
            {
                Value += step;
                _hold = 1;
                _next = clock + 0.35;
            }
            else if (my >= thumbY && my < thumbY + thumbH)
                _drag = true;
            else
                Value += my < thumbY ? -view : view;
        }
        if (!down)
        {
            _drag = false;
            _hold = 0;
        }
        if (_hold != 0 && clock >= _next)
        {
            Value += _hold * step;
            _next = clock + 0.05;
        }
        if (_drag && max > 0)
            Value = (my - ty - thumbH / 2) / MathF.Max(1, th - thumbH) * max;
        Value = Math.Clamp(Value, 0, max);
        thumbY = ty + (th - thumbH) * (max > 0 ? Value / max : 0);
        ui.Rect(x, y, w, h, Track);
        ui.Rect(x, y, w, a, Hit(x, y, w, a) || _hold < 0 ? ButtonHover : Button);
        ui.Rect(x, y + h - a, w, a, Hit(x, y + h - a, w, a) || _hold > 0 ? ButtonHover : Button);
        SN.Vector4 arrow = max > 0 ? new SN.Vector4(0.9f, 0.9f, 0.9f, 1) : new SN.Vector4(0.5f, 0.5f, 0.55f, 1);
        Triangle(ui, x + w / 2, y + a / 2, w * 0.3f, true, arrow);
        Triangle(ui, x + w / 2, y + h - a / 2, w * 0.3f, false, arrow);
        if (max > 0)
            ui.Rect(x + w * 0.18f, thumbY, w * 0.64f, thumbH, _drag ? Active : Thumb);
    }

    /// <summary>Petit triangle plein (une ligne de pixels à la fois), pointe en haut ou en bas.</summary>
    private static void Triangle(Ui ui, float cx, float cy, float r, bool up, SN.Vector4 c)
    {
        int rows = Math.Max(2, (int)MathF.Round(r));
        for (int i = 0; i < rows; i++)
        {
            float t = (i + 0.5f) / rows, half = r * t;
            float yy = up ? cy - r / 2 + i : cy + r / 2 - i - 1;
            ui.Rect(cx - half, yy, 2 * half, 1, c);
        }
    }
}
