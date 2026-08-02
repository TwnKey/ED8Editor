using System.Buffers.Binary;
using System.Text;
using ED8Editor.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// Authors the complete effect used by an imported map.  It is deliberately
/// small: position in, WorldViewProjection supplied by Phyre, one opaque colour
/// out.  No game package, effect variant or material layout is used as a source.
/// </summary>
public static class PhyreMinimalEffectWriter
{
    public const string FileName = "ed8editor_minimal.fx.phyre";
    public const string AssetId = "shaders/ed8editor_minimal.fx";

    private const string Source = """
        cbuffer PhyreGlobals : register(b0)
        {
            float4 _phyreReserved[29];
            float4x4 WorldViewProjection;
        };

        struct VertexInput
        {
            float3 Position : POSITION;
        };

        struct VertexOutput
        {
            float4 Position : SV_Position;
        };

        VertexOutput VSMain(VertexInput input)
        {
            VertexOutput output;
            output.Position = mul(float4(input.Position, 1.0f), WorldViewProjection);
            return output;
        }

        float4 PSMain(VertexOutput input) : SV_Target
        {
            return float4(0.72f, 0.74f, 0.78f, 1.0f);
        }
        """;

    private static readonly string[] Layout =
    {
        "PAssetReference", "PEffect", "PEffectVariant", "PSceneRenderPass",
        "PShader", "PShaderPass", "PShaderParameterDefinition",
        "PShaderStreamDefinition", "PShaderVertexProgram",
        "PShaderFragmentProgram", "PStreamInputDescD3D11",
    };

    public static byte[] Write()
    {
        var vertex = Compile("VSMain", "vs_5_0");
        var fragment = Compile("PSMain", "ps_5_0");
        var wanted = new SortedSet<string>(Layout, StringComparer.Ordinal);
        var queue = new Queue<string>(Layout);
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

        int Group(string name) => Array.IndexOf(Layout, name);
        PhyreDataMember Field(string className, string member)
        {
            var descriptor = descriptors.First(value => value.Name == className);
            return PhyreObjectWriter.Chain(descriptor, descriptors)
                .First(value => value.Name == member);
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
            users.Add(new PhyreUserFixup(
                (int)id, type, null, (uint)bytes.Length, userOffset, bytes, text));
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
            pointerArrays.Add(new PhyreArrayFixup(
                Group(from), fromId, source, 1, 0));
            pointers.Add(new PhyrePointerFixup(
                Group(from), fromId, source,
                (uint)Group(to), toId, 0, 0, null));
        }

        Point("PAssetReference", 0, "m_asset", "PEffectVariant", 0);
        Point("PAssetReference", 0, "m_assetType", "PAssetReference", 0);
        Point("PEffectVariant", 0, "m_effect", "PEffect", 0);
        Point("PShaderPass", 0, "m_vertexProgram", "PShaderVertexProgram", 0);
        Point("PShaderPass", 0, "m_fragmentProgram", "PShaderFragmentProgram", 0);

        PointerArray("PEffect", 0, Field("PEffect", "m_effectVariants").ValueOffset + 4,
            "PEffectVariant", 0);
        PointArray("PEffectVariant", 0,
            Field("PEffectVariant", "m_sceneRenderPasses").ValueOffset + 4,
            "PSceneRenderPass", 0);
        PointerArray("PEffectVariant", 0,
            Field("PEffectVariant", "m_sceneRenderPassLookup").ValueOffset + 4,
            "PSceneRenderPass", 0);
        PointArray("PSceneRenderPass", 0,
            Field("PSceneRenderPass", "m_shaders").ValueOffset + 4,
            "PShader", 0);
        PointArray("PShader", 0, Field("PShader", "m_passes").ValueOffset + 4,
            "PShaderPass", 0);
        PointArray("PShader", 0,
            Field("PShader", "m_parameterDefinitionsForPasses").ValueOffset + 4,
            "PShaderParameterDefinition", 0);
        PointArray("PShader", 0,
            Field("PShader", "m_streamDefinitionsForPasses").ValueOffset + 4,
            "PShaderStreamDefinition", 0);
        PointArray("PShaderVertexProgram", 0, 1252, "PStreamInputDescD3D11", 0);

        pointers.Add(new PhyrePointerFixup(
            Group("PSceneRenderPass"), 0,
            (uint)Field("PSceneRenderPass", "m_passType").Index,
            0, 0, 0, 0, User("Opaque", 5)));
        pointers.Add(new PhyrePointerFixup(
            Group("PShaderStreamDefinition"), 0,
            (uint)Field("PShaderStreamDefinition", "m_renderType").Index,
            0, 0, 0, 0, User("Vertex", 10)));

        var groups = new List<PhyreGroupContents>();
        foreach (var className in Layout)
        {
            var members = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var arrayData = new MemoryStream();
            void Set(string member, uint value)
            {
                var field = Field(className, member);
                var bytes = new byte[field.Size * Math.Max(field.FixedArraySize, 1)];
                if (bytes.Length == 1) bytes[0] = (byte)value;
                else if (bytes.Length == 2)
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value);
                else BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
                members[member] = bytes;
            }
            void Text(string member, string value)
            {
                var offset = (uint)arrayData.Length;
                var bytes = Encoding.ASCII.GetBytes(value + "\0");
                arrayData.Write(bytes);
                arrays.Add(new PhyreArrayFixup(
                    Group(className), 0, (uint)Field(className, member).Index, 0, offset));
            }
            void Bytecode(byte[] code)
            {
                Set("m_compiledCode", (uint)code.Length);
                var offset = (uint)arrayData.Length;
                arrayData.Write(code);
                arrays.Add(new PhyreArrayFixup(
                    Group(className), 0,
                    0x80000000u | (Field(className, "m_compiledCode").ValueOffset + 4),
                    (uint)code.Length, offset));
            }

            switch (className)
            {
                case "PAssetReference":
                    // Cluster paths end in .phyre; exported Phyre asset IDs do
                    // not.  Native effects use e.g. file ed8.fx#HASH.phyre and
                    // export shaders/ed8.fx#HASH.  Imports resolve this ID.
                    Text("m_id", AssetId);
                    break;
                case "PEffect":
                    Set("m_effectVariants", 1);
                    Set("m_numSupportedShaderLODLevels", 1);
                    break;
                case "PEffectVariant":
                    Set("m_sceneRenderPasses", 1);
                    Set("m_sceneRenderPassLookup", 1);
                    Set("m_largestShaderPassCount", 1);
                    break;
                case "PSceneRenderPass":
                    Set("m_shaders", 1);
                    break;
                case "PShader":
                    Set("m_passes", 1);
                    Set("m_parameterDefinitionsForPasses", 1);
                    Set("m_streamDefinitionsForPasses", 1);
                    Set("m_parameterBufferSize", 528);
                    break;
                case "PShaderPass":
                    members["m_state"] = OpaqueState();
                    break;
                case "PShaderParameterDefinition":
                    // The exact Phyre definition of the engine-owned
                    // WorldViewProjection matrix: 64 bytes at cb0 + 464.
                    members["m_arrayElementCount"] = new byte[] { 0, 0 };
                    Set("m_parameterType", 0x84);
                    Set("m_dataType", 0x31);
                    members["m_bufferLoc"] = new byte[] { 0x10, 0x00, 0x40, 0x00 };
                    Set("m_constantBufferLocation", 464);
                    Text("m_name", "WorldViewProjection");
                    break;
                case "PShaderStreamDefinition":
                    Set("m_dataType", 2);
                    Text("m_name", "POSITION");
                    break;
                case "PShaderVertexProgram":
                    Bytecode(vertex);
                    Set("m_constantBufferSize", 528);
                    Set("m_globalConstantBufferIndex", 0);
                    Set("m_shaderProfile", ShaderProfile("vs50"));
                    members["m_inputLayout"] = BitConverter.GetBytes(1u)
                        .Concat(new byte[8]).ToArray();
                    break;
                case "PShaderFragmentProgram":
                    Bytecode(fragment);
                    Set("m_constantBufferSize", 0);
                    Set("m_globalConstantBufferIndex", uint.MaxValue);
                    Set("m_shaderProfile", ShaderProfile("ps50"));
                    break;
                case "PStreamInputDescD3D11":
                    Text("m_semantic", "POSITION");
                    Set("m_semanticIndex", 0);
                    Set("m_d3dFormat", 2);
                    Set("m_inputSlot", 0);
                    break;
            }

            groups.Add(new PhyreGroupContents(
                className,
                new[] { new PhyreObjectContents(className, members, ReadOnlyMemory<byte>.Empty) },
                arrayData.ToArray()));
        }

