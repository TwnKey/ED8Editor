namespace ED8Editor.Decompiler;

/// <summary>Un script decompile : nom de scene + fonctions (scenes).</summary>
public sealed record DecompiledScript(
    string SceneName,
    IReadOnlyList<DecompiledFunction> Functions);

/// <summary>
/// Une fonction (scene). Trois cas exclusifs :
/// - <see cref="IsCode"/> vrai : flot d'instructions (voir <see cref="Instructions"/>).
/// - <see cref="Table"/> non nul : table de donnees decodee en champs types.
/// - sinon : donnees brutes non reconnues.
/// </summary>
public sealed record DecompiledFunction(
    int Index,
    string Name,
    bool IsCode,
    IReadOnlyList<DecompiledInstruction> Instructions,
    DecompiledTable? Table = null);

/// <summary>
/// Une table de donnees (ActionTable, AlgoTable, FieldMonsterData, ...), separee du
/// code. <see cref="IsStale"/> vrai = fichier perime/malforme (ne suit pas le format
/// du jeu, typiquement du debug Falcom) : les champs sont alors un unique blob brut
/// preserve a l'octet pres.
/// </summary>
public sealed record DecompiledTable(
    string Kind,
    int Id,
    bool IsStale,
    IReadOnlyList<TableField> Fields);

/// <summary>
/// Un champ de table type. <see cref="Type"/> vaut "u8", "s16", "s32", "f32",
/// "string", "fill" ou "bytes". <see cref="Text"/> n'est renseigne que pour "string".
/// <see cref="Raw"/> contient toujours les octets bruts (round-trip garanti).
/// </summary>
public sealed record TableField(
    int Index,
    string Type,
    long IntValue,
    double FloatValue,
    string? Text,
    byte[] Raw);

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
///
/// <see cref="Name"/> est un libelle humain (optionnel). <see cref="Sem"/> est un type
/// semantique pour choisir un selecteur adapte dans l'editeur :
/// "color", "position", "vec2"/"vec3"/"vec4", "file" (extension dans <see cref="SemArg"/>),
/// "tbl" (<see cref="SemArg"/> = "nomTbl:typeEntree"), "func_index", "func_name".
/// <see cref="SemSpan"/> = nombre d'operandes consecutifs groupes (ex. position = 3 floats).
/// </summary>
public sealed record InstructionArgument(
    int Index,
    string Kind,
    string Type,
    int IntValue,
    double FloatValue,
    byte[] Raw,
    IReadOnlyList<ExprElement>? Expression,
    string? Name = null,
    string? Sem = null,
    string? SemArg = null,
    int SemSpan = 1);

/// <summary>Un element d'expression : operateur ou operande type.</summary>
public sealed record ExprElement(
    int SubOp,
    string Kind,
    string Label,
    int Value,
    string? NestedInstruction);

/// <summary>
/// Un saut : l'argument (ptr32) qui le porte, la fonction cible et l'index de
/// l'instruction cible (-1 = fin de fonction, -2 = adresse brute non resolue).
/// </summary>
public sealed record JumpTarget(
    int ArgumentIndex,
    int TargetInstructionIndex,
    int TargetOffset,
    int TargetFunctionIndex = -1);
