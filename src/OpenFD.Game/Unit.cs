using System.Numerics;
using OpenFD.Fire;
using OpenFD.Formats;

namespace OpenFD.Game;

/// <summary>Une unité du joueur : véhicule ou pompier (classes Design\Unit\…).</summary>
public sealed class Unit
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string ClassPath { get; init; }     // « Fireman\fireman_sp »
    public required string Model { get; init; }         // chemin Gfx du modèle 3D
    public required bool IsVehicle { get; init; }
    public Vector3 Position;
    public float Heading;                                 // radians, 0 = vers +X
    public float Speed;                                   // unités par seconde
    public float BackSpeed;                               // véhicule : marche arrière ([MOVING] BackwardSpeed), unités par seconde
    public float Accel = 480, Decel = 360;                // véhicule : accélération et freinage ([MOVING] AcceleratePower, DeceleratePower), unités/s²
    public float CurSpeed;                                // véhicule : vitesse du moment (il accélère au départ et freine à l'arrivée)
    public float TurnRadius = 800f;                       // véhicule : rayon de braquage, d'après [MOVING] TurnSpeed
    public Extinguisher? Nozzle;                          // lance (pompiers, médecin)
    public bool HasHose;                                  // (non utilisé : le jeu n'a pas de tuyaux)
    public bool CanHeal;                                  // médecin : peut réanimer un civil inconscient
    public float ResupplyPower;                           // véhicule : eau rendue par seconde aux pompiers (0 = aucune)
    public string SupplyAgent = "WATER";                  // véhicule : agent qu'il fournit ([CAPACITIES_RESERVE] FireAgent, voir Agents.cs)
    public OpenFD.Fire.HotPoint? WarnedAgent;             // feu pour lequel « mauvais agent » a déjà été annoncé
    public bool IsAmbulance;
    /// <summary>Bruit du moteur en marche et sirène (étiquettes de SoundDB_Vehicle.INI), volume de la sirène (0 à 1).</summary>
    public string? RunSound, SirenSound;
    public float SirenVolume = 1;
    /// <summary>Véhicule posé dans le niveau (LevelVehicles.cs) : dessiné pièce par pièce, animé par le script (ANM_Play).</summary>
    public bool FromLevel;
    /// <summary>Ne roule pas (l'hélicoptère sanitaire : le script le fait atterrir et décoller).</summary>
    public bool Fixed;
    public Civilian? GoTo;                                // civil vers lequel l'unité se dirige
    public Civilian? Carrying;                            // civil inconscient porté sur le dos
    public CarryPhase Carry;
    public float CarryTimer;
    public Unit? GoToVehicle;                             // véhicule vers lequel l'unité se dirige (remplir, déposer des civils)
    public Vector2 GoToVehicleAim;                        // où était (ou allait) ce véhicule quand le chemin a été tracé
    public float HealTimer;
    public bool CanBreak, CanUse;                         // hache (portes, fenêtres) ; ouvrir une porte
    public bool CanDemolish;                              // bulldozer : dégage les débris « CouldBeDemolish »
    public bool CanExtricate;                             // désincarcérateur : découpe les portails « à désincarcérer »
    public float ExtricatePower = 20;                     // [CAPACITIES_PROPS] ExtricatePower : points de vie enlevés par seconde
    public float Shove;                                   // coup de pelle en cours : avancée du bulldozer (0 à 1, dessin)
    public bool CanGrapple;                               // grappin ([CAPACITIES_ACTIONS] CanGrapplin : le « scout »)
    public bool CanLadder;                                // pompier : monte à l'échelle d'un camion échelle ([CAPACITIES_ACTIONS] CanLadder)
    public bool IsBoat;                                   // bateau-pompe ([UNIT] Type=FIREBOAT) : seulement sur l'eau
    public bool IsAerial;                                 // camion échelle ([CAPACITIES_ACTIONS] action LADDER)
    public float LadderSpeed = 200, LadderMax = 2500;     // [UNIT] Ladder_Translation_Speed, Ladder_Max_Length
    /// <summary>Camion échelle, repère du modèle : axe de la tourelle (ROTZ1), charnière de l'échelle (LADDER1), longueur repliée.</summary>
    public Vector3 LadderPivot = new Vector3(0, 426, 245), LadderHinge = new Vector3(6.6f, 602, 300);
    public float LadderRest = 894;
    public Ladder? Ladder;                                // camion : son échelle (en route, dressée ou en train de se replier)
    public Opening? LadderNext;                           // camion : fenêtre suivante, une fois l'échelle repliée
    public Vector2? DriveNext;                            // camion : trajet demandé, une fois l'échelle repliée
    public Ladder? OnLadder;                              // pompier sur une échelle
    public LadderPhase LadderStep;
    public float LadderTimer;
    public bool LadderEnter;                              // sur l'échelle en montant : il entrera par la fenêtre (sinon il redescend)
    public bool PassingWindow;
    public Teleport? Stairs;                              // dans un escalier (Teleports.cs) : caché, il ressort à StairsTo
    public float StairsTimer;
    public Vector2 StairsTo;
    public Vector3 StairsFrom;
    /// <summary>Sur une échelle verticale fixe (Teleport.Vertical) : grimpe, passe le haut, enjambe, redescend.</summary>
    public VerticalPhase Vertical;
    /// <summary>
    /// Après un ordre d'attaque du joueur : point du feu demandé, puis du dernier feu éteint. L'IA choisit le feu suivant
    /// le plus près de ce point (et non de l'unité) : elle finit le foyer commencé avant de passer à un autre. Un simple
    /// ordre de déplacement l'efface.
    /// </summary>
    public Vector2? FireFocus;                            // escalier extérieur (Walk) : point de départ de la montée ou de la descente
    public Teleport? FakeStairs;                          // envoyé sur un faux escalier (Lab) : son message à l'arrivée                            // casse ou ouvre la fenêtre de l'échelle pour passer (chemin en cours)
    public float ClimbSpeed = 133;                        // [MOVING] ClimbGrapplinSpeed (unités par seconde)
    public ClimbPhase Climb;                              // passage d'un mur au grappin (voir Grapple.cs)
    public float ClimbTimer;
    public Vector3 ClimbFrom, ClimbWall, ClimbTo, ClimbAnchor;   // ancre : là où le grappin est accroché (corde)
    public GrapplePoint? ClimbGrapple;
    public bool CanRepair;                                // technicien : répare les objets CouldBeRepair
    public UsableObject? UseTarget;                       // objet sur lequel agir (armoire électrique…)
    public float BreakPower = 1;
    public Opening? Opening;                              // porte ou fenêtre à casser / ouvrir
    public bool OpeningBreak;
    public float ActionTimer;
    public int Blows;                                     // coups de hache donnés (affichage)
    public readonly Queue<Vector2> Path = new();
    public HotPoint? Target;                              // feu à éteindre
    public string Status = "";
    public bool Moving => Path.Count > 0;
    /// <summary>Blessé (backdraft, explosion) : au sol, ne peut rien faire pendant ce temps (s).</summary>
    public float InjuredTimer;
    public int Wounds;
    /// <summary>Inconscient (backdraft) : reste au sol jusqu'à ce qu'un autre pompier le ranime.</summary>
    public bool Unconscious;
    public bool CriticalWarned;                           // alerte « à bout de forces » (20 % d'endurance) déjà donnée
    public Unit? ReviveTarget;                            // collègue inconscient que l'unité va ranimer
    /// <summary>Véhicule : places ([CAPACITIES_PROPS] EmbarkCapacity) et passagers à bord.</summary>
    public int Seats;
    public readonly List<Unit> Passengers = new();
    public Unit? InVehicle;                               // pompier à bord de ce véhicule (invisible, suit le véhicule)
    public bool Boarding;                                 // en route pour monter dans GoToVehicle
    public bool AutoResupply;                             // [CAPACITIES_PROPS] AutomaticResupply : se recharge près d'un camion
    public bool IsCanon;                                  // [UNIT] TYPE=CANON : grosse lance sans eau, reliée à un camion (Canon.cs)
    public Unit? FeedTruck;                               // canon : camion qui l'alimente (null : aucun à portée)
    public bool IsDog;                                    // [UNIT] TYPE=CYNOPHILE : maître-chien, flaire les victimes enterrées (Dog.cs)
    public float SenseTimer;                              // chien : aboiement en cours (animation SENSE_VICTIM)
    public bool OnBoard => InVehicle != null;
    /// <summary>Véhicule : équipage à bord (classes), qui descend quand le véhicule est arrivé.</summary>
    public readonly List<string> Crew = new();
    public string? TeamName;                              // nom de l'équipe (« H7 ») : journal, ligne du panneau des unités
    public bool InFire;                                   // dans le rayon de chaleur d'un feu (icône du feu sur son image)
    /// <summary>Endurance (pompiers : [CAPACITIES_PROPS] StaminaPoints/Max/Regen) : la chaleur des feux la fait baisser ;
    /// à 0, le pompier s'évanouit. Vie (véhicules : [DAMAGE] LifePoints, 500) : à 0, le véhicule est hors service.</summary>
    public float Stamina = 100, StaminaMax = 100, StaminaRegen = 0.5f, HeatCoef = 1, SmokeCoef = 0.7f;
    public float Life = 500, LifeMax = 500, FireDamageCoef = 3;
    public float ScriptHeatCoef = 1;                      // ENT_SetFireDamageCoef sur un pompier (0 : insensible au feu)
    public float SuitHeatCoef = 1;                        // combinaison ignifugée : chaleur × FireDamageCoef / celui d'un pompier (1/5)
    public bool Destroyed;                                // véhicule détruit : inutilisable jusqu'à la fin de la partie
    public bool HealsUnits;                               // ambulance : soigne les pompiers venus à elle
    public string? Icon;                                  // [UNIT] Icon : image de l'unité (Data\Overlay\Textures)
    public float Reversing, ManeuverLeft;                 // véhicule : marche arrière restante ; longueur de la manœuvre en cours
    public int ManeuverDir;                               // -1 recule, 1 avance
    /// <summary>Unité créée par le script de la mission (ENT_CreateUnitPath) : son nom dans le script.</summary>
    public string? ScriptName;
    /// <summary>Cachée par le script (ENT_SetVisibility 0) : ni dessinée, ni choisie, ni comptée.</summary>
    public bool Hidden;
}
