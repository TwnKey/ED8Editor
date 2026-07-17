using System.Numerics;
using ED8Editor.Core;

namespace ED8Editor.Scene;

public interface IVertexPositionDecoder
{
    bool Supports(CpuVertexBuffer buffer, CpuVertexAttribute attribute);

    bool Validate(CpuVertexBuffer buffer, CpuVertexAttribute attribute, out string? reason);

    Vector3 Read(CpuVertexBuffer buffer, CpuVertexAttribute attribute, int vertexIndex);
}
