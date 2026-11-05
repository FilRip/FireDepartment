using System.Text.Json;
using OpenFD.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SN = System.Numerics;

namespace OpenFD.MG;

/// <summary>Partie sauvegardée (Save\FD3_CSharp_parties\*.json).</summary>
public sealed class SavedGame
{
    public int Version { get; set; } = 1;
    public string Mission { get; set; } = "";
    public string Difficulty { get; set; } = "";
    public string Nationality { get; set; } = "";
    public DateTime Date { get; set; }
    public int Steps { get; set; }
    public List<JournalEntry> Journal { get; set; } = new();
    public float[] Camera { get; set; } = Array.Empty<float>();   // cible x y z, cap, inclinaison, distance
}
