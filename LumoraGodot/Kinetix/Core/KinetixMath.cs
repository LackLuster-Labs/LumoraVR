using Godot;

namespace Aquamarine.Kinetix.Core;

public static class KinetixMath
{
    // Law of cosines: returns angle opposite to side c
    public static float LawOfCosines(float a, float b, float c)
    {
        float cosAngle = (a * a + b * b - c * c) / (2 * a * b);
        cosAngle = Mathf.Clamp(cosAngle, -1f, 1f);
        return Mathf.Acos(cosAngle);
    }

    public static Quaternion FromToRotation(Vector3 from, Vector3 to)
    {
        from = from.Normalized();
        to = to.Normalized();
        float dot = from.Dot(to);

        if (dot > 0.999999f)
            return Quaternion.Identity;

        if (dot < -0.999999f)
        {
            Vector3 axis = Vector3.Up.Cross(from);
            if (axis.LengthSquared() < 0.000001f)
                axis = Vector3.Right.Cross(from);
            return new Quaternion(axis.Normalized(), Mathf.Pi);
        }

        Vector3 cross = from.Cross(to);
        float angle = Mathf.Acos(dot);
        return new Quaternion(cross.Normalized(), angle);
    }

    public static Vector3 DampedLerp(Vector3 current, Vector3 target, float lambda, float dt)
    {
        return current.Lerp(target, 1 - Mathf.Exp(-lambda * dt));
    }

    public static Quaternion DampedSlerp(Quaternion current, Quaternion target, float lambda, float dt)
    {
        return current.Slerp(target, 1 - Mathf.Exp(-lambda * dt));
    }

    // Exponential damping for floats
    public static float Damp(this float current, float target, float lambda, float dt)
    {
        return Mathf.Lerp(current, target, 1 - Mathf.Exp(-lambda * dt));
    }

    public static float ClampAngle(float angle, float min, float max)
    {
        while (angle > Mathf.Pi) angle -= Mathf.Tau;
        while (angle < -Mathf.Pi) angle += Mathf.Tau;
        return Mathf.Clamp(angle, min, max);
    }

    public static Vector3 ProjectOnPlane(Vector3 vector, Vector3 planeNormal)
    {
        return vector - planeNormal * vector.Dot(planeNormal);
    }

    public static float SignedAngle(Vector3 from, Vector3 to, Vector3 axis)
    {
        float unsignedAngle = from.AngleTo(to);
        Vector3 cross = from.Cross(to);
        float sign = Mathf.Sign(cross.Dot(axis));
        return unsignedAngle * sign;
    }
}
