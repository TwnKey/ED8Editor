using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ED8Editor.Phyre.Authoring;

/// <summary>One slot of a material's constant block, named and placed.</summary>
/// <param name="TypeName">
/// What the slot holds, as the buffer's own child record states it: <c>float</c>
/// for a numeric parameter, <c>PUInt32</c> for an integer, and one of the two
/// capture-buffer classes for a texture or a sampler.
/// </param>
/// <param name="Offset">Where in the serialized buffer it starts.</param>
/// <param name="Count">How many numbers it is made of.</param>
public sealed record PhyreMaterialValue(string Name, string TypeName, uint Offset, uint Count)
{
    /// <summary>Whether the slot holds a texture rather than numbers.</summary>
    public bool IsTexture => TypeName == "PShaderParameterCaptureBufferTexture2D";
}

/// <summary>
/// Fills a material's constant block with what the author chose.
///
/// <see cref="PhyreMaterialTableReader.FromEffect"/> lays the block out from the
/// shader's own declarations and leaves every number at zero — which is honest,
/// since nothing there knows what the values should be, but it is not a material
/// anyone wants: a shader whose tint is zero draws black.
///
/// The block is enough to place them without asking the effect again. A definition
/// carries its name in the group's array data and its position in the buffer's own
/// child record, and the two are written in the same order, so the pairing is the
/// buffer's rather than a guess made beside it.
/// </summary>
public static class PhyreMaterialValues
{
    /// <summary>Every slot the block declares, in the order it declares them.</summary>
    public static IReadOnlyList<PhyreMaterialValue> Parameters(PhyreMaterialTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var found = new List<PhyreMaterialValue>(table.Children.Count);
        var names = table.DefinitionArrayData.Span;
        for (var at = 0; at < table.Children.Count && at < table.DefinitionArrays.Count; at++)
        {
            var start = checked((int)table.DefinitionArrays[at].Offset);
            if (start >= names.Length) continue;
            var text = names[start..];
            var end = text.IndexOf((byte)0);
            var name = Encoding.ASCII.GetString(end < 0 ? text : text[..end]).TrimEnd();
            if (name.Length == 0) continue;
            var child = table.Children[at];
            found.Add(new PhyreMaterialValue(name, child.TypeName, child.Offset, child.Count));
        }
        return found;
    }

    /// <summary>
    /// The same block with the named parameters set.
    ///
    /// A value that will not parse, or that has the wrong number of components,
    /// stops this rather than being rounded into something: a material silently
    /// filled with the wrong constants is exactly the failure that is impossible
    /// to see afterwards. A name the block does not declare is ignored — the
    /// author may have typed values against a shader and then chosen another.
    /// </summary>
    /// <param name="values">
    /// What each parameter is to hold, by name. Numbers for a numeric slot, an
    /// asset id for a texture. An empty string leaves the slot as it is.
    /// </param>
    public static PhyreMaterialTable WithValues(
        PhyreMaterialTable table,
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return table;

        var buffer = table.ParameterBufferObject.ToArray();
        var imports = table.Imports.ToList();
        foreach (var parameter in Parameters(table))
        {
            if (!values.TryGetValue(parameter.Name, out var written)) continue;
            written = written.Trim();
            if (written.Length == 0) continue;

            if (parameter.IsTexture)
            {
                // A texture slot names its image through the cluster's import list,
                // not through the buffer: the four bytes at +12 are an import id the
                // writer fills in, so what changes here is which asset that import
                // stands for.
                var source = 0x80000000u | (parameter.Offset + 12);
                var at = imports.FindIndex(value => value.Source == source);
                var replacement = new PhyreMaterialImport(null, source, written);
                if (at < 0) imports.Add(replacement);
                else imports[at] = replacement;
                continue;
            }

            switch (parameter.TypeName)
            {
                case "float":
                    var numbers = Numbers(parameter, written);
                    for (var slot = 0; slot < numbers.Length; slot++)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(
                            buffer.AsSpan(checked((int)parameter.Offset) + slot * 4),
                            BitConverter.SingleToUInt32Bits(numbers[slot]));
                    }
                    break;
                case "PUInt32":
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        buffer.AsSpan(checked((int)parameter.Offset)),
                        Integer(parameter, written));
                    break;
                default:
                    // A sampler state is an object of its own, not a number in the
                    // block; nothing typed into a cell belongs in it.
                    break;
            }
        }

        return table with
        {
            ParameterBufferObject = buffer,
            Imports = imports,
        };
    }

    private static float[] Numbers(PhyreMaterialValue parameter, string written)
    {
        var parts = written.Split(
            new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parameter.TypeName == "float" && parts.Length != parameter.Count)
        {
            throw new InvalidDataException(
                $"'{parameter.Name}' takes {parameter.Count} number(s);"
                + $" {parts.Length} were given.");
        }
        var numbers = new float[parts.Length];
        for (var at = 0; at < parts.Length; at++)
        {
            if (!float.TryParse(
                    parts[at], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[at]))
            {
                throw new InvalidDataException(
                    $"'{parameter.Name}': '{parts[at]}' is not a number.");
            }
        }
        return numbers;
    }

    private static uint Integer(PhyreMaterialValue parameter, string written)
    {
        var text = written.Trim();
        var hexadecimal = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (uint.TryParse(
                hexadecimal ? text[2..] : text,
                hexadecimal ? NumberStyles.HexNumber : NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return value;
        }
        throw new InvalidDataException($"'{parameter.Name}': '{written}' is not a whole number.");
    }
}
