using System.Numerics;
using System.Text;

namespace OpenFD.Formats;

/// <summary>Grille de simulation (.grid) : cellules de 100 unités, stockées colonne par colonne.</summary>
public sealed class Grid
{
    public const float CellSize = 100f;
    public int Width, Height;
    public float[] GroundHeight = Array.Empty<float>();
    public ushort[] ZoneA = Array.Empty<ushort>(), ZoneB = Array.Empty<ushort>();

    public int Index(int cx, int cy) => cy * Width + cx;
    public bool Inside(int cx, int cy) => cx >= 0 && cy >= 0 && cx < Width && cy < Height;
    public (int cx, int cy) CellOf(Vector3 p) => ((int)MathF.Floor(p.X / CellSize), (int)MathF.Floor(p.Y / CellSize));

    public static Grid Parse(byte[] d)
    {
        int p = 0;
        if (Chunks.ReadString(d, ref p) != "GRD")
            throw new InvalidDataException("Pas un fichier .grid");
        Chunks.ReadString(d, ref p); Chunks.ReadString(d, ref p);
        Grid g = new Grid { Width = BitConverter.ToUInt16(d, p), Height = BitConverter.ToUInt16(d, p + 2) };
        p += 4 + 5 + 4;
        if (!Chunks.IsBegin(d, p))
            throw new InvalidDataException("Grille inattendue");
        p += 5;
        int n = g.Width * g.Height;
        g.GroundHeight = new float[n]; g.ZoneA = new ushort[n]; g.ZoneB = new ushort[n];
        for (int i = 0; i < n; i++)
        {
            int e = Chunks.NodeEnd(d, p);
            int cx = i / g.Height, cy = i % g.Height, k = g.Index(cx, cy);
            g.ZoneA[k] = BitConverter.ToUInt16(d, p + 5);
            g.ZoneB[k] = BitConverter.ToUInt16(d, p + 7);
            g.GroundHeight[k] = BitConverter.ToSingle(d, p + 9);
            p = e;
        }
        return g;
    }
}
