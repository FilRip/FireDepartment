using System.Globalization;
using System.Text;

namespace OpenFD.Script;

// Langage des scripts de mission (.scp) : un C simplifié.
// Types : void, s32, f32, mcString, mcBool, EntityID. Instructions : déclarations, affectations,
// if/else, for, while, return, appels. Les noms de fonctions du moteur ne tiennent pas compte de la casse.

public enum Tok { Ident, Int, Float, Str, Op, End }
