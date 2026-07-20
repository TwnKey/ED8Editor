using System.Buffers.Binary;
using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Phyre;

public sealed class PhyreD3D11ModelReader : IPhyreModelReader
{
    private const uint MeshSegmentPointerOffset = 0x04;
    private const uint SegmentVertexDataPointerOffset = 0x18;
    private const uint DataBlockStreamsPointerOffset = 0x0c;

    public CpuModel Read(string assetId, ReadOnlyMemory<byte> phyreData)
    {
        if (string.IsNullOrWhiteSpace(assetId)) throw new ArgumentException("Asset ID is required.", nameof(assetId));
        var cluster = new PhyreClusterReader().Read(phyreData);
        var meshGroup = FindRequiredGroup(cluster, "PMesh");
        var segmentGroup = FindRequiredGroup(cluster, "PMeshSegment");
        var dataBlockGroup = FindRequiredGroup(cluster, "PDataBlockD3D11");
        var streamGroup = FindRequiredGroup(cluster, "PVertexStream");
        var materialGroup = FindRequiredGroup(cluster, "PMaterial");
        var materialGroupIndex = materialGroup.Index;
        var indexBufferSize = ReadUInt32(cluster.Data.Span, 0x48, cluster.Metadata.IsBigEndian);
        var sceneGraph = new PhyreMeshSceneGraphReader().Read(cluster);

        var meshes = new List<CpuMesh>(checked((int)meshGroup.Group.Count));
        for (uint meshIndex = 0; meshIndex < meshGroup.Group.Count; meshIndex++)
        {
            var meshObject = cluster.GetObject(meshGroup.Index, meshIndex).Span;
            var segmentCount = ReadUInt32(meshObject, 0, cluster.Metadata.IsBigEndian);
            var materialPointers = cluster.Fixups.Pointers
                .Where(value => value.SourceListIndex == meshGroup.Index
                    && value.SourceObjectId == meshIndex
                    && !value.IsClassDataMember
                    && value.SourceOffset == 0x34)
                .OrderBy(value => value.ArrayIndex)
                .ToArray();
            var primitives = new List<CpuMeshPrimitive>(checked((int)segmentCount));
            if (segmentCount != 0)
            {
                var segmentPointer = RequirePointer(cluster, meshGroup.Index, meshIndex, MeshSegmentPointerOffset);
                RequireDestination(segmentPointer, segmentGroup.Index, segmentCount, segmentGroup.Group.Count, "mesh segments");
                for (uint localSegment = 0; localSegment < segmentCount; localSegment++)
                {
                    var segmentId = checked(segmentPointer.DestinationObjectId + localSegment);
                    var primitive = ReadPrimitive(cluster, segmentGroup.Index, segmentId, dataBlockGroup, streamGroup, indexBufferSize);
                    if ((uint)primitive.MaterialIndex >= materialPointers.Length)
                    {
                        throw new InvalidPhyreException($"Mesh {meshIndex} references missing local material {primitive.MaterialIndex}.");
                    }

                    var materialPointer = materialPointers[primitive.MaterialIndex];
                    if (materialPointer.UserFixupId is not null || materialPointer.DestinationListIndex != materialGroupIndex)
                    {
                        throw new InvalidPhyreException($"Mesh {meshIndex} material {primitive.MaterialIndex} has an invalid destination.");
                    }
                    primitive = primitive with { MaterialIndex = checked((int)materialPointer.DestinationObjectId) };
                    primitives.Add(primitive);
                }
            }

            var sceneEntry = sceneGraph.GetValueOrDefault(meshIndex);
            meshes.Add(new CpuMesh(
                sceneEntry?.Name ?? $"{assetId}:mesh:{meshIndex}",
                sceneEntry?.LocalTransform ?? Matrix4x4.Identity,
                primitives,
                sceneEntry?.Purpose ?? CpuMeshPurpose.Render));
        }

        var materials = ReadMaterials(cluster, materialGroupIndex);
        return new CpuModel(assetId, meshes, materials, Array.Empty<CpuTexture>());
    }

