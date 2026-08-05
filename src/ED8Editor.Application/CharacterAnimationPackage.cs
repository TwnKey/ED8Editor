using ED8Editor.Core;
using ED8Editor.Packages;
using ED8Editor.Phyre.Authoring;

namespace ED8Editor.Application;

/// <summary>
/// Puts an imported animation into one of a character's own clip slots.
///
/// The game does not need a script edited to play a new animation. Its ANI logic
/// resolves clips generically, by asset id and slot name — <c>WAIT</c>,
/// <c>RUN</c>, <c>BTL_WAIT</c> — so an animation written under a slot the
/// character already declares is played wherever that slot is played. Which is
/// why this replaces the contents of an existing slot rather than inventing one:
/// a new symbol nothing calls would never be heard.
///
/// The same shape serves enemies. A character keeps its clips in its
/// <c>_DF1</c> package and an enemy in its own, and both name them
/// <c>&lt;asset&gt;_CLIP_&lt;slot&gt;</c>, so nothing here has to know which it
/// is looking at.
/// </summary>
public static class CharacterAnimationPackage
{
    private const string ClipInfix = "_CLIP_";

    /// <summary>One clip a character already declares, and where it lives.</summary>
    /// <param name="Slot">The name the game's animation logic calls it by.</param>
    public sealed record ClipSlot(
        string Symbol, string Slot, string PackagePath, string EntryName);

    /// <summary>
    /// Every clip slot an asset declares, across its own package and the field
    /// animation package beside it.
    /// </summary>
    public static IReadOnlyList<ClipSlot> Slots(
        IAssetPackageResolverFactory resolvers,
        IPackageArchiveReader archives,
        IAssetManifestReader manifests,
        string gameDataPath,
        string assetId)
    {
        ArgumentNullException.ThrowIfNull(resolvers);
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(assetId);

        var found = new List<ClipSlot>();
        var prefix = assetId + ClipInfix;
        foreach (var packageAssetId in new[] { assetId, assetId + "_DF1" })
        {
            var resolution = resolvers.Create(gameDataPath)
                .Resolve(packageAssetId, AssetVariantPreference.English);
            if (resolution.SelectedPackage is null) continue;

            IPackageArchive archive;
            AssetManifest manifest;
            try
            {
                archive = archives.Read(resolution.SelectedPackage.Path);
                manifest = manifests.Read(archive, packageAssetId);
            }
            catch (Exception exception) when (exception is IOException
                or InvalidDataException or ArgumentException)
            {
                continue;
            }

            foreach (var asset in manifest.Assets)
            {
                if (!asset.Symbol.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var resource = asset.Resources
                    .FirstOrDefault(value => value.Kind == AssetResourceKind.Model);
                if (resource is null) continue;
                found.Add(new ClipSlot(
                    asset.Symbol,
                    asset.Symbol[prefix.Length..],
                    resolution.SelectedPackage.Path,
                    resource.ArchiveEntryName));
            }
        }
        return found
            .DistinctBy(value => value.Symbol, StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value.Slot, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// The same animation, with every channel renamed to the bone the game calls
    /// it. A channel whose bone the mapping does not name is dropped rather than
    /// written against a name the skeleton has never heard of.
    /// </summary>
    public static CpuAnimationClip Retarget(
        CpuAnimationClip clip, IReadOnlyDictionary<string, string> mapping)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(mapping);
        var channels = clip.Channels
            .Where(channel => mapping.ContainsKey(channel.TargetName))
            .Select(channel => channel with { TargetName = mapping[channel.TargetName] })
            .ToArray();
        return clip with { Channels = channels };
    }

    /// <summary>
    /// Writes <paramref name="clip"/> into <paramref name="slot"/>, keeping the
    /// skeleton the slot's own clip describes.
    /// </summary>
    public static void Write(
        Action<string, bool> onSaving,
        IPackageArchiveReader archives,
        ClipSlot slot,
        CpuAnimationClip clip)
    {
        ArgumentNullException.ThrowIfNull(onSaving);
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(clip);

        var archive = archives.Read(slot.PackagePath);
        var entry = archive.Entries.FirstOrDefault(value =>
            value.Name.Equals(slot.EntryName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"'{slot.PackagePath}' has no entry called '{slot.EntryName}'.");

        var written = PhyreAnimationClipWriter.Write(archive.ReadEntry(entry), clip);
        var rebuilt = archive.Entries
            .Select(one => (
                one.Name,
                Data: one.Name.Equals(slot.EntryName, StringComparison.OrdinalIgnoreCase)
                    ? written
                    : archive.ReadEntry(one).ToArray()))
            .ToArray();

        onSaving(slot.PackagePath, true);
        new PkgArchiveWriter().Write(slot.PackagePath, archive.Magic, rebuilt);
        onSaving(slot.PackagePath, false);
    }

    /// <summary>
    /// Which imported animation looks like which slot, so the common case needs no
    /// choosing. Matched on the name alone, ignoring case, separators and any
    /// exporter's prefix — <c>Armature|run</c> answers to <c>RUN</c>.
    /// </summary>
    public static string? GuessSlot(string animationName, IEnumerable<string> slots)
    {
        ArgumentNullException.ThrowIfNull(animationName);
        ArgumentNullException.ThrowIfNull(slots);
        var wanted = Simplify(animationName);
        if (wanted.Length == 0) return null;
        foreach (var slot in slots)
        {
            if (Simplify(slot).Equals(wanted, StringComparison.Ordinal)) return slot;
        }
        return null;
    }

    private static string Simplify(string name)
    {
        var bar = name.LastIndexOf('|');
        var text = bar < 0 ? name : name[(bar + 1)..];
        return new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}
