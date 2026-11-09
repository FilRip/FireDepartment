using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SN = System.Numerics;

namespace OpenFD.MG;

/// <summary>Sommet avec deux jeux d'uv : couleur et éclairage précalculé (DualTextureEffect).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VertexDual : IVertexType
{
    public Vector3 Position;
    public Vector2 Uv0;
    public Vector2 Uv1;
    public Vector3 Normal;   // pour les reflets (l'effet à deux textures ne s'en sert pas)
    public static readonly VertexDeclaration Decl = new(
        new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
        new VertexElement(12, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
        new VertexElement(20, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 1),
        new VertexElement(28, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0));
    VertexDeclaration IVertexType.VertexDeclaration => Decl;
}
