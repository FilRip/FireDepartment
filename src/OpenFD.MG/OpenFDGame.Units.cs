using OpenFD.Formats;
using OpenFD.Game;
using Microsoft.Xna.Framework;
using SN = System.Numerics;

namespace OpenFD.MG;

// personnages animés, véhicules et sons
public sealed partial class OpenFDGame
{
    private sealed class CharAnim
    {
        public CharacterRig? Rig;
        public Dictionary<string, string> Tags = new(StringComparer.OrdinalIgnoreCase);
        public string Tag = "";
        public float Time;
        public SN.Vector3 LastPos;
        public float MovingFor;
        public bool Deployed;   // pompier « canon » : lance posée (animation DEPLOY jouée), il attend accroupi
        public Dictionary<CharacterRig.Part, (MeshGpu mesh, float[] pos, float[] nrm)> Skinned = new();
        public SN.Matrix4x4[] World = Array.Empty<SN.Matrix4x4>();
    }
    private readonly Dictionary<object, CharAnim> _chars = new();
    private readonly List<(uint id, string model, SN.Matrix4x4 world)> _scriptShown = new();
    private readonly List<(uint id, string model, SN.Matrix4x4 world, SN.Vector3 origin, float yaw)> _scriptMoved = new();
    // battants de portail que le script fait glisser (Factory) : ils restent à leur hauteur ; le sol de la carte, à côté,
    // est le haut du mur ou du bâtiment où ils se rangent, et ils y monteraient
    private readonly HashSet<uint> _slideFlat = new();

    // objets déplacés par le script (ENT_SetPosition : l'avion et le camion de Mriya avancent de 80 unités à chaque tour
    // de la boucle principale) : on les fait glisser d'une position à la suivante pendant un tour, au lieu de sauter
    private readonly Dictionary<uint, (SN.Vector2 from, SN.Vector2 to, float t0)> _glide = new();

    /// <summary>Position du moment (glissée) d'un objet que le script a déplacé ; null s'il n'a pas bougé.</summary>
    private SN.Vector2? ScriptPos(uint id)
    {
        if (Host?.MovedObjects.TryGetValue(id, out SN.Vector2 target) != true)
            return null;
        float now = (float)(_session.Time + (_paused ? 0 : _owed * Session.Dt));
        static SN.Vector2 At((SN.Vector2 from, SN.Vector2 to, float t0) g, float t) =>
            SN.Vector2.Lerp(g.from, g.to, Math.Clamp((t - g.t0) / Session.MainLoopPeriod, 0, 1));
        if (!_glide.TryGetValue(id, out (SN.Vector2 from, SN.Vector2 to, float t0) g))
            _glide[id] = g = (target, target, now);
        else if (g.to != target)
        {
            SN.Vector2 cur = At(g, now);
            // grand saut (téléportation par le script) : pas de glissade
            _glide[id] = g = (SN.Vector2.Distance(cur, target) > 1500 ? target : cur, target, now);
        }
        return At(g, now);
    }
    /// <summary>
    /// Objet que le script fait avancer (ENT_SetPosition ne donne que x et y) : il suit le relief (la grille de la carte,
    /// lissée), en montant ou en descendant de la même hauteur que le sol, et penche dans le sens de la pente. Street (FD2) :
    /// les tramways montent et redescendent la colline au lieu de passer sous la rue.
    /// </summary>
    private SN.Matrix4x4 FollowGround(uint id, SN.Matrix4x4 world, SN.Vector3 origin, SN.Vector2 at)
    {
        SN.Vector2 o2 = new SN.Vector2(origin.X, origin.Y);
        SN.Vector3 to = new SN.Vector3(at, origin.Z + GridZ(at) - GridZ(o2));
        SN.Matrix4x4 tilt = SN.Matrix4x4.Identity;
        if (_glide.TryGetValue(id, out (SN.Vector2 from, SN.Vector2 to, float t0) g) && SN.Vector2.Distance(g.from, g.to) > 1)
        {
            SN.Vector2 dir = SN.Vector2.Normalize(g.to - g.from);
            float rise = GridZ(at + dir * SlopeReach) - GridZ(at - dir * SlopeReach);
            float ang = MathF.Atan2(rise, 2 * SlopeReach);
            // l'avant se lève quand ça monte : rotation autour de l'axe horizontal perpendiculaire à la marche
            if (MathF.Abs(ang) > 0.005f)
                tilt = SN.Matrix4x4.CreateFromAxisAngle(new SN.Vector3(dir.Y, -dir.X, 0), ang);
        }
        return world * SN.Matrix4x4.CreateTranslation(-origin) * tilt * SN.Matrix4x4.CreateTranslation(to);
    }

