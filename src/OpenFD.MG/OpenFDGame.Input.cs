using OpenFD.Fire;
using OpenFD.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SN = System.Numerics;

namespace OpenFD.MG;

// clavier, souris, choix des unités, ordres, curseurs
public sealed partial class OpenFDGame
{
    private KeyboardState _kPrev;
    private MouseState _mPrev;
    // gauche : choisir (clic) ou encadrer (glisser) ; milieu : tourner la vue ; droit : ordre (clic) ou déplacer la vue (glisser)
    private bool _dragL, _dragR, _dragM, _moved;
    private double _mx, _my, _downX, _downY;

    private bool Pressed(KeyboardState k, Keys key) => k.IsKeyDown(key) && !_kPrev.IsKeyDown(key);

    private void HandleInput(float dt)
    {
        KeyboardState k = Keyboard.GetState();
        MouseState m = Mouse.GetState();
        _sel.RemoveWhere(u => u.OnBoard || u.Unconscious);   // pompiers montés dans un véhicule, ou tombés inconscients
        // cinématique : le joueur regarde (Échap, Entrée ou Espace la passent)
        if (CinemaKeys(k))
        {
            _dragL = _dragR = _dragM = _dragMini = false;
            _kPrev = k;
            _mPrev = m;
            return;
        }
        // fin de mission : Entrée rejoue, Échap revient au menu
        if (_ended)
        {
            EndInput(k);
            _dragL = _dragR = _dragM = _dragMini = false;
            _kPrev = k;
            _mPrev = m;
            return;
        }
        // visée du bombardier d'eau : Échap l'annule (sans ouvrir la pause)
        if (_airTargeting && Pressed(k, Keys.Escape))
        {
            AirTargetInput(new KeyboardStateLike(true), false, false, m.X, m.Y);
            _kPrev = k;
            _mPrev = m;
            return;
        }
        if (Pressed(k, Keys.Escape) || Pressed(k, Keys.Space))
        {
            OpenPause();
            _kPrev = k;
            _mPrev = m;
            return;
        }
        if ((Pressed(k, Keys.D1) || Pressed(k, Keys.NumPad1)) && NetCanControlTime)
        {
            _speed = 1;
            _paused = false;
        }
        if ((Pressed(k, Keys.D2) || Pressed(k, Keys.NumPad2)) && NetCanControlTime)
        {
            _speed = 4;
            _paused = false;
        }
        if ((Pressed(k, Keys.D3) || Pressed(k, Keys.NumPad3)) && NetCanControlTime)
        {
            _speed = 16;
            _paused = false;
        }
        if ((Pressed(k, Keys.D4) || Pressed(k, Keys.NumPad4)) && NetCanControlTime)
        {
            _speed = 64;
            _paused = false;
        }
        if (Pressed(k, Keys.Tab))
            _map = !_map;
        if (QuickSaveKeys(k))
        {
            _kPrev = k;
            _mPrev = m;
            return;
        }
        if (Pressed(k, Keys.F1))
            LoadMission(_mission, _difficulty switch { "EASY" => "NORMAL", "NORMAL" => "HARD", _ => "EASY" });   // difficulté suivante
        HandleArrows(k, dt);

        // molette : zoom
        int wheel = m.ScrollWheelValue - _mPrev.ScrollWheelValue;
        if (wheel != 0)
        {
            float dy = wheel / 120f;
            if (_map)
            {
                SN.Vector2 before = ScreenToMap(m.X, m.Y);
                _mapScale *= MathF.Exp(dy * 0.15f);
                SN.Vector2 after = ScreenToMap(m.X, m.Y);
                _mapCenter += before - after;
            }
            else
                _dist = Math.Clamp(_dist * MathF.Exp(-dy * 0.12f), 300, 60000);
        }

        bool inside = m.X >= 0 && m.Y >= 0 && m.X < _winW && m.Y < _winH;
        bool shift = k.IsKeyDown(Keys.LeftShift) || k.IsKeyDown(Keys.RightShift) || k.IsKeyDown(Keys.LeftControl) || k.IsKeyDown(Keys.RightControl);   // Maj ou Ctrl + clic : ajouter à la sélection
        // boutons : appui / relâchement
        bool pressL = inside && m.LeftButton == ButtonState.Pressed && _mPrev.LeftButton == ButtonState.Released;
        bool pressR = inside && m.RightButton == ButtonState.Pressed && _mPrev.RightButton == ButtonState.Released;
        // mini-carte : prend les clics faits sur elle
        if (pressL && SelectionPanelPress(m.X, m.Y))
            pressL = false;
        if (pressL && ObjButtonPress(m.X, m.Y))
            pressL = false;   // bouton « i » : panneau des objectifs
        if (pressL && SenseButtonPress(m.X, m.Y))
            pressL = false;   // bouton « flairer » du maître-chien
        if (pressL && MiniPress(m.X, m.Y, false))
            pressL = false;
        if (pressL && ExitIconHit(m.X, m.Y))
            pressL = false;   // icône « faire descendre » du véhicule choisi
        if (pressR && MiniPress(m.X, m.Y, true))
            pressR = false;
        if (_dragMini)
        {
            if (m.LeftButton == ButtonState.Released)
                _dragMini = false;
            else if (MiniToWorld(m.X, m.Y) is { } mw)
                MiniLook(mw);
        }
        if (pressL)
        {
            _dragL = true;
            _downX = _mx = m.X;
            _downY = _my = m.Y;
            _moved = false;
        }
        if (pressR)
        {
            _dragR = true;
            _downX = _mx = m.X;
            _downY = _my = m.Y;
            _moved = false;
        }
        if (inside && m.MiddleButton == ButtonState.Pressed && _mPrev.MiddleButton == ButtonState.Released)
        {
            _dragM = true;
            _downX = _mx = m.X;
            _downY = _my = m.Y;
            _moved = false;
        }
        if (_dragL || _dragR || _dragM)
        {
            float dx = (float)(m.X - _mx), dyy = (float)(m.Y - _my);
            _mx = m.X;
            _my = m.Y;
            if (Math.Abs(m.X - _downX) + Math.Abs(m.Y - _downY) > 4 * _scale)
                _moved = true;
            if (_moved && !_dragL)
                Drag(dx / _scale, dyy / _scale);
        }
        if (_dragM && m.MiddleButton == ButtonState.Released)
            _dragM = false;
        if (_dragL && m.LeftButton == ButtonState.Released)
        {
            // (clic de visée du bombardier d'eau : pris par AirTargetInput)
            if (_moved)
                SelectBox(_downX, _downY, m.X, m.Y, shift);
            else if (!AirTargetInput(new KeyboardStateLike(false), true, false, m.X, m.Y))
                Select(m.X, m.Y, shift);
            _dragL = false;
        }
        if (_dragR && m.RightButton == ButtonState.Released)
        {
            // (clic droit pendant la visée du bombardier : annule la visée, pas d'ordre)
            if (!_moved && !AirTargetInput(new KeyboardStateLike(false), false, true, m.X, m.Y))
                CommandWithFeedback(m.X, m.Y);
            _dragR = false;
        }
        _kPrev = k;
        _mPrev = m;
    }

