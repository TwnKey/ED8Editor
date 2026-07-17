using System.Buffers.Binary;
using System.Text;
using ED8Editor.Core;
using ED8Editor.ScriptHeaders;
using ED8Editor.Ops;
using ED8Editor.Assets;
using ED8Editor.Packages;
using ED8Editor.Phyre;
using ED8Editor.Application;
using ED8Editor.Rendering;

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

    var diagnosticClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        "PClusterHeaderD3D11", "PMesh", "PMeshSegment", "PMeshSegmentD3D11", "PMeshSegmentBase",
        "PDataBlockD3D11", "PDataBlockBase", "PIndexDataBlockD3D11", "PIndexDataBlockBase",
        "PVertexStream", "PRenderDataType", "PMaterial", "PMaterialSet", "PParameterBuffer",
        "PShaderParameterDefinition", "PAssetReference", "PAssetReferenceImport", "PTexture2D",
        "PTexture2DD3D11", "PTexture2DBase", "PTextureCommonBase",
    };
    foreach (var descriptor in metadata.Classes.Where(value => diagnosticClasses.Contains(value.Name)))
    {
        Console.WriteLine($"{descriptor.Name} ({descriptor.Size} bytes, super={descriptor.SuperClassId}):");
        foreach (var member in descriptor.Members)
        {
            var arraySuffix = member.FixedArraySize == 0 ? string.Empty : $"[{member.FixedArraySize}]";
            Console.WriteLine($"  +0x{member.ValueOffset:X3} {member.TypeName ?? $"type#{member.TypeId}"} {member.Name}{arraySuffix} ({member.Size} bytes)");
        }
    }
    var membersById = metadata.Classes.SelectMany(value => value.Members).ToDictionary(value => (uint)value.Index);
    foreach (var fixup in fixups.Arrays.Where(value =>
                 metadata.InstanceGroups[value.SourceListIndex].ClassName is "PMesh" or "PDataBlockD3D11" or "PMaterial" or "PParameterBuffer"))
    {
        var memberName = fixup.IsClassDataMember && membersById.TryGetValue(fixup.SourceMemberId, out var member)
            ? member.Name
            : $"offset#0x{fixup.SourceOffset:X}";
        Console.WriteLine($"ARRAY {metadata.InstanceGroups[fixup.SourceListIndex].ClassName}[{fixup.SourceObjectId}].{memberName}: count={fixup.Count}, offset=0x{fixup.Offset:X}");
    }
    foreach (var fixup in fixups.Pointers.Where(value =>
                 metadata.InstanceGroups[value.SourceListIndex].ClassName is "PMesh" or "PMeshSegment" or "PDataBlockD3D11" or "PVertexStream" or "PMaterial" or "PParameterBuffer" or "PShaderParameterDefinition" or "PTexture2D"))
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

if (args is ["--phyre-model", var modelPackagePath, var modelEntryName])
{
    var archive = new PkgArchiveReader().Read(modelPackagePath);
    var model = new PhyreD3D11ModelReader().Read(Path.GetFileNameWithoutExtension(modelEntryName), archive.ReadEntry(modelEntryName));
    Console.WriteLine($"Meshes     : {model.Meshes.Count}");
    Console.WriteLine($"Primitives : {model.Meshes.Sum(value => value.Primitives.Count)}");
    Console.WriteLine($"Materials  : {model.Materials.Count}");
    foreach (var (material, materialIndex) in model.Materials.Select((value, index) => (value, index)))
    {
        Console.WriteLine($"material {materialIndex}: {material.SourceParameters.Count} constants, {material.SourceTextureReferences.Count} texture references");
        foreach (var reference in material.SourceTextureReferences)
        {
            Console.WriteLine($"  {reference.Key} -> {reference.Value}");
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
          <MapObjects>
            <AssetObject asset="O_TEST" name="chair" flag="0x283" custom="kept"
              pos="2.5, 1, -3" rot="0, 0, 0" scl="1, 2, 1"
              materialDiffuse="1, 0.5, 0.25, 1" materialEmission="0, 0.1, 0" />
          </MapObjects>
        </Ops>
        """;
    var path = WriteTemporaryOps(xml);
    try
    {
        var scene = new OpsReader().Read(path);
        Equal(1, scene.Props.Count);
        var prop = scene.Props[0];
        Equal("O_TEST", prop.AssetId);
        Equal("chair", prop.Name);
        Equal(0x283u, prop.Flags!.Value);
        Equal("kept", prop.SourceAttributes["custom"]);
        Near(-2.5f, prop.Transform.Position.X);
        Near(-MathF.Sqrt(0.5f), prop.Transform.Rotation.X);
        Near(MathF.Sqrt(0.5f), prop.Transform.Rotation.W);
        Equal(xml.Length, scene.OriginalBytes.Count);
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
    var files = Directory.GetFiles(opsDirectory, "*.ops", SearchOption.TopDirectoryOnly);
    var props = 0;
    var failures = new List<string>();

    foreach (var file in files)
    {
        try
        {
            props += reader.Read(file).Props.Count;
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
