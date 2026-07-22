using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using ED8Editor.Core;

namespace ED8Editor.Phyre;

public sealed class PhyreEffectRenderPassReader
{
    private static readonly ConcurrentDictionary<string, PhyreEffectMetadata> Cache = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, CpuRenderPassState> Read(ReadOnlyMemory<byte> data)
        => ReadMetadata(data).RenderPassStates;

    public PhyreEffectMetadata ReadMetadata(ReadOnlyMemory<byte> data)
    {
        var cacheKey = Convert.ToHexString(SHA256.HashData(data.Span));
        if (Cache.TryGetValue(cacheKey, out var cached)) return cached;
        var cluster = new PhyreClusterReader().Read(data);
        var scenePassGroup = FindGroup(cluster, "PSceneRenderPass");
        var effectGroup = FindGroup(cluster, "PEffect");
        var variantGroup = FindGroup(cluster, "PEffectVariant");
        var shaderGroup = FindGroup(cluster, "PShader");
        var shaderPassGroup = FindGroup(cluster, "PShaderPass");
        var passTypeMember = FindRequiredMember(cluster, "PSceneRenderPass", "m_passType");
        var variantScenePassesMember = FindRequiredMember(cluster, "PEffectVariant", "m_sceneRenderPasses");
        var scenePassShadersMember = FindRequiredMember(cluster, "PSceneRenderPass", "m_shaders");
        var shaderPassesMember = FindRequiredMember(cluster, "PShader", "m_passes");
        var shaderPassStateMember = FindRequiredMember(cluster, "PShaderPassD3D11", "m_state");
        var rasterDescriptionMember = FindRequiredMember(cluster, "PShaderPassStateD3D11", "m_rasterDesc");
        var blendDescriptionMember = FindRequiredMember(cluster, "PShaderPassStateD3D11", "m_blendDesc");
        var renderTargetsMember = FindRequiredMember(cluster, "CD3D11_BLEND_DESC", "RenderTarget");
        var fillModeMember = FindRequiredMember(cluster, "CD3D11_RASTERIZER_DESC", "FillMode");
        var cullModeMember = FindRequiredMember(cluster, "CD3D11_RASTERIZER_DESC", "CullMode");
        var frontCounterClockwiseMember = FindRequiredMember(cluster, "CD3D11_RASTERIZER_DESC", "FrontCounterClockwise");
        var depthBiasMember = FindRequiredMember(cluster, "CD3D11_RASTERIZER_DESC", "DepthBias");
        var depthBiasClampMember = FindRequiredMember(cluster, "CD3D11_RASTERIZER_DESC", "DepthBiasClamp");
        var slopeScaledDepthBiasMember = FindRequiredMember(cluster, "CD3D11_RASTERIZER_DESC", "SlopeScaledDepthBias");
        var depthClipMember = FindRequiredMember(cluster, "CD3D11_RASTERIZER_DESC", "DepthClipEnable");
        var scissorMember = FindRequiredMember(cluster, "CD3D11_RASTERIZER_DESC", "ScissorEnable");
        var multisampleMember = FindRequiredMember(cluster, "CD3D11_RASTERIZER_DESC", "MultisampleEnable");
        var antialiasedLineMember = FindRequiredMember(cluster, "CD3D11_RASTERIZER_DESC", "AntialiasedLineEnable");
        var states = new Dictionary<string, CpuRenderPassState>(StringComparer.Ordinal);
        for (uint scenePassId = 0; scenePassId < scenePassGroup.Group.Count; scenePassId++)
        {
            var passType = FindPassType(cluster, scenePassGroup.Index, scenePassId, passTypeMember.Index);
            if (string.IsNullOrEmpty(passType)) continue;
            var shaderPointer = RequireArrayPointer(cluster, scenePassGroup.Index, scenePassId, scenePassShadersMember);
            if (shaderPointer.DestinationListIndex != shaderGroup.Index) continue;
            var shaderPassPointer = RequireArrayPointer(cluster, shaderGroup.Index, shaderPointer.DestinationObjectId, shaderPassesMember);
            if (shaderPassPointer.DestinationListIndex != shaderPassGroup.Index) continue;
            var shaderPass = cluster.GetObject(shaderPassGroup.Index, shaderPassPointer.DestinationObjectId).Span;
            states[passType] = ReadPassState(
                shaderPass,
                checked((int)(shaderPassStateMember.ValueOffset
                    + blendDescriptionMember.ValueOffset
                    + renderTargetsMember.ValueOffset)),
                checked((int)(shaderPassStateMember.ValueOffset + rasterDescriptionMember.ValueOffset)),
                checked((int)fillModeMember.ValueOffset),
                checked((int)cullModeMember.ValueOffset),
                checked((int)frontCounterClockwiseMember.ValueOffset),
                checked((int)depthBiasMember.ValueOffset),
                checked((int)depthBiasClampMember.ValueOffset),
                checked((int)slopeScaledDepthBiasMember.ValueOffset),
                checked((int)depthClipMember.ValueOffset),
                checked((int)scissorMember.ValueOffset),
                checked((int)multisampleMember.ValueOffset),
                checked((int)antialiasedLineMember.ValueOffset));
        }
        var result = new PhyreEffectMetadata(
            states,
            ReadMaterialSwitches(cluster),
            ReadDefaultRenderPassName(
                cluster,
                variantGroup,
                scenePassGroup,
                variantScenePassesMember,
                passTypeMember.Index),
            ReadEffectProgram(cluster, effectGroup, scenePassGroup, shaderGroup, shaderPassGroup, passTypeMember));
        Cache.TryAdd(cacheKey, result);
        return result;
    }

