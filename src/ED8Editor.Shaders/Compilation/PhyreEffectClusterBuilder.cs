using System.Text;
using ED8Editor.Core;
using ED8Editor.Phyre;
using ED8Editor.Phyre.Authoring;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11.Shader;

namespace ED8Editor.Shaders.Compilation;

/// <summary>
/// Builds a complete .fx.phyre cluster from HLSL source, automatically
/// reflecting on the compiled bytecode to generate correct
/// PShaderParameterDefinition entries.
/// </summary>
public sealed class PhyreEffectClusterBuilder
{
    private static readonly string[] ClassLayout =
    {
        "PAssetReference", "PEffect", "PEffectVariant", "PSceneRenderPass",
        "PShader", "PShaderPass", "PShaderParameterDefinition",
        "PShaderStreamDefinition", "PShaderVertexProgram",
        "PShaderFragmentProgram", "PStreamInputDescD3D11",
    };

    public byte[] Build(string hlslSource, string shaderAssetId, string outputFileName)
    {
        return BuildFromSources(hlslSource, hlslSource, shaderAssetId, outputFileName);
    }

    public byte[] BuildFromSources(string vsSource, string psSource, string shaderAssetId, string outputFileName)
    {
        return BuildFromSources(vsSource, psSource, shaderAssetId, outputFileName, null);
    }

    public byte[] BuildFromSources(string vsSource, string psSource, string shaderAssetId, string outputFileName, IReadOnlyList<ParamDefInfo>? predefinedParams = null)
    {
        // Compile vertex and fragment shaders from separate sources
        var vsBytecode = CompileHlsl(vsSource, "VSMain", "vs_5_0");
        var psBytecode = CompileHlsl(psSource, "PSMain", "ps_5_0");

        Console.WriteLine($"  VS: {vsBytecode.Length} bytes, PS: {psBytecode.Length} bytes");

        // Use predefined params if provided, otherwise reflect on bytecode
        var paramDefs = predefinedParams?.ToList()
            ?? ReflectParams(vsBytecode, psBytecode);
        Console.WriteLine($"  Parameters: {paramDefs.Count}");

        // Build the cluster
        return WriteCluster(shaderAssetId, outputFileName, vsBytecode, psBytecode, paramDefs);
    }

    private static List<ParamDefInfo> ReflectParams(byte[] vsBytecode, byte[] psBytecode)
    {
        using var vsRefl = Compiler.Reflect<ID3D11ShaderReflection>(vsBytecode);
        using var psRefl = Compiler.Reflect<ID3D11ShaderReflection>(psBytecode);
        return ExtractParameterDefinitions(vsRefl, psRefl);
    }

    private static List<ParamDefInfo> ExtractParameterDefinitions(
        ID3D11ShaderReflection vsRefl, ID3D11ShaderReflection psRefl)
    {
        var parms = new Dictionary<string, ParamDefInfo>(StringComparer.Ordinal);

        // Read from both VS and PS constant buffers (they should agree)
        foreach (var refl in new[] { vsRefl, psRefl })
        {
            foreach (var cb in refl.ConstantBuffers)
            {
                var cbDesc = cb.Description;
                foreach (var v in cb.Variables)
                {
                    var vd = v.Description;
                    if (!parms.ContainsKey(vd.Name))
                    {
                        parms[vd.Name] = new ParamDefInfo(
                            vd.Name, vd.StartOffset, vd.Size);
                    }
                }
            }
        }

        return parms.Values.OrderBy(p => p.Offset).ToList();
    }

