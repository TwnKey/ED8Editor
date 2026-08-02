using System.Buffers.Binary;
using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Phyre.Authoring;

/// <summary>
/// The collision a written model carries: a triangle mesh, and how it behaves.
///
/// A map's collision is a mesh of its own, simpler than the one it draws, held in
/// a PShape as raw vertex and index bytes. The four numbers beside it are the
/// ones a glTF round trip loses and the game needs: which group it collides with,
/// whether it is enabled, whether it is static, and that the shape is a mesh at
/// all (type 7).
/// </summary>
public sealed record PhyrePhysicsSource(
    IReadOnlyList<System.Numerics.Vector3> Vertices,
    IReadOnlyList<int> Indices,
    byte CollisionGroup = 1,
    bool Enabled = true,
    byte RigidBodyType = 0,
    float DynamicFriction = 0.2f,
    float StaticFriction = 0.2f,
    float Restitution = 0.6f);

/// <summary>
/// The effect authored alongside a model.  The minimal map material has no
/// tweakable parameter payload; its transform is engine-owned shader state.
/// </summary>
/// <param name="Parameters">
/// The constant block the shader expects a material to fill — see
/// <see cref="PhyreMaterialTable"/>. Without it the material declares no parameter
/// and the engine issues no draw, so a model written with none is a model that will
/// not appear.
/// </param>
public sealed record PhyreShaderBinding(string ShaderAsset, PhyreMaterialTable? Parameters = null);

/// <summary>
/// Writes a model cluster from nothing — no source file, no template.
///
/// The shape comes from the smallest model the game ships, moviescreen: sixteen
/// classes, and a graph whose whole pattern is
/// <c>PAssetReference.m_asset</c> naming each object worth naming,
/// <c>PDataBlockD3D11 -> PVertexStream</c> one for one,
/// <c>PMaterial -> PParameterBuffer</c>, <c>PMesh -> PMeshSegment</c> and
/// <c>PMeshInstance -> PMesh</c>.
///
/// Most of a model's fields are zero — its substance is the graph and the names —
/// and the few that are not were checked against the corpus: a data block's size
/// is its stride times its count, its offset is where the blocks before it end,
/// and a mesh instance's bounds are the box its own mesh occupies. Those three
/// rules held over 130 640 fields across 4 545 shipped models.
/// </summary>
public static class PhyreModelClusterWriter
{
    private const uint Unskinned = 0xFFFFFFFF;
    private const uint TriangleList = 2;

    /// <summary>The classes a model of this shape lists, in the order it lists them.</summary>
    private static readonly string[] Layout =
    {
        "PAssetReference", "PAssetReferenceImport", "PDataBlockD3D11", "PMaterial",
        "PMesh", "PMeshInstance", "PMeshInstanceBounds", "PMeshInstanceSegmentContext",
        "PMeshSegment", "PNode", "PParameterBuffer", "PVertexStream", "PWorldMatrix",
    };

    /// <summary>The classes collision adds, when a model carries any.</summary>
    private static readonly string[] PhysicsLayout =
    {
        "PPhysicsModel", "PPhysicsRigidBody", "PPhysicsMesh", "PPhysicsMaterial", "PShape",
    };

    /// <summary>A shape that is a triangle mesh rather than a box or a sphere.</summary>
    private const uint MeshShape = 7;

    public static PhyreClusterContents Contents(
        PhyreModelSource model,
        PhyreShaderBinding shader,
        IReadOnlyList<PhyrePackedGeometry> packed,
        PhyrePhysicsSource? physics = null,
        PhyreSchemaProfile schemaProfile = PhyreSchemaProfile.Cs1Native)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentNullException.ThrowIfNull(packed);
        if (packed.Count == 0)
        {
            throw new ArgumentException("A model needs at least one mesh.", nameof(packed));
        }

        // Every stream of every mesh, and which mesh each came from. A model with
        // one segment was a toy: a map has a hundred and seventy.
        var streams = packed.SelectMany(mesh => mesh.Streams).ToArray();
        var streamMesh = packed
            .SelectMany((mesh, index) => mesh.Streams.Select(_ => index))
            .ToArray();
        var firstStream = new int[packed.Count];
        for (int mesh = 0, at = 0; mesh < packed.Count; mesh++)
        {
            firstStream[mesh] = at;
            at += packed[mesh].Streams.Count;
        }

        // A cluster has to list every class its own classes refer to, and their
        // ancestors — so the layout is closed over before anything is numbered.
        // The material's own classes come only when there is a parameter block to
        // put in them: a group with no objects would still be listed, and a cluster
        // names nothing it does not use.
        var material = shader.Parameters;
        // One material per distinct material of the imported model, in the order the
        // meshes first name them; each segment remembers which is its own. A model
        // was written with a single material whatever it carried, so a map of a
        // hundred and seventy-one surfaces wore one texture. A shipped map of this
        // size has thirteen.
        var materialNames = new List<string>();
        var materialOfSegment = new int[model.Meshes.Count];
        for (var mesh = 0; mesh < model.Meshes.Count; mesh++)
        {
            var name = model.Meshes[mesh].MaterialName;
            var at = materialNames.IndexOf(name);
            if (at < 0) { at = materialNames.Count; materialNames.Add(name); }
            materialOfSegment[mesh] = at;
        }
        // Only when there is a parameter block to put in them: without one there is
        // nothing to tell the materials apart, and a group per name would be empty
        // shapes.
        var materialCount = material is null ? 1 : Math.Max(1, materialNames.Count);
        var textureOf = new string?[materialCount];
        for (var mesh = 0; mesh < model.Meshes.Count; mesh++)
        {
            var at = materialOfSegment[mesh];
            if (at < materialCount && textureOf[at] is null)
            {
                textureOf[at] = model.Meshes[mesh].Texture?.Name;
            }
        }
        var layout = physics is null ? Layout : Layout.Concat(PhysicsLayout).ToArray();
        if (material is not null)
        {
            var extra = new List<string>();
            if (material.SamplerStates.Count != 0) extra.Add("PSamplerState");
            if (material.ParameterDefinitions.Count != 0) extra.Add("PShaderParameterDefinition");
            // A buffer is a group of its own — a shipped map lists PParameterBuffer
            // thirteen times, one object each — so the extra materials bring their
            // own groups. The samplers and the definitions stay shared, as they are
            // in the file this copies.
            for (var more = 1; more < materialCount; more++) extra.Add("PParameterBuffer");
            layout = layout.Concat(extra).ToArray();
        }
        // In class order, always. Every cluster the game ships lists its instance
        // groups sorted by class name — checked on six models from fifteen groups to
        // three hundred and thirty-four, without exception. The base layout was
        // written that way; appending physics, and later the material's own classes,
        // quietly broke it, and a group list the engine expects sorted is not
        // something it merely reads differently.
        layout = layout.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        var wanted = new SortedSet<string>(layout, StringComparer.Ordinal);
        var queue = new Queue<string>(layout);
        // A parameter slot names its own type, and a texture slot's type is a class
        // the model instantiates nowhere — it exists only inside the buffer. It still
        // has to be listed, or the record that names it has no id to give.
        foreach (var child in material?.Children ?? Array.Empty<PhyreMaterialChild>())
        {
            // Only the classes: a slot holding a float or a PUInt32 names a plain
            // type, which the type table already carries.
            if (!PhyreSchemaLibrary.IsClass(child.TypeName)) continue;
            if (wanted.Add(child.TypeName)) queue.Enqueue(child.TypeName);
        }
        while (queue.Count != 0)
        {
            foreach (var referenced in PhyreSchemaLibrary.Referenced(queue.Dequeue()))
            {
                if (wanted.Add(referenced)) queue.Enqueue(referenced);
            }
        }

