using System.Numerics;
using Assimp;
using NumericsMatrix = System.Numerics.Matrix4x4;
using NumericsQuaternion = System.Numerics.Quaternion;

namespace ED8Editor.Models;

/// <summary>
/// Multi-format frontend. Assimp's documented output contract is right-handed,
/// Y-up and counter-clockwise, regardless of whether the input is FBX, glTF,
/// OBJ or COLLADA.
/// </summary>
public sealed class AssimpModelImporter : IModelFormatImporter
{
    private static readonly string[] Extensions =
    {
        ".fbx", ".glb", ".gltf", ".obj", ".dae",
    };

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public ImportedModelScene Import(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new ArgumentException("A model path is required.", nameof(modelPath));
        var fullPath = Path.GetFullPath(modelPath);
        using var context = new AssimpContext();
        var scene = context.ImportFile(
            fullPath,
            PostProcessSteps.Triangulate
            | PostProcessSteps.SortByPrimitiveType
            | PostProcessSteps.JoinIdenticalVertices
            | PostProcessSteps.GenerateSmoothNormals
            | PostProcessSteps.CalculateTangentSpace
            | PostProcessSteps.ValidateDataStructure);
        if (scene is null || scene.RootNode is null)
            throw new InvalidDataException($"Assimp returned no scene for '{fullPath}'.");

        var diagnostics = new List<ImportedModelDiagnostic>();
        var nodes = new List<ImportedSceneNode>();
        var nodeIndices = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
        FlattenNodes(scene.RootNode, -1, nodes, nodeIndices);
        var nodesByName = nodes
            .Select((node, index) => (node.Name, Index: index))
            .GroupBy(value => value.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(value => value.Index).ToArray(),
                StringComparer.Ordinal);
        foreach (var duplicate in nodesByName.Where(value => value.Value.Length > 1))
        {
            diagnostics.Add(new ImportedModelDiagnostic(
                ImportedDiagnosticSeverity.Warning,
                "duplicate-node-name",
                $"Node name '{duplicate.Key}' occurs {duplicate.Value.Length} times."));
        }

        var textures = new List<ImportedTexture>();
        var textureIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var materials = scene.Materials
            .Select(material => ImportMaterial(
                material, scene, fullPath, textures, textureIndices, diagnostics))
            .ToArray();
        var meshes = scene.Meshes
            .Select(mesh => ImportMesh(mesh, nodesByName))
            .ToArray();
        var animations = scene.Animations
            .Select(animation => ImportAnimation(animation, nodesByName))
            .ToArray();

        return new ImportedModelScene(
            Path.GetFileNameWithoutExtension(fullPath),
            fullPath,
            // Assimp has already converted FBX/COLLADA coordinates into its
            // common scene basis. Keep the source-declared unit for reporting,
            // but do not apply it to the normalized geometry a second time.
            new ImportedCoordinateSystem(
                true,
                ImportedUpAxis.Y,
                UnitScaleMeters: 1f,
                SourceUnitScaleMeters: ReadSourceUnitScaleMeters(scene)),
            nodes,
            meshes,
            materials,
            textures,
            animations,
            diagnostics);
    }

    private static void FlattenNodes(
        Node node,
        int parentIndex,
        ICollection<ImportedSceneNode> output,
        IDictionary<Node, int> nodeIndices)
    {
        var index = output.Count;
        nodeIndices.Add(node, index);
        output.Add(new ImportedSceneNode(
            node.Name,
            parentIndex,
            ToNumerics(node.Transform),
            node.MeshIndices.ToArray()));
        foreach (var child in node.Children)
            FlattenNodes(child, index, output, nodeIndices);
    }

