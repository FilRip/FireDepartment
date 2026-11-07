using System.Numerics;
using OpenFD.Fire;
using OpenFD.Formats;

namespace OpenFD.Game;

/// <summary>
/// Aide aérienne : une caserne de la mission marquée [FIRESTATION] AirSupport=1 (classic_canadair, code « AS1 ») n'envoie
/// pas de véhicules. Le script l'accorde (LEV_GrantAirSupport) ; le joueur clique sur son cadre puis sur la zone à
/// arroser ; au bout du délai du renfort (ETA de la mission), le bombardier passe au-dessus et largue :
/// [AIRSTRIKE] RADIUS (rayon arrosé), POWER (quantité d'agent), RANDOMRADIUS (imprécision du largage),
/// BeforeDuration / AfterDuration (durée du passage avant et après le largage).
/// </summary>
public sealed partial class UnitManager
{
    /// <summary>Code de la caserne aérienne de la mission (« AS1 ») ; null s'il n'y en a pas.</summary>
    public string? AirTeam { get; private set; }
    public float AirEta = 30, AirRadius = 3000, AirPower = 1000, AirRandom = 800, AirBefore = 3.33f, AirAfter = 3.33f;
    private string _airAgent = "FOAM";
    private ClassFile? _airClass;

    /// <summary>Passages accordés par le script, pas encore utilisés.</summary>
    public int AirGrants { get; private set; }

    /// <summary>Largage en cours (appelé, avion en route ou en train de passer) ; null sinon.</summary>
    public AirStrike? Strike { get; private set; }

    /// <summary>Le joueur peut appeler le bombardier maintenant (cadre « AS1 » actif).</summary>
    public bool AirAvailable => AirTeam != null && AirGrants > 0 && Strike == null;

    /// <summary>Renfort de la mission : true si c'est la caserne aérienne (elle est alors retenue ici, pas envoyée par la route).</summary>
    private bool SetupAirStation(string cfgName, float eta)
    {
        ClassFile? cfg = Class("Design\\Firestation\\" + cfgName + ".cfg");
        if (cfg == null || !cfg.GetBool("FIRESTATION", "AirSupport"))
            return false;
        AirTeam = TeamCode(cfgName);
        AirEta = eta;
        AirRadius = cfg.GetFloat("AIRSTRIKE", "Radius", 3000);
        AirPower = cfg.GetFloat("AIRSTRIKE", "Power", 1000);
        AirRandom = cfg.GetFloat("AIRSTRIKE", "RandomRadius", 800);
        AirBefore = cfg.GetFloat("AIRSTRIKE", "BeforeDuration", 3.33f);
        AirAfter = cfg.GetFloat("AIRSTRIKE", "AfterDuration", 3.33f);
        string? unit = cfg.Get("FIRESTATION", "UNIT01")?.Trim('"', ' ');
        _airClass = unit == null ? null : Class("Design\\Unit\\" + unit + ".class");
        _airAgent = (_airClass?.Get("CAPACITIES_RESERVE", "FireAgent") ?? "FOAM").Trim('"', ' ').ToUpperInvariant();
        AirModel = _airClass?.Get("DISPLAY", "Model")?.Trim('"', ' ') is { Length: > 0 } m ? SceneAssets.GfxPath("Unit\\FireVehicle", m) : null;
        return true;
    }

    /// <summary>Essais : caserne aérienne ajoutée à une mission qui n'en a pas.</summary>
    public void AddAirStationForTest(string cfgName = "classic_canadair", float eta = 30)
    {
        if (AirTeam == null && SetupAirStation(cfgName, eta) && !Teams.Contains(AirTeam!))
            Teams.Add(AirTeam!);
    }

    /// <summary>Modèle de l'avion (Gfx), pour son ombre au sol.</summary>
    public string? AirModel { get; private set; }

    /// <summary>LEV_GrantAirSupport : un passage du bombardier est accordé (voix VOCAL_AIR_SUPPORT_GRANTED).</summary>
    public void GrantAirSupport()
    {
        if (AirTeam == null)
            return;
        AirGrants++;
        Sounds.Add(("VOCAL_AIR_SUPPORT_GRANTED", null));
        Log.Add($"{_s.Time,6:0} s : aide aérienne accordée ({AirTeam})");
    }

    /// <summary>Le joueur a choisi la zone : le bombardier arrive au bout du délai de la caserne. false si pas disponible.</summary>
    public bool CallAirStrike(Vector2 target)
    {
        if (!AirAvailable)
            return false;
        AirGrants--;
        // imprécision du largage, tirée de façon reproductible (même partie rejouée : même point)
        uint h = (uint)(target.X * 73856093) ^ (uint)(target.Y * 19349663) ^ (uint)(_s.Time * 1000);
        h ^= h >> 13;
        h *= 0x5bd1e995;
        h ^= h >> 15;
        float a = (h & 0xFFFF) / 65536f * MathF.Tau, r = AirRandom * MathF.Sqrt(((h >> 16) & 0xFFFF) / 65536f);
        Strike = new AirStrike
        {
            Target = target,
            Drop = target + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r,
            Called = _s.Time,
            Pass = _s.Time + AirEta + AirBefore,
            End = _s.Time + AirEta + AirBefore + AirAfter,
            Heading = ((h >> 8) & 0xFF) / 256f * MathF.Tau,
        };
        _incoming[AirTeam!] = (_s.Time, AirEta);
        Sounds.Add(("VOCAL_AIR_SUPPORT_CALLED", null));   // « l'avion arrive »
        Log.Add($"{_s.Time,6:0} s : bombardier d'eau appelé sur ({target.X:0}, {target.Y:0}), au-dessus dans {AirEta + AirBefore:0} s");
        return true;
    }

    /// <summary>À chaque pas : passage de l'avion, largage quand il est au-dessus du point.</summary>
    private void UpdateAirStrike()
    {
        if (Strike is not { } s)
            return;
        if (AirTeam != null && _s.Time >= s.Called + AirEta)
            _incoming.Remove(AirTeam);
        if (!s.Dropped && _s.Time >= s.Pass)
        {
            s.Dropped = true;
            Sounds.Add(("CANADAIR_PARTICLE", new Vector3(s.Drop, Ground(s.Drop))));   // l'eau qui tombe au sol
            int n = 0;
            foreach (HotPoint hp in _s.Fire.HotPoints.Where(h => h.IsBurning))
            {
                if (Vector2.Distance(new Vector2(hp.Position.X, hp.Position.Y), s.Drop) > AirRadius)
                    continue;
                _s.Fire.Attack(hp, AirPower, 1f, AirEfficiency(hp.Class.FireType));
                n++;
            }
            Log.Add($"{_s.Time,6:0} s : largage du bombardier d'eau ({n} foyer(s) arrosé(s))");
        }
        if (_s.Time >= s.End)
            Strike = null;
    }

    // efficacité de l'agent du bombardier sur ce type de feu (sa classe, sinon les valeurs de base)
    private float AirEfficiency(char type)
    {
        string key = $"FireAgentEfficiency_{_airAgent}_TYPE_{type}";
        return _airClass?.Get("CAPACITIES_RESERVE", key) is { } v && float.TryParse(v.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float e)
            ? e : FireWorld.AgentEfficiency(_airAgent, type);
    }
}