        // Member ids are positions in the packed member table, which follows the
        // order the cluster lists its classes — and the assembler lists them
        // sorted. Numbering them in any other order would point every fixup at
        // the wrong field.
        // The game's table, in the game's order, whenever it covers what this model
        // uses — which it does for every model shape this writer produces. Numbering
        // classes and types our own way made every id in the file disagree with the
        // ones the engine reads in its own files.
        var derived = wanted.ToArray();
        var assetProcessorSchema =
            schemaProfile == PhyreSchemaProfile.FalcomAssetProcessor;
        var authoringLayout =
            schemaProfile is PhyreSchemaProfile.FalcomAssetProcessor
                or PhyreSchemaProfile.Cs1RuntimeAuthoring;
        var canonical = !assetProcessorSchema
            && derived.All(PhyreSchemaLibrary.CanonicalClasses.Contains);
        var canonicalPhysics = !assetProcessorSchema && !canonical
            && derived.All(PhyreSchemaLibrary.CanonicalPhysicsClasses.Contains);
        // The asset processor's table is fixed and describes a model, so a map that
        // carries collision names classes it does not list. They go at the END: a
        // class id is a position in this table, and appending leaves every existing
        // id where the game expects it.
        var assetProcessorClasses = PhyreSchemaLibrary.AssetProcessorCanonicalClasses
            .Concat(PhyreSchemaLibrary.Closure(derived)
                .Where(name =>
                    !PhyreSchemaLibrary.AssetProcessorCanonicalClasses.Contains(name)))
            .ToArray();
        var classNames = assetProcessorSchema
            ? assetProcessorClasses
            : canonical
            ? PhyreSchemaLibrary.CanonicalClasses.ToArray()
            : canonicalPhysics
                ? PhyreSchemaLibrary.CanonicalPhysicsClasses.ToArray()
                : derived;
        var types = assetProcessorSchema
            ? PhyreSchemaLibrary.AssetProcessorCanonicalTypes.ToArray()
            : canonical || canonicalPhysics
            ? PhyreSchemaLibrary.CanonicalTypes.ToArray()
            : PhyreSchemaLibrary.PrimitiveTypesFor(classNames);
        var descriptors = PhyreSchemaLibrary.Descriptors(types, classNames, schemaProfile);
        // Fix PMeshInstance offsets to match official tool (shift by +4 after offset 44, Size=112)

        // A group is placed where this writer states it, independently of how the
        // classes are ordered.
        int Group(string className) => Array.IndexOf(layout, className);

        // Names live in a group's array data, and an object points at its own.
        var names = new Dictionary<int, List<(uint Object, string Text)>>();
        void Name(string className, uint id, string text)
        {
            var group = Group(className);
            if (!names.TryGetValue(group, out var list)) names[group] = list = new();
            list.Add((id, text));
        }

        var pointers = new List<PhyrePointerFixup>();
        var arrayFixups = new List<PhyreArrayFixup>();
        var pointerArrays = new List<PhyreArrayFixup>();

        // A stream's semantic is a user fixup holding its name, which a pointer
        // from the stream refers to. Without it the engine — and our own reader —
        // cannot tell a position from a normal: both are three floats.
        var userFixups = new List<PhyreUserFixup>();
        var importedUserFixups = new Dictionary<uint, uint>();
        var userOffset = 0u;
        // A user fixup names its type by the same numbering the members use: an id
        // below the type count is one of the cluster's own types. Writing the number
        // instead of looking it up made a stream's semantic arrive as a 'bool',
        // because the table is built from the classes this model happens to use and
        // is not the same table twice.
        var typeIds = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (var index = 0; index < types.Count; index++) typeIds[types[index]] = (uint)index;
        uint UserFixup(string typeName, string text)
        {
            var typeId = LookUpTypeId(typeIds, classNames, typeName);
            var existing = userFixups.FindIndex(
                value => value.Text == text && value.TypeId == typeId);
            if (existing >= 0) return (uint)existing;
            var bytes = System.Text.Encoding.ASCII.GetBytes(text + "\0");
            userFixups.Add(new PhyreUserFixup(
                userFixups.Count, typeId, null, (uint)bytes.Length, userOffset, bytes, text));
            userOffset += (uint)bytes.Length;
            return (uint)(userFixups.Count - 1);
        }

        uint Semantic(string text) => UserFixup("PRenderDataType", text);

        /// <summary>
        /// What kind of thing an asset reference names, as the engine asks for it: a
        /// user fixup of type PClassDescriptor holding the class name.
        ///
        /// Without it a reference is a name pointing at an object of no stated kind,
        /// and the map's asset resolves to nothing — the model loads and draws
        /// nothing, exactly as if it were not in the package at all.
        /// </summary>
        void PointAssetType(string fromClass, uint fromId, string member, string targetClass)
            => pointers.Add(new PhyrePointerFixup(
                Group(fromClass), fromId, MemberId(descriptors, fromClass, member),
                0, 0, 0, 0, UserFixup("PClassDescriptor", targetClass)));
        void Point(string fromClass, uint fromId, string member, string toClass, uint toId)
            => pointers.Add(new PhyrePointerFixup(
                Group(fromClass), fromId, MemberId(descriptors, fromClass, member),
                (uint)Group(toClass), toId, 0, 0, null));

        void ImportedPoint(string fromClass, uint fromId, string member, uint importId)
            => ImportedPointAt(fromClass, fromId, MemberId(descriptors, fromClass, member), importId);

        void ImportedPointAt(string fromClass, uint fromId, uint member, uint importId)
            => ImportedPointAtGroup((uint)Group(fromClass), member, importId, fromId);

        // The same, for a group named by index rather than by class: several buffers
        // share the class name, so only their position tells them apart.
        void ImportedPointAtGroup(uint group, uint member, uint importId, uint fromId = 0)
        {
            if (!importedUserFixups.TryGetValue(importId, out var userId))
            {
                var typeId = checked((uint)(
                    types.Count + Array.IndexOf(classNames, "PAssetReferenceImport") + 1));
                var payload = new byte[sizeof(ushort)];
                BinaryPrimitives.WriteUInt16BigEndian(payload, checked((ushort)importId));
                userId = (uint)userFixups.Count;
                userFixups.Add(new PhyreUserFixup(
                    userFixups.Count, typeId, "PAssetReferenceImport",
                    (uint)payload.Length, userOffset, payload, null));
                userOffset += (uint)payload.Length;
                importedUserFixups.Add(importId, userId);
            }
            pointers.Add(new PhyrePointerFixup(
                (int)group, fromId, member, 0, 0, 0, 0, userId));
        }

        // An embedded PArray/PSharray stores its pointer in the second word.
        // Both Falcom's asset processor and every shipped CS1 model inspected
        // encode these links as a raw object offset, never as a packed member id.
        // The high bit selects the raw-offset numbering space and +4 selects the
        // pointer word after the array count.
        void PointArray(
            string fromClass,
            uint fromId,
            string member,
            string toClass,
            uint toId,
            uint count)
        {
            var field = Field(descriptors, fromClass, member);
            if (field is null) return;
            var source = 0x80000000u | (field.ValueOffset + sizeof(uint));
            pointers.Add(new PhyrePointerFixup(
                Group(fromClass), fromId, source,
                (uint)Group(toClass), toId, 0, count, null));
        }

