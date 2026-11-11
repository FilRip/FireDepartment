namespace OpenFD.ConvFD1;

/// <summary>Matériau d'un maillage FD1 : nom, texture de couleur (peut être vide) et couleur diffuse (ARGB).</summary>
public sealed class Fd1Material
{
    public string Name = "";
    public string Texture = "";
    public uint Diffuse = 0xFFFFFFFF;
}
