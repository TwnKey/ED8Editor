using System.Numerics;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// An image to write as a texture cluster. Pixels are straight RGBA rows, top
/// row first; the writer is what flips them, because that is a property of the
/// file and not of whoever hands the image over.
/// </summary>
public sealed record PhyreTextureSource(
    string AssetName,
    int Width,
    int Height,
    string Format,
    byte[] Rgba);

/// <summary>One vertex of a mesh being brought in.</summary>
/// <param name="Joints">Up to four skeleton joints this vertex follows.</param>
/// <param name="Weights">How much of each joint it follows, summing to one.</param>
public sealed record PhyreVertexSource(
    Vector3 Position,
    Vector3 Normal,
    Vector2 TexCoord,
    Vector4 Tangent,
    int[] Joints,
    float[] Weights);

/// <summary>
/// A run of triangles drawn with one material. The material is named, not
/// described: a mesh brought into Cold Steel binds a material the game already
/// compiled a shader for.
/// </summary>
public sealed record PhyreMeshSegmentSource(
    string MaterialName,
    int[] Indices);

/// <summary>A joint of the skeleton a mesh is skinned to.</summary>
public sealed record PhyreJointSource(
    string Name,
    int ParentIndex,
    Matrix4x4 LocalTransform,
    Matrix4x4 InverseBindTransform);

/// <summary>
/// A model to write as a model cluster: what an FBX import produces, stripped of
/// everything the format of the exchange file adds.
///
/// This is the boundary of the black box. Whoever reads the FBX fills this in;
/// nothing here knows about FBX, and nothing here knows about the editor.
/// </summary>
public sealed record PhyreModelSource(
    string AssetName,
    IReadOnlyList<PhyreVertexSource> Vertices,
    IReadOnlyList<PhyreMeshSegmentSource> Segments,
    IReadOnlyList<PhyreJointSource> Joints)
{
    /// <summary>Whether the model follows a skeleton.</summary>
    public bool IsSkinned => Joints.Count > 0;

    /// <summary>
    /// Reports what would stop this model from being written, so an importer can
    /// say so before anything is produced.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Vertices.Count == 0) problems.Add("the model has no vertices");
        if (Segments.Count == 0) problems.Add("the model has no triangles");
        foreach (var segment in Segments)
        {
            if (segment.Indices.Length % 3 != 0)
            {
                problems.Add($"segment '{segment.MaterialName}' has a partial triangle");
            }
            if (segment.Indices.Any(index => index < 0 || index >= Vertices.Count))
            {
                problems.Add($"segment '{segment.MaterialName}' points at a vertex that is not there");
            }
        }
        foreach (var vertex in Vertices)
        {
            if (vertex.Joints.Length != vertex.Weights.Length)
            {
                problems.Add("a vertex has more joints than weights");
                break;
            }
            if (vertex.Joints.Any(joint => joint < 0 || joint >= Joints.Count))
            {
                problems.Add("a vertex follows a joint the skeleton does not have");
                break;
            }
        }
        for (var index = 0; index < Joints.Count; index++)
        {
            var parent = Joints[index].ParentIndex;
            if (parent >= index)
            {
                problems.Add($"joint '{Joints[index].Name}' comes before its own parent");
            }
        }
        return problems;
    }
}