    private static CpuRenderPassState ReadPassState(
        ReadOnlySpan<byte> shaderPass,
        int blend,
        int rasterizer,
        int fillMode,
        int cullMode,
        int frontCounterClockwise,
        int depthBias,
        int depthBiasClamp,
        int slopeScaledDepthBias,
        int depthClip,
        int scissor,
        int multisample,
        int antialiasedLine)
    {
        Ensure(shaderPass, blend + 32);
        Ensure(shaderPass, rasterizer + new[]
        {
            fillMode, cullMode, frontCounterClockwise, depthBias, depthBiasClamp,
            slopeScaledDepthBias, depthClip, scissor, multisample, antialiasedLine,
        }.Max() + sizeof(int));
        return new CpuRenderPassState(
            ReadInt(shaderPass, blend) != 0,
            ReadInt(shaderPass, blend + 4),
            ReadInt(shaderPass, blend + 8),
            ReadInt(shaderPass, blend + 12),
            ReadInt(shaderPass, blend + 16),
            ReadInt(shaderPass, blend + 20),
            ReadInt(shaderPass, blend + 24),
            shaderPass[blend + 28],
            new CpuRasterizerState(
                ReadInt(shaderPass, rasterizer + fillMode),
                ReadInt(shaderPass, rasterizer + cullMode),
                ReadInt(shaderPass, rasterizer + frontCounterClockwise) != 0,
                ReadInt(shaderPass, rasterizer + depthBias),
                ReadFloat(shaderPass, rasterizer + depthBiasClamp),
                ReadFloat(shaderPass, rasterizer + slopeScaledDepthBias),
                ReadInt(shaderPass, rasterizer + depthClip) != 0,
                ReadInt(shaderPass, rasterizer + scissor) != 0,
                ReadInt(shaderPass, rasterizer + multisample) != 0,
                ReadInt(shaderPass, rasterizer + antialiasedLine) != 0));
    }

