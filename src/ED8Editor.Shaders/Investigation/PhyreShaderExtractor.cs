using System.Collections.Concurrent;
using ED8Editor.Phyre;

namespace ED8Editor.Shaders.Investigation;

/// <summary>
/// Batch extracts all .fx.phyre shaders from game package files,
/// decompiles them, and produces comprehensive analysis.
/// </summary>
public sealed class PhyreShaderExtractor
{
    /// <summary>
    /// Discovers all .fx.phyre files in the given directory tree and
    /// extracts their metadata without full decompilation.
    /// </summary>
    public IReadOnlyList<ShaderFileInfo> DiscoverShaders(string gameDataPath)
    {
        var shaderFiles = new ConcurrentBag<ShaderFileInfo>();

        var fxFiles = Directory.EnumerateFiles(gameDataPath, "*.fx.phyre", SearchOption.AllDirectories);

        Parallel.ForEach(fxFiles, filePath =>
        {
            try
            {
                var data = File.ReadAllBytes(filePath);
                var reader = new PhyreEffectRenderPassReader();
                var metadata = reader.ReadMetadata(data);

                var info = new ShaderFileInfo(
                    filePath,
                    Path.GetFileName(filePath),
                    AssetId: ExtractAssetId(data),
                    Passes: metadata.RenderPassStates.Keys.ToArray(),
                    ContextSwitches: metadata.Program?.ContextSwitches?.ToArray() ?? Array.Empty<string>(),
                    ContextCount: metadata.Program?.Contexts?.Count ?? 0,
                    PermutationCount: metadata.Program?.SceneRenderPasses.Values
                        .Sum(p => p.Permutations.Count) ?? 0,
                    MaterialSwitches: metadata.MaterialSwitches.Keys.ToArray(),
                    FileSize: data.Length);

                shaderFiles.Add(info);
            }
            catch (Exception ex)
            {
                shaderFiles.Add(new ShaderFileInfo(
                    filePath, Path.GetFileName(filePath), null,
                    Array.Empty<string>(), Array.Empty<string>(), 0, 0,
                    Array.Empty<string>(), 0, ex.Message));
            }
        });

        return shaderFiles.OrderBy(f => f.FileName).ToArray();
    }

    /// <summary>
    /// Performs full analysis on all discovered shaders and produces
    /// a comprehensive report of common patterns across the game.
    /// </summary>
    public GlobalShaderReport AnalyzeAllShaders(IReadOnlyList<ShaderFileInfo> shaders, IProgress<string>? progress = null)
    {
        var decompiler = new PhyreShaderDecompiler();
        var analyzer = new PhyreShaderAnalyzer();
        var analyses = new ConcurrentDictionary<string, ShaderAnalysisReport>();

        var shadersToAnalyze = shaders
            .Where(s => s.Error == null)
            .Take(50) // Limit for initial analysis
            .ToArray();

        var processed = 0;
        Parallel.ForEach(shadersToAnalyze, shader =>
        {
            try
            {
                var data = File.ReadAllBytes(shader.FilePath);
                var source = decompiler.Decompile(data);
                var analysis = analyzer.Analyze(source);
                analyses.TryAdd(shader.FileName, analysis);

                var count = Interlocked.Increment(ref processed);
                progress?.Report($"Analyzed {count}/{shadersToAnalyze.Length}: {shader.FileName}");
            }
            catch
            {
                Interlocked.Increment(ref processed);
            }
        });

        return BuildGlobalReport(shaders, analyses);
    }

    private static GlobalShaderReport BuildGlobalReport(
        IReadOnlyList<ShaderFileInfo> allShaders,
        ConcurrentDictionary<string, ShaderAnalysisReport> analyses)
    {
        // Aggregate context switches across all shaders
        var allSwitches = new HashSet<string>();
        var allPassTypes = new HashSet<string>();
        var allMaterialSwitches = new HashSet<string>();
        var cbSizes = new List<int>();

        foreach (var (_, report) in analyses)
        {
            foreach (var sw in report.ContextSwitches.Switches)
                allSwitches.Add(sw.Name);
        }

        foreach (var shader in allShaders)
        {
            foreach (var pass in shader.Passes)
                allPassTypes.Add(pass);
            foreach (var sw in shader.MaterialSwitches)
                allMaterialSwitches.Add(sw);
        }

        return new GlobalShaderReport(
            TotalShaders: allShaders.Count,
            AnalyzedShaders: analyses.Count,
            ShadersWithErrors: allShaders.Count(s => s.Error != null),
            AllContextSwitches: allSwitches.OrderBy(s => s).ToArray(),
            AllPassTypes: allPassTypes.OrderBy(s => s).ToArray(),
            AllMaterialSwitches: allMaterialSwitches.OrderBy(s => s).ToArray(),
            ShaderFiles: allShaders,
            PerShaderAnalyses: analyses.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));
    }

    private static string? ExtractAssetId(byte[] data)
    {
        try
        {
            var cluster = new PhyreClusterReader().Read(data);
            var assetGroup = cluster.Metadata.InstanceGroups
                .FirstOrDefault(g => g.ClassName == "PAssetReference");
            if (assetGroup == null) return null;

            // The asset ID is typically the first user fixup text
            var fixup = cluster.Fixups.UserFixups.FirstOrDefault();
            return fixup?.Text;
        }
        catch
        {
            return null;
        }
    }
}

public sealed record ShaderFileInfo(
    string FilePath,
    string FileName,
    string? AssetId,
    string[] Passes,
    string[] ContextSwitches,
    int ContextCount,
    int PermutationCount,
    string[] MaterialSwitches,
    long FileSize,
    string? Error = null);

public sealed record GlobalShaderReport(
    int TotalShaders,
    int AnalyzedShaders,
    int ShadersWithErrors,
    string[] AllContextSwitches,
    string[] AllPassTypes,
    string[] AllMaterialSwitches,
    IReadOnlyList<ShaderFileInfo> ShaderFiles,
    IReadOnlyDictionary<string, ShaderAnalysisReport> PerShaderAnalyses);
