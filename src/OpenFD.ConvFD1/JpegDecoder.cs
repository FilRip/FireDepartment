namespace OpenFD.ConvFD1;

/// <summary>
/// Décodeur JPEG « baseline » (Huffman, 8 bits, échantillonnage quelconque, intervalles de reprise) : les dalles de sol
/// de certaines missions FD1 (Quartier : « QUA__X10_Y11.jpg ») n'existent qu'en JPEG. Pas de JPEG progressif.
/// </summary>
internal static class JpegDecoder
{
    private static readonly int[] ZigZag =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21,
        28, 35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61,
        54, 47, 55, 62, 63,
    };

    /// <summary>Table de Huffman : pour chaque longueur de code, le premier code, le nombre de codes et l'indice des valeurs.</summary>
    private sealed class Huffman
    {
        public readonly int[] MaxCode = new int[18];
        public readonly int[] ValPtr = new int[17];
        public readonly int[] MinCode = new int[17];
        public byte[] Values = Array.Empty<byte>();
    }

    private sealed class Component
    {
        public int Id;
        public int H;
        public int V;
        public int Q;
        public int Dc;
        public int Ac;
        public int Pred;
        public byte[] Pixels = Array.Empty<byte>();
        public int Stride;
    }

    /// <summary>Lecteur de bits des données compressées (octets 0xFF00 → 0xFF ; s'arrête sur un marqueur).</summary>
    private sealed class Bits
    {
        private readonly byte[] _d;
        public int Pos;
        private int _acc;
        private int _n;

        public Bits(byte[] d, int pos)
        {
            _d = d;
            Pos = pos;
        }

        public int Bit()
        {
            if (_n == 0)
            {
                int b = 0;
                if (Pos < _d.Length)
                {
                    b = _d[Pos];
                    if (b == 0xFF)
                    {
                        int next = Pos + 1 < _d.Length ? _d[Pos + 1] : 0;
                        if (next == 0)
                            Pos += 2;
                        else
                            b = 0;   // marqueur : on n'avance plus (des zéros)
                    }
                    else
                        Pos++;
                }
                _acc = b;
                _n = 8;
            }
            _n--;
            return (_acc >> _n) & 1;
        }

        public int Receive(int s)
        {
            int v = 0;
            for (int i = 0; i < s; i++)
                v = (v << 1) | Bit();
            return v;
        }

        /// <summary>Reprise (RSTn) : les bits restants sont jetés et le marqueur sauté.</summary>
        public void Restart()
        {
            _n = 0;
            while (Pos + 1 < _d.Length && !(_d[Pos] == 0xFF && _d[Pos + 1] >= 0xD0 && _d[Pos + 1] <= 0xD7))
                Pos++;
            Pos += 2;
        }
    }

    /// <summary>Image décodée en RGBA (ligne du haut en premier) ; null si ce n'est pas un JPEG baseline lisible.</summary>
    public static (int w, int h, byte[] rgba)? Decode(byte[] d)
    {
        try
        {
            return DecodeCore(d);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (int w, int h, byte[] rgba)? DecodeCore(byte[] d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8)
            return null;
        int[][] quant = new int[4][];
        Huffman?[] dcTables = new Huffman?[4];
        Huffman?[] acTables = new Huffman?[4];
        List<Component> comps = new List<Component>();
        int width = 0;
        int height = 0;
        int restart = 0;
        int p = 2;
        while (p + 4 <= d.Length)
        {
            if (d[p] != 0xFF)
            {
                p++;
                continue;
            }
            int marker = d[p + 1];
            p += 2;
            if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01 || marker == 0xFF)
                continue;
            if (marker == 0xD9)
                break;
            int len = (d[p] << 8) | d[p + 1];
            int seg = p + 2;
            int end = p + len;
            switch (marker)
            {
                case 0xDB:
                    while (seg < end)
                    {
                        int pq = d[seg] >> 4;
                        int tq = d[seg] & 15;
                        seg++;
                        int[] t = new int[64];
                        for (int i = 0; i < 64; i++)
                        {
                            t[ZigZag[i]] = pq == 0 ? d[seg] : (d[seg] << 8) | d[seg + 1];
                            seg += pq == 0 ? 1 : 2;
                        }
                        quant[tq] = t;
                    }
                    break;
                case 0xC4:
                    while (seg < end)
                    {
                        int tc = d[seg] >> 4;
                        int th = d[seg] & 15;
                        seg++;
                        Huffman h = new Huffman();
                        int[] counts = new int[17];
                        int total = 0;
                        for (int i = 1; i <= 16; i++)
                        {
                            counts[i] = d[seg + i - 1];
                            total += counts[i];
                        }
                        seg += 16;
                        h.Values = d.AsSpan(seg, total).ToArray();
                        seg += total;
                        int code = 0;
                        int k = 0;
                        for (int l = 1; l <= 16; l++)
                        {
                            h.ValPtr[l] = k;
                            h.MinCode[l] = code;
                            code += counts[l];
                            k += counts[l];
                            h.MaxCode[l] = counts[l] > 0 ? code - 1 : -1;
                            code <<= 1;
                        }
                        h.MaxCode[17] = int.MaxValue;
                        if (tc == 0)
                            dcTables[th] = h;
                        else
                            acTables[th] = h;
                    }
                    break;
                case 0xC0:
                case 0xC1:
                    height = (d[seg + 1] << 8) | d[seg + 2];
                    width = (d[seg + 3] << 8) | d[seg + 4];
                    int nc = d[seg + 5];
                    for (int i = 0; i < nc; i++)
                    {
                        int o = seg + 6 + i * 3;
                        comps.Add(new Component { Id = d[o], H = d[o + 1] >> 4, V = d[o + 1] & 15, Q = d[o + 2] });
                    }
                    break;
                case 0xC2:
                case 0xC3:
                    return null;   // progressif, sans perte : non pris en charge
                case 0xDD:
                    restart = (d[seg] << 8) | d[seg + 1];
                    break;
                case 0xDA:
                {
                    int ns = d[seg];
                    List<Component> scan = new List<Component>();
                    for (int i = 0; i < ns; i++)
                    {
                        Component c = comps.First(x => x.Id == d[seg + 1 + i * 2]);
                        c.Dc = d[seg + 2 + i * 2] >> 4;
                        c.Ac = d[seg + 2 + i * 2] & 15;
                        scan.Add(c);
                    }
                    p = DecodeScan(d, end, width, height, comps, scan, quant, dcTables, acTables, restart);
                    continue;
                }
            }
            p = end;
        }
        if (width == 0 || height == 0 || comps.Count == 0 || comps[0].Pixels.Length == 0)
            return null;
        return ToRgba(width, height, comps);
    }

    /// <summary>Décode les blocs d'un scan (entrelacé) ; rend la position après ses données.</summary>
    private static int DecodeScan(byte[] d, int start, int width, int height, List<Component> comps, List<Component> scan,
        int[][] quant, Huffman?[] dcTables, Huffman?[] acTables, int restart)
    {
        int hmax = comps.Max(c => c.H);
        int vmax = comps.Max(c => c.V);
        int mcux = (width + 8 * hmax - 1) / (8 * hmax);
        int mcuy = (height + 8 * vmax - 1) / (8 * vmax);
        foreach (Component c in comps)
        {
            c.Stride = mcux * c.H * 8;
            if (c.Pixels.Length == 0)
                c.Pixels = new byte[c.Stride * mcuy * c.V * 8];
            c.Pred = 0;
        }
        Bits bits = new Bits(d, start);
        int[] block = new int[64];
        int count = 0;
        for (int my = 0; my < mcuy; my++)
            for (int mx = 0; mx < mcux; mx++)
            {
                if (restart > 0 && count > 0 && count % restart == 0)
                {
                    bits.Restart();
                    foreach (Component c in scan)
                        c.Pred = 0;
                }
                count++;
                foreach (Component c in scan)
                {
                    Huffman dc = dcTables[c.Dc] ?? throw new InvalidDataException("table DC");
                    Huffman ac = acTables[c.Ac] ?? throw new InvalidDataException("table AC");
                    int[] q = quant[c.Q];
                    int n = scan.Count == 1 ? 1 : c.H * c.V;
                    for (int b = 0; b < n; b++)
                    {
                        Array.Clear(block);
                        int t = DecodeValue(bits, dc);
                        int diff = t == 0 ? 0 : Extend(bits.Receive(t), t);
                        c.Pred += diff;
                        block[0] = c.Pred * q[0];
                        for (int k = 1; k < 64;)
                        {
                            int rs = DecodeValue(bits, ac);
                            int r = rs >> 4;
                            int s = rs & 15;
                            if (s == 0)
                            {
                                if (r != 15)
                                    break;
                                k += 16;
                                continue;
                            }
                            k += r;
                            if (k > 63)
                                break;
                            block[ZigZag[k]] = Extend(bits.Receive(s), s) * q[ZigZag[k]];
                            k++;
                        }
                        int bx = scan.Count == 1 ? mx : mx * c.H + b % c.H;
                        int by = scan.Count == 1 ? my : my * c.V + b / c.H;
                        Idct(block, c.Pixels, c.Stride, bx * 8, by * 8);
                    }
                }
            }
        // fin des données : le marqueur suivant
        int p = bits.Pos;
        while (p + 1 < d.Length && !(d[p] == 0xFF && d[p + 1] != 0 && (d[p + 1] < 0xD0 || d[p + 1] > 0xD7)))
            p++;
        return p;
    }

    private static int DecodeValue(Bits bits, Huffman h)
    {
        int code = bits.Bit();
        int l = 1;
        while (l <= 16 && code > h.MaxCode[l])
        {
            code = (code << 1) | bits.Bit();
            l++;
        }
        if (l > 16)
            return 0;
        return h.Values[h.ValPtr[l] + code - h.MinCode[l]];
    }

    private static int Extend(int v, int t) => v < (1 << (t - 1)) ? v - (1 << t) + 1 : v;

    /// <summary>Transformée inverse 8×8 (directe, en flottants) écrite dans le plan de la composante.</summary>
    private static void Idct(int[] block, byte[] dst, int stride, int x0, int y0)
    {
        Span<float> tmp = stackalloc float[64];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                float sum = 0;
                for (int u = 0; u < 8; u++)
                {
                    float cu = u == 0 ? 0.70710678f : 1f;
                    sum += cu * block[y * 8 + u] * Cos[x * 8 + u];
                }
                tmp[y * 8 + x] = sum / 2;
            }
        for (int x = 0; x < 8; x++)
            for (int y = 0; y < 8; y++)
            {
                float sum = 0;
                for (int v = 0; v < 8; v++)
                {
                    float cv = v == 0 ? 0.70710678f : 1f;
                    sum += cv * tmp[v * 8 + x] * Cos[y * 8 + v];
                }
                int val = (int)MathF.Round(sum / 2 + 128);
                dst[(y0 + y) * stride + x0 + x] = (byte)Math.Clamp(val, 0, 255);
            }
    }

    /// <summary>cos((2x + 1) u π / 16), rangé [x * 8 + u].</summary>
    private static readonly float[] Cos = Enumerable.Range(0, 64).Select(i => MathF.Cos((2 * (i / 8) + 1) * (i % 8) * MathF.PI / 16)).ToArray();

    /// <summary>Plans des composantes (rééchantillonnés) → RGBA ; une composante : gris, trois : YCbCr.</summary>
    private static (int w, int h, byte[] rgba) ToRgba(int width, int height, List<Component> comps)
    {
        int hmax = comps.Max(c => c.H);
        int vmax = comps.Max(c => c.V);
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float[] v = new float[comps.Count];
                for (int i = 0; i < comps.Count; i++)
                {
                    Component c = comps[i];
                    v[i] = c.Pixels[(y * c.V / vmax) * c.Stride + x * c.H / hmax];
                }
                int o = (y * width + x) * 4;
                if (comps.Count >= 3)
                {
                    float yy = v[0];
                    float cb = v[1] - 128;
                    float cr = v[2] - 128;
                    rgba[o] = (byte)Math.Clamp((int)MathF.Round(yy + 1.402f * cr), 0, 255);
                    rgba[o + 1] = (byte)Math.Clamp((int)MathF.Round(yy - 0.344136f * cb - 0.714136f * cr), 0, 255);
                    rgba[o + 2] = (byte)Math.Clamp((int)MathF.Round(yy + 1.772f * cb), 0, 255);
                }
                else
                {
                    byte g = (byte)v[0];
                    rgba[o] = g;
                    rgba[o + 1] = g;
                    rgba[o + 2] = g;
                }
                rgba[o + 3] = 255;
            }
        return (width, height, rgba);
    }
}
