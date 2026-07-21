using System.Text;
using ED8Editor.Decompiler;

if (args is ["--edit-smoke", var editPath])
{
    var temporaryPath = Path.Combine(Path.GetTempPath(), $"ed8editor-{Guid.NewGuid():N}.dat");
    try
    {
        using var document = ScriptEditorDocument.Open(editPath);
        var before = document.Snapshot;
        var function = before.Functions.First(value => value.IsCode && value.Instructions.Count > 0);
        var originalCount = function.Instructions.Count;
        document.InsertInstruction(function.Index, originalCount, "OP0");
        if (document.Snapshot.Functions[function.Index].Instructions.Count != originalCount + 1)
            throw new InvalidOperationException("L'insertion native n'est pas visible dans le modèle.");
        document.Save(temporaryPath);
        using (var inserted = ScriptEditorDocument.Open(temporaryPath))
        {
            if (inserted.Snapshot.Functions[function.Index].Instructions.Count != originalCount + 1)
                throw new InvalidOperationException("L'instruction insérée n'a pas survécu à la sérialisation.");
        }
        document.RemoveInstruction(function.Index, originalCount);
        var jumpOwner = before.Functions.Where(value => value.IsCode)
            .SelectMany(value => value.Instructions.Select(instruction => (Function: value, Instruction: instruction)))
            .FirstOrDefault(value => value.Instruction.Jumps.Any(jump => jump.TargetFunctionIndex >= 0));
        if (jumpOwner.Instruction is not null)
        {
            var jump = jumpOwner.Instruction.Jumps.First(value => value.TargetFunctionIndex >= 0);
            document.SetJump(jumpOwner.Function.Index, jumpOwner.Instruction.Index, jump.ArgumentIndex,
                jump.TargetFunctionIndex, jump.TargetInstructionIndex);
        }
        var editableExpression = before.Functions.Where(value => value.IsCode)
            .SelectMany(value => value.Instructions.Select(instruction => (Function: value, Instruction: instruction)))
            .FirstOrDefault(value => value.Instruction.Arguments.Any(argument => argument.Kind == "expr"
                && argument.Expression is not null
                && argument.Expression.All(element => element.SubOp != 0x1c)));
        if (editableExpression.Instruction is not null)
        {
            var argument = editableExpression.Instruction.Arguments.First(value => value.Kind == "expr");
            var tokens = argument.Expression!.Where(element => element.SubOp != 0x01)
                .Select(element => new ScriptExpressionToken(element.SubOp, element.Value)).ToArray();
            document.ReplaceExpression(editableExpression.Function.Index,
                editableExpression.Instruction.Index, argument.Index, tokens);
        }
        var directJump = before.Functions.Where(value => value.IsCode)
            .SelectMany(value => value.Instructions.Select(instruction => (Function: value, Instruction: instruction)))
            .FirstOrDefault(value => value.Instruction.Opcode == 3
                && value.Instruction.Jumps.Any(jump => jump.TargetFunctionIndex >= 0));
        if (directJump.Instruction is not null)
        {
            var target = directJump.Instruction.Jumps.First(value => value.TargetFunctionIndex >= 0);
            document.ReplaceInstruction(directJump.Function.Index, directJump.Instruction.Index, "OP5");
            document.SetJump(directJump.Function.Index, directJump.Instruction.Index, 1,
                target.TargetFunctionIndex, target.TargetInstructionIndex);
            document.ReplaceExpression(directJump.Function.Index, directJump.Instruction.Index, 0,
                new[] { new ScriptExpressionToken(0x00, 1) });
            document.ReplaceInstruction(directJump.Function.Index, directJump.Instruction.Index, "OP3");
            document.SetJump(directJump.Function.Index, directJump.Instruction.Index, 0,
                target.TargetFunctionIndex, target.TargetInstructionIndex);
        }
        document.Save(temporaryPath);
        using var reopened = ScriptEditorDocument.Open(temporaryPath);
        var after = reopened.Snapshot;
        if (after.Functions[function.Index].Instructions.Count != originalCount)
            throw new InvalidOperationException("Le document réouvert ne conserve pas le flot attendu.");
        if (!File.ReadAllBytes(editPath).SequenceEqual(File.ReadAllBytes(temporaryPath)))
            throw new InvalidOperationException("Le cycle insertion/suppression n'est pas byte-perfect.");
        Console.WriteLine($"PASS edit/serialize/reopen byte-perfect: {Path.GetFileName(editPath)}");
        return 0;
    }
    finally
    {
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
    }
}