    private static IReadOnlyList<CpuMaterial> ReadMaterials(PhyreClusterData cluster, int materialGroupIndex)
    {
        var materialGroup = cluster.Metadata.InstanceGroups[materialGroupIndex];
        var parameterBufferMember = FindRequiredMember(cluster, "PMaterial", "m_parameterBuffer");
        var remapToMember = FindRequiredMember(cluster, "PMaterial", "m_remapTo");
        var effectVariantMember = FindRequiredMember(cluster, "PMaterial", "m_effectVariant");
        var importNames = ReadAssetImportNames(cluster);
        var materials = new CpuMaterial[checked((int)materialGroup.Count)];
        for (uint materialId = 0; materialId < materialGroup.Count; materialId++)
        {
            var bufferPointer = cluster.Fixups.Pointers.SingleOrDefault(value =>
                value.SourceListIndex == materialGroupIndex
                && value.SourceObjectId == materialId
                && value.IsClassDataMember
                && value.SourceMemberId == (uint)parameterBufferMember.Index)
                ?? throw new InvalidPhyreException($"Material {materialId} has no parameter buffer.");
            if (bufferPointer.UserFixupId is not null
                || cluster.Metadata.InstanceGroups[checked((int)bufferPointer.DestinationListIndex)].ClassName != "PParameterBuffer")
            {
                throw new InvalidPhyreException($"Material {materialId} has an invalid parameter buffer.");
            }

            materials[checked((int)materialId)] = ReadMaterialParameterBuffer(
                cluster,
                checked((int)bufferPointer.DestinationListIndex),
                importNames,
                checked((int)materialId),
                ReadRenderPassType(cluster, materialGroupIndex, materialId, remapToMember.Index),
                ReadEffectAssetName(cluster, materialGroupIndex, materialId, effectVariantMember.Index, importNames));
        }

        return materials;
    }

    private static CpuMaterial ReadMaterialParameterBuffer(
        PhyreClusterData cluster,
        int bufferGroupIndex,
        IReadOnlyList<string> importNames,
        int materialId,
        string? renderPassType,
        string? effectAssetName)
    {
        var bufferGroup = cluster.Metadata.InstanceGroups[bufferGroupIndex];
        if (bufferGroup.Count != 1)
        {
            throw new InvalidPhyreException("A material parameter buffer group must contain exactly one object.");
        }

        var buffer = cluster.GetGroupObjectsData(bufferGroupIndex).Span;
        var definitionCount = ReadUInt32(buffer, 0x08, cluster.Metadata.IsBigEndian);
        var definitionsPointer = RequirePointer(cluster, bufferGroupIndex, 0, 0x0c);
        var definitionGroupIndex = checked((int)definitionsPointer.DestinationListIndex);
        var definitionGroup = cluster.Metadata.InstanceGroups[definitionGroupIndex];
        if (definitionsPointer.UserFixupId is not null || definitionGroup.ClassName != "PShaderParameterDefinition"
            || (ulong)definitionsPointer.DestinationObjectId + definitionCount > definitionGroup.Count)
        {
            throw new InvalidPhyreException($"Material {materialId} has invalid shader parameter definitions.");
        }

        var parameters = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var textures = new Dictionary<string, string>(StringComparer.Ordinal);
        for (uint localDefinition = 0; localDefinition < definitionCount; localDefinition++)
        {
            var definitionId = checked(definitionsPointer.DestinationObjectId + localDefinition);
            var definition = cluster.GetObject(definitionGroupIndex, definitionId).Span;
            var name = ReadDefinitionName(cluster, definitionGroupIndex, definitionId);
            var parameterType = definition[0x02];
            var dataType = definition[0x03];
            var location = ReadUInt16(definition, 0x08, cluster.Metadata.IsBigEndian);
            var size = ReadUInt16(definition, 0x0a, cluster.Metadata.IsBigEndian) & 0x1fff;
            if ((uint)location + size > buffer.Length)
            {
                throw new InvalidPhyreException($"Material {materialId} parameter '{name}' exceeds its buffer.");
            }

            if (parameterType is 64 or 65 && dataType <= 3 && size >= (dataType + 1) * sizeof(float))
            {
                var values = new float[dataType + 1];
                for (var component = 0; component < values.Length; component++)
                {
                    values[component] = ReadSingle(buffer, location + component * sizeof(float), cluster.Metadata.IsBigEndian);
                }
                parameters[name] = values;
            }
            else if (parameterType == 66)
            {
                var texturePointer = cluster.Fixups.Pointers.SingleOrDefault(value =>
                    value.SourceListIndex == bufferGroupIndex
                    && value.SourceObjectId == 0
                    && !value.IsClassDataMember
                    && value.SourceOffset >= location
                    && value.SourceOffset < location + size
                    && value.UserFixupId is not null
                    && cluster.Fixups.UserFixups[checked((int)value.UserFixupId.Value)].TypeName == "PAssetReferenceImport");
                if (texturePointer?.UserFixupId is { } userFixupId)
                {
                    var importId = ReadUserImportId(cluster.Fixups.UserFixups[checked((int)userFixupId)]);
                    if ((uint)importId >= importNames.Count)
                    {
                        throw new InvalidPhyreException($"Material {materialId} parameter '{name}' references missing asset import {importId}.");
                    }
                    textures[name] = Path.GetFileName(importNames[importId].Replace('\\', '/'));
                }
            }
        }

        var baseColor = FindBaseColor(parameters);
        return new CpuMaterial(
            $"material:{materialId}",
            baseColor,
            null,
            parameters,
            textures,
            new Dictionary<string, int>(StringComparer.Ordinal),
            renderPassType,
            effectAssetName);
    }

