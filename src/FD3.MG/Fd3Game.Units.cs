using FD3.Formats;
using FD3.Game;
using Microsoft.Xna.Framework;
using SN = System.Numerics;

namespace FD3.MG;

// personnages animés, véhicules et sons
public sealed partial class Fd3Game
{
    private sealed class CharAnim
    {
        public CharacterRig? Rig;
        public Dictionary<string, string> Tags = new(StringComparer.OrdinalIgnoreCase);
        public string Tag = ""; public float Time;
        public SN.Vector3 LastPos; public float MovingFor;
        public Dictionary<CharacterRig.Part, (MeshGpu mesh, float[] pos, float[] nrm)> Skinned = new();
        public SN.Matrix4x4[] World = Array.Empty<SN.Matrix4x4>();
    }
    private readonly Dictionary<object, CharAnim> _chars = new();
    private readonly List<(uint id, string model, SN.Matrix4x4 world)> _scriptShown = new();
    private readonly List<(uint id, string model, SN.Matrix4x4 world, SN.Vector3 origin)> _scriptMoved = new();
    private readonly Dictionary<string, MeshGpu?> _partMeshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CharacterRig?> _rigs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AnimClip?> _clips = new(StringComparer.OrdinalIgnoreCase);
    private float _lastAnimTime = -1;
    private static readonly System.Text.RegularExpressions.Regex ToolRx = new("canon|_axe$|scie|launchfire|bio_detec|grappin|pistolet|NOSHAD", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private CharacterRig? Rig(string model)
    {
        if (_rigs.TryGetValue(model, out var r)) return r;
        try { r = CharacterRig.Build(_assets, _files, model); } catch (Exception e) { Console.WriteLine($"{model} : {e.Message}"); r = null; }
        if (r != null && !r.Animated) r = null;
        return _rigs[model] = r;
    }

    private AnimClip? Clip(string file)
    {
        if (_clips.TryGetValue(file, out var c)) return c;
        try { c = _files.TryRead("Gfx\\Anim\\" + file + ".anim") is { } d ? AnimClip.Parse(d) : null; } catch { c = null; }
        return _clips[file] = c;
    }

    private CharAnim Char(object key, string model, string classRel)
    {
        if (_chars.TryGetValue(key, out var a)) return a;
        a = new CharAnim { Rig = Rig(model) };
        try { a.Tags = CharacterRig.AnimTags(ClassFile.Load(_files, classRel)); } catch { }
        return _chars[key] = a;
    }

    // étiquette d'animation de la classe ([ANIM_PLAYER]) selon ce que fait le personnage
    // « @fichier » : animation donnée directement (à deux personnages : CharChar\cc_lift_body…), jouée une seule fois
    private static string UnitTag(Unit u, bool moving) =>
        u.InjuredTimer > 0 || u.Unconscious ? "FAINT"
        // grappin : lancer, grimper (en boucle), passer le haut du mur, redescendre (en boucle), toucher le sol
        : u.Climb == ClimbPhase.Launch ? "@SingleChar\\sc_grappling_hook_launch"
        : u.Climb == ClimbPhase.Climb ? "~SingleChar\\sc_grappling_hook_climb"
        : u.Climb == ClimbPhase.Stand ? "@SingleChar\\sc_grappling_hook_get_out_top"
        : u.Climb == ClimbPhase.Step ? "WALK"
        : u.Climb == ClimbPhase.Turn ? "WAIT1"
        : u.Climb == ClimbPhase.Top ? "@SingleChar\\sc_grappling_hook_get_on_top"
        : u.Climb == ClimbPhase.Descend ? "~SingleChar\\sc_grappling_hook_descend"
        : u.Climb == ClimbPhase.Land ? "@SingleChar\\sc_grappling_hook_descend_end"
        : u.Carry == CarryPhase.Lifting ? "@CharChar\\cc_lift_body"
        : u.Carry == CarryPhase.Dropping ? "@CharChar\\cc_drop_body"
        : u.Carry == CarryPhase.Walking ? "CARRYWALK"
        : moving ? "RUN"
        : u.Opening != null && u.OpeningBreak ? (u.Opening.IsWindow ? "BREAKWINDOW" : "BREAKDOOR")
        : u.Target != null ? "ATTACKFIRE"
        : u.GoTo != null && u.HealTimer > 0 ? "HEAL"
        : "WAIT1";

    private static string CivTag(Civilian c, bool moving) =>
        c.Carrier is { } cu ? cu.Carry switch { CarryPhase.Lifting => "@CharChar\\cc_lift_body_lifted", CarryPhase.Dropping => "@CharChar\\cc_drop_body_droped", _ => "CARRIEDWALK" }
        : c.State is CivilianState.Unconscious or CivilianState.Dead ? "FAINT"
        : moving ? "WALK" : c.Leader != null ? "WAIT1" : "WAITSHOCKED";

    /// <summary>Dessine un personnage animé ; renvoie false s'il n'a pas de squelette (dessin figé à la place).</summary>
    // vitesse naturelle des animations de déplacement (recul du pied posé au sol, mesuré avec « FD3.Tools foulee »)
    private const float RunAnimSpeed = 520f, WalkAnimSpeed = 300f;

    private bool DrawCharacter(CharAnim a, string tag, SN.Vector3 position, float heading, float dt, bool axe, float rate = 1f)
    {
        if (a.Rig is not { } rig) return false;
        string? file = tag.StartsWith('@') || tag.StartsWith('~') ? tag[1..]   // @ : jouée une fois ; ~ : en boucle
            : a.Tags.TryGetValue(tag, out var f) ? f
            : tag == "RUN" && a.Tags.TryGetValue("WALK", out var w) ? w
            : tag == "HEAL" && a.Tags.TryGetValue("ATTACKFIRE", out var h) ? h
            : a.Tags.TryGetValue("WAIT1", out var i) ? i : null;
        var clip = file != null ? Clip(file) : null;
        if (tag != a.Tag) { a.Tag = tag; a.Time = 0; }
        a.Time += dt * rate;
        float t = clip == null ? 0 : tag == "FAINT" || tag.StartsWith('@') ? MathF.Min(a.Time, clip.Duration) : a.Time % clip.Duration;
        a.World = rig.Pose(clip, t, a.World.Length == rig.Names.Length ? a.World : null);
        var place = SN.Matrix4x4.CreateRotationZ(heading + CharYaw) * SN.Matrix4x4.CreateTranslation(position);
        foreach (var part in rig.Parts)
        {
            string leaf = rig.Names[part.Node];
            // outils du squelette, cachés sauf pendant leur usage : la hache pour casser, la lance (launchfire) pour arroser
            bool nozzle = tag.StartsWith("ATTACKFIRE", StringComparison.OrdinalIgnoreCase) && leaf.Contains("launchfire", StringComparison.OrdinalIgnoreCase);
            if (ToolRx.IsMatch(leaf) && !(axe && leaf.EndsWith("_axe", StringComparison.OrdinalIgnoreCase)) && !nozzle) continue;
            if (part.Skin != null)
            {
                if (!a.Skinned.TryGetValue(part, out var sk))
                {
                    var gm = Upload(_assets.LoadMesh(_mission, part.Mesh, "gfx"), dynamic: true);
                    sk = (gm, new float[part.BasePos.Length], new float[part.BaseNrm.Length]);
                    a.Skinned[part] = sk;
                }
                rig.Deform(part, a.World, sk.pos, sk.nrm);
                sk.mesh.UpdateVertices(sk.pos, sk.nrm);
                DrawMesh(sk.mesh, place.X());
            }
            else
            {
                if (!_partMeshes.TryGetValue(part.Mesh, out var mesh))
                {
                    try { mesh = Upload(_assets.LoadMesh(_mission, part.Mesh, "gfx")); } catch { mesh = null; }
                    _partMeshes[part.Mesh] = mesh;
                }
                if (mesh == null) continue;
                DrawMesh(mesh, (a.World[part.Node] * place).X());
            }
        }
        return true;
    }

    // ---- mouvement fluide : la simulation avance par pas de 0,1 s ; entre deux pas, on dessine les personnages et les
    // véhicules entre leur position du pas précédent et celle du pas actuel (sinon un camion « saute » de 1 m à chaque pas)
    private readonly Dictionary<object, (SN.Vector3 p, float h)> _prevPose = new(ReferenceEqualityComparer.Instance);

    private void SnapshotPoses()
    {
        foreach (var u in _session.Units.Active) _prevPose[u] = (u.Position, u.Heading);
        foreach (var c in _session.Units.Civilians) _prevPose[c] = (c.Position, c.Heading);
    }

    private (SN.Vector3 p, float h) PoseOf(object o, SN.Vector3 p, float h)
    {
        if (!_prevPose.TryGetValue(o, out var prev)) return (p, h);
        float t = _paused ? 1f : (float)Math.Clamp(_owed, 0, 1);
        if (SN.Vector3.DistanceSquared(prev.p, p) > 1000f * 1000f) return (p, h);   // téléporté (arrivée, sortie de véhicule…)
        float dh = MathF.IEEERemainder(h - prev.h, 2 * MathF.PI);
        return (SN.Vector3.Lerp(prev.p, p, t), prev.h + dh * t);
    }

    // orientation affichée des personnages : tourne en douceur vers celle de la simulation (les chemins suivent les cases
    // de la grille et font des zigzags qui feraient pivoter le personnage d'un coup)
    private readonly Dictionary<object, float> _shownHeading = new(ReferenceEqualityComparer.Instance);
    private float SmoothHeading(object o, float h, float dt)
    {
        if (!_shownHeading.TryGetValue(o, out var cur) || dt <= 0) { if (!_shownHeading.ContainsKey(o)) _shownHeading[o] = h; return _shownHeading[o]; }
        float d = MathF.IEEERemainder(h - cur, 2 * MathF.PI);
        cur += d * MathF.Min(1, dt * 9f);
        return _shownHeading[o] = cur;
    }

    // pompiers cachés derrière un mur ou sous un toit : silhouette verte par-dessus le décor (seulement là où ils sont cachés)
    private void RenderGhosts()
    {
        var gd = GraphicsDevice;
        var men = _session.Units.Active.Where(u => !u.IsVehicle).ToList();
        if (men.Count == 0) return;
        var oldDepth = gd.DepthStencilState; var oldBlend = gd.BlendState; var oldRast = gd.RasterizerState;
        gd.DepthStencilState = GhostDepth; gd.BlendState = Microsoft.Xna.Framework.Graphics.BlendState.AlphaBlend; gd.RasterizerState = Microsoft.Xna.Framework.Graphics.RasterizerState.CullNone;
        _ghostPass = true;
        foreach (var u in men)
        {
            var pose = PoseOf(u, u.Position, u.Heading);
            var toEye = _eye - (pose.p + new SN.Vector3(0, 0, 90));
            _ghostShift = (SN.Vector3.Normalize(toEye) * 70f).X();
            var a = Char(u, u.Model, "Design\\Unit\\" + u.ClassPath + ".class");
            bool axe = u.Opening != null && u.OpeningBreak && !u.Moving;
            if (!DrawCharacter(a, a.Tag != "" ? a.Tag : UnitTag(u, u.Moving), pose.p, _shownHeading.TryGetValue(u, out var hh) ? hh : pose.h, 0, axe))
                foreach (var (mesh, part) in UnitModel(u.Model))
                    DrawMesh(mesh, (part * SN.Matrix4x4.CreateRotationZ(pose.h + CharYaw) * SN.Matrix4x4.CreateTranslation(pose.p)).X());
        }
        _ghostPass = false;
        gd.DepthStencilState = oldDepth; gd.BlendState = oldBlend; gd.RasterizerState = oldRast;
    }

    private void RenderUnits()
    {
        // temps continu (entre deux pas de simulation aussi) : l'animation avance à chaque image, pas par bonds de 0,1 s
        float now = _session.Time + (_paused ? 0 : (float)Math.Clamp(_owed, 0, 1) * Session.Dt);
        float dt = _lastAnimTime < 0 ? 0 : Math.Clamp(now - _lastAnimTime, 0, 0.5f);
        if (_paused) dt = 0;
        _lastAnimTime = now;
        var still = new List<Civilian>();
        foreach (var c in _session.Units.Civilians.Where(c => !c.Hidden && c.State != CivilianState.Rescued))
        {
            var a = Char(c, c.Model, c.ClassPath);
            bool moving = SN.Vector3.Distance(a.LastPos, c.Position) > 1f; a.LastPos = c.Position;
            a.MovingFor = moving ? 0.3f : MathF.Max(0, a.MovingFor - dt);
            if (c.Carrier is { } cu)
            {
                // porté : à la place du porteur, avec son cap et sa cadence (les animations des deux vont ensemble)
                var cp = PoseOf(cu, cu.Position, cu.Heading);
                float ch = _shownHeading.TryGetValue(cu, out var sh) ? sh : cp.h;
                float cr = cu.Carry == CarryPhase.Walking ? (cu.Moving ? Math.Clamp(cu.Speed * UnitManager.CarrySpeed / WalkAnimSpeed, 0.5f, 1.6f) : 0f) : 1f;
                if (!DrawCharacter(a, CivTag(c, false), cp.p, ch, dt, false, cr)) still.Add(c);
                continue;
            }
            if (!DrawCharacter(a, CivTag(c, a.MovingFor > 0), PoseOf(c, c.Position, c.Heading).p, SmoothHeading(c, PoseOf(c, c.Position, c.Heading).h, dt), dt, false, 280f / WalkAnimSpeed)) still.Add(c);
        }
        var stillUnits = new List<Unit>();
        foreach (var u in _session.Units.Active.Where(u => !u.IsVehicle))
        {
            var a = Char(u, u.Model, "Design\\Unit\\" + u.ClassPath + ".class");
            bool axe = u.Opening != null && u.OpeningBreak && !u.Moving;
            float rate = u.Moving ? Math.Clamp(u.Speed / RunAnimSpeed, 0.6f, 1.6f) : 1f;
            if (u.Carry == CarryPhase.Walking) rate = u.Moving ? Math.Clamp(u.Speed * UnitManager.CarrySpeed / WalkAnimSpeed, 0.5f, 1.6f) : 0f;   // arrêté avec le corps : figé
            if (!DrawCharacter(a, UnitTag(u, u.Moving), PoseOf(u, u.Position, u.Heading).p, SmoothHeading(u, PoseOf(u, u.Position, u.Heading).h, dt), dt, axe, rate)) stillUnits.Add(u);
        }
        // sans squelette : dessin figé (civils couchés s'ils sont inconscients)
        foreach (var c in still)
            foreach (var (mesh, part) in UnitModel(c.Model))
                DrawMesh(mesh, (part * (c.State == CivilianState.Conscious ? SN.Matrix4x4.Identity
                    : SN.Matrix4x4.CreateRotationY(MathF.PI / 2) * SN.Matrix4x4.CreateTranslation(0, 0, 25))
                    * SN.Matrix4x4.CreateRotationZ(PoseOf(c, c.Position, c.Heading).h + CharYaw) * SN.Matrix4x4.CreateTranslation(PoseOf(c, c.Position, c.Heading).p)).X());
        foreach (var (id, model, world, origin) in _scriptMoved)
        {
            var w = world;
            if (Host?.MovedObjects.TryGetValue(id, out var mv) == true) w.Translation += new SN.Vector3(mv.X - origin.X, mv.Y - origin.Y, 0);
            foreach (var (mesh, part) in UnitModel(model)) DrawMesh(mesh, (part * w).X());
        }
        if (Host is { } hv)
            foreach (var (id, model, world) in _scriptShown)
                if (hv.Visibility.TryGetValue(id, out var vis) && vis)
                    foreach (var (mesh, part) in UnitModel(model)) DrawMesh(mesh, (part * world).X());
        foreach (var u in _session.Units.Active.Where(u => u.IsVehicle || stillUnits.Contains(u)))
        {
            _charred = u.Destroyed;
            foreach (var (mesh, part) in UnitModel(u.Model))
                DrawMesh(mesh, (part * SN.Matrix4x4.CreateRotationZ(PoseOf(u, u.Position, u.Heading).h + (u.IsVehicle ? ModelYaw : CharYaw)) * SN.Matrix4x4.CreateTranslation(PoseOf(u, u.Position, u.Heading).p)).X());
            _charred = false;
        }
    }

    // sons : voix et bruits signalés par le jeu, puis sons entretenus (lances, haches, pas, moteurs, feux proches)
    private void UpdateSound(float dt)
    {
        if (_audio == null) return;
        if (_map) { _audio.Listener = new SN.Vector3(_mapCenter, 0); _audio.ExtraDistance = 3000; }
        else
        {
            _audio.Listener = _target;
            var back = new SN.Vector3(MathF.Cos(_yaw), MathF.Sin(_yaw), 0); _audio.Right = new SN.Vector3(-back.Y, back.X, 0);
            _audio.ExtraDistance = _dist * 0.35f;
        }
        var ev = _session.Units.Sounds;
        foreach (var (label, pos) in ev.Distinct().Take(4)) _audio.Play(label, pos);
        ev.Clear();
        if (!_paused)
        {
            foreach (var u in _session.Units.Active)
            {
                if (u.IsVehicle) { if (u.Moving) _audio.Keep(u, "VEHICLE_1_RUN", u.Position); continue; }
                if (u.Target != null && !u.Moving) _audio.Keep(u, "HOSE_01_01", u.Position);
                else if (u.Opening != null && u.OpeningBreak && !u.Moving) _audio.Keep(u, "SC_BREAK_DOOR", u.Position);
                else if (u.Moving) _audio.Keep(u, "SC_RUN_GENERIC_FIREMAN", u.Position, 0.5f);
            }
            // feux : les 6 plus proches ; étiquette FIRE<type>_<niveau 01..16>_1 selon l'intensité (choix estimé)
            foreach (var h in _session.Fire.HotPoints.Where(h => h.IsBurning)
                .OrderBy(h => SN.Vector2.DistanceSquared(new(h.Position.X, h.Position.Y), new(_audio.Listener.X, _audio.Listener.Y))).Take(6))
            {
                float k = Math.Clamp(h.Intensity / h.Class.IntensityMax, 0, 1);
                int level = 1 + (int)MathF.Min(15, k * 16);
                char type = h.Class.FireType is >= 'A' and <= 'D' ? h.Class.FireType : 'A';
                _audio.Keep(h, $"FIRE{type}_{level:00}_1", h.Position);
            }
        }
        _audio.EndFrame(dt);
    }
}
