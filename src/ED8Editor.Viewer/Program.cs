using ED8Editor.Application;
using ED8Editor.Assets;
using ED8Editor.Ops;
using ED8Editor.Packages;
using ED8Editor.Phyre;

namespace ED8Editor.Viewer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var smokeTest = args.Length > 0 && args[0] == "--smoke";
        var firstArgument = smokeTest ? 1 : 0;
        if (args.Length < firstArgument + 1 || args.Length > firstArgument + 2)
        {
            MessageBox.Show(
                "Usage: ED8Editor.Viewer [--smoke] <script.dat> [game-data-directory]",
                "ED8Editor Viewer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            var session = new EditorProjectLoader(
                new OpsReader(),
                new GameAssetResolverFactory(),
                new PkgArchiveReader(),
                new AssetManifestReader(),
                new PhyreD3D11ModelReader(),
                new PhyreD3D11TextureReader()).OpenScript(
                    args[firstArgument],
                    args.Length > firstArgument + 1 ? args[firstArgument + 1] : null);
            ApplicationConfiguration.Initialize();
            System.Windows.Forms.Application.Run(new ViewerForm(session, smokeTest));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(exception.Message, "Cannot open scene", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
