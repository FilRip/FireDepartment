using OpenFD.Formats;

namespace OpenFD.Game;

// hélicoptères posés dans le niveau (ambulances de FD1 Banque et Forêt, Forest, Stadium) : le script les montre ou les
// cache ; on les voit arriver (animation d'atterrissage du jeu, rotors qui tournent, de loin et de haut) et repartir
public sealed partial class UnitManager
{
    /// <summary>Animations du modèle Helico_FD_animation_base (Gfx\Anim\SingleItem) : atterrissage (20 s) et décollage (23 s).</summary>
    public const string HeliLandAnim = "SingleItem\\si_Helico_FD_animation_landing";
    public const string HeliTakeOffAnim = "SingleItem\\si_Helico_FD_animation_takeOff";
    private readonly Dictionary<string, float> _animLengths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ENT_SetVisibility sur une unité. Un hélicoptère montré atterrit (il n'est ambulance qu'une fois posé) ; caché, il
    /// décolle et ne disparaît qu'une fois parti. Sans animation (autre modèle) : il apparaît ou disparaît d'un coup.
    /// </summary>
    public void SetUnitVisible(Unit u, bool visible)
    {
        if (!u.IsHelicopter || _s.Host is not { } host)
        {
            u.Hidden = !visible;
            return;
        }
        // le script a déjà fait décoller l'hélicoptère (ANM_Play du décollage, FD2 : Forest, Stadium) : il le cache ensuite
        if (!visible && !u.HeliLeaving && host.PlayedAnims.TryGetValue((uint)u.Id, out (string anim, float start, bool loop) played)
            && played.anim.EndsWith("takeOff", StringComparison.OrdinalIgnoreCase))
        {
            u.Hidden = true;
            return;
        }
        string anim = visible ? HeliLandAnim : HeliTakeOffAnim;
        float length = AnimLength(anim);
        bool shown = !u.Hidden && !u.HeliLeaving;
        if (visible == shown || length <= 0 || !ModelHas(u, "helico_Animation"))
        {
            u.Hidden = !visible;
            u.HeliLeaving = false;
            u.HeliLanding = false;
            return;
        }
        if (!u.HeliLanding && !u.HeliLeaving)
        {
            u.HeliAmbulance = u.IsAmbulance;
            u.HeliHeals = u.HealsUnits;
        }
        u.Hidden = false;
        u.HeliLanding = visible;
        u.HeliLeaving = !visible;
        u.HeliUntil = _s.Time + length;
        u.IsAmbulance = false;   // en l'air : ni soins ni civils déposés
        u.HealsUnits = false;
        u.Status = visible ? "atterrit" : "décolle";
        if (!visible)
            UnloadPassengers(u);
        host.PlayedAnims[(uint)u.Id] = (anim, _s.Time, false);
        Log.Add($"{_s.Time,6:0} s : {u.Name} {(visible ? "arrive et atterrit" : "décolle et repart")} ({length:0} s)");
    }

    // atterri : ambulance de nouveau ; reparti : caché
    private void UpdateHelicopters()
    {
        foreach (Unit u in Units.Where(u => (u.HeliLanding || u.HeliLeaving) && _s.Time >= u.HeliUntil))
        {
            if (u.HeliLeaving)
                u.Hidden = true;
            u.IsAmbulance = u.HeliAmbulance;
            u.HealsUnits = u.HeliHeals;
            u.Status = u.HeliLeaving ? "parti" : "posé";
            u.HeliLanding = false;
            u.HeliLeaving = false;
            _s.Host?.PlayedAnims.Remove((uint)u.Id);
        }
    }

    private float AnimLength(string anim)
    {
        if (_animLengths.TryGetValue(anim, out float d))
            return d;
        try
        {
            d = AnimClip.Parse(_files.Read("Gfx\\Anim\\" + anim + ".anim")).Duration;
        }
        catch (Exception)
        {
            d = 0;
        }
        _animLengths[anim] = d;
        return d;
    }

    // le modèle de l'hélicoptère a le nœud que les animations déplacent (le modèle du jeu, Helico_FD_animation_base)
    private bool ModelHas(Unit u, string node)
    {
        try
        {
            return CharacterRig.Build(_assets!, _files, u.Model)?.Names.Contains(node, StringComparer.OrdinalIgnoreCase) == true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
