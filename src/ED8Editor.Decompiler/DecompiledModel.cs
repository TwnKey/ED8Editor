namespace ED8Editor.Decompiler;

/// <summary>Un script decompile : nom de scene + fonctions (scenes).</summary>
public sealed record DecompiledScript(
    string SceneName,
    IReadOnlyList<DecompiledFunction> Functions);

/// <summary>
/// Une fonction (scene). Si <see cref="IsCode"/> est faux, c'est une table de
/// donnees conservee en octets bruts (pas eclatee en instructions).
/// </summary>
public sealed record DecompiledFunction(
    int Index,
    string Name,
    bool IsCode,
    IReadOnlyList<DecompiledInstruction> Instructions);

/// <summary>
/// Une instruction, au niveau branche : un nom lisible (ex. OP48_3), l'opcode
/// brut sous-jacent (interne), ses arguments types et ses sauts eventuels.
/// </summary>
public sealed record DecompiledInstruction(
    int Index,
    int Offset,
    string Name,
    int Opcode,
    IReadOnlyList<InstructionArgument> Arguments,
    IReadOnlyList<JumpTarget> Jumps);

/// <summary>
/// Un argument type. <see cref="Kind"/> vaut "scalar", "string", "expr",
/// "dialog" ou "bytes". <see cref="Expression"/> n'est renseigne que si l'argument
/// est une expression.
/// </summary>
public sealed record InstructionArgument(
    int Index,
    string Kind,
    string Type,
    int IntValue,
    double FloatValue,
    byte[] Raw,
    IReadOnlyList<ExprElement>? Expression);

/// <summary>Un element d'expression : operateur ou operande type.</summary>
public sealed record ExprElement(
    int SubOp,
    string Kind,
    string Label,
    int Value,
    string? NestedInstruction);

/// <summary>
/// Un saut : l'argument (ptr32) qui le porte, et l'index de l'instruction cible
/// dans la meme fonction (-1 si la cible est la fin de fonction ou hors flot).
/// </summary>
public sealed record JumpTarget(
    int ArgumentIndex,
    int TargetInstructionIndex,
    int TargetOffset);
