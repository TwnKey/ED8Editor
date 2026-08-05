using System.Globalization;
using System.Text;
using System.Numerics;
using ED8Editor.Application;
using ED8Editor.Assets;
using ED8Editor.Core;
using ED8Editor.Decompiler;
using ED8Editor.Ops;
using ED8Editor.Packages;
using ED8Editor.Phyre;
using ED8Editor.Scene;

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
            ScriptWaitDuration.VerifySmoke();
            ScriptFlowPanel.VerifySmoke();
            Console.WriteLine(
                "PASS camera orbit, facial pattern, dialogue, script waits and graph layout");
            return;
        }
        if (args is ["--dump-authoring-catalog", var catalogDataPath])
        {
            var library = new ScriptAnimationLibrary(catalogDataPath, null, null);
            var characters = CharacterAuthoringCatalog.LoadCharacters(library);
            var enemies = CharacterAuthoringCatalog.LoadEnemies(catalogDataPath);
            Console.WriteLine(
                $"characters={characters.Count}; enemies={enemies.Count}; "
                + library.NameTableDiagnostics);
            foreach (var entry in characters.Take(5).Concat(enemies.Take(5)))
            {
                Console.WriteLine(
                    $"{entry.Kind}: {entry.DisplayName} | model={entry.ModelAssetId}"
                    + $" | ani={entry.AnimationScript} | source={entry.SourceTable}");
            }
            return;
        }
        if (args is ["--verify-battle-catalog", var battleDataPath])
        {
            var enemies = EnemyBattleCatalog.LoadProfiles(battleDataPath);
            var analyses = enemies
                .Select(enemy => EnemyBattleCatalog.Analyze(
                    battleDataPath, enemy, null))
                .ToArray();
            var scenarios = BattleScenarioCatalog.Load(battleDataPath);
            var scenarioAnalyses = scenarios
                .Select(scenario => BattleScenarioCatalog.Analyze(scenario, null))
                .ToArray();
            Console.WriteLine(
                $"enemies={enemies.Count}; actions={analyses.Sum(value => value.Actions.Count)}; "
                + $"rules={analyses.Sum(value => value.Rules.Count)}; "
                + $"supplemental={analyses.Sum(value => value.SupplementalTables.Count)}; "
                + $"battle_scenarios={scenarios.Count}; "
                + $"lifecycle_functions={scenarioAnalyses.Sum(value => value.Lifecycle.Count)}");
            foreach (var diagnostic in analyses
                         .SelectMany((analysis, index) => analysis.Diagnostics.Select(value =>
                             $"{enemies[index].AssetId}: {value}"))
                         .Concat(scenarioAnalyses.SelectMany(value =>
                             value.Diagnostics.Select(diagnostic =>
                                 $"{value.Entry.Label}: {diagnostic}")))
                         .Take(25))
            {
                Console.WriteLine(diagnostic);
            }
            return;
        }
        if (args is ["--verify-model-asset", var modelAssetId, var modelDataPath])
        {
            var modelLoader = new EditorProjectLoader(
                new OpsReader(), new GameAssetResolverFactory(), new PkgArchiveReader(),
                new AssetManifestReader(), new PhyreD3D11ModelReader(),
                new PhyreD3D11TextureReader());
            var load = modelLoader.LoadAsset(modelAssetId, modelDataPath);
            var bounds = load.Model is null
                ? null
                : new SceneBoundsCalculator().Calculate(new[]
                {
                    new SceneModelInstance(
                        0, modelAssetId, modelAssetId, load.Model,
                        Matrix4x4.Identity),
                });
            Console.WriteLine(
                $"{modelAssetId}: {load.Status}; "
                + $"model={load.Model?.AssetId ?? "<none>"}; "
                + $"meshes={load.Model?.Meshes.Count ?? 0}; "
                + $"bounds={(bounds?.HasGeometry == true
                    ? $"{bounds.Center.X:G4},{bounds.Center.Y:G4},{bounds.Center.Z:G4}"
                        + $" r={bounds.Radius:G4}"
                    : "<none>")}; error={load.Error ?? "<none>"}");
            Environment.ExitCode = load.Status == AssetModelLoadStatus.Loaded ? 0 : 1;
            return;
        }
        if (args is ["--verify-encounter-asset", var monsterAssetId, var encounterDataPath])
        {
            var choice = MonsterTableCatalog.Load(encounterDataPath).SingleOrDefault(value =>
                value.AssetId.Equals(monsterAssetId, StringComparison.OrdinalIgnoreCase));
            if (choice is null)
            {
                Console.Error.WriteLine($"{monsterAssetId}: no exact t_mons status mapping");
                Environment.ExitCode = 1;
                return;
            }
            Console.WriteLine(
                $"{choice.AssetId}: name={choice.DisplayName}; model={choice.ModelAssetId}");
            var modelLoader = new EditorProjectLoader(
                new OpsReader(), new GameAssetResolverFactory(), new PkgArchiveReader(),
                new AssetManifestReader(), new PhyreD3D11ModelReader(),
                new PhyreD3D11TextureReader());
            var load = modelLoader.LoadAsset(choice.ModelAssetId, encounterDataPath);
            Console.WriteLine(
                $"{choice.ModelAssetId}: {load.Status}; meshes={load.Model?.Meshes.Count ?? 0}; "
                + $"error={load.Error ?? "<none>"}");
            Environment.ExitCode = load.Status == AssetModelLoadStatus.Loaded ? 0 : 1;
            return;
        }
        if (args is ["--verify-eff", var effRoot])
        {
            Environment.ExitCode = VerifyEffects(effRoot);
            return;
        }
        if (args is ["--calibrate-delay-camera", var delayRoot])
        {
            CalibrateDelayAgainstCamera(delayRoot);
            return;
        }
        if (args is ["--calibrate-delay", var delayScript])
        {
            CalibrateDelay(delayScript);
            return;
        }
        if (args is ["--dump-function-timeline", var timelineScript, var timelineFunction])
        {
            DumpFunctionTimeline(timelineScript, timelineFunction);
            return;
        }
        if (args is ["--dump-phyre", var phyrePath, ..])
        {
            DumpPhyreCluster(phyrePath, args.Length > 2 && args[2] == "--members");
            return;
        }
        if (args is ["--new-eff", var newEffPath])
        {
            var created = EffAuthoring.CreateEffect(
                Path.GetFileNameWithoutExtension(newEffPath));
            EffAuthoring.AddNewSegment(created, created.Version, 0, "spark");
            EffFileWriter.Write(created, newEffPath);
            Console.WriteLine($"wrote {newEffPath}");
            return;
        }
        if (args is ["--dump-eff", var effPath])
        {
            DumpEffect(effPath);
            return;
        }
        if (args is ["--verify-texture-import", var textureRoot])
        {
            Environment.ExitCode = VerifyTextureImport(textureRoot);
            return;
        }
        if (args is ["--verify-eff-textures", var effTextureRoot])
        {
            Environment.ExitCode = VerifyEffectTextures(effTextureRoot);
            return;
        }
        if (args is ["--dump-eff-nodes", var effNodePath, var effTime])
        {
            DumpEffectNodes(effNodePath, float.Parse(effTime, CultureInfo.InvariantCulture));
            return;
        }
        if (args is ["--dump-subject", var subjectScript])
        {
            var subjectLoader = new EditorProjectLoader(
                new OpsReader(), new GameAssetResolverFactory(), new PkgArchiveReader(),
                new AssetManifestReader(), new PhyreD3D11ModelReader(), new PhyreD3D11TextureReader());
            var subjectSession = subjectLoader.OpenScript(subjectScript, null);
            var gameData = subjectSession.Script.GameDataPath;
            var subjectLibrary = gameData is null
                ? null
                : new ScriptAnimationLibrary(gameData, subjectSession.Script.Header.SourcePath, null);
            var subject = ScriptSubjectResolver.Resolve(
                subjectSession.Script.Header.SourcePath, gameData, subjectLibrary);
            Console.WriteLine(subject is null
                ? $"{Path.GetFileName(subjectScript)}: no actor (scenario or system script)"
                : $"{subject.ScriptName}: {subject.ModelAssetId} (from {subject.Source})"
                    + $", map={(subjectSession.Map is null ? "none -> debug ground" : "loaded")}");
            return;
        }
        if (args is ["--dump-attach", var attachScript])
        {
            var attachLoader = new EditorProjectLoader(
                new OpsReader(), new GameAssetResolverFactory(), new PkgArchiveReader(),
                new AssetManifestReader(), new PhyreD3D11ModelReader(), new PhyreD3D11TextureReader());
            var attachSession = attachLoader.OpenScript(attachScript, null);
            var attachData = attachSession.Script.GameDataPath;
            var attachLibrary = attachData is null
                ? null
                : new ScriptAnimationLibrary(attachData, attachSession.Script.Header.SourcePath, null);
            var attachSubject = ScriptSubjectResolver.Resolve(
                attachSession.Script.Header.SourcePath, attachData, attachLibrary);
            var table = ScriptAttachTable.Load(attachData);
            var character = attachLibrary?.FindCharacterByModel(attachSubject?.ModelAssetId);
            Console.WriteLine(
                $"t_attach.tbl: {table.Count} attachments; subject="
                + $"{attachSubject?.ModelAssetId ?? "none"} character={character?.ToString() ?? "-"}");
            var ownerModel = attachSubject is null || attachData is null
                ? null
                : attachLoader.LoadAsset(attachSubject.ModelAssetId, attachData).Model;
            var ownerPose = ownerModel?.Skeleton is null
                ? null
                : new ED8Editor.Core.CpuSkeletonPoseEvaluator().Evaluate(ownerModel.Skeleton, null, 0f);
            foreach (var attachment in character is { } id
                ? table.FindByCharacter(id)
                : Array.Empty<ScriptAttachment>())
            {
                var joint = ownerModel?.Skeleton?.Joints
                    .Select((value, index) => (value.Name, index))
                    .FirstOrDefault(value => value.Name.Equals(
                        attachment.AttachPoint, StringComparison.OrdinalIgnoreCase));
                var carried = attachData is null
                    ? null
                    : attachLoader.LoadAsset(attachment.ModelAssetId, attachData).Model;
                var placement = joint is { Name: not null } bone && ownerPose is not null
                    ? $"bone #{bone.index} at {ownerPose.WorldTransforms[bone.index].Translation}"
                    : "BONE NOT FOUND";
                Console.WriteLine(
                    $"    {attachment.ModelAssetId} on {attachment.AttachPoint}"
                    + $" -> {placement}, model={(carried is null ? "missing" : $"{carried.Meshes.Count} meshes")}");
            }
            return;
        }
        if (args is ["--dump-tbl", var tblRows, var tblTake])
        {
            var rows = ED8Editor.Tables.Cs1TableDocument.Read(tblRows).Entries.Take(int.Parse(tblTake));
            foreach (var row in rows)
            {
                Console.WriteLine($"{row.Category} [{row.Data.Length}] {Convert.ToHexString(row.Data)}");
                Console.WriteLine("    " + string.Concat(row.Data
                    .Select(value => value is >= 0x20 and < 0x7f ? (char)value : '.')));
            }
            return;
        }
        if (args is ["--dump-tbl", var tblPath])
        {
            var document = ED8Editor.Tables.Cs1TableDocument.Read(tblPath);
            foreach (var group in document.Entries.GroupBy(value => value.Category))
            {
                var first = group.First();
                var ascii = string.Concat(first.Data.Take(72)
                    .Select(value => value is >= 0x20 and < 0x7f ? (char)value : '.'));
                Console.WriteLine($"{group.Key,-28} {group.Count(),6} entries, {first.Data.Length,4} bytes: {ascii}");
            }
            return;
        }
        if (args is ["--dump-tbl-category", var decodedTblPath, var decodedCategory])
        {
            var codec = new ED8Editor.Tables.Cs1TableRecordCodec(
                textEncoding: new UTF8Encoding(false, true));
            var rows = ED8Editor.Tables.Cs1TableDocument.Read(decodedTblPath).Entries
                .Where(value => value.Category.Equals(decodedCategory, StringComparison.Ordinal));
            var index = 0;
            foreach (var row in rows)
            {
                var values = codec.Decode(row);
                Console.WriteLine($"[{index++}] " + (values is null
                    ? Convert.ToHexString(row.Data)
                    : string.Join(", ", values.Select(value =>
                        $"{value.Field.Name}={value.Value}"))));
            }
            return;
        }
        if (args is ["--dump-voice", var voiceScript, var voiceId])
        {
            var voiceLoader = new EditorProjectLoader(
                new OpsReader(), new GameAssetResolverFactory(), new PkgArchiveReader(),
                new AssetManifestReader(), new PhyreD3D11ModelReader(), new PhyreD3D11TextureReader());
            var voiceSession = voiceLoader.OpenScript(voiceScript, null);
            var table = ScriptVoiceTable.Load(
                voiceSession.Script.GameDataPath, voiceSession.Script.Header.SourcePath);
            var wanted = int.Parse(voiceId);
            Console.WriteLine(
                $"t_voice.tbl: {table.Count} lines; id {wanted} -> "
                + (table.FindFile(wanted) ?? "not declared"));
            return;
        }
        if (args is ["--dump-ani-call", var aniScript, var aniFunction, var aniIndex])
        {
            DumpAnimationCall(aniScript, aniFunction, int.Parse(aniIndex));
            return;
        }
        if (args is ["--dump-graph", var dumpScript, var dumpFunction])
        {
            var dumped = ScriptDecompiler.Decompile(dumpScript, null);
            Console.WriteLine(ScriptFlowPanel.DescribeLayout(dumped.Functions.Single(value =>
                value.IsCode && value.Name.Equals(dumpFunction, StringComparison.Ordinal))));
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
            ScriptFlowPanel.StackedAmbiguities = 0;
            ScriptFlowPanel.OverlapReport = new List<string>();
            var overlapping = 0;
            foreach (var target in targets)
            {
                ScriptFlowPanel.VerifyLayout(target, expectLoopArrow: false);
                overlapping += ScriptFlowPanel.CountOverlappingEdges(target);
            }
            Console.WriteLine(
                $"PASS graph layout: {targets.Length} scene(s),"
                + $" {targets.Sum(value => value.Instructions.Count)} instructions,"
                + $" {clock.ElapsedMilliseconds} ms"
                + $" (largest: {targets.Max(value => value.Instructions.Count)} instructions,"
                + $" {ScriptFlowPanel.StackedAmbiguities} stacked-ambiguous pair(s),"
                + $" {overlapping} arrow pair(s) drawn along the same line)");
            foreach (var line in ScriptFlowPanel.OverlapReport!) Console.WriteLine("  " + line);
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
            string? projectPath = null;
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
                // The editor works on a mod project: it reopens the last one and
                // only asks for a file when there is no project to work from.
                projectPath = ResolveStartupProject(settingsStore, installation.DataPath);
                scriptPath = args.Length > 0
                    ? args[0]
                    : ResolveStartupScript(projectPath, installation.DataPath);
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
            var form = new ViewerForm(session, smokeTest, loader, settingsStore);
            if (!smokeTest && projectPath is not null) form.LoadModProject(projectPath);
            System.Windows.Forms.Application.Run(form);
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

    /// <summary>
    /// Project the editor starts on: the one it was last working in, otherwise a
    /// new one the user is offered to create.
    /// </summary>
    private static string? ResolveStartupProject(EditorSettingsStore store, string dataPath)
    {
        var settings = store.Load();
        if (!string.IsNullOrWhiteSpace(settings.LastProjectPath)
            && File.Exists(settings.LastProjectPath))
        {
            return settings.LastProjectPath;
        }
        var answer = MessageBox.Show(
            "No mod project was found.\n\nA project tracks the game files you edit,"
            + " keeps a pristine copy of each one and exports them as a distributable"
            + " archive.\n\nCreate one now?",
            "ED8Editor",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return null;
        using var dialog = new SaveFileDialog
        {
            Title = "Create a mod project",
            Filter = "ED8 mod project (*.ed8mod)|*.ed8mod",
            FileName = "my-mod.ed8mod",
            AddExtension = true,
            DefaultExt = "ed8mod",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != DialogResult.OK) return null;
        var gameRoot = Path.GetDirectoryName(Path.GetFullPath(dataPath));
        if (gameRoot is null) return null;
        try
        {
            var project = ModProject.Create(dialog.FileName, gameRoot);
            store.Save(store.Load() with { LastProjectPath = project.ProjectPath });
            return project.ProjectPath;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or ArgumentException or DirectoryNotFoundException)
        {
            MessageBox.Show(
                exception.Message, "Cannot create the project",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
    }

    /// <summary>
    /// First script to show. A project that already holds one opens on it with no
    /// question asked; an empty project asks once, and keeps the answer so it
    /// never asks again.
    /// </summary>
    private static string ResolveStartupScript(string? projectPath, string dataPath)
    {
        ModProject? project = null;
        if (projectPath is not null)
        {
            try
            {
                project = ModProject.Open(projectPath);
                var tracked = project.Files
                    .Select(value => project.GameFilePath(value.RelativePath))
                    .FirstOrDefault(value =>
                        value.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) && File.Exists(value));
                if (tracked is not null) return tracked;
            }
            catch (Exception exception) when (exception is IOException
                or InvalidDataException or ArgumentException)
            {
                // A damaged project must not stop the editor from opening a file.
                System.Diagnostics.Debug.WriteLine($"Could not read '{projectPath}': {exception.Message}");
            }
        }
        var picked = SelectScript(dataPath);
        if (picked.Length == 0 || project is null) return picked;
        try
        {
            project.Include(picked);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or ArgumentException or FileNotFoundException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not track '{picked}': {exception.Message}");
        }
        return picked;
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
            Title = "Add a script to the mod project",
            Filter = "Cold Steel scripts (*.dat)|*.dat|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(scenarioDirectory) ? scenarioDirectory : Path.Combine(dataPath, "scripts"),
            CheckFileExists = true,
            Multiselect = false,
        };
        return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : string.Empty;
    }

    /// <summary>
    /// Reads every .eff under a directory and writes it back, byte for byte.
    /// The effect format is read from a version and a flag word alone — one field
    /// misread and everything after it slides — so rewriting the whole corpus
    /// unchanged is what proves the reader and the writer agree with the engine.
    /// </summary>
    private static int VerifyEffects(string root)
    {
        var files = Directory.Exists(root)
            ? Directory.GetFiles(root, "*.eff", SearchOption.AllDirectories).Order().ToArray()
            : new[] { root };
        var failures = 0;
        var segments = 0;
        var keyframes = 0;
        foreach (var path in files)
        {
            try
            {
                var original = File.ReadAllBytes(path);
                var effect = EffFileReader.Read(original);
                var written = EffFileWriter.Write(effect);
                segments += effect.Segments.Count;
                keyframes += effect.Segments.Sum(segment =>
                    segment.Position.Count + segment.Rotation.Count + segment.Scale.Count
                    + segment.Rotation2.Count + segment.ColorMultiply.Count + segment.ColorAdd.Count
                    + segment.Children.Count);
                if (written.AsSpan().SequenceEqual(original)) continue;
                failures++;
                var difference = FirstDifference(original, written);
                Console.Error.WriteLine(
                    $"FAIL {Path.GetFileName(path)}: {original.Length} bytes in, {written.Length} out"
                    + (difference < 0 ? string.Empty : $", first difference at 0x{difference:X}"));
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {Path.GetFileName(path)}: {exception.Message}");
            }
        }
        Console.WriteLine(failures == 0
            ? $"PASS {files.Length} effects rewritten byte for byte"
                + $" ({segments} segments, {keyframes} keyframes)"
            : $"FAIL {failures} of {files.Length} effects");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Encodes an image into DXT5 and reads it back, requiring every channel to
    /// land within <paramref name="tolerance"/> of what went in.
    /// </summary>
    private static void CheckBlocks(string what, byte[] rgba, int width, int height, int tolerance)
    {
        var blocks = BlockCompressor.EncodeBc3(rgba, width, height);
        var decoded = BlockCompressor.DecodeBc3(blocks, width, height);
        var worst = 0;
        for (var index = 0; index < rgba.Length; index++)
        {
            worst = Math.Max(worst, Math.Abs(rgba[index] - decoded[index]));
        }
        if (worst > tolerance)
        {
            throw new InvalidDataException(
                $"DXT5 on {what}: a channel came back {worst} off, more than the {tolerance} allowed");
        }
        Console.WriteLine($"  DXT5 on {what}: worst channel off by {worst}");
    }

    private static int FirstDifference(byte[] left, byte[] right)
    {
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            if (left[index] != right[index]) return index;
        }
        return left.Length == right.Length ? -1 : Math.Min(left.Length, right.Length);
    }

    /// <summary>Prints what one effect file holds, segment by segment.</summary>
    private static void DumpEffect(string path)
    {
        var effect = EffFileReader.Read(path);
        Console.WriteLine(
            $"{effect.EffectName} — version {EffGameVersion.Describe(effect.Version)},"
            + $" {effect.Segments.Count} segments, {effect.Textures.Count} textures");
        foreach (var texture in effect.Textures) Console.WriteLine($"  texture {texture}");
        foreach (var name in effect.UnknownNames) Console.WriteLine($"  name {name}");
        foreach (var segment in effect.Segments)
        {
            Console.WriteLine(
                $"  segment {segment.Name} [texture {segment.TextureName}"
                + $"{(segment.ModelName.Length == 0 ? string.Empty : ", model " + segment.ModelName)}]"
                + $" d02=[{string.Join(" ", segment.Data02.Select(value => $"{value:X8}"))}]"
                + $" flags=0x{segment.StructFlags:X}"
                + $" pos={segment.Position.Count} rot={segment.Rotation.Count}"
                + $" scale={segment.Scale.Count} rot2={segment.Rotation2.Count}"
                + $" mul={segment.ColorMultiply.Count} add={segment.ColorAdd.Count}"
                + $" quad=[{string.Join(" ", segment.Data08.Select(value => value.ToString("0.###", CultureInfo.InvariantCulture)))}]"
                + $" children={segment.Children.Count}");
        }
    }

    /// <summary>
    /// Checks that this editor can write a texture package the game would read,
    /// by rebuilding the ones it ships. For every effect texture package: the
    /// cluster is rebuilt from its own template and its own pixels and must come
    /// out byte for byte the file that was read — which is what proves the
    /// template was cut in the right place and every rewritten field is the one
    /// the game reads — and the package itself is rewritten and re-read, entry
    /// for entry.
    /// </summary>
    private static int VerifyTextureImport(string gameDataPath)
    {
        var assets = Path.Combine(gameDataPath, "asset", "D3D11");
        var packages = Directory.GetFiles(assets, "I_EFTEX*.pkg").Order().ToArray();
        var reader = new PkgArchiveReader();
        var writer = new PkgArchiveWriter();
        var failures = 0;
        var rebuilt = 0;
        var unwritable = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in packages)
        {
            try
            {
                var archive = reader.Read(path);
                var entries = archive.Entries
                    .Select(entry => (entry.Name, Data: archive.ReadEntry(entry)))
                    .ToArray();
                var texture = entries.FirstOrDefault(entry =>
                    entry.Name.EndsWith(".phyre", StringComparison.OrdinalIgnoreCase));
                if (texture.Data is null)
                {
                    failures++;
                    Console.Error.WriteLine($"FAIL {Path.GetFileName(path)}: no texture entry");
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(
                    Path.GetFileNameWithoutExtension(texture.Name));
                var template = PhyreTextureBuilder.Extract(texture.Data);
                // Rebuilding reuses the texture's own pixels, so every format is
                // checked here; only bringing an IMAGE in needs an encoder.
                if (!PhyreTextureBuilder.CanWrite(template.Format)) unwritable.Add(template.Format);
                var again = PhyreTextureBuilder.Rebuild(texture.Data, name);
                if (!again.AsSpan().SequenceEqual(texture.Data))
                {
                    failures++;
                    var difference = FirstDifference(texture.Data, again);
                    Console.Error.WriteLine(
                        $"FAIL {Path.GetFileName(path)}: the rebuilt cluster differs"
                        + $" ({texture.Data.Length} bytes in, {again.Length} out)"
                        + $", first at 0x{difference:X} (object at 0x{template.ObjectOffset:X},"
                        + $" size field at 0x{template.BufferSizeOffset:X},"
                        + $" path at 0x{template.AssetPathOffset:X}+{template.AssetPathCapacity})"
                        + $" was {texture.Data[difference]:X2} now {again[difference]:X2}");
                    continue;
                }

                // The package the editor writes must read back the same way.
                var written = writer.Write(archive.Magic, entries);
                var temporary = Path.Combine(Path.GetTempPath(), $"ed8pkg-{Guid.NewGuid():N}.pkg");
                try
                {
                    File.WriteAllBytes(temporary, written);
                    var reopened = reader.Read(temporary);
                    if (reopened.Entries.Count != entries.Length)
                    {
                        throw new InvalidDataException("the entry count changed");
                    }
                    for (var index = 0; index < entries.Length; index++)
                    {
                        if (!reopened.Entries[index].Name.Equals(entries[index].Name, StringComparison.Ordinal)
                            || !reopened.ReadEntry(reopened.Entries[index])
                                .AsSpan().SequenceEqual(entries[index].Data))
                        {
                            throw new InvalidDataException($"entry '{entries[index].Name}' changed");
                        }
                    }
                }
                finally
                {
                    File.Delete(temporary);
                }
                rebuilt++;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException
                or ArgumentException or NotSupportedException or InvalidOperationException)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {Path.GetFileName(path)}: {exception.Message}");
            }
        }
        // Every format an import offers must have a package of the game to model
        // its schema on, or the import would have nothing to write.
        foreach (var format in EffTextureImport.Formats)
        {
            var found = packages.FirstOrDefault(path =>
            {
                try
                {
                    var package = reader.Read(path);
                    var entry = package.Entries.FirstOrDefault(value =>
                        value.Name.EndsWith(".phyre", StringComparison.OrdinalIgnoreCase));
                    return entry is not null
                        && PhyreTextureBuilder.Extract(package.ReadEntry(entry)).Format
                            .Equals(format, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception exception) when (exception is IOException
                    or InvalidDataException or NotSupportedException or ArgumentException)
                {
                    return false;
                }
            });
            if (found is null)
            {
                failures++;
                Console.Error.WriteLine($"FAIL no shipped package is a '{format}' to model on");
                continue;
            }
            Console.WriteLine($"  {format}: modelled on {Path.GetFileName(found)}");
        }

        // A compressed image must come back out of its blocks: flat blocks are
        // exact, and a gradient must stay close.
        try
        {
            const int width = 64;
            const int height = 64;
            var flat = new byte[width * height * 4];
            var smooth = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var pixel = (y * width + x) * 4;
                    // One colour per 4x4 block, which a block encoder stores exactly.
                    flat[pixel] = (byte)(x / 4 * 16);
                    flat[pixel + 1] = (byte)(y / 4 * 16);
                    flat[pixel + 2] = 32;
                    flat[pixel + 3] = (byte)(y / 4 % 2 == 0 ? 255 : 64);
                    smooth[pixel] = (byte)(x * 4);
                    smooth[pixel + 1] = (byte)(y * 4);
                    smooth[pixel + 2] = (byte)(255 - x * 4);
                    smooth[pixel + 3] = (byte)(x * 4);
                }
            }
            // A block of one colour keeps its indices exact; what it loses is
            // the five and six bits the format gives a colour endpoint.
            CheckBlocks("flat blocks", flat, width, height, 8);
            CheckBlocks("a gradient", smooth, width, height, 32);
        }
        catch (Exception exception) when (exception is InvalidDataException)
        {
            failures++;
            Console.Error.WriteLine($"FAIL {exception.Message}");
        }

        // Writing a texture of another size must produce a cluster the editor's
        // own reader understands: that is what says the rewritten fields are the
        // ones the game reads when the image is not the template's.
        try
        {
            var template = PhyreTextureBuilder.Extract(
                reader.Read(Path.Combine(assets, "I_EFTEX000.pkg")) is var source
                    ? source.ReadEntry(source.Entries.First(entry =>
                        entry.Name.EndsWith(".phyre", StringComparison.OrdinalIgnoreCase)))
                    : throw new InvalidDataException("no template"));
            const int width = 96;
            const int height = 48;
            var image = new byte[width * height * 4];
            for (var index = 0; index < image.Length; index++) image[index] = (byte)(index % 251);
            var pixels = PhyreTextureBuilder.EncodeMipChain(template.Format, image, width, height);
            var built = PhyreTextureBuilder.Build(
                template, "i_eftex900", width, height, pixels,
                PhyreTextureBuilder.MipCount(width, height));
            var written = new PhyreD3D11TextureReader().Read("i_eftex900", built);
            if (written.Width != width || written.Height != height
                || written.MipCount != PhyreTextureBuilder.MipCount(width, height)
                || written.Data.Length != pixels.Length
                || !written.Data.AsSpan().SequenceEqual(pixels))
            {
                failures++;
                Console.Error.WriteLine(
                    $"FAIL a written {width}x{height} texture read back as"
                    + $" {written.Width}x{written.Height}, {written.MipCount} mips,"
                    + $" {written.Data.Length} of {pixels.Length} bytes");
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            failures++;
            Console.Error.WriteLine($"FAIL writing a new texture: {exception.Message}");
        }

        // The import as the editor runs it: an image in, a package out, read back
        // through the same pipeline the game's own packages go through.
        var sandbox = Path.Combine(Path.GetTempPath(), $"ed8tex-{Guid.NewGuid():N}");
        try
        {
            var imagePath = Path.Combine(sandbox, "source.png");
            Directory.CreateDirectory(sandbox);
            using (var bitmap = new Bitmap(48, 24))
            {
                for (var y = 0; y < bitmap.Height; y++)
                {
                    for (var x = 0; x < bitmap.Width; x++)
                    {
                        bitmap.SetPixel(x, y, Color.FromArgb(255 - x * 5, x * 5, y * 10, 40));
                    }
                }
                bitmap.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);
            }

            var imported = EffTextureImport.Import(sandbox, imagePath, "I_EFTEX900", "ARGB8");
            var package = reader.Read(imported.PackagePath);
            var manifest = System.Text.Encoding.UTF8.GetString(
                package.ReadEntry(package.Entries.First(entry =>
                    entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))));
            var written = new PhyreD3D11TextureReader().Read(
                "imported",
                package.ReadEntry(package.Entries.First(entry =>
                    entry.Name.EndsWith(".phyre", StringComparison.OrdinalIgnoreCase))));
            if (!manifest.Contains("symbol=\"I_EFTEX900\"", StringComparison.Ordinal))
            {
                failures++;
                Console.Error.WriteLine("FAIL the imported package declares another symbol");
            }
            if (written.Width != 48 || written.Height != 24 || written.Format != "ARGB8")
            {
                failures++;
                Console.Error.WriteLine(
                    $"FAIL the imported texture read back as {written.Width}x{written.Height}"
                    + $" {written.Format}");
            }
            else
            {
                Console.WriteLine(
                    $"  imported 48x24 -> {Path.GetFileName(imported.PackagePath)},"
                    + $" read back {written.Width}x{written.Height} {written.Format},"
                    + $" {written.MipCount} mips, symbol declared");
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException
            or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            failures++;
            Console.Error.WriteLine($"FAIL importing an image: {exception.Message}");
        }
        finally
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
        }

        Console.WriteLine(
            (failures == 0 ? "PASS " : "FAIL ")
            + $"{rebuilt} of {packages.Length} effect texture packages rebuilt byte for byte"
            + (unwritable.Count == 0
                ? string.Empty
                : $"; formats an import cannot encode into: {string.Join(", ", unwritable)}"));
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Resolves the texture every drawn segment of every effect names, through
    /// the same asset pipeline the viewport uses. A segment names an effect
    /// texture package, so a name that resolves to no package would leave a hole
    /// in the render.
    /// </summary>
    private static int VerifyEffectTextures(string gameDataPath)
    {
        var loader = new EditorProjectLoader(
            new OpsReader(), new GameAssetResolverFactory(), new PkgArchiveReader(),
            new AssetManifestReader(), new PhyreD3D11ModelReader(), new PhyreD3D11TextureReader());
        var effects = Directory.GetFiles(
            Path.Combine(gameDataPath, "effects"), "*.eff", SearchOption.AllDirectories);
        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var drawnSegments = 0;
        var textureless = 0;
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var shapes = new SortedDictionary<uint, int>();
        foreach (var path in effects)
        {
            EffFile effect;
            try
            {
                effect = EffFileReader.Read(path);
            }
            catch (InvalidDataException)
            {
                continue;
            }
            foreach (var segment in effect.Segments)
            {
                // Only the segments the engine draws need a texture: a
                // container places its children and draws nothing itself.
                if ((segment.Data02[0] & 1) != 0) continue;
                drawnSegments++;
                shapes[segment.Data02[4] & 0xFF] = shapes.GetValueOrDefault(segment.Data02[4] & 0xFF) + 1;
                // A segment with no texture name draws nothing; it is not a hole.
                if (segment.TextureName.Length == 0)
                {
                    textureless++;
                    continue;
                }
                if (!resolved.TryGetValue(segment.TextureName, out var status))
                {
                    try
                    {
                        var texture = loader.LoadEffectTexture(segment.TextureName, gameDataPath);
                        status = texture is null
                            ? "no package"
                            : $"{texture.Width}x{texture.Height} {texture.Format}";
                    }
                    catch (Exception exception) when (exception is IOException
                        or InvalidDataException or ArgumentException or NotSupportedException)
                    {
                        status = exception.Message;
                    }
                    resolved[segment.TextureName] = status;
                }
                if (status is null or "no package") missing.Add(segment.TextureName);
            }
        }
        var loaded = resolved.Count(pair => pair.Value is not (null or "no package"));
        foreach (var name in missing) Console.Error.WriteLine($"  unresolved: {name}");
        Console.WriteLine(
            $"{(missing.Count == 0 ? "PASS" : "FAIL")} {effects.Length} effects,"
            + $" {drawnSegments} drawn segments ({textureless} name no texture),"
            + $" {resolved.Count} distinct textures, {loaded} loaded, {missing.Count} unresolved");
        Console.WriteLine("  shapes: " + string.Join(
            ", ", shapes.Select(pair => $"0x{pair.Key:X2}x{pair.Value}")));
        return missing.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Measures what unit Ani_Delay counts in against a duration that is already
    /// known: a camera command carries its own duration in milliseconds (read
    /// from the engine's OP45 handler), and a script that starts a camera move
    /// and then waits for it authors a delay that covers exactly that move. The
    /// ratio over every such pair of the corpus says what the delay counts.
    /// </summary>
    private static void CalibrateDelayAgainstCamera(string root)
    {
        var scripts = Directory.Exists(root)
            ? Directory.GetFiles(root, "*.dat", SearchOption.AllDirectories)
            : new[] { root };
        var pairs = new List<(string File, string Function, int Milliseconds, int Delay)>();
        foreach (var path in scripts)
        {
            DecompiledScript script;
            try
            {
                script = ScriptDecompiler.Decompile(path, null);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                continue;
            }
            foreach (var function in script.Functions.Where(value => value.IsCode))
            {
                for (var index = 0; index + 1 < function.Instructions.Count; index++)
                {
                    var moved = function.Instructions[index];
                    var waited = function.Instructions[index + 1];
                    if (moved.Opcode != 45 || waited.Opcode != 16) continue;
                    if (waited.Arguments.Count < 1) continue;
                    var milliseconds = ScriptCameraStateResolver.ReadDurationMs(moved);
                    var delay = waited.Arguments[0].IntValue;
                    if (milliseconds <= 0 || delay <= 0) continue;
                    pairs.Add((Path.GetFileName(path), function.Name, milliseconds, delay));
                }
            }
        }
        Console.WriteLine($"{scripts.Length} scripts, {pairs.Count} camera-move + delay pairs");
        var delays = new SortedDictionary<int, int>();
        foreach (var path in scripts)
        {
            DecompiledScript parsed;
            try
            {
                parsed = ScriptDecompiler.Decompile(path, null);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                continue;
            }
            foreach (var instruction in parsed.Functions.Where(value => value.IsCode)
                         .SelectMany(value => value.Instructions)
                         .Where(value => value.Opcode == 16 && value.Arguments.Count > 0))
            {
                var value = instruction.Arguments[0].IntValue;
                delays[value] = delays.GetValueOrDefault(value) + 1;
            }
        }
        var total = delays.Values.Sum();
        var odd = delays.Where(pair => (pair.Key & 1) != 0).Sum(pair => pair.Value);
        var rounded = delays.Where(pair => pair.Key % 100 == 0).Sum(pair => pair.Value);
        Console.WriteLine(
            $"  {total} delays, {odd} odd, {rounded} multiples of 100"
            + $"; most common: "
            + string.Join(", ", delays.OrderByDescending(pair => pair.Value).Take(8)
                .Select(pair => $"{pair.Key}x{pair.Value}")));
        foreach (var group in pairs
                     .GroupBy(value => (value.Milliseconds, value.Delay))
                     .OrderByDescending(group => group.Count())
                     .Take(15))
        {
            Console.WriteLine(
                $"  {group.Count(),5} x  camera {group.Key.Milliseconds,6} ms"
                + $"  delay {group.Key.Delay,6}"
                + $"  -> delay/ms = {group.Key.Delay / (double)group.Key.Milliseconds:0.###}"
                + $" ({group.First().File}/{group.First().Function})");
        }
        if (pairs.Count == 0) return;
        var ratios = pairs.Select(value => value.Delay / (double)value.Milliseconds)
            .OrderBy(value => value).ToArray();
        Console.WriteLine(
            $"  median delay/ms = {ratios[ratios.Length / 2]:0.####}"
            + $" (a delay counting milliseconds gives 1, one counting 60 Hz frames gives 0.06)");
    }

    /// <summary>
    /// Measures what unit Ani_Delay counts in. A script that starts an animation
    /// and then waits for it authors a delay that must match the length of the
    /// clip it just started, so comparing the two over every such pair of the
    /// file says whether the value counts preview frames, engine ticks or
    /// milliseconds — no guessing needed.
    /// </summary>
    private static void CalibrateDelay(string scriptPath)
    {
        var loader = new EditorProjectLoader(
            new OpsReader(), new GameAssetResolverFactory(), new PkgArchiveReader(),
            new AssetManifestReader(), new PhyreD3D11ModelReader(), new PhyreD3D11TextureReader());
        var session = loader.OpenScript(scriptPath, null);
        var gameDataPath = session.Script.GameDataPath
            ?? throw new ArgumentException("The script has no resolved game data directory.");
        var library = new ScriptAnimationLibrary(gameDataPath, session.Script.Header.SourcePath, null);
        var subject = ScriptSubjectResolver.Resolve(
            session.Script.Header.SourcePath, gameDataPath, library);
        var script = ScriptDecompiler.Decompile(scriptPath, null);
        var clips = new Dictionary<string, CpuAnimationClip?>(StringComparer.OrdinalIgnoreCase);
        var samples = new List<(string Function, string Clip, int Delay, float Seconds)>();

        foreach (var function in script.Functions.Where(value => value.IsCode))
        {
            for (var index = 0; index + 1 < function.Instructions.Count; index++)
            {
                var played = function.Instructions[index];
                var waited = function.Instructions[index + 1];
                if (played.Opcode != 34 || waited.Opcode != 16) continue;
                if (played.Arguments.Count < 2 || waited.Arguments.Count < 1) continue;
                var clipName = ScriptSceneStateResolver.ReadInstructionString(played.Arguments[1]);
                if (clipName.Length == 0) continue;
                var delay = waited.Arguments[0].IntValue;
                if (delay <= 0) continue;
                if (!clips.TryGetValue(clipName, out var clip))
                {
                    clip = subject is null
                        ? null
                        : loader.LoadAnimationAsset(subject.ModelAssetId, clipName, gameDataPath).Clip;
                    clips[clipName] = clip;
                }
                if (clip is null || clip.Duration <= 0f) continue;
                samples.Add((function.Name, clipName, delay, clip.Duration));
            }
        }

        Console.WriteLine($"{Path.GetFileName(scriptPath)}: {samples.Count} play+wait pairs");
        foreach (var sample in samples.Take(25))
        {
            Console.WriteLine(
                $"  {sample.Function,-22} {sample.Clip,-16} delay={sample.Delay,6}"
                + $" clip={sample.Seconds,7:0.###} s"
                + $" -> {sample.Delay / sample.Seconds,9:0.#} units per second");
        }
        if (samples.Count == 0) return;
        var rates = samples.Select(value => value.Delay / value.Seconds).OrderBy(value => value).ToArray();
        Console.WriteLine(
            $"  median {rates[rates.Length / 2]:0.#} units per second"
            + $" (min {rates[0]:0.#}, max {rates[^1]:0.#})");
    }

    /// <summary>
    /// Builds the timeline of a whole function and prints what it will play:
    /// its length and the commands it schedules, which is what the editor's
    /// looped playback runs on.
    /// </summary>
    private static void DumpFunctionTimeline(string scriptPath, string functionName)
    {
        var loader = new EditorProjectLoader(
            new OpsReader(), new GameAssetResolverFactory(), new PkgArchiveReader(),
            new AssetManifestReader(), new PhyreD3D11ModelReader(), new PhyreD3D11TextureReader());
        var session = loader.OpenScript(scriptPath, null);
        var gameDataPath = session.Script.GameDataPath;
        var library = gameDataPath is null
            ? null
            : new ScriptAnimationLibrary(gameDataPath, session.Script.Header.SourcePath, null);
        var systemLibrary = new ScriptSystemLibrary(session.Script.Header.SourcePath, null).Script;
        var subject = ScriptSubjectResolver.Resolve(
            session.Script.Header.SourcePath, gameDataPath, library);
        var script = ScriptDecompiler.Decompile(scriptPath, null);
        var function = script.Functions.Single(value =>
            value.IsCode && value.Name.Equals(functionName, StringComparison.Ordinal));
        if (ScriptSceneStateResolver.BuildFunctionTimeline(
                script, function, library, systemLibrary, subject) is not { } timeline)
        {
            Console.Error.WriteLine($"{functionName}: no timeline");
            Environment.ExitCode = 1;
            return;
        }
        Console.WriteLine(
            $"{timeline.FunctionName}: {timeline.Points.Count} points,"
            + $" {timeline.DurationFrames} frames"
            + $" ({timeline.DurationFrames / ScriptWaitDuration.PreviewFramesPerSecond:0.##} s),"
            + $" loop={timeline.LoopPlayback}");
        // Where the time goes: the instruction that precedes a gap is the one
        // that consumed it, so a scene that drags can be traced to its waits.
        var consumed = new Dictionary<string, (int Frames, int Count)>(StringComparer.Ordinal);
        for (var index = 0; index + 1 < timeline.Points.Count; index++)
        {
            var gap = timeline.Points[index + 1].Frame - timeline.Points[index].Frame;
            if (gap <= 0) continue;
            var name = timeline.Points[index].Instruction.Name;
            var previous = consumed.GetValueOrDefault(name);
            consumed[name] = (previous.Frames + gap, previous.Count + 1);
        }
        foreach (var entry in consumed.OrderByDescending(pair => pair.Value.Frames).Take(10))
        {
            Console.WriteLine(
                $"  {entry.Value.Frames,7} frames"
                + $" ({entry.Value.Frames / ScriptWaitDuration.PreviewFramesPerSecond,7:0.##} s)"
                + $" after {entry.Value.Count,4} x {entry.Key}");
        }
        // The path the replay walks, so a branch it should not have taken shows.
        Console.WriteLine("  path: " + string.Join(" ", timeline.Points
            .Where(point => !point.IsExternalScript)
            .Select(point => point.InstructionIndex)
            .Take(80)));

        // Which animations the scene starts, and when: an animation that is
        // re-issued over and over is one the preview would keep restarting.
        foreach (var point in timeline.Points.Where(value => value.Instruction.Opcode == 34).Take(25))
        {
            var arguments = point.Instruction.Arguments;
            Console.WriteLine(
                $"  frame {point.Frame,6}  animation"
                + $" entity={point.SubjectEntityId?.ToString() ?? "?"}"
                + $" {ScriptSceneStateResolver.ReadInstructionString(arguments[1])}"
                + $" loop={(arguments.Count > 2 ? arguments[2].IntValue : -1)}");
        }
    }

    /// <summary>
    /// Prints what a Phyre cluster is made of: its header, the type schema it
    /// carries, the objects it holds and the fixups that tie them together. This
    /// is the whole of what a writer would have to produce, so it is also how the
    /// size of that job is measured.
    /// </summary>
    private static void DumpPhyreCluster(string path, bool withMembers)
    {
        var data = ReadPhyreEntry(path);
        var cluster = new PhyreClusterReader().Read(data);
        var metadata = cluster.Metadata;
        var header = metadata.Header;
        Console.WriteLine(
            $"{Path.GetFileName(path)}: {data.Length} bytes,"
            + $" platform {metadata.PlatformId}, {(metadata.IsBigEndian ? "big" : "little")}-endian");
        Console.WriteLine(
            $"  header: size {header.Size}, namespace {header.PackedNamespaceSize} bytes,"
            + $" object data at {header.ObjectDataOffset}, {metadata.TotalDataSize} bytes of it");
        Console.WriteLine(
            $"  schema: {metadata.Types.Count} type names, {metadata.Classes.Count} classes,"
            + $" {metadata.Classes.Sum(value => value.Members.Count)} members");
        Console.WriteLine(
            $"  group sizes: {metadata.InstanceGroups.Sum(value => (long)value.ObjectsSize)} of objects,"
            + $" {metadata.InstanceGroups.Sum(value => (long)value.ArraysSize)} of arrays,"
            + $" {metadata.InstanceGroups.Sum(value => (long)value.Size)} declared");
        Console.WriteLine(
            $"  objects: {metadata.InstanceGroups.Count} instance groups,"
            + $" {metadata.InstanceGroups.Sum(value => (long)value.Count)} objects");
        Console.WriteLine(
            $"  fixups: {header.PointerFixupCount} pointer, {header.ArrayFixupCount} array,"
            + $" {header.PointerArrayFixupCount} pointer-array ({header.PointersInArraysCount} pointers),"
            + $" {header.UserFixupCount} user ({header.UserFixupDataSize} bytes)");
        Console.WriteLine($"  gpu payload starts at {cluster.Fixups.VramDataOffset}");
        // Every byte before the pixels has to belong to a structure a writer
        // would produce; whatever is left over is what is still not understood.
        // One 36-byte header per instance group, between the namespace and the
        // object data.
        var instanceHeaders = header.ObjectDataOffset - header.InstanceHeadersOffset;
        var accounted = header.Size
            + header.PackedNamespaceSize
            + instanceHeaders
            + metadata.TotalDataSize
            + header.PointerFixupSize
            + header.ArrayFixupSize
            + header.PointerArrayFixupSize
            + header.UserFixupCount * 12
            + header.UserFixupDataSize
            // The header-class section: one word per instance, sixteen bytes per
            // child entry, between the user fixups and the fixup tables.
            + header.HeaderClassInstanceCount * 4
            + header.HeaderClassChildCount * 16;
        Console.WriteLine(
            $"  accounting: header {header.Size} + namespace {header.PackedNamespaceSize}"
            + $" + instance headers {instanceHeaders} + objects {metadata.TotalDataSize}"
            + $" + fixups {header.PointerFixupSize + header.ArrayFixupSize + header.PointerArrayFixupSize}"
            + $" + user fixups {header.UserFixupCount * 12}+{header.UserFixupDataSize}"
            + $" + header classes {header.HeaderClassInstanceCount * 4 + header.HeaderClassChildCount * 16}"
            + $" = {accounted} of {cluster.Fixups.VramDataOffset}"
            + $" ({cluster.Fixups.VramDataOffset - accounted} unexplained)");
        foreach (var group in metadata.InstanceGroups)
        {
            Console.WriteLine(
                $"    group {group.Index}: {group.Count} x {group.ClassName ?? "?"}"
                + $" ({group.ObjectsSize} bytes of objects, {group.ArraysSize} of arrays)");
        }
        foreach (var fixup in cluster.Fixups.UserFixups.Take(8))
        {
            Console.WriteLine(
                $"    user fixup {fixup.Id}: {fixup.TypeName ?? fixup.TypeId.ToString()}"
                + $" = {fixup.Text ?? $"{fixup.Data.Length} bytes"}");
        }
        if (!withMembers) return;
        foreach (var descriptor in metadata.Classes)
        {
            Console.WriteLine(
                $"  class {descriptor.Index} {descriptor.Name}: {descriptor.Size} bytes,"
                + $" align {descriptor.Alignment}, {descriptor.Members.Count} members");
            foreach (var member in descriptor.Members)
            {
                Console.WriteLine(
                    $"      +{member.ValueOffset,4} {member.Name} : {member.TypeName ?? member.TypeId.ToString()}"
                    + $" ({member.Size} bytes{(member.IsDynamicArrayPointer ? ", dynamic array" : string.Empty)})");
            }
        }
    }

    /// <summary>Reads a .phyre from disk, or out of the package that holds it.</summary>
    private static byte[] ReadPhyreEntry(string path)
    {
        if (!path.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
        {
            return File.ReadAllBytes(path);
        }
        var archive = new PkgArchiveReader().Read(path);
        var entry = archive.Entries.FirstOrDefault(value =>
            value.Name.EndsWith(".phyre", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{Path.GetFileName(path)} holds no .phyre entry.");
        return archive.ReadEntry(entry);
    }

    /// <summary>Plays one effect and prints the nodes it holds at a given time.</summary>
    private static void DumpEffectNodes(string path, float time)
    {
        var effect = EffFileReader.Read(path);
        var frame = EffSimulation.Evaluate(effect, time);
        Console.WriteLine(
            $"{effect.EffectName} at {time:0.###}s: {frame.Nodes.Count} nodes"
            + $", runs for {EffSimulation.FiniteDuration(effect)?.ToString("0.###") ?? "ever"}"
            + (frame.Truncated ? " (the preview stopped short of an endless emitter)" : string.Empty));
        foreach (var node in frame.Nodes.Take(40))
        {
            var segment = effect.Segments[node.SegmentIndex];
            var normal = Vector3.TransformNormal(Vector3.UnitZ, node.Rotation);
            Console.WriteLine(
                $"  {new string(' ', node.Depth * 2)}#{node.SegmentIndex} {segment.Name}"
                + $" t={node.LocalTime:0.###} at {node.Position:0.##} scale={node.Scale:0.##}"
                + $" normal={normal:0.##} tint={node.ColorMultiply:0.##}"
                + $"{(node.Drawn ? string.Empty : " (container)")}");
        }
    }

    /// <summary>
    /// Compares the entities a Script_CallAniFun preview carries with the ones the
    /// plain replay of the same point resolves: a preview must not lose actors.
    /// </summary>
    private static void DumpAnimationCall(string scriptPath, string functionName, int index)
    {
        var loader = new EditorProjectLoader(
            new OpsReader(), new GameAssetResolverFactory(), new PkgArchiveReader(),
            new AssetManifestReader(), new PhyreD3D11ModelReader(), new PhyreD3D11TextureReader());
        var session = loader.OpenScript(scriptPath, null);
        var gameDataPath = session.Script.GameDataPath
            ?? throw new ArgumentException("The script has no resolved game data directory.");
        var library = new ScriptAnimationLibrary(gameDataPath, session.Script.Header.SourcePath, null);
        var systemLibrary = new ScriptSystemLibrary(session.Script.Header.SourcePath, null).Script;
        var script = ScriptDecompiler.Decompile(scriptPath, null);
        var function = script.Functions.Single(value =>
            value.IsCode && value.Name.Equals(functionName, StringComparison.Ordinal));
        var instruction = function.Instructions[index];
        var resolved = ScriptSceneStateResolver.Resolve(
            script, function, index, library, systemLibrary);
        Console.WriteLine(
            $"{instruction.Name} #{index}: replay carries {resolved.Entities.Count} entities"
            + $" ({resolved.Entities.Values.Count(value => value.HasPosition)} placed)");
        foreach (var entity in resolved.Entities.Values.Where(value => value.HasPosition))
        {
            Console.WriteLine(
                $"    replay  {entity.EntityId,5} {entity.DisplayName,-20} {entity.Position}"
                + $" motion={(entity.Motion is null ? "-" : $"{entity.Motion.StartFrame}..{entity.Motion.EndFrame}")}");
        }
        var timeline = ScriptSceneStateResolver.BuildAnimationCallTimeline(
            script, function, instruction, library, systemLibrary);
        if (timeline is null)
        {
            Console.WriteLine("  no preview timeline");
            return;
        }
        Console.WriteLine(
            $"  initial state: {timeline.InitialState.Entities.Count} entities"
            + $" ({timeline.InitialState.Entities.Values.Count(value => value.HasPosition)} placed)");
        foreach (var entity in timeline.InitialState.Entities.Values.Where(value => value.HasPosition))
        {
            var shown = entity.Motion?.PositionAt(0f) ?? entity.Position;
            Console.WriteLine(
                $"    preview {entity.EntityId,5} {entity.DisplayName,-20} shown at {shown}"
                + $" motion={(entity.Motion is null ? "-" : $"{entity.Motion.StartFrame}..{entity.Motion.EndFrame}")}");
        }
        foreach (var point in timeline.Points)
        {
            Console.WriteLine(
                $"  frame {point.Frame,5} {point.Instruction.Name,-22}"
                + $" before={point.Before.Entities.Count}"
                + $" after={point.After.Entities.Count}"
                + $" placed={point.After.Entities.Values.Count(value => value.HasPosition)}");
        }
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
            foreach (var attachment in entity.Attachments ?? new Dictionary<string, ScriptEntityAttachment>())
            {
                report.AppendLine(
                    $"    carries on {attachment.Key}: "
                    + (attachment.Value.ModelAssetId.Length == 0 ? "(default)" : attachment.Value.ModelAssetId)
                    + $" visible={attachment.Value.Visible}");
            }
            foreach (var slot in entity.EffectSlots ?? new Dictionary<int, string>())
                report.AppendLine($"    effect slot {slot.Key}: {slot.Value}");
            foreach (var effect in entity.Effects ?? new Dictionary<int, ScriptEffectInstance>())
            {
                report.AppendLine(
                    $"    playing #{effect.Key}: {effect.Value.EffectPath} slot={effect.Value.Slot}"
                    + $" anchor={effect.Value.AnchorEntityId}"
                    + $"{(effect.Value.AnchorNode.Length == 0 ? string.Empty : "/" + effect.Value.AnchorNode)}"
                    + $" at {effect.Value.Position} scale={effect.Value.Scale}"
                    + $" since frame {effect.Value.StartFrame}");
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