        void SharedArrayElement(
            string fromClass,
            uint fromId,
            string member,
            string toClass,
            uint toId,
            uint elementIndex)
        {
            var field = Field(descriptors, fromClass, member);
            if (field is null) return;
            var source = 0x80000000u | (field.ValueOffset + sizeof(uint));
            pointers.Add(new PhyrePointerFixup(
                Group(fromClass), fromId, source,
                (uint)Group(toClass), toId, 0, elementIndex, null));
        }

        // Strings are serialized as array fixups at the storage offset in the
        // allocated class block, not as packed-namespace member ids.  A derived
        // class can start after its parent's block: PNode is the concrete case in
        // CS1 (m_name is described at +0x50, OffsetFromParent is 4, and the
        // official processor emits +0x4C).  Keeping this conversion in one place
        // makes the same rule serve every PString member.
        uint StringArraySource(string className, string member)
        {
            var descriptor = descriptors.First(value => value.Name == className);
            var field = Field(descriptors, className, member)
                ?? throw new InvalidOperationException(
                    $"'{className}' has no string member called '{member}'.");
            var inheritedOffset = descriptor.OffsetFromParent > 0
                ? checked((uint)descriptor.OffsetFromParent)
                : 0u;
            if (field.ValueOffset < inheritedOffset)
            {
                throw new InvalidOperationException(
                    $"'{className}.{member}' precedes its allocated class block.");
            }
            return 0x80000000u | (field.ValueOffset - inheritedOffset);
        }

        // The asset references: one for the cluster's type, then one naming each
        // object the game looks up by name.
        // Use the short-name convention the engine's own asset processor writes:
        // "{asset}.dae#Name" — not a path-qualified collada id. The game's
        // p_collada loader resolves these against the compiled-class registry
        // keyed by entry name, and a path prefix makes the two disagree.
        var colladaId = $"{model.AssetName}.dae#";
        // The names of the one cluster known to load, in ITS object order: material,
        // node, mesh, instance. That order had been changed to follow the order the
        // name STRINGS appear in the group's array data, which is not the order of the
        // objects those names belong to — the reference lists its strings scene-node
        // first and its objects material first. The names themselves do matter: the
        // instance is named after the node that carries it and the mesh adds "Shape"
        // to that same name, so the three agree.
        var named = new List<(string Class, uint Id, string Name)>
        {
            ("PMaterial", 0, colladaId + materialNames.FirstOrDefault("colladadx11Shader1")),
            ("PNode", 0, colladaId + "VisualSceneNode"),
            ("PMesh", 0, colladaId + model.AssetName + "Shape"),
            ("PMeshInstance", 0, colladaId + model.AssetName),
        };
        for (var more = 1; more < materialCount; more++)
        {
            named.Insert(more, ("PMaterial", (uint)more, colladaId + materialNames[more]));
        }
        if (physics is not null)
            named.Add(("PShape", 0, colladaId + model.AssetName + "Shape-PhysicsShape"));
        for (uint index = 0; index < named.Count; index++)
        {
            var (target, targetId, text) = named[checked((int)index)];
            Point("PAssetReference", index, "m_asset", target, targetId);
            PointAssetType("PAssetReference", index, "m_assetType", target);
            Name("PAssetReference", index, text);
        }
        // Falcom's processor serializes resource dependencies before the effect
        // that consumes them.  This is not cosmetic: the runtime builds the
        // import registry in list order, and resolving an effect before its
        // texture dependencies can leave the asset name passed to the registry
        // lookup null.  Preserve that dependency order explicitly instead of
        // making the shader's import id implicitly zero.
        // The donor's own pictures are kept only when the model brings none: every
        // buffer now points at its material's texture, so the donor's would be an
        // import naming a file the package no longer carries.
        var ownTextures = textureOf.Any(name => name is not null);
        var importAssets = new List<string>();
        foreach (var import in material?.Imports ?? Array.Empty<PhyreMaterialImport>())
        {
            if (ownTextures) continue;
            if (!import.Asset.StartsWith("shaders/", StringComparison.Ordinal)
                && !importAssets.Contains(import.Asset, StringComparer.Ordinal))
            {
                importAssets.Add(import.Asset);
            }
        }
        if (!importAssets.Contains(shader.ShaderAsset, StringComparer.Ordinal))
        {
            importAssets.Add(shader.ShaderAsset);
        }
        // Each material's own texture, so its buffer has an import to point at. The
        // list held only what the donor material named, which is one picture however
        // many materials the model has.
        foreach (var own in textureOf)
        {
            if (own is null) continue;
            var asset = $"map/images/{own}.dds";
            if (!importAssets.Contains(asset, StringComparer.Ordinal)) importAssets.Add(asset);
        }
        foreach (var import in material?.Imports ?? Array.Empty<PhyreMaterialImport>())
        {
            // Same rule as above: a donor picture is only carried when the model has
            // none of its own, or the cluster names a file the package does not hold.
            if (ownTextures
                && !import.Asset.StartsWith("shaders/", StringComparison.Ordinal))
            {
                continue;
            }
            if (!importAssets.Contains(import.Asset, StringComparer.Ordinal))
            {
                importAssets.Add(import.Asset);
            }
        }
        var shaderImportId = importAssets.IndexOf(shader.ShaderAsset);
        if (shaderImportId < 0)
        {
            throw new InvalidOperationException(
                $"Shader import '{shader.ShaderAsset}' was not registered.");
        }
        for (var index = 0; index < importAssets.Count; index++)
        {
            // A shader is imported as an effect variant, a texture as a texture.
            PointAssetType("PAssetReferenceImport", (uint)index, "m_targetAssetType",
                importAssets[index].StartsWith("shaders/", StringComparison.Ordinal)
                    ? "PEffectVariant"
                    : "PTexture2D");
            Name("PAssetReferenceImport", (uint)index, importAssets[index]);
        }

