using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using OpenFD.Fire;
using OpenFD.Formats;
using OpenFD.Game;
using SN = System.Numerics;

namespace OpenFD.MG;

// éditeur : les objets de la mission ouverte (chargement, dessin, choix à la souris, déplacement, pose, suppression)
public sealed partial class OpenFDGame
{
    private readonly List<EditorObject> _edObjects = new();
    private EditorObject? _edSel;
    private Level _edLevel = null!;
    private string _edMission = "";
    private bool _edAllFires;              // montrer aussi les foyers éteints au départ (touche F)
    private readonly Dictionary<string, ClassFile?> _edClasses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (SN.Vector3 min, SN.Vector3 max)?> _edBoxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _edModelOk = new(StringComparer.OrdinalIgnoreCase);
    private BasicEffect? _edFx;

    // déplacement de l'objet choisi (bouton gauche glissé)
    private bool _edMoveArmed, _edMoving;
    private double _edDownX, _edDownY;
    private SN.Vector2 _edMoveOffset;
    private float _edMoveDz;
    private (SN.Vector3 pos, SN.Vector3 euler) _edMoveFrom;

    // objet tiré de la liste vers la vue
    private EditorItem? _edDrag;
    private SN.Vector3 _edDragEuler;
    private SN.Vector3? _edDragAt;
    private float _edDragDz;

    // classes faites par l'éditeur, pas encore écrites (pompier aux outils choisis) : chemin dans Data → texte
    private readonly Dictionary<string, string> _edNewClasses = new(StringComparer.OrdinalIgnoreCase);

    private ClassFile? EdClass(string classPath)
    {
        if (_edClasses.TryGetValue(classPath, out ClassFile? c))
            return c;
        try
        {
            string rel = EditorCatalog.ClassFileOf(classPath);
            c = _edNewClasses.TryGetValue(rel, out string? text) ? ClassFile.Load(_files, rel, text) : ClassFile.Load(_files, rel);
        }
        catch (Exception)
        {
            c = null;
        }
        return _edClasses[classPath] = c;
    }

    private string? EdModelOf(string classPath, string baseDir)
    {
        string? m = EdClass(classPath)?.Get("DISPLAY", "Model")?.Trim('"', ' ');
        if (string.IsNullOrEmpty(m) || m.Equals("noitem", StringComparison.OrdinalIgnoreCase))
            return null;
        string model = SceneAssets.GfxPath(baseDir, m);
        if (!_edModelOk.TryGetValue(model, out bool ok))
            _edModelOk[model] = ok = _files.TryRead("Gfx\\" + model + ".sg") != null;
        return ok ? model : null;
    }

