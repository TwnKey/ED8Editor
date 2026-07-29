using System.Numerics;

namespace ED8Editor.Models;

/// <summary>
/// The coordinate contract exposed by every importer. Assimp normalizes source
/// formats to this basis; keeping the contract explicit prevents a later writer
/// from applying an FBX- or COLLADA-specific conversion a second time.
/// </summary>
public sealed record ImportedCoordinateSystem(
    bool RightHanded,
    ImportedUpAxis UpAxis,
    float UnitScaleMeters,
    float SourceUnitScaleMeters = 1f);

public enum ImportedUpAxis
{
    X,
    Y,
    Z,
}

/// <summary>
/// A complete model package after format-specific parsing. Nothing in this type
/// mentions FBX, glTF, OBJ, COLLADA or Phyre.
/// </summary>
public sealed record ImportedModelScene(
    string Name,
    string SourcePath,
    ImportedCoordinateSystem CoordinateSystem,
    IReadOnlyList<ImportedSceneNode> Nodes,
    IReadOnlyList<ImportedMesh> Meshes,
    IReadOnlyList<ImportedMaterial> Materials,
    IReadOnlyList<ImportedTexture> Textures,
    IReadOnlyList<ImportedAnimationClip> Animations,
    IReadOnlyList<ImportedModelDiagnostic> Diagnostics)
{
    public bool IsSkinned => Meshes.Any(mesh => mesh.Skin is not null);
}

public sealed record ImportedSceneNode(
    string Name,
    int ParentIndex,
    Matrix4x4 LocalTransform,
    IReadOnlyList<int> MeshIndices);

public sealed record ImportedMesh(
    string Name,
    IReadOnlyList<ImportedVertex> Vertices,
    int[] Indices,
    int MaterialIndex,
    ImportedSkin? Skin);

public sealed record ImportedVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector3 Tangent,
    Vector3 Bitangent,
    IReadOnlyList<Vector2> TexCoords,
    IReadOnlyList<Vector4> Colors,
    IReadOnlyList<ImportedVertexInfluence> Influences);

/// <summary>
/// A joint reference and its original weight. Import keeps every influence;
/// target-format limits are enforced only by the corresponding exporter.
/// </summary>
public sealed record ImportedVertexInfluence(int NodeIndex, float Weight);

/// <summary>
/// Bind matrices are per mesh because exchange formats permit two skins to bind
/// the same scene node differently.
/// </summary>
public sealed record ImportedSkin(
    IReadOnlyDictionary<int, Matrix4x4> InverseBindMatrices);

public sealed record ImportedMaterial(
    string Name,
    Vector4 BaseColor,
    Vector3 EmissiveColor,
    float MetallicFactor,
    float RoughnessFactor,
    float Opacity,
    bool DoubleSided,
    IReadOnlyDictionary<ImportedTextureUsage, int> TextureBindings,
    IReadOnlyDictionary<string, string> SourceProperties);

public enum ImportedTextureUsage
{
    BaseColor,
    Normal,
    MetallicRoughness,
    Emissive,
    Occlusion,
    Opacity,
    Specular,
    Height,
    Unknown,
}

/// <summary>
/// Encoded source bytes are retained losslessly. Decoding/compression is a
/// separate target concern, and embedded FBX/glTF images use the same shape.
/// </summary>
public sealed record ImportedTexture(
    string Name,
    string? SourcePath,
    string MediaType,
    byte[] EncodedData,
    bool Embedded,
    string? SourceReference = null);

public sealed record ImportedAnimationClip(
    string Name,
    double DurationSeconds,
    IReadOnlyList<ImportedAnimationChannel> Channels);

public sealed record ImportedAnimationChannel(
    int NodeIndex,
    IReadOnlyList<ImportedVectorKey> TranslationKeys,
    IReadOnlyList<ImportedQuaternionKey> RotationKeys,
    IReadOnlyList<ImportedVectorKey> ScaleKeys);

public sealed record ImportedVectorKey(double TimeSeconds, Vector3 Value);

public sealed record ImportedQuaternionKey(double TimeSeconds, Quaternion Value);

public sealed record ImportedModelDiagnostic(
    ImportedDiagnosticSeverity Severity,
    string Code,
    string Message);

public enum ImportedDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}
