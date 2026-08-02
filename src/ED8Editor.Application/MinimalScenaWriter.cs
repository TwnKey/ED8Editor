using System.Buffers.Binary;
using System.Text;

namespace ED8Editor.Application;

/// <summary>
/// Writes the scene script a map needs, from nothing.
///
/// A map runs a script when it loads, and without one it does not come up. The
/// smallest the game ships is 69 bytes — <c>a0001.dat</c>, holding an
/// <c>Init</c> and a <c>Reinit</c> that do nothing but return — so there is no
/// reason to copy somebody else's.
///
/// The layout was read off that file and checked by rebuilding it: seven words,
/// a marker, the script's name, a table of code offsets, a table of name offsets,
/// the names, then the code. Generating <c>a0001</c> this way gives its 69 bytes
/// back exactly, which is what says the layout is right rather than merely
/// plausible.
/// </summary>
public static class MinimalScenaWriter
{
    /// <summary>The word the format puts after its seven header fields.</summary>
    private static readonly byte[] Marker = { 0x00, 0xEF, 0xCD, 0xAB };

    /// <summary>One opcode: a function that returns and does nothing else.</summary>
    private const byte Return = 0x01;

    /// <summary>
    /// The two functions a map's script has to offer. <c>Init</c> runs when the
    /// map is entered, <c>Reinit</c> when it is returned to; the game's own
    /// smallest map declares exactly these.
    /// </summary>
    public static readonly string[] MapFunctions = { "Init", "Reinit" };

    /// <summary>
    /// A script called <paramref name="name"/> whose functions all return at once.
    /// </summary>
    public static byte[] Write(string name, IReadOnlyList<string>? functions = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A script needs a name.", nameof(name));
        }
        var wanted = functions ?? MapFunctions;
        if (wanted.Count == 0)
        {
            throw new ArgumentException("A script needs at least one function.", nameof(functions));
        }

        var nameAt = 7 * sizeof(uint) + Marker.Length;
        var codeTableAt = nameAt + Encoding.ASCII.GetByteCount(name) + 1;
        var nameTableAt = codeTableAt + sizeof(uint) * wanted.Count;
        var namesAt = nameTableAt + sizeof(ushort) * wanted.Count;

        var strings = new List<byte>();
        var nameOffsets = new List<int>();
        foreach (var function in wanted)
        {
            nameOffsets.Add(namesAt + strings.Count);
            strings.AddRange(Encoding.ASCII.GetBytes(function));
            strings.Add(0);
        }

        // Each function's code starts on a four-byte boundary, which is what the
        // padding between them in the game's own file comes to.
        var codeAt = namesAt + strings.Count;
        var codeOffsets = new List<int>();
        var code = new List<byte>();
        foreach (var _ in wanted)
        {
            while ((codeAt + code.Count) % sizeof(uint) != 0) code.Add(0);
            codeOffsets.Add(codeAt + code.Count);
            code.Add(Return);
        }

        var output = new MemoryStream();
        void Word(int value)
        {
            var bytes = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)value);
            output.Write(bytes);
        }

        Word(nameAt);
        Word(nameAt);
        Word(codeTableAt);
        Word(sizeof(uint) * wanted.Count);
        Word(nameTableAt);
        Word(wanted.Count);
        Word(codeAt);
        output.Write(Marker);
        output.Write(Encoding.ASCII.GetBytes(name));
        output.WriteByte(0);
        foreach (var offset in codeOffsets) Word(offset);
        foreach (var offset in nameOffsets)
        {
            var bytes = new byte[sizeof(ushort)];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)offset);
            output.Write(bytes);
        }
        output.Write(strings.ToArray());
        output.Write(code.ToArray());
        return output.ToArray();
    }
}
