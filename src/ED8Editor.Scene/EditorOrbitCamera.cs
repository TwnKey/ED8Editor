using System.Numerics;

namespace ED8Editor.Scene;

public sealed class EditorOrbitCamera
{
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
        Yaw += deltaX * radiansPerPixel;
        Pitch = Math.Clamp(Pitch - deltaY * radiansPerPixel, -1.5f, 1.5f);
        Position = Target - Forward * Distance;
    }

    public void Pan(float deltaX, float deltaY, float viewportHeight, float verticalFieldOfView)
    {
        if (!float.IsFinite(viewportHeight) || viewportHeight <= 0) throw new ArgumentOutOfRangeException(nameof(viewportHeight));
        if (!float.IsFinite(verticalFieldOfView) || verticalFieldOfView <= 0 || verticalFieldOfView >= MathF.PI)
        {
            throw new ArgumentOutOfRangeException(nameof(verticalFieldOfView));
        }
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, Forward));
        var up = Vector3.Normalize(Vector3.Cross(Forward, right));
        var worldUnitsPerPixel = 2f * Distance * MathF.Tan(verticalFieldOfView * 0.5f) / viewportHeight;
        Translate((-right * deltaX + up * deltaY) * worldUnitsPerPixel);
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
        Pitch = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));
        Yaw = MathF.Atan2(direction.X, direction.Z);
    }

    private static void ValidateFinite(Vector3 value, string parameterName)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
