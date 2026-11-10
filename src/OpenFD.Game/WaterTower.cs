using System.Numerics;
using OpenFD.Fire;
using OpenFD.Formats;

namespace OpenFD.Game;

/// <summary>
/// Camion d'attaque ([UNIT] Type=WATERTOWER, Port) : sa lance est dans la nacelle, au bout de l'échelle. Quand il arrose,
/// la tourelle se tourne vers le feu, l'échelle se lève puis s'allonge, la nacelle à DistFromNacelToFire du feu et
/// HeightFromNacelToFire au-dessus ; sans feu, ou pour rouler, l'échelle se raccourcit puis se couche (il roule ensuite).
/// </summary>
public sealed partial class UnitManager
{
    private static void SetupWaterTower(Unit u, ClassFile c)
    {
        u.TurretYawSpeed = c.GetFloat("UNIT", "Turret_Z_Rot_Speed", 40) * MathF.PI / 180;
        u.TurretPitchSpeed = c.GetFloat("UNIT", "Turret_X_Rot_Speed", 40) * MathF.PI / 180;
        u.NacelToFire = c.GetFloat("UNIT", "DistFromNacelToFire", 1500);
        u.NacelAboveFire = c.GetFloat("UNIT", "HeightFromNacelToFire", 1200);
    }

    /// <summary>Bout de l'échelle du camion d'attaque (la nacelle, d'où part le jet), dans le monde.</summary>
    public static Vector3 BoomTip(Unit u)
    {
        float len = u.LadderRest + u.BoomExtend;
        Vector3 local = u.LadderHinge - u.LadderPivot + new Vector3(0, -len * MathF.Cos(u.BoomPitch), len * MathF.Sin(u.BoomPitch));
        return VehicleToWorld(u, u.LadderPivot + Vector3.Transform(local, Matrix4x4.CreateRotationZ(u.BoomYaw)));
    }

    private void UpdateWaterTowers(float dt)
    {
        foreach (Unit t in Units.Where(v => v.IsWaterTower))
        {
            bool aim = t.Ladder == null && t.Target != null && !t.Moving && t.ManeuverLeft <= 0 && !t.Destroyed && !t.OnBoard;
            t.BoomIdle = aim ? 0 : t.BoomIdle + dt;
            // entre deux feux (le suivant est trouvé en un instant) : l'échelle reste en place ; elle se replie après
            // BoomIdleSeconds sans feu, ou tout de suite pour rouler
            if (!aim && !t.Moving && t.BoomIdle < BoomIdleSeconds && !t.Destroyed && !t.OnBoard && t.Ladder == null)
                continue;
            float yaw = 0, pitch = 0, extend = 0;
            if (aim)
                (yaw, pitch, extend) = BoomAim(t, t.Target!);
            float turn = t.TurretYawSpeed * dt, lift = t.TurretPitchSpeed * dt, slide = t.LadderSpeed * dt;
            float dyaw = MathF.IEEERemainder(yaw - t.BoomYaw, 2 * MathF.PI);
            bool placed = MathF.Abs(dyaw) < 0.02f && MathF.Abs(pitch - t.BoomPitch) < 0.02f;
            // repli : l'allongement d'abord ; mise en place : tourelle et inclinaison, puis l'allongement
            if (t.BoomExtend > extend && !(aim && placed))
            {
                t.BoomExtend = MathF.Max(extend, t.BoomExtend - slide);
                continue;
            }
            t.BoomYaw += Math.Clamp(dyaw, -turn, turn);
            t.BoomPitch += Math.Clamp(pitch - t.BoomPitch, -lift, lift);
            if (placed && t.BoomExtend < extend)
                t.BoomExtend = MathF.Min(extend, t.BoomExtend + slide);
            else if (placed)
                t.BoomExtend = MathF.Max(extend, t.BoomExtend - slide);
        }
    }

    /// <summary>Sans feu à arroser pendant ce temps (s), le camion d'attaque replie son échelle.</summary>
    public const float BoomIdleSeconds = 3f;

    // tourelle vers le feu ; nacelle à DistFromNacelToFire du feu (à l'horizontale) et HeightFromNacelToFire au-dessus,
    // dans la longueur de l'échelle
    private static (float yaw, float pitch, float extend) BoomAim(Unit t, HotPoint h)
    {
        Vector3 pivot = VehicleToWorld(t, t.LadderPivot);
        float yaw = MathF.IEEERemainder(MathF.Atan2(h.Position.Y - pivot.Y, h.Position.X - pivot.X) - t.Heading, 2 * MathF.PI);
        Vector3 hinge = VehicleToWorld(t, LadderHingeAt(t, yaw));
        float reach = MathF.Max(0, Vector2.Distance(new(hinge.X, hinge.Y), new(h.Position.X, h.Position.Y)) - t.NacelToFire);
        float rise = MathF.Max(0, h.Position.Z + t.NacelAboveFire - hinge.Z);
        // feu trop loin pour l'échelle : la nacelle garde sa hauteur au-dessus du feu et s'avance autant qu'elle peut
        float most = t.LadderRest + t.LadderMax;
        if (reach * reach + rise * rise > most * most)
            reach = MathF.Sqrt(MathF.Max(0, most * most - MathF.Min(rise, most) * MathF.Min(rise, most)));
        float pitch = Math.Clamp(MathF.Atan2(rise, reach), 0.15f, 1.3f);
        float len = MathF.Sqrt(reach * reach + rise * rise);
        float extend = Math.Clamp(len - t.LadderRest, 0, t.LadderMax);
        return (yaw, pitch, extend);
    }
}
