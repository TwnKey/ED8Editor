using System.Numerics;

namespace ED8Editor.Scene;

public sealed class EditorOrbitCamera
{
    private static readonly float PitchLimit = MathF.PI * 0.5f - 0.01f;

    public Vector3 Position { get; private set; }
    public Vector3 Target { get; private set; }
    public float Distance { get; private set; }
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }

    public Vector3 Forward
    {
        get
        {
            var cosPitch = MathF.Cos(Pitch);
            return Vector3.Normalize(new Vector3(
                MathF.Sin(Yaw) * cosPitch,
                MathF.Sin(Pitch),
                MathF.Cos(Yaw) * cosPitch));
        }
    }

    private Vector3 GroundRight
        => Vector3.Normalize(new Vector3(MathF.Cos(Yaw), 0f, -MathF.Sin(Yaw)));

    public Vector3 ScreenRight => -GroundRight;

    public Vector3 WorldUp => Vector3.UnitY;

    public Vector3 ScreenUp
        => Vector3.Normalize(Vector3.Cross(Forward, GroundRight));

    public void Initialize(Vector3 target, Vector3 position)
    {
        ValidateFinite(target, nameof(target));
        ValidateFinite(position, nameof(position));
        var direction = target - position;
        if (direction == Vector3.Zero) throw new ArgumentException("Camera position must differ from its target.", nameof(position));
        Target = target;
        Position = position;
        Distance = direction.Length();
        SetDirection(direction / Distance);
    }

    public void Orbit(float deltaX, float deltaY, float radiansPerPixel = 0.004f)
    {
        if (!float.IsFinite(deltaX) || !float.IsFinite(deltaY)) throw new ArgumentOutOfRangeException(nameof(deltaX));
        if (!float.IsFinite(radiansPerPixel) || radiansPerPixel <= 0) throw new ArgumentOutOfRangeException(nameof(radiansPerPixel));
        Yaw = WrapAngle(Yaw + deltaX * radiansPerPixel);
        Pitch = Math.Clamp(Pitch - deltaY * radiansPerPixel, -PitchLimit, PitchLimit);
        Position = Target - Forward * Distance;
    }

    public void Look(float deltaX, float deltaY, float radiansPerPixel = 0.004f)
    {
        if (!float.IsFinite(deltaX) || !float.IsFinite(deltaY)) throw new ArgumentOutOfRangeException(nameof(deltaX));
        if (!float.IsFinite(radiansPerPixel) || radiansPerPixel <= 0) throw new ArgumentOutOfRangeException(nameof(radiansPerPixel));
        Yaw = WrapAngle(Yaw - deltaX * radiansPerPixel);
        Pitch = Math.Clamp(Pitch - deltaY * radiansPerPixel, -PitchLimit, PitchLimit);
        Target = Position + Forward * Distance;
    }

    public void Dolly(float distance)
    {
        if (!float.IsFinite(distance)) throw new ArgumentOutOfRangeException(nameof(distance));
        Translate(Forward * distance);
    }

    public void Pan(float deltaX, float deltaY, float viewportHeight, float verticalFieldOfView)
    {
        if (!float.IsFinite(viewportHeight) || viewportHeight <= 0) throw new ArgumentOutOfRangeException(nameof(viewportHeight));
        if (!float.IsFinite(verticalFieldOfView) || verticalFieldOfView <= 0 || verticalFieldOfView >= MathF.PI)
        {
            throw new ArgumentOutOfRangeException(nameof(verticalFieldOfView));
        }
        var worldUnitsPerPixel = 2f * Distance * MathF.Tan(verticalFieldOfView * 0.5f) / viewportHeight;
        Translate((ScreenRight * deltaX + ScreenUp * deltaY) * worldUnitsPerPixel);
    }

    public void Zoom(float wheelSteps, float minimumDistance, float maximumDistance, float rate = 0.18f)
    {
        if (!float.IsFinite(wheelSteps)) throw new ArgumentOutOfRangeException(nameof(wheelSteps));
        if (!float.IsFinite(minimumDistance) || minimumDistance <= 0) throw new ArgumentOutOfRangeException(nameof(minimumDistance));
        if (!float.IsFinite(maximumDistance) || maximumDistance < minimumDistance) throw new ArgumentOutOfRangeException(nameof(maximumDistance));
        if (!float.IsFinite(rate) || rate <= 0) throw new ArgumentOutOfRangeException(nameof(rate));
        Distance = Math.Clamp(Distance * MathF.Exp(-wheelSteps * rate), minimumDistance, maximumDistance);
        Position = Target - Forward * Distance;
    }

    public void Focus(Vector3 center, float distance)
    {
        ValidateFinite(center, nameof(center));
        if (!float.IsFinite(distance) || distance <= 0) throw new ArgumentOutOfRangeException(nameof(distance));
        Target = center;
        Distance = distance;
        Position = Target - Forward * Distance;
    }

    public void Translate(Vector3 translation)
    {
        ValidateFinite(translation, nameof(translation));
        Position += translation;
        Target += translation;
    }

    private void SetDirection(Vector3 direction)
    {
        Pitch = Math.Clamp(MathF.Asin(Math.Clamp(direction.Y, -1f, 1f)), -PitchLimit, PitchLimit);
        Yaw = WrapAngle(MathF.Atan2(direction.X, direction.Z));
    }

    private static float WrapAngle(float radians)
        => MathF.IEEERemainder(radians, MathF.Tau);

    private static void ValidateFinite(Vector3 value, string parameterName)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
