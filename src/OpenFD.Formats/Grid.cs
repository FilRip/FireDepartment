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

    /// <summary>Zones changées pendant la partie (case de grillage rouverte, voir le jeu), avec leur valeur d'origine.</summary>
    private readonly Dictionary<int, ushort> _zoneAOriginal = new();

    /// <summary>Change la zone d'une case pour la partie seulement : le fichier enregistré garde la valeur d'origine.</summary>
    public void PatchZoneA(int index, ushort zone)
    {
        _zoneAOriginal.TryAdd(index, ZoneA[index]);
        ZoneA[index] = zone;
    }

    private ushort SavedZoneA(int k) => _zoneAOriginal.TryGetValue(k, out ushort z) ? z : ZoneA[k];
    public bool Inside(int cx, int cy) => cx >= 0 && cy >= 0 && cx < Width && cy < Height;
    public (int cx, int cy) CellOf(Vector3 p) => ((int)MathF.Floor(p.X / CellSize), (int)MathF.Floor(p.Y / CellSize));

    /// <summary>Nouvelle grille (mission créée par l'éditeur) : toutes les cases praticables (zone 1), au niveau 0.</summary>
    public static Grid Flat(int width, int height)
    {
        int n = width * height;
        Grid g = new Grid { Width = width, Height = height, GroundHeight = new float[n], ZoneA = new ushort[n], ZoneB = new ushort[n] };
        Array.Fill(g.ZoneA, (ushort)1);
        return g;
    }

    /// <summary>
    /// Fichier .grid : <paramref name="original"/> (même taille) recopié avec les hauteurs et les zones de cette grille, sa fin
    /// (tables du jeu d'origine) gardée telle quelle ; sans original, un fichier neuf (en-tête, cases, fin vide).
    /// </summary>
    public byte[] Serialize(byte[]? original = null)
    {
        if (original != null)
        {
            try
            {
                Grid o = Parse(original);
                if (o.Width == Width && o.Height == Height)
                {
                    byte[] d = (byte[])original.Clone();
                    int p = FirstCell(d);
                    for (int i = 0; i < Width * Height; i++)
                    {
                        int e = Chunks.NodeEnd(d, p);
                        int k = Index(i / Height, i % Height);
                        BitConverter.TryWriteBytes(d.AsSpan(p + 5), SavedZoneA(k));
                        BitConverter.TryWriteBytes(d.AsSpan(p + 7), ZoneB[k]);
                        BitConverter.TryWriteBytes(d.AsSpan(p + 9), GroundHeight[k]);
                        p = e;
                    }
                    return d;
                }
            }
            catch (InvalidDataException)
            {
            }
        }
        using MemoryStream ms = new MemoryStream();
        using BinaryWriter w = new BinaryWriter(ms);
        void Str(string s)
        {
            w.Write(s.Length + 1);
            w.Write(Encoding.Latin1.GetBytes(s));
            w.Write((byte)0);
        }
        void Begin(byte version) => w.Write(new byte[] { 0xAA, 0xAA, 0xFF, 0xFF, version });
        void End() => w.Write(new byte[] { 0xCC, 0xCC, 0xBB, 0xBB });
        Str("GRD");
        Str("18");
        Str("1.04c");
        w.Write((ushort)Width);
        w.Write((ushort)Height);
        Begin(3);
        w.Write((ushort)Width);
        w.Write((ushort)Height);
        Begin(4);
        for (int i = 0; i < Width * Height; i++)
        {
            int k = Index(i / Height, i % Height);   // colonne par colonne
            Begin(5);
            w.Write(SavedZoneA(k));
            w.Write(ZoneB[k]);
            w.Write(GroundHeight[k]);
            End();
        }
        w.Write(0);   // tables du jeu d'origine : vides
        End();
        End();
        w.Write(0L);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Position de la première case dans le fichier (même lecture que Parse).</summary>
    private static int FirstCell(byte[] d)
    {
        int p = 0;
        Chunks.ReadString(d, ref p);
        Chunks.ReadString(d, ref p);
        Chunks.ReadString(d, ref p);
        return p + 4 + 5 + 4 + 5;
    }

    public static Grid Parse(byte[] d)
    {
        int p = 0;
        if (Chunks.ReadString(d, ref p) != "GRD")
            throw new InvalidDataException("Pas un fichier .grid");
        Chunks.ReadString(d, ref p);
        Chunks.ReadString(d, ref p);
        Grid g = new Grid { Width = BitConverter.ToUInt16(d, p), Height = BitConverter.ToUInt16(d, p + 2) };
        p += 4 + 5 + 4;
        if (!Chunks.IsBegin(d, p))
            throw new InvalidDataException("Grille inattendue");
        p += 5;
        int n = g.Width * g.Height;
        g.GroundHeight = new float[n];
        g.ZoneA = new ushort[n];
        g.ZoneB = new ushort[n];
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
