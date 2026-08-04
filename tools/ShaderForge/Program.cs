using System.Text;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

// Compiles an entry point of the game's own effect source, and reads a compiled blob
// back, so the two can be compared without launching anything.
//
// The source a shader cluster carries is an FX file: pure HLSL for its first 8229
// lines, then technique11 blocks that D3DCompile cannot parse. The tail is cut off
// here rather than by hand, so the cut moves with the file.
//
//   ShaderForge compile <source.fx> <entree> <profil> [-D NOM]... [--out blob.dxbc]
//   ShaderForge inspect <blob.dxbc>
//   ShaderForge compare <notre.dxbc> <livre.dxbc>

if (args.Length == 0)
{
    Console.WriteLine("compile <source.fx> <entree> <profil> [-D NOM]... [--out fichier]");
    Console.WriteLine("inspect <blob.dxbc>");
    Console.WriteLine("compare <notre.dxbc> <livre.dxbc>");
    return 1;
}

switch (args[0])
{
    case "compile": return Compile(args);
    case "inspect": return Inspect(File.ReadAllBytes(args[1]), Path.GetFileName(args[1]));
    case "compare": return Compare(args[1], args[2]);
    case "reflect": return ED8Editor.ShaderForge.Reflection.Report(args[1]);
    case "plan": return ED8Editor.ShaderForge.Generation.Plan(args[1]);
    case "semantics": return ED8Editor.ShaderForge.Generation.Semantics(args[1..]);
    case "streams": return ED8Editor.ShaderForge.Generation.Streams(args[1..]);
    case "capture": return ED8Editor.ShaderForge.Generation.Capture(args[1]);
    case "interface": return ED8Editor.ShaderForge.Interface.Report(
        args[1], args.Length > 2 ? args[2] : null);
    case "forge":
    {
        var switchAt = Array.IndexOf(args, "-M");
        var named = Array.IndexOf(args, "--as");
        return ED8Editor.ShaderForge.Forging.Forge(
            args[1], args[2], args[3],
            switchAt >= 0 && switchAt + 1 < args.Length
                ? args[switchAt + 1].Split(',', StringSplitOptions.RemoveEmptyEntries)
                : null,
            named >= 0 && named + 1 < args.Length ? args[named + 1] : null,
            (source, entry, defines) => CompileOne(source, entry, defines),
            Array.IndexOf(args, "--interface") >= 0);
    }

    case "variants": return ED8Editor.ShaderForge.Variants.Report(
        args[1], args[2], (entry, defines) => CompileOne(args[2], entry, defines));
    default:
        Console.WriteLine($"commande inconnue : {args[0]}");
        return 1;
}

/// <summary>One entry point, compiled from the source, or null when it will not.</summary>
static byte[]? CompileOne(string sourcePath, string entry, IReadOnlyList<string> defines)
{
    var built = new List<string> { "compile", sourcePath, entry,
        entry.Contains("VPShader", StringComparison.Ordinal) ? "vs_5_0" : "ps_5_0" };
    foreach (var one in defines) { built.Add("-D"); built.Add(one); }
    var quiet = Console.Out;
    Console.SetOut(TextWriter.Null);
    try
    {
        Forge.Produced = null;
        Compile(built.ToArray());
        return Forge.Produced;
    }
    finally { Console.SetOut(quiet); }
}

