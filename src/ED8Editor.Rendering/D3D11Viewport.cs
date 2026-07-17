using System.Numerics;
using System.Runtime.InteropServices;
using ED8Editor.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ED8Editor.Rendering;

public sealed record ViewportCamera(Matrix4x4 View, Matrix4x4 Projection);

public sealed class D3D11Viewport : IDisposable
{
    private const string ShaderSource = """
        cbuffer PerDraw : register(b0)
        {
            row_major float4x4 WorldViewProjection;
            float4 SelectionColor;
        };
        struct VSPosition { float3 Position : POSITION; };
        struct VSTextured { float3 Position : POSITION; float2 TexCoord : TEXCOORD0; };
        struct VSColored { float3 Position : POSITION; float4 Color : COLOR0; };
        struct PSInput { float4 Position : SV_Position; float2 TexCoord : TEXCOORD0; };
        struct PSColoredInput { float4 Position : SV_Position; float4 Color : COLOR0; };
        PSInput VSPositionMain(VSPosition input)
        {
            PSInput output;
            output.Position = mul(float4(input.Position, 1.0f), WorldViewProjection);
            output.TexCoord = float2(0.0f, 0.0f);
            return output;
        }
        PSInput VSTexturedMain(VSTextured input)
        {
            PSInput output;
            output.Position = mul(float4(input.Position, 1.0f), WorldViewProjection);
            output.TexCoord = input.TexCoord;
            return output;
        }
        PSColoredInput VSColoredMain(VSColored input)
        {
            PSColoredInput output;
            output.Position = mul(float4(input.Position, 1.0f), WorldViewProjection);
            output.Color = input.Color;
            return output;
        }
        Texture2D DiffuseTexture : register(t0);
        SamplerState DiffuseSampler : register(s0);
        float4 ApplySelection(float4 color)
        {
            return lerp(color, float4(SelectionColor.rgb, color.a), SelectionColor.a);
        }
        float4 PSSolidMain(PSInput input) : SV_Target
        {
            return ApplySelection(float4(0.72f, 0.78f, 0.86f, 1.0f));
        }
        float4 PSTexturedMain(PSInput input) : SV_Target
        {
            return ApplySelection(DiffuseTexture.Sample(DiffuseSampler, input.TexCoord));
        }
        float4 PSColoredMain(PSColoredInput input) : SV_Target { return input.Color; }
        """;

    private readonly D3D11GraphicsDevice graphics;
    private readonly IDXGISwapChain1 swapChain;
    private readonly ID3D11VertexShader positionVertexShader;
    private readonly ID3D11VertexShader texturedVertexShader;
    private readonly ID3D11VertexShader coloredVertexShader;
    private readonly ID3D11PixelShader solidPixelShader;
    private readonly ID3D11PixelShader texturedPixelShader;
    private readonly ID3D11PixelShader coloredPixelShader;
    private readonly byte[] positionVertexBytecode;
    private readonly byte[] texturedVertexBytecode;
    private readonly ID3D11InputLayout coloredInputLayout;
    private readonly ID3D11Buffer perDrawBuffer;
    private readonly ID3D11SamplerState sampler;
    private readonly ID3D11RasterizerState rasterizer;
    private readonly Dictionary<string, ID3D11InputLayout> inputLayouts = new(StringComparer.Ordinal);
    private ID3D11RenderTargetView? renderTarget;
    private ID3D11Texture2D? depthTexture;
    private ID3D11DepthStencilView? depthView;
    private ID3D11Buffer? debugLineBuffer;
    private int debugLineVertexCount;
    private int width;
    private int height;

