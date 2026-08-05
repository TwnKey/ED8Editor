using System.Text;

namespace ED8Editor.ShaderForge;

/// <summary>
/// What a compiled program declares about itself, read straight out of its DXBC.
///
/// This is the half a shader cluster describes in its own objects: every constant
/// the shader reads, where it sits in the buffer and how big it is, every texture and
/// sampler it binds and at which register, and the vertex inputs it expects. Today
/// those objects are copied from a template, which is why only shaders keeping the
/// template's interface can be written. Generating them from here is what lifts that.
///
/// The reflection chunk is read by hand rather than through ID3D11ShaderReflection:
/// the layout is documented and stable, and a hand read gives the same numbers on any
/// machine without a device or a COM apartment.
/// </summary>
public static class Reflection
{
    /// <summary>
    /// A constant of the globals buffer. <paramref name="Class"/> and
    /// <paramref name="Type"/> are D3D's own: class 1 is a scalar, 2 a vector,
    /// 3 a column matrix; type 3 is uint and 4 float.
    /// </summary>
    public sealed record Constant(
        string Name, uint Offset, uint Size,
        uint Class, uint Type, uint Rows, uint Columns, uint Elements);

    /// <summary>
    /// A texture, sampler or buffer the program binds. <paramref name="Type"/> is
    /// D3D's input type — 0 a constant buffer, 2 a texture, 3 a sampler — and
    /// <paramref name="Dimension"/> tells a 2D texture (4) from a 3D one (8) or a
    /// cube (9).
    /// </summary>
    public sealed record Binding(
        string Name, uint Type, uint Dimension, uint BindPoint, uint BindCount);

    public sealed record Signature(string Name, uint Index, uint Register, uint Mask);

    public sealed record Program(
        string Buffer,
        uint BufferSize,
        IReadOnlyList<Constant> Constants,
        IReadOnlyList<Binding> Bindings,
        IReadOnlyList<Signature> Inputs,
        IReadOnlyList<Signature> Outputs);

    public static Program Read(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        var chunks = Chunks(blob);
        var (name, size, constants, bindings) = chunks.TryGetValue("RDEF", out var rdef)
            ? Definitions(blob, rdef)
            : (string.Empty, 0u, Array.Empty<Constant>(), (IReadOnlyList<Binding>)Array.Empty<Binding>());
        return new Program(
            name,
            size,
            constants,
            bindings,
            chunks.TryGetValue("ISGN", out var isgn) ? Signatures(blob, isgn) : Array.Empty<Signature>(),
            chunks.TryGetValue("OSGN", out var osgn) ? Signatures(blob, osgn) : Array.Empty<Signature>());
    }

    private static Dictionary<string, int> Chunks(byte[] blob)
    {
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        // Anything that is not a container is no use here, and saying so beats
        // reading a length out of the middle of some other kind of data.
        if (blob.Length < 32 || !blob.AsSpan(0, 4).SequenceEqual("DXBC"u8)) return found;
        var count = BitConverter.ToInt32(blob, 28);
        for (var at = 0; at < count; at++)
        {
            var start = BitConverter.ToInt32(blob, 32 + 4 * at);
            if (start < 0 || start + 8 > blob.Length) continue;
            found[Encoding.ASCII.GetString(blob, start, 4)] = start + 8;
        }
        return found;
    }

