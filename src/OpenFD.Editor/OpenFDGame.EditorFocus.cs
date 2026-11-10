using OpenFD.Game;
using SN = System.Numerics;

namespace OpenFD.MG;

// éditeur : caméra centrée sur un objet (clic droit dans la liste de gauche) sans obstacle entre elle et l'objet :
// on tourne autour de lui, puis on se rapproche, jusqu'à le voir ; on s'arrête avant de toucher l'objet
public sealed partial class OpenFDGame
{
    /// <summary>Triangles du décor (en coordonnées du monde), par morceau, avec leur boîte : faits au premier besoin.</summary>
    private sealed class EdOccluder
    {
        public required float[] Tris;   // 9 flottants par triangle
        public SN.Vector3 Min, Max;
    }

    private List<EdOccluder>? _edOccluders;
    private string _edOccluderMission = "";

    private List<EdOccluder> EdSceneryOccluders()
    {
        if (_edOccluders != null && _edOccluderMission == _mission)
            return _edOccluders;
        List<EdOccluder> list = new List<EdOccluder>();
        foreach (SceneAssets.Node node in _assets.Scenery(_mission))
        {
            try
            {
                if (EdOccluderOf(_assets.LoadMesh(_mission, node.Mesh, null).Mesh, node.World) is { } o)
                    list.Add(o);
            }
            catch (Exception)
            {
            }
        }
        _edOccluders = list;
        _edOccluderMission = _mission;
        return list;
    }

