using System.Numerics;
using OpenFD.Fire;
using OpenFD.Formats;

namespace OpenFD.Game;

/// <summary>Pompier qui porte un civil inconscient : il le soulève, le porte sur son dos, puis le dépose à l'ambulance.</summary>
public enum CarryPhase { None, Lifting, Walking, Dropping }