        return PhyreClusterAssembler.Assemble(new PhyreClusterContents(
            types, groups,
            new PhyreFixupSet(pointerArrays, pointers, arrays, users, 0),
            users, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty,
            new PhyreNamespaceWriter.UnmodelledHeader(0x1020304, 0x8D7, 0, 0),
            new byte[16]));
    }

    private static byte[] Compile(string entryPoint, string profile)
    {
        var result = Compiler.Compile(
            Source, Array.Empty<ShaderMacro>(), null!, entryPoint,
            "ED8Editor.MinimalMap.fx", profile, ShaderFlags.OptimizationLevel3,
            EffectFlags.None, out var blob, out var errors);
        using (blob)
        using (errors)
        {
            result.CheckError();
            return blob.AsBytes();
        }
    }

    private static byte[] OpaqueState()
    {
        var state = new byte[380];
        Write(state, 4, 3);       // D3D11_FILL_SOLID
        Write(state, 8, 3);       // D3D11_CULL_BACK
        Write(state, 28, 1);      // DepthClipEnable
        Write(state, 44, 1);      // DepthEnable
        Write(state, 48, 1);      // D3D11_DEPTH_WRITE_MASK_ALL
        Write(state, 52, 4);      // D3D11_COMPARISON_LESS_EQUAL
        state[96 + 8 + 28] = 0x0F; // all colour channels
        return state;
    }

    private static uint ShaderProfile(string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        // Phyre stores the four profile characters in display order inside the
        // integer (native vs_5_0 assets state 0x76733530 for "vs50").
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static void Write(byte[] bytes, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
