using ED8Editor.Application;
using ED8Editor.Core;
using ED8Editor.Packages;
using ED8Editor.Phyre;
using ED8Editor.Phyre.Authoring;
using ED8Editor.Tables;

namespace ED8Editor.Viewer;

/// <summary>What a piece of equipment is made of, read from its own package.</summary>
/// <param name="Model">The cluster the package carries, or empty if it has none.</param>
/// <param name="Textures">Its images, by their entry names.</param>
/// <param name="Shaders">The effect variants it brings with it.</param>
internal sealed record EquipmentContents(
    string Model,
    IReadOnlyList<string> Textures,
    IReadOnlyList<string> Shaders);

/// <summary>Which character wears a piece of equipment, and where on them.</summary>
internal sealed record EquipmentUse(int Character, int Slot, int ItemId, string AttachPoint)
{
    /// <summary>
    /// What the slot number means. Read off the table rather than guessed: 0 hangs a
    /// weapon on an arm point, 5 is an outfit, 6 is eyewear.
    /// </summary>
    public string Kind => Slot switch
    {
        0 => "arme",
        5 => "tenue",
        6 => "lunettes",
        _ => $"emplacement {Slot}",
    };

    /// <summary>
    /// Who wears it. Every character the table names is a small number except
    /// <c>0xFFFD</c>, which stands on eleven downloadable rows; what it stands for is
    /// not established, so it is shown as it is written rather than guessed at.
    /// </summary>
    public string Wearer => Character >= 0xFFF0 ? $"0x{Character:X4}" : Character.ToString();
}

/// <summary>
/// Every piece of equipment the game carries as a model, with what uses it.
///
/// The list is the packages themselves — <c>C_EQU*.pkg</c> — rather than a table,
/// because a package is what holds the model, its textures and its shaders, and it
/// is what an editor replaces. The attach table then says who wears each one; a
/// package nothing references is still listed, since a mod may be the first thing
/// to use it.
///
/// None of the game's 194 equipment models is skinned: each is a rigid model hung
/// from a locator on the character. That is why replacing one is within reach of the
/// static model writer, where a character body would not be.
/// </summary>
internal sealed record EquipmentEntry(
    string Asset,
    string PackagePath,
    long Size,
    IReadOnlyList<EquipmentUse> Uses)
{
    public string Label
    {
        get
        {
            if (Uses.Count == 0) return $"{Asset}  (personne)";
            var first = Uses[0];
            var more = Uses.Count > 1 ? $" +{Uses.Count - 1}" : string.Empty;
            return $"{Asset}  {first.Kind}, personnage {first.Wearer}"
                + $" sur {first.AttachPoint}{more}";
        }
    }
}

internal static class EquipmentCatalog
{
    /// <summary>Where the game keeps its assets, given the folder holding data.</summary>
    /// <summary>
    /// Where equipment packages are written and read. The loose folder when the game
    /// has one: a package there is the one it loads.
    /// </summary>
    public static string AssetDirectory(string gameDirectory)
        => GameContentDirectories.Assets(gameDirectory).FirstOrDefault()
            ?? Path.Combine(gameDirectory, "data", "asset", "D3D11");

    /// <summary>
    /// What the game models as equipment: every package the attach table names, and
    /// every equipment package besides.
    ///
    /// Both halves are needed. The table names three families — the weapons
    /// (<c>C_EQU</c>), the outfits, which are the character's own body package
    /// (<c>C_PLY</c>), and the downloadable costumes (<c>DLC</c>) — so listing only
    /// the equipment packages would leave out every outfit. And the equipment
    /// packages outnumber what the table uses, so listing only what is worn would
    /// hide the ones a mod is free to take over.
    /// </summary>
    public static IReadOnlyList<EquipmentEntry> Load(string gameDirectory)
    {
        var assets = AssetDirectory(gameDirectory);
        if (!Directory.Exists(assets)) return Array.Empty<EquipmentEntry>();

        var uses = Uses(gameDirectory);
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in GameContentDirectories.Files(
                     GameContentDirectories.Assets(gameDirectory), "C_EQU*.pkg"))
        {
            packages[Path.GetFileNameWithoutExtension(path)] = path;
        }
        foreach (var named in uses.Keys)
        {
            if (packages.ContainsKey(named)) continue;
            var path = GameContentDirectories.Resolve(
                gameDirectory, Path.Combine("data", "asset", "D3D11", named + ".pkg"));
            if (File.Exists(path)) packages[named] = path;
        }

