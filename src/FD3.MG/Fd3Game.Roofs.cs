using FD3.Game;
using SN = System.Numerics;

namespace FD3.MG;

// toits par bâtiment : le toit d'un bâtiment disparaît (pour de bon) dès qu'un pompier y entre, ou s'approche d'une de
// ses portes ouverte ou cassée (ou l'ouvre). La touche T enlève ou remet tous les toits (essais).
// Bâtiment = maillages du décor regroupés quand l'un est presque entièrement dans l'emprise de l'autre
// (London : « Bank_fasad », « Bank_fasad_ROOF », « Bank_ROOM… », « roof_bank_glass » forment la banque).
public sealed partial class Fd3Game
{
    private sealed class Building { public SN.Vector2 Min, Max; public readonly List<string> Nodes = new(); public bool Open; }
    private readonly List<Building> _buildings = new();
    private readonly Dictionary<string, int> _nodeBuilding = new(StringComparer.OrdinalIgnoreCase);
    private int _curBuilding = -1;
    private float _roofCheck;

    private bool RoofShown(int bld) => _roofs && !(bld >= 0 && bld < _buildings.Count && _buildings[bld].Open);

    private void FindBuildings(string mission, IReadOnlyList<SceneAssets.Node> scenery)
    {
        _buildings.Clear(); _nodeBuilding.Clear();
        var boxes = new List<(string name, SN.Vector2 min, SN.Vector2 max)>();
        foreach (var n in scenery)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(n.Mesh, @"^(FGD_|GROUND|Tile_ground)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            try
            {
                var m = _assets.LoadMesh(mission, n.Mesh, null).Mesh;
                SN.Vector2 mn = new(float.MaxValue), mx = new(float.MinValue);
                for (int i = 0; i < m.VertexCount; i++)
                {
                    var v = SN.Vector3.Transform(new SN.Vector3(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]), n.World);
                    mn = SN.Vector2.Min(mn, new(v.X, v.Y)); mx = SN.Vector2.Max(mx, new(v.X, v.Y));
                }
                if (mn.X < mx.X) boxes.Add((n.Mesh, mn, mx));
            }
            catch { }
        }
        // regroupement : l'emprise de l'un recouvre au moins 60 % de celle de l'autre
        var parent = Enumerable.Range(0, boxes.Count).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        float Area(SN.Vector2 a, SN.Vector2 b) => MathF.Max(0, b.X - a.X) * MathF.Max(0, b.Y - a.Y);
        for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
            {
                var a = boxes[i]; var b = boxes[j];
                float inter = Area(SN.Vector2.Max(a.min, b.min), SN.Vector2.Min(a.max, b.max));
                float small = MathF.Min(Area(a.min, a.max), Area(b.min, b.max));
                if (small > 0 && inter >= 0.6f * small) parent[Find(i)] = Find(j);
            }
        var ids = new Dictionary<int, int>();
        for (int i = 0; i < boxes.Count; i++)
        {
            int r = Find(i);
            if (!ids.TryGetValue(r, out var id)) { ids[r] = id = _buildings.Count; _buildings.Add(new Building { Min = new(float.MaxValue), Max = new(float.MinValue) }); }
            var b = _buildings[id];
            b.Min = SN.Vector2.Min(b.Min, boxes[i].min); b.Max = SN.Vector2.Max(b.Max, boxes[i].max); b.Nodes.Add(boxes[i].name);
            _nodeBuilding[boxes[i].name] = id;
        }
        foreach (var b in _buildings.Where(b => b.Nodes.Count > 1))
            Console.WriteLine($"Bâtiment : {string.Join(", ", b.Nodes.Take(6))}{(b.Nodes.Count > 6 ? "…" : "")} ({b.Min.X:0},{b.Min.Y:0})-({b.Max.X:0},{b.Max.Y:0})");
    }

    private static bool Inside(SN.Vector2 p, SN.Vector2 min, SN.Vector2 max, float margin) =>
        p.X >= min.X - margin && p.Y >= min.Y - margin && p.X <= max.X + margin && p.Y <= max.Y + margin;

    /// <summary>Quelques fois par seconde : un pompier dans un bâtiment ou près d'une de ses portes ouvertes en découvre le toit.</summary>
    private void UpdateRoofs(float dt)
    {
        // demandes des scripts (cinématiques : « montrer l'intérieur du hangar »)
        if (Host is { } h)
        {
            foreach (var (mesh, hidden) in h.RoofRequests)
                if (_nodeBuilding.TryGetValue(mesh, out var id)) _buildings[id].Open = hidden;
                else foreach (var b0 in _buildings.Where(b0 => b0.Nodes.Any(n => n.Contains(mesh, StringComparison.OrdinalIgnoreCase)))) b0.Open = hidden;
            h.RoofRequests.Clear();
        }
        if ((_roofCheck -= dt) > 0 || _buildings.Count == 0) return;
        _roofCheck = 0.25f;
        var men = _session.Units.Active.Where(u => !u.IsVehicle).Select(u => new SN.Vector2(u.Position.X, u.Position.Y)).ToList();
        if (men.Count == 0) return;
        var doors = _session.Units.Openings.Where(o => o.OnGround && o.State != OpeningState.Closed)
            .Select(o => new SN.Vector2(o.Center.X, o.Center.Y)).ToList();
        for (int i = 0; i < _buildings.Count; i++)
        {
            var b = _buildings[i];
            if (b.Open || b.Nodes.Count < 2) continue;
            bool inside = men.Any(p => Inside(p, b.Min, b.Max, -200));
            bool atDoor = !inside && doors.Where(d => Inside(d, b.Min, b.Max, 200)).Any(d => men.Any(p => SN.Vector2.Distance(p, d) < 350));
            if (!inside && !atDoor) continue;
            b.Open = true;
            Log($"Toit retiré : {b.Nodes.OrderBy(n => n.Contains("ROOM", StringComparison.OrdinalIgnoreCase) || n.Contains("ROOF", StringComparison.OrdinalIgnoreCase) ? 1 : 0).First()}");
        }
    }
}
