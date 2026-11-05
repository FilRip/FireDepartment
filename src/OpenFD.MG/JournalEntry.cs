using System.Text.Json;
using OpenFD.Game;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SN = System.Numerics;

namespace OpenFD.MG;

/// <summary>Ordre du joueur noté pendant la partie : au pas de simulation Step, l'opération Op sur les objets A et B.</summary>
public sealed class JournalEntry
{
    public int Step { get; set; }
    public string Op { get; set; } = "";
    public long A { get; set; }
    public long B { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public string? S { get; set; }
}
