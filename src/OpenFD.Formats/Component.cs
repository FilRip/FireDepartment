using System.Numerics;
using System.Text;

namespace OpenFD.Formats;

public sealed record Component(ushort Id, int Offset, int Size);
