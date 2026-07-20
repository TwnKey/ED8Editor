using System.Text;
using ED8Editor.Decompiler;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: ED8Editor.DecompilerProbe <script.dat> [cs1_instructions.json]");
    return 2;
}

var jsonPath = args.Length >= 2 ? args[1] : ScriptDecompiler.DefaultInstructionsPath;

DecompiledScript script;
try
{
    script = ScriptDecompiler.Decompile(args[0], jsonPath);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Erreur: {exception.Message}");
    return 1;
}

Console.WriteLine($"Scene     : {script.SceneName}");
Console.WriteLine($"Fonctions : {script.Functions.Count} "
    + $"({script.Functions.Count(x => x.IsCode)} code, {script.Functions.Count(x => !x.IsCode)} data)");

foreach (var function in script.Functions.Where(x => x.IsCode).Take(4))
{
    Console.WriteLine();
    Console.WriteLine($"== {function.Name}  ({function.Instructions.Count} instructions) ==");
    foreach (var instruction in function.Instructions.Take(15))
    {
        var argText = string.Join(", ", instruction.Arguments.Select(FormatArgument));
        var jumpText = instruction.Jumps.Count == 0
            ? string.Empty
            : "  -> " + string.Join(", ", instruction.Jumps.Select(j =>
                j.TargetInstructionIndex >= 0 ? $"#{j.TargetInstructionIndex}" : "(fin)"));
        Console.WriteLine($"  [{instruction.Index,3}] 0x{instruction.Offset:X5} {instruction.Name}({argText}){jumpText}");
    }
}

return 0;

static string FormatArgument(InstructionArgument argument)
{
    switch (argument.Kind)
    {
        case "expr":
            return "expr{ " + string.Join(" ", argument.Expression!.Select(e => e.Label)) + " }";
        case "scalar":
            return argument.Type == "f32"
                ? $"{argument.Type}={argument.FloatValue:G6}"
                : $"{argument.Type}={argument.IntValue}";
        case "string":
            return "\"" + Encoding.Latin1.GetString(argument.Raw).TrimEnd('\0') + "\"";
        default:
            return $"{argument.Type}[{argument.Raw.Length}]";
    }
}
