using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ED8Editor.Rendering;

internal sealed class D3D11BloomPipeline : IDisposable
{
    private const string ShaderSource = """
        cbuffer PostProcess : register(b0)
        {
            float2 GlowTexelSize;
            float2 Padding;
        };

        struct FullscreenInput
        {
            float4 Position : SV_Position;
            float2 TexCoord : TEXCOORD0;
        };

        FullscreenInput VSFullscreenMain(uint vertexId : SV_VertexID)
        {
            FullscreenInput output;
            float2 texCoord = float2((vertexId << 1) & 2, vertexId & 2);
            output.Position = float4(texCoord * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f), 0.0f, 1.0f);
            output.TexCoord = texCoord;
            return output;
        }

        Texture2D<float4> ColorBuffer : register(t0);
        Texture2D<float4> GlowBuffer : register(t1);
        SamplerState LinearClampSampler : register(s0);

        float4 GenerateGlowBuffer(FullscreenInput input) : SV_Target
        {
            float3 glow = ColorBuffer.Sample(LinearClampSampler, input.TexCoord).rgb;
            return float4(glow, 1.0f);
        }

        float3 GaussianBlurX(float2 texCoord)
        {
            float3 color = ColorBuffer.Sample(LinearClampSampler, texCoord).rgb * 0.2270270270f;
            color += ColorBuffer.Sample(LinearClampSampler, texCoord + float2(1.3846153846f * GlowTexelSize.x, 0.0f)).rgb * 0.3162162162f;
            color += ColorBuffer.Sample(LinearClampSampler, texCoord - float2(1.3846153846f * GlowTexelSize.x, 0.0f)).rgb * 0.3162162162f;
            color += ColorBuffer.Sample(LinearClampSampler, texCoord + float2(3.2307692308f * GlowTexelSize.x, 0.0f)).rgb * 0.0702702703f;
            color += ColorBuffer.Sample(LinearClampSampler, texCoord - float2(3.2307692308f * GlowTexelSize.x, 0.0f)).rgb * 0.0702702703f;
            return color;
        }

        float4 RenderGaussianBlurX(FullscreenInput input) : SV_Target
        {
            return float4(GaussianBlurX(input.TexCoord), 1.0f);
        }

        float3 GaussianBlurY(float2 texCoord)
        {
            float3 color = GlowBuffer.Sample(LinearClampSampler, texCoord).rgb * 0.2270270270f;
            color += GlowBuffer.Sample(LinearClampSampler, texCoord + float2(0.0f, 1.3846153846f * GlowTexelSize.y)).rgb * 0.3162162162f;
            color += GlowBuffer.Sample(LinearClampSampler, texCoord - float2(0.0f, 1.3846153846f * GlowTexelSize.y)).rgb * 0.3162162162f;
            color += GlowBuffer.Sample(LinearClampSampler, texCoord + float2(0.0f, 3.2307692308f * GlowTexelSize.y)).rgb * 0.0702702703f;
            color += GlowBuffer.Sample(LinearClampSampler, texCoord - float2(0.0f, 3.2307692308f * GlowTexelSize.y)).rgb * 0.0702702703f;
            return color;
        }

        float4 RenderGaussianBlurYCompositeCombine(FullscreenInput input) : SV_Target
        {
            float3 scene = ColorBuffer.Sample(LinearClampSampler, input.TexCoord).rgb;
            return float4(scene + GaussianBlurY(input.TexCoord), 1.0f);
        }
        """;

