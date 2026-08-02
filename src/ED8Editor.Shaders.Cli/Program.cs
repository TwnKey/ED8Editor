using System.Text.Json;
using ED8Editor.Packages;
using ED8Editor.Phyre;
using ED8Editor.Shaders.Investigation;
using ED8Editor.Shaders.Compilation;
using Vortice.D3DCompiler;
using Vortice.Direct3D11.Shader;

// ============================================================
// ED8Editor.Shaders.Cli — Cold Steel 1 Shader Analyzer
// ============================================================
// Usage:
//   dotnet run                                    → full analysis
//   dotnet run -- --generate <outputDir>          → generate compat HLSL
//   dotnet run -- --build <hlslFile> <outputPath>  → compile HLSL to fx.phyre
// ============================================================

var generateMode = args.Length > 0 && args[0] == "--generate";
var buildMode = args.Length > 0 && args[0] == "--build";
var outputDir = generateMode && args.Length > 1 ? args[1] : ".";

var gamePath = generateMode && args.Length > 2
    ? args[2]
    : @"C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\asset\D3D11";

if (!Directory.Exists(gamePath))
{
    Console.Error.WriteLine($"ERROR: Game path not found: {gamePath}");
    return 1;
}

if (generateMode)
{
    return GenerateCompatibilityShader(gamePath, outputDir);
}

if (buildMode)
{
    var hlslFile = args.Length > 1 ? args[1] : "tmp-inspect/compat-shader/cs1_compat_ps_Basic.hlsl";
    var outputPath = args.Length > 2 ? args[2] : "tmp-inspect/compat-shader/custom.fx.phyre";
    return BuildFxPhyre(hlslFile, outputPath);
}

Console.WriteLine($"=== Cold Steel 1 Shader Analysis ===");
Console.WriteLine($"Game path: {gamePath}");
Console.WriteLine();

// Step 1: Extract all .fx.phyre shaders from .pkg files
Console.WriteLine("Step 1: Extracting .fx.phyre shaders from packages...");
var pkgFiles = Directory.GetFiles(gamePath, "*.pkg");
Console.WriteLine($"  Found {pkgFiles.Length} .pkg files");

var reader = new PkgArchiveReader();
var shaderData = new Dictionary<string, (string PkgName, byte[] Data)>();
var totalEntries = 0;

