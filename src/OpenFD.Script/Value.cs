using System.Globalization;

namespace OpenFD.Script;

/// <summary>Valeur du langage : entier (s32, EntityID, mcBool), flottant (f32) ou texte (mcString).</summary>
public readonly struct Value
{
    public readonly VKind Kind;
    public readonly double N;
    public readonly string? S;
    private Value(VKind k, double n, string? s)
    {
        Kind = k;
        N = n;
        S = s;
    }
    public static Value I(long v) => new(VKind.Int, v, null);
    public static Value F(double v) => new(VKind.Float, v, null);
    public static Value T(string v) => new(VKind.Str, 0, v);
    public static readonly Value Zero = I(0);
    public bool Truthy => Kind == VKind.Str ? !string.IsNullOrEmpty(S) : N != 0;
    public long AsInt => Kind == VKind.Str ? 0 : (long)Math.Truncate(N);
    public double AsNum => Kind == VKind.Str ? 0 : N;
    public string AsText => Kind switch
    {
        VKind.Str => S ?? "",
        VKind.Int => ((long)N).ToString(CultureInfo.InvariantCulture),
        _ => N.ToString("0.######", CultureInfo.InvariantCulture),
    };
    public override string ToString() => Kind == VKind.Str ? $"\"{S}\"" : AsText;

    public static Value Convert(string type, Value v) => type switch
    {
        "f32" or "float" => F(v.AsNum),
        "mcString" => T(v.AsText),
        "void" => v,
        _ => I(v.AsInt),
    };
}
