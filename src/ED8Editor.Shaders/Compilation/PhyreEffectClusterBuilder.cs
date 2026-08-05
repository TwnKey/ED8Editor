using System.Text;
using ED8Editor.Core;
using ED8Editor.Phyre;
using ED8Editor.Phyre.Authoring;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11.Shader;

using ED8Editor.Shaders.Forge;

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
        "PShaderParameterCaptureBufferLocation",
        "PShaderParameterCaptureBufferLocationTypeConstantBuffer",
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

        // Use predefined params if provided, otherwise read what the programs
        // themselves declare.
        var paramDefs = predefinedParams?.ToList()
            ?? Parameters(vsSource + Environment.NewLine + psSource, vsBytecode, psBytecode);
        var streams = Streams(vsBytecode);
        var locations = Locations(paramDefs, vsBytecode, psBytecode, out var runs);
        Console.WriteLine($"  Parameters: {paramDefs.Count}, streams: {streams.Count},"
            + $" locations: {locations.Count}");

        // Build the cluster
        return WriteCluster(
            shaderAssetId, outputFileName, vsBytecode, psBytecode,
            paramDefs, streams, locations, runs);
    }

    /// <summary>
    /// Every parameter the two programs take, as the effect has to declare them.
    ///
    /// Read through the same rules the shipped effects were measured against: the
    /// shape from the bytecode, the semantic from the HLSL the author wrote — D3D
    /// does not keep a uniform's semantic, so the source is the only place it
    /// survives — and a place in the capture buffer, because a parameter with none
    /// is one a material cannot write.
    /// </summary>
    private static List<ParamDefInfo> Parameters(
        string source, byte[] vsBytecode, byte[] psBytecode)
    {
        var declarations = Generation.Declarations(source);
        var found = new Dictionary<string, ParamDefInfo>(StringComparer.Ordinal);
        var textures = new List<ParamDefInfo>();

        foreach (var bytecode in new[] { vsBytecode, psBytecode })
        {
            var program = Reflection.Read(bytecode);
            foreach (var constant in program.Constants)
            {
                if (found.ContainsKey(constant.Name)) continue;
                var (dataType, size) = Generation.Shape(constant);
                found[constant.Name] = new ParamDefInfo(
                    constant.Name,
                    (int)constant.Offset,
                    (int)size,
                    Generation.SemanticOf(constant.Name, declarations, Semantic.Constant),
                    dataType);
            }
            foreach (var binding in program.Bindings.Where(one => one.Type is 2 or 3))
            {
                if (found.ContainsKey(binding.Name)) continue;
                var one = new ParamDefInfo(
                    binding.Name,
                    -1,
                    16,
                    Generation.SemanticOf(
                        binding.Name, declarations, Generation.ResourceFallback(binding)),
                    Generation.ResourceDataType);
                found[binding.Name] = one;
                textures.Add(one);
            }
        }

        // Where each one lands in the capture buffer: two pools, the matrices apart
        // from the rest, both starting at sixteen and packed largest first. Measured
        // on the effects the game ships, and internal to the effect — nothing
        // outside it reads the layout.
        var placed = new List<ParamDefInfo>();
        var matrices = 16u;
        var rest = 16u;
        foreach (var one in found.Values
                     .OrderByDescending(value => value.DataType == 49)
                     .ThenByDescending(value => value.Size)
                     .ThenBy(value => value.Name, StringComparer.Ordinal))
        {
            if (one.DataType == 49)
            {
                placed.Add(one with { Capture = matrices });
                matrices += (uint)one.Size;
            }
            else
            {
                placed.Add(one with { Capture = rest });
                rest += (uint)one.Size;
            }
        }
        return placed;
    }

    /// <summary>
    /// The vertex inputs the compiled program expects, named and shaped as the
    /// effect has to state them. The hash is the engine's own — that is how a
    /// mesh's data is matched to a shader's input, and a zero matches nothing.
    /// </summary>
    private static List<StreamInfo> Streams(byte[] vsBytecode)
    {
        var found = new List<StreamInfo>();
        var slot = 0u;
        foreach (var input in Reflection.Read(vsBytecode).Inputs)
        {
            // A system value is generated rather than fed, so no stream carries it.
            if (input.Name.StartsWith("SV_", StringComparison.OrdinalIgnoreCase)) continue;
            var components = Components(input.Mask);
            found.Add(new StreamInfo(
                input.Name,
                (int)input.Index,
                DataTypeOf(components),
                FormatOf(components),
                slot++,
                Semantic.Hash(input.Name)));
        }
        return found;
    }

    /// <summary>
    /// The copy plan, one entry per constant per program, and where each program's
    /// run of them begins.
    ///
    /// The entries are grouped by frequency — the scene's parameters, then the
    /// material's, then the node's, then the light's — because the pass states how
    /// many of each it has and the engine walks them in that order. A parameter's
    /// frequency is the block its semantic falls in, which is its value divided by
    /// sixty-four.
    /// </summary>
    private static List<LocationInfo> Locations(
        IReadOnlyList<ParamDefInfo> parameters,
        byte[] vsBytecode,
        byte[] psBytecode,
        out LocationRuns runs)
    {
        var byName = parameters.ToDictionary(one => one.Name, StringComparer.Ordinal);
        var found = new List<LocationInfo>();
        var starts = new List<(int Start, int Count, ushort[] Starts, ushort[] Counts)>();

        foreach (var bytecode in new[] { vsBytecode, psBytecode })
        {
            var program = Reflection.Read(bytecode);
            var begin = found.Count;
            var perFrequencyStart = new ushort[4];
            var perFrequencyCount = new ushort[4];

            var ordered = program.Constants
                .Where(one => byName.ContainsKey(one.Name))
                .Select(one => (Constant: one, Parameter: byName[one.Name]))
                .ToList();
            for (byte frequency = 0; frequency < 4; frequency++)
            {
                perFrequencyStart[frequency] = (ushort)(found.Count - begin);
                foreach (var (constant, parameter) in ordered
                             .Where(one => (byte)(one.Parameter.Semantic >> 6) == frequency)
                             .OrderBy(one => one.Constant.Offset))
                {
                    found.Add(new LocationInfo(
                        parameter.Capture, constant.Offset, (uint)parameter.Size, parameter.DataType));
                    perFrequencyCount[frequency]++;
                }
            }
            starts.Add((begin, found.Count - begin, perFrequencyStart, perFrequencyCount));
        }

        // Then the textures, once for each program, naming the register it binds at
        // or saying it binds nowhere.
        var textureRuns = new List<(int Start, int Count)>();
        foreach (var bytecode in new[] { vsBytecode, psBytecode })
        {
            var program = Reflection.Read(bytecode);
            var bound = program.Bindings
                .Where(one => one.Type is 2 or 3)
                .ToDictionary(one => one.Name, one => one.BindPoint, StringComparer.Ordinal);
            var begin = found.Count;
            foreach (var texture in parameters.Where(one => one.DataType == 52))
            {
                found.Add(new LocationInfo(
                    texture.Capture,
                    bound.TryGetValue(texture.Name, out var register) ? register : 0xFFFFFFFF,
                    0,
                    52));
            }
            textureRuns.Add((begin, found.Count - begin));
        }

        runs = new LocationRuns(
            starts[0].Start, starts[0].Count, starts[0].Starts, starts[0].Counts,
            starts[1].Start, starts[1].Count, starts[1].Starts, starts[1].Counts,
            textureRuns[0].Start, textureRuns[0].Count,
            textureRuns[1].Start, textureRuns[1].Count);
        return found;
    }

    /// <summary>
    /// How much room a material needs for this effect's parameters: past the last
    /// one placed, rounded up to sixteen as the engine rounds it.
    /// </summary>
    private static uint CaptureSize(IReadOnlyList<ParamDefInfo> parameters)
    {
        var end = 16u;
        foreach (var one in parameters)
        {
            end = Math.Max(end, one.Capture + (uint)one.Size);
        }
        return (end + 15) / 16 * 16;
    }

    /// <summary>How many components a signature's mask covers.</summary>
    private static int Components(uint mask)
    {
        var count = 0;
        for (var bit = 0; bit < 4; bit++)
        {
            if ((mask & (1u << bit)) != 0) count++;
        }
        return count == 0 ? 4 : count;
    }

    /// <summary>Phyre's own type id: FLOAT is 0, and each component adds one.</summary>
    private static byte DataTypeOf(int components) => (byte)Math.Clamp(components - 1, 0, 3);

    /// <summary>The same shape as DXGI names it.</summary>
    private static uint FormatOf(int components) => components switch
    {
        1 => 41,
        2 => 16,
        3 => 6,
        _ => 2,
    };

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
        List<ParamDefInfo> paramDefs,
        List<StreamInfo> streams,
        List<LocationInfo> locations,
        LocationRuns runs)
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
        PointArray("PShaderVertexProgram", 0,
            Field("PShaderVertexProgram", "m_inputLayout").ValueOffset + 4,
            "PStreamInputDescD3D11", 0);

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
                    Set("m_streamDefinitionsForPasses", (uint)streams.Count);
                    // What a material has to allocate to fill this shader in: the
                    // end of the capture buffer, rounded as the engine rounds it.
                    Set("m_parameterBufferSize", CaptureSize(paramDefs));
                    Set("m_parameterBufferFrequenciesRequired", 0xF);
                    break;
                case "PShaderPass":
                {
                    // Each of the four runs of the copy plan this pass walks. The
                    // block is one 24-byte member of the pass — four starts, four
                    // counts, then the array's own count and pointer — so it is
                    // written whole rather than field by field, and the pointer is
                    // left for the fixup to fill.
                    void Run(string member, int start, int count, ushort[] begins, ushort[] many)
                    {
                        var bytes = new byte[24];
                        for (var frequency = 0; frequency < 4; frequency++)
                        {
                            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                                bytes.AsSpan(frequency * 2), begins[frequency]);
                            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                                bytes.AsSpan(8 + frequency * 2), many[frequency]);
                        }
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                            bytes.AsSpan(16), (uint)count);
                        members[member] = bytes;
                        if (count == 0) return;
                        pointers.Add(new PhyrePointerFixup(
                            Group("PShaderPass"), 0,
                            0x80000000u | (Field("PShaderPass", member).ValueOffset + 16 + 4),
                            (uint)Group("PShaderParameterCaptureBufferLocationTypeConstantBuffer"),
                            (uint)start, 0, (uint)count, null));
                    }

                    // A texture run has no frequencies of its own: every entry is a
                    // material's, so it is one run rather than four.
                    var whole = new ushort[4];
                    var textures = (ushort)paramDefs.Count(one => one.DataType == 52);
                    Run("m_vertexParameterLocation", runs.VertexStart, runs.VertexCount,
                        runs.VertexStarts, runs.VertexCounts);
                    Run("m_fragmentParameterLocation", runs.FragmentStart, runs.FragmentCount,
                        runs.FragmentStarts, runs.FragmentCounts);
                    Run("m_vertexTexParameterLocation", runs.VertexTextureStart,
                        runs.VertexTextureCount, whole, new ushort[] { 0, textures, 0, 0 });
                    Run("m_fragmentTexParameterLocation", runs.FragmentTextureStart,
                        runs.FragmentTextureCount, whole, new ushort[] { 0, textures, 0, 0 });

                    if (streams.Count != 0)
                    {
                        Set("m_streamLocations", (uint)streams.Count);
                        pointers.Add(new PhyrePointerFixup(
                            Group("PShaderPass"), 0,
                            0x80000000u | (Field("PShaderPass", "m_streamLocations").ValueOffset + 4),
                            (uint)Group("PShaderParameterCaptureBufferLocation"),
                            0, 0, (uint)streams.Count, null));
                    }
                    break;
                }
                case "PShaderParameterDefinition":
                    // Handled below — multiple objects
                    break;
                case "PShaderStreamDefinition":
                    break;
                case "PShaderVertexProgram":
                    // What the program itself says its globals measure, rather than
                    // a round number: the engine copies exactly this many bytes.
                    Set("m_constantBufferSize", Reflection.Read(vsBytecode).BufferSize);
                    Set("m_globalConstantBufferIndex", 0xFFFFFFFF);
                    Bytecode(0, vsBytecode);
                    break;
                case "PShaderFragmentProgram":
                    Set("m_constantBufferSize", Reflection.Read(psBytecode).BufferSize);
                    Set("m_globalConstantBufferIndex", 0xFFFFFFFF);
                    Bytecode(0, psBytecode);
                    break;

            }

            // The tables that carry one object per parameter or per stream.
            if (className == "PShaderParameterDefinition")
            {
                var objects = new List<PhyreObjectContents>();
                for (var pi = 0; pi < paramDefs.Count; pi++)
                {
                    var pd = paramDefs[pi];
                    var pm = new Dictionary<string, byte[]>(StringComparer.Ordinal);

                    // The name, and the fixup that finds it. The high bit says the
                    // source is a raw offset: our own reader takes either form, the
                    // engine takes only this one, so a name written without it is a
                    // name the game never sees.
                    var nameOff = (uint)arrayData.Length;
                    arrayData.Write(Encoding.ASCII.GetBytes(pd.Name + " "));
                    if (arrayData.Length % 2 != 0) arrayData.WriteByte(0);
                    arrays.Add(new PhyreArrayFixup(
                        Group(className), (uint)pi,
                        0x80000000u | Field(className, "m_name").ValueOffset, 0, nameOff));

                    // Zero, as every shipped effect writes it: these are single
                    // values, not arrays of them.
                    pm["m_arrayElementCount"] = new byte[2];
                    pm["m_parameterType"] = new[] { pd.Semantic };
                    pm["m_dataType"] = new[] { pd.DataType };

                    // Where the material writes it: the size above the offset. Zero
                    // here would leave the material with nowhere to put the value.
                    var loc = new byte[4];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                        loc, ((uint)pd.Size << 16) | (pd.Capture & 0xFFFF));
                    pm["m_bufferLoc"] = loc;

                    // A texture has no place in the constant buffer, and says so.
                    var cbl = new byte[4];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                        cbl, pd.Offset < 0 ? 0xFFFFFFFFu : (uint)pd.Offset);
                    pm["m_constantBufferLocation"] = cbl;

                    objects.Add(new PhyreObjectContents(className, pm, ReadOnlyMemory<byte>.Empty));
                }
                groups.Add(new PhyreGroupContents(className, objects, arrayData.ToArray()));
            }
            else if (className == "PShaderStreamDefinition")
            {
                var objects = new List<PhyreObjectContents>();
                for (var si = 0; si < streams.Count; si++)
                {
                    var stream = streams[si];
                    var sm = new Dictionary<string, byte[]>(StringComparer.Ordinal);

                    var nameOff = (uint)arrayData.Length;
                    arrayData.Write(Encoding.ASCII.GetBytes(stream.Semantic + " "));
                    if (arrayData.Length % 2 != 0) arrayData.WriteByte(0);
                    arrays.Add(new PhyreArrayFixup(
                        Group(className), (uint)si,
                        0x80000000u | Field(className, "m_name").ValueOffset, 0, nameOff));

                    // What binds a mesh's data to this input. The engine matches on
                    // the hash, so a zero matches nothing at all.
                    var hash = new byte[2];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                        hash, stream.NameHash);
                    sm["m_nameHash"] = hash;
                    sm["m_dataType"] = new[] { stream.DataType };
                    sm["m_index"] = new[] { (byte)stream.Index };

                    objects.Add(new PhyreObjectContents(className, sm, ReadOnlyMemory<byte>.Empty));
                    pointers.Add(new PhyrePointerFixup(
                        Group(className), (uint)si,
                        (uint)Field(className, "m_renderType").Index,
                        0, 0, 0, 0, User(stream.Semantic, 10)));
                }
                groups.Add(new PhyreGroupContents(className, objects, arrayData.ToArray()));
            }
            else if (className == "PShaderParameterCaptureBufferLocationTypeConstantBuffer")
            {
                // The plan the engine copies a material's values by: for each
                // program, one entry per constant saying where it reads from in the
                // capture buffer and where it writes to in that program's own
                // constant buffer. Without these the parameters never arrive.
                var objects = new List<PhyreObjectContents>();
                foreach (var entry in locations)
                {
                    var lm = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                    var offset = new byte[2];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                        offset, (ushort)entry.Capture);
                    lm["m_offset"] = offset;
                    var where = new byte[4];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(where, entry.Where);
                    lm["m_constantBufferLocation"] = where;
                    var size = new byte[4];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(size, entry.Size);
                    lm["m_size"] = size;
                    lm["m_type"] = new[] { entry.Type };
                    objects.Add(new PhyreObjectContents(className, lm, ReadOnlyMemory<byte>.Empty));
                }
                groups.Add(new PhyreGroupContents(className, objects, ReadOnlyMemory<byte>.Empty));
            }
            else if (className == "PShaderParameterCaptureBufferLocation")
            {
                // Where each vertex stream reads from, in the same buffer.
                var objects = new List<PhyreObjectContents>();
                for (var si = 0; si < streams.Count; si++)
                {
                    var lm = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                    var offset = new byte[2];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                        offset, (ushort)(16 + si * 16));
                    lm["m_offset"] = offset;
                    objects.Add(new PhyreObjectContents(className, lm, ReadOnlyMemory<byte>.Empty));
                }
                groups.Add(new PhyreGroupContents(className, objects, ReadOnlyMemory<byte>.Empty));
            }
            else if (className == "PStreamInputDescD3D11")
            {
                var objects = new List<PhyreObjectContents>();
                for (var si = 0; si < streams.Count; si++)
                {
                    var stream = streams[si];
                    var dm = new Dictionary<string, byte[]>(StringComparer.Ordinal);

                    var nameOff = (uint)arrayData.Length;
                    arrayData.Write(Encoding.ASCII.GetBytes(stream.Semantic + " "));
                    if (arrayData.Length % 2 != 0) arrayData.WriteByte(0);
                    arrays.Add(new PhyreArrayFixup(
                        Group(className), (uint)si,
                        0x80000000u | Field(className, "m_semantic").ValueOffset, 0, nameOff));

                    void Put(string member, uint value)
                    {
                        var field = Field(className, member);
                        var bytes = new byte[field.Size * Math.Max(field.FixedArraySize, 1)];
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
                        dm[member] = bytes;
                    }
                    Put("m_semanticIndex", (uint)stream.Index);
                    Put("m_d3dFormat", stream.Format);
                    Put("m_inputSlot", stream.Slot);

                    objects.Add(new PhyreObjectContents(className, dm, ReadOnlyMemory<byte>.Empty));
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

/// <summary>
/// One parameter the effect will declare.
/// </summary>
/// <param name="Offset">Where it sits in the program's own constant buffer.</param>
/// <param name="Semantic">
/// What the engine gives it, resolved from the HLSL semantic the author wrote.
/// A name nothing recognises falls to the material, which is what lets a shader
/// with invented uniforms be filled in at all.
/// </param>
/// <param name="DataType">
/// Its shape as Phyre counts them: 0 a float, 1/2/3 a vector, 8 an integer, 49 a
/// matrix, 52 a texture or a sampler.
/// </param>
/// <param name="Capture">
/// Where a material writes it, in the effect's own capture buffer. Zero would
/// mean the material has nowhere to put the value.
/// </param>
public sealed record ParamDefInfo(
    string Name,
    int Offset,
    int Size,
    byte Semantic = 64,
    byte DataType = 0,
    uint Capture = 0);

/// <summary>One vertex input the compiled program expects.</summary>
/// <param name="DataType">Phyre's own type id: FLOAT2 is 1, FLOAT3 2, FLOAT4 3.</param>
/// <param name="Format">
/// The same shape as DXGI counts it: R32G32 is 16, R32G32B32 6, R32G32B32A32 2.
/// Measured against PhyreGlow, whose position is three components and whose
/// texture coordinate is two, and against a map shader whose position is four.
/// </param>
/// <summary>One line of a program's copy plan.</summary>
/// <param name="Where">
/// Where it lands in the program's constant buffer, or the register a texture
/// binds at — and the unbound marker when the program never uses it.
/// </param>
public sealed record LocationInfo(uint Capture, uint Where, uint Size, byte Type);

/// <summary>Where each program's run of the copy plan begins, and how long it is.</summary>
public sealed record LocationRuns(
    int VertexStart, int VertexCount, ushort[] VertexStarts, ushort[] VertexCounts,
    int FragmentStart, int FragmentCount, ushort[] FragmentStarts, ushort[] FragmentCounts,
    int VertexTextureStart, int VertexTextureCount,
    int FragmentTextureStart, int FragmentTextureCount);

public sealed record StreamInfo(
    string Semantic, int Index, byte DataType, uint Format, uint Slot, ushort NameHash);
