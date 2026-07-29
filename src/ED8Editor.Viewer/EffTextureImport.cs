using System.Drawing.Imaging;
using ED8Editor.Packages;
using ED8Editor.Phyre;
using ED8Editor.Phyre.Authoring;

namespace ED8Editor.Viewer;

/// <summary>What an import wrote, so the mod project can track it.</summary>
public sealed record EffTextureImportResult(string AssetName, string PackagePath, int Width, int Height);

/// <summary>
/// Brings an image into the game as an effect texture.
///
/// A texture package is a .pkg holding an asset manifest and one texture
/// cluster, and both are now written outright: the cluster by
/// <see cref="PhyreTextureClusterWriter"/>, which reproduces every texture the
/// game ships byte for byte from nothing but an image, and the manifest from the
/// name the texture is given. Nothing is copied out of the game's own files any
/// more — which also fixes what copying cost: the manifest declares the SYMBOL
/// the loader resolves, so a package built on a borrowed one declared itself
/// under the borrowed name.
/// </summary>
internal static class EffTextureImport
{
    /// <summary>Where the game keeps the packages an effect draws with.</summary>
    private const string AssetDirectory = "asset/D3D11";

    /// <summary>The pixel formats an image can be brought in as.</summary>
    public static IReadOnlyList<string> Formats { get; } = new[] { "ARGB8", "DXT5", "DXT1" };

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
        Directory.CreateDirectory(assets);

        var (pixels, width, height) = ReadImage(imagePath);
        var stem = assetName.ToLowerInvariant();
        var mipCount = PhyreTextureBuilder.MipCount(width, height);
        var built = PhyreTextureClusterWriter.Write(
            PhyreTextureClusterWriter.AssetPathFor(stem),
            width,
            height,
            format,
            mipCount,
            PhyreTextureBuilder.EncodeMipChain(format, pixels, width, height));

        var entryName = $"{stem}.dds.phyre";
        var package = new PkgArchiveWriter().Write(
            PkgArchiveWriter.DefaultMagic,
            new[]
            {
                (ManifestEntryName, WriteManifest(assetName, entryName)),
                (entryName, built),
            });
        var packagePath = Path.Combine(assets, $"{assetName}.pkg");
        File.WriteAllBytes(packagePath, package);
        return new EffTextureImportResult(assetName, packagePath, width, height);
    }

    /// <summary>The manifest every asset package carries, under this name.</summary>
    private const string ManifestEntryName = "asset_D3D11.xml";

    /// <summary>
    /// The manifest that tells the loader what the package holds: the symbol a
    /// script or an effect names it by, and where the cluster sits inside the
    /// game's asset tree. Written the way the game writes it, tabs and all.
    /// </summary>
    private static byte[] WriteManifest(string assetName, string entryName)
        => System.Text.Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
            + "<fassets>\r\n"
            + $"\t<asset symbol=\"{assetName}\">\r\n"
            + $"\t\t<cluster path=\"data/D3D11/effects/images/{entryName}\" type=\"p_texture\" />\r\n"
            + "\t</asset>\r\n"
            + "</fassets>\r\n");

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
