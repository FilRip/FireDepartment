using OpenFD.Formats;

namespace OpenFD.Game;

/// <summary>
/// Zones d'occupation (Occupation.cs) et objets que le script cache : la place qu'ils prennent dans la grille suit leur
/// visibilité. Port : la rame en gare ferme la voie (« occupation_train », puis « occupation_train_enroute » pendant
/// qu'elle part) ; partie et cachée, elle laisse la voie libre.
/// </summary>
public sealed partial class UnitManager
{
    public List<Occupation> Occupations { get; } = new();
    /// <summary>Nom des objets du décor, pour retrouver leurs cases (PathFind.BlockedBy) quand le script les cache.</summary>
    private readonly Dictionary<uint, string> _placeableNames = new();
    /// <summary>Objets du décor cachés par le script : les cases qu'ils fermaient, rouvertes (refermées s'ils réapparaissent).</summary>
    private readonly Dictionary<uint, List<int>> _hiddenFootprints = new();

    /// <summary>
    /// Objet du décor qui n'est qu'une zone d'occupation (sa forme ne compte pas : seulement ses cases dessinées) : son nom
    /// contient « occupation » (Port, Ch_Plant) ou commence par « occup_ » (Tutorial : « occup_porte_garage », la porte du
    /// garage fermée tant que le tutoriel ne dit pas d'en sortir).
    /// </summary>
    public static bool IsOccupationName(string name) =>
        name.Contains("occupation", StringComparison.OrdinalIgnoreCase) || name.StartsWith("occup_", StringComparison.OrdinalIgnoreCase);

    private void LoadOccupations(Level level)
    {
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.Placeable))
            _placeableNames[o.Id] = o.Name;
        if (_s.Grid is not { } g)
            return;
        Paths.Occupied = new int[g.Width * g.Height];
        Paths.VehicleOccupied = new int[g.Width * g.Height];
        foreach (LevelObject o in level.Objects.Where(o => o.Kind == EntityKind.Placeable && o.HasTransform && IsOccupationName(o.Name)))
        {
            List<(int Dx, int Dy)> list = o.OccupationCells;
            if (list.Count == 0)
                continue;
            (int cx, int cy) = g.CellOf(o.Position);
            Occupation oc = new Occupation { Id = o.Id, Name = o.Name, StartVisible = o.StartVisible };
            HashSet<int> wide = new HashSet<int>();
            foreach ((int dx, int dy) in list)
            {
                int x = cx + dx, y = cy + dy;
                if (!g.Inside(x, y))
                    continue;
                oc.Cells.Add(g.Index(x, y));
                for (int ax = -1; ax <= 1; ax++)
                    for (int ay = -1; ay <= 1; ay++)
                        if (g.Inside(x + ax, y + ay))
                            wide.Add(g.Index(x + ax, y + ay));
            }
            oc.VehicleCells.AddRange(wide);
            Occupations.Add(oc);
            Log.Add($"Zone d'occupation : {o.Name} ({oc.Cells.Count} case(s), {(o.StartVisible ? "posée" : "levée")} au départ)");
        }
        UpdateOccupations();
    }

    /// <summary>Chaque pas : les zones d'occupation et les objets cachés suivent ce que le script a rendu visible ou caché.</summary>
    private void UpdateOccupations()
    {
        Dictionary<uint, bool>? vis = _s.Host?.Visibility;
        foreach (Occupation oc in Occupations)
        {
            bool on = vis != null && vis.TryGetValue(oc.Id, out bool v) ? v : oc.StartVisible;
            if (on == oc.Active)
                continue;
            oc.Active = on;
            int add = on ? 1 : -1;
            foreach (int i in oc.Cells)
                Paths.Occupied[i] += add;
            foreach (int i in oc.VehicleCells)
                Paths.VehicleOccupied[i] += add;
            Log.Add($"{_s.Time,6:0} s : zone d'occupation {oc.Name} {(on ? "posée" : "levée")}");
        }
        if (vis == null)
            return;
        foreach ((uint id, bool shown) in vis)
        {
            if (!shown && !_hiddenFootprints.ContainsKey(id) && _placeableNames.TryGetValue(id, out string? name) && !IsOccupationName(name) && !Openings.Any(d => d.Id == id))
            {
                // caché : ses cases (celles que BuildObstacles lui a données) se rouvrent
                string prefix = name + " ";
                List<int> freed = Paths.BlockedBy.Where(kv => kv.Value.StartsWith(prefix, StringComparison.Ordinal) && Paths.IsBlocked(kv.Key)).Select(kv => kv.Key).ToList();
                foreach (int i in freed)
                    Paths.Blocked[i] = false;
                _hiddenFootprints[id] = freed;
                if (freed.Count > 0)
                    Log.Add($"{_s.Time,6:0} s : {name} caché : {freed.Count} case(s) libérée(s)");
            }
            else if (shown && _hiddenFootprints.Remove(id, out List<int>? cells))
            {
                foreach (int i in cells)
                    Paths.Blocked[i] = true;
            }
        }
    }
}
