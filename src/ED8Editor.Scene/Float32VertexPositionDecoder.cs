using System.Buffers.Binary;
using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Scene;

public sealed class Float32VertexPositionDecoder : IVertexPositionDecoder
{
    public bool Supports(CpuVertexBuffer buffer, CpuVertexAttribute attribute)
        => attribute.Semantic == VertexSemantic.Position
            && attribute.SourceFormat == "Float32x3"
            && attribute.Offset >= 0
            && buffer.Stride >= 12
            && attribute.Offset <= buffer.Stride - 12;

    public bool Validate(CpuVertexBuffer buffer, CpuVertexAttribute attribute, out string? reason)
    {
        if (!Supports(buffer, attribute))
        {
            reason = $"Unsupported position format '{attribute.SourceFormat}'.";
            return false;
        }
        if (buffer.VertexCount < 0)
        {
            reason = "Vertex count is negative.";
            return false;
        }
        var requiredSize = checked((long)buffer.VertexCount * buffer.Stride);
        if (requiredSize > buffer.Data.Length)
        {
            reason = "Position vertex buffer is truncated.";
            return false;
        }
        reason = null;
        return true;
    }

    public Vector3 Read(CpuVertexBuffer buffer, CpuVertexAttribute attribute, int vertexIndex)
    {
        if (!Validate(buffer, attribute, out var reason)) throw new InvalidDataException(reason);
        if ((uint)vertexIndex >= buffer.VertexCount) throw new ArgumentOutOfRangeException(nameof(vertexIndex));
        var offset = checked(vertexIndex * buffer.Stride + attribute.Offset);
        var data = buffer.Data.AsSpan(offset, 12);
        return new Vector3(
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data)),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data[4..])),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data[8..])));
    }
}
