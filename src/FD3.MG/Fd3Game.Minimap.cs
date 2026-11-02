using FD3.Fire;
using FD3.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SN = System.Numerics;

namespace FD3.MG;

// mini-carte en bas à gauche, comme dans le jeu : image vue de dessus de la mission ([MISSION] Minimap du fichier .mis,
// par exemple « eng_bank_minimap » pour London), feux, unités, civils et champ de la caméra (trapèze jaune).
// Clic gauche (ou glisser) : la vue va à cet endroit ; clic droit : les unités choisies y vont.
public sealed partial class Fd3Game
{
    private Texture2D? _miniTex; private Rectangle _miniSrc;   // image et partie utile (la carte occupe le haut à gauche de l'image)
    private bool _dragMini;

    private void LoadMinimap()
    {
        _miniTex?.Dispose(); _miniTex = null;
        var name = _session.MissionConfig.Get("MISSION", "Minimap")?.Trim('"', ' ');
        if (string.IsNullOrEmpty(name) || _assets.Texture(_mission, name) is not { } bytes || Tex.DecodeColors(bytes) is not { } img) return;
        var (w, h, px) = img;
        // partie utile : colonnes et lignes à moitié non noires au moins (le reste de l'image est noir)
        bool Lit(Color c) => c.R + c.G + c.B > 40;
        int x0 = w, x1 = -1, y0 = h, y1 = -1;
        for (int x = 0; x < w; x++) { int n = 0; for (int y = 0; y < h; y++) if (Lit(px[y * w + x])) n++; if (n * 2 >= h) { x0 = Math.Min(x0, x); x1 = x; } }
        for (int y = 0; y < h; y++) { int n = 0; for (int x = 0; x < w; x++) if (Lit(px[y * w + x])) n++; if (n * 2 >= w) { y0 = Math.Min(y0, y); y1 = y; } }
        if (x1 < x0 || y1 < y0) { x0 = 0; y0 = 0; x1 = w - 1; y1 = h - 1; }
        _miniSrc = new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
        _miniTex = Tex.FromColors(GraphicsDevice, w, h, px, false);
    }

    // emplacement à l'écran (pixels) : carré en bas à gauche
    private (float x, float y, float w, float h) MiniRect()
    {
        float size = MathF.Max(180 * _scale, 0.27f * _fbH), m = 12 * _scale;   // environ un quart de la hauteur, comme dans le jeu
        float aspect = _miniTex != null ? (float)_miniSrc.Width / _miniSrc.Height : _gridH > 0 ? _gridW / _gridH : 1;
        float w = aspect >= 1 ? size : size * aspect, h = aspect >= 1 ? size / aspect : size;
        return (m, _fbH - m - h, w, h);
    }

    private bool MiniVisible => !_map && _session?.Grid != null && _gridW > 0;

    // monde → mini-carte (nord en haut, comme l'image du jeu)
    private SN.Vector2 MiniPos(float wx, float wy)
    {
        var r = MiniRect();
        return new(r.x + wx / _gridW * r.w, r.y + (1 - wy / _gridH) * r.h);
    }

    private SN.Vector2? MiniToWorld(double winX, double winY)
    {
        if (!MiniVisible) return null;
        var (sx, sy) = Fb(winX, winY); var r = MiniRect();
        if (sx < r.x || sy < r.y || sx > r.x + r.w || sy > r.y + r.h) return null;
        return new((sx - r.x) / r.w * _gridW, (1 - (sy - r.y) / r.h) * _gridH);
    }

