using System.Numerics;
using ED8Editor.Models;
using ED8Editor.Phyre.Authoring;

namespace ED8Editor.Application;

/// <summary>What converting an imported scene had to change, and what it dropped.</summary>
/// <param name="FlippedTriangles">
/// Triangles whose winding disagreed with their own normals and were turned
/// round. A mesh that arrives wound the other way renders inside out without
/// erroring, so this is corrected rather than reported and left.
/// </param>
/// <param name="DroppedInfluences">
/// Influences past the fourth on a vertex. The game gives a vertex four joints;
/// an exchange format gives as many as the author painted.
/// </param>
public sealed record ImportedModelConversion(
    PhyreModelSource Model,
    int FlippedTriangles,
    int DroppedInfluences,
    IReadOnlyList<string> Notes);

/// <summary>
/// Turns what an importer read into what the model writer takes.
///
/// The two sides are deliberately ignorant of each other — <c>ImportedModelScene</c>
/// mentions no Phyre, <c>PhyreModelSource</c> mentions no FBX — so something has
/// to state the target basis. That basis was measured, not assumed:
///
/// <list type="bullet">
/// <item><b>Y is up, and the unit is the metre.</b> A shipped character measures
/// 1.74 along Y by 1.15 across and 0.37 deep; map a0000 measures 100 by 116 of
/// ground and 12 of height.</item>
/// <item><b>A triangle is wound so that <c>cross(b-a, c-a)</c> points the way its
/// vertex normals do.</b> On map a0000, 1468 of 1468 triangles agree; on ply000,
/// 1766 against 38.</item>
/// </list>
///
/// Both matter because getting either wrong produces a model that loads, draws,
/// and is silently wrong — lying on its side, or inside out.
/// </summary>
public static class ImportedModelAdapter
{
    private const int JointsPerVertex = 4;

    public static ImportedModelConversion Convert(ImportedModelScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var notes = new List<string>();
        var basis = Basis(scene.CoordinateSystem, notes);
        var joints = Joints(scene, basis);
        var flipped = 0;
        var dropped = 0;

        // Which meshes are collision rather than scenery. The game hangs its
        // collision off nodes a rigid body targets — r0510's are CA00, CA01, CK00,
        // CS00 and CS01, named as such both in the cluster it ships and in what an
        // extraction writes beside the model. They are meant to stop the player and
        // never to be seen; drawn, they are the walls standing across the map.
        var collisionMeshes = CollisionMeshes(scene);

        // Where each mesh's node puts it. Assimp is not asked to pre-transform
        // vertices — that would flatten the node graph, and the graph is what the
        // collision nodes are named in — so a mesh arrives in its own local space
        // and it is here that its node's chain has to be applied.
        //
        // Without it a map draws every mesh at the origin of its own node: r0510's
        // forty-six tufts of grass all pile onto one spot, and a bridge sits at the
        // wrong height under a walkable surface, because the collision surfaces
        // happen to hang off untransformed nodes and so land correctly while the
        // scenery does not.
        var placements = Placements(scene, basis);

        var meshes = new List<PhyreMeshSource>();
        for (var index = 0; index < scene.Meshes.Count; index++)
        {
            var mesh = scene.Meshes[index];
            var source = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < scene.Materials.Count
                ? scene.Materials[mesh.MaterialIndex]
                : null;
            var material = source?.Name ?? "material" + index;

            // The image the material paints with. The importer already resolves it,
            // bytes and all — even when the file sits beside the model rather than
            // inside it — and this is where it used to be dropped, leaving the
            // authored model with a material name and no picture to go with it.
            PhyreMeshTexture? texture = null;
            if (source is not null
                && source.TextureBindings.TryGetValue(ImportedTextureUsage.BaseColor, out var slot)
                && slot >= 0 && slot < scene.Textures.Count)
            {
                var found = scene.Textures[slot];
                if (found.EncodedData.Length != 0)
                {
                    texture = new PhyreMeshTexture(
                        Path.GetFileNameWithoutExtension(found.Name), found.EncodedData);
                }
            }

            var placement = placements[index];
            var vertices = mesh.Vertices
                .Select(vertex => Vertex(vertex, placement, ref dropped))
                .ToArray();
            var indices = Wound(mesh, vertices, placement, ref flipped);
            collisionMeshes.TryGetValue(index, out var collisionNode);
            meshes.Add(new PhyreMeshSource(
                material, vertices, indices, texture,
                collisionNode is not null, collisionNode));
        }

        if (scene.Animations.Count != 0)
        {
            notes.Add(
                $"{scene.Animations.Count} animation clips came in and are not converted here;"
                + " a character plays the game's own clips once its rig is mapped.");
        }

        return new ImportedModelConversion(
            new PhyreModelSource(scene.Name, meshes, joints), flipped, dropped, notes);
    }

