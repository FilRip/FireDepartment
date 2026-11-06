using OpenFD.Fire;
using OpenFD.Formats;
using OpenFD.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SN = System.Numerics;

namespace OpenFD.MG;

/// <summary>
/// Le jeu (MonoGame) : chargement d'une mission, simulation, rendu 3D et carte, interface.
/// La logique (feu, fumée, unités, portes…) est celle des bibliothèques OpenFD.Fire / OpenFD.Game, inchangée.
/// </summary>
public sealed partial class OpenFDGame : Microsoft.Xna.Framework.Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly GameFiles _files;
    private readonly SceneAssets _assets;
    private Audio? _audio;
    private string _mission, _difficulty;
    public bool TestAttack, NoRoofs, NoHelp;
    public string? TestDoors;   // essai : « porteA,porteB » : un pompier s'occupe de chaque porte à 3 s
    public string? CapturePath; public int CaptureAfter; public float StartSeconds; public bool StartMap; public string? CameraSpec;

    private Ui _ui = null!;
    private Font _font = null!;
    private BasicEffect _lit = null!, _plain = null!, _part = null!, _ghost = null!, _burnt = null!;
    private bool _ghostPass; private Vector3 _ghostShift;
    private static readonly DepthStencilState GhostDepth = new() { DepthBufferEnable = true, DepthBufferWriteEnable = false, DepthBufferFunction = CompareFunction.Greater };
    private DualTextureEffect _dual = null!;
    private AlphaTestEffect _alphaFx = null!;
    private SceneEffect _scene = null!;

    // monde
    private Session _session = null!;
    private readonly List<(MeshGpu mesh, bool roof, int layer)> _draw = new();   // layer : 0 décor, 1 objets, 2 arbres
    private readonly Dictionary<string, Texture2D?> _texCache = new(StringComparer.OrdinalIgnoreCase);
    private SN.Vector3 _sceneMin, _sceneMax;
    private Texture2D? _gridTex; private float _gridW, _gridH;

    // vue
    private bool _map, _roofs = true, _objects = true, _smokeOn = true, _help = false;
    private SN.Vector3 _target; private float _yaw = -1.75f, _pitch = 0.75f, _dist = 9000f;
    private SN.Vector2 _mapCenter; private float _mapScale = 0.06f;
    private SN.Matrix4x4 _viewProj, _view, _proj; private SN.Vector3 _eye;
    private int _fbW, _fbH, _winW, _winH;
    private float _scale = 1;   // échelle de l'écran (100 % = 1)
    private float _bright = 1.5f;    // luminosité (1 = textures telles quelles)

    // temps
    private float _speed = 1; private bool _paused = true; private double _owed;
    private float _flameFps = 10f, _flameFpsVar = 4f;   // vitesse des flammes (options : Gameplay.FlameSpeed)
    private float _visTime;   // temps des animations (flammes, fumée…) : avance seulement quand la partie tourne
    private bool _altEnterDown;
    private readonly List<(string text, bool error)> _log = new();   // journal de débogage (touche D) ; les erreurs s'affichent en rouge
    private int _frame;

    private string _unitClass = "Unit\\Fireman\\fireman_medic";
    private readonly Dictionary<string, List<(MeshGpu mesh, SN.Matrix4x4 part)>> _unitModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Unit> _sel = new();
    private int _unitLogSeen;
    private int _scriptLogSeen;
    public const float ModelYaw = MathF.PI / 2;    // les véhicules regardent vers −Y (comme les personnages)
    public const float CharYaw = MathF.PI / 2;     // les personnages (squelette Biped) regardent vers −Y

    public OpenFDGame(GameFiles files, string mission, string difficulty)
    {
        _files = files; _assets = new SceneAssets(files); _mission = mission; _difficulty = difficulty;
        _gdm = new GraphicsDeviceManager(this)
        {
            GraphicsProfile = GraphicsProfile.HiDef, PreferMultiSampling = true, SynchronizeWithVerticalRetrace = true,
            PreferredBackBufferWidth = 1400, PreferredBackBufferHeight = 860, PreferredDepthStencilFormat = DepthFormat.Depth24,
        };
        // FD3_SANS_MSAA : sans anticrénelage (cartes ou pilotes qui le refusent)
        if (Environment.GetEnvironmentVariable("FD3_SANS_MSAA") != null)
            _gdm.PreferMultiSampling = false;
        IsMouseVisible = true; IsFixedTimeStep = false;
        Window.AllowUserResizing = true;
        Window.Title = "Open Fire Department";
    }

    protected override void Initialize()
    {
        base.Initialize();
        // fenêtre à la taille de l'écran : 1400 × 860 à 100 %, agrandie suivant l'échelle (200 % → 2800 × 1720), sans dépasser l'écran
        _scale = Display.Scale(Window.Handle);
        DisplayMode mode = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode;
        int w = (int)Math.Min(1400 * _scale, mode.Width * 0.92f), h = (int)Math.Min(860 * _scale, mode.Height * 0.88f);
        if (CapturePath != null)
        {
            w = 1400;
            h = 860;
        }
        _gdm.PreferredBackBufferWidth = w; _gdm.PreferredBackBufferHeight = h; _gdm.ApplyChanges();
        Window.ClientSizeChanged += (_, _) =>
        {
            Rectangle b = Window.ClientBounds;
            if (b.Width > 0 && b.Height > 0 && (b.Width != _gdm.PreferredBackBufferWidth || b.Height != _gdm.PreferredBackBufferHeight))
            {
                _gdm.PreferredBackBufferWidth = b.Width;
                _gdm.PreferredBackBufferHeight = b.Height;
                _gdm.ApplyChanges();
            }
        };
        Window.TextInput += (_, e) => OnChar(e.Character);
    }

    protected override void LoadContent()
    {
        GraphicsDevice gd = GraphicsDevice;
        Tex.Init(gd); Ui.InitHard(gd);
        _ui = new Ui(gd) { Scale = _scale };
        UseGameFont(null);   // police du jeu (Data\Fonte\font18.fnt)
        _lit = new BasicEffect(gd) { TextureEnabled = true, LightingEnabled = true, FogEnabled = true };
        _burnt = new BasicEffect(gd) { LightingEnabled = false, VertexColorEnabled = false, DiffuseColor = new Vector3(0.045f, 0.04f, 0.035f) };   // carcasse calcinée
        _ghost = new BasicEffect(gd) { TextureEnabled = false, LightingEnabled = false, VertexColorEnabled = false, DiffuseColor = new Vector3(0.15f, 1f, 0.25f), Alpha = 0.75f };
        _plain = new BasicEffect(gd) { TextureEnabled = false, LightingEnabled = true, FogEnabled = true, DiffuseColor = new Vector3(0.55f, 0.55f, 0.58f) };
        foreach (BasicEffect e in new[] { _lit, _plain })
        {
            // éclairage du jeu d'origine : 0,62 + 0,55 × |n · soleil| (les deux faces), d'où deux lumières opposées
            Vector3 sun = Vector3.Normalize(new Vector3(-0.4f, -0.3f, 0.85f));   // remplacé par le soleil de la mission (LoadMission)
            e.AmbientLightColor = new Vector3(0.62f);
            e.DirectionalLight0.Enabled = true; e.DirectionalLight0.Direction = -sun; e.DirectionalLight0.DiffuseColor = new Vector3(0.55f); e.DirectionalLight0.SpecularColor = Vector3.Zero;
            e.DirectionalLight1.Enabled = true; e.DirectionalLight1.Direction = sun; e.DirectionalLight1.DiffuseColor = new Vector3(0.55f); e.DirectionalLight1.SpecularColor = Vector3.Zero;
            e.DirectionalLight2.Enabled = false; e.SpecularPower = 1;
        }
        _dual = new DualTextureEffect(gd) { FogEnabled = true };
        _alphaFx = new AlphaTestEffect(gd) { AlphaFunction = CompareFunction.Greater, ReferenceAlpha = 102, FogEnabled = true, DiffuseColor = new Vector3(0.95f) };
        _part = new BasicEffect(gd) { TextureEnabled = true, VertexColorEnabled = true, LightingEnabled = false, Texture = Tex.Soft };
        _scene = new SceneEffect(gd); _depth = new DepthEffect(gd);
        try { _audio = new Audio(SoundDb.Load(_files.DataDir)); } catch (Exception e) { Console.WriteLine("Son : " + e.Message); }
        LoadCursors();
        _settings = Settings.Load(_files.DataDir);
        ApplySettings();

        if (StartMenu && (CapturePath == null || Environment.GetEnvironmentVariable("FD3_INTRO") != null))
            StartIntro();
        else
            _introDone = true;
        if (StartMenu)
        {
            OpenMenu();
            if (int.TryParse(Environment.GetEnvironmentVariable("FD3_ECRAN"), out int e0))
                _screen = e0;
        }
        else
            LoadMission(_mission, _difficulty);
        _map = StartMap;
        if (NoRoofs)
            _roofs = false;
        if (NoHelp)
            _help = false;
        if (StartSeconds > 0)
            Advance(StartSeconds);
        if (CameraSpec != null)
        {
            float[] c = CameraSpec.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            _target = new(c[0], c[1], c[2]); _yaw = c[3]; _pitch = c[4]; _dist = c[5];
        }
    }

    // essais automatiques (--avance, --envoyer, --portes)
    private void Advance(float seconds)
    {
        int n = (int)(seconds / Session.Dt);
        bool camTest = Environment.GetEnvironmentVariable("FD3_ESSAI_CAM") != null;
        for (int i = 0; i < n; i++)
        {
            _session.Step();
            if (camTest && i % 50 == 0 && _session.Scripts is { } sct)
                foreach (string k in new[] { "OnCameraMove", "OnCameraRotate", "OnCameraZoom" })
                    if (_session.MissionConfig.Get("INTERFACE_SCRIPT", k)?.Trim('"', ' ') is { Length: > 0 } fn)
                        sct.TryRun(fn);
            if (TestDoors != null && i == 30)
                foreach (string name in TestDoors.Split(','))
                    if (_session.Units.Openings.FirstOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } d)
                    {
                        Unit? u = _session.Units.Active.Where(u => !u.IsVehicle && u.Opening == null).OrderBy(u => d.Breakable && u.CanBreak ? 0 : 1).FirstOrDefault();
                        if (u != null)
                        {
                            _session.Units.WorkOnOpening(u, d);
                            _sel.Add(u);
                        }
                    }
            if (i == 40 && Environment.GetEnvironmentVariable("FD3_A_BORD") != null
                && _session.Units.Active.FirstOrDefault(u => u.IsVehicle && u.Seats > 0) is { } veh)
            {
                foreach (Unit man in _session.Units.Active.Where(u => !u.IsVehicle).Take(2))
                    _session.Units.BoardNow(man, veh);
                _sel.Clear(); _sel.Add(veh);
            }
            if (i == 30 && Environment.GetEnvironmentVariable("FD3_BOUCHE") != null && _session.Units.Supplies.FirstOrDefault() is { } hyd
                && _session.Units.Active.FirstOrDefault(u => u.IsVehicle && u.Nozzle != null) is { } tk)
            {
                _session.Units.Teleport(tk, new SN.Vector2(hyd.Position.X - 900, hyd.Position.Y - 300));
                _target = new SN.Vector3(hyd.Position.X - 450, hyd.Position.Y - 150, 0); _dist = 2200; _pitch = 0.9f;
            }
            if (i == 400 && Environment.GetEnvironmentVariable("FD3_CASSER") is { } cas
                && _session.Units.Openings.FirstOrDefault(o => o.Name.Equals(cas, StringComparison.OrdinalIgnoreCase)) is { } ob
                && _session.Units.Active.Where(u => u.CanBreak).OrderBy(u => SN.Vector3.Distance(u.Position, ob.Position)).FirstOrDefault() is { } axeman)
            {
                _session.Units.Teleport(axeman, new SN.Vector2(5650, 4250));
                _session.Units.WorkOnOpening(axeman, ob); _sel.Clear(); _sel.Add(axeman);
                Console.WriteLine($"essai hache : {axeman.Status}");
            }
            if (i > 400 && i % 20 == 0 && Environment.GetEnvironmentVariable("FD3_CASSER") != null)
                foreach (Unit u0 in _sel)
                    Console.WriteLine($"  {i / 10} s : {u0.Status} ({u0.Position.X:0},{u0.Position.Y:0},{u0.Position.Z:0})");
            if (TestAttack && i == 50)
                foreach (Unit u in _session.Units.Active.Where(u => u.Nozzle != null))
                {
                    HotPoint? f = _session.Fire.HotPoints.Where(h => h.IsBurning).OrderBy(h => SN.Vector3.Distance(h.Position, u.Position)).FirstOrDefault();
                    if (f != null)
                    {
                        _session.Units.Attack(u, f);
                        _sel.Add(u);
                    }
                }
        }
    }

    protected override void Update(GameTime gameTime)
    {
        float dt = (float)Math.Min(0.1, gameTime.ElapsedGameTime.TotalSeconds);
        AltEnter();
        _fbW = GraphicsDevice.PresentationParameters.BackBufferWidth; _fbH = GraphicsDevice.PresentationParameters.BackBufferHeight;
        _winW = Math.Max(1, Window.ClientBounds.Width); _winH = Math.Max(1, Window.ClientBounds.Height);
        if (_frame % 15 == 0)
            RefreshScale();
        NetUpdate();
        NetTestHooks();
        if (UpdateIntro(dt))
        {
            base.Update(gameTime);
            return;
        }
        if (UpdateBriefing(dt))
        {
            base.Update(gameTime);
            return;
        }
        if (_menu)
        {
            MenuCursor();
            if (!_inGame && _screen != 3)
                UpdateMenuVideo(dt);
            else
                StopMenuVideo();
            if (IsActive)
            {
                KeyboardState mk = Keyboard.GetState();
                MouseState mm = Mouse.GetState();
                ScreenInput(mk, mm);
                _kPrev = mk;
                _mPrev = mm;
            }
            // partie en réseau, client : la partie continue derrière son menu (seul le serveur met en pause)
            if (_menu && _inGame && _netGame && _net is { IsServer: false } && _session != null)
            {
                Simulate(dt);
                UpdateMission();
                UpdateCinema(dt);
            }
            base.Update(gameTime); return;
        }
        if (IsActive)
            HandleInput(dt);
        if (_session != null)
            _session.Units.PlayerCommand = false;   // les décisions de l'IA qui suivent restent muettes
        if (_menu)
        {
            base.Update(gameTime);
            return;
        }
        if (!_paused)
            _visTime += dt;
        Simulate(dt);
        CameraCallbacks(dt);
        UpdateMission();
        UpdateCinema(dt);
        UpdateExplosions(dt);
        UpdateRoofs(dt);
        UpdateSound(dt);
        if (_frame % 3 == 0 && IsActive)
            UpdateCursor();
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        _fbW = GraphicsDevice.PresentationParameters.BackBufferWidth; _fbH = GraphicsDevice.PresentationParameters.BackBufferHeight;
        if (_fbW == 0 || _fbH == 0)
            return;
        float time = _visTime;   // s'arrête en pause : flammes, fumée et eau figées
        if (_intro != null)
        {
            DrawIntro();
            base.Draw(gameTime);
            return;
        }
        // essai : FD3_TEST_CHARGEMENT=mission,progression → l'écran de chargement de cette mission
        if (Environment.GetEnvironmentVariable("FD3_TEST_CHARGEMENT") is { } tc && tc.Split(',') is { Length: 2 } tcp)
        {
            _mission = tcp[0];
            _loadProgress = float.Parse(tcp[1], System.Globalization.CultureInfo.InvariantCulture);
            DrawLoading();
            base.Draw(gameTime);
            if (CapturePath != null && ++_frame >= CaptureAfter)
            {
                Capture(CapturePath);
                Exit();
            }
            return;
        }
        if (_briefing != null || _briefWaiting)
        {
            DrawBriefing();
            base.Draw(gameTime);
            if (CapturePath != null && ++_frame >= CaptureAfter)
            {
                Capture(CapturePath);
                Exit();
            }
            return;
        }
        if (_menu && _inGame && _session != null)
        {
            // pause : la partie figée derrière le menu
            if (_map)
                RenderMap();
            else
            {
                RenderShadowMap();
                Render3D(time);
            }
            RenderHud();
            RenderScreen();
        }
        else if (_menu)
            RenderScreen();
        else
        {
            if (_map)
                RenderMap();
            else
            {
                RenderShadowMap();
                Render3D(time);
            }
            RenderHud();
        }
        base.Draw(gameTime);
        if (_frame == 4 && !_menu && Environment.GetEnvironmentVariable("FD3_ECRAN") is { } ec && int.TryParse(ec, out int ecn) && ecn >= 2)
        {
            OpenPause();
            if (ecn == 2)
            {
                _optionsFrom = 3;
                _screen = 2;
            }
            if (ecn == 4)
                OpenLoadScreen(3);
        }
        if (_frame == 4 && Environment.GetEnvironmentVariable("FD3_SAUVE_TEST") != null && _session != null)
            SaveSelfTest();
        // essai : FD3_MENU_ECRAN=6 (multijoueur) ou 7 (serveur créé) (avec --menu)
        if (_frame == 4 && _menu && Environment.GetEnvironmentVariable("FD3_MENU_ECRAN") is { } me && int.TryParse(me, out int men))
        {
            OpenMultiplayer();
            if (men == 7)
                HostServer();
        }
        // essai : FD3_CAMPAGNE=n → écran « Campagne » à la page n (avec --menu)
        if (_frame == 4 && _menu && Environment.GetEnvironmentVariable("FD3_CAMPAGNE") is { } cp && int.TryParse(cp, out int cpn))
        {
            OpenCampaign();
            _campaignIndex = Math.Clamp(cpn, 0, Math.Max(0, _campaign.Count - 1));
        }
        // essai : FD3_BRIEFING_T=s → briefing de cette page, avancé de s secondes
        if (_frame == 4 && _menu && _screen == 1 && Environment.GetEnvironmentVariable("FD3_BRIEFING_T") is { } bt && float.TryParse(bt, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float bts))
        {
            CampaignPage pg = _campaign[_campaignIndex];
            StartBriefing(pg.Dir, "EASY", pg.Briefing);
            for (int i = 0; i < (int)(bts * 10) && _briefing != null; i++)
                UpdateBriefing(0.1f);
            _briefFrozen = true;
        }
        // essai : FD3_PORTER=phase,secondes → un pompier va chercher le civil inconscient le plus proche ; la partie avance
        // jusqu'à la phase voulue (Lifting, Walking, Dropping) plus quelques secondes, la caméra regarde le porteur
        // essai : FD3_VEHICULES=0.3,0 → vie (part du maximum) des premiers véhicules arrivés ; la caméra regarde le premier
        if (_frame == 4 && Environment.GetEnvironmentVariable("FD3_VEHICULES") is { } vies && _session != null)
        {
            SkipCinematic();
            for (int i = 0; i < 600 && !_session.Units.Active.Any(u => u.IsVehicle && !u.Moving); i++)
                _session.Step();
            List<Unit> vs = _session.Units.Active.Where(u => u.IsVehicle).ToList(); string[] fr = vies.Split(',');
            for (int i = 0; i < Math.Min(vs.Count, fr.Length); i++)
            {
                float f = float.Parse(fr[i], System.Globalization.CultureInfo.InvariantCulture);
                if (f <= 0)
                    _session.Units.Damage(vs[i], vs[i].Life + 1);
                else
                    vs[i].Life = vs[i].LifeMax * f;
            }
            for (int i = 0; i < vs.Count; i++)
                Console.WriteLine($"véhicule {vs[i].Name} ({vs[i].Position.X:0},{vs[i].Position.Y:0}) vie {vs[i].Life:0}/{vs[i].LifeMax:0}");
            SnapshotPoses();
        }
        // essai : FD3_LANCE=1 → un pompier arrose le feu le plus proche ; la caméra le regarde de près
        if (_frame == 4 && Environment.GetEnvironmentVariable("FD3_LANCE") != null && _session != null)
        {
            SkipCinematic();
            UnitManager um = _session.Units;
            for (int i = 0; i < 600 && !um.Active.Any(u => !u.IsVehicle && u.Nozzle != null && !u.Moving); i++)
                _session.Step();
            Unit? man = um.Active.FirstOrDefault(u => !u.IsVehicle && u.Nozzle != null);
            HotPoint? fire = man == null ? null : _session.Fire.HotPoints.Where(h => h.IsBurning && MathF.Abs(h.Position.Z - man.Position.Z) < 200).OrderBy(h => SN.Vector3.Distance(h.Position, man.Position)).FirstOrDefault();
            if (man != null && fire != null)
            {
                um.Attack(man, fire);
                for (int i = 0; i < 1200 && !(man.Target != null && !man.Moving); i++)
                    _session.Step();
                for (int i = 0; i < 20; i++)
                    _session.Step();
                SnapshotPoses();
                _target = man.Position + new SN.Vector3(0, 0, 100); _dist = 450; _pitch = 0.45f; _paused = true;
                Console.WriteLine($"lance : {man.Status}");
            }
        }
        // essai : FD3_GRAPPIN=phase,secondes → le scout passe le premier mur au grappin ; la caméra le regarde
        if (_frame == 4 && Environment.GetEnvironmentVariable("FD3_GRAPPIN") is { } grap && _session != null && _session.Units.Grapples.Count > 0)
        {
            string[] pr = grap.Split(','); ClimbPhase want = Enum.Parse<ClimbPhase>(pr[0]); float extra = pr.Length > 1 ? float.Parse(pr[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
            UnitManager um = _session.Units; SkipCinematic();
            for (int i = 0; i < 600 && !um.Active.Any(u => u.CanGrapple && !u.Moving); i++)
                _session.Step();
            Unit? sc = um.Active.FirstOrDefault(u => u.CanGrapple); GrapplePoint gp = um.Grapples[0];
            if (sc != null)
            {
                sc.Position = new SN.Vector3(gp.SideA, 0); um.GrappleOver(sc, gp);
                for (int i = 0; i < 600 && sc.Climb != want; i++)
                    _session.Step();
                for (int i = 0; i < (int)(extra / Session.Dt); i++)
                    _session.Step();
                SnapshotPoses();
                _target = sc.Position; _dist = 1100; _pitch = 0.5f; _yaw = MathF.Atan2(gp.Axis.Y, gp.Axis.X); _paused = true;
                Console.WriteLine($"grappin : {sc.Status} {sc.Climb} {sc.Position}");
            }
        }
        if (_frame == 4 && Environment.GetEnvironmentVariable("FD3_PORTER") is { } porter && _session != null)
        {
            string[] pr = porter.Split(','); CarryPhase want = Enum.Parse<CarryPhase>(pr[0]); float extra = pr.Length > 1 ? float.Parse(pr[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
            UnitManager um = _session.Units;
            SkipCinematic();
            for (int i = 0; i < 600 && !um.Active.Any(u => !u.IsVehicle && !u.CanHeal && !u.Moving); i++)
                _session.Step();   // équipes arrivées
            Unit? man = um.Active.FirstOrDefault(u => !u.IsVehicle && !u.CanHeal);
            Civilian? civ = man == null ? null : um.Civilians.Where(c => c.State == CivilianState.Unconscious && !c.Hidden && um.CanReach(new(man.Position.X, man.Position.Y), new(c.Position.X, c.Position.Y)))
                .OrderBy(c => SN.Vector3.Distance(c.Position, man.Position)).FirstOrDefault();
            if (man != null && civ != null)
            {
                SkipCinematic();
                um.FetchCivilian(man, civ);
                for (int i = 0; i < 3000 && man.Carry != want; i++)
                    _session.Step();
                for (int i = 0; i < (int)(extra / Session.Dt); i++)
                    _session.Step();
                SnapshotPoses();
                _target = new SN.Vector3(man.Position.X, man.Position.Y, man.Position.Z); _dist = 900; _paused = true; _roofCheck = 0; UpdateRoofs(0.3f);
                Console.WriteLine($"porteur : {man.Status} · {man.Carry} · civil {civ.State} · z {man.Position.Z:0}");
                foreach (Building b in _buildings.Where(b => Inside(new(man.Position.X, man.Position.Y), b.Min, b.Max, 0)))
                    Console.WriteLine($"  sous : {string.Join(",", b.Nodes.Take(5))} ({b.Nodes.Count}) z min {b.MinZ:0} ouvert {b.Open}");
            }
        }
        if (_frame == 4 && Environment.GetEnvironmentVariable("FD3_ACCES") != null && _session != null)
        {
            foreach (Opening d in _session.Units.Openings.Where(o => !o.IsWindow))
                _session.Units.SetOpeningState(d, OpeningState.Open);
            Unit? man = _session.Units.Active.FirstOrDefault(u => !u.IsVehicle);
            if (man != null)
                foreach (Civilian c in _session.Units.Civilians.Where(c => !c.Hidden))
                    Console.WriteLine($"accès {c.Name} ({c.Position.X:0},{c.Position.Y:0}) : {_session.Units.CanReach(new(man.Position.X, man.Position.Y), new(c.Position.X, c.Position.Y))}");
            if (Environment.GetEnvironmentVariable("FD3_ACCES") is { Length: > 2 } zone)
            {
                int[] z = zone.Split(',').Select(int.Parse).ToArray();
                Console.WriteLine(_session.Units.AsciiMap(z[0], z[1], z[2], z[3]));
            }
        }
        // essai (captures) : FD3_CHOISIR → toutes les unités sur le terrain sont choisies
        if (!_menu && _session != null && _sel.Count == 0 && Environment.GetEnvironmentVariable("FD3_CHOISIR") != null)
            foreach (Unit u0 in _session.Units.Active.Where(u => !u.OnBoard))
                _sel.Add(u0);
        // essai : FD3_CLIC=x,y → tous les pompiers choisis, clic droit à ce point de l'écran
        if (_frame == 4 && !_menu && _session != null && Environment.GetEnvironmentVariable("FD3_CLIC") is { } clic)
        {
            double[] xy = clic.Split(',').Select(double.Parse).ToArray();
            _sel.Clear(); foreach (Unit u0 in _session.Units.Active.Where(u => !u.IsVehicle)) _sel.Add(u0);
            SN.Vector2? gp = Ground(xy[0], xy[1]);
            Console.WriteLine($"clic d'essai : sol visé {gp}, {_sel.Count} pompier(s)");
            Command(xy[0], xy[1]);
            foreach (Unit u in _sel)
                Console.WriteLine($"  {u.Name} : {u.Status} chemin {u.Path.Count}");
        }
        if (CapturePath != null && ++_frame >= CaptureAfter)
        {
            Capture(CapturePath);
            Exit();
        }
        else
            _frame++;
    }

    protected override void UnloadContent() { _audio?.Dispose(); base.UnloadContent(); }

    // ------------------------------------------------------------------ chargement
    private Texture2D? TexOf(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        if (_texCache.TryGetValue(name, out Texture2D? t))
            return t;
        byte[]? b = _assets.Texture(_mission, name);
        try { t = b != null ? Tex.Dds(GraphicsDevice, b) : null; } catch (Exception e) { Console.WriteLine($"{name} : {e.Message}"); t = null; }
        return _texCache[name] = t;
    }

    private MeshGpu Upload(SceneAssets.MeshAsset a, bool dynamic = false)
    {
        MeshGpu g = MeshGpu.Create(GraphicsDevice, a.Mesh.Positions, a.Mesh.Normals, a.Uv0, a.Mesh.Indices, dynamic);
        foreach (SceneAssets.Group gr in a.Groups)
            g.Groups.Add((gr.Start, gr.Count, TexOf(gr.Diffuse), null, gr.Alpha));
        return g;
    }

    // ---- regroupement des maillages fixes : sommets déjà placés dans le monde, un lot par texture ----
    private sealed class LitBatch { public readonly List<VertexPositionNormalTexture> V = new(); public readonly List<int> I = new(); }
    private sealed class DualBatch { public readonly List<VertexDual> V = new(); public readonly List<int> I = new(); }
    private readonly Dictionary<(Texture2D? tex, bool alpha, bool roof, int layer), LitBatch> _litBatches = new();
    private readonly Dictionary<(Texture2D? tex, Texture2D lm, bool alpha, bool roof), DualBatch> _dualBatches = new();

    /// <summary>Hauteur de coupe des toits : au-dessus du sol (grille), à hauteur d'un étage.</summary>
    public const float RoofCut = 330f;

    // sol le plus haut autour du point (les cases de mur ont une hauteur 0 dans la grille, même dans un bâtiment
    // dont le plancher est surélevé, comme la banque de London à 4 m)
    private float GroundAt(float x, float y)
    {
        if (_session?.Grid is not { } g)
            return 0;
        (int cx, int cy) = g.CellOf(new SN.Vector3(x, y, 0));
        float best = float.MinValue;
        for (int dx = -2; dx <= 2; dx++)
            for (int dy = -2; dy <= 2; dy++)
                if (g.Inside(cx + dx, cy + dy) && g.ZoneA[g.Index(cx + dx, cy + dy)] != 0)
                    best = MathF.Max(best, _session.Units.GroundZ(new((cx + dx + 0.5f) * Grid.CellSize, (cy + dy + 0.5f) * Grid.CellSize)));
        return best == float.MinValue ? _session.Units.GroundZ(new(x, y)) : best;
    }

    private struct CutV { public SN.Vector3 P, N; public SN.Vector2 T0, TD, TL; }
    private static CutV Lerp(CutV a, CutV b, float t) => new()
    {
        P = SN.Vector3.Lerp(a.P, b.P, t), N = SN.Vector3.Lerp(a.N, b.N, t), T0 = SN.Vector2.Lerp(a.T0, b.T0, t),
        TD = SN.Vector2.Lerp(a.TD, b.TD, t), TL = SN.Vector2.Lerp(a.TL, b.TL, t),
    };

    // découpe d'un polygone par le plan z = cut : partie basse (below = true) ou haute
    private static List<CutV> Clip(List<CutV> poly, float cut, bool below)
    {
        List<CutV> res = new List<CutV>();
        for (int i = 0; i < poly.Count; i++)
        {
            CutV a = poly[i]; CutV b = poly[(i + 1) % poly.Count];
            bool ina = below ? a.P.Z <= cut : a.P.Z >= cut, inb = below ? b.P.Z <= cut : b.P.Z >= cut;
            if (ina)
                res.Add(a);
            if (ina != inb)
                res.Add(Lerp(a, b, (cut - a.P.Z) / (b.P.Z - a.P.Z)));
        }
        return res;
    }

    // ---- décor dessiné avec l'effet « sol / bâtiment » (SceneEffect) ----
    private sealed class SceneBatch { public readonly List<VertexScene> V = new(); public readonly List<int> I = new(); }
    private readonly Dictionary<(Texture2D? tex, Texture2D? dirt, Texture2D? lm, bool alpha, bool roof, int mode, bool caster, int bld), SceneBatch> _sceneBatches = new();
    private readonly List<(VertexBuffer vb, IndexBuffer ib, int count, Texture2D? tex, Texture2D? dirt, Texture2D? lm, bool alpha, bool roof, int mode, bool caster, int bld)> _sceneDraw = new();

    /// <summary>
    /// Jeux d'uv : couleur = premier, éclairage précalculé = dernier (vérifié à l'image), saleté = celui du milieu.
    /// (Les shaders du jeu nomment ces flux autrement : le moteur d'origine les range dans un autre ordre.)
    /// </summary>
    private static (float[] diff, float[] dirt, float[] light) UvSets(Mesh m, int mode)
    {
        List<int> present = Enumerable.Range(0, 8).Where(i => m.Uv[i] != null).ToList();
        if (present.Count == 0)
        {
            float[] z = new float[2 * m.VertexCount];
            return (z, z, z);
        }
        float[] Slot(int k) => m.Uv[present[Math.Min(k, present.Count - 1)]]!;
        return (Slot(0), Slot(1), Slot(present.Count - 1));
    }

    private static int ShaderMode(Mesh m) =>
        m.Shader.Contains("Ground", StringComparison.OrdinalIgnoreCase) ? SceneEffect.ModeGround
        : m.Shader.Contains("Building", StringComparison.OrdinalIgnoreCase) ? SceneEffect.ModeBuilding : SceneEffect.ModePlain;

    /// <summary>
    /// Décor : sol, bâtiments, pièces. Les bâtiments sont coupés à un étage au-dessus du sol (murs, plafonds et toits
    /// sont mêlés dans les mêmes maillages : London, plafond de la banque dans « Bank_fasad ») ; le bas reste visible,
    /// le haut (plafonds, étages, toit) disparaît avec la touche T, ce qui laisse voir les pièces.
    /// <paramref name="noCut"/> : sol (jamais coupé).
    /// </summary>
    private bool _dropRoof;
    private static (float min, float max) ZRange(SceneAssets.MeshAsset a, SN.Matrix4x4 world)
    {
        float zmin = float.MaxValue, zmax = float.MinValue;
        for (int i = 0; i < a.Mesh.VertexCount; i++)
        {
            float z = SN.Vector3.Transform(new SN.Vector3(a.Mesh.Positions[3 * i], a.Mesh.Positions[3 * i + 1], a.Mesh.Positions[3 * i + 2]), world).Z;
            zmin = MathF.Min(zmin, z); zmax = MathF.Max(zmax, z);
        }
        return (zmin, zmax);
    }   // partie haute jetée (sol d'un niveau souterrain : le dessus des parois)

    private void AddRoofCut(SceneAssets.MeshAsset a, SN.Matrix4x4 world, float? fixedCut = null, bool noCut = false, bool allRoof = false)
    {
        Mesh m = a.Mesh;
        int mode = ShaderMode(m);
        Texture2D? lm = a.Uv1 != null ? TexOf(a.Lightmap) : null;
        (float[]? uvD, float[]? uvDirt, float[]? uvL) = UvSets(m, mode);
        CutV V(int v) => new()
        {
            P = SN.Vector3.Transform(new SN.Vector3(m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2]), world),
            N = SN.Vector3.TransformNormal(new SN.Vector3(m.Normals[3 * v], m.Normals[3 * v + 1], m.Normals[3 * v + 2]), world),
            T0 = new(uvD[2 * v], uvD[2 * v + 1]), TD = new(uvDirt[2 * v], uvDirt[2 * v + 1]), TL = new(uvL[2 * v], uvL[2 * v + 1]),
        };
        foreach (SceneAssets.Group gr in a.Groups)
        {
            Texture2D? tex = TexOf(gr.Diffuse); Texture2D? dirt = mode != SceneEffect.ModePlain ? TexOf(gr.Dirt) : null;
            for (int k = gr.Start; k + 2 < gr.Start + gr.Count && k + 2 < m.Indices.Length; k += 3)
            {
                List<CutV> tri = new List<CutV> { V((int)m.Indices[k]), V((int)m.Indices[k + 1]), V((int)m.Indices[k + 2]) };
                if (noCut)
                {
                    EmitTri(tri[0], tri[1], tri[2], tex, dirt, lm, gr.Alpha, false, mode, false);
                    continue;
                }
                if (allRoof)
                {
                    EmitTri(tri[0], tri[1], tri[2], tex, dirt, lm, gr.Alpha, true, mode, true);
                    continue;
                }
                SN.Vector3 c = (tri[0].P + tri[1].P + tri[2].P) / 3;
                // plafonds (faces horizontales tournées vers le bas) : toujours avec le toit
                SN.Vector3 nz = SN.Vector3.Normalize(tri[0].N + tri[1].N + tri[2].N + new SN.Vector3(0, 0, 1e-6f));
                if (nz.Z < -0.7f)
                {
                    if (!_dropRoof)
                        EmitTri(tri[0], tri[1], tri[2], tex, dirt, lm, gr.Alpha, true, mode, true);
                    continue;
                }
                float cut = fixedCut ?? MathF.Max(0, GroundAt(c.X, c.Y)) + RoofCut;
                foreach (bool below in new[] { true, false })
                {
                    List<CutV> poly = Clip(tri, cut, below);
                    if (!below && _dropRoof)
                        continue;
                    for (int i = 1; i + 1 < poly.Count; i++)
                        EmitTri(poly[0], poly[i], poly[i + 1], tex, dirt, lm, gr.Alpha, !below, mode, true);
                }
            }
        }
    }

    // caster : projette une ombre (bâtiments, pièces ; pas le sol)
    private void EmitTri(CutV a, CutV b, CutV c, Texture2D? tex, Texture2D? dirt, Texture2D? lm, bool alpha, bool roof, int mode, bool caster)
    {
        (Texture2D? tex, Texture2D? dirt, Texture2D? lm, bool alpha, bool roof, int mode, bool caster, int _curBuilding) key = (tex, dirt, lm, alpha, roof, mode, caster, _curBuilding);
        if (!_sceneBatches.TryGetValue(key, out SceneBatch? bt))
            _sceneBatches[key] = bt = new SceneBatch();
        foreach (CutV v in new[] { a, b, c })
        {
            bt.I.Add(bt.V.Count);
            bt.V.Add(new VertexScene { Position = v.P.X(), Uv0 = v.T0.X(), UvDirt = v.TD.X(), UvLight = v.TL.X(), Normal = (v.N.LengthSquared() > 1e-12f ? SN.Vector3.Normalize(v.N) : SN.Vector3.UnitZ).X() });
        }
    }

    /// <summary>Objets posés cachés par le script et qui sont des toits (numéro → nom de l'objet posé).</summary>
    private readonly Dictionary<uint, string> _scriptRoofs = new();

    /// <summary>L'objet posé est-il un toit suspendu (une de ses pièces au moins) ?</summary>
    private bool IsOverhead(string mission, SceneAssets.Placed p)
    {
        try
        {
            return _assets.ModelParts(p.Model).Any(part => OverheadBox(_assets.LoadMesh(mission, part.Mesh, "gfx"), part.World * p.World) != null);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void AddStatic(SceneAssets.MeshAsset a, SN.Matrix4x4 world, bool roof, int layer, string? lightmap = null)
    {
        if (layer == 0)
        {
            AddRoofCut(a, world, noCut: !roof);
            return;
        }
        Mesh m = a.Mesh;
        Texture2D? lm = a.Uv1 != null ? TexOf(lightmap ?? a.Lightmap) : null;
        int[] map = new int[m.VertexCount];
        foreach (SceneAssets.Group gr in a.Groups)
        {
            Texture2D? tex = TexOf(gr.Diffuse);
            Array.Fill(map, -1);
            if (lm != null)
            {
                (Texture2D? tex, Texture2D lm, bool Alpha, bool roof) key = (tex, lm, gr.Alpha, roof);
                if (!_dualBatches.TryGetValue(key, out DualBatch? b))
                    _dualBatches[key] = b = new DualBatch();
                for (int k = gr.Start; k < gr.Start + gr.Count && k < m.Indices.Length; k++)
                {
                    int v = (int)m.Indices[k];
                    if (map[v] < 0)
                    {
                        map[v] = b.V.Count;
                        SN.Vector3 p = SN.Vector3.Transform(new SN.Vector3(m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2]), world);
                        b.V.Add(new VertexDual { Position = p.X(), Uv0 = new Vector2(a.Uv0[2 * v], a.Uv0[2 * v + 1]), Uv1 = new Vector2(a.Uv1![2 * v], a.Uv1[2 * v + 1]) });
                    }
                    b.I.Add(map[v]);
                }
            }
            else
            {
                (Texture2D? tex, bool Alpha, bool roof, int layer) key = (tex, gr.Alpha, roof, layer);
                if (!_litBatches.TryGetValue(key, out LitBatch? b))
                    _litBatches[key] = b = new LitBatch();
                for (int k = gr.Start; k < gr.Start + gr.Count && k < m.Indices.Length; k++)
                {
                    int v = (int)m.Indices[k];
                    if (map[v] < 0)
                    {
                        map[v] = b.V.Count;
                        SN.Vector3 p = SN.Vector3.Transform(new SN.Vector3(m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2]), world);
                        SN.Vector3 n = SN.Vector3.TransformNormal(new SN.Vector3(m.Normals[3 * v], m.Normals[3 * v + 1], m.Normals[3 * v + 2]), world);
                        if (n.LengthSquared() > 1e-12f)
                            n = SN.Vector3.Normalize(n);
                        b.V.Add(new VertexPositionNormalTexture(p.X(), n.X(), new Vector2(a.Uv0[2 * v], a.Uv0[2 * v + 1])));
                    }
                    b.I.Add(map[v]);
                }
            }
        }
    }

    private void FlushStatic()
    {
        foreach (((Texture2D? tex, Texture2D lm, bool alpha, bool roof) k, DualBatch? b) in _dualBatches)
        {
            if (b.I.Count == 0)
                continue;
            MeshGpu g = MeshGpu.CreateDual(GraphicsDevice, b.V.ToArray(), b.I.ToArray());
            g.Groups.Add((0, b.I.Count, k.tex, k.lm, k.alpha));
            _draw.Add((g, k.roof, 0));
        }
        foreach (((Texture2D? tex, bool alpha, bool roof, int layer) k, LitBatch? b) in _litBatches)
        {
            if (b.I.Count == 0)
                continue;
            MeshGpu g = MeshGpu.CreateLit(GraphicsDevice, b.V.ToArray(), b.I.ToArray());
            g.Groups.Add((0, b.I.Count, k.tex, null, k.alpha));
            _draw.Add((g, k.roof, k.layer));
        }
        _dualBatches.Clear(); _litBatches.Clear();
        foreach (((Texture2D? tex, Texture2D? dirt, Texture2D? lm, bool alpha, bool roof, int mode, bool caster, int bld) k, SceneBatch? b) in _sceneBatches)
        {
            if (b.I.Count == 0)
                continue;
            VertexBuffer vb = new VertexBuffer(GraphicsDevice, VertexScene.Decl, b.V.Count, BufferUsage.WriteOnly); vb.SetData(b.V.ToArray());
            IndexBuffer ib = new IndexBuffer(GraphicsDevice, IndexElementSize.ThirtyTwoBits, b.I.Count, BufferUsage.WriteOnly); ib.SetData(b.I.ToArray());
            _sceneDraw.Add((vb, ib, b.I.Count, k.tex, k.dirt, k.lm, k.alpha, k.roof, k.mode, k.caster, k.bld));
        }
        _sceneBatches.Clear();
    }

    private void LoadMission(string mission, string difficulty)
    {
        StopMenuVideo();
        _mission = mission; _difficulty = difficulty;
        if (CapturePath == null && !mission.Equals(_settings.LastMission, StringComparison.OrdinalIgnoreCase))
        {
            _settings.LastMission = mission;   // la campagne s'ouvrira sur cette mission au prochain lancement
            _settings.Save();
        }
        Status($"Chargement de {mission}...", 0);
        foreach ((MeshGpu mesh, bool roof, int layer) d in _draw)
            d.mesh.Dispose();
        foreach ((VertexBuffer vb, IndexBuffer ib, int count, Texture2D? tex, Texture2D? dirt, Texture2D? lm, bool alpha, bool roof, int mode, bool caster, int bld) d in _sceneDraw)
        {
            d.vb.Dispose();
            d.ib.Dispose();
        }
        _sceneDraw.Clear();
        foreach (Texture2D? t in _texCache.Values)
            t?.Dispose();
        foreach (List<(MeshGpu mesh, SN.Matrix4x4 part)> l in _unitModels.Values)
            foreach ((MeshGpu? m, _) in l)
                m.Dispose();
        foreach (MeshGpu? m in _partMeshes.Values)
            m?.Dispose();
        foreach (CharAnim c in _chars.Values)
            foreach ((MeshGpu mesh, float[] pos, float[] nrm) s in c.Skinned.Values)
                s.mesh.Dispose();
        _draw.Clear(); _fireRoom.Clear(); _texCache.Clear(); _log.Clear(); _unitModels.Clear(); _sel.Clear(); _unitLogSeen = 0; _scriptLogSeen = 0;
        _chars.Clear(); _rigs.Clear(); _partMeshes.Clear(); _lastAnimTime = -1; _prevPose.Clear(); _shownHeading.Clear();
        _audio?.StopAll();
        UnitManager.Nationality = _loadNat ?? _settings.Nationality; _gameNationality = UnitManager.Nationality;
        _journal = new();
        _session = new Session(_files, mission, difficulty);
        LoadSun();
        // les missions commencent tout de suite (cinématique d'introduction des scripts) ; sans script : en pause
        _paused = false; _speed = 1; _owed = 0;
        _cineOn = false; _cineMoving = false; _cineBars = 0; _subtitle = null; _messages.Clear(); _showMessageHistory = false;
        _ended = false; _endSoundPlayed = false; _menu = false;

        // décor (éclairage précalculé du jeu quand il existe)
        // plafond d'un niveau souterrain (Eurotunnel : « GROUND_ROOF », une dalle au-dessus de toute la carte) : vu de dessus,
        // le jeu d'origine ne le montre pas (face arrière) ; on ne le dessine pas
        List<SceneAssets.Node> scenery = _assets.Scenery(mission).Where(n => !System.Text.RegularExpressions.Regex.IsMatch(n.Mesh, @"^GROUND_?ROOF", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
        FindBuildings(mission, scenery);
        FindRooms();
        LoadLids(mission);
        BuildSurface(mission, scenery);
        int k = 0;
        foreach (SceneAssets.Node n in scenery)
        {
            if (++k % 16 == 0)
                Status($"Chargement du décor... {k} / {scenery.Count}", 0.05f + 0.45f * k / Math.Max(1, scenery.Count));
            // bâtiments (tout sauf le sol) : coupés à hauteur d'étage, le haut disparaît avec T (voir AddRoofCut)
            bool ground = System.Text.RegularExpressions.Regex.IsMatch(n.Mesh, @"^(FGD_|GROUND|Tile_ground)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            _curBuilding = _nodeBuilding.TryGetValue(n.Mesh, out int bi) ? bi : -1;
            try
            {
                SceneAssets.MeshAsset asset = _assets.LoadMesh(mission, n.Mesh, null);
                if (!ground && n.Mesh.EndsWith("_ROOF", StringComparison.OrdinalIgnoreCase) && _curBuilding >= 0)
                {
                    // toit nommé (« M_Parn_12_ROOF » de Maidan : la place au-dessus d'une salle souterraine ; « CompClub_ROOF »…) :
                    // tout entier un toit, que le script retire (LEV_RoofVisibility) ou qui part quand un pompier passe dessous
                    AddRoofCut(asset, n.World, allRoof: true);
                }
                else if (n.Mesh.Contains("_ROOM", StringComparison.OrdinalIgnoreCase))
                {
                    // pièce (sol, cloisons, plafond) : coupée aux 4/5 de sa hauteur, ses cloisons restent visibles sans toit
                    float zmin = float.MaxValue, zmax = float.MinValue;
                    for (int i = 0; i < asset.Mesh.VertexCount; i++)
                    {
                        float z = SN.Vector3.Transform(new SN.Vector3(asset.Mesh.Positions[3 * i], asset.Mesh.Positions[3 * i + 1], asset.Mesh.Positions[3 * i + 2]), n.World).Z;
                        zmin = MathF.Min(zmin, z); zmax = MathF.Max(zmax, z);
                    }
                    AddRoofCut(asset, n.World, zmin + 0.8f * MathF.Max(100, zmax - zmin));
                }
                else if (ground && !n.Mesh.StartsWith("FGD_", StringComparison.OrdinalIgnoreCase) && !n.Mesh.StartsWith("Tile_", StringComparison.OrdinalIgnoreCase)
                         && ZRange(asset, n.World) is (float min, float max) zr && zr.max - zr.min > 300)
                {
                    // « GROUND » d'un niveau souterrain (Eurotunnel) : sol et parois du tunnel, avec la roche par-dessus ;
                    // vu de dessus, le jeu ne montre pas cette roche : on coupe à 3 m et on jette le haut
                    _dropRoof = true;
                    try { AddRoofCut(asset, n.World, zr.min + 0.75f * (zr.max - zr.min)); } finally { _dropRoof = false; }
                }
                else
                    AddStatic(asset, n.World, !ground, 0);
            }
            catch (Exception e) { Console.WriteLine($"{n.Mesh} : {e.Message}"); }
        }
        _curBuilding = -1;
        // objets posés (sans les portes et fenêtres, dessinées à part suivant leur état)
        HashSet<string> openingNames = _session.Units.Openings.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // objets explosifs (voitures…) : dessinés à part, selon leur état (intact, abîmé, épave)
        HashSet<string> explosiveNames = ExplosiveNames();
        List<SceneAssets.Placed> allPlaced = _assets.Placeables(_session.Level).Where(p => !p.Civilian && !openingNames.Contains(p.Name)).Select(p => p with { World = Lift(p.World) }).ToList();
        LoadExplosives(allPlaced.Where(p => explosiveNames.Contains(p.Name)));
        // repères d'objectif (drapeaux « rally_symbol ») : cachés jusqu'à ce que le script les montre (ENT_SetVisibility)
        _scriptShown.Clear();
        Dictionary<string, LevelObject> byName = _session.Level.Objects.GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (SceneAssets.Placed p in allPlaced.Where(p => p.Model.Contains("rally_symbol", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("rally_symbol", StringComparison.OrdinalIgnoreCase)))
            if (byName.TryGetValue(p.Name, out LevelObject? lo))
                _scriptShown.Add((lo.Id, p.Model, p.World));
        // objets que le script déplace (ENT_SetPosition(numéro, …) : le train de l'Eurotunnel) ou cache / montre
        // (ENT_SetVisibility(numéro, …) : la porte fixe du hangar de Mriya, remplacée par la porte animée) : dessinés à part,
        // à leur place du moment et seulement s'ils sont visibles
        _scriptMoved.Clear(); _scriptRoofs.Clear(); _glide.Clear();
        string scriptDir = Path.Combine(_files.DataDir, "Script", "Mission", mission);
        if (Directory.Exists(scriptDir))
        {
            HashSet<uint> movedIds = new HashSet<uint>(), hiddenIds = new HashSet<uint>();
            foreach (string f in Directory.EnumerateFiles(scriptDir, "*.scp"))
                foreach (System.Text.RegularExpressions.Match mm in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f, System.Text.Encoding.Latin1), @"ENT_Set(Position|Visibility)\s*\(\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    (mm.Groups[1].Value.Equals("Position", StringComparison.OrdinalIgnoreCase) ? movedIds : hiddenIds).Add(uint.Parse(mm.Groups[2].Value));
            // objets que le script cache et montre : cachés au départ si le niveau le dit (Mriya : le camion de la
            // cinématique 3, la porte animée du hangar), tant que le script n'a rien dit d'autre
            if (Host is { } host0)
                foreach (LevelObject lo0 in _session.Level.Objects.Where(o => hiddenIds.Contains(o.Id) && o.Kind == EntityKind.Placeable && !o.StartVisible))
                    host0.Visibility.TryAdd(lo0.Id, false);
            foreach (SceneAssets.Placed p in allPlaced)
            {
                // (véhicules explosifs : dessinés par RenderExplosives, qui suit aussi le script — sinon en double)
                if (explosiveNames.Contains(p.Name) || !byName.TryGetValue(p.Name, out LevelObject? lo) || _scriptShown.Any(x => x.id == lo.Id) || !(movedIds.Contains(lo.Id) || hiddenIds.Contains(lo.Id)))
                    continue;
                // toit posé (« angar_roof » de Mriya) que le script cache : il reste un toit (il part aussi quand un pompier
                // passe dessous), et le script peut en plus le faire partir (voir UpdateRoofs)
                if (!movedIds.Contains(lo.Id) && IsOverhead(mission, p))
                {
                    _scriptRoofs[lo.Id] = p.Name;
                    continue;
                }
                _scriptMoved.Add((lo.Id, p.Model, p.World, lo.Position));
            }
        }
        // gravats à dégager : leur morceau cassé (« …_break ») n'apparaît qu'une fois l'obstacle dégagé ; le repère de hache
        // disparaît alors
        _axeMarks.Clear(); _brokenParts.Clear();
        List<Opening> obstacles = _session.Units.Openings.Where(o => o.IsObstacle).ToList();
        foreach (SceneAssets.Placed p in allPlaced)
        {
            if (!byName.TryGetValue(p.Name, out LevelObject? lo))
                continue;
            Opening? near = obstacles.Where(o => SN.Vector3.Distance(o.Position, lo.Position) < 500).OrderBy(o => SN.Vector3.Distance(o.Position, lo.Position)).FirstOrDefault();
            if (near == null)
                continue;
            if (lo.ClassPath.EndsWith("axe_symbol", StringComparison.OrdinalIgnoreCase))
                _axeMarks.Add((lo.Id, p.Model, p.World, near));
            else if (p.Name.Contains("_break", StringComparison.OrdinalIgnoreCase) || p.Model.Contains("break", StringComparison.OrdinalIgnoreCase))
                _brokenParts.Add((lo.Id, p.Model, p.World, near));
        }
        // repères animés : rouages d'un mécanisme (interrupteur) qui tournent sur eux-mêmes, flèches qui avancent et reculent
        _animMarks.Clear();
        foreach (SceneAssets.Placed p in allPlaced)
            if (byName.TryGetValue(p.Name, out LevelObject? lo) && System.Text.RegularExpressions.Regex.IsMatch(lo.ClassPath, @"(mechanism|mechanism_symbol_blue|elec|elec_symbol_off|repair|buldo|arrow|koshka)_symbol$|mechanism_symbol_blue$|elec_symbol_off$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                _animMarks.Add((lo.Id, p.Model, p.World, System.Text.RegularExpressions.Regex.IsMatch(lo.ClassPath, @"(arrow|koshka)_symbol$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)));
        HashSet<string> shownNames = _scriptShown.Select(x => byName.Values.First(o => o.Id == x.id).Name)
            .Concat(_animMarks.Select(x => byName.Values.First(o => o.Id == x.id).Name))
            .Concat(_axeMarks.Concat(_brokenParts).Select(x => byName.Values.First(o => o.Id == x.id).Name))
            .Concat(_scriptMoved.Select(x => byName.Values.First(o => o.Id == x.id).Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<IGrouping<string, SceneAssets.Placed>> placed = allPlaced.Where(p => !explosiveNames.Contains(p.Name) && !shownNames.Contains(p.Name)).GroupBy(p => p.Model, StringComparer.OrdinalIgnoreCase).ToList();
        k = 0;
        foreach (IGrouping<string, SceneAssets.Placed> grp in placed)
        {
            if (++k % 20 == 0)
                Status($"Chargement des objets... {k} / {placed.Count}", 0.5f + 0.4f * k / Math.Max(1, placed.Count));
            try
            {
                foreach (SceneAssets.Node part in _assets.ModelParts(grp.Key))
                {
                    SceneAssets.MeshAsset a = _assets.LoadMesh(mission, part.Mesh, "gfx");
                    foreach (SceneAssets.Placed p in grp)
                    {
                        SN.Matrix4x4 w = part.World * p.World;
                        // objet suspendu (verrière, auvent, passerelle…) : un toit à lui seul, qui part quand un pompier est dessous
                        if (OverheadBox(a, w) is { } ob)
                        {
                            _curBuilding = _buildings.Count;
                            Building b = new Building { Min = ob.min, Max = ob.max, MinZ = ob.zmin }; b.Nodes.Add(p.Name); _buildings.Add(b);
                            AddRoofCut(a, w, allRoof: true);
                            _curBuilding = -1;
                        }
                        else
                            AddStatic(a, w, false, 1);
                    }
                }
            }
            catch (Exception e) { Console.WriteLine($"{grp.Key} : {e.Message}"); }
        }
        Status("Croissance des arbres...", 0.92f);
        LoadTrees();
        Status("Envoi à la carte graphique...", 0.97f);
        FlushStatic();
        // cadrage
        _sceneMin = new SN.Vector3(float.MaxValue); _sceneMax = new SN.Vector3(float.MinValue);
        foreach (HotPoint h in _session.Fire.HotPoints)
        {
            _sceneMin = SN.Vector3.Min(_sceneMin, h.Position);
            _sceneMax = SN.Vector3.Max(_sceneMax, h.Position);
        }
        if (_session.Grid is { } grid)
        {
            _sceneMin = new(0, 0, 0);
            _sceneMax = new(grid.Width * Grid.CellSize, grid.Height * Grid.CellSize, 0);
        }
        _target = (_sceneMin + _sceneMax) / 2; _target.Z = 0;
        _dist = MathF.Max(_sceneMax.X - _sceneMin.X, _sceneMax.Y - _sceneMin.Y) * 0.75f;
        _mapCenter = new(_target.X, _target.Y); _mapScale = 0;
        BuildGridTexture();
        LoadMinimap();
        Window.Title = $"Open Fire Department - {mission} ({DiffName})";
    }

    private string DiffName => _difficulty switch { "EASY" => "facile", "HARD" => "difficile", _ => "normal" };

    // arbres EZ-Tree : quelques variantes par espèce, mises à l'échelle de la hauteur prévue par la carte
    private void LoadTrees()
    {
        List<SceneAssets.TreeInfo> trees = _assets.Trees(_session.Level);
        Dictionary<string, (SceneAssets.MeshAsset bark, SceneAssets.MeshAsset leaves, float h, float y0)> variants = new Dictionary<string, (SceneAssets.MeshAsset bark, SceneAssets.MeshAsset leaves, float h, float y0)>();
        string Preset(string m) => System.Text.RegularExpressions.Regex.IsMatch(m, "conif|pin", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "pine_medium"
            : m.Contains("pleur", StringComparison.OrdinalIgnoreCase) ? "aspen_medium"
            : System.Text.RegularExpressions.Regex.IsMatch(m, "feuillu0[24]", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "oak_medium" : "ash_medium";
        string dir = Path.Combine(AppContext.BaseDirectory, "Assets", "trees");
        foreach (SceneAssets.TreeInfo t in trees)
        {
            string key = Preset(t.Model) + "#" + (t.Seed % 3);
            if (!variants.ContainsKey(key))
            {
                EzTree.TreeGenerator.Options o = OpenFD.EzTree.TreeGenerator.Options.FromJson(File.ReadAllText(Path.Combine(dir, Preset(t.Model) + ".json")));
                o.Seed = 1000 + t.Seed % 3;
                EzTree.TreeGenerator tr = new OpenFD.EzTree.TreeGenerator(o); tr.Generate();
                float ymin = float.MaxValue, ymax = float.MinValue;
                for (int i = 1; i < tr.BranchPos.Count; i += 3)
                {
                    ymin = MathF.Min(ymin, tr.BranchPos[i]);
                    ymax = MathF.Max(ymax, tr.BranchPos[i]);
                }
                for (int i = 1; i < tr.LeafPos.Count; i += 3)
                {
                    ymin = MathF.Min(ymin, tr.LeafPos[i]);
                    ymax = MathF.Max(ymax, tr.LeafPos[i]);
                }
                float[] barkUv = tr.BranchUv.Select((v, i) => i % 2 == 0 ? v * o.BarkScale.X : v * o.BarkScale.Y).ToArray();
                SceneAssets.MeshAsset Asset(List<float> pos, List<float> nrm, float[] uv, List<uint> idx, string texKey, bool alpha) => new()
                {
                    Mesh = new Mesh { Positions = pos.ToArray(), Normals = nrm.ToArray(), Indices = idx.ToArray(), VertexCount = pos.Count / 3 },
                    Groups = new() { new SceneAssets.Group(0, idx.Count, "", texKey, alpha) }, Uv0 = uv, Uv1 = null,
                };
                SceneAssets.MeshAsset bark = Asset(tr.BranchPos, tr.BranchNrm, barkUv, tr.BranchIdx, "ez:bark_" + o.BarkType, false);
                SceneAssets.MeshAsset leaves = Asset(tr.LeafPos, tr.LeafNrm, tr.LeafUv.ToArray(), tr.LeafIdx, "ez:leaves_" + o.LeafType, true);
                TreeTex("bark_" + o.BarkType); TreeTex("leaves_" + o.LeafType);
                variants[key] = (bark, leaves, ymax - ymin, ymin);
            }
            (SceneAssets.MeshAsset bark, SceneAssets.MeshAsset leaves, float h, float y0) v = variants[key];
            float s = t.Height / v.h;
            // EZ-Tree pousse vers +Y ; le jeu a Z vers le haut
            SN.Matrix4x4 w = SN.Matrix4x4.CreateTranslation(0, -v.y0, 0) * SN.Matrix4x4.CreateScale(s) * SN.Matrix4x4.CreateRotationX(MathF.PI / 2) * Lift(t.World);
            AddStatic(v.bark, w, false, 2); AddStatic(v.leaves, w, false, 2);
        }
    }

    private void TreeTex(string name)
    {
        if (_texCache.ContainsKey("ez:" + name))
            return;
        string p = Path.Combine(AppContext.BaseDirectory, "Assets", "trees", name + ".tga");
        _texCache["ez:" + name] = File.Exists(p) && Tex.DecodeTga(File.ReadAllBytes(p)) is { } img ? Tex.FromRgba(GraphicsDevice, img.w, img.h, img.rgba) : null;
    }

    private void BuildGridTexture()
    {
        _gridTex?.Dispose(); _gridTex = null;
        Grid? g = _session.Grid;
        if (g == null)
            return;
        float gmin = g.GroundHeight.DefaultIfEmpty(0).Min(), gmax = g.GroundHeight.DefaultIfEmpty(1).Max();
        Color[] px = new Color[g.Width * g.Height];
        for (int cy = 0; cy < g.Height; cy++)
            for (int cx = 0; cx < g.Width; cx++)
            {
                int i = g.Index(cx, cy), o = (g.Height - 1 - cy) * g.Width + cx;   // nord en haut
                bool wall = i < g.ZoneA.Length && g.ZoneA[i] == 0;
                if (!wall && _session.Units.IsBlocked(i)) { px[o] = new Color(70, 52, 40); continue; }   // objets qui bloquent le passage
                float h = i < g.GroundHeight.Length && gmax > gmin ? (g.GroundHeight[i] - gmin) / (gmax - gmin) : 0;
                byte v = (byte)(22 + h * 30);
                px[o] = wall ? new Color(74, 74, 85) : new Color(v, v, (byte)(v + 3));
            }
        _gridTex = Tex.FromColors(GraphicsDevice, g.Width, g.Height, px, false);
        _gridW = g.Width * Grid.CellSize; _gridH = g.Height * Grid.CellSize;
    }

    // ------------------------------------------------------------------ simulation
    private void Simulate(float dt)
    {
        if (_netGame && _net != null)
        {
            SimulateNet(dt);
            return;
        }
        if (_paused)
        {
            _owed = 0;
            return;
        }
        _owed += dt * _speed / Session.Dt;
        int n = (int)Math.Min(_owed, 400); _owed -= n;
        int before = _session.Fire.ExtinguishLog.Count;
        for (int i = 0; i < n; i++)
        {
            if (i == n - 1)
                SnapshotPoses();
            _session.Step();
        }
        foreach (string l in _session.Fire.ExtinguishLog.Skip(before))
            Log("Éteint : " + l.Trim());
        PullLogs();
    }

    /// <summary>Recopie dans le journal de débogage (touche D) les nouvelles lignes des unités et les erreurs de script.</summary>
    private void PullLogs()
    {
        List<string> ul = _session!.Units.Log;
        for (; _unitLogSeen < ul.Count; _unitLogSeen++)
            Log(ul[_unitLogSeen].Trim());
        if (_session.Scripts is { } sc)
            for (; _scriptLogSeen < sc.Log.Count; _scriptLogSeen++)
            {
                Log("Script : " + sc.Log[_scriptLogSeen].Trim(), true);
                Console.WriteLine("Erreur de script : " + sc.Log[_scriptLogSeen]);
            }
    }

    private void Log(string s, bool error = false)
    {
        _log.Add((s, error));
        if (_log.Count > 6)
            _log.RemoveAt(0);
    }

    // ------------------------------------------------------------------ soleil et ombres
    private SN.Vector3 _sunDir = SN.Vector3.Normalize(new SN.Vector3(0.4f, 0.3f, -0.85f));   // sens de la lumière (vers le sol)
    private float _shadowOpacity = 0.5f;
    private float _lmScale = 2f;    // multiplicateur de l'éclairage précalculé (2 le jour, 1,1 pour la nuit de Fireworks)
    private SN.Vector3 _tint = SN.Vector3.One;   // teinte d'ensemble : blanc le jour, plus sombre et bleutée la nuit
    private SN.Vector3 _sunColor = SN.Vector3.One;
    private bool _shadowPass, _shadowsOn = true;
    private RenderTarget2D? _shadowMap;
    private Matrix _lightVp = Matrix.Identity;
    private DepthEffect _depth = null!;
    private const int ShadowSize = 2048;

    /// <summary>
    /// Soleil de la mission : objet lumière de la carte (classe base_sun, [LIGHT] LightType=SUN). Sa direction est
    /// l'axe Z de son orientation (London : vers le nord-ouest et vers le bas). [SHADOWS] Shadow_Opacity : force de l'ombre.
    /// </summary>
    private void LoadSun()
    {
        _lmScale = 2f; _sunColor = SN.Vector3.One; _shadowOpacity = 0.5f; _tint = SN.Vector3.One;
        _sunDir = SN.Vector3.Normalize(new SN.Vector3(0.4f, 0.3f, -0.85f));
        foreach (LevelObject o in _session.Level.Objects.Where(o => o.Kind == EntityKind.Light && o.HasTransform))
        {
            ClassFile? c = null;
            try { c = ClassFile.Load(_files, "Design\\" + o.ClassPath["design\\".Length..] + ".class"); } catch { }
            if (c?.Get("LIGHT", "LightType")?.Trim('"', ' ').Equals("SUN", StringComparison.OrdinalIgnoreCase) != true)
                continue;
            SN.Matrix4x4 m = o.Matrix; SN.Vector3 d = new SN.Vector3(m.M31, m.M32, m.M33);
            if (d.LengthSquared() < 1e-6f)
                continue;
            _sunDir = SN.Vector3.Normalize(d);
            if (_sunDir.Z > 0)
                _sunDir = -_sunDir;   // toujours vers le sol
            _shadowOpacity = Math.Clamp(c.GetFloat("SHADOWS", "Shadow_Opacity", 0.5f), 0, 1);
            if (!c.GetBool("SHADOWS", "Cast_shadows", true))
                _shadowOpacity = 0;
            ReadSunComponent(o);
            break;
        }
        // un peu plus marquée que la valeur du jeu (demande du joueur : 0,5 paraissait trop clair)
        _shadowOpacity = MathF.Min(0.85f, _shadowOpacity * 1.4f);
        // nuit : le soleil de la mission a un éclairage précalculé faible (Fireworks : 1,1 au lieu de 2). Le jeu d'origine garde
        // pourtant le facteur 2 dans ses shaders (ses textures d'éclairage sont déjà sombres) ; ici on assombrit tout un peu,
        // avec une pointe de bleu, en restant assez clair pour bien voir.
        _tint = _lmScale < 1.5f ? new SN.Vector3(0.6f, 0.63f, 0.74f) : SN.Vector3.One;
        _lmScale = 2f;
        Console.WriteLine($"Soleil : direction {_sunDir}, teinte {_tint}, ombres {_shadowOpacity:0.##}");
        foreach (BasicEffect e in new[] { _lit, _plain })
        {
            e.DirectionalLight0.Direction = _sunDir.X(); e.DirectionalLight1.Direction = -_sunDir.X();
        }
    }

    /// <summary>
    /// Réglages propres à chaque soleil, enregistrés dans la carte (composant « FD2_LIGHT » de l'objet) :
    ///  • lumière en temps réel : ambiante, diffuse (BGRA), multiplicateur ;
    ///  • éclairage précalculé : ambiante, diffuse, multiplicateur (London 2, Fireworks 1,1 : c'est ce qui fait la nuit) ;
    ///  • ombres : projetées (0/1), opacité (London 0,5, Fireworks 0,28)…
    /// </summary>
    private void ReadSunComponent(LevelObject o)
    {
        _lmScale = 2f; _sunColor = SN.Vector3.One;
        byte[] tag = System.Text.Encoding.ASCII.GetBytes("FD2_LIGHT\0");
        foreach (Component comp in o.Components)
        {
            byte[] b = o.ComponentBytes(comp).ToArray();
            int at = b.AsSpan().IndexOf(tag);
            if (at < 0 || b.Length < at + tag.Length + 39)
                continue;
            int p = at + tag.Length;
            float F(int off) => BitConverter.ToSingle(b, p + off);
            _sunColor = new SN.Vector3(b[p + 6], b[p + 5], b[p + 4]) / 255f;
            float lm = F(30);
            if (lm > 0.2f && lm < 8)
                _lmScale = lm;
            if (b[p + 34] == 0)
                _shadowOpacity = 0;
            else
            {
                float op = F(35);
                if (op >= 0 && op <= 1)
                    _shadowOpacity = op;
            }
            return;
        }
    }

    // carte d'ombres : profondeur, vue du soleil, de ce qui n'a pas d'ombre précalculée (personnes, véhicules, arbres)
    // et des bâtiments (l'éclairage précalculé du sol ne contient pas toujours leur ombre : banque de London)
    private void RenderShadowMap()
    {
        if (!_shadowsOn || _shadowOpacity <= 0)
            return;
        GraphicsDevice gd = GraphicsDevice;
        if (_shadowMap == null)
        {
            try { _shadowMap = new RenderTarget2D(gd, ShadowSize, ShadowSize, false, SurfaceFormat.Single, DepthFormat.Depth24); }
            catch { _shadowsOn = false; return; }
        }
        SN.Vector3 center = new SN.Vector3(_target.X, _target.Y, GroundAt(_target.X, _target.Y));
        float size = Math.Clamp(_dist * 2.2f, 3000, 40000);
        SN.Vector3 up = MathF.Abs(_sunDir.Z) > 0.99f ? SN.Vector3.UnitY : SN.Vector3.UnitZ;
        SN.Matrix4x4 view = SN.Matrix4x4.CreateLookAt(center - _sunDir * 30000, center, up);
        SN.Matrix4x4 proj = SN.Matrix4x4.CreateOrthographic(size, size, 1, 60000);
        _lightVp = (view * proj).X();
        gd.SetRenderTarget(_shadowMap);
        gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.White, 1, 0);
        gd.DepthStencilState = DepthStencilState.Default; gd.BlendState = BlendState.Opaque; gd.RasterizerState = RasterizerState.CullNone;
        gd.SamplerStates[0] = SamplerState.LinearWrap;
        _shadowPass = true;
        try
        {
            foreach ((VertexBuffer vb, IndexBuffer ib, int count, Texture2D? tex, Texture2D? dirt, Texture2D? lm, bool alpha, bool roof, int mode, bool caster, int bld) d in _sceneDraw)
            {
                if (!d.caster || d.roof && !RoofShown(d.bld))
                    continue;
                _depth.Set(Matrix.Identity * _lightVp, d.alpha ? d.tex : null);
                gd.SetVertexBuffer(d.vb); gd.Indices = d.ib;
                foreach (EffectPass pass in _depth.Fx.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, d.count / 3);
                }
            }
            foreach ((MeshGpu? mesh, bool roof, int layer) in _draw)
                if (layer == 2 && _objects)
                    DrawMesh(mesh, Matrix.Identity);   // arbres
            RenderUnits();
        }
        finally { _shadowPass = false; gd.SetRenderTarget(null); }
    }

    // ------------------------------------------------------------------ rendu 3D
    private static readonly SN.Vector3 Sky = new(0f, 0f, 0f);   // hors de la carte : noir, comme dans le jeu

    private static readonly RasterizerState SceneCull = Environment.GetEnvironmentVariable("FD3_CULL") == "1" ? RasterizerState.CullCounterClockwise : RasterizerState.CullClockwise;

    private void Render3D(float time)
    {
        float brightSaved = _bright; _bright *= 1 + 0.6f * _flash;   // éclair d'une explosion
        try { Render3DCore(time); } finally { _bright = brightSaved; }
    }

    private void Render3DCore(float time)
    {
        GraphicsDevice gd = GraphicsDevice;
        gd.Clear(new Color(Sky.X, Sky.Y, Sky.Z));
        gd.DepthStencilState = DepthStencilState.Default; gd.BlendState = BlendState.Opaque; gd.RasterizerState = RasterizerState.CullNone;
        gd.SamplerStates[0] = SamplerState.AnisotropicWrap; gd.SamplerStates[1] = SamplerState.AnisotropicWrap; gd.SamplerStates[2] = SamplerState.LinearWrap; gd.SamplerStates[3] = SamplerState.PointClamp;
        _eye = _target + _dist * new SN.Vector3(MathF.Cos(_pitch) * MathF.Cos(_yaw), MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch));
        SN.Vector3 shake = ShakeOffset(); _eye += shake;
        _view = SN.Matrix4x4.CreateLookAt(_eye, _target + shake, SN.Vector3.UnitZ);
        _proj = SN.Matrix4x4.CreatePerspectiveFieldOfView(50f * MathF.PI / 180, (float)_fbW / _fbH, 20f, 80000f);
        _viewProj = _view * _proj;
        Matrix view = _view.X(); Matrix proj = _proj.X();
        foreach (IEffectMatrices e in new IEffectMatrices[] { _lit, _plain, _dual, _alphaFx, _part, _ghost, _burnt })
        {
            e.View = view;
            e.Projection = proj;
            e.World = Matrix.Identity;
        }
        // luminosité réglable (touches + et −) : multiplie l'éclairage du décor, des objets et des personnages
        foreach (BasicEffect e in new[] { _lit, _plain })
        {
            e.AmbientLightColor = (0.62f * _bright * _tint).X();
            e.DirectionalLight0.DiffuseColor = (0.55f * _bright * _tint * _sunColor).X();
            e.DirectionalLight1.DiffuseColor = (0.55f * _bright * _tint).X();
        }
        _dual.DiffuseColor = (_bright * _tint).X(); _alphaFx.DiffuseColor = (0.95f * _bright * _tint).X();
        foreach (IEffectFog f in new IEffectFog[] { _lit, _plain, _dual, _alphaFx })
        {
            f.FogColor = Sky.X();
            f.FogStart = 30000;
            f.FogEnd = 90000;
        }

        // décor : effet « sol / bâtiment » d'après les shaders du jeu
        _scene.SetFrame((_viewProj).X(), _eye.X(), 30000, 90000, Sky.X(), _tint.X());
        _scene.SetShadow(_shadowsOn && _shadowOpacity > 0 ? _shadowMap : null, _lightVp, _shadowOpacity, 0.0008f, _sunDir.X());
        // cinématique : comme le moteur d'origine, les faces vues de dos ne sont pas dessinées ; une caméra placée
        // dans un bâtiment (London : plan sur les boutiques depuis le cinéma) voit ainsi au travers du mur
        gd.RasterizerState = _cineOn ? SceneCull : RasterizerState.CullNone;
        foreach ((VertexBuffer vb, IndexBuffer ib, int count, Texture2D? tex, Texture2D? dirt, Texture2D? lm, bool alpha, bool roof, int mode, bool caster, int bld) d in _sceneDraw)
        {
            if (d.roof && !RoofShown(d.bld))
                continue;
            _scene.Set(Matrix.Identity, d.tex, d.dirt, d.lm, d.mode, d.alpha, _bright, _lmScale);
            gd.SetVertexBuffer(d.vb); gd.Indices = d.ib;
            foreach (EffectPass pass in _scene.Fx.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, d.count / 3);
            }
        }
        gd.RasterizerState = RasterizerState.CullNone;
        RenderLids(_viewProj.X());   // couvercles noirs des pièces pas encore atteintes
        gd.SamplerStates[1] = SamplerState.AnisotropicWrap; gd.SamplerStates[2] = SamplerState.LinearWrap;
        foreach ((MeshGpu? mesh, bool roof, int layer) in _draw)
        {
            if (roof && !_roofs)
                continue;
            if (layer > 0 && !_objects)
                continue;
            DrawMesh(mesh, Matrix.Identity);
        }
        RenderUnits();
        RenderOpenings();
        RenderExplosives();
        RenderGhosts();
        RenderSelectionRings(_viewProj.X());   // après les unités et les véhicules : ce qui passe derrière eux est caché
        RenderParticles(time);
    }

    private void DrawMesh(MeshGpu mesh, Matrix world)
    {
        GraphicsDevice gd = GraphicsDevice;
        gd.SetVertexBuffer(mesh.Vb); gd.Indices = mesh.Ib;
        if (_shadowPass)
        {
            // carte d'ombres : seulement la profondeur vue du soleil (détourage pour les feuillages)
            if (mesh.Dual)
                return;
            foreach ((int start, int count, Texture2D? tex, _, bool alpha) in mesh.Groups)
            {
                _depth.Set(world * _lightVp, alpha ? tex : null);
                foreach (EffectPass pass in _depth.Fx.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, start, count / 3);
                }
            }
            return;
        }
        if (_ghostPass)
        {
            // silhouette d'un pompier caché par un mur : vert uni, avancé vers la caméra pour ne pas se cacher lui-même
            _ghost.World = world * Matrix.CreateTranslation(_ghostShift);
            foreach ((int start, int count, _, _, _) in mesh.Groups)
                foreach (EffectPass pass in _ghost.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, start, count / 3);
                }
            return;
        }
        foreach ((int start, int count, Texture2D? tex, Texture2D? lm, bool alpha) in mesh.Groups)
        {
            Effect fx;
            if (mesh.Dual && lm != null)
            {
                _dual.Texture = tex ?? Tex.White;
                _dual.Texture2 = lm;
                _dual.World = world;
                fx = _dual;
            }
            else if (mesh.Dual)
                continue;
            else if (alpha && tex != null)
            {
                _alphaFx.Texture = tex;
                _alphaFx.World = world;
                fx = _alphaFx;
            }
            else if (tex != null)
            {
                _lit.Texture = tex;
                _lit.World = world;
                fx = _lit;
            }
            else
            {
                _plain.World = world;
                fx = _plain;
            }
            // véhicule détruit : carcasse calcinée, presque noire (comme un objet entièrement consumé) ; effet sans éclairage,
            // la teinte sombre s'applique telle quelle
            if (_charred)
            {
                _burnt.TextureEnabled = tex != null;
                _burnt.Texture = tex;
                _burnt.World = world;
                fx = _burnt;
            }
            foreach (EffectPass pass in fx.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, start, count / 3);
            }
        }
    }

    private bool _charred;   // DrawMesh : dessin d'une carcasse calcinée

    private List<(MeshGpu mesh, SN.Matrix4x4 part)> UnitModel(string model)
    {
        if (_unitModels.TryGetValue(model, out List<(MeshGpu mesh, SN.Matrix4x4 part)>? l))
            return l;
        l = new();
        try { foreach (SceneAssets.Node part in _assets.ModelParts(model)) l.Add((Upload(_assets.LoadMesh(_mission, part.Mesh, "gfx")), part.World)); }
        catch (Exception e) { Console.WriteLine($"{model} : {e.Message}"); }
        _unitModels[model] = l;
        return l;
    }

    // portes : fermées telles quelles, ouvertes pivotées d'un quart de tour, cassées retirées ; fenêtres cassées retirées
    private readonly List<(uint id, string model, SN.Matrix4x4 world, Opening obstacle)> _axeMarks = new(), _brokenParts = new();
    private readonly List<(uint id, string model, SN.Matrix4x4 world, bool arrow)> _animMarks = new();

    private void RenderOpenings()
    {
        bool Vis(uint id, bool def) => Host?.Visibility.TryGetValue(id, out bool v) == true ? v : def;
        foreach ((uint id, string? model, SN.Matrix4x4 world, Opening? ob) in _axeMarks)
            if (ob.State != OpeningState.Broken && Vis(id, true))
                foreach ((MeshGpu? mesh, SN.Matrix4x4 part) in UnitModel(model))
                    DrawMesh(mesh, (part * world).X());
        foreach ((uint id, string? model, SN.Matrix4x4 world, bool arrow) in _animMarks)
        {
            if (!Vis(id, true))
                continue;
            // rouages : un tour toutes les 3 s autour de leur axe ; flèche : va et vient d'environ 20 cm
            SN.Vector3 t = world.Translation; SN.Matrix4x4 w = world; w.Translation = SN.Vector3.Zero;
            // flèche du grappin (si_Koshka_sign_01) : va et vient de 55 unités vers l'arrière et le haut, en 1,2 s
            float bob = 0.5f - 0.5f * MathF.Cos(_visTime * MathF.Tau / 1.2f);
            SN.Matrix4x4 anim = arrow ? SN.Matrix4x4.CreateTranslation(0, -55 * bob, 55 * bob) * w * SN.Matrix4x4.CreateTranslation(t)
                : SN.Matrix4x4.CreateRotationZ(_visTime * 2.1f) * w * SN.Matrix4x4.CreateTranslation(t);
            foreach ((MeshGpu? mesh, SN.Matrix4x4 part) in UnitModel(model))
                DrawMesh(mesh, (part * anim).X());
        }
        foreach ((uint id, string? model, SN.Matrix4x4 world, Opening? ob) in _brokenParts)
            if (Vis(id, ob.State == OpeningState.Broken))
                foreach ((MeshGpu? mesh, SN.Matrix4x4 part) in UnitModel(model))
                    DrawMesh(mesh, (part * world).X());
        foreach (Opening o in _session.Units.Openings.Where(o => o.Model != null && o.State != OpeningState.Broken && Vis(o.Id, true) && (o.OpenAnim != null || o.CloseAnim != null)))
            DrawAnimatedOpening(o);
        foreach (Opening o in _session.Units.Openings.Where(o => o.Model != null && o.State != OpeningState.Broken && Vis(o.Id, true) && o.OpenAnim == null && o.CloseAnim == null))
            foreach ((MeshGpu? mesh, SN.Matrix4x4 part) in UnitModel(o.Model!))
                DrawMesh(mesh, (part * (o.State == OpeningState.Open && !o.IsWindow ? SN.Matrix4x4.CreateRotationZ(-MathF.PI / 2) : SN.Matrix4x4.Identity) * o.World).X());
    }

    // portail coulissant (Jail) : seuls les battants bougent, d'après l'animation d'ouverture ou de fermeture du jeu
    private readonly Dictionary<uint, (OpeningState state, float since)> _doorAnim = new();
    private void DrawAnimatedOpening(Opening o)
    {
        if (!_doorAnim.TryGetValue(o.Id, out (OpeningState state, float since) st) || st.state != o.State)
            _doorAnim[o.Id] = st = (o.State, st.state == default && !_doorAnim.ContainsKey(o.Id) ? -1000f : _visTime);
        // une seule des deux animations peut exister (MonteCristo) : l'autre se joue alors à l'envers (fin → début)
        string? want = o.State == OpeningState.Open ? o.OpenAnim : o.CloseAnim;
        bool reverse = want == null;
        AnimClip? clip = Clip(want ?? (o.State == OpeningState.Open ? o.CloseAnim : o.OpenAnim)!);
        float t = clip == null ? 0 : Math.Clamp(_visTime - st.since, 0, clip.Duration);
        if (clip != null && reverse)
            t = clip.Duration - t;
        List<SceneAssets.Node> nodes = _assets.ModelParts(o.Model!); List<(MeshGpu mesh, SN.Matrix4x4 part)> meshes = UnitModel(o.Model!);
        static string Leaf(string n) => n[(n.LastIndexOf('\\') + 1)..];
        // aucune pièce animée par ce clip (portes ordinaires) : la porte pivote de 90° comme avant
        if (clip == null || !nodes.Any(n => clip.Tracks.ContainsKey(Leaf(n.Mesh))))
        {
            foreach ((MeshGpu? mesh, SN.Matrix4x4 part) in meshes)
                DrawMesh(mesh, (part * (o.State == OpeningState.Open && !o.IsWindow ? SN.Matrix4x4.CreateRotationZ(-MathF.PI / 2) : SN.Matrix4x4.Identity) * o.World).X());
            return;
        }
        for (int i = 0; i < meshes.Count && i < nodes.Count; i++)
        {
            SN.Matrix4x4 w = meshes[i].part;
            if (clip.Tracks.TryGetValue(Leaf(nodes[i].Mesh), out AnimClip.Track? tr))
            {
                SN.Vector3 pos = tr.Pos.Length > 0 ? TrackPos(tr, t) : w.Translation;
                if (tr.Rot.Length > 0)
                    w = SN.Matrix4x4.CreateFromQuaternion(TrackRot(tr, t));
                w.Translation = pos;
            }
            DrawMesh(meshes[i].mesh, (w * o.World).X());
        }
    }

    private static SN.Quaternion TrackRot(AnimClip.Track tr, float tick)
    {
        (float t, SN.Quaternion v)[] k = tr.Rot;
        if (tick <= k[0].t)
            return k[0].v;
        for (int i = 1; i < k.Length; i++)
            if (tick <= k[i].t)
            {
                float f = (tick - k[i - 1].t) / MathF.Max(1e-3f, k[i].t - k[i - 1].t);
                return SN.Quaternion.Slerp(k[i - 1].v, k[i].v, f);
            }
        return k[^1].v;
    }

    private static SN.Vector3 TrackPos(AnimClip.Track tr, float tick)   // tick : secondes
    {
        (float t, SN.Vector3 v)[] k = tr.Pos;
        if (tick <= k[0].t)
            return k[0].v;
        for (int i = 1; i < k.Length; i++)
            if (tick <= k[i].t)
            {
                float f = (tick - k[i - 1].t) / MathF.Max(1e-3f, k[i].t - k[i - 1].t);
                return SN.Vector3.Lerp(k[i - 1].v, k[i].v, f);
            }
        return k[^1].v;
    }

    // feu (lueur + flammes qui montent) et fumée : des disques adoucis toujours tournés vers la caméra
    private readonly List<VertexPositionColorTexture> _pv = new();
    private void P(SN.Vector3 p, float size, SN.Vector4 c, SN.Vector3 right, SN.Vector3 up)
    {
        SN.Vector3 r = right * (size / 2); SN.Vector3 u = up * (size / 2); Color col = c.C();
        Vector3 a = (p - r - u).X(); Vector3 b = (p + r - u).X(); Vector3 cc = (p + r + u).X(); Vector3 d = (p - r + u).X();
        _pv.Add(new(a, col, new Vector2(0, 1))); _pv.Add(new(b, col, new Vector2(1, 1))); _pv.Add(new(cc, col, new Vector2(1, 0)));
        _pv.Add(new(a, col, new Vector2(0, 1))); _pv.Add(new(cc, col, new Vector2(1, 0))); _pv.Add(new(d, col, new Vector2(0, 0)));
    }

    private void RenderParticles(float time)
    {
        _pv.Clear();
        SN.Vector3 right = new SN.Vector3(_view.M11, _view.M21, _view.M31); SN.Vector3 up = new SN.Vector3(_view.M12, _view.M22, _view.M32);
        if (_smokeOn)
            foreach (SmokePoint s in _session.Smoke.Points)
            {
                if (s.Intensity < 0.5f)
                    continue;
                float k = Math.Min(1, s.Intensity / s.Max);
                P(s.Position + new SN.Vector3(0, 0, 250), s.Class.InfluenceRange * (0.95f + k), new SN.Vector4(0.36f, 0.36f, 0.38f, 0.5f * k + 0.16f), right, up);
            }
        // cercle d'action sous les rouages d'un portail (clic droit dessus : ouvrir / fermer)
        foreach (Opening o in _session.Units.Openings.Where(o => o.Lever != null && o.State != OpeningState.Broken))
        {
            SN.Vector2 lv = o.Lever!.Value;
            P(new SN.Vector3(lv, _session.Units.GroundZ(lv) + 15), 220, new SN.Vector4(1f, 0.85f, 0.2f, 0.45f), right, up);
        }
        foreach (Civilian c in _session.Units.Civilians.Where(c => !c.Hidden && c.Carrier == null && (c.State == CivilianState.Unconscious || c.Leader != null)))
            P(c.Position + new SN.Vector3(0, 0, 230), 120, c.State == CivilianState.Unconscious ? new SN.Vector4(1f, 0.2f, 0.2f, 0.9f) : new SN.Vector4(0.3f, 1f, 0.4f, 0.9f), right, up);
        foreach (Unit u in _session.Units.Active.Where(u => u.Target != null && !u.Moving))
        {
            // lance : à la main pour un pompier, à l'avant (sur le toit) pour un véhicule
            SN.Vector3 ja = u.IsVehicle ? u.Position + new SN.Vector3(MathF.Cos(u.Heading) * 330, MathF.Sin(u.Heading) * 330, 300) : u.Position + new SN.Vector3(0, 0, 120);
            SN.Vector3 jb = u.Target!.Position + new SN.Vector3(0, 0, 40);
            // jet bien visible : un filet continu (gouttes serrées), qui s'élargit en gerbe, et des éclaboussures au point visé
            float span = SN.Vector3.Distance(ja, jb), arc = MathF.Min(260, 60 + span * 0.12f);
            int nq = Math.Clamp((int)(span / 45), 16, 60);
            for (int q = 0; q < nq; q++)
            {
                float t = Frac(time * 1.4f + (float)q / nq);
                SN.Vector3 p = SN.Vector3.Lerp(ja, jb, t) + new SN.Vector3(0, 0, MathF.Sin(t * MathF.PI) * arc);
                bool big = u.IsVehicle || u.IsCanon;   // canon : le gros jet des camions (sprinkler_pumper)
                float size = (big ? 70 : 45) + (big ? 150 : 90) * t;
                P(p, size, new SN.Vector4(0.82f, 0.93f, 1f, 0.85f * (1 - t * 0.45f)), right, up);
                if ((q & 1) == 0)
                    P(p, size * 0.45f, new SN.Vector4(1f, 1f, 1f, 0.9f * (1 - t * 0.5f)), right, up);   // cœur blanc du jet
            }
            for (int q = 0; q < 6; q++)
            {
                float t = Frac(time * 2.3f + q / 6f), a = q * 1.7f + time;
                SN.Vector3 sp = jb + new SN.Vector3(MathF.Cos(a) * 90 * t, MathF.Sin(a) * 90 * t, 60 * MathF.Sin(t * MathF.PI));
                P(sp, 90 + 160 * t, new SN.Vector4(0.85f, 0.94f, 1f, 0.7f * (1 - t)), right, up);
            }
        }
        ExplosionSmoke(right, up);
        int smokeCount = _pv.Count;
        ExplosionFire(right, up);
        int hpIndex = 0;
        Texture2D? flames = FlameTexture();
        _fv.Clear();
        // véhicule à moitié détruit (ou plus) : un petit feu sur la carrosserie, qui grandit avec les dégâts
        if (flames != null)
            foreach (Unit v in _session.Units.Active.Where(v => v.IsVehicle && v.LifeMax > 0 && v.Life > 0 && v.Life <= v.LifeMax * 0.5f))   // détruit : carcasse noire, sans feu
            {
                float dmg = 1 - v.Life / (v.LifeMax * 0.5f);   // 0 à mi-vie, 1 détruit
                SN.Vector3 vp = PoseOf(v, v.Position, v.Heading).p;
                P(vp + new SN.Vector3(0, 0, 260), 300 + 200 * dmg, new SN.Vector4(0.9f, 0.35f, 0.08f, 0.06f), right, up);
                FlamesAt(vp + new SN.Vector3(0, 0, 230), (1000f, SN.Vector2.Zero), 7919 + v.Id, 0.2f + 0.35f * dmg, time, right);   // posé vers le toit, sinon caché dans la carrosserie
            }
        foreach (HotPoint h in _session.Fire.HotPoints)
        {
            hpIndex++;
            if (!h.IsBurning)
                continue;
            float k = Math.Min(1, h.Intensity / h.Class.IntensityMax);
            (float clr0, _) = FireRoom(h);
            bool chem = h.Class.FireType == 'D';   // feu chimique (type D, agent anti-métal) : flammes vertes
            bool foam = h.Class.FireType == 'B';   // feu à la mousse (type B, hydrocarbures) : flammes très rouges
            SN.Vector4 glow = chem ? new SN.Vector4(0.2f, 0.85f, 0.25f, flames != null ? 0.07f : 0.45f)
                : foam ? new SN.Vector4(1f, 0.08f, 0.04f, flames != null ? 0.09f : 0.5f)
                : new SN.Vector4(0.9f, 0.35f, 0.08f, flames != null ? 0.07f : 0.45f);
            P(h.Position + new SN.Vector3(0, 0, 60), MathF.Min(260 + 500 * k, clr0 * 1.6f + 120), glow, right, up);
            if (flames != null)
            {
                Flames(h, hpIndex, k, time, right);
                continue;
            }
            for (int q = 0; q < 7; q++)
            {
                float ph = Frac(q * 0.618f + hpIndex * 0.137f), t = Frac(time * 0.9f + ph);
                float ox = MathF.Sin(ph * 40) * (40 + 90 * k) * (1 - 0.6f * t), oy = MathF.Cos(ph * 53) * (40 + 90 * k) * (1 - 0.6f * t);
                ox += MathF.Sin(time * 7 + ph * 20) * 12 * t; oy += MathF.Cos(time * 6 + ph * 13) * 12 * t;
                SN.Vector4 col = chem
                    ? SN.Vector4.Lerp(new SN.Vector4(0.75f, 1f, 0.5f, 0.9f), new SN.Vector4(0.1f, 0.7f, 0.15f, 0.9f), Math.Clamp((t - 0.1f) / 0.7f, 0, 1))
                    : foam ? SN.Vector4.Lerp(new SN.Vector4(1f, 0.3f, 0.15f, 0.9f), new SN.Vector4(0.8f, 0.02f, 0.02f, 0.9f), Math.Clamp((t - 0.1f) / 0.7f, 0, 1))
                    : SN.Vector4.Lerp(new SN.Vector4(1f, 0.9f, 0.45f, 0.9f), new SN.Vector4(0.95f, 0.25f, 0.05f, 0.9f), Math.Clamp((t - 0.1f) / 0.7f, 0, 1));
                col.W *= 1 - t;
                P(h.Position + new SN.Vector3(ox, oy, 20 + t * (120 + 380 * k)), (90 + 240 * k) * (1 - 0.7f * t), col, right, up);
            }
        }
        if (_pv.Count == 0 && _fv.Count == 0)
            return;
        GraphicsDevice gd = GraphicsDevice;
        gd.DepthStencilState = DepthStencilState.DepthRead; gd.SamplerStates[0] = SamplerState.LinearClamp;
        VertexPositionColorTexture[] arr = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_pv).ToArray();
        _part.World = Matrix.Identity; _part.Texture = Tex.Soft;
        if (smokeCount > 0)
        {
            gd.BlendState = BlendState.NonPremultiplied;
            foreach (EffectPass pass in _part.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserPrimitives(PrimitiveType.TriangleList, arr, 0, smokeCount / 3);
            }
        }
        if (arr.Length > smokeCount)
        {
            gd.BlendState = BlendState.Additive;                    // feu : couleurs additionnées
            foreach (EffectPass pass in _part.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserPrimitives(PrimitiveType.TriangleList, arr, smokeCount, (arr.Length - smokeCount) / 3);
            }
        }
        if (_fv.Count > 0 && FlameTexture() is { } ft)
        {
            // flammes : images animées du jeu (Gfx\Effect\Shaders\fire_classic*.dds), opaques, puis un peu de lumière ajoutée
            VertexPositionColorTexture[] fa = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_fv).ToArray();
            _part.Texture = ft;
            gd.BlendState = BlendState.NonPremultiplied;
            foreach (EffectPass pass in _part.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserPrimitives(PrimitiveType.TriangleList, fa, 0, fa.Length / 3);
            }
            gd.BlendState = BlendState.Additive;
            _part.Alpha = 0.2f;
            foreach (EffectPass pass in _part.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserPrimitives(PrimitiveType.TriangleList, fa, 0, fa.Length / 3);
            }
            _part.Alpha = 1f;
        }
        gd.DepthStencilState = DepthStencilState.Default; gd.BlendState = BlendState.Opaque;
    }

    // ---- flammes animées ----
    private readonly List<VertexPositionColorTexture> _fv = new();
    private Texture2D? _flameTex; private bool _flameTried;
    private const int FlameCols = 4, FlameRows = 8;   // 32 images de 128 × 128 dans une texture de 512 × 1024

    private Texture2D? FlameTexture()
    {
        if (_flameTried)
            return _flameTex;
        _flameTried = true;
        if (Environment.GetEnvironmentVariable("FD3_FEU_SIMPLE") != null)
            return null;
        foreach (string name in new[] { "fire_classic10", "fire_classic20" })
            try
            {
                if (_files.TryRead($"Gfx\\Effect\\Shaders\\{name}.dds") is { } d && Tex.Dds(GraphicsDevice, d) is { } t)
                {
                    _flameTex = t;
                    break;
                }
            } catch { }
        Console.WriteLine(_flameTex != null ? $"Flammes : images du jeu {_flameTex.Width}×{_flameTex.Height}" : "Flammes : images du jeu absentes (dessin simple)");
        return _flameTex;
    }

    // plusieurs langues de feu par foyer : debout (toujours verticales), chacune avec son image de départ, sa vitesse,
    // sa taille et sa teinte (rouge, orange, jaune), qui ondulent un peu ; plus le feu est fort, plus elles sont grandes et nombreuses
    private void Flames(HotPoint h, int seed, float k, float time, SN.Vector3 right) => FlamesAt(h.Position, FireRoom(h), seed, k, time, right, h.Class.FireType);

    /// <summary>Flammes d'un foyer (ou d'un véhicule en feu) posées en <paramref name="pos"/> ; room : place libre autour (murs).</summary>
    private void FlamesAt(SN.Vector3 pos, (float clear, SN.Vector2 away) room0, int seed, float k, float time, SN.Vector3 right, char fireType = 'A')
    {
        SN.Vector3 flat = new SN.Vector3(right.X, right.Y, 0);
        flat = flat.LengthSquared() > 1e-6f ? SN.Vector3.Normalize(flat) : SN.Vector3.UnitX;
        int n = 3 + (int)(5 * k);
        float spread = 40 + 110 * k;
        // foyer près d'un mur : les flammes sont poussées vers l'intérieur de la pièce et ne dépassent pas trop le mur
        (float clear, SN.Vector2 away) = room0;
        float shift = MathF.Min(140, MathF.Max(0, 120 - clear));
        SN.Vector3 centre0 = pos + new SN.Vector3(away * shift, 0);
        float room = clear + shift;
        spread = MathF.Min(spread, MathF.Max(15, room - 70));
        for (int q = 0; q < n; q++)
        {
            float ph = Frac(q * 0.618f + seed * 0.137f), ph2 = Frac(q * 0.379f + seed * 0.71f);
            SN.Vector3 off = q == 0 ? SN.Vector3.Zero : new SN.Vector3(MathF.Sin(ph * 40) * spread, MathF.Cos(ph * 53) * spread, 0);   // la première au centre
            float size = (150 + 330 * k) * (0.7f + 0.6f * ph2);
            size = MathF.Min(size, MathF.Max(140, 2 * (room - off.Length()) + 120));
            float w = size, hgt = size * 1.25f;
            // animation lente (6 à 9 images par seconde) et fondue : l'image suivante apparaît pendant que l'autre s'efface
            float ft = time * (_flameFps + _flameFpsVar * ph) + ph * 32;
            int f0 = (int)MathF.Floor(ft); float blend = ft - f0;
            // teintes variées : jaune vif, orange, rouge profond
            // au centre du foyer : rouge profond ; vers le bord : orange puis jaune (avec un peu de variété)
            float centre = 1 - Math.Clamp(off.Length() / MathF.Max(1, spread), 0, 1);
            SN.Vector3 edge = (q % 2) == 0 ? new SN.Vector3(1f, 0.92f, 0.6f) : new SN.Vector3(1f, 0.7f, 0.35f);
            SN.Vector3 core = new SN.Vector3(1f, 0.32f, 0.14f);
            if (fireType == 'D')
            {
                // feu chimique : vert-jaune pâle au bord, vert profond au centre
                edge = (q % 2) == 0 ? new SN.Vector3(0.8f, 1f, 0.55f) : new SN.Vector3(0.5f, 1f, 0.4f);
                core = new SN.Vector3(0.12f, 0.8f, 0.2f);
            }
            else if (fireType == 'B')
            {
                // feu à la mousse (hydrocarbures) : rouge vif au bord, rouge sombre au centre, sans jaune
                edge = (q % 2) == 0 ? new SN.Vector3(1f, 0.32f, 0.18f) : new SN.Vector3(1f, 0.18f, 0.1f);
                core = new SN.Vector3(0.85f, 0.03f, 0.02f);
            }
            SN.Vector3 tintRgb = SN.Vector3.Lerp(edge, core, centre * centre * 0.9f + 0.1f * ph2);
            float sway = MathF.Sin(time * 1.6f + ph * 17) * 0.1f;
            SN.Vector3 bottom = centre0 + off + new SN.Vector3(0, 0, -10);
            SN.Vector3 r = flat * (w / 2); SN.Vector3 up = new SN.Vector3(flat.Y * sway, -flat.X * sway, 1) * hgt;
            Vector3 a = (bottom - r).X(); Vector3 b = (bottom + r).X(); Vector3 c = (bottom + r + up).X(); Vector3 d = (bottom - r + up).X();
            for (int layer = 0; layer < 2; layer++)
            {
                int frame = ((f0 + layer) % (FlameCols * FlameRows) + FlameCols * FlameRows) % (FlameCols * FlameRows);
                float alpha = layer == 0 ? 1 - blend : blend;
                if (alpha < 0.02f)
                    continue;
                float u0 = (frame % FlameCols) / (float)FlameCols, v0 = (frame / FlameCols) / (float)FlameRows;
                float u1 = u0 + 1f / FlameCols, v1 = v0 + 1f / FlameRows;
                Color tint = new Color(tintRgb.X, tintRgb.Y, tintRgb.Z, alpha);
                _fv.Add(new(a, tint, new Vector2(u0, v1))); _fv.Add(new(b, tint, new Vector2(u1, v1))); _fv.Add(new(c, tint, new Vector2(u1, v0)));
                _fv.Add(new(a, tint, new Vector2(u0, v1))); _fv.Add(new(c, tint, new Vector2(u1, v0))); _fv.Add(new(d, tint, new Vector2(u0, v0)));
            }
        }
    }
    private static float Frac(float x) => x - MathF.Floor(x);

    // place libre autour d'un foyer : distance au mur le plus proche (cases fermées de la grille) et direction qui s'en éloigne
    private readonly Dictionary<uint, (float clear, SN.Vector2 away)> _fireRoom = new();
    private (float clear, SN.Vector2 away) FireRoom(HotPoint h)
    {
        if (_fireRoom.TryGetValue(h.Id, out (float clear, SN.Vector2 away) r))
            return r;
        r = (1000f, SN.Vector2.Zero);
        if (_session.Grid is { } g && Environment.GetEnvironmentVariable("FD3_FEU_SANS_MURS") == null)
        {
            (int cx, int cy) = g.CellOf(h.Position);
            const float Cell = OpenFD.Formats.Grid.CellSize;
            if (g.Inside(cx, cy) && g.ZoneA[g.Index(cx, cy)] != 0)   // foyer posé dans une case ouverte (sinon : toit, hors de la grille…)
            {
                SN.Vector2 hp = new SN.Vector2(h.Position.X, h.Position.Y); float best = 1000f; SN.Vector2 push = SN.Vector2.Zero;
                for (int oy = -6; oy <= 6; oy++)
                    for (int ox = -6; ox <= 6; ox++)
                    {
                        int x = cx + ox, y = cy + oy;
                        if (!g.Inside(x, y) || g.ZoneA[g.Index(x, y)] != 0)
                            continue;
                        SN.Vector2 c = new SN.Vector2((x + 0.5f) * Cell, (y + 0.5f) * Cell);
                        float d = MathF.Max(1, SN.Vector2.Distance(hp, c) - Cell / 2);
                        if (d < best)
                            best = d;
                        push += (hp - c) / MathF.Max(1, SN.Vector2.DistanceSquared(hp, c));
                    }
                r = (best, push.LengthSquared() > 1e-12f ? SN.Vector2.Normalize(push) : SN.Vector2.Zero);
            }
        }
        return _fireRoom[h.Id] = r;
    }

    // ------------------------------------------------------------------ carte vue de dessus
    private SN.Vector2 MapToScreen(float x, float y) => new(_fbW / 2f + (x - _mapCenter.X) * _mapScale, _fbH / 2f - (y - _mapCenter.Y) * _mapScale);
    private SN.Vector2 ScreenToMap(float sx, float sy) => new(_mapCenter.X + (sx - _fbW / 2f) / _mapScale, _mapCenter.Y - (sy - _fbH / 2f) / _mapScale);

    private void RenderMap()
    {
        if (_mapScale <= 0)
            _mapScale = MathF.Min(_fbW / MathF.Max(1, _sceneMax.X - _sceneMin.X), _fbH / MathF.Max(1, _sceneMax.Y - _sceneMin.Y)) * 0.92f;
        GraphicsDevice.Clear(new Color(0.07f, 0.07f, 0.08f));
        float px = _scale;   // traits et repères un peu plus gros sur les écrans à forte densité
        if (_gridTex != null)
        {
            _ui.Begin(SamplerState.PointClamp);
            SN.Vector2 a = MapToScreen(0, _gridH);
            _ui.Image(_gridTex, a.X, a.Y, _gridW * _mapScale, _gridH * _mapScale, SN.Vector4.One);
            _ui.End();
        }
        _ui.Begin();
        if (_roofs)
            foreach (Rooms.Room r in _session.RoomList)
            {
                SN.Vector2 a = MapToScreen(r.Min.X, r.Max.Y);
                _ui.Frame(a.X, a.Y, (r.Max.X - r.Min.X) * _mapScale, (r.Max.Y - r.Min.Y) * _mapScale, new SN.Vector4(0.37f, 0.66f, 0.83f, 0.5f), px);
            }
        if (_smokeOn)
            foreach (SmokePoint s in _session.Smoke.Points)
            {
                if (s.Intensity < 0.5f)
                    continue;
                SN.Vector2 p = MapToScreen(s.Position.X, s.Position.Y);
                _ui.Disc(p.X, p.Y, MathF.Max(3 * px, s.Class.InfluenceRange * 0.5f * _mapScale), new SN.Vector4(0.8f, 0.8f, 0.85f, 0.35f * Math.Min(1, s.Intensity / s.Max)));
            }
        HashSet<HotPoint> sprayed = _session.Sprayers.Select(x => x.target).ToHashSet();
        foreach (HotPoint h in _session.Fire.HotPoints.OrderBy(h => h.Intensity))
        {
            SN.Vector2 p = MapToScreen(h.Position.X, h.Position.Y);
            if (h.IsBurning)
            {
                float k = Math.Min(1, h.Intensity / h.Class.IntensityMax), r = MathF.Max(3 * px, (25 + 55 * k) * _mapScale);
                _ui.Disc(p.X, p.Y, r * 2.2f, new SN.Vector4(1, 0.55f, 0.15f, 0.25f));
                _ui.Disc(p.X, p.Y, r, new SN.Vector4(0.89f + 0.11f * k, 0.25f + 0.57f * k, 0.11f + 0.14f * k, 1));
            }
            else if (_objects)
                _ui.Disc(p.X, p.Y, MathF.Max(2 * px, 25 * _mapScale), h.State == HotPointState.Heating ? new SN.Vector4(0.48f, 0.36f, 1, 1)
                    : h.State == HotPointState.Consumed ? new SN.Vector4(0.16f, 0.16f, 0.18f, 1) : new SN.Vector4(0.23f, 0.23f, 0.27f, 1));
            if (sprayed.Contains(h))
                _ui.Disc(p.X, p.Y, MathF.Max(5 * px, 50 * _mapScale), new SN.Vector4(0.32f, 0.71f, 1, 0.5f));
        }
        foreach (Opening o in _session.Units.Openings.Where(o => o.OnGround || _objects))
        {
            SN.Vector2 p = MapToScreen(o.Center.X, o.Center.Y); float r = MathF.Max(2.5f * px, 45 * _mapScale);
            SN.Vector4 col = o.State == OpeningState.Broken ? new SN.Vector4(0.45f, 0.45f, 0.48f, 1) : o.State == OpeningState.Open ? new SN.Vector4(0.4f, 0.85f, 0.4f, 1)
                : o.IsWindow ? new SN.Vector4(0.55f, 0.8f, 1f, 1) : new SN.Vector4(0.95f, 0.6f, 0.2f, 1);
            _ui.Rect(p.X - r, p.Y - r, 2 * r, 2 * r, col);
        }
        foreach (Civilian c in _session.Units.Civilians.Where(c => !c.Hidden && c.State != CivilianState.Rescued))
        {
            SN.Vector2 p = MapToScreen(c.Position.X, c.Position.Y);
            _ui.Disc(p.X, p.Y, MathF.Max(3 * px, 55 * _mapScale), c.State == CivilianState.Unconscious ? new SN.Vector4(1, 0.25f, 0.25f, 1)
                : c.State == CivilianState.Dead ? new SN.Vector4(0.3f, 0.3f, 0.3f, 1) : new SN.Vector4(0.35f, 0.95f, 0.45f, 1));
        }
        foreach (Unit u in _session.Units.Active)
        {
            SN.Vector2 p = MapToScreen(u.Position.X, u.Position.Y);
            float r = MathF.Max((u.IsVehicle ? 5 : 3) * px, (u.IsVehicle ? 200 : 60) * _mapScale);
            if (_sel.Contains(u))
                _ui.Disc(p.X, p.Y, r + 4 * px, new SN.Vector4(1, 1, 1, 0.9f));
            if (u.IsVehicle)
                _ui.Rect(p.X - r, p.Y - r * 0.6f, 2 * r, 1.2f * r, new SN.Vector4(0.85f, 0.15f, 0.15f, 1));
            else
                _ui.Disc(p.X, p.Y, r, new SN.Vector4(0.3f, 0.75f, 1f, 1));
            if (u.Target != null)
            {
                SN.Vector2 q = MapToScreen(u.Target.Position.X, u.Target.Position.Y);
                for (int k = 1; k < 8; k++)
                {
                    SN.Vector2 m = SN.Vector2.Lerp(p, q, k / 8f);
                    _ui.Disc(m.X, m.Y, 1.5f * px, new SN.Vector4(0.5f, 0.8f, 1, 0.8f));
                }
            }
        }
        _ui.End();
    }

    // ------------------------------------------------------------------ interface
    private void RenderHud()
    {
        RenderHudCore();
        RenderNetHud();
    }

    private void RenderHudCore()
    {
        if (_cineOn || _cineBars > 0.01f)
        {
            RenderCinema();
            return;
        }
        _ui.Begin(); RenderSupplyLinks((float)_session.Time); RenderRopes(); RenderElectricArcs(); RenderFireHover(); RenderDebugCircles(); _ui.End();
        _uiClock += 1f / 60f;
        _ui.Begin(); RenderFireBars(); RenderClickMarks(); _ui.End();
        RenderHudPanels();
        _ui.Begin(); RenderSelectionPanel(); _ui.End();
        _ui.Begin(); RenderObjectives(); RenderMessages(); RenderExitIcon(); _ui.End();
        RenderCinema();   // sous-titres hors cinématique
        _ui.Begin(); RenderEndScreen(); _ui.End();
    }

    private void RenderHudPanels()
    {
        _ui.Begin();
        SN.Vector4 panel = new SN.Vector4(0.09f, 0.09f, 0.11f, 0.88f); SN.Vector4 txt = new SN.Vector4(0.91f, 0.9f, 0.89f, 1); SN.Vector4 muted = new SN.Vector4(0.6f, 0.59f, 0.56f, 1);
        SN.Vector4 accent = new SN.Vector4(1f, 0.48f, 0.18f, 1);
        int t = (int)_session.Time;
        List<(string s, SN.Vector4 c)> lines = new List<(string s, SN.Vector4 c)>
        {
            ($"{_mission} · {DiffName}", accent),
            ($"{t / 60}:{t % 60:00}   {(_paused ? "en pause" : $"vitesse ×{_speed:0}")}", txt),
            ($"{_session.Fire.HotPoints.Count(h => h.IsBurning)} foyers en feu · fumée {_session.Smoke.Total:0}", txt),
        };
        foreach ((Extinguisher? u, HotPoint? target) in _session.Sprayers)
            lines.Add(($"Arrosage : {target.Name} - réserve {u.Reserve:0}", new SN.Vector4(0.45f, 0.75f, 1, 1)));
        List<Civilian> civs = _session.Units.Civilians.Where(c => !c.Hidden).ToList();
        lines.Add(($"Civils : {civs.Count(c => c.State == CivilianState.Rescued)} sauvé(s) sur {civs.Count} · {civs.Count(c => c.State == CivilianState.Unconscious)} inconscient(s)", txt));
        lines.Add(($"{_session.Units.Active.Count} unités · {_sel.Count} sélectionnée(s)", txt));
        foreach (Unit u in _sel.Take(6))
            lines.Add(($"  {UnitLabel(u)} : {u.Status}{(u.IsCanon ? (u.FeedTruck != null ? " · relié à un camion" : " · aucun camion à portée") : u.Nozzle != null ? $" · {UnitManager.AgentName(u.Nozzle.Agent)} {u.Nozzle.Reserve:0}/{u.Nozzle.ReserveMax:0}" : "")}{(u.Seats > 0 ? $" · à bord {u.Passengers.Count}/{u.Seats}" : "")}", new SN.Vector4(0.45f, 0.75f, 1, 1)));
        if (_help)
        {
            lines.Add(("", txt));
            lines.Add(("Espace : pause · 1 2 3 4 : vitesse ×1 ×4 ×16 ×64", muted));
            lines.Add(("Tab : carte / 3D · T : toits · O : objets · F : fumée · B : ombres · F1 : difficulté · D : portées (débogage)", muted));
            lines.Add(("F5 : sauvegarder · F9 : charger la dernière sauvegarde", muted));
            lines.Add(("H : masquer l'aide · Échap : menu des missions", muted));
        }
        if (_shadowMap != null && Environment.GetEnvironmentVariable("FD3_VOIR_OMBRES") != null)
            _ui.Image(_shadowMap, _fbW - 410, 10, 400, 400, SN.Vector4.One);
        if (SelectionRect() is { } sr)
        {
            _ui.Rect(sr.x, sr.y, sr.w, sr.h, new SN.Vector4(0.3f, 0.75f, 1f, 0.15f));
            _ui.Frame(sr.x, sr.y, sr.w, sr.h, new SN.Vector4(0.45f, 0.85f, 1f, 0.9f), MathF.Max(1, _scale));
        }
        float m = 12 * _scale, pad = 14 * _scale;
        float lh = _ui.LineHeight + 4 * _scale, w = lines.Max(l => _ui.Measure(l.s)) + 2 * pad, h = lines.Count * lh + 20 * _scale;
        float px0 = _fbW - w - m;   // en haut à droite (les objectifs sont à gauche)
        _ui.Rect(px0, m, w, h, panel);
        float y = m + 10 * _scale;
        foreach ((string? s, SN.Vector4 c) in lines)
        {
            _ui.Text(px0 + pad, y, s, c);
            y += lh;
        }
        // barre de vie des portes et fenêtres entamées
        if (!_map)
            foreach (Opening o in _session.Units.Openings.Where(o => o.State != OpeningState.Broken && o.Life < o.MaxLife))
                if (Project(o.Center + new SN.Vector3(0, 0, 160)) is { } bp && bp.X > 0 && bp.Y > 0 && bp.X < _fbW && bp.Y < _fbH)
                {
                    float bw = 60 * _scale, k = Math.Clamp(o.Life / o.MaxLife, 0, 1);
                    _ui.Rect(bp.X - bw / 2 - 2 * _scale, bp.Y - 2 * _scale, bw + 4 * _scale, 10 * _scale, new SN.Vector4(0, 0, 0, 0.7f));
                    _ui.Rect(bp.X - bw / 2, bp.Y, bw * k, 6 * _scale, new SN.Vector4(1 - k, 0.3f + 0.6f * k, 0.2f, 1));
                }
        if (_log.Count > 0 && _debugAi)   // journal (feux, arrivées, explosions…) : seulement avec les infos de débogage (touche D)
        {
            // journal : en haut, au centre de l'écran
            SN.Vector4 red = new SN.Vector4(1f, 0.35f, 0.3f, 1);
            float lw = MathF.Min(_log.Max(l => _ui.Measure(l.text)) + 2 * pad, _fbW * 0.5f), lhh = _log.Count * lh + 16 * _scale;
            float lx = (_fbW - lw) / 2;
            _ui.Rect(lx, m, lw, lhh, panel);
            float yy = m + 8 * _scale;
            foreach ((string l, bool err) in _log)
            {
                _ui.Text(lx + pad, yy, l, err ? red : muted);
                yy += lh;
            }
        }
        _ui.End();
        RenderMinimap();
        RenderObjButton();
        RenderSenseButton();
    }

    private static string UnitLabel(Unit u) => u.ClassPath.ToLowerInvariant() switch
    {
        string c when c.Contains("medic") => "Médecin",
        string c when c.Contains("ambulance") => "Ambulance",
        string c when c.Contains("pumper") => "Autopompe",
        string c when c.Contains("aerial") => "Grande échelle",
        string c when c.Contains("franc") => "Chef d'équipe",
        string c when c.Contains("fireman") => "Pompier",
        _ => u.Name,
    };

    // capture d'écran (essais) : image TGA 32 bits
    private void Capture(string path)
    {
        Color[] px = new Color[_fbW * _fbH];
        GraphicsDevice.GetBackBufferData(px);
        using FileStream f = File.Create(path);
        byte[] hdr = new byte[18]; hdr[2] = 2; BitConverter.GetBytes((ushort)_fbW).CopyTo(hdr, 12); BitConverter.GetBytes((ushort)_fbH).CopyTo(hdr, 14); hdr[16] = 32; hdr[17] = 8 | 0x20;
        f.Write(hdr);
        byte[] row = new byte[_fbW * 4];
        for (int y = 0; y < _fbH; y++)
        {
            for (int x = 0; x < _fbW; x++)
            {
                Color c = px[y * _fbW + x];
                row[x * 4] = c.B;
                row[x * 4 + 1] = c.G;
                row[x * 4 + 2] = c.R;
                row[x * 4 + 3] = 255;
            }
            f.Write(row);
        }
        Console.WriteLine("Image enregistrée : " + path);
        if (_session != null)
            foreach (Unit u in _session.Units.Units.Take(12))
            {
                Grid g = _session.Grid!;
                (int cx, int cy) = g.CellOf(u.Position);
                Console.WriteLine($"unité {u.Name} [{u.Status}] feu le plus proche {_session.Fire.HotPoints.Where(h => h.IsBurning).Select(h => SN.Vector2.Distance(new(h.Position.X, h.Position.Y), new(u.Position.X, u.Position.Y))).DefaultIfEmpty(-1).Min():0} portée {u.Nozzle?.RangeMax} {_session.Units.AutoDiag(u)} pos {u.Position} sol {GroundAt(u.Position.X, u.Position.Y)} grille {string.Join(" ", Enumerable.Range(-2, 5).Select(d => g.Inside(cx + d, cy) ? g.GroundHeight[g.Index(cx + d, cy)].ToString("0") + "/" + g.ZoneA[g.Index(cx + d, cy)] : "-"))}");
            }
        if (_session?.Scripts is { } sc)
        {
            Console.WriteLine("Fonctions non simulées : " + string.Join(", ", sc.UnknownCalls.OrderByDescending(k => k.Value).Select(k => $"{k.Key}×{k.Value}")));
            foreach (string l in sc.Log.Distinct().Take(15))
                Console.WriteLine("  script : " + l);
        }
    }
}
