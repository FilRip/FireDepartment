using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace OpenFD.MG;

/// <summary>Sommet du décor : position et trois jeux d'uv (couleur, saleté, éclairage précalculé).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VertexScene : IVertexType
{
    public Vector3 Position;
    public Vector2 Uv0, UvDirt, UvLight;
    public Vector3 Normal;
    public static readonly VertexDeclaration Decl = new(
        new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
        new VertexElement(12, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
        new VertexElement(20, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 1),
        new VertexElement(28, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 2),
        new VertexElement(36, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0));
    VertexDeclaration IVertexType.VertexDeclaration => Decl;
}
