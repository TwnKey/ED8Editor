using ED8Editor.Core;
using ED8Editor.Packages;
using ED8Editor.Phyre;
using ED8Editor.Phyre.Authoring;

namespace ED8Editor.Application;

/// <summary>One compiled effect the game ships, and what it was built with.</summary>
/// <param name="Hash">
/// What tells one variant of a source from another. It is not a checksum anything
/// here computes: the asset is named that way and the name is the identity.
/// </param>
/// <param name="Switches">
/// The switches the variant was compiled with, in clear. A variant IS its switch
/// set — that is the whole difference between two builds of the same source — so
/// showing them is showing what the shader does.
/// </param>
public sealed record ShaderVariant(
    string AssetName,
    string Source,
    string Hash,
    string PackagePath,
    string EntryName,
    IReadOnlyList<string> Switches)
{
    public string Label => Switches.Count == 0
        ? AssetName
        : $"{Hash[..Math.Min(8, Hash.Length)]} — {string.Join(", ", Switches)}";
}

/// <summary>A parameter a material fills in, and what it is.</summary>
/// <param name="Settable">
/// Whether a material is what supplies it. The rest are fed by the engine — the
/// world matrices, the light being drawn with — and offering those for editing
/// would offer a value that is overwritten before it is ever read.
/// </param>
public sealed record ShaderParameter(
    string Name, byte Semantic, byte DataType, int Components, bool Settable)
{
    /// <summary>What kind of value it holds, said plainly.</summary>
    public string Kind => DataType switch
    {
        0 => "float",
        1 => "vector 2",
        2 => "vector 3",
        3 => "vector 4",
        8 => "integer",
        49 => "matrix",
        52 => "texture",
        _ => $"type {DataType}",
    };
}

/// <summary>
/// Every effect variant the game ships, so a material can be pointed at one
/// deliberately rather than by borrowing whatever another asset happened to use.
///
/// The catalogue is the packages themselves. A variant is a file named
/// <c>&lt;source&gt;.fx#&lt;hash&gt;.phyre</c>, it carries the switches it was
/// compiled with as plain strings, and it declares every parameter it takes — so
/// nothing here has to be told what any of them are.
///
/// Reading a package's entry table does not read its contents, so listing every
/// variant in the game costs a directory walk rather than a decompression.
/// </summary>
public static class ShaderVariantCatalog
{
    /// <summary>
    /// Every variant the game holds, one entry per distinct asset name. The same
    /// variant is shipped inside every package that uses it, and they are the
    /// same file — so the first one found answers for all of them.
    /// </summary>
    /// <param name="withSwitches">
    /// Whether to open every variant to read what it was compiled with. Listing
    /// them is a directory walk; reading their switches is 2422 clusters, which is
    /// seconds rather than milliseconds — so it is asked for rather than assumed,
    /// and a caller that only needs the list does not pay for it.
    /// </param>
    /// <param name="progress">Called with how many have been read, and of how many.</param>
    public static IReadOnlyList<ShaderVariant> Load(
        string gameDirectory,
        bool withSwitches = false,
        Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(gameDirectory);
        // The loose folder first: a package there is the one the game loads, so its
        // shaders are the ones a material would be pointed at.
        var assets = GameContentDirectories.Assets(gameDirectory);
        if (assets.Count == 0) return Array.Empty<ShaderVariant>();

        var reader = new PkgArchiveReader();
        var found = new Dictionary<string, ShaderVariant>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in GameContentDirectories.Files(assets, "*.pkg"))
        {
            IPackageArchive archive;
            try
            {
                archive = reader.Read(package);
            }
            catch (Exception exception) when (exception is IOException
                or InvalidPackageException or InvalidDataException)
            {
                continue;
            }

            foreach (var entry in archive.Entries)
            {
                if (!entry.Name.EndsWith(".phyre", StringComparison.OrdinalIgnoreCase)) continue;
                var hash = entry.Name.IndexOf(".fx#", StringComparison.OrdinalIgnoreCase);
                if (hash < 0) continue;
                var asset = entry.Name[..^".phyre".Length];
                if (found.ContainsKey(asset)) continue;
                found[asset] = new ShaderVariant(
                    asset,
                    entry.Name[..(hash + 3)],
                    asset[(hash + 4)..],
                    package,
                    entry.Name,
                    Array.Empty<string>());
            }
        }

