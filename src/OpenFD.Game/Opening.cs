using System.Numerics;
using OpenFD.Fire;
using OpenFD.Formats;

namespace OpenFD.Game;

/// <summary>
/// Porte ou fenêtre de la carte (objet dont [OCCUPATION] Occupation_Type = DOOR ou WINDOW).
/// État de départ lu dans le composant A027 (état logique : 0 = « Close », 1 = « Open »).
/// Vie : [DAMAGE] LifePoints ; un coup de hache retire HitDamageCoef × BreakPower × BlowFactor.
/// </summary>
public sealed class Opening
{
    public required uint Id { get; init; }
    public required string Name { get; init; }
    public required bool IsWindow { get; init; }
    public required string? Model { get; init; }
    public required Matrix4x4 World { get; init; }
    public Vector3 Position => World.Translation;
    public bool Breakable;          // [CAPACITIES_ACTIONS] CouldBeBreak et [DAMAGE] UseDamage
    public bool Demolish;           // [CAPACITIES_ACTIONS] CouldBeDemolish : seul un bulldozer (CanDemolish) le dégage
    public float HitSpeed = 20;     // [DAMAGE] HitDamageSpeed : points de vie enlevés par seconde de coups de pelle
    public bool Usable;             // CouldBeUse : s'ouvre et se ferme à la main
    public bool ScriptGate;         // portail animé que seul le script ouvre (ENT_SwitchState) : pas de clic du joueur
    public float Life, MaxLife, HitCoef;
    public OpeningState State;
    public bool OnGround;           // au niveau de la grille (les portes des étages ne touchent pas le chemin)
    public bool Extricate;          // portail à découper : seul le désincarcérateur (CanExtricate) l'ouvre
    public Vector2? ExtricateSide;  // icône de scie : le désincarcérateur travaille de ce côté-là
    public Vector3? SawMark;        // icône de scie qui désigne cet objet (on peut cliquer dessus)
    public string? OnDestroy;       // fonction du script appelée quand l'obstacle est dégagé (composant A024)
    public float BrokenAt = -1;     // instant (s) où il a été dégagé : départ de l'animation de chute
    public string? FallAnim;        // animation du modèle (Gfx\<modèle>.anim) : les battants s'ouvrent, tombent et restent au sol
    public bool IsObstacle;         // gravats, éboulis… à dégager à la hache (repère « axe_symbol » à côté)
    public List<int> Cells = new(); // cases de la grille fermées quand la porte est fermée
    public Vector3 Center;          // milieu du battant (point visé et point de passage)
    public string? SoundLabel;      // [SOUND] LABEL : bruit de la vitre qui casse
    public readonly List<(string action, string script)> Actions = new();   // composant A027 : « Open » / « Close » → fonction du script
    public Vector2 BoxMin, BoxMax;
    /// <summary>Portail coulissant ouvert : cases qui restent fermées (montants, caisson où rentrent les battants).</summary>
    public readonly HashSet<int> OpenBlocked = new();
    /// <summary>Portail coulissant : cases sous le bâti, de part et d'autre du passage (caissons des battants) — toujours fermées.</summary>
    public readonly HashSet<int> FrameCells = new();
    public string? OpenAnim, CloseAnim;
    public Vector2? Lever;          // mécanisme d'ouverture (repère « mechanism_symbol », les rouages) : l'unité s'y arrête   // animations du modèle (portail coulissant) ; sinon la porte pivote de 90°  // emprise du modèle au sol (clic sur tout le portail)
    public bool Passable => IsWindow || State != OpeningState.Closed;
    public bool LetsSmokeOut => State != OpeningState.Closed;
}