    private static IReadOnlyDictionary<string, string> ReadMaterialSwitches(PhyreClusterData cluster)
    {
        var variantGroup = FindGroup(cluster, "PEffectVariant");
        if (variantGroup.Group.Count != 1)
            throw new InvalidPhyreException("An effect asset must contain exactly one effect variant.");
        var switchGroup = FindOptionalGroup(cluster, "PMaterialSwitch");
        var switchesMember = FindRequiredMember(cluster, "PEffectVariant", "m_switches");
        var nameMember = FindRequiredMember(cluster, "PMaterialSwitch", "m_name");
        var valueMember = FindRequiredMember(cluster, "PMaterialSwitch", "m_value");
        var variant = cluster.GetObject(variantGroup.Index, 0).Span;
        var switchesOffset = checked((int)switchesMember.ValueOffset);
        Ensure(variant, switchesOffset + sizeof(uint));
        var count = ReadUInt(variant, switchesOffset, cluster.Metadata.IsBigEndian);
        if (count == 0) return new Dictionary<string, string>(StringComparer.Ordinal);
        if (switchGroup is null)
            throw new InvalidPhyreException("Effect variant declares material switches but has no switch group.");
        var pointer = RequireArrayPointer(cluster, variantGroup.Index, 0, switchesMember);
        if (pointer.DestinationListIndex != switchGroup.Value.Index
            || (ulong)pointer.DestinationObjectId + count > switchGroup.Value.Group.Count)
        {
            throw new InvalidPhyreException("Effect variant material switches have an invalid destination.");
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (uint index = 0; index < count; index++)
        {
            var objectId = checked(pointer.DestinationObjectId + index);
            result.Add(
                ReadString(cluster, switchGroup.Value.Index, objectId, nameMember),
                ReadString(cluster, switchGroup.Value.Index, objectId, valueMember));
        }
        return result;
    }

    private static string? ReadDefaultRenderPassName(
        PhyreClusterData cluster,
        (int Index, PhyreInstanceGroup Group) variantGroup,
        (int Index, PhyreInstanceGroup Group) scenePassGroup,
        PhyreDataMember scenePassesMember,
        int passTypeMemberIndex)
    {
        if (variantGroup.Group.Count != 1)
            throw new InvalidPhyreException("An effect asset must contain exactly one effect variant.");
        var variant = cluster.GetObject(variantGroup.Index, 0).Span;
        var countOffset = checked((int)scenePassesMember.ValueOffset);
        Ensure(variant, countOffset + sizeof(uint));
        var count = ReadUInt(variant, countOffset, cluster.Metadata.IsBigEndian);
        if (count == 0) return null;
        var pointer = RequireArrayPointer(cluster, variantGroup.Index, 0, scenePassesMember);
        if (pointer.UserFixupId is not null
            || pointer.DestinationListIndex != scenePassGroup.Index
            || (ulong)pointer.DestinationObjectId + count > scenePassGroup.Group.Count)
        {
            throw new InvalidPhyreException("Effect variant has an invalid scene-render-pass array.");
        }
        return FindPassType(
            cluster,
            scenePassGroup.Index,
            pointer.DestinationObjectId,
            passTypeMemberIndex);
    }

    private static CpuEffectProgram ReadEffectProgram(
        PhyreClusterData cluster,
        (int Index, PhyreInstanceGroup Group) effectGroup,
        (int Index, PhyreInstanceGroup Group) scenePassGroup,
        (int Index, PhyreInstanceGroup Group) shaderGroup,
        (int Index, PhyreInstanceGroup Group) shaderPassGroup,
        PhyreDataMember passTypeMember)
    {
        var (contextSwitches, contexts) = ReadShaderContexts(cluster, effectGroup);
        var scenePassShadersMember = FindRequiredMember(cluster, "PSceneRenderPass", "m_shaders");
        var shaderPassesMember = FindRequiredMember(cluster, "PShader", "m_passes");
        var shaderStreamsMember = FindRequiredMember(cluster, "PShader", "m_streamDefinitionsForPasses");
        var vertexProgramMember = FindRequiredMember(cluster, "PShaderPassBase", "m_vertexProgram");
        var fragmentProgramMember = FindRequiredMember(cluster, "PShaderPassBase", "m_fragmentProgram");
        var compiledCodeMember = FindRequiredMember(cluster, "PShaderProgramD3D11", "m_compiledCode");
        var constantBufferSizeMember = FindRequiredMember(cluster, "PShaderProgramD3D11", "m_constantBufferSize");
        var globalConstantBufferIndexMember = FindRequiredMember(cluster, "PShaderProgramD3D11", "m_globalConstantBufferIndex");
        var streamNameMember = FindRequiredMember(cluster, "PShaderStreamDefinition", "m_name");
        var streamRenderTypeMember = FindRequiredMember(cluster, "PShaderStreamDefinition", "m_renderType");
        var streamDataTypeMember = FindRequiredMember(cluster, "PShaderStreamDefinition", "m_dataType");
        var streamIndexMember = FindRequiredMember(cluster, "PShaderStreamDefinition", "m_index");
        var passes = new Dictionary<string, CpuSceneRenderPassProgram>(StringComparer.Ordinal);
        for (uint scenePassId = 0; scenePassId < scenePassGroup.Group.Count; scenePassId++)
        {
            var passName = FindPassType(cluster, scenePassGroup.Index, scenePassId, passTypeMember.Index);
            if (string.IsNullOrEmpty(passName)) continue;
            var shaderCount = ReadArrayCount(cluster, scenePassGroup.Index, scenePassId, scenePassShadersMember);
            var shaderPointer = RequireArrayPointer(cluster, scenePassGroup.Index, scenePassId, scenePassShadersMember);
            if (shaderPointer.DestinationListIndex != shaderGroup.Index
                || (ulong)shaderPointer.DestinationObjectId + shaderCount > shaderGroup.Group.Count)
            {
                throw new InvalidPhyreException($"Scene render pass '{passName}' has an invalid shader array.");
            }
            var permutations = new List<CpuShaderPermutation>();
            for (uint localShader = 0; localShader < shaderCount; localShader++)
            {
                var shaderId = checked(shaderPointer.DestinationObjectId + localShader);
                var passCount = ReadArrayCount(cluster, shaderGroup.Index, shaderId, shaderPassesMember);
                var passPointer = RequireArrayPointer(cluster, shaderGroup.Index, shaderId, shaderPassesMember);
                if (passPointer.DestinationListIndex != shaderPassGroup.Index
                    || (ulong)passPointer.DestinationObjectId + passCount > shaderPassGroup.Group.Count)
                {
                    throw new InvalidPhyreException($"Shader {shaderId} has an invalid shader-pass array.");
                }
                var inputs = ReadShaderInputs(
                    cluster,
                    shaderGroup.Index,
                    shaderId,
                    shaderStreamsMember,
                    streamNameMember,
                    streamRenderTypeMember,
                    streamDataTypeMember,
                    streamIndexMember);
                for (uint localPass = 0; localPass < passCount; localPass++)
                {
                    var shaderPassId = checked(passPointer.DestinationObjectId + localPass);
                    var vertexPointer = RequireMemberPointer(cluster, shaderPassGroup.Index, shaderPassId, vertexProgramMember);
                    var fragmentPointer = RequireMemberPointer(cluster, shaderPassGroup.Index, shaderPassId, fragmentProgramMember);
                    permutations.Add(new CpuShaderPermutation(
                        ReadStageProgram(cluster, vertexPointer, compiledCodeMember, constantBufferSizeMember, globalConstantBufferIndexMember),
                        ReadStageProgram(cluster, fragmentPointer, compiledCodeMember, constantBufferSizeMember, globalConstantBufferIndexMember),
                        inputs,
                        localShader < contexts.Count ? contexts[checked((int)localShader)] : null));
                }
            }
            passes.Add(passName, new CpuSceneRenderPassProgram(passName, permutations));
        }
        return new CpuEffectProgram(passes, contextSwitches, contexts);
    }

    private static (IReadOnlyList<string> Switches, IReadOnlyList<CpuShaderContext> Contexts) ReadShaderContexts(
        PhyreClusterData cluster,
        (int Index, PhyreInstanceGroup Group) effectGroup)
    {
        if (effectGroup.Group.Count != 1)
            throw new InvalidPhyreException("An effect asset must contain exactly one effect object.");
        var contextSwitchesMember = FindRequiredMember(cluster, "PEffect", "m_contextSwitches");
        var contextVariantsMember = FindRequiredMember(cluster, "PEffect", "m_contextVariantSwitches");
        var packedSwitchesMember = FindRequiredMember(cluster, "PNodeContext", "m_packedSwitches");
        var switchCount = ReadArrayCount(cluster, effectGroup.Index, 0, contextSwitchesMember);
        var switchPointers = cluster.Fixups.Pointers
            .Where(value => value.SourceListIndex == effectGroup.Index && value.SourceObjectId == 0
                && !value.IsClassDataMember
                && value.SourceOffset == contextSwitchesMember.ValueOffset + sizeof(uint))
            .OrderBy(value => value.ArrayIndex)
            .ToArray();
        if (switchPointers.Length != switchCount
            || switchPointers.Any(value => value.UserFixupId is null))
        {
            throw new InvalidPhyreException("Effect context-switch names have invalid pointer fixups.");
        }
        var switches = switchPointers.Select(value =>
        {
            var text = cluster.Fixups.UserFixups[checked((int)value.UserFixupId!.Value)].Text;
            return text ?? throw new InvalidPhyreException("Effect context-switch name is not text.");
        }).ToArray();

        var contextCount = ReadArrayCount(cluster, effectGroup.Index, 0, contextVariantsMember);
        if (contextCount == 0) return (switches, Array.Empty<CpuShaderContext>());
        var contextPointer = RequireArrayPointer(cluster, effectGroup.Index, 0, contextVariantsMember);
        var contextGroupIndex = checked((int)contextPointer.DestinationListIndex);
        if (cluster.Metadata.InstanceGroups[contextGroupIndex].ClassName != "PNodeContext"
            || (ulong)contextPointer.DestinationObjectId + contextCount > cluster.Metadata.InstanceGroups[contextGroupIndex].Count)
        {
            throw new InvalidPhyreException("Effect context variants have an invalid destination.");
        }
        var contexts = new CpuShaderContext[contextCount];
        for (uint localContext = 0; localContext < contextCount; localContext++)
        {
            var contextId = checked(contextPointer.DestinationObjectId + localContext);
            var valueCount = ReadArrayCount(cluster, contextGroupIndex, contextId, packedSwitchesMember);
            if (valueCount != switchCount)
                throw new InvalidPhyreException($"Effect context {localContext} has {valueCount} values for {switchCount} switches.");
            if (valueCount == 0)
            {
                // Phyre does not emit an array fixup for an empty packed-switch array.
                contexts[localContext] = new CpuShaderContext(
                    checked((int)localContext),
                    new Dictionary<string, uint>(StringComparer.Ordinal));
                continue;
            }
            var matchingValueFixups = cluster.Fixups.Arrays.Where(value =>
                value.SourceListIndex == contextGroupIndex && value.SourceObjectId == contextId
                && !value.IsClassDataMember
                && value.SourceOffset == packedSwitchesMember.ValueOffset + sizeof(uint)).ToArray();
            if (matchingValueFixups.Length != 1)
                throw new InvalidPhyreException(
                    $"Effect context {localContext} has {matchingValueFixups.Length} packed-switch array fixups; expected one.");
            var valueFixup = matchingValueFixups[0];
            var data = cluster.GetArrayData(contextGroupIndex, valueFixup.Offset, checked(valueCount * sizeof(uint))).Span;
            var values = new Dictionary<string, uint>(StringComparer.Ordinal);
            for (var index = 0; index < switches.Length; index++)
            {
                values.Add(switches[index], ReadUInt(data, index * sizeof(uint), cluster.Metadata.IsBigEndian));
            }
            contexts[localContext] = new CpuShaderContext(checked((int)localContext), values);
        }
        return (switches, contexts);
    }

    private static IReadOnlyList<CpuShaderInput> ReadShaderInputs(
        PhyreClusterData cluster,
        int shaderGroupIndex,
        uint shaderId,
        PhyreDataMember streamsMember,
        PhyreDataMember nameMember,
        PhyreDataMember renderTypeMember,
        PhyreDataMember dataTypeMember,
        PhyreDataMember indexMember)
    {
        var count = ReadArrayCount(cluster, shaderGroupIndex, shaderId, streamsMember);
        if (count == 0) return Array.Empty<CpuShaderInput>();
        var pointer = RequireArrayPointer(cluster, shaderGroupIndex, shaderId, streamsMember);
        var groupIndex = checked((int)pointer.DestinationListIndex);
        if (cluster.Metadata.InstanceGroups[groupIndex].ClassName != "PShaderStreamDefinition"
            || (ulong)pointer.DestinationObjectId + count > cluster.Metadata.InstanceGroups[groupIndex].Count)
        {
            throw new InvalidPhyreException($"Shader {shaderId} has an invalid stream-definition array.");
        }
        var inputs = new CpuShaderInput[count];
        for (uint index = 0; index < count; index++)
        {
            var objectId = checked(pointer.DestinationObjectId + index);
            var data = cluster.GetObject(groupIndex, objectId).Span;
            inputs[index] = new CpuShaderInput(
                ReadString(cluster, groupIndex, objectId, nameMember),
                data[checked((int)indexMember.ValueOffset)],
                ReadUInt(data, checked((int)renderTypeMember.ValueOffset), cluster.Metadata.IsBigEndian),
                data[checked((int)dataTypeMember.ValueOffset)]);
        }
        return inputs;
    }

    private static CpuShaderStageProgram ReadStageProgram(
        PhyreClusterData cluster,
        PhyrePointerFixup pointer,
        PhyreDataMember compiledCodeMember,
        PhyreDataMember constantBufferSizeMember,
        PhyreDataMember globalConstantBufferIndexMember)
    {
        if (pointer.UserFixupId is not null) throw new InvalidPhyreException("A shader stage references an external program.");
        var groupIndex = checked((int)pointer.DestinationListIndex);
        var objectId = pointer.DestinationObjectId;
        var data = cluster.GetObject(groupIndex, objectId).Span;
        var codeFixup = cluster.Fixups.Arrays.Single(value =>
            value.SourceListIndex == groupIndex && value.SourceObjectId == objectId
            && !value.IsClassDataMember
            && value.SourceOffset == compiledCodeMember.ValueOffset + sizeof(uint));
        var bytecode = cluster.GetArrayData(groupIndex, codeFixup.Offset, codeFixup.Count).ToArray();
        var inputLayout = cluster.Metadata.InstanceGroups[groupIndex].ClassName == "PShaderVertexProgram"
            ? ReadVertexInputLayout(cluster, groupIndex, objectId, data)
            : null;
        return new CpuShaderStageProgram(
            bytecode,
            checked((int)ReadUInt(data, checked((int)constantBufferSizeMember.ValueOffset), cluster.Metadata.IsBigEndian)),
            ReadUInt(data, checked((int)globalConstantBufferIndexMember.ValueOffset), cluster.Metadata.IsBigEndian),
            inputLayout);
    }

    private static IReadOnlyList<CpuShaderInputLayoutElement> ReadVertexInputLayout(
        PhyreClusterData cluster,
        int programGroupIndex,
        uint programId,
        ReadOnlySpan<byte> programData)
    {
        var inputLayoutMember = FindRequiredMember(cluster, "PShaderVertexProgramD3D11", "m_inputLayout");
        var streamsMember = FindRequiredMember(cluster, "PStreamInputLayoutD3D11", "m_streams");
        var semanticMember = FindRequiredMember(cluster, "PStreamInputDescD3D11", "m_semantic");
        var renderTypeMember = FindRequiredMember(cluster, "PStreamInputDescD3D11", "m_renderType");
        var semanticIndexMember = FindRequiredMember(cluster, "PStreamInputDescD3D11", "m_semanticIndex");
        var formatMember = FindRequiredMember(cluster, "PStreamInputDescD3D11", "m_d3dFormat");
        var inputSlotMember = FindRequiredMember(cluster, "PStreamInputDescD3D11", "m_inputSlot");
        var arrayOffset = checked((int)(inputLayoutMember.ValueOffset + streamsMember.ValueOffset));
        var count = ReadUInt(programData, arrayOffset, cluster.Metadata.IsBigEndian);
        if (count == 0) return Array.Empty<CpuShaderInputLayoutElement>();
        var pointer = cluster.Fixups.Pointers.Single(value =>
            value.SourceListIndex == programGroupIndex && value.SourceObjectId == programId
            && !value.IsClassDataMember && value.SourceOffset == arrayOffset + sizeof(uint));
        var streamGroupIndex = checked((int)pointer.DestinationListIndex);
        if (cluster.Metadata.InstanceGroups[streamGroupIndex].ClassName != "PStreamInputDescD3D11"
            || (ulong)pointer.DestinationObjectId + count > cluster.Metadata.InstanceGroups[streamGroupIndex].Count)
        {
            throw new InvalidPhyreException("Vertex shader input layout has an invalid stream array.");
        }
        var result = new CpuShaderInputLayoutElement[count];
        for (uint index = 0; index < count; index++)
        {
            var streamId = checked(pointer.DestinationObjectId + index);
            var stream = cluster.GetObject(streamGroupIndex, streamId).Span;
            result[index] = new CpuShaderInputLayoutElement(
                ReadString(cluster, streamGroupIndex, streamId, semanticMember),
                checked((int)ReadUInt(stream, checked((int)semanticIndexMember.ValueOffset), cluster.Metadata.IsBigEndian)),
                ReadUInt(stream, checked((int)renderTypeMember.ValueOffset), cluster.Metadata.IsBigEndian),
                ReadUInt(stream, checked((int)formatMember.ValueOffset), cluster.Metadata.IsBigEndian),
                checked((int)ReadUInt(stream, checked((int)inputSlotMember.ValueOffset), cluster.Metadata.IsBigEndian)));
        }
        return result;
    }

    private static uint ReadArrayCount(
        PhyreClusterData cluster,
        int groupIndex,
        uint objectId,
        PhyreDataMember member)
    {
        var data = cluster.GetObject(groupIndex, objectId).Span;
        return ReadUInt(data, checked((int)member.ValueOffset), cluster.Metadata.IsBigEndian);
    }

    private static PhyrePointerFixup RequireMemberPointer(
        PhyreClusterData cluster,
        int groupIndex,
        uint objectId,
        PhyreDataMember member)
        => cluster.Fixups.Pointers.Single(value =>
            value.SourceListIndex == groupIndex && value.SourceObjectId == objectId
            && value.IsClassDataMember && value.SourceMemberId == (uint)member.Index);

    private static string ReadString(
        PhyreClusterData cluster,
        int groupIndex,
        uint objectId,
        PhyreDataMember member)
    {
        var fixup = cluster.Fixups.Arrays.Single(value => value.SourceListIndex == groupIndex
            && value.SourceObjectId == objectId
            && ((value.IsClassDataMember && value.SourceMemberId == (uint)member.Index)
                || (!value.IsClassDataMember && value.SourceOffset == member.ValueOffset)));
        var group = cluster.Metadata.InstanceGroups[groupIndex];
        if (fixup.Offset >= group.ArraysSize) throw new InvalidPhyreException("Effect string exceeds its array storage.");
        var bytes = cluster.GetArrayData(groupIndex, fixup.Offset, group.ArraysSize - fixup.Offset).Span;
        var zero = bytes.IndexOf((byte)0);
        if (zero < 0) throw new InvalidPhyreException("Effect string is not zero terminated.");
        return System.Text.Encoding.ASCII.GetString(bytes[..zero]);
    }

    private static string? FindPassType(
        PhyreClusterData cluster,
        int groupIndex,
        uint objectId,
        int passTypeMemberIndex)
    {
        var pointer = cluster.Fixups.Pointers.SingleOrDefault(value =>
            value.SourceListIndex == groupIndex && value.SourceObjectId == objectId
            && value.IsClassDataMember && value.SourceMemberId == (uint)passTypeMemberIndex);
        return pointer?.UserFixupId is { } userId
            ? cluster.Fixups.UserFixups[checked((int)userId)].Text
            : null;
    }

    private static PhyreDataMember FindRequiredMember(
        PhyreClusterData cluster,
        string className,
        string memberName)
    {
        var descriptor = cluster.Metadata.Classes.SingleOrDefault(value => value.Name == className)
            ?? throw new InvalidPhyreException($"Effect has no {className} descriptor.");
        return descriptor.Members.SingleOrDefault(value => value.Name == memberName)
            ?? throw new InvalidPhyreException($"Effect has no {className}.{memberName} member.");
    }

    private static PhyrePointerFixup RequireArrayPointer(
        PhyreClusterData cluster,
        int groupIndex,
        uint objectId,
        PhyreDataMember member)
        => cluster.Fixups.Pointers.Single(value =>
            value.SourceListIndex == groupIndex && value.SourceObjectId == objectId
            && !value.IsClassDataMember && value.SourceOffset == member.ValueOffset + sizeof(uint));

    private static (int Index, PhyreInstanceGroup Group) FindGroup(PhyreClusterData cluster, string className)
    {
        for (var index = 0; index < cluster.Metadata.InstanceGroups.Count; index++)
        {
            var group = cluster.Metadata.InstanceGroups[index];
            if (group.ClassName == className) return (index, group);
        }
        throw new InvalidPhyreException($"Effect has no {className} group.");
    }

    private static (int Index, PhyreInstanceGroup Group)? FindOptionalGroup(
        PhyreClusterData cluster,
        string className)
    {
        for (var index = 0; index < cluster.Metadata.InstanceGroups.Count; index++)
        {
            var group = cluster.Metadata.InstanceGroups[index];
            if (group.ClassName == className) return (index, group);
        }
        return null;
    }

    private static int ReadInt(ReadOnlySpan<byte> data, int offset)
        => BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);

    private static float ReadFloat(ReadOnlySpan<byte> data, int offset)
        => BitConverter.Int32BitsToSingle(ReadInt(data, offset));

    private static uint ReadUInt(ReadOnlySpan<byte> data, int offset, bool bigEndian)
        => bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data[offset..])
            : BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    private static void Ensure(ReadOnlySpan<byte> data, int size)
    {
        if (size > data.Length) throw new InvalidPhyreException("Shader pass blend state is truncated.");
    }
}

public sealed record PhyreEffectMetadata(
    IReadOnlyDictionary<string, CpuRenderPassState> RenderPassStates,
    IReadOnlyDictionary<string, string> MaterialSwitches,
    string? DefaultRenderPassName = null,
    CpuEffectProgram? Program = null);
