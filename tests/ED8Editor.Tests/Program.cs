using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using ED8Editor.Core;
using ED8Editor.ScriptHeaders;
using ED8Editor.Ops;
using ED8Editor.Assets;
using ED8Editor.Packages;
using ED8Editor.Phyre;
using ED8Editor.Application;
using ED8Editor.Rendering;
using ED8Editor.Scene;
using ED8Editor.Tables;
using ED8Editor.Decompiler;
using ED8Editor.Models;
using ED8Editor.Phyre.Authoring;

// How a shipped effect packs the matrices it declares, read from the compiled
// program itself. It settles whether a constant buffer wants rows or columns,
// which is the difference between a model drawn and a model gone.
//
//   --matrix-packing <package.pkg>
// What a shipped model's materials actually supply, so a constant the native
// renderer leaves at zero can be told from one the material fills.
//
//   --material-fill <package.pkg>
if (Array.IndexOf(args, "--material-fill") >= 0)
{
    var package = new PkgArchiveReader().Read(
        args[Array.IndexOf(args, "--material-fill") + 1]);
    var modelEntry = package.Entries.First(value =>
        value.Name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase));
    var read = new PhyreD3D11ModelReader().Read("probe", package.ReadEntry(modelEntry));
    foreach (var material in read.Materials.Take(4))
    {
        Console.WriteLine($"{material.Name}  effect={material.EffectAssetName}");
        foreach (var pair in material.SourceParameters.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"   f {pair.Key,-38} {string.Join(", ", pair.Value.Select(v => v.ToString("0.###")))}");
        }
        foreach (var pair in (material.SourceIntParameters
                     ?? new Dictionary<string, uint>()).OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"   i {pair.Key,-38} 0x{pair.Value:X8}");
        }
        foreach (var pair in material.SourceTextureReferences.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"   t {pair.Key,-38} {pair.Value}");
        }
    }
    return 0;
}

if (Array.IndexOf(args, "--matrix-packing") >= 0)
{
    var package = new PkgArchiveReader().Read(
        args[Array.IndexOf(args, "--matrix-packing") + 1]);
    foreach (var entry in package.Entries)
    {
        if (!entry.Name.Contains(".fx#", StringComparison.OrdinalIgnoreCase)) continue;
        if (!entry.Name.EndsWith(".phyre", StringComparison.OrdinalIgnoreCase)) continue;
        var metadata = new PhyreEffectRenderPassReader().ReadMetadata(package.ReadEntry(entry));
        if (metadata.Program is not { } program) continue;
        Console.WriteLine(entry.Name);
        foreach (var (passName, pass) in program.SceneRenderPasses)
        {
            var permutation = pass.Permutations.FirstOrDefault();
            if (permutation is null) continue;
            foreach (var (stageName, stage) in new[]
                     {
                         ("VS", permutation.VertexProgram),
                         ("PS", permutation.FragmentProgram),
                     })
            {
                var described = new D3D11ShaderProgramInspector().Inspect(
                    stage,
                    stageName == "VS" ? D3D11ShaderStage.Vertex : D3D11ShaderStage.Fragment);
                Console.WriteLine($"  --- {stageName}");
                foreach (var cb in described.ConstantBuffers)
                {
                    Console.WriteLine(
                        $"    cbuffer {cb.Name,-16} bind b{cb.BindPoint} size {cb.Size}"
                        + $" vars {cb.Variables.Count}");
                }
                foreach (var resource in described.Resources)
                {
                    Console.WriteLine(
                        $"    {resource.Type,-16} {resource.Name,-32} bind {resource.BindPoint}");
                }
            }
            using var reflection = Vortice.D3DCompiler.Compiler
                .Reflect<Vortice.Direct3D11.Shader.ID3D11ShaderReflection>(
                    permutation.VertexProgram.Bytecode);
            foreach (var buffer in reflection.ConstantBuffers)
            {
                foreach (var variable in buffer.Variables)
                {
                    var type = variable.VariableType.Description;
                    var name = variable.Description.Name;
                    var fed = D3D11NativeEffect.EngineValue(
                        name,
                        new D3D11EffectFrame(
                            Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity,
                            Vector3.Zero, Vector3.UnitY, Vector4.One, Vector4.One, 0f));
                    Console.WriteLine(
                        $"  {name,-40} {type.Class,-14}"
                        + $" +{variable.Description.StartOffset,-5} {variable.Description.Size,-4}"
                        + (fed is null ? " -" : $" engine[{fed.Length}]"));
                }
            }
            break;
        }
        break;
    }
    return 0;
}

if (args.Length is 2 or 3 && args[0] == "--script-summary")
{
    var path = args[1];
    var functionFilter = args.Length == 3 ? args[2] : null;
    var decompiled = ScriptDecompiler.Decompile(path);
    Console.WriteLine($"Scene: {decompiled.SceneName}; functions: {decompiled.Functions.Count}");
    var code = decompiled.Functions.Where(value => value.IsCode).ToArray();
    foreach (var group in code.SelectMany(value => value.Instructions)
                 .GroupBy(value => (value.Opcode, value.Name))
                 .OrderBy(value => value.Key.Opcode).ThenBy(value => value.Key.Name))
        Console.WriteLine($"OP {group.Key.Opcode,3} {group.Key.Name,-24} x{group.Count()}");
    foreach (var function in decompiled.Functions.Where(value => functionFilter is null
                 || value.Name.Equals(functionFilter, StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine($"FUNCTION {function.Index} {function.Name} ({function.Instructions.Count}) "
            + (function.IsCode ? "code" : function.Table is { } table ? $"table:{table.Kind}/{table.Fields.Count}" : "raw")
            + $" sourceType={function.SourceType} rawSize={function.RawData?.Length ?? 0} decodeError={function.DecodeErrorOffset}");
        if (function.RawData is { Length: > 0 } raw)
            Console.WriteLine($"  RAW {Convert.ToHexString(raw)}");
        if (function.Table is { } functionTable)
            foreach (var field in functionTable.Fields)
                Console.WriteLine($"  TABLE [{field.Index}] {field.Type} = {Convert.ToHexString(field.Raw)}");
        foreach (var instruction in function.Instructions)
        {
            Console.WriteLine($"  #{instruction.Index} +0x{instruction.Offset:X} {instruction.Name} op={instruction.Opcode}");
            foreach (var argument in instruction.Arguments)
            {
                var value = argument.Kind switch
                {
                    "string" => Encoding.UTF8.GetString(argument.Raw).TrimEnd('\0'),
                    "scalar" when argument.Type == "f32" => argument.FloatValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    "scalar" => argument.IntValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "expr" => string.Join(" ", argument.Expression?.Select(value => value.Label) ?? Array.Empty<string>()),
                    _ => Convert.ToHexString(argument.Raw),
                };
                Console.WriteLine($"    [{argument.Index}] {argument.Kind}/{argument.Type} {argument.Name ?? "-"} = {value}");
            }
        }
    }
    return 0;
}

if (args is ["--object-animation-info", var objectGameDataPath, var objectAssetId])
{
    var actions = new EditorProjectLoader(new OpsReader())
        .LoadObjectAnimationInfo(objectAssetId, objectGameDataPath);
    foreach (var action in actions.Values)
    {
        Console.WriteLine(
            $"{action.Name}: {action.StartFrame}..{action.EndFrame},"
            + $" loop={action.Loop}, reverse={action.Reverse}");
    }
    return actions.Count > 0 ? 0 : 1;
}

if (args is ["--facial-textures", var facialGameDataPath, var facialAssetId])
{
    var textures = new EditorProjectLoader(
        new OpsReader(),
        new GameAssetResolverFactory(),
        new PkgArchiveReader(),
        new AssetManifestReader(),
        new PhyreD3D11ModelReader(),
        new PhyreD3D11TextureReader())
        .LoadFacialTextures(facialAssetId, facialGameDataPath);
    foreach (var pair in textures.Textures.OrderBy(value => value.Key.Channel)
                 .ThenBy(value => value.Key.Frame))
    {
        Console.WriteLine(
            $"{pair.Key.NormalizedChannel}{pair.Key.Frame:00}: "
            + $"{pair.Value.Width}x{pair.Value.Height} {pair.Value.Format}");
    }
    return textures.Textures.Count > 0 ? 0 : 1;
}

if (args is ["--tbl-roundtrip", var tblDirectory])
{
    var tblFailures = 0;
    foreach (var path in Directory.GetFiles(tblDirectory, "*.tbl").Order())
    {
        try
        {
            var original = File.ReadAllBytes(path);
            var table = Cs1TableDocument.Read(new MemoryStream(original), path);
            using var output = new MemoryStream();
            table.Write(output);
            if (!original.AsSpan().SequenceEqual(output.ToArray()))
                throw new InvalidDataException("round-trip bytes differ");
            Console.WriteLine($"PASS {Path.GetFileName(path)} ({table.Entries.Count} entries)");
        }
        catch (Exception exception)
        {
            tblFailures++;
            Console.Error.WriteLine($"FAIL {Path.GetFileName(path)}: {exception.Message}");
        }
    }
    return tblFailures == 0 ? 0 : 1;
}

if (args is ["--tbl-schema-roundtrip", var schemaTblDirectory])
{
    var schemaFailures = 0;
    var decodedEntries = 0;
    var codec = new Cs1TableRecordCodec();
    foreach (var path in Directory.GetFiles(schemaTblDirectory, "*.tbl").Order())
    {
        var table = Cs1TableDocument.Read(path);
        foreach (var entry in table.Entries)
        {
            try
            {
                var values = codec.Decode(entry);
                if (values is null) continue;
                decodedEntries++;
                var encoded = codec.Encode(entry.Category, values);
                if (!entry.Data.AsSpan().SequenceEqual(encoded))
                    throw new InvalidDataException("typed round-trip bytes differ");
            }
            catch (Exception exception)
            {
                schemaFailures++;
                Console.Error.WriteLine($"FAIL {Path.GetFileName(path)}:{entry.Category}: {exception.Message}");
                break;
            }
        }
    }
    Console.WriteLine($"Typed entries validated: {decodedEntries}");
    return schemaFailures == 0 ? 0 : 1;
}

if (args is ["--tbl-entry", var tablePath, var tableCategory, var tableKey])
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var locale = Path.GetFileName(Path.GetDirectoryName(tablePath)) ?? string.Empty;
    var textEncoding = locale.Equals("dat", StringComparison.OrdinalIgnoreCase)
        ? Encoding.GetEncoding(932)
        : new UTF8Encoding(false, true);
    var codec = new Cs1TableRecordCodec(textEncoding: textEncoding);
    var table = Cs1TableDocument.Read(tablePath);
    var matches = 0;
    foreach (var entry in table.Entries.Where(value =>
                 value.Category.Equals(tableCategory, StringComparison.Ordinal)))
    {
        var values = codec.Decode(entry);
        if (values is null || !values.Any(value =>
                value.Value.Equals(tableKey, StringComparison.OrdinalIgnoreCase))) continue;
        Console.WriteLine($"MATCH {++matches}: {entry.Category}");
        foreach (var value in values)
            Console.WriteLine($"  {value.Field.Name} ({value.Field.Type}) = {value.Value}");
    }
    return matches > 0 ? 0 : 1;
}

if (args is ["--shader-api"])
{
    foreach (var assemblyName in new[] { "Vortice.D3DCompiler", "Vortice.Direct3D11" })
    {
        var assembly = System.Reflection.Assembly.Load(assemblyName);
        foreach (var type in assembly.GetTypes().Where(value =>
                     value.FullName?.Contains("ShaderReflection", StringComparison.Ordinal) == true
                     || value.FullName?.Contains("ShaderDescription", StringComparison.Ordinal) == true
                     || value.FullName?.Contains("SignatureParameter", StringComparison.Ordinal) == true
                     || value.FullName?.Contains("ShaderParameterDescription", StringComparison.Ordinal) == true
                     || value.FullName?.Contains("ConstantBufferDescription", StringComparison.Ordinal) == true
                     || value.FullName?.Contains("ShaderVariableDescription", StringComparison.Ordinal) == true
                     || value.FullName?.Contains("InputBind", StringComparison.Ordinal) == true
                     || value.FullName == "Vortice.D3DCompiler.Compiler"))
        {
            Console.WriteLine($"TYPE {type.FullName} GUID={type.GUID}");
            foreach (var constructor in type.GetConstructors()) Console.WriteLine($"  CTOR {constructor}");
            foreach (var member in type.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            {
                Console.WriteLine($"  {member.MemberType} {member}");
            }
        }
    }
    return 0;
}

if (args is ["--scene-scan", var sceneScriptPath])
{
    var session = new EditorProjectLoader(
        new OpsReader(),
        new GameAssetResolverFactory(),
        new PkgArchiveReader(),
        new AssetManifestReader(),
        new PhyreD3D11ModelReader(),
        new PhyreD3D11TextureReader()).OpenScript(sceneScriptPath);
    var instances = new EditorSceneFactory().Create(session);
    var result = new SceneRaycaster().Cast(new SceneRay(Vector3.Zero, Vector3.UnitZ), instances);
    Console.WriteLine($"Scene instances : {instances.Count}");
    Console.WriteLine($"Triangles       : {result.TestedTriangles}");
    Console.WriteLine($"Geometry issues : {result.Issues.Count}");
    Console.WriteLine($"OPS volumes     : {session.Map?.Volumes.Count ?? 0}");
    Console.WriteLine($"OPS points      : {session.Map?.Points.Count ?? 0}");
    Console.WriteLine($"OPS cameras     : {session.Map?.Cameras.Count ?? 0}");
    Console.WriteLine($"OPS sounds      : {session.Map?.Sounds.Count ?? 0}");
    Console.WriteLine($"OPS lights      : {session.Map?.Lights.Count ?? 0}");
    foreach (var issue in result.Issues)
    {
        Console.WriteLine($"  instance {issue.InstanceId}, mesh {issue.MeshIndex}, primitive {issue.PrimitiveIndex}: {issue.Reason}");
    }
    return result.Issues.Count == 0 ? 0 : 1;
}

if (args is ["--gpu-upload", var gpuScriptPath])
{
    var session = new EditorProjectLoader(
        new OpsReader(),
        new GameAssetResolverFactory(),
        new PkgArchiveReader(),
        new AssetManifestReader(),
        new PhyreD3D11ModelReader(),
        new PhyreD3D11TextureReader()).OpenScript(gpuScriptPath);
    using var graphics = D3D11GraphicsDevice.Create();
    var uploader = new D3D11ModelUploader(graphics.Device);
    var uploaded = new List<D3D11ModelResources>();
    try
    {
        foreach (var model in session.AssetModels.Values.Where(value => value.Model is not null))
        {
            uploaded.Add(uploader.Upload(model.Model!));
        }

        Console.WriteLine($"Feature level : {graphics.FeatureLevel}");
        Console.WriteLine($"GPU models    : {uploaded.Count}");
        Console.WriteLine($"GPU meshes    : {uploaded.Sum(value => value.Meshes.Count)}");
        Console.WriteLine($"GPU textures  : {uploaded.Sum(value => value.Textures.Count)}");
        Console.WriteLine($"GPU bytes     : {uploaded.Sum(value => value.AllocatedBytes)}");
        using var renderer = new D3D11SceneRenderer(graphics);
        var report = renderer.RenderOffscreen(uploaded);
        Console.WriteLine($"Draw calls    : {report.DrawCalls}");
        Console.WriteLine($"Draw skipped  : {report.SkippedPrimitives}");
    }
    finally
    {
        foreach (var model in uploaded) model.Dispose();
    }
    return 0;
}

if (args is ["--ops-corpus", var opsDirectory])
{
    return ScanOpsCorpus(opsDirectory);
}

if (args is ["--asset-corpus", var gameDataDirectory])
{
    return ScanAssetCorpus(gameDataDirectory);
}

if (args is ["--pkg", var packagePath])
{
    return ExtractPackage(packagePath);
}

if (args is ["--pkg-entry", var entryPackagePath, var entryName])
{
    var archive = new PkgArchiveReader().Read(entryPackagePath);
    Console.Write(Encoding.UTF8.GetString(archive.ReadEntry(entryName)));
    return 0;
}

if (args is ["--pkg-text-context", var textPackagePath, var textEntryName, var textNeedle])
{
    var archive = new PkgArchiveReader().Read(textPackagePath);
    var text = Encoding.UTF8.GetString(archive.ReadEntry(textEntryName));
    var matchCount = 0;
    for (var searchOffset = 0; searchOffset < text.Length;)
    {
        var matchOffset = text.IndexOf(textNeedle, searchOffset, StringComparison.Ordinal);
        if (matchOffset < 0) break;
        var contextStart = Math.Max(0, matchOffset - 800);
        var contextEnd = Math.Min(text.Length, matchOffset + textNeedle.Length + 1200);
        Console.WriteLine($"MATCH {++matchCount} at 0x{matchOffset:X}");
        Console.WriteLine(text[contextStart..contextEnd]);
        searchOffset = matchOffset + Math.Max(1, textNeedle.Length);
    }
    return matchCount > 0 ? 0 : 1;
}

if (args is ["--script-find-operand", var operandScriptPath, var operandText])
{
    var decompiled = ScriptDecompiler.Decompile(operandScriptPath);
    var matches = 0;
    foreach (var function in decompiled.Functions.Where(value => value.IsCode))
    {
        for (var index = 0; index < function.Instructions.Count; index++)
        {
            var instruction = function.Instructions[index];
            var values = instruction.Arguments.Select(argument => argument.Kind switch
            {
                "string" => Encoding.UTF8.GetString(argument.Raw).TrimEnd('\0'),
                "scalar" when argument.Type == "f32" => argument.FloatValue.ToString(
                    "R", System.Globalization.CultureInfo.InvariantCulture),
                "scalar" => argument.IntValue.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                "expr" => string.Join(" ", argument.Expression?.Select(value => value.Label)
                    ?? Array.Empty<string>()),
                _ => Convert.ToHexString(argument.Raw),
            }).ToArray();
            if (!values.Any(value => value.Contains(
                    operandText, StringComparison.OrdinalIgnoreCase))) continue;
            Console.WriteLine($"MATCH {++matches}: {function.Name} #{instruction.Index}");
            foreach (var nearby in function.Instructions.Skip(Math.Max(0, index - 5)).Take(11))
            {
                var arguments = string.Join(", ", nearby.Arguments.Select(argument =>
                    argument.Kind == "string"
                        ? $"\"{Encoding.UTF8.GetString(argument.Raw).TrimEnd('\0')}\""
                        : argument.Type == "f32"
                            ? argument.FloatValue.ToString(
                                "R", System.Globalization.CultureInfo.InvariantCulture)
                            : argument.IntValue.ToString(
                                System.Globalization.CultureInfo.InvariantCulture)));
                Console.WriteLine($"  #{nearby.Index,-4} {nearby.Name,-28} {arguments}");
            }
        }
    }
    return matches > 0 ? 0 : 1;
}

if (args is ["--pkg-find-string", var searchPackagePath, var searchText])
{
    var archive = new PkgArchiveReader().Read(searchPackagePath);
    var needle = Encoding.ASCII.GetBytes(searchText);
    foreach (var entry in archive.Entries)
    {
        var data = archive.ReadEntry(entry);
        var offset = data.AsSpan().IndexOf(needle);
        if (offset >= 0) Console.WriteLine($"{entry.Name}: 0x{offset:X}");
    }
    return 0;
}

if (args is ["--pkg-find-manifest-text", var packageDirectory, var manifestText])
{
    foreach (var path in Directory.EnumerateFiles(packageDirectory, "*.pkg"))
    {
        try
        {
            var archive = new PkgArchiveReader().Read(path);
            var manifest = archive.Entries.FirstOrDefault(value =>
                value.Name.Equals("asset_D3D11.xml", StringComparison.OrdinalIgnoreCase));
            if (manifest is null) continue;
            var text = Encoding.UTF8.GetString(archive.ReadEntry(manifest));
            if (text.Contains(manifestText, StringComparison.OrdinalIgnoreCase))
                Console.WriteLine(Path.GetFileName(path));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{Path.GetFileName(path)}: {exception.Message}");
        }
    }
    return 0;
}

if (args is ["--pkg-find-entry-name", var entryPackageDirectory, var entryText])
{
    foreach (var path in Directory.EnumerateFiles(entryPackageDirectory, "*.pkg"))
    {
        try
        {
            var archive = new PkgArchiveReader().Read(path);
            var matches = archive.Entries.Where(value =>
                value.Name.Contains(entryText, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 0)
                Console.WriteLine($"{Path.GetFileName(path)}: {string.Join(", ", matches.Select(value => value.Name))}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{Path.GetFileName(path)}: {exception.Message}");
        }
    }
    return 0;
}

if (args is ["--manifest-corpus", var manifestGameDataPath])
{
    return ScanManifestCorpus(manifestGameDataPath);
}

if (args is ["--phyre-metadata", var phyrePackagePath, var phyreEntryName])
{
    var archive = new PkgArchiveReader().Read(phyrePackagePath);
    var clusterBytes = archive.ReadEntry(phyreEntryName);
    var cluster = new PhyreClusterReader().Read(clusterBytes);
    var metadata = cluster.Metadata;
    var fixups = cluster.Fixups;
    Console.WriteLine($"Marker     : 0x{metadata.Marker:X8}");
    Console.WriteLine($"Big endian : {metadata.IsBigEndian}");
    Console.WriteLine($"Platform   : {metadata.PlatformId}");
    Console.WriteLine($"Types      : {metadata.Types.Count}");
    Console.WriteLine($"Classes    : {metadata.Classes.Count}");
    Console.WriteLine($"Groups     : {metadata.InstanceGroups.Count}");
    Console.WriteLine($"Fixups     : {fixups.PointerArrays.Count} pointer arrays, {fixups.Pointers.Count} pointers, {fixups.Arrays.Count} arrays");
    Console.WriteLine($"VRAM data  : 0x{fixups.VramDataOffset:X}");
    foreach (var group in metadata.InstanceGroups.Where(group => group.Count > 0))
    {
        Console.WriteLine($"  {group.ClassName ?? $"class#{group.ClassId}"}: {group.Count} (P={group.PointerFixupCount}, A={group.ArrayFixupCount}, PA={group.PointerArrayFixupCount})");
    }
    var nodeContextGroup = metadata.InstanceGroups.SingleOrDefault(value => value.ClassName == "PNodeContext");
    if (nodeContextGroup is not null)
    {
        foreach (var fixup in fixups.Arrays.Where(value => value.SourceListIndex == nodeContextGroup.Index))
        {
            var words = cluster.GetArrayData(nodeContextGroup.Index, fixup.Offset, checked(fixup.Count * sizeof(uint))).Span;
            var values = new string[fixup.Count];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = $"0x{BinaryPrimitives.ReadUInt32LittleEndian(words[(index * sizeof(uint))..]):X8}";
            }
            Console.WriteLine($"  context#{fixup.SourceObjectId}: {string.Join(", ", values)}");
        }
    }

    var diagnosticClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        "PClusterHeaderD3D11", "PMesh", "PMeshSegment", "PMeshSegmentD3D11", "PMeshSegmentBase",
        "PDataBlockD3D11", "PDataBlockBase", "PIndexDataBlockD3D11", "PIndexDataBlockBase",
        "PVertexStream", "PRenderDataType", "PMaterial", "PMaterialSet", "PParameterBuffer",
        "PShaderParameterDefinition", "PAssetReference", "PAssetReferenceImport", "PTexture2D",
        "PSceneRenderPass", "PEffect", "PEffectVariant", "PShader", "PShaderPass", "PShaderPassD3D11",
        "PShaderVertexProgram", "PShaderFragmentProgram", "PShaderStreamDefinition", "PStreamInputLayoutD3D11", "PStreamInputDescD3D11",
        "PShaderPassInfo", "PShaderParameterCaptureBufferLocation", "PShaderParameterCaptureBufferLocationTypeConstantBuffer",
        "PNodeContext", "PContextVariantFoldingTable",
        "PTexture2DD3D11", "PTexture2DBase", "PTextureCommonBase",
        "PMatrix4", "PString", "PSkinBoneRemap", "PSkeletonJointBounds",
    };
    foreach (var descriptor in metadata.Classes.Where(value => diagnosticClasses.Contains(value.Name)
                 || value.Name.Contains("Animation", StringComparison.Ordinal)
                 || value.Name.Contains("Shader", StringComparison.Ordinal)
                 || value.Name.Contains("Context", StringComparison.Ordinal)
                 || value.Name.Contains("RenderPass", StringComparison.Ordinal)))
    {
        Console.WriteLine($"class#{descriptor.Index} {descriptor.Name} ({descriptor.Size} bytes, super={descriptor.SuperClassId}):");
        foreach (var member in descriptor.Members)
        {
            var arraySuffix = member.FixedArraySize == 0 ? string.Empty : $"[{member.FixedArraySize}]";
            Console.WriteLine($"  +0x{member.ValueOffset:X3} {member.TypeName ?? $"type#{member.TypeId}"} {member.Name}{arraySuffix} ({member.Size} bytes)");
        }
    }
    var membersById = metadata.Classes.SelectMany(value => value.Members).ToDictionary(value => (uint)value.Index);
    foreach (var fixup in fixups.Arrays.Where(value =>
                 metadata.InstanceGroups[value.SourceListIndex].ClassName?.StartsWith("PAnimation", StringComparison.Ordinal) == true
                    || metadata.InstanceGroups[value.SourceListIndex].ClassName is "PMesh" or "PString" or "PDataBlockD3D11" or "PMaterial" or "PParameterBuffer"
                    or "PShader" or "PShaderPass" or "PShaderVertexProgram" or "PShaderFragmentProgram" or "PNodeContext" or "PSceneRenderPass"))
    {
        var memberName = fixup.IsClassDataMember && membersById.TryGetValue(fixup.SourceMemberId, out var member)
            ? member.Name
            : $"offset#0x{fixup.SourceOffset:X}";
        Console.WriteLine($"ARRAY {metadata.InstanceGroups[fixup.SourceListIndex].ClassName}[{fixup.SourceObjectId}].{memberName}: count={fixup.Count}, offset=0x{fixup.Offset:X}");
    }
    foreach (var fixup in fixups.Pointers.Where(value =>
                 metadata.InstanceGroups[value.SourceListIndex].ClassName?.StartsWith("PAnimation", StringComparison.Ordinal) == true
                    || metadata.InstanceGroups[value.SourceListIndex].ClassName is "PMesh" or "PMeshSegment" or "PString" or "PDataBlockD3D11" or "PVertexStream" or "PMaterial" or "PParameterBuffer" or "PShaderParameterDefinition" or "PTexture2D" or "PEffect"
                    or "PSceneRenderPass" or "PShader" or "PShaderPass" or "PShaderVertexProgram" or "PShaderFragmentProgram"))
    {
        var memberName = fixup.IsClassDataMember && membersById.TryGetValue(fixup.SourceMemberId, out var member)
            ? member.Name
            : $"offset#0x{fixup.SourceOffset:X}";
        var destination = fixup.UserFixupId is { } userId
            ? $"user#{userId}({fixups.UserFixups[checked((int)userId)].Text ?? fixups.UserFixups[checked((int)userId)].TypeName})"
            : $"{metadata.InstanceGroups[checked((int)fixup.DestinationListIndex)].ClassName}[{fixup.DestinationObjectId}] + 0x{fixup.DestinationOffset:X}";
        Console.WriteLine($"POINTER {metadata.InstanceGroups[fixup.SourceListIndex].ClassName}[{fixup.SourceObjectId}].{memberName} -> {destination}");
    }

    return 0;
}

if (args is ["--phyre-effect-source", var effectPackagePath, var effectEntryName])
{
    var archive = new PkgArchiveReader().Read(effectPackagePath);
    var cluster = new PhyreClusterReader().Read(archive.ReadEntry(effectEntryName));
    var effectGroup = cluster.Metadata.InstanceGroups.Single(value => value.ClassName == "PEffect");
    var sourceMember = cluster.Metadata.Classes.Single(value => value.Name == "PEffect").Members
        .Single(value => value.Name == "m_effectSource");
    var sourceFixup = cluster.Fixups.Arrays.Single(value =>
        value.SourceListIndex == effectGroup.Index && value.SourceObjectId == 0
        && ((value.IsClassDataMember && value.SourceMemberId == (uint)sourceMember.Index)
            || (!value.IsClassDataMember && value.SourceOffset == sourceMember.ValueOffset)));
    var source = cluster.GetArrayData(effectGroup.Index, sourceFixup.Offset, sourceFixup.Count).Span;
    var zero = source.IndexOf((byte)0);
    Console.Write(Encoding.UTF8.GetString(zero >= 0 ? source[..zero] : source));
    return 0;
}

if (args is ["--phyre-model", var modelPackagePath, var modelEntryName])
{
    var archive = new PkgArchiveReader().Read(modelPackagePath);
    var model = new PhyreD3D11ModelReader().Read(Path.GetFileNameWithoutExtension(modelEntryName), archive.ReadEntry(modelEntryName));
    var effectAssetResolver = new PhyreArchiveAssetResolver();
    var effectPassResolver = new PhyreMaterialRenderPassResolver();
    var effectReader = new PhyreEffectRenderPassReader();
    model = model with
    {
        Materials = model.Materials.Select(material =>
        {
            if (material.EffectAssetName is null) return material;
            var effectEntry = effectAssetResolver.Resolve(archive.Entries, material.EffectAssetName);
            return effectEntry is null
                ? material
                : effectPassResolver.Resolve(material, effectReader.ReadMetadata(archive.ReadEntry(effectEntry)));
        }).ToArray(),
    };
    Console.WriteLine($"Meshes     : {model.Meshes.Count}");
    Console.WriteLine($"Primitives : {model.Meshes.Sum(value => value.Primitives.Count)}");
    Console.WriteLine($"Materials  : {model.Materials.Count}");
    Console.WriteLine($"Skeleton   : {(model.Skeleton is null ? "none" : $"{model.Skeleton.Joints.Count} hierarchy joints, {model.Skeleton.InverseBindMatrices.Count} skin joints")}");
    Console.WriteLine($"Embedded animation: {model.EmbeddedAnimation?.Name ?? "none"}");
    if (model is { EmbeddedAnimation: { } embedded, SceneNodes: { } nodes })
    {
        var pose = new CpuSceneAnimationEvaluator().Evaluate(nodes, embedded,
            (embedded.StartTime + embedded.EndTime) * 0.5f);
        Console.WriteLine($"Animated scene nodes: {pose.WorldTransforms.Count}");
    }
    if (model.Skeleton is { } skeleton)
    {
        var worlds = new Matrix4x4[skeleton.Joints.Count];
        for (var index = 0; index < worlds.Length; index++)
            worlds[index] = skeleton.Joints[index].ParentIndex >= 0
                ? skeleton.Joints[index].DefaultLocalTransform * worlds[skeleton.Joints[index].ParentIndex]
                : skeleton.Joints[index].DefaultLocalTransform;
        static float IdentityError(Matrix4x4 value) =>
            MathF.Abs(value.M11 - 1) + MathF.Abs(value.M22 - 1) + MathF.Abs(value.M33 - 1) + MathF.Abs(value.M44 - 1)
            + MathF.Abs(value.M12) + MathF.Abs(value.M13) + MathF.Abs(value.M14)
            + MathF.Abs(value.M21) + MathF.Abs(value.M23) + MathF.Abs(value.M24)
            + MathF.Abs(value.M31) + MathF.Abs(value.M32) + MathF.Abs(value.M34)
            + MathF.Abs(value.M41) + MathF.Abs(value.M42) + MathF.Abs(value.M43);
        var directError = skeleton.SkeletonToHierarchy.Select((hierarchy, index) =>
            IdentityError(skeleton.InverseBindMatrices[index] * skeleton.Joints[hierarchy].DefaultLocalTransform)).Average();
        var worldError = skeleton.SkeletonToHierarchy.Select((hierarchy, index) =>
            IdentityError(skeleton.InverseBindMatrices[index] * worlds[hierarchy])).Average();
        Console.WriteLine($"Bind errors: direct={directError:R}, hierarchical={worldError:R}");
    }
    var programsReported = new HashSet<CpuEffectProgram>(ReferenceEqualityComparer.Instance);
    var shaderInspector = new D3D11ShaderProgramInspector();
    foreach (var (material, materialIndex) in model.Materials.Select((value, index) => (value, index)))
    {
        Console.WriteLine($"material {materialIndex}: name={material.Name}, pass={material.RenderPassType ?? "<null>"}, phase={material.RenderPhase}, effect={material.EffectAssetName ?? "<null>"}");
        if (material.RenderPassState is { } passState)
        {
            Console.WriteLine($"  blend={passState.BlendEnabled} {passState.SourceBlend}/{passState.DestinationBlend}, raster={passState.RasterizerState?.FillMode}/{passState.RasterizerState?.CullMode}, frontCCW={passState.RasterizerState?.FrontCounterClockwise}");
        }
        Console.WriteLine($"  {material.SourceParameters.Count} constants, {material.SourceTextureReferences.Count} texture references");
        foreach (var parameter in material.SourceParameters)
        {
            Console.WriteLine($"  parameter {parameter.Key}={string.Join(",", parameter.Value.Select(value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))}");
        }
        foreach (var effectSwitch in material.EffectSwitches ?? new Dictionary<string, string>())
        {
            Console.WriteLine($"  switch {effectSwitch.Key}={effectSwitch.Value}");
        }
        foreach (var reference in material.SourceTextureReferences)
        {
            Console.WriteLine($"  {reference.Key} -> {reference.Value}");
        }
        if (material.EffectProgram is { } program && programsReported.Add(program))
        {
            foreach (var pass in program.SceneRenderPasses.Values)
            {
                Console.WriteLine($"  program pass {pass.Name}: {pass.Permutations.Count} permutations");
                foreach (var (permutation, permutationIndex) in pass.Permutations.Select((value, index) => (value, index)))
                {
                    var vertex = shaderInspector.Inspect(permutation.VertexProgram, D3D11ShaderStage.Vertex);
                    var fragment = shaderInspector.Inspect(permutation.FragmentProgram, D3D11ShaderStage.Fragment);
                    Console.WriteLine($"    permutation {permutationIndex}: VS {permutation.VertexProgram.Bytecode.Length} bytes [{string.Join(", ", vertex.Inputs.Select(value => $"{value.SemanticName}{value.SemanticIndex}"))}], PS {permutation.FragmentProgram.Bytecode.Length} bytes");
                    foreach (var stage in new[] { vertex, fragment })
                    {
                        Console.WriteLine($"      {stage.Stage}: cbuffers [{string.Join(", ", stage.ConstantBuffers.Select(value => $"{value.Name}@b{value.BindPoint}:{value.Size}"))}], resources [{string.Join(", ", stage.Resources.Where(value => value.Type != Vortice.Direct3D.ShaderInputType.ConstantBuffer).Select(value => $"{value.Name}@{value.BindPoint}:{value.Type}"))}]");
                        foreach (var buffer in stage.ConstantBuffers)
                        {
                            Console.WriteLine($"        {buffer.Name}: {string.Join(", ", buffer.Variables.Select(value => $"{value.Name}+{value.Offset}:{value.Size}"))}");
                        }
                    }
                }
            }
        }
    }
    foreach (var (mesh, meshIndex) in model.Meshes.Select((value, index) => (value, index)))
    {
        foreach (var (primitive, primitiveIndex) in mesh.Primitives.Select((value, index) => (value, index)))
        {
            Console.WriteLine($"mesh {meshIndex}, primitive {primitiveIndex}: {primitive.Topology}, {primitive.Indices.IndexCount} indices/{primitive.Indices.IndexElementSize * 8}-bit, material {primitive.MaterialIndex}");
            foreach (var buffer in primitive.VertexBuffers)
            {
                Console.WriteLine($"  vertices={buffer.VertexCount}, stride={buffer.Stride}, bytes={buffer.Data.Length}");
                foreach (var attribute in buffer.Attributes)
                {
                    Console.WriteLine($"    +{attribute.Offset}: {attribute.Semantic}[{attribute.SemanticIndex}] {attribute.SourceFormat}");
                }
            }
        }
    }

    return 0;
}

if (args is ["--phyre-texture", var texturePackagePath, var textureEntryName])
{
    var archive = new PkgArchiveReader().Read(texturePackagePath);
    var texture = new PhyreD3D11TextureReader().Read(
        Path.GetFileNameWithoutExtension(textureEntryName),
        archive.ReadEntry(textureEntryName));
    Console.WriteLine($"Name       : {texture.Name}");
    Console.WriteLine($"Dimensions : {texture.Width}x{texture.Height}");
    Console.WriteLine($"Mipmaps    : {texture.MipCount}");
    Console.WriteLine($"Format     : {texture.Format}");
    Console.WriteLine($"GPU bytes  : {texture.Data.Length}");
    return 0;
}

if (args is ["--phyre-animation", var animationPackagePath, var animationEntryName, var animationAssetId])
{
    var archive = new PkgArchiveReader().Read(animationPackagePath);
    var clip = new PhyreAnimationReader().Read(animationAssetId, archive.ReadEntry(animationEntryName));
    Console.WriteLine($"{clip.AssetId}: '{clip.Name}', {clip.StartTime:R}..{clip.EndTime:R}, {clip.Channels.Count} channels");
    foreach (var group in clip.Channels.GroupBy(value => value.Path))
        Console.WriteLine($"  {group.Key}: {group.Count()} channels, {group.Sum(value => value.Times.Count)} keys");
    return 0;
}

if (args is ["--phyre-pose", var poseModelPackage, var poseModelEntry,
    var poseAnimationPackage, var poseAnimationEntry, var poseAnimationAsset])
{
    var modelArchive = new PkgArchiveReader().Read(poseModelPackage);
    var model = new PhyreD3D11ModelReader().Read(Path.GetFileNameWithoutExtension(poseModelEntry),
        modelArchive.ReadEntry(poseModelEntry));
    var animationArchive = new PkgArchiveReader().Read(poseAnimationPackage);
    var clip = new PhyreAnimationReader().Read(poseAnimationAsset, animationArchive.ReadEntry(poseAnimationEntry));
    var skeleton = model.Skeleton ?? throw new InvalidDataException("Model has no skeleton.");
    var jointNames = skeleton.Joints.Select(value => value.Name)
        .ToHashSet(StringComparer.Ordinal);
    var unboundTargets = clip.Channels.Select(value => value.TargetName)
        .Distinct(StringComparer.Ordinal)
        .Where(value => !jointNames.Contains(value))
        .ToArray();
    Console.WriteLine($"Unbound animation targets: {string.Join(", ", unboundTargets)}");
    var start = new CpuSkeletonPoseEvaluator().Evaluate(
        skeleton, clip, clip.StartTime, CpuAnimationUnboundTargetBehavior.Ignore);
    var middle = new CpuSkeletonPoseEvaluator().Evaluate(
        skeleton, clip, (clip.StartTime + clip.EndTime) * 0.5f,
        CpuAnimationUnboundTargetBehavior.Ignore);
    Console.WriteLine($"Pose: {skeleton.Joints.Count} joints, {start.SkinMatrices.Count} skin matrices");
    Console.WriteLine($"Start finite: {start.SkinMatrices.All(IsFinite)}; middle finite: {middle.SkinMatrices.All(IsFinite)}");
    foreach (var (primitive, index) in model.Meshes.SelectMany(value => value.Primitives).Select((value, index) => (value, index)))
    {
        var indices = primitive.VertexBuffers.SelectMany(value => value.Attributes)
            .FirstOrDefault(value => value.Semantic == VertexSemantic.JointIndices);
        var weights = primitive.VertexBuffers.SelectMany(value => value.Attributes)
            .FirstOrDefault(value => value.Semantic == VertexSemantic.JointWeights);
        var maximumDisplacement = MeasureMaximumSkinnedVertexDisplacement(
            primitive, start.SkinMatrices, middle.SkinMatrices);
        Console.WriteLine(
            $"Primitive {index}: bones={primitive.SkinBones?.Count ?? 0},"
            + $" indices={indices?.SourceFormat}, weights={weights?.SourceFormat},"
            + $" animated displacement={maximumDisplacement:R}");
    }
    return 0;

    static float MeasureMaximumSkinnedVertexDisplacement(
        CpuMeshPrimitive primitive,
        IReadOnlyList<Matrix4x4> bindMatrices,
        IReadOnlyList<Matrix4x4> animatedMatrices)
    {
        if (primitive.SkinBones is not { Count: > 0 } bones) return 0f;
        var positionBuffer = primitive.VertexBuffers.FirstOrDefault(value =>
            value.Attributes.Any(attribute => attribute.Semantic == VertexSemantic.Position));
        var indexBuffer = primitive.VertexBuffers.FirstOrDefault(value =>
            value.Attributes.Any(attribute => attribute.Semantic == VertexSemantic.JointIndices));
        var weightBuffer = primitive.VertexBuffers.FirstOrDefault(value =>
            value.Attributes.Any(attribute => attribute.Semantic == VertexSemantic.JointWeights));
        var positionAttribute = positionBuffer?.Attributes.FirstOrDefault(value =>
            value.Semantic == VertexSemantic.Position);
        var indexAttribute = indexBuffer?.Attributes.FirstOrDefault(value =>
            value.Semantic == VertexSemantic.JointIndices);
        var weightAttribute = weightBuffer?.Attributes.FirstOrDefault(value =>
            value.Semantic == VertexSemantic.JointWeights);
        if (positionBuffer is null || indexBuffer is null || weightBuffer is null
            || positionAttribute?.SourceFormat != "Float32x3"
            || indexAttribute?.SourceFormat != "UInt8x4"
            || weightAttribute?.SourceFormat != "Float32x4")
        {
            return 0f;
        }

        var maximum = 0f;
        var vertexCount = Math.Min(
            positionBuffer.VertexCount,
            Math.Min(indexBuffer.VertexCount, weightBuffer.VertexCount));
        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            var positionOffset = vertex * positionBuffer.Stride + positionAttribute.Offset;
            var position = new Vector3(
                BitConverter.ToSingle(positionBuffer.Data, positionOffset),
                BitConverter.ToSingle(positionBuffer.Data, positionOffset + 4),
                BitConverter.ToSingle(positionBuffer.Data, positionOffset + 8));
            var indicesOffset = vertex * indexBuffer.Stride + indexAttribute.Offset;
            var weightsOffset = vertex * weightBuffer.Stride + weightAttribute.Offset;
            var bindPosition = Vector3.Zero;
            var animatedPosition = Vector3.Zero;
            for (var influence = 0; influence < 4; influence++)
            {
                var localBoneIndex = indexBuffer.Data[indicesOffset + influence];
                var weight = BitConverter.ToSingle(
                    weightBuffer.Data,
                    weightsOffset + influence * sizeof(float));
                if (weight == 0f || localBoneIndex >= bones.Count) continue;
                var skeletonIndex = bones[localBoneIndex].SkeletonMatrixIndex;
                bindPosition += Vector3.Transform(position, bindMatrices[skeletonIndex]) * weight;
                animatedPosition += Vector3.Transform(position, animatedMatrices[skeletonIndex]) * weight;
            }
            maximum = Math.Max(maximum, Vector3.Distance(bindPosition, animatedPosition));
        }
        return maximum;
    }

    static bool IsFinite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14)
        && float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24)
        && float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34)
        && float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}

