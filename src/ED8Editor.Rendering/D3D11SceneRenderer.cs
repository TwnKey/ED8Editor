using ED8Editor.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ED8Editor.Rendering;

public sealed record D3D11RenderReport(int DrawCalls, int SkippedPrimitives, int RenderTargetWidth, int RenderTargetHeight);

public sealed class D3D11SceneRenderer : IDisposable
{
    private const string ShaderSource = """
        struct VSInput { float3 Position : POSITION; };
        struct VSOutput { float4 Position : SV_Position; };
        VSOutput VSMain(VSInput input)
        {
            VSOutput output;
            output.Position = float4(input.Position * 0.01f, 1.0f);
            return output;
        }
        float4 PSMain(VSOutput input) : SV_Target
        {
            return float4(0.75f, 0.82f, 1.0f, 1.0f);
        }
        """;

    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11VertexShader vertexShader;
    private readonly ID3D11PixelShader pixelShader;
    private readonly byte[] vertexShaderBytecode;
    private readonly Dictionary<(Format Format, int Offset), ID3D11InputLayout> inputLayouts = new();

    public D3D11SceneRenderer(D3D11GraphicsDevice graphics)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        device = graphics.Device;
        context = graphics.Context;
        vertexShaderBytecode = Compile("VSMain", "vs_5_0");
        var pixelShaderBytecode = Compile("PSMain", "ps_5_0");
        vertexShader = device.CreateVertexShader(vertexShaderBytecode);
        pixelShader = device.CreatePixelShader(pixelShaderBytecode);
    }

    public D3D11RenderReport RenderOffscreen(
        IEnumerable<D3D11ModelResources> models,
        int width = 512,
        int height = 512)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        var targetDescription = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
        };
        using var target = device.CreateTexture2D(targetDescription);
        using var renderTarget = device.CreateRenderTargetView(target);
        context.OMSetRenderTargets(renderTarget);
        context.RSSetViewport(new Viewport(0, 0, width, height));
        context.ClearRenderTargetView(renderTarget, new Color4(0.04f, 0.05f, 0.07f, 1.0f));
        context.VSSetShader(vertexShader);
        context.PSSetShader(pixelShader);

        var draws = 0;
        var skipped = 0;
        foreach (var primitive in models.SelectMany(value => value.Meshes).SelectMany(value => value.Primitives))
        {
            var position = FindPositionBuffer(primitive);
            if (position is null || !TryMapTopology(primitive.Topology, out var topology))
            {
                skipped++;
                continue;
            }

            var attribute = position.Attributes.First(value => value.Semantic == VertexSemantic.Position);
            if (!TryMapFormat(attribute.SourceFormat, out var format))
            {
                skipped++;
                continue;
            }

            var inputLayout = GetInputLayout(format, attribute.Offset);
            context.IASetInputLayout(inputLayout);
            context.IASetPrimitiveTopology(topology);
            context.IASetVertexBuffer(0, position.Buffer, position.Stride, 0);
            context.IASetIndexBuffer(
                primitive.IndexBuffer,
                primitive.IndexElementSize == 2 ? Format.R16_UInt : Format.R32_UInt,
                0);
            context.DrawIndexed(primitive.IndexCount, 0, 0);
            draws++;
        }

        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        context.Flush();
        return new D3D11RenderReport(draws, skipped, width, height);
    }

    public void Dispose()
    {
        foreach (var inputLayout in inputLayouts.Values) inputLayout.Dispose();
        pixelShader.Dispose();
        vertexShader.Dispose();
    }

    private ID3D11InputLayout GetInputLayout(Format format, int offset)
    {
        var key = (format, offset);
        if (!inputLayouts.TryGetValue(key, out var inputLayout))
        {
            inputLayout = device.CreateInputLayout(
                new[] { new InputElementDescription("POSITION", 0, format, offset, 0) },
                vertexShaderBytecode);
            inputLayouts.Add(key, inputLayout);
        }
        return inputLayout;
    }

    private static D3D11VertexBufferResource? FindPositionBuffer(D3D11PrimitiveResources primitive)
    {
        return primitive.VertexBuffers.FirstOrDefault(value =>
            value.Attributes.Any(attribute => attribute.Semantic == VertexSemantic.Position));
    }

    private static bool TryMapFormat(string source, out Format format)
    {
        format = source switch
        {
            "Float32x2" => Format.R32G32_Float,
            "Float32x3" => Format.R32G32B32_Float,
            "Float32x4" => Format.R32G32B32A32_Float,
            "Float16x2" => Format.R16G16_Float,
            "Float16x4" => Format.R16G16B16A16_Float,
            _ => Format.Unknown,
        };
        return format != Format.Unknown;
    }

    private static bool TryMapTopology(Core.PrimitiveTopology source, out Vortice.Direct3D.PrimitiveTopology topology)
    {
        topology = source switch
        {
            Core.PrimitiveTopology.Points => Vortice.Direct3D.PrimitiveTopology.PointList,
            Core.PrimitiveTopology.Lines => Vortice.Direct3D.PrimitiveTopology.LineList,
            Core.PrimitiveTopology.Triangles => Vortice.Direct3D.PrimitiveTopology.TriangleList,
            Core.PrimitiveTopology.TriangleStrip => Vortice.Direct3D.PrimitiveTopology.TriangleStrip,
            _ => Vortice.Direct3D.PrimitiveTopology.Undefined,
        };
        return topology != Vortice.Direct3D.PrimitiveTopology.Undefined;
    }

    private static byte[] Compile(string entryPoint, string profile)
    {
        var result = Compiler.Compile(
            shaderSource: ShaderSource,
            defines: Array.Empty<ShaderMacro>(),
            include: null!,
            entryPoint: entryPoint,
            sourceName: "ED8Editor.Internal.hlsl",
            profile: profile,
            shaderFlags: ShaderFlags.OptimizationLevel3,
            effectFlags: EffectFlags.None,
            out var blob,
            out var errors);
        using (blob)
        using (errors)
        {
            result.CheckError();
            return blob.AsBytes();
        }
    }
}