    // lettres : reçues comme caractères tapés, donc justes quel que soit le clavier (AZERTY, QWERTY…)
    private void OnChar(char ch)
    {
        if (NetText(ch))
            return;
        if (!IsActive || _session == null || _cineOn)
            return;
        switch (char.ToLowerInvariant(ch))
        {
            case 't':
                _roofs = !_roofs;
                break;
            case 'o':
                _objects = !_objects;
                break;
            case 'f':
                _smokeOn = !_smokeOn;
                break;
            case 'b':
                _shadowsOn = !_shadowsOn;
                Log(_shadowsOn ? "Ombres : oui" : "Ombres : non");
                break;
            case 'h':
                _help = !_help;
                break;
            case 'a':
                _sel.Clear();
                foreach (Unit u in _session.Units.Active.Where(u => !u.IsVehicle && !u.Unconscious))
                    _sel.Add(u);
                break;
            case 'u':
                _unitClass = _unitClass.EndsWith("medic") ? "Unit\\Fireman\\fireman_sp" : "Unit\\Fireman\\fireman_medic";
                Log("Unité qui arrose : " + (_unitClass.EndsWith("medic") ? "médecin" : "pompier"));
                break;
            case 'd':
                _debugAi = !_debugAi;
                Log(_debugAi ? "Débogage : portée des pompiers (vert) et propagation des feux (orange)" : "Débogage : non");
                break;
        }
    }

    // flèches du clavier : déplacent la vue (en 3D, dans la direction où regarde la caméra)
    private float _arrowShift = 1.6f;

    private void HandleArrows(KeyboardState k, float dt)
    {
        float x = (k.IsKeyDown(Keys.Right) ? 1 : 0) - (k.IsKeyDown(Keys.Left) ? 1 : 0);
        // Ctrl + flèche droite : la caméra tourne autour du point visé dans le sens des aiguilles d'une montre ; Ctrl + gauche : en sens inverse
        if (k.IsKeyDown(Keys.LeftControl) || k.IsKeyDown(Keys.RightControl))
        {
            if (!_map)
                _yaw -= x * 1.4f * dt;
            x = 0;
        }
        float y = (k.IsKeyDown(Keys.Up) ? 1 : 0) - (k.IsKeyDown(Keys.Down) ? 1 : 0);
        if (x == 0 && y == 0)
            return;
        // Maj : défilement un peu plus rapide (options : Gameplay.ArrowShiftSpeed)
        if (k.IsKeyDown(Keys.LeftShift) || k.IsKeyDown(Keys.RightShift))
            dt *= _arrowShift;
        if (_map)
        {
            _mapCenter += new SN.Vector2(x, y) * (600 * _scale / _mapScale) * dt;
            return;
        }
        SN.Vector3 back = new SN.Vector3(MathF.Cos(_yaw), MathF.Sin(_yaw), 0);
        SN.Vector3 right = new SN.Vector3(-back.Y, back.X, 0);
        _target += (right * x - back * y) * (_dist * 0.8f) * dt;
    }

    private void Drag(float dx, float dy)
    {
        // axe X : la souris vers la droite fait défiler le terrain vers la gauche (demande du joueur)
        if (_map)
        {
            _mapCenter += new SN.Vector2(dx * _scale / _mapScale, dy * _scale / _mapScale);
            return;
        }
        if (_dragM)
        {
            _yaw += dx * 0.006f * (_settings.InvertX ? -1 : 1);
            _pitch = Math.Clamp(_pitch + dy * 0.006f * (_settings.InvertY ? -1 : 1), 0.08f, 1.5f);
        }
        else
        {
            SN.Vector3 fwd = SN.Vector3.Normalize(new SN.Vector3(MathF.Cos(_yaw), MathF.Sin(_yaw), 0));
            SN.Vector3 right = new SN.Vector3(-fwd.Y, fwd.X, 0);
            float s = _dist * 0.0012f;
            _target += -right * dx * s - fwd * dy * s;   // bouton droit : souris à droite → la vue part à gauche
        }
    }