    private static string? ReadRenderPassType(
        PhyreClusterData cluster,
        int materialGroupIndex,
        uint materialId,
        int remapToMemberIndex)
    {
        var pointer = cluster.Fixups.Pointers.SingleOrDefault(value =>
            value.SourceListIndex == materialGroupIndex
            && value.SourceObjectId == materialId
            && value.IsClassDataMember
            && value.SourceMemberId == (uint)remapToMemberIndex);
        if (pointer is null || pointer.UserFixupId is not { } userFixupId) return null;
        var fixup = cluster.Fixups.UserFixups[checked((int)userFixupId)];
        return fixup.TypeName == "PSceneRenderPassType" ? fixup.Text : null;
    }

    private static string? ReadEffectAssetName(
        PhyreClusterData cluster,
        int materialGroupIndex,
        uint materialId,
        int effectVariantMemberIndex,
        IReadOnlyList<string> importNames)
    {
        var pointer = cluster.Fixups.Pointers.SingleOrDefault(value =>
            value.SourceListIndex == materialGroupIndex
            && value.SourceObjectId == materialId
            && value.IsClassDataMember
            && value.SourceMemberId == (uint)effectVariantMemberIndex);
        if (pointer?.UserFixupId is not { } userFixupId) return null;
        var fixup = cluster.Fixups.UserFixups[checked((int)userFixupId)];
        if (fixup.TypeName != "PAssetReferenceImport") return null;
        var importId = ReadUserImportId(fixup);
        return (uint)importId < importNames.Count ? importNames[importId] : null;
    }

    private static IReadOnlyList<string> ReadAssetImportNames(PhyreClusterData cluster)
    {
        var importGroup = FindOptionalGroup(cluster, "PAssetReferenceImport");
        if (importGroup is null) return Array.Empty<string>();
        var names = new string[checked((int)importGroup.Value.Group.Count)];
        for (uint objectId = 0; objectId < importGroup.Value.Group.Count; objectId++)
        {
            var idFixup = cluster.Fixups.Arrays.SingleOrDefault(value =>
                value.SourceListIndex == importGroup.Value.Index && value.SourceObjectId == objectId)
                ?? throw new InvalidPhyreException($"Asset import {objectId} has no identifier.");
            names[checked((int)objectId)] = ReadZeroTerminatedArrayString(cluster, importGroup.Value.Index, idFixup.Offset);
        }
        return names;
    }

    private static string ReadDefinitionName(PhyreClusterData cluster, int groupIndex, uint objectId)
    {
        var fixup = cluster.Fixups.Arrays.SingleOrDefault(value =>
            value.SourceListIndex == groupIndex && value.SourceObjectId == objectId)
            ?? throw new InvalidPhyreException($"Shader parameter definition {objectId} has no name.");
        return ReadZeroTerminatedArrayString(cluster, groupIndex, fixup.Offset);
    }

