using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>What a check found, one line per cluster that failed.</summary>
public sealed record PhyreAuthoringReport(
    int Checked,
    int SectionFailures,
    int NamespaceFailures,
    int SchemaChecked,
    int SchemaFailures,
    int FixupChecked,
    int FixupFailures,
    int FixupSkipped,
    int ClusterChecked,
    int ClusterFailures,
    int RebuiltChecked,
    int RebuiltFailures,
    IReadOnlyList<string> Failures)
{
    public bool Passed => SectionFailures == 0
        && NamespaceFailures == 0
        && SchemaFailures == 0
        && FixupFailures == 0
        && ClusterFailures == 0
        && RebuiltFailures == 0;

    public override string ToString()
        => (Passed ? "PASS " : "FAIL ")
            + $"{Checked} clusters: {Checked - SectionFailures} cut into sections that tile exactly,"
            + $" {Checked - NamespaceFailures} namespaces re-emitted byte for byte,"
            + $" {SchemaChecked - SchemaFailures} of {SchemaChecked} built from the schema in code,"
            + $" {FixupChecked - FixupFailures} of {FixupChecked} fixup tables re-encoded"
            + (FixupSkipped == 0 ? ", " : $" ({FixupSkipped} need the tighter packings), ")
            + $" {ClusterChecked - ClusterFailures} of {ClusterChecked} written whole from the image,"
            + $" {RebuiltChecked - RebuiltFailures} of {RebuiltChecked} rebuilt from what they hold";
}

/// <summary>
/// Proves the pieces of the writer against the files the game ships.
///
/// The rule is the same as everywhere else in this project: a piece of the
/// writer is finished when what it produces from data is, byte for byte, what
/// Falcom shipped. Until then it says how far off it is.
/// </summary>
public static class PhyreAuthoringCheck
{
    /// <summary>
    /// Cuts each cluster into sections, puts it back together, and re-emits its
    /// packed namespace from the parsed schema.
    /// </summary>
    public static PhyreAuthoringReport Run(IEnumerable<(string Name, byte[] Cluster)> clusters)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        var failures = new List<string>();
        var checkedCount = 0;
        var sectionFailures = 0;
        var namespaceFailures = 0;
        var schemaChecked = 0;
        var schemaFailures = 0;
        var fixupChecked = 0;
        var fixupFailures = 0;
        var clusterChecked = 0;
        var clusterFailures = 0;
        var rebuiltChecked = 0;
        var rebuiltFailures = 0;
        var fixupSkipped = 0;

