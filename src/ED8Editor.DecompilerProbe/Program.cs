using System.Text;
using ED8Editor.Decompiler;

if (args is ["--dump-shop", var shopScriptPath, var entryFunction])
{
    var shopScript = ScriptDecompiler.Decompile(shopScriptPath);
    var bindings = ShopScriptBinding.Read(shopScript, entryFunction);
    foreach (var binding in bindings)
    {
        Console.WriteLine(
            $"function=#{binding.FunctionIndex} {binding.FunctionName} "
            + $"instruction=#{binding.InstructionIndex} shop={binding.ShopId} "
            + $"path={string.Join(" -> ", binding.CallPath)}");
    }
    Console.WriteLine($"shop_bindings={bindings.Count}");
    return 0;
}

if (args is ["--dump-fishing-spots", var fishingPath])
{
    var fishingScript = ScriptDecompiler.Decompile(fishingPath);
    var count = 0;
    foreach (var function in fishingScript.Functions.Where(value => value.IsCode))
    {
        foreach (var binding in FishingSpotScriptBinding.Read(fishingScript, function.Name))
        {
            Console.WriteLine(
                $"function=#{binding.FunctionIndex} {binding.FunctionName} "
                + $"instruction=#{binding.InstructionIndex} fish_pnt={binding.FishingPointId} "
                + $"player={binding.PlayerPosition.X:G9},{binding.PlayerPosition.Y:G9},{binding.PlayerPosition.Z:G9} "
                + $"yaw={binding.HeadingDegrees:G9} "
                + $"water={binding.WaterTarget.X:G9},{binding.WaterTarget.Y:G9},{binding.WaterTarget.Z:G9}");
            count++;
        }
    }
    Console.WriteLine($"fishing_spots={count}");
    return 0;
}

if (args is ["--fishing-spot-smoke", var fishingSmokePath])
{
    var temporaryPath = Path.Combine(
        Path.GetTempPath(), $"ed8editor-fishing-{Guid.NewGuid():N}.dat");
    try
    {
        using var fishingDocument = ScriptEditorDocument.Open(fishingSmokePath);
        var snapshot = fishingDocument.Snapshot;
        var binding = snapshot.Functions.Where(value => value.IsCode)
            .SelectMany(function => FishingSpotScriptBinding.Read(snapshot, function.Name))
            .FirstOrDefault()
            ?? throw new InvalidOperationException("The script has no OP73_1 fishing payload.");
        fishingDocument.SetBytes(
            binding.FunctionIndex,
            binding.InstructionIndex,
            binding.PayloadArgumentIndex,
            binding.EncodePayload());
        fishingDocument.Save(temporaryPath);
        if (!File.ReadAllBytes(fishingSmokePath).SequenceEqual(File.ReadAllBytes(temporaryPath)))
            throw new InvalidOperationException("Writing an unchanged fishing payload was not byte-perfect.");
        Console.WriteLine(
            $"PASS fishing payload byte-perfect: {Path.GetFileName(fishingSmokePath)} "
            + $"#{binding.FunctionIndex}/#{binding.InstructionIndex}");
        return 0;
    }
    finally
    {
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
    }
}

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