        return packages
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new EquipmentEntry(
                pair.Key,
                pair.Value,
                new FileInfo(pair.Value).Length,
                uses.TryGetValue(pair.Key, out var found)
                    ? found
                    : Array.Empty<EquipmentUse>()))
            .ToArray();
    }

    /// <summary>What the attach table says about each equipment asset.</summary>
    private static Dictionary<string, List<EquipmentUse>> Uses(string gameDirectory)
    {
        var found = new Dictionary<string, List<EquipmentUse>>(StringComparer.OrdinalIgnoreCase);
        var path = new[] { "dat_us", "dat" }
            .Select(folder => Path.Combine(gameDirectory, "data", "text", folder, "t_attach.tbl"))
            .Concat(new[] { Path.Combine(gameDirectory, "data", "text", "dat", "t_attach.tbl") })
            .FirstOrDefault(File.Exists);
        if (path is null) return found;

        Cs1AttachTable table;
        try
        {
            table = Cs1AttachTable.Read(path);
        }
        catch (Exception)
        {
            // A table we cannot read costs the list its cross-references, not the
            // list itself: the packages are still there to edit.
            return found;
        }

        foreach (var one in table.Attachments)
        {
            // Rows naming no model only reserve an attach point.
            if (one.Model.Length == 0 || one.Model.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!found.TryGetValue(one.Model, out var list))
            {
                list = new List<EquipmentUse>();
                found[one.Model] = list;
            }
            list.Add(new EquipmentUse(one.Character, one.Slot, one.ItemId, one.AttachPoint));
        }
        return found;
    }

    /// <summary>
    /// The asset path a package's model answers to, as the package itself states it:
    /// <c>chr/equip/equ300</c> and <c>equ300_r</c> for the sword in Rean's right hand.
    ///
    /// Read rather than derived. The folder drops the hand suffix the model keeps, and
    /// letters appear on some names and not others, so a rule guessed from the package
    /// name would be wrong often enough to matter — and a model written under the wrong
    /// path is a model nothing finds.
    /// </summary>
    public static (string Folder, string Name)? AssetPaths(string packagePath)
    {
        try
        {
            var package = new PkgArchiveReader().Read(packagePath);
            var model = package.Entries.FirstOrDefault(entry =>
                entry.Name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase));
            if (model is null) return null;
            var cluster = new PhyreClusterReader().Read(package.ReadEntry(model));
            foreach (var reference in AssetReferences(cluster))
            {
                var cut = reference.IndexOf('#');
                if (cut <= 0) continue;
                var path = reference[..cut];
                if (!path.EndsWith(".dae", StringComparison.OrdinalIgnoreCase)) continue;
                var folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
                var name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(folder) || name.Length == 0) continue;
                return (folder, name);
            }
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Every name a cluster's asset references carry. The name is not in the object:
    /// it is an array fixup on <c>m_id</c>, which the file writes either as a member
    /// index or as a raw offset, so both forms are accepted.
    /// </summary>
    private static IEnumerable<string> AssetReferences(PhyreClusterData data)
    {
        var group = data.Metadata.InstanceGroups.FirstOrDefault(value =>
            value.ClassName == "PAssetReference" && value.Count != 0);
        if (group is null) yield break;
        var descriptor = data.Metadata.Classes.First(value => value.Name == "PAssetReference");
        var id = PhyreObjectWriter.Chain(descriptor, data.Metadata.Classes)
            .First(value => value.Name == "m_id");
        for (var objectId = 0u; objectId < group.Count; objectId++)
        {
            var fixup = data.Fixups.Arrays.FirstOrDefault(value =>
                value.SourceListIndex == group.Index
                && value.SourceObjectId == objectId
                && (value.SourceOffsetOrMember == (uint)id.Index
                    || (value.SourceOffsetOrMember & 0x7fffffffu) == id.ValueOffset));
            if (fixup is null) continue;
            var available = checked((int)(group.ArraysSize - fixup.Offset));
            var bytes = data.GetArrayData(group.Index, fixup.Offset, (uint)available).Span;
            var end = bytes.IndexOf((byte)0);
            if (end >= 0) bytes = bytes[..end];
            yield return System.Text.Encoding.ASCII.GetString(bytes);
        }
    }

    /// <summary>What a package holds, sorted into the three kinds that matter.</summary>
    public static EquipmentContents Contents(string packagePath)
    {
        var package = new PkgArchiveReader().Read(packagePath);
        var names = package.Entries.Select(entry => entry.Name).ToList();
        return new EquipmentContents(
            names.FirstOrDefault(name =>
                name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase)) ?? string.Empty,
            names.Where(name =>
                name.EndsWith(".dds.phyre", StringComparison.OrdinalIgnoreCase)).ToArray(),
            names.Where(name =>
                name.Contains(".fx#", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".fx.phyre", StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    /// <summary>
    /// Each material of the equipment's model, with the shader and the textures it
    /// binds. This is what a custom shader replaces, so it is what the editor shows.
    /// </summary>
    public static IReadOnlyList<(string Material, string Shader, IReadOnlyList<string> Textures)>
        Materials(string packagePath)
    {
        try
        {
            var package = new PkgArchiveReader().Read(packagePath);
            var model = package.Entries.FirstOrDefault(entry =>
                entry.Name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase));
            if (model is null)
            {
                return Array.Empty<(string, string, IReadOnlyList<string>)>();
            }
            return PhyreMaterialTableReader.ReadAll(package.ReadEntry(model))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (
                    pair.Key,
                    pair.Value.ShaderAsset,
                    (IReadOnlyList<string>)pair.Value.Imports
                        .Where(import => import.Asset.Contains(
                            "images", StringComparison.OrdinalIgnoreCase)
                            || import.Asset.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                        .Select(import => import.Asset)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()))
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<(string, string, IReadOnlyList<string>)>();
        }
    }
}