        foreach (var (name, cluster) in clusters)
        {
            checkedCount++;
            PhyreClusterSections sections;
            try
            {
                sections = PhyreClusterSectionReader.Read(cluster);
            }
            catch (Exception exception) when (exception is InvalidPhyreException
                or InvalidDataException or ArgumentException or OverflowException)
            {
                sectionFailures++;
                namespaceFailures++;
                failures.Add($"{name}: could not be cut into sections — {exception.Message}");
                continue;
            }

            if (!sections.Compose().AsSpan().SequenceEqual(cluster))
            {
                sectionFailures++;
                failures.Add($"{name}: the sections do not put the cluster back together");
            }

            // The schema written as code has to produce the shipped namespace on
            // its own, without the file being open — that is what says a texture
            // can be written with nothing borrowed from the game.
            if (sections.Metadata.Classes.Count == PhyreTextureSchema.Classes.Count
                && sections.Metadata.Types.SequenceEqual(PhyreTextureSchema.TypeNames))
            {
                var fromSchema = PhyreNamespaceWriter.Write(
                    PhyreTextureSchema.TypeNames,
                    PhyreTextureSchema.Classes,
                    PhyreTextureSchema.Header);
                schemaChecked++;
                if (!fromSchema.AsSpan().SequenceEqual(sections.PackedNamespace.Span))
                {
                    schemaFailures++;
                    failures.Add(
                        $"{name}: the namespace built from the schema in code differs"
                        + Difference(sections.PackedNamespace.Span, fromSchema));
                }
            }

            // The fixup tables, re-encoded from what was decoded out of them.
            var fixups = new PhyreFixupReader().Read(cluster, sections.Metadata);
            // Only the plainest packing is written, which is the one the game's
            // own writer uses when a group holds a single object. A group with
            // several objects is packed tighter over there — grouped, bitmasked
            // or strided — so those tables are left out of the comparison rather
            // than counted as wrong: what this writer produces is readable, just
            // longer.
            fixupChecked++;
            var pointerBytes = PhyreFixupWriter.WritePointers(fixups.Pointers, sections.Metadata.InstanceGroups);
            var arrayBytes = PhyreFixupWriter.WriteArrays(fixups.Arrays, sections.Metadata.InstanceGroups);
            if (!pointerBytes.AsSpan().SequenceEqual(sections.PointerFixups.Span))
            {
                fixupFailures++;
                failures.Add(
                    $"{name}: the pointer fixups came out {pointerBytes.Length} bytes against"
                    + $" {sections.PointerFixups.Length}"
                    + Difference(sections.PointerFixups.Span, pointerBytes));
            }
            else if (!arrayBytes.AsSpan().SequenceEqual(sections.ArrayFixups.Span))
            {
                fixupFailures++;
                failures.Add(
                    $"{name}: the array fixups came out {arrayBytes.Length} bytes against"
                    + $" {sections.ArrayFixups.Length}"
                    + Difference(sections.ArrayFixups.Span, arrayBytes));
            }

            // The whole cluster, written from the image alone. Nothing of the
            // file being compared against is used except what an author would
            // have supplied: the path, the size, the format and the pixels.
            if (schemaChecked > 0 && sections.Metadata.InstanceGroups.Count == 2)
            {
                clusterChecked++;
                var source = new PhyreD3D11TextureReader().Read("check", cluster);
                var reference = sections.Metadata.Classes
                    .First(value => value.Name == "PAssetReference");
                var pathOffset = (int)sections.Metadata.Header.ObjectDataOffset + (int)reference.Size;
                var pathLength = cluster.AsSpan(pathOffset).IndexOf((byte)0);
                var written = PhyreTextureClusterWriter.Write(
                    System.Text.Encoding.ASCII.GetString(cluster, pathOffset, pathLength),
                    source.Width,
                    source.Height,
                    source.Format,
                    source.MipCount,
                    source.Data);
                if (!written.AsSpan().SequenceEqual(cluster))
                {
                    clusterFailures++;
                    failures.Add(
                        $"{name}: the cluster written from the image came out {written.Length} bytes"
                        + $" against {cluster.Length}"
                        + Difference(cluster, written));
                }
            }

            // The whole cluster, rebuilt from what it holds rather than from its
            // bytes — every section but the objects themselves.
            rebuiltChecked++;
            var rebuilt = PhyreClusterWriter.Rebuild(cluster);
            if (!rebuilt.AsSpan().SequenceEqual(cluster))
            {
                rebuiltFailures++;
                failures.Add(
                    $"{name}: rebuilding it came out {rebuilt.Length} bytes against {cluster.Length}"
                    + Difference(cluster, rebuilt));
            }

            var rewritten = PhyreNamespaceWriter.Write(
                sections.Metadata.Types,
                sections.Metadata.Classes,
                PhyreNamespaceWriter.ReadUnmodelledHeader(sections.PackedNamespace));
            if (rewritten.AsSpan().SequenceEqual(sections.PackedNamespace.Span)) continue;
            namespaceFailures++;
            failures.Add(
                $"{name}: the namespace came out {rewritten.Length} bytes against"
                + $" {sections.PackedNamespace.Length}"
                + Difference(sections.PackedNamespace.Span, rewritten));
        }

        return new PhyreAuthoringReport(
            checkedCount, sectionFailures, namespaceFailures, schemaChecked, schemaFailures,
            fixupChecked, fixupFailures, fixupSkipped, clusterChecked, clusterFailures,
            rebuiltChecked, rebuiltFailures, failures);
    }

    private static string Difference(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        var length = Math.Min(expected.Length, actual.Length);
        for (var index = 0; index < length; index++)
        {
            if (expected[index] == actual[index]) continue;
            // Both sides around the difference, which is what says whether it is
            // a shape, an object list or a payload that went wrong.
            var from = Math.Max(0, index - 8);
            var window = Math.Min(24, length - from);
            return $", first difference at 0x{index:X} ({expected[index]:X2} became {actual[index]:X2})"
                + $" shipped {Convert.ToHexString(expected.Slice(from, window))}"
                + $" written {Convert.ToHexString(actual.Slice(from, window))}";
        }
        return string.Empty;
    }
}