static int Compile(string[] args)
{
    if (args.Length < 4)
    {
        Console.WriteLine("compile <source.fx> <entree> <profil> [-D NOM]... [--out fichier]");
        return 1;
    }
    var wholeText = NormaliseDirectives(File.ReadAllText(args[1]), out var normalised);
    Console.WriteLine($"  source : {wholeText.Split('\n').Length} lignes,"
        + $" {normalised} directive(s) normalisee(s)");

    var defines = new List<string>();
    string? output = null;
    string? dump = null;
    for (var at = 4; at < args.Length; at++)
    {
        if (args[at] == "-D" && at + 1 < args.Length) defines.Add(args[++at]);
        else if (args[at] == "--out" && at + 1 < args.Length) output = args[++at];
        else if (args[at] == "--dump" && at + 1 < args.Length) dump = args[++at];
    }
    if (defines.Count != 0) Console.WriteLine($"  defines : {string.Join(" ", defines)}");

    var macros = defines
        .Select(one =>
        {
            var cut = one.IndexOf('=');
            return cut < 0
                ? new ShaderMacro(one, "1")
                : new ShaderMacro(one[..cut], one[(cut + 1)..]);
        })
        .ToArray();

    // Preprocessed first, then the techniques taken out.
    //
    // Cutting the effect syntax off the raw text does not work: a technique sits
    // inside nested #if chains, so any textual cut leaves the preprocessor with
    // blocks nothing closes — and directives inside block comments make counting
    // them wrong as well. Once the file is preprocessed only the taken branches
    // remain, the techniques stand at the top level, and matching braces removes
    // them exactly.
    var raw = Encoding.UTF8.GetBytes(wholeText);
    var pinned = System.Runtime.InteropServices.GCHandle.Alloc(
        raw, System.Runtime.InteropServices.GCHandleType.Pinned);
    Vortice.Direct3D.Blob? expanded;
    Vortice.Direct3D.Blob? preprocessErrors;
    try
    {
        // A terminating pair. The native call reads the array until it meets one
        // whose name is null; without it the preprocessor walks off the end, which
        // shows up as an access violation rather than an error message.
        var terminated = macros.Length == 0
            ? Array.Empty<ShaderMacro>()
            : macros.Append(new ShaderMacro(null!, null!)).ToArray();
        Compiler.Preprocess(
            pinned.AddrOfPinnedObject(), raw.Length, "ed8.fx", terminated, null!,
            out expanded, out preprocessErrors);
    }
    catch (Exception failure)
    {
        Console.WriteLine($"  ECHEC au preprocesseur : {failure.Message}");
        return 1;
    }
    finally
    {
        pinned.Free();
    }
    if (expanded is null)
    {
        Console.WriteLine("  ECHEC au preprocesseur");
        Console.WriteLine(preprocessErrors?.AsString() ?? "(sans message)");
        return 1;
    }
    var source = RemoveTechniques(expanded.AsString(), out var removed);
    if (dump is not null) File.WriteAllText(dump, source);
    Console.WriteLine($"  preprocesse : {source.Split('\n').Length} lignes,"
        + $" {removed} technique(s) retiree(s)");

    try
    {
        // The same flags the shipped blobs were built with are not known, so the
        // default is used and the comparison is made on what the engine actually
        // reads: the signatures and the constant buffer layout.
        // Terminated, as the preprocessor call is: the native side walks the array
        // until a null name.
        var compileMacros = macros.Length == 0
            ? macros
            : macros.Append(new ShaderMacro(null!, null!)).ToArray();
        var result = Compiler.Compile(
            source,
            compileMacros,
            include: null!,
            entryPoint: args[2],
            sourceName: "ed8.fx",
            profile: args[3],
            shaderFlags: ShaderFlags.OptimizationLevel3,
            effectFlags: EffectFlags.None,
            out var blob,
            out var errors);
        if (result.Failure || blob is null)
        {
            Console.WriteLine("  ECHEC");
            Console.WriteLine(errors is null ? result.Description : errors.AsString());
            return 1;
        }
        var bytes = blob.AsBytes();
        Forge.Produced = bytes;
        Console.WriteLine($"  compile : {bytes.Length} octets");
        if (errors is not null && errors.AsString() is { } warned && warned.Trim().Length != 0)
        {
            Console.WriteLine("  avertissements :");
            Console.WriteLine(warned);
        }
        if (output is not null)
        {
            File.WriteAllBytes(output, bytes);
            Console.WriteLine($"  ecrit dans {output}");
        }
        return Inspect(bytes, "notre compilation");
    }
    catch (Exception failure)
    {
        Console.WriteLine($"  ECHEC : {failure.Message}");
        return 1;
    }
}

/// <summary>
/// Turns "#endif NAME" into "#endif // NAME".
///
/// The source was written for the compiler of 2010 — the blobs the game ships name
/// it, "HLSL Shader Compiler 9.29.952.3111" — which took a bare token after #endif.
/// Today's rejects it. Eight lines in ed8.fx do this, all of them "#endif
/// ALPHA_TESTING_ENABLED"; commenting the token changes nothing of what the file
/// means, and is preferable to editing a copy of the source by hand.
/// </summary>
static string NormaliseDirectives(string source, out int fixedUp)
{
    var count = 0;
    var result = System.Text.RegularExpressions.Regex.Replace(
        source,
        @"(?m)^(\s*#endif)[ 	]+(?![/*])(\S.*)$",
        match => { count++; return $"{match.Groups[1].Value} // {match.Groups[2].Value}"; });
    fixedUp = count;
    return result;
}

