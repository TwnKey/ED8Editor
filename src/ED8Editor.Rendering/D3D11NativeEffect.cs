using System.Numerics;
using ED8Editor.Core;
using Vortice.Direct3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11.Shader;
using Vortice.DXGI;

namespace ED8Editor.Rendering;

/// <summary>What the engine hands a shader that a material does not.</summary>
/// <param name="World">Where the thing being drawn is.</param>
/// <param name="MaterialDiffuse">
/// The tint the node being drawn carries. Falcom added this to Phyre as a NODE
/// parameter, so it comes from the thing being drawn rather than from its material
/// — which is why no shipped material supplies it, and why leaving it at zero
/// multiplies every surface by nothing and draws the map black.
/// </param>
public sealed record D3D11EffectFrame(
    Matrix4x4 World,
    Matrix4x4 View,
    Matrix4x4 Projection,
    Vector3 EyePosition,
    Vector3 LightDirection,
    Vector4 LightColor,
    Vector4 AmbientColor,
    float Time,
    Vector4 MaterialDiffuse = default,
    Vector3 MaterialEmission = default);

/// <summary>
/// One of the game's own compiled shaders, ready to draw with.
///
/// The preview has always drawn with shaders of ours: enough to see a shape, never
/// what the game will show. This draws with the program the effect actually
/// carries — the same DXBC the game loads — so what the author sees is what their
/// shader does rather than an impression of it.
///
/// Two things make that possible without guessing. The bytecode names its own
/// constants, so where each value goes is read from the shader rather than agreed
/// with it; and PhyreEngine resolves what a constant means from its name, which is
/// the rule <see cref="PhyreShaderSemantic"/> transcribes — so a uniform called
/// <c>World</c> gets the world matrix and one nothing recognises is the material's
/// to fill, exactly as the engine decides it.
///
/// Anything that does not line up returns null rather than drawing something
/// plausible: a preview that quietly falls back to its own shader while claiming to
/// show the real one is worse than no preview.
/// </summary>
public sealed class D3D11NativeEffect : IDisposable
{
    private readonly ID3D11VertexShader vertexShader;
    private readonly ID3D11PixelShader pixelShader;
    private readonly ID3D11InputLayout inputLayout;
    private readonly ID3D11Buffer? vertexConstants;
    private readonly ID3D11Buffer? pixelConstants;
    private readonly IReadOnlyList<D3D11ShaderVariable> vertexVariables;
    private readonly IReadOnlyList<D3D11ShaderVariable> pixelVariables;
    private readonly int vertexBufferSize;
    private readonly int pixelBufferSize;
    private readonly IReadOnlyList<D3D11ShaderResource> pixelResources;
    private readonly IReadOnlyDictionary<string, uint> contextSwitches;

    /// <summary>Which vertex streams the program insists on, in its own order.</summary>
    public IReadOnlyList<(VertexSemantic Semantic, int Index)> RequiredStreams { get; }

    private D3D11NativeEffect(
        ID3D11VertexShader vertexShader,
        ID3D11PixelShader pixelShader,
        ID3D11InputLayout inputLayout,
        ID3D11Buffer? vertexConstants,
        ID3D11Buffer? pixelConstants,
        IReadOnlyList<D3D11ShaderVariable> vertexVariables,
        IReadOnlyList<D3D11ShaderVariable> pixelVariables,
        int vertexBufferSize,
        int pixelBufferSize,
        IReadOnlyList<VertexSemantic> semantics,
        IReadOnlyList<int> semanticIndices,
        IReadOnlyList<D3D11ShaderResource> pixelResources,
        IReadOnlyDictionary<string, uint> contextSwitches)
    {
        this.pixelResources = pixelResources;
        this.contextSwitches = contextSwitches;
        this.vertexShader = vertexShader;
        this.pixelShader = pixelShader;
        this.inputLayout = inputLayout;
        this.vertexConstants = vertexConstants;
        this.pixelConstants = pixelConstants;
        this.vertexVariables = vertexVariables;
        this.pixelVariables = pixelVariables;
        this.vertexBufferSize = vertexBufferSize;
        this.pixelBufferSize = pixelBufferSize;
        RequiredStreams = semantics
            .Select((value, at) => (value, semanticIndices[at]))
            .ToArray();
    }

