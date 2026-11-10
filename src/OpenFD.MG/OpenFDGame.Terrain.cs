using OpenFD.Formats;
using OpenFD.Game;
using SN = System.Numerics;

namespace OpenFD.MG;

// sol des missions sans décor (créées par l'éditeur de missions) : un maillage tiré des hauteurs de la grille
public sealed partial class OpenFDGame
{
    private const string TerrainTex = "openfd:terrain";
    private bool _noScenery;   // la mission n'a pas de décor : le sol est TerrainAsset

    /// <summary>
    /// Maillage du sol : un sommet à chaque coin de case, à la hauteur moyenne des cases qui le touchent (pentes douces),
    /// texture répétée tous les 4 m.
    /// </summary>
    private SceneAssets.MeshAsset TerrainAsset(Grid g)
    {
        int w = g.Width, h = g.Height, vw = w + 1;
        float Hc(int x, int y) => g.GroundHeight[g.Index(Math.Clamp(x, 0, w - 1), Math.Clamp(y, 0, h - 1))];
        float[] z = new float[vw * (h + 1)];
        for (int y = 0; y <= h; y++)
            for (int x = 0; x <= w; x++)
                z[y * vw + x] = (Hc(x - 1, y - 1) + Hc(x, y - 1) + Hc(x - 1, y) + Hc(x, y)) / 4;
        float[] pos = new float[z.Length * 3], nrm = new float[z.Length * 3], uv = new float[z.Length * 2];
        for (int y = 0; y <= h; y++)
            for (int x = 0; x <= w; x++)
            {
                int i = y * vw + x;
                pos[3 * i] = x * Grid.CellSize;
                pos[3 * i + 1] = y * Grid.CellSize;
                pos[3 * i + 2] = z[i];
                float dx = z[y * vw + Math.Min(w, x + 1)] - z[y * vw + Math.Max(0, x - 1)];
                float dy = z[Math.Min(h, y + 1) * vw + x] - z[Math.Max(0, y - 1) * vw + x];
                SN.Vector3 n = SN.Vector3.Normalize(new SN.Vector3(-dx, -dy, 2 * Grid.CellSize));
                nrm[3 * i] = n.X;
                nrm[3 * i + 1] = n.Y;
                nrm[3 * i + 2] = n.Z;
                uv[2 * i] = x * 0.25f;
                uv[2 * i + 1] = y * 0.25f;
            }
        uint[] idx = new uint[w * h * 6];
        int k = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint a = (uint)(y * vw + x), b = a + 1, c = a + (uint)vw, d = c + 1;
                idx[k++] = a;
                idx[k++] = b;
                idx[k++] = d;
                idx[k++] = a;
                idx[k++] = d;
                idx[k++] = c;
            }
        TerrainTexture();
        return new SceneAssets.MeshAsset
        {
            Mesh = new Mesh { Name = "terrain", Positions = pos, Normals = nrm, Indices = idx, VertexCount = z.Length },
            Groups = new() { new SceneAssets.Group(0, idx.Length, "", TerrainTex, false) },
            Uv0 = uv,
        };
    }

    /// <summary>Texture du sol : herbe rase et terre, faite ici (aucun fichier du jeu n'est commun à toutes les missions).</summary>
    private void TerrainTexture()
    {
        if (_texCache.ContainsKey(TerrainTex))
            return;
        const int S = 128;
        byte[] rgba = new byte[S * S * 4];
        Random r = new Random(7);
        float[] noise = new float[S * S];
        for (int i = 0; i < noise.Length; i++)
            noise[i] = (float)r.NextDouble();
        // bruit adouci (moyenne 3 × 3, bords repliés : la texture se répète sans couture)
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float sum = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        sum += noise[((y + dy + S) % S) * S + (x + dx + S) % S];
                float v = sum / 9, fine = noise[y * S + x];
                int o = (y * S + x) * 4;
                rgba[o] = (byte)Math.Clamp(78 + 40 * v + 18 * fine, 0, 255);
                rgba[o + 1] = (byte)Math.Clamp(92 + 34 * v + 16 * fine, 0, 255);
                rgba[o + 2] = (byte)Math.Clamp(58 + 22 * v + 10 * fine, 0, 255);
                rgba[o + 3] = 255;
            }
        _texCache[TerrainTex] = Tex.FromRgba(GraphicsDevice, S, S, rgba);
    }
}
