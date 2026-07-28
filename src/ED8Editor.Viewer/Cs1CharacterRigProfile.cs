using ED8Editor.Core;

namespace ED8Editor.Viewer;

internal sealed record Cs1CharacterRigComparison(
    IReadOnlyList<string> MissingReferenceNodes,
    IReadOnlyList<string> AdditionalNodes,
    int SharedNodes);

/// <summary>
/// A compatibility profile derived from an explicitly selected CS1 reference
/// model. There is no hard-coded locator-name heuristic: switching reference
/// models updates the contract from real game data.
/// </summary>
internal sealed class Cs1CharacterRigProfile
{
    private readonly HashSet<string> referenceNodes;

    public Cs1CharacterRigProfile(string referenceAssetId, CpuModel reference)
    {
        ReferenceAssetId = referenceAssetId;
        referenceNodes = NodeNames(reference).ToHashSet(StringComparer.Ordinal);
    }

    public string ReferenceAssetId { get; }
    public int ReferenceNodeCount => referenceNodes.Count;

    public Cs1CharacterRigComparison Compare(CpuModel candidate)
    {
        var candidateNodes = NodeNames(candidate).ToHashSet(StringComparer.Ordinal);
        return new Cs1CharacterRigComparison(
            referenceNodes.Except(candidateNodes).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            candidateNodes.Except(referenceNodes).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            referenceNodes.Intersect(candidateNodes).Count());
    }

    private static IEnumerable<string> NodeNames(CpuModel model)
    {
        if (model.Skeleton is not null)
            foreach (var joint in model.Skeleton.Joints)
                if (!string.IsNullOrWhiteSpace(joint.Name)) yield return joint.Name;
        if (model.SceneNodes is not null)
            foreach (var node in model.SceneNodes)
                if (!string.IsNullOrWhiteSpace(node.Name)) yield return node.Name;
    }
}
