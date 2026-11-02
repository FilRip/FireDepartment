using System.Numerics;
using FD3.Fire;
using FD3.Formats;

namespace FD3.Game;

/// <summary>
/// Backdraft. Certains foyers de la carte sont marqués « MakeBackdraft » (London : deux pièces de la banque).
/// Dans une pièce dont toutes les portes et fenêtres sont fermées, un tel foyer qui atteint son seuil (SleepingThreshold)
/// « s'endort » faute d'air : il ne grandit plus, la fumée et la chaleur restent enfermées (de la fumée filtre sous les portes).
///  • Une porte de la pièce qui s'ouvre (ou qu'on casse) : l'air entre d'un coup, la pièce s'embrase (backdraft),
///    les flammes jaillissent par la porte, les pompiers tout près sont blessés ; voix « VOCAL_BACKDRAFT ».
///  • Une fenêtre cassée avant : la fumée s'échappe, la chaleur retombe, les foyers se réveillent doucement, sans backdraft.
/// ([OPENING] AfterBackdraftIntensity = 50 : feu ajouté près de l'ouverture.)
/// </summary>
public sealed class BackdraftRoom
{
    public required string Name { get; init; }
    public required Rooms.Room Box { get; init; }
    public readonly List<HotPoint> Sleepers = new();      // foyers à backdraft de la pièce
    public readonly List<HotPoint> All = new();           // tous les foyers de la pièce
    public readonly List<Opening> Openings = new();
    public bool Closed => Openings.All(o => o.State == OpeningState.Closed);
    public bool Asleep => Sleepers.Any(h => h.Sleeping);
}

public sealed partial class UnitManager
{
    public List<BackdraftRoom> BackdraftRooms { get; } = new();
    /// <summary>Backdrafts survenus (porte, instant) : pour l'image et le son ; le jeu retire ceux qu'il a traités.</summary>
    public List<(Vector3 pos, float time, string room)> Backdrafts { get; } = new();
    private readonly Dictionary<Opening, OpeningState> _lastOpeningState = new();
    private bool _backdraftReady;
    public const float AfterBackdraftIntensity = 50f, BackdraftInjuryRange = 450f, BackdraftInjurySeconds = 12f;

