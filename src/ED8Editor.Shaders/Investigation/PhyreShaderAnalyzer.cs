using System.Text.RegularExpressions;

namespace ED8Editor.Shaders.Investigation;

/// <summary>
/// Analyzes decompiled shader disassembly for patterns that indicate
/// essential game-specific rendering functions.
/// </summary>
public sealed partial class PhyreShaderAnalyzer
{
    // Constant buffer slot 0 is the Phyre global CB
    private const int PhyreGlobalCbSlot = 0;

    /// <summary>
    /// Analyzes all decompiled shader variants and returns a summary of
    /// common patterns, essential functions, and engine dependencies.
    /// </summary>
    public ShaderAnalysisReport Analyze(PhyreShaderSource source)
    {
        var allDisassembly = string.Join("\n", source.Stages.SelectMany(s =>
            new[] { s.VertexShaderDisassembly, s.FragmentShaderDisassembly }));

        return new ShaderAnalysisReport(
            ContextSwitches: AnalyzeContextSwitches(source),
            ConstantBufferUsage: AnalyzeConstantBuffers(allDisassembly),
            EngineFunctions: FindEngineFunctions(allDisassembly),
            TextureSlots: FindTextureSlots(allDisassembly),
            SamplerSlots: FindSamplerSlots(allDisassembly),
            InputSemantics: AnalyzeInputSemantics(source),
            OutputSemantics: AnalyzeOutputSemantics(source),
            CommonPatterns: FindCommonPatterns(source),
            PerPermutationDifferences: AnalyzePermutationDifferences(source),
            Summary: GenerateSummary(source));
    }

    private static ShaderContextSwitchReport AnalyzeContextSwitches(PhyreShaderSource source)
    {
        var switches = new List<ContextSwitchInfo>();
        foreach (var name in source.ContextSwitchNames)
        {
            var values = source.Contexts
                .Where(c => c.PackedValues.TryGetValue(name, out _))
                .Select(c => c.PackedValues[name])
                .Distinct()
                .ToArray();

            switches.Add(new ContextSwitchInfo(name, values));
        }
        return new ShaderContextSwitchReport(switches);
    }

    private static ShaderConstantBufferReport AnalyzeConstantBuffers(string disassembly)
    {
        // Find cb0 references (Phyre global constant buffer)
        var cb0Refs = CbRegisterRegex().Matches(disassembly)
            .Select(m => m.Value)
            .Distinct()
            .ToArray();

        // Find specific offset patterns used by the engine
        var reservedOffsets = FindReservedCbOffsets(disassembly);

        return new ShaderConstantBufferReport(cb0Refs, reservedOffsets);
    }

    private static IReadOnlyList<ShaderCbOffset> FindReservedCbOffsets(string disassembly)
    {
        var offsets = new List<ShaderCbOffset>();

        // Pattern: cb0[N].w or similar - look for array indexing into cb0
        var indexedMatches = CbIndexedAccessRegex().Matches(disassembly);
        foreach (Match match in indexedMatches)
        {
            if (match.Groups.Count >= 2 && int.TryParse(match.Groups[1].Value, out var index))
            {
                offsets.Add(new ShaderCbOffset(
                    index,
                    $"cb0[{index}]",
                    index <= 29 ? CbReservedRange.PhyreReserved : CbReservedRange.WorldMatrix,
                    index <= 29 ? "Reserved Phyre slot" : "World/View/Projection matrix data"));
            }
        }

        return offsets;
    }

    private static ShaderEngineFunctions FindEngineFunctions(string disassembly)
    {
        // Look for calls to known engine helper patterns
        // In compiled bytecode, calls appear as 'call' or direct jumps
        var calls = CallInstructionRegex().Matches(disassembly)
            .Select(m => m.Groups[1].Value.Trim())
            .Distinct()
            .ToArray();

        // Look for includes or well-known Phyre function patterns
        var lighting = disassembly.Contains("light", StringComparison.OrdinalIgnoreCase);
        var fog = disassembly.Contains("fog", StringComparison.OrdinalIgnoreCase);
        var shadow = disassembly.Contains("shadow", StringComparison.OrdinalIgnoreCase);
        var skinning = disassembly.Contains("bone", StringComparison.OrdinalIgnoreCase) ||
                       disassembly.Contains("skin", StringComparison.OrdinalIgnoreCase);
        var gMaterialId = disassembly.Contains("gameMaterial", StringComparison.OrdinalIgnoreCase) ||
                          disassembly.Contains("materialID", StringComparison.OrdinalIgnoreCase);

        return new ShaderEngineFunctions(calls, lighting, fog, shadow, skinning, gMaterialId);
    }