        for (uint index = 0; index < streams.Length; index++)
        {
            PointArray("PDataBlockD3D11", index, "m_streams", "PVertexStream", index, 1);
            // The target is the user fixup holding the semantic, and nothing else.
            // This used to name the stream's own group and object as a destination as
            // well — a fixup pointing at two things at once. Our reader threw that
            // half away, which is why nothing here noticed; the engine does not. It
            // writes a PVertexStream* into m_renderDataType and then calls through it
            // as a PRenderDataType, which is an invalid instruction pointer and
            // exactly the crash the game reports.
            pointers.Add(new PhyrePointerFixup(
                Group("PVertexStream"), index,
                MemberId(descriptors, "PVertexStream", "m_renderDataType"),
                0, 0, 0, 0,
                Semantic(SemanticName(streams[(int)index]))));
        }
        // Effect variants live in another cluster.  A normal object pointer
        // would bind the material to an unrelated local PAssetReference and is
        // accepted by our tolerant reader, but cannot be resolved by Phyre.
        // The wire payload is the big-endian index of the effect import.
        var firstBuffer = Group("PParameterBuffer");
        for (uint slot = 0; slot < materialCount; slot++)
        {
            ImportedPoint("PMaterial", slot, "m_effectVariant", (uint)shaderImportId);
            // Each material reaches its own buffer. The groups sit next to each
            // other because the layout is sorted, so the nth is the first plus n.
            pointers.Add(new PhyrePointerFixup(
                Group("PMaterial"), slot,
                MemberId(descriptors, "PMaterial", "m_parameterBuffer"),
                (uint)(firstBuffer + slot), 0, 0, 0, null));
        }
        // Its effect variant, unless the block already names it. A parameter block
        // carries its own references, and m_effectVariant is one of them — writing it
        // here as well emitted the same fixup twice, so the buffer had thirty pointers
        // where the model it copies has twenty-nine. The engine applies each fixup it
        // is given; a repeat writes over what the first one placed.
        // Every buffer is wired like the donor's, with one substitution: where the
        // donor named ITS texture, each buffer names the one its own material paints
        // with. That is the whole of what tells them apart — the parameter values,
        // the samplers and the definitions are the shader's and are shared.
        for (uint slot = 0; slot < materialCount; slot++)
        {
            var buffer = (uint)(firstBuffer + slot);
            var mine = slot < textureOf.Length && textureOf[slot] is { } ownTexture
                ? $"map/images/{ownTexture}.dds"
                : null;
            void BufferImport(string? member, uint rawOffset, uint importId)
            {
                if (member is not null)
                {
                    var field = Field(descriptors, "PParameterBuffer", member);
                    if (field is null) return;
                    ImportedPointAtGroup(buffer, MemberId(descriptors, "PParameterBuffer", member), importId);
                }
                else
                {
                    ImportedPointAtGroup(buffer, rawOffset, importId);
                }
            }
            if (material?.Imports.Any(value => value.Member == "m_effectVariant") != true)
            {
                BufferImport("m_effectVariant", 0, (uint)shaderImportId);
            }
            foreach (var import in material?.Imports ?? Array.Empty<PhyreMaterialImport>())
            {
                var asset = import.Asset;
                // A texture import becomes this material's texture; the shader's own
                // reference is left alone.
                if (mine is not null
                    && !asset.StartsWith("shaders/", StringComparison.Ordinal)
                    && import.Member != "m_effectVariant")
                {
                    asset = mine;
                }
                var at = importAssets.IndexOf(asset);
                if (at < 0) continue;
                BufferImport(import.Member, import.Source, (uint)at);
            }
            foreach (var pointer in material?.Pointers ?? Array.Empty<PhyreMaterialPointer>())
            {
                if (Group(pointer.TargetClass) < 0) continue;
                pointers.Add(new PhyrePointerFixup(
                    (int)buffer, 0, pointer.SourceOffset,
                    (uint)Group(pointer.TargetClass), pointer.TargetId, 0,
                    pointer.Count, null));
            }
        }
        if (false)
        {
        }
        // The buffer reaches its own sampler states and parameter definitions by raw
        // offset, exactly where the shader's block says they sit.
        foreach (var import in Array.Empty<PhyreMaterialImport>())
        {
            var at = importAssets.IndexOf(import.Asset);
            if (at < 0) continue;
            // Our own number when it is a member; the offset as it stood when it is
            // not, since an offset into the payload means the same in both files —
            // the payload is copied whole.
            if (import.Member is { } memberName)
            {
                ImportedPoint("PParameterBuffer", 0, memberName, (uint)at);
            }
            else
            {
                ImportedPointAt("PParameterBuffer", 0, import.Source, (uint)at);
            }
        }
        foreach (var pointer in Array.Empty<PhyreMaterialPointer>())
        {
            if (Group(pointer.TargetClass) < 0) continue;
            pointers.Add(new PhyrePointerFixup(
                Group("PParameterBuffer"), 0, pointer.SourceOffset,
                (uint)Group(pointer.TargetClass), pointer.TargetId, 0,
                pointer.Count, null));
        }
        PointArray("PMesh", 0, "m_meshSegments", "PMeshSegment", 0, (uint)packed.Count);
        // PMaterialSet is a PSharray<PMaterial *>: its count is in the object and
        // each element is an ordinary pointer carrying its array index. Shipped
        // static models do not emit a pointer-array fixup for this member.
        //
        // ONE ENTRY PER SEGMENT. The set is indexed by segment, so a mesh drawing a
        // hundred and seventy-one of them needs as many entries — a shipped map with
        // fourteen segments carries fourteen. This was fixed at one, which is only
        // ever right for a single-segment mesh; every prop authored so far had
        // exactly one, so the mistake stayed invisible until a map was written.
        // Each segment names ITS material — the set is indexed by segment, and this
        // is where a model with several materials stops wearing a single skin.
        for (uint segment = 0; segment < packed.Count; segment++)
        {
            var mine = segment < materialOfSegment.Length
                ? (uint)materialOfSegment[segment]
                : 0u;
            SharedArrayElement(
                "PMesh", 0, "m_defaultMaterials", "PMaterial",
                mine < materialCount ? mine : 0u, segment);
        }
        // A set of more than one entry also DECLARES itself, as a pointer-array
        // fixup. A shipped map's four meshes hold 1, 1, 6 and 6 materials, and only
        // the two with six carry that declaration — which is why the note here used
        // to say shipped models emit none: every model looked at had a single entry.
        // The element pointers alone leave the engine an array it was never told the
        // length of.
        if (packed.Count > 1)
        {
            var set = Field(descriptors, "PMesh", "m_defaultMaterials");
            if (set is not null)
            {
                pointerArrays.Add(new PhyreArrayFixup(
                    Group("PMesh"), 0,
                    0x80000000u | (set.ValueOffset + sizeof(uint)),
                    (uint)packed.Count, 0));
            }
        }
        Point("PMeshInstance", 0, "m_mesh", "PMesh", 0);
        Point("PMeshInstance", 0, "m_localToWorldMatrix", "PWorldMatrix", 0);
        // m_materialSet points at the PMaterialSet embedded at PMesh.+0x30.
        pointers.Add(new PhyrePointerFixup(
            Group("PMeshInstance"), 0,
            MemberId(descriptors, "PMeshInstance", "m_materialSet"),
            (uint)Group("PMesh"), 0,
            Field(descriptors, "PMesh", "m_defaultMaterials")!.ValueOffset,
            0, null));
        Point("PMeshInstance", 0, "m_bounds", "PMeshInstanceBounds", 0);
        PointArray(
            "PMeshInstance", 0, "m_segmentContext",
            "PMeshInstanceSegmentContext", 0, (uint)packed.Count);
        Point("PMeshInstanceBounds", 0, "m_meshInstance", "PMeshInstance", 0);
        Point("PMeshInstanceBounds", 0, "m_worldMatrix", "PWorldMatrix", 0);
        // The root holds the mesh node, and the mesh node holds the transform the
        // instance is placed by — the arrangement every shipped model uses.
        Point("PNode", 0, "m_firstChild", "PNode", 1);
        Point("PNode", 1, "m_parent", "PNode", 0);
        if (authoringLayout)
        {
            Point("PNode", 1, "m_firstChild", "PNode", 2);
            Point("PNode", 2, "m_parent", "PNode", 1);
            Point("PNode", 2, "m_worldMatrix", "PWorldMatrix", 0);
        }
        else
        {
            Point("PNode", 1, "m_worldMatrix", "PWorldMatrix", 0);
        }

