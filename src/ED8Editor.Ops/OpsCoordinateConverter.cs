using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Ops;

internal static class OpsCoordinateConverter
{
    public static Vector3 ToEditorPosition(Vector3 sourcePosition)
        => sourcePosition;

    public static MapTransform ToEditorTransform(
        Vector3 sourcePosition,
        Vector3 sourceEulerRadians,
        Vector3 scale)
    {
        var editorPosition = ToEditorPosition(sourcePosition);
        return new MapTransform(
            editorPosition,
            FromEditorEuler(sourceEulerRadians),
            scale,
            sourcePosition,
            sourceEulerRadians);
    }

    public static MapTransform ToEditorVolumeTransform(
        Vector3 sourcePosition,
        Vector3 sourceEulerRadians,
        Vector3 scale)
    {
        return new MapTransform(
            ToEditorPosition(sourcePosition),
            FromEditorEuler(sourceEulerRadians),
            scale,
            sourcePosition,
            sourceEulerRadians);
    }

    public static (Vector3 Position, Vector3 EulerRadians, Vector3 Scale) ToSourceTransform(
        MapTransform editorTransform)
    {
        ArgumentNullException.ThrowIfNull(editorTransform);
        var rotation = Quaternion.Normalize(editorTransform.Rotation);

        // A rotation the author did not touch is written back exactly as the file
        // stated it. Going out through a quaternion and back gives an equivalent
        // rotation but not the same numbers — "0, -3.141191, 0" comes back as
        // "3.1415927, -0.0004, 3.1415927", the same turn expressed through gimbal
        // lock — and rewriting a field nobody edited is how a file drifts.
        if (SameRotation(FromEditorEuler(editorTransform.SourceEulerRadians), rotation))
        {
            return (
                editorTransform.Position,
                editorTransform.SourceEulerRadians,
                editorTransform.Scale);
        }

        return (
            editorTransform.Position,
            ToEditorEuler(rotation),
            editorTransform.Scale);
    }

    /// <summary>
    /// Whether two quaternions turn a thing the same way. They may differ in sign
    /// and still agree, so the comparison is on the angle between them.
    /// </summary>
    private static bool SameRotation(Quaternion left, Quaternion right)
        => Math.Abs(Quaternion.Dot(Quaternion.Normalize(left), Quaternion.Normalize(right)))
            >= 1f - 1e-6f;

    private static Quaternion FromEditorEuler(Vector3 editorEuler)
    {
        // The community parser applies intrinsic X/Y/Z Euler rotations and stores
        // its quaternion as W/X/Y/Z. System.Numerics uses X/Y/Z/W.
        var halfX = editorEuler.X * 0.5f;
        var halfY = editorEuler.Y * 0.5f;
        var halfZ = editorEuler.Z * 0.5f;
        var cr = MathF.Cos(halfX);
        var sr = MathF.Sin(halfX);
        var cp = MathF.Cos(halfY);
        var sp = MathF.Sin(halfY);
        var cy = MathF.Cos(halfZ);
        var sy = MathF.Sin(halfZ);

        return Quaternion.Normalize(new Quaternion(
            (sr * cp * cy) - (cr * sp * sy),
            (cr * sp * cy) + (sr * cp * sy),
            (cr * cp * sy) - (sr * sp * cy),
            (cr * cp * cy) + (sr * sp * sy)));

    }

    private static Vector3 ToEditorEuler(Quaternion rotation)
    {
        var x = rotation.X;
        var y = rotation.Y;
        var z = rotation.Z;
        var w = rotation.W;
        var roll = MathF.Atan2(2f * (w * x + y * z), 1f - 2f * (x * x + y * y));
        var pitchTerm = Math.Clamp(2f * (w * y - z * x), -1f, 1f);
        var pitch = MathF.Asin(pitchTerm);
        var yaw = MathF.Atan2(2f * (w * z + x * y), 1f - 2f * (y * y + z * z));
        return new Vector3(roll, pitch, yaw);
    }
}
