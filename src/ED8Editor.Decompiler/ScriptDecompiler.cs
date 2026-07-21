using System.Runtime.InteropServices;

namespace ED8Editor.Decompiler;

/// <summary>
/// Point d'entree du decompilateur. Charge le document d'instructions une fois,
/// puis decode un .dat en un <see cref="DecompiledScript"/> (header + fonctions +
/// flot d'instructions branche, avec arguments types, expressions et sauts).
///
/// S'appuie sur le moteur natif valide (cs1_decompiler.dll). Le fichier
/// cs1_instructions.json et la DLL doivent etre a cote de l'executable
/// (le csproj les y recopie).
/// </summary>
public sealed class ScriptDecompiler
{
    private static readonly object Gate = new();
    private static bool _registryLoaded;

    private static readonly HashSet<string> ScalarTypes = new(StringComparer.Ordinal)
    {
        "u8", "s8", "u16", "s16", "u32", "s32", "f32", "ptr32",
    };

    /// <summary>Chemin par defaut du document, a cote de l'assembly.</summary>
    public static string DefaultInstructionsPath =>
        Path.Combine(AppContext.BaseDirectory, "cs1_instructions.json");

    /// <summary>Charge le registre d'instructions (idempotent).</summary>
    public static void EnsureRegistry(string? instructionsJsonPath = null)
    {
        lock (Gate)
        {
            if (_registryLoaded)
            {
                return;
            }

            var path = instructionsJsonPath ?? DefaultInstructionsPath;
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Document d'instructions introuvable.", path);
            }

            var json = File.ReadAllBytes(path);
            var terminated = new byte[json.Length + 1];
            Array.Copy(json, terminated, json.Length); // termine par 0 pour const char*
            if (NativeMethods.cs1i_load_registry(terminated) == 0)
            {
                throw new InvalidOperationException("Echec du chargement du registre d'instructions.");
            }

            // schema editable des records de tables (optionnel, a cote du document)
            var schemaPath = Path.Combine(Path.GetDirectoryName(path) ?? ".", "cs1_tables.json");
            if (File.Exists(schemaPath))
            {
                var sj = File.ReadAllBytes(schemaPath);
                var st = new byte[sj.Length + 1];
                Array.Copy(sj, st, sj.Length);
                NativeMethods.cs1i_load_tables_schema(st);
            }

            _registryLoaded = true;
        }
    }

    /// <summary>Decode un fichier .dat en un modele decompile complet.</summary>
    public static DecompiledScript Decompile(string datPath, string? instructionsJsonPath = null)
    {
        EnsureRegistry(instructionsJsonPath);

        var data = File.ReadAllBytes(datPath);
        var doc = NativeMethods.cs1i_open(data, data.Length, Path.GetFileName(datPath));
        if (doc == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Le moteur natif n'a pas pu ouvrir '{datPath}'.");
        }

        try
        {
            return Build(doc);
        }
        finally
        {
            NativeMethods.cs1i_close(doc);
        }
    }

    private static DecompiledScript Build(IntPtr doc)
    {
        var scene = Str(NativeMethods.cs1i_scene_name(doc)) ?? string.Empty;
        var functionCount = NativeMethods.cs1i_func_count(doc);
        var functions = new List<DecompiledFunction>(functionCount);

        for (var f = 0; f < functionCount; f++)
        {
            var name = Str(NativeMethods.cs1i_func_name(doc, f)) ?? $"func_{f}";
            var isCode = NativeMethods.cs1i_func_is_code(doc, f) != 0;
            var isTable = NativeMethods.cs1i_func_is_table(doc, f) != 0;
            var instructions = isCode ? BuildInstructions(doc, f) : Array.Empty<DecompiledInstruction>();
            var table = isTable ? BuildTable(doc, f) : null;
            functions.Add(new DecompiledFunction(f, name, isCode, instructions, table));
        }

        return new DecompiledScript(scene, functions);
    }

    private static DecompiledTable BuildTable(IntPtr doc, int f)
    {
        var kind = Str(NativeMethods.cs1i_table_kind(doc, f)) ?? "Table";
        var id = NativeMethods.cs1i_table_id(doc, f);
        var stale = NativeMethods.cs1i_table_is_stale(doc, f) != 0;
        var count = NativeMethods.cs1i_table_field_count(doc, f);
        var fields = new List<TableField>(count < 0 ? 0 : count);
        for (var j = 0; j < count; j++)
        {
            var type = Str(NativeMethods.cs1i_table_field_type(doc, f, j)) ?? "bytes";
            var raw = TableFieldBytes(doc, f, j);
            var text = type == "string" ? Str(NativeMethods.cs1i_table_field_text(doc, f, j)) : null;
            var iv = NativeMethods.cs1i_table_field_i(doc, f, j);
            var fv = NativeMethods.cs1i_table_field_f(doc, f, j);
            fields.Add(new TableField(j, type, iv, fv, text, raw));
        }

        return new DecompiledTable(kind, id, stale, fields);
    }

    private static byte[] TableFieldBytes(IntPtr doc, int f, int j)
    {
        var n = NativeMethods.cs1i_table_field_bytes(doc, f, j, null, 0);
        if (n <= 0)
        {
            return Array.Empty<byte>();
        }

        var buffer = new byte[n];
        NativeMethods.cs1i_table_field_bytes(doc, f, j, buffer, n);
        return buffer;
    }

    private static IReadOnlyList<DecompiledInstruction> BuildInstructions(IntPtr doc, int f)
    {
        var count = NativeMethods.cs1i_func_ninstr(doc, f);
        var offsets = new int[count];
        var offsetToIndex = new Dictionary<int, int>(count);
        for (var k = 0; k < count; k++)
        {
            offsets[k] = NativeMethods.cs1i_instr_offset(doc, f, k);
            offsetToIndex[offsets[k]] = k;
        }

        var list = new List<DecompiledInstruction>(count);
        for (var k = 0; k < count; k++)
        {
            var name = Str(NativeMethods.cs1i_instr_name(doc, f, k)) ?? "??";
            var opcode = NativeMethods.cs1i_instr_op(doc, f, k);
            var argCount = NativeMethods.cs1i_instr_argc(doc, f, k);

            var args = new List<InstructionArgument>(argCount);
            var jumps = new List<JumpTarget>();
            for (var a = 0; a < argCount; a++)
            {
                var type = Str(NativeMethods.cs1i_instr_argtype(doc, f, k, a)) ?? "?";
                var arg = BuildArgument(doc, f, k, a, type);
                args.Add(arg);

                if (type == "ptr32")
                {
                    var target = arg.IntValue;
                    var targetIndex = offsetToIndex.TryGetValue(target, out var idx) ? idx : -1;
                    jumps.Add(new JumpTarget(a, targetIndex, target));
                }
            }

            list.Add(new DecompiledInstruction(k, offsets[k], name, opcode, args, jumps));
        }

        return list;
    }

    private static InstructionArgument BuildArgument(IntPtr doc, int f, int k, int a, string type)
    {
        var nm = Str(NativeMethods.cs1i_instr_argname(doc, f, k, a));
        var sem = Str(NativeMethods.cs1i_instr_argsem(doc, f, k, a));
        var semArg = Str(NativeMethods.cs1i_instr_argsem_arg(doc, f, k, a));
        var span = NativeMethods.cs1i_instr_argsem_span(doc, f, k, a);

        if (type == "expr")
        {
            return new InstructionArgument(a, "expr", type, 0, 0, Array.Empty<byte>(),
                BuildExpression(doc, f, k, a), nm, sem, semArg, span);
        }

        if (ScalarTypes.Contains(type))
        {
            var iv = NativeMethods.cs1i_instr_argi(doc, f, k, a);
            var fv = NativeMethods.cs1i_instr_argf(doc, f, k, a);
            return new InstructionArgument(a, "scalar", type, iv, fv, Array.Empty<byte>(),
                null, nm, sem, semArg, span);
        }

        // string / dialog / bytes : contenu brut
        var raw = ArgBytes(doc, f, k, a);
        var kind = type switch
        {
            "string" => "string",
            "dialog" => "dialog",
            _ => "bytes",
        };
        return new InstructionArgument(a, kind, type, 0, 0, raw, null, nm, sem, semArg, span);
    }

    private static IReadOnlyList<ExprElement> BuildExpression(IntPtr doc, int f, int k, int a)
    {
        var count = NativeMethods.cs1i_expr_count(doc, f, k, a);
        if (count <= 0)
        {
            return Array.Empty<ExprElement>();
        }

        var elements = new List<ExprElement>(count);
        for (var i = 0; i < count; i++)
        {
            var subop = NativeMethods.cs1i_expr_subop(doc, f, k, a, i);
            var kind = Str(NativeMethods.cs1i_expr_kind(doc, f, k, a, i)) ?? "?";
            var label = Str(NativeMethods.cs1i_expr_elem_label(doc, f, k, a, i)) ?? string.Empty;
            var value = NativeMethods.cs1i_expr_value(doc, f, k, a, i);
            var nested = Str(NativeMethods.cs1i_expr_nested_name(doc, f, k, a, i));
            elements.Add(new ExprElement(subop, kind, label, value, nested));
        }

        return elements;
    }

    private static byte[] ArgBytes(IntPtr doc, int f, int k, int a)
    {
        var n = NativeMethods.cs1i_instr_argbytes(doc, f, k, a, null, 0);
        if (n <= 0)
        {
            return Array.Empty<byte>();
        }

        var buffer = new byte[n];
        NativeMethods.cs1i_instr_argbytes(doc, f, k, a, buffer, n);
        return buffer;
    }

    private static string? Str(IntPtr ptr) =>
        ptr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ptr);
}