    private static string ReadZeroTerminatedArrayString(PhyreClusterData cluster, int groupIndex, uint offset)
    {
        var group = cluster.Metadata.InstanceGroups[groupIndex];
        if (offset >= group.ArraysSize) throw new InvalidPhyreException("Phyre string offset exceeds its array storage.");
        var data = cluster.GetArrayData(groupIndex, offset, group.ArraysSize - offset).Span;
        var zero = data.IndexOf((byte)0);
        if (zero < 0) throw new InvalidPhyreException("Phyre string is not zero terminated.");
        return System.Text.Encoding.ASCII.GetString(data[..zero]);
    }

    private static int ReadUserImportId(PhyreUserFixup fixup)
    {
        if (fixup.Data.Length != sizeof(ushort))
        {
            throw new InvalidPhyreException($"Asset import user fixup {fixup.Id} has an invalid size.");
        }
        // Asset import payloads use the serialized user-fixup wire order, independently of object endianness.
        return BinaryPrimitives.ReadUInt16BigEndian(fixup.Data.Span);
    }

    private static Vector4 FindBaseColor(IReadOnlyDictionary<string, float[]> parameters)
    {
        var pair = parameters.FirstOrDefault(value =>
            value.Key.Equals("BaseColor", StringComparison.OrdinalIgnoreCase)
            || value.Key.Equals("DiffuseColor", StringComparison.OrdinalIgnoreCase)
            || value.Key.Equals("MaterialDiffuse", StringComparison.OrdinalIgnoreCase));
        if (pair.Value is not { Length: >= 3 } values) return Vector4.One;
        return new Vector4(values[0], values[1], values[2], values.Length >= 4 ? values[3] : 1f);
    }

    private static PhyreDataMember FindRequiredMember(PhyreClusterData cluster, string className, string name)
    {
        return cluster.Metadata.Classes.SingleOrDefault(value => value.Name == className)?.Members
            .SingleOrDefault(value => value.Name == name)
            ?? throw new InvalidPhyreException($"Phyre metadata has no {className}.{name} member.");
    }

    private static CpuMeshPrimitive ReadPrimitive(
        PhyreClusterData cluster,
        int segmentGroupIndex,
        uint segmentId,
        LocatedGroup dataBlockGroup,
        LocatedGroup streamGroup,
        uint indexBufferSize)
    {
        var segment = cluster.GetObject(segmentGroupIndex, segmentId).Span;
        var materialIndex = checked((int)ReadUInt32(segment, 0x00, cluster.Metadata.IsBigEndian));
        var topology = ReadTopology(ReadUInt32(segment, 0x10, cluster.Metadata.IsBigEndian));
        var vertexDataCount = ReadUInt32(segment, 0x14, cluster.Metadata.IsBigEndian);
        var vertexBuffers = new List<CpuVertexBuffer>(checked((int)vertexDataCount));
        var semanticIndices = new Dictionary<VertexSemantic, int>();
        if (vertexDataCount != 0)
        {
            var dataPointer = RequirePointer(cluster, segmentGroupIndex, segmentId, SegmentVertexDataPointerOffset);
            RequireDestination(dataPointer, dataBlockGroup.Index, vertexDataCount, dataBlockGroup.Group.Count, "vertex data blocks");
            for (uint localBlock = 0; localBlock < vertexDataCount; localBlock++)
            {
                vertexBuffers.Add(ReadVertexBuffer(
                    cluster,
                    dataBlockGroup.Index,
                    checked(dataPointer.DestinationObjectId + localBlock),
                    streamGroup,
                    indexBufferSize,
                    semanticIndices));
            }
        }

        var indexCount = ReadUInt32(segment, 0x24, cluster.Metadata.IsBigEndian);
        var indexType = segment[0x28];
        if (indexType % 4 != 0)
        {
            throw new InvalidPhyreException($"Index buffer for segment {segmentId} uses a non-scalar Phyre type {indexType}.");
        }
        var indexOffset = ReadUInt32(segment, 0x40, cluster.Metadata.IsBigEndian);
        var indexDataSize = ReadUInt32(segment, 0x48, cluster.Metadata.IsBigEndian);
        var indexElementSize = GetPackedElementSize(indexType);
        var requiredIndexSize = checked((long)indexCount * indexElementSize);
        if (requiredIndexSize > indexDataSize)
        {
            throw new InvalidPhyreException($"Index buffer for segment {segmentId} is smaller than its declared element count.");
        }

        var indexData = cluster.GetVramData(indexOffset, indexDataSize).ToArray();
        var indices = new CpuIndexBuffer(indexData, indexElementSize, checked((int)indexCount));
        return new CpuMeshPrimitive(vertexBuffers, indices, materialIndex, topology);
    }

