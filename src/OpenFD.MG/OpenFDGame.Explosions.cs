using OpenFD.Game;
using Microsoft.Xna.Framework;
using SN = System.Numerics;

namespace OpenFD.MG;

// explosions : objets explosifs dessinés selon leur état (intact, abîmé, épave), boule de feu et fumée, son, secousse
public sealed partial class OpenFDGame
{
    private readonly List<(ExplosiveItem item, string model, SN.Matrix4x4 world)> _explosives = new();
    private readonly Dictionary<(string model, string state), List<(MeshGpu mesh, SN.Matrix4x4 part)>> _stateModels = new();
    private readonly List<(SN.Vector3 pos, float t0, float radius)> _blastFx = new();
    private float _clock;          // temps réel (s) pour les effets
    private float _shake;          // secousse de la caméra (0 → 1)
    private float _flash;          // éclair de l'explosion : la scène s'éclaire un instant

    // objets explosifs : retirés du décor fixe, dessinés à part
    private HashSet<string> ExplosiveNames() =>
        _session.Explosions.Items.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void LoadExplosives(IEnumerable<SceneAssets.Placed> placed)
    {
        foreach (List<(MeshGpu mesh, SN.Matrix4x4 part)> l in _stateModels.Values)
            foreach ((MeshGpu? m, _) in l)
                m.Dispose();
        _stateModels.Clear(); _explosives.Clear(); _blastFx.Clear(); _shake = 0;
        Dictionary<string, ExplosiveItem> byName = _session.Explosions.Items.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
        foreach (SceneAssets.Placed p in placed)
            if (byName.TryGetValue(p.Name, out ExplosiveItem? it))
                _explosives.Add((it, p.Model, p.World));
    }

    private List<(MeshGpu mesh, SN.Matrix4x4 part)> StateModel(string model, string state)
    {
        if (_stateModels.TryGetValue((model, state), out List<(MeshGpu mesh, SN.Matrix4x4 part)>? l))
            return l;
        l = new();
        try { foreach (SceneAssets.Node part in _assets.ModelParts(model, state)) l.Add((Upload(_assets.LoadMesh(_mission, part.Mesh, "gfx")), part.World)); }
        catch (Exception e) { Console.WriteLine($"{model} ({state}) : {e.Message}"); }
        return _stateModels[(model, state)] = l;
    }

    private void RenderExplosives()
    {
        if (!_objects)
            return;
        foreach ((ExplosiveItem? it, string? model, SN.Matrix4x4 world) in _explosives)
        {
            // véhicule que le script cache, montre ou déplace (Mriya : le camion de la cinématique, l'avion)
            if (Host?.Visibility.TryGetValue(it.Id, out bool shown) == true && !shown)
                continue;
            SN.Matrix4x4 w = world;
            if (ScriptPos(it.Id) is { } mv)
                w.Translation += new SN.Vector3(mv.X - it.Position.X, mv.Y - it.Position.Y, 0);
            foreach ((MeshGpu? mesh, SN.Matrix4x4 part) in StateModel(model, it.State))
                DrawMesh(mesh, (part * w).X());
        }
    }

    /// <summary>À chaque image : explosions survenues pendant la simulation → effet, son, secousse, journal.</summary>
    private void UpdateExplosions(float dt)
    {
        if (_paused)
            return;   // en pause : explosions figées
        _clock += dt;
        _shake = MathF.Max(0, _shake - dt * 1.8f);
        _flash = MathF.Max(0, _flash - dt * 4f);
        ExplosionSystem ex = _session.Explosions;
        foreach (Explosion e in ex.Happened)
        {
            _blastFx.Add((e.Position, _clock, e.Radius));
            if (e.Sound is { Length: > 0 } s)
                _audio?.Play(s, e.Position);
            else
                _audio?.PlayAny(new[] { "EXPLOSION_VEHICLE_01_01", "EXPLOSION_GAS_TANK_01_01" }, e.Position);
            float d = SN.Vector3.Distance(e.Position, _target);
            _shake = MathF.Max(_shake, Math.Clamp(1.2f - d / 6000f, 0, 1));
            _flash = MathF.Max(_flash, Math.Clamp(1.3f - d / 8000f, 0, 1));
            Log($"Explosion : {e.Name}");
        }
        ex.Happened.Clear();
        // backdraft : les flammes jaillissent par la porte
        foreach ((SN.Vector3 pos, _, string? room) in _session.Units.Backdrafts)
        {
            _blastFx.Add((pos + new SN.Vector3(0, 0, 60), _clock, 700));
            float d = SN.Vector3.Distance(pos, _target);
            _shake = MathF.Max(_shake, Math.Clamp(1.1f - d / 6000f, 0, 1));
            _flash = MathF.Max(_flash, Math.Clamp(1.2f - d / 8000f, 0, 1));
        }
        _session.Units.Backdrafts.Clear();
        _blastFx.RemoveAll(f => _clock - f.t0 > 9);
    }

