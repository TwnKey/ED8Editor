using System.Buffers.Binary;
using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// The parameter block a shader needs a material to hand it.
///
/// A material does not merely name a shader: it has to supply the constant buffer
/// the shader declares — for the map shader, 624 bytes holding 55 parameters, plus
/// the sampler states its textures are read through. A material that cannot fill it
/// issues no draw at all, which is why a model with a correct graph, correct
/// geometry and a real compiled shader still drew nothing.
///
/// This is a property of the SHADER, not of any model. The same shader's parameter
/// block is byte-identical wherever it appears, so it is a lookup keyed by the
/// shader — the same standing decision as binding compiled shaders rather than
/// writing them. Nothing here is taken from the donor's geometry, materials or
/// scene: only the block that belongs to the shader being bound.
/// </summary>
/// <param name="ParameterBufferSize">
/// What the shader's constant buffer measures. The buffer object is bigger than its
/// class: the parameters live in the object itself, past the class size.
/// </param>
/// <param name="Children">
/// The header-class records, sixteen bytes each — { type id, offset in the object,
/// flags, how many } — one per parameter. The type ids are the DONOR's numbering and
/// are re-stated against ours when written.
/// </param>
/// <param name="ShaderAsset">
/// The shader this block belongs to, read from the buffer itself rather than chosen
/// beside it. A parameter block describes one shader's constant buffer: pair it with
/// a different shader and the sizes disagree, which the engine does not survive. A
/// package holding three shaders offers three chances to get that wrong.
/// </param>
public sealed record PhyreMaterialTable(
    string ShaderAsset,
    uint ParameterBufferSize,
    uint DefinitionCount,
    ReadOnlyMemory<byte> ParameterBufferObject,
    IReadOnlyList<PhyreMaterialChild> Children,
    IReadOnlyList<ReadOnlyMemory<byte>> SamplerStates,
    IReadOnlyList<ReadOnlyMemory<byte>> ParameterDefinitions,
    IReadOnlyList<PhyreMaterialPointer> Pointers,
    ReadOnlyMemory<byte> DefinitionArrayData,
    IReadOnlyList<PhyreMaterialArray> DefinitionArrays,
    IReadOnlyList<PhyreMaterialImport> Imports);

/// <summary>
/// A reference the buffer makes to something outside the cluster — a texture, or the
/// effect itself — named rather than numbered.
///
/// The buffer holds these as an import INDEX, which means nothing on its own: the
/// index is into the importing cluster's own list. Carrying the name instead lets the
/// same reference be rebuilt against whatever list the new cluster ends up with, and
/// makes it obvious when the thing referred to has not been brought along at all.
/// </summary>
/// <param name="Member">
/// The member's name when the reference sits on one. Null when it does not: some of
/// these point into the parameter payload itself, past the class, where there is no
/// member to name — a texture slot inside the buffer rather than a field of it.
/// </param>
public sealed record PhyreMaterialImport(string? Member, uint Source, string Asset);

/// <summary>
/// A parameter definition's name, as an offset into the group's array data.
///
/// A definition is sixteen bytes and a name held beside it. Carrying the objects
/// without the bytes their names point into leaves every one of them addressing an
/// empty region.
/// </summary>
public sealed record PhyreMaterialArray(uint ObjectId, uint Member, uint Count, uint Offset);

/// <summary>One parameter slot inside the buffer object, named by type rather than id.</summary>
public sealed record PhyreMaterialChild(string TypeName, uint Offset, uint Flags, uint Count);

/// <summary>
/// A pointer the buffer object makes to one of its own sampler states or parameter
/// definitions, by raw offset and by which object of which class it reaches.
/// </summary>
/// <param name="Count">
/// How many elements the pointer reaches, for a member that is an array — the
/// engine reads exactly this many.
///
/// It used to be dropped on the way in and written as zero, so a buffer told the
/// engine its definitions array was empty while declaring 55 definitions beside
/// it. The one cluster known to load states 55 on that same pointer.
/// </param>
public sealed record PhyreMaterialPointer(
    uint SourceOffset, string TargetClass, uint TargetId, uint Count = 0);

public static class PhyreMaterialTableReader
{
    private const int ParameterBufferRuntimePrefix = 12;