    /// <summary>
    /// How to get from the scene's stated basis to the game's. Only the
    /// vertical axis and the unit are handled, because those are the two the
    /// game's own files were measured for; anything else is said out loud rather
    /// than quietly approximated.
    /// </summary>
    private static Matrix4x4 Basis(ImportedCoordinateSystem system, List<string> notes)
    {
        var scale = system.UnitScaleMeters <= 0 ? 1f : system.UnitScaleMeters;
        var transform = Matrix4x4.CreateScale(scale);
        switch (system.UpAxis)
        {
            case ImportedUpAxis.Y:
                break;
            case ImportedUpAxis.Z:
                // Z-up to Y-up: turn a quarter about X, so what was up stays up.
                transform *= Matrix4x4.CreateRotationX(-MathF.PI / 2f);
                notes.Add("The scene was Z-up and has been turned a quarter about X to sit Y-up.");
                break;
            default:
                notes.Add(
                    $"The scene states {system.UpAxis} as its up axis, which nothing here knows"
                    + " how to reorient; it has been left as it came and will very likely"
                    + " arrive lying down.");
                break;
        }
        if (Math.Abs(scale - 1f) > 1e-6f)
        {
            notes.Add($"Positions were scaled by {scale} to bring the scene into metres.");
        }
        return transform;
    }

    private static PhyreVertexSource Vertex(
        ImportedVertex vertex, Matrix4x4 basis, ref int dropped)
    {
        // One tangent frame per set of texture coordinates, as the game stores
        // them. An exchange format carries a single frame, so it is repeated:
        // saying the same thing for each set is honest, where leaving later sets
        // at zero would quietly flatten their lighting.
        var sets = vertex.TexCoords.Count == 0
            ? new[] { new PhyreTexCoordSet(Vector2.Zero, Vector3.UnitX, Vector3.UnitZ) }
            : vertex.TexCoords
                .Select(uv => new PhyreTexCoordSet(
                    uv,
                    Vector3.TransformNormal(vertex.Tangent, basis),
                    Vector3.TransformNormal(vertex.Bitangent, basis)))
                .ToArray();

        var strongest = vertex.Influences
            .Where(influence => influence.Weight > 0f)
            .OrderByDescending(influence => influence.Weight)
            .ToArray();
        if (strongest.Length > JointsPerVertex) dropped += strongest.Length - JointsPerVertex;
        var kept = strongest.Take(JointsPerVertex).ToArray();
        var total = kept.Sum(influence => influence.Weight);

        // A vertex nothing binds carries no influences at all, rather than four
        // zeroes: joint zero is a real joint, and claiming it would be a claim.
        if (kept.Length == 0)
        {
            return new PhyreVertexSource(
                Vector3.Transform(vertex.Position, basis),
                Vector3.Normalize(Vector3.TransformNormal(vertex.Normal, basis)),
                sets,
                Array.Empty<int>(),
                Array.Empty<float>());
        }

        var joints = new int[JointsPerVertex];
        var weights = new float[JointsPerVertex];
        for (var slot = 0; slot < kept.Length; slot++)
        {
            joints[slot] = kept[slot].NodeIndex;
            weights[slot] = total > 0f ? kept[slot].Weight / total : 0f;
        }

        return new PhyreVertexSource(
            Vector3.Transform(vertex.Position, basis),
            Vector3.Normalize(Vector3.TransformNormal(vertex.Normal, basis)),
            sets,
            joints,
            weights);
    }

    /// <summary>
    /// The triangles, turned round where their winding disagrees with their own
    /// normals. Mirroring a scene — which a change of basis can do — reverses
    /// winding, so this has to be settled after the transform, not before.
    /// </summary>
    private static int[] Wound(
        ImportedMesh mesh,
        IReadOnlyList<PhyreVertexSource> vertices,
        Matrix4x4 basis,
        ref int flipped)
    {
        var indices = mesh.Indices.ToArray();
        for (var at = 0; at + 2 < indices.Length; at += 3)
        {
            var a = indices[at];
            var b = indices[at + 1];
            var c = indices[at + 2];
            if (a < 0 || b < 0 || c < 0
                || a >= vertices.Count || b >= vertices.Count || c >= vertices.Count)
            {
                continue;
            }
            var geometric = Vector3.Cross(
                vertices[b].Position - vertices[a].Position,
                vertices[c].Position - vertices[a].Position);
            if (geometric.LengthSquared() <= 1e-12f) continue;
            var outward = vertices[a].Normal + vertices[b].Normal + vertices[c].Normal;
            if (Vector3.Dot(geometric, outward) >= 0) continue;
            indices[at + 1] = c;
            indices[at + 2] = b;
            flipped++;
        }
        return indices;
    }