if (args is ["--load-animation", var loadAnimationGameData, var loadAnimationAsset, var loadAnimationClip])
{
    var loader = new EditorProjectLoader(
        new OpsReader(), new GameAssetResolverFactory(), new PkgArchiveReader(),
        new AssetManifestReader(), new PhyreD3D11ModelReader(), new PhyreD3D11TextureReader());
    var result = loader.LoadAnimationAsset(loadAnimationAsset, loadAnimationClip, loadAnimationGameData);
    Console.WriteLine($"{result.Status}: {result.Clip?.Name ?? result.Error}");
    if (result.Clip is { } loadedClip)
    {
        Console.WriteLine(
            $"Time {loadedClip.StartTime:R}..{loadedClip.EndTime:R}; "
            + $"channels {loadedClip.Channels.Count}");
        foreach (var group in loadedClip.Channels.GroupBy(value => value.TargetName))
            Console.WriteLine(
                $"  {group.Key}: {string.Join(", ", group.Select(value => value.Path))}");
    }
    return result.Status == AssetAnimationLoadStatus.Loaded ? 0 : 1;
}

if (args is ["--scene-skeletons", var skeletonScenePath, var skeletonGameData])
{
    var loader = new EditorProjectLoader(
        new OpsReader(), new GameAssetResolverFactory(), new PkgArchiveReader(),
        new AssetManifestReader(), new PhyreD3D11ModelReader(), new PhyreD3D11TextureReader());
    var scene = loader.OpenScript(skeletonScenePath, skeletonGameData);
    foreach (var load in scene.AssetModels.Values.Where(value => value.Model?.Skeleton is not null))
        Console.WriteLine($"{load.AssetId}: {load.Model!.Skeleton!.Joints.Count} joints");
    var packageReader = new PkgArchiveReader();
    foreach (var manifestLoad in scene.AssetManifests.Values.Where(value => value.Manifest?.PrimaryAsset is not null))
    {
        var manifest = manifestLoad.Manifest!;
        var resource = manifest.PrimaryAsset!.Resources.FirstOrDefault(value => value.Kind == AssetResourceKind.Model);
        if (resource is null) continue;
        var archive = packageReader.Read(manifest.SourcePackagePath);
        var cluster = new PhyreClusterReader().Read(archive.ReadEntry(resource.ArchiveEntryName));
        var animationGroups = cluster.Metadata.InstanceGroups
            .Where(value => value.Count > 0 && value.ClassName?.Contains("Animation", StringComparison.Ordinal) == true)
            .Select(value => $"{value.ClassName}:{value.Count}").ToArray();
        if (animationGroups.Length > 0)
            Console.WriteLine($"{manifestLoad.AssetId}: embedded {string.Join(", ", animationGroups)}");
    }
    return 0;
}

if (args is ["--phyre-material", var materialPackagePath, var materialEntryName])
{
    var archive = new PkgArchiveReader().Read(materialPackagePath);
    var cluster = new PhyreClusterReader().Read(archive.ReadEntry(materialEntryName));
    var memberNames = cluster.Metadata.Classes.SelectMany(value => value.Members)
        .ToDictionary(value => (uint)value.Index, value => value.Name);
    foreach (var user in cluster.Fixups.UserFixups)
    {
        Console.WriteLine($"USER #{user.Id}: {user.TypeName}, {Convert.ToHexString(user.Data.Span)}, text='{user.Text}'");
    }
    for (var groupIndex = 0; groupIndex < cluster.Metadata.InstanceGroups.Count; groupIndex++)
    {
        var group = cluster.Metadata.InstanceGroups[groupIndex];
        if (group.ClassName == "PAssetReferenceImport")
        {
            for (uint objectId = 0; objectId < group.Count; objectId++)
            {
                var idFixup = cluster.Fixups.Arrays.Single(value => value.SourceListIndex == groupIndex && value.SourceObjectId == objectId);
                var idPointer = cluster.Fixups.Pointers.Single(value => value.SourceListIndex == groupIndex && value.SourceObjectId == objectId);
                Console.WriteLine($"IMPORT {objectId}: '{ReadZeroTerminated(cluster, groupIndex, idFixup.Offset)}', pointer user={idPointer.UserFixupId}");
            }
        }

        if (group.ClassName != "PParameterBuffer") continue;
        var buffer = cluster.GetGroupObjectsData(groupIndex).Span;
        Console.WriteLine($"BUFFER group={groupIndex}, stored={group.ObjectsSize}, declared={BinaryPrimitives.ReadUInt32LittleEndian(buffer)}");
        foreach (var pointer in cluster.Fixups.Pointers.Where(value => value.SourceListIndex == groupIndex && value.UserFixupId is not null))
        {
            var user = cluster.Fixups.UserFixups[checked((int)pointer.UserFixupId!.Value)];
            Console.WriteLine($"  USER +0x{pointer.SourceOffset:X}: #{user.Id} {user.TypeName}, {Convert.ToHexString(user.Data.Span)}, text='{user.Text}'");
        }

        var definitionPointer = cluster.FindPointer(groupIndex, 0, 0x0c);
        var definitionCount = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8, 4));
        if (definitionPointer is null) continue;
        for (uint local = 0; local < definitionCount; local++)
        {
            var objectId = definitionPointer.DestinationObjectId + local;
            var definition = cluster.GetObject(checked((int)definitionPointer.DestinationListIndex), objectId).Span;
            var nameFixup = cluster.Fixups.Arrays.Single(value =>
                value.SourceListIndex == definitionPointer.DestinationListIndex && value.SourceObjectId == objectId
                && (!value.IsClassDataMember || memberNames[value.SourceMemberId] == "m_name"));
            var name = ReadZeroTerminated(cluster, checked((int)definitionPointer.DestinationListIndex), nameFixup.Offset);
            var location = BinaryPrimitives.ReadUInt16LittleEndian(definition.Slice(8, 2));
            var size = BinaryPrimitives.ReadUInt16LittleEndian(definition.Slice(10, 2));
            Console.WriteLine($"  DEF {local}: '{name}' nameCount={nameFixup.Count} nameOffset={nameFixup.Offset} type={definition[2]} data={definition[3]} array={BinaryPrimitives.ReadUInt16LittleEndian(definition)} loc=0x{location:X} size={size}");
        }
    }

    return 0;
}

static string ReadZeroTerminated(PhyreClusterData cluster, int groupIndex, uint offset)
{
    var group = cluster.Metadata.InstanceGroups[groupIndex];
    var remaining = cluster.GetArrayData(groupIndex, offset, group.ArraysSize - offset).Span;
    var zero = remaining.IndexOf((byte)0);
    if (zero < 0) throw new InvalidDataException("Unterminated diagnostic Phyre string.");
    return Encoding.ASCII.GetString(remaining[..zero]);
}

