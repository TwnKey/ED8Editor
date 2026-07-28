namespace ED8Editor.Viewer;

/// <summary>
/// Layout operations that are only valid after WinForms has assigned the real
/// client size. SplitContainer rejects otherwise valid distances while it still
/// has its small designer/default size.
/// </summary>
internal static class WinFormsLayout
{
    public static void SetInitialSplitterDistance(
        SplitContainer split,
        int preferredDistance)
    {
        ArgumentNullException.ThrowIfNull(split);
        if (preferredDistance < 0)
            throw new ArgumentOutOfRangeException(nameof(preferredDistance));

        var applied = false;
        split.HandleCreated += (_, _) =>
        {
            if (!split.IsHandleCreated || split.IsDisposed) return;
            split.BeginInvoke(TryApply);
        };
        split.VisibleChanged += (_, _) =>
        {
            if (split.Visible) TryApply();
        };

        void TryApply()
        {
            if (applied || split.IsDisposed) return;
            var extent = split.Orientation == Orientation.Vertical
                ? split.ClientSize.Width
                : split.ClientSize.Height;
            var minimum = split.Panel1MinSize;
            var maximum = extent - split.SplitterWidth - split.Panel2MinSize;
            if (maximum < minimum) return;
            split.SplitterDistance = Math.Clamp(preferredDistance, minimum, maximum);
            applied = true;
        }
    }
}
