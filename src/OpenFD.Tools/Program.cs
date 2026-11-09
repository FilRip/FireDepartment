using OpenFD.Game;
using OpenFD.Formats;

namespace OpenFD.Tools;

public static class Program
{
    public static int Main(string[] args)
    {
        // Outils de vérification : dotnet run --project src/OpenFD.Tools -- <commande> "<chemin vers Data>"
        if (args.Length < 2)
        {
            Console.WriteLine("Usage : roundtrip|fireclasses <dossier Data>");
            return 1;
        }
        GameFiles files = new GameFiles(args[1]);

        switch (args[0])
        {
            case "roundtrip":
            {
                int bad = 0;
                foreach (string path in files.List("Design\\Mission\\").Where(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase)))
                {
                    byte[] d = files.Read(path);
                    Level L = Level.Parse(d);
                    byte[] o = L.Serialize();
                    bool same = o.AsSpan().SequenceEqual(d);
                    if (!same)
                        bad++;
                    string gp = Path.ChangeExtension(path, ".grid");
                    Grid? g = files.TryRead(gp) is { } gb ? Grid.Parse(gb) : null;
                    Console.WriteLine($"{Path.GetFileName(path),-22} {L.Objects.Count,5} objets  {L.HotPointLinks.Count,4} liens  grille {g?.Width}x{g?.Height}  relecture {(same ? "identique" : "DIFFÉRENTE")}");
                }
                return bad == 0 ? 0 : 2;
            }
            case "reparer":
            {
                // reparer <Data> <Mission> [pas] : objets à réparer / utiliser, et qui peut le faire
                Session ses = new Session(files, args[2], "EASY");
                int steps = args.Length > 3 ? int.Parse(args[3]) : 300;
                for (int i = 0; i < steps; i++)
                {
                    ses.Step();
                    if (i % 50 == 0)
                        ses.Host?.EndCinematic();
                }
                ses.Scripts!.Load(OpenFD.Script.Parser.ParseFile("s32 __V() { return SCR_GetVarS32(\"RepairedGenerator\") + 10 * SCR_GetVarS32(\"RepGen_1\"); }", "essai"));
                foreach (UsableObject o in ses.Units.Usables)
                    Console.WriteLine($"{o.Id} {o.Name,-28} utiliser {o.CanBeUsed} réparer {o.CanBeRepaired} réparé {o.Repaired} actions [{string.Join(", ", o.Actions.Select(a => a.action + "→" + a.script))}]");
                foreach (Unit x in ses.Units.Units.Where(x => !x.IsVehicle))
                    Console.WriteLine($"  {x.Name,-24} à bord {x.OnBoard} CanUse {x.CanUse} CanRepair {x.CanRepair}");
                // le technicien répare le premier objet à réparer
                if (ses.Units.Usables.FirstOrDefault(o => o.CanBeRepaired && !o.Repaired) is { } rep && ses.Units.Units.FirstOrDefault(x => x.CanRepair && !x.OnBoard) is { } ing)
                {
                    Console.WriteLine($"réparation de {rep.Name} : emprise ({rep.BoxMin.X:0},{rep.BoxMin.Y:0})-({rep.BoxMax.X:0},{rep.BoxMax.Y:0}) par {ing.Name}");
                    ing.Position = rep.Position + new System.Numerics.Vector3(300, 0, 0);
                    ing.Path.Clear();
                    ses.Units.UseObject(ing, rep);
                    for (int i = 0; i < 200; i++)
                        ses.Step();
                    Console.WriteLine($"  réparé {rep.Repaired} · {ing.Status} · RepairedGenerator = {ses.Scripts!.TryRun("__V")?.AsNum}");
                    foreach (string l in ses.Units.Log.TakeLast(4))
                        Console.WriteLine("  journal : " + l.Trim());
                    foreach (OpenFD.Game.MissionHost.Objective ob in ses.Host!.Objectives)
                        Console.WriteLine($"  objectif : {ob.Text} {ob.Status}");
                }
                // puis un pompier actionne l'interrupteur (objet à utiliser) ; état des portes animées
                if (ses.Units.Usables.FirstOrDefault(o => o.CanBeUsed && o.Actions.Any(a => a.script == "ElectricityClosed")) is { } sw && ses.Units.Units.FirstOrDefault(x => x.CanUse && !x.OnBoard && !x.IsVehicle) is { } user)
                {
                    Console.WriteLine($"interrupteur {sw.Name} par {user.Name}");
                    user.Position = sw.Position + new System.Numerics.Vector3(150, 0, 0);
                    user.Path.Clear();
                    ses.Units.UseObject(user, sw);
                    for (int i = 0; i < 400; i++)
                    {
                        ses.Step();
                        if (i % 50 == 0)
                            ses.Host?.EndCinematic();
                    }
                    Console.WriteLine($"  {user.Status}");
                    foreach (string l in ses.Units.Log.TakeLast(6))
                        Console.WriteLine("  journal : " + l.Trim());
                    foreach ((string vocal, string text, float x, float y) m in ses.Host!.Messages.TakeLast(3))
                        Console.WriteLine("  message : " + m.text);
                }
                foreach (Opening d in ses.Units.Openings.Where(d => d.ScriptGate))
                    Console.WriteLine($"porte script {d.Id} {d.Name} état {d.State} anim {d.OpenAnim}");
                foreach (Civilian cv in ses.Units.Civilians)
                    Console.WriteLine($"civil {cv.Id} {cv.Name,-24} caché {cv.Hidden,-5} état {cv.State,-12} ({cv.Position.X:0},{cv.Position.Y:0})");
                Console.WriteLine($"script : CivilianSaved {ses.Scripts!.Globals.GetValueOrDefault("CivilianSaved").AsInt} / CounterPeop {ses.Scripts.Globals.GetValueOrDefault("CounterPeop").AsInt}");
                // seconde réparation (Mriya : transformateur du hangar, rendu réparable par le script) : cinématique, PNJ, avion
                if (Environment.GetEnvironmentVariable("FD3_REPARER2") is { Length: > 0 } r2 && uint.TryParse(r2, out uint rid))
                {
                    ses.Units.SetCapacity(rid, "CouldBeRepair", true);
                    UsableObject? t2 = ses.Units.Usables.FirstOrDefault(o => o.Id == rid);
                    Unit? ing2 = ses.Units.Units.FirstOrDefault(x => x.CanRepair && !x.OnBoard);
                    if (t2 != null && ing2 != null)
                    {
                        Console.WriteLine($"réparation 2 de {t2.Name} : emprise ({t2.BoxMin.X:0},{t2.BoxMin.Y:0})-({t2.BoxMax.X:0},{t2.BoxMax.Y:0})");
                        ing2.Position = t2.Position + new System.Numerics.Vector3(300, 0, 0);
                        ing2.Path.Clear();
                        ing2.Target = null;
                        ses.Units.UseObject(ing2, t2);
                        int log0 = ses.Host!.FireLog.Count, slog0 = ses.Scripts!.Log.Count;
                        for (int i = 0; i < 400; i++)
                        {
                            ses.Step();
                            if (i % 40 == 0)
                                foreach (Civilian cv in ses.Units.Civilians.Where(cv => cv.Id is 5017081 or 5017206))
                                    Console.WriteLine($"  t {ses.Time,5:0} {cv.Name} caché {cv.Hidden} état {cv.State} pos ({cv.Position.X:0},{cv.Position.Y:0}) · avion {(ses.Host.MovedObjects.TryGetValue(5004103, out System.Numerics.Vector2 av) ? $"({av.X:0},{av.Y:0})" : "-")}");
                        }
                        Console.WriteLine($"  réparé {t2.Repaired} · {ing2.Status}");
                        foreach (string l in ses.Host.FireLog.Skip(log0).Take(40))
                            Console.WriteLine("  hôte : " + l.Trim());
                        foreach (string l in ses.Scripts.Log.Skip(slog0).Take(20))
                            Console.WriteLine("  script : " + l.Trim());
                        foreach (KeyValuePair<string, int> kv in ses.Scripts.UnknownCalls)
                            Console.WriteLine($"  inconnu : {kv.Key} ×{kv.Value}");
                    }
                }
                return 0;
            }
            case "agents":
            {
                // agents <Data> <Mission> : agents des unités ; changement d'agent à un camion d'un autre agent
                Session ses = new Session(files, args[2], "EASY");
                int steps = args.Length > 3 ? int.Parse(args[3]) : 300;
                for (int i = 0; i < steps; i++)
                {
                    ses.Step();
                    if (i % 50 == 0)
                        ses.Host?.EndCinematic();
                }
                Console.WriteLine($"équipes : {string.Join(", ", ses.Units.Teams)}");
                foreach (Unit x in ses.Units.Units.Where(x => !x.OnBoard))
                    Console.WriteLine($"{x.Name,-28} équipe {x.TeamName ?? "-",-6} véhicule {x.IsVehicle,-5} agent {x.Nozzle?.Agent ?? "-",-6} fournit {(x.ResupplyPower > 0 ? UnitManager.SupplyAgentOf(x) : "-")}");
                Unit? w = ses.Units.Units.FirstOrDefault(x => !x.IsVehicle && !x.OnBoard && x.Nozzle is { Agent: "WATER", InfiniteReserve: false } && !x.IsCanon);
                Unit? tox = ses.Units.Units.FirstOrDefault(x => x.IsVehicle && !x.OnBoard && x.ResupplyPower > 0 && UnitManager.SupplyAgentOf(x) == "METAL");
                if (w == null || tox == null)
                {
                    Console.WriteLine("pas de pompier à l'eau ou pas de camion anti-métal");
                    return 0;
                }
                w.Position = tox.Position + new System.Numerics.Vector3(250, 0, 0);
                w.Path.Clear();
                w.Target = null;
                Console.WriteLine($"{w.Name} : {w.Nozzle!.Agent} {w.Nozzle.Reserve:0}/{w.Nozzle.ReserveMax:0} → camion {tox.Name}");
                ses.Units.GoToVehicle(w, tox);
                for (int k = 0; k < 14; k++)
                {
                    for (int i = 0; i < 20; i++)
                        ses.Step();
                    Console.WriteLine($"  {k * 2 + 2} s : {w.Nozzle.Agent} {w.Nozzle.Reserve:0} · {w.Status}");
                }
                foreach (string l in ses.Units.Log.TakeLast(3))
                    Console.WriteLine("journal : " + l.Trim());
                return 0;
            }
            case "eteindre":
            {
                // eteindre <Data> <Mission> <foyer> [classe d'unité] : un pompier arrose ce foyer ; intensité toutes les 5 s
                Session ses = new Session(files, args[2], "EASY");
                OpenFD.Fire.HotPoint? h = ses.Fire.HotPoints.FirstOrDefault(x => x.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
                if (h == null)
                    return 1;
                for (int i = 0; i < 300; i++)
                    ses.Step();
                Console.WriteLine($"{h.Name} type {h.Class.FireType} : intensité {h.Intensity:0.0}/{h.Class.IntensityMax} allumé {h.Lit} sous tension {h.Powered}");
                OpenFD.Fire.Extinguisher ex = ses.Spray(h, args.Length > 4 ? args[4] : "Unit\\Fireman\\fireman_sp_ua");
                ex.InfiniteReserve = true;
                for (int i = 0; i < 1200; i++)
                {
                    ses.Step();
                    if (i % 50 == 0)
                        Console.WriteLine($"  t {ses.Time,5:0} intensité {h.Intensity:0.0} allumé {h.Lit}");
                }
                return 0;
            }
            case "typesfeux":
            {
                // typesfeux <Data> <Mission> : nombre de foyers par type de feu et par classe
                Session ses = new Session(files, args[2], "EASY", false);
                foreach (IGrouping<string, OpenFD.Fire.HotPoint> g in ses.Fire.HotPoints.GroupBy(h => $"{h.Class.FireType} {h.Class.Name}").OrderBy(g => g.Key))
                    Console.WriteLine($"{g.Key,-50} {g.Count()}");
                // [pas] : après autant de pas, les foyers en feu (type, position)
                if (args.Length > 3)
                {
                    for (int i = 0; i < int.Parse(args[3]); i++)
                        ses.Step();
                    foreach (OpenFD.Fire.HotPoint h in ses.Fire.HotPoints.Where(h => h.IsBurning).Take(60))
                        Console.WriteLine($"  en feu : {h.Class.FireType} {h.Name} ({h.Position.X:0},{h.Position.Y:0},{h.Position.Z:0})");
                }
                return 0;
            }
            case "groupefeux":
            {
                // groupefeux <Data> <Mission> <groupe> : foyers du groupe (classe, type de feu, actif)
                Session ses = new Session(files, args[2], "EASY", false);
                LevelObject g = ses.Level.Objects.First(o => o.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
                foreach (uint m in g.Members)
                    if (ses.Fire.HotPoints.FirstOrDefault(h => h.Id == m) is { } h)
                        Console.WriteLine($"{h.Name,-30} type {h.Class.FireType} actif {h.Enabled} ({h.Position.X:0},{h.Position.Y:0},{h.Position.Z:0})");
                Console.WriteLine("types C de la carte : " + ses.Fire.HotPoints.Count(h => h.Class.FireType == 'C'));
                return 0;
            }
            case "elec":
            {
                // elec <Data> <Mission> : feux électriques — ordre d'arroser sous tension, puis courant coupé
                Session ses = new Session(files, args[2], "EASY");
                for (int i = 0; i < 300; i++)
                {
                    ses.Step();
                    if (i % 50 == 0)
                        ses.Host?.EndCinematic();
                }
                ses.Scripts!.Load(OpenFD.Script.Parser.ParseFile("void __On() { SCR_SetVarS32(\"ElectroRun\", 1); FIR_HPEnable(ENT_FindByName(\"electrogroup\"), 1); FIR_HPAddIntensity(ENT_FindByName(\"electrogroup\"), 100.0); } void __Off() { FIR_HPEnable(ENT_FindByName(\"electrogroup\"), 0); }", "essai"));
                ses.Scripts.TryRun("__On");
                for (int i = 0; i < 20; i++)
                    ses.Step();
                OpenFD.Fire.HotPoint h = Environment.GetEnvironmentVariable("FD3_FEU_A") != null
                    ? ses.Fire.HotPoints.Where(x => x.IsBurning && x.Class.FireType == 'A').OrderByDescending(x => x.Intensity).First()
                    : ses.Fire.HotPoints.First(x => x.Electrified);
                Unit u = Environment.GetEnvironmentVariable("FD3_ELEC_CAMION") != null
                    ? ses.Units.Units.First(x => UnitManager.WaterTruck(x) && !x.OnBoard)
                    : ses.Units.Units.First(x => !x.IsVehicle && x.Nozzle != null && !x.IsCanon && !x.OnBoard);
                u.Position = h.Position + new System.Numerics.Vector3(500, 0, 0);
                u.Path.Clear();
                Console.WriteLine(h.Class.ToString());
                Console.WriteLine($"{h.Name} électrique {h.Electrified} intensité {h.Intensity:0} ; {u.Name} vie {u.Life:0}");
                ses.Units.Attack(u, h);
                for (int i = 0; i < 30; i++)
                    ses.Step();
                Console.WriteLine($"  sous tension : {u.Status} · vie {u.Life:0} · cible {u.Target?.Name ?? "aucune"}");
                ses.Scripts.TryRun("__Off");
                Console.WriteLine($"  courant coupé : électrique {h.Electrified}, actif {h.Enabled} · puissance {u.Nozzle!.Power} · efficacité C {u.Nozzle.EfficiencyFor('C')} · réserve {u.Nozzle.Reserve} · max {h.Class.IntensityMax} état {h.State}");
                ses.Units.Attack(u, h);
                for (int k = 0; k < 6; k++)
                {
                    for (int i = 0; i < 100; i++)
                    {
                        ses.Step();
                        if (k == 0 && i < 3)
                            Console.WriteLine($"    pas {i} : attente {h.PendingAttack:0.00} intensité {h.Intensity:0.00} sous tension {h.Powered} activé {h.Enabled} endurance {u.Nozzle!.Stamina:0} u.cible {u.Target?.Name} bouge {u.Moving} dist {System.Numerics.Vector3.Distance(u.Position, h.Position):0}");
                    }
                    Console.WriteLine($"  ensuite : {u.Status} · intensité {h.Intensity:0} · éteint ? {!h.IsBurning}");
                }
                foreach (string l in ses.Scripts.Log)
                    Console.WriteLine("script : " + l);
                Civilian? bur = ses.Units.Civilians.FirstOrDefault(c => ses.Units.IsBuried(c));
                if (bur != null)
                {
                    bur.Hidden = false;
                    ses.Units.FetchCivilian(u, bur);
                    Console.WriteLine($"civil enterré {bur.Name} : {u.Status}");
                }
                return 0;
            }
            case "chien":
            {
                // chien <Data> <Mission> : victimes enterrées, gravats verrouillés ; le chien est posé près de chacune et flaire
                Session ses = new Session(files, args[2], "EASY");
                for (int i = 0; i < 600; i++)
                {
                    ses.Step();
                    if (i % 50 == 0)
                        ses.Host?.EndCinematic();
                }
                Console.WriteLine("cachés : " + string.Join(", ", ses.Units.Civilians.Where(c => c.Hidden).Select(c => c.Name)));
                Console.WriteLine("gravats cassables : " + string.Join(", ", ses.Units.Openings.Where(o => o.Name.StartsWith("remains")).Select(o => o.Name + "=" + o.Breakable)));
                if (ses.Units.Units.FirstOrDefault(u => u.IsDog) is not { } dog)
                {
                    Console.WriteLine("pas de chien");
                    return 0;
                }
                Console.WriteLine($"chien {dog.Name} ({dog.Position.X:0},{dog.Position.Y:0}) lance {dog.Nozzle != null}");
                Console.WriteLine($"flaire sur place : {ses.Units.DogSense(dog)}");
                Civilian v = ses.Units.Civilians.First(c => c.Hidden);
                dog.Position = v.Position + new System.Numerics.Vector3(600, 0, 0);
                Console.WriteLine($"près de {v.Name} : {ses.Units.DogSense(dog)}");
                Console.WriteLine("cachés : " + string.Join(", ", ses.Units.Civilians.Where(c => c.Hidden).Select(c => c.Name)));
                Console.WriteLine("gravats cassables : " + string.Join(", ", ses.Units.Openings.Where(o => o.Name.StartsWith("remains")).Select(o => o.Name + "=" + o.Breakable)));
                return 0;
            }
            case "canons":
            {
                // canons <Data> <Mission> <secondes> : pompiers « canon », leur camion et ce qu'ils font
                Session ses = new Session(files, args[2], "EASY");
                int n = (int)(float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) / Session.Dt);
                for (int i = 0; i < n; i++)
                {
                    ses.Step();
                    if (i % 50 == 0)
                        ses.Host?.EndCinematic();
                }
                foreach (Unit u in ses.Units.Units.Where(u => u.IsCanon))
                    Console.WriteLine($"{u.Name} ({u.Position.X:0},{u.Position.Y:0}) à bord {u.OnBoard} · camion {u.FeedTruck?.Name ?? "aucun"} · {u.Status}");
                // … attaque : le premier canon reçoit l'ordre d'arroser le feu le plus proche, puis 20 s passent
                if (args.Length > 4 && ses.Units.Units.FirstOrDefault(u => u.IsCanon) is { } cu)
                {
                    OpenFD.Fire.HotPoint h = ses.Fire.HotPoints.Where(h => h.IsBurning).OrderBy(h => System.Numerics.Vector3.Distance(h.Position, cu.Position)).First();
                    Console.WriteLine($"ordre : arroser {h.Name} à {System.Numerics.Vector3.Distance(h.Position, cu.Position):0}, intensité {h.Intensity:0.0}, camion {cu.FeedTruck?.Nozzle?.Reserve:0}");
                    ses.Units.Attack(cu, h);
                    for (int i = 0; i < 200; i++)
                    {
                        ses.Step();
                        if (i % 40 == 0)
                            Console.WriteLine($"  {i / 10} s : ({cu.Position.X:0},{cu.Position.Y:0}) {cu.Status} · camion {cu.FeedTruck?.Name ?? "aucun"} {cu.FeedTruck?.Nozzle?.Reserve:0} · feu {h.Intensity:0.0}");
                    }
                }
                Console.WriteLine($"portée de liaison {OpenFD.Game.UnitManager.CanonLinkRange:0}");
                return 0;
            }
            case "objliste":
            {
                // objliste <Data> <Mission> [motif] : objets du niveau (id, classe, nom, position, rotation)
                string lvPath = files.List("Design\\Mission\\" + args[2] + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                Level level = Level.Parse(files.Read(lvPath));
                System.Text.RegularExpressions.Regex rx = new System.Text.RegularExpressions.Regex(args.Length > 3 ? args[3] : ".", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (LevelObject o in level.Objects.Where(o => rx.IsMatch(o.ClassPath + " " + o.Name)))
                    Console.WriteLine($"{o.Id,9} {o.Kind,-10} {o.ClassPath,-50} {o.Name,-28} ({o.Position.X:0},{o.Position.Y:0},{o.Position.Z:0}) rot ({o.Rotation.X:0.00},{o.Rotation.Y:0.00},{o.Rotation.Z:0.00})");
                return 0;
            }
            case "pieces":
            {
                // pieces <Data> <Mission> [sortie.json] : pièce de chaque foyer et point de fumée (remplace roomall.py)
                string lvPath = files.List("Design\\Mission\\" + args[2] + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                Level level = Level.Parse(files.Read(lvPath));
                List<Rooms.Room> rooms = Rooms.Load(files, args[2]);
                Dictionary<uint, string> map = Rooms.Assign(level, rooms);
                Console.WriteLine($"{rooms.Count} pièces ; {map.Count(kv => kv.Value != "")} objets en pièce sur {map.Count}");
                if (args.Length > 3)
                    File.WriteAllText(args[3], System.Text.Json.JsonSerializer.Serialize(map.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)));
                return 0;
            }
            case "portes":
            {
                // portes <Data> <Mission> : objets dont l'occupation est DOOR ou WINDOW (classe, position, composants)
                Session ses = new Session(files, args[2], "EASY", false);
                Dictionary<string, int> stats = new Dictionary<string, int>();
                foreach (LevelObject o in ses.Level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform))
                {
                    ClassFile cf;
                    try { cf = ClassFile.Load(files, "Design\\" + o.ClassPath["design\\".Length..] + ".class"); } catch { continue; }
                    string type = cf.Get("OCCUPATION", "Occupation_Type")?.Trim('"', ' ').ToUpperInvariant() ?? "";
                    stats[type] = stats.GetValueOrDefault(type) + 1;
                    if (type is not ("DOOR" or "WINDOW"))
                        continue;
                    if (args.Length > 3 && !o.Name.Contains(args[3], StringComparison.OrdinalIgnoreCase) && args[3] != "*")
                        continue;
                    Console.WriteLine($"{type,-6} {o.Name,-28} {o.ClassPath,-50} ({o.Position.X:0},{o.Position.Y:0},{o.Position.Z:0}) rotZ {o.Rotation.Z:0.00} comps {string.Join(",", o.Components.Select(c => c.Id.ToString("X4") + ":" + c.Size))}");
                }
                Console.WriteLine(string.Join(", ", stats.Select(kv => kv.Key + "=" + kv.Value)));
                return 0;
            }
            case "classefich":
            {
                // classefich <Data> <chemin Design\…\x.class>… : toutes les sections fusionnées (avec #INCLUDE)
                foreach (string rel in args.Skip(2))
                {
                    ClassFile cf = ClassFile.Load(files, rel);
                    Console.WriteLine("== " + rel);
                    foreach (string sec in cf.Sections)
                        Console.WriteLine($"  [{sec}] " + string.Join(", ", cf.Section(sec).Select(kv => kv.Key + "=" + kv.Value)));
                }
                return 0;
            }
            case "compos":
            {
                // compos <Data> <Mission> <composant hex> <nom>… : octets d'un composant (hex + texte)
                Session ses = new Session(files, args[2], "EASY", false);
                ushort id = Convert.ToUInt16(args[3], 16);
                foreach (LevelObject o in ses.Level.Objects.Where(o => args.Skip(4).Any(n => o.Name.Equals(n, StringComparison.OrdinalIgnoreCase) || (n.EndsWith("*") && o.Name.StartsWith(n[..^1], StringComparison.OrdinalIgnoreCase)))))
                    if (o.GetComponent(id) is { } c)
                    {
                        byte[] b = o.ComponentBytes(c).ToArray();
                        Console.WriteLine($"{o.Name,-26} {Convert.ToHexString(b)}");
                        Console.WriteLine($"{"",-26} {new string(b.Select(x => x is >= 32 and < 127 ? (char)x : '.').ToArray())}");
                    }
                return 0;
            }
            case "plan":
            {
                // plan <Data> <Mission> x0 y0 x1 y1 : carte texte (# mur, o objet, D porte, F fenêtre, . libre), nord en haut
                Session ses = new Session(files, args[2], "EASY", false);
                Grid g = ses.Grid!;
                int x0 = int.Parse(args[3]) / 100, y0 = int.Parse(args[4]) / 100, x1 = Math.Min(g.Width - 1, int.Parse(args[5]) / 100), y1 = Math.Min(g.Height - 1, int.Parse(args[6]) / 100);
                Dictionary<(int, int), char> marks = new Dictionary<(int, int), char>();
                foreach (Opening d in ses.Units.Doors)
                    foreach (int i in d.Cells)
                        marks[(i % g.Width, i / g.Width)] = d.IsWindow ? 'F' : 'D';
                for (int y = y1; y >= y0; y--)
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder($"{y * 100,6} ");
                    for (int x = x0; x <= x1; x++)
                    {
                        int i = g.Index(x, y);
                        sb.Append(marks.TryGetValue((x, y), out char m) ? m : g.ZoneA[i] == 0 ? '#' : ses.Units.IsBlocked(i) ? 'o' : '.');
                    }
                    Console.WriteLine(sb);
                }
                foreach (Opening d in ses.Units.Doors.Where(d => d.Position.X >= x0 * 100 && d.Position.X <= x1 * 100 && d.Position.Y >= y0 * 100 && d.Position.Y <= y1 * 100))
                    Console.WriteLine($"{d.Name,-26} {d.State,-8} cassable {d.Breakable} ouvrable {d.Usable} ({d.Position.X:0},{d.Position.Y:0},{d.Position.Z:0}) cases {d.Cells.Count}");
                return 0;
            }
            case "fumeepieces":
            {
                // fumeepieces <Data> <Mission> <secondes> : fumée totale par pièce
                Session ses = new Session(files, args[2], "EASY", false);
                for (int i = 0; i < float.Parse(args[3]) / Session.Dt; i++)
                    ses.Step();
                foreach (IGrouping<string, Fire.SmokePoint> g in ses.Smoke.Points.GroupBy(p => p.Room == "" ? "(dehors)" : p.Room).OrderBy(g => g.Key))
                    if (g.Sum(p => p.Intensity) > 0.05)
                        Console.WriteLine($"{g.Key,-28} {g.Sum(p => p.Intensity),8:0.0}");
                Console.WriteLine($"Total {ses.Smoke.Total:0.0}");
                return 0;
            }
            case "porte":
            {
                // porte <Data> <Mission> <porte>… : le premier pompier qui peut casser (sinon le premier piéton) s'occupe de chaque porte, dans l'ordre
                Session ses = new Session(files, args[2], "EASY", false);
                for (int i = 0; i < 30; i++)
                    ses.Step();
                foreach (string name in args.Skip(3))
                {
                    Opening d = ses.Units.Openings.First(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    Unit u = ses.Units.Units.Where(x => !x.IsVehicle).OrderBy(x => d.Breakable && x.CanBreak ? 0 : 1).First();
                    ses.Units.WorkOnOpening(u, d);
                    Console.WriteLine($"{u.Name} ({u.Position.X:0},{u.Position.Y:0}) → {d.Name} centre ({d.Center.X:0},{d.Center.Y:0},{d.Center.Z:0}) ({d.State}, vie {d.Life:0}) : {u.Status}, chemin {u.Path.Count}");
                    float t0 = ses.Time;
                    for (int i = 0; i < 3000 && u.Opening != null; i++)
                        ses.Step();
                    Console.WriteLine($"   {ses.Time - t0:0.0} s plus tard : {d.Name} {d.State}, vie {d.Life:0}, coups {u.Blows}, {u.Name} : {u.Status}");
                }
                if (Environment.GetEnvironmentVariable("FD3_FUMEE") is { } fs)
                {
                    if (Environment.GetEnvironmentVariable("FD3_CASSER") is { } cs)
                        foreach (string n in cs.Split(','))
                            ses.Units.SetOpeningState(ses.Units.Openings.First(o => o.Name == n), OpeningState.Broken);
                    for (int i = 0; i < float.Parse(fs) / Session.Dt; i++)
                        ses.Step();
                    foreach (IGrouping<string, Fire.SmokePoint> g in ses.Smoke.Points.GroupBy(p => p.Room == "" ? "(dehors)" : p.Room).OrderBy(g => g.Key))
                        if (g.Sum(p => p.Intensity) > 0.05)
                            Console.WriteLine($"  fumée {g.Key,-28} {g.Sum(p => p.Intensity),8:0.0}");
                }
                foreach (string l in ses.Units.Log)
                    Console.WriteLine(l);
                return 0;
            }
            case "ouvpieces":
            {
                // ouvpieces <Data> <Mission> : pour chaque porte/fenêtre, les pièces des points de fumée proches (InfluenceRange)
                Session ses = new Session(files, args[2], "EASY", false);
                foreach (Opening o in ses.Units.Openings)
                {
                    IEnumerable<string> rooms = ses.Smoke.Points.Where(p => System.Numerics.Vector2.Distance(new(p.Position.X, p.Position.Y), new(o.Center.X, o.Center.Y)) < p.Class.InfluenceRange && MathF.Abs(p.Position.Z - o.Center.Z) < 400)
                        .GroupBy(p => p.Room == "" ? "(dehors)" : p.Room).Select(g => $"{g.Key}×{g.Count()}");
                    Console.WriteLine($"{o.Name,-28} {o.State,-7} z {o.Center.Z,5:0} : {string.Join(", ", rooms)}");
                }
                return 0;
            }
            case "conduire":
            {
                // conduire <Data> <Mission> x y : chaque véhicule roule vers (x, y) ; on vérifie qu'il ne traverse ni mur ni objet
                Session ses = new Session(files, args[2], "EASY", false);
                Grid g = ses.Grid!;
                for (int i = 0; i < 300; i++)
                    ses.Step();
                System.Numerics.Vector2 dest = new System.Numerics.Vector2(float.Parse(args[3]), float.Parse(args[4]));
                foreach (Unit v in ses.Units.Units.Where(u => u.IsVehicle).ToList())
                {
                    ses.Units.Drive(v, dest);
                    Console.WriteLine($"{v.Name} ({v.Position.X:0},{v.Position.Y:0}) : {v.Status}, {v.Path.Count} points");
                    int bad = 0;
                    for (int i = 0; i < 3000 && v.Moving; i++)
                    {
                        ses.Step();
                        (int cx, int cy) = g.CellOf(v.Position);
                        if (!g.Inside(cx, cy) || g.ZoneA[g.Index(cx, cy)] == 0 || ses.Units.IsBlocked(g.Index(cx, cy)))
                            bad++;
                    }
                    Console.WriteLine($"   arrivé ({v.Position.X:0},{v.Position.Y:0}) à {System.Numerics.Vector2.Distance(new(v.Position.X, v.Position.Y), dest):0} du point ; pas dans un obstacle : {bad} ; {v.Status}");
                }
                return 0;
            }
            case "liste":
            {
                // liste <Data> <préfixe> [filtre] : fichiers des archives
                foreach (string f in files.List(args[2]).Where(f => args.Length < 4 || f.Contains(args[3], StringComparison.OrdinalIgnoreCase)))
                    Console.WriteLine(f);
                return 0;
            }
            case "toutextraire":
            {
                // toutextraire <Data> <préfixe> <dossier> : copie tous les fichiers des archives sous ce préfixe
                int n = 0;
                foreach (string f in files.List(args[2]))
                {
                    string dest = Path.Combine(args[3], f.Replace('\\', '/'));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    try
                    {
                        File.WriteAllBytes(dest, files.Read(f));
                        n++;
                    } catch { }
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
                SceneAssets assets = new OpenFD.Game.SceneAssets(files);
                CharacterRig rig = OpenFD.Game.CharacterRig.Build(assets, files, args[2])!;
                Console.WriteLine($"{rig.Names.Length} nœuds, {rig.Parts.Count} pièces");
                AnimClip? clip = args.Length > 3 ? AnimClip.Parse(files.Read(args[3].EndsWith(".anim", StringComparison.OrdinalIgnoreCase) ? args[3] : "Gfx\\Anim\\" + args[3] + ".anim")) : null;
                if (clip != null)
                    Console.WriteLine($"animation : {clip.Tracks.Count} pistes, {clip.Duration:0.00} s");
                if (Environment.GetEnvironmentVariable("FD3_PISTES") == "1")
                    foreach (float t in clip == null ? new[] { 0f } : new[] { 0f, clip.Duration / 2, clip.Duration })
                    {
                        System.Numerics.Matrix4x4[] w = rig.Pose(clip, t);
                        for (int i = 0; i < rig.Names.Length; i++)
                            Console.WriteLine($"  t={t:0.0} {rig.Names[i],-24} parent {(rig.Parent[i] >= 0 ? rig.Names[rig.Parent[i]] : "-"),-22} pos ({w[i].Translation.X:0},{w[i].Translation.Y:0},{w[i].Translation.Z:0}) haut ({w[i].M31:0.00},{w[i].M32:0.00},{w[i].M33:0.00})");
                    }
                foreach (CharacterRig.Part p in rig.Parts)
                {
                    Console.Write($"  {p.Mesh} nœud {rig.Names[p.Node]} peau {(p.Skin != null ? p.Skin.Bones.Length + " os" : "non")}");
                    if (p.Skin == null)
                    {
                        Console.WriteLine();
                        continue;
                    }
                    float[] pos = new float[p.BasePos.Length];
                    float[] nrm = new float[pos.Length];
                    rig.Deform(p, rig.Pose(null, 0), pos, nrm);
                    float err = 0;
                    for (int i = 0; i < pos.Length; i++)
                        err = MathF.Max(err, MathF.Abs(pos[i] - p.BasePos[i]));
                    Console.Write($" · écart au repos {err:0.00}");
                    if (clip != null)
                        for (float t = 0; t <= clip.Duration; t += clip.Duration / 4)
                        {
                            rig.Deform(p, rig.Pose(clip, t), pos, nrm);
                            float zmin = float.MaxValue, zmax = float.MinValue;
                            for (int i = 2; i < pos.Length; i += 3)
                            {
                                zmin = MathF.Min(zmin, pos[i]);
                                zmax = MathF.Max(zmax, pos[i]);
                            }
                            Console.Write($" · t={t:0.00} z {zmin:0}..{zmax:0}");
                        }
                    Console.WriteLine();
                }
                return 0;
            }
            case "orient":
            {
                // orient <Data> <modèle> <anim> : direction des orteils (avant du personnage) et sens du mouvement des pieds au sol
                SceneAssets assets = new OpenFD.Game.SceneAssets(files);
                CharacterRig rig = OpenFD.Game.CharacterRig.Build(assets, files, args[2])!;
                int I(string n) => Array.FindIndex(rig.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                System.Numerics.Matrix4x4[] w0 = rig.Pose(null, 0);
                Console.WriteLine($"orteil - pied (repos) : {w0[I("Bip01 L Toe0")].Translation - w0[I("Bip01 L Foot")].Translation}");
                AnimClip clip = AnimClip.Parse(files.Read("Gfx\\Anim\\" + args[3] + ".anim"));
                for (float t = 0; t < clip.Duration; t += clip.Duration / 8)
                {
                    System.Numerics.Matrix4x4[] w = rig.Pose(clip, t);
                    Console.WriteLine($"t={t:0.00} pied G {w[I("Bip01 L Foot")].Translation} pied D {w[I("Bip01 R Foot")].Translation} orteil G-pied {w[I("Bip01 L Toe0")].Translation - w[I("Bip01 L Foot")].Translation}");
                }
                return 0;
            }
            case "decor":
            {
                // decor <Data> <Mission> [filtre] : maillages du décor (nom, nombre de sommets, boîte en monde)
                SceneAssets assets = new OpenFD.Game.SceneAssets(files);
                foreach (SceneAssets.Node n in assets.Scenery(args[2]).Where(n => args.Length < 4 || n.Mesh.Contains(args[3], StringComparison.OrdinalIgnoreCase)))
                {
                    SceneAssets.MeshAsset a = assets.LoadMesh(args[2], n.Mesh, null);
                    Mesh m = a.Mesh;
                    System.Numerics.Vector3 mn = new System.Numerics.Vector3(float.MaxValue);
                    System.Numerics.Vector3 mx = new System.Numerics.Vector3(float.MinValue);
                    for (int i = 0; i < m.VertexCount; i++)
                    {
                        System.Numerics.Vector3 v = System.Numerics.Vector3.Transform(new(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]), n.World);
                        mn = System.Numerics.Vector3.Min(mn, v);
                        mx = System.Numerics.Vector3.Max(mx, v);
                    }
                    Console.WriteLine($"{n.Mesh,-40} {m.VertexCount,6} sommets  ({mn.X:0},{mn.Y:0},{mn.Z:0}) – ({mx.X:0},{mx.Y:0},{mx.Z:0})  {string.Join(" ", a.Groups.Select(g => g.Diffuse))}");
                    // FD3_ZONE=x0,y0,x1,y1 : hauteurs du maillage dans cette zone
                    if (Environment.GetEnvironmentVariable("FD3_ZONE") is { } zs)
                    {
                        float[] zb = zs.Split(',').Select(float.Parse).ToArray();
                        List<float> zz = new List<float>();
                        for (int i = 0; i < m.VertexCount; i++)
                        {
                            System.Numerics.Vector3 v = System.Numerics.Vector3.Transform(new(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]), n.World);
                            if (v.X >= zb[0] && v.Y >= zb[1] && v.X <= zb[2] && v.Y <= zb[3])
                                zz.Add(v.Z);
                        }
                        if (zz.Count > 0)
                            Console.WriteLine($"    zone : {zz.Count} sommets, z {zz.Min():0} … {zz.Max():0}, moyenne {zz.Average():0}");
                    }
                }
                return 0;
            }
            case "decorz":
            {
                // decorz <Data> <Mission> <maillage> : nombre de triangles par tranche de hauteur (centre), et horizontaux / verticaux
                SceneAssets assets = new OpenFD.Game.SceneAssets(files);
                SceneAssets.Node n = assets.Scenery(args[2]).FirstOrDefault(n => n.Mesh.Equals(args[3], StringComparison.OrdinalIgnoreCase))
                    ?? new OpenFD.Game.SceneAssets.Node(args[3], Rooms.World(Rooms.SceneNodes(files.Read("Gfx\\Mission\\" + args[2] + "\\" + args[2] + ".sg")), args[3]));
                SceneAssets.MeshAsset a = assets.LoadMesh(args[2], n.Mesh, null);
                Mesh m = a.Mesh;
                SortedDictionary<int, (int h, int v)> hist = new SortedDictionary<int, (int h, int v)>();
                System.Numerics.Vector3 P(uint i) => System.Numerics.Vector3.Transform(new(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]), n.World);
                for (int k = 0; k + 2 < m.Indices.Length; k += 3)
                {
                    System.Numerics.Vector3 p0 = P(m.Indices[k]);
                    System.Numerics.Vector3 p1 = P(m.Indices[k + 1]);
                    System.Numerics.Vector3 p2 = P(m.Indices[k + 2]);
                    System.Numerics.Vector3 nn = System.Numerics.Vector3.Cross(p1 - p0, p2 - p0);
                    bool horiz = MathF.Abs(nn.Z) > 0.9f * nn.Length();
                    int z = (int)((p0.Z + p1.Z + p2.Z) / 3 / 50) * 50;
                    (int h, int v) e = hist.GetValueOrDefault(z);
                    hist[z] = horiz ? (e.h + 1, e.v) : (e.h, e.v + 1);
                }
                foreach ((int z, (int h, int v) e) in hist)
                    Console.WriteLine($"z {z,5} : {e.h,5} horizontaux {e.v,5} verticaux");
                Console.WriteLine($"uv : {string.Join(",", Enumerable.Range(0, 8).Select(i => m.Uv[i] != null ? i.ToString() : "-"))} lightmap {a.Lightmap} ; textures {string.Join(" ", a.Groups.Select(g => g.Diffuse ?? "(aucune)"))} ; {m.VertexCount} sommets");
                return 0;
            }
            case "murs":
            {
                // murs <Data> <Mission> [filtre pièce] : pour chaque pièce, triangles verticaux du décor sur ses bords (± 40)
                Session ses = new Session(files, args[2], "EASY", false);
                SceneAssets assets = new OpenFD.Game.SceneAssets(files);
                List<(System.Numerics.Vector3 a, System.Numerics.Vector3 b, System.Numerics.Vector3 c, string mesh)> tris = new List<(System.Numerics.Vector3 a, System.Numerics.Vector3 b, System.Numerics.Vector3 c, string mesh)>();
                foreach (SceneAssets.Node n in assets.Scenery(args[2]))
                {
                    Mesh m = assets.LoadMesh(args[2], n.Mesh, null).Mesh;
                    System.Numerics.Vector3 P(uint i) => System.Numerics.Vector3.Transform(new(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]), n.World);
                    for (int k = 0; k + 2 < m.Indices.Length; k += 3)
                    {
                        System.Numerics.Vector3 a = P(m.Indices[k]);
                        System.Numerics.Vector3 b = P(m.Indices[k + 1]);
                        System.Numerics.Vector3 c = P(m.Indices[k + 2]);
                        System.Numerics.Vector3 nn = System.Numerics.Vector3.Cross(b - a, c - a);
                        if (nn.Length() < 1 || MathF.Abs(nn.Z) > 0.3f * nn.Length())
                            continue;
                        tris.Add((a, b, c, n.Mesh));
                    }
                }
                foreach (Rooms.Room r in ses.RoomList.Where(r => args.Length < 4 || r.Name.Contains(args[3], StringComparison.OrdinalIgnoreCase)))
                {
                    Dictionary<string, int> hits = new Dictionary<string, int>();
                    foreach ((System.Numerics.Vector3 a, System.Numerics.Vector3 b, System.Numerics.Vector3 c, string mesh) t in tris)
                    {
                        System.Numerics.Vector3 c = (t.a + t.b + t.c) / 3;
                        if (c.Z < r.Min.Z - 50 || c.Z > r.Max.Z + 50)
                            continue;
                        bool onX = (MathF.Abs(c.X - r.Min.X) < 40 || MathF.Abs(c.X - r.Max.X) < 40) && c.Y > r.Min.Y && c.Y < r.Max.Y;
                        bool onY = (MathF.Abs(c.Y - r.Min.Y) < 40 || MathF.Abs(c.Y - r.Max.Y) < 40) && c.X > r.Min.X && c.X < r.Max.X;
                        if (onX || onY)
                            hits[t.mesh] = hits.GetValueOrDefault(t.mesh) + 1;
                    }
                    Console.WriteLine($"{r.Name,-22} ({r.Min.X:0},{r.Min.Y:0},{r.Min.Z:0})-({r.Max.X:0},{r.Max.Y:0},{r.Max.Z:0}) : {string.Join(", ", hits.Select(h => h.Key + "×" + h.Value))}");
                }
                return 0;
            }
            case "zone":
            {
                // zone <Data> <Mission> x0 y0 x1 y1 : triangles verticaux du décor dans la zone, par maillage / texture / hauteur
                SceneAssets assets = new OpenFD.Game.SceneAssets(files);
                float x0 = float.Parse(args[3]), y0 = float.Parse(args[4]), x1 = float.Parse(args[5]), y1 = float.Parse(args[6]);
                SortedDictionary<string, (int n, float zmin, float zmax)> stat = new SortedDictionary<string, (int n, float zmin, float zmax)>();
                foreach (SceneAssets.Node n in assets.Scenery(args[2]))
                {
                    SceneAssets.MeshAsset a = assets.LoadMesh(args[2], n.Mesh, null);
                    Mesh m = a.Mesh;
                    System.Numerics.Vector3 P(uint i) => System.Numerics.Vector3.Transform(new(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]), n.World);
                    foreach (SceneAssets.Group g in a.Groups)
                        for (int k = g.Start; k + 2 < g.Start + g.Count; k += 3)
                        {
                            System.Numerics.Vector3 p0 = P(m.Indices[k]);
                            System.Numerics.Vector3 p1 = P(m.Indices[k + 1]);
                            System.Numerics.Vector3 p2 = P(m.Indices[k + 2]);
                            System.Numerics.Vector3 c = (p0 + p1 + p2) / 3;
                            if (c.X < x0 || c.X > x1 || c.Y < y0 || c.Y > y1)
                                continue;
                            System.Numerics.Vector3 nn = System.Numerics.Vector3.Cross(p1 - p0, p2 - p0);
                            string kind = MathF.Abs(nn.Z) > 0.9f * nn.Length() ? "H" : "V";
                            string key = $"{n.Mesh} {g.Diffuse} alpha={g.Alpha} {kind}";
                            (int n, float zmin, float zmax) e = stat.TryGetValue(key, out (int n, float zmin, float zmax) e0) ? e0 : (n: 0, zmin: float.MaxValue, zmax: float.MinValue);
                            float zl = MathF.Min(p0.Z, MathF.Min(p1.Z, p2.Z)), zh = MathF.Max(p0.Z, MathF.Max(p1.Z, p2.Z));
                            stat[key] = (e.n + 1, MathF.Min(e.zmin, zl), MathF.Max(e.zmax, zh));
                        }
                }
                foreach ((string? k, (int n, float zmin, float zmax) e) in stat)
                    Console.WriteLine($"{k,-70} {e.n,6}  z {e.zmin:0}..{e.zmax:0}");
                return 0;
            }
            case "sgnoeuds":
            {
                // sgnoeuds <Data> <Gfx\…\x.sg> : nœuds d'un .sg (parent, translation locale, boîte du maillage en monde)
                string sgp = args[2];
                string dir = sgp[..sgp.LastIndexOf('\\')] + "\\";
                Dictionary<string, (string parent, System.Numerics.Matrix4x4 m)> nodes = Rooms.SceneNodes(files.Read(sgp));
                foreach ((string? k, (string? par, System.Numerics.Matrix4x4 nm)) in nodes)
                {
                    System.Numerics.Matrix4x4 w = Rooms.World(nodes, k);
                    string box = "";
                    if (files.TryRead(dir + k + ".mesh") is { } md && Rooms.MeshBox(md) is { } mb)
                    {
                        System.Numerics.Vector3 a = System.Numerics.Vector3.Transform(mb.min, w), b = System.Numerics.Vector3.Transform(mb.max, w);
                        box = $" boîte ({a.X:0},{a.Y:0},{a.Z:0})–({b.X:0},{b.Y:0},{b.Z:0})";
                    }
                    Console.WriteLine($"  {k,-36} parent {par,-24} local ({nm.M41:0},{nm.M42:0},{nm.M43:0}) monde ({w.M41:0},{w.M42:0},{w.M43:0}){box}");
                }
                return 0;
            }
            case "noeuds":
            {
                // noeuds <Data> <Mission> : nœuds des .sg de la mission, avec ou sans maillage, et ceux que le décor écarte
                string prefix = "Gfx\\Mission\\" + args[2] + "\\";
                foreach (string sg in files.List(prefix).Where(p => p.EndsWith(".sg", StringComparison.OrdinalIgnoreCase)))
                {
                    Dictionary<string, (string parent, System.Numerics.Matrix4x4 m)> nodes = Rooms.SceneNodes(files.Read(sg));
                    Console.WriteLine($"== {sg} : {nodes.Count} nœuds");
                    foreach ((string? k, (string? par, System.Numerics.Matrix4x4 nm)) in nodes)
                    {
                        if (Environment.GetEnvironmentVariable("FD3_MATRICES") != null)
                            Console.WriteLine($"  {k,-40} local ({nm.M41:0},{nm.M42:0},{nm.M43:0}) diag ({nm.M11:0.00},{nm.M22:0.00},{nm.M33:0.00})");
                        bool mesh = files.TryRead(prefix + k + ".mesh") != null;
                        string up = k.ToUpperInvariant();
                        string why = up.Contains("_ROOM") ? "pièce" : up.Contains("_COLL") ? "collision" : up.Contains("MASK") ? "masque" : !mesh ? "sans maillage" : "affiché";
                        if (args.Length < 4 || why != "affiché")
                            Console.WriteLine($"  {k,-40} parent {par,-20} {why}");
                    }
                }
                return 0;
            }
            case "solz":
            {
                // solz <Data> <Mission> x0 y0 x1 y1 : hauteur du sol de la grille (0 = mur), une ligne par rangée (nord en haut)
                Session ses = new Session(files, args[2], "EASY", false);
                Grid g = ses.Grid!;
                int x0 = int.Parse(args[3]) / 100, y0 = int.Parse(args[4]) / 100, x1 = int.Parse(args[5]) / 100, y1 = int.Parse(args[6]) / 100;
                for (int y = y1; y >= y0; y -= 2)
                    Console.WriteLine($"{y * 100,6} " + string.Concat(Enumerable.Range(x0, x1 - x0 + 1).Where(x => (x - x0) % 2 == 0).Select(x => g.ZoneA[g.Index(x, y)] == 0 ? "  # " : $"{g.GroundHeight[g.Index(x, y)],4:0}")));
                return 0;
            }
            case "materiaux":
            {
                // materiaux <Data> <Mission> <maillage> : matériaux de chaque groupe (toutes leurs textures), uv et couleurs
                string prefix = args[2].StartsWith("Gfx\\", StringComparison.OrdinalIgnoreCase) ? args[2] + "\\" : "Gfx\\Mission\\" + args[2] + "\\";
                Mesh m = Mesh.Load(files.Read(prefix + args[3] + ".mesh"), files.Read(prefix + args[3] + ".meshdata"), args[3]);
                Console.WriteLine($"{m.VertexCount} sommets ; uv {string.Join(",", Enumerable.Range(0, 8).Select(i => m.Uv[i] != null ? i.ToString() : "-"))} ; couleurs {(m.Colors != null ? "oui" : "non")} ; shader « {m.Shader} »");
                if (m.Uv[0] is { } u0)
                    Console.WriteLine($"  uv0 u {Enumerable.Range(0, m.VertexCount).Min(i => u0[2 * i]):0.00}..{Enumerable.Range(0, m.VertexCount).Max(i => u0[2 * i]):0.00} v {Enumerable.Range(0, m.VertexCount).Min(i => u0[2 * i + 1]):0.00}..{Enumerable.Range(0, m.VertexCount).Max(i => u0[2 * i + 1]):0.00} ; x {Enumerable.Range(0, m.VertexCount).Min(i => m.Positions[3 * i]):0}..{Enumerable.Range(0, m.VertexCount).Max(i => m.Positions[3 * i]):0} y {Enumerable.Range(0, m.VertexCount).Min(i => m.Positions[3 * i + 1]):0}..{Enumerable.Range(0, m.VertexCount).Max(i => m.Positions[3 * i + 1]):0} z {Enumerable.Range(0, m.VertexCount).Min(i => m.Positions[3 * i + 2]):0}..{Enumerable.Range(0, m.VertexCount).Max(i => m.Positions[3 * i + 2]):0}");
                if (m.Colors != null)
                    Console.WriteLine($"  alpha des sommets : min {m.Colors.Where((b, i) => i % 4 == 3).Min()} max {m.Colors.Where((b, i) => i % 4 == 3).Max()} ; rvb exemple {m.Colors[0]},{m.Colors[1]},{m.Colors[2]} ; moyenne bvr {m.Colors.Where((b, i) => i % 4 == 0).Average(x => x):0},{m.Colors.Where((b, i) => i % 4 == 1).Average(x => x):0},{m.Colors.Where((b, i) => i % 4 == 2).Average(x => x):0} ; max {m.Colors.Where((b, i) => i % 4 != 3).Max()}");
                foreach ((string material, int start, int count) g in m.Groups)
                {
                    byte[]? mb = files.TryRead(prefix + "Shaders\\" + g.material);
                    Material? mat = mb != null ? Material.Load(mb, g.material) : null;
                    Console.WriteLine($"  {g.material,-28} {g.count / 3,6} tri : {(mat == null ? "(introuvable)" : string.Join(" | ", mat.Textures))}");
                }
                return 0;
            }
            case "foulee":
            {
                // foulee <Data> <classe d'unité> [étiquette] : vitesse naturelle de l'animation (recul du pied posé au sol)
                ClassFile cf = ClassFile.Load(files, "Design\\" + args[2] + ".class");
                string model = cf.Get("DISPLAY", "Model")!.Trim('"', ' ');
                string[] dirs = new[] { "Unit\\Fireman", "Unit\\Civilian" };
                OpenFD.Game.CharacterRig? rig = null;
                foreach (string d in dirs) { try
                {
                    rig = OpenFD.Game.CharacterRig.Build(new OpenFD.Game.SceneAssets(files), files, OpenFD.Game.SceneAssets.GfxPath(d, model));
                    if (rig != null)
                        break;
                } catch { } }
                Dictionary<string, string> tags = OpenFD.Game.CharacterRig.AnimTags(cf);
                Console.WriteLine($"modèle {model}, RunSpeed {cf.GetFloat("MOVING", "RunSpeed", -1)}, WalkSpeed {cf.GetFloat("MOVING", "WalkSpeed", -1)} ; [MOVING] " + string.Join(", ", cf.Section("MOVING").Select(kv => kv.Key + "=" + kv.Value)));
                foreach (string tag in args.Length > 3 ? args.Skip(3) : new[] { "RUN", "WALK" })
                {
                    if (!tags.TryGetValue(tag, out string? file))
                    {
                        Console.WriteLine($"{tag} : absent");
                        continue;
                    }
                    AnimClip clip = AnimClip.Parse(files.Read("Gfx\\Anim\\" + file + ".anim"));
                    List<int> feet = Enumerable.Range(0, rig!.Names.Length).Where(i => System.Text.RegularExpressions.Regex.IsMatch(rig.Names[i], "foot|toe|pied", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
                    Console.WriteLine($"{tag} = {file} : {clip.Duration:0.000} s ; pieds {string.Join(",", feet.Select(i => rig.Names[i]))}");
                    foreach (int fi in feet)
                    {
                        int n = 120;
                        List<System.Numerics.Vector3> pts = new List<System.Numerics.Vector3>();
                        for (int k = 0; k < n; k++)
                        {
                            System.Numerics.Matrix4x4[] w = rig.Pose(clip, clip.Duration * k / n);
                            pts.Add(w[fi].Translation);
                        }
                        float zmin = pts.Min(p => p.Z);
                        // pied au sol : z proche du minimum ; recul pendant l'appui
                        List<(System.Numerics.Vector3 p, int k)> ground = pts.Select((p, k) => (p, k)).Where(x => x.p.Z < zmin + 4).ToList();
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
                Session ses = new Session(files, args[2], "EASY");
                foreach (ExplosiveItem it in ses.Explosions.Items)
                    Console.WriteLine($"{it.Name,-28} vie {it.Life,5:0.#}/{it.MaxLife:0} feu x{it.FireDamageCoef} souffle PV {it.LpPower}/{it.LpRadius} foyers {it.HpPower}/{it.HpRadius} endurance {it.StaminaPower}/{it.StaminaRadius} son {it.Sound} foyers proches {it.NearFires.Count} ({it.Position.X:0},{it.Position.Y:0})");
                int steps = (int)(float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) / Session.Dt);
                for (int i = 0; i < steps; i++)
                {
                    ses.Step();
                    if (i % 300 == 299)
                        Console.WriteLine($"FEUX {ses.Time,5:0} s : {ses.Fire.HotPoints.Count(h => h.IsBurning)} foyers en feu, dont {ses.Fire.HotPoints.Count(h => h.IsBurning && !(h.Position.X > 8400 && h.Position.X < 14500 && h.Position.Y > 5200 && h.Position.Y < 11800))} hors du rectangle 8400-14500 × 5200-11800 ({string.Join(", ", ses.Fire.HotPoints.Where(h => h.IsBurning).GroupBy(h => ses.RoomOf.GetValueOrDefault(h.Id, "")).Select(g => $"{(g.Key == "" ? "dehors" : g.Key)} {g.Count()}"))}), {ses.Explosions.Items.Count(x => x.Exploded)} objets explosés");
                }
                foreach (string l in ses.Explosions.Log)
                    Console.WriteLine(l);
                return 0;
            }
            case "cartefeu":
            {
                // cartefeu <Data> <Mission> <secondes> <sortie.ppm> : grille (blanc ouvert, noir fermé), foyers allumés au départ
                // (rouge), en feu à la fin (orange), objets explosifs (bleu, cyan : explosés)
                Session ses = new Session(files, args[2], "EASY");
                Grid g = ses.Grid!;
                HashSet<uint> start = ses.Fire.HotPoints.Where(h => h.IsBurning).Select(h => h.Id).ToHashSet();
                int steps = (int)(float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) / Session.Dt);
                for (int i = 0; i < steps; i++)
                    ses.Step();
                byte[] px = new byte[g.Width * g.Height * 3];
                for (int k = 0; k < g.Width * g.Height; k++)
                {
                    byte v = g.ZoneA[k] == 0 ? (byte)40 : (byte)200;
                    px[3 * k] = v;
                    px[3 * k + 1] = v;
                    px[3 * k + 2] = v;
                }
                void Dot(System.Numerics.Vector3 p, byte r, byte gg, byte b)
                {
                    (int cx, int cy) = g.CellOf(p);
                    if (!g.Inside(cx, cy))
                        return;
                    int k = ((g.Height - 1 - cy) * g.Width + cx) * 3;
                    px[k] = r;
                    px[k + 1] = gg;
                    px[k + 2] = b;
                }
                // la grille est rangée y croissant : on retourne l'image (nord en haut)
                byte[] flip = new byte[px.Length];
                for (int y = 0; y < g.Height; y++)
                    Array.Copy(px, y * g.Width * 3, flip, (g.Height - 1 - y) * g.Width * 3, g.Width * 3);
                px = flip;
                foreach (OpenFD.Fire.HotPoint h in ses.Fire.HotPoints.Where(h => h.IsBurning))
                    Dot(h.Position, 255, 150, 0);
                foreach (OpenFD.Fire.HotPoint h in ses.Fire.HotPoints.Where(h => start.Contains(h.Id)))
                    Dot(h.Position, 255, 0, 0);
                foreach (ExplosiveItem it in ses.Explosions.Items)
                    Dot(it.Position, 0, it.Exploded ? (byte)220 : (byte)0, 255);
                using (FileStream fs = File.Create(args[4]))
                {
                    byte[] head = System.Text.Encoding.ASCII.GetBytes($"P6 {g.Width} {g.Height} 255\n");
                    fs.Write(head);
                    fs.Write(px);
                }
                Console.WriteLine($"{start.Count} foyers au départ, {ses.Fire.HotPoints.Count(h => h.IsBurning)} en feu à {ses.Time:0} s");
                return 0;
            }
            case "hpbloc":
            {
                // hpbloc <Data> : bloc de 96 octets des paramètres d'instance des foyers, pour toutes les cartes (octets 80 à 95)
                foreach (string lv in files.List("Design\\Mission\\").Where(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase)))
                {
                    Level L;
                    try { L = Level.Parse(files.Read(lv)); } catch { continue; }
                    Dictionary<string, int> hist = new Dictionary<string, int>();
                    foreach (LevelObject o in L.Objects.Where(o => o.Kind == EntityKind.HotPoint))
                    {
                        Component? c = o.GetComponent(0xFE00);
                        if (c == null)
                            continue;
                        byte[] bb = o.ComponentBytes(c).ToArray();
                        int last = -1, prev = -1;
                        for (int i = bb.Length - 4; i >= 0; i--)
                            if (Chunks.IsEnd(bb, i))
                            {
                                if (last < 0)
                                    last = i;
                                else
                                {
                                    prev = i;
                                    break;
                                }
                            }
                        if (last < 0 || prev < 0 || last - (prev + 4) != 96)
                            continue;
                        int t = prev + 4;
                        string key = Convert.ToHexString(bb, t, 34) + " | " + Convert.ToHexString(bb, t + 82, 14);
                        hist[key] = hist.GetValueOrDefault(key) + 1;
                        if ((Environment.GetEnvironmentVariable("FD3_MOTIF") is { } motif && key.Contains(motif)) || (Environment.GetEnvironmentVariable("FD3_BD") == lv.Split('\\')[2] && key.Substring(34, 2) == "01"))
                            Console.WriteLine($"   {o.Name} ({o.Position.X:0},{o.Position.Y:0},{o.Position.Z:0}) {o.ClassName} : {key}");
                    }
                    Console.WriteLine($"== {lv} : {hist.Values.Sum()} foyers ; backdraft : {hist.Where(kv => kv.Key.Length > 36 && kv.Key.Substring(34, 2) == "01").Sum(kv => kv.Value)}");
                    foreach ((string? k, int v) in hist.OrderByDescending(x => x.Value).Take(8))
                        Console.WriteLine($"  {v,5} {k}");
                }
                return 0;
            }
            case "backdraft":
            {
                // backdraft <Data> <Mission> <secondes> [fenetre] : pièces à risque, état des foyers ; à la fin, ouvre une porte (ou casse une fenêtre puis ouvre)
                Session ses = new Session(files, args[2], "EASY");
                int steps = (int)(float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) / Session.Dt);
                for (int i = 0; i < steps; i++)
                {
                    ses.Step();
                    if (i % 200 == 199)
                        foreach (BackdraftRoom r in ses.Units.BackdraftRooms)
                            Console.WriteLine($"  {ses.Time,5:0} s {r.Name} fermée {r.Closed} : " + string.Join(" ", r.Sleepers.Select(h => $"{h.Name}={(h.Lit ? h.Intensity.ToString("0") : "-")}{(h.Sleeping ? "z" : "")}")));
                }
                foreach (BackdraftRoom r in ses.Units.BackdraftRooms)
                {
                    Console.WriteLine($"{r.Name} : ouvertures " + string.Join(", ", r.Openings.Select(o => $"{o.Name}({(o.IsWindow ? "fenêtre" : "porte")},{o.State})")));
                    if (args.Length > 4)
                    {
                        Opening? w = r.Openings.FirstOrDefault(o => o.IsWindow);
                        if (w != null)
                        {
                            ses.Units.SetOpeningState(w, OpeningState.Broken);
                            ses.Step();
                        }
                    }
                    Opening? d = r.Openings.FirstOrDefault(o => !o.IsWindow);
                    if (d != null)
                    {
                        ses.Units.SetOpeningState(d, OpeningState.Open);
                        ses.Step();
                    }
                }
                foreach (string l in ses.Units.Log.Where(l => l.Contains("backdraft", StringComparison.OrdinalIgnoreCase) || l.Contains("couve") || l.Contains("fumée s'échappe")))
                    Console.WriteLine(l);
                return 0;
            }
            case "hauteurs":
            {
                // hauteurs <Data> <Model>… : parties d'un modèle (véhicule) et hauteurs min/max des maillages placés
                SceneAssets assets = new SceneAssets(files);
                foreach (string name in args.Skip(2))
                {
                    string gfx = name.Contains('\\') ? name : SceneAssets.GfxPath("Unit\\FireVehicle", name);
                    float lo = float.MaxValue, hi = float.MinValue;
                    foreach (SceneAssets.Node part in assets.ModelParts(gfx))
                    {
                        Mesh m = assets.LoadMesh("", part.Mesh, "gfx").Mesh;
                        float a0 = float.MaxValue, a1 = float.MinValue;
                        System.Numerics.Vector3 mn = new System.Numerics.Vector3(float.MaxValue), mx = new System.Numerics.Vector3(float.MinValue);
                        for (int i = 0; i < m.VertexCount; i++)
                        {
                            System.Numerics.Vector3 v = System.Numerics.Vector3.Transform(new(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]), part.World);
                            a0 = Math.Min(a0, v.Z);
                            a1 = Math.Max(a1, v.Z);
                            mn = System.Numerics.Vector3.Min(mn, v);
                            mx = System.Numerics.Vector3.Max(mx, v);
                        }
                        lo = Math.Min(lo, a0);
                        hi = Math.Max(hi, a1);
                        Console.WriteLine($"  {part.Mesh} z {a0:0}..{a1:0} x {mn.X:0}..{mx.X:0} y {mn.Y:0}..{mx.Y:0} T {part.World.Translation}");
                    }
                    Console.WriteLine($"{name} ({gfx}) : z {lo:0}..{hi:0}");
                }
                return 0;
            }
            case "utiliser":
            {
                // utiliser <Data> <Mission> : objets utilisables ou à réparer, puis un pompier agit sur le premier
                Session ses = new Session(files, args[2], "EASY");
                void Run(float sec)
                {
                    for (int i = 0; i < (int)(sec / Session.Dt); i++)
                        ses.Step();
                }
                foreach (UsableObject o in ses.Units.Usables)
                    Console.WriteLine($"{o.Name} id {o.Id} utiliser {o.CanBeUsed} réparer {o.CanBeRepaired} actions {string.Join(",", o.Actions.Select(a => a.action + ":" + a.script))}");
                Run(40);
                (UsableObject o, Unit u) pair = (from o in ses.Units.Usables where o.Enabled from u in ses.Units.Units where !u.Hidden && ses.Units.CanWorkOn(u, o)
                            orderby System.Numerics.Vector3.Distance(u.Position, o.Position) select (o, u)).FirstOrDefault();
                UsableObject obj = pair.o;
                Unit man = pair.u;
                if (obj == null || man == null)
                {
                    Console.WriteLine("aucun objet ou pompier");
                    return 0;
                }
                ses.Units.UseObject(man, obj);
                Console.WriteLine($"{man.Name} → {obj.Name} : {man.Status}");
                for (int k = 0; k < 10 && man.UseTarget != null; k++)
                {
                    Run(3);
                    Console.WriteLine($"  {ses.Time:0} s : {man.Status} pos ({man.Position.X:0},{man.Position.Y:0}) obj ({obj.Position.X:0},{obj.Position.Y:0}) chemin {man.Path.Count}");
                }
                foreach (string l in ses.Units.Log.TakeLast(3))
                    Console.WriteLine(l);
                foreach (string l in ses.Scripts!.Log.TakeLast(3))
                    Console.WriteLine("script : " + l);
                return 0;
            }
            case "foyerspiece":
            {
                // foyerspiece <Data> <Mission> [filtre] : foyers (position, pièce) et carte des cases autour du premier
                Session ses = new Session(files, args[2], "EASY");
                List<Fire.HotPoint> sel = ses.Fire.HotPoints.Where(h => args.Length < 4 || (ses.RoomOf.TryGetValue(h.Id, out string? r) && r.Contains(args[3], StringComparison.OrdinalIgnoreCase)) || h.Name.Contains(args[3], StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (Fire.HotPoint h in sel.Take(30))
                    Console.WriteLine($"{h.Name} ({h.Position.X:0},{h.Position.Y:0},{h.Position.Z:0}) pièce {(ses.RoomOf.TryGetValue(h.Id, out string? r2) ? r2 : "-")}");
                if (sel.Count > 0)
                {
                    System.Numerics.Vector3 c = sel[0].Position;
                    Console.WriteLine(ses.Units.AsciiMap((int)c.X - 900, (int)c.Y - 900, (int)c.X + 900, (int)c.Y + 900));
                }
                return 0;
            }
            case "toutesmissions":
            {
                // toutesmissions <Data> [minutes] : chaque mission jouée sans affichage ; erreurs et fonctions inconnues de ses scripts
                float minutes = args.Length > 2 ? float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 10;
                Dictionary<string, (int n, SortedSet<string> where)> total = new Dictionary<string, (int n, SortedSet<string> where)>(StringComparer.OrdinalIgnoreCase);
                foreach (string dir in Directory.GetDirectories(Path.Combine(args[1], "Script", "Mission")).OrderBy(d => d))
                {
                    string mission = Path.GetFileName(dir);
                    try
                    {
                        Session ses = new Session(files, mission, "EASY");
                        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                        int steps = (int)(minutes * 60 / Session.Dt);
                        string? crash = null;
                        for (int i = 0; i < steps; i++)
                        {
                            try { ses.Step(); } catch (Exception e)
                            {
                                crash = e.GetType().Name + " : " + e.Message;
                                break;
                            }
                            if (ses.Host?.Cinematic == true && i % 50 == 0)
                                ses.Host.EndCinematic();
                            if (ses.Host?.Outcome != null)
                                break;
                        }
                        Script.Interpreter sc = ses.Scripts!;
                        MissionHost h = ses.Host!;
                        Console.WriteLine($"== {mission} : {ses.Time / 60:0.0} min simulées en {sw.Elapsed.TotalSeconds:0} s · unités {ses.Units.Units.Count} · objectifs {h.Objectives.Count} ({string.Join(" ", h.Objectives.Select(o => o.Status))}) · issue {(h.Outcome == null ? "en cours" : h.Outcome == true ? "victoire" : "défaite")}");
                        if (crash != null)
                            Console.WriteLine("   PLANTAGE : " + crash);
                        if (h.Outcome == false)
                        {
                            Console.WriteLine($"   défaite : {h.DebriefText}");
                            foreach (string l in h.FireLog.TakeLast(6))
                                Console.WriteLine("   journal : " + l);
                        }
                        foreach (string l in sc.Log.Distinct().Take(8))
                            Console.WriteLine("   script : " + l);
                        if (sc.UnknownCalls.Count > 0)
                            Console.WriteLine("   inconnues : " + string.Join(", ", sc.UnknownCalls.OrderByDescending(k => k.Value).Select(k => $"{k.Key}×{k.Value}")));
                        foreach ((string? k, int v) in sc.UnknownCalls)
                        {
                            if (!total.TryGetValue(k, out (int n, SortedSet<string> where) t))
                                t = (0, new SortedSet<string>());
                            t.where.Add(mission);
                            total[k] = (t.n + v, t.where);
                        }
                    }
                    catch (Exception e) { Console.WriteLine($"== {mission} : impossible de charger ({e.GetType().Name} : {e.Message})"); }
                }
                Console.WriteLine("== Total des fonctions inconnues appelées :");
                foreach ((string? k, (int n, SortedSet<string> where) t) in total.OrderByDescending(x => x.Value.n))
                    Console.WriteLine($"   {k,-32} {t.n,6}  {string.Join(", ", t.where)}");
                return 0;
            }
            case "depart":
            {
                // depart <Data> <Mission> : équipes de départ et renforts prévus
                Session ses = new Session(files, args[2], "EASY");
                Console.WriteLine($"unités {ses.Units.Units.Count} · pompiers comptés {ses.Units.FiremenCount} · attendus {ses.Units.DeferredFiremen}");
                foreach (string l in ses.Units.Log.Take(20))
                    Console.WriteLine("  " + l);
                for (int i = 0; i < 600; i++)
                    ses.Step();
                Console.WriteLine($"après 60 s : unités {ses.Units.Units.Count} · pompiers {ses.Units.FiremenCount}");
                foreach (string l in ses.Units.Log.Skip(0).Take(30))
                    Console.WriteLine("  " + l);
                return 0;
            }
            case "animtags":
            {
                // animtags <Data> <classe relative à Design> : étiquettes d'animation [ANIM_PLAYER]
                foreach ((string? k, string? v) in CharacterRig.AnimTags(ClassFile.Load(files, args[2])))
                    Console.WriteLine($"{k,-20} {v}");
                return 0;
            }
            case "duree":
            {
                // duree <Data> <anim>… : durée des animations (Gfx\Anim\<anim>.anim)
                foreach (string n in args.Skip(2))
                {
                    byte[]? d = files.TryRead(n.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) ? n : "Gfx\\Anim\\" + n + ".anim");
                    Console.WriteLine($"{n} : {(d != null ? AnimClip.Parse(d).Duration.ToString("0.00") + " s" : "introuvable")}");
                    if (d != null && Environment.GetEnvironmentVariable("FD3_PISTES") == "1")
                        foreach (KeyValuePair<string, AnimClip.Track> kv in AnimClip.Parse(d).Tracks)
                            Console.WriteLine($"   {kv.Key} : {kv.Value.Pos.Length} pos, {kv.Value.Rot.Length} rot");
                }
                return 0;
            }
            case "porter":
            {
                // porter <Data> <Mission> : un pompier va chercher un civil inconscient, le porte et le dépose à l'ambulance
                Session ses = new Session(files, args[2], "EASY");
                void Run(float sec)
                {
                    for (int i = 0; i < (int)(sec / Session.Dt); i++)
                        ses.Step();
                }
                Run(40);
                Unit? man = ses.Units.Units.FirstOrDefault(u => !u.IsVehicle && !u.CanHeal && !u.Hidden && !u.OnBoard);
                System.Numerics.Vector2 me = man == null ? default : new System.Numerics.Vector2(man.Position.X, man.Position.Y);
                Civilian? civ = ses.Units.Civilians.Where(c => c.State == CivilianState.Unconscious && !c.Hidden && ses.Units.CanReach(me, new(c.Position.X, c.Position.Y)))
                    .OrderBy(c => System.Numerics.Vector2.Distance(me, new(c.Position.X, c.Position.Y))).FirstOrDefault();
                if (man == null || civ == null)
                {
                    Console.WriteLine("pas de pompier ou de civil inconscient atteignable");
                    return 1;
                }
                ses.Units.FetchCivilian(man, civ);
                string last = "";
                bool moveAmb = args.Length > 3 && args[3] == "bouge";   // porter <Data> <Mission> bouge : l'ambulance part pendant le trajet
                for (int t = 0; t < 240 && civ.State != CivilianState.Rescued; t++)
                {
                    Run(1);
                    if (moveAmb && man.Carry == CarryPhase.Walking && man.GoToVehicle is { } amb)
                    {
                        moveAmb = false;
                        foreach (System.Numerics.Vector2 d in new[] { new System.Numerics.Vector2(2500, 0), new System.Numerics.Vector2(-2500, 0), new System.Numerics.Vector2(0, 2500), new System.Numerics.Vector2(0, -2500) })
                            if (ses.Units.Drive(amb, new System.Numerics.Vector2(amb.Position.X, amb.Position.Y) + d))
                            {
                                Console.WriteLine($"{ses.Time,6:0} s ambulance ({amb.Position.X:0},{amb.Position.Y:0}) envoyée vers ({amb.Path.Last().X:0},{amb.Path.Last().Y:0})");
                                break;
                            }
                    }
                    string line = $"{man.Status} · {man.Carry}";
                    if (line != last)
                    {
                        Console.WriteLine($"{ses.Time,6:0} s ({man.Position.X:0},{man.Position.Y:0}) {line}");
                        last = line;
                    }
                }
                Console.WriteLine($"civil : {civ.State}");
                return civ.State == CivilianState.Rescued ? 0 : 2;
            }
            case "chercherclasse":
            {
                // chercherclasse <Data> <motif> : classes de Design dont le texte contient le motif (expression régulière)
                System.Text.RegularExpressions.Regex rx = new System.Text.RegularExpressions.Regex(args[2], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (string f in files.List("Design\\").Where(f => f.EndsWith(".class", StringComparison.OrdinalIgnoreCase)))
                {
                    string t = System.Text.Encoding.Latin1.GetString(files.Read(f));
                    foreach (System.Text.RegularExpressions.Match m in rx.Matches(t))
                        Console.WriteLine($"{f} : {m.Value.Trim()}");
                }
                return 0;
            }
            case "zones":
            {
                // zones <Data> <Mission> : valeurs des zones A et B de la grille (combien de cases, et une case exemple)
                Session ses = new Session(files, args[2], "EASY", false);
                Grid g = ses.Grid!;
                foreach ((string? name, ushort[]? arr) in new[] { ("A", g.ZoneA), ("B", g.ZoneB) })
                    foreach (IGrouping<ushort, int> grp in Enumerable.Range(0, arr.Length).GroupBy(i => arr[i]).OrderBy(x => x.Key))
                    {
                        int i = grp.First();
                        Console.WriteLine($"zone {name} {grp.Key,6} (0x{grp.Key:X4}) : {grp.Count(),7} case(s), ex. ({(i % g.Width) * 100 + 50},{(i / g.Width) * 100 + 50})");
                    }
                return 0;
            }
            case "interrupteur":
            {
                // interrupteur <Data> <Mission> <objet> <porte> : un pompier utilise l'objet ; état de la porte avant et après
                Session ses = new Session(files, args[2], "EASY");
                void Run(float sec)
                {
                    for (int i = 0; i < (int)(sec / Session.Dt); i++)
                        ses.Step();
                }
                Run(40);
                UsableObject obj = ses.Units.Usables.First(o => o.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
                Opening door = ses.Units.Openings.First(o => o.Name.Equals(args[4], StringComparison.OrdinalIgnoreCase));
                Unit man = ses.Units.Units.First(u => !u.IsVehicle && !u.Hidden && !u.OnBoard && ses.Units.CanWorkOn(u, obj));
                Console.WriteLine($"porte {door.Name} : {door.State}");
                for (int k = 0; k < 2; k++)
                {
                    man.Position = new System.Numerics.Vector3(obj.Position.X + 100, obj.Position.Y, obj.Position.Z);
                    ses.Units.UseObject(man, obj);
                    for (int t = 0; t < 30 && man.UseTarget != null; t++)
                        Run(1);
                    Console.WriteLine($"après utilisation {k + 1} : porte {door.State} · {man.Status}");
                }
                return 0;
            }
            case "pistes":
            {
                // pistes <Data> <anim> : durée et étendue des déplacements de chaque piste (Gfx\Anim\<anim>.anim)
                AnimClip c = AnimClip.Parse(files.Read("Gfx\\Anim\\" + args[2] + ".anim"));
                Console.WriteLine($"durée {c.Duration:0.00} s, {c.Tracks.Count} pistes");
                foreach ((string? n, AnimClip.Track? t) in c.Tracks.Take(int.TryParse(args.ElementAtOrDefault(3), out int k) ? k : 6))
                    if (t.Pos.Length > 0)
                        Console.WriteLine($"  {n} : {t.Pos.Length} clés, de {t.Pos.First().v} à {t.Pos.Last().v} ; min {t.Pos.Select(p => p.v).Aggregate(System.Numerics.Vector3.Min)} max {t.Pos.Select(p => p.v).Aggregate(System.Numerics.Vector3.Max)}");
                foreach ((string? n, AnimClip.Track? t) in c.Tracks.Where(x => x.Value.Rot.Length > 0).Take(4))
                    Console.WriteLine($"  {n} : rotation de {t.Rot.First().v} à {t.Rot.Last().v}");
                return 0;
            }
            case "carte":
            {
                // carte <Data> <Mission> x0 y0 x1 y1 [secondes] : cases de la grille (# mur, m cloison, x bloqué, o zone
                // d'occupation, D porte fermée, d porte), après quelques secondes de jeu si on le demande
                Session ses = new Session(files, args[2], "EASY");
                float wait = args.Length > 7 ? float.Parse(args[7]) : 0;
                while (ses.Time < wait)
                    ses.Step();
                Console.WriteLine(ses.Units.AsciiMap(int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]), int.Parse(args[6])));
                return 0;
            }
            case "grappin":
            {
                // grappin <Data> <Mission> : passages au grappin ; un pompier ordinaire puis le scout essaient de passer le mur
                Session ses = new Session(files, args[2], "EASY");
                void Run(float sec)
                {
                    for (int i = 0; i < (int)(sec / Session.Dt); i++)
                        ses.Step();
                }
                foreach (GrapplePoint gp in ses.Units.Grapples)
                    Console.WriteLine($"passage {gp.Id} : haut {gp.Top}, cases {gp.Cells.Count}, côtés {gp.SideA} / {gp.SideB}");
                GrapplePoint? g0 = ses.Units.Grapples.FirstOrDefault();
                if (g0 == null)
                {
                    Console.WriteLine("aucun passage");
                    return 1;
                }
                Run(40);
                List<Unit> men = ses.Units.Units.Where(u => !u.IsVehicle && !u.Hidden && !u.OnBoard).ToList();
                Console.WriteLine("unités : " + string.Join(", ", men.Select(u => $"{u.ClassPath}{(u.CanGrapple ? " (grappin)" : "")}")));
                foreach (Unit? u in new[] { men.FirstOrDefault(m => !m.CanGrapple), men.FirstOrDefault(m => m.CanGrapple) })
                {
                    if (u == null)
                        continue;
                    // posé du côté A, envoyé du côté B
                    u.Position = new System.Numerics.Vector3(g0.SideA, 0);
                    bool ok = u.CanGrapple ? ses.Units.GrappleOver(u, g0) : ses.Units.Order(u, g0.SideB);
                    Console.WriteLine($"{u.ClassPath} : ordre {ok} · {u.Status} · chemin {u.Path.Count}");
                    string last = "";
                    for (int t = 0; t < 60; t++)
                    {
                        Run(0.5f);
                        string l = $"{u.Status} {u.Climb}";
                        if (l != last)
                        {
                            Console.WriteLine($"   {t * 0.5f,5:0.0} s ({u.Position.X:0},{u.Position.Y:0},{u.Position.Z:0}) {l}");
                            last = l;
                        }
                        if (!u.Moving && u.Climb == OpenFD.Game.ClimbPhase.None && t > 2)
                            break;
                    }
                    Console.WriteLine($"   arrivé à {System.Numerics.Vector2.Distance(new(u.Position.X, u.Position.Y), g0.SideB):0} du côté B");
                }
                return 0;
            }
            case "portailcases":
            {
                // portailcases <Data> <Mission> <porte> : cases du portail (D fermé, o libre une fois ouvert, B bloqué ouvert)
                Session ses = new Session(files, args[2], "EASY", false);
                Opening d = ses.Units.Openings.First(o => o.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
                Grid g = ses.Grid!;
                List<int> all = d.Cells.Concat(d.OpenBlocked).Concat(d.FrameCells).ToList();
                int x0 = all.Min(i => i % g.Width) - 2, x1 = all.Max(i => i % g.Width) + 2, y0 = all.Min(i => i / g.Width) - 2, y1 = all.Max(i => i / g.Width) + 2;
                Console.WriteLine($"{d.Name} : x {x0 * 100}..{x1 * 100}, y {y0 * 100}..{y1 * 100} ; boîte {d.BoxMin} {d.BoxMax}");
                for (int y = y1; y >= y0; y--)
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    for (int x = x0; x <= x1; x++)
                    {
                        int i = g.Index(x, y);
                        sb.Append(d.FrameCells.Contains(i) ? 'F' : d.OpenBlocked.Contains(i) ? (d.Cells.Contains(i) ? 'B' : 'b') : d.Cells.Contains(i) ? 'o' : g.ZoneA[i] == 0 ? '#' : '.');
                    }
                    Console.WriteLine(sb);
                }
                return 0;
            }
            case "zonescarte":
            {
                // zonescarte <Data> <Mission> x0 y0 x1 y1 [B] : identifiant de zone de chaque case (une lettre par zone)
                Session ses = new Session(files, args[2], "EASY", false);
                Grid g = ses.Grid!;
                ushort[] arr = args.Length > 7 && args[7] == "B" ? g.ZoneB : g.ZoneA;
                Dictionary<ushort, char> letters = new Dictionary<ushort, char>();
                const string L = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
                for (int y = int.Parse(args[6]) / 100; y >= int.Parse(args[4]) / 100; y--)
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    for (int x = int.Parse(args[3]) / 100; x <= int.Parse(args[5]) / 100; x++)
                    {
                        ushort z = arr[g.Index(x, y)];
                        if (z == 0)
                        {
                            sb.Append('#');
                            continue;
                        }
                        if (!letters.TryGetValue(z, out char ch))
                            letters[z] = ch = L[letters.Count % L.Length];
                        sb.Append(ch);
                    }
                    Console.WriteLine(sb);
                }
                Console.WriteLine(string.Join(" ", letters.Select(kv => $"{kv.Value}={kv.Key}")));
                return 0;
            }
            case "zoneb0":
            {
                // zoneb0 <Data> : par mission, cases zone A ≠ 0 et zone B = 0 ; plus grand groupe d'un seul tenant
                foreach (string path in files.List("Design\\Mission\\").Where(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase)))
                {
                    string name = path.Split('\\')[2];
                    Grid? g;
                    try { g = new Session(files, name, "EASY", false).Grid; } catch { continue; }
                    if (g == null)
                        continue;
                    HashSet<int> set = new HashSet<int>(Enumerable.Range(0, g.Width * g.Height).Where(i => g.ZoneA[i] != 0 && g.ZoneB[i] == 0));
                    int best = 0, comps = 0;
                    HashSet<int> seen = new HashSet<int>();
                    int bestAt = -1;
                    foreach (int st in set)
                    {
                        if (!seen.Add(st))
                            continue;
                        comps++;
                        Queue<int> q = new Queue<int>();
                        q.Enqueue(st);
                        int n = 0;
                        while (q.Count > 0)
                        {
                            int c = q.Dequeue();
                            n++;
                            int x = c % g.Width, y = c / g.Width;
                            foreach ((int dx, int dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                            {
                                if (!g.Inside(x + dx, y + dy))
                                    continue;
                                int k = g.Index(x + dx, y + dy);
                                if (set.Contains(k) && seen.Add(k))
                                    q.Enqueue(k);
                            }
                        }
                        if (n > best)
                        {
                            best = n;
                            bestAt = st;
                        }
                    }
                    int total = Enumerable.Range(0, g.Width * g.Height).Count(i => g.ZoneA[i] != 0);
                    Console.WriteLine($"{name,-14} {set.Count,6} cases B0 / {total} ; {comps} groupes, le plus grand {best} vers ({(bestAt % g.Width) * 100},{(bestAt / g.Width) * 100})");
                }
                return 0;
            }
            case "atteignable":
            {
                // atteignable <Data> <Mission> : toutes portes ouvertes, cases atteignables depuis le départ des pompiers ; pièces (zone B) jamais atteintes
                Session ses = new Session(files, args[2], "EASY");
                for (int i = 0; i < (int)(40 / Session.Dt); i++)
                    ses.Step();
                foreach (Opening d in ses.Units.Openings)
                    ses.Units.SetOpeningState(d, OpenFD.Game.OpeningState.Open);
                Grid g = ses.Grid!;
                Unit u0 = ses.Units.Units.First(u => !u.IsVehicle && !u.Hidden && !u.OnBoard);
                (int sx, int sy) = g.CellOf(u0.Position);
                bool[] seen = new bool[g.Width * g.Height];
                Queue<(int, int)> q = new Queue<(int, int)>();
                q.Enqueue((sx, sy));
                seen[g.Index(sx, sy)] = true;
                while (q.Count > 0) { (int x, int y) = q.Dequeue();
                    foreach ((int dx, int dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = x + dx, ny = y + dy;
                    if (!g.Inside(nx, ny) || seen[g.Index(nx, ny)] || !ses.Units.WalkableCell(nx, ny))
                        continue;
                    seen[g.Index(nx, ny)] = true;
                    q.Enqueue((nx, ny));
                } }
                List<IGrouping<ushort, int>> rooms = Enumerable.Range(0, seen.Length).Where(i => g.ZoneB[i] != 0 && g.ZoneA[i] != 0).GroupBy(i => g.ZoneB[i]).ToList();
                List<IGrouping<ushort, int>> miss = rooms.Where(r => !r.Any(i => seen[i])).ToList();
                Console.WriteLine($"{args[2]} : {seen.Count(b => b)} cases atteintes ; {rooms.Count} zones B, {miss.Count} jamais atteintes : " + string.Join(" ", miss.Select(r => $"{r.Key}({r.Count()}c @{(r.First() % g.Width) * 100},{(r.First() / g.Width) * 100})").Take(25)));
                return 0;
            }
            case "journalscript":
            {
                // journalscript <Data> <Mission> <secondes> : erreurs des scripts et objectifs pendant la partie
                Session ses = new Session(files, args[2], "EASY");
                for (int i = 0; i < (int)(float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) / Session.Dt); i++)
                {
                    ses.Step();
                    if (ses.Host?.Cinematic == true && i % 50 == 0)
                        ses.Host.EndCinematic();
                }
                foreach (string l in ses.Scripts!.Log)
                    Console.WriteLine("script : " + l);
                foreach (OpenFD.Game.MissionHost.Objective o in ses.Host!.Objectives)
                    Console.WriteLine($"objectif : {o.Text} {o.Status}");
                // journalscript … <secondes> "<expression de script>" : valeur d'une expression à la fin
                if (args.Length > 4)
                {
                    ses.Scripts.Load(OpenFD.Script.Parser.ParseFile("float __Eval() { return " + args[4] + "; }", "eval"));
                    Console.WriteLine($"{args[4]} = {ses.Scripts.TryRun("__Eval")?.AsNum}");
                    foreach (string l in ses.Scripts.Log)
                        Console.WriteLine("script : " + l);
                }
                Console.WriteLine("texte : " + OpenFD.Game.Localization.Load(files.DataDir).Get("&Tuto_Texte_3_1_1"));
                return 0;
            }
            case "traverse":
            {
                // traverse <Data> <Mission> <porte> x y : la porte s'ouvre, tous les pompiers vont en (x,y) ; cases interdites traversées
                Session ses = new Session(files, args[2], "EASY");
                void Run(float sec)
                {
                    for (int i = 0; i < (int)(sec / Session.Dt); i++)
                        ses.Step();
                }
                Run(40);
                Opening door = ses.Units.Openings.First(o => o.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
                if (args.Length > 6)
                {
                    // ouverture par le mécanisme, comme en jeu (scout posé à côté)
                    Unit sc = ses.Units.Units.First(u => u.CanGrapple);
                    if (door.Lever is { } lv)
                        sc.Position = new System.Numerics.Vector3(lv, 0);
                    ses.Units.WorkOnOpening(sc, door);
                    for (int t = 0; t < 60 && sc.Opening != null; t++)
                        Run(1);
                    Run(5);
                    Console.WriteLine($"porte {door.State} · {string.Join(", ", ses.Units.Openings.Where(o => o.State != OpenFD.Game.OpeningState.Closed).Select(o => o.Name + " " + o.State))}");
                }
                else
                    ses.Units.SetOpeningState(door, OpenFD.Game.OpeningState.Open);
                Grid g = ses.Grid!;
                System.Numerics.Vector2 dest = new System.Numerics.Vector2(float.Parse(args[4]), float.Parse(args[5]));
                List<Unit> men = ses.Units.Units.Where(u => !u.IsVehicle && !u.Hidden && !u.OnBoard && !u.Unconscious).ToList();
                foreach (Unit u in men)
                    Console.WriteLine($"{u.ClassPath} ({u.Position.X:0},{u.Position.Y:0}) ordre {ses.Units.Order(u, dest)} " + string.Join(" → ", u.Path.Select(p => $"({p.X:0},{p.Y:0})")));
                Dictionary<string, List<string>> bad = new Dictionary<string, List<string>>();
                for (int t = 0; t < 1200; t++)
                {
                    ses.Step();
                    foreach (Unit u in men)
                    {
                        if (u.Climb != OpenFD.Game.ClimbPhase.None)
                            continue;
                        (int cx, int cy) = g.CellOf(u.Position);
                        if (!ses.Units.WalkableCell(cx, cy))
                        {
                            if (!bad.TryGetValue(u.Name, out List<string>? l))
                                bad[u.Name] = l = new();
                            string s2 = $"({u.Position.X:0},{u.Position.Y:0})";
                            if (l.Count < 8 && !l.Contains(s2))
                                l.Add(s2);
                        }
                    }
                }
                foreach (Unit u in men)
                    Console.WriteLine($"{u.Name} fin ({u.Position.X:0},{u.Position.Y:0}) {u.Status} · hors grille : {(bad.TryGetValue(u.Name, out List<string>? l2) ? string.Join(" ", l2) : "-")}");
                return 0;
            }
            case "portail":
            {
                // portail <Data> <Mission> <porte> : un pompier va ouvrir la porte ; où il s'arrête (mécanisme)
                Session ses = new Session(files, args[2], "EASY");
                void Run(float sec)
                {
                    for (int i = 0; i < (int)(sec / Session.Dt); i++)
                        ses.Step();
                }
                Run(40);
                Opening door = ses.Units.Openings.First(o => o.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
                Unit man = ses.Units.Units.First(u => !u.IsVehicle && !u.Hidden && !u.OnBoard && u.CanUse);
                Console.WriteLine($"porte {door.Name} {door.State} centre {door.Center} mécanisme {door.Lever}");
                if (args.Length > 5)
                    man.Position = new System.Numerics.Vector3(float.Parse(args[4]), float.Parse(args[5]), 0);   // départ imposé
                ses.Units.WorkOnOpening(man, door);
                for (int t = 0; t < 60 && man.Opening != null; t++)
                    Run(1);
                Console.WriteLine($"pompier arrêté en ({man.Position.X:0},{man.Position.Y:0}) · porte {door.State} · {man.Status}");
                return 0;
            }
            case "portesanim":
            {
                // portesanim <Data> <Mission> : portes avec une animation d'ouverture, et les pièces du modèle qu'elle anime
                Session ses = new Session(files, args[2], "EASY", false);
                SceneAssets assets = new OpenFD.Game.SceneAssets(files);
                foreach (Opening d in ses.Units.Openings.Where(d => d.OpenAnim != null || d.CloseAnim != null).Take(200))
                {
                    AnimClip? clip = files.TryRead("Gfx\\Anim\\" + (d.OpenAnim ?? d.CloseAnim) + ".anim") is { } b ? AnimClip.Parse(b) : null;
                    List<string> parts = d.Model == null ? new List<string>() : assets.ModelParts(d.Model).Select(n => n.Mesh[(n.Mesh.LastIndexOf('\\') + 1)..]).ToList();
                    Console.WriteLine($"{d.Name} : {d.OpenAnim} / {d.CloseAnim} · pistes {(clip == null ? "?" : string.Join(",", clip.Tracks.Keys))} · pièces {string.Join(",", parts)}");
                }
                return 0;
            }
            case "arroser":
            {
                // arroser <Data> <Mission> : un pompier reçoit l'ordre d'arroser des feux d'intérieur ; d'où arrosera-t-il ?
                Session ses = new Session(files, args[2], "EASY");
                for (int i = 0; i < 400; i++)
                {
                    ses.Step();
                    if (i % 50 == 0)
                        ses.Host?.EndCinematic();
                }
                Unit? u = ses.Units.Units.FirstOrDefault(x => !x.IsVehicle && !x.OnBoard && x.Nozzle != null && !x.IsCanon);
                if (u == null)
                    return 1;
                System.Numerics.Vector3 start = u.Position;
                foreach (OpenFD.Fire.HotPoint h in ses.Fire.HotPoints.Where(h => h.IsBurning && ses.RoomOf.ContainsKey(h.Id)).Take(6))
                {
                    u.Position = start;
                    u.Path.Clear();
                    u.Target = null;
                    ses.Units.Attack(u, h);
                    System.Numerics.Vector2 end = u.Path.Count > 0 ? u.Path.Last() : new(u.Position.X, u.Position.Y);
                    bool wall = ses.Units.WallBetween(end, new(h.Position.X, h.Position.Y));
                    System.Numerics.Vector2 f2 = new(h.Position.X, h.Position.Y), s2 = new(start.X, start.Y);
                    System.Numerics.Vector2 old = f2 + System.Numerics.Vector2.Normalize(s2 - f2) * System.Numerics.Vector2.Distance(end, f2);
                    Console.Write($"  (ancienne place ({old.X:0},{old.Y:0}) : mur entre {ses.Units.WallBetween(old, f2)})  ");
                    Console.WriteLine($"{h.Name,-22} pièce {ses.RoomOf[h.Id],-22} → arrose depuis ({end.X:0},{end.Y:0}) à {System.Numerics.Vector2.Distance(end, new(h.Position.X, h.Position.Y)):0} · mur entre : {wall} · {u.Status}");
                }
                return 0;
            }
            case "renforts":
            {
                // renforts <Data> <Mission> [équipe choisie] : renforts accordés, choix, arrivée
                Session ses = new Session(files, args[2], "EASY");
                string? pick = args.Length > 3 ? args[3] : null;
                bool chosen = false;
                for (int i = 0; i <= 3000; i++)
                {
                    if (i % 50 == 0)
                        ses.Host?.EndCinematic();
                    if (ses.Units.GrantsAvailable > 0 && !chosen && pick != null)
                    {
                        Console.WriteLine($"t {ses.Time:0} : à choisir {string.Join("/", ses.Units.ChoosableTeams)} ; aperçu {pick} : {string.Join(", ", ses.Units.TeamComposition(pick).Select(c => c.cls + (c.icon != null ? $" [{c.icon}]" : "")))}");
                        chosen = ses.Units.ChooseReinforcement(pick);
                    }
                    if (i % 200 == 0)
                        Console.WriteLine($"t {ses.Time,5:0} · accordés {ses.Units.GrantsAvailable} · " + string.Join(" · ", ses.Units.Teams.Select(t => $"{t} : {ses.Units.Units.Count(u => u.TeamName == t)} unité(s){(ses.Units.IncomingProgress(t) is { } p ? $" en approche {p:P0}" : "")}")));
                    ses.Step();
                }
                foreach (string l in ses.Units.Log.Where(l => l.Contains("renfort") || l.Contains("équipe")))
                    Console.WriteLine("  journal : " + l.Trim());
                return 0;
            }
            case "eaux":
            {
                // eaux <Data> <Mission> : maillages d'eau du décor (shader « FD2Water ») et leur étendue dans le monde
                SceneAssets assets = new SceneAssets(files);
                foreach (SceneAssets.Node n in assets.Scenery(args[2]))
                {
                    SceneAssets.MeshAsset a;
                    try { a = assets.LoadMesh(args[2], n.Mesh, null); } catch { continue; }
                    if (!a.Mesh.Shader.Contains("Water", StringComparison.OrdinalIgnoreCase))
                        continue;
                    System.Numerics.Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
                    for (int i = 0; i < a.Mesh.VertexCount; i++)
                    {
                        System.Numerics.Vector3 v = System.Numerics.Vector3.Transform(new(a.Mesh.Positions[3 * i], a.Mesh.Positions[3 * i + 1], a.Mesh.Positions[3 * i + 2]), n.World);
                        mn = System.Numerics.Vector3.Min(mn, v);
                        mx = System.Numerics.Vector3.Max(mx, v);
                    }
                    Console.WriteLine($"{n.Mesh} : x {mn.X:0}..{mx.X:0} y {mn.Y:0}..{mx.Y:0} z {mn.Z:0}..{mx.Z:0} ({a.Mesh.Indices.Length / 3} triangles)");
                }
                return 0;
            }
            case "bateau":
            {
                // bateau <Data> <Mission> x y dx dy : un bateau-pompe posé en (x, y), envoyé en (dx, dy) ; puis un pompier envoyé sur l'eau
                Session ses = new Session(files, args[2], "EASY");
                Console.WriteLine(string.Join(" · ", ses.Units.Log.Where(l => l.StartsWith("Eau"))));
                for (int i = 0; i < 300; i++)
                    ses.Step();
                Unit boat = ses.Units.SpawnScripted("Data/Design/Unit/Firevehicle/Veh_P_fireboat.class", "bateau", new System.Numerics.Vector3(float.Parse(args[3]), float.Parse(args[4]), 0))!;
                Console.WriteLine($"bateau {boat.IsBoat} vitesse {boat.Speed:0} accélération {boat.Accel:0} lance {boat.Nozzle?.RangeMax} en ({boat.Position.X:0},{boat.Position.Y:0},{boat.Position.Z:0})");
                ses.Units.PlayerCommand = true;
                bool ok = ses.Units.Drive(boat, new(float.Parse(args[5]), float.Parse(args[6])));
                Console.WriteLine($"ordre : {ok} {boat.Status} chemin {boat.Path.Count} → {(boat.Path.Count > 0 ? $"({boat.Path.Last().X:0},{boat.Path.Last().Y:0})" : "-")}");
                Unit man = ses.Units.Units.First(u => !u.IsVehicle && !u.OnBoard);
                bool walk = ses.Units.Order(man, new(float.Parse(args[3]), float.Parse(args[4])));
                ses.Units.PlayerCommand = false;
                Console.WriteLine($"pompier envoyé sur l'eau : {walk} · {man.Status} · fin du chemin {(man.Path.Count > 0 ? $"({man.Path.Last().X:0},{man.Path.Last().Y:0})" : "-")}");
                for (int i = 0; i < 1200; i++)
                {
                    ses.Step();
                    if (i % 40 == 0)
                        Console.WriteLine($"t {ses.Time:0} bateau ({boat.Position.X:0},{boat.Position.Y:0},{boat.Position.Z:0}) {boat.Status} vitesse {boat.CurSpeed:0}");
                }
                return 0;
            }
            case "marches":
            {
                // marches <Data> <Mission>… : différences de hauteur entre cases voisines ouvertes (histogramme)
                foreach (string mis in args.Skip(2))
                {
                    Grid gr = Grid.Parse(files.Read(files.List("Design\\Mission\\" + mis + "\\").First(p => p.EndsWith(".grid", StringComparison.OrdinalIgnoreCase))));
                    int[] bins = new int[12];
                    for (int y = 0; y < gr.Height; y++)
                        for (int x = 0; x + 1 < gr.Width; x++)
                            foreach ((int nx, int ny) in new[] { (x + 1, y), (x, y + 1) })
                            {
                                if (!gr.Inside(nx, ny))
                                    continue;
                                int a = gr.Index(x, y), b = gr.Index(nx, ny);
                                if (gr.ZoneA[a] == 0 || gr.ZoneA[b] == 0)
                                    continue;
                                float d = MathF.Abs(gr.GroundHeight[a] - gr.GroundHeight[b]);
                                bins[d < 10 ? 0 : d < 25 ? 1 : d < 50 ? 2 : d < 75 ? 3 : d < 100 ? 4 : d < 125 ? 5 : d < 150 ? 6 : d < 200 ? 7 : d < 300 ? 8 : d < 500 ? 9 : d < 1000 ? 10 : 11]++;
                            }
                    Console.WriteLine($"{mis,-12} <10:{bins[0]} <25:{bins[1]} <50:{bins[2]} <75:{bins[3]} <100:{bins[4]} <125:{bins[5]} <150:{bins[6]} <200:{bins[7]} <300:{bins[8]} <500:{bins[9]} <1000:{bins[10]} ≥1000:{bins[11]}");
                }
                return 0;
            }
            case "atteignables":
            {
                // atteignables <Data> <Mission>… : cases ouvertes atteintes à pied depuis le départ des piétons, sans et avec la
                // règle des marches trop hautes (PathFind.MaxStep)
                foreach (string mis in args.Skip(2))
                {
                    Grid gr = Grid.Parse(files.Read(files.List("Design\\Mission\\" + mis + "\\").First(p => p.EndsWith(".grid", StringComparison.OrdinalIgnoreCase))));
                    // départ : une case de la plus grande zone ouverte (sans la règle), la plus basse de cette zone (la rue)
                    bool[] mark = new bool[gr.Width * gr.Height];
                    int start = -1, bestSize = 0;
                    for (int s0 = 0; s0 < mark.Length; s0++)
                    {
                        if (mark[s0] || gr.ZoneA[s0] == 0)
                            continue;
                        List<int> comp = new List<int> { s0 };
                        mark[s0] = true;
                        for (int ci = 0; ci < comp.Count; ci++)
                        {
                            int k = comp[ci], x = k % gr.Width, y = k / gr.Width;
                            foreach ((int nx, int ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                                if (gr.Inside(nx, ny) && !mark[gr.Index(nx, ny)] && gr.ZoneA[gr.Index(nx, ny)] != 0)
                                {
                                    mark[gr.Index(nx, ny)] = true;
                                    comp.Add(gr.Index(nx, ny));
                                }
                        }
                        if (comp.Count > bestSize)
                        {
                            bestSize = comp.Count;
                            start = comp.OrderBy(k => gr.GroundHeight[k]).ElementAt(comp.Count / 3);
                        }
                    }
                    bool[] lastSeen = new bool[0];
                    int Fill(bool steps)
                    {
                        bool[] seen = new bool[gr.Width * gr.Height];
                        lastSeen = seen;
                        Queue<int> q = new Queue<int>();
                        if (start < 0)
                            return -1;
                        seen[start] = true;
                        q.Enqueue(start);
                        int n = 0;
                        while (q.Count > 0)
                        {
                            int k = q.Dequeue();
                            n++;
                            int x = k % gr.Width, y = k / gr.Width;
                            foreach ((int nx, int ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                            {
                                if (!gr.Inside(nx, ny))
                                    continue;
                                int j = gr.Index(nx, ny);
                                if (seen[j] || gr.ZoneA[j] == 0 || (steps && MathF.Abs(gr.GroundHeight[j] - gr.GroundHeight[k]) > PathFind.MaxStep))
                                    continue;
                                seen[j] = true;
                                q.Enqueue(j);
                            }
                        }
                        return n;
                    }
                    int before = Fill(false);
                    bool[] all = lastSeen;
                    int after = Fill(true);
                    bool[] kept = lastSeen;
                    if (Environment.GetEnvironmentVariable("FD3_CARTE") == "1")
                    {
                        Console.WriteLine($"départ ({start % gr.Width * 100},{start / gr.Width * 100}) h {gr.GroundHeight[start]:0} ; # perdu (chiffre : hauteur/2 m), o gardé");
                        for (int y = gr.Height - 1; y >= 0; y -= 4)
                        {
                            System.Text.StringBuilder line = new System.Text.StringBuilder($"{y * 100,6} ");
                            for (int x = 0; x < gr.Width; x += 2)
                            {
                                int k = gr.Index(x, y);
                                line.Append(kept[k] ? 'o' : all[k] ? (char)('0' + Math.Clamp((int)(gr.GroundHeight[k] / 200), 0, 9)) : gr.ZoneA[k] == 0 ? ' ' : '.');
                            }
                            Console.WriteLine(line.ToString());
                        }
                    }
                    Console.WriteLine($"{mis,-12} {before,7} → {after,7} ({(before > 0 ? 100.0 * after / before : 0):0.0} %)");
                }
                return 0;
            }
            case "relief":
            {
                // relief <Data> <Mission> [pas] : carte grossière de la grille (. fermé, chiffre : hauteur du sol / 2 m)
                Grid gr = Grid.Parse(files.Read(files.List("Design\\Mission\\" + args[2] + "\\").First(p => p.EndsWith(".grid", StringComparison.OrdinalIgnoreCase))));
                int step = args.Length > 3 ? int.Parse(args[3]) : 4;
                Console.WriteLine($"grille {gr.Width}×{gr.Height} ; hauteurs {gr.GroundHeight.Min():0}..{gr.GroundHeight.Max():0}");
                for (int y = gr.Height - 1; y >= 0; y -= step * 2)
                {
                    System.Text.StringBuilder line = new System.Text.StringBuilder($"{y * 100,6} ");
                    for (int x = 0; x < gr.Width; x += step)
                    {
                        int k = gr.Index(x, y);
                        line.Append(gr.ZoneA[k] == 0 ? '.' : args.Length > 4 ? (char)('A' + (args[4] == "b" ? gr.ZoneB[k] : gr.ZoneA[k]) % 26) : (char)('0' + Math.Clamp((int)(gr.GroundHeight[k] / 200), 0, 9)));
                    }
                    Console.WriteLine(line.ToString());
                }
                return 0;
            }
            case "escaliers":
            {
                // escaliers <Data> <Mission> : escaliers du niveau ; un pompier envoyé sur la première icône de chacun
                Session ses = new Session(files, args[2], "EASY");
                for (int i = 0; i < 300; i++)
                    ses.Step();
                foreach (Teleport t in ses.Units.Teleports)
                    Console.WriteLine($"{t.Name} ({t.Id}) : actif {t.Active}, {t.Duration} s, ({t.EndA.X:0},{t.EndA.Y:0}) ↔ ({t.EndB.X:0},{t.EndB.Y:0}), entrée « {t.OnEnter} » sortie « {t.OnLeave} » ; cases {t.CellA}/{t.CellB}");
                Unit? man = ses.Units.Units.FirstOrDefault(u => !u.IsVehicle && !u.OnBoard && !u.Hidden);
                // escalier encore fermé par le script (Tutorial) : ouvert pour l'essai
                if (!ses.Units.Teleports.Any(t => t.Active) && ses.Units.Teleports.FirstOrDefault(t => !t.Fake) is { } shut)
                {
                    ses.Units.ActivateTeleport(shut.Id, true);
                    ses.Step();
                }
                Teleport? tp = ses.Units.Teleports.FirstOrDefault(t => t.Active);
                if (tp != null && ses.Grid is { } tg)
                {
                    // chemins depuis les deux bouts (et depuis l'ancienne case de sortie, un palier isolé) vers un point plus loin dans la rue
                    System.Numerics.Vector2 Cc(int k) => new System.Numerics.Vector2((k % tg.Width + 0.5f) * Grid.CellSize, (k / tg.Width + 0.5f) * Grid.CellSize);
                    System.Numerics.Vector2 street = tp.EndB + new System.Numerics.Vector2(0, 600);
                    foreach ((string what, System.Numerics.Vector2 from) in new[] { ("bout A", Cc(tp.CellA)), ("bout B", Cc(tp.CellB)), ("palier isolé", ses.Units.Paths.Walkable(tp.EndA)) })
                    {
                        List<System.Numerics.Vector2> pth = ses.Units.Paths.FindPath(from, street, out bool part);
                        Console.WriteLine($"{what} ({from.X:0},{from.Y:0}, sol {ses.Units.GroundZ(from):0}) → rue : {pth.Count} points{(part ? " (partiel)" : "")} {string.Join(" → ", pth.Take(6).Select(p => $"({p.X:0},{p.Y:0})"))}");
                    }
                }
                if (man == null || tp == null)
                    return 0;
                // posé près de la première icône, envoyé dessus, puis vers un point près de l'autre bout
                ses.Units.Teleport(man, tp.EndA + new System.Numerics.Vector2(300, 0));
                ses.Units.PlayerCommand = true;
                bool ok = ses.Units.Order(man, tp.EndA);
                ses.Units.PlayerCommand = false;
                Console.WriteLine($"ordre sur l'icône : {ok} · chemin {string.Join(" → ", man.Path.Select(p => $"({p.X:0},{p.Y:0})"))}");
                for (int i = 0; i < 400; i++)
                {
                    ses.Step();
                    if (i % 10 == 0)
                        Console.WriteLine($"t {ses.Time:0.0} ({man.Position.X:0},{man.Position.Y:0},{man.Position.Z:0}) {man.Status} {(man.Stairs != null ? $"[dans l'escalier {man.StairsTimer:0.0}]" : "")} chemin {man.Path.Count}");
                    if (man.Stairs == null && man.Path.Count == 0 && System.Numerics.Vector2.Distance(new(man.Position.X, man.Position.Y), tp.EndB) < 300)
                        break;
                }
                // retour : un point près du premier bout, sans viser l'icône ; le chemin doit reprendre l'escalier
                ses.Units.Order(man, tp.EndA + new System.Numerics.Vector2(-300, -100));
                Console.WriteLine($"retour : {man.Status} · chemin {string.Join(" → ", man.Path.Select(p => $"({p.X:0},{p.Y:0})"))}");
                for (int i = 0; i < 400 && (man.Path.Count > 0 || man.Stairs != null); i++)
                {
                    ses.Step();
                    if (i % 10 == 0)
                        Console.WriteLine($"t {ses.Time:0.0} ({man.Position.X:0},{man.Position.Y:0},{man.Position.Z:0}) {man.Status} {(man.Stairs != null ? "[dans l'escalier]" : "")}");
                }
                return 0;
            }
            case "soigner":
            {
                // soigner <Data> <Mission> : postes de soins du niveau ; un pompier épuisé posé sur le premier visible, son
                // endurance pendant 10 s (et un autre, à 10 m, pour comparer)
                Session ses = new Session(files, args[2], "EASY");
                foreach (HealPoint hp in ses.Units.HealPoints)
                    Console.WriteLine($"poste {hp.Name} ({hp.Position.X:0},{hp.Position.Y:0}) visible {ses.Units.HealPointActive(hp)} modèle {hp.Model}");
                while (ses.Time < 300 && ses.Units.Active.Count(u => !u.IsVehicle && !u.OnBoard) < 2)
                    ses.Step();
                HealPoint? p0 = ses.Units.HealPoints.FirstOrDefault(ses.Units.HealPointActive);
                List<Unit> men = ses.Units.Active.Where(u => !u.IsVehicle && !u.OnBoard).Take(2).ToList();
                if (p0 == null || men.Count < 2)
                    return 1;
                ses.Units.Teleport(men[0], new System.Numerics.Vector2(p0.Position.X, p0.Position.Y));
                ses.Units.Teleport(men[1], new System.Numerics.Vector2(p0.Position.X + 1000, p0.Position.Y));
                foreach (Unit u in men)
                    u.Stamina = 20;
                for (int i = 0; i < (int)(10 / Session.Dt); i++)
                    ses.Step();
                foreach (Unit u in men)
                    Console.WriteLine($"{u.Name} ({u.Position.X:0},{u.Position.Y:0}) endurance {u.Stamina:0} · {u.Status}");
                // un civil conscient qui suit le premier pompier, amené sur le poste : il doit être mis à l'abri
                if (ses.Units.Civilians.FirstOrDefault(c => c.State == OpenFD.Game.CivilianState.Conscious && !c.Hidden) is { } cv)
                {
                    cv.Leader = men[0];
                    cv.Position = men[0].Position + new System.Numerics.Vector3(60, 0, 0);
                    for (int i = 0; i < (int)(3 / Session.Dt); i++)
                        ses.Step();
                    Console.WriteLine($"civil {cv.Name} : {cv.State} · {ses.Units.Rescued} sauvé(s)");
                }
                return 0;
            }
            case "empreinte":
            {
                // empreinte <Data> <modèle> [bas haut] : empreinte au sol du modèle (points tous les 25 cm sous ses faces entre
                // ces hauteurs), en rangées le long de son axe Y : # occupé, . libre
                SceneAssets assets = new OpenFD.Game.SceneAssets(files);
                float lo = args.Length > 4 ? float.Parse(args[3]) : 30, hi = args.Length > 4 ? float.Parse(args[4]) : 170;
                List<System.Numerics.Vector2> pts = PathFind.Footprint(assets, SceneAssets.GfxPath("Placeable", args[2]), lo, hi);
                HashSet<(int, int)> set = pts.Select(p => ((int)MathF.Floor(p.X / 25), (int)MathF.Floor(p.Y / 25))).ToHashSet();
                int x0 = set.Min(p => p.Item1), x1 = set.Max(p => p.Item1), y0 = set.Min(p => p.Item2), y1 = set.Max(p => p.Item2);
                for (int y = y1; y >= y0; y--)
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    for (int x = x0; x <= x1; x++)
                        sb.Append(set.Contains((x, y)) ? '#' : '.');
                    Console.WriteLine($"{y * 25,7} {sb}");
                }
                return 0;
            }
            case "reflets":
            {
                // reflets <Data> <Mission> : matériaux du décor et des objets posés qui ont un reflet (image simple ou 6 faces)
                SceneAssets assets = new OpenFD.Game.SceneAssets(files);
                Dictionary<string, string> found = new Dictionary<string, string>();
                foreach (SceneAssets.Node n in assets.Scenery(args[2]))
                    foreach (SceneAssets.Group gr in assets.LoadMesh(args[2], n.Mesh, null).Groups.Where(g => g.Env != null || g.Cube != null))
                        found.TryAdd("décor " + gr.Material, gr.Env ?? gr.Cube!);
                string lvRef = files.List("Design\\Mission\\" + args[2] + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                foreach (SceneAssets.Placed pl in assets.Placeables(Level.Parse(files.Read(lvRef))))
                    try
                    {
                        foreach (SceneAssets.Node part in assets.ModelParts(pl.Model))
                            foreach (SceneAssets.Group gr in assets.LoadMesh(args[2], part.Mesh, "gfx").Groups.Where(g => g.Env != null || g.Cube != null))
                                found.TryAdd("objet " + gr.Material, gr.Env ?? gr.Cube!);
                    }
                    catch { }
                foreach ((string k, string v) in found.OrderBy(kv => kv.Key))
                    Console.WriteLine($"{k,-50} {v}");
                Console.WriteLine($"{found.Count} matériau(x) avec reflet");
                return 0;
            }
            case "octetsfenetres":
            {
                // octetsfenetres <Data> <Mission> : octets des composants de chaque fenêtre ; les positions qui diffèrent d'une fenêtre à l'autre
                Session ses = new Session(files, args[2], "EASY", false);
                Dictionary<uint, LevelObject> byId = ses.Level.Objects.ToDictionary(o => o.Id);
                List<LevelObject> wins = ses.Units.Openings.Where(o => o.IsWindow).Select(o => byId[o.Id]).ToList();
                foreach (ushort cid in wins.SelectMany(w => w.Components.Select(c => c.Id)).Distinct().OrderBy(x => x))
                {
                    List<byte[]> all = wins.Where(w => w.GetComponent(cid) != null).Select(w => w.ComponentBytes(w.GetComponent(cid)!).ToArray()).ToList();
                    int len = all.Max(b => b.Length);
                    List<int> diff = Enumerable.Range(0, len).Where(i => all.Select(b => i < b.Length ? b[i] : -1).Distinct().Count() > 1).ToList();
                    Console.WriteLine($"{cid:X4} : {all.Count} fenêtres, {all.Select(b => b.Length).Distinct().Count()} longueur(s) ({string.Join("/", all.Select(b => b.Length).Distinct())}), octets qui varient : {string.Join(",", diff.Take(40))}");
                    if (args.Length > 3 && args[3].Equals(cid.ToString("X4"), StringComparison.OrdinalIgnoreCase))
                        foreach ((LevelObject w, byte[] b) in wins.Zip(all).Take(int.Parse(args.Length > 4 ? args[4] : "12")))
                            Console.WriteLine($"   {w.Name,-30} {BitConverter.ToString(b).Replace("-", " ")}");
                }
                return 0;
            }
            case "etatsfenetres":
            {
                // etatsfenetres <Data> : état de départ (composant A027) des fenêtres de chaque mission, et états logiques de leur classe
                foreach (string dir in Directory.GetDirectories(Path.Combine(args[1], "Script", "Mission")).OrderBy(d => d))
                {
                    string mission = Path.GetFileName(dir);
                    try
                    {
                        Session ses = new Session(files, mission, "EASY", false);
                        Dictionary<uint, LevelObject> byId = ses.Level.Objects.ToDictionary(o => o.Id);
                        List<Opening> wins = ses.Units.Openings.Where(o => o.IsWindow).ToList();
                        string counts = $"{wins.Count(w => w.State == OpeningState.Broken)} cassée(s) au départ ; portes cassées au départ : {ses.Units.Openings.Count(o => !o.IsWindow && o.State == OpeningState.Broken)}";
                        string ex = string.Join(", ", wins.Where(w => w.State == OpeningState.Broken).Take(4).Select(w => w.Name)
                            .Concat(ses.Units.Openings.Where(o => !o.IsWindow && o.State == OpeningState.Broken).Take(4).Select(o => "porte " + o.Name)));
                        Console.WriteLine($"{mission,-14} {wins.Count,4} fenêtres : {counts}{(ex.Length > 0 ? " · ex. " + ex : "")}");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"{mission,-14} impossible : {e.Message}");
                    }
                }
                return 0;
            }
            case "pente":
            {
                // pente <Data> <Mission> : chaque camion à lance et chaque canon, posé tour à tour en haut d'une pente au-dessus d'un feu
                // (sol plus haut de 3,6 m au moins), à portée de lance : voit-il les feux plus de 3,5 m plus bas que lui ?
                Session ses = new Session(files, args[2], "EASY");
                for (int i = 0; i < (int)(30 / Session.Dt); i++)
                    ses.Step();
                Unit? truck = ses.Units.Units.FirstOrDefault(u => u.IsVehicle && u.Nozzle != null && !u.Destroyed);
                if (truck == null)
                {
                    Console.WriteLine("pas de camion à lance");
                    return 1;
                }
                int tried = 0, seen = 0;
                foreach (Fire.HotPoint h in ses.Fire.HotPoints.Where(h => h.IsBurning && truck.Nozzle!.Suits(h.Class.FireType)))
                {
                    System.Numerics.Vector2 f = new System.Numerics.Vector2(h.Position.X, h.Position.Y);
                    for (int k = 0; k < 24; k++)
                    {
                        System.Numerics.Vector2 p = f + new System.Numerics.Vector2(MathF.Cos(k * MathF.PI / 12), MathF.Sin(k * MathF.PI / 12)) * truck.Nozzle!.RangeMax * 0.6f;
                        (int cx, int cy) = ses.Grid!.CellOf(new System.Numerics.Vector3(p, 0));
                        if (ses.Units.GroundZ(p) - h.Position.Z < 360 || !ses.Units.Paths.WalkableCell(cx, cy))
                            continue;
                        ses.Units.Teleport(truck, p);
                        Dictionary<uint, float> skip = ses.Fire.HotPoints.Where(o => o.Position.Z - truck.Position.Z >= -350).ToDictionary(o => o.Id, o => float.MaxValue);
                        Fire.HotPoint? got = ses.Units.VisibleFireForTest(truck, skip);
                        tried++;
                        seen += got != null ? 1 : 0;
                        if (tried <= 6)
                        {
                            float step = 0, prevZ = ses.Units.GroundZ(p);
                            int nn = (int)(System.Numerics.Vector2.Distance(p, f) / 50) + 1;
                            for (int q = 1; q <= nn; q++)
                            {
                                float z = ses.Units.GroundZ(System.Numerics.Vector2.Lerp(p, f, (float)q / nn));
                                step = MathF.Max(step, MathF.Abs(z - prevZ));
                                prevZ = z;
                            }
                            Console.WriteLine($"  camion à ({p.X:0},{p.Y:0}) hauteur {truck.Position.Z:0} ; {h.Name} à {h.Position.Z:0} (sol {ses.Units.GroundZ(f):0}, plus grande marche {step:0}, jet libre {ses.Units.Paths.WaterLineFree(p, f - System.Numerics.Vector2.Normalize(f - p) * 120)}, obstacles {string.Join("/", Enumerable.Range(1, (int)(System.Numerics.Vector2.Distance(p, f) / 40)).Select(q => ses.Grid!.CellOf(new System.Numerics.Vector3(System.Numerics.Vector2.Lerp(p, f, q * 40 / System.Numerics.Vector2.Distance(p, f)), 0))).Select(c => ses.Grid!.Index(c.Item1, c.Item2)).Distinct().Select(k => ses.Units.Paths.BlockedBy.TryGetValue(k, out string? bb) ? bb : ses.Grid!.ZoneA[k] == 0 ? "zoneA0" : "").Where(x => x != "").Distinct())}) : voit {(got == null ? "rien" : $"{got.Name} (dz {got.Position.Z - truck.Position.Z:0})")}");
                        }
                        break;
                    }
                }
                Console.WriteLine($"{tried} essais en haut d'une pente : un feu plus bas vu {seen} fois");
                return 0;
            }
            case "vuefenetre":
            {
                // vuefenetre <Data> <Mission> : pour chaque fenêtre au sol, vue d'un point à 3 m devant elle vers un point à 3 m derrière,
                // vitre intacte puis cassée (on n'arrose par une fenêtre qu'une fois la vitre cassée)
                Session ses = new Session(files, args[2], "EASY", false);
                Grid g = ses.Grid!;
                int tried = 0, seenShut = 0, seenBroken = 0;
                foreach (Opening w in ses.Units.Openings.Where(o => o.IsWindow && o.State == OpeningState.Closed))
                    foreach (System.Numerics.Vector2 dir in new[] { System.Numerics.Vector2.UnitX, System.Numerics.Vector2.UnitY })
                    {
                        System.Numerics.Vector2 c = new System.Numerics.Vector2(w.Center.X, w.Center.Y), a = c - dir * 300, b = c + dir * 300;
                        (int ax, int ay) = g.CellOf(new System.Numerics.Vector3(a, 0));
                        (int bx, int by) = g.CellOf(new System.Numerics.Vector3(b, 0));
                        if (!ses.Units.Paths.WalkableCell(ax, ay) || !ses.Units.Paths.WalkableCell(bx, by))
                            continue;
                        tried++;
                        bool shut = ses.Units.Paths.LineFree(a, b, ignoreFire: true) && !ses.Units.WallBetween(a, b + (b - a) * 0.5f);
                        ses.Units.SetOpeningState(w, OpeningState.Broken);
                        bool broken = ses.Units.Paths.LineFree(a, b, ignoreFire: true) && !ses.Units.WallBetween(a, b + (b - a) * 0.5f);
                        ses.Units.SetOpeningState(w, OpeningState.Closed);
                        seenShut += shut ? 1 : 0;
                        seenBroken += broken ? 1 : 0;
                    }
                Console.WriteLine($"{tried} vues au travers d'une fenêtre : {seenShut} dégagées vitre intacte, {seenBroken} dégagées vitre cassée");
                return 0;
            }
            case "passefenetres":
            {
                // passefenetres <Data> <Mission> : chemins à pied d'un côté à l'autre de chaque fenêtre (2,5 m de part et d'autre) ;
                // combien passent par une fenêtre, avec et sans l'interdiction (PathFind.WindowCell)
                Session ses = new Session(files, args[2], "EASY", false);
                Grid g = ses.Grid!;
                PathFind pf = ses.Units.Paths;
                List<Opening> windows = ses.Units.Openings.Where(o => o.IsWindow).ToList();
                bool Through(List<System.Numerics.Vector2> path, System.Numerics.Vector2 from, bool[] win)
                {
                    System.Numerics.Vector2 cur = from;
                    foreach (System.Numerics.Vector2 q in path)
                    {
                        int n = (int)(System.Numerics.Vector2.Distance(cur, q) / 30) + 1;
                        for (int i = 1; i <= n; i++)
                        {
                            (int cx, int cy) = g.CellOf(new System.Numerics.Vector3(System.Numerics.Vector2.Lerp(cur, q, (float)i / n), 0));
                            if (g.Inside(cx, cy) && win[g.Index(cx, cy)])
                                return true;
                        }
                        cur = q;
                    }
                    return false;
                }
                bool[] marks = pf.WindowCell;
                foreach (bool forbid in new[] { false, true })
                {
                    pf.WindowCell = forbid ? marks : new bool[marks.Length];
                    int tried = 0, through = 0;
                    foreach (Opening w in windows)
                        foreach (System.Numerics.Vector2 dir in new[] { System.Numerics.Vector2.UnitX, System.Numerics.Vector2.UnitY })
                        {
                            System.Numerics.Vector2 c = new System.Numerics.Vector2(w.Center.X, w.Center.Y), a = c - dir * 250, b = c + dir * 250;
                            (int ax, int ay) = g.CellOf(new System.Numerics.Vector3(a, 0));
                            (int bx, int by) = g.CellOf(new System.Numerics.Vector3(b, 0));
                            if (!pf.WalkableCell(ax, ay) || !pf.WalkableCell(bx, by))
                                continue;
                            List<System.Numerics.Vector2> path = pf.FindPath(a, b, out bool partial);
                            if (path.Count == 0 || partial)
                                continue;
                            tried++;
                            if (Through(path, a, marks))
                            {
                                through++;
                                if (forbid)
                                {
                                    (int wx, int wy) = g.CellOf(w.Center);
                                    Console.WriteLine("    cases autour : " + string.Join(" ", Enumerable.Range(-2, 5).SelectMany(oy => Enumerable.Range(-2, 5).Select(ox => (ox, oy)))
                                        .Where(o => g.Inside(wx + o.ox, wy + o.oy)).Select(o => { int k = g.Index(wx + o.ox, wy + o.oy); return $"{o.ox},{o.oy}:{(pf.IsWindowCell(k) ? "F" : pf.WalkableCell(wx + o.ox, wy + o.oy) ? "." : "#")}{(pf.Links.Any(l => l.a == k || l.b == k) ? "L" : "")}"; })));
                                }
                                if (forbid)
                                    Console.WriteLine($"  {w.Name} ({c.X:0},{c.Y:0}) cases {w.Cells.Count} emprise ({w.BoxMin.X:0},{w.BoxMin.Y:0})-({w.BoxMax.X:0},{w.BoxMax.Y:0}) : ({a.X:0},{a.Y:0}) → " + string.Join(" → ", path.Select(q => $"({q.X:0},{q.Y:0})")));
                            }
                        }
                    Console.WriteLine($"{(forbid ? "avec" : "sans")} l'interdiction : {windows.Count} fenêtres, {marks.Count(x => x)} cases de fenêtre, {tried} chemins d'un côté à l'autre, {through} passent par une fenêtre");
                }
                return 0;
            }
            case "ciblesauto":
            {
                // ciblesauto <Data> <Mission> <secondes> : feux que les unités à lance arrosent sans ordre (type de feu, agent)
                Session ses = new Session(files, args[2], "EASY");
                float until = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
                Dictionary<int, uint> last = new();
                bool placed = args.Length <= 4;   // ciblesauto … carburant : à 40 s, un camion à lance posé près des feux du mauvais type
                while (ses.Time < until)
                {
                    ses.Step();
                    if (!placed && ses.Time > 40)
                    {
                        placed = true;
                        Unit? truck = ses.Units.Units.FirstOrDefault(u => u.IsVehicle && u.Nozzle != null && !u.Destroyed);
                        Fire.HotPoint? wrong = truck == null ? null : ses.Fire.HotPoints.Where(h => h.IsBurning && !truck.Nozzle!.Suits(h.Class.FireType)).OrderByDescending(h => h.Intensity).FirstOrDefault();
                        if (truck != null && wrong != null)
                        {
                            ses.Units.Teleport(truck, ses.Units.Paths.Walkable(new System.Numerics.Vector2(wrong.Position.X - truck.Nozzle!.RangeMax * 0.6f, wrong.Position.Y)));
                            truck.Path.Clear();
                            truck.Target = null;
                            Console.WriteLine($"{truck.Name} posé à {System.Numerics.Vector3.Distance(truck.Position, wrong.Position):0} de {wrong.Name} (type {wrong.Class.FireType}) ; feux B en feu : {ses.Fire.HotPoints.Count(h => h.IsBurning && h.Class.FireType == 'B')}");
                        }
                        else
                            Console.WriteLine("pas de camion à lance ou pas de feu du mauvais type");
                    }
                    foreach (Unit u in ses.Units.Units.Where(u => u.Nozzle != null && u.Target != null))
                    {
                        if (last.TryGetValue(u.Id, out uint seen) && seen == u.Target!.Id)
                            continue;
                        last[u.Id] = u.Target!.Id;
                        bool ok = u.IsCanon || u.Nozzle!.Suits(u.Target.Class.FireType);
                        Console.WriteLine($"{ses.Time,6:0.0} s {(u.IsVehicle ? "véhicule" : "pompier")} {u.Name} ({u.Nozzle!.Agent}) → {u.Target.Name} type {u.Target.Class.FireType} classe {u.Target.Class.Name} {(ok ? "" : "  <<< MAUVAIS AGENT")} · {u.Status}");
                    }
                }
                return 0;
            }
            case "ignifuge":
            {
                // ignifuge <Data> <Mission> : un pompier ordinaire et le pompier ignifugé posés côte à côte près d'un feu ; endurance
                Session ses = new Session(files, args[2], "EASY");
                for (int i = 0; i < (int)(20 / Session.Dt); i++)
                    ses.Step();
                Unit? igni = ses.Units.Units.FirstOrDefault(u => u.ClassPath.Contains("igni", StringComparison.OrdinalIgnoreCase));
                Unit? sp = ses.Units.Units.FirstOrDefault(u => !u.IsVehicle && u.Nozzle != null && u != igni && u.SuitHeatCoef == 1);
                Fire.HotPoint? fire = ses.Fire.HotPoints.Where(h => h.IsBurning).OrderByDescending(h => h.Intensity).FirstOrDefault();
                if (igni == null || sp == null || fire == null)
                {
                    Console.WriteLine($"ignifugé {igni?.Name ?? "absent"} · pompier {sp?.Name ?? "absent"} · feu {fire?.Name ?? "aucun"}");
                    return 1;
                }
                Console.WriteLine($"ignifugé {igni.ClassPath} chaleur ×{igni.SuitHeatCoef} · pompier {sp.ClassPath} · feu {fire.Name} {fire.Intensity:0}");
                foreach (Unit u in new[] { igni, sp })
                {
                    ses.Units.Teleport(u, new System.Numerics.Vector2(fire.Position.X + 60, fire.Position.Y));
                    u.Stamina = u.StaminaMax;
                }
                for (int k = 0; k < 12; k++)
                {
                    for (int i = 0; i < (int)(5 / Session.Dt); i++)
                    {
                        foreach (Unit u in new[] { igni, sp })
                        {
                            u.Path.Clear();
                            u.Target = null;
                        }
                        ses.Step();
                    }
                    Console.WriteLine($"  {ses.Time:0} s · ignifugé {igni.Stamina:0.0} ({(igni.Unconscious ? "évanoui" : "debout")}) · pompier {sp.Stamina:0.0} ({(sp.Unconscious ? "évanoui" : "debout")}) · feu {fire.Intensity:0}");
                }
                return 0;
            }
            case "grilles":
            {
                // grilles <Data> <Mission> <secondes> : Factory, grilles automatiques (zones TGrid_n, battants Grid_n_1 et Grid_n_2)
                Session ses = new Session(files, args[2], "EASY");
                float until = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
                string last = "";
                foreach (TriggerZone tz in ses.Units.Triggers.Where(t => t.Name.StartsWith("tgrid", StringComparison.OrdinalIgnoreCase)))
                    Console.WriteLine($"zone {tz.Name} ({tz.Position.X:0},{tz.Position.Y:0}) demi-taille ({tz.Half.X:0},{tz.Half.Y:0},{tz.Half.Z:0}) entrée {tz.Script} sortie {tz.OnLeave} active {tz.Enabled}");
                bool sent = false;
                while (ses.Time < until)
                {
                    ses.Step();
                    // à 15 s : le pompier le plus proche de la première zone y va, puis en repart à 60 s
                    if (!sent && ses.Time > 15 && ses.Units.Triggers.FirstOrDefault(t => t.Name.Equals("tgrid_1", StringComparison.OrdinalIgnoreCase)) is { } z1
                        && ses.Units.Units.Where(u => !u.IsVehicle && !u.Hidden && !u.OnBoard).OrderBy(u => System.Numerics.Vector3.Distance(u.Position, z1.Position)).FirstOrDefault() is { } man)
                    {
                        sent = true;
                        Console.WriteLine($"{man.Name} ({man.Position.X:0},{man.Position.Y:0}) envoyé à la zone : {ses.Units.Order(man, new System.Numerics.Vector2(z1.Position.X, z1.Position.Y))}");
                    }
                    Dictionary<string, OpenFD.Script.Value> gl = ses.Scripts!.Globals;
                    string line = string.Join(" | ", Enumerable.Range(1, 4).Select(n =>
                    {
                        string pieces = string.Join(" ", new[] { 1, 2 }.Select(k => gl.TryGetValue($"Grid_{n}_{k}", out OpenFD.Script.Value id)
                            && ses.Host!.MovedObjects.TryGetValue((uint)id.AsInt, out System.Numerics.Vector2 p) ? $"({p.X:0},{p.Y:0})" : "-"));
                        return $"{n}: {(gl.TryGetValue($"NbCibleGrid{n}", out OpenFD.Script.Value c) ? c.AsInt : -9)} {(gl.TryGetValue($"GridStatut{n}", out OpenFD.Script.Value st) ? st.AsInt : -9)} {pieces}";
                    }));
                    if (line != last)
                    {
                        Console.WriteLine($"{ses.Time,6:0.0} s {line}");
                        last = line;
                    }
                }
                return 0;
            }
            case "messages":
            {
                // messages <Data> <Mission> <secondes> [fonction,secondes] : messages du script au fil du temps (une fonction du script
                // peut être appelée en cours de route : College « TransitionS2,20 » lance le court-circuit)
                Session ses = new Session(files, args[2], "EASY");
                float until = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
                string[]? call = args.Length > 4 ? args[4].Split(',') : null;
                bool called = false;
                int seen = 0;
                Dictionary<string, int> counts = new();
                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                while (ses.Time < until && ses.Host?.Outcome == null)
                {
                    ses.Step();
                    if (call != null && !called && ses.Time > float.Parse(call[1], System.Globalization.CultureInfo.InvariantCulture))
                    {
                        called = true;
                        ses.Scripts?.TryRun(call[0]);
                        Console.WriteLine($"{ses.Time,6:0} s appel {call[0]}");
                    }
                    if (ses.Steps % 300 == 0)
                        Console.WriteLine($"{ses.Time,6:0} s · calcul {watch.Elapsed.TotalSeconds:0.0} s · foyers en feu {ses.Fire.HotPoints.Count(h => h.IsBurning)}/{ses.Fire.HotPoints.Count}");
                    List<(string vocal, string text, float x, float y)> msgs = ses.Host!.Messages;
                    for (; seen < msgs.Count; seen++)
                    {
                        counts[msgs[seen].text] = counts.GetValueOrDefault(msgs[seen].text) + 1;
                        Console.WriteLine($"{ses.Time,6:0} s {msgs[seen].text}");
                    }
                }
                Console.WriteLine("total : " + string.Join(" · ", counts.Select(kv => $"{kv.Key} ×{kv.Value}")));
                return 0;
            }
            case "suivreveh":
            {
                // suivreveh <Data> <Mission> : des pompiers vont au camion (monter, recharger), le camion part ailleurs entre-temps
                Session ses = new Session(files, args[2], "EASY");
                for (int i = 0; i < (int)(30 / Session.Dt); i++)
                    ses.Step();
                Unit truck = ses.Units.Units.First(u => u.IsVehicle && !u.Fixed && !u.Moving && (u.Seats > 0 || u.ResupplyPower > 0));
                List<Unit> men = ses.Units.Units.Where(u => !u.IsVehicle && u.Nozzle != null).OrderByDescending(u => System.Numerics.Vector3.Distance(u.Position, truck.Position)).Take(2).ToList();
                men[0].Nozzle!.Reserve = 0;
                ses.Units.GoToVehicle(men[0], truck);
                if (men.Count > 1)
                    ses.Units.BoardVehicle(men[1], truck);
                Console.WriteLine($"camion {truck.Name} ({truck.Position.X:0},{truck.Position.Y:0}) ; " + string.Join(" | ", men.Select(m => $"{m.Name} à {System.Numerics.Vector3.Distance(m.Position, truck.Position):0} : {m.Status}")));
                for (int i = 0; i < (int)(1 / Session.Dt); i++)
                    ses.Step();
                bool driven = false;
                foreach (System.Numerics.Vector2 d in new[] { new System.Numerics.Vector2(2500, 0), new System.Numerics.Vector2(-2500, 0), new System.Numerics.Vector2(0, 2500), new System.Numerics.Vector2(0, -2500) })
                    if (!driven && ses.Units.Drive(truck, new System.Numerics.Vector2(truck.Position.X, truck.Position.Y) + d))
                        driven = true;
                Console.WriteLine($"camion envoyé : {driven} → ({(truck.Moving ? truck.Path.Last().X : truck.Position.X):0},{(truck.Moving ? truck.Path.Last().Y : truck.Position.Y):0})");
                for (int k = 0; k < 30; k++)
                {
                    for (int i = 0; i < (int)(1 / Session.Dt); i++)
                        ses.Step();
                    if (k % 3 == 2)
                        Console.WriteLine($"  {ses.Time:0} s camion ({truck.Position.X:0},{truck.Position.Y:0}) {(truck.Moving ? "roule" : "arrêté")} ; " + string.Join(" | ", men.Select(m => m.OnBoard ? "à bord" : $"à {System.Numerics.Vector3.Distance(m.Position, truck.Position):0} : {m.Status} ({m.Nozzle!.Reserve:0})")));
                }
                return 0;
            }
            case "soinambu":
            {
                // soinambu <Data> <Mission> : chaque unité à pied, épuisée (20 %), posée à 2 m de l'ambulance : endurance après 10 s
                Session ses = new Session(files, args[2], "EASY");
                while (ses.Time < 300 && !ses.Units.Active.Any(u => u.HealsUnits && !u.OnBoard))
                    ses.Step();
                for (int i = 0; i < (int)(20 / Session.Dt); i++)
                    ses.Step();
                Unit? amb = ses.Units.Active.FirstOrDefault(u => u.HealsUnits && !u.OnBoard);
                if (amb == null)
                    return 1;
                List<Unit> walkers = ses.Units.Active.Where(u => !u.IsVehicle).ToList();
                foreach (Unit v in ses.Units.Active.Where(v => v.IsVehicle).ToList())
                    ses.Units.UnloadPassengers(v);
                for (int i = 0; i < (int)(5 / Session.Dt); i++)
                    ses.Step();
                int n = 0;
                foreach (Unit u in walkers)
                {
                    ses.Units.Teleport(u, new System.Numerics.Vector2(amb.Position.X + 200 + 60 * n, amb.Position.Y + 150));
                    u.Stamina = u.StaminaMax * 0.2f;
                    n++;
                }
                for (int i = 0; i < (int)(10 / Session.Dt); i++)
                    ses.Step();
                foreach (Unit u in walkers)
                    Console.WriteLine($"{u.Name,-28} à bord {u.OnBoard} · bouge {u.Moving} · endurance {100 * u.Stamina / u.StaminaMax:0} % · {u.Status} · {System.Numerics.Vector3.Distance(u.Position, amb.Position):0} cm");
                return 0;
            }
            case "appel":
            {
                // appel <Data> <Mission> <fonction> [secondes] : la fonction du script est appelée à 3 s ; puis, pendant ce temps,
                // l'état des véhicules posés dans le niveau (visibles ou non, position, animation lancée)
                Session ses = new Session(files, args[2], "EASY");
                float end = args.Length > 4 ? float.Parse(args[4]) : 30;
                bool called = false;
                int k = 0;
                while (ses.Time < end)
                {
                    ses.Step();
                    if (!called && ses.Time > 3)
                    {
                        called = true;
                        ses.Scripts?.TryRun(args[3]);
                    }
                    if (k++ % 50 == 0)
                        foreach (Unit u in ses.Units.Units.Where(u => u.FromLevel))
                            Console.WriteLine($"t {ses.Time:0} {u.Name} n° {u.Id} caché {u.Hidden} ({u.Position.X:0},{u.Position.Y:0},{u.Position.Z:0}) anim {(ses.Host!.PlayedAnims.TryGetValue((uint)u.Id, out (string anim, float start, bool loop) pa) ? pa.anim : "-")}");
                }
                foreach (string l in ses.Scripts?.Log ?? new List<string>())
                    Console.WriteLine("script : " + l);
                return 0;
            }
            case "vent":
            {
                // vent <Data> <Mission> [secondes] [fonction] : chaque changement du vent (force, direction en degrés) pendant la
                // partie ; « fonction » : cette fonction du script est appelée à 5 s (Forest : Wind_direction_X2, le vent tourne)
                Session ses = new Session(files, args[2], "EASY");
                float end = args.Length > 3 ? float.Parse(args[3]) : 900;
                (float p, System.Numerics.Vector3 d) last = (-1, default);
                bool called = args.Length < 5;
                while (ses.Time < end)
                {
                    ses.Step();
                    if (!called && ses.Time > 5)
                    {
                        called = true;
                        ses.Scripts?.TryRun(args[4]);
                    }
                    OpenFD.Fire.FireSettings fs = ses.Fire.Settings;
                    if (fs.WindPower != last.p || fs.WindDirection != last.d)
                    {
                        last = (fs.WindPower, fs.WindDirection);
                        Console.WriteLine($"t {ses.Time,5:0} s : force {fs.WindPower:0.00}, vers {MathF.Atan2(fs.WindDirection.Y, fs.WindDirection.X) * 180 / MathF.PI:0}°");
                    }
                }
                return 0;
            }
            case "grimper":
            {
                // grimper <Data> <Mission> <x départ> <y départ> <x> <y> : le premier pompier à pied (après 12 s) posé au départ va au but ; sa
                // position, son état et sa phase sur une échelle verticale, toutes les 0,5 s
                Session ses = new Session(files, args[2], "EASY");
                while (ses.Time < 12 || (ses.Time < 300 && !ses.Units.Active.Any(u => !u.IsVehicle && !u.OnBoard && !u.Hidden)))
                    ses.Step();
                Unit w = ses.Units.Active.First(u => !u.IsVehicle && !u.OnBoard && !u.Hidden);
                ses.Units.Teleport(w, ses.Units.Paths.Walkable(new System.Numerics.Vector2(float.Parse(args[3]), float.Parse(args[4]))));
                ses.Step();
                ses.Units.PlayerCommand = true;
                Console.WriteLine($"ordre : {ses.Units.Order(w, new System.Numerics.Vector2(float.Parse(args[5]), float.Parse(args[6])))} de ({w.Position.X:0},{w.Position.Y:0},{w.Position.Z:0})");
                ses.Units.PlayerCommand = false;
                for (int i = 0; i < 2000; i++)
                {
                    ses.Step();
                    if (i % 5 == 0)
                        Console.WriteLine($"t {ses.Time:0.0} ({w.Position.X:0},{w.Position.Y:0},{w.Position.Z:0}) {w.Vertical} {w.Status}");
                    if (w.Path.Count == 0 && w.Stairs == null && i > 10)
                        break;
                }
                Console.WriteLine($"fin ({w.Position.X:0},{w.Position.Y:0},{w.Position.Z:0}) {w.Status}");
                return 0;
            }
            case "bloquepar":
            {
                // bloquepar <Data> <Mission> x y [x y …] : ce qui ferme ces cases de la grille (objet et modèle, mur, hauteur)
                Session ses = new Session(files, args[2], "EASY", false);
                Grid g = ses.Grid!;
                for (int a = 3; a + 1 < args.Length; a += 2)
                {
                    (int cx, int cy) = g.CellOf(new System.Numerics.Vector3(float.Parse(args[a]), float.Parse(args[a + 1]), 0));
                    int i = g.Index(cx, cy);
                    string who = ses.Units.BlockedBy.TryGetValue(i, out string? b) ? b : "-";
                    Console.WriteLine($"({args[a]},{args[a + 1]}) case {cx},{cy} : bloquée {ses.Units.Paths.IsBlocked(i)} par {who} · zone A {g.ZoneA[i]} · zone B {g.ZoneB[i]} · sol {g.GroundHeight[i]:0}");
                }
                return 0;
            }
            case "solgrille":
            {
                // solgrille <Data> <Mission> x0 y0 x1 y1 : hauteur du sol de chaque case (en mètres ; « -- » hors zone)
                Session ses = new Session(files, args[2], "EASY", false);
                Grid g = ses.Grid!;
                (int ax, int ay) = g.CellOf(new System.Numerics.Vector3(float.Parse(args[3]), float.Parse(args[4]), 0));
                (int bx, int by) = g.CellOf(new System.Numerics.Vector3(float.Parse(args[5]), float.Parse(args[6]), 0));
                for (int y = by; y >= ay; y--)
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder($"{y * 100,6} ");
                    for (int x = ax; x <= bx; x++)
                        sb.Append(!g.Inside(x, y) || g.ZoneA[g.Index(x, y)] == 0 ? "  --" : $"{g.GroundHeight[g.Index(x, y)] / 100,4:0}");
                    Console.WriteLine(sb);
                }
                Console.WriteLine($"{"",6} x de {ax * 100} à {bx * 100}");
                return 0;
            }
            case "boite":
            {
                // boite <Data> <modèle Gfx> : boîte englobante de chaque pièce du modèle, et les points nommés du squelette
                SceneAssets assets = new OpenFD.Game.SceneAssets(files);
                foreach (SceneAssets.Node part in assets.ModelParts(args[2]))
                {
                    Mesh m = assets.LoadMesh("", part.Mesh, "gfx").Mesh;
                    System.Numerics.Vector3 lo = new System.Numerics.Vector3(float.MaxValue), hi = new System.Numerics.Vector3(float.MinValue);
                    for (int i = 0; i < m.VertexCount; i++)
                    {
                        System.Numerics.Vector3 v = System.Numerics.Vector3.Transform(new System.Numerics.Vector3(m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]), part.World);
                        lo = System.Numerics.Vector3.Min(lo, v);
                        hi = System.Numerics.Vector3.Max(hi, v);
                    }
                    Console.WriteLine($"{part.Mesh,-40} ({lo.X:0},{lo.Y:0},{lo.Z:0}) - ({hi.X:0},{hi.Y:0},{hi.Z:0})");
                }
                if (OpenFD.Game.CharacterRig.Build(assets, files, args[2]) is { } rig)
                {
                    System.Numerics.Matrix4x4[] pose = rig.Pose(null, 0);
                    for (int i = 0; i < rig.Names.Length; i++)
                        Console.WriteLine($"  point {rig.Names[i],-30} ({pose[i].Translation.X:0},{pose[i].Translation.Y:0},{pose[i].Translation.Z:0})");
                }
                return 0;
            }
            case "sauvercivil":
            {
                // sauvercivil <Data> <Mission> [secondes] [nom] [suivi] : comme F6 (débogage), sauve ce civil (sinon le premier
                // vivant) ; compte avant et après ; « suivi » : encore 120 s de jeu, position des civils et journal du script
                Session ses = new Session(files, args[2], "EASY");
                float wait = args.Length > 3 ? float.Parse(args[3]) : 10;
                while (ses.Time < wait)
                    ses.Step();
                Civilian? c = ses.Units.Civilians.FirstOrDefault(x => (args.Length > 4 ? x.Name.Equals(args[4], StringComparison.OrdinalIgnoreCase) : !x.Hidden)
                    && x.State is OpenFD.Game.CivilianState.Conscious or OpenFD.Game.CivilianState.Unconscious);
                Console.WriteLine($"avant : {ses.Units.Rescued} sauvé(s)");
                if (c != null)
                    Console.WriteLine($"{c.Name} ({c.State}) : {ses.Units.DebugSave(c)} → {c.State}");
                for (int i = 0; i < 20; i++)
                    ses.Step();
                Console.WriteLine($"après : {ses.Units.Rescued} sauvé(s)");
                if (args.Length > 5)
                {
                    int shown = ses.Units.Log.Count;
                    float end = ses.Time + 120;
                    int k = 0;
                    while (ses.Time < end)
                    {
                        ses.Step();
                        if (k++ % 100 == 0)
                            foreach (Civilian cv in ses.Units.Civilians.Where(x => x.Name.Contains(args[5], StringComparison.OrdinalIgnoreCase)))
                                Console.WriteLine($"t {ses.Time:0} {cv.Name} ({cv.Position.X:0},{cv.Position.Y:0}) {cv.State} caché {cv.Hidden} chemin {cv.Path.Count}");
                    }
                    foreach (string line in ses.Units.Log.Skip(shown))
                        Console.WriteLine(line);
                }
                return 0;
            }
            case "franchir":
            {
                // franchir <Data> <Mission> <x> <y> <secondes> [objet caché…] : après ce temps de jeu (et les objets nommés cachés
                // comme par le script), chemin d'un véhicule (IA, puis conduit par le joueur) et d'un pompier vers (x, y)
                Session ses = new Session(files, args[2], "EASY");
                while (ses.Time < float.Parse(args[5]))
                    ses.Step();
                foreach (string n in args.Skip(6))
                    foreach (LevelObject o in ses.Level.Objects.Where(o => o.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                        ses.Host!.Visibility[o.Id] = false;
                ses.Step();
                System.Numerics.Vector2 dest = new System.Numerics.Vector2(float.Parse(args[3]), float.Parse(args[4]));
                Unit? v = ses.Units.Units.FirstOrDefault(u => u.IsVehicle && !u.IsBoat && !u.Destroyed);
                Unit? f = ses.Units.Units.FirstOrDefault(u => !u.IsVehicle && !u.Hidden && !u.OnBoard);
                void Show(string what, Unit u, bool ok)
                {
                    System.Numerics.Vector2 end = u.Path.Count > 0 ? u.Path.Last() : new System.Numerics.Vector2(u.Position.X, u.Position.Y);
                    Console.WriteLine($"{what} {u.Name} de ({u.Position.X:0},{u.Position.Y:0}) : ordre {ok}, arrive à ({end.X:0},{end.Y:0}), à {System.Numerics.Vector2.Distance(end, dest):0} du but ({u.Status})");
                }
                if (v != null)
                {
                    Show("véhicule (IA)", v, ses.Units.Order(v, dest, refuseUnreachable: false));
                    Show("véhicule (joueur)", v, ses.Units.Drive(v, dest));
                }
                if (f != null)
                    Show("pompier", f, ses.Units.Order(f, dest));
                foreach (string line in ses.Units.Log.Where(l => l.Contains("occupation") || l.Contains("caché")))
                    Console.WriteLine(line);
                return 0;
            }
            case "occupations":
            {
                // occupations <Data> [Mission] : objets avec des cases d'occupation dessinées (nombre, étendue, visible au départ)
                IEnumerable<string> levels = files.List("Design\\Mission\\").Where(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase)
                    && (args.Length < 3 || p.Contains("\\" + args[2] + "\\", StringComparison.OrdinalIgnoreCase)));
                foreach (string lvPath in levels)
                {
                    Level level = Level.Parse(files.Read(lvPath));
                    foreach (LevelObject o in level.Objects)
                    {
                        List<(int Dx, int Dy)> cells = o.OccupationCells;
                        if (cells.Count == 0 || o.Kind != EntityKind.Placeable)
                            continue;
                        ClassFile? oc = null;
                        try { oc = ClassFile.Load(files, "Design\\" + o.ClassPath["design\\".Length..] + ".class"); } catch { }
                        string mode = oc?.Get("OCCUPATION", "Occupation_Mode")?.Trim('"', ' ').ToUpperInvariant() ?? "?";
                        int rect = (oc?.GetInt("OCCUPATION", "Rect_SizeX", 0) ?? 0) * (oc?.GetInt("OCCUPATION", "Rect_SizeY", 0) ?? 0);
                        // seulement les cases qui ne viennent pas de la forme de la classe : rectangle plus grand que prévu
                        if (mode != "RECTANGLE" || cells.Count <= rect)
                            continue;
                        Console.WriteLine($"{Path.GetFileNameWithoutExtension(lvPath),-14} {o.Name,-28} {o.ClassName,-20} {mode} {rect} {cells.Count,4} cases x {cells.Min(c => c.Dx)}..{cells.Max(c => c.Dx)} y {cells.Min(c => c.Dy)}..{cells.Max(c => c.Dy)} visible {o.StartVisible}");
                    }
                }
                return 0;
            }
            case "composant":
            {
                // composant <Data> <Mission> <nom> [tout] : tous les composants de l'objet, en hexadécimal (et texte lisible ; « tout » : sans couper)
                string lvPath = files.List("Design\\Mission\\" + args[2] + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                Level level = Level.Parse(files.Read(lvPath));
                foreach (LevelObject o in level.Objects.Where(o => o.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase)))
                    foreach (Component c in o.Components)
                    {
                        byte[] b = o.ComponentBytes(c).ToArray();
                        string txt = new string(b.Select(x => x >= 32 && x < 127 ? (char)x : '.').ToArray());
                        int show = args.Length > 4 && args[4] == "tout" ? b.Length : Math.Min(b.Length, 96);
                        Console.WriteLine($"{c.Id:X4} ({b.Length}) {BitConverter.ToString(b, 0, show)}");
                        Console.WriteLine($"      {txt[..Math.Min(txt.Length, 160)]}");
                    }
                return 0;
            }
            case "desinc":
            {
                // desinc <Data> <Mission> : le désincarcérateur découpe le portail, d'un côté puis de l'autre
                Session ses = new Session(files, args[2], "EASY");
                for (int i = 0; i < 600 && !ses.Units.Active.Any(u => u.CanExtricate); i++)
                    ses.Step();
                Unit? man = ses.Units.Active.FirstOrDefault(u => u.CanExtricate);
                Opening? gate = ses.Units.Openings.FirstOrDefault(o => o.Extricate);
                if (man == null || gate == null)
                {
                    Console.WriteLine($"désincarcérateur {man?.Name ?? "absent"} · portail {gate?.Name ?? "absent"}");
                    return 0;
                }
                System.Numerics.Vector2 c = new System.Numerics.Vector2(gate.Center.X, gate.Center.Y), side = gate.ExtricateSide!.Value;
                System.Numerics.Vector2 dir = System.Numerics.Vector2.Normalize(side - c);
                foreach (float k in new[] { -600f, 600f })
                {
                    ses.Units.Teleport(man, c + dir * k);
                    ses.Units.PlayerCommand = true;
                    ses.Units.WorkOnOpening(man, gate);
                    ses.Units.PlayerCommand = false;
                    Console.WriteLine($"côté {(k > 0 ? "icône" : "opposé")} ({man.Position.X:0},{man.Position.Y:0}) : {man.Status}");
                    for (int i = 0; i < 300 && man.Opening != null; i++)
                    {
                        ses.Step();
                        if (i % 10 == 0)
                            Console.WriteLine($"  t {ses.Time:0.0} ({man.Position.X:0},{man.Position.Y:0}) {man.Status} vie {gate.Life:0}");
                    }
                }
                Console.WriteLine($"portail {gate.State} · {string.Join(" | ", ses.Units.Log.TakeLast(3))}");
                return 0;
            }
            case "autour":
            {
                // autour <Data> <Mission> <x> <y> [r] : cases autour d'un point (hauteur du sol, l : libre, # : fermée)
                Session ses = new Session(files, args[2], "EASY", false);
                Grid g = ses.Grid!;
                (int cx, int cy) = g.CellOf(new System.Numerics.Vector3(float.Parse(args[3]), float.Parse(args[4]), 0));
                int r = args.Length > 5 ? int.Parse(args[5]) : 4;
                for (int y = cy + r; y >= cy - r; y--)
                {
                    System.Text.StringBuilder line = new System.Text.StringBuilder($"{y * 100,6} ");
                    for (int x = cx - r; x <= cx + r; x++)
                        line.Append(!g.Inside(x, y) ? "   ----  "
                            : Environment.GetEnvironmentVariable("FD3_ZONES") == "1" ? $"{g.ZoneA[g.Index(x, y)],4}/{g.ZoneB[g.Index(x, y)],-4}"
                            : $"{g.GroundHeight[g.Index(x, y)],6:0}{(ses.Units.WalkableCell(x, y) ? 'l' : '#')}{(x == cx && y == cy ? '*' : ' ')}");
                    Console.WriteLine(line.ToString());
                }
                foreach (Teleport t in ses.Units.Teleports)
                    Console.WriteLine($"{t.Name} : cases {t.CellA % g.Width},{t.CellA / g.Width} et {t.CellB % g.Width},{t.CellB / g.Width}");
                return 0;
            }
            case "tutorenfort":
            {
                // tutorenfort <Data> : étape 19 du tutoriel (renfort accordé), clic sur la caserne, arrivée au garage (objectif)
                Session ses = new Session(files, "Tutorial", "EASY");
                for (int i = 0; i < 50; i++)
                    ses.Step();
                ses.Scripts?.TryRun("Etap_19_1");
                for (int i = 0; i < 100; i++)
                    ses.Step();
                Console.WriteLine($"accordés {ses.Units.GrantsAvailable} · casernes {string.Join(",", ses.Units.ChoosableTeams)} · unités {ses.Units.Active.Count()}");
                string? team = ses.Units.ChoosableTeams.FirstOrDefault();
                if (team != null)
                    Console.WriteLine($"clic sur {team} : {ses.Units.ChooseReinforcement(team)}");
                for (int i = 0; i < 1200; i++)
                {
                    ses.Step();
                    if (i % 100 == 0)
                        Console.WriteLine($"t {ses.Time:0} : {string.Join(" ; ", ses.Units.Active.Where(u => u.IsVehicle).Select(u => $"{u.Name} ({u.Position.X:0},{u.Position.Y:0}) type {ses.Units.UnitType(u)}"))}");
                }
                foreach (string l in ses.Units.Log.Where(l => l.Contains("renfort", StringComparison.OrdinalIgnoreCase) || l.Contains("arrivée")))
                    Console.WriteLine("  " + l);
                if (ses.Units.Triggers.FirstOrDefault(t => t.Name == "trigger_20") is { } tr)
                    Console.WriteLine($"trigger_20 ({tr.Position.X:0},{tr.Position.Y:0}) actif {tr.Enabled} script {tr.Script} demi {tr.Half}");
                foreach ((string key, bool success) in ses.Host?.ObjectiveEvents ?? new List<(string key, bool success)>())
                    Console.WriteLine($"  objectif {key} : {success}");
                return 0;
            }
            case "loc":
            {
                // loc <Data> : langue et nombre de textes chargés ; clés « &… » des missions (campagne, .mis) sans texte
                Localization loc = Localization.Load(args[1]);
                Console.WriteLine($"langue {loc.Language} · {loc.Count} textes · édition {Localization.StartIni(args[1], "License")}");
                System.Text.RegularExpressions.Regex key = new System.Text.RegularExpressions.Regex(@"&([A-Za-z0-9_]+)");
                Dictionary<string, string> missing = new Dictionary<string, string>();
                int found = 0;
                foreach (string f in files.List("Design\\Mission\\").Where(p => p.EndsWith(".mis", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)))
                    foreach (System.Text.RegularExpressions.Match mt in key.Matches(System.Text.Encoding.Latin1.GetString(files.Read(f))))
                    {
                        string k = mt.Groups[1].Value;
                        if (loc.Get(k) != k)
                            found++;
                        else
                            missing.TryAdd(k, f);
                    }
                Console.WriteLine($"{found} clés trouvées, {missing.Count} sans texte :");
                foreach (KeyValuePair<string, string> kv in missing.Take(40))
                    Console.WriteLine($"  {kv.Key}  ({kv.Value})");
                return 0;
            }
            case "aller":
            {
                // aller <Data> <Mission> <x> <y> : comme FD3_ALLER (à 12 s, le premier pompier à pied), suivi de sa marche
                Session ses = new Session(files, args[2], "EASY");
                Unit? w = null;
                for (int i = 0; i < 3000; i++)
                {
                    ses.Step();
                    if (w == null && ses.Time > 12 && ses.Units.Active.FirstOrDefault(u => !u.IsVehicle && !u.OnBoard) is { } f)
                    {
                        w = f;
                        ses.Units.PlayerCommand = true;
                        Console.WriteLine($"ordre : {ses.Units.Order(w, new System.Numerics.Vector2(float.Parse(args[3]), float.Parse(args[4])))}");
                        ses.Units.PlayerCommand = false;
                    }
                    if (w != null && i % 20 == 0)
                        Console.WriteLine($"t {ses.Time:0} ({w.Position.X:0},{w.Position.Y:0},{w.Position.Z:0}) {w.Status}");
                    if (w != null && w.Path.Count == 0 && w.Stairs == null && ses.Time > 20)
                        break;
                }
                return 0;
            }
            case "tram":
            {
                // tram <Data> : Street, à 15 s le camion E9 est posé sur la voie du tram 1 ; suivi du tram et de son arrêt
                Session ses = new Session(files, "Street", "EASY");
                Unit? truck = null;
                for (int i = 0; i < 900; i++)
                {
                    ses.Step();
                    if (truck == null && ses.Time > 40 && ses.Units.Active.FirstOrDefault(u => u.IsVehicle) is { } v)
                    {
                        truck = v;
                        ses.Units.Teleport(v, new System.Numerics.Vector2(10232, 6000));
                        v.Path.Clear();
                        v.GoTo = null;
                    }
                    if (truck != null && ses.Time > 75 && ses.Time < 75.15)
                        ses.Units.Teleport(truck, new System.Numerics.Vector2(9000, 6000));   // il quitte la voie : le tram repart
                    if (i % 20 == 0 && ses.Host != null)
                    {
                        uint cc = (uint)(ses.Scripts?.Globals.GetValueOrDefault("CC1").AsInt ?? 0);
                        string pos = ses.Host.MovedObjects.TryGetValue(cc, out System.Numerics.Vector2 p) ? $"({p.X:0},{p.Y:0})" : "-";
                        Console.WriteLine($"t {ses.Time:0} tram {pos} enMarche {ses.Scripts?.Globals.GetValueOrDefault("CC1enMarche").AsInt} déclencheur {ses.Units.LastTrigger?.Name} groupe {string.Join(",", ((System.Collections.IDictionary)typeof(MissionHost).GetField("_groups", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(ses.Host)!).Values.Cast<List<long>>().Select(l => l.Count))} camion {(truck == null ? "-" : $"({truck.Position.X:0},{truck.Position.Y:0})")}");
                    }
                }
                return 0;
            }
            case "suivreescalier":
            {
                // suivreescalier <Data> : Street, un pompier posé dans le couloir de la maison au sud-est emmène un civil dans la rue
                // par l'escalier ; positions du pompier et du civil
                Session ses = new Session(files, "Street", "EASY");
                for (int i = 0; i < 400; i++)
                    ses.Step();
                Unit? man = ses.Units.Active.FirstOrDefault(u => !u.IsVehicle && !u.IsCanon && u.Nozzle != null);
                Civilian? civ = ses.Units.Civilians.Where(c => c.State == CivilianState.Conscious).OrderBy(c => System.Numerics.Vector2.Distance(new(c.Position.X, c.Position.Y), new(7684, 3218))).FirstOrDefault();
                if (man == null || civ == null)
                    return 0;
                ses.Units.Teleport(man, new System.Numerics.Vector2(7750, 3218));
                civ.Position = new System.Numerics.Vector3(7650, 3218, ses.Units.GroundZ(new(7650, 3218)));
                civ.Leader = man;
                ses.Units.PlayerCommand = true;
                ses.Units.Order(man, new System.Numerics.Vector2(8900, 3300));
                ses.Units.PlayerCommand = false;
                for (int i = 0; i < 400; i++)
                {
                    ses.Step();
                    if (i % 10 == 0)
                        Console.WriteLine($"t {ses.Time:0.0} pompier ({man.Position.X:0},{man.Position.Y:0},{man.Position.Z:0}){(man.Stairs != null ? " escalier" : "")} · civil ({civ.Position.X:0},{civ.Position.Y:0},{civ.Position.Z:0}) chemin {civ.FollowPath.Count}");
                }
                return 0;
            }
            case "capacites":
            {
                // capacites <Data> <Mission>… : objets dont le composant A026 (capacités propres à l'objet) n'est pas nul
                foreach (string mis in args.Skip(2))
                {
                    string lvPath = files.List("Design\\Mission\\" + mis + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                    Level level = Level.Parse(files.Read(lvPath));
                    foreach (LevelObject o in level.Objects)
                    {
                        if (o.GetComponent(0xA026) is not { } c)
                            continue;
                        byte[] b = o.ComponentBytes(c).ToArray();
                        int i = 22;
                        if (b.Length < i + 30 || b.Skip(i).All(x => x == 0 || x == 0xAA || x == 0xFF || x == 0xCC || x == 0xBB || x == 4 || x == 9))
                            continue;
                        Console.WriteLine($"{mis,-10} {o.Name,-30} {o.ClassPath,-50} {BitConverter.ToString(b, i, b.Length - i)}");
                    }
                }
                return 0;
            }
            case "visible":
            {
                // visible <Data> <Mission> <nom>… : visibilité de départ et octets du bloc A001 qui suivent « AA AA FF FF 0A »
                string lvPath = files.List("Design\\Mission\\" + args[2] + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                Level level = Level.Parse(files.Read(lvPath));
                foreach (LevelObject o in level.Objects.Where(o => args.Skip(3).Any(n => o.Name.Contains(n, StringComparison.OrdinalIgnoreCase))))
                {
                    string bytes = "";
                    if (o.GetComponent(0xA001) is { } c)
                    {
                        byte[] b = o.ComponentBytes(c).ToArray();
                        int i = ((ReadOnlySpan<byte>)b).LastIndexOf(new byte[] { 0xAA, 0xAA, 0xFF, 0xFF, 0x0A });
                        if (i >= 0)
                            bytes = BitConverter.ToString(b, i, Math.Min(24, b.Length - i));
                    }
                    Console.WriteLine($"{o.Name,-26} visible {o.StartVisible} · composants {string.Join(",", o.Components.Select(k => k.Id.ToString("X4")))} · {bytes}");
                }
                return 0;
            }
            case "ouverturescachees":
            {
                // ouverturescachees <Data> <Mission>… : portes et débris cachés pour toute la partie (jamais montrés par le script)
                foreach (string mis in args.Skip(2))
                {
                    Session ses = new Session(files, mis, "EASY", false);
                    Console.WriteLine($"{mis} : {string.Join(", ", ses.Units.HiddenOpenings)} ({ses.Units.Openings.Count} ouvertures)");
                }
                return 0;
            }
            case "caches":
            {
                // caches <Data> <Mission> : objets posés cachés au départ (octet de visibilité à 0), par classe
                string lvPath = files.List("Design\\Mission\\" + args[2] + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                Level level = Level.Parse(files.Read(lvPath));
                foreach (IGrouping<string, LevelObject> g in level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform && !o.StartVisible).GroupBy(o => o.ClassPath))
                    Console.WriteLine($"{g.Count(),4} {g.Key} : {string.Join(", ", g.Take(4).Select(o => o.Name + " " + o.Id))}");
                return 0;
            }
            case "canadair":
            {
                // canadair <Data> <Mission> [durée] : aide aérienne accordée par le script, puis appelée sur le feu le plus fort
                Session ses = new Session(files, args[2], "EASY");
                Console.WriteLine($"caserne aérienne {ses.Units.AirTeam} : délai {ses.Units.AirEta} s, rayon {ses.Units.AirRadius}, puissance {ses.Units.AirPower}, imprécision {ses.Units.AirRandom}, modèle {ses.Units.AirModel}");
                int steps = (int)((args.Length > 3 ? float.Parse(args[3]) : 900) / Session.Dt);
                bool called = false;
                for (int i = 0; i <= steps; i++)
                {
                    ses.Step();
                    if (i % 50 == 0)
                        ses.Host?.EndCinematic();
                    if (i == 600 && !called && ses.Units.AirGrants == 0)
                        ses.Units.GrantAirSupport();   // (essai : accordée d'office à 60 s si le script ne l'a pas fait)
                    if (ses.Units.AirAvailable && !called)
                    {
                        called = true;
                        OpenFD.Fire.HotPoint hot = ses.Fire.HotPoints.Where(h => h.IsBurning).OrderByDescending(h => h.Intensity).First();
                        System.Numerics.Vector2 c = new(hot.Position.X, hot.Position.Y);
                        int before = ses.Fire.HotPoints.Count(h => h.IsBurning && System.Numerics.Vector2.Distance(new(h.Position.X, h.Position.Y), c) < ses.Units.AirRadius);
                        Console.WriteLine($"t {ses.Time:0} accordée ; appel sur {hot.Name} ({c.X:0},{c.Y:0}) : {before} foyers en feu dans le rayon · {ses.Units.CallAirStrike(c)}");
                    }
                    if (called && ses.Units.Strike is { } st && i % 10 == 0)
                    {
                        int n = ses.Fire.HotPoints.Count(h => h.IsBurning && System.Numerics.Vector2.Distance(new(h.Position.X, h.Position.Y), st.Drop) < ses.Units.AirRadius);
                        Console.WriteLine($"t {ses.Time:0.0} passage {st.Pass:0.0} largué {st.Dropped} · {n} foyers en feu dans le rayon du largage");
                    }
                }
                Console.WriteLine(string.Join("\n", ses.Units.Log.Where(l => l.Contains("aérienne") || l.Contains("bombardier") || l.Contains("largage"))));
                return 0;
            }
            case "camionia":
            {
                // camionia <Data> <Mission> [durée] : ce que font les véhicules à lance, et le feu le plus proche (distance, angle)
                Session ses = new Session(files, args[2], "EASY");
                int steps = (int)((args.Length > 3 ? float.Parse(args[3]) : 400) / Session.Dt);
                for (int i = 0; i <= steps; i++)
                {
                    ses.Step();
                    if (i % 50 == 0)
                        ses.Host?.EndCinematic();
                    if (i % 200 != 0)
                        continue;
                    foreach (Unit v in ses.Units.Units.Where(v => v.IsVehicle && v.Nozzle != null && !v.OnBoard && !v.Hidden))
                    {
                        OpenFD.Fire.HotPoint? h = ses.Fire.HotPoints.Where(x => x.IsBurning).OrderBy(x => System.Numerics.Vector3.Distance(x.Position, v.Position)).FirstOrDefault();
                        string near = h == null ? "aucun feu" : $"feu le plus proche {h.Name} à {System.Numerics.Vector3.Distance(h.Position, v.Position):0} angle {MathF.IEEERemainder(MathF.Atan2(h.Position.Y - v.Position.Y, h.Position.X - v.Position.X) - v.Heading, 2 * MathF.PI):0.00} · {ses.Units.VehicleAutoDiag(v, h)}";
                        Console.WriteLine($"t {ses.Time,5:0} {v.Name,-22} [{v.Status}{(v.Target is { } tg ? $" dz {tg.Position.Z - v.Position.Z:0}" : "")}] eau {v.Nozzle!.Reserve:0} portée {v.Nozzle.RangeMin:0}-{v.Nozzle.RangeMax:0} · {near}");
                    }
                }
                return 0;
            }
            case "vitesses":
            {
                // vitesses <Data> <Mission> : vitesse réelle des véhicules (unités par seconde) pendant leur arrivée
                Session ses = new Session(files, args[2], "EASY");
                Dictionary<Unit, (float dist, float time, System.Numerics.Vector3 last)> m = new();
                for (int i = 0; i < 1200; i++)
                {
                    ses.Step();
                    if (i % 50 == 0)
                        ses.Host?.EndCinematic();
                    foreach (Unit v in ses.Units.Units.Where(v => v.IsVehicle && !v.Hidden))
                    {
                        if (!m.TryGetValue(v, out var e))
                        {
                            m[v] = (0, 0, v.Position);
                            continue;
                        }
                        float d = System.Numerics.Vector2.Distance(new(v.Position.X, v.Position.Y), new(e.last.X, e.last.Y));
                        m[v] = d > 0.01f ? (e.dist + d, e.time + Session.Dt, v.Position) : (e.dist, e.time, v.Position);
                    }
                }
                foreach ((Unit v, (float dist, float time, System.Numerics.Vector3 last) e) in m)
                    Console.WriteLine($"{v.Name,-28} vitesse classe {v.Speed:0} · parcouru {e.dist:0} en {e.time:0.0} s → {(e.time > 0 ? e.dist / e.time : 0):0} u/s");
                Console.WriteLine($"grille {ses.Grid!.Width}×{ses.Grid.Height}");
                return 0;
            }
            case "endurance":
            {
                // endurance <Data> <Mission> : un pompier posé dans un feu ; endurance et alertes jusqu'à l'évanouissement
                Session ses = new Session(files, args[2], "EASY");
                for (int i = 0; i < 400; i++)
                    ses.Step();
                Unit man = ses.Units.Units.First(u => !u.IsVehicle && !u.OnBoard && u.StaminaMax > 0 && u.ScriptHeatCoef > 0 && u.HeatCoef > 0 && u.Nozzle != null);
                OpenFD.Fire.HotPoint hot = ses.Fire.HotPoints.Where(h => h.IsBurning && h.Class.UnitHeatDamage > 0).OrderByDescending(h => h.Intensity).First();
                ses.Units.Teleport(man, new(hot.Position.X + 60, hot.Position.Y));
                int logs = ses.Units.Log.Count;
                for (int i = 0; i < 3000 && !man.Unconscious; i++)
                {
                    if (i % 50 == 0)
                        hot = ses.Fire.HotPoints.Where(h => h.IsBurning && h.Class.UnitHeatDamage > 0).OrderByDescending(h => h.Intensity * h.Class.UnitHeatDamage).First();
                    man.Path.Clear();
                    man.Position = new System.Numerics.Vector3(hot.Position.X + 60, hot.Position.Y, hot.Position.Z);
                    ses.Step();
                    foreach ((string label, System.Numerics.Vector3? pos) in ses.Units.Sounds.Where(x => x.label == "VOCAL_FIREMAN_CRITICAL"))
                        Console.WriteLine($"t {ses.Time:0.0} SON {label} endurance {man.Stamina:0.0}/{man.StaminaMax:0}");
                    ses.Units.Sounds.Clear();
                    if (i % 50 == 0)
                        Console.WriteLine($"t {ses.Time:0.0} endurance {man.Stamina:0.0} {man.Name} feu {man.InFire} {hot.Name} {hot.Intensity:0} chaleur {hot.Class.UnitHeatDamage} coef {man.HeatCoef} {man.ScriptHeatCoef} pos {man.Position} feu {hot.Position} caché {man.Hidden}");
                }
                Console.WriteLine(string.Join("\n", ses.Units.Log.Skip(logs).Where(l => l.Contains("pompier"))));
                return 0;
            }
            case "bulldo":
            {
                // bulldo <Data> <Mission> <débris> x y : un bulldozer part de (x, y) et dégage les débris à coups de pelle
                Session ses = new Session(files, args[2], "EASY");
                for (int i = 0; i < 100; i++)
                    ses.Step();
                Opening? d = ses.Units.Openings.FirstOrDefault(o => o.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
                if (d == null)
                    return 1;
                Unit dozer = ses.Units.SpawnScripted("Data/Design/Unit/Firevehicle/Veh_P_bulldozer.class", "bulldo", new System.Numerics.Vector3(float.Parse(args[4]), float.Parse(args[5]), 0))!;
                Unit? axe = ses.Units.Units.FirstOrDefault(u => u.CanBreak && !u.OnBoard);
                if (axe != null)
                {
                    ses.Units.WorkOnOpening(axe, d);
                    Console.WriteLine($"pompier à hache : {axe.Status}");
                }
                ses.Units.WorkOnOpening(dozer, d);
                Console.WriteLine($"débris {d.Name} ({d.Center.X:0},{d.Center.Y:0}) vie {d.Life}/{d.MaxLife} démolir {d.Demolish} ; bulldozer {dozer.CanDemolish} : {dozer.Status} chemin {dozer.Path.Count}");
                for (int i = 0; i < 600 && d.State != OpeningState.Broken; i++)
                {
                    ses.Step();
                    if (i % 10 == 0)
                        Console.WriteLine($"t {ses.Time,5:0.0} bulldozer ({dozer.Position.X:0},{dozer.Position.Y:0}) cap {dozer.Heading:0.00} {dozer.Status} pelle {dozer.Shove:0.00} coups {dozer.Blows}");
                }
                Console.WriteLine($"fin : {d.State} · {string.Join(" · ", ses.Units.Log.TakeLast(2))}");
                return 0;
            }
            case "echelle":
            {
                // echelle <Data> <Mission> <fenêtre> x y : camion échelle garé vers (x, y), échelle dressée, un pompier monte,
                // casse la fenêtre, entre, puis redescend jusqu'au point de départ du camion
                Session ses = new Session(files, args[2], "EASY");
                for (int i = 0; i < 400; i++)
                    ses.Step();
                Opening? d = ses.Units.Openings.FirstOrDefault(o => o.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
                if (d == null)
                    return 1;
                System.Numerics.Vector3 at = new System.Numerics.Vector3(float.Parse(args[4]), float.Parse(args[5]), 0);
                Unit truck = ses.Units.Units.FirstOrDefault(u => u.IsAerial && !u.OnBoard) ?? ses.Units.SpawnScripted("Data/Design/Unit/Firevehicle/Veh_P_aerial_ua.class", "echelle", at)!;
                Unit man = ses.Units.Units.Where(u => u.CanLadder && u.CanBreak && !u.OnBoard && !u.IsVehicle).OrderBy(u => System.Numerics.Vector3.Distance(u.Position, at)).First();
                if (args.Length > 7)
                    man.Position = new System.Numerics.Vector3(float.Parse(args[6]), float.Parse(args[7]), 0);
                Console.WriteLine($"camion {truck.Name} ({truck.Position.X:0},{truck.Position.Y:0}) pivot {truck.LadderPivot} charnière {truck.LadderHinge} repliée {truck.LadderRest:0} ; fenêtre haute {ses.Units.IsHighWindow(d)}");
                Console.WriteLine($"déploiement : {ses.Units.DeployLadder(truck, d)} · {truck.Status}");
                bool ordered = false, back = false;
                for (int i = 0; i < 2400; i++)
                {
                    ses.Step();
                    if (truck.Ladder is { Ready: true } lr && !ordered)
                    {
                        ordered = true;
                        System.Numerics.Matrix4x4 turn = System.Numerics.Matrix4x4.CreateTranslation(-truck.LadderPivot) * System.Numerics.Matrix4x4.CreateRotationZ(lr.Yaw) * System.Numerics.Matrix4x4.CreateTranslation(truck.LadderPivot);
                        System.Numerics.Matrix4x4 lad = System.Numerics.Matrix4x4.CreateTranslation(0, -lr.Extend, 0) * System.Numerics.Matrix4x4.CreateTranslation(-truck.LadderHinge) * System.Numerics.Matrix4x4.CreateRotationX(-lr.Pitch) * System.Numerics.Matrix4x4.CreateTranslation(truck.LadderHinge) * turn;
                        System.Numerics.Vector3 tipModel = new System.Numerics.Vector3(truck.LadderHinge.X, truck.LadderHinge.Y - truck.LadderRest, truck.LadderHinge.Z);
                        System.Numerics.Vector3 drawn = UnitManager.VehicleToWorld(truck, System.Numerics.Vector3.Transform(tipModel, lad));
                        Console.WriteLine($"cap du camion {truck.Heading:0.00} ; bout dessiné {drawn} ; bout voulu {lr.Tip} ; base {lr.Base} ; fenêtre {d.Center}");
                        if (args.Length > 9)
                        {
                            ses.Units.PlayerCommand = true;
                            ses.Units.Order(man, new(float.Parse(args[8]), float.Parse(args[9])));
                            ses.Units.PlayerCommand = false;
                        }
                        else
                            ses.Units.WorkOnOpening(man, d);
                        Console.WriteLine($"t {ses.Time:0} ordre au pompier : {man.Status} chemin {man.Path.Count} : " + string.Join(" → ", man.Path.Select(p => $"({p.X:0},{p.Y:0})")));
                    }
                    if (ordered && !back && d.State == OpeningState.Broken && man.OnLadder == null && !man.Moving && man.Opening == null)
                    {
                        back = true;
                        ses.Units.Order(man, new(at.X + 600, at.Y + 300));
                        Console.WriteLine($"t {ses.Time:0} ordre de redescendre : {man.Status} chemin {man.Path.Count}");
                    }
                    if (i % 20 == 0)
                        Console.WriteLine($"t {ses.Time,5:0.0} camion ({truck.Position.X:0},{truck.Position.Y:0}) {truck.Status} · échelle {(truck.Ladder is { } l ? $"levée {l.Raise:0.00} long {l.Extend:0}/{l.ExtendWanted:0} lacet {l.Yaw:0.00} incl {l.Pitch:0.00} base {l.Base} bout {l.Tip}" : "-")} · pompier ({man.Position.X:0},{man.Position.Y:0},{man.Position.Z:0}) {man.LadderStep} {man.Status} · fenêtre {d.State}");
                }
                return 0;
            }
            case "sols":
            {
                // sols <Data> <Mission> x0 y0 x1 y1 : hauteur du sol (centaines, ou . si case fermée) case par case
                Session ses = new Session(files, args[2], "EASY", false);
                Grid gr = ses.Grid!;
                int x0 = int.Parse(args[3]) / 100, y0 = int.Parse(args[4]) / 100, x1 = int.Parse(args[5]) / 100, y1 = int.Parse(args[6]) / 100;
                for (int y = y1; y >= y0; y--)
                {
                    System.Text.StringBuilder line = new System.Text.StringBuilder($"{y * 100,6} ");
                    for (int x = x0; x <= x1; x++)
                    {
                        if (!gr.Inside(x, y))
                            line.Append("  ");
                        else if (!ses.Units.Paths.FreeCell(x, y))
                            line.Append(" .");
                        else
                            line.Append($"{(int)(ses.Units.GroundZ(new((x + 0.5f) * 100, (y + 0.5f) * 100)) / 100),2}");
                    }
                    Console.WriteLine(line.ToString());
                }
                foreach (Civilian c in ses.Units.Civilians)
                    Console.WriteLine($"civil {c.Name} ({c.Position.X:0},{c.Position.Y:0},{c.Position.Z:0})");
                return 0;
            }
            case "fenetres":
            {
                // fenetres <Data> <Mission> : hauteur des fenêtres au-dessus du sol (pour savoir ce qui est hors d'atteinte)
                Session ses = new Session(files, args[2], "EASY");
                foreach (Opening o in ses.Units.Openings.Where(o => o.IsWindow).OrderByDescending(o => o.Center.Z).Take(int.Parse(args.Length > 3 ? args[3] : "40")))
                {
                    (int cx, int cy) = ses.Grid!.CellOf(o.Center);
                    float g = ses.Grid.Inside(cx, cy) ? ses.Grid.GroundHeight[ses.Grid.Index(cx, cy)] : 0;
                    Console.WriteLine($"{o.Name,-34} centre ({o.Center.X:0},{o.Center.Y:0},{o.Center.Z:0}) pivot z {o.Position.Z:0} · sol {g:0} · cassable {o.Breakable}");
                }
                return 0;
            }
            case "vieobjet":
            {
                // vieobjet <Data> <Mission> <numéro> [durée] : vie de l'objet (ENT_GetDamage) et objectifs, toutes les 10 s
                Session ses = new Session(files, args[2], "EASY");
                uint id = uint.Parse(args[3]);
                int steps = (int)((args.Length > 4 ? float.Parse(args[4]) : 300) / Session.Dt);
                for (int i = 0; i <= steps; i++)
                {
                    if (i % 100 == 0)
                        Console.WriteLine($"t {ses.Time,5:0} vie {ses.Explosions.LifeOf(id)?.ToString("0.0") ?? "-"} · " + string.Join(" · ", ses.Host!.Objectives.Select(o => $"{o.Text} {o.Value:0}/{o.Max:0} état {o.Status}")) + (ses.Host.Outcome is { } oc ? $" · FIN {(oc ? "victoire" : "défaite")}" : ""));
                    if (i % 50 == 0)
                        ses.Host?.EndCinematic();
                    ses.Step();
                }
                return 0;
            }
            case "chrono":
            {
                // chrono <Data> <Mission> <porte> : durée de l'ordre « casser cette porte » donné au premier pompier à hache
                Session ses = new Session(files, args[2], "EASY");
                for (int i = 0; i < 300; i++)
                    ses.Step();
                Opening? d = ses.Units.Openings.FirstOrDefault(o => o.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
                Unit? axe = ses.Units.Units.FirstOrDefault(x => x.CanBreak && !x.OnBoard);
                if (d == null || axe == null)
                    return 1;
                for (int k = 0; k < 3; k++)
                {
                    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                    ses.Units.WorkOnOpening(axe, d);
                    Console.WriteLine($"{axe.Name} → {d.Name} : {sw.ElapsedMilliseconds} ms, {axe.Status}, chemin {axe.Path.Count}");
                }
                System.Diagnostics.Stopwatch sw2 = System.Diagnostics.Stopwatch.StartNew();
                ses.Units.Order(axe, new(d.Position.X - 300, d.Position.Y));
                Console.WriteLine($"simple déplacement près de la porte : {sw2.ElapsedMilliseconds} ms, {axe.Status}");
                Console.WriteLine($"{axe.Name} en ({axe.Position.X:0},{axe.Position.Y:0},{axe.Position.Z:0}) ; porte ({d.Position.X:0},{d.Position.Y:0}) centre z {d.Center.Z:0} cassable {d.Breakable} état {d.State} au sol {d.OnGround}");
                Console.WriteLine(ses.Units.AsciiMap((int)d.Position.X - 1500, (int)d.Position.Y - 1200, (int)d.Position.X + 1500, (int)d.Position.Y + 1200, new List<System.Numerics.Vector2> { new(d.Position.X, d.Position.Y) }));
                return 0;
            }
            case "chemin":
            {
                // chemin <Data> <Mission> x0 y0 x1 y1 [porte_ouverte…] : chemin d'un pompier ordinaire (portes nommées ouvertes)
                Session ses = new Session(files, args[2], "EASY", false);
                foreach (string n in args.Skip(7))
                    if (ses.Units.Openings.FirstOrDefault(o => o.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } d)
                        ses.Units.SetOpeningState(d, OpenFD.Game.OpeningState.Open);
                Unit u = ses.Units.SpawnScripted("Data/Design/Unit/Fireman/fireman_sp.class", "essai", new System.Numerics.Vector3(float.Parse(args[3]), float.Parse(args[4]), 0))!;
                bool ok = ses.Units.Order(u, new(float.Parse(args[5]), float.Parse(args[6])));
                Console.WriteLine($"ordre {ok} : {u.Status} ; " + string.Join(" → ", u.Path.Select(p => $"({p.X:0},{p.Y:0})")));
                List<System.Numerics.Vector2> pts = new List<System.Numerics.Vector2> { new(u.Position.X, u.Position.Y) };
                pts.AddRange(u.Path);
                Console.WriteLine(ses.Units.AsciiMap(Environment.GetEnvironmentVariable("FD3_ZONE") is { } zz ? int.Parse(zz.Split(",")[0]) : 1200, Environment.GetEnvironmentVariable("FD3_ZONE") is { } z2 ? int.Parse(z2.Split(",")[1]) : 10600, Environment.GetEnvironmentVariable("FD3_ZONE") is { } z3 ? int.Parse(z3.Split(",")[2]) : 4200, Environment.GetEnvironmentVariable("FD3_ZONE") is { } z4 ? int.Parse(z4.Split(",")[3]) : 13800, pts));
                return 0;
            }
            case "ranimer":
            {
                // ranimer <Data> <Mission> : un pompier inconscient est-il ranimé par un collègue (pompier ou médecin) ?
                Session ses = new Session(files, args[2], "EASY");
                void Run(float sec)
                {
                    for (int i = 0; i < (int)(sec / Session.Dt); i++)
                        ses.Step();
                }
                Run(40);
                List<Unit> men = ses.Units.Units.Where(u => !u.IsVehicle && !u.Hidden && !u.OnBoard).ToList();
                foreach (Unit who in men.Skip(1).Take(3))
                {
                    Unit victim = men[0];
                    victim.Unconscious = true;
                    victim.Stamina = 0;
                    ses.Units.ReviveColleague(who, victim);
                    Console.WriteLine($"{who.ClassPath} → {victim.ClassPath} ({System.Numerics.Vector3.Distance(who.Position, victim.Position):0}) : {who.Status}");
                    for (int t = 0; t < 60 && victim.Unconscious; t++)
                        Run(1);
                    Console.WriteLine($"   ranimé : {!victim.Unconscious} · {who.Status}");
                    victim.Unconscious = false;
                }
                return 0;
            }
            case "auto":
            {
                // auto <Data> <Mission> : un pompier et un camion posés près d'un feu visible l'éteignent-ils d'eux-mêmes ?
                Session ses = new Session(files, args[2], "EASY");
                void Run(float sec)
                {
                    for (int i = 0; i < (int)(sec / Session.Dt); i++)
                        ses.Step();
                }
                Run(40);
                foreach (Unit u in ses.Units.Units.Where(u => u.Nozzle != null && !u.Hidden && !u.OnBoard).GroupBy(u => u.IsVehicle).Select(g => g.First()).ToList())
                {
                    // case libre au sol à 6–9 m d'un feu, d'où on le voit
                    (Fire.HotPoint h, System.Numerics.Vector2 p, int a) spot = (from h in ses.Fire.HotPoints where h.IsBurning && MathF.Abs(h.Position.Z - u.Position.Z) < 200
                                from a in Enumerable.Range(0, 16) let r = u.IsVehicle ? 1500f : (float.TryParse(Environment.GetEnvironmentVariable("FD3_AUTO_R"), out float rr) ? rr : 700f)
                                let p = new System.Numerics.Vector2(h.Position.X + r * MathF.Cos(a * 0.39f), h.Position.Y + r * MathF.Sin(a * 0.39f))
                                select (h, p, a)).FirstOrDefault(x => ses.Units.AutoDiagSpot(u, x.p, x.h));
                    if (spot.h == null)
                    {
                        Console.WriteLine($"{u.Name} : aucun emplacement d'essai");
                        continue;
                    }
                    ses.Units.Teleport(u, spot.p);
                    u.Target = null;
                    u.Path.Clear();
                    u.GoToVehicle = null;
                    if (u.IsVehicle)
                    {
                        u.Heading = MathF.Atan2(spot.h.Position.Y - spot.p.Y, spot.h.Position.X - spot.p.X) + 2.2f;
                        ses.Units.Attack(u, spot.h);
                        for (int k = 0; k < 14; k++)
                        {
                            Run(1);
                            Console.WriteLine($"   {ses.Time:0} s ({u.Position.X:0},{u.Position.Y:0}) cap {u.Heading:0.00} {u.Status}");
                        }
                    }
                    float i0 = spot.h.Intensity;
                    Run(6);
                    Console.WriteLine($"{u.Name} à {System.Numerics.Vector2.Distance(spot.p, new(spot.h.Position.X, spot.h.Position.Y)):0} de {spot.h.Name} : {u.Status} ; intensité {i0:0} → {spot.h.Intensity:0}");
                }
                return 0;
            }
            case "conduite":
            {
                // conduite <Data> <Mission> : le camion part vers un point derrière lui (marche arrière, puis virage)
                Session ses = new Session(files, args[2], "EASY");
                void Run(float sec)
                {
                    for (int i = 0; i < (int)(sec / Session.Dt); i++)
                        ses.Step();
                }
                Run(30);
                Unit truck = ses.Units.Units.First(u => u.IsVehicle && u.Nozzle != null);
                System.Numerics.Vector2 back = new System.Numerics.Vector2(truck.Position.X, truck.Position.Y) - new System.Numerics.Vector2(MathF.Cos(truck.Heading), MathF.Sin(truck.Heading)) * 1500;
                Console.WriteLine($"départ ({truck.Position.X:0},{truck.Position.Y:0}) cap {truck.Heading:0.00} → ({back.X:0},{back.Y:0}) : {ses.Units.Drive(truck, back)} {truck.Status} chemin {truck.Path.Count}");
                for (int k = 0; k < 14; k++)
                {
                    Run(1);
                    Console.WriteLine($"  {ses.Time:0} s ({truck.Position.X:0},{truck.Position.Y:0}) cap {truck.Heading:0.00} recul {truck.Reversing:0} {truck.Status}");
                }
                return 0;
            }
            case "ouvertures":
            {
                // ouvertures <Data> <Mission> [zmin] : portes et fenêtres (au sol ou non, cases bloquées, cassable)
                Session ses = new Session(files, args[2], "EASY", Environment.GetEnvironmentVariable("FD3_APRES") != null);
                if (Environment.GetEnvironmentVariable("FD3_APRES") is { } ap)
                    for (int i = 0; i < (int)(float.Parse(ap) / Session.Dt); i++)
                        ses.Step();
                float zmin = args.Length > 3 ? float.Parse(args[3]) : -1e9f;
                foreach (Opening d in ses.Units.Openings.Where(d => d.Position.Z >= zmin))
                    Console.WriteLine($"{d.Name,-28} {(d.IsWindow ? "fenêtre" : "porte"),-8} z {d.Position.Z:0} au sol {d.OnGround} cases {d.Cells.Count} cassable {d.Breakable} vie {d.Life:0} état {d.State} centre ({d.Center.X:0},{d.Center.Y:0})");
                return 0;
            }
            case "grille":
            {
                // grille <Data> <Mission> x0 y0 x1 y1 : hauteurs et zones des cases (une lettre par case)
                Session ses = new Session(files, args[2], "EASY", false);
                Grid g = ses.Grid!;
                int x0 = int.Parse(args[3]) / 100, y0 = int.Parse(args[4]) / 100, x1 = int.Parse(args[5]) / 100, y1 = int.Parse(args[6]) / 100;
                Dictionary<float, int> hs = new Dictionary<float, int>();
                for (int y = y1; y >= y0; y--)
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    for (int x = x0; x <= x1; x++)
                    {
                        if (!g.Inside(x, y))
                        {
                            sb.Append(' ');
                            continue;
                        }
                        int i = g.Index(x, y);
                        float h = MathF.Round(g.GroundHeight[i]);
                        hs[h] = hs.GetValueOrDefault(h) + 1;
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
                Session ses = new Session(files, args[2], "EASY");
                void Run(float sec)
                {
                    for (int i = 0; i < (int)(sec / Session.Dt); i++)
                        ses.Step();
                }
                Run(40);
                Opening d = ses.Units.Openings.First(o => o.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
                Unit man = ses.Units.Units.Where(u => u.CanBreak && !u.Hidden && !u.OnBoard).OrderBy(u => System.Numerics.Vector3.Distance(u.Position, d.Position)).First();
                if (args.Length > 5)
                    ses.Units.Teleport(man, new System.Numerics.Vector2(float.Parse(args[4]), float.Parse(args[5])));
                ses.Units.WorkOnOpening(man, d);
                Console.WriteLine($"{man.Name} → {d.Name} : {man.Status}");
                for (int k = 0; k < 30 && d.State != OpeningState.Broken; k++)
                {
                    Run(3);
                    Console.WriteLine($"  {ses.Time:0} s : {man.Status} ({man.Position.X:0},{man.Position.Y:0},{man.Position.Z:0}) vie {d.Life:0} obstacle z {d.Center.Z:0}");
                }
                Run(2);
                Console.WriteLine($"état {d.State} ; visibilité script : {string.Join(", ", ses.Host!.Visibility.Select(kv => $"{kv.Key}={kv.Value}"))}");
                return 0;
            }
            case "declencheur":
            {
                // declencheur <Data> <Mission> <nom de zone> : un pompier entre, ressort et rentre dans la zone ; combien de déclenchements ?
                Session ses = new Session(files, args[2], "EASY");
                void Run(float sec)
                {
                    for (int i = 0; i < (int)(sec / Session.Dt); i++)
                        ses.Step();
                }
                Run(40);
                TriggerZone t = ses.Units.Triggers.First(z => z.Name.Equals(args[3], StringComparison.OrdinalIgnoreCase));
                Unit man = ses.Units.Units.First(u => !u.IsVehicle && !u.Hidden && !u.OnBoard);
                int fired = 0;
                for (int k = 0; k < 4; k++)
                {
                    ses.Units.Teleport(man, new System.Numerics.Vector2(t.Position.X, t.Position.Y));
                    man.Path.Clear();
                    double before = ses.Units.LastTriggerTime;
                    Run(1);
                    if (ses.Units.LastTriggerTime != before)
                        fired++;
                    ses.Units.Teleport(man, new System.Numerics.Vector2(t.Position.X + 2500, t.Position.Y));
                    man.Path.Clear();
                    Run(8);
                    if (ses.Host!.Cinematic)
                        ses.Host.EndCinematic();
                }
                Console.WriteLine($"zone {t.Name} : déclenchée {fired} fois sur 4 entrées ; active {t.Enabled}");
                foreach (string l in ses.Host!.FireLog.TakeLast(3))
                    Console.WriteLine("  " + l);
                return 0;
            }
            case "video":
            {
                // video <fichier.bik> [image] : décode toute la vidéo (OpenFD.Video) ; images, son, durée ; enregistre une image (.ppm)
                using Video.VideoDecoder? v = OpenFD.Video.VideoDecoder.Open(args[1]);
                if (v == null)
                {
                    Console.WriteLine("échec : " + OpenFD.Video.VideoDecoder.LastError);
                    return 1;
                }
                Console.WriteLine($"{v.Width}×{v.Height} {v.Fps:0.##} images/s, {v.Duration:0.0} s, son {v.AudioRate} Hz × {v.AudioChannels}");
                byte[] rgba = new byte[v.Width * v.Height * 4];
                short[] audio = new short[16384];
                int want = args.Length > 2 ? int.Parse(args[2]) : 100, frames = 0;
                long samples = 0;
                double last = 0;
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                for (;;)
                {
                    Video.VideoDecoder.Item it = v.Next(rgba, audio, out double pts, out int n);
                    if (it == OpenFD.Video.VideoDecoder.Item.End)
                        break;
                    if (it == OpenFD.Video.VideoDecoder.Item.Sound)
                    {
                        samples += n;
                        continue;
                    }
                    frames++;
                    last = pts;
                    if (frames == want)
                    {
                        using FileStream f = File.Create(Path.Combine(Path.GetTempPath(), "fd3_video.ppm"));
                        byte[] hdr = System.Text.Encoding.ASCII.GetBytes($"P6\n{v.Width} {v.Height}\n255\n");
                        f.Write(hdr);
                        for (int i = 0; i < v.Width * v.Height; i++)
                        {
                            f.WriteByte(rgba[i * 4]);
                            f.WriteByte(rgba[i * 4 + 1]);
                            f.WriteByte(rgba[i * 4 + 2]);
                        }
                    }
                }
                Console.WriteLine($"{frames} images (dernière à {last:0.00} s), {samples / (double)Math.Max(1, v.AudioRate):0.0} s de son, décodé en {sw.Elapsed.TotalSeconds:0.0} s ; rembobinage {v.Rewind()}");
                return 0;
            }
            case "vie":
            {
                // vie <Data> <Mission> : un pompier posé dans les flammes s'évanouit-il ? puis, ranimé, il se soigne à l'ambulance ;
                // un camion posé dans le feu finit-il hors service ?
                Session ses = new Session(files, args[2], "EASY");
                void Run(float sec)
                {
                    for (int i = 0; i < (int)(sec / Session.Dt); i++)
                        ses.Step();
                }
                Run(40);
                Fire.HotPoint fire = ses.Fire.HotPoints.Where(h => h.IsBurning).OrderByDescending(h => h.Intensity * h.Class.HeatRangeMax).First();
                Unit man = ses.Units.Units.First(u => !u.IsVehicle && !u.Hidden && !u.OnBoard);
                ses.Units.Teleport(man, new System.Numerics.Vector2(fire.Position.X + 60, fire.Position.Y));
                Console.WriteLine($"feu {fire.Name} intensité {fire.Intensity:0}/{fire.Class.IntensityMax} chaleur {fire.Class.UnitHeatDamage} rayon {fire.Class.HeatRangeMax}");
                for (int k = 0; k < 10 && !man.Unconscious; k++)
                {
                    Run(2);
                    Console.WriteLine($"  {ses.Time:0} s : endurance {man.Stamina:0}/{man.StaminaMax} {man.Status}");
                }
                man.Unconscious = false;
                man.Stamina = 20;
                Unit? amb = ses.Units.Units.FirstOrDefault(u => u.HealsUnits);
                if (amb != null)
                {
                    ses.Units.GoToVehicle(man, amb);
                    for (int k = 0; k < 20; k++)
                    {
                        Run(3);
                        Console.WriteLine($"  {ses.Time:0} s : endurance {man.Stamina:0} {man.Status} à {System.Numerics.Vector3.Distance(man.Position, amb.Position):0} de l'ambulance");
                    }
                }
                Unit truck = ses.Units.Units.First(u => u.IsVehicle && u.Nozzle != null);
                fire = ses.Fire.HotPoints.Where(h => h.IsBurning && MathF.Abs(h.Position.Z) < 60).OrderByDescending(h => h.Intensity).First();
                ses.Units.Teleport(truck, new System.Numerics.Vector2(fire.Position.X + 100, fire.Position.Y));
                for (int k = 0; k < 8 && !truck.Destroyed; k++)
                {
                    Run(10);
                    Console.WriteLine($"  {ses.Time:0} s : camion vie {truck.Life:0}/{truck.LifeMax} {truck.Status} ; feu {fire.Intensity:0}");
                }
                return 0;
            }
            case "suivre":
            {
                // suivre <Data> <Mission> : un pompier va chercher un civil conscient puis l'emmène à l'ambulance ;
                // le civil passe-t-il par des cases libres (pas à travers les murs) ?
                Session ses = new Session(files, args[2], "EASY");
                void Run(float sec)
                {
                    for (int i = 0; i < (int)(sec / Session.Dt); i++)
                        ses.Step();
                }
                Run(40);
                Civilian? civ = null;
                Unit? man = null;
                foreach (Civilian c0 in ses.Units.Civilians.Where(c => c.State == CivilianState.Conscious && !c.Hidden).ToList())
                {
                    Unit m0 = ses.Units.Units.Where(u => !u.IsVehicle && !u.Hidden && !u.OnBoard).OrderBy(u => System.Numerics.Vector3.Distance(u.Position, c0.Position)).First();
                    ses.Units.FetchCivilian(m0, c0);
                    for (int k = 0; k < 40 && c0.Leader == null; k++)
                        Run(1);
                    if (c0.Leader != null)
                    {
                        civ = c0;
                        man = m0;
                        break;
                    }
                    Console.WriteLine($"  {c0.Name} : pas atteint ({m0.Status})");
                }
                if (civ == null || man == null)
                {
                    Console.WriteLine("aucun civil atteint");
                    return 0;
                }
                Console.WriteLine($"{man.Name} → {civ.Name} : suit {civ.Leader != null} à {ses.Time:0} s");
                Unit amb = ses.Units.Units.First(u => u.IsAmbulance);
                ses.Units.GoToVehicle(man, amb);
                int bad = 0, steps = 0;
                Grid g = ses.Grid!;
                for (int i = 0; i < 600 && civ.State == CivilianState.Conscious; i++)
                {
                    ses.Step();
                    steps++;
                    (int cx, int cy) = g.CellOf(civ.Position);
                    if (!g.Inside(cx, cy) || g.ZoneA[g.Index(cx, cy)] == 0 || ses.Units.IsBlocked(g.Index(cx, cy)))
                        bad++;
                    if (i % 50 == 0)
                        Console.WriteLine($"  {ses.Time:0} s : civil ({civ.Position.X:0},{civ.Position.Y:0}) à {System.Numerics.Vector3.Distance(civ.Position, man.Position):0} du pompier ; {man.Status}");
                }
                Console.WriteLine($"état final {civ.State} ; pas dans un mur : {bad}/{steps}");
                return 0;
            }
            case "cassables":
            {
                // cassables <Data> <Mission> : objets du décor qu'on peut casser (CouldBeBreak), hors portes et fenêtres
                Session ses = new Session(files, args[2], "EASY", false);
                Dictionary<string, ClassFile> cache = new Dictionary<string, ClassFile>(StringComparer.OrdinalIgnoreCase);
                foreach (LevelObject o in ses.Level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform))
                {
                    ClassFile? c;
                    try { c = ClassFile.Load(files, "Design\\" + o.ClassPath["design\\".Length..] + ".class", cache); } catch { continue; }
                    if (!c.GetBool("CAPACITIES_ACTIONS", "CouldBeBreak"))
                        continue;
                    string? type = c.Get("OCCUPATION", "Occupation_Type")?.Trim('"', ' ');
                    Console.WriteLine($"{o.Name,-30} {o.ClassPath,-60} type {type} vie {c.GetFloat("DAMAGE", "LifePoints", -1)} usedmg {c.Get("DAMAGE", "UseDamage")} pos ({o.Position.X:0},{o.Position.Y:0},{o.Position.Z:0}) modèle {c.Get("DISPLAY", "Model")}");
                }
                return 0;
            }
            case "camion":
            {
                // camion <Data> <Mission> : lance du camion, montée / descente, recharge d'eau (essai automatique)
                Session ses = new Session(files, args[2], "EASY");
                void Run(float sec)
                {
                    for (int i = 0; i < (int)(sec / Session.Dt); i++)
                        ses.Step();
                }
                Run(30);
                Unit truck = ses.Units.Units.First(u => u.IsVehicle && u.Nozzle != null);
                Console.WriteLine($"camion {truck.Name} : lance portée {truck.Nozzle!.RangeMin}–{truck.Nozzle.RangeMax}, puissance {truck.Nozzle.Power}, réserve {truck.Nozzle.Reserve}, places {truck.Seats}, recharge {truck.ResupplyPower}");
                Fire.HotPoint fire = ses.Fire.HotPoints.Where(h => h.IsBurning).OrderBy(h => System.Numerics.Vector3.Distance(h.Position, truck.Position)).First();
                Console.WriteLine($"feu {fire.Name} à {System.Numerics.Vector3.Distance(fire.Position, truck.Position):0}");
                ses.Units.Attack(truck, fire);
                for (int k = 0; k < 6; k++)
                {
                    Run(3);
                    Console.WriteLine($"  {ses.Time:0} s camion : {truck.Status} ; eau {truck.Nozzle.Reserve:0} ; {fire.Name} {fire.Intensity:0}");
                }
                List<Unit> men = ses.Units.Units.Where(u => !u.IsVehicle && u.Nozzle != null).Take(2).ToList();
                foreach (Unit m in men)
                {
                    m.Nozzle!.Reserve = 0;
                    ses.Units.GoToVehicle(m, truck);
                }
                for (int k = 0; k < 6; k++)
                {
                    Run(4);
                    Console.WriteLine($"  {ses.Time:0} s recharge : " + string.Join(" | ", men.Select(m => $"{m.Status} {m.Nozzle!.Reserve:0}")));
                }
                foreach (Unit m in men)
                    ses.Units.BoardVehicle(m, truck);
                Run(8);
                Console.WriteLine($"  à bord : {truck.Passengers.Count}/{truck.Seats} ; " + string.Join(" | ", men.Select(m => m.Status)));
                ses.Units.Drive(truck, new System.Numerics.Vector2(truck.Position.X - 1500, truck.Position.Y));
                Run(10);
                Console.WriteLine($"  après trajet : camion {truck.Status} ; passagers {string.Join(",", men.Select(m => $"({m.Position.X:0},{m.Position.Y:0})"))}");
                Console.WriteLine($"  descente : {ses.Units.UnloadPassengers(truck)} ; " + string.Join(" | ", men.Select(m => $"{m.Status} ({m.Position.X:0},{m.Position.Y:0})")));
                foreach (string l in ses.Units.Log.TakeLast(6))
                    Console.WriteLine(l);
                return 0;
            }
            case "issue":
            {
                // issue <Data> <Mission> <secondes> : objectifs et issue (victoire / défaite) au fil du temps
                Session ses = new Session(files, args[2], "EASY");
                int steps = (int)(float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) / Session.Dt);
                for (int i = 0; i < steps && ses.Host!.Outcome == null; i++)
                    ses.Step();
                MissionHost h = ses.Host!;
                Console.WriteLine($"{ses.Time:0} s : issue {(h.Outcome == null ? "en cours" : h.Outcome == true ? "VICTOIRE" : "DÉFAITE")} ; texte {h.DebriefText}");
                foreach (MissionHost.Objective o in h.Objectives)
                    Console.WriteLine($"  objectif {o.Id} {o.Text} statut {o.Status} valeur {o.Value} {string.Join(" ", o.Positions)}");
                Console.WriteLine($"  déclencheurs {ses.Units.Triggers.Count} : " + string.Join(", ", ses.Units.Triggers.Take(8).Select(t => t.Script)));
                Console.WriteLine($"  points d'eau {ses.Units.Supplies.Count} : " + string.Join(", ", ses.Units.Supplies.Select(t => $"{t.Name} r{t.Radius}")));
                foreach (string l in ses.Scripts!.Log.TakeLast(5))
                    Console.WriteLine("  script : " + l);
                return 0;
            }
            case "objscripts":
            {
                // objscripts <Data> <Mission> : objets dont un composant nomme une fonction des scripts de la mission
                Session ses = new Session(files, args[2], "EASY", false);
                HashSet<string> fns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string d in new[] { Path.Combine(files.DataDir, "Script", "Mission", args[2]), Path.Combine(files.DataDir, "Script", "_Common") }.Where(Directory.Exists))
                    foreach (string f in Directory.EnumerateFiles(d, "*.scp"))
                        foreach (System.Text.RegularExpressions.Match mm in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"^\s*void\s+(\w+)\s*\(", System.Text.RegularExpressions.RegexOptions.Multiline))
                            fns.Add(mm.Groups[1].Value);
                foreach (LevelObject o in ses.Level.Objects)
                    foreach (Component c in o.Components)
                    {
                        byte[] b = o.ComponentBytes(c).ToArray();
                        string txt = System.Text.Encoding.Latin1.GetString(b);
                        foreach (System.Text.RegularExpressions.Match mm in System.Text.RegularExpressions.Regex.Matches(txt, @"[A-Za-z_][A-Za-z0-9_]{2,}"))
                            if (fns.Contains(mm.Value))
                                Console.WriteLine($"{o.Name,-28} [{o.Kind}] {o.ClassName,-28} id {o.Id} comp {c.Id:X4} @{mm.Index} : {mm.Value}");
                    }
                return 0;
            }
            case "objet":
            {
                // objet <Data> <Mission> <nom>… : genre, classe, position, axes de la matrice
                Session ses = new Session(files, args[2], "EASY", false);
                foreach (string name in args.Skip(3))
                    foreach (LevelObject o in ses.Level.Objects.Where(o => name.StartsWith("cls:") ? System.Text.RegularExpressions.Regex.IsMatch(o.ClassPath, name[4..], System.Text.RegularExpressions.RegexOptions.IgnoreCase) : o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        System.Numerics.Matrix4x4 m = o.Matrix;
                        Console.WriteLine($"{o.Name} [{o.Kind}] {o.ClassPath} id {o.Id} pos {o.Position} rot {o.Rotation} transform {o.HasTransform}");
                        Console.WriteLine($"   X {new System.Numerics.Vector3(m.M11, m.M12, m.M13)} Y {new System.Numerics.Vector3(m.M21, m.M22, m.M23)} Z {new System.Numerics.Vector3(m.M31, m.M32, m.M33)} T {new System.Numerics.Vector3(m.M41, m.M42, m.M43)}");
                        foreach (Component c in o.Components)
                            Console.WriteLine($"   comp {c.Id:X4} ({c.Size}) : {Convert.ToHexString(o.ComponentBytes(c).ToArray())[..Math.Min(240, c.Size * 2)]}");
                    }
                return 0;
            }
            case "soleil":
            {
                // soleil <Data> <Mission> : objet lumière (position, rotation, matrice, direction des axes) et sa classe
                Session ses = new Session(files, args[2], "EASY", false);
                foreach (LevelObject o in ses.Level.Objects.Where(o => o.Kind == EntityKind.Light && o.HasTransform))
                {
                    System.Numerics.Matrix4x4 m = o.Matrix;
                    Console.WriteLine($"{o.Name} pos {o.Position} rot {o.Rotation} ; axes X {new System.Numerics.Vector3(m.M11, m.M12, m.M13)} Y {new System.Numerics.Vector3(m.M21, m.M22, m.M23)} Z {new System.Numerics.Vector3(m.M31, m.M32, m.M33)}");
                    ClassFile cf = ClassFile.Load(files, "Design\\" + o.ClassPath["design\\".Length..] + ".class");
                    foreach (string sec in cf.Sections)
                        Console.WriteLine($"  [{sec}] " + string.Join(", ", cf.Section(sec).Select(kv => kv.Key + "=" + kv.Value)));
                    foreach (Component c in o.Components)
                        Console.WriteLine($"  comp {c.Id:X4} : {Convert.ToHexString(o.ComponentBytes(c).ToArray())[..Math.Min(200, c.Size * 2)]}");
                }
                return 0;
            }
            case "classe":
            {
                // classe <Data> <Mission> <nom d'objet>… : section [OCCUPATION] et modèle des objets de la carte
                Session ses = new Session(files, args[2], "EASY", false);
                foreach (string name in args.Skip(3))
                    foreach (LevelObject o in ses.Level.Objects.Where(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(1))
                    {
                        string rel = "Design\\" + o.ClassPath["design\\".Length..] + ".class";
                        Console.WriteLine($"== {o.Name} {rel}");
                        ClassFile cf = ClassFile.Load(files, rel);
                        foreach (string sec in new[] { "OCCUPATION", "DAMAGE" })
                            Console.WriteLine($"  [{sec}] " + string.Join(", ", cf.Section(sec).Select(kv => kv.Key + "=" + kv.Value)));
                        if (Environment.GetEnvironmentVariable("FD3_ALL") == null)
                            continue;
                        string txt = System.Text.Encoding.Latin1.GetString(files.Read(rel));
                        bool on = false;
                        foreach (string line in txt.Split('\n'))
                        {
                            string t = line.Trim();
                            if (t.StartsWith("["))
                                on = t.StartsWith("[OCCUPATION", StringComparison.OrdinalIgnoreCase) || t.StartsWith("[DISPLAY", StringComparison.OrdinalIgnoreCase) || t.StartsWith("[INHERIT", StringComparison.OrdinalIgnoreCase) || Environment.GetEnvironmentVariable("FD3_ALL") != null;
                            if (on && t.Length > 0)
                                Console.WriteLine("  " + t);
                        }
                    }
                return 0;
            }
            case "unites":
            {
                // unites <Data> <Mission> <secondes> : arrivée et déplacement des secours
                Session ses = new Session(files, args[2], "EASY", false);
                float secs = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
                for (int i = 0; i < secs / Session.Dt; i++)
                {
                    ses.Step();
                    // essai : à 5 s, chaque pompier marche jusqu'au centre du feu le plus proche (sans ordre d'attaque)
                    if (args.Contains("marcher") && i == 50)
                        foreach (Unit u in ses.Units.Units.Where(u => u.Nozzle != null))
                        {
                            Fire.HotPoint f = ses.Fire.HotPoints.Where(h => h.IsBurning).OrderBy(h => System.Numerics.Vector3.Distance(h.Position, u.Position)).First();
                            ses.Units.Order(u, new(f.Position.X, f.Position.Y));
                        }
                }
                if (args.Contains("camion") && ses.Grid is { } gr)
                {
                    // essai : chaque pompier traverse jusqu'à l'autre côté du véhicule garé le plus proche ; on vérifie qu'il ne passe pas dedans
                    int bad = 0, total = 0;
                    foreach (Unit u in ses.Units.Units.Where(u => !u.IsVehicle))
                    {
                        Unit v = ses.Units.Units.Where(x => x.IsVehicle).OrderBy(x => System.Numerics.Vector3.Distance(x.Position, u.Position)).First();
                        System.Numerics.Vector2 me = new System.Numerics.Vector2(u.Position.X, u.Position.Y);
                        System.Numerics.Vector2 vc = new System.Numerics.Vector2(v.Position.X, v.Position.Y);
                        System.Numerics.Vector2 dest = vc + (vc - me) * 1.0f;
                        ses.Units.Order(u, dest);
                        int inside = 0;
                        for (int i = 0; i < 600 && u.Moving; i++)
                        {
                            ses.Step();
                            (int cx, int cy) = gr.CellOf(u.Position);
                            if (gr.Inside(cx, cy) && ses.Units.IsVehicleBlocked(gr.Index(cx, cy)))
                                inside++;
                        }
                        total++;
                        if (inside > 2)
                            bad++;
                        Console.WriteLine($"{u.Name,-22} contourne {v.Name,-20} : pas dans le véhicule = {inside}, arrivé ({u.Position.X:0},{u.Position.Y:0}) {u.Status}");
                    }
                    Console.WriteLine($"Traversées : {total}, passées dans un véhicule : {bad}");
                }
                Console.WriteLine($"Cases bloquées par des objets : {ses.Units.BlockedCells}");
                foreach (string l in ses.Units.Log)
                    Console.WriteLine(l);
                foreach (Unit u in ses.Units.Units)
                    Console.WriteLine($"{u.Name,-22} {u.Model,-34} ({u.Position.X:0},{u.Position.Y:0},{u.Position.Z:0}) vitesse {u.Speed:0} {u.Status} chemin {u.Path.Count}");
                return 0;
            }
            case "sauvetage":
            {
                // sauvetage <Data> : London, le médecin réanime le civil inconscient le plus proche puis le ramène à l'ambulance
                Session ses = new Session(files, "London", "EASY", false);
                foreach (Civilian c in ses.Units.Civilians)
                    Console.WriteLine($"{c.Name,-20} {c.State,-12} ({c.Position.X:0},{c.Position.Y:0}){(c.Hidden ? " caché" : "")}");
                for (int i = 0; i < 30; i++)
                    ses.Step();
                Unit medic = ses.Units.Units.First(u => u.CanHeal);
                Unit amb = ses.Units.Units.First(u => u.IsAmbulance);
                if (args.Contains("tous"))
                {
                    foreach (Civilian cv in ses.Units.Civilians.Where(c => c.State == OpenFD.Game.CivilianState.Unconscious && !c.Hidden))
                    {
                        Session s2 = new Session(files, "London", "EASY", false);
                        for (int i = 0; i < 30; i++)
                            s2.Step();
                        Unit m2 = s2.Units.Units.First(u => u.CanHeal);
                        Civilian c2 = s2.Units.Civilians.First(c => c.Id == cv.Id);
                        s2.Units.FetchCivilian(m2, c2);
                        for (int i = 0; i < 2000 && c2.State == OpenFD.Game.CivilianState.Unconscious; i++)
                            s2.Step();
                        Console.WriteLine($"{c2.Name,-20} ({c2.Position.X:0},{c2.Position.Y:0}) → {c2.State}, médecin à ({m2.Position.X:0},{m2.Position.Y:0}) : {m2.Status}");
                    }
                    return 0;
                }
                Civilian civ = ses.Units.Civilians.Where(c => c.State == OpenFD.Game.CivilianState.Unconscious && !c.Hidden).OrderBy(c => System.Numerics.Vector3.Distance(c.Position, medic.Position)).First();
                ses.Units.FetchCivilian(medic, civ);
                Console.WriteLine($"médecin ({medic.Position.X:0},{medic.Position.Y:0}) chemin {medic.Path.Count} {medic.Status}");
                if (Environment.GetEnvironmentVariable("FD3_DUMP") is { } dump)
                    System.IO.File.WriteAllLines(dump, ses.Units.BlockedBy.Select(kv => $"{kv.Key} {kv.Value}").Concat(medic.Path.Select(p => $"P {p.X} {p.Y}")).Append($"W {ses.Grid!.Width}"));
                bool sent = false;
                for (int i = 0; i < 3000; i++)
                {
                    ses.Step();
                    if (!sent && civ.Leader == medic)
                    {
                        ses.Units.GoToVehicle(medic, amb);
                        sent = true;
                        Console.WriteLine($"{ses.Time:0.0} s : le civil suit le médecin, direction l'ambulance");
                    }
                    if (i % 100 == 0)
                        Console.WriteLine($"{ses.Time,6:0.0} s  médecin : {medic.Status,-40} civil : {civ.State} ({civ.Position.X:0},{civ.Position.Y:0})");
                    if (civ.State == OpenFD.Game.CivilianState.Rescued)
                        break;
                }
                foreach (string l in ses.Units.Log)
                    Console.WriteLine(l);
                return 0;
            }
            case "modele":
            {
                // modele <Data> <chemin Gfx> : pièces d'un modèle
                SceneAssets sa = new SceneAssets(files);
                foreach (SceneAssets.Node n in sa.ModelParts(args[2]))
                {
                    SceneAssets.MeshAsset a = sa.LoadMesh("London", n.Mesh, "gfx");
                    Console.WriteLine($"{n.Mesh,-50} sommets {a.Mesh.VertexCount,6} {string.Join(" | ", a.Groups.Select(g => g.Material + ":" + g.Diffuse))}");
                }
                return 0;
            }
            case "typesfeu":
            {
                // typesfeu <Data> : nombre de foyers par type de feu (A, B, C, D) dans chaque mission
                foreach (string dir in (args.Length > 2 ? args[2..] : new[] { "Tutorial", "London", "Fireworks" }))
                {
                    string? lv = files.List("Design\\Mission\\" + dir + "\\").FirstOrDefault(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                    if (lv == null)
                        continue;
                    Level level = Level.Parse(files.Read(lv));
                    Fire.FireWorld w = OpenFD.Fire.FireWorld.Load(level, files, new OpenFD.Fire.FireSettings());
                    Console.WriteLine($"{dir,-10} " + string.Join("  ", w.HotPoints.GroupBy(h => h.Class.FireType).OrderBy(g => g.Key)
                        .Select(g => $"{g.Key}={g.Count()}" + (g.Key != 'A' ? " (" + string.Join(", ", g.Select(h => h.Name).Take(8)) + ")" : ""))));
                }
                return 0;
            }
            case "fireclasses":
            {
                Dictionary<string, ClassFile> cache = new Dictionary<string, ClassFile>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in files.List("Design\\Hotpoint\\").Where(p => p.EndsWith(".class", StringComparison.OrdinalIgnoreCase)).Take(5))
                {
                    ClassFile c = ClassFile.Load(files, path, cache);
                    IReadOnlyDictionary<string, string> hp = c.Section("HOTPOINT_PROPS");
                    Console.WriteLine($"{Path.GetFileNameWithoutExtension(path),-28} " + string.Join("  ", hp.Select(kv => $"{kv.Key}={kv.Value}")));
                }
                return 0;
            }
            case "objets":
            {
                // objets <Data> <Mission> : position, classe, type et composants de chaque objet (JSON)
                string lvPath = files.List("Design\\Mission\\" + args[2] + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                Level level = Level.Parse(files.Read(lvPath));
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
                string lvPath = files.List("Design\\Mission\\" + args[2] + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                Level level = Level.Parse(files.Read(lvPath));
                Fire.FireWorld w = OpenFD.Fire.FireWorld.Load(level, files, new OpenFD.Fire.FireSettings());
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(w.HotPoints.Select(h => new {
                    nom = h.Name, h.Lit, h.Intensity, h.BurnTime, tempo = h.Class.Tempo, seuil = h.Class.LightingThreshold,
                    portee = h.Class.LightingRange, croissance = h.Class.GrowthRate, max = h.Class.IntensityMax, x = h.Position.X, y = h.Position.Y, z = h.Position.Z })));
                return 0;
            }
            case "mission":
            {
                // mission <Data> <Mission> <minutes> <FACILE|NORMAL|DIFFICILE> pieces=<json> [repartir]
                string mission = args[2];
                float minutes = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
                string diffName = args[4].ToUpperInvariant();
                List<string> opts = args.Skip(5).ToList();
                string lvPath = files.List("Design\\Mission\\" + mission + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                Level level = Level.Parse(files.Read(lvPath));
                string misRel = "Design\\" + level.MissionPath[(level.MissionPath.IndexOf("Design\\", StringComparison.OrdinalIgnoreCase) + 7)..];
                ClassFile mis = ClassFile.Load(files, misRel);
                Fire.FireWorld world = OpenFD.Fire.FireWorld.Load(level, files, OpenFD.Fire.FireSettings.FromMission(mis, diffName));
                world.Proximity3D = true;
                string? roomsOpt = opts.FirstOrDefault(o => o.StartsWith("pieces="));
                if (roomsOpt != null)
                    world.AddProximityNeighbours(null, RoomMap(files, level, mission, roomsOpt[7..]));
                OpenFD.Fire.SmokeSystem? smoke = null;
                string? smokeOpt = opts.FirstOrDefault(o => o.StartsWith("fumee="));
                if (smokeOpt != null)
                    smoke = OpenFD.Fire.SmokeSystem.Load(level, files, world, RoomMap(files, level, mission, smokeOpt[6..]));
                MissionHost host = new MissionHost(level, world)
                {
                    Difficulty = diffName switch { "EASY" => 0, "HARD" => 2, _ => 1 },
                    GroupAddSplits = opts.Contains("repartir"),
                };
                Script.Interpreter interp = new OpenFD.Script.Interpreter(host);
                string scriptRoot = Path.Combine(args[1], "Script");
                string[] dirs = new[] { Path.Combine(scriptRoot, "_Common"), Path.Combine(scriptRoot, "Mission", mission) };
                foreach (string d in dirs.Where(Directory.Exists))
                    foreach (string f in Directory.EnumerateFiles(d, "*.scp"))
                        interp.Load(OpenFD.Script.Parser.ParseFile(File.ReadAllText(f, System.Text.Encoding.Latin1), Path.GetFileName(f)));
                string? autoexec = mis.Get("SCRIPT", "AutoExecScript");
                string[] mains = new[] { mis.Get("SCRIPT", "MissionScript1"), mis.Get("SCRIPT", "MissionScript2") }.Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToArray();
                Console.WriteLine($"{interp.FunctionNames.Count} fonctions chargées ; démarrage {autoexec} ; boucles {string.Join(", ", mains)}");
                if (!string.IsNullOrEmpty(autoexec))
                    interp.TryRun(autoexec!);
                using StreamWriter csv = new StreamWriter(Path.Combine(Path.GetTempPath(), $"fd3_{mission}_sim.csv"));
                csv.WriteLine("temps_s,id,nom,intensite,etat");
                const float dt = 0.1f;
                int steps = (int)(minutes * 60 / dt);
                for (int st = 0; st <= steps; st++)
                {
                    if (st % 100 == 0)
                        foreach (Fire.HotPoint h in world.HotPoints.Where(h => h.Lit || h.State == OpenFD.Fire.HotPointState.Consumed))
                            csv.WriteLine($"{world.Time:0.0},{h.Id},{h.Name},{h.Intensity:0.0},{h.State}");
                    interp.Update();
                    if (st % 5 == 0)
                        foreach (string m in mains)
                            interp.TryRun(m);
                    world.Step(dt);
                    smoke?.Step(world, dt);
                }
                if (smoke != null)
                {
                    Console.WriteLine($"Fumée finale : total {smoke.Total:0.0} ; points non vides : " + string.Join(", ", smoke.Points.Where(p => p.Intensity > 0.5f).Select(p => $"{p.Name}={p.Intensity:0.0}").Take(12)));
                    foreach (string l in smoke.Log)
                        Console.WriteLine("  " + l);
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), $"fd3_{mission}_fumee.json"), System.Text.Json.JsonSerializer.Serialize(smoke.Points.ToDictionary(p => p.Name, p => p.Intensity)));
                }
                Console.WriteLine("Événements feu déclenchés par les scripts :");
                foreach (string l in host.FireLog.Take(40))
                    Console.WriteLine("  " + l);
                Console.WriteLine($"Erreurs de script : {interp.Log.Count}");
                foreach (string l in interp.Log.Distinct().Take(10))
                    Console.WriteLine("  " + l);
                Console.WriteLine("Fonctions du moteur non simulées (appels) : " + string.Join(", ", interp.UnknownCalls.OrderByDescending(k => k.Value).Take(15).Select(k => $"{k.Key}×{k.Value}")));
                Console.WriteLine($"Foyers en feu à la fin : {world.HotPoints.Count(h => h.IsBurning)}");
                return 0;
            }
            case "liens":
            {
                // liens <Data> <Mission> : liste des liens (noms, type, sens, ouverture)
                string lvPath = files.List("Design\\Mission\\" + args[2] + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                Level level = Level.Parse(files.Read(lvPath));
                Dictionary<uint, LevelObject> byId = level.Objects.ToDictionary(o => o.Id);
                string N(uint i) => byId.TryGetValue(i, out LevelObject? o) ? o.Name : i.ToString();
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(level.HotPointLinks.Select(l => new {
                    a = N(l.A), b = N(l.B), type = BitConverter.ToInt32(l.Data, 13), sens = BitConverter.ToInt32(l.Data, 17), ouverture = N(BitConverter.ToUInt32(l.Data, 0)) })));
                return 0;
            }
            case "etats":
            {
                // etats <Data> <Mission> : début du bloc d'état FE00 de chaque foyer (hexadécimal)
                string lvPath = files.List("Design\\Mission\\" + args[2] + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                Level level = Level.Parse(files.Read(lvPath));
                Dictionary<string, string> res = new Dictionary<string, string>();
                foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.HotPoint))
                    if (o.GetComponent(0xFE00) is { } c)
                        res[o.Name] = Convert.ToHexString(o.ComponentBytes(c)[17..44]);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(res));
                return 0;
            }
            case "scripts":
            {
                // scripts <Data> : analyse tous les fichiers .scp et signale les erreurs
                int ok = 0, ko = 0, fns = 0;
                foreach (string f in Directory.EnumerateFiles(Path.Combine(args[1], "Script"), "*.scp", SearchOption.AllDirectories).OrderBy(x => x))
                {
                    try
                    {
                        List<Script.Function> list = OpenFD.Script.Parser.ParseFile(File.ReadAllText(f, System.Text.Encoding.Latin1), Path.GetFileName(f));
                        fns += list.Count;
                        ok++;
                    }
                    catch (OpenFD.Script.ParseError e)
                    {
                        ko++;
                        Console.WriteLine("ERREUR " + e.Message);
                    }
                }
                Console.WriteLine($"{ok} fichiers analysés, {ko} en erreur, {fns} fonctions");
                return ko == 0 ? 0 : 2;
            }
            case "groupes":
            {
                // groupes <Data> <Mission> <nom1,nom2,...> : membres de groupes (directs) en JSON
                string lvPath = files.List("Design\\Mission\\" + args[2] + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                Level level = Level.Parse(files.Read(lvPath));
                Dictionary<uint, LevelObject> byId = level.Objects.ToDictionary(o => o.Id);
                Dictionary<string, object> outd = new Dictionary<string, object>();
                foreach (string g in args[3].Split(','))
                {
                    if (g.StartsWith("@"))
                    {   // @nom : groupes qui contiennent cet objet
                        LevelObject? t = level.Objects.FirstOrDefault(o => o.Name.Equals(g[1..], StringComparison.OrdinalIgnoreCase));
                        if (t != null)
                            outd[g] = level.Objects.Where(o => o.Members.Contains(t.Id)).Select(o => o.Name).ToList();
                        continue;
                    }
                    LevelObject? go = level.Objects.FirstOrDefault(o => o.Name.Equals(g, StringComparison.OrdinalIgnoreCase));
                    if (go == null)
                        continue;
                    outd[g] = go.Members.Select(m => byId.TryGetValue(m, out LevelObject? mo) ? new { nom = mo.Name, type = (int)mo.Kind } : new { nom = "?" + m, type = -1 }).ToList();
                }
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(outd));
                return 0;
            }
            case "simulate":
            {
                // simulate <Data> <Mission> [minutes] [difficulté]
                string mission = args.Length > 2 ? args[2] : "London";
                float minutes = args.Length > 3 ? float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 15f;
                string diff = args.Length > 4 ? args[4].ToUpperInvariant() : "NORMAL";
                string lvPath = files.List("Design\\Mission\\" + mission + "\\").First(p => p.EndsWith(".level", StringComparison.OrdinalIgnoreCase));
                Level level = Level.Parse(files.Read(lvPath));
                string misRel = "Design\\" + level.MissionPath[(level.MissionPath.IndexOf("Design\\", StringComparison.OrdinalIgnoreCase) + 7)..];
                ClassFile mis = ClassFile.Load(files, misRel);
                Fire.FireSettings settings = OpenFD.Fire.FireSettings.FromMission(mis, diff);
                Fire.FireWorld world = OpenFD.Fire.FireWorld.Load(level, files, settings);
                if (args.Length <= 5)
                    world.BuildNeighbourhoods();
                // options : proximite | proximite-murs ; seed=<fichier json d'identifiants allumés au départ>
                List<string> opts = args.Skip(5).ToList();
                world.ClosedOpeningsBlock = opts.Contains("portes-fermees");
                world.Proximity3D = opts.Contains("3d");
                world.ProximityUsesTargetRange = opts.Contains("portee-cible");
                if (!opts.Contains("sans-voisinage"))
                    world.BuildNeighbourhoods();
                Grid? grid = files.TryRead(Path.ChangeExtension(lvPath, ".grid")) is { } gb ? Grid.Parse(gb) : null;
                if (opts.Contains("proximite"))
                    world.AddProximityNeighbours();
                if (opts.Contains("proximite-murs"))
                    world.AddProximityNeighbours(grid);
                string? roomsOpt = opts.FirstOrDefault(o => o.StartsWith("pieces="));
                if (roomsOpt != null)
                    world.AddProximityNeighbours(null, RoomMap(files, level, mission, roomsOpt[7..]));
                string? seedOpt = opts.FirstOrDefault(o => o.StartsWith("seed="));
                if (seedOpt != null)
                {
                    uint[] seedIds = System.Text.Json.JsonSerializer.Deserialize<uint[]>(File.ReadAllText(seedOpt[5..]))!;
                    foreach (Fire.HotPoint h in world.HotPoints)
                    {
                        h.Lit = false;
                        h.State = OpenFD.Fire.HotPointState.Idle;
                        if (h.Intensity > 0)
                            h.Intensity = 0;
                    }
                    foreach (uint id in seedIds)
                        if (world.ById.TryGetValue(id, out Fire.HotPoint? h))
                            world.Ignite(h, h.Class.InitialIntensity);
                }
                // evenements=<json> : [{"t":24.8,"action":"ajouter|multiplier","groupe":"explo_fire","valeur":100}]
                string? evOpt = opts.FirstOrDefault(o => o.StartsWith("evenements="));
                List<(float t, string action, List<Fire.HotPoint> hps, float v)> events = new List<(float t, string action, List<OpenFD.Fire.HotPoint> hps, float v)>();
                if (evOpt != null)
                {
                    Dictionary<string, LevelObject> byName = level.Objects.GroupBy(o => o.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
                    Dictionary<uint, LevelObject> byId = level.Objects.ToDictionary(o => o.Id);
                    List<OpenFD.Fire.HotPoint> Resolve(LevelObject o, int depth)
                    {
                        List<Fire.HotPoint> res = new List<OpenFD.Fire.HotPoint>();
                        if (world.ById.TryGetValue(o.Id, out Fire.HotPoint? h))
                            res.Add(h);
                        if (depth < 5)
                            foreach (uint m in o.Members)
                                if (byId.TryGetValue(m, out LevelObject? mo))
                                    res.AddRange(Resolve(mo, depth + 1));
                        return res;
                    }
                    using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(evOpt[11..]));
                    foreach (System.Text.Json.JsonElement e in doc.RootElement.EnumerateArray())
                    {
                        string g = e.GetProperty("groupe").GetString()!.ToLowerInvariant();
                        if (!byName.TryGetValue(g, out LevelObject? go))
                        {
                            Console.WriteLine("Groupe introuvable : " + g);
                            continue;
                        }
                        events.Add(((float)e.GetProperty("t").GetDouble(), e.GetProperty("action").GetString()!, Resolve(go, 0), (float)e.GetProperty("valeur").GetDouble()));
                    }
                    foreach ((float t, string action, List<Fire.HotPoint> hps, float v) ev in events)
                        Console.WriteLine($"Événement à {ev.t} s : {ev.action} {ev.v} sur {ev.hps.Count} foyers");
                }
                string? smokeOpt = opts.FirstOrDefault(o => o.StartsWith("fumee="));
                OpenFD.Fire.SmokeSystem? smoke = null;
                if (smokeOpt != null)
                {
                    smoke = OpenFD.Fire.SmokeSystem.Load(level, files, world, RoomMap(files, level, mission, smokeOpt[6..]));
                    if (opts.Contains("fumee-murs"))
                        smoke.Walls = grid;
                    Console.WriteLine($"Fumée : {smoke.Points.Count} points, {smoke.Sources.Count} foyers producteurs, total initial {smoke.Total:0.0}");
                }
                // arroser=<foyer>@<début>-<fin>[:<classe d'unité>] : une unité arrose ce foyer
                //   (classe par défaut Unit\Fireman\fireman_medic ; exemple : arroser=ground_l109@60.6-74:Unit\Fireman\fireman_sp)
                Dictionary<string, ClassFile> unitCache = new Dictionary<string, ClassFile>(StringComparer.OrdinalIgnoreCase);
                List<(Fire.HotPoint hp, float t0, float t1, Fire.Extinguisher unit)> sprays = opts.Where(o => o.StartsWith("arroser=")).Select(o =>
                {
                    System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(o[8..], @"^([^@]+)@([\d.]+)-([\d.]+)(?::(.+))?$");
                    System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
                    string cls = m.Groups[4].Success ? m.Groups[4].Value : "Unit\\Fireman\\fireman_medic";
                    Fire.Extinguisher unit = OpenFD.Fire.Extinguisher.FromClass(cls, ClassFile.Load(files, "Design\\" + cls + ".class", unitCache));
                    return (hp: world.HotPoints.First(h => h.Name.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)),
                            t0: float.Parse(m.Groups[2].Value, inv), t1: float.Parse(m.Groups[3].Value, inv), unit);
                }).ToList();
                string? stepOpt = opts.FirstOrDefault(o => o.StartsWith("pas="));
                int reportEvery = stepOpt != null ? int.Parse(stepOpt[4..]) : 60;
                // avance=<s> : fait brûler les foyers de départ pendant ce temps avant de commencer à mesurer
                // (les feux de départ brûlent déjà pendant la cinématique d'introduction)
                string? warmOpt = opts.FirstOrDefault(o => o.StartsWith("avance="));
                if (warmOpt != null)
                {
                    HashSet<Fire.HotPoint> seedSet = world.HotPoints.Where(h => h.Lit).ToHashSet();
                    float warm = float.Parse(warmOpt[7..], System.Globalization.CultureInfo.InvariantCulture);
                    for (float t = 0; t < warm; t += 0.1f)
                    {
                        world.Step(0.1f);
                        foreach (Fire.HotPoint h in world.HotPoints)
                            if (h.Lit && !seedSet.Contains(h))
                            {
                                h.Lit = false;
                                h.State = OpenFD.Fire.HotPointState.Idle;
                                h.SetIntensity(0);
                            }
                    }
                }
                int withSources = world.HotPoints.Count(h => h.HeatSources.Count > 0);
                Console.WriteLine($"{mission} : {world.HotPoints.Count} foyers, {world.Links.Count} liens, {withSources} foyers ont au moins une source de chaleur");
                Console.WriteLine($"Difficulté {diff} : tempo ×{settings.TempoFactor}, croissance ×{settings.GrowthFactor}, évaporation {settings.NaturalEvaporationRate}/s");
                Console.WriteLine($"En feu au départ : {world.HotPoints.Count(h => h.Lit)}");
                Console.WriteLine(" temps(s) | en feu | consumés | intensité totale | plus forte");
                using StreamWriter csv = new StreamWriter(Path.Combine(Path.GetTempPath(), $"fd3_{mission}_sim.csv"));
                csv.WriteLine("temps_s,id,nom,intensite,etat");
                const float dt = 0.1f;
                int steps = (int)(minutes * 60 / dt);
                for (int s = 0; s <= steps; s++)
                {
                    if (s % (int)(10 / dt) == 0)
                        foreach (Fire.HotPoint h in world.HotPoints.Where(h => h.Lit || h.State == OpenFD.Fire.HotPointState.Consumed))
                            csv.WriteLine($"{world.Time:0.0},{h.Id},{h.Name},{h.Intensity:0.0},{h.State}");
                    if (s % (int)(reportEvery / dt) == 0)
                    {
                        List<Fire.HotPoint> lit = world.HotPoints.Where(h => h.IsBurning).ToList();
                        Console.WriteLine($" {world.Time,6:0} | {lit.Count,6} | {world.HotPoints.Count(h => h.State == OpenFD.Fire.HotPointState.Consumed),8} | {lit.Sum(h => h.Intensity),16:0} | {(lit.Count > 0 ? lit.Max(h => h.Intensity) : 0),10:0}");
                    }
                    foreach ((float t, string action, List<Fire.HotPoint> hps, float v) ev in events.Where(ev => ev.t > world.Time - dt / 2 && ev.t <= world.Time + dt / 2))
                        foreach (Fire.HotPoint h in ev.hps)
                            if (ev.action == "ajouter")
                                world.AddIntensity(h, ev.v);
                            else
                                h.GrowthMultiplier *= ev.v;
                    foreach ((Fire.HotPoint hp, float t0, float t1, Fire.Extinguisher unit) sp in sprays)
                        if (world.Time >= sp.t0 && world.Time < sp.t1 && sp.hp.Lit)
                            sp.unit.Spray(world, sp.hp, dt);
                    world.Step(dt);
                    smoke?.Step(world, dt);
                }
                foreach (string l in world.ExtinguishLog)
                    Console.WriteLine("Extinction : " + l);
                foreach ((Fire.HotPoint hp, float t0, float t1, Fire.Extinguisher unit) sp in sprays)
                    Console.WriteLine($"Unité {sp.unit.Name} ({sp.unit.Agent}, puissance {sp.unit.Power}) : réserve {sp.unit.Reserve:0.00}/{sp.unit.ReserveMax}, endurance {sp.unit.Stamina:0.0}");
                if (smoke != null)
                {
                    Console.WriteLine($"Fumée finale : total {smoke.Total:0.0} ; points non vides : " + string.Join(", ", smoke.Points.Where(p => p.Intensity > 0.5f).Select(p => $"{p.Name}={p.Intensity:0.0}").Take(30)));
                    foreach (string l in smoke.Log)
                        Console.WriteLine("  " + l);
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
            if (spec.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return Rooms.Assign(level, Rooms.Load(files, mission));
            Dictionary<string, string> map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(spec))!;
            return map.ToDictionary(kv => uint.Parse(kv.Key), kv => kv.Value);
        }
    }
}