    private static CpuVertexBuffer ReadVertexBuffer(
        PhyreClusterData cluster,
        int dataBlockGroupIndex,
        uint dataBlockId,
        LocatedGroup streamGroup,
        uint indexBufferSize,
        Dictionary<VertexSemantic, int> semanticIndices)
    {
        var dataBlock = cluster.GetObject(dataBlockGroupIndex, dataBlockId).Span;
        var stride = ReadUInt32(dataBlock, 0x00, cluster.Metadata.IsBigEndian);
        var vertexCount = ReadUInt32(dataBlock, 0x04, cluster.Metadata.IsBigEndian);
        var streamCount = ReadUInt32(dataBlock, 0x08, cluster.Metadata.IsBigEndian);
        var vertexOffset = ReadUInt32(dataBlock, 0x28, cluster.Metadata.IsBigEndian);
        var dataSize = ReadUInt32(dataBlock, 0x30, cluster.Metadata.IsBigEndian);
        if (stride == 0 || (long)stride * vertexCount > dataSize)
        {
            throw new InvalidPhyreException($"Vertex data block {dataBlockId} has an invalid stride or element count.");
        }

        var attributes = new List<CpuVertexAttribute>(checked((int)streamCount));
        if (streamCount != 0)
        {
            var streamPointer = RequirePointer(cluster, dataBlockGroupIndex, dataBlockId, DataBlockStreamsPointerOffset);
            RequireDestination(streamPointer, streamGroup.Index, streamCount, streamGroup.Group.Count, "vertex streams");
            for (uint localStream = 0; localStream < streamCount; localStream++)
            {
                var streamId = checked(streamPointer.DestinationObjectId + localStream);
                var stream = cluster.GetObject(streamGroup.Index, streamId).Span;
                var offset = ReadUInt32(stream, 0x00, cluster.Metadata.IsBigEndian);
                var type = stream[0x08];
                var semanticName = ResolveStreamSemantic(cluster, streamGroup.Index, streamId);
                var semantic = MapSemantic(semanticName);
                semanticIndices.TryGetValue(semantic, out var semanticIndex);
                semanticIndices[semantic] = semanticIndex + 1;
                if ((long)offset + GetPackedElementSize(type) > stride)
                {
                    throw new InvalidPhyreException($"Vertex stream {streamId} exceeds its data block stride.");
                }

                attributes.Add(new CpuVertexAttribute(
                    semantic,
                    semanticIndex,
                    GetSourceFormat(type),
                    checked((int)offset)));
            }
        }

        var vramOffset = checked(indexBufferSize + vertexOffset);
        var bytes = cluster.GetVramData(vramOffset, dataSize).ToArray();
        return new CpuVertexBuffer(bytes, checked((int)stride), checked((int)vertexCount), attributes);
    }

    private static string? ResolveStreamSemantic(PhyreClusterData cluster, int streamGroupIndex, uint streamId)
    {
        var fixup = cluster.Fixups.Pointers.SingleOrDefault(value =>
            value.SourceListIndex == streamGroupIndex
            && value.SourceObjectId == streamId
            && value.IsClassDataMember
            && value.UserFixupId is not null);
        if (fixup?.UserFixupId is not { } userId || userId >= cluster.Fixups.UserFixups.Count)
        {
            return null;
        }

        return cluster.Fixups.UserFixups[checked((int)userId)].Text;
    }