        var listed = found.Values
            .OrderBy(variant => variant.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(variant => variant.AssetName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!withSwitches) return listed;

        // The switches, read from each variant once its file is known. Kept out of
        // the walk above so listing stays a directory scan and this is the only
        // step that opens anything.
        for (var index = 0; index < listed.Length; index++)
        {
            listed[index] = listed[index] with { Switches = Switches(listed[index]) };
            progress?.Invoke(index + 1, listed.Length);
        }
        return listed
            .OrderBy(variant => variant.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(variant => string.Join(",", variant.Switches), StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The switches a variant declares, in clear, from its own objects.</summary>
    public static IReadOnlyList<string> Switches(ShaderVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        try
        {
            var cluster = Cluster(variant);
            var data = new PhyreClusterReader().Read(cluster);
            var group = data.Metadata.InstanceGroups
                .FirstOrDefault(value => value.ClassName == "PMaterialSwitch");
            if (group is null || group.Count == 0) return Array.Empty<string>();

            var classes = data.Metadata.Classes.ToList();
            var nameAt = PhyreObjectWriter
                .Chain(classes.First(value => value.Name == "PMaterialSwitch"), classes)
                .First(value => value.Name == "m_name").ValueOffset;

            var names = new List<string>();
            for (var id = 0u; id < group.Count; id++)
            {
                var fixup = data.Fixups.Arrays.FirstOrDefault(value =>
                    value.SourceListIndex == group.Index && value.SourceObjectId == id
                    && (value.SourceOffsetOrMember & 0x7fffffffu) == nameAt);
                if (fixup is null) continue;
                var span = data.GetArrayData(
                    group.Index, fixup.Offset, group.ArraysSize - fixup.Offset).Span;
                var zero = span.IndexOf((byte)0);
                if (zero <= 0) continue;
                names.Add(System.Text.Encoding.ASCII.GetString(span[..zero]));
            }
            return names.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidPhyreException or ArgumentException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Every parameter the variant declares, and whether a material is what fills
    /// it. Read from the effect's own definitions, so it is what this shader takes
    /// rather than what shaders usually take.
    /// </summary>
    public static IReadOnlyList<ShaderParameter> Parameters(ShaderVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        try
        {
            var declared = PhyreEffectParameters.Read(Cluster(variant));

            return declared.Values
                .Select(one => new ShaderParameter(
                    one.Name,
                    one.Semantic,
                    one.DataType,
                    Components(one.DataType),
                    // The material block fills the material's own: an arbitrary
                    // constant, a colour, a texture or a sampler. Everything else
                    // is the engine's to supply.
                    one.Semantic is 64 or 65 or 66 or 67 or 68 or 71))
                .OrderByDescending(one => one.Settable)
                .ThenBy(one => one.Name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException or InvalidPhyreException or ArgumentException)
        {
            return Array.Empty<ShaderParameter>();
        }
    }

    /// <summary>How many numbers a value of this kind is made of.</summary>
    public static int Components(byte dataType) => dataType switch
    {
        0 or 8 => 1,
        1 => 2,
        2 => 3,
        3 => 4,
        49 => 16,
        _ => 0,
    };

    /// <summary>
    /// The compiled effect itself. A package binding a shader has to carry it, so
    /// choosing one of the game's variants means bringing its file along rather
    /// than hoping a neighbouring package still holds it.
    /// </summary>
    public static byte[] Cluster(ShaderVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        var archive = new PkgArchiveReader().Read(variant.PackagePath);
        var entry = archive.Entries.First(value =>
            value.Name.Equals(variant.EntryName, StringComparison.OrdinalIgnoreCase));
        return archive.ReadEntry(entry).ToArray();
    }
}