    /// <summary>
    /// Keeps the complete material ABI and switch values selected by the source
    /// mesh, while rebinding only its texture captures.
    ///
    /// Effect metadata describes locations and types, but not the model's material
    /// and context switch values. Zeroing those values can select no render context
    /// at all. The source material is therefore the authoritative default for the
    /// source prop being replaced.
    /// </summary>
    public static PhyreMaterialTable WithTextureAsset(
        PhyreMaterialTable material,
        string textureAsset)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (string.IsNullOrWhiteSpace(textureAsset))
            throw new ArgumentException("A texture asset id is required.", nameof(textureAsset));
        return material with
        {
            Imports = material.Imports
                .Select(value => value.Member == "m_effectVariant"
                    ? value
                    : value with { Asset = textureAsset })
                .ToArray(),
        };
    }

    /// <summary>
    /// Authors a structurally complete default material from an effect variant's
    /// own ABI. No model material is used: the definitions, locations and buffer
    /// size come from the compiled effect, while sampler objects are newly authored
    /// linear-wrap states.
    /// </summary>
    public static PhyreMaterialTable FromEffect(
        string shaderAsset,
        ReadOnlyMemory<byte> effectCluster,
        string textureAsset)
    {
        if (string.IsNullOrWhiteSpace(shaderAsset))
            throw new ArgumentException("A shader asset id is required.", nameof(shaderAsset));
        if (string.IsNullOrWhiteSpace(textureAsset))
            throw new ArgumentException(
                "A concrete texture asset is required for effect texture parameters.",
                nameof(textureAsset));

        var cluster = new PhyreClusterReader().Read(effectCluster);
        var variant = SingleGroup(cluster, "PEffectVariant");
        if (variant.Group.Count != 1)
        {
            throw new InvalidDataException(
                $"Effect '{shaderAsset}' contains {variant.Group.Count} variants; expected exactly one.");
        }

        var definitionsMember = RequiredMember(
            cluster, "PEffectVariant", "m_tweakableShaderParameterDefinitions");
        var bufferSizeMember = RequiredMember(
            cluster, "PEffectVariant", "m_tweakableParameterBufferSize");
        var variantObject = cluster.GetObject(variant.Index, 0).Span;
        var definitionCount = ReadUInt32(
            variantObject, checked((int)definitionsMember.ValueOffset), cluster.Metadata.IsBigEndian);
        var parameterBufferSize = ReadUInt16(
            variantObject, checked((int)bufferSizeMember.ValueOffset), cluster.Metadata.IsBigEndian);
        if (definitionCount == 0 || parameterBufferSize <= ParameterBufferRuntimePrefix)
        {
            throw new InvalidDataException(
                $"Effect '{shaderAsset}' declares an empty tweakable material ABI.");
        }

        var definitionsPointer = cluster.Fixups.Pointers.SingleOrDefault(value =>
            value.SourceListIndex == variant.Index
            && value.SourceObjectId == 0
            && !value.IsClassDataMember
            && value.SourceOffset == definitionsMember.ValueOffset + sizeof(uint));
        if (definitionsPointer is null || definitionsPointer.UserFixupId is not null)
        {
            throw new InvalidDataException(
                $"Effect '{shaderAsset}' has no valid tweakable-definition array.");
        }

        var definitionGroupIndex = checked((int)definitionsPointer.DestinationListIndex);
        var definitionGroup = cluster.Metadata.InstanceGroups[definitionGroupIndex];
        if (definitionGroup.ClassName != "PShaderParameterDefinition"
            || (ulong)definitionsPointer.DestinationObjectId + definitionCount > definitionGroup.Count)
        {
            throw new InvalidDataException(
                $"Effect '{shaderAsset}' has an invalid tweakable-definition range.");
        }

        var parameterBuffer = new byte[checked(parameterBufferSize - ParameterBufferRuntimePrefix)];
        BinaryPrimitives.WriteUInt32LittleEndian(parameterBuffer, parameterBufferSize);
        BinaryPrimitives.WriteUInt32LittleEndian(parameterBuffer.AsSpan(8), definitionCount);

        var definitions = new List<ReadOnlyMemory<byte>>(checked((int)definitionCount));
        var definitionNames = new MemoryStream();
        var definitionArrays = new List<PhyreMaterialArray>(checked((int)definitionCount));
        var children = new List<PhyreMaterialChild>(checked((int)definitionCount));
        var pointers = new List<PhyreMaterialPointer>();
        var samplers = new List<ReadOnlyMemory<byte>>();
        var imports = new List<PhyreMaterialImport>();

        for (uint localId = 0; localId < definitionCount; localId++)
        {
            var sourceId = checked(definitionsPointer.DestinationObjectId + localId);
            var source = cluster.GetObject(definitionGroupIndex, sourceId).ToArray();
            if (source.Length < 16)
            {
                throw new InvalidDataException(
                    $"Effect '{shaderAsset}' definition {localId} is truncated.");
            }

            var parameterType = source[2];
            var dataType = source[3];
            var location = ReadUInt16(source, 8, cluster.Metadata.IsBigEndian);
            var size = (ushort)(ReadUInt16(source, 10, cluster.Metadata.IsBigEndian) & 0x1fff);
            if ((uint)location + size > parameterBuffer.Length || size == 0)
            {
                throw new InvalidDataException(
                    $"Effect '{shaderAsset}' definition {localId} exceeds its"
                    + $" {parameterBuffer.Length}-byte serialized parameter buffer.");
            }

            var (typeName, count) = ParameterStorage(parameterType, dataType, size);
            children.Add(new PhyreMaterialChild(typeName, location, 0, count));

            // Capture-buffer objects state their exact parameter kind in their
            // first word. Numeric parameters are initialized to zero.
            if (parameterType is 66 or 71)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    parameterBuffer.AsSpan(location), parameterType);
            }
            if (parameterType is 66 or 71)
            {
                var samplerId = checked((uint)samplers.Count);
                samplers.Add(DefaultLinearWrapSampler());
                pointers.Add(new PhyreMaterialPointer(
                    0x80000000u | checked((uint)location + 8),
                    "PSamplerState",
                    samplerId));
            }
            if (parameterType == 66)
            {
                imports.Add(new PhyreMaterialImport(
                    null,
                    0x80000000u | checked((uint)location + 12),
                    textureAsset));
            }

            definitions.Add(source);
            var name = ReadDefinitionName(cluster, definitionGroupIndex, sourceId);
            var nameOffset = checked((uint)definitionNames.Length);
            var nameBytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
            definitionNames.Write(nameBytes);
            definitionArrays.Add(new PhyreMaterialArray(localId, 0, 0, nameOffset));
        }

        // PParameterBuffer.m_tweakableShaderParameterDefinitions is an embedded
        // PArray whose pointer word is at serialized offset 0x0c.
        pointers.Insert(0, new PhyreMaterialPointer(
            0x8000000Cu, "PShaderParameterDefinition", 0, definitionCount));

        return new PhyreMaterialTable(
            shaderAsset,
            parameterBufferSize,
            definitionCount,
            parameterBuffer,
            children,
            samplers,
            definitions,
            pointers,
            definitionNames.ToArray(),
            definitionArrays,
            imports);
    }

    /// <summary>
    /// The smallest parameter block Cold Steel 1 accepts, as the game's own patched
    /// asset processor writes it: a thirty-two byte constant buffer holding one
    /// parameter, <c>PhyreMaterialSwitches</c>, and nothing else — no sampler, no
    /// texture slot.
    ///
    /// Read off a cluster that processor produced. Carrying a shipped model's whole
    /// block instead brings twenty-seven sampler pointers addressed by raw offset,
    /// and that is the shape the game does not survive.
    /// </summary>
    public static PhyreMaterialTable Minimal(string shaderAsset)
    {
        var buffer = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 32);           // m_parameterBufferSize
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), 1);  // one definition
        // The switches word, as that processor leaves it.
        new byte[] { 0x00, 0xEC, 0x07, 0xCD }.CopyTo(buffer, 16);

        var definition = new byte[]
        {
            0x00, 0x00, 0x40, 0x08, 0x00, 0x00, 0x00, 0x00,
            0x10, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00,
        };
        var name = new byte[24];
        System.Text.Encoding.ASCII.GetBytes("PhyreMaterialSwitches ").CopyTo(name, 0);

        return new PhyreMaterialTable(
            shaderAsset,
            32,
            1,
            buffer,
            new[] { new PhyreMaterialChild("PUInt32", 16, 0, 1) },
            Array.Empty<ReadOnlyMemory<byte>>(),
            new ReadOnlyMemory<byte>[] { definition },
            // PParameterBuffer.m_tweakableShaderParameterDefinitions is an
            // embedded PArray.  The official asset processor fixes its pointer
            // word at +0x0C to the first definition object.
            // One definition, so the array pointer states one.
            new[] { new PhyreMaterialPointer(
                0x8000000Cu, "PShaderParameterDefinition", 0, 1) },
            name,
            new[] { new PhyreMaterialArray(0, 0, 0, 0) },
            Array.Empty<PhyreMaterialImport>());
    }

    private static (string TypeName, uint Count) ParameterStorage(
        byte parameterType,
        byte dataType,
        ushort size)
        => (parameterType, dataType) switch
        {
            (64, 8) when size == sizeof(uint) => ("PUInt32", 1),
            (64, <= 3) when size % sizeof(float) == 0
                => ("float", (uint)(size / sizeof(float))),
            (71, 52) when size == 16 => ("PShaderParameterCaptureBufferSampler", 1),
            (66, 52) when size == 16 => ("PShaderParameterCaptureBufferTexture2D", 1),
            _ => throw new InvalidDataException(
                $"Unsupported Phyre material parameter ABI: type={parameterType},"
                + $" dataType={dataType}, size={size}."),
        };

    private static ReadOnlyMemory<byte> DefaultLinearWrapSampler()
    {
        // A newly-authored CS1 PSamplerStateBase: trilinear minification, linear
        // magnification, wrapping on all axes, anisotropy 16 and the engine's
        // normal 1024-level upper bound.
        var sampler = new byte[36];
        sampler[0] = 5;
        sampler[1] = 1;
        sampler[2] = 1;
        sampler[3] = 1;
        sampler[4] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(
            sampler.AsSpan(12), BitConverter.SingleToUInt32Bits(16f));
        BinaryPrimitives.WriteUInt32LittleEndian(sampler.AsSpan(24), 1024);
        return sampler;
    }

    private static string ReadDefinitionName(
        PhyreClusterData cluster,
        int groupIndex,
        uint objectId)
    {
        var member = RequiredMember(cluster, "PShaderParameterDefinition", "m_name");
        var fixup = cluster.Fixups.Arrays.SingleOrDefault(value =>
            value.SourceListIndex == groupIndex
            && value.SourceObjectId == objectId
            && ((value.IsClassDataMember && value.SourceMemberId == (uint)member.Index)
                || (!value.IsClassDataMember && value.SourceOffset == member.ValueOffset)));
        if (fixup is null)
        {
            throw new InvalidDataException(
                $"Shader parameter definition {objectId} has no name.");
        }
        var group = cluster.Metadata.InstanceGroups[groupIndex];
        var bytes = cluster.GetArrayData(
            groupIndex, fixup.Offset, group.ArraysSize - fixup.Offset).Span;
        var zero = bytes.IndexOf((byte)0);
        if (zero < 0)
        {
            throw new InvalidDataException(
                $"Shader parameter definition {objectId} has an unterminated name.");
        }
        return System.Text.Encoding.ASCII.GetString(bytes[..zero]);
    }

    private static (int Index, PhyreInstanceGroup Group) SingleGroup(
        PhyreClusterData cluster,
        string className)
    {
        var groups = cluster.Metadata.InstanceGroups
            .Select((group, index) => (Index: index, Group: group))
            .Where(value => value.Group.ClassName == className)
            .ToArray();
        return groups.Length == 1
            ? groups[0]
            : throw new InvalidDataException(
                $"Effect contains {groups.Length} '{className}' groups; expected one.");
    }

    private static PhyreDataMember RequiredMember(
        PhyreClusterData cluster,
        string className,
        string memberName)
    {
        var descriptor = cluster.Metadata.Classes.SingleOrDefault(value => value.Name == className)
            ?? throw new InvalidDataException($"Effect has no '{className}' descriptor.");
        return PhyreObjectWriter.Chain(descriptor, cluster.Metadata.Classes)
            .SingleOrDefault(value => value.Name == memberName)
            ?? throw new InvalidDataException(
                $"Effect has no '{className}.{memberName}' member.");
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
        => bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data[offset..])
            : BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool bigEndian)
        => bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data[offset..])
            : BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);

    /// <summary>
    /// Reads the parameter block out of a shipped model that binds the shader.
    ///
    /// Refuses rather than guesses: a cluster with no parameter buffer, or one whose
    /// buffer declares no parameters, cannot teach us what the shader wants, and
    /// carrying an empty block forward would reproduce the very fault this exists to
    /// fix.
    /// </summary>
    public static PhyreMaterialTable Read(ReadOnlyMemory<byte> cluster)
    {
        var cut = PhyreClusterSectionReader.Read(cluster);
        var data = new PhyreClusterReader().Read(cluster);
        var fixups = new PhyreFixupReader().Read(cluster, cut.Metadata);
        var classes = cut.Metadata.Classes.ToList();
        var types = cut.Metadata.Types;

        string TypeName(uint id)
        {
            if (id < types.Count) return types[(int)id];
            var index = (int)id - types.Count - 1;
            return index >= 0 && index < classes.Count ? classes[index].Name : $"<id {id}>";
        }

        var groups = cut.Metadata.InstanceGroups;
        var material = ResolveFirstMeshMaterial(fixups, classes, groups);
        var buffer = material.ParameterBufferGroup;
        var bufferObjectId = material.ParameterBufferObjectId;

        // The object is the whole run: bigger than its class, the parameters being
        // laid inside it.
        var objects = data.GetGroupObjectsData(buffer.Index).ToArray();
        var each = buffer.Count == 0 ? 0 : (int)(buffer.ObjectsSize / buffer.Count);
        if (each == 0)
        {
            throw new InvalidDataException("The parameter buffer holds no object.");
        }
        if (bufferObjectId >= buffer.Count)
        {
            throw new InvalidDataException(
                $"The selected parameter-buffer object {bufferObjectId} lies outside"
                + $" its {buffer.Count}-object group.");
        }
        var bufferObject = objects.AsMemory(checked((int)bufferObjectId * each), each);

        var children = ReadChildren(cut, classes, groups, buffer, TypeName);
        if (children.Count == 0)
        {
            throw new InvalidDataException(
                "The parameter buffer declares no parameter, which is the fault this reads it to avoid.");
        }

        // An object's id is local to its own group, and a class may have several
        // groups. Flattening them into one list without re-numbering makes every
        // pointer past the first group land on the wrong object — which is not a
        // model that draws badly, it is a model the engine walks off the end of.
        var parameterBufferSize =
            SizeOf(bufferObject, classes, buffer, "m_parameterBufferSize");
        var definitionCount =
            SizeOf(bufferObject, classes, buffer, "m_tweakableShaderParameterDefinitions");
        var selectedPointers = fixups.Pointers.Where(value =>
                value.SourceListIndex == buffer.Index
                && value.SourceObjectId == bufferObjectId
                && value.UserFixupId is null)
            .ToArray();
        var samplers = ReachableObjects(
            data, groups, selectedPointers, "PSamplerState", out var samplerIds);
        var definitionPointer = selectedPointers.SingleOrDefault(value =>
            groups[checked((int)value.DestinationListIndex)].ClassName
                == "PShaderParameterDefinition");
        if (definitionPointer is null || definitionCount == 0)
        {
            throw new InvalidDataException(
                "The selected parameter buffer has no parameter-definition range.");
        }
        var definitionGroup = groups[checked((int)definitionPointer.DestinationListIndex)];
        if ((ulong)definitionPointer.DestinationObjectId + definitionCount > definitionGroup.Count)
        {
            throw new InvalidDataException(
                "The selected parameter buffer's definition range exceeds its group.");
        }
        var definitions = ObjectRange(
            data, definitionGroup, definitionPointer.DestinationObjectId, definitionCount);
        var definitionIds = Enumerable.Range(0, checked((int)definitionCount))
            .ToDictionary(
                value => (definitionGroup.Index,
                    checked(definitionPointer.DestinationObjectId + (uint)value)),
                value => checked((uint)value));

        // Everything the buffer reaches outside this cluster, by name. Skipping these
        // — they are the ones held as a user fixup — left the block referring to a
        // texture that was never brought along, which is a pointer into nothing.
        var importGroup = groups.FirstOrDefault(group => group.ClassName == "PAssetReferenceImport");
        var imports = new List<PhyreMaterialImport>();
        foreach (var fixup in fixups.Pointers)
        {
            if (fixup.SourceListIndex != buffer.Index
                || fixup.SourceObjectId != bufferObjectId
                || fixup.UserFixupId is null) continue;
            var user = fixups.UserFixups[checked((int)fixup.UserFixupId.Value)];
            if (user.Data.Length < 2 || importGroup is null) continue;
            var index = (uint)((user.Data.Span[0] << 8) | user.Data.Span[1]);
            // By member NAME. The donor numbered its members against its own table,
            // and that number names a different field in ours.
            var descriptor = classes.First(value => value.Name == buffer.ClassName);
            var member = PhyreObjectWriter.Chain(descriptor, classes)
                .FirstOrDefault(value => value.Index == fixup.SourceOffsetOrMember);
            if (NameOf(data, classes, importGroup, index) is { } asset)
            {
                imports.Add(new PhyreMaterialImport(
                    member?.Name, fixup.SourceOffsetOrMember, asset));
            }
        }

        var pointers = new List<PhyreMaterialPointer>();
        foreach (var fixup in fixups.Pointers)
        {
            if (fixup.SourceListIndex != buffer.Index
                || fixup.SourceObjectId != bufferObjectId
                || fixup.UserFixupId is not null) continue;
            var target = groups.FirstOrDefault(group => group.Index == (int)fixup.DestinationListIndex);
            if (target?.ClassName is not ("PSamplerState" or "PShaderParameterDefinition")) continue;
            var renumbered = target.ClassName == "PSamplerState" ? samplerIds : definitionIds;
            if (!renumbered.TryGetValue(
                    (target.Index, fixup.DestinationObjectId), out var id))
            {
                continue;
            }
            pointers.Add(new PhyreMaterialPointer(
                fixup.SourceOffsetOrMember, target.ClassName, id, fixup.ArrayIndex));
        }

        // The definitions' own array data, and the fixups that reach into it. Only
        // the first group's: the writer lays them out as one group, and an offset
        // means nothing outside the run it was measured in.
        var definitionArrayData =
            data.GetArrayData(definitionGroup.Index, 0, definitionGroup.ArraysSize);
        var definitionArrays = new List<PhyreMaterialArray>();
        foreach (var array in fixups.Arrays)
        {
            if (array.SourceListIndex != definitionGroup.Index
                || array.SourceObjectId < definitionPointer.DestinationObjectId
                || array.SourceObjectId
                    >= definitionPointer.DestinationObjectId + definitionCount) continue;
            definitionArrays.Add(new PhyreMaterialArray(
                array.SourceObjectId - definitionPointer.DestinationObjectId,
                array.SourceOffsetOrMember,
                array.Count,
                array.Offset));
        }

        return new PhyreMaterialTable(
            ShaderOf(data, fixups, classes, groups, buffer, bufferObjectId),
            parameterBufferSize,
            definitionCount,
            bufferObject,
            children,
            samplers,
            definitions,
            pointers,
            definitionArrayData,
            definitionArrays,
            imports);
    }

    /// <summary>
    /// Resolves the material actually bound by the first mesh, then follows that
    /// material to its parameter buffer.
    ///
    /// Object ids are local to an instance group. Consequently neither the first
    /// PMaterial nor the first PParameterBuffer has any special meaning. Shipped
    /// CS1 assets commonly contain several groups of both, and selecting one by
    /// enumeration order pairs the authored mesh with an unrelated shader ABI.
    /// </summary>
    private static ResolvedMaterial ResolveFirstMeshMaterial(
        PhyreFixupSet fixups,
        IReadOnlyList<PhyreClassDescriptor> classes,
        IReadOnlyList<PhyreInstanceGroup> groups)
    {
        var mesh = groups.FirstOrDefault(value => value.ClassName == "PMesh" && value.Count > 0)
            ?? throw new InvalidDataException(
                "This model contains no mesh whose material can be resolved.");
        var defaultMaterials = RequiredMember(classes, "PMesh", "m_defaultMaterials");
        var materialPointer = RequirePointerToClass(
            fixups,
            groups,
            mesh.Index,
            0,
            defaultMaterials,
            "PMaterial",
            embeddedArrayPointer: true);
        var materialGroup = groups[checked((int)materialPointer.DestinationListIndex)];
        if (materialPointer.DestinationObjectId >= materialGroup.Count)
        {
            throw new InvalidDataException(
                $"PMesh[0] points to PMaterial[{materialPointer.DestinationObjectId}]"
                + $" outside its {materialGroup.Count}-object group.");
        }

        var parameterBuffer = RequiredMember(classes, "PMaterial", "m_parameterBuffer");
        var bufferPointer = RequirePointerToClass(
            fixups,
            groups,
            materialGroup.Index,
            materialPointer.DestinationObjectId,
            parameterBuffer,
            "PParameterBuffer",
            embeddedArrayPointer: false);
        var bufferGroup = groups[checked((int)bufferPointer.DestinationListIndex)];
        return new ResolvedMaterial(
            materialGroup,
            materialPointer.DestinationObjectId,
            bufferGroup,
            bufferPointer.DestinationObjectId);
    }

    private static PhyrePointerFixup RequirePointerToClass(
        PhyreFixupSet fixups,
        IReadOnlyList<PhyreInstanceGroup> groups,
        int sourceGroupIndex,
        uint sourceObjectId,
        PhyreDataMember member,
        string targetClass,
        bool embeddedArrayPointer)
    {
        var rawOffset = checked(
            member.ValueOffset + (embeddedArrayPointer ? sizeof(uint) : 0u));
        var matches = fixups.Pointers.Where(value =>
                value.SourceListIndex == sourceGroupIndex
                && value.SourceObjectId == sourceObjectId
                && value.UserFixupId is null
                && ((value.IsClassDataMember && value.SourceMemberId == (uint)member.Index)
                    || (!value.IsClassDataMember && value.SourceOffset == rawOffset))
                && value.DestinationListIndex < groups.Count
                && groups[checked((int)value.DestinationListIndex)].ClassName == targetClass)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                $"{groups[sourceGroupIndex].ClassName}[{sourceObjectId}].{member.Name}"
                + $" has {matches.Length} pointers to {targetClass}; expected exactly one.");
    }

    private static PhyreDataMember RequiredMember(
        IReadOnlyList<PhyreClassDescriptor> classes,
        string className,
        string memberName)
    {
        var descriptor = classes.SingleOrDefault(value => value.Name == className)
            ?? throw new InvalidDataException($"Model has no '{className}' descriptor.");
        return PhyreObjectWriter.Chain(descriptor, classes)
            .SingleOrDefault(value => value.Name == memberName)
            ?? throw new InvalidDataException(
                $"Model has no '{className}.{memberName}' member.");
    }

    private sealed record ResolvedMaterial(
        PhyreInstanceGroup MaterialGroup,
        uint MaterialObjectId,
        PhyreInstanceGroup ParameterBufferGroup,
        uint ParameterBufferObjectId);

    /// <summary>
    /// Which shader the buffer says it is for.
    ///
    /// The buffer's effect variant is an imported reference, held as a user fixup
    /// whose two bytes are the index of the import — and the import names the shader.
    /// Following that is the only way to be sure the block and the shader are the
    /// same material's.
    /// </summary>
    private static string ShaderOf(
        PhyreClusterData data,
        PhyreFixupSet fixups,
        IReadOnlyList<PhyreClassDescriptor> classes,
        IReadOnlyList<PhyreInstanceGroup> groups,
        PhyreInstanceGroup buffer,
        uint bufferObjectId)
    {
        var descriptor = classes.First(value => value.Name == buffer.ClassName);
        var member = PhyreObjectWriter.Chain(descriptor, classes)
            .FirstOrDefault(value => value.Name == "m_effectVariant")
            ?? throw new InvalidDataException("A parameter buffer names no effect variant.");

        var fixup = fixups.Pointers.FirstOrDefault(value =>
            value.SourceListIndex == buffer.Index
            && value.SourceObjectId == bufferObjectId
            && value.UserFixupId is not null
            && (value.SourceOffsetOrMember == member.Index
                || value.SourceOffsetOrMember == (0x80000000u | member.ValueOffset)))
            ?? throw new InvalidDataException(
                "This parameter buffer does not say which shader it belongs to.");

        var user = fixups.UserFixups[checked((int)fixup.UserFixupId!.Value)];
        if (user.Data.Length < 2)
        {
            throw new InvalidDataException("The buffer's effect reference holds no import index.");
        }
        var import = (uint)((user.Data.Span[0] << 8) | user.Data.Span[1]);

        var imports = groups.FirstOrDefault(group => group.ClassName == "PAssetReferenceImport")
            ?? throw new InvalidDataException("This model imports nothing, so it names no shader.");
        var name = NameOf(data, classes, imports, import)
            ?? throw new InvalidDataException($"Import {import} has no name.");
        return name;
    }

    private static string? NameOf(
        PhyreClusterData data,
        IReadOnlyList<PhyreClassDescriptor> classes,
        PhyreInstanceGroup group,
        uint id)
    {
        var idMember = classes.First(value => value.Name == group.ClassName).Members
            .First(value => value.Name == "m_id");
        var fixup = data.Fixups.Arrays.FirstOrDefault(value =>
            value.SourceListIndex == group.Index && value.SourceObjectId == id
            && (value.SourceOffsetOrMember == (uint)idMember.Index
                || value.SourceOffset == idMember.ValueOffset));
        if (fixup is null) return null;
        var bytes = data.GetArrayData(
            group.Index, fixup.Offset, group.ArraysSize - fixup.Offset).Span;
        var zero = bytes.IndexOf((byte)0);
        if (zero >= 0) bytes = bytes[..zero];
        return System.Text.Encoding.ASCII.GetString(bytes);
    }

    private static IReadOnlyList<PhyreMaterialChild> ReadChildren(
        PhyreClusterSections cut,
        IReadOnlyList<PhyreClassDescriptor> classes,
        IReadOnlyList<PhyreInstanceGroup> groups,
        PhyreInstanceGroup wanted,
        Func<uint, string> typeName)
    {
        var section = cut.HeaderClasses.Span;
        if (section.Length == 0) return Array.Empty<PhyreMaterialChild>();

        // One count per header-flagged group, in group order, then the records of
        // each run one after another — so reaching ours means counting past the
        // groups that come before it.
        var headerGroups = groups
            .Where(group => classes.Any(value => value.Name == group.ClassName && (value.Flags & 4) != 0))
            .ToList();
        var instances = (int)cut.Metadata.Header.HeaderClassInstanceCount;
        var counts = new int[instances];
        for (var index = 0; index < instances; index++)
        {
            counts[index] = BitConverter.ToInt32(section[(index * 4)..]);
        }

        var recordBase = instances * 4;
        var skipped = 0;
        for (var index = 0; index < instances && index < headerGroups.Count; index++)
        {
            if (headerGroups[index].Index != wanted.Index)
            {
                skipped += counts[index];
                continue;
            }
            var children = new List<PhyreMaterialChild>();
            for (var child = 0; child < counts[index]; child++)
            {
                var at = recordBase + (skipped + child) * 16;
                children.Add(new PhyreMaterialChild(
                    typeName(BitConverter.ToUInt32(section[at..])),
                    BitConverter.ToUInt32(section[(at + 4)..]),
                    BitConverter.ToUInt32(section[(at + 8)..]),
                    BitConverter.ToUInt32(section[(at + 12)..])));
            }
            return children;
        }
        return Array.Empty<PhyreMaterialChild>();
    }

    /// <param name="renumbered">
    /// Where each of the donor's objects landed in the single group we write, keyed
    /// by the group it came from and the id it had there.
    /// </param>
    private static IReadOnlyList<ReadOnlyMemory<byte>> ReachableObjects(
        PhyreClusterData data,
        IReadOnlyList<PhyreInstanceGroup> groups,
        IReadOnlyList<PhyrePointerFixup> pointers,
        string targetClass,
        out IReadOnlyDictionary<(int Group, uint Id), uint> renumbered)
    {
        var result = new List<ReadOnlyMemory<byte>>();
        var map = new Dictionary<(int, uint), uint>();
        foreach (var pointer in pointers)
        {
            var groupIndex = checked((int)pointer.DestinationListIndex);
            if (groupIndex < 0 || groupIndex >= groups.Count)
            {
                throw new InvalidDataException(
                    $"A material pointer targets missing instance group {groupIndex}.");
            }
            var group = groups[groupIndex];
            if (group.ClassName != targetClass) continue;
            if (pointer.DestinationObjectId >= group.Count)
            {
                throw new InvalidDataException(
                    $"A material pointer targets {targetClass}[{pointer.DestinationObjectId}]"
                    + $" outside its {group.Count}-object group.");
            }

            // A source model may keep every material's sampler states in one large
            // instance group. Reaching one object in that group does not make its
            // siblings reachable: carrying all of them merged five unrelated
            // lamp-post materials into the one material being authored.
            //
            // Two material pointers can reference the same sampler state object
            // (e.g. PSamplerState[0] at two different buffer offsets).  The
            // engine counts objects per group, so the cluster must carry as many
            // PSamplerState objects as there are pointers to them — including the
            // duplicate.  Both pointers resolve to the first occurrence's id so
            // the fixups target the correct object.
            var key = (group.Index, pointer.DestinationObjectId);
            if (!map.ContainsKey(key))
            {
                map[key] = checked((uint)result.Count);
            }
            result.Add(data.GetObject(group.Index, pointer.DestinationObjectId));
        }
        renumbered = map;
        return result;
    }

    private static IReadOnlyList<ReadOnlyMemory<byte>> ObjectRange(
        PhyreClusterData data,
        PhyreInstanceGroup group,
        uint first,
        uint count)
    {
        var result = new ReadOnlyMemory<byte>[checked((int)count)];
        for (uint index = 0; index < count; index++)
            result[checked((int)index)] = data.GetObject(group.Index, first + index);
        return result;
    }

    private static uint SizeOf(
        ReadOnlyMemory<byte> bufferObject,
        IReadOnlyList<PhyreClassDescriptor> classes,
        PhyreInstanceGroup group,
        string member)
    {
        var descriptor = classes.FirstOrDefault(value => value.Name == group.ClassName);
        var field = descriptor is null
            ? null
            : PhyreObjectWriter.Chain(descriptor, classes).FirstOrDefault(value => value.Name == member);
        return field is null || field.ValueOffset + 4 > bufferObject.Length
            ? 0
            : BitConverter.ToUInt32(bufferObject.Span[(int)field.ValueOffset..]);
    }
}
