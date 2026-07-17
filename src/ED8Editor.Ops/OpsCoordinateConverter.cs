using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Ops;

internal static class OpsCoordinateConverter
{
    public static Vector3 ToEditorPosition(Vector3 sourcePosition)
        => new(-sourcePosition.X, sourcePosition.Y, sourcePosition.Z);

    public static MapTransform ToEditorTransform(
        Vector3 sourcePosition,
        Vector3 sourceEulerRadians,
        Vector3 scale)
    {
        var editorPosition = ToEditorPosition(sourcePosition);
        var editorEuler = new Vector3(
            sourceEulerRadians.X - (MathF.PI / 2f),
            -sourceEulerRadians.Y,
            sourceEulerRadians.Z);

        return new MapTransform(
            editorPosition,
            FromEditorEuler(editorEuler),
            scale,
            sourcePosition,
            sourceEulerRadians);
    }

    public static MapTransform ToEditorVolumeTransform(
        Vector3 sourcePosition,
        Vector3 sourceEulerRadians,
        Vector3 scale)
    {
        var editorEuler = new Vector3(
            sourceEulerRadians.X,
            -sourceEulerRadians.Y,
            sourceEulerRadians.Z);
        return new MapTransform(
            ToEditorPosition(sourcePosition),
            FromEditorEuler(editorEuler),
            scale,
            sourcePosition,
            sourceEulerRadians);
    }

    public static (Vector3 Position, Vector3 EulerRadians, Vector3 Scale) ToSourceTransform(
        MapTransform editorTransform,
        bool assetObject)
    {
        ArgumentNullException.ThrowIfNull(editorTransform);
        var editorEuler = ToEditorEuler(Quaternion.Normalize(editorTransform.Rotation));
        var sourceEuler = new Vector3(
            editorEuler.X + (assetObject ? MathF.PI / 2f : 0f),
            -editorEuler.Y,
            editorEuler.Z);
        return (
            new Vector3(-editorTransform.Position.X, editorTransform.Position.Y, editorTransform.Position.Z),
            sourceEuler,
            editorTransform.Scale);
    }

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
