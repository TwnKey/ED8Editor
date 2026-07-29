using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// The order the engine sorts a list's fixups into before packing them: by the
/// source they start from, then by what they point at, then by which object
/// carries them.
///
/// This is not a detail of taste. Sorting by target is what puts fixups that
/// share a destination next to each other, and that is what lets a block hoist
/// the destination list out of its payloads and pack its objects by target.
/// Blocks are then simply runs of neighbours sharing a source.
/// </summary>
internal sealed class PhyreFixupOrder : IComparer<PhyreFixup>
{
    public static PhyreFixupOrder Instance { get; } = new();

    public int Compare(PhyreFixup? left, PhyreFixup? right)
    {
        if (left is null || right is null) return left is null ? (right is null ? 0 : -1) : 1;
        var bySource = CompareSource(left, right);
        if (bySource != 0) return bySource;
        var byTarget = CompareTarget(left, right);
        if (byTarget != 0) return byTarget;
        return left.SourceObjectId.CompareTo(right.SourceObjectId);
    }

    /// <summary>A member always comes before an offset; alike ones by their value.</summary>
    private static int CompareSource(PhyreFixup left, PhyreFixup right)
    {
        if (left.IsClassDataMember && right.IsClassDataMember)
        {
            return left.SourceMemberId.CompareTo(right.SourceMemberId);
        }
        if (left.IsClassDataMember) return -1;
        if (right.IsClassDataMember) return 1;
        return left.SourceOffset.CompareTo(right.SourceOffset);
    }

    private static int CompareTarget(PhyreFixup left, PhyreFixup right)
    {
        if (left is PhyrePointerFixup leftPointer && right is PhyrePointerFixup rightPointer)
        {
            var byObject = leftPointer.DestinationObjectId.CompareTo(rightPointer.DestinationObjectId);
            if (byObject != 0) return byObject;
            var byList = leftPointer.DestinationListIndex.CompareTo(rightPointer.DestinationListIndex);
            if (byList != 0) return byList;
            var byOffset = leftPointer.DestinationOffset.CompareTo(rightPointer.DestinationOffset);
            if (byOffset != 0) return byOffset;
            var byArray = leftPointer.ArrayIndex.CompareTo(rightPointer.ArrayIndex);
            if (byArray != 0) return byArray;
            // A fixup with no user fixup carries the largest value there is, so
            // it sorts after every fixup that names one.
            return (leftPointer.UserFixupId ?? uint.MaxValue)
                .CompareTo(rightPointer.UserFixupId ?? uint.MaxValue);
        }
        if (left is PhyreArrayFixup leftArray && right is PhyreArrayFixup rightArray)
        {
            var byOffset = leftArray.Offset.CompareTo(rightArray.Offset);
            return byOffset != 0 ? byOffset : leftArray.Count.CompareTo(rightArray.Count);
        }
        return 0;
    }
}

/// <summary>The engine's fixup order, for tools that need to see it.</summary>
public static class PhyreFixupOrderView
{
    public static IComparer<PhyreFixup> Instance => PhyreFixupOrder.Instance;
}