    private static (string, uint, IReadOnlyList<Constant>, IReadOnlyList<Binding>) Definitions(
        byte[] blob, int body)
    {
        var bufferCount = BitConverter.ToInt32(blob, body);
        var bufferAt = BitConverter.ToInt32(blob, body + 4);
        var bindCount = BitConverter.ToInt32(blob, body + 8);
        var bindAt = BitConverter.ToInt32(blob, body + 12);
        // Shader model 5 grew the variable record from 24 bytes to 40, adding where
        // its textures and samplers start. The game ships both: ed8.fx is model 5,
        // generic.fx and postfx.fx are model 4, and reading one as the other walks
        // off the end of the chunk.
        var model5 = blob[body + 17] >= 5;
        var variableSize = model5 ? 40 : 24;

        var bindings = new List<Binding>();
        for (var at = 0; at < bindCount; at++)
        {
            // Eight words: name, type, return type, dimension, sample count, then the
            // bind point at +20 and the count at +24.
            var one = body + bindAt + at * 32;
            bindings.Add(new Binding(
                Text(blob, body + BitConverter.ToInt32(blob, one)),
                BitConverter.ToUInt32(blob, one + 4),
                BitConverter.ToUInt32(blob, one + 12),
                BitConverter.ToUInt32(blob, one + 20),
                BitConverter.ToUInt32(blob, one + 24)));
        }

        var name = string.Empty;
        var size = 0u;
        var constants = new List<Constant>();
        for (var at = 0; at < bufferCount; at++)
        {
            var one = body + bufferAt + at * 24;
            var bufferName = Text(blob, body + BitConverter.ToInt32(blob, one));
            var variables = BitConverter.ToInt32(blob, one + 4);
            var variableAt = BitConverter.ToInt32(blob, one + 8);
            // Only the globals: a shader may declare several buffers, and it is that
            // one the material's parameter block fills.
            if (!bufferName.Equals("$Globals", StringComparison.Ordinal)) continue;
            name = bufferName;
            size = BitConverter.ToUInt32(blob, one + 12);
            for (var which = 0; which < variables; which++)
            {
                // A variable is 40 bytes: name, start offset, size, flags, then the
                // offset of its type at +16. Reading the type from +12 gets the flags
                // instead, which is why the same constant read as one thing in one
                // blob and another in the next.
                var variable = body + variableAt + which * variableSize;
                var typeAt = body + BitConverter.ToInt32(blob, variable + 16);
                var declared = Text(blob, body + BitConverter.ToInt32(blob, variable));
                var offset = BitConverter.ToUInt32(blob, variable + 4);
                // A struct is one variable here but many parameters to the engine: the
                // light a shader is drawn with arrives as a struct, and it is its
                // members the effect names. Class 5 is the struct, and its type record
                // holds how many members it has at +10 and where they are at +12.
                if (BitConverter.ToUInt16(blob, typeAt) == 5)
                {
                    var members = BitConverter.ToUInt16(blob, typeAt + 10);
                    var memberAt = body + BitConverter.ToInt32(blob, typeAt + 12);
                    for (var member = 0; member < members; member++)
                    {
                        var record = memberAt + member * 12;
                        var memberType = body + BitConverter.ToInt32(blob, record + 4);
                        constants.Add(new Constant(
                            Text(blob, body + BitConverter.ToInt32(blob, record)),
                            offset + BitConverter.ToUInt32(blob, record + 8),
                            0,
                            BitConverter.ToUInt16(blob, memberType),
                            BitConverter.ToUInt16(blob, memberType + 2),
                            BitConverter.ToUInt16(blob, memberType + 4),
                            BitConverter.ToUInt16(blob, memberType + 6),
                            BitConverter.ToUInt16(blob, memberType + 8)));
                    }
                    continue;
                }
                constants.Add(new Constant(
                    declared,
                    offset,
                    BitConverter.ToUInt32(blob, variable + 8),
                    BitConverter.ToUInt16(blob, typeAt),
                    BitConverter.ToUInt16(blob, typeAt + 2),
                    BitConverter.ToUInt16(blob, typeAt + 4),
                    BitConverter.ToUInt16(blob, typeAt + 6),
                    BitConverter.ToUInt16(blob, typeAt + 8)));
            }
        }
        return (name, size, constants, bindings);
    }

    private static IReadOnlyList<Signature> Signatures(byte[] blob, int body)
    {
        var count = BitConverter.ToInt32(blob, body);
        var found = new List<Signature>();
        for (var at = 0; at < count; at++)
        {
            var one = body + 8 + at * 24;
            found.Add(new Signature(
                Text(blob, body + BitConverter.ToInt32(blob, one)),
                BitConverter.ToUInt32(blob, one + 4),
                BitConverter.ToUInt32(blob, one + 16),
                blob[one + 20]));
        }
        return found;
    }

    private static string Text(byte[] blob, int at)
    {
        var end = at;
        while (end < blob.Length && blob[end] != 0) end++;
        return Encoding.ASCII.GetString(blob, at, end - at);
    }

    public static int Report(string path)
    {
        var read = Read(File.ReadAllBytes(path));
        Console.WriteLine($"  {read.Buffer} : {read.Constants.Count} constantes,"
            + $" {read.BufferSize} octets");
        foreach (var one in read.Constants.OrderBy(value => value.Name, StringComparer.Ordinal).Take(400))
        {
            Console.WriteLine($"     +{one.Offset,-5} {one.Size,4} o  classe {one.Class}"
                + $" type {one.Type} {one.Rows}x{one.Columns} [{one.Elements}]  {one.Name}");
        }

        Console.WriteLine($"  {read.Bindings.Count} liaison(s) de ressource");
        foreach (var one in read.Bindings.Take(40))
        {
            Console.WriteLine($"     registre {one.BindPoint,-3} x{one.BindCount}"
                + $"  type {one.Type}  {one.Name}");
        }
        if (read.Bindings.Count > 8) Console.WriteLine($"     … et {read.Bindings.Count - 8} autres");
        Console.WriteLine($"  entrees : {string.Join(", ",
            read.Inputs.Select(value => $"{value.Name}{value.Index}@r{value.Register}"))}");
        return 0;
    }
}
