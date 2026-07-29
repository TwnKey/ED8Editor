using ED8Editor.Models;
using ED8Editor.Phyre.Authoring;
using ED8Editor.Core;
using System.Numerics;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: ModelImportProbe <model-file-or-directory>");
    return 2;
}

var service = new ModelImportService();
var candidates = service.FindCandidates(args[0]);
if (candidates.Count != 1)
{
    Console.WriteLine($"candidates={candidates.Count}");
    foreach (var candidate in candidates)
        Console.WriteLine($"{candidate.Format}|{candidate.Length}|{candidate.Path}");
    return candidates.Count == 0 ? 1 : 3;
}

var packageRoot = Directory.Exists(args[0])
    ? Path.GetFullPath(args[0])
    : Path.GetDirectoryName(Path.GetFullPath(args[0]));
var scene = service.Import(candidates[0].Path, packageRoot);
var vertexCount = scene.Meshes.Sum(mesh => mesh.Vertices.Count);
var triangleCount = scene.Meshes.Sum(mesh => mesh.Indices.Length / 3);
var skinnedMeshes = scene.Meshes.Count(mesh => mesh.Skin is not null);
var influencedVertices = scene.Meshes.Sum(mesh =>
    mesh.Vertices.Count(vertex => vertex.Influences.Count > 0));
var maxInfluences = scene.Meshes
    .SelectMany(mesh => mesh.Vertices)
    .Select(vertex => vertex.Influences.Count)
    .DefaultIfEmpty()
    .Max();
Console.WriteLine($"source={scene.SourcePath}");
Console.WriteLine(
    $"nodes={scene.Nodes.Count}; meshes={scene.Meshes.Count};"
    + $" vertices={vertexCount}; triangles={triangleCount};"
    + $" materials={scene.Materials.Count}; textures={scene.Textures.Count};"
    + $" animations={scene.Animations.Count}");
Console.WriteLine(
    $"skinned_meshes={skinnedMeshes}; influenced_vertices={influencedVertices};"
    + $" max_influences={maxInfluences}; unit_meters={scene.CoordinateSystem.UnitScaleMeters:G9};"
    + $" source_unit_meters={scene.CoordinateSystem.SourceUnitScaleMeters:G9}");
var phyre = ImportedModelPhyreAdapter.Convert(scene);
Console.WriteLine(
    $"phyre_meshes={phyre.Meshes.Count}; phyre_joints={phyre.Joints.Count};"
    + $" phyre_problems={phyre.Problems().Count}");
var preview = ImportedModelCpuAdapter.Convert(scene);
var bindPose = new CpuSkeletonPoseEvaluator().Evaluate(
    preview.Model.Skeleton!, null, 0f);
var bindTranslation = bindPose.SkinMatrices
    .Select(matrix => new Vector3(matrix.M41, matrix.M42, matrix.M43).Length())
    .DefaultIfEmpty()
    .Max();
var bindIdentityDeviation = bindPose.SkinMatrices
    .Select(matrix =>
    {
        var values = new[]
        {
            matrix.M11 - 1f, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22 - 1f, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33 - 1f, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44 - 1f,
        };
        return values.Max(MathF.Abs);
    })
    .DefaultIfEmpty()
    .Max();
var bindMeshSpread = preview.Model.Meshes
    .Select(mesh =>
    {
        var remaps = mesh.Primitives.Single().SkinBones ?? Array.Empty<CpuSkinBoneRemap>();
        if (remaps.Count < 2) return 0f;
        var first = bindPose.SkinMatrices[remaps[0].SkeletonMatrixIndex];
        return remaps.Skip(1)
            .Select(remap => MatrixDeviation(
                first,
                bindPose.SkinMatrices[remap.SkeletonMatrixIndex]))
            .DefaultIfEmpty()
            .Max();
    })
    .DefaultIfEmpty()
    .Max();