foreach (var pkgPath in pkgFiles)
{
    try
    {
        var archive = reader.Read(pkgPath);
        var pkgName = Path.GetFileName(pkgPath);
        foreach (var entry in archive.Entries)
        {
            totalEntries++;
            if (!entry.Name.EndsWith(".fx.phyre", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var data = archive.ReadEntry(entry);
                var key = $"{pkgName}/{entry.Name}";
                if (!shaderData.ContainsKey(key))
                    shaderData[key] = (pkgName, data);
            }
            catch { /* skip unreadable entries */ }
        }
    }
    catch { /* skip unreadable packages */ }
}

Console.WriteLine($"  Scanned {totalEntries} entries across {pkgFiles.Length} packages");
Console.WriteLine($"  Found {shaderData.Count} unique .fx.phyre shaders");
Console.WriteLine();

if (shaderData.Count == 0)
{
    Console.WriteLine("No shaders found. Make sure the game is installed at the right path.");
    return 1;
}

// Step 2: Quick metadata scan (fast, no bytecode decompilation)
Console.WriteLine("Step 2: Quick metadata scan...");
var decompiler = new PhyreShaderDecompiler();
var analyzer = new PhyreShaderAnalyzer();

var allSwitches = new HashSet<string>();
var allPassTypes = new HashSet<string>();
var allMaterialSwitches = new HashSet<string>();
var shaderInfos = new List<ShaderSummaryInfo>();
var errors = 0;

foreach (var (key, (pkgName, data)) in shaderData)
{
    try
    {
        var metadata = new PhyreEffectRenderPassReader().ReadMetadata(data);
        var program = metadata.Program;

        var switches = program?.ContextSwitches ?? Array.Empty<string>();
        var passNames = metadata.RenderPassStates.Keys.ToArray();
        var matSwitches = metadata.MaterialSwitches.Keys.ToArray();
        var permCount = program?.SceneRenderPasses.Values.Sum(p => p.Permutations.Count) ?? 0;

        foreach (var sw in switches) allSwitches.Add(sw);
        foreach (var pass in passNames) allPassTypes.Add(pass);
        foreach (var ms in matSwitches) allMaterialSwitches.Add(ms);

        shaderInfos.Add(new ShaderSummaryInfo(
            pkgName, key, switches.ToArray(), passNames, matSwitches,
            metadata.Program?.Contexts?.Count ?? 0, permCount, data.Length));
    }
    catch (Exception ex)
    {
        errors++;
        if (errors <= 3)
            Console.Error.WriteLine($"  WARNING: Failed to read {key}: {ex.Message}");
    }
}

Console.WriteLine($"  Scanned {shaderInfos.Count} shaders ({errors} errors)");
Console.WriteLine();

// Step 3: Print summary
Console.WriteLine("========================================");
Console.WriteLine("        SHADER ANALYSIS REPORT");
Console.WriteLine("========================================");
Console.WriteLine();
Console.WriteLine($"Total shaders:     {shaderInfos.Count}");
Console.WriteLine($"Parse errors:      {errors}");
Console.WriteLine();

Console.WriteLine("--- CONTEXT SWITCHES (all unique) ---");
foreach (var sw in allSwitches.OrderBy(s => s))
{
    var usageCount = shaderInfos.Count(s => s.Switches.Contains(sw));
    Console.WriteLine($"  {sw}  (used by {usageCount} shaders)");
}
Console.WriteLine();

Console.WriteLine("--- RENDER PASS TYPES (all unique) ---");
foreach (var pass in allPassTypes.OrderBy(s => s))
{
    var usageCount = shaderInfos.Count(s => s.Passes.Contains(pass));
    Console.WriteLine($"  {pass}  ({usageCount} shaders)");
}
Console.WriteLine();

Console.WriteLine("--- MATERIAL SWITCHES (all unique) ---");
if (allMaterialSwitches.Count == 0)
    Console.WriteLine("  (none)");
else
    foreach (var ms in allMaterialSwitches.OrderBy(s => s))
        Console.WriteLine($"  {ms}");
Console.WriteLine();

// Step 4: Detailed analysis of a representative sample
Console.WriteLine("--- SAMPLE DETAILED ANALYSIS (5 diverse shaders) ---");
var samples = shaderInfos
    .GroupBy(s => s.PackageName[..Math.Min(3, s.PackageName.Length)])
    .Select(g => g.First())
    .Take(5)
    .ToArray();

foreach (var sample in samples)
{
    Console.WriteLine();
    Console.WriteLine($"  Shader: {sample.FileName}");
    Console.WriteLine($"  Package: {sample.PackageName}");
    Console.WriteLine($"  Passes: {string.Join(", ", sample.Passes)}");
    Console.WriteLine($"  Switches: {string.Join(", ", sample.Switches)}");
    Console.WriteLine($"  Contexts: {sample.ContextCount}, Permutations: {sample.PermutationCount}");
    Console.WriteLine($"  Size: {sample.FileSize / 1024.0:F1} KB");

    // Full decode
    try
    {
        var data = shaderData[sample.FileName].Data;
        var source = decompiler.Decompile(data);
        var report = analyzer.Analyze(source);
        Console.WriteLine($"  {report.Summary.Replace("\n", "\n  ")}");

        // Show engine function presence
        var ef = report.EngineFunctions;
        var features = new List<string>();
        if (ef.HasLighting) features.Add("LIGHTING");
        if (ef.HasFog) features.Add("FOG");
        if (ef.HasShadow) features.Add("SHADOW");
        if (ef.HasSkinning) features.Add("SKINNING");
        if (ef.HasGameMaterialId) features.Add("gameMaterialID");
        Console.WriteLine($"  Detected features: {(features.Count > 0 ? string.Join(", ", features) : "(basic only)")}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Full analysis failed: {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine("--- FILE SIZE DISTRIBUTION ---");
var sizes = shaderInfos.Select(s => s.FileSize / 1024).OrderBy(s => s).ToArray();
Console.WriteLine($"  Min: {sizes[0]} KB, Max: {sizes[^1]} KB, Median: {sizes[sizes.Length / 2]} KB");
Console.WriteLine($"  < 100 KB: {sizes.Count(s => s < 100)}, 100-500 KB: {sizes.Count(s => s >= 100 && s < 500)}, 500+ KB: {sizes.Count(s => s >= 500)}");

Console.WriteLine();
Console.WriteLine("--- DEEP PARAMETER DUMP (1 main shader: ed8.fx.phyre from first package) ---");
var mainShader = shaderData.FirstOrDefault(kv => kv.Key.EndsWith("ed8.fx.phyre", StringComparison.OrdinalIgnoreCase) && kv.Value.Data.Length > 100_000);
if (mainShader.Value.Data is not null)
{
    DumpShaderParameters(mainShader.Value.Data);
}
Console.WriteLine();

Console.WriteLine("--- BYTECODE USAGE: what the shader ACTUALLY reads ---");
var mainShader2 = shaderData.FirstOrDefault(kv => kv.Key.EndsWith("ed8.fx.phyre", StringComparison.OrdinalIgnoreCase) && kv.Value.Data.Length > 100_000);
if (mainShader2.Value.Data is not null)
{
    AnalyzeBytecodeUsage(mainShader2.Value.Data);
}
Console.WriteLine();

Console.WriteLine("=== ANALYSIS COMPLETE ===");
return 0;

// ============================================================
static void AnalyzeBytecodeUsage(byte[] fxData)
{
    var reader = new PhyreEffectRenderPassReader();
    var metadata = reader.ReadMetadata(fxData);
    if (metadata.Program?.SceneRenderPasses.Values.FirstOrDefault()?.Permutations.FirstOrDefault() is not { } perm)
    {
        Console.WriteLine("  No permutation found.");
        return;
    }

    // Use D3D Reflect to see what's in the constant buffer and what's actually read
    try
    {
        using var vsRefl = Vortice.D3DCompiler.Compiler.Reflect<Vortice.Direct3D11.Shader.ID3D11ShaderReflection>(perm.VertexProgram.Bytecode);
        using var psRefl = Vortice.D3DCompiler.Compiler.Reflect<Vortice.Direct3D11.Shader.ID3D11ShaderReflection>(perm.FragmentProgram.Bytecode);

        Console.WriteLine("  === VERTEX SHADER ===");
        Console.WriteLine($"  Bytecode: {perm.VertexProgram.Bytecode.Length} bytes, CB size: {perm.VertexProgram.ConstantBufferSize}");
        DumpReflectedBuffers(vsRefl, "VS");

        Console.WriteLine("  === FRAGMENT SHADER ===");
        Console.WriteLine($"  Bytecode: {perm.FragmentProgram.Bytecode.Length} bytes, CB size: {perm.FragmentProgram.ConstantBufferSize}");
        DumpReflectedBuffers(psRefl, "PS");

        // Search bytecode for cb0 offset patterns matching known parameters
        Console.WriteLine("  === BYTECODE PATTERN SEARCH ===");
        var knownOffsets = new Dictionary<uint, string>
        {
            [0x180] = "PhyreContextSwitches",
            [0x184] = "PhyreMaterialSwitches",
            [0x1D0] = "PerMaterialMainLightClampFactor",
            [0x1DC] = "GameMaterialID",
            [0x200] = "GameMaterialTexcoord",
            [0x218] = "AlphaThreshold",
            [0x21C] = "FogRatio",
            [0x220] = "ShadowColorShift",
            [0x1E0] = "GameMaterialDiffuse",
            [0x1F0] = "GameMaterialEmission",
            [0x2E0] = "GameEdgeParameters",
        };

        var vsBytes = perm.VertexProgram.Bytecode;
        var psBytes = perm.FragmentProgram.Bytecode;

        foreach (var (offset, name) in knownOffsets.OrderBy(kv => kv.Key))
        {
            var vsRefs = CountCbOffsetReferences(vsBytes, offset);
            var psRefs = CountCbOffsetReferences(psBytes, offset);
            var total = vsRefs + psRefs;
            var mark = total > 0 ? "USED" : "(unused)";
            Console.WriteLine($"    0x{offset:X3} {name}: VS={vsRefs} PS={psRefs} → {mark}");
        }

        // Also search for interesting strings in the bytecode
        Console.WriteLine("  === STRING PATTERNS IN BYTECODE ===");
        var allBytes = new byte[vsBytes.Length + psBytes.Length];
        vsBytes.CopyTo(allBytes, 0);
        psBytes.CopyTo(allBytes, vsBytes.Length);
        var ascii = System.Text.Encoding.ASCII.GetString(allBytes);
        foreach (var word in new[] { "GameMaterial", "gameMaterial", "Light", "Shadow", "Fog", "fog", "Skin", "Bone", "bone", "Phyre", "Context" })
        {
            var count = 0;
            var idx = 0;
            while ((idx = ascii.IndexOf(word, idx, StringComparison.Ordinal)) >= 0) { count++; idx++; }
            if (count > 0) Console.WriteLine($"    \"{word}\": {count} occurrences in bytecode");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Reflection failed: {ex.Message}");
    }
}

static void DumpReflectedBuffers(Vortice.Direct3D11.Shader.ID3D11ShaderReflection refl, string stage)
{
    foreach (var cb in refl.ConstantBuffers)
    {
        var desc = cb.Description;
var varCount = cb.Variables.Count();
        Console.WriteLine($"  cbuffer {desc.Name}: {desc.Size}B, {varCount} variables");
        foreach (var v in cb.Variables)
        {
            var vd = v.Description;
            Console.WriteLine($"    +0x{vd.StartOffset:X3} {vd.Name} ({vd.Size}B)");
        }
    }

    var resources = refl.BoundResources;
    if (resources.Length > 0)
    {
        Console.WriteLine($"  Bound resources ({resources.Length}):");
        foreach (var r in resources)
        {
            Console.WriteLine($"    t{r.BindPoint}: {r.Name} ({r.Type})");
        }
    }
}

static int CountCbOffsetReferences(byte[] bytecode, uint cbOffset)
{
    // Search for the cb offset as a DWORD in the bytecode
    // D3D11 bytecode references cb registers by index (cb0[N] means N*16 offset)
    // The register index = cbOffset / 16
    var regIndex = cbOffset / 16;
    var regBytes = BitConverter.GetBytes((uint)regIndex);
    
    var count = 0;
    for (var i = 0; i <= bytecode.Length - 4; i++)
    {
        if (bytecode[i] == regBytes[0] && bytecode[i+1] == regBytes[1]
            && bytecode[i+2] == regBytes[2] && bytecode[i+3] == regBytes[3])
            count++;
    }
    return count;
}

// ============================================================
static int GenerateCompatibilityShader(string gamePath, string outputDir)
{
    Console.WriteLine("=== Compatibility Shader Generator ===");
    
    var pkgFiles = Directory.GetFiles(gamePath, "*.pkg");
    var reader = new PkgArchiveReader();
    byte[]? fxData = null;
    string? pkgName = null;

    foreach (var pkgPath in pkgFiles)
    {
        try
        {
            var archive = reader.Read(pkgPath);
            var fxEntry = archive.Entries.FirstOrDefault(e =>
                e.Name.Equals("ed8.fx.phyre", StringComparison.OrdinalIgnoreCase));
            if (fxEntry is null) continue;
            fxData = archive.ReadEntry(fxEntry);
            pkgName = Path.GetFileName(pkgPath);
            if (fxData.Length > 100_000) break;
        }
        catch { }
    }

    if (fxData is null) { Console.Error.WriteLine("No main shader found!"); return 1; }

    Console.WriteLine($"Using shader from: {pkgName} ({fxData.Length} bytes)");
    var extractor = new PhyreShaderSignatureExtractor();
    var sig = extractor.Extract(fxData);
    Console.WriteLine($"Signature: {sig.UniqueParameters.Length} params, {sig.UniqueSamplers.Length} samplers");
    Console.WriteLine($"Context switches: {string.Join(", ", sig.ContextSwitches)}");
    Console.WriteLine($"Passes: {string.Join(", ", sig.Passes)}");

    Directory.CreateDirectory(outputDir);
    var sigJson = JsonSerializer.Serialize(sig, new JsonSerializerOptions { WriteIndented = true, MaxDepth = 10 });
    var sigPath = Path.Combine(outputDir, "cs1_shader_signature.json");
    File.WriteAllText(sigPath, sigJson);
    Console.WriteLine($"Saved signature to: {sigPath}");

    var generator = new PhyreCompatibilityShaderGenerator();
    foreach (var level in new[] { CompatibilityLevel.Basic, CompatibilityLevel.SimpleTextured, CompatibilityLevel.FullLighting })
    {
        var vs = generator.GenerateVertexShader(sig);
        var ps = generator.GenerateFragmentShader(sig, level);
        var vsPath = Path.Combine(outputDir, $"cs1_compat_vs_{level}.hlsl");
        var psPath = Path.Combine(outputDir, $"cs1_compat_ps_{level}.hlsl");
        File.WriteAllText(vsPath, vs);
        File.WriteAllText(psPath, ps);
        Console.WriteLine($"Generated {level}: {vsPath}, {psPath}");
    }

    var planPath = Path.Combine(outputDir, "TEST_PLAN.md");
    File.WriteAllText(planPath, GenerateTestPlan(sig));
    Console.WriteLine($"Test plan: {planPath}");
    Console.WriteLine($"Done. Output: {Path.GetFullPath(outputDir)}");
    return 0;
}

static string GenerateTestPlan(PhyreShaderSignature sig)
{
    return $@"# Cold Steel 1 Custom Shader Test Plan

## Signature
- Parameters: {sig.UniqueParameters.Length}
- Samplers: {sig.UniqueSamplers.Length}
- Context switches: {string.Join(", ", sig.ContextSwitches)}
- Passes: {string.Join(", ", sig.Passes)}

## Test Levels

### Level 0: Signature-only
1. Take a working model (lamppost O_T10LIG03)
2. Use fx.phyre with identical signature but solid color HLSL
3. Expected: model renders solid color, no crash

### Level 1: Basic (cs1_compat_Basic.hlsl)
1. Compile HLSL to fx.phyre
2. Test with a prop model
3. Expected: diffuse texture visible

### Level 2: +Fog (cs1_compat_SimpleTextured.hlsl)
1. Test in area with visible fog

### Level 3: Full lighting (cs1_compat_FullLighting.hlsl)
1. Test in various lighting conditions

## Critical failure points
- [ ] PParameterBuffer size mismatch -> crash
- [ ] Missing sampler -> invisible model
- [ ] Wrong cbuffer offset -> corrupted rendering
- [ ] Missing GameMaterialID -> possible crash
";
}

// ============================================================
static int BuildFxPhyre(string hlslFile, string outputPath)
{
    Console.WriteLine($"=== Build fx.phyre ===");
    Console.WriteLine($"Source: {hlslFile}");

    // Auto-detect VS file: replace "ps_" with "vs_" or "_PS_" with "_VS_"
    var vsFile = hlslFile
        .Replace("_ps_", "_vs_", StringComparison.OrdinalIgnoreCase)
        .Replace("_PS_", "_VS_", StringComparison.OrdinalIgnoreCase);

    // For now, always use default VS to avoid packoffset conflicts in generated files
    vsFile = ""; // force default

    if (!File.Exists(hlslFile))
    {
        Console.Error.WriteLine($"HLSL file not found: {hlslFile}");
        return 1;
    }

    Console.WriteLine($"  VS: {(File.Exists(vsFile) ? vsFile : "(auto-generated default)")}");
    Console.WriteLine($"  PS: {hlslFile}");

    var psSource = File.ReadAllText(hlslFile);
    var vsSource = File.Exists(vsFile) ? File.ReadAllText(vsFile) : GenerateDefaultVS();

    // Load pre-defined parameter list from signature JSON
    List<ParamDefInfo>? predefinedParams = null;
    var sigJsonDir = Path.GetDirectoryName(hlslFile);
    if (sigJsonDir is not null)
    {
        var sigPath = Path.Combine(sigJsonDir, "cs1_shader_signature.json");
        if (File.Exists(sigPath))
        {
            var sig = JsonSerializer.Deserialize<PhyreShaderSignature>(File.ReadAllText(sigPath));
            if (sig?.UniqueParameters is not null)
            {
                predefinedParams = sig.UniqueParameters
                    .Select(p => new ParamDefInfo(p.Name, (int)p.CbLocation, p.SizeInBytes()))
                    .ToList();
                Console.WriteLine($"  Loaded {predefinedParams.Count} params from signature");
            }
        }
    }

    var builder = new PhyreEffectClusterBuilder();
    try
    {
        var fxData = builder.BuildFromSources(
            vsSource, psSource,
            "shaders/ed8editor_custom.fx",
            "ed8editor_custom.fx.phyre",
            predefinedParams);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllBytes(outputPath, fxData);
        Console.WriteLine($"Success! {fxData.Length} bytes -> {outputPath}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"BUILD FAILED: {ex}");
        return 1;
    }
}

static string GenerateDefaultVS()
{
    return @"
cbuffer Globals : register(b0) {
    float4x4 scene_ViewProjection : packoffset(c5);
    float4x4 World : packoffset(c25);
}
struct VSInput { float3 Position : POSITION; float3 Normal : NORMAL; float2 TexCoord0 : TEXCOORD0; };
struct PSInput { float4 Position : SV_Position; float3 WorldPos : TEXCOORD0; float2 TexCoord0 : TEXCOORD1; };
PSInput VSMain(VSInput input) {
    PSInput output;
    float4 wp = mul(float4(input.Position, 1), World);
    output.Position = mul(wp, scene_ViewProjection);
    output.WorldPos = wp.xyz;
    output.TexCoord0 = input.TexCoord0;
    return output;
}";
}

// ============================================================
static void DumpShaderParameters(byte[] fxData)
{
    var cluster = new PhyreClusterReader().Read(fxData);
    var meta = cluster.Metadata;

    // Find the PShaderParameterDefinition class descriptor
    var paramDesc = meta.Classes.FirstOrDefault(c => c.Name == "PShaderParameterDefinition");
    if (paramDesc is null) { Console.WriteLine("  No PShaderParameterDefinition class found!"); return; }
    
    // Member offsets from the class descriptor:
    // +00: m_arrayElementCount (uint16)
    // +02: m_parameterType (uint8)
    // +03: m_dataType (uint8)
    // +04: m_name (uint32 — points to array data via fixup)
    // +08: m_bufferLoc (uint32)
    // +0C: m_constantBufferLocation (uint32)

    for (var gi = 0; gi < meta.InstanceGroups.Count; gi++)
    {
        var g = meta.InstanceGroups[gi];
        if (g.ClassName != "PShaderParameterDefinition" || g.Count == 0) continue;

        Console.WriteLine($"  === Shader Parameters ({g.Count} total) ===");
        
        for (uint i = 0; i < g.Count; i++)
        {
            var obj = cluster.GetObject(gi, i).Span;
            ushort arrayCount = (ushort)(obj[0] | (obj[1] << 8));
            byte paramType = obj[2];
            byte dataType = obj[3];
            uint bufferLoc = (uint)(obj[8] | (obj[9] << 8) | (obj[10] << 16) | (obj[11] << 24));
            uint cbLocation = (uint)(obj[12] | (obj[13] << 8) | (obj[14] << 16) | (obj[15] << 24));
            
            // Name is at +04, resolved via array fixup
            var nameFixup = cluster.Fixups.Arrays.FirstOrDefault(f =>
                f.SourceListIndex == gi && f.SourceObjectId == i
                && f.SourceOffset == 0x04);
            var name = "?";
            if (nameFixup is not null)
            {
                try { name = ReadNullTerminatedString(cluster.GetArrayData(gi, nameFixup.Offset, Math.Min(nameFixup.Count == 0 ? 256u : nameFixup.Count, 256u)).Span); }
                catch { name = $"<err offset=0x{nameFixup.Offset:X}>"; }
            }
            
            Console.WriteLine($"    [{i:D3}] {name}  arrayCnt={arrayCount} pType={paramType} dType={dataType} bufLoc=0x{bufferLoc:X} cbLoc=0x{cbLocation:X}");
        }
    }

    // Find all PSamplerState groups
    Console.WriteLine();
    for (var gi = 0; gi < meta.InstanceGroups.Count; gi++)
    {
        var g = meta.InstanceGroups[gi];
        if (g.ClassName != "PSamplerState") continue;
        Console.WriteLine($"  --- PSamplerState group[{gi}] ({g.Count} samplers, objSize={g.Size}) ---");
        for (uint i = 0; i < Math.Min(g.Count, 8); i++)
        {
            var obj = cluster.GetObject(gi, i).Span;
            if (obj.Length < 20) { Console.WriteLine($"    [{i}] object too small ({obj.Length}B)"); continue; }
            var filter = ReadU32(obj, 0x04, meta.IsBigEndian);
            var addrU = obj.Length > 0x0C ? ReadU32(obj, 0x08, meta.IsBigEndian) : 0u;
            var addrV = obj.Length > 0x10 ? ReadU32(obj, 0x0C, meta.IsBigEndian) : 0u;
            var addrW = obj.Length > 0x14 ? ReadU32(obj, 0x10, meta.IsBigEndian) : 0u;

            var nameFixup = cluster.Fixups.Arrays.FirstOrDefault(a =>
                a.SourceListIndex == gi && a.SourceObjectId == i
                && !a.IsClassDataMember && a.SourceOffset == 0x00);
            var name = nameFixup is not null
                ? System.Text.Encoding.ASCII.GetString(cluster.GetArrayData(gi, nameFixup.Offset, nameFixup.Count).Span).TrimEnd('\0')
                : $"?{i}";

            Console.WriteLine($"    [{name}] filter={filter} addr=({addrU},{addrV},{addrW})");
        }
        if (g.Count > 8)
            Console.WriteLine($"    ... and {g.Count - 8} more samplers");
    }
}

static uint ReadU32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    => bigEndian
        ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[offset..])
        : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

static string ReadNullTerminatedString(ReadOnlySpan<byte> data)
{
    var nul = data.IndexOf((byte)0);
    return nul >= 0
        ? System.Text.Encoding.ASCII.GetString(data[..nul])
        : System.Text.Encoding.ASCII.GetString(data);
}

// --- Types ---
internal sealed record ShaderSummaryInfo(
    string PackageName,
    string FileName,
    string[] Switches,
    string[] Passes,
    string[] MaterialSwitches,
    int ContextCount,
    int PermutationCount,
    int FileSize);
