namespace OpenFD.ConvFD1;

/// <summary>
/// Un maillage d'un fichier .SKIN de FD1 (un nœud du squelette du même nom) : sommets en décimètres, triangles,
/// coordonnées de texture et normales (un par sommet), matériau de chaque triangle.
/// </summary>
public sealed class Fd1SubMesh
{
    public string Name = "";
    public float[] Positions = Array.Empty<float>();
    public uint[] Indices = Array.Empty<uint>();
    public float[]? Uv;
    public float[]? Normals;
    public List<Fd1Material> Materials = new();
    public ushort[] FaceMaterial = Array.Empty<ushort>();

    public int VertexCount => Positions.Length / 3;
}