    private static ImportedMesh ImportMesh(
        Mesh mesh,
        IReadOnlyDictionary<string, int[]> nodesByName)
    {
        var influences = Enumerable.Range(0, mesh.VertexCount)
            .Select(_ => new List<ImportedVertexInfluence>())
            .ToArray();
        var inverseBinds = new Dictionary<int, NumericsMatrix>();
        foreach (var bone in mesh.Bones)
        {
            var nodeIndex = RequireUniqueNode(bone.Name, nodesByName, "bone");
            inverseBinds[nodeIndex] = ToNumerics(bone.OffsetMatrix);
            foreach (var weight in bone.VertexWeights)
            {
                if (weight.VertexID < 0 || weight.VertexID >= influences.Length)
                    throw new InvalidDataException(
                        $"Bone '{bone.Name}' targets vertex {weight.VertexID},"
                        + $" outside mesh '{mesh.Name}'.");
                if (weight.Weight > 0f)
                    influences[weight.VertexID].Add(
                        new ImportedVertexInfluence(nodeIndex, weight.Weight));
            }
        }

        var uvChannels = mesh.TextureCoordinateChannelCount;
        var colorChannels = mesh.VertexColorChannelCount;
        var vertices = new ImportedVertex[mesh.VertexCount];
        for (var index = 0; index < vertices.Length; index++)
        {
            var normal = mesh.HasNormals ? ToNumerics(mesh.Normals[index]) : Vector3.UnitY;
            var tangent = mesh.HasTangentBasis ? ToNumerics(mesh.Tangents[index]) : Vector3.Zero;
            var bitangent = mesh.HasTangentBasis ? ToNumerics(mesh.BiTangents[index]) : Vector3.Zero;
            var texCoords = Enumerable.Range(0, uvChannels)
                .Select(channel =>
                {
                    var value = mesh.TextureCoordinateChannels[channel][index];
                    return new Vector2(value.X, value.Y);
                })
                .ToArray();
            var colors = Enumerable.Range(0, colorChannels)
                .Select(channel =>
                {
                    var value = mesh.VertexColorChannels[channel][index];
                    return value;
                })
                .ToArray();
            vertices[index] = new ImportedVertex(
                ToNumerics(mesh.Vertices[index]),
                normal,
                tangent,
                bitangent,
                texCoords,
                colors,
                influences[index]
                    .OrderByDescending(value => value.Weight)
                    .ToArray());
        }

        var indices = mesh.Faces.SelectMany(face =>
        {
            if (face.IndexCount != 3)
                throw new InvalidDataException(
                    $"Mesh '{mesh.Name}' contains a non-triangle after triangulation.");
            return face.Indices;
        }).ToArray();
        return new ImportedMesh(
            string.IsNullOrWhiteSpace(mesh.Name) ? $"mesh_{mesh.MaterialIndex}" : mesh.Name,
            vertices,
            indices,
            mesh.MaterialIndex,
            inverseBinds.Count == 0 ? null : new ImportedSkin(inverseBinds));
    }

    private static ImportedMaterial ImportMaterial(
        Material material,
        Scene scene,
        string modelPath,
        ICollection<ImportedTexture> textures,
        IDictionary<string, int> textureIndices,
        ICollection<ImportedModelDiagnostic> diagnostics)
    {
        var bindings = new Dictionary<ImportedTextureUsage, int>();
        AddTextureBinding(material, TextureType.Diffuse, ImportedTextureUsage.BaseColor);
        AddTextureBinding(material, TextureType.BaseColor, ImportedTextureUsage.BaseColor);
        AddTextureBinding(material, TextureType.Normals, ImportedTextureUsage.Normal);
        AddTextureBinding(material, TextureType.NormalCamera, ImportedTextureUsage.Normal);
        AddTextureBinding(material, TextureType.Metalness, ImportedTextureUsage.MetallicRoughness);
        AddTextureBinding(material, TextureType.Roughness, ImportedTextureUsage.MetallicRoughness);
        AddTextureBinding(
            material,
            TextureType.GltfMetallicRoughness,
            ImportedTextureUsage.MetallicRoughness);
        AddTextureBinding(material, TextureType.Emissive, ImportedTextureUsage.Emissive);
        AddTextureBinding(material, TextureType.AmbientOcclusion, ImportedTextureUsage.Occlusion);
        AddTextureBinding(material, TextureType.Opacity, ImportedTextureUsage.Opacity);
        AddTextureBinding(material, TextureType.Specular, ImportedTextureUsage.Specular);
        AddTextureBinding(material, TextureType.Height, ImportedTextureUsage.Height);

        var color = material.HasColorDiffuse
            ? material.ColorDiffuse
            : Vector4.One;
        var emissive = material.HasColorEmissive
            ? new Vector3(
                material.ColorEmissive.X,
                material.ColorEmissive.Y,
                material.ColorEmissive.Z)
            : Vector3.Zero;
        var opacity = material.HasOpacity ? material.Opacity : color.W;
        return new ImportedMaterial(
            material.Name,
            color,
            emissive,
            0f,
            1f,
            opacity,
            false,
            bindings,
            new Dictionary<string, string>(StringComparer.Ordinal));

        void AddTextureBinding(
            Material source,
            TextureType type,
            ImportedTextureUsage usage)
        {
            if (bindings.ContainsKey(usage)
                || !source.GetMaterialTexture(type, 0, out var slot))
                return;
            var key = slot.FilePath;
            if (!textureIndices.TryGetValue(key, out var textureIndex))
            {
                var imported = ImportTexture(scene, modelPath, key, diagnostics);
                textureIndex = textures.Count;
                textures.Add(imported);
                textureIndices.Add(key, textureIndex);
            }
            bindings.Add(usage, textureIndex);
        }
    }