if (args is ["--instruction-copy-smoke", var copyPath])
{
    var temporaryPath = Path.Combine(
        Path.GetTempPath(),
        $"ed8editor-copy-{Guid.NewGuid():N}.dat");
    try
    {
        using var document = ScriptEditorDocument.Open(copyPath);
        var before = document.Snapshot;
        var candidate = before.Functions
            .Where(value => value.IsCode)
            .SelectMany(function => function.Instructions
                .Select(instruction => (Function: function, Instruction: instruction)))
            .FirstOrDefault(value => value.Instruction.Jumps.Any(jump =>
                jump.TargetFunctionIndex == value.Function.Index
                && jump.TargetInstructionIndex >= 0
                && jump.TargetInstructionIndex != value.Instruction.Index));
        var function = candidate.Function
            ?? before.Functions.First(value => value.IsCode && value.Instructions.Count >= 3);
        var indices = candidate.Instruction is null
            ? new[] { 0, 1 }
            : new[]
            {
                candidate.Instruction.Index,
                candidate.Instruction.Jumps.First(value =>
                    value.TargetFunctionIndex == function.Index
                    && value.TargetInstructionIndex >= 0).TargetInstructionIndex,
            }.Distinct().OrderBy(value => value).ToArray();
        var insertion = function.Instructions
            .Select((instruction, index) => (instruction, index))
            .LastOrDefault(value => value.instruction.Opcode == 1).index;
        if (insertion <= 0) insertion = function.Instructions.Count;

        document.CopyInstructions(function.Index, indices);
        var pastedCount = document.PasteInstructions(function.Index, insertion);
        if (pastedCount != indices.Length)
            throw new InvalidOperationException("The native clipboard pasted the wrong instruction count.");
        var edited = document.Snapshot;
        var editedFunction = edited.Functions[function.Index];
        for (var index = 0; index < indices.Length; index++)
        {
            if (editedFunction.Instructions[insertion + index].Name
                != function.Instructions[indices[index]].Name)
            {
                throw new InvalidOperationException("A pasted instruction changed its registered variant.");
            }
        }
        if (candidate.Instruction is not null)
        {
            var sourcePosition = Array.IndexOf(indices, candidate.Instruction.Index);
            var sourceTarget = candidate.Instruction.Jumps.First(value =>
                value.TargetFunctionIndex == function.Index
                && value.TargetInstructionIndex >= 0).TargetInstructionIndex;
            var targetPosition = Array.IndexOf(indices, sourceTarget);
            var pastedJump = editedFunction.Instructions[insertion + sourcePosition].Jumps
                .FirstOrDefault(value => value.TargetFunctionIndex == function.Index);
            if (targetPosition >= 0
                && pastedJump?.TargetInstructionIndex != insertion + targetPosition)
            {
                throw new InvalidOperationException(
                    "A branch internal to the copied set did not target its pasted counterpart.");
            }
        }
        document.Save(temporaryPath);
        using var reopened = ScriptEditorDocument.Open(temporaryPath);
        if (reopened.Snapshot.Functions[function.Index].Instructions.Count
            != function.Instructions.Count + indices.Length)
        {
            throw new InvalidOperationException("Pasted instructions did not survive serialization.");
        }
        Console.WriteLine(
            $"PASS copy/paste {indices.Length} instruction(s): {Path.GetFileName(copyPath)}");
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

if (args is ["--field-monster-insert-smoke", var fieldMonsterPath])
{
    var temporaryPath = Path.Combine(
        Path.GetTempPath(), $"ed8editor-field-monster-{Guid.NewGuid():N}.dat");
    try
    {
        using var document = ScriptEditorDocument.Open(fieldMonsterPath);
        var fieldMonsterScript = document.Snapshot;
        var battleFunction = fieldMonsterScript.Functions.First(value =>
            value.Table is not null
            && CreateMonstersTableReader.TryRead(value.Table, out _));
        CreateMonstersTableReader.TryRead(battleFunction.Table!, out var table);
        var encounter = table!.Encounters.First();
        var target = fieldMonsterScript.Functions.First(value => value.IsCode);
        var insertion = target.Instructions.Count > 0
            && target.Instructions[^1].Opcode == 1
                ? target.Instructions.Count - 1
                : target.Instructions.Count;
        var parameters = FieldMonsterSpawnParameters.CreateDefault(
            30000, encounter.MonsterAssets.First(value => value.Length > 0),
            battleFunction.Index, encounter.Id) with
        {
            Position = new System.Numerics.Vector3(1.25f, 2.5f, 3.75f),
            HeadingDegrees = 90f,
        };
        document.InsertInstruction(target.Index, insertion, "Entity_Spawn");
        document.SetInteger(target.Index, insertion, 0, parameters.EntityId);
        document.SetString(target.Index, insertion, 1, parameters.ModelAsset);
        document.SetString(target.Index, insertion, 2, parameters.DisplayName);
        document.SetString(target.Index, insertion, 3, parameters.MonsterAsset);
        document.SetInteger(target.Index, insertion, 4, parameters.EntityType);
        document.SetInteger(target.Index, insertion, 5, parameters.Flags);
        document.SetFloat(target.Index, insertion, 6, parameters.Position.X);
        document.SetFloat(target.Index, insertion, 7, parameters.Position.Y);
        document.SetFloat(target.Index, insertion, 8, parameters.Position.Z);
        document.SetFloat(target.Index, insertion, 9, parameters.HeadingDegrees);
        document.SetFloat(target.Index, insertion, 10, parameters.Scale);
        document.SetFloat(target.Index, insertion, 11, parameters.CollisionHeight);
        document.SetFloat(target.Index, insertion, 12, parameters.CollisionRadius);
        document.SetString(target.Index, insertion, 13, parameters.ScriptFile);
        document.SetString(target.Index, insertion, 14, parameters.InitFunction);
        document.SetInteger(target.Index, insertion, 15, parameters.BattleFunctionIndex);
        document.SetInteger(target.Index, insertion, 16, parameters.EncounterIndex);
        document.SetInteger(target.Index, insertion, 17, parameters.UnknownParameter1);
        document.SetInteger(target.Index, insertion, 18, parameters.UnknownParameter2);
        document.SetInteger(target.Index, insertion, 19, parameters.UnknownParameter3);
        document.Save(temporaryPath);
        var reopened = ScriptDecompiler.Decompile(temporaryPath);
        var inserted = ScriptMonsterSpawnReader.Read(reopened).Single(value =>
            value.EntityId == parameters.EntityId);
        if (inserted.AssetId != parameters.MonsterAsset
            || inserted.Position != parameters.Position
            || inserted.BattleFunctionIndex != parameters.BattleFunctionIndex
            || inserted.EncounterIndex != parameters.EncounterIndex)
        {
            throw new InvalidOperationException(
                "The inserted field-monster OP19 did not round-trip exactly.");
        }
        Console.WriteLine("PASS field-monster OP19 insert/save/reopen");
        return 0;
    }
    finally
    {
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
    }
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
    Console.Error.WriteLine("       ED8Editor.DecompilerProbe --instruction-copy-smoke <script.dat>");
    Console.Error.WriteLine("       ED8Editor.DecompilerProbe --dump-tables <script.dat>");
    Console.Error.WriteLine("       ED8Editor.DecompilerProbe --find-table <directory> <kind>");
    Console.Error.WriteLine("       ED8Editor.DecompilerProbe --dump-code <script.dat>");
    Console.Error.WriteLine("       ED8Editor.DecompilerProbe --dump-monsters <script.dat>");
    Console.Error.WriteLine("       ED8Editor.DecompilerProbe --field-monster-insert-smoke <script.dat>");
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
