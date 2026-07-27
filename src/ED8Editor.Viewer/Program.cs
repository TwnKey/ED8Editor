using System.Text;
using ED8Editor.Application;
using ED8Editor.Assets;
using ED8Editor.Decompiler;
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
        if (args is ["--verify-conventions"])
        {
            // Reversed engine conventions that no rendering is needed to check.
            ScriptCameraOrbit.VerifySmoke();
            ScriptFacialPattern.VerifySmoke();
            ScriptDialogText.VerifySmoke();
            ScriptFlowPanel.VerifySmoke();
            Console.WriteLine(
                "PASS camera orbit, facial pattern, dialogue and script graph layout");
            return;
        }
        if (args is ["--verify-graph", var graphScript, var graphFunction])
        {
            var graph = ScriptDecompiler.Decompile(graphScript, null);
            var targets = graphFunction == "*"
                ? graph.Functions.Where(value => value.IsCode).ToArray()
                : new[]
                {
                    graph.Functions.Single(value =>
                        value.IsCode && value.Name.Equals(graphFunction, StringComparison.Ordinal)),
                };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var target in targets)
                ScriptFlowPanel.VerifyLayout(target, expectLoopArrow: false);
            Console.WriteLine(
                $"PASS graph layout: {targets.Length} scene(s),"
                + $" {targets.Sum(value => value.Instructions.Count)} instructions,"
                + $" {clock.ElapsedMilliseconds} ms"
                + $" (largest: {targets.Max(value => value.Instructions.Count)} instructions)");
            return;
        }
        if (args is ["--dump-entity-state", var stateScript, var stateFunction,
            var stateInstruction, var stateOutput, ..])
        {
            DumpEntityState(
                stateScript,
                stateFunction,
                int.Parse(stateInstruction),
                stateOutput,
                args.Length > 5 ? args[5] : null);
            return;
        }
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

    /// <summary>
    /// Headless replay of one scenario block. Writes the resolved entity state
    /// (model, animation slots, facial expression) so animation and expression
    /// regressions can be diagnosed without the interactive viewer.
    /// </summary>
    private static void DumpEntityState(
        string scriptPath,
        string functionName,
        int instructionIndex,
        string outputPath,
        string? gameDirectory)
    {
        var installation = gameDirectory is null ? null : RequireInstallation(gameDirectory);
        var loader = new EditorProjectLoader(
            new OpsReader(), new GameAssetResolverFactory(), new PkgArchiveReader(),
            new AssetManifestReader(), new PhyreD3D11ModelReader(), new PhyreD3D11TextureReader());
        var session = loader.OpenScript(scriptPath, installation?.DataPath);
        var gameDataPath = session.Script.GameDataPath
            ?? throw new ArgumentException("The script has no resolved game data directory.");
        var library = new ScriptAnimationLibrary(
            gameDataPath, session.Script.Header.SourcePath, null);
        var systemLibrary = new ScriptSystemLibrary(
            session.Script.Header.SourcePath, null).Script;
        var script = ScriptDecompiler.Decompile(scriptPath, null);
        var function = script.Functions.Single(value =>
            value.IsCode && value.Name.Equals(functionName, StringComparison.Ordinal));
        var state = ScriptSceneStateResolver.Resolve(
            script, function, instructionIndex, library, systemLibrary);
        var report = new StringBuilder();
        report.AppendLine($"{functionName} @ #{instructionIndex}: "
            + $"{function.Instructions[instructionIndex].Name}");
        var camera = state.Camera;
        report.AppendLine(
            $"camera: target={Describe(camera.Target)} distance={Describe(camera.Distance)} "
            + $"pitch={Describe(camera.PitchDegrees)} yaw={Describe(camera.YawDegrees)} "
            + $"roll={Describe(camera.RollDegrees)} fov={Describe(camera.VerticalFieldOfViewDegrees)} "
            + $"targetEntity={Describe(camera.TargetEntityId)}");
        if (camera.Target is { } lookAt
            && camera.Distance is { } orbitDistance
            && (camera.PitchDegrees is not null || camera.YawDegrees is not null))
        {
            var eye = lookAt + orbitDistance * ScriptCameraOrbit.EyeOffsetDirection(
                camera.PitchDegrees ?? 0f, camera.YawDegrees ?? 0f);
            report.AppendLine($"    eye={eye}");
        }
        foreach (var entity in state.Entities.Values.OrderBy(value => value.EntityId))
        {
            report.AppendLine(
                $"entity {entity.EntityId} asset={entity.AssetId} name={entity.DisplayName} "
                + $"facial={entity.FacialAssetId} position={entity.Position} yaw={entity.YawDegrees}");
            report.AppendLine(
                $"    initialAnimation='{entity.InitialAnimation}' "
                + $"banks=[{string.Join(", ", entity.AnimationBanks ?? Array.Empty<string>())}]");
            foreach (var slot in entity.AnimationSlots ?? new Dictionary<int, ScriptEntityAnimation>())
            {
                report.AppendLine(
                    $"    slot {slot.Key}: {slot.Value.Name} loop={slot.Value.Loop} "
                    + $"hold={slot.Value.HoldFinalFrame} start={slot.Value.StartFrame}");
            }
            if (entity.FacialExpression is { } expression)
            {
                report.AppendLine(
                    $"    face: E='{expression.PrimaryEyes}' M='{expression.Mouth}' "
                    + $"e='{expression.SecondaryEyes}' H='{expression.Complexion}' "
                    + $"start={expression.StartFrame}");
                var samples = Enumerable.Range(0, 12)
                    .Select(step =>
                    {
                        var pose = expression.Evaluate(step * 0.5f);
                        return $"{step * 0.5f:0.0}s:e{pose.PrimaryEyes}/e{pose.SecondaryEyes}"
                            + $"/m{pose.Mouth}/c{pose.Complexion}";
                    });
                report.AppendLine($"    face frames: {string.Join(" ", samples)}");
            }
        }
        File.WriteAllText(outputPath, report.ToString());

        static string Describe<T>(T? value) where T : struct
            => value is { } present ? present.ToString() ?? "-" : "-";
    }

    private static void ShowUsage()
        => MessageBox.Show(
            "Usage: ED8Editor.Viewer [<script.dat> [game-directory]]\n"
            + "       ED8Editor.Viewer --smoke <script.dat> [game-directory]",
            "ED8Editor Viewer",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
}