    private static EdOccluder? EdOccluderOf(Formats.Mesh m, SN.Matrix4x4 world)
    {
        if (m.Indices.Length < 3)
            return null;
        float[] t = new float[m.Indices.Length / 3 * 9];
        SN.Vector3 mn = new SN.Vector3(float.MaxValue), mx = new SN.Vector3(float.MinValue);
        for (int k = 0; k < m.Indices.Length / 3 * 3; k++)
        {
            int i = (int)m.Indices[k];
            SN.Vector3 p = SN.Vector3.Transform(new SN.Vector3(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]), world);
            t[3 * k] = p.X;
            t[3 * k + 1] = p.Y;
            t[3 * k + 2] = p.Z;
            mn = SN.Vector3.Min(mn, p);
            mx = SN.Vector3.Max(mx, p);
        }
        return new EdOccluder { Tris = t, Min = mn, Max = mx };
    }

    /// <summary>Obstacles du moment : le décor, et les objets posés (sauf celui que l'on regarde).</summary>
    private List<EdOccluder> EdOccluders(EditorObject except)
    {
        List<EdOccluder> list = new List<EdOccluder>(EdSceneryOccluders());
        foreach (EditorObject o in _edObjects)
        {
            if (o == except || o.Model == null)
                continue;
            try
            {
                foreach (SceneAssets.Node part in _assets.ModelParts(o.Model))
                    if (EdOccluderOf(_assets.LoadMesh(_mission, part.Mesh, "gfx").Mesh, part.World * o.Shown) is { } oc)
                        list.Add(oc);
            }
            catch (Exception)
            {
            }
        }
        return list;
    }

    /// <summary>
    /// Vue dégagée sur l'objet : on tourne la caméra autour de lui (par pas de 15°, de part et d'autre de l'angle actuel) ;
    /// si aucun angle ne le montre, on se rapproche d'un quart et on recommence ; on s'arrête avant de toucher l'objet.
    /// </summary>
    private void EdClearView(EditorObject e)
    {
        List<EdOccluder> occ = EdOccluders(e);
        SN.Vector3 target = e.Center;
        float minDist = MathF.Max(e.Radius * 1.3f, 150);
        // toits cachés (T) : ce qui est plus haut que l'étage de l'objet ne gêne pas
        float? ceiling = _roofs ? null : e.Position.Z + 280;
        float yaw0 = _yaw;
        for (float d = _dist; d >= minDist; d *= 0.75f)
            for (int k = 0; k < 24; k++)
            {
                int step = (k + 1) / 2 * (k % 2 == 1 ? 1 : -1);
                float yaw = yaw0 + step * MathF.PI / 12;
                SN.Vector3 eye = target + d * new SN.Vector3(MathF.Cos(_pitch) * MathF.Cos(yaw), MathF.Cos(_pitch) * MathF.Sin(yaw), MathF.Sin(_pitch));
                if (!EdBlocked(occ, eye, target, e.Radius * 0.8f, ceiling))
                {
                    _yaw = yaw;
                    _dist = d;
                    return;
                }
            }
        // toujours caché : la caméra au plus près, sans toucher l'objet
        _yaw = yaw0;
        _dist = minDist;
        EdMessage(Tr("FocusHidden", "{0} : aucun angle ne le montre sans obstacle, caméra au plus près", e.Name));
    }

    /// <summary>Un triangle coupe-t-il le segment œil → cible (en dehors de la sphère de l'objet, de rayon <paramref name="keep"/>) ?</summary>
    private static bool EdBlocked(List<EdOccluder> occ, SN.Vector3 eye, SN.Vector3 target, float keep, float? ceiling)
    {
        SN.Vector3 dir = target - eye;
        float len = dir.Length();
        if (len < 1)
            return false;
        // le segment s'arrête au bord de la sphère de l'objet
        float tMax = MathF.Max(0, (len - keep) / len);
        SN.Vector3 lo = SN.Vector3.Min(eye, eye + dir * tMax), hi = SN.Vector3.Max(eye, eye + dir * tMax);
        foreach (EdOccluder o in occ)
        {
            if (o.Max.X < lo.X || o.Min.X > hi.X || o.Max.Y < lo.Y || o.Min.Y > hi.Y || o.Max.Z < lo.Z || o.Min.Z > hi.Z)
                continue;
            if (!SegmentBox(eye, dir, tMax, o.Min, o.Max))
                continue;
            float[] t = o.Tris;
            for (int i = 0; i + 8 < t.Length; i += 9)
            {
                SN.Vector3 a = new SN.Vector3(t[i], t[i + 1], t[i + 2]), b = new SN.Vector3(t[i + 3], t[i + 4], t[i + 5]), c = new SN.Vector3(t[i + 6], t[i + 7], t[i + 8]);
                if (ceiling is { } cz && MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) > cz)
                    continue;
                if (SegmentTriangle(eye, dir, tMax, a, b, c))
                    return true;
            }
        }
        return false;
    }

    private static bool SegmentBox(SN.Vector3 o, SN.Vector3 d, float tMax, SN.Vector3 min, SN.Vector3 max)
    {
        float t0 = 0, t1 = tMax;
        for (int k = 0; k < 3; k++)
        {
            float ok = k == 0 ? o.X : k == 1 ? o.Y : o.Z, dk = k == 0 ? d.X : k == 1 ? d.Y : d.Z;
            float mn = k == 0 ? min.X : k == 1 ? min.Y : min.Z, mx = k == 0 ? max.X : k == 1 ? max.Y : max.Z;
            if (MathF.Abs(dk) < 1e-6f)
            {
                if (ok < mn || ok > mx)
                    return false;
                continue;
            }
            float ta = (mn - ok) / dk, tb = (mx - ok) / dk;
            if (ta > tb)
                (ta, tb) = (tb, ta);
            t0 = MathF.Max(t0, ta);
            t1 = MathF.Min(t1, tb);
            if (t0 > t1)
                return false;
        }
        return true;
    }

    /// <summary>Möller-Trumbore : le segment o + t·d (0 &lt; t &lt; tMax) traverse-t-il le triangle ?</summary>
    private static bool SegmentTriangle(SN.Vector3 o, SN.Vector3 d, float tMax, SN.Vector3 a, SN.Vector3 b, SN.Vector3 c)
    {
        SN.Vector3 e1 = b - a, e2 = c - a;
        SN.Vector3 p = SN.Vector3.Cross(d, e2);
        float det = SN.Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-8f)
            return false;
        float inv = 1 / det;
        SN.Vector3 s = o - a;
        float u = SN.Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1)
            return false;
        SN.Vector3 q = SN.Vector3.Cross(s, e1);
        float v = SN.Vector3.Dot(d, q) * inv;
        if (v < 0 || u + v > 1)
            return false;
        float t = SN.Vector3.Dot(e2, q) * inv;
        return t > 1e-4f && t < tMax;
    }
}
