using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Ops;

internal static class OpsCoordinateConverter
{
    public static MapTransform ToEditorTransform(
        Vector3 sourcePosition,
        Vector3 sourceEulerRadians,
        Vector3 scale)
    {
        var editorPosition = new Vector3(-sourcePosition.X, sourcePosition.Y, sourcePosition.Z);
        var editorEuler = new Vector3(
            sourceEulerRadians.X - (MathF.PI / 2f),
            -sourceEulerRadians.Y,
            sourceEulerRadians.Z);

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

        var rotation = Quaternion.Normalize(new Quaternion(
            (sr * cp * cy) - (cr * sp * sy),
            (cr * sp * cy) + (sr * cp * sy),
            (cr * cp * sy) - (sr * sp * cy),
            (cr * cp * cy) + (sr * sp * sy)));

        return new MapTransform(
            editorPosition,
            rotation,
            scale,
            sourcePosition,
            sourceEulerRadians);
    }
}
