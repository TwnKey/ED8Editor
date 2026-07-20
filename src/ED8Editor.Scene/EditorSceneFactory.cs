using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Scene;

public sealed class EditorSceneFactory
{
    public IReadOnlyList<SceneModelInstance> Create(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var instances = new List<SceneModelInstance>();
        if (session.Map is not null)
        {
            foreach (var prop in session.Map.Props)
            {
                if (!session.AssetModels.TryGetValue(prop.AssetId, out var load) || load.Model is null) continue;
                instances.Add(new SceneModelInstance(
                    prop.SourceIndex,
                    prop.AssetId,
                    prop.Name,
                    load.Model,
                    CreateTransform(prop.Transform),
                    prop.MaterialDiffuse,
                    prop.MaterialEmission));
            }
        }

        if (instances.Count == 0)
        {
            var id = 0;
            foreach (var load in session.AssetModels.Values.Where(value => value.Model is not null))
            {
                instances.Add(new SceneModelInstance(id++, load.AssetId, load.AssetId, load.Model!, Matrix4x4.Identity, Vector4.One, Vector3.Zero));
            }
        }
        return instances;
    }

    public static Matrix4x4 CreateTransform(MapTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return Matrix4x4.CreateScale(transform.Scale)
            * Matrix4x4.CreateFromQuaternion(transform.Rotation)
            * Matrix4x4.CreateTranslation(transform.Position);
    }
}