    private const int GlowDownsampleFactor = 2;
    private readonly D3D11GraphicsDevice graphics;
    private readonly ID3D11VertexShader fullscreenVertexShader;
    private readonly ID3D11PixelShader generateGlowPixelShader;
    private readonly ID3D11PixelShader horizontalBlurPixelShader;
    private readonly ID3D11PixelShader verticalBlurCompositePixelShader;
    private readonly ID3D11SamplerState linearClampSampler;
    private readonly ID3D11DepthStencilState depthDisabledState;
    private readonly ID3D11Buffer postProcessBuffer;
    private ID3D11Texture2D? sceneTexture;
    private ID3D11RenderTargetView? sceneRenderTarget;
    private ID3D11ShaderResourceView? sceneShaderResource;
    private ID3D11Texture2D? glowSourceTexture;
    private ID3D11RenderTargetView? glowSourceRenderTarget;
    private ID3D11ShaderResourceView? glowSourceShaderResource;
    private BloomTarget? glowTarget;
    private BloomTarget? horizontalBlurTarget;
    private int sceneWidth;
    private int sceneHeight;
    private int glowWidth;
    private int glowHeight;

    public D3D11BloomPipeline(D3D11GraphicsDevice graphics)
    {
        this.graphics = graphics ?? throw new ArgumentNullException(nameof(graphics));
        fullscreenVertexShader = graphics.Device.CreateVertexShader(Compile("VSFullscreenMain", "vs_5_0"));
        generateGlowPixelShader = graphics.Device.CreatePixelShader(Compile("GenerateGlowBuffer", "ps_5_0"));
        horizontalBlurPixelShader = graphics.Device.CreatePixelShader(Compile("RenderGaussianBlurX", "ps_5_0"));
        verticalBlurCompositePixelShader = graphics.Device.CreatePixelShader(
            Compile("RenderGaussianBlurYCompositeCombine", "ps_5_0"));
        linearClampSampler = graphics.Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });
        depthDisabledState = graphics.Device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = false,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.Always,
            StencilEnable = false,
        });
        postProcessBuffer = graphics.Device.CreateBuffer(new BufferDescription(
            Marshal.SizeOf<PostProcessConstants>(),
            BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write));
    }

    public ID3D11RenderTargetView? SceneRenderTarget => sceneRenderTarget;
    public ID3D11RenderTargetView? GlowSourceRenderTarget => glowSourceRenderTarget;

    public void Resize(int width, int height)
    {
        ReleaseTargets();
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        sceneWidth = width;
        sceneHeight = height;
        glowWidth = Math.Max(1, width / GlowDownsampleFactor);
        glowHeight = Math.Max(1, height / GlowDownsampleFactor);

        var sceneDescription = CreateTargetDescription(width, height);
        sceneTexture = graphics.Device.CreateTexture2D(sceneDescription);
        sceneRenderTarget = graphics.Device.CreateRenderTargetView(sceneTexture);
        sceneShaderResource = graphics.Device.CreateShaderResourceView(sceneTexture);
        glowSourceTexture = graphics.Device.CreateTexture2D(sceneDescription);
        glowSourceRenderTarget = graphics.Device.CreateRenderTargetView(glowSourceTexture);
        glowSourceShaderResource = graphics.Device.CreateShaderResourceView(glowSourceTexture);
        glowTarget = CreateBloomTarget(glowWidth, glowHeight);
        horizontalBlurTarget = CreateBloomTarget(glowWidth, glowHeight);
    }

    public void Composite(ID3D11RenderTargetView destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (sceneShaderResource is null || glowSourceShaderResource is null
            || glowTarget is null || horizontalBlurTarget is null) return;

        var context = graphics.Context;
        context.OMSetDepthStencilState(depthDisabledState);
        context.OMSetBlendState(null, new Color4(0f, 0f, 0f, 0f), uint.MaxValue);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(fullscreenVertexShader);
        context.PSSetSampler(0, linearClampSampler);

        context.RSSetViewport(new Viewport(0, 0, glowWidth, glowHeight));
        context.OMSetRenderTargets(glowTarget.RenderTarget);
        context.ClearRenderTargetView(glowTarget.RenderTarget, new Color4(0f, 0f, 0f, 0f));
        context.PSSetShader(generateGlowPixelShader);
        context.PSSetShaderResource(0, glowSourceShaderResource);
        context.Draw(3, 0);
        context.PSSetShaderResource(0, null!);

        UpdatePostProcessConstants();
        context.PSSetConstantBuffer(0, postProcessBuffer);
        context.OMSetRenderTargets(horizontalBlurTarget.RenderTarget);
        context.ClearRenderTargetView(horizontalBlurTarget.RenderTarget, new Color4(0f, 0f, 0f, 0f));
        context.PSSetShader(horizontalBlurPixelShader);
        context.PSSetShaderResource(0, glowTarget.ShaderResource);
        context.Draw(3, 0);
        context.PSSetShaderResource(0, null!);

        context.RSSetViewport(new Viewport(0, 0, sceneWidth, sceneHeight));
        context.OMSetRenderTargets(destination);
        context.PSSetShader(verticalBlurCompositePixelShader);
        context.PSSetShaderResource(0, sceneShaderResource);
        context.PSSetShaderResource(1, horizontalBlurTarget.ShaderResource);
        context.Draw(3, 0);
        context.PSSetShaderResource(0, null!);
        context.PSSetShaderResource(1, null!);
        context.OMSetDepthStencilState(null);
    }

    public void Dispose()
    {
        ReleaseTargets();
        postProcessBuffer.Dispose();
        depthDisabledState.Dispose();
        linearClampSampler.Dispose();
        verticalBlurCompositePixelShader.Dispose();
        horizontalBlurPixelShader.Dispose();
        generateGlowPixelShader.Dispose();
        fullscreenVertexShader.Dispose();
    }

    private BloomTarget CreateBloomTarget(int width, int height)
    {
        var texture = graphics.Device.CreateTexture2D(CreateTargetDescription(width, height));
        return new BloomTarget(
            texture,
            graphics.Device.CreateRenderTargetView(texture),
            graphics.Device.CreateShaderResourceView(texture));
    }

    private static Texture2DDescription CreateTargetDescription(int width, int height)
        => new()
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };

    private unsafe void UpdatePostProcessConstants()
    {
        var mapped = graphics.Context.Map(
            postProcessBuffer,
            0,
            MapMode.WriteDiscard,
            Vortice.Direct3D11.MapFlags.None);
        *(PostProcessConstants*)mapped.DataPointer = new PostProcessConstants
        {
            GlowTexelSize = new Vector2(1f / glowWidth, 1f / glowHeight),
        };
        graphics.Context.Unmap(postProcessBuffer, 0);
    }

    private void ReleaseTargets()
    {
        horizontalBlurTarget?.Dispose();
        horizontalBlurTarget = null;
        glowTarget?.Dispose();
        glowTarget = null;
        glowSourceShaderResource?.Dispose();
        glowSourceShaderResource = null;
        glowSourceRenderTarget?.Dispose();
        glowSourceRenderTarget = null;
        glowSourceTexture?.Dispose();
        glowSourceTexture = null;
        sceneShaderResource?.Dispose();
        sceneShaderResource = null;
        sceneRenderTarget?.Dispose();
        sceneRenderTarget = null;
        sceneTexture?.Dispose();
        sceneTexture = null;
    }

    private static byte[] Compile(string entryPoint, string profile)
    {
        var result = Compiler.Compile(
            shaderSource: ShaderSource,
            defines: Array.Empty<ShaderMacro>(),
            include: null!,
            entryPoint: entryPoint,
            sourceName: "ED8Editor.Bloom.hlsl",
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

    [StructLayout(LayoutKind.Sequential)]
    private struct PostProcessConstants
    {
        public Vector2 GlowTexelSize;
        public Vector2 Padding;
    }

    private sealed record BloomTarget(
        ID3D11Texture2D Texture,
        ID3D11RenderTargetView RenderTarget,
        ID3D11ShaderResourceView ShaderResource) : IDisposable
    {
        public void Dispose()
        {
            ShaderResource.Dispose();
            RenderTarget.Dispose();
            Texture.Dispose();
        }
    }
}
