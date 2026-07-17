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
        ApplicationConfiguration.Initialize();
        var smokeTest = args.Length > 0 && args[0] == "--smoke";
        var firstArgument = smokeTest ? 1 : 0;
        if (smokeTest && args.Length is not (2 or 3) || !smokeTest && args.Length > 2)
        {
            ShowUsage();
            return;
        }

        try
        {
            var settingsStore = new EditorSettingsStore();
            GameInstallation? installation;
            string scriptPath;
            if (smokeTest)
            {
                scriptPath = args[firstArgument];
                installation = args.Length > firstArgument + 1
                    ? RequireInstallation(args[firstArgument + 1])
                    : null;
            }
            else
            {
                installation = ResolveInteractiveInstallation(
                    settingsStore,
                    args.Length > 1 ? args[1] : null);
                if (installation is null) return;
                scriptPath = args.Length > 0 ? args[0] : SelectScript(installation.DataPath);
                if (string.IsNullOrEmpty(scriptPath)) return;
            }

            var loader = new EditorProjectLoader(
                new OpsReader(),
                new GameAssetResolverFactory(),
                new PkgArchiveReader(),
                new AssetManifestReader(),
                new PhyreD3D11ModelReader(),
                new PhyreD3D11TextureReader());
            var session = loader.OpenScript(scriptPath, installation?.DataPath);
            System.Windows.Forms.Application.Run(new ViewerForm(session, smokeTest, loader, settingsStore));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(exception.Message, "Cannot open scene", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static GameInstallation? ResolveInteractiveInstallation(EditorSettingsStore store, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var installation = RequireInstallation(explicitPath);
            store.Save(store.Load() with { GameDirectory = installation.RootPath });
            return installation;
        }

        var settings = store.Load();
        if (GameInstallation.TryOpen(settings.GameDirectory, out var configured, out _)) return configured;
        while (true)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select the Trails of Cold Steel installation folder",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
            };
            if (dialog.ShowDialog() != DialogResult.OK) return null;
            if (GameInstallation.TryOpen(dialog.SelectedPath, out var selected, out var reason))
            {
                store.Save(settings with { GameDirectory = selected!.RootPath });
                return selected;
            }
            MessageBox.Show(reason, "Invalid game directory", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static GameInstallation RequireInstallation(string path)
    {
        if (GameInstallation.TryOpen(path, out var installation, out var reason)) return installation!;
        throw new ArgumentException(reason, nameof(path));
    }

    private static string SelectScript(string dataPath)
    {
        var scenarioDirectory = Path.Combine(dataPath, "scripts", "scena");
        using var dialog = new OpenFileDialog
        {
            Title = "Open a Cold Steel script",
            Filter = "Cold Steel scripts (*.dat)|*.dat|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(scenarioDirectory) ? scenarioDirectory : Path.Combine(dataPath, "scripts"),
            CheckFileExists = true,
            Multiselect = false,
        };
        return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : string.Empty;
    }

    private static void ShowUsage()
        => MessageBox.Show(
            "Usage: ED8Editor.Viewer [<script.dat> [game-directory]]\n"
            + "       ED8Editor.Viewer --smoke <script.dat> [game-directory]",
            "ED8Editor Viewer",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
}