if (args is ["--dump-tables", var tablePath])
{
    var tableScript = ScriptDecompiler.Decompile(tablePath);
    foreach (var function in tableScript.Functions.Where(value => !value.IsCode))
    {
        if (function.Table is not { } table)
        {
            Console.WriteLine($"== #{function.Index} {function.Name} [unrecognized data] ==");
            continue;
        }
        Console.WriteLine($"== #{function.Index} {function.Name} [{table.Kind}] stale={table.IsStale} ==");
        foreach (var field in table.Fields)
        {
            var value = field.Type switch
            {
                "string" => $"\"{field.Text}\"",
                "f32" => field.FloatValue.ToString("G9"),
                "bytes" or "fill" => Convert.ToHexString(field.Raw),
                _ => field.IntValue.ToString(),
            };
            Console.WriteLine($"  [{field.Index,3}] {field.Type,-7} {value}");
        }
    }
    return 0;
}

if (args is ["--find-table", var tableRoot, var requestedKind])
{
    foreach (var path in Directory.EnumerateFiles(tableRoot, "*.dat", SearchOption.AllDirectories))
    {
        try
        {
            var candidate = ScriptDecompiler.Decompile(path);
            foreach (var function in candidate.Functions.Where(value =>
                         value.Table?.Kind.Equals(requestedKind, StringComparison.OrdinalIgnoreCase) == true))
            {
                Console.WriteLine($"{path} :: #{function.Index} {function.Name}");
            }
        }
        catch (InvalidOperationException)
        {
            // Some non-scenario DAT files share this extension and are intentionally ignored.
        }
    }
    return 0;
}

if (args is ["--dump-code", var codePath])
{
    var codeScript = ScriptDecompiler.Decompile(codePath);
    foreach (var function in codeScript.Functions.Where(value => value.IsCode))
    {
        Console.WriteLine($"== #{function.Index} {function.Name} ==");
        foreach (var instruction in function.Instructions)
        {
            Console.WriteLine($"  [{instruction.Index,3}] {instruction.Name}("
                + string.Join(", ", instruction.Arguments.Select(FormatArgument)) + ")");
        }
    }
    return 0;
}

if (args is ["--dump-monsters", var monsterPath])
{
    var monsterScript = ScriptDecompiler.Decompile(monsterPath);
    foreach (var spawn in ScriptMonsterSpawnReader.Read(monsterScript))
    {
        Console.WriteLine($"entity={spawn.EntityId} asset={spawn.AssetId} "
            + $"position={spawn.Position.X:G9},{spawn.Position.Y:G9},{spawn.Position.Z:G9} "
            + $"heading={spawn.HeadingDegrees:G9} battle=#{spawn.BattleFunctionIndex}");
    }
    return 0;
}

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: ED8Editor.DecompilerProbe <script.dat> [cs1_instructions.json]");
    Console.Error.WriteLine("       ED8Editor.DecompilerProbe --edit-smoke <script.dat>");
    Console.Error.WriteLine("       ED8Editor.DecompilerProbe --dump-tables <script.dat>");
    Console.Error.WriteLine("       ED8Editor.DecompilerProbe --find-table <directory> <kind>");
    Console.Error.WriteLine("       ED8Editor.DecompilerProbe --dump-code <script.dat>");
    Console.Error.WriteLine("       ED8Editor.DecompilerProbe --dump-monsters <script.dat>");
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
