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
    ("reports unsupported picking geometry", ReportsUnsupportedPickingGeometry),
    ("reports truncated picking vertex data", ReportsTruncatedPickingVertexData),
    ("reports truncated picking index data", ReportsTruncatedPickingIndexData),
    ("calculates transformed scene bounds", CalculatesTransformedSceneBounds),
    ("creates a center viewport ray", CreatesCenterViewportRay),
    ("supports editor camera orbit and free flight", KeepsEditorCameraOrbitCentered),
    ("builds typed OPS overlay geometry", BuildsTypedOpsOverlayGeometry),
    ("picks exact OPS volume geometry", PicksExactOpsVolumeGeometry),
    ("undoes and redoes scene document transforms", UndoesAndRedoesSceneDocumentTransforms),
    ("picks and parameterizes translation gizmo axes", PicksTranslationGizmoAxes),
    ("picks rotation rings and computes signed angles", PicksRotationRings),
    ("picks camera eye and look-at handles", PicksCameraHandles),
    ("snaps scene transforms to explicit increments", SnapsSceneTransforms),
    ("groups editable elements for the scene outliner", GroupsSceneOutlinerElements),
    ("validates and normalizes game installations", ValidatesGameInstallations),
    ("persists editor user settings", PersistsEditorUserSettings),
    ("writes transformed OPS props without losing unknown data", WritesTransformedOpsProps),
    ("writes duplicated and deleted OPS spatial elements", WritesStructuralOpsEdits),
    ("creates observed OPS spatial profiles in empty sections", CreatesObservedOpsProfiles),
    ("indexes PKG names without reading archives", IndexesPkgNamesWithoutReadingArchives),
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
            <SoundObject seName="wind" seType="POINT" seRange="12.5"
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
        Near(-2.5f, prop.Transform.Position.X);
        Near(-MathF.Sqrt(0.5f), prop.Transform.Rotation.X);
        Near(MathF.Sqrt(0.5f), prop.Transform.Rotation.W);
        Equal(2, scene.Volumes.Count);
        var entry = scene.Volumes.Single(value => value.Kind == MapVolumeKind.Entry);
        Equal("event_box", entry.Name);
        Equal("a0100", entry.DestinationMap!);
        Near(-4f, entry.Transform.Position.X);
        Near(3f, entry.Transform.Scale.Y);
        Near(1f, entry.Transform.Rotation.W);
        Equal(1, scene.Points.Count);
        Near(-7f, scene.Points[0].Position.X);
        Near(2.5f, scene.Points[0].Radius!.Value);
        Equal(1, scene.Cameras.Count);
        Near(-10f, scene.Cameras[0].Eye.X);
        Near(-13f, scene.Cameras[0].LookAt.X);
        Equal(1, scene.Sounds.Count);
        Equal(MapSoundKind.Point, scene.Sounds[0].Kind);
        Near(-16f, scene.Sounds[0].Position.X);
        Near(12.5f, scene.Sounds[0].Range);
        Equal(1, scene.Lights.Count);
        Near(-19f, scene.Lights[0].Position.X);
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

static void CreatesCenterViewportRay()
{
    var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, -2), Vector3.Zero, Vector3.UnitY);
    var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1f, 0.1f, 100f);
    var ray = SceneRayFactory.FromViewport(50, 50, 100, 100, view, projection);
    Near(0f, ray.Direction.X);
    Near(0f, ray.Direction.Y);
    Near(1f, ray.Direction.Z);
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
    var lines = new SceneOverlayBuilder().Build(map);
    Equal(22, lines.Count);
    Equal(true, lines.Any(line => line.Start == new Vector3(-1, -2, -3)));
    Equal(true, lines.Any(line => line.Start == new Vector3(0, 1, -2) && line.End == Vector3.Zero));
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
    Equal(false, document.IsDirty);
    var selection = new SceneElementSelection(SceneElementKind.Prop, 0, "editable");
    var original = document.Find(selection)!.Transform;
    document.PreviewTransform(selection, original with { Position = new Vector3(8, 4, 5) });
    Equal(false, document.IsDirty);
    Equal(true, document.CommitPreview(selection, original));
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
        Apply(SceneElementKind.EntryVolume, values => values["next"] = "a9999");
        Apply(SceneElementKind.GroupVolume, values => values["flag"] = "0x7");
        Apply(SceneElementKind.LookPoint, values => values["radius"] = "4.5");
        Apply(SceneElementKind.Camera, values => values["fov"] = "45");
        Apply(SceneElementKind.Sound, values =>
        {
            values["seRange"] = "12.5";
            values["seVolume"] = "0.75";
        });
        Apply(SceneElementKind.Light, values =>
        {
            values["colorPower"] = "4";
            values["outerRange"] = "11";
        });
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
        Equal("0x7", attributesReloaded.Volumes.Single(value => value.Kind == MapVolumeKind.Group).SourceAttributes["flag"]);
        Equal("45", attributesReloaded.Cameras[0].SourceAttributes["fov"]);
        Near(12.5f, attributesReloaded.Sounds[0].Range);
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
        var protectedEntry = new Dictionary<string, string>(
            document.FindElementAttributes(originals.Single(value => value.Kind == SceneElementKind.EntryVolume))!.Values)
        {
            ["pos"] = "invalid",
        };
        Equal(true, document.ApplyElementAttributes(
            originals.Single(value => value.Kind == SceneElementKind.EntryVolume), protectedEntry));
        Equal("1,2,3, 0,0,0, 4,5,6", document.FindElementAttributes(
            originals.Single(value => value.Kind == SceneElementKind.EntryVolume))!.Values["pos"]);
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
        Equal("entry_001", reloaded.Volumes.Single(value => value.Kind == MapVolumeKind.Entry).Name);
        Equal("group_001", reloaded.Volumes.Single(value => value.Kind == MapVolumeKind.Group).Name);
        Equal("look_001", reloaded.Points[0].Name);
        Equal("7", reloaded.Cameras[0].Name);
        Equal("bell", reloaded.Sounds[0].SoundName);
        Equal("0.75", reloaded.Sounds[0].SourceAttributes["seVolume"]);
        Near(12.5f, reloaded.Sounds[0].Range);
        Equal("0x5", reloaded.Lights[0].SourceAttributes["flag"]);
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
