using System.Numerics;

namespace ED8Editor.Scene;

public sealed record SceneBoundsResult(
    bool HasGeometry,
    Vector3 Minimum,
    Vector3 Maximum,
    Vector3 Center,
    float Radius,
    IReadOnlyList<SceneGeometryIssue> Issues);

public sealed class SceneBoundsCalculator
{
    private readonly SceneGeometryDecoder geometryDecoder;

    public SceneBoundsCalculator(IEnumerable<IVertexPositionDecoder>? positionDecoders = null)
    {
        geometryDecoder = new SceneGeometryDecoder(positionDecoders);
    }

    public SceneBoundsResult Calculate(IEnumerable<SceneModelInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var hasGeometry = false;
        var issues = new List<SceneGeometryIssue>();
        foreach (var instance in instances)
        {
            for (var meshIndex = 0; meshIndex < instance.Model.Meshes.Count; meshIndex++)
            {
                var mesh = instance.Model.Meshes[meshIndex];
                var transform = mesh.LocalTransform * instance.Transform;
                for (var primitiveIndex = 0; primitiveIndex < mesh.Primitives.Count; primitiveIndex++)
                {
                    if (!geometryDecoder.TryFindPositionSource(
                        mesh.Primitives[primitiveIndex],
                        out var source,
                        out var reason))
                    {
                        issues.Add(new SceneGeometryIssue(instance.Id, meshIndex, primitiveIndex, reason!));
                        continue;
                    }
                    for (var vertexIndex = 0; vertexIndex < source.Buffer.VertexCount; vertexIndex++)
                    {
                        var position = source.Decoder.Read(source.Buffer, source.Attribute, vertexIndex);
                        position = Vector3.Transform(position, transform);
                        minimum = Vector3.Min(minimum, position);
                        maximum = Vector3.Max(maximum, position);
                        hasGeometry = true;
                    }
                }
            }
        }
        if (!hasGeometry)
        {
            return new SceneBoundsResult(false, Vector3.Zero, Vector3.Zero, Vector3.Zero, 0f, issues);
        }
        var center = (minimum + maximum) * 0.5f;
        return new SceneBoundsResult(
            true,
            minimum,
            maximum,
            center,
            Vector3.Distance(minimum, maximum) * 0.5f,
            issues);
    }
}