        // Collision, when there is any: a model holds a rigid body, the body holds
        // a mesh shape, and the shape holds the collision triangles.
        if (physics is not null)
        {
            Point("PPhysicsModel", 0, "m_rigidBodies", "PPhysicsRigidBody", 0);
            Point("PPhysicsRigidBody", 0, "m_material", "PPhysicsMaterial", 0);
            // The node that carries a world matrix, not the scene root. Bullet takes
            // the body's transform from the target node — PhyrePhysicsRigidBodyBullet
            // reads node->getLocalToWorldMatrix() — and the root has none, so a body
            // aimed at it is a body placed by nothing. A shipped map aims each of its
            // bodies at the named node holding its surfaces: CA00, CK00, CS00.
            Point("PPhysicsRigidBody", 0, "m_targetNode", "PNode", authoringLayout ? 2u : 0u);
            Point("PPhysicsRigidBody", 0, "m_model", "PPhysicsModel", 0);
            Point("PPhysicsMesh", 0, "m_shape", "PShape", 0);

            // m_shapes is a shared array of POINTERS, so it takes a pointer-array
            // fixup declaring the array and one pointer per element, each carrying
            // its index. It is the only one of the three fixup kinds this writer
            // had never produced.
            var shapes = Field(descriptors, "PPhysicsRigidBody", "m_shapes");
            if (shapes is not null)
            {
                pointerArrays.Add(new PhyreArrayFixup(
                    Group("PPhysicsRigidBody"), 0,
                    0x80000000u | (shapes.ValueOffset + sizeof(uint)), 1, 0));
                pointers.Add(new PhyrePointerFixup(
                    Group("PPhysicsRigidBody"), 0,
                    0x80000000u | (shapes.ValueOffset + sizeof(uint)),
                    (uint)Group("PPhysicsMesh"), 0, 0, 0, null));
            }
        }
        for (uint segment = 0; segment < packed.Count; segment++)
        {
            PointArray("PMeshSegment", segment, "m_vertexData",
                "PDataBlockD3D11", (uint)firstStream[segment],
                (uint)packed[(int)segment].Streams.Count);
        }

        // Where each segment's indices begin, counted from the start of the index
        // region — which is what the segment states.
        var indexStart = new uint[packed.Count];
        for (int mesh = 0, at = 0; mesh < packed.Count; mesh++)
        {
            indexStart[mesh] = (uint)at;
            at += packed[mesh].IndexBuffers[0].Length;
        }