    private static ImportedTexture ImportTexture(
        Scene scene,
        string modelPath,
        string reference,
        ICollection<ImportedModelDiagnostic> diagnostics)
    {
        if (reference.StartsWith('*')
            && int.TryParse(reference.AsSpan(1), out var embeddedIndex)
            && embeddedIndex >= 0
            && embeddedIndex < scene.TextureCount)
        {
            var embedded = scene.Textures[embeddedIndex];
            var bytes = embedded.IsCompressed
                ? embedded.CompressedData
                : ConvertTexels(embedded.NonCompressedData);
            return new ImportedTexture(
                string.IsNullOrWhiteSpace(embedded.Filename)
                    ? $"embedded_{embeddedIndex}"
                    : embedded.Filename,
                null,
                MediaTypeFromHint(embedded.CompressedFormatHint),
                bytes,
                true,
                reference);
        }

        var resolved = ResolveExternalTexture(modelPath, reference);
        if (resolved is null)
        {
            diagnostics.Add(new ImportedModelDiagnostic(
                ImportedDiagnosticSeverity.Warning,
                "missing-texture",
                $"Texture '{reference}' referenced by the model was not found."));
            return new ImportedTexture(
                Path.GetFileName(reference),
                null,
                MediaTypeFromExtension(Path.GetExtension(reference)),
                Array.Empty<byte>(),
                false,
                reference);
        }
        return new ImportedTexture(
            Path.GetFileName(resolved),
            resolved,
            MediaTypeFromExtension(Path.GetExtension(resolved)),
            File.ReadAllBytes(resolved),
            false,
            reference);
    }

    private static string? ResolveExternalTexture(string modelPath, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var normalized = reference.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) && File.Exists(normalized))
            return Path.GetFullPath(normalized);
        var directory = Path.GetDirectoryName(modelPath)!;
        var relative = Path.GetFullPath(Path.Combine(directory, normalized));
        if (File.Exists(relative)) return relative;

        // Exporters often retain an obsolete source directory in the material.
        // A basename is accepted only when it identifies exactly one package
        // resource; ambiguity is never settled by choosing the first file.
        var fileName = Path.GetFileName(normalized);
        var matches = Directory.EnumerateFiles(directory, fileName, SearchOption.AllDirectories)
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? Path.GetFullPath(matches[0]) : null;
    }

    private static ImportedAnimationClip ImportAnimation(
        Animation animation,
        IReadOnlyDictionary<string, int[]> nodesByName)
    {
        var ticksPerSecond = animation.TicksPerSecond > 0 ? animation.TicksPerSecond : 25d;
        var channels = animation.NodeAnimationChannels.Select(channel =>
            new ImportedAnimationChannel(
                RequireUniqueNode(channel.NodeName, nodesByName, "animation channel"),
                channel.PositionKeys.Select(key =>
                    new ImportedVectorKey(
                        key.Time / ticksPerSecond,
                        ToNumerics(key.Value))).ToArray(),
                channel.RotationKeys.Select(key =>
                    new ImportedQuaternionKey(
                        key.Time / ticksPerSecond,
                        ToNumerics(key.Value))).ToArray(),
                channel.ScalingKeys.Select(key =>
                    new ImportedVectorKey(
                        key.Time / ticksPerSecond,
                        ToNumerics(key.Value))).ToArray())).ToArray();
        return new ImportedAnimationClip(
            string.IsNullOrWhiteSpace(animation.Name) ? "animation" : animation.Name,
            animation.DurationInTicks / ticksPerSecond,
            channels);
    }

    private static int RequireUniqueNode(
        string name,
        IReadOnlyDictionary<string, int[]> nodesByName,
        string role)
    {
        if (!nodesByName.TryGetValue(name, out var matches) || matches.Length == 0)
            throw new InvalidDataException($"{role} '{name}' has no scene node.");
        if (matches.Length != 1)
            throw new InvalidDataException(
                $"{role} '{name}' is ambiguous because {matches.Length} nodes share that name.");
        return matches[0];
    }

    private static float ReadSourceUnitScaleMeters(Scene scene)
    {
        if (scene.Metadata is null) return 1f;
        if (scene.Metadata.TryGetValue("UnitScaleFactor", out var value)
            && value.Data is float scale)
            return scale / 100f;
        return 1f;
    }

    private static byte[] ConvertTexels(Texel[] texels)
    {
        var bytes = new byte[checked(texels.Length * 4)];
        for (var index = 0; index < texels.Length; index++)
        {
            bytes[index * 4] = texels[index].R;
            bytes[index * 4 + 1] = texels[index].G;
            bytes[index * 4 + 2] = texels[index].B;
            bytes[index * 4 + 3] = texels[index].A;
        }
        return bytes;
    }

    private static string MediaTypeFromHint(string hint)
        => hint.Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "dds" => "image/vnd-ms.dds",
            var value when value.Length > 0 => $"image/{value}",
            _ => "application/octet-stream",
        };

    private static string MediaTypeFromExtension(string extension)
        => MediaTypeFromHint(extension);

    private static Vector3 ToNumerics(Vector3 value)
        => value;

    private static NumericsQuaternion ToNumerics(NumericsQuaternion value)
        => NumericsQuaternion.Normalize(value);

    private static NumericsMatrix ToNumerics(NumericsMatrix value)
        // Assimp exposes transforms in its column-vector convention, whereas
        // System.Numerics and the ED8 renderer compose row vectors. A transpose
        // converts the convention without changing the represented transform.
        => NumericsMatrix.Transpose(value);
}