    // rectangle de sélection (bouton gauche glissé) : toutes les unités et véhicules dedans
    private void SelectBox(double x0, double y0, double x1, double y1, bool add)
    {
        (float ax, float ay) = Fb(Math.Min(x0, x1), Math.Min(y0, y1));
        (float bx, float by) = Fb(Math.Max(x0, x1), Math.Max(y0, y1));
        if (!add)
            _sel.Clear();
        int n = 0;
        foreach (Unit u in _session.Units.Active.Where(u => !u.Unconscious))   // un inconscient ne se choisit pas
            if (Project(u.Position + new SN.Vector3(0, 0, u.IsVehicle ? 150 : 90)) is { } p && p.X >= ax && p.X <= bx && p.Y >= ay && p.Y <= by)
            {
                _sel.Add(u);
                n++;
            }
        if (_sel.Any(u => !u.IsVehicle) && n > 0)
            _audio?.PlayAny(Enumerable.Range(1, 6).Select(i => $"VOCAL_SELECTION{i}"));
    }

    /// <summary>Rectangle en cours de tracé (pour l'affichage), ou null.</summary>
    private (float x, float y, float w, float h)? SelectionRect()
    {
        if (!_dragL || !_moved)
            return null;
        MouseState m = Mouse.GetState();
        (float ax, float ay) = Fb(Math.Min(_downX, m.X), Math.Min(_downY, m.Y));
        (float bx, float by) = Fb(Math.Max(_downX, m.X), Math.Max(_downY, m.Y));
        return (ax, ay, bx - ax, by - ay);
    }

    // position écran (pixels) d'un point du monde
    private SN.Vector2? Project(SN.Vector3 w)
    {
        if (_map)
            return MapToScreen(w.X, w.Y);
        SN.Vector4 c = SN.Vector4.Transform(new SN.Vector4(w, 1), _viewProj);
        if (c.W <= 0)
            return null;
        return new((c.X / c.W * 0.5f + 0.5f) * _fbW, (1 - (c.Y / c.W * 0.5f + 0.5f)) * _fbH);
    }

    private (float sx, float sy) Fb(double x, double y) => ((float)(x * _fbW / Math.Max(1, _winW)), (float)(y * _fbH / Math.Max(1, _winH)));
    private float Tol(float px) => px * _scale;

    // point du sol visé (plan z = 0)
    private SN.Vector2? Ground(double x, double y)
    {
        (float sx, float sy) = Fb(x, y);
        if (_map)
            return ScreenToMap(sx, sy);
        if (!SN.Matrix4x4.Invert(_viewProj, out SN.Matrix4x4 inv))
            return null;
        float nx = sx / _fbW * 2 - 1, ny = 1 - sy / _fbH * 2;
        SN.Vector4 a = SN.Vector4.Transform(new SN.Vector4(nx, ny, 0, 1), inv);
        SN.Vector4 b = SN.Vector4.Transform(new SN.Vector4(nx, ny, 1, 1), inv);
        SN.Vector3 o = new SN.Vector3(a.X, a.Y, a.Z) / a.W;
        SN.Vector3 d = new SN.Vector3(b.X, b.Y, b.Z) / b.W - o;
        if (MathF.Abs(d.Z) < 1e-6f)
            return null;
        // le sol n'est pas toujours à la hauteur 0 (Eurotunnel : -4 m, en sous-sol) : on suit sa hauteur pas à pas
        float z = _session?.Units.GroundZ(new(_target.X, _target.Y)) ?? 0;
        SN.Vector2? hit = null;
        for (int it = 0; it < 4; it++)
        {
            float t = (z - o.Z) / d.Z;
            if (t <= 0)
                return hit;
            hit = new SN.Vector2(o.X + d.X * t, o.Y + d.Y * t);
            float nz = _session?.Units.GroundZ(hit.Value) ?? 0;
            if (MathF.Abs(nz - z) < 1)
                break;
            z = nz;
        }
        return hit;
    }

    private void Select(double x, double y, bool add)
    {
        Unit? best = UnitAt(x, y);
        if (best is { Unconscious: true })
            return;   // pompier ou médecin inconscient : ne se choisit pas (clic droit dessus : le ranimer)
        if (!add)
            _sel.Clear();
        if (best != null)
        {
            if (add && _sel.Contains(best))
                _sel.Remove(best);
            else
                _sel.Add(best);
            if (!best.IsVehicle && _sel.Contains(best))
                _audio?.PlayAny(Enumerable.Range(1, 6).Select(i => $"VOCAL_SELECTION{i}"));
        }
        else if (!add)
            Pick(x, y);
    }

