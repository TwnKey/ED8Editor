using ED8Editor.Core;

namespace ED8Editor.Scene;

internal readonly record struct VertexPositionSource(
    CpuVertexBuffer Buffer,
    CpuVertexAttribute Attribute,
    IVertexPositionDecoder Decoder);

internal sealed class SceneGeometryDecoder
{
    private readonly IReadOnlyList<IVertexPositionDecoder> positionDecoders;

    public SceneGeometryDecoder(IEnumerable<IVertexPositionDecoder>? positionDecoders)
    {
        this.positionDecoders = (positionDecoders ?? new IVertexPositionDecoder[] { new Float32VertexPositionDecoder() }).ToArray();
        if (this.positionDecoders.Count == 0) throw new ArgumentException("At least one position decoder is required.", nameof(positionDecoders));
    }

    public bool TryFindPositionSource(
        CpuMeshPrimitive primitive,
        out VertexPositionSource source,
        out string? reason)
    {
        string? invalidReason = null;
        foreach (var buffer in primitive.VertexBuffers)
        {
            foreach (var attribute in buffer.Attributes.Where(value => value.Semantic == VertexSemantic.Position))
            {
                var decoder = positionDecoders.FirstOrDefault(value => value.Supports(buffer, attribute));
                if (decoder is null) continue;
                if (decoder.Validate(buffer, attribute, out var validationReason))
                {
                    source = new VertexPositionSource(buffer, attribute, decoder);
                    reason = null;
                    return true;
                }
                invalidReason ??= validationReason;
            }
        }
        source = default;
        reason = invalidReason ?? "No supported position stream.";
        return false;
    }
}