    /// <summary>Ouvre une mission : son décor (sans scripts), ses objets, son .mis, ses scripts (copiés dans le dossier de travail).</summary>
    private void EdLoadMission(string mission)
    {
        EdCommitFocus();
        _edPopup = null;
        _edDrag = null;
        _edSel = null;
        _edMoving = false;
        _edMoveArmed = false;
        EdClearPreviews();
        _edClasses.Clear();
        _edBoxes.Clear();
        _edNewClasses.Clear();
        _edLoaded = false;
        _edUndoLast = null;
        try
        {
            LoadMission(mission, "EASY");
        }
        catch (Exception e)
        {
            EdMessage(Tr("OpenError", "Impossible d'ouvrir {0} : {1}", mission, e.Message), true);
            Console.WriteLine(e);
            return;
        }
        _edLoaded = true;
        _edMission = mission;
        _edName = mission;
        _map = false;
        _paused = true;
        try
        {
            string lv = _files.List("Design\\Mission\\" + mission + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
            _edGrid = _files.TryRead(lv[..^".level".Length] + ".grid");
            string mp = _edLevel.MissionPath;
            int at = mp.IndexOf("Design\\", StringComparison.OrdinalIgnoreCase);
            string misRel = at >= 0 ? mp[at..] : $"Design\\Mission\\{mission}\\mission_{mission}.mis";
            _edMisText = System.Text.Encoding.Latin1.GetString(_files.Read(misRel));
        }
        catch (Exception e)
        {
            _edMisText = "";
            EdMessage(Tr("MisError", "Fichier .mis : {0}", e.Message), true);
        }
        EdCopyScriptsToWork(mission);
        EdUndoReset();
        Window.Title = Tr("Title", "Open Fire Department - éditeur de missions") + " - " + mission;
        EdMessage(Tr("Opened", "{0} : {1} objets ({2} feux au départ)", mission, _edObjects.Count, _edObjects.Count(o => o.Kind == EditorItemKind.Fire && o.Lit)));
    }

    partial void EditorLoadObjects()
    {
        _edObjects.Clear();
        _edSel = null;
        _edLevel = _session.Level;
        // pas d'unités ni de civils de la simulation : l'éditeur dessine ceux du niveau
        _session.Units.Units.Clear();
        _session.Units.Civilians.Clear();
        foreach (LevelObject o in _edLevel.Objects)
        {
            EditorObject? e = EdMake(o);
            if (e == null)
                continue;
            EdPlace(e);
            _edObjects.Add(e);
        }
        _edTreesDirty = true;
        EdRebuildTerrain();
    }

    /// <summary>L'objet de l'éditeur qui représente cet objet du niveau (null : rien à montrer).</summary>
    private EditorObject? EdMake(LevelObject o)
    {
        if (!o.HasTransform)
            return null;
        EditorObject? e = null;
        string leaf = o.ClassName;
        switch (o.Kind)
        {
            case EntityKind.Placeable:
                if (leaf.StartsWith("openfd_mask", StringComparison.OrdinalIgnoreCase))
                {
                    e = new EditorObject { Source = o, Kind = EditorItemKind.RoofMask, Size = SizeOfClass(leaf, new(400, 400)) };
                    break;
                }
                if (EdClass(o.ClassPath) is { } c && c.Get("SPEEDTREE", "TreeModel")?.Trim('"', ' ') is { Length: > 0 } tree)
                {
                    e = new EditorObject
                    {
                        Source = o, Kind = EditorItemKind.Tree, TreeModel = tree,
                        TreeHeight = c.GetFloat("SPEEDTREE", "TreeHeight", 1000), TreeSeed = c.GetInt("SPEEDTREE", "TreeSeed", 1),
                    };
                    break;
                }
                if (EdModelOf(o.ClassPath, "Placeable") is not { } model)
                    return null;
                bool functional = o.ClassPath.Contains("\\functional\\", StringComparison.OrdinalIgnoreCase);
                e = new EditorObject { Source = o, Kind = functional ? EditorItemKind.Dynamic : EditorItemKind.Object, Model = model };
                break;
            case EntityKind.Civilian:
                if (EdModelOf(o.ClassPath, "Unit\\Civilian") is not { } cm)
                    return null;
                e = new EditorObject { Source = o, Kind = EditorItemKind.Civilian, Model = cm };
                break;
            case EntityKind.FireVehicle:
                bool man = o.ClassPath.Contains("\\fireman\\", StringComparison.OrdinalIgnoreCase);
                e = new EditorObject { Source = o, Kind = man ? EditorItemKind.Fireman : EditorItemKind.Vehicle, Model = EdModelOf(o.ClassPath, man ? "Unit\\Fireman" : "Unit\\FireVehicle") };
                if (man)
                    e.HiddenParts = EdHiddenTools(o.ClassPath, e.Model);
                break;
            case EntityKind.HotPoint:
                e = new EditorObject { Source = o, Kind = EditorItemKind.Fire };
                HotPoint? hp = _session.Fire.ById.TryGetValue(o.Id, out HotPoint? h) ? h : null;
                e.FireClass = hp?.Class ?? EdFireClass(o.ClassPath);
                if (LevelEdit.FireState(o) is { } st)
                {
                    e.Lit = st.lit && st.intensity > 0;
                    e.Intensity = st.intensity;
                }
                else if (hp != null)
                {
                    e.Lit = hp.IsBurning;
                    e.Intensity = hp.Intensity;
                }
                break;
            case EntityKind.Trigger:
                e = new EditorObject { Source = o, Kind = EditorItemKind.Zone };
                break;
            case EntityKind.Waypoint:
                bool cam = leaf.Equals("cam_startpos", StringComparison.OrdinalIgnoreCase);
                e = new EditorObject { Source = o, Kind = cam ? EditorItemKind.Camera : EditorItemKind.Waypoint };
                break;
        }
        if (e == null)
            return null;
        e.Position = o.Position;
        e.Euler = o.Rotation;
        return e;
    }

    private HotPointClass? EdFireClass(string classPath)
    {
        try
        {
            string leaf = classPath[(classPath.LastIndexOf('\\') + 1)..];
            return HotPointClass.From(leaf, ClassFile.Load(_files, EditorCatalog.ClassFileOf(classPath)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>« openfd_blocker_2x3 » → (2, 3).</summary>
    private static SN.Vector2 SizeOfClass(string leaf, SN.Vector2 fallback)
    {
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(leaf, @"_(\d+)x(\d+)$");
        return m.Success ? new SN.Vector2(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : fallback;
    }

    /// <summary>Boîte d'un modèle (repère du modèle), d'après ses maillages.</summary>
    private (SN.Vector3 min, SN.Vector3 max)? EdModelBox(string model)
    {
        if (_edBoxes.TryGetValue(model, out (SN.Vector3 min, SN.Vector3 max)? b))
            return b;
        SN.Vector3 lo = new SN.Vector3(float.MaxValue), hi = new SN.Vector3(float.MinValue);
        try
        {
            foreach (SceneAssets.Node part in _assets.ModelParts(model))
            {
                SceneAssets.MeshAsset a = _assets.LoadMesh(_mission, part.Mesh, "gfx");
                float[] p = a.Mesh.Positions;
                for (int i = 0; i + 2 < p.Length; i += 3)
                {
                    SN.Vector3 v = SN.Vector3.Transform(new SN.Vector3(p[i], p[i + 1], p[i + 2]), part.World);
                    lo = SN.Vector3.Min(lo, v);
                    hi = SN.Vector3.Max(hi, v);
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"{model} : {e.Message}");
        }
        b = lo.X <= hi.X ? (lo, hi) : null;
        _edBoxes[model] = b;
        return b;
    }

    /// <summary>Boîte d'un repère de l'éditeur (repère de l'objet).</summary>
    private (SN.Vector3 min, SN.Vector3 max) EdMarkerBox(EditorObject e)
    {
        switch (e.Kind)
        {
            case EditorItemKind.Fire:
                float r = FireSize(e) / 2;
                return (new(-r, -r, 0), new(r, r, 2.4f * r));
            case EditorItemKind.Zone:
                SN.Vector3 s = ZoneSize(e);
                return (new(-s.X / 2, -s.Y / 2, 0), new(s.X / 2, s.Y / 2, MathF.Max(50, s.Z)));
            case EditorItemKind.RoofMask:
                return (new(-e.Size.X / 2, -e.Size.Y / 2, -10), new(e.Size.X / 2, e.Size.Y / 2, 10));
            case EditorItemKind.Tree:
                float tr = e.TreeHeight * 0.3f;
                return (new(-tr, -tr, 0), new(tr, tr, e.TreeHeight));
            default:
                return (new(-70, -70, -70), new(70, 70, 70));
        }
    }

    private static SN.Vector3 ZoneSize(EditorObject e) =>
        LevelEdit.Trigger(e.Source) is { } t && float.IsFinite(t.size.X) && t.size.X > 0 ? t.size : new SN.Vector3(200, 200, 100);

    private static float FireSize(EditorObject e) => Math.Clamp((e.FireClass?.LifeDamageRange ?? 120) * 1.6f, 120, 700);

    /// <summary>Matrice de dessin et sphère englobante, après un changement de place.</summary>
    private void EdPlace(EditorObject e)
    {
        SN.Matrix4x4 w = e.World;
        // objets du décor posés sur le trottoir comme dans le jeu (Lift)
        if (e.Kind is EditorItemKind.Object or EditorItemKind.Dynamic or EditorItemKind.Tree)
            w = Lift(w);
        e.Shown = w;
        (SN.Vector3 min, SN.Vector3 max)? box = e.Model != null ? EdModelBox(e.Model) : null;
        (SN.Vector3 min, SN.Vector3 max) b = box ?? EdMarkerBox(e);
        e.BoxMin = b.min;
        e.BoxMax = b.max;
        e.HasBox = true;
        e.Center = SN.Vector3.Transform((b.min + b.max) / 2, w);
        e.Radius = MathF.Max(30, (b.max - b.min).Length() / 2);
        if (e.Kind == EditorItemKind.Tree)
            _edTreesDirty = true;
    }

    private bool EdShown(EditorObject e) => e.Kind != EditorItemKind.Fire || e.Lit || _edAllFires;

    // ------------------------------------------------------------------ dessin 3D
    partial void EditorRender3D(float time)
    {
        GraphicsDevice gd = GraphicsDevice;
        BoundingFrustum view = new BoundingFrustum(_viewProj.X());
        gd.DepthStencilState = DepthStencilState.Default;
        gd.BlendState = BlendState.Opaque;
        gd.RasterizerState = RasterizerState.CullNone;
        if (_edTerrain != null)
            DrawMesh(_edTerrain, Matrix.Identity);
        foreach (EditorObject e in _edObjects)
            if (e.Model != null && view.Intersects(new BoundingSphere(e.Center.X(), e.Radius)))
                EdDrawModel(e.Model, e.Shown, e.HiddenParts);
        // objet en train d'être posé : à la place visée
        if (_edDrag is { } di && _edDragAt is { } at)
        {
            SN.Matrix4x4 w = LevelEdit.MatrixOf(_edDragEuler, at);
            if (di.Model != null)
                EdDrawModel(di.Model, w, di.Kind == EditorItemKind.Fireman && di.ClassPath != null ? EdHiddenTools(di.ClassPath, di.Model) : null);
            else if (di.TreeModel != null)
                EdDrawTree(di.TreeModel, di.TreeSeed, di.TreeHeight, w);
        }
        EdDrawMarkers();
        EdDrawFires();
    }

    partial void EditorShadows()
    {
        BoundingFrustum light = new BoundingFrustum(_lightVp);
        if (_edTerrain != null)
            DrawMesh(_edTerrain, Matrix.Identity);
        foreach (EditorObject e in _edObjects)
            if (e.Model != null && light.Intersects(new BoundingSphere(e.Center.X(), e.Radius)))
                EdDrawModel(e.Model, e.Shown, e.HiddenParts);
    }

    /// <summary>Un modèle, sans les pièces cachées (outils qu'un pompier n'a pas).</summary>
    private void EdDrawModel(string model, SN.Matrix4x4 world, HashSet<int>? hidden)
    {
        List<(MeshGpu mesh, SN.Matrix4x4 part)> parts = UnitModel(model);
        for (int i = 0; i < parts.Count; i++)
            if (hidden == null || !hidden.Contains(i))
                DrawMesh(parts[i].mesh, (parts[i].part * world).X());
    }

    // ---- arbres : dessinés par le jeu (DrawTrees, liste _treeDraw) ; l'éditeur refait leurs places quand l'un bouge ----
    private bool _edTreesDirty;

    private void EdRebuildTrees()
    {
        _edTreesDirty = false;
        foreach ((MeshGpu mesh, List<(Matrix world, BoundingSphere bounds)> at) t in _treeDraw)
            t.at.Clear();
        foreach (EditorObject e in _edObjects.Where(o => o.Kind == EditorItemKind.Tree && o.TreeModel != null))
        {
            (int bark, int leaves, float h, float y0) v = TreeVariant(e.TreeModel!, e.TreeSeed);
            SN.Matrix4x4 w = TreeWorld(v, e.TreeHeight, e.Shown);
            BoundingSphere bounds = new BoundingSphere(new Vector3(e.Shown.M41, e.Shown.M42, e.Shown.M43 + e.TreeHeight / 2), e.TreeHeight * 0.75f);
            _treeDraw[v.bark].at.Add((w.X(), bounds));
            _treeDraw[v.leaves].at.Add((w.X(), bounds));
        }
    }

    /// <summary>Un arbre seul (objet qu'on pose, aperçu de la liste).</summary>
    private void EdDrawTree(string model, int seed, float height, SN.Matrix4x4 place)
    {
        (int bark, int leaves, float h, float y0) v = TreeVariant(model, seed);
        SN.Matrix4x4 w = TreeWorld(v, height, place);
        DrawMesh(_treeDraw[v.bark].mesh, w.X());
        DrawMesh(_treeDraw[v.leaves].mesh, w.X());
    }

    private readonly List<VertexPositionColor> _edLines = new(), _edTris = new();

    private void EdLine3(SN.Vector3 a, SN.Vector3 b, Color c)
    {
        _edLines.Add(new VertexPositionColor(a.X(), c));
        _edLines.Add(new VertexPositionColor(b.X(), c));
    }

    private void EdBox3(SN.Vector3 min, SN.Vector3 max, SN.Matrix4x4 w, Color c)
    {
        SN.Vector3 P(int i) => SN.Vector3.Transform(new SN.Vector3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z), w);
        int[] edges = { 0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7 };
        for (int i = 0; i < edges.Length; i += 2)
            EdLine3(P(edges[i]), P(edges[i + 1]), c);
    }

    private void EdQuad3(SN.Vector3 a, SN.Vector3 b, SN.Vector3 c, SN.Vector3 d, Color col)
    {
        foreach (SN.Vector3 p in new[] { a, b, c, a, c, d })
            _edTris.Add(new VertexPositionColor(p.X(), col));
    }

    /// <summary>Zones, bloqueurs, masques, caméras, points de passage, et la boîte de l'objet choisi.</summary>
    private void EdDrawMarkers()
    {
        _edLines.Clear();
        _edTris.Clear();
        foreach (EditorObject e in _edObjects)
        {
            SN.Matrix4x4 w = e.Shown;
            switch (e.Kind)
            {
                case EditorItemKind.Zone:
                    EdBox3(e.BoxMin, e.BoxMax, w, new Color(80, 230, 110));
                    EdQuad3(SN.Vector3.Transform(new(e.BoxMin.X, e.BoxMin.Y, 3), w), SN.Vector3.Transform(new(e.BoxMax.X, e.BoxMin.Y, 3), w),
                        SN.Vector3.Transform(new(e.BoxMax.X, e.BoxMax.Y, 3), w), SN.Vector3.Transform(new(e.BoxMin.X, e.BoxMax.Y, 3), w), new Color(80, 230, 110, 60));
                    break;
                case EditorItemKind.RoofMask:
                    EdQuad3(SN.Vector3.Transform(new(e.BoxMin.X, e.BoxMin.Y, 0), w), SN.Vector3.Transform(new(e.BoxMax.X, e.BoxMin.Y, 0), w),
                        SN.Vector3.Transform(new(e.BoxMax.X, e.BoxMax.Y, 0), w), SN.Vector3.Transform(new(e.BoxMin.X, e.BoxMax.Y, 0), w), new Color(0, 0, 0, 235));
                    break;
                case EditorItemKind.Camera when SN.Vector3.Distance(e.Position, CurrentPose().eye) > 400:
                {
                    // petite pyramide : la caméra regarde vers −Y de son repère (comme les repères de caméra du jeu : WaypointPose)
                    Color blue = new Color(90, 170, 255);
                    SN.Vector3 o = w.Translation;
                    SN.Vector3 f = -SN.Vector3.Normalize(new SN.Vector3(w.M21, w.M22, w.M23)) * 260;
                    SN.Vector3 r = SN.Vector3.Normalize(new SN.Vector3(w.M11, w.M12, w.M13)) * 110, u = SN.Vector3.Normalize(new SN.Vector3(w.M31, w.M32, w.M33)) * 80;
                    SN.Vector3[] c = { o + f + r + u, o + f - r + u, o + f - r - u, o + f + r - u };
                    for (int i = 0; i < 4; i++)
                    {
                        EdLine3(o, c[i], blue);
                        EdLine3(c[i], c[(i + 1) % 4], blue);
                    }
                    EdBox3(new(-40, -40, -40), new(40, 40, 40), w, blue);
                    break;
                }
                case EditorItemKind.Waypoint:
                {
                    Color c = new Color(120, 190, 255);
                    SN.Vector3 o = e.Position;
                    EdLine3(o, o + new SN.Vector3(0, 0, 160), c);
                    EdLine3(o + new SN.Vector3(-40, 0, 120), o + new SN.Vector3(40, 0, 120), c);
                    EdLine3(o + new SN.Vector3(0, -40, 120), o + new SN.Vector3(0, 40, 120), c);
                    break;
                }
            }
        }
        EdHeightCursor();
        EdCellMarks();
        if (_edDrag is { Model: null, TreeModel: null } di && _edDragAt is { } at)
        {
            EditorObject ghost = new EditorObject { Source = _edLevel.Objects[0], Kind = di.Kind, Position = at, Euler = _edDragEuler };
            ghost.Size = di.Kind == EditorItemKind.RoofMask ? new SN.Vector2(400, 400) : new SN.Vector2(1, 1);
            (SN.Vector3 min, SN.Vector3 max) gb = EdMarkerBox(ghost);
            if (di.Kind == EditorItemKind.Zone)
                gb = (new(-200, -200, 0), new(200, 200, 300));
            EdBox3(gb.min, gb.max, ghost.World, new Color(255, 200, 80));
        }
        GraphicsDevice gd = GraphicsDevice;
        _edFx ??= new BasicEffect(gd) { VertexColorEnabled = true, LightingEnabled = false, TextureEnabled = false };
        _edFx.World = Matrix.Identity;
        _edFx.View = _view.X();
        _edFx.Projection = _proj.X();
        gd.RasterizerState = RasterizerState.CullNone;
        if (_edTris.Count > 0)
        {
            gd.BlendState = BlendState.NonPremultiplied;
            gd.DepthStencilState = DepthStencilState.DepthRead;
            foreach (EffectPass pass in _edFx.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserPrimitives(PrimitiveType.TriangleList, _edTris.ToArray(), 0, _edTris.Count / 3);
            }
        }
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        EdFlushLines();
        // objet choisi : sa boîte, toujours visible (même derrière un mur)
        if (_edSel is { } s)
        {
            EdBox3(s.BoxMin, s.BoxMax, s.Shown, new Color(255, 140, 40));
            gd.DepthStencilState = DepthStencilState.None;
            EdFlushLines();
            gd.DepthStencilState = DepthStencilState.Default;
        }
    }

    private void EdFlushLines()
    {
        if (_edLines.Count == 0 || _edFx == null)
            return;
        foreach (EffectPass pass in _edFx.CurrentTechnique.Passes)
        {
            pass.Apply();
            GraphicsDevice.DrawUserPrimitives(PrimitiveType.LineList, _edLines.ToArray(), 0, _edLines.Count / 2);
        }
        _edLines.Clear();
    }

    /// <summary>Feux allumés au départ : une image de flamme, sans animation ; foyers éteints : petit carré gris (touche F).</summary>
    private void EdDrawFires()
    {
        Texture2D? tex = FlameTexture(DefaultFlameModel);
        List<VertexPositionColorTexture> flames = new List<VertexPositionColorTexture>(), dots = new List<VertexPositionColorTexture>();
        SN.Vector3 right = SN.Vector3.Normalize(new SN.Vector3(_view.M11, _view.M21, _view.M31));
        SN.Vector3 up = SN.Vector3.UnitZ;
        const int Frame = 9;
        float u0 = (Frame % FlameCols) / (float)FlameCols, v0 = (Frame / FlameCols) / (float)FlameRows;
        float u1 = u0 + 1f / FlameCols, v1 = v0 + 1f / FlameRows;
        foreach (EditorObject e in _edObjects)
        {
            if (e.Kind != EditorItemKind.Fire || !EdShown(e))
                continue;
            if (e.Lit)
            {
                float k = e.FireClass != null && e.FireClass.IntensityMax > 0 ? Math.Clamp(e.Intensity / e.FireClass.IntensityMax, 0.25f, 1) : 0.6f;
                float w = FireSize(e) * (0.6f + 0.4f * k), h = w * 1.25f;
                SN.Vector3 b = e.Position + new SN.Vector3(0, 0, 5);
                SN.Vector3 r = right * (w / 2);
                SN.Vector3 tint = FlameTint(e.FireClass?.FireType ?? 'A', 0.4f + 0.5f * k);
                Color col = new Color(tint.X, tint.Y, tint.Z, 1f);
                EdQuadTex(flames, b - r, b + r, b + r + up * h, b - r + up * h, col, u0, v1, u1, v0);
            }
            else
            {
                SN.Vector3 b = e.Position + new SN.Vector3(0, 0, 10);
                SN.Vector3 r = right * 30;
                EdQuadTex(dots, b - r, b + r, b + r + up * 60, b - r + up * 60, new Color(150, 150, 160), 0, 1, 1, 0);
            }
        }
        GraphicsDevice gd = GraphicsDevice;
        gd.DepthStencilState = DepthStencilState.DepthRead;
        gd.BlendState = BlendState.NonPremultiplied;
        gd.SamplerStates[0] = SamplerState.LinearClamp;
        _part.World = Matrix.Identity;
        if (flames.Count > 0 && tex != null)
        {
            _part.Texture = tex;
            foreach (EffectPass pass in _part.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserPrimitives(PrimitiveType.TriangleList, flames.ToArray(), 0, flames.Count / 3);
            }
        }
        if (dots.Count > 0)
        {
            _part.Texture = Tex.White;
            foreach (EffectPass pass in _part.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserPrimitives(PrimitiveType.TriangleList, dots.ToArray(), 0, dots.Count / 3);
            }
        }
        _part.Texture = Tex.Soft;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.BlendState = BlendState.Opaque;
    }

    private static void EdQuadTex(List<VertexPositionColorTexture> l, SN.Vector3 a, SN.Vector3 b, SN.Vector3 c, SN.Vector3 d, Color col, float u0, float vBottom, float u1, float vTop)
    {
        l.Add(new VertexPositionColorTexture(a.X(), col, new Vector2(u0, vBottom)));
        l.Add(new VertexPositionColorTexture(b.X(), col, new Vector2(u1, vBottom)));
        l.Add(new VertexPositionColorTexture(c.X(), col, new Vector2(u1, vTop)));
        l.Add(new VertexPositionColorTexture(a.X(), col, new Vector2(u0, vBottom)));
        l.Add(new VertexPositionColorTexture(c.X(), col, new Vector2(u1, vTop)));
        l.Add(new VertexPositionColorTexture(d.X(), col, new Vector2(u0, vTop)));
    }

    /// <summary>Nom de l'objet choisi, au-dessus de lui.</summary>
    private void EdOverlay3D()
    {
        if (_edSel is not { } s)
            return;
        SN.Vector4 p = SN.Vector4.Transform(new SN.Vector4(s.Center + new SN.Vector3(0, 0, (s.BoxMax.Z - s.BoxMin.Z) / 2 + 40), 1), _viewProj);
        if (p.W <= 0)
            return;
        float sx = (p.X / p.W * 0.5f + 0.5f) * _fbW, sy = (0.5f - p.Y / p.W * 0.5f) * _fbH;
        string label = s.Name;
        float tw = _ui.Measure(label);
        _ui.Begin();
        _ui.Rect(sx - tw / 2 - 5 * _scale, sy - _ui.LineHeight - 6 * _scale, tw + 10 * _scale, _ui.LineHeight + 4 * _scale, new SN.Vector4(0, 0, 0, 0.6f));
        _ui.Text(sx - tw / 2, sy - _ui.LineHeight - 4 * _scale, label, EdAccent);
        _ui.End();
    }

    // ------------------------------------------------------------------ vue de dessus (Tab)
    private void EdRenderMap()
    {
        if (_mapScale <= 0)
            _mapScale = MathF.Min(_fbW / MathF.Max(1, _sceneMax.X - _sceneMin.X), _fbH / MathF.Max(1, _sceneMax.Y - _sceneMin.Y)) * 0.92f;
        GraphicsDevice.Clear(new Color(0.07f, 0.07f, 0.08f));
        float px = _scale;
        if (_gridTex != null)
        {
            _ui.Begin(SamplerState.PointClamp);
            SN.Vector2 a = MapToScreen(0, _gridH);
            _ui.Image(_gridTex, a.X, a.Y, _gridW * _mapScale, _gridH * _mapScale, SN.Vector4.One);
            _ui.End();
        }
        _ui.Begin();
        foreach (Rooms.Room r in _session.RoomList)
        {
            SN.Vector2 a = MapToScreen(r.Min.X, r.Max.Y);
            _ui.Frame(a.X, a.Y, (r.Max.X - r.Min.X) * _mapScale, (r.Max.Y - r.Min.Y) * _mapScale, new SN.Vector4(0.37f, 0.66f, 0.83f, 0.5f), px);
        }
        foreach (EditorObject e in _edObjects)
        {
            if (!EdShown(e))
                continue;
            SN.Vector2 c = MapToScreen(e.Position.X, e.Position.Y);
            bool sel = e == _edSel;
            switch (e.Kind)
            {
                case EditorItemKind.Fire:
                    _ui.Disc(c.X, c.Y, MathF.Max(3 * px, FireSize(e) * 0.3f * _mapScale), e.Lit ? new SN.Vector4(1, 0.5f, 0.12f, 1) : new SN.Vector4(0.4f, 0.4f, 0.45f, 1));
                    break;
                case EditorItemKind.Camera:
                case EditorItemKind.Waypoint:
                    _ui.Disc(c.X, c.Y, 4 * px, new SN.Vector4(0.4f, 0.7f, 1, 1));
                    break;
                default:
                    SN.Vector4 col = e.Kind switch
                    {
                        EditorItemKind.Zone => new SN.Vector4(0.3f, 0.9f, 0.45f, 0.9f),
                        EditorItemKind.RoofMask => new SN.Vector4(0, 0, 0, 1),
                        EditorItemKind.Vehicle or EditorItemKind.Fireman => new SN.Vector4(0.9f, 0.2f, 0.2f, 1),
                        EditorItemKind.Civilian => new SN.Vector4(0.35f, 0.95f, 0.45f, 1),
                        EditorItemKind.Dynamic => new SN.Vector4(0.95f, 0.75f, 0.25f, 0.9f),
                        _ => new SN.Vector4(0.75f, 0.75f, 0.8f, 0.6f),
                    };
                    EdMapBox(e, col, px);
                    break;
            }
            if (sel)
                EdMapBox(e, EdAccent, 2 * px);
        }
        _ui.End();
    }

    private void EdMapBox(EditorObject e, SN.Vector4 col, float t)
    {
        SN.Vector2[] p = new SN.Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            SN.Vector3 v = SN.Vector3.Transform(new SN.Vector3(i is 1 or 2 ? e.BoxMax.X : e.BoxMin.X, i >= 2 ? e.BoxMax.Y : e.BoxMin.Y, 0), e.Shown);
            p[i] = MapToScreen(v.X, v.Y);
        }
        for (int i = 0; i < 4; i++)
            _ui.Line(p[i].X, p[i].Y, p[(i + 1) % 4].X, p[(i + 1) % 4].Y, col, t);
    }

    // ------------------------------------------------------------------ souris : choisir, déplacer, poser
    /// <summary>Objet sous la souris (coordonnées de la fenêtre), le plus proche de la caméra.</summary>
    private EditorObject? EdPick(double wx, double wy)
    {
        (float sx, float sy) = Fb(wx, wy);
        if (_map)
        {
            SN.Vector2 m = ScreenToMap(sx, sy);
            EditorObject? best = null;
            float bestArea = float.MaxValue;
            foreach (EditorObject e in _edObjects.Where(EdShown))
            {
                if (!SN.Matrix4x4.Invert(e.Shown, out SN.Matrix4x4 inv))
                    continue;
                SN.Vector3 l = SN.Vector3.Transform(new SN.Vector3(m, e.Position.Z), inv);
                float tol = 6 * _scale / _mapScale;
                if (l.X < e.BoxMin.X - tol || l.X > e.BoxMax.X + tol || l.Y < e.BoxMin.Y - tol || l.Y > e.BoxMax.Y + tol)
                    continue;
                float area = (e.BoxMax.X - e.BoxMin.X) * (e.BoxMax.Y - e.BoxMin.Y);
                if (area < bestArea)
                {
                    bestArea = area;
                    best = e;
                }
            }
            return best;
        }
        if (!SN.Matrix4x4.Invert(_viewProj, out SN.Matrix4x4 vinv))
            return null;
        float nx = sx / _fbW * 2 - 1, ny = 1 - sy / _fbH * 2;
        SN.Vector4 a = SN.Vector4.Transform(new SN.Vector4(nx, ny, 0, 1), vinv);
        SN.Vector4 b = SN.Vector4.Transform(new SN.Vector4(nx, ny, 1, 1), vinv);
        SN.Vector3 o = new SN.Vector3(a.X, a.Y, a.Z) / a.W;
        SN.Vector3 d = SN.Vector3.Normalize(new SN.Vector3(b.X, b.Y, b.Z) / b.W - o);
        EditorObject? hit = null;
        float bt = float.MaxValue;
        foreach (EditorObject e in _edObjects)
        {
            if (!EdShown(e) || !SN.Matrix4x4.Invert(e.Shown, out SN.Matrix4x4 inv))
                continue;
            // rayon dans le repère de l'objet (sans échelle : les distances restent celles du monde)
            SN.Vector3 lo = SN.Vector3.Transform(o, inv), ld = SN.Vector3.TransformNormal(d, inv);
            if (RayBox(lo, ld, e.BoxMin, e.BoxMax) is float t && t < bt)
            {
                // les repères (zones…) passent après les objets qui les recouvrent : on les vise par leur bord
                bt = t;
                hit = e;
            }
        }
        return hit;
    }

    private static float? RayBox(SN.Vector3 o, SN.Vector3 d, SN.Vector3 min, SN.Vector3 max)
    {
        float t0 = 0, t1 = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float oi = i == 0 ? o.X : i == 1 ? o.Y : o.Z, di = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
            float lo = i == 0 ? min.X : i == 1 ? min.Y : min.Z, hi = i == 0 ? max.X : i == 1 ? max.Y : max.Z;
            if (MathF.Abs(di) < 1e-9f)
            {
                if (oi < lo || oi > hi)
                    return null;
                continue;
            }
            float a = (lo - oi) / di, b = (hi - oi) / di;
            if (a > b)
                (a, b) = (b, a);
            t0 = MathF.Max(t0, a);
            t1 = MathF.Min(t1, b);
            if (t0 > t1)
                return null;
        }
        return t0;
    }

    private float EdGroundZ(SN.Vector2 p) => _session?.Units.GroundZ(p) ?? 0;

    private void EdSelect(EditorObject? e)
    {
        if (e != _edSel)
            EdCommitFocus();
        _edSel = e;
        _edCell = null;
        _edPropBar.Value = 0;
    }

    private void EdObjectMouse(MouseState m, bool inView)
    {
        // pose d'un objet tiré de la liste : il suit la souris sur le sol, on le lâche dans la vue
        if (_edDrag != null)
        {
            _edDragAt = inView && Ground(m.X, m.Y) is { } g ? new SN.Vector3(g, EdGroundZ(g) + _edDragDz) : null;
            if (_edReleaseL || m.LeftButton == ButtonState.Released)
            {
                _edReleaseL = false;
                EditorItem item = _edDrag;
                SN.Vector3? at = _edDragAt;
                _edDrag = null;
                _edDragAt = null;
                if (at is { } p)
                    EdDrop(item, p);
            }
            return;
        }
        if (inView && _edPressL)
        {
            _edPressL = false;
            EditorObject? hit = EdPick(m.X, m.Y);
            EdSelect(hit);
            // clic sur le sol, sans objet : la case de la grille (ses propriétés à droite)
            if (hit == null && Ground(m.X, m.Y) is { } cg && _session.Grid is { } grid)
            {
                (int cx, int cy) = grid.CellOf(new SN.Vector3(cg, 0));
                if (grid.Inside(cx, cy))
                    _edCell = (cx, cy);
            }
            if (hit != null && Ground(m.X, m.Y) is { } g)
            {
                _edMoveArmed = true;
                _edDownX = m.X;
                _edDownY = m.Y;
                _edMoveOffset = new SN.Vector2(hit.Position.X, hit.Position.Y) - g;
                _edMoveDz = hit.Position.Z - EdGroundZ(new SN.Vector2(hit.Position.X, hit.Position.Y));
                _edMoveFrom = (hit.Position, hit.Euler);
            }
        }
        if (_edMoveArmed && !_edMoving && m.LeftButton == ButtonState.Pressed && Math.Abs(m.X - _edDownX) + Math.Abs(m.Y - _edDownY) > 4 * _scale)
            _edMoving = true;
        if (_edMoving && _edSel is { } s && Ground(m.X, m.Y) is { } at2)
        {
            SN.Vector2 p = at2 + _edMoveOffset;
            s.Position = new SN.Vector3(p, EdGroundZ(p) + _edMoveDz);
            EdPlace(s);
        }
        if (m.LeftButton == ButtonState.Released)
        {
            if (_edMoving && _edSel != null)
                EdMessage(Tr("Moved", "{0} déplacé en ({1:0}, {2:0}, {3:0})", _edSel.Name, _edSel.Position.X, _edSel.Position.Y, _edSel.Position.Z));
            _edMoving = false;
            _edMoveArmed = false;
        }
    }

    private void EdCancelMove()
    {
        if (_edSel is { } s)
        {
            s.Position = _edMoveFrom.pos;
            s.Euler = _edMoveFrom.euler;
            EdPlace(s);
        }
        _edMoving = false;
        _edMoveArmed = false;
    }

    /// <summary>Flèches pendant qu'on pose ou déplace un objet : il tourne (Maj : plus vite) ; Pg.Préc / Pg.Suiv : plus haut, plus bas.</summary>
    private void EdRotateKeys(float dt)
    {
        float sp = (EdShift ? 3f : 1f) * 1.8f * dt;
        SN.Vector3 d = SN.Vector3.Zero;
        if (_edKeys.IsKeyDown(Keys.Left))
            d.Z += sp;
        if (_edKeys.IsKeyDown(Keys.Right))
            d.Z -= sp;
        if (_edKeys.IsKeyDown(Keys.Up))
            d.X += sp;
        if (_edKeys.IsKeyDown(Keys.Down))
            d.X -= sp;
        float dz = (_edKeys.IsKeyDown(Keys.PageUp) ? 1 : 0) - (_edKeys.IsKeyDown(Keys.PageDown) ? 1 : 0);
        dz *= (EdShift ? 600 : 200) * dt;
        if (EdCtrl)
            d = new SN.Vector3(0, d.X, d.Z);   // Ctrl + haut/bas : basculer sur l'autre axe
        if (_edDrag != null)
        {
            _edDragEuler = EdWrap(_edDragEuler + d);
            _edDragDz += dz;
        }
        else if (_edSel is { } s)
        {
            s.Euler = EdWrap(s.Euler + d);
            _edMoveDz += dz;
            s.Position.Z += dz;
            if (d != SN.Vector3.Zero || dz != 0)
                EdPlace(s);
        }
    }

    private static SN.Vector3 EdWrap(SN.Vector3 e)
    {
        static float W(float a) => a > MathF.PI ? a - MathF.Tau : a < -MathF.PI ? a + MathF.Tau : a;
        return new SN.Vector3(W(e.X), W(e.Y), W(e.Z));
    }

    // ------------------------------------------------------------------ ajout et suppression
    private List<Level>? _edOtherLevels;

    /// <summary>Niveaux de toutes les missions (pour copier un objet d'une classe absente de la mission ouverte).</summary>
    private List<Level> EdOtherLevels()
    {
        if (_edOtherLevels != null)
            return _edOtherLevels;
        _edOtherLevels = new List<Level>();
        foreach (string lv in _files.List("Design\\Mission\\").Where(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                _edOtherLevels.Add(Level.Parse(_files.Read(lv)));
            }
            catch (Exception e)
            {
                Console.WriteLine($"{lv} : {e.Message}");
            }
        }
        return _edOtherLevels;
    }

    /// <summary>
    /// Objet à recopier pour en créer un nouveau : un objet de la même classe (dans cette mission, sinon dans une autre),
    /// sinon un objet simple du même genre d'entité, dont on change la classe.
    /// </summary>
    private LevelObject? EdTemplate(EditorItem item)
    {
        bool Same(LevelObject o) => o.HasTransform && o.Kind == item.Entity && item.ClassPath != null && o.ClassPath.Equals(item.ClassPath, StringComparison.OrdinalIgnoreCase);
        LevelObject? t = _edLevel.Objects.FirstOrDefault(Same) ?? EdOtherLevels().SelectMany(l => l.Objects).FirstOrDefault(Same);
        if (t != null)
            return t;
        bool Kind(LevelObject o) => o.HasTransform && o.Kind == item.Entity && (item.Entity != EntityKind.HotPoint || LevelEdit.FireState(o) is { full: true });
        IEnumerable<LevelObject> all = _edLevel.Objects.Where(Kind).Concat(EdOtherLevels().SelectMany(l => l.Objects).Where(Kind));
        // objet du décor : de préférence un repère simple (pas une porte, ni un objet qui explose)
        if (item.Entity == EntityKind.Placeable)
            return all.FirstOrDefault(o => o.ClassName.EndsWith("_symbol", StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
        if (item.Entity == EntityKind.FireVehicle && item.Kind == EditorItemKind.Fireman)
            return all.FirstOrDefault(o => o.ClassName.Contains("aerial", StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
        return all.FirstOrDefault();
    }

    private string EdNewName(string baseName)
    {
        HashSet<string> names = _edLevel.Objects.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; ; i++)
            if (!names.Contains(baseName + i))
                return baseName + i;
    }

    /// <summary>Remplace l'objet du niveau (même numéro) par sa nouvelle version.</summary>
    private void EdUpdate(EditorObject e, LevelObject o)
    {
        int i = _edLevel.Objects.FindIndex(x => x.Id == e.Source.Id);
        if (i >= 0)
            _edLevel.Objects[i] = o;
        e.Source = o;
    }

    private void EdDrop(EditorItem item, SN.Vector3 at)
    {
        if (EdTemplate(item) is not { } tpl)
        {
            EdMessage(Tr("NoTemplate", "{0} : aucun objet de ce genre dans les missions du jeu, impossible d'en créer un", item.Name), true);
            return;
        }
        string cls = item.ClassPath ?? tpl.ClassPath;
        string leaf = cls[(cls.LastIndexOf('\\') + 1)..];
        if (item.Kind == EditorItemKind.RoofMask)
            cls = EditorCatalog.MaskClass + "_400x400";
        if (item.Kind == EditorItemKind.Fire)
        {
            cls = tpl.ClassPath;
            leaf = "foyer_";
        }
        else if (item.Kind == EditorItemKind.Camera)
            leaf = "cam_startpos";
        else
            leaf += "_";
        uint id = LevelEdit.NewId(_edLevel);
        LevelObject o = LevelEdit.Clone(tpl, id, item.Kind == EditorItemKind.Camera && !_edLevel.Objects.Any(x => x.Name.Equals("cam_startpos", StringComparison.OrdinalIgnoreCase)) ? "cam_startpos" : EdNewName(leaf), cls);
        o = LevelEdit.WithTransform(o, at, _edDragEuler);
        o = LevelEdit.WithStartVisible(o, true);
        if (item.Kind == EditorItemKind.Zone)
            o = LevelEdit.WithTrigger(o, "", "", true, new SN.Vector3(400, 400, 300));
        _edLevel.Objects.Add(o);
        EditorObject? e = EdMake(o);
        if (e == null)
        {
            EdMessage(Tr("NothingToDraw", "{0} : objet créé mais rien à dessiner", item.Name), true);
            return;
        }
        e.Added = true;
        if (item.Kind == EditorItemKind.Fire)
        {
            e.FireClass ??= EdFireClass(o.ClassPath);
            e.Lit = true;
            e.Intensity = MathF.Max(10, (e.FireClass?.IntensityMax ?? 100) * 0.5f);
            EdUpdate(e, LevelEdit.WithFire(e.Source, true, e.Intensity));
        }
        if (item.Kind == EditorItemKind.Camera)
        {
            // la caméra de départ regarde comme la vue actuelle
            EdCameraFromView(e);
        }
        EdPlace(e);
        _edObjects.Add(e);
        EdSelect(e);
        _edDragEuler = SN.Vector3.Zero;
        _edDragDz = 0;
        EdMessage(Tr("Added", "{0} ajouté ({1})", e.Name, item.Name));
    }

    private void EdDelete(EditorObject e)
    {
        _edObjects.Remove(e);
        uint id = e.Source.Id;
        _edLevel.Objects.RemoveAll(o => o.Id == id);
        for (int i = 0; i < _edLevel.Objects.Count; i++)
            if (_edLevel.Objects[i].Members.Contains(id))
                _edLevel.Objects[i] = LevelEdit.WithoutMember(_edLevel.Objects[i], id);
        if (_edSel == e)
            EdSelect(null);
        if (e.Kind == EditorItemKind.Tree)
            _edTreesDirty = true;
        EdMessage(Tr("Removed", "{0} enlevé", e.Name));
    }

    /// <summary>
    /// Caméra de départ : placée et orientée comme la vue actuelle. Le jeu la fait regarder vers −Y de son repère
    /// (WaypointPose) ; avec Rz·Rx·Ry et y = 0, −Y vaut (sin z, −cos z cos x, −cos z sin x) : on en tire z puis x.
    /// </summary>
    private void EdCameraFromView(EditorObject cam)
    {
        (SN.Vector3 eye, SN.Vector3 f) = CurrentPose();
        float z = MathF.Asin(Math.Clamp(f.X, -1, 1));
        float x = MathF.Atan2(-f.Z, -f.Y);
        cam.Position = eye;
        cam.Euler = new SN.Vector3(x, 0, z);
        EdPlace(cam);
    }

    /// <summary>La vue se met à la place de la caméra de départ (comme au début de la mission).</summary>
    private void EdViewFromCamera(EditorObject cam) => ApplyPose(WaypointPose(LevelEdit.WithTransform(cam.Source, cam.Position, cam.Euler)));
}