    private static IReadOnlyList<ShaderTextureSlot> FindTextureSlots(string disassembly)
    {
        var slots = new List<ShaderTextureSlot>();
        var matches = TextureDeclarationRegex().Matches(disassembly);
        foreach (Match match in matches)
        {
            slots.Add(new ShaderTextureSlot(
                match.Groups[2].Value.Trim(),
                int.Parse(match.Groups[1].Value),
                match.Groups[3].Value.Trim()));
        }
        return slots;
    }

    private static IReadOnlyList<ShaderSamplerSlot> FindSamplerSlots(string disassembly)
    {
        var slots = new List<ShaderSamplerSlot>();
        var matches = SamplerDeclarationRegex().Matches(disassembly);
        foreach (Match match in matches)
        {
            slots.Add(new ShaderSamplerSlot(
                match.Groups[2].Value.Trim(),
                int.Parse(match.Groups[1].Value)));
        }
        return slots;
    }

    private static IReadOnlyList<ShaderSemantic> AnalyzeInputSemantics(PhyreShaderSource source)
    {
        return source.Stages
            .SelectMany(s => s.Inputs)
            .Select(si => si.Name)
            .Distinct()
            .Select(name => new ShaderSemantic(name, SemanticDirection.Input))
            .ToArray();
    }

    private static IReadOnlyList<ShaderSemantic> AnalyzeOutputSemantics(PhyreShaderSource source)
    {
        var semantics = new HashSet<string>();
        foreach (var stage in source.Stages)
        {
            // VS output = PS input. Look for SV_Position, TEXCOORD, etc. in PS
            ExtractSemantics(stage.FragmentShaderDisassembly, "input", semantics);
            ExtractSemantics(stage.VertexShaderDisassembly, "output", semantics);
        }
        return semantics.Select(name => new ShaderSemantic(name, SemanticDirection.Output)).ToArray();
    }

    private static void ExtractSemantics(string disassembly, string direction, HashSet<string> semantics)
    {
        var matches = SemanticRegex().Matches(disassembly);
        foreach (Match match in matches)
        {
            semantics.Add(match.Groups[1].Value.Trim());
        }
    }

    private static IReadOnlyList<string> FindCommonPatterns(PhyreShaderSource source)
    {
        var patterns = new List<string>();

        // Compare all vertex shaders to find common instructions
        var allVs = source.Stages.Select(s => s.VertexShaderDisassembly).ToArray();
        if (allVs.Length >= 2)
        {
            var commonVs = FindCommonInstructions(allVs);
            if (commonVs.Count > 0)
                patterns.Add($"Common VS instructions across {allVs.Length} permutations: {commonVs.Count} shared operations");
        }

        // Compare all fragment shaders
        var allPs = source.Stages.Select(s => s.FragmentShaderDisassembly).ToArray();
        if (allPs.Length >= 2)
        {
            var commonPs = FindCommonInstructions(allPs);
            if (commonPs.Count > 0)
                patterns.Add($"Common PS instructions across {allPs.Length} permutations: {commonPs.Count} shared operations");
        }

        // Check for gameMaterialID references
        foreach (var stage in source.Stages)
        {
            if (stage.VertexShaderDisassembly.Contains("gameMaterialID") ||
                stage.FragmentShaderDisassembly.Contains("gameMaterialID"))
            {
                patterns.Add($"Permutation {stage.PermutationIndex} ({stage.PassName}) references gameMaterialID");
            }
        }

        // Check for standard Phyre patterns
        var allText = string.Join("\n", allVs) + "\n" + string.Join("\n", allPs);
        if (allText.Contains("WorldViewProjection")) patterns.Add("Uses WorldViewProjection matrix");
        if (allText.Contains("_phyreReserved")) patterns.Add("References _phyreReserved array");

        return patterns;
    }

    private static HashSet<string> FindCommonInstructions(string[] shaders)
    {
        if (shaders.Length == 0) return new HashSet<string>();

        // Extract instruction mnemonics from each shader
        var instructionSets = shaders.Select(s =>
        {
            var instructions = new HashSet<string>();
            foreach (Match m in InstructionRegex().Matches(s))
            {
                instructions.Add(m.Groups[1].Value.Trim());
            }
            return instructions;
        }).ToArray();

        // Find intersection
        var common = new HashSet<string>(instructionSets[0]);
        for (var i = 1; i < instructionSets.Length; i++)
            common.IntersectWith(instructionSets[i]);

        return common;
    }