    /// <summary>
    /// The scene's nodes as a skeleton. Every node is kept, in scene order, so a
    /// vertex influence keeps pointing at the same joint; a node no skin binds
    /// simply gets an identity bind matrix.
    /// </summary>
    /// <summary>
    /// The matrix each mesh's vertices are to be written through: its node's whole
    /// chain up to the root, with the change of basis at the top.
    ///
    /// A skinned mesh is left on the basis alone. Its vertices are in bind space
    /// and the skeleton is what moves them; putting the node's transform in as well
    /// would apply the same placement twice.
    /// </summary>
    private static Matrix4x4[] Placements(ImportedModelScene scene, Matrix4x4 basis)
    {
        var nodes = scene.Nodes;
        var world = new Matrix4x4[nodes.Count];
        for (var at = 0; at < nodes.Count; at++)
        {
            var node = nodes[at];
            // A parent always precedes its children in the flattened order, so one
            // pass suffices; a node that claims a later parent falls back to the
            // basis rather than reading a matrix that is not built yet.
            world[at] = node.ParentIndex >= 0 && node.ParentIndex < at
                ? node.LocalTransform * world[node.ParentIndex]
                : node.LocalTransform * basis;
        }

        var placements = new Matrix4x4[scene.Meshes.Count];
        Array.Fill(placements, basis);
        var placed = new bool[scene.Meshes.Count];
        for (var at = 0; at < nodes.Count; at++)
        {
            foreach (var mesh in nodes[at].MeshIndices)
            {
                if (mesh < 0 || mesh >= placements.Length || placed[mesh]) continue;
                if (scene.Meshes[mesh].Skin is not null) { placed[mesh] = true; continue; }
                placements[mesh] = world[at];
                placed[mesh] = true;
            }
        }
        return placements;
    }

    /// <summary>
    /// The meshes that are collision rather than scenery, by the node they hang from.
    ///
    /// A map's collision lives under nodes a rigid body targets. Their names follow
    /// the exporter's convention — two letters and two digits, CA00, CK00, CS01 —
    /// which is what r0510 uses in the cluster the game ships and in the physics data
    /// an extraction writes. Nothing else in a map is named that way.
    ///
    /// It is a convention, so it is reported rather than applied silently: an import
    /// says which meshes it set aside, and a model that names things differently
    /// simply has none.
    /// </summary>
    private static Dictionary<int, string> CollisionMeshes(ImportedModelScene scene)
    {
        static bool NamesCollision(string name)
            => name.Length == 4
                && name[0] == 'C'
                && (name[1] is 'A' or 'K' or 'S')
                && char.IsDigit(name[2])
                && char.IsDigit(name[3]);

        // The node's NAME travels with it: the game gives each collision surface a
        // rigid body of its own, aimed at the node that carries it, so the surfaces
        // have to stay told apart all the way to the writer.
        var found = new Dictionary<int, string>();
        var byIndex = scene.Nodes;
        for (var at = 0; at < byIndex.Count; at++)
        {
            // The node itself, or any ancestor of it: the mesh usually hangs one
            // level below, as CA00 -> CA00_00.
            var walk = at;
            string? collision = null;
            var guard = 0;
            while (walk >= 0 && walk < byIndex.Count && guard++ < 64)
            {
                if (NamesCollision(byIndex[walk].Name)) { collision = byIndex[walk].Name; break; }
                walk = byIndex[walk].ParentIndex;
            }
            if (collision is null) continue;
            foreach (var mesh in byIndex[at].MeshIndices) found[mesh] = collision;
        }
        return found;
    }

    private static PhyreJointSource[] Joints(ImportedModelScene scene, Matrix4x4 basis)
    {
        if (!scene.IsSkinned) return Array.Empty<PhyreJointSource>();

        var binds = new Dictionary<int, Matrix4x4>();
        foreach (var mesh in scene.Meshes)
        {
            if (mesh.Skin is null) continue;
            foreach (var (node, matrix) in mesh.Skin.InverseBindMatrices)
            {
                binds.TryAdd(node, matrix);
            }
        }

        var joints = new PhyreJointSource[scene.Nodes.Count];
        for (var index = 0; index < scene.Nodes.Count; index++)
        {
            var node = scene.Nodes[index];
            // Only a root carries the change of basis: applying it to every
            // local transform would compound it once per level of the hierarchy.
            var local = node.ParentIndex < 0
                ? node.LocalTransform * basis
                : node.LocalTransform;
            joints[index] = new PhyreJointSource(
                node.Name,
                node.ParentIndex,
                local,
                binds.TryGetValue(index, out var bind) ? bind : Matrix4x4.Identity);
        }
        return joints;
    }
}
