using System.Text;
using ED8Editor.Decompiler;

if (args is ["--edit-smoke", var editPath])
{
    var temporaryPath = Path.Combine(Path.GetTempPath(), $"ed8editor-{Guid.NewGuid():N}.dat");
    try
    {
        using var document = ScriptEditorDocument.Open(editPath);
        var before = document.Snapshot;
        var byteOperand = before.Functions.Where(value => value.IsCode)
            .SelectMany(value => value.Instructions.SelectMany(instruction =>
                instruction.Arguments.Where(argument => argument.Kind == "bytes")
                    .Select(argument => (Function: value, Instruction: instruction, Argument: argument))))
            .FirstOrDefault();
        if (byteOperand.Argument is not null)
            document.SetBytes(byteOperand.Function.Index, byteOperand.Instruction.Index,
                byteOperand.Argument.Index, byteOperand.Argument.Raw);
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

if (args is ["--function-smoke", var functionPath])
{
    var temporaryPath = Path.Combine(Path.GetTempPath(), $"ed8editor-fn-{Guid.NewGuid():N}.dat");
    try
    {
        using var document = ScriptEditorDocument.Open(functionPath);
        var before = document.Snapshot.Functions.Count;
        var index = document.AddCodeFunction("EV_ED8EDITOR_SMOKE");
        var created = document.Snapshot.Functions[index];
        if (created.Name != "EV_ED8EDITOR_SMOKE" || !created.IsCode || created.Instructions.Count != 1)
            throw new InvalidOperationException("The created function is not an executable one-instruction body.");
        document.Save(temporaryPath);
        using (var reopened = ScriptEditorDocument.Open(temporaryPath))
        {
            var snapshot = reopened.Snapshot;
            if (snapshot.Functions.Count != before + 1)
                throw new InvalidOperationException("The created function did not survive serialization.");
            var reloaded = snapshot.Functions.FirstOrDefault(value => value.Name == "EV_ED8EDITOR_SMOKE")
                ?? throw new InvalidOperationException("The created function lost its name.");
            if (!reloaded.IsCode || reloaded.Instructions.Count == 0 || reloaded.Instructions[0].Opcode != 1)
                throw new InvalidOperationException("The reopened function does not start with its RETURN.");
            // Every other function must still decode exactly as before.
            var original = ScriptDecompiler.Decompile(functionPath);
            foreach (var function in original.Functions)
            {
                var match = snapshot.Functions.FirstOrDefault(value => value.Name == function.Name);
                if (match is null || match.IsCode != function.IsCode
                    || match.Instructions.Count != function.Instructions.Count)
                {
                    throw new InvalidOperationException(
                        $"Function '{function.Name}' changed when a new function was added.");
                }
            }
        }
        document.RemoveFunction(index);
        document.Save(temporaryPath);
        if (!File.ReadAllBytes(functionPath).SequenceEqual(File.ReadAllBytes(temporaryPath)))
            throw new InvalidOperationException("Add then remove is not byte-perfect.");
        Console.WriteLine($"PASS function add/serialize/remove byte-perfect: {Path.GetFileName(functionPath)}");
        return 0;
    }
    finally
    {
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
    }
}

if (args is ["--time-snapshot", var timePath])
{
    var clock = System.Diagnostics.Stopwatch.StartNew();
    using var timed = ScriptEditorDocument.Open(timePath);
    Console.WriteLine($"open: {clock.ElapsedMilliseconds} ms");
    for (var pass = 0; pass < 3; pass++)
    {
        clock.Restart();
        var snapshot = timed.Snapshot;
        Console.WriteLine(
            $"snapshot {pass}: {clock.ElapsedMilliseconds} ms"
            + $" ({snapshot.Functions.Count} functions,"
            + $" {snapshot.Functions.Sum(value => value.Instructions.Count)} instructions)");
    }
    return 0;
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

if (args.Length is 2 or 3 && args[0] == "--dump-code")
{
    var codePath = args[1];
    var codeScript = ScriptDecompiler.Decompile(codePath, args.Length == 3 ? args[2] : null);
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

if (args is ["--find-instruction", var instructionRoot, var requestedInstruction, var registryPath])
{
    foreach (var path in Directory.EnumerateFiles(instructionRoot, "*.dat", SearchOption.AllDirectories))
    {
        try
        {
            var candidate = ScriptDecompiler.Decompile(path, registryPath);
            foreach (var function in candidate.Functions.Where(value => value.IsCode))
            foreach (var instruction in function.Instructions.Where(value =>
                         value.Name.Equals(requestedInstruction, StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine($"{path} :: {function.Name} #{instruction.Index} ("
                    + string.Join(", ", instruction.Arguments.Select(FormatArgument)) + ")");
        }
        catch (InvalidOperationException)
        {
            // DAT containers which are not scenario scripts are outside this diagnostic.
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
            + $"heading={spawn.HeadingDegrees:G9} battle=#{spawn.BattleFunctionIndex}"
            + $" encounter={spawn.EncounterIndex}");
    }
    return 0;
}

if (args is ["--dump-create-monsters", var createMonstersPath])
{
    var createMonstersScript = ScriptDecompiler.Decompile(createMonstersPath);
    foreach (var function in createMonstersScript.Functions.Where(value =>
                 value.Table is not null
                 && CreateMonstersTableReader.TryRead(value.Table, out _)))
    {
        CreateMonstersTableReader.TryRead(function.Table!, out var table);
        Console.WriteLine($"function=#{function.Index} name={function.Name} "
            + $"map={table!.MapAsset} encounters={table.Encounters.Count}");
        Console.WriteLine("header=" + string.Join(", ", table.HeaderFields.Select(FormatTableField)));
        foreach (var encounter in table.Encounters)
        {
            Console.WriteLine($"  encounter[{encounter.Index}] id={encounter.Id} "
                + $"aux={encounter.AuxiliaryAsset ?? "<none>"}");
            for (var slot = 0; slot < encounter.MonsterAssets.Count; slot++)
                Console.WriteLine($"    slot[{slot}] asset={encounter.MonsterAssets[slot]} "
                    + $"weight={encounter.Weights[slot]}");
        }
        Console.WriteLine("trailer=" + string.Join(", ", table.TrailerFields.Select(FormatTableField)));
    }
    return 0;
}

if (args is ["--table-edit-smoke", var tableEditPath])
{
    var temporaryPath = Path.Combine(Path.GetTempPath(), $"ed8editor-table-{Guid.NewGuid():N}.dat");
    try
    {
        using var tableDocument = ScriptEditorDocument.Open(tableEditPath);
        var tableFunction = tableDocument.Snapshot.Functions.First(value =>
            value.Table is { IsStale: false } table
            && table.Fields.Any(field => field.Type is "u8" or "s16" or "s32"));
        var table = tableFunction.Table!;
        var field = table.Fields.First(value => value.Type is "u8" or "s16" or "s32");
        tableDocument.SetTableInteger(tableFunction.Index, field.Index, checked((int)field.IntValue));
        tableDocument.Save(temporaryPath);
        using var reopened = ScriptEditorDocument.Open(temporaryPath);
        var reopenedTable = reopened.Snapshot.Functions[tableFunction.Index].Table
            ?? throw new InvalidOperationException("The edited table was not parsed after reopening.");
        if (reopenedTable.Kind != table.Kind || reopenedTable.Fields.Count != table.Fields.Count)
            throw new InvalidOperationException("The edited table structure changed after reopening.");
        if (!File.ReadAllBytes(tableEditPath).SequenceEqual(File.ReadAllBytes(temporaryPath)))
            throw new InvalidOperationException("Writing an unchanged table value was not byte-perfect.");
        Console.WriteLine($"PASS table edit/serialize/reopen byte-perfect: {Path.GetFileName(tableEditPath)}");
        return 0;
    }
    finally
    {
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
    }
}

if (args is ["--create-monsters-edit-smoke", var encounterEditPath])
{
    var temporaryPath = Path.Combine(Path.GetTempPath(), $"ed8editor-monsters-{Guid.NewGuid():N}.dat");
    try
    {
        using var encounterDocument = ScriptEditorDocument.Open(encounterEditPath);
        var function = encounterDocument.Snapshot.Functions.First(value =>
            value.Table is not null && CreateMonstersTableReader.TryRead(value.Table, out _));
        CreateMonstersTableReader.TryRead(function.Table!, out var original);
        if (original!.Encounters.Count == 0)
            throw new InvalidOperationException("The smoke test requires one encounter to clone.");
        var insertedId = original.Encounters.Max(value => value.Id) + 1;
        CreateMonstersTableEditor.DuplicateEncounter(
            encounterDocument,
            function.Index,
            0,
            original.Encounters.Count,
            insertedId);
        encounterDocument.Save(temporaryPath);
        using (var reopened = ScriptEditorDocument.Open(temporaryPath))
        {
            var reopenedFunction = reopened.Snapshot.Functions[function.Index];
            var edited = reopenedFunction.Table;
            if (edited is null
                || !CreateMonstersTableReader.TryRead(edited, out var parsed)
                || parsed!.Encounters.Count != original.Encounters.Count + 1
                || parsed.Encounters[^1].Id != insertedId)
            {
                throw new InvalidOperationException(
                    "The inserted encounter did not survive serialization/reopen. "
                    + $"function={reopenedFunction.Name}, sourceType={reopenedFunction.SourceType}, "
                    + $"table={edited?.Kind ?? "<none>"}, raw={reopenedFunction.RawData?.Length ?? 0}.");
            }
        }
        CreateMonstersTableEditor.RemoveEncounter(
            encounterDocument, function.Index, original.Encounters.Count);
        encounterDocument.Save(temporaryPath);
        if (!File.ReadAllBytes(encounterEditPath).SequenceEqual(File.ReadAllBytes(temporaryPath)))
            throw new InvalidOperationException(
                "Adding and removing an encounter did not restore the original script bytes.");
        Console.WriteLine($"PASS CreateMonsters add/remove/reopen: {Path.GetFileName(encounterEditPath)}");
        return 0;
    }
    finally
    {
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
    }
}

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: ED8Editor.DecompilerProbe <script.dat> [cs1_instructions.json]");
    Console.Error.WriteLine("       ED8Editor.DecompilerProbe --edit-smoke <script.dat>");
    Console.Error.WriteLine("       ED8Editor.DecompilerProbe --dump-tables <script.dat>");
    Console.Error.WriteLine("       ED8Editor.DecompilerProbe --find-table <directory> <kind>");
    Console.Error.WriteLine("       ED8Editor.DecompilerProbe --dump-code <script.dat>");
    Console.Error.WriteLine("       ED8Editor.DecompilerProbe --dump-monsters <script.dat>");
    Console.Error.WriteLine("       ED8Editor.DecompilerProbe --dump-create-monsters <script.dat>");
    Console.Error.WriteLine("       ED8Editor.DecompilerProbe --table-edit-smoke <script.dat>");
    Console.Error.WriteLine("       ED8Editor.DecompilerProbe --create-monsters-edit-smoke <script.dat>");
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
            return $"{argument.Type}[{argument.Raw.Length}]={Convert.ToHexString(argument.Raw)}";
    }
}

static string FormatTableField(TableField field) => field.Type switch
{
    "string" => $"string=\"{field.Text}\"",
    "f32" => $"f32={field.FloatValue:G9}",
    "bytes" or "fill" => $"{field.Type}[{field.Raw.Length}]={Convert.ToHexString(field.Raw)}",
    _ => $"{field.Type}={field.IntValue}",
};
