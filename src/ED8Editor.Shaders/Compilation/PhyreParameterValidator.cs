using ED8Editor.Phyre;

namespace ED8Editor.Shaders.Compilation;

/// <summary>
/// Validates that a set of shader parameters (as would be found in a
/// .dae.phyre's PParameterBuffer) matches the parameter definitions in
/// a .fx.phyre.
///
/// This is critical: the game engine requires them to match EXACTLY.
/// </summary>
public sealed class PhyreParameterValidator
{
    /// <summary>
    /// Validates that parameter definitions from a model match those from a shader.
    /// Returns a list of discrepancies (empty = all good).
    /// </summary>
    public IReadOnlyList<ParameterDiscrepancy> Validate(
        byte[] fxPhyreData,
        IReadOnlyList<ParameterDef> modelParameters)
    {
        var discrepancies = new List<ParameterDiscrepancy>();

        try
        {
            var cluster = new PhyreClusterReader().Read(fxPhyreData);
            var paramGroups = cluster.Metadata.InstanceGroups
                .Where(g => g.ClassName == "PShaderParameterDefinition")
                .ToList();

            if (paramGroups.Count == 0)
            {
                if (modelParameters.Count > 0)
                    discrepancies.Add(new ParameterDiscrepancy(
                        "__global__", "Count",
                        "0 (in shader)", $"{modelParameters.Count} (in model)",
                        "The shader has no parameter definitions but the model declares parameters."));
                return discrepancies;
            }

            // Extract shader parameter definitions
            var shaderParams = new List<ParameterDef>();
            foreach (var group in paramGroups)
            {
                for (uint i = 0; i < group.Count; i++)
                {
                    var obj = cluster.GetObject(group.Index, i).Span;
                    // PShaderParameterDefinition layout:
                    // +0x00: m_name (string, via array fixup)
                    // +0x04: m_type (uint)
                    // +0x08: m_offset (uint)
                    // +0x0C: m_size (uint)
                    // +0x10: m_arraySize (uint)

                    // Extract name from fixup
                    var nameFixup = cluster.Fixups.Arrays.FirstOrDefault(f =>
                        f.SourceListIndex == group.Index &&
                        f.SourceObjectId == i &&
                        !f.IsClassDataMember);

                    var name = nameFixup != null
                        ? System.Text.Encoding.ASCII.GetString(
                            cluster.GetArrayData(group.Index, nameFixup.Offset, nameFixup.Count).Span
                                .ToArray())
                            .TrimEnd('\0')
                        : $"<unnamed_{i}>";

                    shaderParams.Add(new ParameterDef(
                        name,
                        ReadU32(obj, 0x04, cluster.Metadata.IsBigEndian),
                        ReadU32(obj, 0x08, cluster.Metadata.IsBigEndian),
                        ReadU32(obj, 0x0C, cluster.Metadata.IsBigEndian),
                        ReadU32(obj, 0x10, cluster.Metadata.IsBigEndian)));
                }
            }

            // Compare
            var modelByName = modelParameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
            var shaderByName = shaderParams.ToDictionary(p => p.Name, StringComparer.Ordinal);

            // Parameters in shader but not in model
            foreach (var (name, sp) in shaderByName)
            {
                if (!modelByName.TryGetValue(name, out var mp))
                {
                    discrepancies.Add(new ParameterDiscrepancy(
                        name, "Missing",
                        FormatParam(sp), "(not present)",
                        $"Parameter '{name}' exists in shader but not in model."));
                }
            }

            // Parameters in model but not in shader
            foreach (var (name, mp) in modelByName)
            {
                if (!shaderByName.TryGetValue(name, out var sp))
                {
                    discrepancies.Add(new ParameterDiscrepancy(
                        name, "Unexpected",
                        "(not present)", FormatParam(mp),
                        $"Parameter '{name}' exists in model but not in shader."));
                }
            }

            // Parameters in both: check offset, size, type
            foreach (var (name, sp) in shaderByName)
            {
                if (!modelByName.TryGetValue(name, out var mp)) continue;

                if (sp.Type != mp.Type)
                {
                    discrepancies.Add(new ParameterDiscrepancy(
                        name, "Type mismatch",
                        $"Type={sp.Type}", $"Type={mp.Type}",
                        "The parameter type differs between shader and model."));
                }

                if (sp.Offset != mp.Offset)
                {
                    discrepancies.Add(new ParameterDiscrepancy(
                        name, "Offset mismatch",
                        $"Offset={sp.Offset}", $"Offset={mp.Offset}",
                        "The parameter offset in the constant buffer differs."));
                }

                if (sp.Size != mp.Size)
                {
                    discrepancies.Add(new ParameterDiscrepancy(
                        name, "Size mismatch",
                        $"Size={sp.Size}", $"Size={mp.Size}",
                        "The parameter size differs."));
                }

                if (sp.ArraySize != mp.ArraySize)
                {
                    discrepancies.Add(new ParameterDiscrepancy(
                        name, "Array size mismatch",
                        $"ArraySize={sp.ArraySize}", $"ArraySize={mp.ArraySize}",
                        "The parameter array size differs."));
                }
            }
        }
        catch (Exception ex)
        {
            discrepancies.Add(new ParameterDiscrepancy(
                "__error__", "Parse error",
                "-", "-", $"Failed to parse shader parameters: {ex.Message}"));
        }

        return discrepancies;
    }

    /// <summary>
    /// Validates that the total constant buffer size matches between shader and model.
    /// </summary>
    public ParameterDiscrepancy? ValidateBufferSize(
        byte[] fxPhyreData,
        uint modelBufferSize)
    {
        try
        {
            var reader = new PhyreEffectRenderPassReader();
            var metadata = reader.ReadMetadata(fxPhyreData);

            if (metadata.Program?.SceneRenderPasses.Values.FirstOrDefault() is not { } pass)
                return null;

            if (pass.Permutations.Count == 0)
                return null;

            var shaderCbSize = (uint)pass.Permutations[0].VertexProgram.ConstantBufferSize;

            if (shaderCbSize != modelBufferSize)
            {
                return new ParameterDiscrepancy(
                    "__buffer__", "Buffer size mismatch",
                    $"Shader expects {shaderCbSize} bytes",
                    $"Model provides {modelBufferSize} bytes",
                    "The constant buffer size declared by the shader differs from the model's PParameterBuffer size.");
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
        => bigEndian
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[offset..])
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);

    private static string FormatParam(ParameterDef p)
        => $"Type={p.Type}, Offset={p.Offset}, Size={p.Size}, ArraySize={p.ArraySize}";
}

public sealed record ParameterDiscrepancy(
    string ParameterName,
    string Issue,
    string ShaderValue,
    string ModelValue,
    string Description);

public sealed record ParameterDef(
    string Name,
    uint Type,
    uint Offset,
    uint Size,
    uint ArraySize);