var rowWorld = new Matrix4x4[scene.Nodes.Count];
var columnWorld = new Matrix4x4[scene.Nodes.Count];
for (var nodeIndex = 0; nodeIndex < scene.Nodes.Count; nodeIndex++)
{
    var node = scene.Nodes[nodeIndex];
    rowWorld[nodeIndex] = node.ParentIndex < 0
        ? node.LocalTransform
        : node.LocalTransform * rowWorld[node.ParentIndex];
    columnWorld[nodeIndex] = node.ParentIndex < 0
        ? node.LocalTransform
        : columnWorld[node.ParentIndex] * node.LocalTransform;
}
var bindFormulaSpreads = new[] { 0f, 0f, 0f, 0f };
foreach (var mesh in scene.Meshes.Where(value => value.Skin is not null))
{
    var matrices = mesh.Skin!.InverseBindMatrices
        .Select(binding => new[]
        {
            binding.Value * rowWorld[binding.Key],
            rowWorld[binding.Key] * binding.Value,
            binding.Value * columnWorld[binding.Key],
            columnWorld[binding.Key] * binding.Value,
        })
        .ToArray();
    for (var formula = 0; formula < 4; formula++)
    {
        var first = matrices[0][formula];
        bindFormulaSpreads[formula] = Math.Max(
            bindFormulaSpreads[formula],
            matrices.Skip(1)
                .Select(value => MatrixDeviation(first, value[formula]))
                .DefaultIfEmpty()
                .Max());
    }
}
var previewPositions = preview.Model.Meshes
    .SelectMany(mesh => mesh.Primitives)
    .SelectMany(primitive => primitive.VertexBuffers)
    .Where(buffer => buffer.Attributes.Any(attribute =>
        attribute.Semantic == VertexSemantic.Position
        && attribute.SourceFormat == "Float32x3"))
    .SelectMany(buffer => Enumerable.Range(0, buffer.VertexCount).Select(index =>
    {
        var offset = index * buffer.Stride
            + buffer.Attributes.First(attribute =>
                attribute.Semantic == VertexSemantic.Position).Offset;
        return new Vector3(
            BitConverter.ToSingle(buffer.Data, offset),
            BitConverter.ToSingle(buffer.Data, offset + 4),
            BitConverter.ToSingle(buffer.Data, offset + 8));
    }))
    .ToArray();
var previewMinimum = new Vector3(
    previewPositions.Min(value => value.X),
    previewPositions.Min(value => value.Y),
    previewPositions.Min(value => value.Z));
var previewMaximum = new Vector3(
    previewPositions.Max(value => value.X),
    previewPositions.Max(value => value.Y),
    previewPositions.Max(value => value.Z));
Console.WriteLine(
    $"preview_meshes={preview.Model.Meshes.Count};"
    + $" preview_joints={preview.Model.Skeleton?.Joints.Count ?? 0};"
    + $" preview_animations={preview.Animations.Count};"
    + $" preview_diagnostics={preview.Diagnostics.Count};"
    + $" bind_max_translation={bindTranslation:G6};"
    + $" bind_identity_deviation={bindIdentityDeviation:G6};"
    + $" bind_mesh_spread={bindMeshSpread:G6};"
    + $" formula_spreads={string.Join(",", bindFormulaSpreads.Select(value => value.ToString("G6")))};"
    + $" bounds={previewMinimum}..{previewMaximum}");
foreach (var texture in scene.Textures)
    Console.WriteLine(
        $"texture|{texture.Name}|{texture.MediaType}|{texture.EncodedData.Length}|"
        + $"{texture.SourcePath ?? "<embedded>"}");
foreach (var material in scene.Materials)
    Console.WriteLine(
        $"material|{material.Name}|base={material.BaseColor}|opacity={material.Opacity:G6}|"
        + $"textures={string.Join(",", material.TextureBindings.Select(value => $"{value.Key}:{value.Value}"))}");
foreach (var pair in scene.Nodes
             .Select((node, index) => (Node: node, Index: index))
             .Where(value => value.Node.MeshIndices.Count > 0))
{
    Console.WriteLine(
        $"mesh-node|{pair.Index}|{pair.Node.Name}|parent={pair.Node.ParentIndex}|"
        + $"meshes={string.Join(",", pair.Node.MeshIndices)}|"
        + $"translation={pair.Node.LocalTransform.M41:G6},"
        + $"{pair.Node.LocalTransform.M42:G6},{pair.Node.LocalTransform.M43:G6}");
}
foreach (var diagnostic in scene.Diagnostics)
    Console.WriteLine(
        $"{diagnostic.Severity}|{diagnostic.Code}|{diagnostic.Message}");
return scene.Diagnostics.Any(value =>
    value.Severity == ImportedDiagnosticSeverity.Error) ? 1 : 0;

static float MatrixDeviation(Matrix4x4 left, Matrix4x4 right)
{
    var values = new[]
    {
        left.M11 - right.M11, left.M12 - right.M12, left.M13 - right.M13, left.M14 - right.M14,
        left.M21 - right.M21, left.M22 - right.M22, left.M23 - right.M23, left.M24 - right.M24,
        left.M31 - right.M31, left.M32 - right.M32, left.M33 - right.M33, left.M34 - right.M34,
        left.M41 - right.M41, left.M42 - right.M42, left.M43 - right.M43, left.M44 - right.M44,
    };
    return values.Max(MathF.Abs);
}
