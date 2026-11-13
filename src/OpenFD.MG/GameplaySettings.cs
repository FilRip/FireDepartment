using System.Text.Json;
using OpenFD.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SN = System.Numerics;

namespace OpenFD.MG;

/// <summary>Réglages du jeu modifiables à la main dans Save\FD3_CSharp_options.json (rubrique « Gameplay »).</summary>
public sealed class GameplaySettings
{
    public float HeatFactor { get; set; } = 4f;            // chaleur des feux → endurance des pompiers
    public float SmokeFactor { get; set; } = 0.08f;        // fumée épaisse → endurance des pompiers
    public float AmbulanceHeal { get; set; } = 12f;        // endurance rendue par seconde à l'ambulance
    public float ReviveStamina { get; set; } = 0.35f;      // endurance (part du maximum) après une réanimation
    public float VehicleFireFactor { get; set; } = 1.5f;   // feu → vie des véhicules
    public float AutoAttackRange { get; set; } = 2f;       // portée où un pompier libre va de lui-même éteindre un feu (× portée de sa lance)
    public float AttackBackOff { get; set; } = 0.5f;       // feu tout proche : recul du pompier (part de sa distance d'arrosage habituelle)
    public float StaminaLoss { get; set; } = 0.25f;        // vitesse de la perte d'endurance des pompiers (chaleur + fumée), multiplie HeatFactor et SmokeFactor
    public float CarrySpeed { get; set; } = 0.6f;          // pompier qui porte un civil inconscient : part de sa vitesse normale
    public float ArrowShiftSpeed { get; set; } = 4.8f;     // flèches + Maj : vitesse du défilement de la vue (× la vitesse normale)
    public float FlameSpeed { get; set; } = 10f;           // animation des flammes : images par seconde
    public float FlameSpeedVariation { get; set; } = 4f;   // écart d'une flamme à l'autre (images par seconde en plus, au hasard)
    public float ObjectiveTimeFactor { get; set; } = 2f;
    public bool OnlyOneOnLadder { get; set; }              // une seule personne à la fois sur l'échelle d'un camion : les autres attendent leur tour devant   // objectifs « vie d'un objet » (Mriya : générateur, 2 min en facile dans le jeu d'origine) : durée × ce facteur
}