    // décalage de la caméra pendant la secousse
    private SN.Vector3 ShakeOffset()
    {
        if (_shake <= 0)
            return SN.Vector3.Zero;
        float a = _shake * _shake * MathF.Min(120, _dist * 0.02f);
        return new SN.Vector3(MathF.Sin(_clock * 53) * a, MathF.Cos(_clock * 47) * a, MathF.Sin(_clock * 61) * a * 0.6f);
    }

    // fumée de l'explosion (dessinée avec la fumée) puis boule de feu (couleurs additionnées)
    private void ExplosionSmoke(SN.Vector3 right, SN.Vector3 up)
    {
        // fumée noire qui filtre sous les portes d'une pièce où le feu couve (risque de backdraft)
        int di = 0;
        foreach (Opening door in _session.Units.SmolderingDoors)
        {
            di++;
            for (int q = 0; q < 6; q++)
            {
                float t = Frac(_clock * 0.35f + q / 6f + di * 0.17f);
                SN.Vector3 p = door.Center + new SN.Vector3(MathF.Sin(q * 2.1f + di) * 40, MathF.Cos(q * 1.7f + di) * 40, -70 + t * 260);
                P(p, 90 + 160 * t, new SN.Vector4(0.08f, 0.08f, 0.09f, 0.55f * (1 - t)), right, up);
            }
        }
        foreach ((SN.Vector3 pos, float t0, float r) in _blastFx)
        {
            float age = _clock - t0;
            for (int q = 0; q < 14; q++)
            {
                float ph = Frac(q * 0.618f), life = 2.5f + 6f * ph;
                float t = (age - 0.15f) / life;
                if (t < 0 || t > 1)
                    continue;
                float ang = q * 2.4f, spread = r * (0.25f + 0.55f * MathF.Sqrt(t)) * (0.4f + ph);
                SN.Vector3 p = pos + new SN.Vector3(MathF.Cos(ang) * spread, MathF.Sin(ang) * spread, 150 + t * (600 + 900 * ph));
                float g = 0.12f + 0.2f * ph;
                P(p, r * (0.7f + 1.6f * t), new SN.Vector4(g, g, g * 1.05f, 0.75f * (1 - t) * MathF.Min(1, age * 4)), right, up);
            }
        }
    }

    private void ExplosionFire(SN.Vector3 right, SN.Vector3 up)
    {
        foreach ((SN.Vector3 pos, float t0, float r) in _blastFx)
        {
            float age = _clock - t0;
            if (age > 2.2f)
                continue;
            // éclair
            if (age < 0.35f)
                P(pos + new SN.Vector3(0, 0, 150), r * (1.6f + 3 * age), new SN.Vector4(1f, 0.6f, 0.25f, 0.45f * (1 - age / 0.35f)), right, up);
            for (int q = 0; q < 24; q++)
            {
                float ph = Frac(q * 0.618f + 0.3f), life = 0.6f + 1.4f * ph;
                float t = age / life;
                if (t > 1)
                    continue;
                float ang = q * 2.4f, el = 0.3f + 0.7f * Frac(q * 0.37f);
                float dist = r * MathF.Sqrt(t) * (0.5f + 0.6f * ph);
                SN.Vector3 dir = new SN.Vector3(MathF.Cos(ang) * (1 - el * 0.6f), MathF.Sin(ang) * (1 - el * 0.6f), el);
                SN.Vector3 p = pos + new SN.Vector3(0, 0, 80) + dir * dist + new SN.Vector3(0, 0, t * t * 300);
                SN.Vector4 col = SN.Vector4.Lerp(new SN.Vector4(1f, 0.75f, 0.35f, 1f), new SN.Vector4(0.85f, 0.18f, 0.03f, 1f), Math.Clamp(t * 2f, 0, 1));
                col.W = (1 - t) * 0.5f;
                P(p, r * (0.45f + 0.8f * t) * (0.7f + 0.6f * ph), col, right, up);
            }
        }
    }
}