    private static byte[] CompileHlsl(string source, string entryPoint, string profile)
    {
        var result = Compiler.Compile(
            shaderSource: source,
            defines: Array.Empty<ShaderMacro>(),
            include: null!,
            entryPoint: entryPoint,
            sourceName: $"{entryPoint}.hlsl",
            profile: profile,
            shaderFlags: ShaderFlags.OptimizationLevel3 | ShaderFlags.Debug,
            effectFlags: EffectFlags.None,
            out var blob,
            out var errors);
        using (blob)
        using (errors)
        {
            if (errors is not null && errors.BufferSize > 0)
            {
                var errMsg = System.Text.Encoding.ASCII.GetString(errors.AsSpan());
                throw new InvalidOperationException(
                    $"HLSL compilation failed for {entryPoint} ({profile}):\n{errMsg}");
            }
            result.CheckError();
            if (blob is null || blob.BufferSize == 0)
                throw new InvalidOperationException($"HLSL compilation produced no output for {entryPoint}");
            return blob.AsBytes();
        }
    }

    private static byte[] WriteCluster(
        string assetId, string fileName,
        byte[] vsBytecode, byte[] psBytecode,
        List<ParamDefInfo> paramDefs)
    {
        // Resolve class schema
        var wanted = new SortedSet<string>(ClassLayout, StringComparer.Ordinal);
        var queue = new Queue<string>(ClassLayout);
        while (queue.Count != 0)
        {
            foreach (var referenced in PhyreSchemaLibrary.Referenced(queue.Dequeue()))
                if (wanted.Add(referenced)) queue.Enqueue(referenced);
        }
        var derived = wanted.ToArray();
        var canonical = derived.All(PhyreSchemaLibrary.CanonicalClasses.Contains);
        var classes = canonical
            ? PhyreSchemaLibrary.CanonicalClasses.ToArray()
            : derived;
        var types = canonical
            ? PhyreSchemaLibrary.CanonicalTypes.ToArray()
            : PhyreSchemaLibrary.PrimitiveTypesFor(classes);
        var descriptors = PhyreSchemaLibrary.Descriptors(types, classes);

        int Group(string name) => Array.IndexOf(ClassLayout, name);
        PhyreDataMember Field(string className, string member)
        {
            var descriptor = descriptors.FirstOrDefault(value => value.Name == className)
                ?? throw new InvalidOperationException($"Class '{className}' not found in descriptors");
            var chain = PhyreObjectWriter.Chain(descriptor, descriptors).ToArray();
            return chain.FirstOrDefault(value => value.Name == member)
                ?? throw new InvalidOperationException(
                    $"Member '{member}' not found in class '{className}' (available: {string.Join(", ", chain.Select(m => m.Name))})");
        }

        var pointers = new List<PhyrePointerFixup>();
        var pointerArrays = new List<PhyreArrayFixup>();
        var arrays = new List<PhyreArrayFixup>();
        var users = new List<PhyreUserFixup>();
        var userOffset = 0u;

        uint User(string text, uint type)
        {
            var bytes = Encoding.ASCII.GetBytes(text + "\0");
            var id = (uint)users.Count;
            users.Add(new PhyreUserFixup((int)id, type, null, (uint)bytes.Length, userOffset, bytes, text));
            userOffset += (uint)bytes.Length;
            return id;
        }

        void Point(string from, uint fromId, string member, string to, uint toId)
            => pointers.Add(new PhyrePointerFixup(
                Group(from), fromId, (uint)Field(from, member).Index,
                (uint)Group(to), toId, 0, 0, null));

        void PointArray(string from, uint fromId, uint pointerOffset, string to, uint toId)
            => pointers.Add(new PhyrePointerFixup(
                Group(from), fromId, 0x80000000u | pointerOffset,
                (uint)Group(to), toId, 0, 1, null));

        void PointerArray(string from, uint fromId, uint pointerOffset, string to, uint toId)
        {
            var source = 0x80000000u | pointerOffset;
            pointerArrays.Add(new PhyreArrayFixup(Group(from), fromId, source, 1, 0));
            pointers.Add(new PhyrePointerFixup(
                Group(from), fromId, source, (uint)Group(to), toId, 0, 0, null));
        }

        // Wire up the effect graph
        Point("PAssetReference", 0, "m_asset", "PEffectVariant", 0);
        Point("PAssetReference", 0, "m_assetType", "PAssetReference", 0);
        Point("PEffectVariant", 0, "m_effect", "PEffect", 0);
        Point("PShaderPass", 0, "m_vertexProgram", "PShaderVertexProgram", 0);
        Point("PShaderPass", 0, "m_fragmentProgram", "PShaderFragmentProgram", 0);
        PointerArray("PEffect", 0, Field("PEffect", "m_effectVariants").ValueOffset + 4, "PEffectVariant", 0);
        PointArray("PEffectVariant", 0, Field("PEffectVariant", "m_sceneRenderPasses").ValueOffset + 4, "PSceneRenderPass", 0);
        PointerArray("PEffectVariant", 0, Field("PEffectVariant", "m_sceneRenderPassLookup").ValueOffset + 4, "PSceneRenderPass", 0);
        PointArray("PSceneRenderPass", 0, Field("PSceneRenderPass", "m_shaders").ValueOffset + 4, "PShader", 0);
        PointArray("PShader", 0, Field("PShader", "m_passes").ValueOffset + 4, "PShaderPass", 0);

        // Parameter definitions
        PointArray("PShader", 0, Field("PShader", "m_parameterDefinitionsForPasses").ValueOffset + 4, "PShaderParameterDefinition", 0);
        for (uint pi = 1; pi < paramDefs.Count; pi++)
        {
            pointerArrays.Add(new PhyreArrayFixup(Group("PShaderParameterDefinition"), pi, 0, 1, 0));
        }

        // Stream definitions
        PointArray("PShader", 0, Field("PShader", "m_streamDefinitionsForPasses").ValueOffset + 4, "PShaderStreamDefinition", 0);
        PointArray("PShaderVertexProgram", 0, 1252, "PStreamInputDescD3D11", 0);

        // User fixups for pass type and render type
        pointers.Add(new PhyrePointerFixup(
            Group("PSceneRenderPass"), 0,
            (uint)Field("PSceneRenderPass", "m_passType").Index,
            0, 0, 0, 0, User("Opaque", 5)));
        pointers.Add(new PhyrePointerFixup(
            Group("PShaderStreamDefinition"), 0,
            (uint)Field("PShaderStreamDefinition", "m_renderType").Index,
            0, 0, 0, 0, User("Vertex", 10)));

        // Build groups
        var groups = new List<PhyreGroupContents>();
        foreach (var className in ClassLayout)
        {
            var members = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var arrayData = new MemoryStream();

            void Set(string member, uint value)
            {
                var field = Field(className, member);
                var bytes = new byte[field.Size * Math.Max(field.FixedArraySize, 1)];
                if (bytes.Length == 1) bytes[0] = (byte)value;
                else if (bytes.Length == 2)
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value);
                else System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
                members[member] = bytes;
            }
            void Text(string member, string value)
            {
                var offset = (uint)arrayData.Length;
                var bytes = Encoding.ASCII.GetBytes(value + "\0");
                arrayData.Write(bytes);
                arrays.Add(new PhyreArrayFixup(
                    Group(className), 0, (uint)Field(className, member).Index, 0, offset));
                Set(member, (uint)bytes.Length);
            }
            void Bytecode(uint objectId, byte[] code)
            {
                Set("m_compiledCode", (uint)code.Length);
                var offset = (uint)arrayData.Length;
                arrayData.Write(code);
                arrays.Add(new PhyreArrayFixup(
                    Group(className), objectId,
                    0x80000000u | (Field(className, "m_compiledCode").ValueOffset + 4),
                    (uint)code.Length, offset));
            }

            switch (className)
            {
                case "PAssetReference":
                    Text("m_id", assetId);
                    break;
                case "PEffect":
                    Set("m_effectVariants", 1);
                    Set("m_contextSwitches", 0);
                    Set("m_contextVariantSwitches", 0);
                    break;
                case "PEffectVariant":
                    Set("m_sceneRenderPasses", 1);
                    Set("m_sceneRenderPassLookup", 1);
                    Set("m_switches", 0);
                    break;
                case "PSceneRenderPass":
                    Set("m_shaders", 1);
                    break;
                case "PShader":
                    Set("m_passes", 1);
                    Set("m_parameterDefinitionsForPasses", (uint)paramDefs.Count);
                    Set("m_streamDefinitionsForPasses", 1);
                    break;
                case "PShaderPass":
                    break;
                case "PShaderParameterDefinition":
                    // Handled below — multiple objects
                    break;
                case "PShaderStreamDefinition":
                    break;
                case "PShaderVertexProgram":
                    Set("m_constantBufferSize", 1024u);
                    Set("m_globalConstantBufferIndex", 0);
                    Bytecode(0, vsBytecode);
                    break;
                case "PShaderFragmentProgram":
                    Set("m_constantBufferSize", 1024u);
                    Set("m_globalConstantBufferIndex", 0);
                    Bytecode(0, psBytecode);
                    break;
                case "PStreamInputDescD3D11":
                    Text("m_semantic", "POSITION");
                    Set("m_semanticIndex", 0);
                    Set("m_d3dFormat", 2);
                    Set("m_inputSlot", 0);
                    break;
            }

            // Handle PShaderParameterDefinition with multiple objects
            if (className == "PShaderParameterDefinition")
            {
                var objects = new List<PhyreObjectContents>();
                for (var pi = 0; pi < paramDefs.Count; pi++)
                {
                    var pd = paramDefs[pi];
                    var pm = new Dictionary<string, byte[]>(StringComparer.Ordinal);

                    // m_name: array fixup, name goes into arrayData
                    var nameBytes = Encoding.ASCII.GetBytes(pd.Name + "\0");
                    var nameOff = (uint)arrayData.Length;
                    arrayData.Write(nameBytes);
                    arrays.Add(new PhyreArrayFixup(
                        Group(className), (uint)pi,
                        Field(className, "m_name").ValueOffset, 0, nameOff));

                    // m_arrayElementCount (ushort at +0)
                    var ac = new byte[2];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(ac, 1);
                    pm["m_arrayElementCount"] = ac;

                    // m_parameterType (byte at +2)
                    pm["m_parameterType"] = new byte[] { 64 };

                    // m_dataType (byte at +3) — infer from size
                    byte dtype = pd.Size switch { 4 => 0, 8 => 1, 12 => 2, 16 => 3, 64 => 3, _ => 0 };
                    pm["m_dataType"] = new byte[] { dtype };

                    // m_bufferLoc (uint at +8)
                    var bl = new byte[4];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bl, 0);
                    pm["m_bufferLoc"] = bl;

                    // m_constantBufferLocation (uint at +12)
                    var cbl = new byte[4];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(cbl, (uint)pd.Offset);
                    pm["m_constantBufferLocation"] = cbl;

                    objects.Add(new PhyreObjectContents(className, pm, ReadOnlyMemory<byte>.Empty));
                }

                groups.Add(new PhyreGroupContents(className, objects, arrayData.ToArray()));
            }
            else
            {
                groups.Add(new PhyreGroupContents(
                    className,
                    new[] { new PhyreObjectContents(className, members, ReadOnlyMemory<byte>.Empty) },
                    arrayData.ToArray()));
            }
        }

        // Assemble — follow PhyreMinimalEffectWriter pattern exactly
        return PhyreClusterAssembler.Assemble(new PhyreClusterContents(
            types, groups,
            new PhyreFixupSet(pointerArrays, pointers, arrays, users, 0),
            users, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty,
            new PhyreNamespaceWriter.UnmodelledHeader(0x1020304, 0x8D7, 0, 0),
            new byte[16]));
    }
}

public sealed record ParamDefInfo(string Name, int Offset, int Size);
