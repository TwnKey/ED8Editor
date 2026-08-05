using System.Text;
using ED8Editor.Packages;

namespace ED8Editor.Application;

/// <summary>
/// Puts an effect into a package that has to carry it, and says so in the manifest.
///
/// A material naming a shader its own package does not hold is bound to whatever
/// else the game happens to have loaded — it resolves in the map it was tested in
/// and nowhere else. So a shader assignment is two things at once: the material
/// pointing at a name, and the file that name resolves to travelling with it.
///
/// The manifest is what makes the file reachable. A package lists its clusters, and
/// one that is present but unlisted is a file the loader never opens.
/// </summary>
public static class AuthoredShaderPackage
{
    /// <summary>
    /// The package's entries with these effects in place, and its manifest listing
    /// them. The archive is returned rather than written: what changes a package on
    /// disk is one write, with the model and its shaders in the same rebuild.
    /// </summary>
    public static IReadOnlyList<(string Name, byte[] Data)> With(
        string packagePath,
        IReadOnlyList<(string Name, byte[] Data)> effects,
        Func<string, byte[]>? replaceModel = null)
    {
        ArgumentNullException.ThrowIfNull(packagePath);
        ArgumentNullException.ThrowIfNull(effects);

        var archive = new PkgArchiveReader().Read(packagePath);
        var added = effects.ToDictionary(
            value => value.Name, value => value.Data, StringComparer.OrdinalIgnoreCase);

        var entries = new List<(string Name, byte[] Data)>();
        foreach (var entry in archive.Entries)
        {
            var data = archive.ReadEntry(entry).ToArray();
            if (added.TryGetValue(entry.Name, out var replacement))
            {
                // Already there under the same name: the newer bytes win, and it is
                // not listed twice.
                entries.Add((entry.Name, replacement));
                added.Remove(entry.Name);
                continue;
            }
            if (entry.Name.Equals("asset_D3D11.xml", StringComparison.OrdinalIgnoreCase))
            {
                entries.Add((entry.Name, data));
                continue;
            }
            if (replaceModel is not null
                && entry.Name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase))
            {
                entries.Add((entry.Name, replaceModel(entry.Name)));
                continue;
            }
            entries.Add((entry.Name, data));
        }
        foreach (var pair in added) entries.Add((pair.Key, pair.Value));

        var manifest = entries.FindIndex(entry =>
            entry.Name.Equals("asset_D3D11.xml", StringComparison.OrdinalIgnoreCase));
        if (manifest >= 0)
        {
            entries[manifest] = (
                entries[manifest].Name,
                Listing(entries[manifest].Data, effects.Select(value => value.Name)));
        }
        return entries;
    }

    /// <summary>The manifest with every effect listed that was not listed already.</summary>
    private static byte[] Listing(byte[] manifest, IEnumerable<string> effects)
    {
        var text = new UTF8Encoding(false).GetString(manifest);
        var close = text.LastIndexOf("</asset>", StringComparison.OrdinalIgnoreCase);
        if (close < 0) return manifest;

        var lines = new StringBuilder();
        foreach (var effect in effects)
        {
            var line = $"    <cluster path=\"data/D3D11/shaders/{effect}\" type=\"binary\" />\r\n";
            if (text.Contains($"shaders/{effect}\"", StringComparison.OrdinalIgnoreCase)) continue;
            lines.Append(line);
        }
        if (lines.Length == 0) return manifest;

        return new UTF8Encoding(false).GetBytes(
            text[..close] + lines + text[close..]);
    }
}
