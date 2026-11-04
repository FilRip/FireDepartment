using OpenFD.Formats;
using OpenFD.Game;
using Microsoft.Xna.Framework.Input;
using SN = System.Numerics;

namespace OpenFD.MG;

// cinématiques des missions, jouées par les scripts du jeu (Data\Script\Mission\…) :
//  • CAM_TeleportToWayPoint / CAM_CinematicSetToWaypoint : la caméra saute ou glisse vers un point de passage de la carte
//    (sa position = l'œil ; l'axe −Y de son orientation = la direction du regard, l'axe Z = le haut) ;
//  • PNL_PrintSubtitle : sous-titre (texte traduit de Data\Localize), affiché en bas, centré ;
//  • SND_PlaySound2D : voix off (Data\Sound\Vocal) ;
//  • CAM_ModePlay : fin, le joueur reprend la main là où la caméra s'est arrêtée.
// Pendant la cinématique : bandes noires, pas d'interface ; Échap ou Entrée la passe.
public sealed partial class OpenFDGame
{
    private Localization? _loc;
    private bool _cineOn;                                  // cinématique en cours (vue du jeu)
    private (SN.Vector3 eye, SN.Vector3 fwd) _cinePose, _cineFrom, _cineTo;
    private double _cineT0; private float _cineDur; private bool _cineMoving;
    private float _cineBars;                               // 0 → 1 : hauteur des bandes noires (apparition en douceur)
    private string? _subtitle;

    private MissionHost? Host => _session?.Host;

    private static (SN.Vector3 eye, SN.Vector3 fwd) WaypointPose(LevelObject o)
    {
        SN.Matrix4x4 m = o.Matrix;
        SN.Vector3 fwd = -new SN.Vector3(m.M21, m.M22, m.M23);
        if (fwd.LengthSquared() < 1e-6f)
            fwd = new SN.Vector3(0, 1, -0.5f);
        return (new SN.Vector3(m.M41, m.M42, m.M43), SN.Vector3.Normalize(fwd));
    }

