using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SN = System.Numerics;

namespace OpenFD.MG;

/// <summary>
/// Maillage sur la carte graphique, découpé en groupes (texture, éclairage précalculé, détourage alpha).
/// Dual = sommets à deux uv (décor éclairé par le jeu), sinon position + normale + uv (objets, personnages).
/// </summary>
public sealed class MeshGpu : IDisposable
{
    public VertexBuffer Vb = null!; public IndexBuffer Ib = null!;
    public bool Dual; public int VertexCount;
    public List<(int start, int count, Texture2D? tex, Texture2D? lm, bool alpha)> Groups = new();
    private VertexPositionNormalTexture[]? _dyn;

    public static MeshGpu Create(GraphicsDevice gd, float[] pos, float[] nrm, float[] uv0, uint[] idx, bool dynamic = false)
    {
        int n = pos.Length / 3;
        VertexPositionNormalTexture[] v = new VertexPositionNormalTexture[n];
        for (int i = 0; i < n; i++)
            v[i] = new VertexPositionNormalTexture(new Vector3(pos[3 * i], pos[3 * i + 1], pos[3 * i + 2]), new Vector3(nrm[3 * i], nrm[3 * i + 1], nrm[3 * i + 2]), new Vector2(uv0[2 * i], uv0[2 * i + 1]));
        MeshGpu m = new MeshGpu { VertexCount = n };
        m.Vb = dynamic ? new DynamicVertexBuffer(gd, typeof(VertexPositionNormalTexture), n, BufferUsage.WriteOnly) : new VertexBuffer(gd, typeof(VertexPositionNormalTexture), n, BufferUsage.WriteOnly);
        m.Vb.SetData(v);
        if (dynamic)
            m._dyn = v;
        m.Ib = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, idx.Length, BufferUsage.WriteOnly);
        m.Ib.SetData(idx.Select(x => (int)x).ToArray());
        return m;
    }

    public static MeshGpu CreateDual(GraphicsDevice gd, VertexDual[] v, int[] idx)
    {
        MeshGpu m = new MeshGpu { VertexCount = v.Length, Dual = true };
        m.Vb = new VertexBuffer(gd, VertexDual.Decl, v.Length, BufferUsage.WriteOnly); m.Vb.SetData(v);
        m.Ib = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, idx.Length, BufferUsage.WriteOnly); m.Ib.SetData(idx);
        return m;
    }

    public static MeshGpu CreateLit(GraphicsDevice gd, VertexPositionNormalTexture[] v, int[] idx)
    {
        MeshGpu m = new MeshGpu { VertexCount = v.Length };
        m.Vb = new VertexBuffer(gd, typeof(VertexPositionNormalTexture), v.Length, BufferUsage.WriteOnly); m.Vb.SetData(v);
        m.Ib = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits, idx.Length, BufferUsage.WriteOnly); m.Ib.SetData(idx);
        return m;
    }

    /// <summary>Maillage déformé (personnage animé) : remplace positions et normales.</summary>
    public void UpdateVertices(float[] pos, float[] nrm)
    {
        if (_dyn == null)
            return;
        for (int i = 0; i < _dyn.Length; i++)
        {
            _dyn[i].Position = new Vector3(pos[3 * i], pos[3 * i + 1], pos[3 * i + 2]);
            _dyn[i].Normal = new Vector3(nrm[3 * i], nrm[3 * i + 1], nrm[3 * i + 2]);
        }
        ((DynamicVertexBuffer)Vb).SetData(_dyn, 0, _dyn.Length, SetDataOptions.Discard);
    }

    public void Dispose() { Vb?.Dispose(); Ib?.Dispose(); }
}
