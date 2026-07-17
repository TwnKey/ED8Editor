namespace ED8Editor.Core;

public sealed record CpuVertexAttribute(
    VertexSemantic Semantic,
    int SemanticIndex,
    string SourceFormat,
    int Offset);