    // pose de la caméra actuelle (œil, regard), d'après la caméra orbitale du joueur
    private (SN.Vector3 eye, SN.Vector3 fwd) CurrentPose()
    {
        SN.Vector3 eye = _target + _dist * new SN.Vector3(MathF.Cos(_pitch) * MathF.Cos(_yaw), MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch));
        return (eye, SN.Vector3.Normalize(_target - eye));
    }

    // place la caméra orbitale pour reproduire exactement la vue (œil, regard)
    private void ApplyPose((SN.Vector3 eye, SN.Vector3 fwd) p)
    {
        SN.Vector3 f = SN.Vector3.Normalize(p.fwd);
        float d = f.Z < -0.05f ? (p.eye.Z - GroundAt(p.eye.X + f.X * p.eye.Z / -f.Z, p.eye.Y + f.Y * p.eye.Z / -f.Z)) / -f.Z : 3000f;
        d = Math.Clamp(d, 300, 60000);
        _target = p.eye + f * d; _dist = d;
        _yaw = MathF.Atan2(-f.Y, -f.X);
        _pitch = MathF.Asin(Math.Clamp(-f.Z, -1, 1));
    }

    /// <summary>À chaque image : ordres de caméra des scripts, trajet en cours, sous-titres, voix, messages.</summary>
    private void UpdateCinema(float dt)
    {
        if (Host is not { } h)
            return;
        _loc ??= Localization.Load(_files.DataDir);
        foreach (string s in h.Sounds2D) _audio?.Play(s);
        h.Sounds2D.Clear();
        foreach (string m in h.Messages) Log(_loc.Get(m));
        h.Messages.Clear();
        _subtitle = h.Subtitle != null ? _loc.Get(h.Subtitle) : null;

        bool free = CameraSpec != null;   // essais : caméra imposée
        // vue du joueur, retenue avant que les ordres de caméra d'une cinématique (souvent plusieurs : saut, puis glissement…) ne la changent
        if (!_cineOn && !_cineMoving && h.CameraOrders.Count == 0)
            _playerView = (_target, _yaw, _pitch, _dist);
        double now = _session.Time + (_paused ? 0 : _owed * Session.Dt);
        foreach ((LevelObject? wp, float dur, bool tp, double at) in h.CameraOrders)
        {
            if (free)
                continue;
            (SN.Vector3 eye, SN.Vector3 fwd) to = WaypointPose(wp);
            if (tp || dur <= 0.01f)
            {
                _cinePose = to;
                _cineMoving = false;
            }
            else
            {
                _cineFrom = _cineMoving || _cineOn ? _cinePose : CurrentPose();
                _cineTo = to;
                _cineT0 = at;
                _cineDur = dur;
                _cineMoving = true;
            }
            ApplyPose(_cinePose = _cineMoving ? _cineFrom : _cinePose);
        }
        h.CameraOrders.Clear();

        bool was = _cineOn;
        _cineOn = h.Cinematic;
        if (_cineOn && !was)
        {
            _sel.Clear(); _speed = 1; _paused = false;
            // cinématique en cours de partie (pas celle d'introduction, au tout début) : on retient la vue du joueur
            _viewBeforeCine = _session.Time > 3 ? _playerView : null;
        }
        if (!_cineOn && was && _viewBeforeCine is { } v)
        {
            // fin de la cinématique : la vue revient où elle était
            (_target, _yaw, _pitch, _dist) = v; _viewBeforeCine = null; _cineMoving = false;
        }
        if (_cineMoving && !free)
        {
            float t = (float)Math.Clamp((now - _cineT0) / _cineDur, 0, 1);
            // CAM_SetInertia(1) : départ et arrivée en douceur
            float k = h.CameraInertia > 0 ? t * t * (3 - 2 * t) : t;
            SN.Vector3 eye = SN.Vector3.Lerp(_cineFrom.eye, _cineTo.eye, k);
            SN.Vector3 fwd = SN.Vector3.Normalize(SN.Vector3.Lerp(_cineFrom.fwd, _cineTo.fwd, k) + new SN.Vector3(0, 0, -1e-4f));
            ApplyPose(_cinePose = (eye, fwd));
            if (t >= 1)
                _cineMoving = false;
        }
        _cineBars = Math.Clamp(_cineBars + (_cineOn ? dt : -dt) * 2.5f, 0, 1);
    }

    /// <summary>Le joueur passe la cinématique : la simulation avance (scripts compris) jusqu'à sa fin.</summary>
    private (SN.Vector3 target, float yaw, float pitch, float dist)? _viewBeforeCine, _playerView;

    private void SkipCinematic()
    {
        if (Host is not { } h || !h.Cinematic)
            return;
        Rec("skipcine");
        for (int i = 0; i < 1200 && h.Cinematic; i++) { SnapshotPoses(); _session.Step(); }
        h.EndCinematic();
        h.Sounds2D.Clear();
        _audio?.StopAll();
        _cineMoving = false;
        // dernière position demandée par la cinématique
        if (h.CameraOrders.Count > 0)
            ApplyPose(WaypointPose(h.CameraOrders[^1].waypoint));
        h.CameraOrders.Clear();
        _owed = 0;
    }

    private bool CinemaKeys(KeyboardState k)
    {
        if (!_cineOn)
            return false;
        if (Pressed(k, Keys.Escape) || Pressed(k, Keys.Enter) || Pressed(k, Keys.Space))
        {
            // en réseau : seul le serveur passe la cinématique, au même pas chez tous
            if (_netGame && _net != null)
            {
                if (_net.IsServer)
                    NetSubmit("skipcine");
            }
            else
                SkipCinematic();
        }
        return true;
    }

    // bandes noires et sous-titres
    private void RenderCinema()
    {
        float bar = _fbH * 0.11f * _cineBars;
        SN.Vector4 black = new SN.Vector4(0, 0, 0, 1);
        _ui.Begin();
        if (bar > 0.5f)
        {
            _ui.Rect(0, 0, _fbW, bar, black);
            _ui.Rect(0, _fbH - bar, _fbW, bar, black);
        }
        if (!string.IsNullOrEmpty(_subtitle))
        {
            float sc = _scale * 1.3f, lh = _font.LineHeight * sc * 1.15f, maxW = _fbW * 0.7f;
            List<string> lines = Wrap(_subtitle!, maxW, sc);
            float y = _fbH - MathF.Max(bar, 0) + (bar > lh * lines.Count ? (bar - lh * lines.Count) / 2 : -lh * lines.Count - 16 * _scale);
            if (bar < lh * lines.Count)
            {
                float w = lines.Max(l => _font.Measure(l) * sc) + 40 * _scale;
                _ui.Rect((_fbW - w) / 2, y - 8 * _scale, w, lh * lines.Count + 16 * _scale, new SN.Vector4(0, 0, 0, 0.6f));
            }
            foreach (string l in lines)
            {
                float w = _font.Measure(l) * sc, x = (_fbW - w) / 2;
                _ui.Text(x + 2 * _scale, y + 2 * _scale, l, new SN.Vector4(0, 0, 0, 0.9f), sc);
                _ui.Text(x, y, l, new SN.Vector4(1f, 0.96f, 0.85f, 1), sc);
                y += lh;
            }
        }
        if (_cineOn && bar > 0.5f)
        {
            const string hint = "Échap : passer";
            _ui.Text(_fbW - _ui.Measure(hint) - 16 * _scale, (bar - _ui.LineHeight) / 2, hint, new SN.Vector4(0.6f, 0.6f, 0.6f, _cineBars));
        }
        _ui.End();
    }

    // coupe le texte en lignes d'au plus maxW pixels
    private List<string> Wrap(string text, float maxW, float scale)
    {
        List<string> res = new List<string>();
        foreach (string para in text.Split('\n'))
        {
            string line = "";
            foreach (string word in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string t = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && _font.Measure(t) * scale > maxW)
                {
                    res.Add(line);
                    line = word;
                }
                else
                    line = t;
            }
            if (line.Length > 0)
                res.Add(line);
        }
        return res;
    }
}