    // clic droit : les unités choisies vont au point visé, ou agissent sur ce qui est visé
    private void Command(double x, double y)
    {
        if (_sel.Count == 0)
            return;
        // collègue inconscient (backdraft) : le pompier choisi le plus proche va le ranimer
        if (UnitAt(x, y) is { Unconscious: true } victim)
        {
            Unit? who = _sel.Where(u => !u.IsVehicle && !u.Unconscious && u != victim).OrderBy(u => SN.Vector3.Distance(u.Position, victim.Position)).FirstOrDefault();
            if (who != null)
            {
                DoRevive(who, victim);
                Log($"{UnitLabel(who)} : {who.Status}");
            }
            return;
        }
        Civilian? civ = CivilianAt(x, y);
        if (civ != null)
        {
            Unit? who = _sel.Where(u => !u.IsVehicle).OrderBy(u => civ.State == CivilianState.Unconscious && !u.CanHeal ? 1 : 0).FirstOrDefault();
            if (who != null)
            {
                DoFetch(who, civ);
                Log($"{UnitLabel(who)} : {who.Status}");
            }
            return;
        }
        Unit? veh = _sel.Any(u => !u.IsVehicle) ? VehicleAt(x, y) : null;
        if (veh != null)
        {
            List<Unit> walkers = _sel.Where(u => !u.IsVehicle && !u.Unconscious).ToList();
            KeyboardState kb = Keyboard.GetState();
            if (kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl))
            {
                // Ctrl + clic droit : monter à bord
                if (veh.Seats <= 0)
                {
                    Log("Ce véhicule n'a pas de place");
                    return;
                }
                foreach (Unit u in walkers)
                    DoBoard(u, veh);
                Log($"{walkers.Count} pompier(s) vont monter à bord ({veh.Seats - veh.Passengers.Count} place(s) libre(s))");
                return;
            }
            // clic droit : aller au véhicule (y déposer les civils qui suivent, s'y recharger en eau)
            foreach (Unit u in walkers)
                DoGoToVehicle(u, veh);
            Log(veh.IsAmbulance && walkers.Any(u => _session.Units.Civilians.Any(c => c.Leader == u)) ? "Direction l'ambulance"
                : veh.ResupplyPower > 0 ? "Direction le camion pour remplir l'eau" : "Direction le véhicule");
            return;
        }
        if (GrappleAt(x, y) is { } gp)
        {
            // flèche jaune du grappin : le scout va au pied du mur et passe de l'autre côté
            Unit? who = _sel.Where(u => u.CanGrapple && !u.Unconscious).OrderBy(u => SN.Vector3.Distance(u.Position, gp.Top)).FirstOrDefault();
            if (who == null)
            {
                Log("Il faut un pompier avec un grappin (scout)");
                _audio?.Play("VOCAL_NO_SKILLS_FOR_THIS_ACTION");
                return;
            }
            DoGrapple(who, gp);
            Log($"{UnitLabel(who)} : {who.Status}");
            return;
        }
        if (UsableAt(x, y) is { } obj)
        {
            // objet utilisable (armoire électrique…) ou à réparer : le technicien d'abord pour une réparation
            Unit? who = _sel.Where(u => _session.Units.CanWorkOn(u, obj)).OrderBy(u => obj.CanBeRepaired && u.CanRepair ? 0 : 1)
                .ThenBy(u => SN.Vector3.Distance(u.Position, obj.Position)).FirstOrDefault();
            if (who == null)
            {
                Log(obj.CanBeRepaired && !obj.CanBeUsed ? "Il faut un technicien pour réparer cela" : "Aucun pompier choisi ne peut utiliser cet objet");
                return;
            }
            DoUse(who, obj);
            Log($"{UnitLabel(who)} : {who.Status}");
            return;
        }
        if (OpeningAt(x, y) is { } door)
        {
            // débris : le bulldozer choisi va les dégager à coups de pelle
            if (door.Demolish && _sel.FirstOrDefault(u => u.CanDemolish && !u.Destroyed) is { } dozer)
            {
                DoOpening(dozer, door);
                Log($"{UnitLabel(dozer)} : {dozer.Status}");
                return;
            }
            // fenêtre d'étage : le camion échelle choisi va dresser son échelle devant ; les pompiers choisis y montent ensuite
            if (_session.Units.IsHighWindow(door) && _sel.FirstOrDefault(u => u.IsAerial && !u.Destroyed) is { } aerial)
            {
                DoLadder(aerial, door);
                Log($"{UnitLabel(aerial)} : {aerial.Status}");
                if (_session.Units.LadderAt(door) == null)
                    return;
            }
            bool wantBreak = door.Breakable && (door.IsWindow || door.State == OpeningState.Closed);
            Unit? who = _sel.Where(u => !u.IsVehicle && (wantBreak && u.CanBreak || door.Usable && u.CanUse))
                .OrderBy(u => wantBreak && u.CanBreak ? 0 : 1).FirstOrDefault() ?? _sel.FirstOrDefault(u => !u.IsVehicle);
            if (who != null)
            {
                DoOpening(who, door);
                Log($"{UnitLabel(who)} : {who.Status}");
            }
            return;
        }
        HotPoint? fire = FireAt(x, y);
        if (fire != null)
        {
            int n = 0;
            // ceux qui arrosent déjà ce feu ne font rien (ni déplacement, ni voix) ; si ce sont tous, l'ordre est ignoré
            List<Unit> already = _sel.Where(u => u.Nozzle != null && u.Target == fire && !u.Moving && u.Path.Count == 0).ToList();
            if (already.Count > 0 && already.Count == _sel.Count(u => u.Nozzle != null))
            {
                _ignoredOrder = true;
                return;
            }
            foreach (Unit u in _sel.Where(u => u.Nozzle != null && !already.Contains(u)))
            {
                DoAttack(u, fire);
                n++;
            }
            Log(n > 0 ? $"{n} unité(s) envoyée(s) sur {fire.Name}" : "Ces unités ne peuvent pas éteindre un feu");
            return;
        }
        if (Ground(x, y) is not { } g)
            return;
        MoveSelection(g);
    }

    // les unités choisies vont au point g (pompiers en cercle autour, véhicules côte à côte)
    private void MoveSelection(SN.Vector2 g)
    {
        int i = 0;
        foreach (Unit u in _sel.Where(u => !u.IsVehicle))
        {
            float a = i * 2.39996f, r = i == 0 ? 0 : 120 + 40 * MathF.Sqrt(i);
            if (!DoOrder(u, g + new SN.Vector2(MathF.Cos(a), MathF.Sin(a)) * r))
                Log($"{UnitLabel(u)} : {u.Status}");
            i++;
        }
        // véhicules : côte à côte, en gardant leur gabarit
        int j = 0;
        foreach (Unit v in _sel.Where(u => u.IsVehicle))
        {
            float a = j * 2.39996f, r = j == 0 ? 0 : 450 * MathF.Sqrt(j);
            DoDrive(v, g + new SN.Vector2(MathF.Cos(a), MathF.Sin(a)) * r);
            j++;
            if (v.Status != "en route")
                Log($"{UnitLabel(v)} : {v.Status}");
        }
    }

    // civil visé : sur toute sa hauteur (de ses pieds au repère coloré au-dessus de lui), ou au sol tout près
    private Civilian? CivilianAt(double x, double y)
    {
        (float csx, float csy) = Fb(x, y);
        Civilian? civ = null;
        SN.Vector2 mouse = new SN.Vector2(csx, csy);
        float cd = Tol(30) * 1.4f;
        foreach (Civilian c in _session.Units.Civilians.Where(c => !c.Hidden && (c.State == CivilianState.Conscious || c.State == CivilianState.Unconscious)))
        {
            if (Project(c.Position) is not { } a || Project(c.Position + new SN.Vector3(0, 0, 240)) is not { } b)
                continue;
            SN.Vector2 ab = b - a;
            float t = ab.LengthSquared() > 1e-3f ? Math.Clamp(SN.Vector2.Dot(mouse - a, ab) / ab.LengthSquared(), 0, 1) : 0;
            if (SN.Vector2.Distance(mouse, a + ab * t) is float d && d < cd)
            {
                cd = d;
                civ = c;
            }
        }
        if (civ == null && Ground(x, y) is { } gp)
        {
            float gd = 180;
            foreach (Civilian c in _session.Units.Civilians.Where(c => !c.Hidden && (c.State == CivilianState.Conscious || c.State == CivilianState.Unconscious)))
                if (SN.Vector2.Distance(new(c.Position.X, c.Position.Y), gp) is float d && d < gd)
                {
                    gd = d;
                    civ = c;
                }
        }
        return civ;
    }

    // véhicule visé : n'importe où sur sa carrosserie (le rayon de la souris traverse son gabarit entre le sol et le toit)
    private Unit? VehicleAt(double x, double y)
    {
        (float csx, float csy) = Fb(x, y);
        float vd = Tol(48);
        Unit? veh = null;
        foreach (Unit v in _session.Units.Active.Where(v => v.IsVehicle))
            if (Project(v.Position + new SN.Vector3(0, 0, 150)) is { } p && SN.Vector2.Distance(p, new(csx, csy)) is float d && d < vd)
            {
                vd = d;
                veh = v;
            }
        if (veh != null)
            return veh;
        float best = float.MaxValue;
        foreach (Unit v in _session.Units.Active.Where(v => v.IsVehicle))
            foreach (float z in new[] { 300f, 200f, 100f, 0f })
                if (RayAtZ(x, y, v.Position.Z + z) is { } g && _session.Units.VehicleContains(v, g, 30))
                {
                    float d = SN.Vector2.Distance(g, new(v.Position.X, v.Position.Y));
                    if (d < best)
                    {
                        best = d;
                        veh = v;
                    }
                    break;
                }
        return veh;
    }

    // point visé sur le plan horizontal de hauteur z
    private SN.Vector2? RayAtZ(double x, double y, float z)
    {
        (float sx, float sy) = Fb(x, y);
        if (_map)
            return ScreenToMap(sx, sy);
        if (!SN.Matrix4x4.Invert(_viewProj, out SN.Matrix4x4 inv))
            return null;
        float nx = sx / _fbW * 2 - 1, ny = 1 - sy / _fbH * 2;
        SN.Vector4 a = SN.Vector4.Transform(new SN.Vector4(nx, ny, 0, 1), inv);
        SN.Vector4 b = SN.Vector4.Transform(new SN.Vector4(nx, ny, 1, 1), inv);
        SN.Vector3 o = new SN.Vector3(a.X, a.Y, a.Z) / a.W;
        SN.Vector3 d = new SN.Vector3(b.X, b.Y, b.Z) / b.W - o;
        if (MathF.Abs(d.Z) < 1e-6f)
            return null;
        float t = (z - o.Z) / d.Z;
        return t > 0 ? new SN.Vector2(o.X + d.X * t, o.Y + d.Y * t) : null;
    }

    private Unit? UnitAt(double x, double y)
    {
        (float sx, float sy) = Fb(x, y);
        Unit? best = null;
        float bd = Tol(28);
        foreach (Unit u in _session.Units.Active)
            // allongé (inconscient) : visé au sol, là où il est couché
            if (Project(u.Position + new SN.Vector3(0, 0, u.IsVehicle ? 150 : u.Unconscious ? 25 : 90)) is { } p && SN.Vector2.Distance(p, new(sx, sy)) is float d && d < bd * (u.Unconscious ? 1.4f : 1f))
            {
                bd = d;
                best = u;
            }
        // pas de pompier tout près du clic : un véhicule, sur toute sa carrosserie
        return best ?? VehicleAt(x, y);
    }

    // porte ou fenêtre visée : sur la hauteur du battant, ou au sol tout près (portes du rez-de-chaussée)
    // objet utilisable sous la souris : près de son pied ou de sa hauteur d'homme à l'écran
    private UsableObject? UsableAt(double x, double y)
    {
        (float sx, float sy) = Fb(x, y);
        SN.Vector2 m = new SN.Vector2(sx, sy);
        // objets sous la souris : près du pied de leur repère (pivot), ou souris sur l'emprise du modèle (le pivot peut être
        // à un bout de l'objet : générateur de Mriya) ; rang : 0 près du pivot, puis la taille de l'emprise
        List<(UsableObject o, float rank)> hits = new List<(UsableObject o, float rank)>();
        float tol = Tol(30);
        foreach (UsableObject o in _session.Units.Usables.Where(o => o.Enabled))
        {
            if (Project(o.Position) is not { } a || Project(o.Position + new SN.Vector3(0, 0, 160)) is not { } b)
                continue;
            SN.Vector2 ab = b - a;
            float t = ab.LengthSquared() > 1e-3f ? Math.Clamp(SN.Vector2.Dot(m - a, ab) / ab.LengthSquared(), 0, 1) : 0;
            float d = SN.Vector2.Distance(m, a + ab * t);
            if (d < tol)
                hits.Add((o, d / tol));
        }
        // (pas sur un feu : le feu posé sur le générateur de Mriya doit pouvoir être arrosé avant la réparation)
        if (hits.Count == 0 && FireAt(x, y) != null)
            return null;
        if (Ground(x, y) is { } gp)
            foreach (UsableObject o in _session.Units.Usables.Where(o => o.Enabled && Inside(gp, o.BoxMin, o.BoxMax, 40) && o.BoxMax.X - o.BoxMin.X < 3000 && o.BoxMax.Y - o.BoxMin.Y < 3000))
                if (!hits.Any(h => h.o == o))
                    hits.Add((o, 1 + (o.BoxMax.X - o.BoxMin.X) * (o.BoxMax.Y - o.BoxMin.Y) / 1e7f));
        if (hits.Count <= 1)
            return hits.Count == 1 ? hits[0].o : null;
        // plusieurs objets (Mriya : le calendrier posé sur le transformateur du hangar) : d'abord celui que les pompiers
        // choisis peuvent travailler, une réparation en premier si un technicien est choisi
        List<Unit> walkers = _sel.Where(u => !u.IsVehicle).ToList();
        bool repairer = walkers.Any(u => u.CanRepair);
        int Pref(UsableObject o) => repairer && o.CanBeRepaired && !o.Repaired ? 0 : walkers.Any(u => _session.Units.CanWorkOn(u, o)) ? 1 : 2;
        return hits.OrderBy(h => Pref(h.o)).ThenBy(h => h.rank).First().o;
    }

    // flèche du grappin visée : près du repère en haut du mur, ou de sa base
    private GrapplePoint? GrappleAt(double x, double y)
    {
        (float sx, float sy) = Fb(x, y);
        SN.Vector2 m = new SN.Vector2(sx, sy);
        GrapplePoint? best = null;
        float bd = Tol(34);
        foreach (GrapplePoint gp in _session.Units.Grapples)
        {
            if (Project(gp.Top + new SN.Vector3(0, 0, 60)) is not { } a || Project(gp.Top - new SN.Vector3(0, 0, 120)) is not { } b)
                continue;
            SN.Vector2 ab = b - a;
            float t = ab.LengthSquared() > 1e-3f ? Math.Clamp(SN.Vector2.Dot(m - a, ab) / ab.LengthSquared(), 0, 1) : 0;
            if (SN.Vector2.Distance(m, a + ab * t) is float d && d < bd)
            {
                bd = d;
                best = gp;
            }
        }
        return best;
    }

    private Opening? OpeningAt(double x, double y)
    {
        (float sx, float sy) = Fb(x, y);
        SN.Vector2 m = new SN.Vector2(sx, sy);
        Opening? best = null;
        float bd = Tol(26);
        foreach (Opening o in _session.Units.Openings.Where(o => (o.State != OpeningState.Broken || _session.Units.LadderAt(o) != null) && (o.OnGround || o.IsWindow)))
        {
            if (Project(o.Center - new SN.Vector3(0, 0, 90)) is not { } a || Project(o.Center + new SN.Vector3(0, 0, 90)) is not { } b)
                continue;
            SN.Vector2 ab = b - a;
            float t = ab.LengthSquared() > 1e-3f ? Math.Clamp(SN.Vector2.Dot(m - a, ab) / ab.LengthSquared(), 0, 1) : 0;
            if (SN.Vector2.Distance(m, a + ab * t) is float d && d < bd)
            {
                bd = d;
                best = o;
            }
        }
        // portail à mécanisme (Jail) : un petit cercle au sol, sous les rouages
        foreach (Opening o in _session.Units.Openings.Where(o => o.Lever != null && o.State != OpeningState.Broken))
        {
            SN.Vector2 lv = o.Lever!.Value;
            SN.Vector3 lp = new SN.Vector3(lv, _session.Units.GroundZ(lv) + 10);
            if (Project(lp) is not { } c || Project(lp + new SN.Vector3(110, 0, 0)) is not { } e)
                continue;
            float rr = MathF.Max(Tol(14), SN.Vector2.Distance(c, e));
            if (SN.Vector2.Distance(c, m) < rr)
                return o;
        }
        // grand portail sans mécanisme : tout le modèle est cliquable, du sol jusqu'à 3 m
        if (best == null)
            foreach (Opening o in _session.Units.Openings.Where(o => o.Lever == null && o.OnGround && !o.IsWindow && o.State != OpeningState.Broken && o.BoxMax.X - o.BoxMin.X + o.BoxMax.Y - o.BoxMin.Y > 400))
            {
                float sx0 = float.MaxValue, sy0 = float.MaxValue, sx1 = float.MinValue, sy1 = float.MinValue;
                foreach (float cx in new[] { o.BoxMin.X, o.BoxMax.X })
                    foreach (float cy in new[] { o.BoxMin.Y, o.BoxMax.Y })
                        foreach (float cz in new[] { o.Center.Z - 100, o.Center.Z + 200 })
                            if (Project(new SN.Vector3(cx, cy, cz)) is { } q)
                            {
                                sx0 = MathF.Min(sx0, q.X);
                                sy0 = MathF.Min(sy0, q.Y);
                                sx1 = MathF.Max(sx1, q.X);
                                sy1 = MathF.Max(sy1, q.Y);
                            }
                if (m.X >= sx0 && m.X <= sx1 && m.Y >= sy0 && m.Y <= sy1)
                {
                    best = o;
                    break;
                }
            }
        if (best == null && Ground(x, y) is { } g)
        {
            float gd = 90;
            foreach (Opening o in _session.Units.Openings.Where(o => o.OnGround && !o.IsWindow && o.State != OpeningState.Broken))
                if (SN.Vector2.Distance(new(o.Center.X, o.Center.Y), g) is float d && d < gd)
                {
                    gd = d;
                    best = o;
                }
        }
        return best;
    }

    // feu visé : on vise n'importe où sur la hauteur des flammes, ou au sol tout près du feu
    private HotPoint? FireAt(double x, double y)
    {
        (float sx, float sy) = Fb(x, y);
        SN.Vector2 m = new SN.Vector2(sx, sy);
        HotPoint? best = null;
        float bd = Tol(45);
        foreach (HotPoint h in _session.Fire.HotPoints.Where(h => h.IsBurning))
        {
            float k = Math.Min(1, h.Intensity / h.Class.IntensityMax), top = 120 + 380 * k;
            if (Project(h.Position) is not { } a || Project(h.Position + new SN.Vector3(0, 0, top)) is not { } b)
                continue;
            SN.Vector2 ab = b - a;
            float t = ab.LengthSquared() > 1e-3f ? Math.Clamp(SN.Vector2.Dot(m - a, ab) / ab.LengthSquared(), 0, 1) : 0;
            float d = SN.Vector2.Distance(m, a + ab * t);
            if (d < bd)
            {
                bd = d;
                best = h;
            }
        }
        if (best == null && Ground(x, y) is { } g)
        {
            float gd = 250;
            foreach (HotPoint h in _session.Fire.HotPoints.Where(h => h.IsBurning))
                if (SN.Vector2.Distance(new(h.Position.X, h.Position.Y), g) is float d && d < gd)
                {
                    gd = d;
                    best = h;
                }
        }
        return best;
    }

    // clic sans unité choisie : l'unité d'appoint (U) arrose le feu le plus proche du point visé
    private void Pick(double x, double y)
    {
        (float sx, float sy) = Fb(x, y);
        HotPoint? best = null;
        float bd = float.MaxValue;
        if (_map)
        {
            SN.Vector2 m = ScreenToMap(sx, sy);
            foreach (HotPoint h in _session.Fire.HotPoints.Where(h => h.IsBurning))
            {
                float d = SN.Vector2.Distance(new(h.Position.X, h.Position.Y), m);
                if (d < bd)
                {
                    bd = d;
                    best = h;
                }
            }
            if (bd > Tol(20) / _mapScale)
                best = null;
        }
        else
        {
            if (!SN.Matrix4x4.Invert(_viewProj, out SN.Matrix4x4 inv))
                return;
            float nx = sx / _fbW * 2 - 1, ny = 1 - sy / _fbH * 2;
            SN.Vector4 a = SN.Vector4.Transform(new SN.Vector4(nx, ny, 0, 1), inv);
            SN.Vector4 b = SN.Vector4.Transform(new SN.Vector4(nx, ny, 1, 1), inv);
            SN.Vector3 o = new SN.Vector3(a.X, a.Y, a.Z) / a.W;
            SN.Vector3 dir = SN.Vector3.Normalize(new SN.Vector3(b.X, b.Y, b.Z) / b.W - o);
            foreach (HotPoint h in _session.Fire.HotPoints.Where(h => h.IsBurning))
            {
                SN.Vector3 v = h.Position + new SN.Vector3(0, 0, 100) - o;
                float t = SN.Vector3.Dot(v, dir);
                if (t < 0)
                    continue;
                float d = (v - dir * t).Length();
                if (d < bd)
                {
                    bd = d;
                    best = h;
                }
            }
            if (bd > 500)
                best = null;
        }
        if (best == null)
            return;
        DoSpray(best, _unitClass);
        Log($"Arrosage de {best.Name} ({(_unitClass.EndsWith("medic") ? "médecin" : "pompier")})");
    }

    // ---- curseurs du jeu (Gfx\Textures\_cache\MouseCursors.dds, cases de 32 × 32, 8 par ligne) ----
    private enum Cur { Grapple = 12, Normal = 0, Unit = 1, Use = 2, Heal = 3, Axe = 4, Move = 5, Target = 8, Rescue = 3, Forbidden = 13, Enter = 14, Water = 15, Ladder = 11, Bulldozer = 20 }
    private readonly Dictionary<Cur, MouseCursor> _cursors = new();
    private readonly List<Texture2D> _cursorTex = new();
    private Cur _cur = (Cur)(-1);
    private float _cursorScale;

    // taille des curseurs suivant l'échelle d'affichage de l'écran (200 % → 64 × 64 pixels)
    private void LoadCursors()
    {
        (int w, int h, byte[] rgba)? img = _files.TryRead("Gfx\\Textures\\_cache\\MouseCursors.dds") is { } d ? Tex.DecodeDds32(d) : null;
        if (img is not { } im)
            return;
        float scale = _scale;
        _cursorScale = scale;
        int size = Math.Max(16, (int)MathF.Round(32 * scale));
        foreach (MouseCursor c in _cursors.Values)
            c.Dispose();
        foreach (Texture2D t in _cursorTex)
            t.Dispose();
        _cursors.Clear();
        _cursorTex.Clear();
        _cur = (Cur)(-1);
        foreach (Cur c in Enum.GetValues<Cur>())
        {
            int cell = (int)c, cx = cell % 8 * 32, cy = cell / 8 * 32;
            Color[] px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int sxp = Math.Min(31, (int)(x / scale)), syp = Math.Min(31, (int)(y / scale)), o = ((cy + syp) * im.w + cx + sxp) * 4;
                    px[y * size + x] = new Color(im.rgba[o], im.rgba[o + 1], im.rgba[o + 2], im.rgba[o + 3]);
                }
            try
            {
                Texture2D tex = new Texture2D(GraphicsDevice, size, size);
                tex.SetData(px);
                _cursorTex.Add(tex);
                _cursors[c] = MouseCursor.FromTexture2D(tex, 0, 0);
            }
            catch (Exception e) { Console.WriteLine("Curseur : " + e.Message); }
        }
    }

    // ce que ferait un clic droit à cet endroit, d'après les unités choisies
    /// <summary>Menus, pause : le pointeur normal du système (le curseur du jeu revient en partie).</summary>
    private void MenuCursor()
    {
        if (_arrowShown)
            return;
        _arrowShown = true;
        _cur = (Cur)(-1);
        try { Mouse.SetCursor(MouseCursor.Arrow); } catch { }
    }
    private bool _arrowShown;

    /// <summary>
    /// Échelle de l'écran (100 %, 150 %…) : relue régulièrement, au menu comme en jeu. Au lancement, Windows peut donner
    /// 100 % tant que la fenêtre n'est pas encore affichée ; la bonne valeur arrive quelques images plus tard.
    /// </summary>
    private void RefreshScale()
    {
        float s = Display.Scale(Window.Handle);
        if (MathF.Abs(s - _scale) <= 0.01f && MathF.Abs(s - _cursorScale) <= 0.01f)
            return;
        _scale = s;
        _ui.Scale = s;
        LoadCursors();
    }

    private void UpdateCursor()
    {
        _arrowShown = false;
        if (_cursors.Count == 0 || _session == null)
            return;
        MouseState ms = Mouse.GetState();
        double x = ms.X, y = ms.Y;
        List<Unit> walkers = _sel.Where(u => !u.IsVehicle).ToList();
        Cur want = Cur.Normal;
        walkers = walkers.Where(u => !u.Unconscious).ToList();
        if (MiniToWorld(x, y) != null)
            want = Cur.Normal;
        else if (_sel.Any(u => u.CanDemolish && !u.Destroyed) && OpeningAt(x, y) is { Demolish: true })
            want = Cur.Bulldozer;   // débris : le bulldozer les dégage
        else if (_sel.Any(u => u.IsAerial && !u.Destroyed) && OpeningAt(x, y) is { } hw && _session.Units.IsHighWindow(hw))
            want = Cur.Ladder;      // fenêtre d'étage : dresser l'échelle
        else if (walkers.Count > 0 && Ground(x, y) is { } stairAt && _session.Units.TeleportEndNear(stairAt))
            want = Cur.Enter;       // icône d'escalier : y entrer (il ressort par l'autre)
        else if (walkers.Count == 0)
            want = UnitAt(x, y) != null ? Cur.Unit : Cur.Normal;
        else if (GrappleAt(x, y) != null)
            want = walkers.Any(u => u.CanGrapple) ? Cur.Grapple : Cur.Forbidden;
        else if (UnitAt(x, y) is { Unconscious: true })
            want = Cur.Heal;   // pompier ou médecin inconscient : le ranimer
        else if (CivilianAt(x, y) is { } civ)
            want = civ.State == CivilianState.Unconscious && walkers.Any(u => u.CanHeal) ? Cur.Heal : Cur.Rescue;   // inconscient : le médecin le soigne, un pompier le porte
        else if (VehicleAt(x, y) is { } v)
            want = (ms.X >= 0 && (Keyboard.GetState().IsKeyDown(Keys.LeftControl) || Keyboard.GetState().IsKeyDown(Keys.RightControl))) ? (v.Seats > 0 ? Cur.Enter : Cur.Forbidden)
                : v.IsAmbulance ? Cur.Heal   // ambulance : soins (Ctrl : y monter)
                : v.ResupplyPower > 0 && walkers.Any(u => u.Nozzle != null) ? Cur.Water : Cur.Move;
        else if (UsableAt(x, y) is { } uo)
            want = walkers.Any(u => _session.Units.CanWorkOn(u, uo)) ? Cur.Use : Cur.Forbidden;
        else if (OpeningAt(x, y) is { } o)
        {
            bool brk = o.Breakable && o.State != OpeningState.Broken && (o.IsWindow || o.State == OpeningState.Closed);
            bool ladder = _session.Units.LadderAt(o) != null && walkers.Any(u => u.CanLadder);
            want = o.Demolish ? Cur.Forbidden   // débris : seul le bulldozer
                : brk && walkers.Any(u => u.CanBreak) ? Cur.Axe
                : ladder ? Cur.Ladder           // échelle dressée devant la fenêtre ouverte : y monter
                : o.Usable && walkers.Any(u => u.CanUse) ? Cur.Use : Cur.Forbidden;
        }
        else if (FireAt(x, y) != null)
            want = walkers.Any(u => u.Nozzle != null) ? Cur.Target : Cur.Forbidden;
        else if (UnitAt(x, y) != null)
            want = Cur.Unit;
        if (want == _cur)
            return;
        _cur = want;
        try { Mouse.SetCursor(_cursors.TryGetValue(want, out MouseCursor? c) ? c : MouseCursor.Arrow); } catch { }
    }
}
