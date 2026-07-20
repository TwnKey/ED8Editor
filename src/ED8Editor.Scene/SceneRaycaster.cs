using System.Buffers.Binary;
using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Scene;

public sealed class SceneRaycaster
{
    private readonly SceneGeometryDecoder geometryDecoder;

    public SceneRaycaster(IEnumerable<IVertexPositionDecoder>? positionDecoders = null)
    {
        geometryDecoder = new SceneGeometryDecoder(positionDecoders);
    }

    public SceneRaycastResult Cast(SceneRay ray, IEnumerable<SceneModelInstance> instances)
    {
        var result = CastAll(ray, instances);
        return new SceneRaycastResult(result.Hits.FirstOrDefault(), result.TestedTriangles, result.Issues);
    }

    public SceneRaycastHitsResult CastAll(SceneRay ray, IEnumerable<SceneModelInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);
        var nearestByInstance = new Dictionary<int, ScenePickHit>();
        var testedTriangles = 0;
        var issues = new List<SceneGeometryIssue>();
        foreach (var instance in instances)
        {
            for (var meshIndex = 0; meshIndex < instance.Model.Meshes.Count; meshIndex++)
            {
                var mesh = instance.Model.Meshes[meshIndex];
                var transform = mesh.LocalTransform * instance.Transform;
                for (var primitiveIndex = 0; primitiveIndex < mesh.Primitives.Count; primitiveIndex++)
                {
                    var primitive = mesh.Primitives[primitiveIndex];
                    if (!geometryDecoder.TryFindPositionSource(primitive, out var source, out var sourceReason))
                    {
                        issues.Add(new SceneGeometryIssue(instance.Id, meshIndex, primitiveIndex, sourceReason!));
                        continue;
                    }
                    if (!TryEnumerateTriangles(primitive, out var triangles, out var reason))
                    {
                        issues.Add(new SceneGeometryIssue(instance.Id, meshIndex, primitiveIndex, reason!));
                        continue;
                    }

                    var (buffer, attribute, decoder) = source;
                    var triangleIndex = 0;
                    foreach (var (indexA, indexB, indexC) in triangles!)
                    {
                        if ((uint)indexA >= buffer.VertexCount || (uint)indexB >= buffer.VertexCount || (uint)indexC >= buffer.VertexCount)
                        {
                            issues.Add(new SceneGeometryIssue(instance.Id, meshIndex, primitiveIndex, "An index exceeds the position stream."));
                            break;
                        }
                        var a = Vector3.Transform(decoder.Read(buffer, attribute, indexA), transform);
                        var b = Vector3.Transform(decoder.Read(buffer, attribute, indexB), transform);
                        var c = Vector3.Transform(decoder.Read(buffer, attribute, indexC), transform);
                        testedTriangles++;
                        if (Intersect(ray, a, b, c, out var distance)
                            && (!nearestByInstance.TryGetValue(instance.Id, out var nearest)
                                || distance < nearest.Distance))
                        {
                            nearestByInstance[instance.Id] = new ScenePickHit(
                                instance,
                                meshIndex,
                                primitiveIndex,
                                triangleIndex,
                                ray.Origin + ray.Direction * distance,
                                Vector3.Normalize(Vector3.Cross(b - a, c - a)),
                                distance);
                        }
                        triangleIndex++;
                    }
                }
            }
        }
        return new SceneRaycastHitsResult(
            nearestByInstance.Values.OrderBy(value => value.Distance).ToArray(),
            testedTriangles,
            issues);
    }

    private static bool TryEnumerateTriangles(
        CpuMeshPrimitive primitive,
        out IEnumerable<(int A, int B, int C)>? triangles,
        out string? reason)
    {
        if (primitive.Indices.IndexElementSize is not (2 or 4))
        {
            triangles = null;
            reason = $"Unsupported {primitive.Indices.IndexElementSize}-byte index format.";
            return false;
        }
        if (!TryDecodeIndices(primitive.Indices, out var indices, out reason))
        {
            triangles = null;
            return false;
        }
        var decodedIndices = indices!;
        if (primitive.Topology == PrimitiveTopology.Triangles && decodedIndices.Length % 3 != 0)
        {
            triangles = null;
            reason = "Triangle-list index count is not divisible by three.";
            return false;
        }
        if (primitive.Topology is PrimitiveTopology.TriangleStrip or PrimitiveTopology.TriangleFan && decodedIndices.Length < 3)
        {
            triangles = null;
            reason = $"Topology {primitive.Topology} requires at least three indices.";
            return false;
        }
        triangles = primitive.Topology switch
        {
            PrimitiveTopology.Triangles => TriangleList(decodedIndices),
            PrimitiveTopology.TriangleStrip => TriangleStrip(decodedIndices),
            PrimitiveTopology.TriangleFan => TriangleFan(decodedIndices),
            _ => null,
        };
        reason = triangles is null ? $"Topology {primitive.Topology} does not define triangles." : null;
        return triangles is not null;
    }

    private static bool TryDecodeIndices(CpuIndexBuffer source, out int[]? indices, out string? reason)
    {
        if (source.IndexCount < 0)
        {
            indices = null;
            reason = "Index count is negative.";
            return false;
        }
        var requiredSize = checked((long)source.IndexCount * source.IndexElementSize);
        if (requiredSize > source.Data.Length)
        {
            indices = null;
            reason = "Index buffer is truncated.";
            return false;
        }
        indices = new int[source.IndexCount];
        for (var index = 0; index < indices.Length; index++)
        {
            var offset = index * source.IndexElementSize;
            if (source.IndexElementSize == 2)
            {
                indices[index] = BinaryPrimitives.ReadUInt16LittleEndian(source.Data.AsSpan(offset, 2));
            }
            else
            {
                var value = BinaryPrimitives.ReadUInt32LittleEndian(source.Data.AsSpan(offset, 4));
                if (value > int.MaxValue)
                {
                    indices = null;
                    reason = $"Index {value} exceeds the supported vertex index range.";
                    return false;
                }
                indices[index] = (int)value;
            }
        }
        reason = null;
        return true;
    }

    private static IEnumerable<(int, int, int)> TriangleList(IReadOnlyList<int> indices)
    {
        for (var index = 0; index + 2 < indices.Count; index += 3)
        {
            yield return (indices[index], indices[index + 1], indices[index + 2]);
        }
    }

    private static IEnumerable<(int, int, int)> TriangleStrip(IReadOnlyList<int> indices)
    {
        for (var index = 2; index < indices.Count; index++)
        {
            var a = indices[index - 2];
            var b = indices[index - 1];
            var c = indices[index];
            if ((index & 1) != 0) (a, b) = (b, a);
            if (a != b && b != c && a != c) yield return (a, b, c);
        }
    }

    private static IEnumerable<(int, int, int)> TriangleFan(IReadOnlyList<int> indices)
    {
        if (indices.Count == 0) yield break;
        for (var index = 2; index < indices.Count; index++)
        {
            yield return (indices[0], indices[index - 1], indices[index]);
        }
    }

    private static bool Intersect(SceneRay ray, Vector3 a, Vector3 b, Vector3 c, out float distance)
    {
        var edge1X = (double)b.X - a.X;
        var edge1Y = (double)b.Y - a.Y;
        var edge1Z = (double)b.Z - a.Z;
        var edge2X = (double)c.X - a.X;
        var edge2Y = (double)c.Y - a.Y;
        var edge2Z = (double)c.Z - a.Z;
        var crossX = (double)ray.Direction.Y * edge2Z - (double)ray.Direction.Z * edge2Y;
        var crossY = (double)ray.Direction.Z * edge2X - (double)ray.Direction.X * edge2Z;
        var crossZ = (double)ray.Direction.X * edge2Y - (double)ray.Direction.Y * edge2X;
        var determinant = edge1X * crossX + edge1Y * crossY + edge1Z * crossZ;
        if (determinant == 0d)
        {
            distance = 0;
            return false;
        }
        var inverse = 1d / determinant;
        var fromAX = (double)ray.Origin.X - a.X;
        var fromAY = (double)ray.Origin.Y - a.Y;
        var fromAZ = (double)ray.Origin.Z - a.Z;
        var u = (fromAX * crossX + fromAY * crossY + fromAZ * crossZ) * inverse;
        if (u < 0d || u > 1d)
        {
            distance = 0;
            return false;
        }
        var qX = fromAY * edge1Z - fromAZ * edge1Y;
        var qY = fromAZ * edge1X - fromAX * edge1Z;
        var qZ = fromAX * edge1Y - fromAY * edge1X;
        var v = ((double)ray.Direction.X * qX + (double)ray.Direction.Y * qY + (double)ray.Direction.Z * qZ) * inverse;
        if (v < 0d || u + v > 1d)
        {
            distance = 0;
            return false;
        }
        var hitDistance = (edge2X * qX + edge2Y * qY + edge2Z * qZ) * inverse;
        if (hitDistance < 0d || hitDistance > float.MaxValue)
        {
            distance = 0;
            return false;
        }
        distance = (float)hitDistance;
        return true;
    }
}