    // demi-longueur sur laquelle on mesure la pente sous un objet déplacé par le script
    private const float SlopeReach = 300f;

    /// <summary>Hauteur de la grille de la carte en un point, interpolée entre les centres des cases (sans marches).</summary>
    private float GridZ(SN.Vector2 p)
    {
        if (_session?.Grid is not { } g)
            return 0;
        float fx = p.X / Grid.CellSize - 0.5f, fy = p.Y / Grid.CellSize - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float tx = fx - x0, ty = fy - y0;
        float H(int x, int y) => g.GroundHeight[g.Index(Math.Clamp(x, 0, g.Width - 1), Math.Clamp(y, 0, g.Height - 1))];
        return (H(x0, y0) * (1 - tx) + H(x0 + 1, y0) * tx) * (1 - ty) + (H(x0, y0 + 1) * (1 - tx) + H(x0 + 1, y0 + 1) * tx) * ty;
    }

    private readonly Dictionary<string, MeshGpu?> _partMeshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CharacterRig?> _rigs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AnimClip?> _clips = new(StringComparer.OrdinalIgnoreCase);
    private float _lastAnimTime = -1;
    private static readonly System.Text.RegularExpressions.Regex ToolRx = new("canon|_axe$|scie|launchfire|bio_detec|grappin|pistolet|NOSHAD", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private CharacterRig? Rig(string model)
    {
        if (_rigs.TryGetValue(model, out CharacterRig? r))
            return r;
        try { r = CharacterRig.Build(_assets, _files, model); } catch (Exception e)
        {
            Console.WriteLine($"{model} : {e.Message}");
            r = null;
        }
        if (r != null && !r.Animated)
            r = null;
        return _rigs[model] = r;
    }

    private AnimClip? Clip(string file)
    {
        if (_clips.TryGetValue(file, out AnimClip? c))
            return c;
        // « Gfx\… » : chemin complet (animation d'un modèle posé) ; « ..\Placeable\… » : relatif au dossier des animations
        string path = file.StartsWith("Gfx\\", StringComparison.OrdinalIgnoreCase) ? file : SceneAssets.GfxPath("Gfx\\Anim", file);
        try { c = _files.TryRead(path + ".anim") is { } d ? AnimClip.Parse(d) : null; } catch { c = null; }
        return _clips[file] = c;
    }

    private CharAnim Char(object key, string model, string classRel)
    {
        if (_chars.TryGetValue(key, out CharAnim? a))
            return a;
        a = new CharAnim { Rig = Rig(model) };
        try { a.Tags = CharacterRig.AnimTags(ClassFile.Load(_files, classRel)); } catch { }
        return _chars[key] = a;
    }

    // étiquette d'animation de la classe ([ANIM_PLAYER]) selon ce que fait le personnage
    // « @fichier » : animation donnée directement (à deux personnages : CharChar\cc_lift_body…), jouée une seule fois
    // dans un escalier « téléporteur » : caché jusqu'à ce qu'il ressorte ; dans un escalier extérieur (Walk) : visible
    private static bool InStairs(Unit u) => u.Stairs is { Walk: false };

    private static string UnitTag(Unit u, bool moving) =>
        // échelle verticale fixe (Forest) : grimper, passer sur le plancher du haut ; enjamber le haut, descendre
        u.Vertical == VerticalPhase.Climb ? "~SingleChar\\sc_vertical_ladder_climb"
        : u.Vertical == VerticalPhase.Out ? "@SingleChar\\sc_vertical_ladder_get_out_top"
        : u.Vertical == VerticalPhase.On ? "@SingleChar\\sc_vertical_ladder_get_on_top"
        : u.Vertical == VerticalPhase.Descend ? "~SingleChar\\sc_vertical_ladder_descend"
        : u.Stairs is { Walk: true } ? "~SingleChar\\sc_walk_generic_stair"   // escalier extérieur : il monte ou descend les marches
        : u.InjuredTimer > 0 || u.Unconscious ? "FAINT"
        : u.SenseTimer > 0 && !moving ? "SENSE_VICTIM"   // chien : aboie (sc_bark)
        // grappin : lancer, grimper (en boucle), passer le haut du mur, redescendre (en boucle), toucher le sol
        : u.Climb == ClimbPhase.Launch ? "@SingleChar\\sc_grappling_hook_launch"
        : u.Climb == ClimbPhase.Climb ? "~SingleChar\\sc_grappling_hook_climb"
        : u.Climb == ClimbPhase.Stand ? "@SingleChar\\sc_grappling_hook_get_out_top"
        : u.Climb == ClimbPhase.Step ? "WALK"
        : u.Climb == ClimbPhase.Turn ? "WAIT1"
        : u.Climb == ClimbPhase.Top ? "@SingleChar\\sc_grappling_hook_get_on_top"
        : u.Climb == ClimbPhase.Descend ? "~SingleChar\\sc_grappling_hook_descend"
        : u.Climb == ClimbPhase.Land ? "@SingleChar\\sc_grappling_hook_descend_end"
        // échelle du camion échelle : grimper, passer la fenêtre, casser la vitre depuis le haut ; échelle presque à plat : marcher
        : u.OnLadder is { } ol ? LadderTag(u, ol)
        : u.Carry == CarryPhase.Lifting ? "@CharChar\\cc_lift_body"
        : u.Carry == CarryPhase.Dropping ? "@CharChar\\cc_drop_body"
        : u.Carry == CarryPhase.Walking ? "CARRYWALK"
        : moving ? "RUN"
        // désincarcérateur : il met sa pince en place (sc_extricate_init) puis coupe en boucle (EXTRICATE)
        : u.Opening is { Extricate: true } && u.OpeningBreak ? (u.ActionTimer < UnitManager.ExtricateStart ? "@SingleChar\\sc_extricate_init" : "EXTRICATE")
        : u.Opening != null && u.OpeningBreak ? (u.Opening.IsWindow ? "BREAKWINDOW" : "BREAKDOOR")
        : u.Target != null ? "ATTACKFIRE"
        : u.GoTo != null && u.HealTimer > 0 ? "HEAL"
        : "WAIT1";

    private static string LadderTag(Unit u, Ladder l) => LadderStepTag(u.LadderStep, l, u.Opening != null && u.OpeningBreak);

    // les mêmes animations pour un civil qui suit son guide sur l'échelle (SingleChar : même squelette)
    private static string LadderStepTag(LadderPhase step, Ladder l, bool breaking)
    {
        bool steep = l.Pitch > 0.6f;
        return step switch
        {
            LadderPhase.Mount => "@SingleChar\\sc_ladder_get_on_top",
            LadderPhase.Up => steep ? "~SingleChar\\sc_ladder_climb" : "WALK",
            LadderPhase.Down => steep ? "~SingleChar\\sc_ladder_descend" : "WALK",
            LadderPhase.Top when breaking => "~SingleChar\\sc_ladder_break_window",
            LadderPhase.Enter => "@SingleChar\\sc_ladder_get_out_top",
            LadderPhase.Leave => "@SingleChar\\sc_ladder_get_on_top",
            LadderPhase.Dismount => "@SingleChar\\sc_ladder_get_out_top",
            _ => "WAIT1",
        };
    }

    private static string CivTag(Civilian c, bool moving) =>
        c.OnLadder is { } ol ? LadderStepTag(c.LadderStep, ol, false)
        : c.Carrier is { } cu ? cu.Carry switch { CarryPhase.Lifting => "@CharChar\\cc_lift_body_lifted", CarryPhase.Dropping => "@CharChar\\cc_drop_body_droped", _ => "CARRIEDWALK" }
        : c.State is CivilianState.Unconscious or CivilianState.Dead ? "FAINT"
        : moving ? "WALK" : c.Leader != null ? "WAIT1" : "WAITSHOCKED";

    /// <summary>Dessine un personnage animé ; renvoie false s'il n'a pas de squelette (dessin figé à la place).</summary>
    // vitesse naturelle des animations de déplacement (recul du pied posé au sol, mesuré avec « OpenFD.Tools foulee »)
    private const float RunAnimSpeed = 520f, WalkAnimSpeed = 300f;

    /// <summary>
    /// Pompier « canon » : il marche lance en main (WALK : sc_walk_canon) ; arrêté, il pose sa lance (DEPLOY, une fois) puis
    /// attend accroupi (WAIT_DEPLOYED), même sans eau ; il arrose dans cette position (ATTACKFIRE : sc_canon_extinguish).
    /// </summary>
    private string CanonTag(Unit u, CharAnim a)
    {
        string tag = UnitTag(u, u.ShownMoving);
        if (tag == "FAINT")
        {
            a.Deployed = false;
            return tag;
        }
        if (u.ShownMoving)
        {
            a.Deployed = false;
            return "WALK";
        }
        if (u.Target != null)
        {
            a.Deployed = true;
            return "ATTACKFIRE";
        }
        if (a.Deployed || !a.Tags.TryGetValue("DEPLOY", out string? deploy))
            return "WAIT_DEPLOYED";
        string once = "@" + deploy;
        if (a.Tag == once && Clip(deploy) is { } dc && a.Time >= dc.Duration)
        {
            a.Deployed = true;
            return "WAIT_DEPLOYED";
        }
        return once;
    }

    private bool DrawCharacter(CharAnim a, string tag, SN.Vector3 position, float heading, float dt, bool axe, float rate = 1f, bool canon = false)
    {
        if (a.Rig is not { } rig)
            return false;
        string? file = tag.StartsWith('@') || tag.StartsWith('~') ? tag[1..]   // @ : jouée une fois ; ~ : en boucle
            : a.Tags.TryGetValue(tag, out string? f) ? f
            : tag == "RUN" && a.Tags.TryGetValue("WALK", out string? w) ? w
            : tag == "HEAL" && a.Tags.TryGetValue("ATTACKFIRE", out string? h) ? h
            : a.Tags.TryGetValue("WAIT1", out string? i) ? i : null;
        AnimClip? clip = file != null ? Clip(file) : null;
        if (dt > 0 && !_ghostPass && !_paused && file != null)
            AnimSound(a, file, tag != a.Tag, position);
        if (tag != a.Tag)
        {
            a.Tag = tag;
            a.Time = 0;
        }
        a.Time += dt * rate;
        float t = clip == null ? 0 : tag == "FAINT" || tag.StartsWith('@') ? MathF.Min(a.Time, clip.Duration) : a.Time % clip.Duration;
        a.World = rig.Pose(clip, t, a.World.Length == rig.Names.Length ? a.World : null);
        SN.Matrix4x4 place = SN.Matrix4x4.CreateRotationZ(heading + CharYaw) * SN.Matrix4x4.CreateTranslation(position);
        foreach (CharacterRig.Part part in rig.Parts)
        {
            string leaf = rig.Names[part.Node];
            // outils du squelette, cachés sauf pendant leur usage : la hache pour casser, la lance (launchfire) pour arroser
            bool nozzle = tag.StartsWith("ATTACKFIRE", StringComparison.OrdinalIgnoreCase) && leaf.Contains("launchfire", StringComparison.OrdinalIgnoreCase);
            // canon : sa grosse lance (CH_P_canon) toujours en main, jamais la petite
            bool canonTool = canon && leaf.EndsWith("_canon", StringComparison.OrdinalIgnoreCase);
            if (canon && leaf.Contains("launchfire", StringComparison.OrdinalIgnoreCase))
                continue;
            // pince du désincarcérateur (CH_P_des_scie) : seulement pendant la découpe
            bool saw = axe && tag.Contains("extricate", StringComparison.OrdinalIgnoreCase) && leaf.Contains("scie", StringComparison.OrdinalIgnoreCase);
            if (ToolRx.IsMatch(leaf) && !(axe && leaf.EndsWith("_axe", StringComparison.OrdinalIgnoreCase)) && !nozzle && !canonTool && !saw)
                continue;
            if (part.Skin != null)
            {
                if (!a.Skinned.TryGetValue(part, out (MeshGpu mesh, float[] pos, float[] nrm) sk))
                {
                    MeshGpu gm = Upload(_assets.LoadMesh(_mission, part.Mesh, "gfx"), dynamic: true);
                    sk = (gm, new float[part.BasePos.Length], new float[part.BaseNrm.Length]);
                    a.Skinned[part] = sk;
                }
                rig.Deform(part, a.World, sk.pos, sk.nrm);
                sk.mesh.UpdateVertices(sk.pos, sk.nrm);
                DrawMesh(sk.mesh, place.X());
            }
            else
            {
                if (!_partMeshes.TryGetValue(part.Mesh, out MeshGpu? mesh))
                {
                    try { mesh = Upload(_assets.LoadMesh(_mission, part.Mesh, "gfx")); } catch { mesh = null; }
                    _partMeshes[part.Mesh] = mesh;
                }
                if (mesh == null)
                    continue;
                DrawMesh(mesh, (a.World[part.Node] * place).X());
            }
        }
        return true;
    }

    // ---- sons des animations : le jeu range sous le nom de l'animation le bruit qui l'accompagne (cc_lift_body →
    // CC_LIFT_BODY, sc_break_door → SC_BREAK_DOOR, sc_extricate_loop → SC_EXTRICATE_LOOP…). Son en boucle dans la base
    // (LOOP = 1) : entretenu tant que l'animation joue ; sinon joué une fois quand elle commence (variantes _02 … _06 au hasard)
    private readonly Dictionary<string, (string label, bool loop)?> _animSounds = new(StringComparer.OrdinalIgnoreCase);

    private void AnimSound(CharAnim a, string file, bool started, SN.Vector3 position)
    {
        if (_audio == null)
            return;
        if (!_animSounds.TryGetValue(file, out (string label, bool loop)? snd))
        {
            string label = file[(file.LastIndexOf('\\') + 1)..].ToUpperInvariant();
            snd = _audio.Db.TryGet(label, out SoundDb.Entry? e) ? (label, e.Loop) : null;
            _animSounds[file] = snd;
        }
        if (snd is not { } sd)
            return;
        if (sd.loop)
            _audio.Keep(a, sd.label, position);
        else if (started)
            _audio.PlayAny(new[] { sd.label }.Concat(Enumerable.Range(2, 5).Select(k => $"{sd.label}_{k:00}")), position);
    }

    // ---- mouvement fluide : la simulation avance par pas de 0,1 s ; entre deux pas, on dessine les personnages et les
    // véhicules entre leur position du pas précédent et celle du pas actuel (sinon un camion « saute » de 1 m à chaque pas)
    private readonly Dictionary<object, (SN.Vector3 p, float h)> _prevPose = new(ReferenceEqualityComparer.Instance);

    private void SnapshotPoses()
    {
        foreach (Unit u in _session.Units.Active)
            _prevPose[u] = (u.Position, u.Heading);
        foreach (Civilian c in _session.Units.Civilians)
            _prevPose[c] = (c.Position, c.Heading);
    }

    private (SN.Vector3 p, float h) PoseOf(object o, SN.Vector3 p, float h)
    {
        if (!_prevPose.TryGetValue(o, out (SN.Vector3 p, float h) prev))
            return (p, h);
        float t = _paused ? 1f : (float)Math.Clamp(_owed, 0, 1);
        if (SN.Vector3.DistanceSquared(prev.p, p) > 1000f * 1000f)
            return (p, h);   // téléporté (arrivée, sortie de véhicule…)
        float dh = MathF.IEEERemainder(h - prev.h, 2 * MathF.PI);
        return (SN.Vector3.Lerp(prev.p, p, t), prev.h + dh * t);
    }

    // orientation affichée des personnages : tourne en douceur vers celle de la simulation (les chemins suivent les cases
    // de la grille et font des zigzags qui feraient pivoter le personnage d'un coup)
    private readonly Dictionary<object, float> _shownHeading = new(ReferenceEqualityComparer.Instance);
    private float SmoothHeading(object o, float h, float dt)
    {
        if (!_shownHeading.TryGetValue(o, out float cur) || dt <= 0)
        {
            if (!_shownHeading.ContainsKey(o))
                _shownHeading[o] = h;
            return _shownHeading[o];
        }
        float d = MathF.IEEERemainder(h - cur, 2 * MathF.PI);
        cur += d * MathF.Min(1, dt * 9f);
        return _shownHeading[o] = cur;
    }

    // pompiers cachés derrière un mur ou sous un toit : silhouette verte par-dessus le décor (seulement là où ils sont cachés)
    private void RenderGhosts()
    {
        Microsoft.Xna.Framework.Graphics.GraphicsDevice gd = GraphicsDevice;
        List<Unit> men = _session.Units.Active.Where(u => !u.IsVehicle && !InStairs(u)).ToList();
        if (men.Count == 0)
            return;
        Microsoft.Xna.Framework.Graphics.DepthStencilState oldDepth = gd.DepthStencilState;
        Microsoft.Xna.Framework.Graphics.BlendState oldBlend = gd.BlendState;
        Microsoft.Xna.Framework.Graphics.RasterizerState oldRast = gd.RasterizerState;
        gd.DepthStencilState = GhostDepth;
        gd.BlendState = Microsoft.Xna.Framework.Graphics.BlendState.AlphaBlend;
        gd.RasterizerState = Microsoft.Xna.Framework.Graphics.RasterizerState.CullNone;
        _ghostPass = true;
        foreach (Unit u in men)
        {
            (SN.Vector3 p, float h) pose = PoseOf(u, u.Position, u.Heading);
            SN.Vector3 toEye = _eye - (pose.p + new SN.Vector3(0, 0, 90));
            _ghostShift = (SN.Vector3.Normalize(toEye) * 70f).X();
            CharAnim a = Char(u, u.Model, "Design\\Unit\\" + u.ClassPath + ".class");
            bool axe = u.Opening != null && u.OpeningBreak && !u.Moving;
            if (!DrawCharacter(a, a.Tag != "" ? a.Tag : UnitTag(u, u.Moving), pose.p, _shownHeading.TryGetValue(u, out float hh) ? hh : pose.h, 0, axe, 1f, u.IsCanon))
                foreach ((MeshGpu? mesh, SN.Matrix4x4 part) in UnitModel(u.Model))
                    DrawMesh(mesh, (part * SN.Matrix4x4.CreateRotationZ(pose.h + CharYaw) * SN.Matrix4x4.CreateTranslation(pose.p)).X());
        }
        _ghostPass = false;
        gd.DepthStencilState = oldDepth;
        gd.BlendState = oldBlend;
        gd.RasterizerState = oldRast;
    }

    private void RenderUnits()
    {
        // temps continu (entre deux pas de simulation aussi) : l'animation avance à chaque image, pas par bonds de 0,1 s
        float now = _session.Time + (_paused ? 0 : (float)Math.Clamp(_owed, 0, 1) * Session.Dt);
        float dt = _lastAnimTime < 0 ? 0 : Math.Clamp(now - _lastAnimTime, 0, 0.5f);
        if (_paused)
            dt = 0;
        _lastAnimTime = now;
        List<Civilian> still = new List<Civilian>();
        foreach (Civilian c in _session.Units.Civilians.Where(c => !c.Hidden && c.State != CivilianState.Rescued))
        {
            CharAnim a = Char(c, c.Model, c.ClassPath);
            bool moving = SN.Vector3.Distance(a.LastPos, c.Position) > 1f;
            a.LastPos = c.Position;
            a.MovingFor = moving ? 0.3f : MathF.Max(0, a.MovingFor - dt);
            if (c.Carrier is { } inStairs && InStairs(inStairs))
                continue;   // porté dans un escalier : caché avec son porteur
            if (c.Carrier is { } cu)
            {
                // porté : à la place du porteur, avec son cap et sa cadence (les animations des deux vont ensemble)
                (SN.Vector3 p, float h) cp = PoseOf(cu, cu.Position, cu.Heading);
                float ch = _shownHeading.TryGetValue(cu, out float sh) ? sh : cp.h;
                float cr = cu.Carry == CarryPhase.Walking ? (cu.Moving ? Math.Clamp(cu.Speed * UnitManager.CarrySpeed / WalkAnimSpeed, 0.5f, 1.6f) : 0f) : 1f;
                if (!DrawCharacter(a, CivTag(c, false), cp.p, ch, dt, false, cr))
                    still.Add(c);
                continue;
            }
            if (!DrawCharacter(a, CivTag(c, a.MovingFor > 0), PoseOf(c, c.Position, c.Heading).p, SmoothHeading(c, PoseOf(c, c.Position, c.Heading).h, dt), dt, false, 280f / WalkAnimSpeed))
                still.Add(c);
        }
        List<Unit> stillUnits = new List<Unit>();
        // (dans un escalier : caché jusqu'à ce qu'il ressorte de l'autre côté)
        foreach (Unit u in _session.Units.Active.Where(u => !u.IsVehicle && !InStairs(u)))
        {
            CharAnim a = Char(u, u.Model, "Design\\Unit\\" + u.ClassPath + ".class");
            bool axe = u.Opening != null && u.OpeningBreak && !u.Moving;
            float rate = u.ShownMoving ? Math.Clamp(u.Speed / RunAnimSpeed, 0.6f, 1.6f) : 1f;
            if (u.Carry == CarryPhase.Walking)
                rate = u.ShownMoving ? Math.Clamp(u.Speed * UnitManager.CarrySpeed / WalkAnimSpeed, 0.5f, 1.6f) : 0f;   // arrêté avec le corps : figé
            string tag = UnitTag(u, u.ShownMoving);
            if (u.IsCanon)
            {
                tag = CanonTag(u, a);
                if (u.ShownMoving)
                    rate = Math.Clamp(u.Speed / WalkAnimSpeed, 0.5f, 1.6f);
            }
            if (!DrawCharacter(a, tag, PoseOf(u, u.Position, u.Heading).p, SmoothHeading(u, PoseOf(u, u.Position, u.Heading).h, dt), dt, axe, rate, u.IsCanon))
                stillUnits.Add(u);
        }
        // sans squelette : dessin figé (civils couchés s'ils sont inconscients)
        foreach (Civilian c in still)
            foreach ((MeshGpu? mesh, SN.Matrix4x4 part) in UnitModel(c.Model))
                DrawMesh(mesh, (part * (c.State == CivilianState.Conscious ? SN.Matrix4x4.Identity
                    : SN.Matrix4x4.CreateRotationY(MathF.PI / 2) * SN.Matrix4x4.CreateTranslation(0, 0, 25))
                    * SN.Matrix4x4.CreateRotationZ(PoseOf(c, c.Position, c.Heading).h + CharYaw) * SN.Matrix4x4.CreateTranslation(PoseOf(c, c.Position, c.Heading).p)).X());
        foreach ((uint id, string? model, SN.Matrix4x4 world, SN.Vector3 origin, float yaw) in _scriptMoved)
        {
            if (Host?.Visibility.TryGetValue(id, out bool shown) == true && !shown)
                continue;
            SN.Matrix4x4 w = world;
            // tourné par le script (ENT_SetOrientation : la manche à air de Forest suit le vent), autour de son pied
            if (Host?.TurnedObjects.TryGetValue(id, out float turn) == true)
                w = w * SN.Matrix4x4.CreateTranslation(-origin) * SN.Matrix4x4.CreateRotationZ(turn - yaw) * SN.Matrix4x4.CreateTranslation(origin);
            if (ScriptPos(id) is { } mv)
                w = _slideFlat.Contains(id) ? w * SN.Matrix4x4.CreateTranslation(mv.X - origin.X, mv.Y - origin.Y, 0) : FollowGround(id, world, origin, mv);
            if (DrawPlayedProp(id, model, w))
                continue;
            foreach ((MeshGpu? mesh, SN.Matrix4x4 part) in UnitModel(model))
                DrawMesh(mesh, (part * w).X());
        }
        if (Host is { } hv)
            foreach ((uint id, string? model, SN.Matrix4x4 world) in _scriptShown)
                if (hv.Visibility.TryGetValue(id, out bool vis) && vis)
                    foreach ((MeshGpu? mesh, SN.Matrix4x4 part) in UnitModel(model))
                        DrawMesh(mesh, (part * world).X());
        foreach (Unit u in _session.Units.Active.Where(u => u.IsVehicle || stillUnits.Contains(u)))
        {
            _charred = u.Destroyed;
            (SN.Vector3 p, float h) vp = PoseOf(u, u.Position, u.Heading);
            // coup de pelle du bulldozer : il avance d'un coup vers les débris
            if (u.Shove > 0)
                vp.p += new SN.Vector3(MathF.Cos(vp.h), MathF.Sin(vp.h), 0) * (60f * u.Shove);
            List<(MeshGpu mesh, SN.Matrix4x4 part)> parts = UnitModel(u.Model);
            List<string> names = _unitPartNames.TryGetValue(u.Model, out List<string>? nl) ? nl : new List<string>();
            SN.Matrix4x4 place = SN.Matrix4x4.CreateRotationZ(vp.h + (u.IsVehicle ? ModelYaw : CharYaw)) * SN.Matrix4x4.CreateTranslation(vp.p);
            if (u.FromLevel && DrawLevelVehicle(u, place))
            {
                _charred = false;
                continue;
            }
            for (int i = 0; i < parts.Count; i++)
            {
                SN.Matrix4x4 pose = u.IsVehicle && i < names.Count ? VehiclePartPose(u, names[i]) : SN.Matrix4x4.Identity;
                DrawMesh(parts[i].mesh, (parts[i].part * pose * place).X());
            }
            _charred = false;
        }
    }

    /// <summary>
    /// Objet du décor animé par le script (ENT_SwitchState sur un objet à états, ANM_Play) : ses pièces suivent l'animation,
    /// jouée une fois, puis restent sur la dernière image (London : les gravats qui tombent du plafond, cinématique cat_3).
    /// false : pas d'animation lancée, ou modèle sans pièce animée (dessin ordinaire).
    /// </summary>
    private bool DrawPlayedProp(uint id, string model, SN.Matrix4x4 place)
    {
        if (Host?.PlayedAnims.TryGetValue(id, out (string anim, float start, bool loop) pa) != true || Clip(pa.anim) is not { } clip || clip.Duration <= 0)
            return false;
        if (!_propRigs.TryGetValue(model, out CharacterRig? rig))
        {
            try { rig = CharacterRig.Build(_assets, _files, model); } catch { rig = null; }
            _propRigs[model] = rig;
        }
        if (rig == null || !rig.Parts.Any(p => clip.Tracks.ContainsKey(rig.Names[p.Node])))
            return false;
        float now = (float)(_session.Time + (_paused ? 0 : _owed * Session.Dt));
        float t = pa.loop ? (now - pa.start) % clip.Duration : Math.Clamp(now - pa.start, 0, clip.Duration);
        SN.Matrix4x4[] world = rig.Pose(clip, t);
        foreach (CharacterRig.Part part in rig.Parts)
        {
            if (!_partMeshes.TryGetValue(part.Mesh, out MeshGpu? mesh))
            {
                try { mesh = Upload(_assets.LoadMesh(_mission, part.Mesh, "gfx")); } catch { mesh = null; }
                _partMeshes[part.Mesh] = mesh;
            }
            if (mesh != null)
                DrawMesh(mesh, (world[part.Node] * place).X());
        }
        return true;
    }

    /// <summary>
    /// Véhicule posé dans le niveau dont le modèle est animé pièce par pièce (l'hélicoptère : corps, portes, rotors) : les
    /// pièces de l'état intact (« …_e1 », détruit : « …_e2 » ; jamais les débris d'explosion), posées par l'animation que le
    /// script a lancée (ANM_Play : atterrissage, décollage ; « hold » : la dernière image reste). false : modèle ordinaire.
    /// </summary>
    private bool DrawLevelVehicle(Unit u, SN.Matrix4x4 place)
    {
        if (!_propRigs.TryGetValue(u.Model, out CharacterRig? rig))
        {
            try { rig = CharacterRig.Build(_assets, _files, u.Model); } catch { rig = null; }
            _propRigs[u.Model] = rig;
        }
        if (rig == null || !rig.Names.Any(n => n.EndsWith("_e1", StringComparison.OrdinalIgnoreCase)))
            return false;
        AnimClip? clip = null;
        float t = 0;
        if (Host?.PlayedAnims.TryGetValue((uint)u.Id, out (string anim, float start, bool loop) pa) == true && Clip(pa.anim) is { } c && c.Duration > 0)
        {
            clip = c;
            float now = (float)(_session.Time + (_paused ? 0 : _owed * Session.Dt));
            t = pa.loop ? (now - pa.start) % c.Duration : Math.Clamp(now - pa.start, 0, c.Duration);
        }
        SN.Matrix4x4[] world = rig.Pose(clip, t);
        string state = u.Destroyed ? "_e2" : "_e1";
        foreach (CharacterRig.Part part in rig.Parts)
        {
            int root = part.Node;
            while (rig.Parent[root] >= 0)
                root = rig.Parent[root];
            if (!rig.Names[root].EndsWith(state, StringComparison.OrdinalIgnoreCase) && !rig.Names[root].EndsWith(state + "_base", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!_partMeshes.TryGetValue(part.Mesh, out MeshGpu? mesh))
            {
                try { mesh = Upload(_assets.LoadMesh(_mission, part.Mesh, "gfx")); } catch { mesh = null; }
                _partMeshes[part.Mesh] = mesh;
            }
            if (mesh != null)
                DrawMesh(mesh, (world[part.Node] * place).X());
        }
        return true;
    }

    // sons : voix et bruits signalés par le jeu, puis sons entretenus (lances, haches, pas, moteurs, feux proches)
    private void UpdateSound(float dt)
    {
        if (_audio == null)
            return;
        if (_map)
        {
            _audio.Listener = new SN.Vector3(_mapCenter, 0);
            _audio.ExtraDistance = 3000;
        }
        else
        {
            _audio.Listener = _target;
            SN.Vector3 back = new SN.Vector3(MathF.Cos(_yaw), MathF.Sin(_yaw), 0);
            _audio.Right = new SN.Vector3(-back.Y, back.X, 0);
            _audio.ExtraDistance = _dist * 0.35f;
        }
        List<(string label, SN.Vector3? pos)> ev = _session.Units.Sounds;
        foreach ((string? label, SN.Vector3? pos) in ev.Distinct().Take(4))
            _audio.Play(label, pos);
        ev.Clear();
        if (!_paused)
        {
            foreach (Unit u in _session.Units.Active)
            {
                if (u.IsVehicle)
                {
                    // en route : le moteur, et la sirène de sa nationalité (pompe, échelle, ambulance : chacune la sienne)
                    if (u.Moving)
                    {
                        _audio.Keep(u, u.RunSound ?? "VEHICLE_1_RUN", u.Position);
                        if (u.SirenSound != null)
                            _audio.Keep((u.Id, "sirène"), u.SirenSound, u.Position, u.SirenVolume);
                    }
                    continue;
                }
                if (u.Target != null && !u.Moving)
                    _audio.Keep(u, "HOSE_01_01", u.Position);
                // pas, coups de hache sur une porte, scie du désincarcérateur… : sons des animations (AnimSound) ;
                // la vitre (sc_break_window) n'a pas de son à son nom : celui de la hache sur la porte
                else if (u.Opening is { IsWindow: true } && u.OpeningBreak && !u.Moving)
                    _audio.Keep(u, "SC_BREAK_DOOR", u.Position);
            }
            // bombardier d'eau : bruit de l'avion pendant son passage, qui monte puis redescend (il arrive, passe, s'en va)
            if (_session.Units.Strike is { } air)
            {
                float t0 = air.Pass - _session.Units.AirBefore;
                float k = (_session.Time - t0) / MathF.Max(0.1f, air.End - t0);
                if (k >= 0 && k <= 1)
                    _audio.Keep(air, "AIRSTRIKE", _audio.Listener, MathF.Pow(MathF.Sin(MathF.PI * k), 1.5f));
            }
            // feux : les 6 plus proches ; étiquette FIRE<type>_<niveau 01..16>_1 selon l'intensité (choix estimé)
            foreach (Fire.HotPoint h in _session.Fire.HotPoints.Where(h => h.IsBurning)
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