/// <summary>
/// Removes every "technique11 NAME &lt;…&gt; { … }" from preprocessed text.
///
/// The annotation block and the body are both taken by matching their delimiters, so
/// a technique holding passes with their own braces goes whole.
/// </summary>
static string RemoveTechniques(string source, out int removed)
{
    removed = 0;
    var text = new StringBuilder(source.Length);
    var at = 0;
    while (true)
    {
        var found = source.IndexOf("technique11", at, StringComparison.Ordinal);
        if (found < 0) { text.Append(source, at, source.Length - at); break; }
        text.Append(source, at, found - at);
        removed++;

        // The keyword, then the name, then the annotations if any, then the body.
        var walk = found + "technique11".Length;
        while (walk < source.Length && char.IsWhiteSpace(source[walk])) walk++;
        while (walk < source.Length
            && (char.IsLetterOrDigit(source[walk]) || source[walk] == '_')) walk++;
        walk = Skip(source, walk, '<', '>');
        walk = Skip(source, walk, '{', '}');
        at = walk;
    }
    return text.ToString();
}

/// <summary>
/// Past a delimited run starting at or after <paramref name="from"/>, counting nested
/// pairs. When the opening delimiter is not the next thing seen, nothing is skipped.
/// </summary>
static int Skip(string source, int from, char opens, char closes)
{
    var at = from;
    // Whitespace, and the #line directives the preprocessor leaves behind. One of
    // those sits between a technique's annotations and its body, and stopping on it
    // left the body in place — a lone brace the compiler could make nothing of.
    while (at < source.Length)
    {
        if (char.IsWhiteSpace(source[at])) { at++; continue; }
        if (source[at] != '#') break;
        while (at < source.Length && source[at] != '\n') at++;
    }
    if (at >= source.Length || source[at] != opens) return from;
    var depth = 0;
    for (; at < source.Length; at++)
    {
        if (source[at] == opens) depth++;
        else if (source[at] == closes && --depth == 0) return at + 1;
    }
    return source.Length;
}

static int Inspect(byte[] blob, string label)
{
    Console.WriteLine($"### {label} — {blob.Length} octets");
    if (blob.Length < 32 || Encoding.ASCII.GetString(blob, 0, 4) != "DXBC")
    {
        Console.WriteLine("   ce n'est pas un blob DXBC");
        return 1;
    }
    var chunks = BitConverter.ToInt32(blob, 28);
    for (var at = 0; at < chunks; at++)
    {
        var start = BitConverter.ToInt32(blob, 32 + 4 * at);
        var tag = Encoding.ASCII.GetString(blob, start, 4);
        var size = BitConverter.ToInt32(blob, start + 4);
        Console.WriteLine($"   {tag} {size,7} octets");
        switch (tag)
        {
            case "RDEF": ReadDefinitions(blob, start + 8); break;
            case "ISGN": ReadSignature(blob, start + 8, "entrees"); break;
            case "OSGN": ReadSignature(blob, start + 8, "sorties"); break;
        }
    }
    return 0;
}

static void ReadDefinitions(byte[] blob, int body)
{
    var buffers = BitConverter.ToInt32(blob, body);
    var bufferAt = BitConverter.ToInt32(blob, body + 4);
    var version = BitConverter.ToInt16(blob, body + 16);
    var creator = BitConverter.ToInt32(blob, body + 24);
    Console.WriteLine($"      profil 0x{version:X4}  compilateur : {Text(blob, body + creator)}");
    for (var at = 0; at < buffers; at++)
    {
        var one = body + bufferAt + at * 24;
        var name = Text(blob, body + BitConverter.ToInt32(blob, one));
        var variables = BitConverter.ToInt32(blob, one + 4);
        var size = BitConverter.ToInt32(blob, one + 12);
        Console.WriteLine($"      buffer '{name}' : {variables} variables, {size} octets");
    }
}

static void ReadSignature(byte[] blob, int body, string label)
{
    var count = BitConverter.ToInt32(blob, body);
    var names = new List<string>();
    for (var at = 0; at < count; at++)
    {
        var one = body + 8 + at * 24;
        names.Add(Text(blob, body + BitConverter.ToInt32(blob, one))
            + BitConverter.ToInt32(blob, one + 4));
    }
    Console.WriteLine($"      {label} : {string.Join(", ", names)}");
}

static string Text(byte[] blob, int at)
{
    var end = at;
    while (end < blob.Length && blob[end] != 0) end++;
    return Encoding.ASCII.GetString(blob, at, end - at);
}

static int Compare(string ours, string shipped)
{
    var a = File.ReadAllBytes(ours);
    var b = File.ReadAllBytes(shipped);
    Console.WriteLine($"  notre {a.Length} octets, livre {b.Length} octets");
    Inspect(a, "nous");
    Inspect(b, "livre");
    return 0;
}


/// <summary>The blob the last compile produced, so a caller can take it.</summary>
internal static class Forge
{
    public static byte[]? Produced;
}