    private static VertexSemantic MapSemantic(string? name) => name switch
    {
        "Vertex" => VertexSemantic.Position,
        "SkinnableVertex" => VertexSemantic.Position,
        "Normal" => VertexSemantic.Normal,
        "SkinnableNormal" => VertexSemantic.Normal,
        "Tangent" => VertexSemantic.Tangent,
        "SkinnableTangent" => VertexSemantic.Tangent,
        "Binormal" => VertexSemantic.Bitangent,
        "SkinnableBinormal" => VertexSemantic.Bitangent,
        "ST" => VertexSemantic.TextureCoordinate,
        "Color" => VertexSemantic.Color,
        "SkinIndices" => VertexSemantic.JointIndices,
        "SkinWeights" => VertexSemantic.JointWeights,
        _ => VertexSemantic.Unknown,
    };

    private static PrimitiveTopology ReadTopology(uint value) => value switch
    {
        0 => PrimitiveTopology.Points,
        1 => PrimitiveTopology.Lines,
        2 => PrimitiveTopology.Triangles,
        3 => PrimitiveTopology.TriangleStrip,
        4 => PrimitiveTopology.TriangleFan,
        5 => PrimitiveTopology.PointSprites,
        _ => PrimitiveTopology.Unknown,
    };

    private static string GetSourceFormat(byte type)
    {
        var components = type % 4 + 1;
        var scalar = (type / 4) switch
        {
            0 => "Float32",
            1 => "Float16",
            2 => "UInt32",
            3 => "UInt16",
            4 => "UInt8",
            5 => "UNorm16",
            6 => "UNorm8",
            7 => "SInt32",
            8 => "SInt16",
            9 => "SInt8",
            10 => "SNorm16",
            11 => "SNorm8",
            _ => $"PhyreType{type}",
        };
        return $"{scalar}x{components}";
    }

    private static int GetPackedElementSize(byte type)
    {
        var scalarSize = (type / 4) switch
        {
            0 or 2 or 7 => 4,
            1 or 3 or 5 or 8 or 10 => 2,
            4 or 6 or 9 or 11 => 1,
            _ => throw new InvalidPhyreException($"Unknown Phyre data type {type}."),
        };
        return scalarSize * (type % 4 + 1);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        if ((uint)offset > data.Length - sizeof(uint))
        {
            throw new InvalidPhyreException("A Phyre object field lies outside its object.");
        }

        var span = data.Slice(offset, sizeof(uint));
        return bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(span) : BinaryPrimitives.ReadUInt32LittleEndian(span);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        if ((uint)offset > data.Length - sizeof(ushort))
        {
            throw new InvalidPhyreException("A Phyre object field lies outside its object.");
        }

        var span = data.Slice(offset, sizeof(ushort));
        return bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span);
    }

    private static float ReadSingle(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        var bits = ReadUInt32(data, offset, bigEndian);
        return BitConverter.Int32BitsToSingle(unchecked((int)bits));
    }

    private static PhyrePointerFixup RequirePointer(PhyreClusterData cluster, int listIndex, uint objectId, uint offset)
    {
        return cluster.FindPointer(listIndex, objectId, offset)
            ?? throw new InvalidPhyreException($"Missing Phyre pointer fixup for list {listIndex}, object {objectId}, offset 0x{offset:X}.");
    }

    private static void RequireDestination(PhyrePointerFixup pointer, int expectedList, uint count, uint groupCount, string kind)
    {
        if (pointer.UserFixupId is not null || pointer.DestinationListIndex != expectedList
            || (ulong)pointer.DestinationObjectId + count > groupCount)
        {
            throw new InvalidPhyreException($"Phyre pointer does not reference a valid contiguous range of {kind}.");
        }
    }

    private static LocatedGroup FindRequiredGroup(PhyreClusterData cluster, string name)
    {
        return FindOptionalGroup(cluster, name)
            ?? throw new InvalidPhyreException($"Phyre cluster has no {name} instance group.");
    }

    private static LocatedGroup? FindOptionalGroup(PhyreClusterData cluster, string name)
    {
        for (var index = 0; index < cluster.Metadata.InstanceGroups.Count; index++)
        {
            if (cluster.Metadata.InstanceGroups[index].ClassName == name)
            {
                return new LocatedGroup(index, cluster.Metadata.InstanceGroups[index]);
            }
        }

        return null;
    }

    private readonly record struct LocatedGroup(int Index, PhyreInstanceGroup Group);
}
