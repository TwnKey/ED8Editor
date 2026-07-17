namespace ED8Editor.Core;

public sealed record ScriptHeader(
    string SourcePath,
    string Identifier,
    ScriptKind Kind,
    ScriptTargetKind TargetKind,
    uint IdentifierOffset,
    uint IdentifierEndOffset,
    IReadOnlyList<byte> RawPreamble);
