using System.Numerics;

namespace ED8Editor.Models;

public static class ImportedModelValidator
{
    public static IReadOnlyList<ImportedModelDiagnostic> Validate(ImportedModelScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var diagnostics = new List<ImportedModelDiagnostic>();
        if (scene.Nodes.Count == 0)
            Error("missing-nodes", "The imported scene has no nodes.");
        for (var nodeIndex = 0; nodeIndex < scene.Nodes.Count; nodeIndex++)
        {
            var node = scene.Nodes[nodeIndex];
            if (node.ParentIndex < -1 || node.ParentIndex >= nodeIndex)
                Error(
                    "invalid-node-parent",
                    $"Node {nodeIndex} '{node.Name}' has invalid parent {node.ParentIndex}.");
            if (!IsFinite(node.LocalTransform))
                Error(
                    "non-finite-node-transform",
                    $"Node {nodeIndex} '{node.Name}' has a non-finite transform.");
            foreach (var meshIndex in node.MeshIndices)
            {
                if ((uint)meshIndex >= scene.Meshes.Count)
                    Error(
                        "invalid-node-mesh",
                        $"Node {nodeIndex} '{node.Name}' references missing mesh {meshIndex}.");
            }
        }

        for (var meshIndex = 0; meshIndex < scene.Meshes.Count; meshIndex++)
        {
            var mesh = scene.Meshes[meshIndex];
            if ((uint)mesh.MaterialIndex >= scene.Materials.Count)
                Error(
                    "invalid-mesh-material",
                    $"Mesh {meshIndex} '{mesh.Name}' references missing material {mesh.MaterialIndex}.");
            if (mesh.Indices.Length % 3 != 0)
                Error(
                    "partial-triangle",
                    $"Mesh {meshIndex} '{mesh.Name}' has a partial triangle.");
            if (mesh.Indices.Any(index => index < 0 || index >= mesh.Vertices.Count))
                Error(
                    "invalid-mesh-index",
                    $"Mesh {meshIndex} '{mesh.Name}' has an index outside its vertex array.");
            for (var vertexIndex = 0; vertexIndex < mesh.Vertices.Count; vertexIndex++)
            {
                var vertex = mesh.Vertices[vertexIndex];
                if (!IsFinite(vertex.Position)
                    || !IsFinite(vertex.Normal)
                    || !IsFinite(vertex.Tangent)
                    || !IsFinite(vertex.Bitangent)
                    || vertex.TexCoords.Any(value => !IsFinite(value))
                    || vertex.Colors.Any(value => !IsFinite(value)))
                {
                    Error(
                        "non-finite-vertex",
                        $"Mesh {meshIndex} '{mesh.Name}' vertex {vertexIndex} contains a non-finite value.");
                }
                foreach (var influence in vertex.Influences)
                {
                    if ((uint)influence.NodeIndex >= scene.Nodes.Count)
                        Error(
                            "invalid-influence-node",
                            $"Mesh {meshIndex} '{mesh.Name}' vertex {vertexIndex}"
                            + $" follows missing node {influence.NodeIndex}.");
                    if (!float.IsFinite(influence.Weight) || influence.Weight < 0f)
                        Error(
                            "invalid-influence-weight",
                            $"Mesh {meshIndex} '{mesh.Name}' vertex {vertexIndex}"
                            + $" has invalid weight {influence.Weight}.");
                    if (mesh.Skin is null
                        || !mesh.Skin.InverseBindMatrices.ContainsKey(influence.NodeIndex))
                    {
                        Error(
                            "missing-inverse-bind",
                            $"Mesh {meshIndex} '{mesh.Name}' vertex {vertexIndex}"
                            + $" follows node {influence.NodeIndex} without an inverse bind matrix.");
                    }
                }
            }
            if (mesh.Skin is not null)
            {
                foreach (var binding in mesh.Skin.InverseBindMatrices)
                {
                    if ((uint)binding.Key >= scene.Nodes.Count)
                        Error(
                            "invalid-bind-node",
                            $"Mesh {meshIndex} '{mesh.Name}' binds missing node {binding.Key}.");
                    if (!IsFinite(binding.Value))
                        Error(
                            "non-finite-inverse-bind",
                            $"Mesh {meshIndex} '{mesh.Name}' has a non-finite inverse bind matrix.");
                }
            }
        }

        for (var materialIndex = 0; materialIndex < scene.Materials.Count; materialIndex++)
        {
            foreach (var binding in scene.Materials[materialIndex].TextureBindings)
            {
                if ((uint)binding.Value >= scene.Textures.Count)
                    Error(
                        "invalid-material-texture",
                        $"Material {materialIndex} references missing texture {binding.Value}.");
            }
        }
        foreach (var clip in scene.Animations)
        {
            if (!double.IsFinite(clip.DurationSeconds) || clip.DurationSeconds < 0d)
                Error("invalid-animation-duration", $"Animation '{clip.Name}' has invalid duration.");
            foreach (var channel in clip.Channels)
            {
                if ((uint)channel.NodeIndex >= scene.Nodes.Count)
                    Error(
                        "invalid-animation-node",
                        $"Animation '{clip.Name}' targets missing node {channel.NodeIndex}.");
                ValidateTimes(clip.Name, channel.TranslationKeys.Select(value => value.TimeSeconds));
                ValidateTimes(clip.Name, channel.RotationKeys.Select(value => value.TimeSeconds));
                ValidateTimes(clip.Name, channel.ScaleKeys.Select(value => value.TimeSeconds));
            }
        }
        return diagnostics;

        void Error(string code, string message)
            => diagnostics.Add(
                new ImportedModelDiagnostic(ImportedDiagnosticSeverity.Error, code, message));

        void ValidateTimes(string clipName, IEnumerable<double> times)
        {
            var previous = double.NegativeInfinity;
            foreach (var time in times)
            {
                if (!double.IsFinite(time) || time < previous)
                {
                    Error(
                        "invalid-animation-time",
                        $"Animation '{clipName}' contains non-finite or unordered key times.");
                    return;
                }
                previous = time;
            }
        }
    }

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Vector4 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y)
            && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12)
            && float.IsFinite(value.M13) && float.IsFinite(value.M14)
            && float.IsFinite(value.M21) && float.IsFinite(value.M22)
            && float.IsFinite(value.M23) && float.IsFinite(value.M24)
            && float.IsFinite(value.M31) && float.IsFinite(value.M32)
            && float.IsFinite(value.M33) && float.IsFinite(value.M34)
            && float.IsFinite(value.M41) && float.IsFinite(value.M42)
            && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