        var groups = new List<PhyreGroupContents>();
        foreach (var className in layout)
        {
            var descriptor = descriptors.First(value => value.Name == className);
            var count = className switch
            {
                "PAssetReference" => (uint)named.Count,
                "PDataBlockD3D11" or "PVertexStream" => (uint)streams.Length,
                "PMeshSegment" or "PMeshInstanceSegmentContext" => (uint)packed.Count,
                "PAssetReferenceImport" => (uint)importAssets.Count,
                // The visual scene root, and a node for the mesh under it. No model
                // the game ships has a single node: a0003 has four, a bench eight, a
                // lamppost seven, and in every one it is a CHILD node that owns the
                // world matrix and shares it with the mesh instance — the root owns
                // none. A lone unparented root is a shape the engine never meets.
                "PNode" => authoringLayout
                    ? 3u
                    : 2u,
                "PMaterial" when material is not null => (uint)materialCount,
                "PSamplerState" => (uint)(material?.SamplerStates.Count ?? 0),
                "PShaderParameterDefinition" => (uint)(material?.ParameterDefinitions.Count ?? 0),
                _ => 1u,
            };

            var objects = new List<PhyreObjectContents>();
            var running = 0u;
            for (uint id = 0; id < count; id++)
            {
                var members = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                // An array member is a count followed by a pointer: the count is
                // stated here, the pointer is left at zero for the fixup that
                // fills it. Writing the value across the whole field would
                // overwrite the pointer with nonsense.
                void Float(string member, float value)
                {
                    if (Field(descriptors, className, member) is null) return;
                    members[member] = BitConverter.GetBytes(value);
                }

                void Set(string member, uint value)
                {
                    var field = Field(descriptors, className, member);
                    if (field is null) return;
                    var width = (int)(field.Size * Math.Max(field.FixedArraySize, 1));
                    var bytes = new byte[width];
                    if (width == 1) bytes[0] = (byte)value;
                    else if (width >= 4) BitConverter.GetBytes(value).CopyTo(bytes, 0);
                    else if (width == 2) BitConverter.GetBytes((ushort)value).CopyTo(bytes, 0);
                    members[member] = bytes;
                }

                switch (className)
                {
                    case "PDataBlockD3D11":
                    {
                        var stream = streams[(int)id];
                        var owner = packed[streamMesh[(int)id]];
                        var bytes = (uint)stream.Stride * (uint)owner.VertexCount;
                        Set("m_stride", (uint)stream.Stride);
                        Set("m_elementCount", (uint)owner.VertexCount);
                        Set("m_dataSize", bytes);
                        Set("m_offsetInVertexBuffer", running);
                        Set("m_streams", 1);
                        // How many GPU buffers this block holds: one. Left at zero,
                        // the block describes a stride, a count and a size for data
                        // that is declared to live in no buffer at all, and the
                        // engine binds nothing — the geometry is in the file and
                        // never drawn. Every data block of every shipped model says
                        // one, with the pointer left null for the loader to fill.
                        Set("m_buffers", 1);
                        running += bytes;
                        break;
                    }
                    case "PMeshSegment":
                        // WHICH material this segment draws with — an index into its
                        // mesh's material set, not into the cluster's materials. Left
                        // at zero, every segment of a map painted itself with the
                        // first material however many the model carried, which is a
                        // map wearing one skin.
                        Set("m_materialIndex", (uint)(
                            id < materialOfSegment.Length ? materialOfSegment[id] : 0));
                        Set("m_primitiveType", TriangleList);
                        Set("m_matrixIndex", Unskinned);
                        Set("m_vertexData", (uint)packed[(int)id].Streams.Count);
                        // The index block sits inline in the segment: its count
                        // and type at 0x24 and 0x28, where it starts and how long
                        // it runs at 0x40 and 0x48.
                        members["m_indexData"] = IndexBlock(
                            (uint)model.Meshes[(int)id].Indices.Length,
                            packed[(int)id].SixteenBitIndices,
                            (uint)packed[(int)id].IndexBuffers[0].Length,
                            indexStart[(int)id],
                            (uint)Math.Max(model.Meshes[(int)id].Vertices.Count - 1, 0),
                            Field(descriptors, className, "m_indexData"));
                        break;
                    case "PMesh":
                        Set("m_meshSegments", (uint)packed.Count);
                        Set("m_defaultMaterials", (uint)packed.Count);
                        break;
                    case "PMeshInstance":
                        Set("m_segmentContext", (uint)packed.Count);
                        break;
                    case "PMeshInstanceBounds":
                        members["m_min"] = Vector(Bounds(model).Min);
                        members["m_size"] = Vector(Bounds(model).Size);
                        break;
                    case "PNode":
                        members["m_localMatrix"] = Identity4x4();
                        break;
                    case "PPhysicsRigidBody" when physics is not null:
                        // A rotation of nothing is not a rotation: a quaternion of
                        // four zeros has no length, and the body it orients collapses.
                        // Shipped maps write the identity, w = 1.
                        members["m_initialOrientation"] = Quaternion();
                        // The frame the body's mass sits in. Twelve zeros are not a
                        // transformation: shipped bodies carry a real one, whose
                        // translation is where their collision node stands. Ours sits
                        // at the origin, so the identity is what it is.
                        members["m_massFrameTransform"] = Identity3x4();
                        // And a scale of zero flattens the shape to a point, which is
                        // what a collision mesh that never stops anything looks like.
                        members["m_scale"] = Scale();
                        Set("m_collisionGroup", physics.CollisionGroup);
                        Set("m_enabled", physics.Enabled ? 1u : 0u);
                        Set("m_rigidBodyType", physics.RigidBodyType);
                        // A mass is a float. Writing the integer 1 into it gives
                        // 1.4e-45, which is not a mass at all.
                        Float("m_mass", 1f);
                        Set("m_shapes", 1);
                        break;
                    case "PPhysicsMesh" when physics is not null:
                        Set("m_type", MeshShape);
                        Set("m_hollow", 1);
                        Float("m_mass", 1f);
                        // A shape with no scale collapses to nothing, so it is
                        // stated rather than left at zero.
                        members["m_scale"] = Scale();
                        break;
                    case "PPhysicsMaterial" when physics is not null:
                        members["m_dynamicFriction"] = BitConverter.GetBytes(physics.DynamicFriction);
                        members["m_staticFriction"] = BitConverter.GetBytes(physics.StaticFriction);
                        members["m_restitution"] = BitConverter.GetBytes(physics.Restitution);
                        break;
                    case "PShape" when physics is not null:
                        Set("m_vertexCount", (uint)physics.Vertices.Count);
                        Set("m_indexCount", (uint)physics.Indices.Count);
                        Set("m_vertexFormat", 2);
                        // 0x0C, sixteen-bit — the only format shipped maps use, on
                        // every shape of every map looked at. They stay under the
                        // limit by splitting their collision into several shapes, the
                        // largest around three thousand vertices.
                        if (physics.Vertices.Count >= 0x10000)
                        {
                            throw new InvalidOperationException(
                                $"A collision shape of {physics.Vertices.Count} vertices"
                                + " cannot be indexed with sixteen bits, and no shipped"
                                + " map uses anything else. Split it into shapes of"
                                + " fewer than 65 536 vertices, as the game does.");
                        }
                        Set("m_indexFormat", 12u);
                        Set("m_vertexData", (uint)(physics.Vertices.Count * 12));
                        Set("m_indices", (uint)(physics.Indices.Count
                            * (physics.Vertices.Count < 0x10000 ? 2 : 4)));
                        break;
                    case "PVertexStream":
                        Set("m_type", StreamType(streams[(int)id]));
                        break;
                    case "PParameterBuffer":
                        Set("m_parameterBufferSize", material?.ParameterBufferSize ?? 0);
                        Set("m_tweakableShaderParameterDefinitions", material?.DefinitionCount ?? 0);
                        break;
                    case "PWorldMatrix":
                        members["m_matrix"] = Identity4x3();
                        break;
                }

                // A parameter buffer is a header class: the parameters live in the
                // object itself, past the class. A sampler state and a parameter
                // definition are plain objects, and both belong to the shader — so
                // all three are stated as the bytes the shader's block says, rather
                // than re-derived field by field from a layout nothing here knows.
                // A parameter buffer is a header class: what it carries goes PAST the
                // class, which is what a trailing payload is for. A sampler state and
                // a parameter definition are ordinary objects of exactly their class
                // size — their bytes are the object, not something after it. Handing
                // those over as a payload appends them to a zeroed class and doubles
                // the object, which the engine walks straight off the end of.
                var trailing = className == "PParameterBuffer" && material is not null
                    ? Beyond(material.ParameterBufferObject, ClassSize(descriptors, className))
                    : ReadOnlyMemory<byte>.Empty;
                if (material is not null && className is "PSamplerState" or "PShaderParameterDefinition")
                {
                    var donor = className == "PSamplerState"
                        ? material.SamplerStates[(int)id]
                        : material.ParameterDefinitions[(int)id];
                    members.Clear();
                    foreach (var (name, value) in MembersOf(descriptors, className, donor))
                    {
                        members[name] = value;
                    }
                }
                objects.Add(new PhyreObjectContents(className, members, trailing));
            }

            // The names this group holds, laid one after another, and an array
            // fixup per name pointing at where it starts.
            var arrays = new MemoryStream();
            var group = Group(className);
            if (className == "PNode")
            {
                // The mesh node carries a name, as every shipped one does — the same
                // name the mesh instance is exported under. The root carries none,
                // also as shipped.
                void NodeName(uint objectId, string name)
                {
                    var at = (uint)arrays.Length;
                    arrays.Write(System.Text.Encoding.ASCII.GetBytes(name + " "));
                    arrayFixups.Add(new PhyreArrayFixup(
                        group, objectId, StringArraySource(className, "m_name"), 0, at));
                }

                // The scene node names itself, and the mesh node carries the name the
                // mesh instance is exported under — not that name plus "Shape", which
                // is the mesh's own reference and not a node's.
                NodeName(1, "VisualSceneNode1");
                if (authoringLayout)
                    NodeName(2, model.AssetName);
            }
            if (className == "PMeshInstance"
                && !authoringLayout)
            {
                // CS1's shipped static meshes all provide one game-material id per
                // segment. The array pointer is serialized at m_gameMaterialIDs+4
                // even though the compact 64-byte object data does not extend that
                // far; the loader fixes it in its larger runtime PMeshInstance.
                // Falcom's generic AssetProcessor omits this CS1-specific array,
                // but models bound to ed8.fx do not.
                var member = Field(descriptors, className, "m_gameMaterialIDs")
                    ?? throw new InvalidOperationException(
                        "PMeshInstance has no m_gameMaterialIDs member.");
                var offset = (uint)arrays.Length;
                for (var segment = 0; segment < packed.Count; segment++)
                    arrays.Write(BitConverter.GetBytes(0u));
                arrayFixups.Add(new PhyreArrayFixup(
                    group,
                    0,
                    0x80000000u | (member.ValueOffset + sizeof(uint)),
                    (uint)packed.Count,
                    offset));
            }
            if (className == "PShaderParameterDefinition" && material is not null)
            {
                // A definition's name lives in the group's array data, and the fixup
                // beside it says where. Carrying the objects alone leaves every name
                // pointing into a region that is not there.
                arrays.Write(material.DefinitionArrayData.Span);
                foreach (var array in material.DefinitionArrays)
                {
                    arrayFixups.Add(new PhyreArrayFixup(
                        group, array.ObjectId, StringArraySource(className, "m_name"),
                        array.Count, array.Offset));
                }
            }
            if (className == "PShape" && physics is not null)
            {
                // The collision triangles: positions, then indices, in this
                // group's own array data, with an array fixup naming each run.
                var vertexAt = (uint)arrays.Length;
                foreach (var point in physics.Vertices)
                {
                    arrays.Write(BitConverter.GetBytes(point.X));
                    arrays.Write(BitConverter.GetBytes(point.Y));
                    arrays.Write(BitConverter.GetBytes(point.Z));
                }
                var indexAt = (uint)arrays.Length;
                var narrow = physics.Vertices.Count < 0x10000;
                foreach (var index in physics.Indices)
                {
                    arrays.Write(narrow
                        ? BitConverter.GetBytes((ushort)index)
                        : BitConverter.GetBytes((uint)index));
                }
                arrayFixups.Add(new PhyreArrayFixup(group, 0,
                    0x80000000u | (Field(descriptors, className, "m_vertexData")!.ValueOffset + 4),
                    (uint)physics.Vertices.Count * 12, vertexAt));
                arrayFixups.Add(new PhyreArrayFixup(group, 0,
                    0x80000000u | (Field(descriptors, className, "m_indices")!.ValueOffset + 4),
                    (uint)(physics.Indices.Count * (narrow ? 2 : 4)), indexAt));
            }
            if (names.TryGetValue(group, out var mine))
            {
                foreach (var (id, text) in mine)
                {
                    var offset = (uint)arrays.Length;
                    var bytes = System.Text.Encoding.ASCII.GetBytes(text);
                    arrays.Write(bytes);
                    arrays.WriteByte(0);
                    // Every name starts on an even offset. The names were packed back
                    // to back, so whether the next one landed even was decided by the
                    // length of the one before it — and a cluster whose second name
                    // began on an odd byte crashed the game while an otherwise
                    // identical one loaded. Measured: the asset processor's own cube
                    // puts its shader name at 14 after a twelve-character texture name
                    // (13 bytes with its terminator), leaving exactly this gap.
                    if (arrays.Length % 2 != 0) arrays.WriteByte(0);
                    // Only PAssetReference and PAssetReferenceImport carry m_id;
                    // other classes (PMeshInstance, PNode) that have names do not.
                    // Looking up m_id on those returns a garbage member index that
                    // the engine misinterprets.
                    var idMember = Field(descriptors, className, "m_id");
                    if (idMember is not null)
                    {
                        arrayFixups.Add(new PhyreArrayFixup(
                            group, id, StringArraySource(className, "m_id"), 0, offset));
                    }
                }
            }
            groups.Add(new PhyreGroupContents(className, objects, arrays.ToArray()));
        }