    /// <summary>
    /// Builds the program a permutation describes, or says why it cannot be built.
    /// </summary>
    /// <param name="streamOf">
    /// Which vertex buffer of the thing being drawn carries a semantic, and at what
    /// offset and format. A program asking for a stream the mesh has not got cannot
    /// be used on it, and that is a fact about the pair rather than about either.
    /// </param>
    public static D3D11NativeEffect? Create(
        ID3D11Device device,
        CpuShaderPermutation permutation,
        Func<VertexSemantic, int, (int Slot, int Offset, Format Format)?> streamOf,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(permutation);
        ArgumentNullException.ThrowIfNull(streamOf);
        reason = null;

        var vertexCode = permutation.VertexProgram.Bytecode;
        var pixelCode = permutation.FragmentProgram.Bytecode;
        if (vertexCode.Length == 0 || pixelCode.Length == 0)
        {
            reason = "the permutation carries no compiled program";
            return null;
        }

        var declared = permutation.VertexProgram.InputLayout;
        if (declared is null || declared.Count == 0)
        {
            reason = "the vertex program declares no input layout";
            return null;
        }

        var elements = new List<InputElementDescription>(declared.Count);
        var semantics = new List<VertexSemantic>(declared.Count);
        var indices = new List<int>(declared.Count);
        foreach (var element in declared)
        {
            if (!TryReadSemantic(element.Semantic, out var semantic))
            {
                reason = $"the program asks for '{element.Semantic}', which is not a"
                    + " stream this editor reads";
                return null;
            }
            if (streamOf(semantic, element.SemanticIndex) is not { } found)
            {
                reason = $"the mesh has no {element.Semantic}{element.SemanticIndex} stream,"
                    + " which the program reads";
                return null;
            }
            elements.Add(new InputElementDescription(
                element.Semantic, element.SemanticIndex, found.Format, found.Offset, found.Slot));
            semantics.Add(semantic);
            indices.Add(element.SemanticIndex);
        }

        ID3D11VertexShader? vertex = null;
        ID3D11PixelShader? pixel = null;
        ID3D11InputLayout? layout = null;
        try
        {
            vertex = device.CreateVertexShader(vertexCode);
            pixel = device.CreatePixelShader(pixelCode);
            layout = device.CreateInputLayout(elements.ToArray(), vertexCode);

            var inspector = new D3D11ShaderProgramInspector();
            var vertexProgram = inspector.Inspect(
                permutation.VertexProgram, D3D11ShaderStage.Vertex);
            var pixelProgram = inspector.Inspect(
                permutation.FragmentProgram, D3D11ShaderStage.Fragment);
            var vertexGlobals = Globals(vertexProgram);
            var pixelGlobals = Globals(pixelProgram);

            return new D3D11NativeEffect(
                vertex,
                pixel,
                layout,
                Buffer(device, vertexGlobals?.Size ?? 0),
                Buffer(device, pixelGlobals?.Size ?? 0),
                vertexGlobals?.Variables ?? Array.Empty<D3D11ShaderVariable>(),
                pixelGlobals?.Variables ?? Array.Empty<D3D11ShaderVariable>(),
                Rounded(vertexGlobals?.Size ?? 0),
                Rounded(pixelGlobals?.Size ?? 0),
                semantics,
                indices,
                pixelProgram.Resources
                    .Where(value => value.Type == ShaderInputType.Texture)
                    .ToArray(),
                permutation.Context?.PackedSwitchValues
                    ?? new Dictionary<string, uint>(StringComparer.Ordinal));
        }
        catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException
            or InvalidDataException or ArgumentException)
        {
            layout?.Dispose();
            pixel?.Dispose();
            vertex?.Dispose();
            reason = exception.Message;
            return null;
        }
    }

    /// <summary>
    /// Fills both constant buffers and binds the program.
    ///
    /// A value the material supplies wins; anything else the engine feeds is
    /// computed from the frame; and a constant nothing answers for stays zero,
    /// which is what the engine leaves it at too when nothing writes it.
    /// </summary>
    /// <param name="textureOf">
    /// The image bound under a name the shader declares. Asked by NAME and set at
    /// the register the bytecode states, so a shader with its own texture slots is
    /// filled the same way one of the game's is.
    /// </param>
    public void Bind(
        ID3D11DeviceContext context,
        CpuMaterial? material,
        D3D11EffectFrame frame,
        Func<string, ID3D11ShaderResourceView?>? textureOf = null,
        ID3D11SamplerState? sampler = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.IASetInputLayout(inputLayout);
        context.VSSetShader(vertexShader);
        context.PSSetShader(pixelShader);

        if (vertexConstants is not null)
        {
            Fill(context, vertexConstants, vertexBufferSize, vertexVariables, material, frame);
            context.VSSetConstantBuffer(0, vertexConstants);
        }
        if (pixelConstants is not null)
        {
            Fill(context, pixelConstants, pixelBufferSize, pixelVariables, material, frame);
            context.PSSetConstantBuffer(0, pixelConstants);
        }
        foreach (var resource in pixelResources)
        {
            context.PSSetShaderResource(resource.BindPoint, textureOf?.Invoke(resource.Name)!);
            if (sampler is not null) context.PSSetSampler(resource.BindPoint, sampler);
        }
    }

    public void Dispose()
    {
        pixelConstants?.Dispose();
        vertexConstants?.Dispose();
        inputLayout.Dispose();
        pixelShader.Dispose();
        vertexShader.Dispose();
    }

    /// <summary>
    /// The constants nothing filled, which are therefore zero.
    ///
    /// Worth being able to read: a shader drawing black is usually a term
    /// multiplied by a constant nobody supplied, and this is the list of the
    /// candidates rather than a guess at which one it was.
    /// </summary>
    public IReadOnlyCollection<string> Unfilled => unfilled;

    private readonly SortedSet<string> unfilled = new(StringComparer.Ordinal);

    private void Fill(
        ID3D11DeviceContext context,
        ID3D11Buffer buffer,
        int size,
        IReadOnlyList<D3D11ShaderVariable> variables,
        CpuMaterial? material,
        D3D11EffectFrame frame)
    {
        var bytes = new byte[size];
        foreach (var variable in variables)
        {
            if (variable.Offset < 0 || variable.Offset + variable.Size > bytes.Length) continue;

            // A whole number first: the switch word that decides which branches of
            // the shader run is one of these, and a shader whose switches all read
            // zero draws nothing at all.
            if (Whole(variable.Name, material) is { } number)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(variable.Offset), number);
                continue;
            }
            var written = Value(variable, material, frame);
            if (written is null)
            {
                unfilled.Add(variable.Name);
                continue;
            }
            var many = Math.Min(written.Length * sizeof(float), variable.Size);
            System.Buffer.BlockCopy(written, 0, bytes, variable.Offset, many);
        }
        var mapped = context.Map(buffer, MapMode.WriteDiscard);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, mapped.DataPointer, bytes.Length);
        }
        finally
        {
            context.Unmap(buffer);
        }
    }

    /// <summary>
    /// What one constant holds. The material's own value first — it is the thing
    /// the author set — then whatever the engine feeds a constant of that name.
    /// </summary>
    private static float[]? Value(
        D3D11ShaderVariable variable,
        CpuMaterial? material,
        D3D11EffectFrame frame)
    {
        if (material?.SourceParameters.TryGetValue(variable.Name, out var supplied) == true
            && supplied.Length != 0)
        {
            return supplied;
        }
        return EngineValue(variable.Name, frame);
    }

    /// <summary>
    /// What the engine feeds a constant of this name, or null when nothing does and
    /// it is the material's to fill.
    ///
    /// The name is all there is to go on: D3D drops a uniform's HLSL semantic from
    /// the bytecode, so the engine resolves what a constant means from what it is
    /// called — and this is that same resolution, applied to what the shader itself
    /// says its constants are called.
    /// </summary>
    public static float[]? EngineValue(string name, D3D11EffectFrame frame)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(frame);
        var view = frame.View;
        var world = frame.World;

        // The three parameters Falcom added past the end of PhyreEngine's own range.
        // They are node parameters, so the engine supplies them per draw and the
        // material declares none of them; and their names are not semantics the
        // engine's own resolution knows, so they are named here. Read off Cold
        // Steel 1's effects, which is where 223/224/225 came from in the first place.
        switch (name)
        {
            case "GameMaterialDiffuse":
                var tint = frame.MaterialDiffuse == default ? Vector4.One : frame.MaterialDiffuse;
                return new[] { tint.X, tint.Y, tint.Z, tint.W };
            case "GameMaterialEmission":
                return new[]
                {
                    frame.MaterialEmission.X, frame.MaterialEmission.Y,
                    frame.MaterialEmission.Z, 0f,
                };

            // Scene-wide factors nothing else supplies. Their value is not read from
            // the game — but zero is provably not it: a factor that scales a term
            // erases it, and these two scale the main light and the texture
            // coordinates. One leaves both terms as they were written.
            case "GlobalMainLightClampFactor":
            case "GlobalTexcoordFactor":
                return new[] { 1f };
        }

        return PhyreShaderSemantic.Of(Unprefixed(name), null) switch
        {
            130 => Floats(world),
            131 => Floats(world * view),
            132 => Floats(world * view * frame.Projection),
            133 => Floats(Inverted(world)),
            134 => Floats(Inverted(world * view)),
            136 => Floats(Matrix4x4.Transpose(Inverted(world))),
            139 => Floats(Matrix4x4.Transpose(world)),
            140 => Floats(Matrix4x4.Transpose(world * view)),
            2 => Floats(frame.Projection),
            3 => Floats(view),
            4 => Floats(Inverted(view)),
            5 => Floats(Matrix4x4.Transpose(Inverted(view))),
            6 => Floats(view * frame.Projection),
            16 => new[] { frame.AmbientColor.X, frame.AmbientColor.Y, frame.AmbientColor.Z, frame.AmbientColor.W },
            17 => new[] { frame.Time },
            1 => new[] { frame.EyePosition.X, frame.EyePosition.Y, frame.EyePosition.Z, 1f },
            0 => Direction(frame.EyePosition),
            // The eye in the model's own space, which is what the engine's rule
            // answers for a name that does not say "world" — and what a shader
            // reading it against object-space positions actually wants.
            129 => Object(frame.EyePosition, world, 1f),
            128 => Object(frame.EyePosition, world, 0f),
            192 or 194 => new[]
            {
                frame.LightColor.X, frame.LightColor.Y, frame.LightColor.Z, frame.LightColor.W,
            },
            193 => new[] { frame.LightColor.W },
            203 or 201 => new[]
            {
                frame.LightDirection.X, frame.LightDirection.Y, frame.LightDirection.Z, 0f,
            },
            // A light's position slot, for a light that has no position. The viewport
            // lights a scene with one directional key light, and Phyre carries a
            // directional light in this slot as a direction with w = 0. A point
            // light's actual position is not something this has, and an all-zero
            // vector is a light at the origin pointing nowhere, which is the surface
            // drawn black.
            202 or 204 => new[]
            {
                -frame.LightDirection.X, -frame.LightDirection.Y, -frame.LightDirection.Z, 0f,
            },
            // Constant attenuation. Not measured — chosen so the term does not
            // remove the light, which an attenuation of zero does.
            195 => new[] { 1f, 0f, 0f, 0f },
            _ => null,
        };
    }

    /// <summary>
    /// The whole number a constant holds: the material's own, or the value the
    /// permutation was selected for. Both are switch words, and neither is a float.
    /// </summary>
    private uint? Whole(string name, CpuMaterial? material)
    {
        if (material?.SourceIntParameters?.TryGetValue(name, out var supplied) == true)
        {
            return supplied;
        }
        return contextSwitches.TryGetValue(name, out var context) ? context : null;
    }

    /// <summary>
    /// A constant's name without the frequency it is filed under.
    ///
    /// The game's effects name their scene-wide constants <c>scene_View</c>,
    /// <c>scene_EyePosition</c> and so on: the prefix says when the value is
    /// supplied, not what it is. Reading it as part of the name leaves the view
    /// matrix at zero, which is a scene lit by nothing and fogged to the near
    /// plane.
    /// </summary>
    private static string Unprefixed(string name)
    {
        foreach (var prefix in new[] { "scene_", "node_", "object_", "context_" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return name[prefix.Length..];
            }
        }
        return name;
    }

    /// <summary>A world-space point or direction brought into the model's space.</summary>
    private static float[] Object(Vector3 value, Matrix4x4 world, float w)
    {
        var inverse = Inverted(world);
        var moved = w == 0f
            ? Vector3.Normalize(Vector3.TransformNormal(value, inverse))
            : Vector3.Transform(value, inverse);
        return new[] { moved.X, moved.Y, moved.Z, w };
    }

    private static float[] Direction(Vector3 from)
    {
        var normalized = from.LengthSquared() > 0 ? Vector3.Normalize(from) : Vector3.UnitZ;
        return new[] { normalized.X, normalized.Y, normalized.Z, 0f };
    }

    private static Matrix4x4 Inverted(Matrix4x4 value)
        => Matrix4x4.Invert(value, out var inverted) ? inverted : Matrix4x4.Identity;

    /// <summary>
    /// A matrix as a constant buffer holds one.
    ///
    /// Transposed, because HLSL packs a float4x4 column-major unless the shader says
    /// otherwise and the game's shaders do not say otherwise — while
    /// <see cref="Matrix4x4"/> is rows. The viewport's own shaders sidestep this by
    /// declaring <c>row_major</c>; the game's cannot be asked to. Uploading rows
    /// into a column-major constant sends every vertex somewhere else, which is a
    /// model that vanishes rather than one that looks wrong.
    /// </summary>
    private static float[] Floats(Matrix4x4 value) => new[]
    {
        value.M11, value.M21, value.M31, value.M41,
        value.M12, value.M22, value.M32, value.M42,
        value.M13, value.M23, value.M33, value.M43,
        value.M14, value.M24, value.M34, value.M44,
    };

    /// <summary>The unnamed constant buffer, which is where a shader's uniforms live.</summary>
    private static D3D11ShaderConstantBuffer? Globals(D3D11ShaderProgramDescription reflected)
        => reflected.ConstantBuffers.FirstOrDefault(value => value.Name == "$Globals")
            ?? reflected.ConstantBuffers.FirstOrDefault(value => value.BindPoint == 0);

    private static int Rounded(int size) => size <= 0 ? 0 : (size + 15) / 16 * 16;

    private static ID3D11Buffer? Buffer(ID3D11Device device, int size)
    {
        var rounded = Rounded(size);
        if (rounded == 0) return null;
        return device.CreateBuffer(new BufferDescription
        {
            ByteWidth = rounded,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });
    }

    private static bool TryReadSemantic(string name, out VertexSemantic semantic)
    {
        switch (name.ToUpperInvariant())
        {
            case "POSITION": semantic = VertexSemantic.Position; return true;
            case "NORMAL": semantic = VertexSemantic.Normal; return true;
            case "TANGENT": semantic = VertexSemantic.Tangent; return true;
            case "BINORMAL": semantic = VertexSemantic.Bitangent; return true;
            case "TEXCOORD": semantic = VertexSemantic.TextureCoordinate; return true;
            case "COLOR": semantic = VertexSemantic.Color; return true;
            case "BLENDINDICES": semantic = VertexSemantic.JointIndices; return true;
            case "BLENDWEIGHT": semantic = VertexSemantic.JointWeights; return true;
            default: semantic = VertexSemantic.Position; return false;
        }
    }
}
