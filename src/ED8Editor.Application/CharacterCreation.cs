using System.Text;
using ED8Editor.Packages;

namespace ED8Editor.Application;

/// <summary>
/// Makes a new character or enemy by standing one up from an existing one.
///
/// A character is not one file. Its model is a package, and beside it sit the
/// companions the game loads by name: the field animations, the battle set, the
/// event variants, the costumes. Which companions exist is not the same for a
/// character and an enemy — a character keeps its clips in a <c>_DF1</c> package
/// while an enemy keeps them in its own — and neither is a rule this has to know,
/// because both follow from one thing: every companion is a package whose name
/// begins with the asset's.
///
/// What does have to be understood is that a package NAMES itself inside. Its
/// manifest declares <c>symbol="C_NPC000"</c> for the model and
/// <c>symbol="C_NPC000_CLIP_WAIT"</c> for a clip, and the game looks those up by
/// the asset id it is asking for. Copy a package without rewriting them and the
/// new character's clips are still called the old one's, so nothing finds them —
/// which is precisely the difference between a copy that works and one that loads
/// a character with no animations at all.
/// </summary>
public static class CharacterCreation
{
    /// <summary>What a new asset was made of, and what it now owns.</summary>
    /// <param name="Written">Every package written, game-relative.</param>
    /// <param name="Symbols">How many manifest symbols were renamed onto the new id.</param>
    /// <param name="TablePath">The table it was named in, or null if it was not.</param>
    /// <param name="Key">The identifier the table gave it, where the table has one.</param>
    public sealed record Result(
        string AssetId,
        IReadOnlyList<string> Written,
        int Symbols,
        string? TablePath = null,
        string? Key = null);

    /// <summary>
    /// Every package that belongs to <paramref name="assetId"/>: the asset's own
    /// and each companion beside it. Matching on the name and a separator rather
    /// than the name alone, so <c>C_NPC000</c> does not claim <c>C_NPC0001</c>.
    /// </summary>
    public static IReadOnlyList<string> Companions(string gameDirectory, string assetId)
    {
        ArgumentNullException.ThrowIfNull(gameDirectory);
        ArgumentNullException.ThrowIfNull(assetId);
        var assets = Path.Combine(gameDirectory, "data", "asset", "D3D11");
        if (!Directory.Exists(assets)) return Array.Empty<string>();
        return Directory.EnumerateFiles(assets, assetId + "*.pkg")
            .Where(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                return name.Equals(assetId, StringComparison.OrdinalIgnoreCase)
                    || (name.Length > assetId.Length && name[assetId.Length] == '_');
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Copies <paramref name="sourceAssetId"/> and every companion to
    /// <paramref name="newAssetId"/>, renaming what each package calls itself.
    ///
    /// Nothing else is changed. The model, the clips, the textures and the shaders
    /// are the source's, which is the point: what comes out is a character the
    /// game can already load, and everything after this — its mesh, its
    /// animations — is an edit to something that works rather than a guess at
    /// something that never has.
    ///
    /// <paramref name="registerAsEnemy"/> decides which table names the result:
    /// t_mons for an enemy, t_name for a character. Null leaves it unnamed, which
    /// makes an asset that loads but that nothing in the game asks for.
    /// </summary>
    public static Result Create(
        ModProject project,
        string sourceAssetId,
        string newAssetId,
        Action<string>? say = null,
        bool? registerAsEnemy = null,
        Action<string, bool>? onSaving = null,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sourceAssetId);
        ArgumentNullException.ThrowIfNull(newAssetId);
        if (newAssetId.Equals(sourceAssetId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The new asset needs a name of its own.", nameof(newAssetId));
        }

        var sources = Companions(project.GameDirectory, sourceAssetId);
        if (sources.Count == 0)
        {
            throw new FileNotFoundException(
                $"There is no package called '{sourceAssetId}' to copy.", sourceAssetId);
        }

        var written = new List<string>();
        var symbols = 0;
        foreach (var source in sources)
        {
            var suffix = Path.GetFileNameWithoutExtension(source)[sourceAssetId.Length..];
            var target = Path.Combine(
                Path.GetDirectoryName(source)!, newAssetId + suffix + ".pkg");
            if (File.Exists(target))
            {
                throw new IOException($"'{Path.GetFileName(target)}' already exists.");
            }

            var archive = new PkgArchiveReader().Read(source);
            var rebuilt = archive.Entries
                .Select(entry =>
                {
                    var data = archive.ReadEntry(entry).ToArray();
                    if (!entry.Name.Equals("asset_D3D11.xml", StringComparison.OrdinalIgnoreCase))
                    {
                        return (entry.Name, Data: data);
                    }
                    var renamed = Rename(data, sourceAssetId, newAssetId, out var count);
                    symbols += count;
                    return (entry.Name, Data: renamed);
                })
                .ToArray();

            project.CaptureOriginal(target);
            new PkgArchiveWriter().Write(target, archive.Magic, rebuilt);
            project.TrackSave(target);
            written.Add(Path.GetRelativePath(project.GameDirectory, target));
            say?.Invoke($"{Path.GetFileName(source)} -> {Path.GetFileName(target)}");
        }

        // Naming it in the table belongs to the same action, not to a second
        // button: an asset whose packages exist and whose row does not is a
        // half-made thing, and nothing tells its author which half they have.
        // If the row cannot be written, the packages go back, so a failed
        // creation leaves nothing behind rather than that same half.
        if (registerAsEnemy is not { } enemy)
        {
            return new Result(newAssetId, written, symbols);
        }
        try
        {
            var row = CharacterTableRegistration.Add(
                onSaving ?? ((_, _) => { }),
                Path.Combine(project.GameDirectory, "data"),
                enemy,
                sourceAssetId,
                newAssetId,
                displayName);
            say?.Invoke($"{Path.GetFileName(row.TablePath)} row {row.RowIndex}");
            return new Result(newAssetId, written, symbols, row.TablePath, row.Key);
        }
        catch (Exception)
        {
            foreach (var relative in written)
            {
                var path = Path.Combine(project.GameDirectory, relative);
                try
                {
                    project.Remove(relative);
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (IOException)
                {
                    // A file that will not go is worth less than the error that
                    // says why the creation failed, which is about to be thrown.
                }
            }
            throw;
        }
    }

    /// <summary>
    /// Renames every symbol the manifest declares, and nothing else.
    ///
    /// Only a symbol that IS the old asset id, or begins with it followed by a
    /// separator, is renamed — a blind text replacement would also rewrite the
    /// cluster paths, which name files that still exist under their own names.
    /// </summary>
    private static byte[] Rename(byte[] manifest, string oldId, string newId, out int renamed)
    {
        var text = new UTF8Encoding(false).GetString(manifest);
        var count = 0;
        var rewritten = System.Text.RegularExpressions.Regex.Replace(
            text,
            "symbol=\"([^\"]*)\"",
            match =>
            {
                var symbol = match.Groups[1].Value;
                if (!symbol.StartsWith(oldId, StringComparison.OrdinalIgnoreCase))
                {
                    return match.Value;
                }
                if (symbol.Length != oldId.Length && symbol[oldId.Length] != '_')
                {
                    return match.Value;
                }
                count++;
                return $"symbol=\"{newId}{symbol[oldId.Length..]}\"";
            });
        renamed = count;
        return new UTF8Encoding(false).GetBytes(rewritten);
    }
}
