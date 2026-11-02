// Visualiseur : simulation du feu de Fire Department 3, vue de dessus dans le navigateur.
// Lancement : dotnet run --project src/FD3.Viewer -- "<dossier Data du jeu>"
using System.Diagnostics;
using System.Numerics;
using FD3.Formats;
using FD3.Game;

namespace FD3.Viewer;

public static class Program
{
    public static int Main(string[] args)
    {
        var dataDir = args.FirstOrDefault(a => !a.StartsWith("--")) ?? Environment.GetEnvironmentVariable("FD3_DATA") ?? "";
        if (!Directory.Exists(dataDir) && Directory.Exists(Path.Combine(dataDir, "..", "Data"))) dataDir = Path.Combine(dataDir, "Data");
        if (Directory.Exists(Path.Combine(dataDir, "Data"))) dataDir = Path.Combine(dataDir, "Data");
        if (!File.Exists(Path.Combine(dataDir, "design.pak")))
        {
            Console.WriteLine("Indiquez le dossier du jeu, par exemple :");
            Console.WriteLine("  dotnet run --project src/FD3.Viewer -- \"E:\\Jeux\\Fire Department 3\"");
            return 1;
        }
        var files = new GameFiles(dataDir);
        const string Url = "http://localhost:5173";

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ContentRootPath = AppContext.BaseDirectory, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls(Url);
        var app = builder.Build();
        // jamais de copie en cache dans le navigateur : on voit toujours la dernière version
        app.Use(async (ctx, next) => { ctx.Response.Headers.CacheControl = "no-store"; await next(); });
        app.UseDefaultFiles();
        app.UseStaticFiles();

        var gate = new object();
        Session? session = null;
        float speed = 1f; bool paused = true;
        string status = "";

        Session Start(string mission, string diff)
        {
            var s = new Session(files, mission, diff);
            lock (gate) { session = s; paused = true; }
            return s;
        }

        // boucle de simulation : 10 pas de 0,1 s par seconde réelle × vitesse
        _ = Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew(); double owed = 0;
            while (true)
            {
                await Task.Delay(20);
                double el = clock.Elapsed.TotalSeconds; clock.Restart();
                lock (gate)
                {
                    if (session == null || paused) { owed = 0; continue; }
                    owed += el * speed / Session.Dt;
                    int n = (int)Math.Min(owed, 400); owed -= n;
                    try { for (int i = 0; i < n; i++) session.Step(); }
                    catch (Exception e) { status = "Erreur : " + e.Message; paused = true; }
                }
            }
        });

        string[] Missions() => files.List("Gfx\\Mission\\").Select(p => p.Split('\\')[2]).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(m => files.List("Design\\Mission\\").Select(p => p.Split('\\')[2]).FirstOrDefault(d => d.Equals(m, StringComparison.OrdinalIgnoreCase)))
            .Where(m => m != null).Select(m => m!).Distinct().OrderBy(m => m).ToArray();

        app.MapGet("/api/missions", () => Missions());

        app.MapPost("/api/start", (string mission, string diff) =>
        {
            try { Start(mission, diff); return Results.Ok(); }
            catch (Exception e) { return Results.Problem(e.Message); }
        });

        app.MapGet("/api/map", () =>
        {
            Session? s; lock (gate) s = session;
            if (s == null) return Results.NotFound();
            var g = s.Grid;
            string? walls = null, ground = null;
            if (g != null)
            {
                var w = new byte[g.Width * g.Height]; var h = new byte[g.Width * g.Height];
                float gmin = g.GroundHeight.DefaultIfEmpty(0).Min(), gmax = g.GroundHeight.DefaultIfEmpty(1).Max();
                for (int i = 0; i < w.Length; i++)
                {
                    w[i] = (byte)(i < g.ZoneA.Length && g.ZoneA[i] == 0 ? 1 : 0);
                    h[i] = i < g.GroundHeight.Length && gmax > gmin ? (byte)(255 * (g.GroundHeight[i] - gmin) / (gmax - gmin)) : (byte)0;
                }
                walls = Convert.ToBase64String(w); ground = Convert.ToBase64String(h);
            }
            return Results.Json(new
            {
                mission = s.Mission, difficulty = s.Difficulty,
                grid = g == null ? null : new { width = g.Width, height = g.Height, cell = Grid.CellSize, walls, ground },
                rooms = s.RoomList.Select(r => new { name = r.Name, x0 = r.Min.X, y0 = r.Min.Y, x1 = r.Max.X, y1 = r.Max.Y }),
                hotpoints = s.Fire.HotPoints.Select(h => new { id = h.Id, name = h.Name, x = h.Position.X, y = h.Position.Y, z = h.Position.Z,
                    type = h.Class.FireType.ToString(), max = h.Class.IntensityMax, range = h.Class.LightingRange }),
                smoke = s.Smoke.Points.Select(p => new { id = p.Id, name = p.Name, x = p.Position.X, y = p.Position.Y, max = p.Max, r = p.Class.InfluenceRange }),
            });
        });

        app.MapGet("/api/state", () =>
        {
            lock (gate)
            {
                var s = session;
                if (s == null) return Results.Json(new { running = false, missions = Missions() });
                var f = s.Fire;
                return Results.Json(new
                {
                    running = true, t = s.Time, speed, paused, status,
                    hp = f.HotPoints.Select(h => MathF.Round(h.Intensity, 1)),
                    st = f.HotPoints.Select(h => (int)h.State),
                    lit = f.HotPoints.Count(h => h.IsBurning),
                    smoke = s.Smoke.Points.Select(p => MathF.Round(p.Intensity, 1)),
                    smokeTotal = MathF.Round(s.Smoke.Total),
                    sprayers = s.Sprayers.Select(x => new { target = x.target.Id, unit = x.unit.Name.Split('\\').Last(), reserve = MathF.Round(x.unit.Reserve, 1), stamina = MathF.Round(x.unit.Stamina, 1) }),
                    log = f.ExtinguishLog.TakeLast(6).Concat(s.Smoke.Log.TakeLast(4)),
                });
            }
        });

        app.MapPost("/api/control", (float? speedArg, bool? pause) =>
        {
            lock (gate) { if (speedArg is { } v) speed = Math.Clamp(v, 0.25f, 64f); if (pause is { } p) paused = p; }
            return Results.Ok();
        });

        app.MapPost("/api/spray", (uint id, string? unit) =>
        {
            lock (gate)
            {
                var h = session?.Fire.HotPoints.FirstOrDefault(x => x.Id == id);
                if (session == null || h == null) return Results.NotFound();
                session.Spray(h, string.IsNullOrEmpty(unit) ? "Unit\\Fireman\\fireman_medic" : unit);
                return Results.Ok();
            }
        });

        // ---------- 3D : décor de la mission ----------
        var meshCache = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var assets = new SceneAssets(files);
        static float[] Arr(System.Numerics.Matrix4x4 w) => new[] { w.M11, w.M12, w.M13, w.M14, w.M21, w.M22, w.M23, w.M24, w.M31, w.M32, w.M33, w.M34, w.M41, w.M42, w.M43, w.M44 };

        app.MapGet("/api/scene", (string mission) => Results.Json(assets.Scenery(mission).Select(n => new { mesh = n.Mesh, m = Arr(n.World) })));

        app.MapGet("/api/placeables", () =>
        {
            Session? s; lock (gate) s = session;
            return s == null ? Results.NotFound()
                : Results.Json(assets.Placeables(s.Level).Select(p => new { model = p.Model, name = p.Name, civil = p.Civilian, m = Arr(p.World) }));
        });

        app.MapGet("/api/trees", () =>
        {
            Session? s; lock (gate) s = session;
            return s == null ? Results.NotFound()
                : Results.Json(assets.Trees(s.Level).Select(t => new { model = t.Model, name = t.Name, height = t.Height, seed = t.Seed, bark = t.Bark, leaves = t.Leaves, m = Arr(t.World) }));
        });
        app.MapGet("/api/treetex", (string name) => assets.TreeTexture(name) is { } b ? Results.Bytes(b, "image/x-tga") : Results.NotFound());

        app.MapGet("/api/model", (string name) => Results.Json(assets.ModelParts(name).Select(n => new { mesh = n.Mesh, m = Arr(n.World) })));

        // format binaire : u32 taille JSON, JSON (groupes, matériaux), bourrage à 4, puis float32 positions, normales, uv0, uv1, u32 indices
        var meshBytes = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        app.MapGet("/api/mesh", (string mission, string name, string? src) => Results.Bytes(meshBytes.GetOrAdd((src ?? mission) + "/" + name, _ =>
        {
            var a = assets.LoadMesh(mission, name, src); var m = a.Mesh;
            var groups = a.Groups.Select(g => new { start = g.Start, count = g.Count, material = g.Material, diffuse = g.Diffuse, alpha = g.Alpha });
            var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { m.VertexCount, indexCount = m.Indices.Length, groups, m.Shader, uv1 = a.Uv1 != null, lightmap = a.Lightmap });
            using var ms = new MemoryStream(); using var bw = new BinaryWriter(ms);
            bw.Write(json.Length); bw.Write(json); while (ms.Length % 4 != 0) bw.Write((byte)0);
            void W(float[] x) { foreach (var v in x) bw.Write(v); }
            W(m.Positions); W(m.Normals); W(a.Uv0); if (a.Uv1 != null) W(a.Uv1);
            foreach (var i in m.Indices) bw.Write(i);
            return ms.ToArray();
        }), "application/octet-stream"));

        app.MapGet("/api/tex", (string mission, string name) => assets.Texture(mission, name) is { } b ? Results.Bytes(b, "image/vnd-ms.dds") : Results.NotFound());

        Console.WriteLine($"Données du jeu : {dataDir}");
        Console.WriteLine($"Missions disponibles : {string.Join(", ", Missions())}");
        Console.WriteLine($"Ouvrez {Url} dans votre navigateur (fermez cette fenêtre pour arrêter).");
        // le navigateur ne s'ouvre qu'une fois le serveur réellement démarré
        if (!args.Contains("--sans-navigateur"))
            app.Lifetime.ApplicationStarted.Register(() => { try { Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true }); } catch { } });
        try { app.Run(); }
        catch (IOException)
        {
            Console.WriteLine();
            Console.WriteLine("Le visualiseur est déjà ouvert dans une autre fenêtre noire (adresse " + Url + " occupée).");
            Console.WriteLine("Fermez toutes les fenêtres noires du visualiseur, puis relancez Lancer_visualiseur.bat.");
            return 2;
        }
        return 0;
    }
}
