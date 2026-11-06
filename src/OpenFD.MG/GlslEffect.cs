using System.Text;
using Microsoft.Xna.Framework.Graphics;

namespace OpenFD.MG;

/// <summary>
/// Fabrique un effet MonoGame (format MGFX version 10, profil OpenGL) à partir de code GLSL écrit à la main,
/// sans outil de compilation de shaders. Conventions (celles des effets compilés par MonoGame pour OpenGL) :
///  • constantes des sommets : <c>uniform vec4 vs_uniforms_vec4[n]</c>, des pixels : <c>ps_uniforms_vec4[n]</c> ;
///    une matrice occupe 4 vec4 (colonnes de la matrice XNA : <c>dot(p, u[i])</c> donne la coordonnée i) ;
///  • attributs <c>vs_v0</c>, <c>vs_v1</c>… ; textures <c>ps_s0</c>, <c>ps_s1</c>… ; <c>uniform vec4 posFixup</c> à appliquer en fin de shader de sommets.
/// </summary>
public static class GlslEffect
{
    public sealed record Param(string Name, int Vec4Count, bool Vertex);   // 4 = matrice, 1 = vecteur

    public static Effect Create(GraphicsDevice gd, string vs, string ps, Param[] parameters, string[] textures,
        (string name, VertexElementUsage usage, int index)[] attributes)
    {
        MemoryStream ms = new MemoryStream();
        BinaryWriter w = new BinaryWriter(ms);
        const int Signature = 0x5846474D;   // « MGFX »
        w.Write(Signature);
        w.Write((byte)10);
        w.Write((byte)0);
        w.Write((vs + ps).GetHashCode() ^ 0x0FD3);

        // tampons de constantes : 0 = sommets, 1 = pixels
        List<(Param p, int i)> vsParams = parameters.Select((p, i) => (p, i)).Where(x => x.p.Vertex).ToList();
        List<(Param p, int i)> psParams = parameters.Select((p, i) => (p, i)).Where(x => !x.p.Vertex).ToList();
        w.Write(2);
        foreach ((string? name, List<(Param p, int i)>? list) in new[] { ("vs_uniforms_vec4", vsParams), ("ps_uniforms_vec4", psParams) })
        {
            w.Write(name);
            w.Write((short)(16 * Math.Max(1, list.Sum(x => x.p.Vec4Count))));
            w.Write(list.Count);
            int off = 0;
            foreach ((Param? p, int i) in list)
            {
                w.Write(i);
                w.Write((ushort)off);
                off += 16 * p.Vec4Count;
            }
        }

        // shaders : 0 = sommets, 1 = pixels
        w.Write(2);
        WriteShader(w, true, vs, Array.Empty<(string, int, int)>(), 0, attributes);
        WriteShader(w, false, ps, textures.Select((t, k) => ($"ps_s{k}", k, parameters.Length + k)).ToArray(), 1, Array.Empty<(string, VertexElementUsage, int)>());

        // paramètres : flottants puis textures
        w.Write(parameters.Length + textures.Length);
        foreach (Param p in parameters)
        {
            bool m = p.Vec4Count == 4;
            w.Write((byte)(m ? 2 : 1));
            w.Write((byte)3);
            w.Write(p.Name);
            w.Write("");
            w.Write(0);
            w.Write((byte)(m ? 4 : 1));
            w.Write((byte)4);
            w.Write(0);
            w.Write(0);
            for (int i = 0; i < 4 * p.Vec4Count; i++)
                w.Write(0f);
        }
        foreach (string t in textures)
        {
            w.Write((byte)3);
            w.Write((byte)6);
            w.Write(t);
            w.Write("");
            w.Write(0);
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write(0);
            w.Write(0);
        }

        w.Write(1);
        w.Write("T0");
        w.Write(0);
        w.Write(1);
        w.Write("P0");
        w.Write(0);
        w.Write(0);
        w.Write(1);
        w.Write(false);
        w.Write(false);
        w.Write(false);
        w.Write(Signature);
        return new Effect(gd, ms.ToArray());
    }

    private static void WriteShader(BinaryWriter w, bool vertex, string glsl, (string name, int slot, int param)[] samplers, byte cbuffer,
        (string name, VertexElementUsage usage, int index)[] attributes)
    {
        w.Write(vertex);
        byte[] code = Encoding.ASCII.GetBytes(glsl);
        w.Write(code.Length);
        w.Write(code);
        w.Write((byte)samplers.Length);
        foreach ((string? name, int slot, int param) in samplers)
        {
            w.Write((byte)0);
            w.Write((byte)slot);
            w.Write((byte)slot);
            w.Write(false);
            w.Write(name);
            w.Write((byte)param);
        }
        w.Write((byte)1);
        w.Write(cbuffer);
        w.Write((byte)attributes.Length);
        foreach ((string? name, VertexElementUsage usage, int index) in attributes)
        {
            w.Write(name);
            w.Write((byte)usage);
            w.Write((byte)index);
            w.Write((short)-1);
        }
    }

    /// <summary>Fin commune des shaders de sommets : passage aux conventions d'OpenGL (comme les effets de MonoGame).</summary>
    public const string PosFixup = @"
    gl_Position.y = gl_Position.y * posFixup.y;
    gl_Position.xy += posFixup.zw * gl_Position.ww;
    gl_Position.z = gl_Position.z * 2.0 - gl_Position.w;";
}
