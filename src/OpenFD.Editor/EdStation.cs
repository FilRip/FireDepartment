namespace OpenFD.MG;

/// <summary>
/// Caserne de la mission dans l'éditeur : celle du départ ([FIRESTATION] InitialDispatch) ou un renfort
/// (Reinforcement1 à 9), avec ses unités (classes « Firevehicle\Veh_P_pumper », « Fireman\fireman_sp »).
/// </summary>
internal sealed class EdStation
{
    /// <summary>Fichier de la caserne (Design\Firestation\&lt;Cfg&gt;.cfg) ; vide pour une nouvelle.</summary>
    public string Cfg = "";

    /// <summary>Nom de code ([FIRESTATION] CodeName, « &amp;E9 ») : le nom de l'équipe dans le jeu.</summary>
    public string Code = "";

    /// <summary>[FIRESTATION] Name et Icon d'origine, recopiés tels quels.</summary>
    public string Name = "";
    public string Icon = "";

    /// <summary>Unités, dans l'ordre (20 au plus).</summary>
    public List<string> Units = new();

    /// <summary>Renfort : délai d'arrivée (s).</summary>
    public float Eta = 60;

    /// <summary>Point d'arrivée (waypoints locstartN_k et locendN_k).</summary>
    public int Spawn = 1;

    /// <summary>Caserne aérienne (bombardier d'eau, [FIRESTATION] AirSupport) : ses unités ne se changent pas ici.</summary>
    public bool Air;

    /// <summary>Unités ou nom de code changés : la caserne est écrite pour la mission.</summary>
    public bool Changed;

    /// <summary>Nom de code affiché (sans « &amp; » ni guillemets).</summary>
    public string ShownCode => Code.Trim('&', '"', ' ');
}
