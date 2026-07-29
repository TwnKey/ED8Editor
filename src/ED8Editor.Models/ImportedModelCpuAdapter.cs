using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Models;

public sealed record ImportedModelCpuOptions(
    int MaximumVertexInfluences = 4,
    bool ConvertUnitsToMeters = true);

public sealed record ImportedCpuModelBundle(
    CpuModel Model,
    IReadOnlyList<CpuAnimationClip> Animations,
    IReadOnlyList<ImportedModelDiagnostic> Diagnostics);

/// <summary>
/// Adapts the lossless imported scene to the editor's current CPU renderer contract.
/// Import is deliberately independent from this step: influence reduction and image
/// decoding happen here and never mutate the format-neutral source scene.
/// </summary>
public static class ImportedModelCpuAdapter
{
    public static ImportedCpuModelBundle Convert(
        ImportedModelScene scene,
        Func<ImportedTexture, CpuTexture?>? textureDecoder = null,
        ImportedModelCpuOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        options ??= new ImportedModelCpuOptions();
        if (options.MaximumVertexInfluences is < 1 or > 4)
            throw new ArgumentOutOfRangeException(
                nameof(options), "The D3D11 preview supports between one and four influences.");

        var validation = ImportedModelValidator.Validate(scene);
        var errors = validation
            .Where(value => value.Severity == ImportedDiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length != 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors.Select(value => value.Message)));