        // A class the engine calls a header class puts a child count in the
        // header class section — four bytes per such group, even when it declares
        // no children. Leaving the section out makes every table after it start
        // four bytes early, which reads as a truncated fixup stream.
        var headerClasses = new MemoryStream();
        var headerRecords = new MemoryStream();
        foreach (var className in layout)
        {
            if (!PhyreSchemaLibrary.IsHeaderClass(className)) continue;
            var children = className == "PParameterBuffer" && material is not null
                ? material.Children
                : Array.Empty<PhyreMaterialChild>();
            headerClasses.Write(BitConverter.GetBytes((uint)children.Count));
            foreach (var child in children)
            {
                // The donor numbered its types against its own tables; ours are
                // built from the classes this model uses, so every id is restated.
                headerRecords.Write(BitConverter.GetBytes(
                    LookUpTypeId(typeIds, classNames, child.TypeName)));
                headerRecords.Write(BitConverter.GetBytes(child.Offset));
                headerRecords.Write(BitConverter.GetBytes(child.Flags));
                headerRecords.Write(BitConverter.GetBytes(child.Count));
            }
        }
        headerRecords.Position = 0;
        headerRecords.CopyTo(headerClasses);

        // The payload is two regions, indices then vertices — that order is what
        // the header's two sizes describe and what every offset counts from.
        var payload = new MemoryStream();
        foreach (var mesh in packed)
        foreach (var buffer in mesh.IndexBuffers) payload.Write(buffer);
        var indexRegion = (uint)payload.Length;
        foreach (var stream in streams) payload.Write(stream.Data);
        var vertexRegion = (uint)payload.Length - indexRegion;