    private void SetupBackdrafts()
    {
        _backdraftReady = true;
        var roomOf = _s.RoomOf;
        foreach (var grp in _s.Fire.HotPoints.Where(h => h.MakeBackdraft && roomOf.TryGetValue(h.Id, out var r) && r != "")
                     .GroupBy(h => roomOf[h.Id]))
        {
            var box = _s.RoomList.FirstOrDefault(r => r.Name == grp.Key);
            if (box == null) continue;
            var room = new BackdraftRoom { Name = grp.Key, Box = box };
            room.Sleepers.AddRange(grp);
            room.All.AddRange(_s.Fire.HotPoints.Where(h => roomOf.TryGetValue(h.Id, out var r) && r == grp.Key));
            // ouvertures de la pièce : sur ses murs (boîte de la pièce élargie)
            foreach (var o in Openings)
            {
                var c = o.Center; const float m = 160;
                if (c.X >= box.Min.X - m && c.X <= box.Max.X + m && c.Y >= box.Min.Y - m && c.Y <= box.Max.Y + m
                    && c.Z >= box.Min.Z - 200 && c.Z <= box.Max.Z + 200) room.Openings.Add(o);
            }
            if (room.Openings.Count == 0) continue;
            BackdraftRooms.Add(room);
            Console.WriteLine($"Pièce à risque de backdraft : {room.Name} ({room.Sleepers.Count} foyers, {room.Openings.Count} ouvertures)");
        }
        // foyers à backdraft hors des pièces connues (Tutorial, leçon 15) : pièce = groupe de foyers voisins, boîte élargie
        var loose = _s.Fire.HotPoints.Where(h => h.MakeBackdraft && !BackdraftRooms.Any(r => r.Sleepers.Contains(h))).ToList();
        while (loose.Count > 0)
        {
            var grp = new List<HotPoint> { loose[0] }; loose.RemoveAt(0);
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (var h in loose.ToList())
                    if (grp.Any(g => Vector3.Distance(g.Position, h.Position) < 800)) { grp.Add(h); loose.Remove(h); grew = true; }
            }
            var mn = new Vector3(grp.Min(h => h.Position.X), grp.Min(h => h.Position.Y), grp.Min(h => h.Position.Z)) - new Vector3(400, 400, 150);
            var mx = new Vector3(grp.Max(h => h.Position.X), grp.Max(h => h.Position.Y), grp.Max(h => h.Position.Z)) + new Vector3(400, 400, 350);
            var room = new BackdraftRoom { Name = "pièce de " + grp[0].Name, Box = new Rooms.Room("pièce de " + grp[0].Name, mn, mx) };
            room.Sleepers.AddRange(grp);
            room.All.AddRange(_s.Fire.HotPoints.Where(h => h.Position.X >= mn.X && h.Position.X <= mx.X && h.Position.Y >= mn.Y && h.Position.Y <= mx.Y && h.Position.Z >= mn.Z && h.Position.Z <= mx.Z));
            foreach (var o in Openings)
            {
                var c = o.Center;
                if (c.X >= mn.X && c.X <= mx.X && c.Y >= mn.Y && c.Y <= mx.Y && c.Z >= mn.Z - 100 && c.Z <= mx.Z + 100) room.Openings.Add(o);
            }
            if (room.Openings.Count == 0) continue;
            BackdraftRooms.Add(room);
            Console.WriteLine($"Pièce à risque de backdraft : {room.Name} ({room.Sleepers.Count} foyers, {room.Openings.Count} ouvertures)");
        }
        foreach (var o in Openings) _lastOpeningState[o] = o.State;
    }

    public void UpdateBackdrafts(float time, float dt)
    {
        if (!_backdraftReady) SetupBackdrafts();
        foreach (var room in BackdraftRooms)
        {
            var opened = room.Openings.Where(o => _lastOpeningState.TryGetValue(o, out var was) && was == OpeningState.Closed && o.State != OpeningState.Closed).ToList();
            if (room.Closed)
            {
                // pièce fermée : les foyers à backdraft s'endorment à leur seuil et y restent
                bool wasAsleep = room.Asleep;
                foreach (var h in room.Sleepers)
                {
                    if (h.Lit && h.Intensity >= h.SleepingThreshold) h.Sleeping = true;
                    if (h.Sleeping) h.SetIntensity(MathF.Min(h.Intensity, h.SleepingThreshold));
                }
                if (!wasAsleep && room.Asleep) Log.Add($"{time,6:0} s : {room.Name} : le feu couve, pièce fermée (risque de backdraft)");
                continue;
            }
            if (!room.Asleep) continue;
            var door = opened.FirstOrDefault(o => !o.IsWindow);
            if (door != null) { Backdraft(room, door, time); continue; }
            // une fenêtre cassée (ou une ouverture déjà là) : la fumée s'échappe, la chaleur retombe, sans explosion
            if (opened.Any(o => o.IsWindow))
                Log.Add($"{time,6:0} s : {room.Name} : fenêtre cassée, la fumée s'échappe, plus de risque de backdraft");
            foreach (var h in room.Sleepers.Where(h => h.Sleeping)) { h.Sleeping = false; h.SetIntensity(h.Intensity * 0.6f); }
        }
        foreach (var o in Openings) _lastOpeningState[o] = o.State;
    }

    private void Backdraft(BackdraftRoom room, Opening door, float time)
    {
        foreach (var h in room.Sleepers) h.Sleeping = false;
        // la pièce s'embrase d'un coup
        foreach (var h in room.All.Where(h => h.Enabled)) _s.Fire.AddIntensity(h, h.Class.IntensityMax);
        // les flammes sortent par la porte
        foreach (var h in _s.Fire.HotPoints.Where(h => h.Enabled && Vector3.Distance(h.Position, door.Center) < 600))
            _s.Fire.AddIntensity(h, AfterBackdraftIntensity);
        int hurt = 0;
        foreach (var u in Units.Where(u => !u.IsVehicle && Vector3.Distance(u.Position, door.Center) < BackdraftInjuryRange))
        {
            // il ne perd pas de vie : il tombe inconscient, un collègue doit venir le ranimer
            u.Unconscious = true; u.Wounds++; hurt++;
            u.Path.Clear(); u.Target = null; u.Opening = null; u.GoTo = null; u.GoToVehicle = null;
            // projeté en arrière, loin de la porte
            var away = new Vector2(u.Position.X - door.Center.X, u.Position.Y - door.Center.Y);
            if (away.LengthSquared() > 1) u.Heading = MathF.Atan2(-away.Y, -away.X);
        }
        Backdrafts.Add((door.Center, time, room.Name));
        Sounds.Add(("BACKDRAFT", door.Center));
        Sounds.Add(("SC_OPEN_DOOR_BACKDRAFT", door.Center));
        Sounds.Add(("VOCAL_BACKDRAFT", null));
        Log.Add($"{time,6:0} s : BACKDRAFT dans {room.Name} !{(hurt > 0 ? $" {hurt} pompier(s) inconscient(s) : un collègue doit les ranimer" : "")}");
    }

    /// <summary>Portes derrière lesquelles le feu couve (fumée qui filtre dessous : signe du danger).</summary>
    public IEnumerable<Opening> SmolderingDoors =>
        BackdraftRooms.Where(r => r.Closed && r.Asleep).SelectMany(r => r.Openings.Where(o => !o.IsWindow));
}
