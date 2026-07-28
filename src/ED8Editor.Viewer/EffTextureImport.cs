using System.Drawing.Imaging;
using ED8Editor.Packages;
using ED8Editor.Phyre;

namespace ED8Editor.Viewer;

/// <summary>What an import wrote, so the mod project can track it.</summary>
public sealed record EffTextureImportResult(string AssetName, string PackagePath, int Width, int Height);

/// <summary>
/// Brings an image into the game as an effect texture.
///
/// A texture package is a .pkg holding the asset manifest and one texture
/// cluster. Neither is invented: the manifest and the cluster's whole schema are
/// taken from a package the game already ships, and only the fields that belong
/// to the image — its size, its mip count, the path it was built from — are
/// rewritten. What the editor writes is therefore the game's own format by
/// construction, which --verify-texture-import checks by rebuilding the shipped
/// packages byte for byte.
/// </summary>
internal static class EffTextureImport
{
    /// <summary>Where the game keeps the packages an effect draws with.</summary>
    private const string AssetDirectory = "asset/D3D11";

    /// <summary>The pixel formats an image can be brought in as.</summary>
    public static IReadOnlyList<string> Formats { get; } = new[] { "ARGB8", "DXT5", "DXT1" };

    /// <summary>
    /// The package a given format is modelled on, once one has been found: the
    /// scan reads packages until it meets that format, so it is done once.
    /// </summary>
    private static readonly Dictionary<string, string> TemplatesByFormat =
        new(StringComparer.OrdinalIgnoreCase);

    public static EffTextureImportResult Import(
        string gameDataPath,
        string imagePath,
        string assetName,
        string format)
    {
        if (string.IsNullOrWhiteSpace(assetName))
        {
            throw new ArgumentException("The texture needs a name.", nameof(assetName));
        }
        if (!PhyreTextureBuilder.CanWrite(format))
        {
            throw new NotSupportedException($"This editor cannot encode an image into '{format}'.");
        }
        var assets = Path.Combine(
            gameDataPath, AssetDirectory.Replace('/', Path.DirectorySeparatorChar));
        var reader = new PkgArchiveReader();
        var templatePath = FindTemplate(assets, reader, format);
        var template = reader.Read(templatePath);
        var manifest = template.Entries.FirstOrDefault(entry =>
            entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"{Path.GetFileName(templatePath)} carries no asset manifest.");
        var texture = template.Entries.FirstOrDefault(entry =>
            entry.Name.EndsWith(".phyre", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"{Path.GetFileName(templatePath)} carries no texture.");

        var cluster = PhyreTextureBuilder.Extract(template.ReadEntry(texture));

        var (pixels, width, height) = ReadImage(imagePath);
        var built = PhyreTextureBuilder.Build(
            cluster,
            assetName.ToLowerInvariant(),
            width,
            height,
            PhyreTextureBuilder.EncodeMipChain(cluster.Format, pixels, width, height),
            PhyreTextureBuilder.MipCount(width, height));

        var entryName = $"{assetName.ToLowerInvariant()}.dds.phyre";
        var package = new PkgArchiveWriter().Write(
            template.Magic,
            new[]
            {
                (manifest.Name, template.ReadEntry(manifest)),
                (entryName, built),
            });
        var packagePath = Path.Combine(assets, $"{assetName}.pkg");
        File.WriteAllBytes(packagePath, package);
        return new EffTextureImportResult(assetName, packagePath, width, height);
    }

    /// <summary>
    /// A package of the game whose texture is in the wanted format, to model the
    /// new one on. The serialized schema of a texture cluster — its namespace,
    /// its class descriptors, its fixup tables — is the same for every texture
    /// of a format and is not something this editor writes: it is taken from the
    /// game, which is also what the Rust tool this work comes from does, except
    /// that it carries the schema baked into its own binary.
    /// </summary>
    private static string FindTemplate(string assets, PkgArchiveReader reader, string format)
    {
        if (TemplatesByFormat.TryGetValue(format, out var known) && File.Exists(known)) return known;
        foreach (var path in Directory.EnumerateFiles(assets, "I_EFTEX*.pkg").Order())
        {
            try
            {
                var package = reader.Read(path);
                var entry = package.Entries.FirstOrDefault(value =>
                    value.Name.EndsWith(".phyre", StringComparison.OrdinalIgnoreCase));
                if (entry is null) continue;
                if (!PhyreTextureBuilder.Extract(package.ReadEntry(entry)).Format
                        .Equals(format, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                TemplatesByFormat[format] = path;
                return path;
            }
            catch (Exception exception) when (exception is IOException
                or InvalidDataException or NotSupportedException or ArgumentException)
            {
                // A package this editor cannot read is simply not a template.
            }
        }
        throw new FileNotFoundException(
            $"No effect texture package of the game is a '{format}', so there is no schema"
            + " to model a new one on.");
    }

    /// <summary>
    /// The image, as straight RGBA rows. It is flipped on the way in because a
    /// texture cluster stores its rows bottom-up, the way D3D11 samples them.
    /// </summary>
    private static (byte[] Pixels, int Width, int Height) ReadImage(string path)
    {
        using var source = new Bitmap(path);
        var width = source.Width;
        var height = source.Height;
        using var copy = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(copy))
        {
            graphics.DrawImage(source, 0, 0, width, height);
        }
        var locked = copy.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                var row = locked.Scan0 + (height - 1 - y) * locked.Stride;
                var line = new byte[width * 4];
                System.Runtime.InteropServices.Marshal.Copy(row, line, 0, line.Length);
                for (var x = 0; x < width; x++)
                {
                    // GDI hands over blue first; the encoder wants red first.
                    pixels[(y * width + x) * 4] = line[x * 4 + 2];
                    pixels[(y * width + x) * 4 + 1] = line[x * 4 + 1];
                    pixels[(y * width + x) * 4 + 2] = line[x * 4];
                    pixels[(y * width + x) * 4 + 3] = line[x * 4 + 3];
                }
            }
            return (pixels, width, height);
        }
        finally
        {
            copy.UnlockBits(locked);
        }
    }

    /// <summary>
    /// A name no package uses yet, in the range the game keeps for its own effect
    /// textures so the loader resolves it like any other.
    /// </summary>
    public static string SuggestName(string gameDataPath)
    {
        var assets = Path.Combine(
            gameDataPath, AssetDirectory.Replace('/', Path.DirectorySeparatorChar));
        for (var index = 900; index < 1000; index++)
        {
            var name = $"I_EFTEX{index:000}";
            if (!File.Exists(Path.Combine(assets, $"{name}.pkg"))) return name;
        }
        return "I_EFTEX999";
    }
}