        // Those two sizes live past the words the cluster writer names, at 72
        // and 76, so they travel in the header's tail.
        var tail = new byte[84 - 17 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(72 - 17 * sizeof(uint)), indexRegion);
        BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(76 - 17 * sizeof(uint)), vertexRegion);

        // User fixups in the order the game writes them: every class descriptor, then
        // every imported reference, then the stream semantics. Checked on four shipped
        // clusters, which group them exactly so; ours interleaved them because they
        // were created as they were needed.
        //
        // Reordering means renumbering: a pointer names its user fixup by index, and
        // the data blob is read at the offset each one states, so both are rebuilt.
        var rank = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["PClassDescriptor"] = 0,
            ["PAssetReferenceImport"] = 1,
            ["PRenderDataType"] = 2,
        };
        int RankOf(PhyreUserFixup fixup)
        {
            // Both numbering spaces again: a user fixup's type is a type when its id
            // is below the type count and a class above it. Reading only the first
            // left every class descriptor unranked, and sorted them last.
            var classIndex = (int)fixup.TypeId - types.Count - 1;
            var name = fixup.TypeId < types.Count
                ? types[(int)fixup.TypeId]
                : classIndex >= 0 && classIndex < classNames.Length
                    ? classNames[classIndex]
                    : fixup.TypeName;
            return name is not null && rank.TryGetValue(name, out var value) ? value : 3;
        }

        var ordered = userFixups
            .Select((fixup, index) => (fixup, index))
            .OrderBy(pair => RankOf(pair.fixup))
            .ThenBy(pair => pair.index)
            .ToArray();
        var moved = new uint[userFixups.Count];
        var reordered = new List<PhyreUserFixup>(ordered.Length);
        var runningOffset = 0u;
        for (var index = 0; index < ordered.Length; index++)
        {
            var (fixup, was) = ordered[index];
            moved[was] = (uint)index;
            reordered.Add(fixup with { Id = index, DataOffset = runningOffset });
            runningOffset += fixup.DeclaredSize;
        }
        for (var index = 0; index < pointers.Count; index++)
        {
            if (pointers[index].UserFixupId is not { } id) continue;
            pointers[index] = pointers[index] with { UserFixupId = moved[id] };
        }
        userFixups.Clear();
        userFixups.AddRange(reordered);

        return new PhyreClusterContents(
            types,
            groups,
            new PhyreFixupSet(
                pointerArrays, pointers, arrayFixups, userFixups, 0),
            userFixups,
            headerClasses.ToArray(),
            payload.ToArray(),
            new PhyreNamespaceWriter.UnmodelledHeader(0x1020304, 0x8D7, 0, 0),
            tail,
            schemaProfile);
    }

    private static (Vector3 Min, Vector3 Size) Bounds(PhyreModelSource model)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        foreach (var mesh in model.Meshes)
        foreach (var vertex in mesh.Vertices)
        {
            lo = Vector3.Min(lo, vertex.Position);
            hi = Vector3.Max(hi, vertex.Position);
        }
        return lo.X > hi.X ? (Vector3.Zero, Vector3.Zero) : (lo, hi - lo);
    }

    /// <summary>A unit scale, in the sixteen bytes the shape keeps it in.</summary>
    /// <summary>
    /// The identity, as a PMatrix4x3 — three Vector4 whose W components hold the
    /// FIRST column between them:
    ///
    ///   m_col1 = (col1.x, col1.y, col1.z, col0.x)
    ///   m_col2 = (col2.x, col2.y, col2.z, col0.y)
    ///   m_col3 = (translation, col0.z)
    ///
    /// so the three ones land at 4, 12 and 24 rather than on a diagonal. Written on
    /// the diagonal, as it was, the matrix is not the identity and not a rotation —
    /// and a shipped body has its ones at exactly 4, 12 and 24.
    /// </summary>
    private static byte[] Identity3x4()
    {
        var bytes = new byte[48];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), 1f);   // col1.y
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), 1f);  // col0.x
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(24), 1f);  // col2.z
        return bytes;
    }

    /// <summary>The identity rotation, x=y=z=0 and w=1.</summary>
    private static byte[] Quaternion()
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), 1f);
        return bytes;
    }

    private static byte[] Scale()
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, 1f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8), 1f);
        return bytes;
    }

    private static byte[] Vector(Vector3 value)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), value.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8), value.Z);
        return bytes;
    }

    private static byte[] Identity4x4()
    {
        var bytes = new byte[64];
        for (var index = 0; index < 4; index++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * 20), 1f);
        return bytes;
    }

    /// <summary>
    /// Cold Steel's DX11 runtime layout for an identity <c>PWorldMatrix</c>.
    /// This is not the row-major layout used by <see cref="Matrix4x4"/>:
    /// unrelated shipped props and maps consistently store the three unit
    /// components at byte offsets 4, 12 and 24, with translation at 32..40.
    /// </summary>
    private static byte[] Identity4x3()
    {
        var bytes = new byte[48];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(24), 1f);
        return bytes;
    }

    /// <summary>
    /// What a stream says it holds. Read off the game's own files: a three-float
    /// stream states 2, a two-float stream states 3.
    /// </summary>
    /// <summary>
    /// What a stream says it holds. The engine reads this as two numbers in one
    /// byte: <c>type / 4</c> picks the scalar size — 0 means four bytes — and
    /// <c>type % 4 + 1</c> the number of components. So three floats is 2, two
    /// floats is 1, four floats is 3, which is what moviescreen states for its
    /// blocks of stride 12, 8 and 16.
    /// </summary>
    /// <summary>
    /// Where a type sits in this cluster's own type table. A user fixup names its
    /// type by that index, and the table is built from the classes the model happens
    /// to use — so it is not the same table from one model to the next, and a number
    /// written by hand lands on whatever type happens to be there.
    /// </summary>
    /// <summary>
    /// An object of the shader's, taken apart into the members its class declares.
    ///
    /// Stated member by member rather than as one block, because that is what an
    /// object is here — and because a member the schema knows and the donor's bytes
    /// disagree about would then say so, instead of being copied over in silence.
    /// </summary>
    private static IEnumerable<(string Name, byte[] Value)> MembersOf(
        IReadOnlyList<PhyreClassDescriptor> descriptors,
        string className,
        ReadOnlyMemory<byte> bytes)
    {
        var descriptor = descriptors.First(value => value.Name == className);
        foreach (var member in PhyreObjectWriter.Chain(descriptor, descriptors))
        {
            var span = checked((int)(member.Size * Math.Max(member.FixedArraySize, 1)));
            if (member.ValueOffset + span > bytes.Length) continue;
            yield return (member.Name, bytes.Span.Slice((int)member.ValueOffset, span).ToArray());
        }
    }

    /// <summary>What a class measures, so a header object's extra bytes start after it.</summary>
    private static int ClassSize(IReadOnlyList<PhyreClassDescriptor> descriptors, string className)
        => (int)(descriptors.First(value => value.Name == className).Size);

    private static ReadOnlyMemory<byte> Beyond(ReadOnlyMemory<byte> bytes, int start)
        => start >= bytes.Length ? ReadOnlyMemory<byte>.Empty : bytes[start..];

    private static uint LookUpTypeId(
        IReadOnlyDictionary<string, uint> typeIds,
        IReadOnlyList<string> classNames,
        string typeName)
    {
        if (typeIds.TryGetValue(typeName, out var id)) return id;

        // Two numbering spaces share one field, as everywhere else in this format:
        // an id below the type count names a type, and anything above it names a
        // class, one-based past the types. PClassDescriptor is a class, so it is
        // never found in the first table.
        var classIndex = -1;
        for (var index = 0; index < classNames.Count; index++)
        {
            if (string.Equals(classNames[index], typeName, StringComparison.Ordinal))
            {
                classIndex = index;
                break;
            }
        }
        if (classIndex >= 0) return checked((uint)(typeIds.Count + classIndex + 1));

        throw new InvalidOperationException(
            $"This cluster declares neither a type nor a class called '{typeName}',"
            + " so nothing in it can refer to one.");
    }

    private static uint StreamType(PhyrePackedStream stream) => stream.Format switch
    {
        "Float32x2" => 1,
        "Float32x3" => 2,
        "Float32x4" => 3,
        _ => throw new InvalidOperationException(
            $"Nothing here knows how the engine numbers a stream of '{stream.Format}'."
            + " Guessing would give a stream the wrong width, which reads as"
            + " garbage rather than as an error."),
    };


    /// <summary>
    /// What the engine calls each stream. These are the names its own files use,
    /// and our reader maps them back: Vertex for a position, Binormal for a
    /// bitangent.
    /// </summary>
    private static string SemanticName(PhyrePackedStream stream) => stream.Semantic switch
    {
        VertexSemantic.Position => "Vertex",
        VertexSemantic.Normal => "Normal",
        VertexSemantic.Tangent => "Tangent",
        VertexSemantic.Bitangent => "Binormal",
        VertexSemantic.TextureCoordinate => "ST",
        VertexSemantic.Color => "Color",
        _ => throw new InvalidOperationException(
            $"Nothing here knows what the engine calls a {stream.Semantic} stream."),
    };

    /// <summary>
    /// The index block a segment carries inline: how many indices, how wide they
    /// are, where they start in the payload and how long they run.
    /// </summary>
    private static byte[] IndexBlock(
        uint count, bool sixteenBit, uint size, uint start, uint highestIndex,
        PhyreDataMember? field)
    {
        var block = new byte[field?.Size ?? 52];
        void At(int inSegment, uint value)
        {
            var at = inSegment - (int)(field?.ValueOffset ?? 28);
            if (at >= 0 && at + sizeof(uint) <= block.Length)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(at), value);
            }
        }
        // The highest index the segment uses. Left at zero it says the segment
        // reaches no vertex at all. Every shipped block states it, and states it as
        // one less than the vertex count: 213 for 214, 19 for 20, 3 for 4.
        At(0x20, highestIndex);
        At(0x24, count);
        // Sixteen-bit indices are stated as 12 — read off three shipped models that
        // agree, not derived. The earlier 4 came from applying the vertex-stream rule
        // (scalar size in the high bits, components in the low) to a block that does
        // not use it.
        if (!sixteenBit)
        {
            throw new InvalidOperationException(
                "Nothing here knows how a thirty-two-bit index block states its type."
                + " Every shipped block seen uses sixteen-bit indices, and guessing the"
                + " other value would hand the engine a format it reads wrongly rather"
                + " than an error.");
        }
        At(0x28, 12u);
        At(0x40, start);
        At(0x48, size);
        // One index buffer, as every shipped block says. The same field, and the same
        // omission, as PDataBlockD3D11.m_buffers — fixed there and missed here.
        At(0x30, 1u);
        return block;
    }

    private static PhyreDataMember? Field(
        IReadOnlyList<PhyreClassDescriptor> descriptors, string className, string member)
    {
        var descriptor = descriptors.FirstOrDefault(value => value.Name == className);
        return descriptor is null
            ? null
            : PhyreObjectWriter.Chain(descriptor, descriptors)
                .FirstOrDefault(value => value.Name == member);
    }

    private static uint MemberId(
        IReadOnlyList<PhyreClassDescriptor> descriptors, string className, string member)
        => (uint)(Field(descriptors, className, member)?.Index ?? 0);

}
