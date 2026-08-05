using ED8Editor.Core;
using ED8Editor.Models;
using ED8Editor.Packages;
using ED8Editor.Phyre;
using ED8Editor.Phyre.Authoring;

namespace ED8Editor.Application;

/// <summary>
/// Puts an imported model onto an existing character's own skeleton, riding
/// every animation the game already has for it.
///
/// This is the low-friction path for "no animation of its own": the character
/// keeps its own <see cref="CpuSkeleton"/>, its own shaders and every one of
/// its clips, byte for byte — only the geometry of one material segment
/// changes. What makes it possible without writing a new skeleton at all is
/// three pieces already proven against the game's own files:
/// <see cref="ED8Editor.Phyre.Authoring.PhyreRigTransfer"/> re-addresses a
/// foreign rig's weights onto the target's joints, keeping the author's own
/// proportions; <see cref="ED8Editor.Phyre.Authoring.PhyreProximitySkinBinder"/>
/// does the same from bare geometry when there is no rig to read weights from;
/// and <see cref="ED8Editor.Phyre.Authoring.PhyreSkinnedSegmentReplacement"/>
/// addresses the result into the one segment's own local bone table and hands
/// every other segment back untouched.
/// </summary>
public static class CharacterRetargetPackage
{
    /// <summary>What replacing a segment reported, folded into one summary.</summary>
    public sealed record FitResult(
        PhyreModelSource Full,
        int SegmentIndex,
        int SegmentCount,
        bool UsedForeignSkin,
        IReadOnlyList<Cs1RigNameMapper.Mapping> JointMapping,
        int Walked,
        float DroppedWeight);

    /// <summary>
    /// The package and cluster entry an asset ID resolves to, and the cluster
    /// itself, read once so a caller does not resolve the same asset twice.
    /// </summary>
    public sealed record ResolvedModel(string PackagePath, string EntryName, byte[] Cluster);

    public static ResolvedModel? ResolveModel(
        IAssetPackageResolverFactory resolvers,
        IPackageArchiveReader archives,
        IAssetManifestReader manifests,
        string gameDataPath,
        string assetId)
    {
        ArgumentNullException.ThrowIfNull(resolvers);
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(gameDataPath);
        ArgumentNullException.ThrowIfNull(assetId);
        var resolution = resolvers.Create(gameDataPath).Resolve(assetId, AssetVariantPreference.English);
        if (resolution.SelectedPackage is null) return null;
        var archive = archives.Read(resolution.SelectedPackage.Path);
        var manifest = manifests.Read(archive, assetId);
        var asset = manifest.PrimaryAsset
            ?? manifest.Assets.FirstOrDefault(value =>
                value.Symbol.Equals(assetId, StringComparison.OrdinalIgnoreCase));
        var resource = asset?.Resources.FirstOrDefault(value => value.Kind == AssetResourceKind.Model);
        if (resource is null) return null;
        var entry = archive.Entries.First(value =>
            value.Name.Equals(resource.ArchiveEntryName, StringComparison.OrdinalIgnoreCase));
        return new ResolvedModel(
            resolution.SelectedPackage.Path, entry.Name, archive.ReadEntry(entry).ToArray());
    }