var tests = new (string Name, Action Run)[]
{
    ("reads a valid scenario header", ReadsValidHeader),
    ("reads an identifier stored after header tables", ReadsRelocatedIdentifier),
    ("rejects an invalid marker", RejectsInvalidMarker),
    ("rejects an unterminated identifier", RejectsUnterminatedIdentifier),
    ("reads OPS props and preserves source data", ReadsOpsProps),
    ("keeps OPS transforms in the Phyre scene basis", KeepsOpsTransformsInPhyreSceneBasis),
    ("creates scene instances at OPS prop transforms", CreatesSceneInstancesAtOpsTransforms),
    ("rejects malformed OPS vectors", RejectsMalformedOpsVector),
    ("resolves the requested localized asset variant", ResolvesLocalizedAsset),
    ("falls back to the base asset variant", FallsBackToBaseAsset),
    ("reports missing and ambiguous assets", ReportsMissingAndAmbiguousAssets),
    ("reads uncompressed PKG entries", ReadsUncompressedPackageEntry),
    ("decompresses NISLZSS PKG entries", DecompressesPackageEntry),
    ("rejects truncated PKG entry data", RejectsTruncatedPackageEntry),
    ("selects the matching asset manifest symbol", SelectsManifestSymbol),
    ("uses a documented single-asset manifest fallback", UsesManifestFallback),
    ("rejects a Phyre cluster with an unknown marker", RejectsUnknownPhyreMarker),
    ("reads Phyre packed class members", ReadsPhyrePackedClassMembers),
    ("decompresses Phyre pointer and array fixups", DecompressesPhyreFixups),
    ("raycasts transformed scene triangles exactly", RaycastsTransformedSceneTriangles),
    ("returns every model hit in depth order", ReturnsEveryModelHitInDepthOrder),
    ("preserves the declared selection kind of model instances", PreservesModelSelectionKind),
    ("reports unsupported picking geometry", ReportsUnsupportedPickingGeometry),
    ("reports truncated picking vertex data", ReportsTruncatedPickingVertexData),
    ("reports truncated picking index data", ReportsTruncatedPickingIndexData),
    ("calculates transformed scene bounds", CalculatesTransformedSceneBounds),
    ("creates a center viewport ray", CreatesCenterViewportRay),
    ("validates explicit viewport lighting", ValidatesViewportLighting),
    ("derives viewport behavior from Phyre effect switches", DerivesViewportMaterialSettings),
    ("resolves Phyre archive asset paths", ResolvesPhyreArchiveAssetPaths),
    ("resolves Phyre material render phases", ResolvesPhyreMaterialRenderPhases),
    ("selects Phyre shader permutations from declared contexts", SelectsPhyreShaderPermutationContexts),
    ("selects authored environment variants like the game", SelectsAuthoredEnvironmentVariants),
    ("supports editor camera orbit and free flight", KeepsEditorCameraOrbitCentered),
    ("smooths accumulated editor camera dolly input", SmoothsEditorCameraDollyInput),
    ("builds a ground-oriented surface placement decal", BuildsSurfacePlacementDecal),
    ("builds typed OPS overlay geometry", BuildsTypedOpsOverlayGeometry),
    ("renders declared sound volume shapes", RendersDeclaredSoundVolumeShapes),
    ("picks exact OPS volume geometry", PicksExactOpsVolumeGeometry),
    ("undoes and redoes scene document transforms", UndoesAndRedoesSceneDocumentTransforms),
    ("picks and parameterizes translation gizmo axes", PicksTranslationGizmoAxes),
    ("picks rotation rings and computes signed angles", PicksRotationRings),
    ("picks camera eye and look-at handles", PicksCameraHandles),
    ("snaps scene transforms to explicit increments", SnapsSceneTransforms),
    ("groups editable elements for the scene outliner", GroupsSceneOutlinerElements),
    ("validates and normalizes game installations", ValidatesGameInstallations),
    ("persists editor user settings", PersistsEditorUserSettings),
    ("catalogs battle maps and creates minimal INF metadata", CatalogsBattleMapAssets),
    ("defines a complete field-monster OP19 profile", DefinesFieldMonsterSpawnProfile),
    ("writes transformed OPS props without losing unknown data", WritesTransformedOpsProps),
    ("writes duplicated and deleted OPS spatial elements", WritesStructuralOpsEdits),
    ("creates observed OPS spatial profiles in empty sections", CreatesObservedOpsProfiles),
    ("offers script functions only for an entry box that runs one", ResolvesEntryBoxNameKind),
    ("writes an entry box dragged in the viewport back to its own file", WritesDraggedEntryBox),
    ("saves a moved entry box over the map the game reads, and reverts it", SavesMapInPlaceAndReverts),
    ("indexes PKG names without reading archives", IndexesPkgNamesWithoutReadingArchives),
    ("round-trips CS1 TBL entries byte-exactly", RoundTripsCs1Table),
    ("preserves localized QSText stale lengths", PreservesQuestTextStaleLength),
    ("indexes verified quest script mutations by opcode and selector", IndexesQuestScriptMutations),
    ("encodes the established fishing spot payload exactly", EncodesFishingSpotPayload),
    ("follows exact local shop calls to OP114", ResolvesShopScriptBinding),
    ("edits shop titles and inventory without losing unknown words", EditsShopTable),
    ("creates a shop title by preserving an explicit binary template", CreatesShopTitleFromTemplate),
    ("creates a fishing point by preserving an explicit binary template", CreatesFishingPointFromTemplate),
    ("catalogs ambiguous model packages without guessing", CatalogsModelImportCandidates),
    ("imports textured OBJ into the canonical model scene", ImportsCanonicalObjModel),
    ("resolves a uniquely identified package texture", ResolvesUniquePackageTexture),
    ("adapts canonical skinning explicitly for Phyre", AdaptsCanonicalSkinningForPhyre),
    ("maps common rig bone names onto the game's own", MapsCommonRigBoneNames),
    ("matches imported animations to the slots the game plays", MatchesImportedAnimationSlots),
    ("binds an unskinned mesh to the nearest bones", BindsUnskinnedMeshByProximity),
    ("adapts canonical geometry and animation for preview", AdaptsCanonicalModelForPreview),
    ("resolves semantic TBL references by category", ResolvesSemanticTableReferences),
    ("builds semantic choices from the requested TBL category", BuildsSemanticTableChoices),
    ("flattens repeated and referenced TBL schema fields", FlattensTblSchemaFields),
    ("edits typed TBL fields without changing adjacent values", EditsTypedTblFields),
    ("evaluates hierarchical Phyre skeleton animation", EvaluatesSkeletonAnimation),
    ("evaluates embedded scene-node animation", EvaluatesSceneNodeAnimation),
    ("reads exact animation actions from object INF metadata", ReadsObjectAnimationInfo),
    ("segments embedded animations by authored INF frames", SegmentsEmbeddedAnimation),
    ("backs up, restores and ships mod project files", TracksModProjectFiles),
    ("round-trips an effect file byte-exactly", RoundTripsEffectFile),
    ("evaluates effect keyframe tracks like the engine", EvaluatesEffectTracks),
    ("adds, removes and moves effect segments", EditsEffectSegments),
    ("writes a new effect from the format alone", CreatesEffectFromScratch),
    ("reads back an authored cluster as the bytes it wrote", AuthoredClusterRoundTrips),
    ("writes the coherent Falcom AssetProcessor model ABI", WritesAssetProcessorModelAbi),
    ("writes AssetProcessor objects against the CS1 runtime class registry", WritesCs1RuntimeAuthoringAbi),
    ("authors a material from an effect's declared ABI", AuthorsMaterialFromEffectAbi),
    ("writes engine-compatible authored string and material fixups", WritesAuthoredModelFixups),
    ("sets a material's declared parameters from typed values", SetsMaterialParameterValues),
    ("binds each material to the shader the author assigned it", BindsAuthoredShadersPerMaterial),
    ("swaps a shipped material's shader in place, or refuses", RepointsShippedMaterialShader),
    ("keeps what a map was authored from, so it can be opened again", RemembersMapAuthoring),
    ("feeds a native shader's constants from their own names", FeedsNativeShaderConstants),
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

return failures == 0 ? 0 : 1;

static void CatalogsModelImportCandidates()
{
    var directory = Path.Combine(Path.GetTempPath(), $"ed8-model-catalog-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        File.WriteAllBytes(Path.Combine(directory, "model.fbx"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(directory, "model.dae"), new byte[] { 2 });
        File.WriteAllBytes(Path.Combine(directory, "texture.png"), new byte[] { 3 });
        var candidates = ModelImportCatalog.Find(directory);
        if (candidates.Count != 2
            || candidates.Select(value => Path.GetExtension(value.Path))
                .OrderBy(value => value)
                .SequenceEqual(new[] { ".dae", ".fbx" }) == false)
        {
            throw new InvalidOperationException(
                "A package with FBX and COLLADA did not expose both explicit choices.");
        }
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void ImportsCanonicalObjModel()
{
    var directory = Path.Combine(Path.GetTempPath(), $"ed8-model-import-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var obj = Path.Combine(directory, "triangle.obj");
        File.WriteAllText(
            obj,
            """
            mtllib triangle.mtl
            o Triangle
            v 0 0 0
            v 1 0 0
            v 0 1 0
            vt 0 0
            vt 1 0
            vt 0 1
            vn 0 0 1
            usemtl Surface
            f 1/1/1 2/2/1 3/3/1
            """);
        File.WriteAllText(
            Path.Combine(directory, "triangle.mtl"),
            """
            newmtl Surface
            Kd 1 1 1
            map_Kd albedo.png
            """);
        var textureBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        File.WriteAllBytes(Path.Combine(directory, "albedo.png"), textureBytes);

        var scene = new ModelImportService().Import(obj, directory);
        if (scene.Meshes.Count != 1
            || scene.Meshes[0].Vertices.Count != 3
            || scene.Meshes[0].Indices.Length != 3
            || scene.Materials.Count == 0
            || scene.Textures.Count != 1
            || !scene.Textures[0].EncodedData.SequenceEqual(textureBytes)
            || scene.Diagnostics.Any(value =>
                value.Severity == ImportedDiagnosticSeverity.Error))
        {
            throw new InvalidOperationException(
                "The OBJ did not survive canonical geometry/material/texture import.");
        }
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void ResolvesUniquePackageTexture()
{
    var directory = Path.Combine(Path.GetTempPath(), $"ed8-model-package-{Guid.NewGuid():N}");
    var modelDirectory = Path.Combine(directory, "model");
    var textureDirectory = Path.Combine(directory, "textures");
    Directory.CreateDirectory(modelDirectory);
    Directory.CreateDirectory(textureDirectory);
    try
    {
        var obj = Path.Combine(modelDirectory, "triangle.obj");
        File.WriteAllText(
            obj,
            """
            mtllib triangle.mtl
            o Triangle
            v 0 0 0
            v 1 0 0
            v 0 1 0
            usemtl Surface
            f 1 2 3
            """);
        File.WriteAllText(
            Path.Combine(modelDirectory, "triangle.mtl"),
            """
            newmtl Surface
            Kd 1 1 1
            map_Kd obsolete/export/path/albedo.png
            """);
        var expected = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var actualPath = Path.Combine(textureDirectory, "albedo.png");
        File.WriteAllBytes(actualPath, expected);

        var scene = new ModelImportService().Import(obj, directory);
        var texture = scene.Textures.Single();
        if (!Path.GetFullPath(actualPath).Equals(
                texture.SourcePath, StringComparison.OrdinalIgnoreCase)
            || !texture.EncodedData.SequenceEqual(expected)
            || scene.Diagnostics.Any(value => value.Code == "missing-texture"))
        {
            throw new InvalidOperationException(
                "A unique texture elsewhere in the selected package was not resolved exactly.");
        }
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

// An imported animation has to find the slot the game's own logic plays, whatever
// an exporter decided to call it. The slot names are read off C_NPC000's manifest.
static void MatchesImportedAnimationSlots()
{
    var slots = new[] { "WAIT", "WALK", "RUN", "BTL_WAIT", "FIELD_ATTACK" };

    foreach (var (imported, expected) in new[]
             {
                 ("RUN", "RUN"),
                 ("run", "RUN"),
                 ("Armature|Run", "RUN"),
                 ("btl_wait", "BTL_WAIT"),
                 ("Btl Wait", "BTL_WAIT"),
                 ("field-attack", "FIELD_ATTACK"),
             })
    {
        var found = CharacterAnimationPackage.GuessSlot(imported, slots);
        if (found != expected)
            throw new InvalidDataException($"'{imported}' matched {found ?? "nothing"}, not {expected}.");
    }

    // A name that means nothing here is left for a person rather than dropped on
    // whichever slot happens to sort first.
    if (CharacterAnimationPackage.GuessSlot("mocap_take_017", slots) is not null)
        throw new InvalidDataException("An unrecognised animation was matched to a slot.");

    // Renaming a channel's bone is what makes an imported curve drive anything;
    // a channel whose bone the mapping does not name is left out rather than
    // written against a name the skeleton has never heard of.
    var clip = new CpuAnimationClip("a", "RUN", 0f, 1f, new[]
    {
        new CpuAnimationChannel("mixamorig:Hips", CpuAnimationPath.Translation,
            CpuAnimationInterpolation.Linear, new[] { 0f }, new[] { Vector4.Zero }),
        new CpuAnimationChannel("cape_flap", CpuAnimationPath.Rotation,
            CpuAnimationInterpolation.Linear, new[] { 0f }, new[] { Vector4.Zero }),
    });
    var mapping = new Dictionary<string, string>(StringComparer.Ordinal) { ["mixamorig:Hips"] = "Hips" };
    var retargeted = CharacterAnimationPackage.Retarget(clip, mapping);
    if (retargeted.Channels.Count != 1 || retargeted.Channels[0].TargetName != "Hips")
    {
        throw new InvalidDataException(
            $"Retargeting kept {retargeted.Channels.Count} channel(s) named"
            + $" {string.Join(",", retargeted.Channels.Select(c => c.TargetName))}.");
    }
}

// A humanoid rig from the usual pipelines has to land on the game's own bone
// names without anyone typing a table. The names below are what Mixamo, Blender
// and a Max Biped actually export; the game's are ply000's, read off the file.
static void MapsCommonRigBoneNames()
{
    var game = new[]
    {
        "Hips", "Spine", "Head", "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
        "RightShoulder", "RightArm", "RightForeArm", "RightHand",
        "LeftUpLeg", "LeftLeg", "LeftFoot", "LeftToe",
        "RightUpLeg", "RightLeg", "RightFoot", "RightToe",
        "Bag01", "L_cat_point", "BS01",
    };

    // Mixamo: the game's own names under a namespace.
    var mixamo = new[]
    {
        "mixamorig:Hips", "mixamorig:Spine", "mixamorig:Head",
        "mixamorig:LeftArm", "mixamorig:LeftForeArm", "mixamorig:LeftHand",
        "mixamorig:RightUpLeg", "mixamorig:RightLeg", "mixamorig:RightFoot",
    };
    var mapped = Cs1RigNameMapper.AutoMap(mixamo, game);
    foreach (var one in mapped)
    {
        var expected = one.SourceName["mixamorig:".Length..];
        if (one.TargetName != expected)
            throw new InvalidDataException($"{one.SourceName} mapped to {one.TargetName ?? "nothing"}, not {expected}.");
    }

    // Blender/Rigify style: side as a suffix, different words for the same bones.
    var blender = new[] { "pelvis", "upper_arm.L", "lower_arm.L", "thigh.R", "shin.R", "foot.R" };
    var expectedBlender = new[] { "Hips", "LeftArm", "LeftForeArm", "RightUpLeg", "RightLeg", "RightFoot" };
    var blenderMapped = Cs1RigNameMapper.AutoMap(blender, game);
    for (var index = 0; index < blender.Length; index++)
    {
        if (blenderMapped[index].TargetName != expectedBlender[index])
        {
            throw new InvalidDataException(
                $"{blender[index]} mapped to {blenderMapped[index].TargetName ?? "nothing"},"
                + $" not {expectedBlender[index]}.");
        }
    }

    // A bone no convention covers is left for a person, never guessed at: a wrong
    // guess here drives a joint with the wrong rotation, which is worse than still.
    var unknown = Cs1RigNameMapper.AutoMap(new[] { "cape_flap_03" }, game);
    if (unknown[0].TargetName is not null)
        throw new InvalidDataException("An unrecognised bone was mapped rather than left blank.");

    // No two source bones may claim the same game bone.
    var duplicated = Cs1RigNameMapper.AutoMap(new[] { "Hips", "pelvis" }, game);
    if (duplicated.Count(one => one.TargetName == "Hips") != 1)
        throw new InvalidDataException("Two source bones were both mapped onto Hips.");
}

// A mesh with no weights at all still has to end up following the skeleton. Every
// vertex is placed right on top of one bone, so the nearest-bone bind has exactly
// one right answer and any mistake shows.
static void BindsUnskinnedMeshByProximity()
{
    var joints = new[]
    {
        new CpuSkeletonJoint("Hips", -1, Matrix4x4.CreateTranslation(0, 1, 0)),
        new CpuSkeletonJoint("Spine", 0, Matrix4x4.CreateTranslation(0, 0.5f, 0)),
        new CpuSkeletonJoint("Head", 1, Matrix4x4.CreateTranslation(0, 0.5f, 0)),
        new CpuSkeletonJoint("head_point", 2, Matrix4x4.CreateTranslation(0, 0.2f, 0)),
    };
    var skeleton = new CpuSkeleton(joints, Array.Empty<Matrix4x4>(), Array.Empty<int>());

    // One vertex sitting on each of the three deforming joints.
    var positions = new[] { new Vector3(0, 1, 0), new Vector3(0, 1.5f, 0), new Vector3(0, 2f, 0) };
    var vertices = positions.Select(position => new PhyreVertexSource(
        position, Vector3.UnitY,
        new[] { new PhyreTexCoordSet(Vector2.Zero, Vector3.UnitX, Vector3.UnitZ) },
        Array.Empty<int>(), Array.Empty<float>())).ToArray();
    var model = new PhyreModelSource(
        "m", new[] { new PhyreMeshSource("m", vertices, new[] { 0, 1, 2 }) },
        Array.Empty<PhyreJointSource>());

    var bound = PhyreProximitySkinBinder.Bind(model, skeleton);
    var boundVertices = bound.Meshes[0].Vertices;
    for (var index = 0; index < 3; index++)
    {
        if (boundVertices[index].Joints[0] != index)
        {
            throw new InvalidDataException(
                $"Vertex {index} sits on joint {index} but bound to {boundVertices[index].Joints[0]}.");
        }
        var total = boundVertices[index].Weights.Sum();
        if (MathF.Abs(total - 1f) > 1e-4f)
            throw new InvalidDataException($"Vertex {index} weights sum to {total}, not one.");
        // An attachment locator never deforms geometry, so nothing may bind to it.
        if (boundVertices[index].Joints.Take(2).Contains(3))
            throw new InvalidDataException($"Vertex {index} bound to the attachment locator.");
    }

    // Fitting to height has to make an import of any size stand where the skeleton
    // does — the whole basis on which "nearest bone" means anything.
    var giant = model with
    {
        Meshes = new[]
        {
            model.Meshes[0] with
            {
                Vertices = vertices
                    .Select(vertex => vertex with { Position = vertex.Position * 100f })
                    .ToArray(),
            },
        },
    };
    var fitted = PhyreProximitySkinBinder.FitToHeight(giant, skeleton);
    var fittedY = fitted.Meshes[0].Vertices.Select(vertex => vertex.Position.Y).ToArray();
    if (MathF.Abs(fittedY.Min() - 1f) > 1e-3f || MathF.Abs(fittedY.Max() - 2f) > 1e-3f)
    {
        throw new InvalidDataException(
            $"Fitted mesh spans {fittedY.Min()}..{fittedY.Max()}, not the skeleton's 1..2.");
    }
}

static void AdaptsCanonicalSkinningForPhyre()
{
    var nodes = Enumerable.Range(0, 6)
        .Select(index => new ImportedSceneNode(
            $"joint{index}",
            index - 1,
            Matrix4x4.CreateTranslation(index, 0, 0),
            index == 0 ? new[] { 0 } : Array.Empty<int>()))
        .ToArray();
    var influences = Enumerable.Range(1, 5)
        .Select(index => new ImportedVertexInfluence(index, index))
        .ToArray();
    var vertex = new ImportedVertex(
        new Vector3(100, 0, 0),
        Vector3.UnitY,
        Vector3.UnitX,
        Vector3.UnitZ,
        new[] { Vector2.Zero },
        Array.Empty<Vector4>(),
        influences);
    var skin = new ImportedSkin(
        Enumerable.Range(1, 5).ToDictionary(index => index, _ => Matrix4x4.Identity));
    var scene = new ImportedModelScene(
        "Skin",
        "memory",
        new ImportedCoordinateSystem(true, ImportedUpAxis.Y, 0.01f),
        nodes,
        new[] { new ImportedMesh("mesh", new[] { vertex, vertex, vertex }, new[] { 0, 1, 2 }, 0, skin) },
        new[]
        {
            new ImportedMaterial(
                "material", Vector4.One, Vector3.Zero, 0f, 1f, 1f, false,
                new Dictionary<ImportedTextureUsage, int>(),
                new Dictionary<string, string>()),
        },
        Array.Empty<ImportedTexture>(),
        Array.Empty<ImportedAnimationClip>(),
        Array.Empty<ImportedModelDiagnostic>());
    var converted = ImportedModelPhyreAdapter.Convert(scene);
    var convertedVertex = converted.Meshes.Single().Vertices[0];
    if (convertedVertex.Joints.Length != 4
        || convertedVertex.Joints.SequenceEqual(new[] { 5, 4, 3, 2 }) == false
        || Math.Abs(convertedVertex.Weights.Sum() - 1f) > 0.0001f
        || Vector3.Distance(convertedVertex.Position, Vector3.UnitX) > 0.0001f)
    {
        throw new InvalidOperationException(
            "The Phyre adapter did not explicitly trim/normalize skinning and units.");
    }
}

static void AdaptsCanonicalModelForPreview()
{
    var nodes = new[]
    {
        new ImportedSceneNode("root", -1, Matrix4x4.CreateTranslation(100, 0, 0), new[] { 0 }),
        new ImportedSceneNode("bone", 0, Matrix4x4.CreateTranslation(0, 100, 0), Array.Empty<int>()),
    };
    var vertices = Enumerable.Range(0, 3)
        .Select(index => new ImportedVertex(
            new Vector3(index * 100, 0, 0),
            Vector3.UnitY,
            Vector3.UnitX,
            Vector3.UnitZ,
            new[] { Vector2.Zero },
            Array.Empty<Vector4>(),
            new[] { new ImportedVertexInfluence(1, 1f) }))
        .ToArray();
    var scene = new ImportedModelScene(
        "Preview",
        "preview.fbx",
        new ImportedCoordinateSystem(true, ImportedUpAxis.Y, 0.01f),
        nodes,
        new[]
        {
            new ImportedMesh(
                "mesh",
                vertices,
                new[] { 0, 1, 2 },
                0,
                new ImportedSkin(new Dictionary<int, Matrix4x4>
                {
                    [1] = Matrix4x4.Identity,
                })),
        },
        new[]
        {
            new ImportedMaterial(
                "material", Vector4.One, Vector3.Zero, 0f, 1f, 1f, false,
                new Dictionary<ImportedTextureUsage, int>(),
                new Dictionary<string, string>()),
        },
        Array.Empty<ImportedTexture>(),
        new[]
        {
            new ImportedAnimationClip(
                "move",
                1d,
                new[]
                {
                    new ImportedAnimationChannel(
                        1,
                        new[]
                        {
                            new ImportedVectorKey(0d, new Vector3(0, 100, 0)),
                            new ImportedVectorKey(1d, new Vector3(0, 200, 0)),
                        },
                        Array.Empty<ImportedQuaternionKey>(),
                        Array.Empty<ImportedVectorKey>()),
                }),
        },
        Array.Empty<ImportedModelDiagnostic>());

    var result = ImportedModelCpuAdapter.Convert(scene);
    var positionData = result.Model.Meshes.Single().Primitives.Single()
        .VertexBuffers.Single(value =>
            value.Attributes.Single().Semantic == VertexSemantic.Position).Data;
    var clip = result.Animations.Single();
    if (result.Model.Skeleton?.Joints.Count != 2
        || result.Model.Skeleton.Joints[0].DefaultLocalTransform.M41 != 1f
        || BitConverter.ToSingle(positionData, 12) != 1f
        || clip.Channels.Single().Values[1].Y != 2f)
    {
        throw new InvalidOperationException(
            "Preview adaptation did not preserve the rig/animation while converting source units.");
    }
    _ = new CpuSkeletonPoseEvaluator().Evaluate(
        result.Model.Skeleton, clip, clip.EndTime);
}

static void EncodesFishingSpotPayload()
{
    var binding = new FishingSpotScriptBinding(
        FunctionIndex: 12,
        FunctionName: "LP_fishpoint00",
        InstructionIndex: 7,
        PayloadArgumentIndex: 1,
        FishingPointId: 7,
        PlayerPosition: new Vector3(21.85f, -1.12f, -42.13f),
        HeadingDegrees: -70f,
        WaterTarget: new Vector3(19.06f, -1.47f, -39.95f));
    var expected =
        "07000000CDCCAE41295C8FBF1F8528C200008CC2E17A9841F628BCBFCDCC1FC2";
    var actual = Convert.ToHexString(binding.EncodePayload());
    if (actual != expected)
        throw new InvalidOperationException($"Fishing payload mismatch: {actual}.");
}

static void ResolvesShopScriptBinding()
{
    static InstructionArgument Scalar(int index, string type, int value) =>
        new(index, "scalar", type, value, value, Array.Empty<byte>(), null);
    static InstructionArgument Text(int index, string value) =>
        new(index, "string", "string", 0, 0, Encoding.UTF8.GetBytes(value + "\0"), null);
    var script = new DecompiledScript("shop-test", new[]
    {
        new DecompiledFunction(0, "LP_Shop01", true, new[]
        {
            new DecompiledInstruction(0, 0, "CallExt", 4,
                new[] { Scalar(0, "u8", 11), Text(1, "TK_Keilis") },
                Array.Empty<JumpTarget>()),
        }),
        new DecompiledFunction(1, "TK_Keilis", true, new[]
        {
            new DecompiledInstruction(0, 0, "OP114", 114,
                new[] { Scalar(0, "s16", 110) },
                Array.Empty<JumpTarget>()),
        }),
    });
    var binding = ShopScriptBinding.Read(script, "LP_Shop01").Single();
    if (binding.ShopId != 110
        || !binding.CallPath.SequenceEqual(new[] { "LP_Shop01", "TK_Keilis" }))
        throw new Exception("The local shop call chain was not resolved exactly.");
}

static void EditsShopTable()
{
    var directory = Path.Combine(
        Path.GetTempPath(), "ed8editor-shop-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "t_shop.tbl");
    try
    {
        var title = new byte[]
        {
            110, 0, 7, (byte)'O', (byte)'l', (byte)'d', 0,
            1, 2, 3, 4, 5, 6, 7, 8,
        };
        var item = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(item, 110);
        BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(2), 511);
        BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(4), 6);
        new Cs1TableDocumentBuilder()
            .WithEntry("ShopTitle", title)
            .WithEntry("ShopItem", item)
            .Build()
            .Write(path);

        var table = Cs1ShopTable.Read(path);
        table.SetTitleName(110, "New title");
        table.ReplaceItems(110, new[]
        {
            new Cs1ShopItemValue(512, 6),
            new Cs1ShopItemValue(514, 3),
        });
        table.Write();

        var reopened = Cs1ShopTable.Read(path);
        var reopenedTitle = reopened.Titles.Single();
        var reopenedItems = reopened.Items(110);
        if (reopenedTitle.Name != "New title"
            || !reopenedTitle.UnknownSuffix.SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })
            || reopenedItems.Count != 2
            || reopenedItems[0].ItemId != 512
            || reopenedItems[0].UnknownValue != 6
            || reopenedItems[1].UnknownValue != 3)
        {
            throw new Exception("The shop table edit did not preserve its unknown data.");
        }
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void CreatesShopTitleFromTemplate()
{
    var directory = Path.Combine(
        Path.GetTempPath(), "ed8editor-shop-create-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "t_shop.tbl");
    try
    {
        var title = new byte[]
        {
            110, 0, 7, (byte)'O', (byte)'l', (byte)'d', 0,
            1, 2, 3, 4, 5, 6, 7, 8,
        };
        new Cs1TableDocumentBuilder()
            .WithEntry("ShopTitle", title)
            .Build()
            .Write(path);
        var table = Cs1ShopTable.Read(path);
        table.AddTitle(111, "Created", 110);
        table.Write();

        var created = Cs1ShopTable.Read(path).Titles.Single(value => value.Id == 111);
        if (created.Name != "Created"
            || created.UnknownByte != 7
            || !created.UnknownSuffix.SequenceEqual(
                new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }))
        {
            throw new Exception("The cloned ShopTitle lost undocumented template bytes.");
        }
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void CreatesFishingPointFromTemplate()
{
    var directory = Path.Combine(
        Path.GetTempPath(), "ed8editor-fish-create-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "t_fish.tbl");
    try
    {
        var payload = Enumerable.Range(0, Cs1FishingPointTable.RecordSize)
            .Select(value => (byte)value)
            .ToArray();
        BinaryPrimitives.WriteInt16LittleEndian(payload, 12);
        for (var field = 5; field < 18; field++)
            BinaryPrimitives.WriteInt16LittleEndian(
                payload.AsSpan(field * sizeof(short), sizeof(short)),
                -1);
        BinaryPrimitives.WriteInt16LittleEndian(
            payload.AsSpan(5 * sizeof(short), sizeof(short)),
            2);
        new Cs1TableDocumentBuilder()
            .WithEntry("fish_pnt", payload)
            .Build()
            .Write(path);
        var fishName = new byte[2 + "Kasagin".Length + 1];
        BinaryPrimitives.WriteInt16LittleEndian(fishName, 2);
        Encoding.UTF8.GetBytes("Kasagin").CopyTo(fishName, 2);
        new Cs1TableDocumentBuilder()
            .WithEntry("QSFish", fishName)
            .Build()
            .Write(Path.Combine(directory, "t_notefish.tbl"));
        var table = Cs1FishingPointTable.Read(path);
        if (table.Fish.Single().Name != "Kasagin"
            || table.Points.Single().FishNames.Single() != "Kasagin")
        {
            throw new Exception("Fishing species IDs were not resolved through t_notefish.tbl.");
        }
        table.AddPoint(13, 12, new[] { 2 });
        table.Write();

        var document = Cs1TableDocument.Read(path);
        var records = document.Entries
            .Where(value => value.Category == "fish_pnt")
            .ToArray();
        if (records.Length != 2
            || BinaryPrimitives.ReadInt16LittleEndian(records[1].Data) != 13
            || !records[1].Data.AsSpan(2).SequenceEqual(payload.AsSpan(2)))
        {
            throw new Exception("The cloned fish_pnt lost undocumented template bytes.");
        }
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void IndexesQuestScriptMutations()
{
    static InstructionArgument Scalar(int index, int value) =>
        new(index, "scalar", "s16", value, value, Array.Empty<byte>(), null);
    var instructions = new[]
    {
        new DecompiledInstruction(0, 12, "renamed", 103,
            new[] { Scalar(0, 28), Scalar(1, 3), Scalar(2, 4) },
            Array.Empty<JumpTarget>()),
        new DecompiledInstruction(1, 18, "also_renamed", 103,
            new[] { Scalar(0, 28), Scalar(1, 1), Scalar(2, 7) },
            Array.Empty<JumpTarget>()),
        new DecompiledInstruction(2, 24, "OP103_6", 103,
            new[] { Scalar(0, 28), Scalar(1, 6), Scalar(2, 2) },
            Array.Empty<JumpTarget>()),
        new DecompiledInstruction(3, 30, "unrelated", 100,
            new[] { Scalar(0, 28), Scalar(1, 1) },
            Array.Empty<JumpTarget>()),
    };
    var script = new DecompiledScript("test", new[]
    {
        new DecompiledFunction(2, "arbitrary_name", true, instructions),
    });
    var result = new QuestScriptAnalyzer().Analyze("quest-test.dat", script);
    if (result.Count != 3
        || result[0].Kind != QuestMutationKind.LifecycleFlags
        || result[0].Value != 4
        || result[1].Kind != QuestMutationKind.JournalStage
        || result[1].Value != 7
        || result[2].Kind != QuestMutationKind.UnknownSelector6)
    {
        throw new InvalidOperationException(
            "Quest mutations were not classified from the raw opcode/selector pair.");
    }
}

static void CatalogsBattleMapAssets()
{
    var root = Path.Combine(Path.GetTempPath(), $"ed8editor-battle-{Guid.NewGuid():N}");
    var data = Path.Combine(root, "data");
    var existing = Path.Combine(data, "map", "battle", "bm0010");
    try
    {
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "bm0010.inf"), "<node_infomation></node_infomation>");
        File.WriteAllBytes(Path.Combine(existing, "cloud.uvb"), Encoding.ASCII.GetBytes("UVab"));

        var catalog = new BattleMapAssetCatalog(data);
        var entry = catalog.Entries.Single();
        if (entry.AssetId != "bm0010" || !entry.HasInf
            || entry.UvAnimationFiles.Single() != "cloud.uvb")
        {
            throw new InvalidOperationException("The existing battle-map metadata was not cataloged.");
        }

        var created = catalog.CreateMinimalInf("bm9990");
        var bytes = File.ReadAllBytes(created.InfPath);
        if (!created.HasInf || bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })
            || !Encoding.UTF8.GetString(bytes).Contains("<node_infomation>"))
        {
            throw new InvalidOperationException("The minimal battle-map INF is not the authored XML skeleton.");
        }
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static void BuildsSurfacePlacementDecal()
{
    var normal = Vector3.Normalize(new Vector3(0.25f, 1f, -0.4f));
    var position = new Vector3(3f, 4f, 5f);
    var geometry = SceneSurfacePlacementMarker.Build(position, normal, 2f);
    if (geometry.Lines.Count != 22)
        throw new Exception("Surface decal did not create its ring and cross.");
    if (geometry.Triangles.Count != 20)
        throw new Exception("Surface decal did not create its translucent fill.");
    foreach (var point in geometry.Triangles.SelectMany(value =>
                 new[] { value.A, value.B, value.C }))
    {
        var planeDistance = Vector3.Dot(point - position, normal);
        if (planeDistance <= 0f || planeDistance >= 0.1f)
            throw new Exception("Surface decal is not oriented in the picked surface plane.");
    }
}

static void DefinesFieldMonsterSpawnProfile()
{
    var profile = FieldMonsterSpawnParameters.CreateDefault(2000, "mon116", 4, 2);
    if (profile.EntityType != 2)
        throw new Exception("Field-monster entity type changed.");
    if (profile.Scale != -1f)
        throw new Exception("Field-monster scale sentinel changed.");
    if (profile.BattleFunctionIndex != 4 || profile.EncounterIndex != 2)
        throw new Exception("Encounter linkage was not encoded in the OP19 profile.");
    if (profile.UnknownParameter1 != 0x40C00000
        || profile.UnknownParameter2 != 0x41A00000)
        throw new Exception("Verified retail raw parameters changed.");
}

// An .eff is read with its version and flag word in hand, and its fixed-width
// name fields keep authoring leftovers past their null terminator. Reading one
// and writing it back must reproduce every byte, leftovers included, or an edit
// would silently shift everything that follows.
static void RoundTripsEffectFile()
{
    var original = BuildColdSteelEffect();
    var effect = EffFileReader.Read(original);
    if (effect.EffectName != "test_fx")
        throw new Exception("The effect name was not read from its fixed field.");
    if (effect.Textures is not ["fx_smoke.dds"])
        throw new Exception("The texture list was not read.");
    var segment = effect.Segments.Single();
    if (segment.Name != "煙" || segment.TextureName != "I_EFTEX000")
        throw new Exception("The segment names were not read.");
    if (segment.StructFlags != 3)
        throw new Exception("The PC layout must settle on flags 0x003 after block 15.");
    if (segment.Position.Count != 2 || Math.Abs(segment.Position[1].Time - 0.5f) > 1e-6f)
        throw new Exception("The position track was not read.");
    if (segment.Position[1].Flags != 0x0003)
        throw new Exception("The keyframe mode word was not read from the low half of its integer.");
    if (segment.Data17PcRaw.Length != 16)
        throw new Exception("The unparsed PC block was not kept.");

    var written = EffFileWriter.Write(effect);
    if (!written.AsSpan().SequenceEqual(original))
        throw new Exception("The effect did not round-trip byte-exactly.");

    // A renamed effect is re-encoded rather than written from the stale bytes.
    effect.EffectName = "renamed";
    var renamed = EffFileReader.Read(EffFileWriter.Write(effect));
    if (renamed.EffectName != "renamed" || renamed.Segments.Single().Name != "煙")
        throw new Exception("Renaming an effect lost its content.");
}

static byte[] BuildColdSteelEffect()
{
    var stream = new MemoryStream();
    var writer = new BinaryWriter(stream);
    void Fixed(string text, int size, bool japanese = false)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = japanese ? Encoding.GetEncoding(932).GetBytes(text) : Encoding.ASCII.GetBytes(text);
        writer.Write(bytes);
        for (var index = bytes.Length; index < size; index++) writer.Write((byte)0);
    }
    void Floats(int count, float seed)
    {
        for (var index = 0; index < count; index++) writer.Write(seed + index);
    }
    void Ints(int count)
    {
        for (var index = 0; index < count; index++) writer.Write((uint)index);
    }
    void Keyframe(float time, uint flags)
    {
        for (var index = 0; index < 8; index++) writer.Write(index * 0.25f);
        writer.Write(time);
        writer.Write(flags);
        writer.Write(0u);
        writer.Write(1f);
    }

    writer.Write(EffGameVersion.Pc);
    writer.Write(7u);
    Fixed("test_fx", 16);
    writer.Write(1u);
    Fixed("fx_smoke.dds", 20);
    writer.Write(0u);
    writer.Write(1u);

    Fixed("煙", 16, japanese: true);
    // Authoring leftovers after the terminator: the writer must keep them.
    stream.Seek(-3, SeekOrigin.End);
    writer.Write((byte)0x41);
    stream.Seek(0, SeekOrigin.End);
    Fixed("I_EFTEX000", 16);
    Fixed("", 16);
    Ints(8);
    Floats(12, 1f);
    Floats(3, 2f);
    Floats(9, 3f);
    Floats(8, 4f);
    writer.Write(2u);
    Keyframe(0f, 0x0000);
    Keyframe(0.5f, 0x0003);
    for (var track = 0; track < 5; track++) writer.Write(0u);
    writer.Write(0u);
    Floats(2, 5f);
    Floats(16, 6f);
    for (var index = 0; index < 16; index++) writer.Write((byte)(0x10 + index));
    writer.Write(new byte[8]);
    return stream.ToArray();
}

// A new effect is written from what the format says, not copied from a file the
// game ships. It has to read back as what was written — above all the blocks the
// PC layout always expects after the spawn list, which have no flag word to
// announce them.
/// <summary>
/// A cluster this project writes has to read back as the bytes it wrote.
///
/// Not a style point. The fixup tables are what the engine walks to write pointers
/// INTO objects, so a block our own reader decodes differently from the way our
/// writer packed it is a block whose pointers land somewhere nobody chose — and an
/// overwritten vtable is exactly the crash the game reports (0xC0000005 with an
/// invalid instruction pointer).
///
/// This caught a real disagreement: one byte of the pointer table, in the last
/// block. The two oracles that compare an authored cluster with a shipped one could
/// not see it, because both read the file the same wrong way.
/// </summary>
static void AuthoredClusterRoundTrips()
{
    var vertices = new List<PhyreVertexSource>();
    foreach (var corner in new[]
             {
                 new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f),
                 new Vector3(0.5f, 1f, -0.5f), new Vector3(-0.5f, 1f, -0.5f),
             })
    {
        vertices.Add(new PhyreVertexSource(
            corner,
            new Vector3(0f, 0f, -1f),
            new[] { new PhyreTexCoordSet(new Vector2(0f, 0f), Vector3.UnitX, Vector3.UnitY) },
            Array.Empty<int>(),
            Array.Empty<float>()));
    }
    var model = new PhyreModelSource(
        "roundtrip",
        new[] { new PhyreMeshSource("mesh", vertices, new[] { 0, 1, 2, 0, 2, 3 }) },
        Array.Empty<PhyreJointSource>());

    var written = PhyreClusterAssembler.Assemble(PhyreModelClusterWriter.Contents(
        model, new PhyreShaderBinding("shaders/ed8.fx#TEST"),
        PhyreModelGeometryPacker.Pack(model)));

    // Taken apart with our own readers and put back together with our own writer.
    var cut = PhyreClusterSectionReader.Read(written);
    var data = new PhyreClusterReader().Read(written);
    var fixups = new PhyreFixupReader().Read(written, cut.Metadata);
    var classes = cut.Metadata.Classes.ToList();
    var groups = new List<PhyreGroupContents>();
    foreach (var group in cut.Metadata.InstanceGroups)
    {
        var className = group.ClassName ?? string.Empty;
        var objects = new List<PhyreObjectContents>();
        var each = group.Count == 0 ? 0 : (int)(group.ObjectsSize / group.Count);
        var stored = data.GetGroupObjectsData(group.Index).Span;
        for (uint id = 0; id < group.Count; id++)
        {
            objects.Add(PhyreObjectWriter.ReadObject(
                stored.Slice((int)(id * each), each), className, classes));
        }
        groups.Add(new PhyreGroupContents(
            className,
            objects,
            group.ArraysSize == 0
                ? ReadOnlyMemory<byte>.Empty
                : data.GetArrayData(group.Index, 0, group.ArraysSize)));
    }

    var again = PhyreClusterAssembler.Assemble(new PhyreClusterContents(
        cut.Metadata.Types,
        groups,
        fixups,
        fixups.UserFixups,
        cut.HeaderClasses,
        cut.Payload,
        PhyreNamespaceWriter.ReadUnmodelledHeader(cut.PackedNamespace),
        cut.Header[(17 * sizeof(uint))..]));

    Equal(written.Length, again.Length);
    for (var at = 0; at < written.Length; at++)
    {
        if (written[at] == again[at]) continue;
        throw new InvalidOperationException(
            $"An authored cluster does not read back as itself: byte {at} was written"
            + $" 0x{written[at]:X2} and comes back 0x{again[at]:X2}."
            + " The fixup tables are what the engine walks to place pointers, so a"
            + " block it decodes differently writes them somewhere nobody chose.");
    }
}

static void WritesAssetProcessorModelAbi()
{
    var vertices = new[]
    {
        new PhyreVertexSource(
            Vector3.Zero, Vector3.UnitZ,
            Array.Empty<PhyreTexCoordSet>(), Array.Empty<int>(), Array.Empty<float>()),
        new PhyreVertexSource(
            Vector3.UnitX, Vector3.UnitZ,
            Array.Empty<PhyreTexCoordSet>(), Array.Empty<int>(), Array.Empty<float>()),
        new PhyreVertexSource(
            Vector3.UnitY, Vector3.UnitZ,
            Array.Empty<PhyreTexCoordSet>(), Array.Empty<int>(), Array.Empty<float>()),
    };
    var model = new PhyreModelSource(
        "asset_processor_abi",
        new[] { new PhyreMeshSource("triangle", vertices, new[] { 0, 1, 2 }) },
        Array.Empty<PhyreJointSource>());
    var cluster = PhyreClusterAssembler.Assemble(
        PhyreModelClusterWriter.Contents(
            model,
            new PhyreShaderBinding("shaders/ed8.fx#TEST"),
            PhyreModelGeometryPacker.Pack(model),
            schemaProfile: PhyreSchemaProfile.FalcomAssetProcessor));
    var read = new PhyreClusterReader().Read(cluster);

    Equal(126, read.Metadata.Classes.Count);
    Equal(15, read.Metadata.Types.Count);
    Equal(148u, read.Metadata.Classes.Single(value =>
        value.Name == "PClassDescriptor").Size);
    Equal(64u, read.Metadata.Classes.Single(value =>
        value.Name == "PDataBlockD3D11").Size);
    Equal(112u, read.Metadata.Classes.Single(value =>
        value.Name == "PMeshInstance").Size);
    if (read.Metadata.Classes.All(value => value.Name != "PIndexDataBlock"))
        throw new InvalidOperationException("The AssetProcessor PIndexDataBlock class is absent.");
    var meshInstance = read.Metadata.InstanceGroups.Single(value =>
        value.ClassName == "PMeshInstance");
    Equal(112u, meshInstance.ObjectsSize);
    Equal(0u, meshInstance.ArraysSize);
}

static void WritesCs1RuntimeAuthoringAbi()
{
    var vertices = new[]
    {
        new PhyreVertexSource(
            Vector3.Zero, Vector3.UnitZ,
            Array.Empty<PhyreTexCoordSet>(), Array.Empty<int>(), Array.Empty<float>()),
        new PhyreVertexSource(
            Vector3.UnitX, Vector3.UnitZ,
            Array.Empty<PhyreTexCoordSet>(), Array.Empty<int>(), Array.Empty<float>()),
        new PhyreVertexSource(
            Vector3.UnitY, Vector3.UnitZ,
            Array.Empty<PhyreTexCoordSet>(), Array.Empty<int>(), Array.Empty<float>()),
    };
    var model = new PhyreModelSource(
        "runtime_authoring_abi",
        new[] { new PhyreMeshSource("triangle", vertices, new[] { 0, 1, 2 }) },
        Array.Empty<PhyreJointSource>());
    var cluster = PhyreClusterAssembler.Assemble(
        PhyreModelClusterWriter.Contents(
            model,
            new PhyreShaderBinding("shaders/ed8.fx#TEST"),
            PhyreModelGeometryPacker.Pack(model),
            schemaProfile: PhyreSchemaProfile.Cs1RuntimeAuthoring));
    var read = new PhyreClusterReader().Read(cluster);

    Equal(125, read.Metadata.Classes.Count);
    Equal(15, read.Metadata.Types.Count);
    if (read.Metadata.Classes.Any(value => value.Name == "PIndexDataBlock"))
        throw new InvalidOperationException(
            "The CS1 runtime profile declares unsupported PIndexDataBlock.");
    Equal(64u, read.Metadata.Classes.Single(value =>
        value.Name == "PDataBlockD3D11").Size);
    Equal(112u, read.Metadata.Classes.Single(value =>
        value.Name == "PMeshInstance").Size);
    Equal(112u, read.Metadata.InstanceGroups.Single(value =>
        value.ClassName == "PMeshInstance").ObjectsSize);
}

static void WritesAuthoredModelFixups()
{
    var vertices = new[]
    {
        new PhyreVertexSource(
            new Vector3(-0.5f, 0f, 0f), Vector3.UnitZ,
            new[] { new PhyreTexCoordSet(Vector2.Zero, Vector3.UnitX, Vector3.UnitY) },
            Array.Empty<int>(), Array.Empty<float>()),
        new PhyreVertexSource(
            new Vector3(0.5f, 0f, 0f), Vector3.UnitZ,
            new[] { new PhyreTexCoordSet(Vector2.UnitX, Vector3.UnitX, Vector3.UnitY) },
            Array.Empty<int>(), Array.Empty<float>()),
        new PhyreVertexSource(
            new Vector3(0f, 1f, 0f), Vector3.UnitZ,
            new[] { new PhyreTexCoordSet(Vector2.UnitY, Vector3.UnitX, Vector3.UnitY) },
            Array.Empty<int>(), Array.Empty<float>()),
    };
    var model = new PhyreModelSource(
        "fixups",
        new[] { new PhyreMeshSource("mesh", vertices, new[] { 0, 1, 2 }) },
        Array.Empty<PhyreJointSource>());
    var material = PhyreMaterialTableReader.Minimal("shaders/ed8.fx#TEST");
    var cluster = PhyreClusterAssembler.Assemble(PhyreModelClusterWriter.Contents(
        model, new PhyreShaderBinding(material.ShaderAsset, material),
        PhyreModelGeometryPacker.Pack(model)));

    var sections = PhyreClusterSectionReader.Read(cluster);
    var metadata = sections.Metadata;
    Equal(
        metadata.InstanceGroups.Sum(group => group.Size),
        metadata.TotalDataSize);
    foreach (var group in metadata.InstanceGroups)
    {
        Equal(0u, group.Size % sizeof(uint));
        Equal(0u, group.ArraysSize % sizeof(uint));
    }
    Equal(0u, metadata.TotalDataSize % sizeof(uint));
    var fixups = new PhyreFixupReader().Read(cluster, metadata);

    int Group(string name) => metadata.InstanceGroups
        .Single(group => group.ClassName == name).Index;

    var nodeName = fixups.Arrays.Single(value =>
        value.SourceListIndex == Group("PNode"));
    Equal(1u, nodeName.SourceObjectId);
    Equal(0x8000004Cu, nodeName.SourceOffsetOrMember);

    foreach (var value in fixups.Arrays.Where(value =>
                 value.SourceListIndex == Group("PAssetReference")))
    {
        Equal(0x80000018u, value.SourceOffsetOrMember);
    }
    Equal(
        0x80000004u,
        fixups.Arrays.Single(value =>
            value.SourceListIndex == Group("PAssetReferenceImport"))
            .SourceOffsetOrMember);
    Equal(
        0x80000004u,
        fixups.Arrays.Single(value =>
            value.SourceListIndex == Group("PShaderParameterDefinition"))
            .SourceOffsetOrMember);

    var definitions = metadata.InstanceGroups.Single(value =>
        value.ClassName == "PShaderParameterDefinition");
    var definitionPointer = fixups.Pointers.Single(value =>
        value.SourceListIndex == Group("PParameterBuffer")
        && value.SourceOffsetOrMember == 0x8000000Cu);
    Equal((uint)definitions.Index, definitionPointer.DestinationListIndex);
    Equal(0u, definitionPointer.DestinationObjectId);

    foreach (var (className, expectedSource) in new[]
             {
                 ("PDataBlockD3D11", 0x8000000Cu),
                 ("PMesh", 0x80000004u),
                 ("PMeshInstance", 0x80000028u),
                 ("PMeshSegment", 0x80000018u),
             })
    {
        var group = Group(className);
        Equal(
            expectedSource,
            fixups.Pointers.First(value =>
                value.SourceListIndex == group
                && value.UserFixupId is null
                && (value.SourceOffsetOrMember & 0x80000000u) != 0)
                .SourceOffsetOrMember);
    }

    var gameMaterials = fixups.Arrays.Single(value =>
        value.SourceListIndex == Group("PMeshInstance"));
    Equal(0x80000064u, gameMaterials.SourceOffsetOrMember);
    Equal(1u, gameMaterials.Count);
    Equal(4u, metadata.InstanceGroups[Group("PMeshInstance")].ArraysSize);
}

/// <summary>
/// A parameter block is filled at the offsets the block itself states, and a value
/// that does not fit the parameter stops rather than being truncated into it.
/// </summary>
/// <summary>
/// A shipped model's material is pointed at another shader without the model being
/// rewritten — and a shader whose interface does not fit is refused instead.
/// </summary>
/// <summary>
/// A map's settings survive being written and read back, so reopening one brings
/// the form back rather than an empty one.
/// </summary>
/// <summary>
/// A shader's constants are filled from what they are called, by the engine's own
/// rule — and a name the rule does not recognise is left for the material.
/// </summary>
static void FeedsNativeShaderConstants()
{
    var world = Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateTranslation(3f, 4f, 5f);
    var view = Matrix4x4.CreateLookAt(new Vector3(0, 2, -10), Vector3.Zero, Vector3.UnitY);
    var projection = Matrix4x4.CreatePerspectiveFieldOfView(1f, 1.5f, 0.1f, 500f);
    var frame = new D3D11EffectFrame(
        world, view, projection,
        new Vector3(0, 2, -10),
        new Vector3(0, -1, 0),
        new Vector4(0.7f, 0.7f, 0.7f, 1f),
        new Vector4(0.4f, 0.4f, 0.4f, 1f),
        12.5f);

    static void SameMatrix(Matrix4x4 expected, float[]? actual)
    {
        if (actual is null) throw new InvalidOperationException("Nothing was supplied.");
        Equal(16, actual.Length);
        // Column-major, as a constant buffer holds a matrix the shader did not
        // declare row_major — which the game's shaders never do.
        var wanted = new[]
        {
            expected.M11, expected.M21, expected.M31, expected.M41,
            expected.M12, expected.M22, expected.M32, expected.M42,
            expected.M13, expected.M23, expected.M33, expected.M43,
            expected.M14, expected.M24, expected.M34, expected.M44,
        };
        for (var at = 0; at < 16; at++) Near(wanted[at], actual[at], 0.0001f);
    }

    SameMatrix(world, D3D11NativeEffect.EngineValue("World", frame));
    SameMatrix(world * view, D3D11NativeEffect.EngineValue("WorldView", frame));
    SameMatrix(
        world * view * projection,
        D3D11NativeEffect.EngineValue("WorldViewProjection", frame));
    SameMatrix(view * projection, D3D11NativeEffect.EngineValue("ViewProjection", frame));
    SameMatrix(projection, D3D11NativeEffect.EngineValue("Projection", frame));

    // The names the engine finds by searching rather than by an exact table.
    var lightDirection = D3D11NativeEffect.EngineValue("LightDirWS", frame);
    if (lightDirection is null) throw new InvalidOperationException("No light direction.");
    Near(-1f, lightDirection[1]);
    var lightColour = D3D11NativeEffect.EngineValue("LightColorIntensity", frame);
    if (lightColour is null) throw new InvalidOperationException("No light colour.");
    Near(0.7f, lightColour[0]);

    Near(12.5f, D3D11NativeEffect.EngineValue("Time", frame)![0]);
    Near(0.4f, D3D11NativeEffect.EngineValue("GlobalAmbientColor", frame)![0]);

    // A uniform the author invented is the material's to fill, not the engine's —
    // which is exactly what makes a shader with its own parameters usable.
    Equal(true, D3D11NativeEffect.EngineValue("MyOwnTint", frame) is null);
    Equal(true, D3D11NativeEffect.EngineValue("PhyreMaterialSwitches", frame) is null);
}

static void RemembersMapAuthoring()
{
    var directory = Path.Combine(Path.GetTempPath(), $"ed8-map-record-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var project = ModProject.Create(
            Path.Combine(directory, "mod.ed8proj"), directory, "record");
        Equal(0, MapAuthoringRecord.Authored(project).Count);
        Equal(true, MapAuthoringRecord.Load(project, "z9100") is null);

        var record = new MapAuthoringRecord(
            "z9100",
            "New Area",
            6,
            "O_S00SKY02",
            @"C:\models\arena.glb",
            new Dictionary<string, string> { ["3"] = "CK00", ["7"] = "CS01" },
            new Dictionary<string, MapShaderRecord>(StringComparer.Ordinal)
            {
                ["S_ground"] = new(
                    "ed8.fx#77F8C6B2524D1A0A4D01C2D0E8AE5B47",
                    null,
                    new Dictionary<string, string> { ["Tint"] = "1 0.5 0" }),
                ["S_glass"] = new(
                    "ed8.fx#MINE",
                    @"C:\shaders\glass.hlsl",
                    new Dictionary<string, string>()),
            });
        record.Save(project);

        Equal(1, MapAuthoringRecord.Authored(project).Count);
        Equal("z9100", MapAuthoringRecord.Authored(project)[0]);

        var read = MapAuthoringRecord.Load(project, "z9100")
            ?? throw new InvalidOperationException("The record did not come back.");
        Equal(record.DisplayName, read.DisplayName);
        Equal(record.PlaceKind, read.PlaceKind);
        Equal(record.Skybox, read.Skybox);
        Equal(record.ModelPath, read.ModelPath);
        Equal(2, read.CollisionNodes.Count);
        Equal("CK00", read.CollisionNodes["3"]);
        Equal("CS01", read.CollisionNodes["7"]);
        Equal(2, read.MaterialShaders.Count);
        // The author's own shader is remembered by where its source is, not by the
        // megabytes compiling it produced.
        Equal(@"C:\shaders\glass.hlsl", read.MaterialShaders["S_glass"].HlslPath!);
        Equal(true, read.MaterialShaders["S_ground"].HlslPath is null);
        Equal("1 0.5 0", read.MaterialShaders["S_ground"].Values["Tint"]);

        // Reading it by the name it was saved under is case-insensitive, since a
        // map is named z9100 in one place and Z9100 in another.
        Equal(true, MapAuthoringRecord.Load(project, "Z9100") is not null);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }
}

static void RepointsShippedMaterialShader()
{
    var assets = @"C:\Program Files (x86)\Steam\steamapps\common\Trails of Cold Steel\data\asset\D3D11";
    var packagePath = Path.Combine(assets, "C_EQU021.pkg");
    if (!File.Exists(packagePath)) return;

    var reader = new PkgArchiveReader();
    var package = reader.Read(packagePath);
    var cluster = package.ReadEntry(package.Entries.Single(value =>
        value.Name.EndsWith(".dae.phyre", StringComparison.OrdinalIgnoreCase))).ToArray();

    // Another variant of the same source, shipped elsewhere, whose material
    // interface is the one C_EQU021's block already fills.
    const string fitting = "shaders/ed8.fx#77F8C6B2524D1A0A4D01C2D0E8AE5B47";
    var holder = reader.Read(Path.Combine(assets, "C_EQU013.pkg"));
    var effect = holder.ReadEntry(holder.Entries.Single(value =>
        value.Name.Equals(
            "ed8.fx#77F8C6B2524D1A0A4D01C2D0E8AE5B47.phyre",
            StringComparison.OrdinalIgnoreCase))).ToArray();

    var before = PhyreMaterialTableReader.ReadAll(cluster);
    var plan = PhyreEffectRebind.Plan(cluster, "S_model", fitting, effect);
    Equal(0, plan.Problems.Count);
    Equal(before["S_model"].ShaderAsset, plan.Current);

    var written = PhyreEffectRebind.Repoint(cluster, "S_model", fitting, effect);
    // Nothing moved: a cluster addresses itself by offset, so the swap is a name
    // of the same length written where the old one was.
    Equal(cluster.Length, written.Length);
    var after = PhyreMaterialTableReader.ReadAll(written);
    Equal(fitting, after["S_model"].ShaderAsset);
    // Every material that shared the old name changed with it, and no other did.
    foreach (var (name, table) in after)
    {
        var was = before[name].ShaderAsset;
        Equal(was == plan.Current ? fitting : was, table.ShaderAsset);
    }

    // Its own shader is not a change.
    Equal(1, PhyreEffectRebind.Plan(cluster, "S_model", plan.Current, effect).Problems.Count);

    // A name of another length would move everything written after it, which is
    // exactly what a model that is not rewritten cannot survive.
    var longer = PhyreEffectRebind.Plan(cluster, "S_model", fitting + "X", effect);
    Equal(1, longer.Problems.Count);
    Throws<InvalidDataException>(() =>
        PhyreEffectRebind.Repoint(cluster, "S_model", fitting + "X", effect));

    // A material the model does not have is said so rather than guessed at.
    Equal(1, PhyreEffectRebind.Plan(cluster, "no_such_material", fitting, effect).Problems.Count);
}

static void SetsMaterialParameterValues()
{
    // Three slots, laid out as a shader's block lays them out: a colour of three
    // floats, an integer, and a texture whose image is named through an import.
    var names = Encoding.ASCII.GetBytes("Tint\0Switches\0DiffuseMap\0");
    var table = new PhyreMaterialTable(
        "shaders/test.fx#0",
        60,
        3,
        new byte[48],
        new[]
        {
            new PhyreMaterialChild("float", 0, 0, 3),
            new PhyreMaterialChild("PUInt32", 16, 0, 1),
            new PhyreMaterialChild("PShaderParameterCaptureBufferTexture2D", 32, 0, 1),
        },
        Array.Empty<ReadOnlyMemory<byte>>(),
        Array.Empty<ReadOnlyMemory<byte>>(),
        Array.Empty<PhyreMaterialPointer>(),
        names,
        new[]
        {
            new PhyreMaterialArray(0, 0, 0, 0),
            new PhyreMaterialArray(1, 0, 0, 5),
            new PhyreMaterialArray(2, 0, 0, 14),
        },
        new[] { new PhyreMaterialImport(null, 0x80000000u | 44, "map/images/old.dds") });

    Equal(3, PhyreMaterialValues.Parameters(table).Count);
    Equal("DiffuseMap", PhyreMaterialValues.Parameters(table)[2].Name);

    var filled = PhyreMaterialValues.WithValues(table, new Dictionary<string, string>
    {
        ["Tint"] = "0.25 0.5 1",
        ["Switches"] = "0xCD07EC00",
        ["DiffuseMap"] = "chr/images/mine.dds",
        // A parameter this block does not declare: the author typed it against
        // another shader, and it is no reason to refuse the ones that do fit.
        ["NotHere"] = "3",
    });

    var bytes = filled.ParameterBufferObject.Span;
    Near(0.25f, BitConverter.ToSingle(bytes[..4]));
    Near(0.5f, BitConverter.ToSingle(bytes[4..8]));
    Near(1f, BitConverter.ToSingle(bytes[8..12]));
    Equal(0xCD07EC00u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..20]));
    // The original is untouched: a block is replaced, never edited underneath a
    // caller that still holds it.
    Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(table.ParameterBufferObject.Span[16..20]));

    Equal(1, filled.Imports.Count);
    Equal("chr/images/mine.dds", filled.Imports[0].Asset);
    Equal(0x80000000u | 44, filled.Imports[0].Source);

    // Three numbers were declared; two are not "close enough".
    Throws<InvalidDataException>(() => PhyreMaterialValues.WithValues(
        table, new Dictionary<string, string> { ["Tint"] = "1 0" }));
    Throws<InvalidDataException>(() => PhyreMaterialValues.WithValues(
        table, new Dictionary<string, string> { ["Tint"] = "1 0 rouge" }));
}

/// <summary>
/// Each material gets the block of the shader it was assigned, the ones left alone
/// keep what they had, and every assigned effect travels with them.
/// </summary>
static void BindsAuthoredShadersPerMaterial()
{
    var packagePath =
        @"C:\Users\Administrator\Desktop\my-mod.files\original\data\asset\D3D11\O_T10LIG03.pkg";
    if (!File.Exists(packagePath)) return;
    var archive = new PkgArchiveReader().Read(packagePath);
    var entryName = "ed8.fx#D506953A7385090896B925A6E8DE8286.phyre";
    var effect = archive.ReadEntry(archive.Entries.Single(value =>
        value.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase))).ToArray();

    var kept = PhyreMaterialTableReader.Minimal("shaders/kept.fx#0");
    var assignment = AuthoredShaderBinding.For(
        "painted",
        "ed8.fx#D506953A7385090896B925A6E8DE8286",
        effect,
        new Dictionary<string, string>(),
        custom: false);
    Equal("shaders/ed8.fx#D506953A7385090896B925A6E8DE8286", assignment.ShaderAsset);
    Equal(entryName, assignment.EntryName);

    var (binding, shaders) = AuthoredShaderBinding.Build(
        new[] { "plain", "painted" },
        new Dictionary<string, ShaderAssignment> { ["painted"] = assignment },
        kept,
        "map/images/test-neutral.dds");

    Equal(2, binding.PerMaterial!.Count);
    // The unassigned slot keeps the block it had, not the one its neighbour got.
    Equal("shaders/kept.fx#0", binding.PerMaterial[0]!.ShaderAsset);
    Equal(assignment.ShaderAsset, binding.PerMaterial[1]!.ShaderAsset);
    Equal(624u, binding.PerMaterial[1]!.ParameterBufferSize);
    // The model names an assigned shader, and the package carries its file.
    Equal(assignment.ShaderAsset, binding.ShaderAsset);
    Equal(1, shaders.Count);
    Equal(entryName, shaders[0].Name);
    Equal(effect.Length, shaders[0].Data.Length);
}

static void AuthorsMaterialFromEffectAbi()
{
    var packagePath =
        @"C:\Users\Administrator\Desktop\my-mod.files\original\data\asset\D3D11\O_T10LIG03.pkg";
    if (!File.Exists(packagePath)) return;
    var archive = new PkgArchiveReader().Read(packagePath);
    var effectEntry = archive.Entries.Single(value =>
        value.Name.Equals(
            "ed8.fx#D506953A7385090896B925A6E8DE8286.phyre",
            StringComparison.OrdinalIgnoreCase));
    var effect = archive.ReadEntry(effectEntry);
    var material = PhyreMaterialTableReader.FromEffect(
        "shaders/ed8.fx#D506953A7385090896B925A6E8DE8286",
        effect,
        "map/images/test-neutral.dds");

    Equal(624u, material.ParameterBufferSize);
    Equal(55u, material.DefinitionCount);
    Equal(55, material.Children.Count);
    // A texture capture owns both a sampler pointer at +8 and the texture
    // import at +12. Together with the 25 standalone sampler captures this
    // gives the exact 26 sampler objects used by shipped D506 materials.
    Equal(26, material.SamplerStates.Count);
    Equal(55, material.ParameterDefinitions.Count);
    Equal(55, material.DefinitionArrays.Count);
    Equal(1, material.Imports.Count);
    Equal(0x8000001Cu, material.Imports[0].Source);
    Equal(1, material.Pointers.Count(value =>
        value.SourceOffset == 0x80000018u
        && value.TargetClass == "PSamplerState"));

    var model = new PhyreModelSource(
        "effect_abi",
        new[]
        {
            new PhyreMeshSource(
                "triangle",
                new[]
                {
                    new PhyreVertexSource(
                        Vector3.Zero, Vector3.UnitZ,
                        Array.Empty<PhyreTexCoordSet>(), Array.Empty<int>(), Array.Empty<float>()),
                    new PhyreVertexSource(
                        Vector3.UnitX, Vector3.UnitZ,
                        Array.Empty<PhyreTexCoordSet>(), Array.Empty<int>(), Array.Empty<float>()),
                    new PhyreVertexSource(
                        Vector3.UnitY, Vector3.UnitZ,
                        Array.Empty<PhyreTexCoordSet>(), Array.Empty<int>(), Array.Empty<float>()),
                },
                new[] { 0, 1, 2 }),
        },
        Array.Empty<PhyreJointSource>());
    var cluster = PhyreClusterAssembler.Assemble(
        PhyreModelClusterWriter.Contents(
            model,
            new PhyreShaderBinding(material.ShaderAsset, material),
            PhyreModelGeometryPacker.Pack(model)));
    var readBack = PhyreMaterialTableReader.Read(cluster);
    Equal(material.ParameterBufferSize, readBack.ParameterBufferSize);
    Equal(material.DefinitionCount, readBack.DefinitionCount);
}

static void CreatesEffectFromScratch()
{
    var effect = EffAuthoring.CreateEffect("brand_new");
    var child = EffAuthoring.AddNewSegment(effect, effect.Version, 0, "spark");
    effect.Segments[0].TextureName = "I_EFTEX900";
    effect.Textures.Add("I_EFTEX900");

    var written = EffFileWriter.Write(effect);
    var reopened = EffFileReader.Read(written);
    if (reopened.EffectName != "brand_new" || reopened.Segments.Count != 2)
        throw new Exception("The new effect did not read back.");
    if (reopened.Segments[0].TextureName != "I_EFTEX900" || reopened.Textures is not ["I_EFTEX900"])
        throw new Exception("The texture the segment draws with was lost.");
    if (reopened.Segments[1].Name != "spark")
        throw new Exception($"The added segment lost its name: got '{reopened.Segments[1].Name}' "
            + $"(root='{reopened.Segments[0].Name}', {reopened.Segments.Count} segments).");
    if (EffAuthoring.TargetOf(reopened.Segments[0].Children[0]) != 1)
        throw new Exception("The root does not spawn the segment it was given.");
    if (reopened.Segments[0].Scale is not [{ } scale] || Math.Abs(scale.Floats[0] - 1f) > 1e-6f)
        throw new Exception("A new segment must keep its own size.");
    if (reopened.Segments[0].Data17PcRaw.Length != 16 || reopened.Segments[0].StructFlags != 3)
        throw new Exception("The blocks the PC layout always carries were not written.");

    // Writing what was just read must not drift.
    if (!EffFileWriter.Write(reopened).AsSpan().SequenceEqual(written))
        throw new Exception("The new effect did not round-trip byte-exactly.");

    // It has to play, too: the root is drawn and its child is spawned after it.
    var frame = EffSimulation.Evaluate(reopened, 0.5f);
    if (frame.Nodes.Count != 2 || !frame.Nodes[0].Drawn || !frame.Nodes[0].Billboard)
        throw new Exception("A new effect does not play as a drawn, camera-facing segment.");
}

// A segment's place in the tree lives in the spawn descriptors of whoever fires
// it, and those name their target by its index in the file. So every structural
// edit has to keep those indices straight — above all when a segment is removed
// and everything after it moves up.
static void EditsEffectSegments()
{
    var effect = EffFileReader.Read(BuildColdSteelEffect());
    var root = 0;
    var child = EffAuthoring.AddSegment(effect, root, root, "child");
    var grandChild = EffAuthoring.AddSegment(effect, root, child, "grand-child");
    if (effect.Segments.Count != 3)
        throw new Exception("The segments were not added.");
    if (EffAuthoring.Roots(effect) is not [0])
        throw new Exception("A segment that is spawned is not a root.");
    if (!EffAuthoring.Descendants(effect, root).OrderBy(value => value)
            .SequenceEqual(new[] { root, child, grandChild }.OrderBy(value => value)))
        throw new Exception("The spawn chain was not built.");
    if (effect.Segments[child].Children.Count != 1
        || EffAuthoring.TargetOf(effect.Segments[child].Children[0]) != grandChild)
    {
        throw new Exception("The child does not spawn the grand-child.");
    }
    if (effect.Segments[child].Name != "child")
        throw new Exception("The new segment did not take its name.");

    // A copy must share nothing with what it was copied from.
    effect.Segments[child].Position[0].Time = 9f;
    if (Math.Abs(effect.Segments[root].Position[0].Time - 9f) < 1e-6f)
        throw new Exception("The copy shares its keyframes with the original.");

    // Moving the grand-child under the root leaves the child with nothing.
    EffAuthoring.Reparent(effect, grandChild, root);
    if (effect.Segments[child].Children.Count != 0
        || effect.Segments[root].Children.Count != 2)
    {
        throw new Exception("The segment was not moved.");
    }
    try
    {
        EffAuthoring.Reparent(effect, root, grandChild);
        throw new Exception("A segment was allowed under one of its own children.");
    }
    catch (InvalidOperationException)
    {
    }

    // Removing the middle segment renumbers what the descriptors point at.
    EffAuthoring.RemoveSegment(effect, child);
    if (effect.Segments.Count != 2) throw new Exception("The segment was not removed.");
    var remaining = effect.Segments.FindIndex(value => value.Name == "grand-child");
    if (remaining < 0) throw new Exception("Removing a segment took another one with it.");
    if (effect.Segments[0].Children.Count != 1
        || EffAuthoring.TargetOf(effect.Segments[0].Children[0]) != remaining)
    {
        throw new Exception("The spawn descriptors were not renumbered.");
    }

    // What was edited has to survive being written and read back.
    var reopened = EffFileReader.Read(EffFileWriter.Write(effect));
    if (reopened.Segments.Count != 2
        || reopened.Segments[remaining].Name != "grand-child"
        || EffAuthoring.TargetOf(reopened.Segments[0].Children[0]) != remaining)
    {
        throw new Exception("The edited effect did not survive a round-trip.");
    }
}

// The keyframe modes reversed from the engine: additive values chain onto the
// previous keyframe, uniform ones broadcast a single component, random ones roll
// between the two bounds, and bits 4/5 loop a region of the track.
static void EvaluatesEffectTracks()
{
    static EffKeyframe Keyframe(float time, ushort flags, float[] value, float[] bound)
        => new()
        {
            Floats = new[]
            {
                value[0], value[1], value[2], value[3],
                bound[0], bound[1], bound[2], bound[3],
                time,
            },
            Ints = new uint[] { flags, 0 },
        };
    static void Near(float actual, float expected, float tolerance, string what)
    {
        if (Math.Abs(actual - expected) > tolerance)
            throw new Exception($"{what}: expected {expected}, got {actual}.");
    }

    var ones = new[] { 1f, 1f, 1f, 1f };
    // Scale of a real segment: 1.0 uniform, then +0.4 and -0.1 additive.
    var scale = new List<EffKeyframe>
    {
        Keyframe(0f, 0x02, new[] { 1f, 1f, 1f, 1f }, ones),
        Keyframe(0.2f, 0x03, new[] { 0.4f, 1f, 1f, 1f }, ones),
        Keyframe(0.6f, 0x03, new[] { -0.1f, 1f, 1f, 1f }, ones),
    };
    float Scale(float time) => EffTrackEvaluator.Evaluate(scale, time, ones, 0)[0];
    Near(Scale(0f), 1f, 1e-5f, "scale at rest");
    Near(Scale(0.1f), 1.2f, 1e-5f, "scale rising toward 1.0 + 0.4");
    Near(Scale(0.2f), 1.4f, 1e-5f, "scale at the additive keyframe");
    Near(Scale(0.4f), 1.35f, 1e-5f, "scale falling toward 1.4 - 0.1");
    Near(Scale(2f), 1.3f, 1e-5f, "scale held after the last keyframe");

    var zeros = new[] { 0f, 0f, 0f, 0f };
    var absolute = new List<EffKeyframe>
    {
        Keyframe(0f, 0x00, zeros, zeros),
        Keyframe(1f, 0x10, new[] { 5f, 0f, 0f, 0f }, zeros),
        Keyframe(2f, 0x20, new[] { 9f, 0f, 0f, 0f }, zeros),
    };
    float Absolute(float time) => EffTrackEvaluator.Evaluate(absolute, time, zeros, 0)[0];
    Near(Absolute(0.5f), 2.5f, 1e-5f, "absolute loop before wrapping");
    Near(Absolute(2.5f), 7f, 1e-4f, "absolute loop wrapped to 1.5");
    Near(Absolute(10.7f), 7.8f, 1e-3f, "absolute loop wrapped to 1.7");

    var accumulating = new List<EffKeyframe>
    {
        Keyframe(0f, 0x00, zeros, zeros),
        Keyframe(1f, 0x11, new[] { 1f, 0f, 0f, 0f }, zeros),
        Keyframe(2f, 0x21, new[] { 2f, 0f, 0f, 0f }, zeros),
    };
    float Accumulating(float time) => EffTrackEvaluator.Evaluate(accumulating, time, zeros, 0)[0];
    Near(Accumulating(1.5f), 2f, 1e-5f, "additive loop, first pass");
    Near(Accumulating(2.5f), 5f, 1e-5f, "additive loop, second pass");
    Near(Accumulating(3.5f), 8f, 1e-4f, "additive loop, third pass");

    // A random keyframe stays inside its bounds and rolls once per instance.
    var random = new List<EffKeyframe>
    {
        Keyframe(0f, 0x04, new[] { 0f, 0f, 30f, 0f }, new[] { 0f, 0f, -30f, 0f }),
    };
    float? previousRoll = null;
    var distinct = false;
    foreach (var seed in new uint[] { 1, 42, 999, 123456 })
    {
        var value = EffTrackEvaluator.Evaluate(random, 0f, zeros, seed)[2];
        if (value < -30f - 1e-3f || value > 30f + 1e-3f)
            throw new Exception($"A random keyframe left its bounds: {value}.");
        if (EffTrackEvaluator.Evaluate(random, 0f, zeros, seed)[2] != value)
            throw new Exception("A random keyframe rolled twice for the same instance.");
        if (previousRoll is { } roll && Math.Abs(value - roll) > 1e-3f) distinct = true;
        previousRoll = value;
    }
    if (!distinct) throw new Exception("Different instances rolled the same value.");

    // Uniform and random together: one roll, broadcast to x, y and z.
    var uniform = new List<EffKeyframe>
    {
        Keyframe(0f, 0x06, new[] { 1f, 5f, 9f, 0f }, new[] { 2f, 6f, 10f, 0f }),
    };
    var broadcast = EffTrackEvaluator.Evaluate(uniform, 0f, zeros, 7);
    if (broadcast[0] < 1f || broadcast[0] > 2f)
        throw new Exception("The uniform roll ignored its bounds.");
    if (broadcast[0] != broadcast[1] || broadcast[0] != broadcast[2])
        throw new Exception("The uniform roll was not broadcast to x, y and z.");
}

static void TracksModProjectFiles()
{
    var root = Path.Combine(Path.GetTempPath(), $"ed8mod-{Guid.NewGuid():N}");
    var game = Path.Combine(root, "game");
    var scripts = Path.Combine(game, "data", "scripts", "scena", "dat_us");
    Directory.CreateDirectory(scripts);
    var edited = Path.Combine(scripts, "t1000.dat");
    var added = Path.Combine(scripts, "brand_new.dat");
    File.WriteAllText(edited, "original");
    try
    {
        var project = ED8Editor.Application.ModProject.Create(
            Path.Combine(root, "my-mod.ed8mod"), game);

        // An edit of a shipped file keeps a pristine copy.
        project.CaptureOriginal(edited);
        File.WriteAllText(edited, "modded");
        project.TrackSave(edited);
        // A file the game never had is tracked with no original.
        File.WriteAllText(added, "new content");
        project.Include(added);

        if (project.Files.Count != 2) throw new Exception("The project did not track both files.");
        var relative = project.Files.Select(value => value.RelativePath).ToArray();
        if (!relative.Contains("data/scripts/scena/dat_us/t1000.dat"))
            throw new Exception("The tracked path is not game-relative with forward slashes.");

        var archive = Path.Combine(root, "ship.zip");
        if (project.ExportArchive(archive) != 2) throw new Exception("The archive is missing files.");
        using (var zip = System.IO.Compression.ZipFile.OpenRead(archive))
        {
            if (zip.GetEntry("data/scripts/scena/dat_us/t1000.dat") is null)
                throw new Exception("The archive does not keep the game paths at its root.");
        }

        project.RestoreOriginals();
        if (File.ReadAllText(edited) != "original")
            throw new Exception("Restoring did not put the shipped file back.");
        if (File.Exists(added))
            throw new Exception("Restoring did not remove a file the game never had.");

        project.ApplyMod();
        if (File.ReadAllText(edited) != "modded" || File.ReadAllText(added) != "new content")
            throw new Exception("Re-applying the mod did not write its files back.");

        // The project survives a reload.
        var reopened = ED8Editor.Application.ModProject.Open(Path.Combine(root, "my-mod.ed8mod"));
        if (reopened.Files.Count != 2 || !reopened.Files.All(value => value.HasModCopy))
            throw new Exception("The reopened project lost its file list.");
        reopened.Remove("data/scripts/scena/dat_us/t1000.dat");
        var afterRemoval = ED8Editor.Application.ModProject.Open(
            Path.Combine(root, "my-mod.ed8mod"));
        if (afterRemoval.Files.Count != 1
            || afterRemoval.Files.Single().RelativePath
                != "data/scripts/scena/dat_us/brand_new.dat")
        {
            throw new Exception("Removing one mod file removed another project entry.");
        }
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static void RoundTripsCs1Table()
{
    using var source = new MemoryStream();
    source.Write(new byte[] { 2, 0 });
    source.Write(Encoding.UTF8.GetBytes("sample\0"));
    source.Write(new byte[] { 3, 0, 1, 2, 3 });
    source.Write(Encoding.UTF8.GetBytes("other\0"));
    source.Write(new byte[] { 2, 0, 4, 5 });
    var original = source.ToArray();
    source.Position = 0;
    var table = Cs1TableDocument.Read(source);
    using var output = new MemoryStream();
    table.Write(output);
    if (!original.AsSpan().SequenceEqual(output.ToArray()))
        throw new Exception("The TBL did not round-trip byte-exactly.");
}

static void ResolvesSemanticTableReferences()
{
    if (!Cs1TableReference.TryParse("t_item:item", out var reference) || reference is null)
        throw new Exception("A valid semantic TBL reference was rejected.");
    if (reference.TableName != "t_item.tbl" || reference.Category != "item")
        throw new Exception("The semantic TBL reference was parsed incorrectly.");
    if (Cs1TableReference.TryParse("t_item", out _))
        throw new Exception("A semantic TBL reference without category was accepted.");
}

static void PreservesQuestTextStaleLength()
{
    using var source = new MemoryStream();
    source.Write(new byte[] { 1, 0 });
    source.Write(Encoding.UTF8.GetBytes("QSText\0"));
    source.Write(new byte[] { 0xff, 0x7f }); // deliberately stale serialized length
    source.Write(new byte[] { 0x34, 0x12, 0x02 });
    source.Write(Encoding.UTF8.GetBytes("Quest text\0"));
    source.WriteByte(1);
    var original = source.ToArray();
    source.Position = 0;
    var table = Cs1TableDocument.Read(source);
    using var output = new MemoryStream();
    table.Write(output);
    if (!original.AsSpan().SequenceEqual(output.ToArray()))
        throw new Exception("The stale QSText length was not preserved during an unchanged round-trip.");
}

static void BuildsSemanticTableChoices()
{
    var directory = Path.Combine(Path.GetTempPath(), "ed8editor-tbl-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        using var payload = new MemoryStream();
        payload.Write(new byte[] { 0x34, 0x12, 0, 0 });
        payload.Write(Encoding.UTF8.GetBytes("flags\0"));
        payload.Write(new byte[46]);
        payload.Write(Encoding.UTF8.GetBytes("Test item\0Description\0"));
        var table = new Cs1TableDocumentBuilder().WithEntry("item", payload.ToArray()).Build();
        table.Write(Path.Combine(directory, "t_item.tbl"));
        var choices = new Cs1TableCatalog(directory).GetChoices(new Cs1TableReference("t_item.tbl", "item"));
        if (choices.Count != 1 || choices[0].Value != 0x1234 || !choices[0].Label.Contains("Test item"))
            throw new Exception("The semantic item selector did not expose the documented key and label.");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void FlattensTblSchemaFields()
{
    var schemas = Cs1TableSchemaSet.Default;
    if (schemas.Entries.Count != 51)
        throw new Exception($"Expected 51 CS1 entry schemas, found {schemas.Entries.Count}.");
    var fields = schemas.FindAtomicFields("item")
        ?? throw new Exception("The item schema was not loaded.");
    var names = fields.Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
    foreach (var expected in new[] { "effects[1] id", "effects[2] data[2]", "name" })
    {
        if (!names.Contains(expected))
            throw new Exception($"The flattened item schema is missing '{expected}'.");
    }
}

static void EditsTypedTblFields()
{
    using var payload = new MemoryStream();
    payload.Write(new byte[] { 0x34, 0x12 });
    payload.Write(Encoding.UTF8.GetBytes("Original text\0"));
    var codec = new Cs1TableRecordCodec();
    var entry = new Cs1TableEntry("TextTableData", payload.ToArray());
    var values = codec.Decode(entry)?.ToArray()
        ?? throw new Exception("The TextTableData schema was not loaded.");
    values[1] = values[1] with { Value = "Edited text" };
    var edited = new Cs1TableEntry(entry.Category, codec.Encode(entry.Category, values));
    var result = codec.Decode(edited)?.ToArray()
        ?? throw new Exception("The edited TextTableData could not be decoded.");
    if (result[0].Value != "4660" || result[1].Value != "Edited text")
        throw new Exception("Typed editing changed the ID or failed to update the text.");
}

static void SelectsPhyreShaderPermutationContexts()
{
    var stage = new CpuShaderStageProgram(new byte[] { 1 }, 16, 0);
    var noLight = new CpuShaderPermutation(stage, stage, Array.Empty<CpuShaderInput>(),
        new CpuShaderContext(0, new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["NUM_LIGHTS"] = 0,
            ["INSTANCING_ENABLED"] = 0,
            ["SHADER_LOD_LEVEL"] = 0,
        }));
    var lit = noLight with
    {
        Context = new CpuShaderContext(1, new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["NUM_LIGHTS"] = 0x11,
            ["INSTANCING_ENABLED"] = 0,
            ["SHADER_LOD_LEVEL"] = 0,
        }),
    };
    var program = new CpuEffectProgram(
        new Dictionary<string, CpuSceneRenderPassProgram>(StringComparer.Ordinal)
        {
            ["Opaque"] = new CpuSceneRenderPassProgram("Opaque", new[] { noLight, lit }),
        },
        new[] { "NUM_LIGHTS", "INSTANCING_ENABLED", "SHADER_LOD_LEVEL" },
        new[] { noLight.Context!, lit.Context! });
    var material = new CpuMaterial(
        "test", Vector4.One, null,
        new Dictionary<string, float[]>(), new Dictionary<string, string>(), new Dictionary<string, int>(),
        ResolvedRenderPassName: "Opaque", EffectProgram: program);
    var selector = new D3D11ShaderPermutationSelector();
    var selected = selector.Select(material, D3D11ShaderContextPolicy.ViewerWithoutDynamicLights);
    Equal(true, selected.IsSupported);
    Equal(true, ReferenceEquals(noLight, selected.Permutation));

    var unknownProgram = program with { ContextSwitches = program.ContextSwitches!.Append("UNREGISTERED_CONTEXT").ToArray() };
    var unsupported = selector.Select(material with { EffectProgram = unknownProgram }, D3D11ShaderContextPolicy.ViewerWithoutDynamicLights);
    Equal(false, unsupported.IsSupported);
    Equal(true, unsupported.UnsupportedReason!.Contains("UNREGISTERED_CONTEXT", StringComparison.Ordinal));
}

static void ReadsValidHeader()
{
    using var stream = CreateHeader("a0000");
    var result = new ScriptHeaderReader().Read(stream, "a0000.dat", ScriptKind.Scenario);

    Equal("a0000", result.Identifier);
    Equal(ScriptKind.Scenario, result.Kind);
    Equal(ScriptTargetKind.Map, result.TargetKind);
    Equal(0x20u, result.IdentifierOffset);
}

static void RejectsInvalidMarker()
{
    using var stream = CreateHeader("a0000", 0xDEADBEEF);
    Throws<InvalidScriptHeaderException>(() => new ScriptHeaderReader().Read(stream, "bad.dat"));
}

static void ReadsRelocatedIdentifier()
{
    using var stream = CreateHeader("t0600", identifierOffset: 0x136);
    var result = new ScriptHeaderReader().Read(stream, "t0600.dat", ScriptKind.Scenario);

    Equal("t0600", result.Identifier);
    Equal(0x136u, result.IdentifierOffset);
}

static void RejectsUnterminatedIdentifier()
{
    using var stream = CreateHeader("a0000", terminateIdentifier: false);
    Throws<InvalidScriptHeaderException>(() => new ScriptHeaderReader().Read(stream, "bad.dat"));
}

static void ReadsOpsProps()
{
    const string xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Ops version="1">
          <MapSetting>
            <MapColor>
              <Type type="default" />
              <Fog near="2" far="800" color="0.1, 0.2, 0.3" />
            </MapColor>
          </MapSetting>
          <MapObjects>
            <AssetObject asset="O_TEST" name="chair" flag="0x283" custom="kept"
              pos="2.5, 1, -3" rot="0, 0, 0" scl="1, 2, 1"
              materialDiffuse="1, 0.5, 0.25, 1" materialEmission="0, 0.1, 0" />
          </MapObjects>
          <Entrys>
            <EntryBox name="event_box" next="a0100" entry="from_test"
              pos="4, 5, 6,  0, 0, 0,  2, 3, 4" />
          </Entrys>
          <GroupBoxes>
            <GroupBox name="group" pos="1, 2, 3,  0, 0, 0,  4, 5, 6" />
          </GroupBoxes>
          <LookPoints>
            <LookPoint name="look" pos="7, 8, 9" radius="2.5" />
          </LookPoints>
          <MapCameras>
            <MapCamera no="12" eye="10, 11, 12" lookat="13, 14, 15" />
          </MapCameras>
          <MapSounds>
            <SoundObject seName="wind" seGroupId="7" seType="POINT" seVolume="0.65" seRange="12.5"
              sePosition="16, 17, 18" seRotation="0" seScale="1, 1, 1" />
          </MapSounds>
          <Lights>
            <Light group="0" type="1" pos="19, 20, 21" color="1, 0.5, 0.25, 1"
              colorPower="2" innerRange="3" outerRange="8" />
          </Lights>
        </Ops>
        """;
    var path = WriteTemporaryOps(xml);
    try
    {
        var scene = new OpsReader().Read(path);
        Equal(1, scene.Props.Count);
        Equal("default", scene.DefaultEnvironment!.ProfileName);
        Near(0.1f, scene.DefaultEnvironment.FogColor.X);
        Near(2f, scene.DefaultEnvironment.FogNearDistance);
        Near(800f, scene.DefaultEnvironment.FogFarDistance);
        var prop = scene.Props[0];
        Equal("O_TEST", prop.AssetId);
        Equal("chair", prop.Name);
        Equal(0x283u, prop.Flags!.Value);
        Equal("kept", prop.SourceAttributes["custom"]);
        Near(2.5f, prop.Transform.Position.X);
        Near(1f, MathF.Abs(Quaternion.Dot(Quaternion.Identity, prop.Transform.Rotation)));
        Equal(2, scene.Volumes.Count);
        var entry = scene.Volumes.Single(value => value.Kind == MapVolumeKind.Entry);
        Equal("event_box", entry.Name);
        Equal("a0100", entry.DestinationMap!);
        Near(4f, entry.Transform.Position.X);
        Near(3f, entry.Transform.Scale.Y);
        Near(1f, entry.Transform.Rotation.W);
        Equal(1, scene.Points.Count);
        Near(7f, scene.Points[0].Position.X);
        Near(2.5f, scene.Points[0].Radius!.Value);
        Equal(1, scene.Cameras.Count);
        Near(10f, scene.Cameras[0].Eye.X);
        Near(13f, scene.Cameras[0].LookAt.X);
        Equal(1, scene.Sounds.Count);
        Equal(MapSoundKind.Point, scene.Sounds[0].Kind);
        Equal(7, scene.Sounds[0].GroupId);
        Near(0.65f, scene.Sounds[0].Volume);
        Near(16f, scene.Sounds[0].Position.X);
        Near(12.5f, scene.Sounds[0].Range);
        Equal(1, scene.Lights.Count);
        Near(19f, scene.Lights[0].Position.X);
        Near(8f, scene.Lights[0].OuterRange);
        Equal(xml.Length, scene.OriginalBytes.Count);
    }
    finally
    {
        File.Delete(path);
    }
}

static void CreatesSceneInstancesAtOpsTransforms()
{
    var transform = new MapTransform(
        new Vector3(3, 4, 5),
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f),
        new Vector3(2, 3, 4),
        Vector3.Zero,
        Vector3.Zero);
    var prop = new MapProp(
        17,
        "O_TEST",
        "placed",
        transform,
        null,
        Vector4.One,
        Vector3.Zero,
        new Dictionary<string, string>());
    var map = new MapScene(
        "test.ops",
        new[] { prop },
        Array.Empty<byte>(),
        Array.Empty<MapVolume>(),
        Array.Empty<MapPoint>(),
        Array.Empty<MapCameraMarker>(),
        Array.Empty<MapSoundMarker>(),
        Array.Empty<MapLightMarker>());
    var model = CreateTriangleModel("O_TEST", "Float32x3");
    var header = new ScriptHeader("test.dat", "test", ScriptKind.Scenario, ScriptTargetKind.Map, 0, 0, Array.Empty<byte>());
    var session = new EditorSession(
        new ScriptOpenResult(header, null, "test.ops"),
        map,
        new Dictionary<string, AssetResolution>(),
        new Dictionary<string, AssetManifestLoad>(),
        new Dictionary<string, AssetModelLoad>
        {
            ["O_TEST"] = new("O_TEST", AssetModelLoadStatus.Loaded, model, null),
        });
    var instance = new EditorSceneFactory().Create(session).Single();
    Equal(17, instance.Id);
    var origin = Vector3.Transform(Vector3.Zero, instance.Transform);
    Near(3f, origin.X);
    Near(4f, origin.Y);
    Near(5f, origin.Z);
    var scaledX = Vector3.TransformNormal(Vector3.UnitX, instance.Transform);
    Near(2f, scaledX.Length());
}

static void KeepsOpsTransformsInPhyreSceneBasis()
{
    const string xml = "<Ops><MapObjects><AssetObject asset=\"O_LAMP\" name=\"lamp\" pos=\"2,3,4\" rot=\"0,1.570796,0\" scl=\"1,1,1\" /></MapObjects></Ops>";
    var path = WriteTemporaryOps(xml);
    try
    {
        var prop = new OpsReader().Read(path).Props.Single();
        Near(2f, prop.Transform.Position.X);
        Near(3f, prop.Transform.Position.Y);
        Near(4f, prop.Transform.Position.Z);
        var transformedUp = Vector3.Transform(Vector3.UnitY, prop.Transform.Rotation);
        Near(0f, transformedUp.X);
        Near(1f, transformedUp.Y);
        Near(0f, transformedUp.Z);
        var transformedForward = Vector3.Transform(Vector3.UnitZ, prop.Transform.Rotation);
        Near(1f, transformedForward.X);
        Near(0f, transformedForward.Y);
        Near(0f, transformedForward.Z);
    }
    finally
    {
        File.Delete(path);
    }
}

static void RejectsMalformedOpsVector()
{
    const string xml = "<Ops><MapObjects><AssetObject asset=\"O_TEST\" name=\"bad\" pos=\"1, 2\" rot=\"0, 0, 0\" scl=\"1, 1, 1\" /></MapObjects></Ops>";
    var path = WriteTemporaryOps(xml);
    try
    {
        Throws<InvalidOpsException>(() => new OpsReader().Read(path));
    }
    finally
    {
        File.Delete(path);
    }
}

static void ResolvesLocalizedAsset()
{
    var root = CreateAssetTree(
        Path.Combine("D3D11", "O_TEST.pkg"),
        Path.Combine("D3D11_us", "O_TEST.pkg"));
    try
    {
        var resolution = new GameAssetResolver(root)
            .Resolve("o_test", AssetVariantPreference.English);
        Equal(AssetResolutionStatus.Resolved, resolution.Status);
        Equal(AssetVariant.English, resolution.SelectedPackage!.Variant);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void FallsBackToBaseAsset()
{
    var root = CreateAssetTree(Path.Combine("D3D11", "M_TEST.pkg"));
    try
    {
        var resolution = new GameAssetResolver(root)
            .Resolve("M_TEST", AssetVariantPreference.English);
        Equal(AssetResolutionStatus.Resolved, resolution.Status);
        Equal(AssetVariant.Base, resolution.SelectedPackage!.Variant);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void ReportsMissingAndAmbiguousAssets()
{
    var root = CreateAssetTree(
        Path.Combine("D3D11", "first", "O_DUP.pkg"),
        Path.Combine("D3D11", "second", "O_DUP.pkg"));
    try
    {
        var resolver = new GameAssetResolver(root);
        Equal(AssetResolutionStatus.Missing, resolver.Resolve("NOPE", AssetVariantPreference.Base).Status);
        var duplicate = resolver.Resolve("O_DUP", AssetVariantPreference.Base);
        Equal(AssetResolutionStatus.Ambiguous, duplicate.Status);
        Equal(2, duplicate.Candidates.Count);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void ReadsUncompressedPackageEntry()
{
    var path = WriteTemporaryPackage("hello.txt", Encoding.ASCII.GetBytes("hello"), 5, PackageCompressionType.None);
    try
    {
        var archive = new PkgArchiveReader().Read(path);
        Equal(1, archive.Entries.Count);
        Equal("hello.txt", archive.Entries[0].Name);
        Equal("hello", Encoding.ASCII.GetString(archive.ReadEntry("HELLO.TXT")));
    }
    finally
    {
        File.Delete(path);
    }
}

static void DecompressesPackageEntry()
{
    // ABC followed by a non-overlapping three-byte look-behind.
    var compressed = new byte[18];
    BinaryPrimitives.WriteUInt32LittleEndian(compressed.AsSpan(0, 4), 6);
    BinaryPrimitives.WriteUInt32LittleEndian(compressed.AsSpan(4, 4), 18);
    BinaryPrimitives.WriteUInt32LittleEndian(compressed.AsSpan(8, 4), 0xff);
    compressed[12] = (byte)'A';
    compressed[13] = (byte)'B';
    compressed[14] = (byte)'C';
    compressed[15] = 0xff;
    compressed[16] = 3;
    compressed[17] = 3;

    var path = WriteTemporaryPackage("repeat.bin", compressed, 6, PackageCompressionType.NisLzss);
    try
    {
        var archive = new PkgArchiveReader().Read(path);
        Equal("ABCABC", Encoding.ASCII.GetString(archive.ReadEntry(archive.Entries[0])));
    }
    finally
    {
        File.Delete(path);
    }
}

static void RejectsTruncatedPackageEntry()
{
    var path = WriteTemporaryPackage("bad.bin", new byte[] { 1, 2, 3 }, 10, PackageCompressionType.NisLzss);
    try
    {
        var archive = new PkgArchiveReader().Read(path);
        Throws<InvalidPackageException>(() => archive.ReadEntry("bad.bin"));
    }
    finally
    {
        File.Delete(path);
    }
}

static void SelectsManifestSymbol()
{
    const string xml = """
        <fassets>
          <asset symbol="M_TEST"><cluster path="data/D3D11/map/test.dae.phyre" type="p_collada" custom="kept" /></asset>
          <asset symbol="M_TEST_MI"><cluster path="data/D3D11/map/test_mi.dae.phyre" type="p_collada" /></asset>
        </fassets>
        """;
    var path = WriteTemporaryPackage(
        AssetManifestReader.ManifestEntryName,
        Encoding.UTF8.GetBytes(xml),
        checked((uint)Encoding.UTF8.GetByteCount(xml)),
        PackageCompressionType.None);
    try
    {
        var archive = new PkgArchiveReader().Read(path);
        var manifest = new AssetManifestReader().Read(archive, "m_test");
        Equal("M_TEST", manifest.PrimaryAsset!.Symbol);
        Equal(false, manifest.UsedSingleAssetFallback);
        var resource = manifest.PrimaryAsset.Resources[0];
        Equal(AssetResourceKind.Model, resource.Kind);
        Equal("test.dae.phyre", resource.ArchiveEntryName);
        Equal("kept", resource.SourceAttributes["custom"]);
        Equal(false, resource.IsEmbedded);
    }
    finally
    {
        File.Delete(path);
    }
}

static void UsesManifestFallback()
{
    const string xml = "<fassets><asset symbol=\"ACTUAL\"><cluster path=\"x.dds.phyre\" type=\"p_texture\" /></asset></fassets>";
    var path = WriteTemporaryPackage(
        AssetManifestReader.ManifestEntryName,
        Encoding.UTF8.GetBytes(xml),
        checked((uint)Encoding.UTF8.GetByteCount(xml)),
        PackageCompressionType.None);
    try
    {
        var manifest = new AssetManifestReader().Read(new PkgArchiveReader().Read(path), "EXPECTED");
        Equal("ACTUAL", manifest.PrimaryAsset!.Symbol);
        Equal(true, manifest.UsedSingleAssetFallback);
    }
    finally
    {
        File.Delete(path);
    }
}

static void RejectsUnknownPhyreMarker()
{
    Throws<InvalidPhyreException>(() => new PhyreClusterMetadataReader().Read(new byte[68]));
}

static void ReadsPhyrePackedClassMembers()
{
    const int headerSize = 68;
    var strings = Encoding.ASCII.GetBytes("PUInt32\0PMesh\0m_count\0");
    var bytes = new byte[headerSize + 32 + 4 + 36 + 24 + strings.Length];
    var offset = 0;

    WriteUInt32(PhyreClusterMetadataReader.LittleEndianMarker);
    WriteUInt32(headerSize);
    WriteUInt32((uint)(bytes.Length - headerSize));
    WriteUInt32(0x44583331); // Synthetic platform ID.
    WriteUInt32(0); // No instance lists.
    for (var index = 0; index < 9; index++) WriteUInt32(0);
    WriteUInt32(0); // No object data.
    WriteUInt32(0);
    WriteUInt32(0);

    WriteUInt32((uint)(bytes.Length - headerSize));
    WriteUInt32(0);
    WriteUInt32(1); // Types.
    WriteUInt32(1); // Classes.
    WriteUInt32(1); // Members.
    WriteUInt32((uint)strings.Length);
    WriteUInt32(0);
    WriteUInt32(0);
    WriteUInt32(0); // PUInt32 string offset.

    WriteUInt32(0); // No superclass.
    WriteUInt32(0x20000010); // 16-byte class, 4-byte alignment.
    WriteUInt32(8); // PMesh string offset.
    WriteUInt32(1);
    WriteUInt32(0);
    WriteUInt32(0);
    WriteUInt32(0);
    WriteUInt32(0x42);
    WriteUInt32(0);

    WriteUInt32(14); // m_count string offset.
    WriteUInt32(0); // PUInt32.
    WriteUInt32(4);
    WriteUInt32(4);
    WriteUInt32(0x80000000);
    WriteUInt32(0);
    strings.CopyTo(bytes, offset);

    var metadata = new PhyreClusterMetadataReader().Read(bytes);
    var descriptor = metadata.Classes[0];
    var member = descriptor.Members[0];
    Equal("PMesh", descriptor.Name);
    Equal(16u, descriptor.Size);
    Equal(4u, descriptor.Alignment);
    Equal(0x42u, descriptor.Flags);
    Equal("m_count", member.Name);
    Equal("PUInt32", member.TypeName!);
    Equal(4u, member.ValueOffset);
    Equal(true, member.IsDynamicArrayPointer);

    void WriteUInt32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        offset += 4;
    }
}

static void DecompressesPhyreFixups()
{
    // One pointer block followed by a grouped array block and a second array block.
    byte[] bytes =
    {
        0, 4, 0, 5, 2, 7, 1, // member #2 -> list #2, object #5, offset 7, array index 1
        1, 6, 0, 1, 10,       // member #3 -> same array target (count 1, offset 10) for both objects
        0, 8, 2, 20, 3, 30,  // member #4 -> arrays (count 2, offset 20), (count 3, offset 30)
    };
    var groups = new[]
    {
        new PhyreInstanceGroup(0, 0, "Synthetic", 1, 0, 0, 0, 0, 1, 0),
        new PhyreInstanceGroup(1, 0, "Synthetic", 2, 0, 0, 0, 4, 0, 0),
    };
    var header = new PhyreClusterHeader(
        0, 0,
        ArrayFixupSize: 11, ArrayFixupCount: 4,
        PointerFixupSize: 7, PointerFixupCount: 1,
        PointerArrayFixupSize: 0, PointerArrayFixupCount: 0,
        PointersInArraysCount: 0,
        UserFixupCount: 0, UserFixupDataSize: 0,
        HeaderClassInstanceCount: 0, HeaderClassChildCount: 0,
        InstanceHeadersOffset: 0, ObjectDataOffset: 0);
    var metadata = new PhyreClusterMetadata(
        PhyreClusterMetadataReader.LittleEndianMarker,
        false,
        0,
        0,
        Array.Empty<string>(),
        Array.Empty<PhyreClassDescriptor>(),
        groups,
        header);

    var fixups = new PhyreFixupReader().Read(bytes, metadata);
    Equal(1, fixups.Pointers.Count);
    Equal(4, fixups.Arrays.Count);
    Equal(2u, fixups.Pointers[0].SourceMemberId);
    Equal(2u, fixups.Pointers[0].DestinationListIndex);
    Equal(5u, fixups.Pointers[0].DestinationObjectId);
    Equal(7u, fixups.Pointers[0].DestinationOffset);
    Equal(1u, fixups.Pointers[0].ArrayIndex);
    Equal(1u, fixups.Arrays[1].Count);
    Equal(10u, fixups.Arrays[1].Offset);
    Equal(3u, fixups.Arrays[3].Count);
    Equal(30u, fixups.Arrays[3].Offset);
    Equal(18L, fixups.VramDataOffset);
}

static void RaycastsTransformedSceneTriangles()
{
    var model = CreateTriangleModel("TEST", "Float32x3");
    var instance = new SceneModelInstance(7, "TEST", "triangle", model, Matrix4x4.CreateTranslation(5, 0, 0));
    var result = new SceneRaycaster().Cast(new SceneRay(new Vector3(5, 0, -2), Vector3.UnitZ), new[] { instance });
    Equal(1, result.TestedTriangles);
    Equal(0, result.Issues.Count);
    Equal(7, result.Hit!.Instance.Id);
    Near(2f, result.Hit.Distance);
    Near(5f, result.Hit.Position.X);
    Near(1f, result.Hit.Normal.Z);
}

static void ReturnsEveryModelHitInDepthOrder()
{
    var model = CreateTriangleModel("TEST", "Float32x3");
    var front = new SceneModelInstance(10, "MAP", "map", model, Matrix4x4.Identity);
    var behind = new SceneModelInstance(11, "PROP", "prop", model, Matrix4x4.CreateTranslation(0f, 0f, 2f));
    var ray = new SceneRay(new Vector3(0f, 0f, -2f), Vector3.UnitZ);
    var result = new SceneRaycaster().CastAll(ray, new[] { behind, front });
    Equal(2, result.Hits.Count);
    Equal(10, result.Hits[0].Instance.Id);
    Equal(11, result.Hits[1].Instance.Id);
    var picked = new SceneElementPicker().PickAll(ray, new[] { behind, front }, null, 0.3f);
    Equal("map", picked[0].Selection.Name);
    Equal("prop", picked[1].Selection.Name);
}

static void PreservesModelSelectionKind()
{
    var model = CreateTriangleModel("CHARACTER", "Float32x3");
    var character = new SceneModelInstance(
        17,
        "C_MON000",
        "Monster 17",
        model,
        Matrix4x4.Identity,
        SelectionKind: SceneElementKind.ScriptCharacter);
    var picked = new SceneElementPicker().Pick(
        new SceneRay(new Vector3(0f, 0f, -2f), Vector3.UnitZ),
        new[] { character },
        null,
        0.3f);
    Equal(SceneElementKind.ScriptCharacter, picked!.Selection.Kind);
}

static void ReportsUnsupportedPickingGeometry()
{
    var model = CreateTriangleModel("TEST", "Float16x3");
    var instance = new SceneModelInstance(2, "TEST", "unsupported", model, Matrix4x4.Identity);
    var result = new SceneRaycaster().Cast(new SceneRay(new Vector3(0, 0, -2), Vector3.UnitZ), new[] { instance });
    Equal(0, result.TestedTriangles);
    Equal(1, result.Issues.Count);
    Equal(true, result.Hit is null);
}

static void ReportsTruncatedPickingVertexData()
{
    var model = CreateTriangleModel("TEST", "Float32x3");
    var primitive = model.Meshes[0].Primitives[0];
    var truncatedBuffer = primitive.VertexBuffers[0] with { Data = primitive.VertexBuffers[0].Data[..20] };
    var malformedModel = model with
    {
        Meshes = new[]
        {
            model.Meshes[0] with
            {
                Primitives = new[] { primitive with { VertexBuffers = new[] { truncatedBuffer } } },
            },
        },
    };
    var instance = new SceneModelInstance(4, "TEST", "truncated vertices", malformedModel, Matrix4x4.Identity);
    var result = new SceneRaycaster().Cast(new SceneRay(new Vector3(0, 0, -2), Vector3.UnitZ), new[] { instance });
    Equal(0, result.TestedTriangles);
    Equal("Position vertex buffer is truncated.", result.Issues.Single().Reason);
}

static void ReportsTruncatedPickingIndexData()
{
    var model = CreateTriangleModel("TEST", "Float32x3");
    var primitive = model.Meshes[0].Primitives[0];
    var malformedModel = model with
    {
        Meshes = new[]
        {
            model.Meshes[0] with
            {
                Primitives = new[] { primitive with { Indices = primitive.Indices with { Data = new byte[4] } } },
            },
        },
    };
    var instance = new SceneModelInstance(5, "TEST", "truncated indices", malformedModel, Matrix4x4.Identity);
    var result = new SceneRaycaster().Cast(new SceneRay(new Vector3(0, 0, -2), Vector3.UnitZ), new[] { instance });
    Equal(0, result.TestedTriangles);
    Equal("Index buffer is truncated.", result.Issues.Single().Reason);
}

static void CalculatesTransformedSceneBounds()
{
    var model = CreateTriangleModel("TEST", "Float32x3");
    var instance = new SceneModelInstance(3, "TEST", "bounds", model, Matrix4x4.CreateTranslation(5, 0, 0));
    var result = new SceneBoundsCalculator().Calculate(new[] { instance });
    Equal(true, result.HasGeometry);
    Equal(0, result.Issues.Count);
    Near(4f, result.Minimum.X);
    Near(6f, result.Maximum.X);
    Near(5f, result.Center.X);
    Near(MathF.Sqrt(2f), result.Radius);
}

static void EvaluatesSkeletonAnimation()
{
    var skeleton = new CpuSkeleton(
        new[]
        {
            new CpuSkeletonJoint("Root", -1, Matrix4x4.Identity),
            new CpuSkeletonJoint("Child", 0, Matrix4x4.CreateTranslation(1, 0, 0)),
        },
        new[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation(-1, 0, 0) },
        new[] { 0, 1 });
    var clip = new CpuAnimationClip(
        "TEST_CLIP_MOVE", "MOVE", 0f, 1f,
        new[]
        {
            new CpuAnimationChannel(
                "Child", CpuAnimationPath.Translation, CpuAnimationInterpolation.Linear,
                new[] { 0f, 1f },
                new[] { new Vector4(1, 0, 0, 0), new Vector4(3, 0, 0, 0) }),
        });
    var pose = new CpuSkeletonPoseEvaluator().Evaluate(skeleton, clip, 0.5f);
    Near(2f, pose.WorldTransforms[1].M41);
    Near(1f, pose.SkinMatrices[1].M41);

    var clipWithAuxiliaryTarget = clip with
    {
        Channels = clip.Channels.Append(new CpuAnimationChannel(
            "effector1", CpuAnimationPath.Translation, CpuAnimationInterpolation.Linear,
            new[] { 0f, 1f },
            new[] { Vector4.Zero, Vector4.One })).ToArray(),
    };
    Throws<InvalidDataException>(() =>
        new CpuSkeletonPoseEvaluator().Evaluate(skeleton, clipWithAuxiliaryTarget, 0.5f));
    var projected = new CpuSkeletonPoseEvaluator().Evaluate(
        skeleton,
        clipWithAuxiliaryTarget,
        0.5f,
        CpuAnimationUnboundTargetBehavior.Ignore);
    Near(2f, projected.WorldTransforms[1].M41);
}

static void EvaluatesSceneNodeAnimation()
{
    var nodes = new[]
    {
        new CpuSceneNode("Root", -1, Matrix4x4.CreateTranslation(5, 0, 0)),
        new CpuSceneNode("Door", 0, Matrix4x4.CreateTranslation(1, 0, 0)),
    };
    var clip = new CpuAnimationClip(
        "DOOR", "DOOR", 0f, 2f,
        new[]
        {
            new CpuAnimationChannel(
                "Door", CpuAnimationPath.Translation, CpuAnimationInterpolation.Linear,
                new[] { 0f, 2f },
                new[] { new Vector4(1, 0, 0, 0), new Vector4(3, 0, 0, 0) }),
        });
    var pose = new CpuSceneAnimationEvaluator().Evaluate(nodes, clip, 1f);
    Near(7f, pose.WorldTransforms[1].M41);
}

static void ReadsObjectAnimationInfo()
{
    const string source = """
        <dae_inf>
          <anim_infomation>
            <Animation animName="wait" start="0" end="0" />
            <Animation animName="open1" start="0" end="10" soundid="13020" />
            <Animation animName="close1" start="10" end="20" loop="1" reverse="1" />
          </anim_infomation>
        </dae_inf>
        """;
    var actions = new GameObjectAnimationInfoReader().Read(new StringReader(source));
    Equal(3, actions.Count);
    Equal(0, actions["open1"].StartFrame);
    Equal(10, actions["open1"].EndFrame);
    Equal("13020", actions["open1"].Attributes["soundId"]);
    Equal(true, actions["close1"].Loop);
    Equal(true, actions["close1"].Reverse);
}

static void SegmentsEmbeddedAnimation()
{
    var source = new CpuAnimationClip(
        "O_DOOR", "embedded", 0f, 40f / 30f,
        Array.Empty<CpuAnimationChannel>());
    var open = CpuAnimationClipSegment.FromFrames(source, "open1", 0, 10);
    var close = CpuAnimationClipSegment.FromFrames(source, "close2", 30, 40);
    Equal("open1", open.Name);
    Near(0f, open.StartTime);
    Near(10f / 30f, open.EndTime);
    Near(1f, close.StartTime);
    Near(40f / 30f, close.EndTime);
    Throws<InvalidDataException>(() =>
        CpuAnimationClipSegment.FromFrames(source, "invalid", 40, 41));
}

static void CreatesCenterViewportRay()
{
    var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, -2), Vector3.Zero, Vector3.UnitY);
    var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1f, 0.1f, 100f);
    var ray = SceneRayFactory.FromViewport(50, 50, 100, 100, view, projection);
    Near(0f, ray.Direction.X);
    Near(0f, ray.Direction.Y);
    Near(1f, ray.Direction.Z);
}

static void ValidatesViewportLighting()
{
    var lighting = ViewportLighting.Neutral;
    Near(1f, lighting.DirectionToLight.Length());
    Throws<ArgumentOutOfRangeException>(() => new ViewportLighting(Vector3.Zero, Vector3.One, Vector3.One));
    Throws<ArgumentOutOfRangeException>(() => new ViewportLighting(Vector3.UnitY, -Vector3.One, Vector3.One));
}

static void DerivesViewportMaterialSettings()
{
    var material = new CpuMaterial(
        "leaves",
        new Vector4(0.8f, 0.9f, 1f, 1f),
        0,
        new Dictionary<string, float[]>
        {
            ["AlphaThreshold"] = new[] { 0.5f },
            ["UVaMUvColor"] = new[] { 0.2f, 0.3f, 0.4f, 0.5f },
            ["UVaMUvTexcoord"] = new[] { 0.1f, 0.2f, 2f, 3f },
        },
        new Dictionary<string, string>(),
        new Dictionary<string, int>(),
        EffectSwitches: new Dictionary<string, string>
        {
            ["ALPHA_TESTING_ENABLED"] = "1",
            ["ALPHA_BLENDING_ENABLED"] = "1",
            ["VERTEX_COLOR_ENABLED"] = "1",
            ["NO_ALL_LIGHTING_ENABLED"] = "1",
            ["GLARE_HIGHTPASS_ENABLED"] = "1",
            ["MULTI_UV_ENANLED"] = "1",
        });
    var settings = ViewportMaterialSettings.FromMaterial(material);
    Near(0.8f, settings.BaseColor.X);
    Near(0.5f, settings.AlphaThreshold!.Value);
    Equal(true, settings.AlphaTestingEnabled);
    Equal(true, settings.VertexColorEnabled);
    Equal(true, settings.AlphaBlendingEnabled);
    Equal(false, settings.LightingEnabled);
    Equal(true, settings.GlareHighPassEnabled);
    Equal(ViewportMultiUvBlendMode.Alpha, settings.MultiUvBlendMode);
    Near(0.3f, settings.MultiUvColor.Y);
    Near(3f, settings.MultiUvTransform.W);
    Equal(ViewportMaterialSettings.Fallback, ViewportMaterialSettings.FromMaterial(null));
}

static void ResolvesPhyreMaterialRenderPhases()
{
    var rasterizerState = new CpuRasterizerState(3, 2, false, 0, 0f, 0f, true, false, true, false);
    var opaqueState = new CpuRenderPassState(false, 2, 1, 1, 2, 1, 1, 15, rasterizerState);
    var transparentState = new CpuRenderPassState(true, 5, 6, 1, 2, 2, 5, 15, rasterizerState);
    var passes = new Dictionary<string, CpuRenderPassState>(StringComparer.Ordinal)
    {
        ["Opaque"] = opaqueState,
        ["ForceTransparent"] = transparentState,
        ["TransparentNoDepthMask"] = transparentState,
    };
    var resolver = new PhyreMaterialRenderPassResolver();
    var material = new CpuMaterial(
        "material",
        Vector4.One,
        null,
        new Dictionary<string, float[]>(),
        new Dictionary<string, string>(),
        new Dictionary<string, int>());

    var opaque = resolver.Resolve(material, new PhyreEffectMetadata(
        passes, new Dictionary<string, string>()));
    Equal(CpuMaterialRenderPhase.Opaque, opaque.RenderPhase);
    Equal(opaqueState, opaque.RenderPassState!);

    var glare = resolver.Resolve(material, new PhyreEffectMetadata(
        passes, new Dictionary<string, string> { ["GLARE_HIGHTPASS_ENABLED"] = "1" }));
    Equal(CpuMaterialRenderPhase.EffectTransparent, glare.RenderPhase);
    Equal(transparentState, glare.RenderPassState!);

    var transparent = resolver.Resolve(material with { RenderPassType = "ForceTransparent" },
        new PhyreEffectMetadata(passes, new Dictionary<string, string>()));
    Equal(CpuMaterialRenderPhase.Transparent, transparent.RenderPhase);
    Equal(transparentState, transparent.RenderPassState!);

    var authoredDefault = resolver.Resolve(material with { RenderPassType = "Default" },
        new PhyreEffectMetadata(
            passes,
            new Dictionary<string, string> { ["GLARE_HIGHTPASS_ENABLED"] = "1" },
            "TransparentNoDepthMask"));
    Equal(CpuMaterialRenderPhase.EffectTransparent, authoredDefault.RenderPhase);
    Equal(transparentState, authoredDefault.RenderPassState!);

    var unspecifiedPass = resolver.Resolve(material,
        new PhyreEffectMetadata(passes, new Dictionary<string, string>(), "TransparentNoDepthMask"));
    Equal(CpuMaterialRenderPhase.Opaque, unspecifiedPass.RenderPhase);
    Equal(opaqueState, unspecifiedPass.RenderPassState!);

    var explicitlyOpaqueGlare = resolver.Resolve(material with { RenderPassType = "Opaque" },
        new PhyreEffectMetadata(
            passes, new Dictionary<string, string> { ["GLARE_HIGHTPASS_ENABLED"] = "1" }));
    Equal(CpuMaterialRenderPhase.Opaque, explicitlyOpaqueGlare.RenderPhase);
    Equal(opaqueState, explicitlyOpaqueGlare.RenderPassState!);
}

static void SelectsAuthoredEnvironmentVariants()
{
    Equal(SceneEnvironmentVariant.Daylight, SceneEnvironmentVariantSelector.FromProfileName("default"));
    Equal(SceneEnvironmentVariant.Evening, SceneEnvironmentVariantSelector.FromProfileName("evening"));
    Equal(true, SceneEnvironmentVariantSelector.TryFromScriptProfile(
        0, out var daylightProfile));
    Equal(SceneEnvironmentVariant.Daylight, daylightProfile);
    Equal(true, SceneEnvironmentVariantSelector.TryFromScriptProfile(
        1, out var eveningProfile));
    Equal(SceneEnvironmentVariant.Evening, eveningProfile);
    Equal(true, SceneEnvironmentVariantSelector.TryFromScriptProfile(
        2, out var nightProfile));
    Equal(SceneEnvironmentVariant.Night, nightProfile);
    Equal(true, SceneEnvironmentVariantSelector.TryFromScriptProfile(
        3, out var morningProfile));
    Equal(SceneEnvironmentVariant.Morning, morningProfile);
    Equal(true, SceneEnvironmentVariantSelector.TryFromScriptProfile(
        4, out var rainProfile));
    Equal(SceneEnvironmentVariant.Rain, rainProfile);
    Equal(false, SceneEnvironmentVariantSelector.TryFromScriptProfile(
        5, out _));
    Equal(SceneEnvironmentVariant.Night, SceneEnvironmentVariantSelector.GetAuthoredVariant("light_night")!.Value);
    Equal(true, SceneEnvironmentVariantSelector.IsVisible("lamp", SceneEnvironmentVariant.Daylight));
    Equal(true, SceneEnvironmentVariantSelector.IsVisible("light_daylight", SceneEnvironmentVariant.Daylight));
    Equal(false, SceneEnvironmentVariantSelector.IsVisible("light_evening", SceneEnvironmentVariant.Daylight));
    Equal(false, SceneEnvironmentVariantSelector.IsVisible("light_night", SceneEnvironmentVariant.Daylight));
    Equal(true, SceneEnvironmentVariantSelector.IsVisible("sky_night", SceneEnvironmentVariant.Night));
    Equal(false, SceneEnvironmentVariantSelector.IsVisible("sky_night", SceneEnvironmentVariant.Daylight));
}

static void ResolvesPhyreArchiveAssetPaths()
{
    var expected = new PackageEntry(2, "ed8.fx#ABC.phyre", 10, 10, 0, PackageCompressionType.None);
    var entries = new[]
    {
        new PackageEntry(1, "model.phyre", 10, 10, 0, PackageCompressionType.None),
        expected,
    };
    var resolver = new PhyreArchiveAssetResolver();
    Equal(expected, resolver.Resolve(entries, "shaders/ed8.fx#ABC")!);
    Equal(expected, resolver.Resolve(entries, "ed8.fx#ABC.phyre")!);
}

static void KeepsEditorCameraOrbitCentered()
{
    var camera = new EditorOrbitCamera();
    camera.Initialize(Vector3.Zero, new Vector3(0, 0, -10));
    camera.Orbit(100, -20);
    Near(10f, camera.Distance);
    Near(10f, Vector3.Distance(camera.Position, camera.Target));
    var positionBeforePan = camera.Position;
    var targetBeforePan = camera.Target;
    camera.Pan(25, -10, 720, MathF.PI / 3f);
    var cameraTranslation = camera.Position - positionBeforePan;
    var targetTranslation = camera.Target - targetBeforePan;
    Near(cameraTranslation.X, targetTranslation.X);
    Near(cameraTranslation.Y, targetTranslation.Y);
    Near(cameraTranslation.Z, targetTranslation.Z);
    camera.Zoom(1, 0.1f, 100f);
    Equal(true, camera.Distance < 10f);

    var freeCamera = new EditorOrbitCamera();
    freeCamera.Initialize(Vector3.Zero, new Vector3(0, 0, -10));
    Equal(-Vector3.UnitX, freeCamera.ScreenRight);
    var freePosition = freeCamera.Position;
    freeCamera.Look(100, -20);
    Equal(freePosition, freeCamera.Position);
    Equal(true, freeCamera.Forward.X < 0f);
    Equal(true, freeCamera.Forward.Y > 0f);
    var forward = freeCamera.Forward;
    freeCamera.Dolly(5f);
    Near(5f, Vector3.Distance(freePosition, freeCamera.Position));
    Near(1f, Vector3.Dot(Vector3.Normalize(freeCamera.Position - freePosition), forward));

    var stableCamera = new EditorOrbitCamera();
    stableCamera.Initialize(new Vector3(0, 0, 1), Vector3.Zero);
    var quarterTurnPixels = (MathF.PI / 2f) / 0.004f;
    stableCamera.Look(0f, quarterTurnPixels);
    Equal(true, stableCamera.Forward.Y < -0.999f);
    Equal(Vector3.UnitY, stableCamera.WorldUp);
    Near(0f, Vector3.Dot(stableCamera.Forward, stableCamera.ScreenUp), 0.0001f);
    var downwardDirection = stableCamera.Forward;
    stableCamera.Look(0f, quarterTurnPixels);
    Near(downwardDirection.X, stableCamera.Forward.X);
    Near(downwardDirection.Y, stableCamera.Forward.Y);
    Near(downwardDirection.Z, stableCamera.Forward.Z);
}

static void SmoothsEditorCameraDollyInput()
{
    var smoother = new EditorCameraDollySmoother();
    smoother.Add(10f);
    var first = smoother.Advance(1f / 60f);
    Equal(true, first > 0f && first < 10f);
    Equal(true, smoother.RemainingDistance > 0f);
    var accumulated = first;
    for (var frame = 0; frame < 120; frame++) accumulated += smoother.Advance(1f / 60f);
    Near(10f, accumulated, 0.001f);
    Near(0f, smoother.RemainingDistance, 0.001f);
    smoother.Add(-5f);
    Equal(true, smoother.Advance(1f / 60f) < 0f);
    smoother.Reset();
    Near(0f, smoother.RemainingDistance);
}

static void BuildsTypedOpsOverlayGeometry()
{
    var transform = new MapTransform(Vector3.Zero, Quaternion.Identity, new Vector3(2, 4, 6), Vector3.Zero, Vector3.Zero);
    var map = new MapScene(
        "test.ops",
        Array.Empty<MapProp>(),
        Array.Empty<byte>(),
        new[]
        {
            new MapVolume(0, MapVolumeKind.Entry, "entry", transform, null, null, new Dictionary<string, string>()),
        },
        new[]
        {
            new MapPoint(0, MapPointKind.LookPoint, "look", Vector3.Zero, 2f, new Dictionary<string, string>()),
        },
        new[]
        {
            new MapCameraMarker(0, "camera", new Vector3(0, 1, -2), Vector3.Zero, new Dictionary<string, string>()),
        },
        Array.Empty<MapSoundMarker>(),
        Array.Empty<MapLightMarker>());
    var geometry = new SceneOverlayBuilder().BuildGeometry(map);
    Equal(22, geometry.Lines.Count);
    Equal(204, geometry.Triangles.Count);
    Equal(true, geometry.Lines.Any(line => line.Start == new Vector3(-1, -2, -3)));
    Equal(true, geometry.Lines.Any(line => line.Thickness > 2f));
    Equal(true, geometry.Lines.Any(line => line.Start == new Vector3(0, 1, -2) && line.End == Vector3.Zero));
    Equal(true, geometry.Triangles.All(triangle => triangle.Color.W > 0f && triangle.Color.W < 1f));
}

static void RendersDeclaredSoundVolumeShapes()
{
    var sound = new MapSoundMarker(
        0,
        "water",
        MapSoundKind.Box,
        "BOX",
        new Vector3(2, 3, 4),
        20f,
        90f,
        new Vector3(30, 1, 80),
        new Dictionary<string, string>(),
        3,
        1f);
    var map = new MapScene(
        "test.ops",
        Array.Empty<MapProp>(),
        Array.Empty<byte>(),
        Array.Empty<MapVolume>(),
        Array.Empty<MapPoint>(),
        Array.Empty<MapCameraMarker>(),
        new[] { sound },
        Array.Empty<MapLightMarker>());

    var geometry = new SceneOverlayBuilder().BuildGeometry(map);
    Equal(15, geometry.Lines.Count);
    Equal(12, geometry.Triangles.Count);
    Equal(false, geometry.Lines.Any(line => Vector3.Distance(line.Start, sound.Position) == sound.Range));
    Equal(true, geometry.Triangles.All(triangle => MathF.Abs(triangle.Color.W - 0.09f) < 0.0001f));
}

static void PicksExactOpsVolumeGeometry()
{
    var transform = new MapTransform(Vector3.Zero, Quaternion.Identity, new Vector3(2, 2, 2), Vector3.Zero, Vector3.Zero);
    var map = new MapScene(
        "test.ops",
        Array.Empty<MapProp>(),
        Array.Empty<byte>(),
        new[]
        {
            new MapVolume(9, MapVolumeKind.Entry, "transition", transform, "a0100", "entry", new Dictionary<string, string>()),
        },
        Array.Empty<MapPoint>(),
        Array.Empty<MapCameraMarker>(),
        Array.Empty<MapSoundMarker>(),
        Array.Empty<MapLightMarker>());
    var hit = new SceneElementPicker().Pick(
        new SceneRay(new Vector3(0, 0, -5), Vector3.UnitZ),
        Array.Empty<SceneModelInstance>(),
        map,
        0.3f);
    Equal(SceneElementKind.EntryVolume, hit!.Selection.Kind);
    Equal(9, hit.Selection.SourceIndex);
    Near(4f, hit.Distance);
}

static void UndoesAndRedoesSceneDocumentTransforms()
{
    var transform = new MapTransform(
        new Vector3(3, 4, 5),
        Quaternion.Identity,
        Vector3.One,
        Vector3.Zero,
        Vector3.Zero);
    var prop = new MapProp(
        0, "O_TEST", "editable", transform, null, Vector4.One, Vector3.Zero,
        new Dictionary<string, string>());
    var map = new MapScene(
        "test.ops",
        new[] { prop },
        Array.Empty<byte>(),
        Array.Empty<MapVolume>(),
        Array.Empty<MapPoint>(),
        Array.Empty<MapCameraMarker>(),
        Array.Empty<MapSoundMarker>(),
        Array.Empty<MapLightMarker>());
    var model = CreateTriangleModel("O_TEST", "Float32x3");
    var header = new ScriptHeader("test.dat", "test", ScriptKind.Scenario, ScriptTargetKind.Map, 0, 0, Array.Empty<byte>());
    var session = new EditorSession(
        new ScriptOpenResult(header, null, "test.ops"),
        map,
        new Dictionary<string, AssetResolution>(),
        new Dictionary<string, AssetManifestLoad>(),
        new Dictionary<string, AssetModelLoad>
        {
            ["O_TEST"] = new("O_TEST", AssetModelLoadStatus.Loaded, model, null),
        });
    var document = new EditorSceneDocument(session);
    var previewChanges = 0;
    var committedChanges = 0;
    document.PreviewChanged += (_, _) => previewChanges++;
    document.Changed += (_, _) => committedChanges++;
    Equal(false, document.IsDirty);
    var selection = new SceneElementSelection(SceneElementKind.Prop, 0, "editable");
    var original = document.Find(selection)!.Transform;
    document.PreviewTransform(selection, original with { Position = new Vector3(8, 4, 5) });
    Equal(1, previewChanges);
    Equal(0, committedChanges);
    Equal(false, document.IsDirty);
    Equal(true, document.CommitPreview(selection, original));
    Equal(1, committedChanges);
    Equal(true, document.IsDirty);
    Near(8f, document.CreateMapSnapshot()!.Props[0].Transform.Position.X);
    Equal(true, document.Undo());
    Equal(false, document.IsDirty);
    Near(3f, document.CreateModelInstances()[0].Transform.M41);
    Equal(true, document.Redo());
    Equal(true, document.IsDirty);
    Near(8f, document.CreateModelInstances()[0].Transform.M41);
    document.MarkSaved();
    Equal(false, document.IsDirty);
    Equal(true, document.ApplyPropAttributes(
        selection,
        new Dictionary<string, string>(document.FindProp(selection)!.SourceAttributes)));
    Equal(false, document.IsDirty);
    var added = document.AddPropFromTemplate(selection, "O_TEST", "copy", model);
    Equal(true, document.IsDirty);
    Equal(2, document.CreateMapSnapshot()!.Props.Count);
    Equal(true, document.Undo());
    Equal(1, document.CreateMapSnapshot()!.Props.Count);
    Equal(true, document.Redo());
    Equal(2, document.CreateMapSnapshot()!.Props.Count);
    Equal(true, document.DeleteProp(added));
    Equal(1, document.CreateMapSnapshot()!.Props.Count);
    Equal(true, document.Undo());
    Equal(2, document.CreateMapSnapshot()!.Props.Count);
    var independent = document.AddProp("O_TEST", "independent", model, new Vector3(20, 1, 2));
    var uniqueIndependent = document.AddProp("O_TEST", "independent", model, new Vector3(21, 1, 2));
    Equal("independent_001", uniqueIndependent.Name);
    var independentProp = document.CreateMapSnapshot()!.Props.Single(value => value.SourceIndex == independent.SourceIndex);
    Equal(OpsNewPropProfile.UndocumentedNeutralFlags, independentProp.Flags!.Value);
    Near(20f, independentProp.Transform.Position.X);
    Near(1f, MathF.Abs(Quaternion.Dot(Quaternion.Identity, independentProp.Transform.Rotation)));
    var changedAttributes = new Dictionary<string, string>(document.FindProp(selection)!.SourceAttributes)
    {
        ["flag"] = "0x283",
        ["customEditorValue"] = "kept",
    };
    Equal(true, document.ApplyPropAttributes(selection, changedAttributes));
    Equal(0x283u, document.FindProp(selection)!.Flags!.Value);
    Equal(true, document.Undo());
    Equal(false, document.FindProp(selection)!.SourceAttributes.ContainsKey("customEditorValue"));
}

static void PicksTranslationGizmoAxes()
{
    var gizmo = new SceneTranslationGizmo();
    var ray = new SceneRay(new Vector3(1, 0, -5), Vector3.UnitZ);
    Equal(true, gizmo.TryPickAxis(ray, Vector3.Zero, 2f, 0.1f, out var axis));
    Equal(SceneGizmoAxis.X, axis);
    Equal(true, gizmo.TryGetAxisParameter(ray, Vector3.Zero, axis, out var parameter));
    Near(1f, parameter);
}

static void PicksRotationRings()
{
    var gizmo = new SceneRotationGizmo();
    var startRay = new SceneRay(new Vector3(1, 0, -5), Vector3.UnitZ);
    Equal(true, gizmo.TryPickAxis(startRay, Vector3.Zero, 1f, 0.1f, out var axis, out var start));
    Equal(SceneGizmoAxis.Z, axis);
    var endRay = new SceneRay(new Vector3(0, 1, -5), Vector3.UnitZ);
    Equal(true, gizmo.TryGetRingVector(endRay, Vector3.Zero, axis, out var end));
    Near(MathF.PI / 2f, SceneRotationGizmo.SignedAngle(axis, start, end));
}

static void PicksCameraHandles()
{
    var camera = new MapCameraMarker(
        0, "0", Vector3.Zero, new Vector3(0, 0, 5), new Dictionary<string, string>());
    var gizmo = new SceneCameraGizmo();
    Equal(7, gizmo.Build(camera, 0.2f, SceneCameraHandle.LookAt).Count);
    Equal(true, gizmo.TryPickHandle(
        new SceneRay(new Vector3(0, 0, -5), Vector3.UnitZ), camera, 0.2f, out var eye));
    Equal(SceneCameraHandle.Eye, eye);
    var targetRayOrigin = new Vector3(2, 0, 0);
    Equal(true, gizmo.TryPickHandle(
        new SceneRay(targetRayOrigin, Vector3.Normalize(camera.LookAt - targetRayOrigin)),
        camera,
        0.2f,
        out var lookAt));
    Equal(SceneCameraHandle.LookAt, lookAt);
}

static void SnapsSceneTransforms()
{
    var settings = new SceneSnapSettings(0.25f, MathF.PI / 12f, 0.1f);
    Near(1.25f, settings.SnapTranslation(1.14f));
    Near(-0.5f, settings.SnapTranslation(-0.4f));
    Near(MathF.PI / 6f, settings.SnapRotation(0.49f));
    Near(1.2f, settings.SnapScale(1.16f));
    Near(-0.1f, settings.SnapScale(-0.01f));
    Throws<ArgumentOutOfRangeException>(() => new SceneSnapSettings(0f, 1f, 1f));
}

static void GroupsSceneOutlinerElements()
{
    var identity = new SceneTransform(Vector3.Zero, Quaternion.Identity, Vector3.One);
    var elements = new[]
    {
        new EditableSceneElement(new SceneElementSelection(SceneElementKind.Light, 3, "light"), SceneTransformCapabilities.Translate, identity),
        new EditableSceneElement(new SceneElementSelection(SceneElementKind.Prop, 2, "z prop"), SceneTransformCapabilities.All, identity),
        new EditableSceneElement(new SceneElementSelection(SceneElementKind.Prop, 1, "a prop"), SceneTransformCapabilities.All, identity),
    };
    var groups = new SceneOutlinerBuilder().Build(elements);
    Equal(2, groups.Count);
    Equal("Props", groups[0].Name);
    Equal("Prop", groups[0].ElementTypeName);
    Equal("a prop", groups[0].Elements[0].Name);
    Equal("Lights", groups[1].Name);
    Equal("Light", groups[1].ElementTypeName);
}

static void ValidatesGameInstallations()
{
    var root = Path.Combine(Path.GetTempPath(), $"ed8-install-{Guid.NewGuid():N}");
    var data = Path.Combine(root, "data");
    try
    {
        Directory.CreateDirectory(Path.Combine(data, "scripts"));
        Directory.CreateDirectory(Path.Combine(data, "ops"));
        Directory.CreateDirectory(Path.Combine(data, "asset"));
        Equal(true, GameInstallation.TryOpen(root, out var fromRoot, out _));
        Equal(Path.GetFullPath(data), fromRoot!.DataPath);
        Equal(true, GameInstallation.TryOpen(data, out var fromData, out _));
        Equal(Path.GetFullPath(root), fromData!.RootPath);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void PersistsEditorUserSettings()
{
    var root = Path.Combine(Path.GetTempPath(), $"ed8-settings-{Guid.NewGuid():N}");
    var path = Path.Combine(root, "settings.json");
    try
    {
        var store = new EditorSettingsStore(path);
        Equal(EditorUserSettings.Default, store.Load());
        Directory.CreateDirectory(root);
        File.WriteAllText(path, """{"Version":1,"GameDirectory":"C:\\\\Legacy"}""");
        Equal(EditorKeyboardLayout.Azerty, store.Load().KeyboardLayout);
        var settings = new EditorUserSettings(1, @"C:\Games\Cold Steel", EditorKeyboardLayout.Qwerty);
        store.Save(settings);
        Equal(settings, store.Load());
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static void WritesTransformedOpsProps()
{
    const string xml = "<Ops><MapObjects><AssetObject asset=\"O_TEST\" name=\"prop\" custom=\"preserved\" pos=\"1, 2, 3\" rot=\"0, 0, 0\" scl=\"1, 1, 1\" /></MapObjects></Ops>";
    var sourcePath = WriteTemporaryOps(xml);
    var outputPath = Path.Combine(Path.GetTempPath(), $"ed8-written-{Guid.NewGuid():N}.ops");
    try
    {
        var source = new OpsReader().Read(sourcePath);
        Equal(true, new OpsWriter().Serialize(source, source).SequenceEqual(source.OriginalBytes));
        var newRotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.35f, -0.2f, 0.1f));
        var edited = source with
        {
            Props = new[]
            {
                source.Props[0] with
                {
                    Transform = source.Props[0].Transform with
                    {
                        Position = new Vector3(7, 8, 9),
                        Rotation = newRotation,
                        Scale = new Vector3(2, 3, 4),
                    },
                },
            },
        };
        new OpsWriter().Write(outputPath, source, edited);
        var writtenText = File.ReadAllText(outputPath);
        Equal(true, writtenText.Contains("custom=\"preserved\"", StringComparison.Ordinal));
        var reloaded = new OpsReader().Read(outputPath);
        Near(7f, reloaded.Props[0].Transform.Position.X);
        Near(8f, reloaded.Props[0].Transform.Position.Y);
        Near(9f, reloaded.Props[0].Transform.Position.Z);
        Near(2f, reloaded.Props[0].Transform.Scale.X);
        Near(3f, reloaded.Props[0].Transform.Scale.Y);
        Near(4f, reloaded.Props[0].Transform.Scale.Z);
        Near(1f, MathF.Abs(Quaternion.Dot(newRotation, reloaded.Props[0].Transform.Rotation)));

        var addedProp = source.Props[0] with
        {
            SourceIndex = 1,
            AssetId = "O_ADDED",
            Name = "added",
            SourceAttributes = new Dictionary<string, string>(source.Props[0].SourceAttributes)
            {
                ["asset"] = "O_ADDED",
                ["name"] = "added",
            },
        };
        new OpsWriter().Write(outputPath, source, source with { Props = new[] { source.Props[0], addedProp } });
        var withAddition = new OpsReader().Read(outputPath);
        Equal(2, withAddition.Props.Count);
        Equal("O_ADDED", withAddition.Props[1].AssetId);
        Equal("preserved", withAddition.Props[1].SourceAttributes["custom"]);

        new OpsWriter().Write(outputPath, source, source with { Props = Array.Empty<MapProp>() });
        Equal(0, new OpsReader().Read(outputPath).Props.Count);

        var metadataEdit = source with
        {
            Props = new[]
            {
                source.Props[0] with
                {
                    SourceAttributes = new Dictionary<string, string>(source.Props[0].SourceAttributes)
                    {
                        ["custom"] = "changed",
                        ["newAttribute"] = "new value",
                    },
                },
            },
        };
        new OpsWriter().Write(outputPath, source, metadataEdit);
        var metadataReload = new OpsReader().Read(outputPath).Props[0];
        Equal("changed", metadataReload.SourceAttributes["custom"]);
        Equal("new value", metadataReload.SourceAttributes["newAttribute"]);
    }
    finally
    {
        File.Delete(sourcePath);
        if (File.Exists(outputPath)) File.Delete(outputPath);
    }
}

static void WritesStructuralOpsEdits()
{
    const string xml = """
        <Ops>
          <MapObjects />
          <Entrys><EntryBox name="entry" next="a0001" entry="start" flag="0x1" pos="1,2,3, 0,0,0, 4,5,6" /></Entrys>
          <GroupBoxes><GroupBox name="group" flag="0x2" pos="2,3,4, 0,0,0, 5,6,7" /></GroupBoxes>
          <LookPoints><LookPoint name="look" flag="0x3" pos="3,4,5" radius="2" /></LookPoints>
          <MapCameras><MapCamera no="7" flag="0x4" fov="35" eye="4,5,6" lookat="7,8,9" /></MapCameras>
          <MapSounds><SoundObject seName="bell" seType="POINT" sePosition="5,6,7" seRange="8" seRotation="0" seScale="1,1,1" seVolume="0.5" /></MapSounds>
          <Lights><Light group="0" type="POINT" flag="0x5" pos="6,7,8" color="1,0.5,0.25,1" colorPower="2" innerRange="3" outerRange="9" /></Lights>
        </Ops>
        """;
    var sourcePath = WriteTemporaryOps(xml);
    var outputPath = Path.Combine(Path.GetTempPath(), $"ed8-structural-{Guid.NewGuid():N}.ops");
    try
    {
        var source = new OpsReader().Read(sourcePath);
        var header = new ScriptHeader("test.dat", "test", ScriptKind.Scenario, ScriptTargetKind.Map, 0, 0, Array.Empty<byte>());
        var session = new EditorSession(
            new ScriptOpenResult(header, null, sourcePath),
            source,
            new Dictionary<string, AssetResolution>(),
            new Dictionary<string, AssetManifestLoad>(),
            new Dictionary<string, AssetModelLoad>());
        var document = new EditorSceneDocument(session);
        Equal(true, new OpsWriter().Serialize(source, document.CreateMapSnapshot()!).SequenceEqual(source.OriginalBytes));
        var originals = document.Elements.Select(value => value.Selection).ToArray();
        var cameraSelection = originals.Single(value => value.Kind == SceneElementKind.Camera);
        var originalLookAt = document.FindCamera(cameraSelection)!.LookAt;
        var editedLookAt = new Vector3(20, 21, 22);
        Equal(true, document.PreviewCameraLookAt(cameraSelection, editedLookAt));
        Equal(false, document.IsDirty);
        Equal(true, document.CommitCameraLookAtPreview(cameraSelection, originalLookAt));
        Equal(true, document.IsDirty);
        Equal(editedLookAt, document.FindCamera(cameraSelection)!.LookAt);
        Equal(true, document.Undo());
        Equal(originalLookAt, document.FindCamera(cameraSelection)!.LookAt);
        Equal(true, document.Redo());
        Equal(editedLookAt, document.FindCamera(cameraSelection)!.LookAt);
        void Apply(SceneElementKind kind, Action<Dictionary<string, string>> edit)
        {
            var selected = originals.Single(value => value.Kind == kind);
            var values = new Dictionary<string, string>(document.FindElementAttributes(selected)!.Values);
            edit(values);
            Equal(true, document.ApplyElementAttributes(selected, values));
        }
        Apply(SceneElementKind.EntryVolume, values =>
        {
            values["name"] = "entry_event";
            values["next"] = "a9999";
            values["pos"] = "10,11,12, 0,0,0, 4,5,6";
        });
        Apply(SceneElementKind.GroupVolume, values => values["flag"] = "0x7");
        Apply(SceneElementKind.LookPoint, values =>
        {
            values["name"] = "look_event";
            values["pos"] = "13,14,15";
            values["radius"] = "4.5";
        });
        Apply(SceneElementKind.Camera, values =>
        {
            values["eye"] = "16,17,18";
            values["lookat"] = "19,20,21";
            values["fov"] = "45";
        });
        Apply(SceneElementKind.Sound, values =>
        {
            values["seName"] = "river";
            values["sePosition"] = "22,23,24";
            values["seRange"] = "12.5";
            values["seVolume"] = "0.75";
        });
        Apply(SceneElementKind.Light, values =>
        {
            values["pos"] = "25,26,27";
            values["colorPower"] = "4";
            values["outerRange"] = "11";
        });
        Near(10f, document.CreateMapSnapshot()!.Volumes.Single(
            value => value.Kind == MapVolumeKind.Entry).Transform.Position.X);
        Near(13f, document.CreateMapSnapshot()!.Points[0].Position.X);
        Near(16f, document.CreateMapSnapshot()!.Cameras[0].Eye.X);
        Near(19f, document.CreateMapSnapshot()!.Cameras[0].LookAt.X);
        Near(22f, document.CreateMapSnapshot()!.Sounds[0].Position.X);
        Near(25f, document.CreateMapSnapshot()!.Lights[0].Position.X);
        Near(4.5f, document.CreateMapSnapshot()!.Points[0].Radius!.Value);
        Near(12.5f, document.CreateMapSnapshot()!.Sounds[0].Range);
        Near(11f, document.CreateMapSnapshot()!.Lights[0].OuterRange);
        Equal(true, document.Undo());
        Near(9f, document.CreateMapSnapshot()!.Lights[0].OuterRange);
        Equal(true, document.Redo());
        Near(11f, document.CreateMapSnapshot()!.Lights[0].OuterRange);

        new OpsWriter().Write(outputPath, source, document.CreateMapSnapshot()!);
        var attributesReloaded = new OpsReader().Read(outputPath);
        Equal("a9999", attributesReloaded.Volumes.Single(value => value.Kind == MapVolumeKind.Entry).DestinationMap!);
        Equal("entry_event", attributesReloaded.Volumes.Single(value => value.Kind == MapVolumeKind.Entry).Name);
        Equal("0x7", attributesReloaded.Volumes.Single(value => value.Kind == MapVolumeKind.Group).SourceAttributes["flag"]);
        Equal("45", attributesReloaded.Cameras[0].SourceAttributes["fov"]);
        Near(12.5f, attributesReloaded.Sounds[0].Range);
        Equal("river", attributesReloaded.Sounds[0].SoundName);
        Near(4f, attributesReloaded.Lights[0].ColorPower);

        var invalidSound = new Dictionary<string, string>(
            document.FindElementAttributes(originals.Single(value => value.Kind == SceneElementKind.Sound))!.Values);
        invalidSound.Remove("seRange");
        Throws<ArgumentException>(() => document.ApplyElementAttributes(
            originals.Single(value => value.Kind == SceneElementKind.Sound), invalidSound));
        var invalidCamera = new Dictionary<string, string>(
            document.FindElementAttributes(originals.Single(value => value.Kind == SceneElementKind.Camera))!.Values)
        {
            ["fov"] = "not-a-number",
        };
        Throws<ArgumentException>(() => document.ApplyElementAttributes(
            originals.Single(value => value.Kind == SceneElementKind.Camera), invalidCamera));
        var invalidEntryPosition = new Dictionary<string, string>(
            document.FindElementAttributes(originals.Single(value => value.Kind == SceneElementKind.EntryVolume))!.Values)
        {
            ["pos"] = "invalid",
        };
        Throws<ArgumentException>(() => document.ApplyElementAttributes(
            originals.Single(value => value.Kind == SceneElementKind.EntryVolume), invalidEntryPosition));
        foreach (var original in originals)
        {
            var duplicate = document.DuplicateElement(original);
            Equal(1, duplicate.SourceIndex);
            Equal(true, document.DeleteElement(original));
        }
        var edited = document.CreateMapSnapshot()!;
        Equal(1, edited.Volumes.Count(value => value.Kind == MapVolumeKind.Entry));
        Equal(1, edited.Volumes.Count(value => value.Kind == MapVolumeKind.Group));
        Equal(1, edited.Points.Count);
        Equal(1, edited.Cameras.Count);
        Equal(1, edited.Sounds.Count);
        Equal(1, edited.Lights.Count);

        new OpsWriter().Write(outputPath, source, edited);
        var reloaded = new OpsReader().Read(outputPath);
        Equal("entry_event_001", reloaded.Volumes.Single(value => value.Kind == MapVolumeKind.Entry).Name);
        Near(10f, reloaded.Volumes.Single(value => value.Kind == MapVolumeKind.Entry).Transform.Position.X);
        Equal("group_001", reloaded.Volumes.Single(value => value.Kind == MapVolumeKind.Group).Name);
        Equal("look_event_001", reloaded.Points[0].Name);
        Near(13f, reloaded.Points[0].Position.X);
        Equal("7", reloaded.Cameras[0].Name);
        Near(16f, reloaded.Cameras[0].Eye.X);
        Near(19f, reloaded.Cameras[0].LookAt.X);
        Equal("river", reloaded.Sounds[0].SoundName);
        Near(22f, reloaded.Sounds[0].Position.X);
        Equal("0.75", reloaded.Sounds[0].SourceAttributes["seVolume"]);
        Near(12.5f, reloaded.Sounds[0].Range);
        Equal("0x5", reloaded.Lights[0].SourceAttributes["flag"]);
        Near(25f, reloaded.Lights[0].Position.X);
        Near(11f, reloaded.Lights[0].OuterRange);
        Equal(true, document.Undo());
        Equal(2, document.CreateMapSnapshot()!.Lights.Count);
    }
    finally
    {
        File.Delete(sourcePath);
        if (File.Exists(outputPath)) File.Delete(outputPath);
    }

}

/// <summary>
/// Which entry boxes get offered the map's script functions for their name.
///
/// An entry box means two different things in this game, and only its destination
/// map tells them apart. With one, it is a way out and the name is a label: of the
/// 37 boxes the game ships with entry type 2, not one carries the name of a
/// function. With none, it is a walk-in event trigger and the name *is* the
/// function the game calls — 462 of the 513 such boxes name one.
///
/// So the list belongs to the second kind alone. Offering it on a teleporter tells
/// an author to choose something they must not choose.
/// </summary>
/// <summary>
/// An entry box moved the way the viewport moves it reaches the file.
///
/// The existing coverage edits attributes; dragging goes through ApplyTransform
/// instead, and that is the path an author actually uses. It is written back under
/// the map's own name — a copy called <c>z9100.edited.ops</c> is a file the game
/// will never read, since it loads a map by name.
/// </summary>
/// <summary>
/// The whole path an author walks: a map in the game folder, a box moved, the edit
/// saved over the file the game reads, and the project putting it back.
///
/// This is what silently did nothing before. The map was only ever written when
/// asked, the only way to ask was a shortcut with no menu entry, and asking put up
/// a dialog offering <c>z9100.edited.ops</c> — a name the game never loads. Every
/// piece worked; nothing joined them.
/// </summary>
static void SavesMapInPlaceAndReverts()
{
    var root = Path.Combine(Path.GetTempPath(), $"ed8-savemap-{Guid.NewGuid():N}");
    var game = Path.Combine(root, "game");
    var opsFolder = Path.Combine(game, "data", "ops");
    Directory.CreateDirectory(opsFolder);
    var mapPath = Path.Combine(opsFolder, "z9100.ops");
    File.WriteAllText(
        mapPath,
        "<Ops><MapObjects/><Entrys>"
            + "<EntryBox name=\"default\" next=\"z9100\" entry=\"default\" placeid=\"0\" flag=\"0x0\""
            + " pos=\"0, 0, 0,  0, 0, 0,  2, 3, 2\" distance=\"1\" cameraDir=\"-1\""
            + " entryType=\"0\" markPos=\"0, 0, 0\" />"
            + "</Entrys></Ops>",
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    try
    {
        var project = ED8Editor.Application.ModProject.Create(
            Path.Combine(root, "my-mod.ed8mod"), game);

        // The map is inside the project's game folder, so this is where the editor
        // now saves without asking — the file the game reads, under its own name.
        Equal("data/ops/z9100.ops", project.RelativePathOf(mapPath) ?? "outside");
        Equal(
            "outside",
            project.RelativePathOf(Path.Combine(root, "elsewhere", "z9100.ops")) ?? "outside");

        var source = new OpsReader().Read(mapPath);
        var header = new ScriptHeader(
            "z9100.dat", "z9100", ScriptKind.Scenario, ScriptTargetKind.Map, 0, 0, Array.Empty<byte>());
        var document = new EditorSceneDocument(new EditorSession(
            new ScriptOpenResult(header, null, mapPath),
            source,
            new Dictionary<string, AssetResolution>(),
            new Dictionary<string, AssetManifestLoad>(),
            new Dictionary<string, AssetModelLoad>()));
        var box = document.Elements.Single(
            element => element.Selection.Kind == SceneElementKind.EntryVolume);
        Equal(true, document.ApplyTransform(
            box.Selection, box.Transform with { Position = new Vector3(-37f, 4f, -215f) }));

        // Exactly what saving does: pristine copy, write over the map, track it.
        project.CaptureOriginal(mapPath);
        new OpsWriter().Write(
            mapPath,
            source,
            document.CreateMapSnapshot() ?? throw new InvalidOperationException("No map."));
        project.TrackSave(mapPath);
        document.MarkSaved();
        Equal(false, document.IsDirty);

        var savedBox = new OpsReader().Read(mapPath).Volumes
            .Single(volume => volume.Kind == MapVolumeKind.Entry);
        Near(-37f, savedBox.Transform.Position.X);
        Near(-215f, savedBox.Transform.Position.Z);

        // And the point of doing it through a project: the game folder goes back.
        Equal(1, project.RestoreOriginals());
        var restored = new OpsReader().Read(mapPath).Volumes
            .Single(volume => volume.Kind == MapVolumeKind.Entry);
        Near(0f, restored.Transform.Position.X);
        Near(0f, restored.Transform.Position.Z);
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static void WritesDraggedEntryBox()
{
    const string xml = "<Ops><MapObjects/><Entrys>"
        + "<EntryBox name=\"default\" next=\"z9100\" entry=\"default\" placeid=\"0\" flag=\"0x0\""
        + " pos=\"0, 0, 0,  0, 0, 0,  2, 3, 2\" distance=\"1\" cameraDir=\"-1\""
        + " entryType=\"0\" markPos=\"0, 0, 0\" />"
        + "</Entrys></Ops>";
    var sourcePath = WriteTemporaryOps(xml);
    var outputPath = Path.Combine(Path.GetTempPath(), $"ed8-dragged-{Guid.NewGuid():N}.ops");
    try
    {
        var source = new OpsReader().Read(sourcePath);
        var header = new ScriptHeader(
            "test.dat", "test", ScriptKind.Scenario, ScriptTargetKind.Map, 0, 0, Array.Empty<byte>());
        var document = new EditorSceneDocument(new EditorSession(
            new ScriptOpenResult(header, null, sourcePath),
            source,
            new Dictionary<string, AssetResolution>(),
            new Dictionary<string, AssetManifestLoad>(),
            new Dictionary<string, AssetModelLoad>()));

        var box = document.Elements.Single(
            element => element.Selection.Kind == SceneElementKind.EntryVolume);
        Equal(false, document.IsDirty);
        Equal(true, document.ApplyTransform(
            box.Selection,
            box.Transform with { Position = new Vector3(-37f, 4f, -215f) }));
        Equal(true, document.IsDirty);

        var snapshot = document.CreateMapSnapshot()
            ?? throw new InvalidOperationException("The document has no map.");
        new OpsWriter().Write(outputPath, source, snapshot);
        var reloaded = new OpsReader().Read(outputPath);
        var written = reloaded.Volumes.Single(volume => volume.Kind == MapVolumeKind.Entry);
        Near(-37f, written.Transform.Position.X);
        Near(4f, written.Transform.Position.Y);
        Near(-215f, written.Transform.Position.Z);

        // What the box is stays what it was: only its position moved.
        Equal("z9100", written.DestinationMap ?? string.Empty);
        Equal("default", written.Name);
        Near(2f, written.Transform.Scale.X);
        Near(3f, written.Transform.Scale.Y);
    }
    finally
    {
        File.Delete(sourcePath);
        if (File.Exists(outputPath)) File.Delete(outputPath);
    }
}

static void ResolvesEntryBoxNameKind()
{
    const string xml = "<Ops><MapObjects/><Entrys>"
        + "<EntryBox name=\"go_z9100\" next=\"z9100\" entry=\"default\" placeid=\"0\" flag=\"0x1\""
        + " pos=\"1, 2, 3,  0, 0, 0,  10, 2.5, 2\" distance=\"2\" cameraDir=\"-1\""
        + " entryType=\"2\" markPos=\"0, 0, 0\" />"
        + "<EntryBox name=\"EV_C08E30S00\" next=\"\" entry=\"\" placeid=\"0\" flag=\"0x3\""
        + " pos=\"4, 5, 6,  0, 0, 0,  3, 2.5, 3\" distance=\"2\" cameraDir=\"-1\""
        + " entryType=\"0\" markPos=\"0, 0, 0\" />"
        + "</Entrys></Ops>";
    var sourcePath = WriteTemporaryOps(xml);
    try
    {
        var header = new ScriptHeader(
            "test.dat", "test", ScriptKind.Scenario, ScriptTargetKind.Map, 0, 0, Array.Empty<byte>());
        var document = new EditorSceneDocument(new EditorSession(
            new ScriptOpenResult(header, null, sourcePath),
            new OpsReader().Read(sourcePath),
            new Dictionary<string, AssetResolution>(),
            new Dictionary<string, AssetManifestLoad>(),
            new Dictionary<string, AssetModelLoad>()));

        var boxes = document.Elements
            .Where(element => element.Selection.Kind == SceneElementKind.EntryVolume)
            .ToArray();
        Equal(2, boxes.Length);

        // Resolved from what the editor itself hands the dialog, not from a
        // dictionary written here: the attributes go out through the codec, and a
        // key lost on the way is exactly the sort of thing that would put the
        // function list on a teleporter.
        foreach (var box in boxes)
        {
            var attributes = document.FindElementAttributes(box.Selection)
                ?? throw new InvalidOperationException($"'{box.Selection.Name}' has no attributes.");
            Equal(true, attributes.Values.ContainsKey("next"));
            var kind = OpsAttributeValueKinds.Resolve(box.Selection, "name", attributes.Values);
            Equal(
                box.Selection.Name == "go_z9100" ? OpsValueKind.Text : OpsValueKind.ScriptFunction,
                kind);
        }
    }
    finally
    {
        File.Delete(sourcePath);
    }
}

static void CreatesObservedOpsProfiles()
{
    const string xml = "<Ops><MapObjects/><Entrys/></Ops>";
    var sourcePath = WriteTemporaryOps(xml);
    var outputPath = Path.Combine(Path.GetTempPath(), $"ed8-profiles-{Guid.NewGuid():N}.ops");
    try
    {
        var source = new OpsReader().Read(sourcePath);
        var header = new ScriptHeader("test.dat", "test", ScriptKind.Scenario, ScriptTargetKind.Map, 0, 0, Array.Empty<byte>());
        var document = new EditorSceneDocument(new EditorSession(
            new ScriptOpenResult(header, null, sourcePath),
            source,
            new Dictionary<string, AssetResolution>(),
            new Dictionary<string, AssetManifestLoad>(),
            new Dictionary<string, AssetModelLoad>()));
        OpsSpatialCreationProfile Profile(string id)
            => OpsSpatialCreationCatalog.Profiles.Single(value => value.Id == id);

        Equal(
            OpsValueKind.DestinationMap,
            Profile("observed.m0010.entry_type_2").Inputs[0].Kind);
        Equal(
            OpsValueKind.DestinationEntry,
            Profile("observed.m0010.entry_type_2").Inputs[1].Kind);
        Equal(
            OpsValueKind.ScriptFunction,
            Profile("observed.t1000.entry_event_trigger").Inputs.Single().Kind);
        Equal(
            OpsValueKind.MapSoundSource,
            Profile("observed.a0007.point_sound").Inputs.Single().Kind);
        var eventSelection = new SceneElementSelection(
            SceneElementKind.EntryVolume, 0, "EV_TEST");
        Equal(
            OpsValueKind.ScriptFunction,
            OpsAttributeValueKinds.Resolve(
                eventSelection,
                "name",
                new Dictionary<string, string>
                {
                    ["name"] = "EV_TEST",
                    ["next"] = string.Empty,
                }));
        Equal(
            OpsValueKind.DestinationMap,
            OpsAttributeValueKinds.Resolve(
                eventSelection,
                "next",
                new Dictionary<string, string> { ["next"] = "a1000" }));
        Equal(
            OpsValueKind.MapSoundSource,
            OpsAttributeValueKinds.Resolve(
                new SceneElementSelection(SceneElementKind.Sound, 0, "river"),
                "seName",
                new Dictionary<string, string> { ["seName"] = "river" }));
        Throws<ArgumentException>(() => document.AddSpatialElement(
            Profile("observed.m0010.entry_type_2"), Vector3.Zero, new Dictionary<string, string>()));
        var entry = document.AddSpatialElement(
            Profile("observed.m0010.entry_type_2"),
            new Vector3(1, 2, 3),
            new Dictionary<string, string> { ["next"] = "a1000", ["entry"] = "from_test" });
        document.AddSpatialElement(
            Profile("observed.c0010.group_box"), new Vector3(4, 5, 6), new Dictionary<string, string>());
        document.AddSpatialElement(
            Profile("observed.a0006.look_point_type_0"), new Vector3(7, 8, 9), new Dictionary<string, string>());
        document.AddSpatialElement(
            Profile("observed.a1700.map_camera_type_3"), new Vector3(8, 9, 10), new Dictionary<string, string>());
        document.AddSpatialElement(
            Profile("observed.a0007.point_sound"),
            new Vector3(10, 11, 12),
            new Dictionary<string, string> { ["seName"] = "se_test" });
        document.AddSpatialElement(
            Profile("observed.a0004.point_light_0x103"), new Vector3(13, 14, 15), new Dictionary<string, string>());
        Equal(true, document.IsDirty);
        Equal(true, document.Undo());
        Equal(0, document.CreateMapSnapshot()!.Lights.Count);
        Equal(true, document.Redo());
        Equal(1, document.CreateMapSnapshot()!.Lights.Count);
        Equal("go_a1000", entry.Name);

        new OpsWriter().Write(outputPath, source, document.CreateMapSnapshot()!);
        var reloaded = new OpsReader().Read(outputPath);
        var transition = reloaded.Volumes.Single(value => value.Kind == MapVolumeKind.Entry);
        Equal("a1000", transition.DestinationMap!);
        Equal("from_test", transition.DestinationEntry!);
        Equal("2", transition.SourceAttributes["entryType"]);
        Near(10f, transition.Transform.Scale.X);
        Equal("0x3", reloaded.Volumes.Single(value => value.Kind == MapVolumeKind.Group).SourceAttributes["flag"]);
        Equal("0", reloaded.Points[0].SourceAttributes["type"]);
        Equal("3", reloaded.Cameras[0].SourceAttributes["type"]);
        Near(-0.86f, reloaded.Cameras[0].LookAt.X - reloaded.Cameras[0].Eye.X);
        Near(-1f, reloaded.Cameras[0].LookAt.Y - reloaded.Cameras[0].Eye.Y);
        Near(-1.5f, reloaded.Cameras[0].LookAt.Z - reloaded.Cameras[0].Eye.Z);
        Equal("se_test", reloaded.Sounds[0].SoundName);
        Equal(MapSoundKind.Point, reloaded.Sounds[0].Kind);
        Equal("0x103", reloaded.Lights[0].SourceAttributes["flag"]);
    }
    finally
    {
        File.Delete(sourcePath);
        if (File.Exists(outputPath)) File.Delete(outputPath);
    }
}

static void IndexesPkgNamesWithoutReadingArchives()
{
    var root = Path.Combine(Path.GetTempPath(), $"ed8-catalog-{Guid.NewGuid():N}");
    try
    {
        var baseDirectory = Path.Combine(root, "asset", "D3D11");
        var englishDirectory = Path.Combine(root, "asset", "D3D11_us");
        Directory.CreateDirectory(baseDirectory);
        Directory.CreateDirectory(englishDirectory);
        File.WriteAllBytes(Path.Combine(baseDirectory, "O_CHAIR.pkg"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(englishDirectory, "O_CHAIR.pkg"), new byte[] { 4, 5 });
        File.WriteAllBytes(Path.Combine(baseDirectory, "O_TABLE.pkg"), new byte[] { 6 });
        var catalog = new GameAssetCatalog(root);
        Equal(2, catalog.Entries.Count);
        Equal(2, catalog.Entries.Single(value => value.AssetId == "O_CHAIR").Packages.Count);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static CpuModel CreateTriangleModel(string assetId, string sourceFormat)
{
    var vertices = new byte[36];
    WriteFloat(vertices, 0, -1f);
    WriteFloat(vertices, 4, -1f);
    WriteFloat(vertices, 8, 0f);
    WriteFloat(vertices, 12, 1f);
    WriteFloat(vertices, 16, -1f);
    WriteFloat(vertices, 20, 0f);
    WriteFloat(vertices, 24, 0f);
    WriteFloat(vertices, 28, 1f);
    WriteFloat(vertices, 32, 0f);
    var indices = new byte[6];
    BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan(0, 2), 0);
    BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan(2, 2), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan(4, 2), 2);
    var vertexBuffer = new CpuVertexBuffer(
        vertices,
        12,
        3,
        new[] { new CpuVertexAttribute(VertexSemantic.Position, 0, sourceFormat, 0) });
    var primitive = new CpuMeshPrimitive(
        new[] { vertexBuffer },
        new CpuIndexBuffer(indices, 2, 3),
        0,
        PrimitiveTopology.Triangles);
    return new CpuModel(
        assetId,
        new[] { new CpuMesh("triangle", Matrix4x4.Identity, new[] { primitive }) },
        Array.Empty<CpuMaterial>(),
        Array.Empty<CpuTexture>());

    static void WriteFloat(byte[] destination, int offset, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(destination.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));
}


static MemoryStream CreateHeader(
    string identifier,
    uint magic = ScriptHeaderReader.ExpectedMagic,
    bool terminateIdentifier = true,
    uint identifierOffset = 0x20)
{
    var identifierBytes = Encoding.ASCII.GetBytes(identifier);
    var length = checked((int)identifierOffset + identifierBytes.Length + 1);
    var bytes = new byte[length];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x00, 4), 0x20);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x04, 4), identifierOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x08, 4), 0x20);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x1c, 4), magic);
    identifierBytes.CopyTo(bytes, checked((int)identifierOffset));
    bytes[^1] = terminateIdentifier ? (byte)0 : (byte)'x';
    return new MemoryStream(bytes);
}

static void Equal<T>(T expected, T actual)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}

static void Throws<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static string WriteTemporaryOps(string contents)
{
    var path = Path.Combine(Path.GetTempPath(), $"ed8editor-{Guid.NewGuid():N}.ops");
    File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    return path;
}

static void Near(float expected, float actual, float tolerance = 0.00001f)
{
    if (MathF.Abs(expected - actual) > tolerance)
    {
        throw new InvalidOperationException($"Expected approximately {expected}, got {actual}.");
    }
}

static int ScanOpsCorpus(string opsDirectory)
{
    var reader = new OpsReader();
    var attributeCodec = new OpsSpatialAttributeCodec();
    var files = Directory.GetFiles(opsDirectory, "*.ops", SearchOption.TopDirectoryOnly);
    var props = 0;
    var failures = new List<string>();

    foreach (var file in files)
    {
        try
        {
            var scene = reader.Read(file);
            props += scene.Props.Count;
            foreach (var volume in scene.Volumes) attributeCodec.Apply(volume, volume.SourceAttributes);
            foreach (var point in scene.Points) attributeCodec.Apply(point, point.SourceAttributes);
            foreach (var camera in scene.Cameras) attributeCodec.Apply(camera, camera.SourceAttributes);
            foreach (var sound in scene.Sounds) attributeCodec.Apply(sound, sound.SourceAttributes);
            foreach (var light in scene.Lights) attributeCodec.Apply(light, light.SourceAttributes);
        }
        catch (Exception exception)
        {
            failures.Add($"{Path.GetFileName(file)}: {exception.Message}");
        }
    }

    Console.WriteLine($"OPS files : {files.Length}");
    Console.WriteLine($"Valid     : {files.Length - failures.Count}");
    Console.WriteLine($"Props     : {props}");
    Console.WriteLine($"Failures  : {failures.Count}");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine(failure);
    }

    return failures.Count == 0 ? 0 : 1;
}

static string CreateAssetTree(params string[] relativePackagePaths)
{
    var root = Path.Combine(Path.GetTempPath(), $"ed8editor-assets-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    foreach (var relativePath in relativePackagePaths)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
    }

    return root;
}

static int ScanAssetCorpus(string gameDataDirectory)
{
    var dataPath = Path.GetFullPath(gameDataDirectory);
    var opsReader = new OpsReader();
    var resolver = new GameAssetResolver(Path.Combine(dataPath, "asset"));
    var assetIds = Directory.GetFiles(Path.Combine(dataPath, "ops"), "*.ops")
        .SelectMany(path => opsReader.Read(path).Props)
        .Select(prop => prop.AssetId)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(assetId => assetId, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var resolutions = assetIds
        .Select(assetId => resolver.Resolve(assetId, AssetVariantPreference.English))
        .ToArray();

    Console.WriteLine($"Packages         : {resolver.PackageCount}");
    Console.WriteLine($"Unique packages  : {resolver.UniqueAssetCount}");
    Console.WriteLine($"Referenced IDs   : {assetIds.Length}");
    foreach (var status in Enum.GetValues<AssetResolutionStatus>())
    {
        Console.WriteLine($"{status,-16} : {resolutions.Count(value => value.Status == status)}");
    }

    foreach (var missing in resolutions.Where(value => value.Status == AssetResolutionStatus.Missing))
    {
        Console.WriteLine($"MISSING {missing.AssetId}");
    }

    foreach (var ambiguous in resolutions.Where(value => value.Status == AssetResolutionStatus.Ambiguous))
    {
        Console.WriteLine($"AMBIGUOUS {ambiguous.AssetId}: {ambiguous.Candidates.Count} candidates");
    }

    return resolutions.Any(value => value.Status == AssetResolutionStatus.Ambiguous) ? 1 : 0;
}

static string WriteTemporaryPackage(
    string entryName,
    byte[] storedData,
    uint uncompressedSize,
    PackageCompressionType compressionType)
{
    const int dataOffset = 88;
    var bytes = new byte[dataOffset + storedData.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x12345678);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 1);
    Encoding.ASCII.GetBytes(entryName).CopyTo(bytes, 8);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(72, 4), uncompressedSize);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76, 4), checked((uint)storedData.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80, 4), dataOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(84, 4), (uint)compressionType);
    storedData.CopyTo(bytes, dataOffset);

    var path = Path.Combine(Path.GetTempPath(), $"ed8editor-pkg-{Guid.NewGuid():N}.pkg");
    File.WriteAllBytes(path, bytes);
    return path;
}

static int ExtractPackage(string packagePath)
{
    var archive = new PkgArchiveReader().Read(packagePath);
    long totalBytes = 0;
    Console.WriteLine($"Package : {archive.SourcePath}");
    Console.WriteLine($"Magic   : 0x{archive.Magic:X8}");
    Console.WriteLine($"Entries : {archive.Entries.Count}");

    foreach (var entry in archive.Entries)
    {
        var data = archive.ReadEntry(entry);
        totalBytes += data.Length;
        Console.WriteLine(
            $"[{entry.Index}] {entry.Name}: {entry.StoredSize} -> {data.Length} bytes ({entry.CompressionType})");
    }

    Console.WriteLine($"Extracted bytes: {totalBytes}");
    return 0;
}

static int ScanManifestCorpus(string gameDataDirectory)
{
    var dataPath = Path.GetFullPath(gameDataDirectory);
    var opsReader = new OpsReader();
    var resolver = new GameAssetResolver(Path.Combine(dataPath, "asset"));
    var packageReader = new PkgArchiveReader();
    var manifestReader = new AssetManifestReader();
    var ids = Directory.GetFiles(Path.Combine(dataPath, "ops"), "*.ops")
        .SelectMany(path => opsReader.Read(path).Props)
        .Select(prop => prop.AssetId)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var loaded = 0;
    var missingPackages = 0;
    var fallbacks = new List<string>();
    var noPrimary = new List<string>();
    var errors = new List<string>();
    var externalResources = new List<string>();
    var resourceTypes = new Dictionary<string, int>(StringComparer.Ordinal);

    foreach (var id in ids)
    {
        var resolution = resolver.Resolve(id, AssetVariantPreference.English);
        if (resolution.SelectedPackage is null)
        {
            missingPackages++;
            continue;
        }

        try
        {
            var archive = packageReader.Read(resolution.SelectedPackage.Path);
            var manifest = manifestReader.Read(archive, id);
            loaded++;
            if (manifest.UsedSingleAssetFallback)
            {
                fallbacks.Add($"{id} -> {manifest.PrimaryAsset!.Symbol}");
            }

            if (manifest.PrimaryAsset is null)
            {
                noPrimary.Add(id);
            }

            foreach (var resource in manifest.Assets.SelectMany(asset => asset.Resources))
            {
                resourceTypes[resource.SourceType] = resourceTypes.TryGetValue(resource.SourceType, out var count)
                    ? count + 1
                    : 1;
                if (!resource.IsEmbedded)
                {
                    externalResources.Add($"{id}: {resource.Path}");
                }
            }
        }
        catch (Exception exception)
        {
            errors.Add($"{id}: {exception.Message}");
        }
    }

    Console.WriteLine($"Referenced IDs     : {ids.Length}");
    Console.WriteLine($"Loaded manifests   : {loaded}");
    Console.WriteLine($"Missing packages   : {missingPackages}");
    Console.WriteLine($"Manifest errors    : {errors.Count}");
    Console.WriteLine($"Symbol fallbacks   : {fallbacks.Count}");
    Console.WriteLine($"No primary symbol : {noPrimary.Count}");
    Console.WriteLine($"External resources : {externalResources.Count}");
    Console.WriteLine("Resource types:");
    foreach (var pair in resourceTypes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"  {pair.Key}: {pair.Value}");
    }

    foreach (var item in fallbacks)
    {
        Console.WriteLine($"FALLBACK {item}");
    }

    foreach (var item in noPrimary)
    {
        Console.WriteLine($"NO PRIMARY {item}");
    }

    foreach (var item in externalResources.Take(30))
    {
        Console.WriteLine($"EXTERNAL {item}");
    }

    foreach (var item in errors)
    {
        Console.WriteLine($"ERROR {item}");
    }

    return errors.Count == 0 && noPrimary.Count == 0 ? 0 : 1;
}