    public D3D11Viewport(D3D11GraphicsDevice graphics, IntPtr windowHandle, int width, int height)
    {
        this.graphics = graphics ?? throw new ArgumentNullException(nameof(graphics));
        if (windowHandle == IntPtr.Zero) throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));

        using var dxgiDevice = graphics.Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var description = new SwapChainDescription1
        {
            Width = Math.Max(1, width),
            Height = Math.Max(1, height),
            Format = Format.R8G8B8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
        };
        swapChain = factory.CreateSwapChainForHwnd(graphics.Device, windowHandle, description);
        factory.MakeWindowAssociation(windowHandle, WindowAssociationFlags.IgnoreAltEnter);

        positionVertexBytecode = Compile("VSPositionMain", "vs_5_0");
        texturedVertexBytecode = Compile("VSTexturedMain", "vs_5_0");
        var coloredVertexBytecode = Compile("VSColoredMain", "vs_5_0");
        positionVertexShader = graphics.Device.CreateVertexShader(positionVertexBytecode);
        texturedVertexShader = graphics.Device.CreateVertexShader(texturedVertexBytecode);
        coloredVertexShader = graphics.Device.CreateVertexShader(coloredVertexBytecode);
        solidPixelShader = graphics.Device.CreatePixelShader(Compile("PSSolidMain", "ps_5_0"));
        texturedPixelShader = graphics.Device.CreatePixelShader(Compile("PSTexturedMain", "ps_5_0"));
        coloredPixelShader = graphics.Device.CreatePixelShader(Compile("PSColoredMain", "ps_5_0"));
        coloredInputLayout = graphics.Device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 12, 0),
        }, coloredVertexBytecode);
        perDrawBuffer = graphics.Device.CreateBuffer(new BufferDescription(
            Marshal.SizeOf<PerDrawConstants>(),
            BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write));
        sampler = graphics.Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            MaxAnisotropy = 1,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });
        rasterizer = graphics.Device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            DepthClipEnable = true,
        });
        Resize(width, height);
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (this.width == width && this.height == height && renderTarget is not null) return;

        ReleaseTargets();
        swapChain.ResizeBuffers(0, width, height, Format.Unknown, SwapChainFlags.None).CheckError();
        using var backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        renderTarget = graphics.Device.CreateRenderTargetView(backBuffer);
        var depthDescription = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D24_UNorm_S8_UInt,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
        };
        depthTexture = graphics.Device.CreateTexture2D(depthDescription);
        depthView = graphics.Device.CreateDepthStencilView(depthTexture);
        this.width = width;
        this.height = height;
    }

    public void SetDebugLines(IReadOnlyList<D3D11DebugLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        debugLineBuffer?.Dispose();
        debugLineBuffer = null;
        debugLineVertexCount = 0;
        if (lines.Count == 0) return;
        var vertices = new DebugLineVertex[checked(lines.Count * 2)];
        for (var index = 0; index < lines.Count; index++)
        {
            vertices[index * 2] = new DebugLineVertex(lines[index].Start, lines[index].Color);
            vertices[index * 2 + 1] = new DebugLineVertex(lines[index].End, lines[index].Color);
        }
        debugLineBuffer = graphics.Device.CreateBuffer(vertices, BindFlags.VertexBuffer);
        debugLineVertexCount = vertices.Length;
    }

    public void Render(IReadOnlyList<D3D11SceneInstance> instances, ViewportCamera camera, bool verticalSync = true)
    {
        ArgumentNullException.ThrowIfNull(instances);
        if (renderTarget is null || depthView is null) return;

        var context = graphics.Context;
        context.OMSetRenderTargets(renderTarget, depthView);
        context.RSSetViewport(new Viewport(0, 0, width, height));
        context.RSSetState(rasterizer);
        context.ClearRenderTargetView(renderTarget, new Color4(0.035f, 0.045f, 0.065f, 1.0f));
        context.ClearDepthStencilView(depthView, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 1.0f, 0);
        context.VSSetConstantBuffer(0, perDrawBuffer);
        context.PSSetSampler(0, sampler);

        foreach (var instance in instances)
        {
            foreach (var mesh in instance.Model.Meshes)
            {
                var world = mesh.LocalTransform * instance.Transform;
                var matrix = world * camera.View * camera.Projection;
                UpdateConstants(matrix, instance.IsSelected);
                foreach (var primitive in mesh.Primitives)
                {
                    DrawPrimitive(instance.Model, primitive);
                }
            }
        }

        DrawDebugLines(camera);

        swapChain.Present(verticalSync ? 1 : 0, PresentFlags.None).CheckError();
    }

    public void Dispose()
    {
        graphics.Context.ClearState();
        ReleaseTargets();
        foreach (var layout in inputLayouts.Values) layout.Dispose();
        debugLineBuffer?.Dispose();
        coloredInputLayout.Dispose();
        rasterizer.Dispose();
        sampler.Dispose();
        perDrawBuffer.Dispose();
        texturedPixelShader.Dispose();
        coloredPixelShader.Dispose();
        solidPixelShader.Dispose();
        texturedVertexShader.Dispose();
        coloredVertexShader.Dispose();
        positionVertexShader.Dispose();
        swapChain.Dispose();
    }

    private void DrawDebugLines(ViewportCamera camera)
    {
        if (debugLineBuffer is null || debugLineVertexCount == 0) return;
        UpdateConstants(camera.View * camera.Projection, false);
        var context = graphics.Context;
        context.IASetInputLayout(coloredInputLayout);
        context.IASetVertexBuffer(0, debugLineBuffer, Marshal.SizeOf<DebugLineVertex>(), 0);
        context.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.LineList);
        context.VSSetShader(coloredVertexShader);
        context.PSSetShader(coloredPixelShader);
        context.PSSetShaderResource(0, null!);
        context.Draw(debugLineVertexCount, 0);
    }

    private void DrawPrimitive(D3D11ModelResources model, D3D11PrimitiveResources primitive)
    {
        var positionBuffer = FindBuffer(primitive, VertexSemantic.Position);
        if (positionBuffer is null || !TryMapTopology(primitive.Topology, out var topology)) return;
        var positionAttribute = positionBuffer.Attributes.First(value => value.Semantic == VertexSemantic.Position);
        if (!TryMapFormat(positionAttribute.SourceFormat, out var positionFormat)) return;

        var textureView = primitive.MaterialIndex >= 0 && primitive.MaterialIndex < model.Materials.Count
            && model.Materials[primitive.MaterialIndex].Source.BaseColorTextureIndex is { } textureIndex
            && (uint)textureIndex < model.Textures.Count
                ? model.Textures[textureIndex].ShaderResourceView
                : null;
        var textureBuffer = FindBuffer(primitive, VertexSemantic.TextureCoordinate);
        var textureAttribute = textureBuffer?.Attributes.First(value => value.Semantic == VertexSemantic.TextureCoordinate);
        var textureFormat = Format.Unknown;
        var textured = textureView is not null && textureBuffer is not null && textureAttribute is not null
            && TryMapFormat(textureAttribute.SourceFormat, out textureFormat);

        var context = graphics.Context;
        if (textured)
        {
            context.IASetInputLayout(GetTexturedLayout(positionFormat, positionAttribute.Offset, textureFormat, textureAttribute!.Offset));
            context.IASetVertexBuffers(0,
                new[] { positionBuffer.Buffer, textureBuffer!.Buffer },
                new[] { positionBuffer.Stride, textureBuffer.Stride },
                new[] { 0, 0 });
            context.VSSetShader(texturedVertexShader);
            context.PSSetShader(texturedPixelShader);
            context.PSSetShaderResource(0, textureView!);
        }
        else
        {
            context.IASetInputLayout(GetPositionLayout(positionFormat, positionAttribute.Offset));
            context.IASetVertexBuffer(0, positionBuffer.Buffer, positionBuffer.Stride, 0);
            context.VSSetShader(positionVertexShader);
            context.PSSetShader(solidPixelShader);
            context.PSSetShaderResource(0, null!);
        }

        context.IASetPrimitiveTopology(topology);
        context.IASetIndexBuffer(primitive.IndexBuffer, primitive.IndexElementSize == 2 ? Format.R16_UInt : Format.R32_UInt, 0);
        context.DrawIndexed(primitive.IndexCount, 0, 0);
    }

    private unsafe void UpdateConstants(Matrix4x4 matrix, bool selected)
    {
        var mapped = graphics.Context.Map(perDrawBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        *(PerDrawConstants*)mapped.DataPointer = new PerDrawConstants
        {
            WorldViewProjection = matrix,
            SelectionColor = selected ? new Vector4(1f, 0.32f, 0.04f, 0.68f) : Vector4.Zero,
        };
        graphics.Context.Unmap(perDrawBuffer, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerDrawConstants
    {
        public Matrix4x4 WorldViewProjection;
        public Vector4 SelectionColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DebugLineVertex(Vector3 Position, Vector4 Color);

    private ID3D11InputLayout GetPositionLayout(Format format, int offset)
    {
        var key = $"P:{format}:{offset}";
        if (!inputLayouts.TryGetValue(key, out var layout))
        {
            layout = graphics.Device.CreateInputLayout(
                new[] { new InputElementDescription("POSITION", 0, format, offset, 0) },
                positionVertexBytecode);
            inputLayouts.Add(key, layout);
        }
        return layout;
    }

    private ID3D11InputLayout GetTexturedLayout(Format positionFormat, int positionOffset, Format textureFormat, int textureOffset)
    {
        var key = $"T:{positionFormat}:{positionOffset}:{textureFormat}:{textureOffset}";
        if (!inputLayouts.TryGetValue(key, out var layout))
        {
            layout = graphics.Device.CreateInputLayout(new[]
            {
                new InputElementDescription("POSITION", 0, positionFormat, positionOffset, 0),
                new InputElementDescription("TEXCOORD", 0, textureFormat, textureOffset, 1),
            }, texturedVertexBytecode);
            inputLayouts.Add(key, layout);
        }
        return layout;
    }

    private void ReleaseTargets()
    {
        graphics.Context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        depthView?.Dispose();
        depthView = null;
        depthTexture?.Dispose();
        depthTexture = null;
        renderTarget?.Dispose();
        renderTarget = null;
    }

    private static D3D11VertexBufferResource? FindBuffer(D3D11PrimitiveResources primitive, VertexSemantic semantic)
        => primitive.VertexBuffers.FirstOrDefault(value => value.Attributes.Any(attribute => attribute.Semantic == semantic));

    private static bool TryMapFormat(string source, out Format format)
    {
        format = source switch
        {
            "Float32x2" => Format.R32G32_Float,
            "Float32x3" => Format.R32G32B32_Float,
            "Float32x4" => Format.R32G32B32A32_Float,
            "Float16x2" => Format.R16G16_Float,
            "Float16x4" => Format.R16G16B16A16_Float,
            "UNorm16x2" => Format.R16G16_UNorm,
            "UNorm8x2" => Format.R8G8_UNorm,
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
            sourceName: "ED8Editor.Viewport.hlsl",
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
