namespace OpenFD.ConvFD1;

/// <summary>Maillage à écrire au format du moteur (.mesh + .meshdata) : positions en cm, normales, une couche de coordonnées de texture, triangles par matériau.</summary>
public sealed class Fd3Mesh
{
    public string Name = "";
    public List<float> Positions = new();
    public List<float> Normals = new();
    public List<float> Uv = new();
    public List<ushort> Indices = new();
    public List<(string material, int start, int count)> Groups = new();

    public int VertexCount => Positions.Count / 3;
}
