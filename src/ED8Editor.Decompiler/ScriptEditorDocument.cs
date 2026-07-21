using System.Runtime.InteropServices;

namespace ED8Editor.Decompiler;

/// <summary>
/// Editable script document backed by the native decoder. The native handle stays alive
/// for the whole editing session, so instruction identities and symbolic jump targets are
/// preserved until serialization performs the final relocation.
/// </summary>
public sealed class ScriptEditorDocument : IDisposable
{
    private IntPtr document;

    private ScriptEditorDocument(IntPtr document, string sourcePath)
    {
        this.document = document;
        SourcePath = Path.GetFullPath(sourcePath);
    }

    public string SourcePath { get; }
    public string? SavedPath { get; private set; }
    public bool IsDirty { get; private set; }
    public DecompiledScript Snapshot => ScriptDecompiler.Build(Handle);

    public static ScriptEditorDocument Open(string path, string? instructionsJsonPath = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.", nameof(path));
        ScriptDecompiler.EnsureRegistry(instructionsJsonPath);
        var data = File.ReadAllBytes(path);
        var handle = NativeMethods.cs1i_open(data, data.Length, Path.GetFileName(path));
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"The native engine could not open '{path}'.");
        return new ScriptEditorDocument(handle, path);
    }

    public static IReadOnlyList<string> GetInstructionNames(string? instructionsJsonPath = null)
    {
        ScriptDecompiler.EnsureRegistry(instructionsJsonPath);
        var count = NativeMethods.cs1i_reg_count();
        var names = new List<string>(Math.Max(0, count));
        for (var index = 0; index < count; index++)
        {
            var pointer = NativeMethods.cs1i_reg_name(index);
            var name = pointer == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(pointer);
            if (!string.IsNullOrEmpty(name)) names.Add(name);
        }
        return names;
    }

    public void SetInteger(int function, int instruction, int argument, int value) =>
        Mutate(NativeMethods.cs1i_instr_set_i(Handle, function, instruction, argument, value), "update the integer");

    public void SetFloat(int function, int instruction, int argument, double value) =>
        Mutate(NativeMethods.cs1i_instr_set_f(Handle, function, instruction, argument, value), "update the floating-point value");

    public void SetString(int function, int instruction, int argument, string value) =>
        Mutate(NativeMethods.cs1i_instr_set_s(Handle, function, instruction, argument, value), "update the text");

    public void SetJump(int function, int instruction, int argument, int targetFunction, int targetInstruction) =>
        Mutate(NativeMethods.cs1i_instr_set_jump(
            Handle, function, instruction, argument, targetFunction, targetInstruction), "update the branch");

    public void InsertInstruction(int function, int position, string name)
    {
        if (NativeMethods.cs1i_instr_insert(Handle, function, position, name) < 0)
            throw new InvalidOperationException($"The native engine could not insert instruction '{name}'.");
        IsDirty = true;
    }

    public void RemoveInstruction(int function, int instruction) =>
        Mutate(NativeMethods.cs1i_instr_remove(Handle, function, instruction), "delete the instruction");

    public void ReplaceInstruction(int function, int instruction, string name) =>
        Mutate(NativeMethods.cs1i_instr_replace(Handle, function, instruction, name), "replace the instruction");

    public void MoveInstruction(int function, int from, int to) =>
        Mutate(NativeMethods.cs1i_instr_move(Handle, function, from, to), "move the instruction");

    public void ReplaceExpression(
        int function, int instruction, int argument, IReadOnlyList<ScriptExpressionToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        foreach (var token in tokens) ValidateExpressionToken(token);
        if (NativeMethods.cs1i_arg_expr_clear(Handle, function, instruction, argument) == 0)
            throw new InvalidOperationException("The native engine could not clear the expression.");
        foreach (var token in tokens)
        {
            if (NativeMethods.cs1i_arg_expr_push(
                    Handle, function, instruction, argument, token.SubOp, token.Value) == 0)
                throw new InvalidOperationException($"The native engine rejected expression token 0x{token.SubOp:X2}.");
        }
        IsDirty = true;
    }

    public void Save(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.", nameof(path));
        var pointer = NativeMethods.cs1i_serialize(Handle, out var length);
        if (pointer == IntPtr.Zero || length <= 0)
            throw new InvalidOperationException("The native engine could not serialize the script.");

        var bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The save path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        SavedPath = fullPath;
        IsDirty = false;
    }

    public void Dispose()
    {
        if (document == IntPtr.Zero) return;
        NativeMethods.cs1i_close(document);
        document = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }

    private IntPtr Handle => document != IntPtr.Zero
        ? document
        : throw new ObjectDisposedException(nameof(ScriptEditorDocument));

    private void Mutate(int result, string operation)
    {
        if (result == 0)
            throw new InvalidOperationException($"The native engine could not {operation}.");
        IsDirty = true;
    }

    private static void ValidateExpressionToken(ScriptExpressionToken token)
    {
        var supported = token.SubOp == 0x00
            || token.SubOp is >= 0x02 and <= 0x23 && token.SubOp != 0x1c;
        if (!supported)
            throw new ArgumentException($"Expression token 0x{token.SubOp:X2} cannot be constructed.");
        var (minimum, maximum) = token.SubOp switch
        {
            0x00 => (int.MinValue, int.MaxValue),
            0x1e => (0, (int)ushort.MaxValue),
            0x1f or 0x20 or 0x23 => (0, (int)byte.MaxValue),
            0x21 => (0, 0x00ff_ffff),
            _ => (0, 0),
        };
        if (token.Value < minimum || token.Value > maximum)
            throw new ArgumentOutOfRangeException(nameof(token),
                $"Value {token.Value} is outside the range for token 0x{token.SubOp:X2}.");
    }
}

public sealed record ScriptExpressionToken(int SubOp, int Value);