    private static ShaderPermutationDifferences AnalyzePermutationDifferences(PhyreShaderSource source)
    {
        var differences = new List<PermutationDiff>();

        foreach (var ctxSwitch in source.ContextSwitchNames)
        {
            var groups = source.Stages
                .GroupBy(s => s.ContextLabel)
                .ToDictionary(g => g.Key, g => g.ToArray());

            if (groups.Count <= 1) continue;

            var keys = groups.Keys.ToArray();
            for (var i = 0; i < keys.Length - 1; i++)
            {
                for (var j = i + 1; j < keys.Length; j++)
                {
                    var a = groups[keys[i]];
                    var b = groups[keys[j]];
                    var sizeDiff = (a.Sum(s => s.VertexShaderDisassembly.Length + s.FragmentShaderDisassembly.Length) -
                                    b.Sum(s => s.VertexShaderDisassembly.Length + s.FragmentShaderDisassembly.Length));

                    if (sizeDiff != 0)
                    {
                        differences.Add(new PermutationDiff(
                            ctxSwitch, keys[i], keys[j], sizeDiff));
                    }
                }
            }
        }

        return new ShaderPermutationDifferences(differences);
    }

    private static string GenerateSummary(PhyreShaderSource source)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== Shader Analysis Summary ===");
        sb.AppendLine($"Passes: {source.Stages.Select(s => s.PassName).Distinct().Count()}");
        sb.AppendLine($"Total permutations: {source.Stages.Count}");
        sb.AppendLine($"Context switches: {source.ContextSwitchNames.Count} ({string.Join(", ", source.ContextSwitchNames)})");
        sb.AppendLine($"Context variants: {source.Contexts.Count}");
        sb.AppendLine($"Material switches: {source.MaterialSwitches.Count}");

        var (minCb, maxCb) = (source.Stages.Min(s => s.VertexConstantBufferSize),
                               source.Stages.Max(s => s.VertexConstantBufferSize));
        sb.AppendLine($"VS constant buffer size: {minCb}-{maxCb} bytes");
        (minCb, maxCb) = (source.Stages.Min(s => s.FragmentConstantBufferSize),
                           source.Stages.Max(s => s.FragmentConstantBufferSize));
        sb.AppendLine($"PS constant buffer size: {minCb}-{maxCb} bytes");

        return sb.ToString();
    }

    // Regex patterns
    [GeneratedRegex(@"cb0\[\d+\]", RegexOptions.IgnoreCase)]
    private static partial Regex CbRegisterRegex();

    [GeneratedRegex(@"cb0\[(\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex CbIndexedAccessRegex();

    [GeneratedRegex(@"call\s+(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex CallInstructionRegex();

    [GeneratedRegex(@"declare\s+resource\s+texture\s+t(\d+)\s*;\s*//\s*(\S+)\s*\((\S+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex TextureDeclarationRegex();

    [GeneratedRegex(@"declare\s+sampler\s+s(\d+)\s*;\s*//\s*(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex SamplerDeclarationRegex();

    [GeneratedRegex(@"(?:name|semantic)\s+index:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SemanticRegex();

    [GeneratedRegex(@"^\s*(\w+)\s", RegexOptions.Multiline)]
    private static partial Regex InstructionRegex();
}

// --- Report types ---

public sealed record ShaderAnalysisReport(
    ShaderContextSwitchReport ContextSwitches,
    ShaderConstantBufferReport ConstantBufferUsage,
    ShaderEngineFunctions EngineFunctions,
    IReadOnlyList<ShaderTextureSlot> TextureSlots,
    IReadOnlyList<ShaderSamplerSlot> SamplerSlots,
    IReadOnlyList<ShaderSemantic> InputSemantics,
    IReadOnlyList<ShaderSemantic> OutputSemantics,
    IReadOnlyList<string> CommonPatterns,
    ShaderPermutationDifferences PerPermutationDifferences,
    string Summary);

public sealed record ShaderContextSwitchReport(IReadOnlyList<ContextSwitchInfo> Switches);
public sealed record ContextSwitchInfo(string Name, uint[] Values);

public sealed record ShaderConstantBufferReport(
    string[] Cb0References,
    IReadOnlyList<ShaderCbOffset> ReservedOffsets);

public sealed record ShaderCbOffset(
    int Index, string Pattern, CbReservedRange Range, string Description);

public enum CbReservedRange
{
    PhyreReserved,   // indices 0-28
    WorldMatrix,     // indices 29+
    Unknown
}

public sealed record ShaderEngineFunctions(
    string[] CallTargets,
    bool HasLighting,
    bool HasFog,
    bool HasShadow,
    bool HasSkinning,
    bool HasGameMaterialId);

public sealed record ShaderTextureSlot(string Name, int Slot, string Type);
public sealed record ShaderSamplerSlot(string Name, int Slot);

public sealed record ShaderSemantic(string Name, SemanticDirection Direction);
public enum SemanticDirection { Input, Output }

public sealed record ShaderPermutationDifferences(IReadOnlyList<PermutationDiff> Differences);
public sealed record PermutationDiff(
    string ContextSwitch, string VariantA, string VariantB, int BytecodeSizeDiff);
