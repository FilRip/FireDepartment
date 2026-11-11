namespace OpenFD.ConvFD1;

/// <summary>
/// Fichier .SKIN de FD1 (maillages rigides du décor et des objets).
///
/// En-tête : 5 octets (01 03 00 00 00) + 8 octets ; u32 nombre de maillages ; pour chacun :
///  chaîne nom ; u32 nv + nv × 3 f32 (positions) ; u32 ni + ni × u32 (triangles) ;
///  u8 + u32 n + n × 2 f32 (coordonnées de texture) ; u8 + u32 n + n × 3 f32 (normales) ; u8 + u32 n + n × u32 (couleurs) ;
///  u32 matériaux, chacun : 20 octets (couleurs ambiante, diffuse, brillance, spéculaire, émissive), u8,
///  chaîne texture, deux chaînes, chaîne nom ; u32 nombre de triangles + u16 matériau par triangle ; 16 octets (0 sur un objet rigide).
/// Les personnages (maillages déformés par des os) ont un autre bloc final : ils ne sont pas lus ici.
/// </summary>
public static class Fd1Skin
{
    public static List<Fd1SubMesh> Load(byte[] data)
    {
        Fd1Reader r = new Fd1Reader(data, 13);
        uint count = r.U32();
        List<Fd1SubMesh> list = new List<Fd1SubMesh>();
        for (uint m = 0; m < count; m++)
        {
            Fd1SubMesh s = new Fd1SubMesh { Name = r.Str() };
            int nv = r.I32();
            s.Positions = new float[3 * nv];
            for (int i = 0; i < 3 * nv; i++)
                s.Positions[i] = r.F32();
            int ni = r.I32();
            s.Indices = new uint[ni];
            for (int i = 0; i < ni; i++)
                s.Indices[i] = r.U32();
            r.U8();
            int nuv = r.I32();
            if (nuv > 0)
            {
                s.Uv = new float[2 * nuv];
                for (int i = 0; i < 2 * nuv; i++)
                    s.Uv[i] = r.F32();
            }
            r.U8();
            int nn = r.I32();
            if (nn > 0)
            {
                s.Normals = new float[3 * nn];
                for (int i = 0; i < 3 * nn; i++)
                    s.Normals[i] = r.F32();
            }
            r.U8();
            int nc = r.I32();
            r.Skip(4L * nc);
            uint nmat = r.U32();
            for (uint k = 0; k < nmat; k++)
            {
                r.U32();
                uint diffuse = r.U32();
                r.Skip(12);
                r.U8();
                string tex = r.Str();
                r.Str();
                r.Str();
                string name = r.Str();
                s.Materials.Add(new Fd1Material { Name = name, Texture = tex, Diffuse = diffuse });
            }
            int nf = r.I32();
            s.FaceMaterial = new ushort[nf];
            for (int i = 0; i < nf; i++)
                s.FaceMaterial[i] = r.U16();
            uint t0 = r.U32();
            uint t1 = r.U32();
            r.U32();
            r.U32();
            if (t0 != 0 || t1 != 0)
                throw new InvalidDataException("maillage déformé par des os (personnage) : non pris en charge");
            if (nuv != 0 && nuv != nv)
                s.Uv = null;
            if (nn != 0 && nn != nv)
                s.Normals = null;
            list.Add(s);
        }
        return list;
    }
}