    private void RenderMinimap()
    {
        if (!MiniVisible) return;
        var r = MiniRect(); float px = MathF.Max(1, _scale), b = 3 * _scale;
        _ui.Begin();
        _ui.Rect(r.x - b, r.y - b, r.w + 2 * b, r.h + 2 * b, new SN.Vector4(0.09f, 0.09f, 0.11f, 0.9f));
        _ui.End();
        var clip = new Rectangle((int)r.x, (int)r.y, (int)MathF.Ceiling(r.w), (int)MathF.Ceiling(r.h));
        _ui.BeginClipped(clip);
        if (_miniTex != null) _ui.Image(_miniTex, r.x, r.y, r.w, r.h, _miniSrc, SN.Vector4.One);
        else if (_gridTex != null) _ui.Image(_gridTex, r.x, r.y, r.w, r.h, SN.Vector4.One);
        float k = r.w / _gridW;   // pixels par unité du monde
        // feux
        foreach (var h in _session.Fire.HotPoints)
        {
            if (!h.IsBurning) continue;
            var p = MiniPos(h.Position.X, h.Position.Y);
            float f = Math.Min(1, h.Intensity / h.Class.IntensityMax);
            _ui.Disc(p.X, p.Y, MathF.Max(1.5f * px, (40 + 60 * f) * k), new SN.Vector4(1f, 0.25f + 0.35f * f, 0.1f, 0.9f));
        }
        // civils : inconscients en rouge, conscients en vert
        foreach (var c in _session.Units.Civilians.Where(c => !c.Hidden && (c.State == CivilianState.Conscious || c.State == CivilianState.Unconscious)))
        {
            var p = MiniPos(c.Position.X, c.Position.Y); float s = 2.5f * px;
            _ui.Rect(p.X - s, p.Y - s, 2 * s, 2 * s, new SN.Vector4(0, 0, 0, 0.8f));
            _ui.Rect(p.X - s + px, p.Y - s + px, 2 * s - 2 * px, 2 * s - 2 * px, c.State == CivilianState.Unconscious ? new SN.Vector4(1, 0.2f, 0.2f, 1) : new SN.Vector4(0.3f, 1, 0.4f, 1));
        }
        // unités : pompiers en bleu, véhicules plus gros ; celles choisies entourées de blanc
        foreach (var u in _session.Units.Active)
        {
            var p = MiniPos(u.Position.X, u.Position.Y); float s = (u.IsVehicle ? 3.5f : 2f) * px;
            if (_sel.Contains(u)) _ui.Rect(p.X - s - px, p.Y - s - px, 2 * s + 2 * px, 2 * s + 2 * px, new SN.Vector4(1, 1, 1, 1));
            _ui.Rect(p.X - s, p.Y - s, 2 * s, 2 * s, u.IsVehicle ? new SN.Vector4(0.9f, 0.15f, 0.15f, 1) : new SN.Vector4(0.25f, 0.55f, 1f, 1));
        }
        // objectifs en cours : repère « i » (LEV_SetObjectivePos), comme sur la mini-carte du jeu
        if (Host is { } host)
            foreach (var o in host.Objectives.Where(o => o.Status == 1))
                foreach (var (ox, oy) in o.Positions)
                {
                    var p = MiniPos(ox, oy); float rr = 7 * px;
                    _ui.Disc(p.X, p.Y, rr + px, new SN.Vector4(0, 0, 0, 0.8f));
                    _ui.Disc(p.X, p.Y, rr, new SN.Vector4(0.95f, 0.95f, 0.95f, 0.95f));
                    _ui.Text(p.X - _ui.Measure("i") / 2, p.Y - _ui.LineHeight / 2, "i", new SN.Vector4(0.1f, 0.1f, 0.1f, 1));
                }
        // champ de la caméra : coins de l'écran posés sur le sol
        var corners = new[] { (0f, 0f), (_fbW, 0f), (_fbW, _fbH), (0f, _fbH) }.Select(c => GroundFb(c.Item1, c.Item2)).ToArray();
        var yellow = new SN.Vector4(1f, 0.92f, 0.2f, 0.95f);
        for (int i = 0; i < 4; i++)
        {
            var a = MiniPos(corners[i].X, corners[i].Y); var c = MiniPos(corners[(i + 1) % 4].X, corners[(i + 1) % 4].Y);
            _ui.Line(a.X, a.Y, c.X, c.Y, yellow, 1.5f * px);
        }
        _ui.End();
        _ui.Begin();
        _ui.Frame(r.x - px, r.y - px, r.w + 2 * px, r.h + 2 * px, new SN.Vector4(0.55f, 0.55f, 0.6f, 0.9f), px);
        _ui.End();
    }

    // point du sol vu à une position de l'écran (pixels de l'image) ; vers l'horizon, un point lointain dans la même direction
    private SN.Vector2 GroundFb(float sx, float sy)
    {
        if (!SN.Matrix4x4.Invert(_viewProj, out var inv)) return new(_target.X, _target.Y);
        float nx = sx / _fbW * 2 - 1, ny = 1 - sy / _fbH * 2;
        var a = SN.Vector4.Transform(new SN.Vector4(nx, ny, 0, 1), inv); var b = SN.Vector4.Transform(new SN.Vector4(nx, ny, 1, 1), inv);
        var o = new SN.Vector3(a.X, a.Y, a.Z) / a.W; var d = new SN.Vector3(b.X, b.Y, b.Z) / b.W - o;
        float far = 4 * MathF.Max(_gridW, _gridH);
        if (d.Z < -1e-6f)
        {
            float t = -o.Z / d.Z;
            var g = o + d * t;
            var flat = new SN.Vector2(g.X - o.X, g.Y - o.Y);
            if (flat.Length() <= far) return new(g.X, g.Y);
        }
        var h = new SN.Vector2(d.X, d.Y);
        if (h.LengthSquared() < 1e-9f) return new(o.X, o.Y);
        return new SN.Vector2(o.X, o.Y) + SN.Vector2.Normalize(h) * far;
    }

    // clic sur la mini-carte : true si le clic lui revient
    private bool MiniPress(double x, double y, bool right)
    {
        if (MiniToWorld(x, y) is not { } w) return false;
        if (right) MoveSelection(w);
        else { _dragMini = true; MiniLook(w); }
        return true;
    }

    private void MiniLook(SN.Vector2 w) { _target.X = w.X; _target.Y = w.Y; }
}
