using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Scene;

public sealed record OpsNewPropProfile(
    uint Flags,
    IReadOnlyDictionary<string, string> AdditionalAttributes)
{
    // This is the dominant neutral NewObject profile in the CS1 OPS corpus.
    // The individual flag bits are intentionally not named until their engine
    // semantics are documented; changing the profile requires no editor rewrite.
    public const uint UndocumentedNeutralFlags = 0x281;

    public static OpsNewPropProfile Neutral { get; } = new(
        UndocumentedNeutralFlags,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["flag"] = "0x281",
            ["clipGroup"] = "0",
            ["clipFarDistance"] = "-1",
            ["skyboxFactor"] = "0",
            ["materialDiffuse"] = "1, 1, 1, 1",
            ["materialEmission"] = "0, 0, 0",
        });

    public MapProp Create(int sourceIndex, string assetId, string name, CpuModel model, Vector3 position)
    {
        ArgumentNullException.ThrowIfNull(model);
        var sourceEuler = Vector3.Zero;
        var rotation = Quaternion.Identity;
        var transform = new MapTransform(position, rotation, Vector3.One, new Vector3(-position.X, position.Y, position.Z), sourceEuler);
        var attributes = new Dictionary<string, string>(AdditionalAttributes, StringComparer.Ordinal)
        {
            ["asset"] = assetId,
            ["name"] = name,
            ["pos"] = $"{-position.X}, {position.Y}, {position.Z}",
            ["rot"] = "0, 0, 0",
            ["scl"] = "1, 1, 1",
        };
        return new MapProp(
            sourceIndex, assetId, name, transform, Flags, Vector4.One, Vector3.Zero, attributes);
    }
}
