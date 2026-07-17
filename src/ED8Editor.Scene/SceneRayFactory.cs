using System.Numerics;

namespace ED8Editor.Scene;

public static class SceneRayFactory
{
    public static SceneRay FromViewport(
        float pixelX,
        float pixelY,
        float viewportWidth,
        float viewportHeight,
        Matrix4x4 view,
        Matrix4x4 projection)
    {
        if (!float.IsFinite(pixelX)) throw new ArgumentOutOfRangeException(nameof(pixelX));
        if (!float.IsFinite(pixelY)) throw new ArgumentOutOfRangeException(nameof(pixelY));
        if (!float.IsFinite(viewportWidth) || viewportWidth <= 0) throw new ArgumentOutOfRangeException(nameof(viewportWidth));
        if (!float.IsFinite(viewportHeight) || viewportHeight <= 0) throw new ArgumentOutOfRangeException(nameof(viewportHeight));
        if (!Matrix4x4.Invert(view * projection, out var inverse))
        {
            throw new ArgumentException("View-projection matrix is not invertible.");
        }
        var x = pixelX * 2f / viewportWidth - 1f;
        var y = 1f - pixelY * 2f / viewportHeight;
        var near = Unproject(new Vector4(x, y, 0f, 1f), inverse);
        var far = Unproject(new Vector4(x, y, 1f, 1f), inverse);
        return new SceneRay(near, far - near);
    }

    private static Vector3 Unproject(Vector4 point, Matrix4x4 inverseViewProjection)
    {
        var transformed = Vector4.Transform(point, inverseViewProjection);
        if (transformed.W == 0f) throw new InvalidOperationException("Cannot unproject a point with W=0.");
        return new Vector3(transformed.X, transformed.Y, transformed.Z) / transformed.W;
    }
}
