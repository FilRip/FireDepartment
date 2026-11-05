using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SN = System.Numerics;

namespace OpenFD.MG;

public static class Conv
{
    // System.Numerics et XNA rangent les matrices de la même façon (vecteur-ligne, M11…M44)
    public static Matrix X(this SN.Matrix4x4 m) => new(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
    public static Vector3 X(this SN.Vector3 v) => new(v.X, v.Y, v.Z);
    public static Vector2 X(this SN.Vector2 v) => new(v.X, v.Y);
    public static Color C(this SN.Vector4 c) => new(c.X, c.Y, c.Z, c.W);
}