    /// <summary>
    /// Retargets <paramref name="scene"/> onto <paramref name="targetSkeleton"/>
    /// and addresses the result into segment <paramref name="segmentIndex"/> of
    /// <paramref name="targetModel"/>, ready for
    /// <see cref="ED8Editor.Phyre.Authoring.PhyreModelReplacement.Replace"/>.
    ///
    /// Every mesh the import carries is merged into one, since a target
    /// segment is one material and an import's own material split need not
    /// agree with it. <paramref name="jointMapping"/> overrides or extends
    /// what <see cref="Cs1RigNameMapper.AutoMap"/> would guess on its own — a
    /// caller normally starts from that guess, lets a person fix the few
    /// names it left blank, and passes the corrected table back in.
    /// </summary>
    public static FitResult Fit(
        ImportedModelScene scene,
        CpuModel targetModel,
        int segmentIndex,
        IReadOnlyDictionary<string, string>? jointMapping = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(targetModel);
        if (targetModel.Skeleton is not { Joints.Count: > 0 } skeleton)
        {
            throw new InvalidOperationException(
                $"'{targetModel.AssetId}' has no skeleton to fit onto.");
        }

        var imported = ImportedModelAdapter.Convert(scene).Model;
        var mapping = new List<Cs1RigNameMapper.Mapping>();
        PhyreMeshSource merged;
        bool usedForeignSkin;

        if (imported.IsSkinned)
        {
            var targetNames = skeleton.Joints.Select(joint => joint.Name).ToArray();
            var sourceNames = imported.Joints.Select(joint => joint.Name).ToArray();
            var guessed = Cs1RigNameMapper.AutoMap(sourceNames, targetNames);
            var resolved = jointMapping is null
                ? guessed.Where(value => value.TargetName is not null)
                    .ToDictionary(value => value.SourceName, value => value.TargetName!, StringComparer.Ordinal)
                : new Dictionary<string, string>(jointMapping, StringComparer.Ordinal);
            mapping.AddRange(guessed);

            var (fitted, _) = PhyreRigTransfer.Apply(imported, skeleton, resolved);
            merged = Merge(fitted.Meshes);
            usedForeignSkin = true;
        }
        else
        {
            var scaled = PhyreProximitySkinBinder.FitToHeight(imported, skeleton);
            var bound = PhyreProximitySkinBinder.Bind(scaled, skeleton);
            merged = Merge(bound.Meshes);
            usedForeignSkin = false;
        }

        var (full, report) = PhyreSkinnedSegmentReplacement.Build(
            targetModel, skeleton, segmentIndex, merged);
        return new FitResult(
            full, segmentIndex, report.Segments, usedForeignSkin, mapping, report.Walked, report.Dropped);
    }

    /// <summary>Every material name a target model's segments answer to, in order.</summary>
    public static IReadOnlyList<string> SegmentLabels(CpuModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Meshes
            .SelectMany(mesh => mesh.Primitives)
            .Select(primitive => (uint)primitive.MaterialIndex < model.Materials.Count
                ? model.Materials[primitive.MaterialIndex].Name
                : $"material {primitive.MaterialIndex}")
            .ToArray();
    }

    /// <summary>
    /// Writes <paramref name="fitted"/> into the character's own package,
    /// keeping its shaders, its materials and every other segment untouched.
    /// <paramref name="onSaving"/> is called before and after, exactly as the
    /// rest of the editor tracks a mod's files — see
    /// <c>ModProject.CaptureOriginal</c>/<c>TrackSave</c>.
    /// </summary>
    public static string WriteSegment(
        Action<string, bool> onSaving,
        ResolvedModel target,
        PhyreModelSource fitted)
    {
        ArgumentNullException.ThrowIfNull(onSaving);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(fitted);

        var problems = PhyreModelReplacement.Problems(new PhyreClusterReader().Read(target.Cluster), fitted);
        if (problems.Count != 0)
        {
            throw new InvalidOperationException(
                "This model cannot go into '" + target.EntryName + "': " + string.Join("; ", problems));
        }
        var written = PhyreModelReplacement.Replace(target.Cluster, fitted);

        var archive = new PkgArchiveReader().Read(target.PackagePath);
        var rebuilt = archive.Entries
            .Select(entry => (
                entry.Name,
                Data: entry.Name.Equals(target.EntryName, StringComparison.OrdinalIgnoreCase)
                    ? written
                    : archive.ReadEntry(entry).ToArray()))
            .ToArray();

        onSaving(target.PackagePath, true);
        new PkgArchiveWriter().Write(target.PackagePath, archive.Magic, rebuilt);
        onSaving(target.PackagePath, false);
        return target.PackagePath;
    }

    /// <summary>One mesh's worth of vertices and indices, several meshes concatenated.</summary>
    private static PhyreMeshSource Merge(IReadOnlyList<PhyreMeshSource> meshes)
    {
        if (meshes.Count == 1) return meshes[0];
        var vertices = new List<PhyreVertexSource>();
        var indices = new List<int>();
        foreach (var mesh in meshes)
        {
            var offset = vertices.Count;
            vertices.AddRange(mesh.Vertices);
            indices.AddRange(mesh.Indices.Select(index => index + offset));
        }
        return new PhyreMeshSource("merged", vertices, indices.ToArray());
    }
}
