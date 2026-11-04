using OpenFD.Game;
using SN = System.Numerics;

namespace OpenFD.MG;

// toits par bâtiment : le toit d'un bâtiment disparaît (pour de bon) dès qu'un pompier y entre, ou s'approche d'une de
// ses portes ouverte ou cassée (ou l'ouvre). La touche T enlève ou remet tous les toits (essais).
// Bâtiment = maillages du décor regroupés quand l'un est presque entièrement dans l'emprise de l'autre
// (London : « Bank_fasad », « Bank_fasad_ROOF », « Bank_ROOM… », « roof_bank_glass » forment la banque).
public sealed partial class Fd3Game
{
    private sealed class Building { public SN.Vector2 Min, Max; public float MinZ = float.MaxValue; public readonly List<string> Nodes = new(); public bool Open; }
    private readonly List<Building> _buildings = new();
    private readonly Dictionary<string, int> _nodeBuilding = new(StringComparer.OrdinalIgnoreCase);
    private int _curBuilding = -1;
    private float _roofCheck;

    private bool RoofShown(int bld) => _roofs && !(bld >= 0 && bld < _buildings.Count && _buildings[bld].Open);

    private void FindBuildings(string mission, IReadOnlyList<SceneAssets.Node> scenery)
    {
        _buildings.Clear(); _nodeBuilding.Clear();
        List<(string name, SN.Vector2 min, SN.Vector2 max)> boxes = new List<(string name, SN.Vector2 min, SN.Vector2 max)>();
        Dictionary<string, float> zmin = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (SceneAssets.Node n in scenery)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(n.Mesh, @"^(FGD_|GROUND|Tile_ground)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                continue;
            try
            {
                Formats.Mesh m = _assets.LoadMesh(mission, n.Mesh, null).Mesh;
                SN.Vector2 mn = new(float.MaxValue), mx = new(float.MinValue);
                for (int i = 0; i < m.VertexCount; i++)
                {
                    SN.Vector3 v = SN.Vector3.Transform(new SN.Vector3(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]), n.World);
                    mn = SN.Vector2.Min(mn, new(v.X, v.Y)); mx = SN.Vector2.Max(mx, new(v.X, v.Y));
                    zmin[n.Mesh] = MathF.Min(zmin.TryGetValue(n.Mesh, out float z0) ? z0 : float.MaxValue, v.Z);
                }
                if (mn.X < mx.X)
                    boxes.Add((n.Mesh, mn, mx));
            }
            catch { }
        }
        // regroupement : l'emprise de l'un recouvre au moins 60 % de celle de l'autre
        int[] parent = Enumerable.Range(0, boxes.Count).ToArray();
        float mapArea = _session.Grid is { } gg ? gg.Width * gg.Height * OpenFD.Formats.Grid.CellSize * OpenFD.Formats.Grid.CellSize : float.MaxValue;
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        float Area(SN.Vector2 a, SN.Vector2 b) => MathF.Max(0, b.X - a.X) * MathF.Max(0, b.Y - a.Y);
        for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
            {
                (string name, SN.Vector2 min, SN.Vector2 max) a = boxes[i]; (string name, SN.Vector2 min, SN.Vector2 max) b = boxes[j];
                // un toit nommé « …_ROOF » reste seul : il ne part que quand un pompier est dessous (pas dès qu'il est dans
                // l'emprise de tout un groupe : la place de Maidan couvre presque toute la carte)
                if (a.name.EndsWith("_ROOF", StringComparison.OrdinalIgnoreCase) || b.name.EndsWith("_ROOF", StringComparison.OrdinalIgnoreCase))
                    continue;
                // maillage géant (couloirs et escalators de Maidan sur toute la carte) : il n'avale pas les autres
                if (MathF.Max(Area(a.min, a.max), Area(b.min, b.max)) > 0.25f * mapArea)
                    continue;
                float inter = Area(SN.Vector2.Max(a.min, b.min), SN.Vector2.Min(a.max, b.max));
                float small = MathF.Min(Area(a.min, a.max), Area(b.min, b.max));
                if (small > 0 && inter >= 0.6f * small)
                    parent[Find(i)] = Find(j);
            }
        Dictionary<int, int> ids = new Dictionary<int, int>();
        for (int i = 0; i < boxes.Count; i++)
        {
            int r = Find(i);
            if (!ids.TryGetValue(r, out int id))
            {
                ids[r] = id = _buildings.Count;
                _buildings.Add(new Building { Min = new(float.MaxValue), Max = new(float.MinValue) });
            }
            Building b = _buildings[id];
            b.Min = SN.Vector2.Min(b.Min, boxes[i].min); b.Max = SN.Vector2.Max(b.Max, boxes[i].max); b.Nodes.Add(boxes[i].name);
            if (zmin.TryGetValue(boxes[i].name, out float bz))
                b.MinZ = MathF.Min(b.MinZ, bz);
            _nodeBuilding[boxes[i].name] = id;
        }
        foreach (Building b in _buildings.Where(b => b.Nodes.Count > 1))
            Console.WriteLine($"Bâtiment : {string.Join(", ", b.Nodes.Take(6))}{(b.Nodes.Count > 6 ? "…" : "")} ({b.Min.X:0},{b.Min.Y:0})-({b.Max.X:0},{b.Max.Y:0})");
    }

    /// <summary>Emprise d'un objet posé en hauteur (2,2 m au moins au-dessus du sol, 4 m² au moins vu de dessus), sinon null.</summary>
    private (SN.Vector2 min, SN.Vector2 max, float zmin)? OverheadBox(SceneAssets.MeshAsset a, SN.Matrix4x4 w)
    {
        Formats.Mesh m = a.Mesh;
        if (m.VertexCount == 0)
            return null;
        SN.Vector2 mn = new(float.MaxValue), mx = new(float.MinValue); float zmin = float.MaxValue;
        for (int i = 0; i < m.VertexCount; i++)
        {
            SN.Vector3 v = SN.Vector3.Transform(new SN.Vector3(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]), w);
            mn = SN.Vector2.Min(mn, new(v.X, v.Y)); mx = SN.Vector2.Max(mx, new(v.X, v.Y)); zmin = MathF.Min(zmin, v.Z);
        }
        SN.Vector2 c = (mn + mx) / 2;
        float ground = _session.Units.GroundZ(c);
        if (zmin - ground < 220 || (mx.X - mn.X) * (mx.Y - mn.Y) < 40000)
            return null;
        return (mn, mx, zmin);
    }

    private static bool Inside(SN.Vector2 p, SN.Vector2 min, SN.Vector2 max, float margin) =>
        p.X >= min.X - margin && p.Y >= min.Y - margin && p.X <= max.X + margin && p.Y <= max.Y + margin;

    /// <summary>Quelques fois par seconde : un pompier dans un bâtiment ou près d'une de ses portes ouvertes en découvre le toit.</summary>
    private void UpdateRoofs(float dt)
    {
        // demandes des scripts (cinématiques : « montrer l'intérieur du hangar »)
        if (Host is { } h)
        {
            foreach ((string? mesh, bool hidden) in h.RoofRequests)
                if (_nodeBuilding.TryGetValue(mesh, out int id))
                    _buildings[id].Open = hidden;
                else
                    foreach (Building b0 in _buildings.Where(b0 => b0.Nodes.Any(n => n.Contains(mesh, StringComparison.OrdinalIgnoreCase)))) b0.Open = hidden;
            h.RoofRequests.Clear();
        }
        if ((_roofCheck -= dt) > 0 || _buildings.Count == 0)
            return;
        _roofCheck = 0.25f;
        List<SN.Vector2> men = _session.Units.Active.Where(u => !u.IsVehicle).Select(u => new SN.Vector2(u.Position.X, u.Position.Y)).ToList();
        if (men.Count == 0)
            return;
        // toit seul (place au-dessus d'un souterrain à Maidan, verrière…) : il part quand un pompier passe dessous
        List<SN.Vector3> men3 = _session.Units.Active.Where(u => !u.IsVehicle).Select(u => u.Position).ToList();
        // (de même pour tout maillage seul suspendu au-dessus d'un pompier : verrière, auvent, dalle…)
        foreach (Building b in _buildings.Where(b => !b.Open && b.Nodes.Count == 1))
            if (men3.Any(p => Inside(new(p.X, p.Y), b.Min, b.Max, 0) && p.Z < b.MinZ - 120))
            {
                b.Open = true;
                Log($"Toit retiré : {b.Nodes[0]}");
            }
        List<SN.Vector2> doors = _session.Units.Openings.Where(o => o.OnGround && o.State != OpeningState.Closed)
            .Select(o => new SN.Vector2(o.Center.X, o.Center.Y)).ToList();
        for (int i = 0; i < _buildings.Count; i++)
        {
            Building b = _buildings[i];
            if (b.Open || b.Nodes.Count < 2)
                continue;
            // groupe qui couvre une grande partie de la carte (souterrains de Maidan) : être dans son emprise ne veut rien dire
            if ((b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y) > 0.25f * _gridW * _gridH)
                continue;
            bool inside = men.Any(p => Inside(p, b.Min, b.Max, -200));
            bool atDoor = !inside && doors.Where(d => Inside(d, b.Min, b.Max, 200)).Any(d => men.Any(p => SN.Vector2.Distance(p, d) < 350));
            if (!inside && !atDoor)
                continue;
            b.Open = true;
            Log($"Toit retiré : {b.Nodes.OrderBy(n => n.Contains("ROOM", StringComparison.OrdinalIgnoreCase) || n.Contains("ROOF", StringComparison.OrdinalIgnoreCase) ? 1 : 0).First()}");
        }
    }
}
