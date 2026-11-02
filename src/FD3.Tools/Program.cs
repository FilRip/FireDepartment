using FD3.Game;
using FD3.Formats;

namespace FD3.Tools;

public static class Program
{
    public static int Main(string[] args)
    {
        // Outils de vérification : dotnet run --project src/FD3.Tools -- <commande> "<chemin vers Data>"
        if (args.Length < 2) { Console.WriteLine("Usage : roundtrip|fireclasses <dossier Data>"); return 1; }
        var files = new GameFiles(args[1]);

        switch (args[0])
        {
            case "roundtrip":
            {
                int bad = 0;
                foreach (var path in files.List("Design\\Mission\\").Where(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase)))
                {
                    var d = files.Read(path);
                    var L = Level.Parse(d);
                    var o = L.Serialize();
                    bool same = o.AsSpan().SequenceEqual(d);
                    if (!same) bad++;
                    var gp = Path.ChangeExtension(path, ".grid");
                    var g = files.TryRead(gp) is { } gb ? Grid.Parse(gb) : null;
                    Console.WriteLine($"{Path.GetFileName(path),-22} {L.Objects.Count,5} objets  {L.HotPointLinks.Count,4} liens  grille {g?.Width}x{g?.Height}  relecture {(same ? "identique" : "DIFFÉRENTE")}");
                }
                return bad == 0 ? 0 : 2;
            }
            case "pieces":
            {
                // pieces <Data> <Mission> [sortie.json] : pièce de chaque foyer et point de fumée (remplace roomall.py)
                var lvPath = files.List("Design\\Mission\\" + args[2] + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                var level = Level.Parse(files.Read(lvPath));
                var rooms = Rooms.Load(files, args[2]);
                var map = Rooms.Assign(level, rooms);
                Console.WriteLine($"{rooms.Count} pièces ; {map.Count(kv => kv.Value != "")} objets en pièce sur {map.Count}");
                if (args.Length > 3) File.WriteAllText(args[3], System.Text.Json.JsonSerializer.Serialize(map.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)));
                return 0;
            }
            case "portes":
            {
                // portes <Data> <Mission> : objets dont l'occupation est DOOR ou WINDOW (classe, position, composants)
                var ses = new Session(files, args[2], "EASY", false);
                var stats = new Dictionary<string, int>();
                foreach (var o in ses.Level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform))
                {
                    ClassFile cf;
                    try { cf = ClassFile.Load(files, "Design\\" + o.ClassPath["design\\".Length..] + ".class"); } catch { continue; }
                    var type = cf.Get("OCCUPATION", "Occupation_Type")?.Trim('"', ' ').ToUpperInvariant() ?? "";
                    stats[type] = stats.GetValueOrDefault(type) + 1;
                    if (type is not ("DOOR" or "WINDOW")) continue;
                    if (args.Length > 3 && !o.Name.Contains(args[3], StringComparison.OrdinalIgnoreCase) && args[3] != "*") continue;
                    Console.WriteLine($"{type,-6} {o.Name,-28} {o.ClassPath,-50} ({o.Position.X:0},{o.Position.Y:0},{o.Position.Z:0}) rotZ {o.Rotation.Z:0.00} comps {string.Join(",", o.Components.Select(c => c.Id.ToString("X4") + ":" + c.Size))}");
                }
                Console.WriteLine(string.Join(", ", stats.Select(kv => kv.Key + "=" + kv.Value)));
                return 0;
            }
            case "classefich":
            {
                // classefich <Data> <chemin Design\…\x.class>… : toutes les sections fusionnées (avec #INCLUDE)
                foreach (var rel in args.Skip(2))
                {
                    var cf = ClassFile.Load(files, rel);
                    Console.WriteLine("== " + rel);
                    foreach (var sec in cf.Sections) Console.WriteLine($"  [{sec}] " + string.Join(", ", cf.Section(sec).Select(kv => kv.Key + "=" + kv.Value)));
                }
                return 0;
            }
            case "compos":
            {
                // compos <Data> <Mission> <composant hex> <nom>… : octets d'un composant (hex + texte)
                var ses = new Session(files, args[2], "EASY", false);
                ushort id = Convert.ToUInt16(args[3], 16);
                foreach (var o in ses.Level.Objects.Where(o => args.Skip(4).Any(n => o.Name.Equals(n, StringComparison.OrdinalIgnoreCase) || (n.EndsWith("*") && o.Name.StartsWith(n[..^1], StringComparison.OrdinalIgnoreCase)))))
                    if (o.GetComponent(id) is { } c)
                    {
                        var b = o.ComponentBytes(c).ToArray();
                        Console.WriteLine($"{o.Name,-26} {Convert.ToHexString(b)}");
                        Console.WriteLine($"{"",-26} {new string(b.Select(x => x is >= 32 and < 127 ? (char)x : '.').ToArray())}");
                    }
                return 0;
            }
            case "plan":
            {
                // plan <Data> <Mission> x0 y0 x1 y1 : carte texte (# mur, o objet, D porte, F fenêtre, . libre), nord en haut
                var ses = new Session(files, args[2], "EASY", false); var g = ses.Grid!;
                int x0 = int.Parse(args[3]) / 100, y0 = int.Parse(args[4]) / 100, x1 = Math.Min(g.Width - 1, int.Parse(args[5]) / 100), y1 = Math.Min(g.Height - 1, int.Parse(args[6]) / 100);
                var marks = new Dictionary<(int, int), char>();
                foreach (var d in ses.Units.Doors) foreach (var i in d.Cells) marks[(i % g.Width, i / g.Width)] = d.IsWindow ? 'F' : 'D';
                for (int y = y1; y >= y0; y--)
                {
                    var sb = new System.Text.StringBuilder($"{y * 100,6} ");
                    for (int x = x0; x <= x1; x++)
                    {
                        int i = g.Index(x, y);
                        sb.Append(marks.TryGetValue((x, y), out var m) ? m : g.ZoneA[i] == 0 ? '#' : ses.Units.IsBlocked(i) ? 'o' : '.');
                    }
                    Console.WriteLine(sb);
                }
                foreach (var d in ses.Units.Doors.Where(d => d.Position.X >= x0 * 100 && d.Position.X <= x1 * 100 && d.Position.Y >= y0 * 100 && d.Position.Y <= y1 * 100))
                    Console.WriteLine($"{d.Name,-26} {d.State,-8} cassable {d.Breakable} ouvrable {d.Usable} ({d.Position.X:0},{d.Position.Y:0},{d.Position.Z:0}) cases {d.Cells.Count}");
                return 0;
            }
            case "fumeepieces":
            {
                // fumeepieces <Data> <Mission> <secondes> : fumée totale par pièce
                var ses = new Session(files, args[2], "EASY", false);
                for (int i = 0; i < float.Parse(args[3]) / Session.Dt; i++) ses.Step();
                foreach (var g in ses.Smoke.Points.GroupBy(p => p.Room == "" ? "(dehors)" : p.Room).OrderBy(g => g.Key))
                    if (g.Sum(p => p.Intensity) > 0.05) Console.WriteLine($"{g.Key,-28} {g.Sum(p => p.Intensity),8:0.0}");
                Console.WriteLine($"Total {ses.Smoke.Total:0.0}");
                return 0;
            }
            case "porte":
            {
                // porte <Data> <Mission> <porte>… : le premier pompier qui peut casser (sinon le premier piéton) s'occupe de chaque porte, dans l'ordre
                var ses = new Session(files, args[2], "EASY", false);
                for (int i = 0; i < 30; i++) ses.Step();
                foreach (var name in args.Skip(3))
                {
                    var d = ses.Units.Openings.First(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    var u = ses.Units.Units.Where(x => !x.IsVehicle).OrderBy(x => d.Breakable && x.CanBreak ? 0 : 1).First();
                    ses.Units.WorkOnOpening(u, d);
                    Console.WriteLine($"{u.Name} ({u.Position.X:0},{u.Position.Y:0}) → {d.Name} centre ({d.Center.X:0},{d.Center.Y:0},{d.Center.Z:0}) ({d.State}, vie {d.Life:0}) : {u.Status}, chemin {u.Path.Count}");
                    float t0 = ses.Time;
                    for (int i = 0; i < 3000 && u.Opening != null; i++) ses.Step();
                    Console.WriteLine($"   {ses.Time - t0:0.0} s plus tard : {d.Name} {d.State}, vie {d.Life:0}, coups {u.Blows}, {u.Name} : {u.Status}");
                }
                if (Environment.GetEnvironmentVariable("FD3_FUMEE") is { } fs)
                {
                    if (Environment.GetEnvironmentVariable("FD3_CASSER") is { } cs)
                        foreach (var n in cs.Split(','))
                            ses.Units.SetOpeningState(ses.Units.Openings.First(o => o.Name == n), OpeningState.Broken);
                    for (int i = 0; i < float.Parse(fs) / Session.Dt; i++) ses.Step();
                    foreach (var g in ses.Smoke.Points.GroupBy(p => p.Room == "" ? "(dehors)" : p.Room).OrderBy(g => g.Key))
                        if (g.Sum(p => p.Intensity) > 0.05) Console.WriteLine($"  fumée {g.Key,-28} {g.Sum(p => p.Intensity),8:0.0}");
                }
                foreach (var l in ses.Units.Log) Console.WriteLine(l);
                return 0;
            }
            case "ouvpieces":
            {
                // ouvpieces <Data> <Mission> : pour chaque porte/fenêtre, les pièces des points de fumée proches (InfluenceRange)
                var ses = new Session(files, args[2], "EASY", false);
                foreach (var o in ses.Units.Openings)
                {
                    var rooms = ses.Smoke.Points.Where(p => System.Numerics.Vector2.Distance(new(p.Position.X, p.Position.Y), new(o.Center.X, o.Center.Y)) < p.Class.InfluenceRange && MathF.Abs(p.Position.Z - o.Center.Z) < 400)
                        .GroupBy(p => p.Room == "" ? "(dehors)" : p.Room).Select(g => $"{g.Key}×{g.Count()}");
                    Console.WriteLine($"{o.Name,-28} {o.State,-7} z {o.Center.Z,5:0} : {string.Join(", ", rooms)}");
                }
                return 0;
            }
            case "conduire":
            {
                // conduire <Data> <Mission> x y : chaque véhicule roule vers (x, y) ; on vérifie qu'il ne traverse ni mur ni objet
                var ses = new Session(files, args[2], "EASY", false); var g = ses.Grid!;
                for (int i = 0; i < 300; i++) ses.Step();
                var dest = new System.Numerics.Vector2(float.Parse(args[3]), float.Parse(args[4]));
                foreach (var v in ses.Units.Units.Where(u => u.IsVehicle).ToList())
                {
                    ses.Units.Drive(v, dest);
                    Console.WriteLine($"{v.Name} ({v.Position.X:0},{v.Position.Y:0}) : {v.Status}, {v.Path.Count} points");
                    int bad = 0;
                    for (int i = 0; i < 3000 && v.Moving; i++)
                    {
                        ses.Step();
                        var (cx, cy) = g.CellOf(v.Position);
                        if (!g.Inside(cx, cy) || g.ZoneA[g.Index(cx, cy)] == 0 || ses.Units.IsBlocked(g.Index(cx, cy))) bad++;
                    }
                    Console.WriteLine($"   arrivé ({v.Position.X:0},{v.Position.Y:0}) à {System.Numerics.Vector2.Distance(new(v.Position.X, v.Position.Y), dest):0} du point ; pas dans un obstacle : {bad} ; {v.Status}");
                }
                return 0;
            }
            case "liste":
            {
                // liste <Data> <préfixe> [filtre] : fichiers des archives
                foreach (var f in files.List(args[2]).Where(f => args.Length < 4 || f.Contains(args[3], StringComparison.OrdinalIgnoreCase))) Console.WriteLine(f);
                return 0;
            }
            case "toutextraire":
            {
                // toutextraire <Data> <préfixe> <dossier> : copie tous les fichiers des archives sous ce préfixe
                int n = 0;
                foreach (var f in files.List(args[2]))
                {
                    var dest = Path.Combine(args[3], f.Replace('\\', '/'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    try { File.WriteAllBytes(dest, files.Read(f)); n++; } catch { }
                }
                Console.WriteLine($"{n} fichiers");
                return 0;
            }
            case "extraire":
            {
                // extraire <Data> <chemin> <sortie> : copie un fichier des archives
                File.WriteAllBytes(args[3], files.Read(args[2]));
                return 0;
            }
            case "rig":
            {
                // rig <Data> <modèle Gfx> [anim] : squelette, peau ; écart pose de référence / maillage d'origine ; étendue animée
                var assets = new FD3.Game.SceneAssets(files);
                var rig = FD3.Game.CharacterRig.Build(assets, files, args[2])!;
                Console.WriteLine($"{rig.Names.Length} nœuds, {rig.Parts.Count} pièces");
                var clip = args.Length > 3 ? AnimClip.Parse(files.Read("Gfx\\Anim\\" + args[3] + ".anim")) : null;
                if (clip != null) Console.WriteLine($"animation : {clip.Tracks.Count} pistes, {clip.Duration:0.00} s");
                foreach (var p in rig.Parts)
                {
                    Console.Write($"  {p.Mesh} nœud {rig.Names[p.Node]} peau {(p.Skin != null ? p.Skin.Bones.Length + " os" : "non")}");
                    if (p.Skin == null) { Console.WriteLine(); continue; }
                    var pos = new float[p.BasePos.Length]; var nrm = new float[pos.Length];
                    rig.Deform(p, rig.Pose(null, 0), pos, nrm);
                    float err = 0; for (int i = 0; i < pos.Length; i++) err = MathF.Max(err, MathF.Abs(pos[i] - p.BasePos[i]));
                    Console.Write($" · écart au repos {err:0.00}");
                    if (clip != null)
                        for (float t = 0; t <= clip.Duration; t += clip.Duration / 4)
                        {
                            rig.Deform(p, rig.Pose(clip, t), pos, nrm);
                            float zmin = float.MaxValue, zmax = float.MinValue;
                            for (int i = 2; i < pos.Length; i += 3) { zmin = MathF.Min(zmin, pos[i]); zmax = MathF.Max(zmax, pos[i]); }
                            Console.Write($" · t={t:0.00} z {zmin:0}..{zmax:0}");
                        }
                    Console.WriteLine();
                }
                return 0;
            }
            case "orient":
            {
                // orient <Data> <modèle> <anim> : direction des orteils (avant du personnage) et sens du mouvement des pieds au sol
                var assets = new FD3.Game.SceneAssets(files);
                var rig = FD3.Game.CharacterRig.Build(assets, files, args[2])!;
                int I(string n) => Array.FindIndex(rig.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                var w0 = rig.Pose(null, 0);
                Console.WriteLine($"orteil - pied (repos) : {w0[I("Bip01 L Toe0")].Translation - w0[I("Bip01 L Foot")].Translation}");
                var clip = AnimClip.Parse(files.Read("Gfx\\Anim\\" + args[3] + ".anim"));
                for (float t = 0; t < clip.Duration; t += clip.Duration / 8)
                {
                    var w = rig.Pose(clip, t);
                    Console.WriteLine($"t={t:0.00} pied G {w[I("Bip01 L Foot")].Translation} pied D {w[I("Bip01 R Foot")].Translation} orteil G-pied {w[I("Bip01 L Toe0")].Translation - w[I("Bip01 L Foot")].Translation}");
                }
                return 0;
            }
            case "decor":
            {
                // decor <Data> <Mission> [filtre] : maillages du décor (nom, nombre de sommets, boîte en monde)
                var assets = new FD3.Game.SceneAssets(files);
                foreach (var n in assets.Scenery(args[2]).Where(n => args.Length < 4 || n.Mesh.Contains(args[3], StringComparison.OrdinalIgnoreCase)))
                {
                    var a = assets.LoadMesh(args[2], n.Mesh, null); var m = a.Mesh;
                    var mn = new System.Numerics.Vector3(float.MaxValue); var mx = new System.Numerics.Vector3(float.MinValue);
                    for (int i = 0; i < m.VertexCount; i++) { var v = System.Numerics.Vector3.Transform(new(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]), n.World); mn = System.Numerics.Vector3.Min(mn, v); mx = System.Numerics.Vector3.Max(mx, v); }
                    Console.WriteLine($"{n.Mesh,-40} {m.VertexCount,6} sommets  ({mn.X:0},{mn.Y:0},{mn.Z:0}) – ({mx.X:0},{mx.Y:0},{mx.Z:0})  {string.Join(" ", a.Groups.Select(g => g.Diffuse))}");
                }
                return 0;
            }
            case "decorz":
            {
                // decorz <Data> <Mission> <maillage> : nombre de triangles par tranche de hauteur (centre), et horizontaux / verticaux
                var assets = new FD3.Game.SceneAssets(files);
                var n = assets.Scenery(args[2]).FirstOrDefault(n => n.Mesh.Equals(args[3], StringComparison.OrdinalIgnoreCase))
                    ?? new FD3.Game.SceneAssets.Node(args[3], Rooms.World(Rooms.SceneNodes(files.Read("Gfx\\Mission\\" + args[2] + "\\" + args[2] + ".sg")), args[3]));
                var a = assets.LoadMesh(args[2], n.Mesh, null); var m = a.Mesh;
                var hist = new SortedDictionary<int, (int h, int v)>();
                System.Numerics.Vector3 P(uint i) => System.Numerics.Vector3.Transform(new(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]), n.World);
                for (int k = 0; k + 2 < m.Indices.Length; k += 3)
                {
                    var p0 = P(m.Indices[k]); var p1 = P(m.Indices[k + 1]); var p2 = P(m.Indices[k + 2]);
                    var nn = System.Numerics.Vector3.Cross(p1 - p0, p2 - p0); bool horiz = MathF.Abs(nn.Z) > 0.9f * nn.Length();
                    int z = (int)((p0.Z + p1.Z + p2.Z) / 3 / 50) * 50;
                    var e = hist.GetValueOrDefault(z); hist[z] = horiz ? (e.h + 1, e.v) : (e.h, e.v + 1);
                }
                foreach (var (z, e) in hist) Console.WriteLine($"z {z,5} : {e.h,5} horizontaux {e.v,5} verticaux");
                Console.WriteLine($"uv : {string.Join(",", Enumerable.Range(0, 8).Select(i => m.Uv[i] != null ? i.ToString() : "-"))} lightmap {a.Lightmap} ; textures {string.Join(" ", a.Groups.Select(g => g.Diffuse ?? "(aucune)"))} ; {m.VertexCount} sommets");
                return 0;
            }
            case "murs":
            {
                // murs <Data> <Mission> [filtre pièce] : pour chaque pièce, triangles verticaux du décor sur ses bords (± 40)
                var ses = new Session(files, args[2], "EASY", false);
                var assets = new FD3.Game.SceneAssets(files);
                var tris = new List<(System.Numerics.Vector3 a, System.Numerics.Vector3 b, System.Numerics.Vector3 c, string mesh)>();
                foreach (var n in assets.Scenery(args[2]))
                {
                    var m = assets.LoadMesh(args[2], n.Mesh, null).Mesh;
                    System.Numerics.Vector3 P(uint i) => System.Numerics.Vector3.Transform(new(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]), n.World);
                    for (int k = 0; k + 2 < m.Indices.Length; k += 3)
                    {
                        var a = P(m.Indices[k]); var b = P(m.Indices[k + 1]); var c = P(m.Indices[k + 2]);
                        var nn = System.Numerics.Vector3.Cross(b - a, c - a); if (nn.Length() < 1 || MathF.Abs(nn.Z) > 0.3f * nn.Length()) continue;
                        tris.Add((a, b, c, n.Mesh));
                    }
                }
                foreach (var r in ses.RoomList.Where(r => args.Length < 4 || r.Name.Contains(args[3], StringComparison.OrdinalIgnoreCase)))
                {
                    var hits = new Dictionary<string, int>();
                    foreach (var t in tris)
                    {
                        var c = (t.a + t.b + t.c) / 3;
                        if (c.Z < r.Min.Z - 50 || c.Z > r.Max.Z + 50) continue;
                        bool onX = (MathF.Abs(c.X - r.Min.X) < 40 || MathF.Abs(c.X - r.Max.X) < 40) && c.Y > r.Min.Y && c.Y < r.Max.Y;
                        bool onY = (MathF.Abs(c.Y - r.Min.Y) < 40 || MathF.Abs(c.Y - r.Max.Y) < 40) && c.X > r.Min.X && c.X < r.Max.X;
                        if (onX || onY) hits[t.mesh] = hits.GetValueOrDefault(t.mesh) + 1;
                    }
                    Console.WriteLine($"{r.Name,-22} ({r.Min.X:0},{r.Min.Y:0},{r.Min.Z:0})-({r.Max.X:0},{r.Max.Y:0},{r.Max.Z:0}) : {string.Join(", ", hits.Select(h => h.Key + "×" + h.Value))}");
                }
                return 0;
            }
            case "zone":
            {
                // zone <Data> <Mission> x0 y0 x1 y1 : triangles verticaux du décor dans la zone, par maillage / texture / hauteur
                var assets = new FD3.Game.SceneAssets(files);
                float x0 = float.Parse(args[3]), y0 = float.Parse(args[4]), x1 = float.Parse(args[5]), y1 = float.Parse(args[6]);
                var stat = new SortedDictionary<string, (int n, float zmin, float zmax)>();
                foreach (var n in assets.Scenery(args[2]))
                {
                    var a = assets.LoadMesh(args[2], n.Mesh, null); var m = a.Mesh;
                    System.Numerics.Vector3 P(uint i) => System.Numerics.Vector3.Transform(new(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]), n.World);
                    foreach (var g in a.Groups)
                        for (int k = g.Start; k + 2 < g.Start + g.Count; k += 3)
                        {
                            var p0 = P(m.Indices[k]); var p1 = P(m.Indices[k + 1]); var p2 = P(m.Indices[k + 2]); var c = (p0 + p1 + p2) / 3;
                            if (c.X < x0 || c.X > x1 || c.Y < y0 || c.Y > y1) continue;
                            var nn = System.Numerics.Vector3.Cross(p1 - p0, p2 - p0); string kind = MathF.Abs(nn.Z) > 0.9f * nn.Length() ? "H" : "V";
                            string key = $"{n.Mesh} {g.Diffuse} alpha={g.Alpha} {kind}";
                            var e = stat.TryGetValue(key, out var e0) ? e0 : (n: 0, zmin: float.MaxValue, zmax: float.MinValue);
                            float zl = MathF.Min(p0.Z, MathF.Min(p1.Z, p2.Z)), zh = MathF.Max(p0.Z, MathF.Max(p1.Z, p2.Z));
                            stat[key] = (e.n + 1, MathF.Min(e.zmin, zl), MathF.Max(e.zmax, zh));
                        }
                }
                foreach (var (k, e) in stat) Console.WriteLine($"{k,-70} {e.n,6}  z {e.zmin:0}..{e.zmax:0}");
                return 0;
            }
            case "noeuds":
            {
                // noeuds <Data> <Mission> : nœuds des .sg de la mission, avec ou sans maillage, et ceux que le décor écarte
                var prefix = "Gfx\\Mission\\" + args[2] + "\\";
                foreach (var sg in files.List(prefix).Where(p => p.EndsWith(".sg", StringComparison.OrdinalIgnoreCase)))
                {
                    var nodes = Rooms.SceneNodes(files.Read(sg));
                    Console.WriteLine($"== {sg} : {nodes.Count} nœuds");
                    foreach (var (k, (par, _)) in nodes)
                    {
                        bool mesh = files.TryRead(prefix + k + ".mesh") != null;
                        var up = k.ToUpperInvariant();
                        string why = up.Contains("_ROOM") ? "pièce" : up.Contains("_COLL") ? "collision" : up.Contains("MASK") ? "masque" : !mesh ? "sans maillage" : "affiché";
                        if (args.Length < 4 || why != "affiché") Console.WriteLine($"  {k,-40} parent {par,-20} {why}");
                    }
                }
                return 0;
            }
            case "solz":
            {
                // solz <Data> <Mission> x0 y0 x1 y1 : hauteur du sol de la grille (0 = mur), une ligne par rangée (nord en haut)
                var ses = new Session(files, args[2], "EASY", false); var g = ses.Grid!;
                int x0 = int.Parse(args[3]) / 100, y0 = int.Parse(args[4]) / 100, x1 = int.Parse(args[5]) / 100, y1 = int.Parse(args[6]) / 100;
                for (int y = y1; y >= y0; y -= 2)
                    Console.WriteLine($"{y * 100,6} " + string.Concat(Enumerable.Range(x0, x1 - x0 + 1).Where(x => (x - x0) % 2 == 0).Select(x => g.ZoneA[g.Index(x, y)] == 0 ? "  # " : $"{g.GroundHeight[g.Index(x, y)],4:0}")));
                return 0;
            }
            case "materiaux":
            {
                // materiaux <Data> <Mission> <maillage> : matériaux de chaque groupe (toutes leurs textures), uv et couleurs
                var prefix = "Gfx\\Mission\\" + args[2] + "\\";
                var m = Mesh.Load(files.Read(prefix + args[3] + ".mesh"), files.Read(prefix + args[3] + ".meshdata"), args[3]);
                Console.WriteLine($"{m.VertexCount} sommets ; uv {string.Join(",", Enumerable.Range(0, 8).Select(i => m.Uv[i] != null ? i.ToString() : "-"))} ; couleurs {(m.Colors != null ? "oui" : "non")} ; shader « {m.Shader} »");
                if (m.Colors != null) Console.WriteLine($"  alpha des sommets : min {m.Colors.Where((b, i) => i % 4 == 3).Min()} max {m.Colors.Where((b, i) => i % 4 == 3).Max()} ; rvb exemple {m.Colors[0]},{m.Colors[1]},{m.Colors[2]} ; moyenne bvr {m.Colors.Where((b, i) => i % 4 == 0).Average(x => x):0},{m.Colors.Where((b, i) => i % 4 == 1).Average(x => x):0},{m.Colors.Where((b, i) => i % 4 == 2).Average(x => x):0} ; max {m.Colors.Where((b, i) => i % 4 != 3).Max()}");
                foreach (var g in m.Groups)
                {
                    var mb = files.TryRead(prefix + "Shaders\\" + g.material);
                    var mat = mb != null ? Material.Load(mb, g.material) : null;
                    Console.WriteLine($"  {g.material,-28} {g.count / 3,6} tri : {(mat == null ? "(introuvable)" : string.Join(" | ", mat.Textures))}");
                }
                return 0;
            }
            case "foulee":
            {
                // foulee <Data> <classe d'unité> [étiquette] : vitesse naturelle de l'animation (recul du pied posé au sol)
                var cf = ClassFile.Load(files, "Design\\" + args[2] + ".class");
                var model = cf.Get("DISPLAY", "Model")!.Trim('"', ' ');
                var dirs = new[] { "Unit\\Fireman", "Unit\\Civilian" };
                FD3.Game.CharacterRig? rig = null;
                foreach (var d in dirs) { try { rig = FD3.Game.CharacterRig.Build(new FD3.Game.SceneAssets(files), files, FD3.Game.SceneAssets.GfxPath(d, model)); if (rig != null) break; } catch { } }
                var tags = FD3.Game.CharacterRig.AnimTags(cf);
                Console.WriteLine($"modèle {model}, RunSpeed {cf.GetFloat("MOVING", "RunSpeed", -1)}, WalkSpeed {cf.GetFloat("MOVING", "WalkSpeed", -1)} ; [MOVING] " + string.Join(", ", cf.Section("MOVING").Select(kv => kv.Key + "=" + kv.Value)));
                foreach (var tag in args.Length > 3 ? args.Skip(3) : new[] { "RUN", "WALK" })
                {
                    if (!tags.TryGetValue(tag, out var file)) { Console.WriteLine($"{tag} : absent"); continue; }
                    var clip = AnimClip.Parse(files.Read("Gfx\\Anim\\" + file + ".anim"));
                    var feet = Enumerable.Range(0, rig!.Names.Length).Where(i => System.Text.RegularExpressions.Regex.IsMatch(rig.Names[i], "foot|toe|pied", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
                    Console.WriteLine($"{tag} = {file} : {clip.Duration:0.000} s ; pieds {string.Join(",", feet.Select(i => rig.Names[i]))}");
                    foreach (var fi in feet)
                    {
                        int n = 120; var pts = new List<System.Numerics.Vector3>();
                        for (int k = 0; k < n; k++) { var w = rig.Pose(clip, clip.Duration * k / n); pts.Add(w[fi].Translation); }
                        float zmin = pts.Min(p => p.Z);
                        // pied au sol : z proche du minimum ; recul pendant l'appui
                        var ground = pts.Select((p, k) => (p, k)).Where(x => x.p.Z < zmin + 4).ToList();
                        float x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
                        Console.WriteLine($"   {rig.Names[fi]} : étendue x {x1 - x0:0.0} y {y1 - y0:0.0} z {pts.Max(p => p.Z) - zmin:0.0} ; au sol {ground.Count}/{n} images, " +
                            $"recul y {(ground.Count > 1 ? ground.Max(g => g.p.Y) - ground.Min(g => g.p.Y) : 0):0.0} x {(ground.Count > 1 ? ground.Max(g => g.p.X) - ground.Min(g => g.p.X) : 0):0.0}");
                    }
                }
                return 0;
            }
            case "explosions":
            {
                // explosions <Data> <Mission> <secondes> : objets explosifs et leurs explosions au fil du temps
                var ses = new Session(files, args[2], "EASY");
                foreach (var it in ses.Explosions.Items)
                    Console.WriteLine($"{it.Name,-28} vie {it.Life,5:0.#}/{it.MaxLife:0} feu x{it.FireDamageCoef} souffle PV {it.LpPower}/{it.LpRadius} foyers {it.HpPower}/{it.HpRadius} endurance {it.StaminaPower}/{it.StaminaRadius} son {it.Sound} foyers proches {it.NearFires.Count} ({it.Position.X:0},{it.Position.Y:0})");
                int steps = (int)(float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) / Session.Dt);
                for (int i = 0; i < steps; i++)
                {
                    ses.Step();
                    if (i % 300 == 299) Console.WriteLine($"FEUX {ses.Time,5:0} s : {ses.Fire.HotPoints.Count(h => h.IsBurning)} foyers en feu, {ses.Explosions.Items.Count(x => x.Exploded)} objets explosés");
                }
                foreach (var l in ses.Explosions.Log) Console.WriteLine(l);
                return 0;
            }
            case "hpbloc":
            {
                // hpbloc <Data> : bloc de 96 octets des paramètres d'instance des foyers, pour toutes les cartes (octets 80 à 95)
                foreach (var lv in files.List("Design\\Mission\\").Where(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase)))
                {
                    Level L; try { L = Level.Parse(files.Read(lv)); } catch { continue; }
                    var hist = new Dictionary<string, int>();
                    foreach (var o in L.Objects.Where(o => o.Kind == EntityKind.HotPoint))
                    {
                        var c = o.GetComponent(0xFE00); if (c == null) continue;
                        var bb = o.ComponentBytes(c).ToArray();
                        int last = -1, prev = -1;
                        for (int i = bb.Length - 4; i >= 0; i--) if (Chunks.IsEnd(bb, i)) { if (last < 0) last = i; else { prev = i; break; } }
                        if (last < 0 || prev < 0 || last - (prev + 4) != 96) continue;
                        int t = prev + 4;
                        var key = Convert.ToHexString(bb, t, 34) + " | " + Convert.ToHexString(bb, t + 82, 14);
                        hist[key] = hist.GetValueOrDefault(key) + 1;
                        if ((Environment.GetEnvironmentVariable("FD3_MOTIF") is { } motif && key.Contains(motif)) || (Environment.GetEnvironmentVariable("FD3_BD") == lv.Split('\\')[2] && key.Substring(34, 2) == "01")) Console.WriteLine($"   {o.Name} ({o.Position.X:0},{o.Position.Y:0},{o.Position.Z:0}) {o.ClassName} : {key}");
                    }
                    Console.WriteLine($"== {lv} : {hist.Values.Sum()} foyers ; backdraft : {hist.Where(kv => kv.Key.Length > 36 && kv.Key.Substring(34, 2) == "01").Sum(kv => kv.Value)}");
                    foreach (var (k, v) in hist.OrderByDescending(x => x.Value).Take(8)) Console.WriteLine($"  {v,5} {k}");
                }
                return 0;
            }
            case "backdraft":
            {
                // backdraft <Data> <Mission> <secondes> [fenetre] : pièces à risque, état des foyers ; à la fin, ouvre une porte (ou casse une fenêtre puis ouvre)
                var ses = new Session(files, args[2], "EASY");
                int steps = (int)(float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) / Session.Dt);
                for (int i = 0; i < steps; i++)
                {
                    ses.Step();
                    if (i % 200 == 199)
                        foreach (var r in ses.Units.BackdraftRooms)
                            Console.WriteLine($"  {ses.Time,5:0} s {r.Name} fermée {r.Closed} : " + string.Join(" ", r.Sleepers.Select(h => $"{h.Name}={(h.Lit ? h.Intensity.ToString("0") : "-")}{(h.Sleeping ? "z" : "")}")));
                }
                foreach (var r in ses.Units.BackdraftRooms)
                {
                    Console.WriteLine($"{r.Name} : ouvertures " + string.Join(", ", r.Openings.Select(o => $"{o.Name}({(o.IsWindow ? "fenêtre" : "porte")},{o.State})")));
                    if (args.Length > 4) { var w = r.Openings.FirstOrDefault(o => o.IsWindow); if (w != null) { ses.Units.SetOpeningState(w, OpeningState.Broken); ses.Step(); } }
                    var d = r.Openings.FirstOrDefault(o => !o.IsWindow);
                    if (d != null) { ses.Units.SetOpeningState(d, OpeningState.Open); ses.Step(); }
                }
                foreach (var l in ses.Units.Log.Where(l => l.Contains("backdraft", StringComparison.OrdinalIgnoreCase) || l.Contains("couve") || l.Contains("fumée s'échappe"))) Console.WriteLine(l);
                return 0;
            }
            case "hauteurs":
            {
                // hauteurs <Data> <Model>… : parties d'un modèle (véhicule) et hauteurs min/max des maillages placés
                var assets = new SceneAssets(files);
                foreach (var name in args.Skip(2))
                {
                    var gfx = SceneAssets.GfxPath("Unit\\FireVehicle", name);
                    float lo = float.MaxValue, hi = float.MinValue;
                    foreach (var part in assets.ModelParts(gfx))
                    {
                        var m = assets.LoadMesh("", part.Mesh, "gfx").Mesh; float a0 = float.MaxValue, a1 = float.MinValue;
                        for (int i = 0; i < m.VertexCount; i++) { var v = System.Numerics.Vector3.Transform(new(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]), part.World); a0 = Math.Min(a0, v.Z); a1 = Math.Max(a1, v.Z); }
                        lo = Math.Min(lo, a0); hi = Math.Max(hi, a1);
                        Console.WriteLine($"  {part.Mesh} z {a0:0}..{a1:0} T {part.World.Translation}");
                    }
                    Console.WriteLine($"{name} ({gfx}) : z {lo:0}..{hi:0}");
                }
                return 0;
            }
            case "utiliser":
            {
                // utiliser <Data> <Mission> : objets utilisables ou à réparer, puis un pompier agit sur le premier
                var ses = new Session(files, args[2], "EASY");
                void Run(float sec) { for (int i = 0; i < (int)(sec / Session.Dt); i++) ses.Step(); }
                foreach (var o in ses.Units.Usables) Console.WriteLine($"{o.Name} id {o.Id} utiliser {o.CanBeUsed} réparer {o.CanBeRepaired} actions {string.Join(",", o.Actions.Select(a => a.action + ":" + a.script))}");
                Run(40);
                var pair = (from o in ses.Units.Usables where o.Enabled from u in ses.Units.Units where !u.Hidden && ses.Units.CanWorkOn(u, o)
                            orderby System.Numerics.Vector3.Distance(u.Position, o.Position) select (o, u)).FirstOrDefault();
                var obj = pair.o; var man = pair.u;
                if (obj == null || man == null) { Console.WriteLine("aucun objet ou pompier"); return 0; }
                ses.Units.UseObject(man, obj); Console.WriteLine($"{man.Name} → {obj.Name} : {man.Status}");
                for (int k = 0; k < 10 && man.UseTarget != null; k++) { Run(3); Console.WriteLine($"  {ses.Time:0} s : {man.Status} pos ({man.Position.X:0},{man.Position.Y:0}) obj ({obj.Position.X:0},{obj.Position.Y:0}) chemin {man.Path.Count}"); }
                foreach (var l in ses.Units.Log.TakeLast(3)) Console.WriteLine(l);
                foreach (var l in ses.Scripts!.Log.TakeLast(3)) Console.WriteLine("script : " + l);
                return 0;
            }
            case "foyerspiece":
            {
                // foyerspiece <Data> <Mission> [filtre] : foyers (position, pièce) et carte des cases autour du premier
                var ses = new Session(files, args[2], "EASY");
                var sel = ses.Fire.HotPoints.Where(h => args.Length < 4 || (ses.RoomOf.TryGetValue(h.Id, out var r) && r.Contains(args[3], StringComparison.OrdinalIgnoreCase)) || h.Name.Contains(args[3], StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var h in sel.Take(30)) Console.WriteLine($"{h.Name} ({h.Position.X:0},{h.Position.Y:0},{h.Position.Z:0}) pièce {(ses.RoomOf.TryGetValue(h.Id, out var r2) ? r2 : "-")}");
                if (sel.Count > 0) { var c = sel[0].Position; Console.WriteLine(ses.Units.AsciiMap((int)c.X - 900, (int)c.Y - 900, (int)c.X + 900, (int)c.Y + 900)); }
                return 0;
            }
            case "toutesmissions":
            {
                // toutesmissions <Data> [minutes] : chaque mission jouée sans affichage ; erreurs et fonctions inconnues de ses scripts
                float minutes = args.Length > 2 ? float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 10;
                var total = new Dictionary<string, (int n, SortedSet<string> where)>(StringComparer.OrdinalIgnoreCase);
                foreach (var dir in Directory.GetDirectories(Path.Combine(args[1], "Script", "Mission")).OrderBy(d => d))
                {
                    var mission = Path.GetFileName(dir);
                    try
                    {
                        var ses = new Session(files, mission, "EASY");
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        int steps = (int)(minutes * 60 / Session.Dt);
                        string? crash = null;
                        for (int i = 0; i < steps; i++)
                        {
                            try { ses.Step(); } catch (Exception e) { crash = e.GetType().Name + " : " + e.Message; break; }
                            if (ses.Host?.Cinematic == true && i % 50 == 0) ses.Host.EndCinematic();
                            if (ses.Host?.Outcome != null) break;
                        }
                        var sc = ses.Scripts!;
                        var h = ses.Host!;
                        Console.WriteLine($"== {mission} : {ses.Time / 60:0.0} min simulées en {sw.Elapsed.TotalSeconds:0} s · unités {ses.Units.Units.Count} · objectifs {h.Objectives.Count} ({string.Join(" ", h.Objectives.Select(o => o.Status))}) · issue {(h.Outcome == null ? "en cours" : h.Outcome == true ? "victoire" : "défaite")}");
                        if (crash != null) Console.WriteLine("   PLANTAGE : " + crash);
                        if (h.Outcome == false) { Console.WriteLine($"   défaite : {h.DebriefText}"); foreach (var l in h.FireLog.TakeLast(6)) Console.WriteLine("   journal : " + l); }
                        foreach (var l in sc.Log.Distinct().Take(8)) Console.WriteLine("   script : " + l);
                        if (sc.UnknownCalls.Count > 0) Console.WriteLine("   inconnues : " + string.Join(", ", sc.UnknownCalls.OrderByDescending(k => k.Value).Select(k => $"{k.Key}×{k.Value}")));
                        foreach (var (k, v) in sc.UnknownCalls)
                        {
                            if (!total.TryGetValue(k, out var t)) t = (0, new SortedSet<string>());
                            t.where.Add(mission); total[k] = (t.n + v, t.where);
                        }
                    }
                    catch (Exception e) { Console.WriteLine($"== {mission} : impossible de charger ({e.GetType().Name} : {e.Message})"); }
                }
                Console.WriteLine("== Total des fonctions inconnues appelées :");
                foreach (var (k, t) in total.OrderByDescending(x => x.Value.n)) Console.WriteLine($"   {k,-32} {t.n,6}  {string.Join(", ", t.where)}");
                return 0;
            }
            case "depart":
            {
                // depart <Data> <Mission> : équipes de départ et renforts prévus
                var ses = new Session(files, args[2], "EASY");
                Console.WriteLine($"unités {ses.Units.Units.Count} · pompiers comptés {ses.Units.FiremenCount} · attendus {ses.Units.DeferredFiremen}");
                foreach (var l in ses.Units.Log.Take(20)) Console.WriteLine("  " + l);
                for (int i = 0; i < 600; i++) ses.Step();
                Console.WriteLine($"après 60 s : unités {ses.Units.Units.Count} · pompiers {ses.Units.FiremenCount}");
                foreach (var l in ses.Units.Log.Skip(0).Take(30)) Console.WriteLine("  " + l);
                return 0;
            }
            case "animtags":
            {
                // animtags <Data> <classe relative à Design> : étiquettes d'animation [ANIM_PLAYER]
                foreach (var (k, v) in CharacterRig.AnimTags(ClassFile.Load(files, args[2]))) Console.WriteLine($"{k,-20} {v}");
                return 0;
            }
            case "duree":
            {
                // duree <Data> <anim>… : durée des animations (Gfx\Anim\<anim>.anim)
                foreach (var n in args.Skip(2)) { var d = files.TryRead("Gfx\\Anim\\" + n + ".anim"); Console.WriteLine($"{n} : {(d != null ? AnimClip.Parse(d).Duration.ToString("0.00") + " s" : "introuvable")}"); }
                return 0;
            }
            case "porter":
            {
                // porter <Data> <Mission> : un pompier va chercher un civil inconscient, le porte et le dépose à l'ambulance
                var ses = new Session(files, args[2], "EASY");
                void Run(float sec) { for (int i = 0; i < (int)(sec / Session.Dt); i++) ses.Step(); }
                Run(40);
                var man = ses.Units.Units.FirstOrDefault(u => !u.IsVehicle && !u.CanHeal && !u.Hidden && !u.OnBoard);
                var me = man == null ? default : new System.Numerics.Vector2(man.Position.X, man.Position.Y);
                var civ = ses.Units.Civilians.Where(c => c.State == CivilianState.Unconscious && !c.Hidden && ses.Units.CanReach(me, new(c.Position.X, c.Position.Y)))
                    .OrderBy(c => System.Numerics.Vector2.Distance(me, new(c.Position.X, c.Position.Y))).FirstOrDefault();
                if (man == null || civ == null) { Console.WriteLine("pas de pompier ou de civil inconscient atteignable"); return 1; }
                ses.Units.FetchCivilian(man, civ);
                string last = "";
                for (int t = 0; t < 240 && civ.State != CivilianState.Rescued; t++)
                {
                    Run(1);
                    var line = $"{man.Status} · {man.Carry}";
                    if (line != last) { Console.WriteLine($"{ses.Time,6:0} s ({man.Position.X:0},{man.Position.Y:0}) {line}"); last = line; }
                }
                Console.WriteLine($"civil : {civ.State}");
                return civ.State == CivilianState.Rescued ? 0 : 2;
            }
            case "chercherclasse":
            {
                // chercherclasse <Data> <motif> : classes de Design dont le texte contient le motif (expression régulière)
                var rx = new System.Text.RegularExpressions.Regex(args[2], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var f in files.List("Design\\").Where(f => f.EndsWith(".class", StringComparison.OrdinalIgnoreCase)))
                {
                    var t = System.Text.Encoding.Latin1.GetString(files.Read(f));
                    foreach (System.Text.RegularExpressions.Match m in rx.Matches(t)) Console.WriteLine($"{f} : {m.Value.Trim()}");
                }
                return 0;
            }
            case "zones":
            {
                // zones <Data> <Mission> : valeurs des zones A et B de la grille (combien de cases, et une case exemple)
                var ses = new Session(files, args[2], "EASY", false);
                var g = ses.Grid!;
                foreach (var (name, arr) in new[] { ("A", g.ZoneA), ("B", g.ZoneB) })
                    foreach (var grp in Enumerable.Range(0, arr.Length).GroupBy(i => arr[i]).OrderBy(x => x.Key))
                    {
                        int i = grp.First();
                        Console.WriteLine($"zone {name} {grp.Key,6} (0x{grp.Key:X4}) : {grp.Count(),7} case(s), ex. ({(i % g.Width) * 100 + 50},{(i / g.Width) * 100 + 50})");
                    }
                return 0;
            }
            case "ranimer":
            {
                // ranimer <Data> <Mission> : un pompier inconscient est-il ranimé par un collègue (pompier ou médecin) ?
                var ses = new Session(files, args[2], "EASY");
                void Run(float sec) { for (int i = 0; i < (int)(sec / Session.Dt); i++) ses.Step(); }
                Run(40);
                var men = ses.Units.Units.Where(u => !u.IsVehicle && !u.Hidden && !u.OnBoard).ToList();
                foreach (var who in men.Skip(1).Take(3))
                {
                    var victim = men[0];
                    victim.Unconscious = true; victim.Stamina = 0;
                    ses.Units.ReviveColleague(who, victim);
                    Console.WriteLine($"{who.ClassPath} → {victim.ClassPath} ({System.Numerics.Vector3.Distance(who.Position, victim.Position):0}) : {who.Status}");
                    for (int t = 0; t < 60 && victim.Unconscious; t++) Run(1);
                    Console.WriteLine($"   ranimé : {!victim.Unconscious} · {who.Status}");
                    victim.Unconscious = false;
                }
                return 0;
            }
            case "auto":
            {
                // auto <Data> <Mission> : un pompier et un camion posés près d'un feu visible l'éteignent-ils d'eux-mêmes ?
                var ses = new Session(files, args[2], "EASY");
                void Run(float sec) { for (int i = 0; i < (int)(sec / Session.Dt); i++) ses.Step(); }
                Run(40);
                foreach (var u in ses.Units.Units.Where(u => u.Nozzle != null && !u.Hidden && !u.OnBoard).GroupBy(u => u.IsVehicle).Select(g => g.First()).ToList())
                {
                    // case libre au sol à 6–9 m d'un feu, d'où on le voit
                    var spot = (from h in ses.Fire.HotPoints where h.IsBurning && MathF.Abs(h.Position.Z - u.Position.Z) < 200
                                from a in Enumerable.Range(0, 16) let r = u.IsVehicle ? 1500f : (float.TryParse(Environment.GetEnvironmentVariable("FD3_AUTO_R"), out var rr) ? rr : 700f)
                                let p = new System.Numerics.Vector2(h.Position.X + r * MathF.Cos(a * 0.39f), h.Position.Y + r * MathF.Sin(a * 0.39f))
                                select (h, p, a)).FirstOrDefault(x => ses.Units.AutoDiagSpot(u, x.p, x.h));
                    if (spot.h == null) { Console.WriteLine($"{u.Name} : aucun emplacement d'essai"); continue; }
                    ses.Units.Teleport(u, spot.p); u.Target = null; u.Path.Clear(); u.GoToVehicle = null;
                    if (u.IsVehicle) { u.Heading = MathF.Atan2(spot.h.Position.Y - spot.p.Y, spot.h.Position.X - spot.p.X) + 2.2f; ses.Units.Attack(u, spot.h); for (int k = 0; k < 8; k++) { Run(1); Console.WriteLine($"   {ses.Time:0} s ({u.Position.X:0},{u.Position.Y:0}) cap {u.Heading:0.00} {u.Status}"); } }
                    float i0 = spot.h.Intensity;
                    Run(6);
                    Console.WriteLine($"{u.Name} à {System.Numerics.Vector2.Distance(spot.p, new(spot.h.Position.X, spot.h.Position.Y)):0} de {spot.h.Name} : {u.Status} ; intensité {i0:0} → {spot.h.Intensity:0}");
                }
                return 0;
            }
            case "conduite":
            {
                // conduite <Data> <Mission> : le camion part vers un point derrière lui (marche arrière, puis virage)
                var ses = new Session(files, args[2], "EASY");
                void Run(float sec) { for (int i = 0; i < (int)(sec / Session.Dt); i++) ses.Step(); }
                Run(30);
                var truck = ses.Units.Units.First(u => u.IsVehicle && u.Nozzle != null);
                var back = new System.Numerics.Vector2(truck.Position.X, truck.Position.Y) - new System.Numerics.Vector2(MathF.Cos(truck.Heading), MathF.Sin(truck.Heading)) * 1500;
                Console.WriteLine($"départ ({truck.Position.X:0},{truck.Position.Y:0}) cap {truck.Heading:0.00} → ({back.X:0},{back.Y:0}) : {ses.Units.Drive(truck, back)} {truck.Status} chemin {truck.Path.Count}");
                for (int k = 0; k < 14; k++) { Run(1); Console.WriteLine($"  {ses.Time:0} s ({truck.Position.X:0},{truck.Position.Y:0}) cap {truck.Heading:0.00} recul {truck.Reversing:0} {truck.Status}"); }
                return 0;
            }
            case "ouvertures":
            {
                // ouvertures <Data> <Mission> [zmin] : portes et fenêtres (au sol ou non, cases bloquées, cassable)
                var ses = new Session(files, args[2], "EASY", false);
                float zmin = args.Length > 3 ? float.Parse(args[3]) : -1e9f;
                foreach (var d in ses.Units.Openings.Where(d => d.Position.Z >= zmin))
                    Console.WriteLine($"{d.Name,-28} {(d.IsWindow ? "fenêtre" : "porte"),-8} z {d.Position.Z:0} au sol {d.OnGround} cases {d.Cells.Count} cassable {d.Breakable} vie {d.Life:0} état {d.State} centre ({d.Center.X:0},{d.Center.Y:0})");
                return 0;
            }
            case "grille":
            {
                // grille <Data> <Mission> x0 y0 x1 y1 : hauteurs et zones des cases (une lettre par case)
                var ses = new Session(files, args[2], "EASY", false);
                var g = ses.Grid!; int x0 = int.Parse(args[3]) / 100, y0 = int.Parse(args[4]) / 100, x1 = int.Parse(args[5]) / 100, y1 = int.Parse(args[6]) / 100;
                var hs = new Dictionary<float, int>();
                for (int y = y1; y >= y0; y--)
                {
                    var sb = new System.Text.StringBuilder();
                    for (int x = x0; x <= x1; x++)
                    {
                        if (!g.Inside(x, y)) { sb.Append(' '); continue; }
                        int i = g.Index(x, y);
                        float h = MathF.Round(g.GroundHeight[i]); hs[h] = hs.GetValueOrDefault(h) + 1;
                        sb.Append(g.ZoneA[i] == 0 ? '#' : ses.Units.IsBlocked(i) ? 'x' : ses.Units.IsClosedDoor(i) ? 'D' : h >= 350 ? '4' : h >= 150 ? '2' : h > 20 ? '1' : '.');
                    }
                    Console.WriteLine(sb.ToString());
                }
                Console.WriteLine(string.Join(" ", hs.OrderByDescending(k => k.Value).Take(10).Select(k => $"{k.Key}:{k.Value}")));
                return 0;
            }
            case "casser":
            {
                // casser <Data> <Mission> <nom> : un pompier à hache va casser cet obstacle (ou porte) ; état et réaction du script
                var ses = new Session(files, args[2], "EASY");
                void Run(float sec) { for (int i = 0; i < (int)(sec / Session.Dt); i++) ses.Step(); }
                Run(40);
                var d = ses.Units.Openings.First(o => o.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
                var man = ses.Units.Units.Where(u => u.CanBreak && !u.Hidden && !u.OnBoard).OrderBy(u => System.Numerics.Vector3.Distance(u.Position, d.Position)).First();
                if (args.Length > 5) ses.Units.Teleport(man, new System.Numerics.Vector2(float.Parse(args[4]), float.Parse(args[5])));
                ses.Units.WorkOnOpening(man, d);
                Console.WriteLine($"{man.Name} → {d.Name} : {man.Status}");
                for (int k = 0; k < 30 && d.State != OpeningState.Broken; k++) { Run(3); Console.WriteLine($"  {ses.Time:0} s : {man.Status} ({man.Position.X:0},{man.Position.Y:0},{man.Position.Z:0}) vie {d.Life:0} obstacle z {d.Center.Z:0}"); }
                Run(2);
                Console.WriteLine($"état {d.State} ; visibilité script : {string.Join(", ", ses.Host!.Visibility.Select(kv => $"{kv.Key}={kv.Value}"))}");
                return 0;
            }
            case "declencheur":
            {
                // declencheur <Data> <Mission> <nom de zone> : un pompier entre, ressort et rentre dans la zone ; combien de déclenchements ?
                var ses = new Session(files, args[2], "EASY");
                void Run(float sec) { for (int i = 0; i < (int)(sec / Session.Dt); i++) ses.Step(); }
                Run(40);
                var t = ses.Units.Triggers.First(z => z.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
                var man = ses.Units.Units.First(u => !u.IsVehicle && !u.Hidden && !u.OnBoard);
                int fired = 0;
                for (int k = 0; k < 4; k++)
                {
                    ses.Units.Teleport(man, new System.Numerics.Vector2(t.Position.X, t.Position.Y)); man.Path.Clear();
                    var before = ses.Units.LastTriggerTime; Run(1);
                    if (ses.Units.LastTriggerTime != before) fired++;
                    ses.Units.Teleport(man, new System.Numerics.Vector2(t.Position.X + 2500, t.Position.Y)); man.Path.Clear(); Run(8);
                    if (ses.Host!.Cinematic) ses.Host.EndCinematic();
                }
                Console.WriteLine($"zone {t.Name} : déclenchée {fired} fois sur 4 entrées ; active {t.Enabled}");
                foreach (var l in ses.Host!.FireLog.TakeLast(3)) Console.WriteLine("  " + l);
                return 0;
            }
            case "video":
            {
                // video <fichier.bik> [image] : décode toute la vidéo (FD3.Video) ; images, son, durée ; enregistre une image (.ppm)
                using var v = FD3.Video.VideoDecoder.Open(args[1]);
                if (v == null) { Console.WriteLine("échec : " + FD3.Video.VideoDecoder.LastError); return 1; }
                Console.WriteLine($"{v.Width}×{v.Height} {v.Fps:0.##} images/s, {v.Duration:0.0} s, son {v.AudioRate} Hz × {v.AudioChannels}");
                var rgba = new byte[v.Width * v.Height * 4]; var audio = new short[16384];
                int want = args.Length > 2 ? int.Parse(args[2]) : 100, frames = 0; long samples = 0; double last = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (;;)
                {
                    var it = v.Next(rgba, audio, out var pts, out var n);
                    if (it == FD3.Video.VideoDecoder.Item.End) break;
                    if (it == FD3.Video.VideoDecoder.Item.Sound) { samples += n; continue; }
                    frames++; last = pts;
                    if (frames == want)
                    {
                        using var f = File.Create(Path.Combine(Path.GetTempPath(), "fd3_video.ppm"));
                        var hdr = System.Text.Encoding.ASCII.GetBytes($"P6\n{v.Width} {v.Height}\n255\n"); f.Write(hdr);
                        for (int i = 0; i < v.Width * v.Height; i++) { f.WriteByte(rgba[i * 4]); f.WriteByte(rgba[i * 4 + 1]); f.WriteByte(rgba[i * 4 + 2]); }
                    }
                }
                Console.WriteLine($"{frames} images (dernière à {last:0.00} s), {samples / (double)Math.Max(1, v.AudioRate):0.0} s de son, décodé en {sw.Elapsed.TotalSeconds:0.0} s ; rembobinage {v.Rewind()}");
                return 0;
            }
            case "vie":
            {
                // vie <Data> <Mission> : un pompier posé dans les flammes s'évanouit-il ? puis, ranimé, il se soigne à l'ambulance ;
                // un camion posé dans le feu finit-il hors service ?
                var ses = new Session(files, args[2], "EASY");
                void Run(float sec) { for (int i = 0; i < (int)(sec / Session.Dt); i++) ses.Step(); }
                Run(40);
                var fire = ses.Fire.HotPoints.Where(h => h.IsBurning).OrderByDescending(h => h.Intensity * h.Class.HeatRangeMax).First();
                var man = ses.Units.Units.First(u => !u.IsVehicle && !u.Hidden && !u.OnBoard);
                ses.Units.Teleport(man, new System.Numerics.Vector2(fire.Position.X + 60, fire.Position.Y));
                Console.WriteLine($"feu {fire.Name} intensité {fire.Intensity:0}/{fire.Class.IntensityMax} chaleur {fire.Class.UnitHeatDamage} rayon {fire.Class.HeatRangeMax}");
                for (int k = 0; k < 10 && !man.Unconscious; k++) { Run(2); Console.WriteLine($"  {ses.Time:0} s : endurance {man.Stamina:0}/{man.StaminaMax} {man.Status}"); }
                man.Unconscious = false; man.Stamina = 20;
                var amb = ses.Units.Units.FirstOrDefault(u => u.HealsUnits);
                if (amb != null)
                {
                    ses.Units.GoToVehicle(man, amb);
                    for (int k = 0; k < 20; k++) { Run(3); Console.WriteLine($"  {ses.Time:0} s : endurance {man.Stamina:0} {man.Status} à {System.Numerics.Vector3.Distance(man.Position, amb.Position):0} de l'ambulance"); }
                }
                var truck = ses.Units.Units.First(u => u.IsVehicle && u.Nozzle != null);
                fire = ses.Fire.HotPoints.Where(h => h.IsBurning && MathF.Abs(h.Position.Z) < 60).OrderByDescending(h => h.Intensity).First();
                ses.Units.Teleport(truck, new System.Numerics.Vector2(fire.Position.X + 100, fire.Position.Y));
                for (int k = 0; k < 8 && !truck.Destroyed; k++) { Run(10); Console.WriteLine($"  {ses.Time:0} s : camion vie {truck.Life:0}/{truck.LifeMax} {truck.Status} ; feu {fire.Intensity:0}"); }
                return 0;
            }
            case "suivre":
            {
                // suivre <Data> <Mission> : un pompier va chercher un civil conscient puis l'emmène à l'ambulance ;
                // le civil passe-t-il par des cases libres (pas à travers les murs) ?
                var ses = new Session(files, args[2], "EASY");
                void Run(float sec) { for (int i = 0; i < (int)(sec / Session.Dt); i++) ses.Step(); }
                Run(40);
                Civilian? civ = null; Unit? man = null;
                foreach (var c0 in ses.Units.Civilians.Where(c => c.State == CivilianState.Conscious && !c.Hidden).ToList())
                {
                    var m0 = ses.Units.Units.Where(u => !u.IsVehicle && !u.Hidden && !u.OnBoard).OrderBy(u => System.Numerics.Vector3.Distance(u.Position, c0.Position)).First();
                    ses.Units.FetchCivilian(m0, c0);
                    for (int k = 0; k < 40 && c0.Leader == null; k++) Run(1);
                    if (c0.Leader != null) { civ = c0; man = m0; break; }
                    Console.WriteLine($"  {c0.Name} : pas atteint ({m0.Status})");
                }
                if (civ == null || man == null) { Console.WriteLine("aucun civil atteint"); return 0; }
                Console.WriteLine($"{man.Name} → {civ.Name} : suit {civ.Leader != null} à {ses.Time:0} s");
                var amb = ses.Units.Units.First(u => u.IsAmbulance);
                ses.Units.GoToVehicle(man, amb);
                int bad = 0, steps = 0; var g = ses.Grid!;
                for (int i = 0; i < 600 && civ.State == CivilianState.Conscious; i++)
                {
                    ses.Step(); steps++;
                    var (cx, cy) = g.CellOf(civ.Position);
                    if (!g.Inside(cx, cy) || g.ZoneA[g.Index(cx, cy)] == 0 || ses.Units.IsBlocked(g.Index(cx, cy))) bad++;
                    if (i % 50 == 0) Console.WriteLine($"  {ses.Time:0} s : civil ({civ.Position.X:0},{civ.Position.Y:0}) à {System.Numerics.Vector3.Distance(civ.Position, man.Position):0} du pompier ; {man.Status}");
                }
                Console.WriteLine($"état final {civ.State} ; pas dans un mur : {bad}/{steps}");
                return 0;
            }
            case "cassables":
            {
                // cassables <Data> <Mission> : objets du décor qu'on peut casser (CouldBeBreak), hors portes et fenêtres
                var ses = new Session(files, args[2], "EASY", false);
                var cache = new Dictionary<string, ClassFile>(StringComparer.OrdinalIgnoreCase);
                foreach (var o in ses.Level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform))
                {
                    ClassFile? c; try { c = ClassFile.Load(files, "Design\\" + o.ClassPath["design\\".Length..] + ".class", cache); } catch { continue; }
                    if (!c.GetBool("CAPACITIES_ACTIONS", "CouldBeBreak")) continue;
                    var type = c.Get("OCCUPATION", "Occupation_Type")?.Trim('"', ' ');
                    Console.WriteLine($"{o.Name,-30} {o.ClassPath,-60} type {type} vie {c.GetFloat("DAMAGE", "LifePoints", -1)} usedmg {c.Get("DAMAGE", "UseDamage")} pos ({o.Position.X:0},{o.Position.Y:0},{o.Position.Z:0}) modèle {c.Get("DISPLAY", "Model")}");
                }
                return 0;
            }
            case "camion":
            {
                // camion <Data> <Mission> : lance du camion, montée / descente, recharge d'eau (essai automatique)
                var ses = new Session(files, args[2], "EASY");
                void Run(float sec) { for (int i = 0; i < (int)(sec / Session.Dt); i++) ses.Step(); }
                Run(30);
                var truck = ses.Units.Units.First(u => u.IsVehicle && u.Nozzle != null);
                Console.WriteLine($"camion {truck.Name} : lance portée {truck.Nozzle!.RangeMin}–{truck.Nozzle.RangeMax}, puissance {truck.Nozzle.Power}, réserve {truck.Nozzle.Reserve}, places {truck.Seats}, recharge {truck.ResupplyPower}");
                var fire = ses.Fire.HotPoints.Where(h => h.IsBurning).OrderBy(h => System.Numerics.Vector3.Distance(h.Position, truck.Position)).First();
                Console.WriteLine($"feu {fire.Name} à {System.Numerics.Vector3.Distance(fire.Position, truck.Position):0}");
                ses.Units.Attack(truck, fire);
                for (int k = 0; k < 6; k++) { Run(3); Console.WriteLine($"  {ses.Time:0} s camion : {truck.Status} ; eau {truck.Nozzle.Reserve:0} ; {fire.Name} {fire.Intensity:0}"); }
                var men = ses.Units.Units.Where(u => !u.IsVehicle && u.Nozzle != null).Take(2).ToList();
                foreach (var m in men) { m.Nozzle!.Reserve = 0; ses.Units.GoToVehicle(m, truck); }
                for (int k = 0; k < 6; k++) { Run(4); Console.WriteLine($"  {ses.Time:0} s recharge : " + string.Join(" | ", men.Select(m => $"{m.Status} {m.Nozzle!.Reserve:0}"))); }
                foreach (var m in men) ses.Units.BoardVehicle(m, truck);
                Run(8); Console.WriteLine($"  à bord : {truck.Passengers.Count}/{truck.Seats} ; " + string.Join(" | ", men.Select(m => m.Status)));
                ses.Units.Drive(truck, new System.Numerics.Vector2(truck.Position.X - 1500, truck.Position.Y)); Run(10);
                Console.WriteLine($"  après trajet : camion {truck.Status} ; passagers {string.Join(",", men.Select(m => $"({m.Position.X:0},{m.Position.Y:0})"))}");
                Console.WriteLine($"  descente : {ses.Units.UnloadPassengers(truck)} ; " + string.Join(" | ", men.Select(m => $"{m.Status} ({m.Position.X:0},{m.Position.Y:0})")));
                foreach (var l in ses.Units.Log.TakeLast(6)) Console.WriteLine(l);
                return 0;
            }
            case "issue":
            {
                // issue <Data> <Mission> <secondes> : objectifs et issue (victoire / défaite) au fil du temps
                var ses = new Session(files, args[2], "EASY");
                int steps = (int)(float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) / Session.Dt);
                for (int i = 0; i < steps && ses.Host!.Outcome == null; i++) ses.Step();
                var h = ses.Host!;
                Console.WriteLine($"{ses.Time:0} s : issue {(h.Outcome == null ? "en cours" : h.Outcome == true ? "VICTOIRE" : "DÉFAITE")} ; texte {h.DebriefText}");
                foreach (var o in h.Objectives) Console.WriteLine($"  objectif {o.Id} {o.Text} statut {o.Status} valeur {o.Value} {string.Join(" ", o.Positions)}");
                Console.WriteLine($"  déclencheurs {ses.Units.Triggers.Count} : " + string.Join(", ", ses.Units.Triggers.Take(8).Select(t => t.Script)));
                Console.WriteLine($"  points d'eau {ses.Units.Supplies.Count} : " + string.Join(", ", ses.Units.Supplies.Select(t => $"{t.Name} r{t.Radius}")));
                foreach (var l in ses.Scripts!.Log.TakeLast(5)) Console.WriteLine("  script : " + l);
                return 0;
            }
            case "objscripts":
            {
                // objscripts <Data> <Mission> : objets dont un composant nomme une fonction des scripts de la mission
                var ses = new Session(files, args[2], "EASY", false);
                var fns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in new[] { Path.Combine(files.DataDir, "Script", "Mission", args[2]), Path.Combine(files.DataDir, "Script", "_Common") }.Where(Directory.Exists))
                    foreach (var f in Directory.EnumerateFiles(d, "*.scp"))
                        foreach (System.Text.RegularExpressions.Match mm in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"^\s*void\s+(\w+)\s*\(", System.Text.RegularExpressions.RegexOptions.Multiline))
                            fns.Add(mm.Groups[1].Value);
                foreach (var o in ses.Level.Objects)
                    foreach (var c in o.Components)
                    {
                        var b = o.ComponentBytes(c).ToArray();
                        var txt = System.Text.Encoding.Latin1.GetString(b);
                        foreach (System.Text.RegularExpressions.Match mm in System.Text.RegularExpressions.Regex.Matches(txt, @"[A-Za-z_][A-Za-z0-9_]{2,}"))
                            if (fns.Contains(mm.Value)) Console.WriteLine($"{o.Name,-28} [{o.Kind}] {o.ClassName,-28} id {o.Id} comp {c.Id:X4} @{mm.Index} : {mm.Value}");
                    }
                return 0;
            }
            case "objet":
            {
                // objet <Data> <Mission> <nom>… : genre, classe, position, axes de la matrice
                var ses = new Session(files, args[2], "EASY", false);
                foreach (var name in args.Skip(3))
                    foreach (var o in ses.Level.Objects.Where(o => name.StartsWith("cls:") ? System.Text.RegularExpressions.Regex.IsMatch(o.ClassPath, name[4..], System.Text.RegularExpressions.RegexOptions.IgnoreCase) : o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        var m = o.Matrix;
                        Console.WriteLine($"{o.Name} [{o.Kind}] {o.ClassPath} id {o.Id} pos {o.Position} rot {o.Rotation} transform {o.HasTransform}");
                        Console.WriteLine($"   X {new System.Numerics.Vector3(m.M11, m.M12, m.M13)} Y {new System.Numerics.Vector3(m.M21, m.M22, m.M23)} Z {new System.Numerics.Vector3(m.M31, m.M32, m.M33)} T {new System.Numerics.Vector3(m.M41, m.M42, m.M43)}");
                        foreach (var c in o.Components) Console.WriteLine($"   comp {c.Id:X4} ({c.Size}) : {Convert.ToHexString(o.ComponentBytes(c).ToArray())[..Math.Min(240, c.Size * 2)]}");
                    }
                return 0;
            }
            case "soleil":
            {
                // soleil <Data> <Mission> : objet lumière (position, rotation, matrice, direction des axes) et sa classe
                var ses = new Session(files, args[2], "EASY", false);
                foreach (var o in ses.Level.Objects.Where(o => o.Kind == EntityKind.Light && o.HasTransform))
                {
                    var m = o.Matrix;
                    Console.WriteLine($"{o.Name} pos {o.Position} rot {o.Rotation} ; axes X {new System.Numerics.Vector3(m.M11, m.M12, m.M13)} Y {new System.Numerics.Vector3(m.M21, m.M22, m.M23)} Z {new System.Numerics.Vector3(m.M31, m.M32, m.M33)}");
                    var cf = ClassFile.Load(files, "Design\\" + o.ClassPath["design\\".Length..] + ".class");
                    foreach (var sec in cf.Sections) Console.WriteLine($"  [{sec}] " + string.Join(", ", cf.Section(sec).Select(kv => kv.Key + "=" + kv.Value)));
                    foreach (var c in o.Components) Console.WriteLine($"  comp {c.Id:X4} : {Convert.ToHexString(o.ComponentBytes(c).ToArray())[..Math.Min(200, c.Size * 2)]}");
                }
                return 0;
            }
            case "classe":
            {
                // classe <Data> <Mission> <nom d'objet>… : section [OCCUPATION] et modèle des objets de la carte
                var ses = new Session(files, args[2], "EASY", false);
                foreach (var name in args.Skip(3))
                    foreach (var o in ses.Level.Objects.Where(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(1))
                    {
                        var rel = "Design\\" + o.ClassPath["design\\".Length..] + ".class";
                        Console.WriteLine($"== {o.Name} {rel}");
                        var cf = ClassFile.Load(files, rel);
                        foreach (var sec in new[] { "OCCUPATION", "DAMAGE" })
                            Console.WriteLine($"  [{sec}] " + string.Join(", ", cf.Section(sec).Select(kv => kv.Key + "=" + kv.Value)));
                        if (Environment.GetEnvironmentVariable("FD3_ALL") == null) continue;
                        var txt = System.Text.Encoding.Latin1.GetString(files.Read(rel));
                        bool on = false;
                        foreach (var line in txt.Split('\n'))
                        {
                            var t = line.Trim();
                            if (t.StartsWith("[")) on = t.StartsWith("[OCCUPATION", StringComparison.OrdinalIgnoreCase) || t.StartsWith("[DISPLAY", StringComparison.OrdinalIgnoreCase) || t.StartsWith("[INHERIT", StringComparison.OrdinalIgnoreCase) || Environment.GetEnvironmentVariable("FD3_ALL") != null;
                            if (on && t.Length > 0) Console.WriteLine("  " + t);
                        }
                    }
                return 0;
            }
            case "unites":
            {
                // unites <Data> <Mission> <secondes> : arrivée et déplacement des secours
                var ses = new Session(files, args[2], "EASY", false);
                float secs = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
                for (int i = 0; i < secs / Session.Dt; i++)
                {
                    ses.Step();
                    // essai : à 5 s, chaque pompier marche jusqu'au centre du feu le plus proche (sans ordre d'attaque)
                    if (args.Contains("marcher") && i == 50)
                        foreach (var u in ses.Units.Units.Where(u => u.Nozzle != null))
                        {
                            var f = ses.Fire.HotPoints.Where(h => h.IsBurning).OrderBy(h => System.Numerics.Vector3.Distance(h.Position, u.Position)).First();
                            ses.Units.Order(u, new(f.Position.X, f.Position.Y));
                        }
                }
                if (args.Contains("camion") && ses.Grid is { } gr)
                {
                    // essai : chaque pompier traverse jusqu'à l'autre côté du véhicule garé le plus proche ; on vérifie qu'il ne passe pas dedans
                    int bad = 0, total = 0;
                    foreach (var u in ses.Units.Units.Where(u => !u.IsVehicle))
                    {
                        var v = ses.Units.Units.Where(x => x.IsVehicle).OrderBy(x => System.Numerics.Vector3.Distance(x.Position, u.Position)).First();
                        var me = new System.Numerics.Vector2(u.Position.X, u.Position.Y); var vc = new System.Numerics.Vector2(v.Position.X, v.Position.Y);
                        var dest = vc + (vc - me) * 1.0f;
                        ses.Units.Order(u, dest);
                        int inside = 0;
                        for (int i = 0; i < 600 && u.Moving; i++)
                        {
                            ses.Step();
                            var (cx, cy) = gr.CellOf(u.Position);
                            if (gr.Inside(cx, cy) && ses.Units.IsVehicleBlocked(gr.Index(cx, cy))) inside++;
                        }
                        total++; if (inside > 2) bad++;
                        Console.WriteLine($"{u.Name,-22} contourne {v.Name,-20} : pas dans le véhicule = {inside}, arrivé ({u.Position.X:0},{u.Position.Y:0}) {u.Status}");
                    }
                    Console.WriteLine($"Traversées : {total}, passées dans un véhicule : {bad}");
                }
                Console.WriteLine($"Cases bloquées par des objets : {ses.Units.BlockedCells}");
                foreach (var l in ses.Units.Log) Console.WriteLine(l);
                foreach (var u in ses.Units.Units) Console.WriteLine($"{u.Name,-22} {u.Model,-34} ({u.Position.X:0},{u.Position.Y:0},{u.Position.Z:0}) vitesse {u.Speed:0} {u.Status} chemin {u.Path.Count}");
                return 0;
            }
            case "sauvetage":
            {
                // sauvetage <Data> : London, le médecin réanime le civil inconscient le plus proche puis le ramène à l'ambulance
                var ses = new Session(files, "London", "EASY", false);
                foreach (var c in ses.Units.Civilians) Console.WriteLine($"{c.Name,-20} {c.State,-12} ({c.Position.X:0},{c.Position.Y:0}){(c.Hidden ? " caché" : "")}");
                for (int i = 0; i < 30; i++) ses.Step();
                var medic = ses.Units.Units.First(u => u.CanHeal);
                var amb = ses.Units.Units.First(u => u.IsAmbulance);
                if (args.Contains("tous"))
                {
                    foreach (var cv in ses.Units.Civilians.Where(c => c.State == FD3.Game.CivilianState.Unconscious && !c.Hidden))
                    {
                        var s2 = new Session(files, "London", "EASY", false);
                        for (int i = 0; i < 30; i++) s2.Step();
                        var m2 = s2.Units.Units.First(u => u.CanHeal); var c2 = s2.Units.Civilians.First(c => c.Id == cv.Id);
                        s2.Units.FetchCivilian(m2, c2);
                        for (int i = 0; i < 2000 && c2.State == FD3.Game.CivilianState.Unconscious; i++) s2.Step();
                        Console.WriteLine($"{c2.Name,-20} ({c2.Position.X:0},{c2.Position.Y:0}) → {c2.State}, médecin à ({m2.Position.X:0},{m2.Position.Y:0}) : {m2.Status}");
                    }
                    return 0;
                }
                var civ = ses.Units.Civilians.Where(c => c.State == FD3.Game.CivilianState.Unconscious && !c.Hidden).OrderBy(c => System.Numerics.Vector3.Distance(c.Position, medic.Position)).First();
                ses.Units.FetchCivilian(medic, civ);
                Console.WriteLine($"médecin ({medic.Position.X:0},{medic.Position.Y:0}) chemin {medic.Path.Count} {medic.Status}");
                if (Environment.GetEnvironmentVariable("FD3_DUMP") is { } dump) System.IO.File.WriteAllLines(dump, ses.Units.BlockedBy.Select(kv => $"{kv.Key} {kv.Value}").Concat(medic.Path.Select(p => $"P {p.X} {p.Y}")).Append($"W {ses.Grid!.Width}"));
                bool sent = false;
                for (int i = 0; i < 3000; i++)
                {
                    ses.Step();
                    if (!sent && civ.Leader == medic) { ses.Units.GoToVehicle(medic, amb); sent = true; Console.WriteLine($"{ses.Time:0.0} s : le civil suit le médecin, direction l'ambulance"); }
                    if (i % 100 == 0) Console.WriteLine($"{ses.Time,6:0.0} s  médecin : {medic.Status,-40} civil : {civ.State} ({civ.Position.X:0},{civ.Position.Y:0})");
                    if (civ.State == FD3.Game.CivilianState.Rescued) break;
                }
                foreach (var l in ses.Units.Log) Console.WriteLine(l);
                return 0;
            }
            case "modele":
            {
                // modele <Data> <chemin Gfx> : pièces d'un modèle
                var sa = new SceneAssets(files);
                foreach (var n in sa.ModelParts(args[2]))
                {
                    var a = sa.LoadMesh("London", n.Mesh, "gfx");
                    Console.WriteLine($"{n.Mesh,-50} sommets {a.Mesh.VertexCount,6} {string.Join(" | ", a.Groups.Select(g => g.Material + ":" + g.Diffuse))}");
                }
                return 0;
            }
            case "typesfeu":
            {
                // typesfeu <Data> : nombre de foyers par type de feu (A, B, C, D) dans chaque mission
                foreach (var dir in (args.Length > 2 ? args[2..] : new[] { "Tutorial", "London", "Fireworks" }))
                {
                    var lv = files.List("Design\\Mission\\" + dir + "\\").FirstOrDefault(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                    if (lv == null) continue;
                    var level = Level.Parse(files.Read(lv));
                    var w = FD3.Fire.FireWorld.Load(level, files, new FD3.Fire.FireSettings());
                    Console.WriteLine($"{dir,-10} " + string.Join("  ", w.HotPoints.GroupBy(h => h.Class.FireType).OrderBy(g => g.Key)
                        .Select(g => $"{g.Key}={g.Count()}" + (g.Key != 'A' ? " (" + string.Join(", ", g.Select(h => h.Name).Take(8)) + ")" : ""))));
                }
                return 0;
            }
            case "fireclasses":
            {
                var cache = new Dictionary<string, ClassFile>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in files.List("Design\\Hotpoint\\").Where(p => p.EndsWith(".class", StringComparison.OrdinalIgnoreCase)).Take(5))
                {
                    var c = ClassFile.Load(files, path, cache);
                    var hp = c.Section("HOTPOINT_PROPS");
                    Console.WriteLine($"{Path.GetFileNameWithoutExtension(path),-28} " + string.Join("  ", hp.Select(kv => $"{kv.Key}={kv.Value}")));
                }
                return 0;
            }
            case "objets":
            {
                // objets <Data> <Mission> : position, classe, type et composants de chaque objet (JSON)
                var lvPath = files.List("Design\\Mission\\" + args[2] + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                var level = Level.Parse(files.Read(lvPath));
                var list = level.Objects.Where(o => o.HasTransform).Select(o => new {
                    nom = o.Name, id = o.Id, type = (int)o.Kind, classe = o.ClassName,
                    x = o.Position.X, y = o.Position.Y, z = o.Position.Z,
                    comps = o.Components.Select(c => c.Id.ToString("X4")).ToArray(),
                    explosion = o.GetComponent(0xD007) is { } c7 ? Convert.ToHexString(o.ComponentBytes(c7)) : null });
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(list));
                return 0;
            }
            case "foyers":
            {
                // foyers <Data> <Mission> : paramètres effectifs (instance) et état initial de chaque foyer
                var lvPath = files.List("Design\\Mission\\" + args[2] + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                var level = Level.Parse(files.Read(lvPath));
                var w = FD3.Fire.FireWorld.Load(level, files, new FD3.Fire.FireSettings());
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(w.HotPoints.Select(h => new {
                    nom = h.Name, h.Lit, h.Intensity, h.BurnTime, tempo = h.Class.Tempo, seuil = h.Class.LightingThreshold,
                    portee = h.Class.LightingRange, croissance = h.Class.GrowthRate, max = h.Class.IntensityMax, x = h.Position.X, y = h.Position.Y, z = h.Position.Z })));
                return 0;
            }
            case "mission":
            {
                // mission <Data> <Mission> <minutes> <FACILE|NORMAL|DIFFICILE> pieces=<json> [repartir]
                var mission = args[2];
                float minutes = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
                var diffName = args[4].ToUpperInvariant();
                var opts = args.Skip(5).ToList();
                var lvPath = files.List("Design\\Mission\\" + mission + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                var level = Level.Parse(files.Read(lvPath));
                var misRel = "Design\\" + level.MissionPath[(level.MissionPath.IndexOf("Design\\", StringComparison.OrdinalIgnoreCase) + 7)..];
                var mis = ClassFile.Load(files, misRel);
                var world = FD3.Fire.FireWorld.Load(level, files, FD3.Fire.FireSettings.FromMission(mis, diffName));
                world.Proximity3D = true;
                var roomsOpt = opts.FirstOrDefault(o => o.StartsWith("pieces="));
                if (roomsOpt != null)
                {
                    world.AddProximityNeighbours(null, RoomMap(files, level, mission, roomsOpt[7..]));
                }
                FD3.Fire.SmokeSystem? smoke = null;
                var smokeOpt = opts.FirstOrDefault(o => o.StartsWith("fumee="));
                if (smokeOpt != null)
                {
                    smoke = FD3.Fire.SmokeSystem.Load(level, files, world, RoomMap(files, level, mission, smokeOpt[6..]));
                }
                var host = new MissionHost(level, world)
                {
                    Difficulty = diffName switch { "EASY" => 0, "HARD" => 2, _ => 1 },
                    GroupAddSplits = opts.Contains("repartir"),
                };
                var interp = new FD3.Script.Interpreter(host);
                var scriptRoot = Path.Combine(args[1], "Script");
                var dirs = new[] { Path.Combine(scriptRoot, "_Common"), Path.Combine(scriptRoot, "Mission", mission) };
                foreach (var d in dirs.Where(Directory.Exists))
                    foreach (var f in Directory.EnumerateFiles(d, "*.scp"))
                        interp.Load(FD3.Script.Parser.ParseFile(File.ReadAllText(f, System.Text.Encoding.Latin1), Path.GetFileName(f)));
                var autoexec = mis.Get("SCRIPT", "AutoExecScript");
                var mains = new[] { mis.Get("SCRIPT", "MissionScript1"), mis.Get("SCRIPT", "MissionScript2") }.Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToArray();
                Console.WriteLine($"{interp.FunctionNames.Count} fonctions chargées ; démarrage {autoexec} ; boucles {string.Join(", ", mains)}");
                if (!string.IsNullOrEmpty(autoexec)) interp.TryRun(autoexec!);
                using var csv = new StreamWriter(Path.Combine(Path.GetTempPath(), $"fd3_{mission}_sim.csv"));
                csv.WriteLine("temps_s,id,nom,intensite,etat");
                const float dt = 0.1f;
                int steps = (int)(minutes * 60 / dt);
                for (int st = 0; st <= steps; st++)
                {
                    if (st % 100 == 0)
                        foreach (var h in world.HotPoints.Where(h => h.Lit || h.State == FD3.Fire.HotPointState.Consumed))
                            csv.WriteLine($"{world.Time:0.0},{h.Id},{h.Name},{h.Intensity:0.0},{h.State}");
                    interp.Update();
                    if (st % 5 == 0) foreach (var m in mains) interp.TryRun(m);
                    world.Step(dt);
                    smoke?.Step(world, dt);
                }
                if (smoke != null)
                {
                    Console.WriteLine($"Fumée finale : total {smoke.Total:0.0} ; points non vides : " + string.Join(", ", smoke.Points.Where(p => p.Intensity > 0.5f).Select(p => $"{p.Name}={p.Intensity:0.0}").Take(12)));
                    foreach (var l in smoke.Log) Console.WriteLine("  " + l);
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), $"fd3_{mission}_fumee.json"), System.Text.Json.JsonSerializer.Serialize(smoke.Points.ToDictionary(p => p.Name, p => p.Intensity)));
                }
                Console.WriteLine("Événements feu déclenchés par les scripts :");
                foreach (var l in host.FireLog.Take(40)) Console.WriteLine("  " + l);
                Console.WriteLine($"Erreurs de script : {interp.Log.Count}");
                foreach (var l in interp.Log.Distinct().Take(10)) Console.WriteLine("  " + l);
                Console.WriteLine("Fonctions du moteur non simulées (appels) : " + string.Join(", ", interp.UnknownCalls.OrderByDescending(k => k.Value).Take(15).Select(k => $"{k.Key}×{k.Value}")));
                Console.WriteLine($"Foyers en feu à la fin : {world.HotPoints.Count(h => h.IsBurning)}");
                return 0;
            }
            case "liens":
            {
                // liens <Data> <Mission> : liste des liens (noms, type, sens, ouverture)
                var lvPath = files.List("Design\\Mission\\" + args[2] + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                var level = Level.Parse(files.Read(lvPath));
                var byId = level.Objects.ToDictionary(o => o.Id);
                string N(uint i) => byId.TryGetValue(i, out var o) ? o.Name : i.ToString();
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(level.HotPointLinks.Select(l => new {
                    a = N(l.A), b = N(l.B), type = BitConverter.ToInt32(l.Data, 13), sens = BitConverter.ToInt32(l.Data, 17), ouverture = N(BitConverter.ToUInt32(l.Data, 0)) })));
                return 0;
            }
            case "etats":
            {
                // etats <Data> <Mission> : début du bloc d'état FE00 de chaque foyer (hexadécimal)
                var lvPath = files.List("Design\\Mission\\" + args[2] + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                var level = Level.Parse(files.Read(lvPath));
                var res = new Dictionary<string, string>();
                foreach (var o in level.Objects.Where(o => o.Kind == EntityKind.HotPoint))
                    if (o.GetComponent(0xFE00) is { } c) res[o.Name] = Convert.ToHexString(o.ComponentBytes(c)[17..44]);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(res));
                return 0;
            }
            case "scripts":
            {
                // scripts <Data> : analyse tous les fichiers .scp et signale les erreurs
                int ok = 0, ko = 0, fns = 0;
                foreach (var f in Directory.EnumerateFiles(Path.Combine(args[1], "Script"), "*.scp", SearchOption.AllDirectories).OrderBy(x => x))
                {
                    try { var list = FD3.Script.Parser.ParseFile(File.ReadAllText(f, System.Text.Encoding.Latin1), Path.GetFileName(f)); fns += list.Count; ok++; }
                    catch (FD3.Script.ParseError e) { ko++; Console.WriteLine("ERREUR " + e.Message); }
                }
                Console.WriteLine($"{ok} fichiers analysés, {ko} en erreur, {fns} fonctions");
                return ko == 0 ? 0 : 2;
            }
            case "groupes":
            {
                // groupes <Data> <Mission> <nom1,nom2,...> : membres de groupes (directs) en JSON
                var lvPath = files.List("Design\\Mission\\" + args[2] + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                var level = Level.Parse(files.Read(lvPath));
                var byId = level.Objects.ToDictionary(o => o.Id);
                var outd = new Dictionary<string, object>();
                foreach (var g in args[3].Split(','))
                {
                    if (g.StartsWith("@"))
                    {   // @nom : groupes qui contiennent cet objet
                        var t = level.Objects.FirstOrDefault(o => o.Name.Equals(g[1..], StringComparison.OrdinalIgnoreCase));
                        if (t != null) outd[g] = level.Objects.Where(o => o.Members.Contains(t.Id)).Select(o => o.Name).ToList();
                        continue;
                    }
                    var go = level.Objects.FirstOrDefault(o => o.Name.Equals(g, StringComparison.OrdinalIgnoreCase));
                    if (go == null) continue;
                    outd[g] = go.Members.Select(m => byId.TryGetValue(m, out var mo) ? new { nom = mo.Name, type = (int)mo.Kind } : new { nom = "?" + m, type = -1 }).ToList();
                }
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(outd));
                return 0;
            }
            case "simulate":
            {
                // simulate <Data> <Mission> [minutes] [difficulté]
                var mission = args.Length > 2 ? args[2] : "London";
                float minutes = args.Length > 3 ? float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 15f;
                var diff = args.Length > 4 ? args[4].ToUpperInvariant() : "NORMAL";
                var lvPath = files.List("Design\\Mission\\" + mission + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                var level = Level.Parse(files.Read(lvPath));
                var misRel = "Design\\" + level.MissionPath[(level.MissionPath.IndexOf("Design\\", StringComparison.OrdinalIgnoreCase) + 7)..];
                var mis = ClassFile.Load(files, misRel);
                var settings = FD3.Fire.FireSettings.FromMission(mis, diff);
                var world = FD3.Fire.FireWorld.Load(level, files, settings);
                if (args.Length <= 5) world.BuildNeighbourhoods();
                // options : proximite | proximite-murs ; seed=<fichier json d'identifiants allumés au départ>
                var opts = args.Skip(5).ToList();
                world.ClosedOpeningsBlock = opts.Contains("portes-fermees");
                world.Proximity3D = opts.Contains("3d");
                world.ProximityUsesTargetRange = opts.Contains("portee-cible");
                if (!opts.Contains("sans-voisinage")) world.BuildNeighbourhoods();
                var grid = files.TryRead(Path.ChangeExtension(lvPath, ".grid")) is { } gb ? Grid.Parse(gb) : null;
                if (opts.Contains("proximite")) world.AddProximityNeighbours();
                if (opts.Contains("proximite-murs")) world.AddProximityNeighbours(grid);
                var roomsOpt = opts.FirstOrDefault(o => o.StartsWith("pieces="));
                if (roomsOpt != null)
                {
                    world.AddProximityNeighbours(null, RoomMap(files, level, mission, roomsOpt[7..]));
                }
                var seedOpt = opts.FirstOrDefault(o => o.StartsWith("seed="));
                if (seedOpt != null)
                {
                    var seedIds = System.Text.Json.JsonSerializer.Deserialize<uint[]>(File.ReadAllText(seedOpt[5..]))!;
                    foreach (var h in world.HotPoints) { h.Lit = false; h.State = FD3.Fire.HotPointState.Idle; if (h.Intensity > 0) h.Intensity = 0; }
                    foreach (var id in seedIds) if (world.ById.TryGetValue(id, out var h)) world.Ignite(h, h.Class.InitialIntensity);
                }
                // evenements=<json> : [{"t":24.8,"action":"ajouter|multiplier","groupe":"explo_fire","valeur":100}]
                var evOpt = opts.FirstOrDefault(o => o.StartsWith("evenements="));
                var events = new List<(float t, string action, List<FD3.Fire.HotPoint> hps, float v)>();
                if (evOpt != null)
                {
                    var byName = level.Objects.GroupBy(o => o.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
                    var byId = level.Objects.ToDictionary(o => o.Id);
                    List<FD3.Fire.HotPoint> Resolve(LevelObject o, int depth)
                    {
                        var res = new List<FD3.Fire.HotPoint>();
                        if (world.ById.TryGetValue(o.Id, out var h)) res.Add(h);
                        if (depth < 5) foreach (var m in o.Members) if (byId.TryGetValue(m, out var mo)) res.AddRange(Resolve(mo, depth + 1));
                        return res;
                    }
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(evOpt[11..]));
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        var g = e.GetProperty("groupe").GetString()!.ToLowerInvariant();
                        if (!byName.TryGetValue(g, out var go)) { Console.WriteLine("Groupe introuvable : " + g); continue; }
                        events.Add(((float)e.GetProperty("t").GetDouble(), e.GetProperty("action").GetString()!, Resolve(go, 0), (float)e.GetProperty("valeur").GetDouble()));
                    }
                    foreach (var ev in events) Console.WriteLine($"Événement à {ev.t} s : {ev.action} {ev.v} sur {ev.hps.Count} foyers");
                }
                var smokeOpt = opts.FirstOrDefault(o => o.StartsWith("fumee="));
                FD3.Fire.SmokeSystem? smoke = null;
                if (smokeOpt != null)
                {
                    smoke = FD3.Fire.SmokeSystem.Load(level, files, world, RoomMap(files, level, mission, smokeOpt[6..]));
                    if (opts.Contains("fumee-murs")) smoke.Walls = grid;
                    Console.WriteLine($"Fumée : {smoke.Points.Count} points, {smoke.Sources.Count} foyers producteurs, total initial {smoke.Total:0.0}");
                }
                // arroser=<foyer>@<début>-<fin>[:<classe d'unité>] : une unité arrose ce foyer
                //   (classe par défaut Unit\Fireman\fireman_medic ; exemple : arroser=ground_l109@60.6-74:Unit\Fireman\fireman_sp)
                var unitCache = new Dictionary<string, ClassFile>(StringComparer.OrdinalIgnoreCase);
                var sprays = opts.Where(o => o.StartsWith("arroser=")).Select(o =>
                {
                    var m = System.Text.RegularExpressions.Regex.Match(o[8..], @"^([^@]+)@([\d.]+)-([\d.]+)(?::(.+))?$");
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    var cls = m.Groups[4].Success ? m.Groups[4].Value : "Unit\\Fireman\\fireman_medic";
                    var unit = FD3.Fire.Extinguisher.FromClass(cls, ClassFile.Load(files, "Design\\" + cls + ".class", unitCache));
                    return (hp: world.HotPoints.First(h => h.Name.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)),
                            t0: float.Parse(m.Groups[2].Value, inv), t1: float.Parse(m.Groups[3].Value, inv), unit);
                }).ToList();
                var stepOpt = opts.FirstOrDefault(o => o.StartsWith("pas="));
                int reportEvery = stepOpt != null ? int.Parse(stepOpt[4..]) : 60;
                // avance=<s> : fait brûler les foyers de départ pendant ce temps avant de commencer à mesurer
                // (les feux de départ brûlent déjà pendant la cinématique d'introduction)
                var warmOpt = opts.FirstOrDefault(o => o.StartsWith("avance="));
                if (warmOpt != null)
                {
                    var seedSet = world.HotPoints.Where(h => h.Lit).ToHashSet();
                    float warm = float.Parse(warmOpt[7..], System.Globalization.CultureInfo.InvariantCulture);
                    for (float t = 0; t < warm; t += 0.1f)
                    {
                        world.Step(0.1f);
                        foreach (var h in world.HotPoints) if (h.Lit && !seedSet.Contains(h)) { h.Lit = false; h.State = FD3.Fire.HotPointState.Idle; h.SetIntensity(0); }
                    }
                }
                int withSources = world.HotPoints.Count(h => h.HeatSources.Count > 0);
                Console.WriteLine($"{mission} : {world.HotPoints.Count} foyers, {world.Links.Count} liens, {withSources} foyers ont au moins une source de chaleur");
                Console.WriteLine($"Difficulté {diff} : tempo ×{settings.TempoFactor}, croissance ×{settings.GrowthFactor}, évaporation {settings.NaturalEvaporationRate}/s");
                Console.WriteLine($"En feu au départ : {world.HotPoints.Count(h => h.Lit)}");
                Console.WriteLine(" temps(s) | en feu | consumés | intensité totale | plus forte");
                using var csv = new StreamWriter(Path.Combine(Path.GetTempPath(), $"fd3_{mission}_sim.csv"));
                csv.WriteLine("temps_s,id,nom,intensite,etat");
                const float dt = 0.1f;
                int steps = (int)(minutes * 60 / dt);
                for (int s = 0; s <= steps; s++)
                {
                    if (s % (int)(10 / dt) == 0)
                        foreach (var h in world.HotPoints.Where(h => h.Lit || h.State == FD3.Fire.HotPointState.Consumed))
                            csv.WriteLine($"{world.Time:0.0},{h.Id},{h.Name},{h.Intensity:0.0},{h.State}");
                    if (s % (int)(reportEvery / dt) == 0)
                    {
                        var lit = world.HotPoints.Where(h => h.IsBurning).ToList();
                        Console.WriteLine($" {world.Time,6:0} | {lit.Count,6} | {world.HotPoints.Count(h => h.State == FD3.Fire.HotPointState.Consumed),8} | {lit.Sum(h => h.Intensity),16:0} | {(lit.Count > 0 ? lit.Max(h => h.Intensity) : 0),10:0}");
                    }
                    foreach (var ev in events.Where(ev => ev.t > world.Time - dt / 2 && ev.t <= world.Time + dt / 2))
                        foreach (var h in ev.hps)
                            if (ev.action == "ajouter") world.AddIntensity(h, ev.v); else h.GrowthMultiplier *= ev.v;
                    foreach (var sp in sprays) if (world.Time >= sp.t0 && world.Time < sp.t1 && sp.hp.Lit) sp.unit.Spray(world, sp.hp, dt);
                    world.Step(dt);
                    smoke?.Step(world, dt);
                }
                foreach (var l in world.ExtinguishLog) Console.WriteLine("Extinction : " + l);
                foreach (var sp in sprays) Console.WriteLine($"Unité {sp.unit.Name} ({sp.unit.Agent}, puissance {sp.unit.Power}) : réserve {sp.unit.Reserve:0.00}/{sp.unit.ReserveMax}, endurance {sp.unit.Stamina:0.0}");
                if (smoke != null)
                {
                    Console.WriteLine($"Fumée finale : total {smoke.Total:0.0} ; points non vides : " + string.Join(", ", smoke.Points.Where(p => p.Intensity > 0.5f).Select(p => $"{p.Name}={p.Intensity:0.0}").Take(30)));
                    foreach (var l in smoke.Log) Console.WriteLine("  " + l);
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), $"fd3_{mission}_fumee.json"), System.Text.Json.JsonSerializer.Serialize(smoke.Points.ToDictionary(p => p.Name, p => p.Intensity)));
                }
                Console.WriteLine("Détail toutes les 10 s : " + Path.Combine(Path.GetTempPath(), $"fd3_{mission}_sim.csv"));
                return 0;
            }
        }
        return 1;

        // Pièces : « auto » = calculées à partir des graphismes de la mission, sinon fichier JSON {id: pièce}.
        static Dictionary<uint, string> RoomMap(GameFiles files, Level level, string mission, string spec)
        {
            if (spec.Equals("auto", StringComparison.OrdinalIgnoreCase)) return Rooms.Assign(level, Rooms.Load(files, mission));
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(spec))!;
            return map.ToDictionary(kv => uint.Parse(kv.Key), kv => kv.Value);
        }
    }
}