        var diagnostics = new List<ImportedModelDiagnostic>(scene.Diagnostics);
        diagnostics.AddRange(validation);
        var unitScale = options.ConvertUnitsToMeters
            ? scene.CoordinateSystem.UnitScaleMeters
            : 1f;
        var nodeNames = MakeUniqueNodeNames(scene.Nodes);
        var nodeWorld = CalculateNodeWorldTransforms(scene.Nodes, unitScale);
        var skeleton = BuildSkeleton(scene, nodeNames, unitScale, out var bindingIndices);
        var textures = DecodeTextures(scene, textureDecoder, diagnostics, out var textureRemap);
        var materials = BuildMaterials(scene, textureRemap);
        var meshes = BuildMeshes(
            scene, nodeWorld, skeleton, bindingIndices, unitScale,
            options.MaximumVertexInfluences);
        var animations = scene.Animations
            .Select(clip => BuildAnimation(scene, clip, nodeNames, unitScale))
            .ToArray();
        var sceneNodes = scene.Nodes
            .Select((node, index) => new CpuSceneNode(
                nodeNames[index],
                node.ParentIndex,
                ScaleTranslation(node.LocalTransform, unitScale)))
            .ToArray();
        var model = new CpuModel(
            Path.GetFileNameWithoutExtension(scene.SourcePath),
            meshes,
            materials,
            textures,
            skeleton,
            sceneNodes,
            animations.FirstOrDefault());
        return new ImportedCpuModelBundle(model, animations, diagnostics);
    }

    private static CpuSkeleton BuildSkeleton(
        ImportedModelScene scene,
        IReadOnlyList<string> nodeNames,
        float unitScale,
        out IReadOnlyDictionary<(int MeshIndex, int NodeIndex), int> bindingIndices)
    {
        var joints = scene.Nodes
            .Select((node, index) => new CpuSkeletonJoint(
                nodeNames[index],
                node.ParentIndex,
                ScaleTranslation(node.LocalTransform, unitScale)))
            .ToArray();
        var inverseBinds = new List<Matrix4x4>();
        var hierarchy = new List<int>();
        var bindings = new Dictionary<(int MeshIndex, int NodeIndex), int>();
        for (var meshIndex = 0; meshIndex < scene.Meshes.Count; meshIndex++)
        {
            var skin = scene.Meshes[meshIndex].Skin;
            if (skin is null) continue;
            foreach (var binding in skin.InverseBindMatrices.OrderBy(value => value.Key))
            {
                bindings.Add((meshIndex, binding.Key), inverseBinds.Count);
                inverseBinds.Add(ScaleTranslation(binding.Value, unitScale));
                hierarchy.Add(binding.Key);
            }
        }
        bindingIndices = bindings;
        return new CpuSkeleton(joints, inverseBinds, hierarchy);
    }

    private static IReadOnlyList<CpuMesh> BuildMeshes(
        ImportedModelScene scene,
        IReadOnlyList<Matrix4x4> nodeWorld,
        CpuSkeleton skeleton,
        IReadOnlyDictionary<(int MeshIndex, int NodeIndex), int> bindingIndices,
        float unitScale,
        int maximumInfluences)
    {
        var result = new List<CpuMesh>();
        var references = scene.Nodes
            .SelectMany((node, nodeIndex) => node.MeshIndices.Select(meshIndex => (nodeIndex, meshIndex)))
            .ToArray();
        foreach (var reference in references)
        {
            var mesh = scene.Meshes[reference.meshIndex];
            var localBones = mesh.Skin?.InverseBindMatrices.Keys
                .OrderBy(value => value)
                .ToArray() ?? Array.Empty<int>();
            if (localBones.Length > byte.MaxValue + 1)
                throw new InvalidDataException(
                    $"Mesh '{mesh.Name}' uses {localBones.Length} bones; the preview limit is 256.");
            var localBoneIndices = localBones
                .Select((nodeIndex, localIndex) => (nodeIndex, localIndex))
                .ToDictionary(value => value.nodeIndex, value => value.localIndex);
            var buffers = BuildVertexBuffers(
                mesh, localBoneIndices, unitScale, maximumInfluences);
            var remaps = localBones
                .Select((nodeIndex, localIndex) => new CpuSkinBoneRemap(
                    nodeIndex,
                    bindingIndices[(reference.meshIndex, nodeIndex)]))
                .ToArray();
            var primitive = new CpuMeshPrimitive(
                buffers,
                BuildIndexBuffer(mesh.Indices),
                mesh.MaterialIndex,
                PrimitiveTopology.Triangles,
                remaps);
            result.Add(new CpuMesh(
                mesh.Name,
                mesh.Skin is null ? nodeWorld[reference.nodeIndex] : Matrix4x4.Identity,
                new[] { primitive },
                CpuMeshPurpose.Render,
                mesh.Skin is null ? reference.nodeIndex : -1));
        }
        return result;
    }

    private static IReadOnlyList<CpuVertexBuffer> BuildVertexBuffers(
        ImportedMesh mesh,
        IReadOnlyDictionary<int, int> localBoneIndices,
        float unitScale,
        int maximumInfluences)
    {
        var result = new List<CpuVertexBuffer>
        {
            Vector3Buffer(mesh.Vertices.Select(value => value.Position * unitScale), VertexSemantic.Position),
            Vector3Buffer(mesh.Vertices.Select(value => value.Normal), VertexSemantic.Normal),
        };
        if (mesh.Vertices.Any(value => value.TexCoords.Count != 0))
        {
            result.Add(Vector2Buffer(
                mesh.Vertices.Select(value =>
                    value.TexCoords.Count == 0 ? Vector2.Zero : value.TexCoords[0]),
                VertexSemantic.TextureCoordinate));
        }
        if (mesh.Skin is not null)
        {
            var jointBytes = new byte[checked(mesh.Vertices.Count * 4)];
            var weightBytes = new byte[checked(mesh.Vertices.Count * 16)];
            for (var vertexIndex = 0; vertexIndex < mesh.Vertices.Count; vertexIndex++)
            {
                var selected = mesh.Vertices[vertexIndex].Influences
                    .Where(value => value.Weight > 0f)
                    .OrderByDescending(value => value.Weight)
                    .ThenBy(value => value.NodeIndex)
                    .Take(maximumInfluences)
                    .ToArray();
                var total = selected.Sum(value => value.Weight);
                for (var influenceIndex = 0; influenceIndex < selected.Length; influenceIndex++)
                {
                    if (!localBoneIndices.TryGetValue(
                            selected[influenceIndex].NodeIndex, out var localIndex))
                    {
                        throw new InvalidDataException(
                            $"Mesh '{mesh.Name}' has an influence without a local bind.");
                    }
                    jointBytes[vertexIndex * 4 + influenceIndex] = checked((byte)localIndex);
                    WriteFloat(
                        weightBytes,
                        vertexIndex * 16 + influenceIndex * 4,
                        total > 0f ? selected[influenceIndex].Weight / total : 0f);
                }
            }
            result.Add(new CpuVertexBuffer(
                jointBytes, 4, mesh.Vertices.Count,
                new[] { new CpuVertexAttribute(VertexSemantic.JointIndices, 0, "UInt8x4", 0) }));
            result.Add(new CpuVertexBuffer(
                weightBytes, 16, mesh.Vertices.Count,
                new[] { new CpuVertexAttribute(VertexSemantic.JointWeights, 0, "Float32x4", 0) }));
        }
        return result;
    }

    private static CpuVertexBuffer Vector3Buffer(
        IEnumerable<Vector3> values,
        VertexSemantic semantic)
    {
        var source = values.ToArray();
        var data = new byte[checked(source.Length * 12)];
        for (var index = 0; index < source.Length; index++)
        {
            WriteFloat(data, index * 12, source[index].X);
            WriteFloat(data, index * 12 + 4, source[index].Y);
            WriteFloat(data, index * 12 + 8, source[index].Z);
        }
        return new CpuVertexBuffer(
            data, 12, source.Length,
            new[] { new CpuVertexAttribute(semantic, 0, "Float32x3", 0) });
    }

    private static CpuVertexBuffer Vector2Buffer(
        IEnumerable<Vector2> values,
        VertexSemantic semantic)
    {
        var source = values.ToArray();
        var data = new byte[checked(source.Length * 8)];
        for (var index = 0; index < source.Length; index++)
        {
            WriteFloat(data, index * 8, source[index].X);
            WriteFloat(data, index * 8 + 4, source[index].Y);
        }
        return new CpuVertexBuffer(
            data, 8, source.Length,
            new[] { new CpuVertexAttribute(semantic, 0, "Float32x2", 0) });
    }

    private static CpuIndexBuffer BuildIndexBuffer(IReadOnlyList<int> indices)
    {
        var use16Bit = indices.Count == 0 || indices.Max() <= ushort.MaxValue;
        var size = use16Bit ? 2 : 4;
        var data = new byte[checked(indices.Count * size)];
        for (var index = 0; index < indices.Count; index++)
        {
            if (use16Bit)
                BitConverter.GetBytes(checked((ushort)indices[index])).CopyTo(data, index * size);
            else
                BitConverter.GetBytes(checked((uint)indices[index])).CopyTo(data, index * size);
        }
        return new CpuIndexBuffer(data, size, indices.Count);
    }

    private static IReadOnlyList<CpuTexture> DecodeTextures(
        ImportedModelScene scene,
        Func<ImportedTexture, CpuTexture?>? decoder,
        ICollection<ImportedModelDiagnostic> diagnostics,
        out IReadOnlyDictionary<int, int> textureRemap)
    {
        var result = new List<CpuTexture>();
        var remap = new Dictionary<int, int>();
        if (decoder is not null)
        {
            for (var index = 0; index < scene.Textures.Count; index++)
            {
                try
                {
                    var decoded = decoder(scene.Textures[index]);
                    if (decoded is null) continue;
                    remap.Add(index, result.Count);
                    result.Add(decoded);
                }
                catch (Exception exception) when (
                    exception is InvalidDataException
                    or ArgumentException
                    or NotSupportedException)
                {
                    diagnostics.Add(new ImportedModelDiagnostic(
                        ImportedDiagnosticSeverity.Warning,
                        "texture-preview-decode-failed",
                        $"Texture '{scene.Textures[index].Name}' was preserved but cannot be previewed: "
                        + exception.Message));
                }
            }
        }
        textureRemap = remap;
        return result;
    }

    private static IReadOnlyList<CpuMaterial> BuildMaterials(
        ImportedModelScene scene,
        IReadOnlyDictionary<int, int> textureRemap)
        => scene.Materials.Select(material =>
        {
            int? baseColorTexture = null;
            var textureBindings = new Dictionary<string, int>(StringComparer.Ordinal);
            if (material.TextureBindings.TryGetValue(
                    ImportedTextureUsage.BaseColor, out var importedTexture)
                && textureRemap.TryGetValue(importedTexture, out var cpuTexture))
            {
                baseColorTexture = cpuTexture;
                textureBindings.Add("DiffuseMapSampler", cpuTexture);
            }
            return new CpuMaterial(
                material.Name,
                material.BaseColor with { W = material.Opacity },
                baseColorTexture,
                new Dictionary<string, float[]>
                {
                    ["MetallicFactor"] = new[] { material.MetallicFactor },
                    ["RoughnessFactor"] = new[] { material.RoughnessFactor },
                    ["EmissiveColor"] = new[]
                    {
                        material.EmissiveColor.X,
                        material.EmissiveColor.Y,
                        material.EmissiveColor.Z,
                    },
                },
                material.SourceProperties,
                textureBindings);
        }).ToArray();

    private static CpuAnimationClip BuildAnimation(
        ImportedModelScene scene,
        ImportedAnimationClip clip,
        IReadOnlyList<string> nodeNames,
        float unitScale)
    {
        var channels = new List<CpuAnimationChannel>();
        foreach (var source in clip.Channels)
        {
            AddVector(source.TranslationKeys, CpuAnimationPath.Translation, unitScale);
            AddRotation(source.RotationKeys);
            AddVector(source.ScaleKeys, CpuAnimationPath.Scale, 1f);

            void AddVector(
                IReadOnlyList<ImportedVectorKey> keys,
                CpuAnimationPath path,
                float valueScale)
            {
                if (keys.Count == 0) return;
                channels.Add(new CpuAnimationChannel(
                    nodeNames[source.NodeIndex],
                    path,
                    CpuAnimationInterpolation.Linear,
                    keys.Select(value => checked((float)value.TimeSeconds)).ToArray(),
                    keys.Select(value => new Vector4(value.Value * valueScale, 0f)).ToArray()));
            }

            void AddRotation(IReadOnlyList<ImportedQuaternionKey> keys)
            {
                if (keys.Count == 0) return;
                channels.Add(new CpuAnimationChannel(
                    nodeNames[source.NodeIndex],
                    CpuAnimationPath.Rotation,
                    CpuAnimationInterpolation.Linear,
                    keys.Select(value => checked((float)value.TimeSeconds)).ToArray(),
                    keys.Select(value => new Vector4(
                        value.Value.X, value.Value.Y, value.Value.Z, value.Value.W)).ToArray()));
            }
        }
        return new CpuAnimationClip(
            Path.GetFileNameWithoutExtension(scene.SourcePath),
            clip.Name,
            0f,
            checked((float)clip.DurationSeconds),
            channels);
    }

    private static IReadOnlyList<string> MakeUniqueNodeNames(
        IReadOnlyList<ImportedSceneNode> nodes)
    {
        var counts = nodes.GroupBy(value => value.Name, StringComparer.Ordinal)
            .ToDictionary(value => value.Key, value => value.Count(), StringComparer.Ordinal);
        return nodes.Select((node, index) =>
            counts[node.Name] == 1 ? node.Name : $"{node.Name}#{index}").ToArray();
    }

    private static IReadOnlyList<Matrix4x4> CalculateNodeWorldTransforms(
        IReadOnlyList<ImportedSceneNode> nodes,
        float unitScale)
    {
        var result = new Matrix4x4[nodes.Count];
        for (var index = 0; index < nodes.Count; index++)
        {
            var local = ScaleTranslation(nodes[index].LocalTransform, unitScale);
            result[index] = nodes[index].ParentIndex >= 0
                ? local * result[nodes[index].ParentIndex]
                : local;
        }
        return result;
    }

    private static Matrix4x4 ScaleTranslation(Matrix4x4 value, float scale)
    {
        value.M41 *= scale;
        value.M42 *= scale;
        value.M43 *= scale;
        return value;
    }

    private static void WriteFloat(byte[] destination, int offset, float value)
        => BitConverter.GetBytes(value).CopyTo(destination, offset);
}
