using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Scene;

public enum SceneElementKind
{
    Prop,
    EntryVolume,
    GroupVolume,
    LookPoint,
    Camera,
    Sound,
    Light,
}

public sealed record SceneElementSelection(SceneElementKind Kind, int SourceIndex, string Name);

public sealed record SceneElementPickHit(SceneElementSelection Selection, Vector3 Position, float Distance);

public sealed class SceneElementPicker
{
    private readonly SceneRaycaster modelRaycaster;

    public SceneElementPicker(IEnumerable<IVertexPositionDecoder>? positionDecoders = null)
    {
        modelRaycaster = new SceneRaycaster(positionDecoders);
    }

    public SceneElementPickHit? Pick(
        SceneRay ray,
        IReadOnlyList<SceneModelInstance> modelInstances,
        MapScene? map,
        float pointMarkerSize)
        => PickAll(ray, modelInstances, map, pointMarkerSize).FirstOrDefault();

    public IReadOnlyList<SceneElementPickHit> PickAll(
        SceneRay ray,
        IReadOnlyList<SceneModelInstance> modelInstances,
        MapScene? map,
        float pointMarkerSize)
    {
        ArgumentNullException.ThrowIfNull(modelInstances);
        if (!float.IsFinite(pointMarkerSize) || pointMarkerSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pointMarkerSize));
        }

        var hits = modelRaycaster.CastAll(ray, modelInstances).Hits
            .Select(modelHit => new SceneElementPickHit(
                new SceneElementSelection(SceneElementKind.Prop, modelHit.Instance.Id, modelHit.Instance.Name),
                modelHit.Position,
                modelHit.Distance))
            .ToList();
        if (map is null) return hits;

        foreach (var volume in map.Volumes)
        {
            var matrix = Matrix4x4.CreateScale(volume.Transform.Scale)
                * Matrix4x4.CreateFromQuaternion(volume.Transform.Rotation)
                * Matrix4x4.CreateTranslation(volume.Transform.Position);
            if (IntersectBox(ray, matrix, out var distance))
            {
                Consider(
                    volume.Kind == MapVolumeKind.Entry ? SceneElementKind.EntryVolume : SceneElementKind.GroupVolume,
                    volume.SourceIndex,
                    volume.Name,
                    distance);
            }
        }
        foreach (var point in map.Points)
        {
            if (IntersectSphere(ray, point.Position, point.Radius ?? pointMarkerSize, out var distance))
            {
                Consider(SceneElementKind.LookPoint, point.SourceIndex, point.Name, distance);
            }
        }
        foreach (var camera in map.Cameras)
        {
            if (IntersectSphere(ray, camera.Eye, pointMarkerSize, out var distance))
            {
                Consider(SceneElementKind.Camera, camera.SourceIndex, camera.Name, distance);
            }
        }
        foreach (var sound in map.Sounds)
        {
            if (IntersectSphere(ray, sound.Position, pointMarkerSize, out var distance))
            {
                Consider(SceneElementKind.Sound, sound.SourceIndex, sound.SoundName, distance);
            }
        }
        foreach (var light in map.Lights)
        {
            if (IntersectSphere(ray, light.Position, pointMarkerSize, out var distance))
            {
                Consider(SceneElementKind.Light, light.SourceIndex, $"Light {light.SourceIndex}", distance);
            }
        }
        return hits.OrderBy(value => value.Distance).ToArray();

        void Consider(SceneElementKind kind, int sourceIndex, string name, float distance)
        {
            hits.Add(new SceneElementPickHit(
                new SceneElementSelection(kind, sourceIndex, name),
                ray.Origin + ray.Direction * distance,
                distance));
        }
    }

    private static bool IntersectBox(SceneRay ray, Matrix4x4 transform, out float distance)
    {
        if (!Matrix4x4.Invert(transform, out var inverse))
        {
            distance = 0;
            return false;
        }
        var origin = Vector3.Transform(ray.Origin, inverse);
        var direction = Vector3.TransformNormal(ray.Direction, inverse);
        var minimum = float.NegativeInfinity;
        var maximum = float.PositiveInfinity;
        if (!IntersectSlab(origin.X, direction.X, ref minimum, ref maximum)
            || !IntersectSlab(origin.Y, direction.Y, ref minimum, ref maximum)
            || !IntersectSlab(origin.Z, direction.Z, ref minimum, ref maximum))
        {
            distance = 0;
            return false;
        }
        var hit = minimum >= 0 ? minimum : maximum;
        if (hit < 0 || !float.IsFinite(hit))
        {
            distance = 0;
            return false;
        }
        distance = hit;
        return true;
    }

    private static bool IntersectSlab(float origin, float direction, ref float minimum, ref float maximum)
    {
        if (direction == 0f) return origin is >= -0.5f and <= 0.5f;
        var first = (-0.5f - origin) / direction;
        var second = (0.5f - origin) / direction;
        if (first > second) (first, second) = (second, first);
        minimum = Math.Max(minimum, first);
        maximum = Math.Min(maximum, second);
        return minimum <= maximum;
    }

    private static bool IntersectSphere(SceneRay ray, Vector3 center, float radius, out float distance)
    {
        if (!float.IsFinite(radius) || radius <= 0)
        {
            distance = 0;
            return false;
        }
        var offset = ray.Origin - center;
        var projected = Vector3.Dot(offset, ray.Direction);
        var discriminant = projected * projected - (Vector3.Dot(offset, offset) - radius * radius);
        if (discriminant < 0)
        {
            distance = 0;
            return false;
        }
        var root = MathF.Sqrt(discriminant);
        var first = -projected - root;
        var second = -projected + root;
        distance = first >= 0 ? first : second;
        return distance >= 0;
    }
}
